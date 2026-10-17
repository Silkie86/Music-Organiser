"""
Does the exported model find the right kind of MIDI from a description? Makes a dozen clearly different parts (pads,
arps, basslines, drums, melodies), asks for each in words, and shows where the intended part lands, with the fp32
and int8 exports side by side. Also checks "more like this": two variations of a part should be each other's closest.

  python check_quality.py DIR      # DIR from export_onnx.py --keep-fp32 (symbolic*.onnx, text*.onnx, tokenizer.json)
"""
import os
import random
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from export_onnx import MAX_TEXT, PATCH_LENGTH, mtf_patches, session  # noqa: E402

BEAT = 96


def mtf(notes, tempo=120, drums=False):
    """The same MTF the app writes (MtfText.FromNotes): notes are (start, length, key, velocity) in ticks at 96 a beat."""
    ch = 9 if drums else 0
    lines = ["ticks_per_beat 96", f"set_tempo {round(60_000_000 / tempo)} 0", "time_signature 4 4 24 8 0"]
    if not drums:
        lines.append("program_change 0 0 0")
    ev = []
    for s, l, k, v in notes:
        ev += [(s, 1, k, v), (s + max(1, l), 0, k, 0)]
    ev.sort(key=lambda e: (e[0], e[1], e[2]))
    last = 0
    for t, on, k, v in ev:
        lines.append(f"{'note_on' if on else 'note_off'} {t - last} {ch} {k} {v}")
        last = t
    lines.append("end_of_track 0")
    return "\n".join(lines)


def chords(prog, bars_each=1, vel=70, octave=0):
    out = []
    for i, c in enumerate(prog):
        for k in c:
            out.append((i * 4 * BEAT * bars_each, 4 * BEAT * bars_each - 4, k + octave, vel))
    return out


def line(keys, step, length=None, vel=100, start=0):
    return [(start + i * step, length or step - 4, k, vel) for i, k in enumerate(keys) if k is not None]


Am, F, C, G = [57, 60, 64], [53, 57, 60], [48, 52, 55], [55, 59, 62]
Cm, Ab, Eb, Bb = [48, 51, 55], [44, 48, 51], [51, 55, 58], [46, 50, 53]


def parts():
    rng = random.Random(4)
    p = {}
    p["dark slow minor pad chords"] = (chords([Cm + [60], Ab + [60], Eb + [58], Bb + [62]], bars_each=2, vel=60), 70, False)
    p["happy major piano chords"] = (chords([[60, 64, 67], [65, 69, 72], [67, 71, 74], [60, 64, 67]]), 120, False)
    p["fast trance arpeggio"] = (line([57, 60, 64, 69, 64, 60] * 8, BEAT // 4), 138, False)
    p["funky syncopated slap bassline"] = (
        [(t, 40, k, 110) for t, k in [(0, 33), (72, 33), (144, 45), (240, 31), (288, 33), (360, 36), (432, 38), (480, 33),
                                      (552, 45), (624, 43), (720, 40), (768, 33), (840, 33), (912, 45)]], 108, False)
    p["deep sub bass held notes"] = (line([33, 29, 24, 31], 4 * BEAT, 4 * BEAT - 8, 100), 85, False)
    p["four on the floor house drum beat"] = (
        [(b * BEAT, 24, 36, 120) for b in range(16)] + [(b * BEAT + 48, 24, 42, 90) for b in range(16)] +
        [(b * BEAT, 24, 39, 110) for b in range(1, 16, 2)], 124, True)
    p["trap hi hat rolls and 808"] = (
        [(i * 24, 12, 42, 70 + rng.randint(0, 40)) for i in range(64)] + [(0, 300, 36, 120), (576, 200, 36, 120), (2 * 384, 24, 38, 120)], 140, True)
    p["sad slow piano melody"] = (line([69, 67, 65, 64, 62, 64, 65, 64, None, 62, 60, 59, 57], BEAT * 2, vel=60), 66, False)
    p["bright catchy pop melody"] = (line([72, 74, 76, 79, 76, 74, 72, 74, 76, 76, 74, 72, 79, 77, 76, 74], BEAT // 2), 118, False)
    p["jazzy seventh chords"] = (chords([[50, 53, 57, 60], [55, 59, 62, 65], [48, 52, 55, 59], [57, 60, 64, 67]]), 96, False)
    p["epic cinematic strings"] = (chords([[45, 52, 57, 64, 69], [41, 48, 53, 60, 65], [43, 50, 55, 62, 67], [40, 47, 52, 59, 64]], bars_each=2, vel=90), 80, False)
    p["acid techno 303 synth line"] = (line([36, 36, 48, 36, 39, 36, 51, 36, 34, 36, 46, 36, 41, 39, 36, 48] * 2, BEAT // 4, 20, 110), 132, False)
    return p


def embed_midi(s, text):
    patches = mtf_patches(text)
    vecs, weights = [], []
    n = len(patches)
    windows = [(0, n, n)] if n <= PATCH_LENGTH else (
        [(i, PATCH_LENGTH, PATCH_LENGTH) for i in range(0, n - PATCH_LENGTH + 1, PATCH_LENGTH)] +
        ([(n - PATCH_LENGTH, PATCH_LENGTH, n % PATCH_LENGTH)] if n % PATCH_LENGTH else []))
    for start, count, w in windows:
        p = np.array([patches[start:start + count]], dtype=np.int64)
        vecs.append(s.run(None, {"patches": p, "mask": np.ones((1, count), dtype=np.int64)})[0][0] * w)
        weights.append(w)
    v = np.sum(vecs, axis=0) / sum(weights)
    return v / np.linalg.norm(v)


def embed_text(s, tok, text):
    ids = tok.encode(text).ids[:MAX_TEXT]
    v = s.run(None, {"input_ids": np.array([ids], dtype=np.int64), "attention_mask": np.ones((1, len(ids)), dtype=np.int64)})[0][0]
    return v / np.linalg.norm(v)


def main():
    from tokenizers import Tokenizer
    d = sys.argv[1]
    tok = Tokenizer.from_file(os.path.join(d, "tokenizer.json"))
    ps = parts()
    names = list(ps)
    texts = {n: mtf(*ps[n]) for n in names}
    # Variations for "more like this": the same parts moved up a fifth and played a little differently.
    variations = {n: mtf([(s + (6 if i % 3 == 0 else 0), l, k + (0 if ps[n][2] else 7), max(1, v - 10))
                          for i, (s, l, k, v) in enumerate(ps[n][0])], ps[n][1], ps[n][2]) for n in names}
    results = {}
    for label, sym, txt in (("fp32", "symbolic_fp32.onnx", "text_fp32.onnx"), ("int8", "symbolic.onnx", "text.onnx")):
        if not os.path.exists(os.path.join(d, sym)):
            continue
        ss, ts = session(os.path.join(d, sym)), session(os.path.join(d, txt))
        M = np.stack([embed_midi(ss, texts[n]) for n in names])
        V = np.stack([embed_midi(ss, variations[n]) for n in names])
        Q = np.stack([embed_text(ts, tok, n) for n in names])
        sim = Q @ M.T
        ranks = [int((sim[i] > sim[i, i]).sum()) + 1 for i in range(len(names))]
        like = V @ M.T
        like_ranks = [int((like[i] > like[i, i]).sum()) + 1 for i in range(len(names))]
        results[label] = (sim, ranks, like_ranks)
        print(f"\n== {label} ==  words: top-1 {np.mean(np.array(ranks) == 1):.0%}, top-3 {np.mean(np.array(ranks) <= 3):.0%}, "
              f"mean rank {np.mean(ranks):.1f} of {len(names)} (chance {(len(names) + 1) / 2:.1f}); "
              f"more like this: top-1 {np.mean(np.array(like_ranks) == 1):.0%}")
        for i, n in enumerate(names):
            best = names[int(sim[i].argmax())]
            print(f"  {ranks[i]:>2}  {n:<36} best: {best}")
    if len(results) == 2:
        a, b = results["fp32"][0].ravel(), results["int8"][0].ravel()
        print(f"\nint8 against fp32: score correlation {np.corrcoef(a, b)[0, 1]:.3f}, "
              f"same best part for {np.mean(results['fp32'][0].argmax(1) == results['int8'][0].argmax(1)):.0%} of the descriptions")


if __name__ == "__main__":
    main()
