# Native tools

C++ tools that the app uses but that can't be written in C#. Each builds on Windows, macOS and Linux
with CMake, and CI builds and tests them on all three.

| Tool | What it is |
|---|---|
| `mo-scan` | The plugin scanner. Loads one plugin file and prints what's inside as JSON lines. |
| `MO Probe` | A test plugin (instrument and effect) that reports what a DAW does with it. |

## Building

The app builds the scanner for you: building `MusicOrganiser.App` (or the Avalonia app, or the tests)
runs `build-scanner.cmd` (Windows) or `build-scanner.sh` (Mac, Linux) the first time and whenever
the scanner's code changes, then copies `mo-scan` next to the app. That needs CMake and a C++
compiler. In Visual Studio 2026 that's the **Desktop development with C++** workload (Visual Studio
Installer → Modify), which includes CMake; on a Mac, Xcode's command line tools and CMake. Without
them the app still builds and runs, with a build warning, and just can't read plugin details. What
went wrong is in `native/out/app/build.log`. `-p:MoBuildNative=false` turns this off; on CI it's off
unless asked for (the Windows build job asks).

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
mo-scan [--no-params] [--state] [--only <plugin id>] [--format clap|vst3|vst2] <plugin path>
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
  plugin file this way in the background, and a plugin's parameters only when you ask.
- `--only <id>` reads one plugin from the file, e.g. one from a Waves shell.
- `--state` adds the plugin's default state (base64), for working out how its settings are stored.
  (Scanning the probe with `--state` writes a probe report, since saving its state counts as using it.)
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
  - Linux `~/.local/share/MusicOrganiser/probe`

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

## Layout

```
native/
  CMakeLists.txt          top level; MO_PROBE_WRAPPERS option
  CMakePresets.json       for Visual Studio's Open Folder
  MusicOrganiser.Native.targets   builds the scanner from the .NET projects (imported by Core)
  build-scanner.cmd/.sh   what that runs: the scanner only, into out/app
  mo-scan/src/            scanner: main, CLAP, VST3, VST2, loading libraries, JSON
  mo-probe/src/           probe: plugin, report writer, entry point
  tests/                  probe_test.cpp (hosts the probe), check_scan.cmake (scans it),
                          fake_vst2.cpp + check_vst2.cmake (a stand-in VST2 shell)
  third_party/            CLAP headers, VST3 pluginterfaces (MIT)
```
