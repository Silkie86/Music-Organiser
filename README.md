# Music Organiser

A Windows desktop app that keeps track of all your music projects across Bitwig Studio, FL Studio and Studio One / Fender Studio Pro. It works offline and keeps everything in a local library on your PC.

## What it does

- **Finds your projects** in the folders you choose (DAW autosave and backup folders, and the demo, tutorial and template folders that come with the DAW, are skipped).
- **Shows what each project uses**: plugins (instruments, effects, note FX) and samples.
- **Flags what's missing**: plugins that aren't installed on this PC, and samples that can't be found.
  - *Find matches* searches your sample folders for the same file name or similar names, and can copy a match into the project folder so the DAW finds it.
  - *Find similar* lists installed plugins with close names (e.g. a newer version).
  - *Search online* opens a web search for the plugin or sample.
- **Spots your Elektron boxes** (Analog Rytm MKII, Digitakt II, Syntakt) in a project through their Overbridge plugins.
- **Shows and exports MIDI** per channel or track (FL Studio patterns placed as in the playlist), with what plays each one and a mini piano roll, plus **plugin presets** (where the format allows): all at once, filtered to third-party or FL plugins and synths or effects, or one plugin at a time. Synths that play no notes in an FL song are tucked away in the plugin list.
- **Reads your installed plugins** with its own plugin scanner (`native/mo-scan`, C++): every plugin inside each VST3, CLAP and VST2 file (including the hundreds in a Waves shell), with the exact ID projects save it under, its vendor, version and kind. Select a plugin on the Plugins screen to see this, and press *Read parameters* to list its parameters. Each plugin file is read in its own process with a time limit, so a plugin that crashes or hangs can't take the app down, and only new or updated files are read again.
- **Checks its reading against the DAW**: small scripts for Bitwig and FL Studio, installed from Settings, let the app ask the DAW what it shows for the open project. *Check with Bitwig* or *Check with FL Studio* on a project then compares that with what the app read from the file (tempo, tracks or channels, devices and plugins). See `extensions/README.md`.
- **Opens projects** in their DAW, or shows them in Explorer.
- **Organise however you like**: status (Idea → Released), rating, favourite, musical key, tags and notes. Filter, search and group by DAW, status, folder, month, year, Elektron box or rating.

## Sound Lab (your sample library, learned)

The first screen. Point it at your sample folders (Settings) and it listens to every sound, offline:

- **Type sorting that learns your library.** Kicks, snares, hats, 808s, pads, vocals, loops and more. It starts from your folder and file names, trains a classifier on them, then sorts the sounds whose names don't say. Every label you correct counts three times as much on the next retrain, and the Teach tab lists sounds where what it hears disagrees with the name.
- **Your own tags, learned.** Tag five or more sounds with any word ("dusty", "signature") and it suggests that tag for other sounds that share the quality.
- **Pitch, key and chords.** Root note of one-shots (YIN), key with Camelot code for loops, a chord timeline, and "keys that mix well" filtering. **Detect notes** transcribes a sound's notes with Spotify's Basic Pitch model and saves them as MIDI.
- **Tempo** from the file's ACID tag, its name, or the audio (onset autocorrelation snapped to whole bars).
- **Rediscover**: sounds you haven't used, matched to your taste (what your scanned projects use, favourites, plays) and kept varied. Plus "forgotten folders" ranked by how little of them you use.
- **Kit builder**: random drum kits with locks, per-pad re-rolls, "favour unused", "one pack", a key for bass/808 pads and a described vibe. Exports a numbered folder, optionally Elektron-ready (48 kHz 16-bit) for Digitakt II / Analog Rytm via Elektron Transfer.
- **Sounds like this**: nearest neighbours by timbre.

### The models

| Model | What it does | Where it comes from |
|---|---|---|
| Sound features + classifier | Types, tags, sound-alikes, taste | Built in; trains on your PC in seconds |
| Basic Pitch (Spotify, Apache 2.0) | Note and chord transcription | Built in (`Models/basic-pitch.onnx`, 230 KB) |
| CLAP (LAION, via `Xenova/larger_clap_music_and_speech`) | Search by description, vibe kits, sharper types and sound-alikes | Optional one-time download (~205 MB) from the Teach tab, then **Deep listen** |

All models run locally with ONNX Runtime. Nothing is uploaded. Analysis lives in `%LOCALAPPDATA%\MusicOrganiser\sounds.db`, the CLAP model in `%LOCALAPPDATA%\MusicOrganiser\models\clap`.

WAV and AIFF are decoded by the app; MP3, FLAC, M4A and OGG go through Windows Media Foundation.

## How much each format gives up

| DAW | Plugins | Samples | MIDI export | Preset export |
|---|---|---|---|---|
| FL Studio (.flp) | Yes, with vendor and instrument/effect | Yes | Yes, per channel, placed as in the playlist (FL 9 and later; older files pattern by pattern) | VST2 as .fxp (chunk or parameter presets: load into the VST2 version of the plugin), VST3 as .vstpreset, FL plugin states raw (FL only) |
| Studio One / Fender Studio Pro (.song) | Yes | Yes | Experimental | Yes, the stored .vstpreset/.fxp files |
| Bitwig (.bwproject) | Yes, with vendor and format* | Yes | Yes, per track (clips one after another)* | Yes, the stored .vstpreset/.fxb files |

\* Bitwig's format isn't documented. The app decodes it with its own reader (`BitwigFormat.cs`), mapped from projects saved by Bitwig 5.1 to 6.1, and checks that each file decodes exactly to the end. If a project can't be decoded (for example a future Bitwig version), it falls back to scanning readable text for plugin and sample names, and says so on the project.

## Running it

You need **Visual Studio 2026** (the free Community edition) with the **.NET desktop development** workload. For the plugin scanner, add the **Desktop development with C++** workload too (Visual Studio Installer → Modify). Without it everything else still works, and the build shows a warning saying the scanner wasn't built.

1. Open `MusicOrganiser.sln`.
2. Make sure `MusicOrganiser.App` is the startup project (right-click → *Set as Startup Project*).
3. Press **F5**. The first build also builds the plugin scanner, which takes a minute; after that it's only rebuilt when its code changes.

On first run, go to **Settings** and add your project folders, sample folders and (if they're not in the standard places) plugin folders.

Hot Reload works for most changes while the app is running.

## Layout

- `src/MusicOrganiser.Core` – file readers, MIDI writer, library database (SQLite), scanner, match finder. No UI.
- `src/MusicOrganiser.Core/Sounds` – the Sound Lab engine: audio decoding, DSP and analysis, key/chord theory, Basic Pitch and CLAP (ONNX), the classifier, rediscovery and kits.
- `src/MusicOrganiser.ViewModels` – the screens' logic (MVVM with CommunityToolkit.Mvvm), with no UI framework in it. Anything it needs from the UI (lists that filter and sort, dialogs, timers, sound, popups) goes through `IPlatformUi` in `Platform.cs`, so the Windows app and a future Mac/Linux (Avalonia) app share it.
- `src/MusicOrganiser.Avalonia` – the same app on Avalonia, for Windows, macOS and Linux. Every screen is moved over: Projects (with the details panel), Sound Lab, Plugins, Samples, Settings and the MIDI and Presets windows. Dragging files out to a DAW is WPF-only for now. It plays sound through OpenAL Soft (WAV and AIFF files, and MIDI previews; MP3, FLAC and OGG still need a decoder there). To try it in Visual Studio, right-click it → *Set as Startup Project* and press F5.
- `src/MusicOrganiser.App` – the Windows (WPF) app: XAML views, Fluent styling with WPF-UI, NAudio playback, and `Platform/WpfUi.cs`.
- `native` – C++ tools, built with CMake: the plugin scanner (`mo-scan`) and **MO Probe**, a test plugin (CLAP, VST3, AU) that reports what a DAW does with it, for working out project formats. See `native/README.md`.
- `extensions` – the Bitwig controller script and FL Studio MIDI script that link the app with the DAWs, each tested in a stand-in DAW. See `extensions/README.md`.
- `tests/MusicOrganiser.Core.Tests` – xUnit tests using small generated project files. Run with `dotnet test`.

Your library lives in `%LOCALAPPDATA%\MusicOrganiser\library.db`.
