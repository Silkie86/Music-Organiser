# Music Organiser

A Windows desktop app that keeps track of all your music projects across Bitwig Studio, FL Studio and Studio One / Fender Studio Pro. It works offline and keeps everything in a local library on your PC.

## What it does

- **Finds your projects** in the folders you choose (DAW autosave and backup folders are skipped).
- **Shows what each project uses**: plugins (instruments, effects, note FX) and samples.
- **Flags what's missing**: plugins that aren't installed on this PC, and samples that can't be found.
  - *Find matches* searches your sample folders for the same file name or similar names, and can copy a match into the project folder so the DAW finds it.
  - *Find similar* lists installed plugins with close names (e.g. a newer version).
  - *Search online* opens a web search for the plugin or sample.
- **Spots your Elektron boxes** (Analog Rytm MKII, Digitakt II, Syntakt) in a project through their Overbridge plugins.
- **Exports MIDI** (one .mid per pattern or part) and **plugin presets** (where the format allows).
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
| FL Studio (.flp) | Yes, with vendor and instrument/effect | Yes | Yes, per pattern | VST states as .fxp, FL plugin states raw |
| Studio One / Fender Studio Pro (.song) | Yes | Yes | Experimental | Yes, the stored .vstpreset/.fxp files |
| Bitwig (.bwproject) | Best effort* | Best effort* | No | No |

\* Bitwig's project format is closed. The app finds plugins and samples by scanning readable text inside the file and recognising names of plugins installed on your PC, plus audio in the project's `samples` and `recordings` folders. Export MIDI from Bitwig itself.

## Running it

You need **Visual Studio 2026** (the free Community edition) with the **.NET desktop development** workload.

1. Open `MusicOrganiser.sln`.
2. Make sure `MusicOrganiser.App` is the startup project (right-click → *Set as Startup Project*).
3. Press **F5**.

On first run, go to **Settings** and add your project folders, sample folders and (if they're not in the standard places) plugin folders.

Hot Reload works for most changes while the app is running.

## Layout

- `src/MusicOrganiser.Core` – file readers, MIDI writer, library database (SQLite), scanner, match finder. No UI.
- `src/MusicOrganiser.Core/Sounds` – the Sound Lab engine: audio decoding, DSP and analysis, key/chord theory, Basic Pitch and CLAP (ONNX), the classifier, rediscovery and kits.
- `src/MusicOrganiser.App` – the WPF app (MVVM with CommunityToolkit.Mvvm, Fluent styling with WPF-UI).
- `tests/MusicOrganiser.Core.Tests` – xUnit tests using small generated project files. Run with `dotnet test`.

Your library lives in `%LOCALAPPDATA%\MusicOrganiser\library.db`.
