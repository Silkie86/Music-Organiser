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
- `src/MusicOrganiser.App` – the WPF app (MVVM with CommunityToolkit.Mvvm, Fluent styling with WPF-UI).
- `tests/MusicOrganiser.Core.Tests` – xUnit tests using small generated project files. Run with `dotnet test`.

Your library lives in `%LOCALAPPDATA%\MusicOrganiser\library.db`.
