# CLaMP 3 for the MIDI page

The MIDI page can search by description ("dark pad chords") and find parts that sound alike with
[CLaMP 3](https://github.com/sanderwood/clamp3) (MIT licence), run on the user's computer with ONNX Runtime
(`src/MusicOrganiser.Core/Midi/Clamp3/`). The app downloads the model itself on first use from this repository's
`clamp3-c2-onnx-v1` release. The model files are never committed to git.

- `export_onnx.py` exports CLaMP 3's MIDI encoder (M3) and text encoder (XLM-RoBERTa) from the symbolic "C2"
  checkpoint, checks them against PyTorch, quantises them to int8, and checks that text-to-MIDI search ranks the same.
- `.github/workflows/clamp3-onnx.yml` runs it on GitHub and publishes the release (run it by hand from the Actions tab).
- `export_onnx.py --tiny` makes the small random model in `tests/MusicOrganiser.Core.Tests/Data/clamp3-tiny`, with
  what the Python side gives for each input, so the C# tests check the MTF text, patches, tokens and both encoders
  against CLaMP 3's own code.
- `export_onnx.py --stand-in` makes a full-size model with random weights, to measure sizes and speed without a download.
- `check_quality.py DIR` checks an export (run export_onnx.py with `--keep-fp32` to compare fp32 and int8).

Real C2 weights (checked 2026-10-16): the int8 exports (weights in blocks of 32, MatMulNBits at accuracy level 4,
the word table as int8 rows) stay within cosine 0.9994 of PyTorch and give the same best match as fp32 for every
test search. Sizes: MIDI encoder 125 MB, text encoder 293 MB, tokenizer 9 MB. Speed on one CPU thread: about 90 ms a
part (the app runs several at once in the background) and 30 ms a search. `check_quality.py` asks for 12 clearly
different made-up parts in words: every one lands in the top 3 (half first), and More like this finds each part's
variation first, for fp32 and int8 alike.

Python packages: `torch transformers==4.46.3 tokenizers onnx onnx_ir onnxruntime mido numpy`.
