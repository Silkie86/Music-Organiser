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

Sizes and speed with the full-size stand-in (one CPU thread): MIDI encoder 93 MB, about 30 ms a part; text encoder
278 MB, about 20 ms a search.

Python packages: `torch transformers==4.46.3 tokenizers onnx onnxruntime mido numpy`.
