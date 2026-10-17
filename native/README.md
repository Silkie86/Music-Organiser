# Native tools

C++ tools that the app uses but that can't be written in C#. Each builds on Windows, macOS and Linux
with CMake, and CI builds and tests them on all three.

| Tool | What it is |
|---|---|
| `mo-scan` | The plugin scanner. Loads one plugin file and prints what's inside as JSON lines. |
| `MO Probe` | A test plugin (instrument and effect) that reports what a DAW does with it. |

## Building

The app builds the scanner for you: building `MusicOrganiser.App` (or the Avalonia app, or the tests)
runs `build-scanner.cmd` (Windows) or `build-scanner.sh` (Mac, Linux) while the scanner isn't built
and whenever its code changes, then copies `mo-scan` next to the app. That needs CMake and a C++
compiler. In Visual Studio 2026 that's the **Desktop development with C++** workload (Visual Studio
Installer → Modify), which includes CMake; on a Mac, Xcode's command line tools and CMake. Without
them the app still builds and runs, with a build warning, and just can't read plugin details. What
went wrong is in `native/out/app/build.log`, and the Plugins screen says it too. Once they're
installed, F5 builds the scanner: Visual Studio keeps building the app's projects until it's there.
`-p:MoBuildNative=false` turns this off; on CI it's off unless asked for (the Windows build job asks).

By hand, everything including the probe and the tests:

```
cmake -S native -B native/build
cmake --build native/build --config Release
ctest --test-dir native/build -C Release
```

That builds the scanner and the CLAP probe offline: the CLAP and VST3 headers it needs are in
`third_party` (both MIT licensed). Add `-DMO_PROBE_WRAPPERS=ON` to also build the probe as a **VST3**
(and an **AU** on macOS) with [clap-wrapper](https://github.com/free-audio/clap-wrapper). That
downloads clap-wrapper and the VST3 SDK with git.

Ready-built copies for all three systems, including the VST3 and AU probes, are attached to every CI
run on GitHub (Actions → Build → a run → Artifacts → `music-organiser-native-Windows`).

Output: `native/build/bin/mo-scan(.exe)` and `native/build/plugins/` (`MO Probe.clap`, and with the
wrappers `MO Probe.vst3` and `MO Probe.component`).

You can also open the `native` folder in Visual Studio (File → Open → Folder) to work on the C++
code; it picks up `CMakePresets.json`.

## mo-scan

```
mo-scan [--no-params] [--state] [--curve <steps>] [--load <state file> [--load-program]] [--calibrate <steps> [--ids <from>-<to>]] [--only <plugin id>] [--format clap|vst3|vst2] <plugin path>
mo-scan --version
```

Prints one JSON object per plugin in the file (a CLAP or VST3 file can hold several; a VST2 "shell"
like Waves holds many):

```json
{"format":"VST3","path":"C:\\Program Files\\Common Files\\VST3\\MO Probe.vst3","id":"B0DDF78C9D004BA9B2359D3E7B74D7F1",
 "name":"MO Probe Instrument","vendor":"Music Organiser","version":"1.0.0","kind":"instrument",
 "features":["Instrument","Synth"],"audioIn":0,"audioOut":2,"noteIn":1,"noteOut":0,
 "params":[{"id":1001,"name":"MO Knob A","units":"","steps":0,"default":0.25,"value":0.25,"text":"0.250",
            "automatable":true,"readOnly":false,"list":false,"hidden":false,"bypass":false, ...}, ...]}
```

- **id** is the exact plugin ID: the VST3 class ID (as 32 hex digits, the four 32-bit words), the
  CLAP ID, or the VST2 four-letter ID. These are what DAW project files store, so matching by ID
  replaces guessing by name.
- **params** has every parameter: ID, name, units, range, default and current value, the value as
  the plugin shows it, and flags (automatable, stepped, list, hidden, bypass).
- `--no-params` only lists the plugins in the file. CLAP and VST3 plugins aren't even started, and a
  VST2 shell's plugins are listed without opening each one, so this is quick. The app reads every
  plugin file this way in the background, and a plugin's parameters only when you ask. A VST3 whose
  bundle has a `Contents/Resources/moduleinfo.json` (VST3 SDK 3.7.5 and later) isn't scanned at all
  for that: the app lists it from that file (`Vst3ModuleInfo.cs`).
- `--only <id>` reads one plugin from the file, e.g. one from a Waves shell.
- `--state` adds the plugin's default state (base64), for working out how its settings are stored.
  (Scanning the probe with `--state` writes a probe report, since saving its state counts as using it.)
- `--curve <steps>` adds each parameter's text at that many even steps from 0 to 1 (`"curve":[{"x":0,"text":"0.00"},…]`),
  with the value in real units for VST3 (`"plain"`). A VST2 knob is set to each step and put back. It's for matching
  one plugin's knobs to another's, such as Serum's to Serum 2's.
- `--load <state file>` hands the plugin that saved state before anything is read (a VST2 bank chunk, or with
  `--load-program` one program; a VST3 component state, given to the controller too), then prints the knobs and
  the state it ends up with, plus `"loadResult"`. It's for seeing whether a plugin can open another's saved sound,
  such as Serum 2 opening Serum 1's. With `--no-params` the state only goes to the component, since Serum 2's
  controller crashed when handed a state of its own outside a DAW.
- `--calibrate <steps>` (VST3) starts the plugin's audio, sets each parameter to that many even steps from 0 to 1
  through the audio processor, as a DAW's automation does, and prints one line per parameter with the state saved at
  each step (`{"calibrate":<id>,"steps":[{"x":0,"state":"..."},…]}`), after a first line with the starting state.
  A switch or list is stepped through each of its choices (up to 256) instead.
  `--ids <from>-<to>` keeps it to those parameter IDs. It's for working out how a plugin stores each knob: Serum 2
  saves its own units (seconds, percent, amplitude), not the 0 to 1 a DAW sees.
- A plugin that can't be loaded gives `{"path":..., "error":"..."}` and exit code 2. A 32-bit
  VST2 says so.

Plugins print all sorts of things; that goes to stderr, so stdout is only JSON. The app runs
`mo-scan` once per plugin file (four at a time) with a 30-second limit, so a plugin that crashes or
hangs only takes the scanner down, not the app. On Windows the scanner turns off the system's crash
and missing-DLL boxes, so a bad plugin can't leave one waiting. Plugins aren't unloaded at the end
because many crash when they are; the process just exits.

VST2: Steinberg no longer licenses the VST2 SDK, so the scanner declares the few structures a host
needs from the binary interface itself (`scan_vst2.cpp`). AU (macOS) scanning is still to do.

## MO Probe

Two plugins, **MO Probe Instrument** (plays a quiet sine per note) and **MO Probe Effect** (gain),
vendor "Music Organiser". Put one on a track, save, and:

- **Its saved state is text starting with `MOPROBE1`**, followed by JSON with its parameter values,
  a random identity and a save counter. Search a project file for `MOPROBE1` to see where and how
  the DAW stores plugin state (plain, zlib, base64, with what header).
- **Its eight parameters are easy to spot**: IDs 1001-1008, named "MO Knob A", "MO Gain" (-60 to
  +12 dB), "MO Frequency" (20-20,000 Hz), "MO Steps" (0-7), "MO Switch", "MO Choice"
  (Alpha/Bravo/Charlie/Delta), "MO Ramp" (0-100 %) and "MO Bypass". Automate "MO Ramp" with a known
  shape to see how the DAW stores automation.
- **It writes a live report** to the app's data folder, one file per plugin instance:
  - Windows `%LOCALAPPDATA%\MusicOrganiser\probe`
  - macOS `~/Library/Application Support/MusicOrganiser/probe`
  - Linux `~/.local/share/MusicOrganiser/probe` (or under `$XDG_DATA_HOME` when that's set)

  Each line is JSON with a time (`t`), an order number (`n`) and an `event`:

  | event | when |
  |---|---|
  | `create` | the DAW creates the plugin: host name, vendor and version, format (CLAP, VST3, AU) |
  | `track` | the DAW says which track it's on: name, colour, return/bus/master (CLAP hosts like Bitwig) |
  | `activate` | sample rate and block sizes |
  | `play`, `stop`, `jump`, `tempo`, `time-signature`, `loop` | transport changes |
  | `note-on`, `note-off`, `note-choke`, `note-expression`, `midi` | each note, with its exact position in beats, seconds and bar |
  | `param`, `param-mod`, `param-last` | parameter changes and automation, with position |
  | `mapping`, `automation` | the DAW marks a knob as mapped to a controller or automated |
  | `save`, `load` | state saved or loaded, and whether for a project, a preset or a duplicate |
  | `render` | offline bounce starts or ends |

  The file only appears once the plugin is really used (activated, saved, loaded or put on a
  track), so a DAW scanning its plugins doesn't leave empty reports.

**Installing it.** Copy `MO Probe.clap` to `C:\Program Files\Common Files\CLAP` (or
`%LOCALAPPDATA%\Programs\Common\CLAP`), and `MO Probe.vst3` to `C:\Program Files\Common Files\VST3`.
On macOS the folders are `/Library/Audio/Plug-Ins/CLAP`, `VST3` and `Components`; on Linux `~/.clap`
and `~/.vst3`. Then rescan plugins in the DAW.

**Reading its reports.** The app's Settings has an MO Probe card: whether the probe is installed, and its
newest reports (the DAW and its version, the format, the track, and how many saves, notes and knob
moves). `formatlab probe "My song.flp"` finds the probe's saved state inside a project, with its knob
values, and says which save of which report wrote it (see `tools/README.md`).

**Following one probe across sessions.** The identity in the state survives save and load, and a
`label` can be set by whoever writes the state (for the app's round-trip check of the Bitwig
writer: put a probe with a known label on each track, open the project in Bitwig, press play, and
compare the reported notes with what the song should play).

### Talking to it while it runs

Once a probe is in use (when its report starts), it keeps a **status file** up to date and takes
**commands** from files, so anything that can read and write files can talk to it: the app (Settings'
MO Probe card, and its knob check through Bitwig), `formatlab probe live` and `probe send`, and Claude
through file tools. Both are in `live` inside the report folder, named after the instance (the
16-digit `instance` in its report):

| File | What it is |
|---|---|
| `live/<instance>.json` | its status, rewritten about every half second |
| `live/<instance>.in/` | its command folder: put `.json` files in here |

Both go when the DAW removes the plugin. A status whose `t` is more than 3 seconds old is from a DAW
that went away without saying so: treat that probe as gone. One over a minute old can be deleted, with
its `.in` folder (probes starting up clear those too).

**The status** is one JSON object, written to `<instance>.json.tmp` and renamed over the old one, so a
reader never sees half of it:

```json
{"probe":"instrument","instance":"1683A80113A64904","identity":"1683A80113A64904","label":"",
 "host":"Bitwig Studio","hostVersion":"6.1.3","format":"CLAP","track":"Inst 3","trackColor":"#C69F6F",
 "report":"20261014T091855Z-instrument-1683A801.jsonl","t":"2026-10-14T09:30:00.123Z","pid":1234,
 "active":true,"playing":false,"beats":12.5,"bar":4,"tempo":110,"commands":3,
 "params":{"1001":0.25,"1002":0,"1003":440,"1004":3,"1005":0,"1006":1,"1007":50,"1008":0}}
```

- `identity` and `label` are the ones in its saved state; `track` and `trackColor` are what the DAW
  last said (empty if it doesn't say); `report` is its report file, in the folder above.
- `active` while the DAW has it switched on. `playing`, `beats` (song position), `bar` and `tempo`
  are from the last block it played, and left out when the DAW gives no transport.
- `params` has each knob's value in its own units (dB, Hz and so on, as listed above). `commands`
  counts the command lines it has handled.

**Commands.** Write a file ending in `.json` into the `.in` folder: one command per line, each a JSON
object with a `cmd`. Write it under another name (say `.tmp`) and rename it where you can; the probe
leaves alone any file changed in the last 150 ms anyway. About every 100 ms it reads the files in name
order, deletes each one and handles its lines, then writes its status straight away. Give a command an
`id` (any text) and its answer in the report carries it as `for`:

```
{"cmd":"ping","id":"a1"}
{"cmd":"set","param":1002,"value":-12,"id":"a2"}
```

| cmd | fields | what it does | report line |
|---|---|---|---|
| `ping` | | answers, and writes its status | `pong` |
| `set` | `param` (1001-1008), `value` | sets the knob, in its own units (clamped; text like `"On"`, `"Bravo"` or `"-6 dB"` works too), and tells the DAW it moved as if turned by hand, so the DAW's knob, automation recording and undo follow | `app-set` with `id`, `name`, `value` and `told`; then `app-set-sent` once the DAW has been told |
| `label` | `label` | keeps it as its label (up to 64 characters; quotes, backslashes and control characters are dropped), saved in its state, and tells the DAW the project has changed | `label` |
| `beep` | `seconds` (0.3 if left out, at most 2) | plays a quiet 880 Hz tone on its output, so you can hear which track it's on | `beep` |

`told` says how the DAW hears about a `set`: `process` (the next block it plays tells it), `flush` (it
isn't playing blocks, so the probe asked the DAW for a flush) or `waiting` (the DAW can't be asked;
it's told when it next plays or flushes). A line that isn't a JSON object with a `cmd`, or a command
the probe doesn't know, gives `command-error` with the `error`, `file` and `line`. Files are read as
UTF-8 (a UTF-16 file, as Windows PowerShell's `>` writes, is fine too).

**For Claude**, through file tools that list, read and write whole files: list `live`, read a status
(a fresh `t` means it's running), and write each command as a new file in its `.in` folder (files
can't be added to once written, and the probe deletes them once read). Then read the file named in
`report` for the lines with your `for`, or read the status again for the new knob value.

## Layout

```
native/
  CMakeLists.txt          top level; MO_PROBE_WRAPPERS option
  CMakePresets.json       for Visual Studio's Open Folder
  MusicOrganiser.Native.targets   builds the scanner from the .NET projects (imported by Core)
  build-scanner.cmd/.sh   what that runs: the scanner only, into out/app
  mo-scan/src/            scanner: main, CLAP, VST3, VST2, loading libraries, JSON
  mo-probe/src/           probe: plugin, report writer, live status and commands, entry point
  tests/                  probe_test.cpp (hosts the probe), check_scan.cmake (scans it),
                          fake_vst2.cpp + check_vst2.cmake (a stand-in VST2 shell)
  third_party/            CLAP headers, VST3 pluginterfaces (MIT)
```
