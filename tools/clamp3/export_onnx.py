"""
Exports CLaMP 3's MIDI (M3) and text encoders to ONNX for the Music Organiser's MIDI page.

CLaMP 3 (https://github.com/sanderwood/clamp3, MIT licence) puts symbolic music and text in one vector space. The app
needs two pieces of it, each ending in the shared 768-number space (masked average, then the projection layer):

  symbolic.onnx  patches int64 [1, P, 64], mask int64 [1, P]       -> embedding float [1, 768]   (P <= 512)
  text.onnx      input_ids int64 [1, T], attention_mask int64 [1, T] -> embedding float [1, 768] (T <= 128)

Both are checked against PyTorch, then quantised to int8 and checked again. The C# side (Clamp3Model.cs) writes MTF,
cuts it into patches and tokenises text exactly as CLaMP 3's Python does.

  python export_onnx.py --out DIR                      # downloads the C2 checkpoint and tokenizer from Hugging Face
  python export_onnx.py --out DIR --checkpoint X.pth --tokenizer tokenizer.json
  python export_onnx.py --out DIR --tiny               # small random model, for the C# tests (no download)

Never commit the exported models: the app downloads them on first use.
"""
import argparse
import json
import math
import os
import random
import sys
import time
import urllib.request

import numpy as np
import torch
from transformers import BertConfig, BertModel, XLMRobertaConfig, XLMRobertaModel

HF = "https://huggingface.co"
C2_CHECKPOINT = ("weights_clamp3_c2_h_size_768_t_model_FacebookAI_xlm-roberta-base_t_length_128_a_size_768_a_layers_12"
                 "_a_length_128_s_size_768_s_layers_12_p_size_64_p_length_512.pth")
PATCH_SIZE, PATCH_LENGTH, MAX_TEXT = 64, 512, 128


class Symbolic(torch.nn.Module):
    """M3PatchEncoder + avg pooling + symbolic_proj. The one-hot then Linear(64*128) of the original is the same sum as
    picking one column per character, which is what this does (no 8192-wide one-hot in the graph)."""

    def __init__(self, hidden, layers, heads):
        super().__init__()
        self.patch_embedding = torch.nn.Linear(PATCH_SIZE * 128, hidden)
        cfg = BertConfig(vocab_size=1, hidden_size=hidden, num_hidden_layers=layers, num_attention_heads=heads,
                         intermediate_size=hidden * 4, max_position_embeddings=PATCH_LENGTH, attn_implementation="eager")
        self.base = BertModel(cfg)
        self.proj = torch.nn.Linear(hidden, 768 if hidden == 768 else hidden)

    def embed(self, patches):
        table = self.patch_embedding.weight.t()                                   # [64*128, hidden]
        offsets = torch.arange(PATCH_SIZE, device=patches.device) * 128
        return table[patches + offsets].sum(dim=-2) + self.patch_embedding.bias   # [B, P, hidden]

    def forward(self, patches, mask):
        hidden = self.base(inputs_embeds=self.embed(patches), attention_mask=mask)["last_hidden_state"]
        m = mask.unsqueeze(-1).to(hidden.dtype)
        return self.proj((hidden * m).sum(dim=1) / m.sum(dim=1))

    def reference(self, patches, mask):
        """CLaMP 3's own formulation (one-hot, Linear), to check the export against."""
        one_hot = torch.nn.functional.one_hot(patches, num_classes=128).reshape(len(patches), -1, PATCH_SIZE * 128).float()
        hidden = self.base(inputs_embeds=self.patch_embedding(one_hot), attention_mask=mask)["last_hidden_state"]
        m = mask.unsqueeze(-1).float()
        return self.proj((hidden * m).sum(dim=1) / m.sum(dim=1))


class Text(torch.nn.Module):
    """XLM-RoBERTa + avg pooling + text_proj."""

    def __init__(self, cfg, out):
        super().__init__()
        self.model = XLMRobertaModel(cfg)
        self.proj = torch.nn.Linear(cfg.hidden_size, out)

        self.int8_embeddings = False

    def quantise_embeddings(self):
        """The 250,002-word table is two thirds of the model: kept as int8 rows with one scale each (a quarter of the
        size, and next to no change), looked up in the graph and handed to XLM-R as embeddings. Position ids are then
        counted from the start, as they are for real tokens when nothing is padded (the app never pads)."""
        w = self.model.embeddings.word_embeddings.weight.detach()
        scale = w.abs().amax(dim=1, keepdim=True).clamp(min=1e-8) / 127
        self.register_buffer("words_int8", torch.round(w / scale).to(torch.int8))
        self.register_buffer("words_scale", scale)
        self.int8_embeddings = True

    def forward(self, input_ids, attention_mask):
        if self.int8_embeddings:
            embeds = self.words_int8[input_ids].float() * self.words_scale[input_ids]
            hidden = self.model(inputs_embeds=embeds, attention_mask=attention_mask)["last_hidden_state"]
        else:
            hidden = self.model(input_ids, attention_mask=attention_mask)["last_hidden_state"]
        m = attention_mask.unsqueeze(-1).to(hidden.dtype)
        return self.proj((hidden * m).sum(dim=1) / m.sum(dim=1))


def download(url, path):
    if os.path.exists(path):
        return path
    print(f"Downloading {url}")
    tmp = path + ".part"
    with urllib.request.urlopen(url) as r, open(tmp, "wb") as f:
        while chunk := r.read(1 << 20):
            f.write(chunk)
    os.replace(tmp, path)
    return path


def load_c2(checkpoint, symbolic, text):
    state = torch.load(checkpoint, map_location="cpu", weights_only=True)
    print(f"Checkpoint epoch {state.get('epoch')}, eval loss {state.get('min_eval_loss')}")
    state = state["model"]
    sym = {"patch_embedding." + k[len("symbolic_model.patch_embedding."):]: v for k, v in state.items() if k.startswith("symbolic_model.patch_embedding.")}
    sym.update({"base." + k[len("symbolic_model.base."):]: v for k, v in state.items() if k.startswith("symbolic_model.base.")})
    sym.update({"proj." + k[len("symbolic_proj."):]: v for k, v in state.items() if k.startswith("symbolic_proj.")})
    txt = {"model." + k[len("text_model."):]: v for k, v in state.items() if k.startswith("text_model.")}
    txt.update({"proj." + k[len("text_proj."):]: v for k, v in state.items() if k.startswith("text_proj.")})
    for name, module, sd in (("symbolic", symbolic, sym), ("text", text, txt)):
        missing, unexpected = module.load_state_dict(sd, strict=False)
        missing = [k for k in missing if not k.endswith("position_ids")]
        print(f"{name}: {len(sd)} tensors loaded; missing {missing}; unexpected {unexpected}")
        if missing:
            sys.exit(f"{name}: weights missing from the checkpoint")


def export(module, args, names, out_name, path):
    dynamic = {n: {0: "batch", 1: "length"} for n in names}
    dynamic[out_name] = {0: "batch"}
    torch.onnx.export(module, args, path, input_names=names, output_names=[out_name], dynamic_axes=dynamic,
                      opset_version=17, do_constant_folding=True, dynamo=False)


def quantise(src, dst):
    """Int8 weights in blocks of 32 (MatMulNBits): the weights shrink to a quarter. Accuracy level 4 lets ONNX Runtime
    multiply in int8 with each block of the inputs scaled on its own, as fast as fp32 and within 0.9995 of it.
    Ordinary dynamic int8 (one scale for a whole input) moved the real CLaMP 3 text encoder too far: cosine 0.95 to
    fp32, and the same best match for only 71% of searches."""
    import onnx
    from onnxruntime.quantization.matmul_nbits_quantizer import DefaultWeightOnlyQuantConfig, MatMulNBitsQuantizer
    config = DefaultWeightOnlyQuantConfig(block_size=32, is_symmetric=True, bits=8, op_types_to_quantize=("MatMul",),
                                          accuracy_level=4)
    q = MatMulNBitsQuantizer(onnx.load(src), algo_config=config)
    q.process()
    q.model.save_model_to_file(dst, use_external_data_format=False)


def cosine(a, b):
    a, b = np.asarray(a, dtype=np.float64).ravel(), np.asarray(b, dtype=np.float64).ravel()
    return float(a @ b / (np.linalg.norm(a) * np.linalg.norm(b)))


def session(path):
    import onnxruntime as ort
    o = ort.SessionOptions()
    o.intra_op_num_threads = 1
    return ort.InferenceSession(path, o, providers=["CPUExecutionProvider"])


def mtf_patches(mtf):
    """CLaMP 3's M3Patchilizer.encode(item, add_special_patches=True) for MTF."""
    import re
    lines = [l for l in re.findall(r".*?\n|.*$", mtf) if l]
    patches, patch = [], ""
    for line in lines:
        if patch.startswith(line.split(" ")[0]) and (len(patch) + len(" ".join(line.split(" ")[1:])) <= PATCH_SIZE - 2):
            patch = patch[:-1] + "\t" + " ".join(line.split(" ")[1:])
        else:
            if patch:
                patches.append(patch)
            patch = line
    if patch != "":
        patches.append(patch)
    patches = [chr(1) * PATCH_SIZE] + patches + [chr(2) * PATCH_SIZE]
    out = []
    for p in patches:
        ids = ([1] + [ord(c) for c in p] + [2])[:PATCH_SIZE]
        out.append(ids + [0] * (PATCH_SIZE - len(ids)))
    return out


def sample_mtf(seed, notes=None):
    rng = random.Random(seed)
    n = notes or rng.randint(4, 60)
    lines = ["ticks_per_beat 96", "set_tempo 500000 0", "time_signature 4 4 24 8 0", "program_change 0 0 0"]
    events, t = [], 0
    for _ in range(n):
        key, start = rng.randint(36, 84), t
        length = rng.choice([24, 48, 96, 192])
        events += [(start, 1, key), (start + length, 0, key)]
        t += rng.choice([0, 0, 24, 48, 96])
    events.sort(key=lambda e: (e[0], e[1], e[2]))
    last = 0
    for tick, on, key in events:
        lines.append(f"{'note_on' if on else 'note_off'} {tick - last} 0 {key} {100 if on else 0}")
        last = tick
    lines.append("end_of_track 0")
    return "\n".join(lines)


def check(name, sess_path, module, inputs_list, reference=None):
    s = session(sess_path)
    worst, timings = 1.0, []
    for inputs in inputs_list:
        with torch.no_grad():
            want = (reference or module)(*[torch.tensor(np.asarray(v)) for v in inputs.values()]).numpy()
        start = time.perf_counter()
        got = s.run(None, inputs)[0]
        timings.append(time.perf_counter() - start)
        worst = min(worst, cosine(want, got))
    print(f"{name}: worst cosine to PyTorch {worst:.6f}; {1000 * np.median(timings):.1f} ms median on one thread; "
          f"{os.path.getsize(sess_path) / 1e6:.1f} MB")
    return worst


def mido_case(seed):
    """Notes written to a .mid file with mido and read back the way batch_midi2mtf.py does (--m3_compatible), so the C#
    MTF writer can be checked against the real thing. Notes: (start, length, key, velocity) at 96 ticks a beat."""
    import io
    import mido
    rng = random.Random(seed)
    notes = [(rng.choice([0, 24, 48, 96, 192, 384]) + 96 * i // 2, rng.choice([12, 48, 96, 200]), rng.randint(30, 90), rng.randint(1, 127))
             for i in range(rng.randint(3, 12))]
    drums, tempo = seed == 2, [120, 87.5, 174][seed]
    channel = 9 if drums else 0
    events = []
    for start, length, key, vel in notes:
        events += [(start, 1, key, vel), (start + length, 0, key, 0)]
    events.sort(key=lambda e: (e[0], e[1], e[2]))
    mid = mido.MidiFile(ticks_per_beat=96)
    track = mido.MidiTrack()
    mid.tracks.append(track)
    track.append(mido.MetaMessage("track_name", name="Part", time=0))
    track.append(mido.MetaMessage("set_tempo", tempo=round(60_000_000 / tempo), time=0))
    track.append(mido.MetaMessage("time_signature", numerator=4, denominator=4, time=0))
    if not drums:
        track.append(mido.Message("program_change", program=0, channel=channel, time=0))
    last = 0
    for tick, on, key, vel in events:
        track.append(mido.Message("note_on" if on else "note_off", note=key, velocity=vel, channel=channel, time=tick - last))
        last = tick
    buf = io.BytesIO()
    mid.save(file=buf)
    buf.seek(0)
    read = mido.MidiFile(file=buf)
    lines = ["ticks_per_beat " + str(read.ticks_per_beat)]
    for msg in read.merged_track:
        if msg.is_meta and msg.type in ["text", "copyright", "track_name", "instrument_name", "lyrics", "marker", "cue_marker", "device_name"]:
            continue
        lines.append(" ".join(str(v) for v in msg.dict().values()).strip().encode("unicode_escape").decode("utf-8"))
    return {"notes": [list(n) for n in notes], "tempo": tempo, "drums": drums, "mtf": "\n".join(lines)}


def tiny_tokenizer(path):
    """A small SentencePiece-style Unigram tokenizer.json (NFKC, Metaspace), for the C# tokenizer tests."""
    from tokenizers import Regex, Tokenizer, models, normalizers, pre_tokenizers, trainers, processors
    tok = Tokenizer(models.Unigram())
    tok.normalizer = normalizers.Sequence([normalizers.NFKC(), normalizers.Replace(Regex(" {2,}"), " ")])
    tok.pre_tokenizer = pre_tokenizers.Metaspace(replacement="▁", prepend_scheme="always")
    words = ("dark pad chords bright happy melody funky bassline slap bass arpeggio rising falling piano strings sad "
             "minor major seventh trap drums hihat rolling groove jazzy smooth lofi chill ambient epic cinematic "
             "techno house acid synth lead bouncy staccato legato swing triplet").split()
    rng = random.Random(1)
    corpus = [" ".join(rng.choice(words) for _ in range(rng.randint(2, 8))) for _ in range(3000)]
    trainer = trainers.UnigramTrainer(vocab_size=300, special_tokens=["<s>", "<pad>", "</s>", "<unk>"], unk_token="<unk>")
    tok.train_from_iterator(corpus, trainer)
    tok.post_processor = processors.RobertaProcessing(("</s>", 2), ("<s>", 0))
    tok.save(path)
    return tok


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--checkpoint")
    ap.add_argument("--tokenizer")
    ap.add_argument("--tiny", action="store_true", help="small random model and tokenizer, plus expected outputs, for tests")
    ap.add_argument("--stand-in", action="store_true", help="full-size model with random weights: sizes and speed only (no download)")
    ap.add_argument("--keep-fp32", action="store_true")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    torch.manual_seed(0)

    if a.tiny:
        hidden, layers, heads = 16, 2, 2
        tok_path = os.path.join(a.out, "tokenizer.json")
        tok = tiny_tokenizer(tok_path)
        tcfg = XLMRobertaConfig(vocab_size=tok.get_vocab_size(), hidden_size=hidden, num_hidden_layers=layers, num_attention_heads=heads,
                                intermediate_size=hidden * 4, max_position_embeddings=MAX_TEXT + 2, type_vocab_size=1,
                                layer_norm_eps=1e-5, pad_token_id=1, bos_token_id=0, eos_token_id=2, attn_implementation="eager")
        symbolic, text = Symbolic(hidden, layers, heads), Text(tcfg, hidden)
    elif a.stand_in:
        tok = tiny_tokenizer(os.path.join(a.out, "tokenizer.json"))
        tcfg = XLMRobertaConfig(vocab_size=250002, hidden_size=768, num_hidden_layers=12, num_attention_heads=12, intermediate_size=3072,
                                max_position_embeddings=514, type_vocab_size=1, layer_norm_eps=1e-5, pad_token_id=1, bos_token_id=0,
                                eos_token_id=2, attn_implementation="eager")
        symbolic, text = Symbolic(768, 12, 12), Text(tcfg, 768)
    else:
        from tokenizers import Tokenizer
        work = os.path.join(a.out, "download")
        os.makedirs(work, exist_ok=True)
        ckpt = a.checkpoint or download(f"{HF}/sander-wood/clamp3/resolve/main/{C2_CHECKPOINT}", os.path.join(work, C2_CHECKPOINT))
        tok_path = a.tokenizer or download(f"{HF}/FacebookAI/xlm-roberta-base/resolve/main/tokenizer.json", os.path.join(work, "tokenizer.json"))
        tok = Tokenizer.from_file(tok_path)
        tcfg = XLMRobertaConfig(vocab_size=250002, hidden_size=768, num_hidden_layers=12, num_attention_heads=12, intermediate_size=3072,
                                max_position_embeddings=514, type_vocab_size=1, layer_norm_eps=1e-5, pad_token_id=1, bos_token_id=0,
                                eos_token_id=2, attn_implementation="eager")
        symbolic, text = Symbolic(768, 12, 12), Text(tcfg, 768)
        load_c2(ckpt, symbolic, text)
        if os.path.abspath(tok_path) != os.path.abspath(os.path.join(a.out, "tokenizer.json")):
            import shutil
            shutil.copyfile(tok_path, os.path.join(a.out, "tokenizer.json"))
    symbolic.eval()
    text.eval()

    mtfs = [sample_mtf(i) for i in range(12)] + [sample_mtf(99, notes=400)]
    sym_inputs = []
    for m in mtfs:
        p = mtf_patches(m)[:PATCH_LENGTH]
        sym_inputs.append({"patches": np.array([p], dtype=np.int64), "mask": np.ones((1, len(p)), dtype=np.int64)})
    queries = ["dark pad chords", "bouncy funk bassline", "sad piano melody in a minor key", "fast trance arpeggio",
               "lofi chill keys", "epic cinematic strings", "acid techno synth lead"]
    txt_inputs = []
    for q in queries:
        ids = tok.encode(q).ids[:MAX_TEXT]
        txt_inputs.append({"input_ids": np.array([ids], dtype=np.int64), "attention_mask": np.ones((1, len(ids)), dtype=np.int64)})

    sym32, txt32 = os.path.join(a.out, "symbolic_fp32.onnx"), os.path.join(a.out, "text_fp32.onnx")
    with torch.no_grad():
        export(symbolic, (torch.tensor(sym_inputs[0]["patches"]), torch.tensor(sym_inputs[0]["mask"])), ["patches", "mask"], "embedding", sym32)
        export(text, (torch.tensor(txt_inputs[0]["input_ids"]), torch.tensor(txt_inputs[0]["attention_mask"])), ["input_ids", "attention_mask"], "embedding", txt32)
    # A real export that drifts from PyTorch must not be published: it fails here, so the workflow stops before releasing.
    # (v1 gave 0.9995 or better on all four; the tiny test model's random weights aren't held to it.)
    def must(worst, least, what):
        if not a.tiny and worst < least:
            sys.exit(f"{what}: worst cosine {worst:.6f} is below {least}, so this export isn't safe to publish")
    must(check("symbolic fp32", sym32, symbolic, sym_inputs, reference=symbolic.reference), 0.9999, "symbolic fp32")
    must(check("text fp32", txt32, text, txt_inputs), 0.9999, "text fp32")

    sym8, txt8 = os.path.join(a.out, "symbolic.onnx"), os.path.join(a.out, "text.onnx")
    quantise(sym32, sym8)
    text.quantise_embeddings()
    txt_q = os.path.join(a.out, "text_int8_words.onnx")
    with torch.no_grad():
        export(text, (torch.tensor(txt_inputs[0]["input_ids"]), torch.tensor(txt_inputs[0]["attention_mask"])), ["input_ids", "attention_mask"], "embedding", txt_q)
    text.int8_embeddings = False   # PyTorch with the original table stays the reference
    quantise(txt_q, txt8)
    os.remove(txt_q)
    must(check("symbolic int8", sym8, symbolic, sym_inputs, reference=symbolic.reference), 0.999, "symbolic int8")
    must(check("text int8", txt8, text, txt_inputs), 0.999, "text int8")

    # Does int8 keep the ranking? Cosine of each query to each MIDI part, fp32 against int8.
    s32, s8, t32, t8 = session(sym32), session(sym8), session(txt32), session(txt8)
    m32 = np.stack([s32.run(None, i)[0][0] for i in sym_inputs]); m8 = np.stack([s8.run(None, i)[0][0] for i in sym_inputs])
    q32 = np.stack([t32.run(None, i)[0][0] for i in txt_inputs]); q8 = np.stack([t8.run(None, i)[0][0] for i in txt_inputs])
    unit = lambda x: x / np.linalg.norm(x, axis=1, keepdims=True)
    sim32, sim8 = unit(q32) @ unit(m32).T, unit(q8) @ unit(m8).T
    same_top = np.mean(sim32.argmax(1) == sim8.argmax(1))
    corr = np.mean([np.corrcoef(sim32[i], sim8[i])[0, 1] for i in range(len(queries))])
    print(f"int8 against fp32 on text-to-MIDI search: same best match {same_top:.0%}, score correlation {corr:.3f}")
    if not a.tiny and (same_top < 0.85 or corr < 0.99):
        sys.exit("int8 changes which MIDI parts a description finds too much to publish")

    if a.tiny:
        # Expected outputs for the C# tests: the MTF texts, the patches CLaMP 3's Python makes, and both embeddings. The
        # tests use the fp32 models: int8 matrix maths differs a little between processors (AVX2, AVX-512, ARM), which
        # in a 16-number model is enough to blur a check of the C# inputs.
        cases = {
            "midi": [{"mtf": m, "patches": mtf_patches(m), "embedding": s32.run(None, i)[0][0].tolist()} for m, i in zip(mtfs[:4], sym_inputs[:4])],
            "text": [{"text": q, "ids": i["input_ids"][0].tolist(), "embedding": t32.run(None, i)[0][0].tolist()} for q, i in zip(queries, txt_inputs)],
        }
        extra = ["  Dark   PAD chords ", "ｆｕｌｌ width", "émotional piano", "trap 808 rolls", "x"]
        cases["notes"] = [mido_case(seed) for seed in range(3)]
        cases["tokens"] = [{"text": q, "ids": tok.encode(q).ids} for q in queries + extra]
        with open(os.path.join(a.out, "expected.json"), "w", encoding="utf-8") as f:
            json.dump(cases, f)
    if a.tiny:
        os.replace(sym32, sym8)
        os.replace(txt32, txt8)
    elif not a.keep_fp32:
        os.remove(sym32)
        os.remove(txt32)


if __name__ == "__main__":
    main()
