# Tools

## Format Lab

`formatlab` looks inside Bitwig and FL Studio files, to learn how each DAW saves things. Change one
thing in the DAW, save a copy, and `diff` names every value that changed. It reads `.bwproject`,
`.bwpreset`, `.bwclip` and `.flp` files and never changes them.

Run it from the repository folder (in Visual Studio: View > Terminal):

```
dotnet run --project tools/MusicOrganiser.FormatLab -- tree "Night Drive.flp"
```

| Command | What it shows |
|---|---|
| `tree <file> [--depth N]` | Everything in the file, one value per line, named where the app knows the name. |
| `diff <before> <after>` | Every value that differs between two saves. Something added or removed is listed once, with how much it holds. |
| `find <file> <value> [--text]` | Everywhere the file holds a text or number: in a named field, or as raw bytes (16-bit text, 32-bit numbers, 32- and 64-bit floats, in both byte orders). `--text` looks for it as text only. |
| `census <folder>... [--top N]` | Across every project in the folders: which FL Studio events and Bitwig classes and fields the app doesn't know yet, by how many projects have them. Only ids and counts, nothing of the music, so it's safe to paste into a conversation. |
| `probe [<file>] [--reports <folder>]` | MO Probe's reports, newest first. With a project file: where the probe's saved state is in it, its knob values, and which save of which report wrote it. |
| `probe live` | The MO Probes running in a DAW now: where each is (DAW, format, track, transport), its knobs, and its report. |
| `probe send <probe> <command>` | Sends a running probe a command and prints its answer (the line in its report). The commands are `ping`, `beep [<seconds>]`, `label <text>` and `set <knob> <value>`, with the knob by name or id (`gain`, `1002`) and the value in its own units (`-12`, `On`, `Charlie`). |
| `probe check-bitwig [<probe>]` | With a probe on a track in Bitwig and the Music Organiser script answering: turns each knob through Bitwig (0, ¼, ½ and 1, or each step) and reads what the probe got, then has the probe turn it and reads Bitwig's value back. One line per turn, then a summary; the knobs are put back at the end. Needs "Let the app make changes" in Bitwig for the Bitwig-to-probe half. |
| `fields bitwig\|fl [<id or name>]` | What the app knows: the field registry. With an id or name, also what it means, how that was found out and where the code uses it. |
| `live bitwig [--port N] [--reports <folder>]` | Runs the Bitwig live tests in the Bitwig open now, as Settings' *Run live tests* does: a line for each test as it ends (with why, for one that failed or was skipped), then the summary and where the report is. They only change a project whose name starts with “MO Live Test”, while Bitwig lets the app make changes. Exits with 1 when a test failed; Ctrl+C stops the run after the test running then, and the report is still written. `--reports` puts the report somewhere other than `reports\live` in the app's data folder, and `--port` asks Bitwig on another port. Unlike Settings, it doesn't read the app's library, so it looks for the saved test project only in Bitwig's Projects folder, and for the Drum Machine's ID only in Bitwig's own files. |

Ids are written the way each format numbers them: hex for Bitwig (`0x2c8`), plain numbers for FL Studio
events (`156`).

`extensions/README.md` (“Live tests”) says what the live tests do, how to make the project they change, and
what their reports hold.

The `probe` commands all take `--reports <folder>` for a report folder other than the probe's own. A `<probe>`
is its instance (or its first few characters, as report names have them), its track or its label; it can
be left out of `check-bitwig` when only one probe is running in Bitwig.

### The loop for working out a format

1. Save a project, change one thing in the DAW (a knob, a name, a note), and save it under another name.
2. `formatlab diff before.flp after.flp` names what changed. `find` locates a value you typed in the DAW.
3. When it's worked out, add it to the registry with how you know (below), so `tree` names it from then on.

MO Probe helps with plugin state and automation: its saved state starts with `MOPROBE1`, its knobs have
ids 1001 to 1008, and its reports say what the DAW sent it and when it saved (`native/README.md`).

### Talking to MO Probe while it runs

Each probe in use writes its live status to `live/<instance>.json` in its report folder about twice a
second (its track, the transport and every knob), and takes commands as files dropped in
`live/<instance>.in/`, answering in its report. They're plain files, so anything that can read and write
files can take part:

```
formatlab probe live
formatlab probe send "Inst 3" set gain -12
formatlab probe send 1683A801 beep
formatlab probe check-bitwig
```

By hand, write one JSON object per line, e.g. `{"cmd":"set","param":1002,"value":-12,"id":"mine-1"}`, to
a file ending in `.json` in the probe's `.in` folder (write it under another name and rename it, where you
can). The probe takes it within a moment and deletes it, and the line in its report that answers has
`"for":"mine-1"`. `native/README.md` has the commands and the status in full.

## The field registry

`src/MusicOrganiser.Core/Formats/Registry` holds what the app knows about each format, one entry per
id: `bitwig.json` (classes, fields, metadata) and `fl-studio.json` (events, and the records inside a
Fruity Wrapper's state). Each entry says what it means, which DAW versions it was seen in, how it was
found out (`proof`: a test, a file, a confirmation) and the constant that holds it in the code (`code`).

The tests keep it honest: every id constant the readers and writers use, and every Bitwig id written
straight into the code, must be in it under the right name; every test it cites must exist; and every
FL layout must read. So adding an id to the code means adding it here too.

FL events can describe their data: `layout` names numbers at fixed offsets (`"pan i32@0, volume i32@4"`,
with types `u8` to `u64`, `i8` to `i64`, `f32` and `f64`), and `record` with `recordLayout` splits data
into equal records (`recordByVersion` when the size changed with FL's version). `tree` then shows each
named number.

## Run latest (testing at home)

`tools/dev/Run latest.cmd` runs the newest app straight from GitHub without Visual Studio, and keeps it up to
date while it runs: pick an open pull request (or main), and it builds and starts the app with `dotnet watch`.
Every 20 seconds it checks GitHub, so a change pushed to that pull request shows in the open app by itself:
screens (.axaml) reload in place through HotAvalonia, and C# changes apply live or restart the app.

The first run copies itself to `%LOCALAPPDATA%\MusicOrganiser\run-latest`, with its own clone of the code (so it
never touches Visual Studio's), and puts a "Music Organiser (latest)" shortcut on the Desktop.

While testing, the app keeps a diary of each run in `logs\` in its data folder (the build, the pages opened,
every warning and error), and F12 saves a note for Claude with a picture of the window in `notes\`. Claude reads
both folders from the PC, so nothing needs copying out. A note being written is kept as it's typed and holds off
the launcher's automatic restarts until it's saved, so a new build never loses it.

Settings › Self-check runs the app's checks against the real library (folders there, every project opens, how fast
the sound library loads, the plugin scanner, sound output, how the galaxy draws) and writes a report to
`reports\checks` in the data folder, for Claude to read the same way. It only reads.

## Theory bench

`theorybench` measures the app's harmony code on real music: its key finder, chord readers, the MIDI page's fit
and red marks, and the Playground's fit and red marks, run over the POP909 songs (909 Chinese pop songs as MIDI, with
melody, bridge and piano parts plus chord, key and beat labels; MIT licence). Use it to check a change to how the app
hears harmony before shipping it: run it before and after, and compare.

Get the songs once, outside the repository (about 76 MB; they are never committed):

```
git clone --filter=blob:none --sparse https://github.com/music-x-lab/POP909-Dataset.git pop909
cd pop909 && git sparse-checkout set --no-cone '/POP909/*/*.mid' '/POP909/*/*.txt'
```

Then, from the repository folder:

```
dotnet run -c Release --project tools/MusicOrganiser.TheoryBench -- path/to/pop909/POP909             # everything
dotnet run -c Release --project tools/MusicOrganiser.TheoryBench -- path/to/pop909/POP909 --fitonly   # fit scores and red marks
dotnet run -c Release --project tools/MusicOrganiser.TheoryBench -- path/to/pop909/POP909 --keysonly  # keys
dotnet run -c Release --project tools/MusicOrganiser.TheoryBench -- path/to/pop909/POP909 --clips     # keys on 8-bar clips
```

It can also read a folder of whole-song MIDI files, one track per instrument, such as the construction kits in gospel,
jazz and soul MIDI packs. In each file, the melody is a Lead, Flute, Sax or Talk Box track, or another part the app calls
a melody. It is tested against the file's other parts, and the key written in the file or folder name ("Ebmin",
"F#maj") is checked against the app's key. Subfolders at the first level are reported as styles. The files are only
read; keep bought packs out of the repository.

```
dotnet run -c Release --project tools/MusicOrganiser.TheoryBench -- --library "path/to/New Midi" [--verbose]
```

`--verbose` lists each file whose key the app got wrong.

Things to know:

- Only songs whose MIDI beats line up with the labelled beats are used (387 of them). The chord, key and beat labels
  were made by software, so the absolute chord and key numbers are rough; comparing two versions of the app is the
  reliable part.
- Ranking numbers are "times in 100 a song's own melody scores higher than another song's melody over the same
  chords" (AUC). Red-mark numbers are the share of a song's own melody notes the app would mark red: lower is better,
  since those notes are what the song really plays.
- Lines saying "as it is" call the app's own code, so they show what the app does now. `Together` and `Fit` in
  `Program.cs` are copies of the app's scores as they were before the 2026-10-20 fixes, with a switch for each idea
  measured then; they stay as a fixed point to compare against.
- The 2026-10-20 check-up and its numbers: https://claude.ai/artifact/5Rr3Y59gLCo9Q3Xh3Cohqp
