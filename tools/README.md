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
| `fields bitwig\|fl [<id or name>]` | What the app knows: the field registry. With an id or name, also what it means, how that was found out and where the code uses it. |

Ids are written the way each format numbers them: hex for Bitwig (`0x2c8`), plain numbers for FL Studio
events (`156`).

### The loop for working out a format

1. Save a project, change one thing in the DAW (a knob, a name, a note), and save it under another name.
2. `formatlab diff before.flp after.flp` names what changed. `find` locates a value you typed in the DAW.
3. When it's worked out, add it to the registry with how you know (below), so `tree` names it from then on.

MO Probe helps with plugin state and automation: its saved state starts with `MOPROBE1`, its knobs have
ids 1001 to 1008, and its reports say what the DAW sent it and when it saved (`native/README.md`).

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
