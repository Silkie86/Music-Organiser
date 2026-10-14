# DAW extensions

Two small scripts that run inside the DAWs, so the app can ask a DAW what it shows for the open project.
The app keeps up with both while they're open (see [Staying connected](#staying-connected)), and uses
them to check how it reads project files (**Check with Bitwig** and **Check with FL Studio** on a project,
which also run by themselves when the DAW has that project open), to send things into Bitwig (FL Studio
channels with their sounds, knobs and notes, and Sound Lab kits; see
[Sending into Bitwig](#sending-into-bitwig)), and later to learn what plugin settings and file changes
mean.

| Folder | Runs in | How the app talks to it |
|---|---|---|
| `bitwig` | Bitwig Studio, as a controller script | TCP on port 19791, on this computer only |
| `fl-studio` | FL Studio, as a MIDI controller script | Files in the app's data folder |

Neither changes anything in a project unless you turn that on (see [Changes](#changes)).

## Installing

In the app: **Settings → DAW links → Set up both** installs (or updates) each DAW's script, skipping a DAW
the app doesn't find on this computer, makes FL Studio's MIDI port the way chosen in Settings, then checks
each link and says what's left to do. Each DAW also has its own **Install** button. The scripts ship next
to the app (`Extensions` in its folder), and installing copies them where the DAW looks:

- **Bitwig**: `Documents\Bitwig Studio\Controller Scripts\Music Organiser` (on Linux, `~/Bitwig Studio/...`).
  Then in Bitwig: Settings → Controllers → Add controller → Music Organiser → DAW link.
- **FL Studio**: `Settings\Hardware\Music Organiser` in FL Studio's user data folder (normally
  `Documents\Image-Line\FL Studio`). The app also finds user data folders elsewhere from where your FL
  projects are, and **Choose folder…** points it at one. FL Studio only runs a script for a MIDI input, and
  the app and the script talk over that port, so it has to be one the app can send into: a port called
  Music Organiser, which gets the script by itself after a restart of FL Studio. In Options → MIDI settings,
  the Music Organiser output needs the same Port number as the input, since the script answers through it.
  On Windows, Settings offers two ways to make that port:
  - **loopMIDI**: [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html), a free app, makes it
    (type Music Organiser under *New port-name* and press +). Works on any Windows; loopMIDI has to be
    running for the port to be there. Settings says whether loopMIDI is installed, and **Get loopMIDI**
    opens its page.
  - **Windows' own MIDI system**: Install also makes the port, with Windows MIDI Services, so there's
    nothing else to install: a saved loopback pair, *Music Organiser* and *Organiser link (spare end)*,
    which Windows makes again after a restart (`Core/DawLink/WindowsMidiPort.cs`). Apps can only make one
    through Windows' own API, which Windows 11 25H2 gets with an update expected late in November 2026
    (24H2 never gets it). The app calls it through Windows PowerShell, so it needs nothing extra to build,
    and when picking this way, Settings checks whether this PC can make the port and says why not if it
    can't.

  On a Mac, the IAC Driver (Audio MIDI Setup) makes the port.

Settings says when an installed copy is older than the app's, and Install updates it. **Check connection**
asks the DAW to answer. When FL Studio doesn't, the app says which part is missing, on Windows: FL Studio
isn't open, there's no MIDI port called Music Organiser (and how to make one, the way chosen in Settings),
or there is one but FL Studio isn't running the script for it (`Core/DawLink/FlSetup.cs`).

## Changes

Reading is always allowed. Changing things needs your say-so in the DAW:

- **Bitwig**: Settings → Controllers → Music Organiser → *Let the app make changes*. Then the app can add
  tracks, insert plugins, devices, presets and samples, put MIDI files in clip slots, fill a Drum
  Machine's pads, set the pitch of a Sampler or Note Transpose, set the selected device's parameters, and
  run actions such as Save.
- **FL Studio**: set `LET_THE_APP_MAKE_CHANGES = True` near the top of `device_MusicOrganiser.py` (in the
  folder above) and reload the script. Then the app can set plugin parameters. Updating the script from
  Settings keeps this line as you set it.

## Staying connected

While the app runs, it asks both DAWs every 2 seconds what they have open (`Core/DawLink/DawWatch.cs` and
`ViewModels/LiveDawsViewModel.cs`). Its connection to Bitwig stays open between looks (the script takes
more than one connection at a time, so the app's other features connect alongside it). FL Studio is only
asked while it's open, as far as this computer can tell, and not while another screen is asking it
something. A DAW that doesn't answer is asked again 6 seconds later; one that answered is only shown as
gone after three missed looks in a row.

- The sidebar's **In your DAWs** shows the project each DAW has open, whether it has unsaved changes, and
  whether the library has it. Clicking one shows that project, or Settings while the DAW isn't linked.
- The open project is found in the library by name (Bitwig's project name, or the name in FL Studio's
  window title), the newest save first. The projects list marks it *Open in Bitwig* or *Open in FL
  Studio*, and its page says so and checks it with the DAW by itself, once, showing the result under the
  buttons.
- When a DAW saves (its unsaved changes clear, or it shows another project after Save As), the app re-reads
  the file as soon as the DAW has finished writing it. A project the library doesn't have yet is found by
  a scan for new projects, at most once a minute.
- Sending FL Studio channels to Bitwig while FL Studio has the project open reads each plugin's knobs in
  FL Studio first (see [Sending into Bitwig](#sending-into-bitwig)).

## Bitwig link

`MusicOrganiser.control.js`, plain ES5 against Bitwig's controller API 18.

The script listens on port 19791 (`host.createRemoteConnection`). Each message is UTF-8 JSON with a
4-byte big-endian length in front, which is how Bitwig's remote connections frame messages; the script
also ends each message with a newline, so the app reads them either way. Text outside ASCII is sent as
`\u` escapes. When the app connects, the script greets it with `{"event": "hello", "result": {...}}`.

Requests are `{"id": 1, "cmd": "...", ...}`. Answers are `{"id": 1, "ok": true, "result": ...}`, or
`{"id": 1, "ok": false, "error": "..."}` with the reason in words the app shows as they are.

| Command | Asks for | Result |
|---|---|---|
| `hello` | | `link` (this protocol's version, now 2), `product`, `version`, `api`, `platform`, `port`, `project`, `modified`, `engine`, `changes` |
| `project` | | `name`, `modified`, `tempo`, `timeSignature`, `playing`, `position`, `trackCount`, `tracks`: the first 128 tracks in Bitwig's flat list, each with `index`, `name`, `type`, `group`, `color`, `active`, `mute`, `solo`, `arm`, `volume`, `pan` (as shown, with `volumeValue` and `panValue` from 0 to 1), `deviceCount` and the first 16 `devices` (`name`, `preset`, `kind`, `plugin`, `enabled`) |
| `device` | | The selected device: `track`, `device` (or null), `drumPads` (whether it has a Drum Machine's pads) and every parameter as `id` (what Bitwig saves it under), `name`, `value` (0 to 1) and `display` ("-6.0 dB"). Waits up to 2 seconds for the display texts. |
| `actions` | | Every Bitwig action: `id`, `name`, `category`, `menu` |
| `select` | `track` (its index in `project`), `device` (optional) | `{}` |
| `set` ✎ | `param` (an `id` from `device`), `value` (0 to 1) | `{}` |
| `insert` ✎ | `kind`: `vst3` or `clap` with `plugin` (the ID the plugin scanner reads), `vst2` with `plugin` as a number, `bitwig` with `device` (a UUID), or `file` with `path` (a preset, or a sample, which goes in a Sampler). Then `track` (optional) and `at`: `"end"` (the default) or `"start"` of its chain. | Without `track`, at the end of the selected track's chain: `{}`, since Bitwig quietly ignores what it can't use. With `track`, once Bitwig has added it: `added`, `index` and `device` (as in `project`), or `added: false`. |
| `newtrack` ✎ | `name`, `color` (optional, `#rrggbb`) | `index` and `name`. An instrument track after the last main track (before the effect tracks), selected. If adding it opened Bitwig's browser, the script closes it. |
| `clip` ✎ | `track`, `path` (a MIDI or audio file), `slot` (optional) | `slot`: where Bitwig made the clip, by default the first empty one in the first 8 scenes |
| `pads` ✎ | `pads`, each with `note` (0 to 127), `path` and `name` (optional). Then `track` and `device` for a Drum Machine's place, or nothing for the selected device. | `filled`, `skipped` (pads that already had something on them, left alone) and `failed`, as notes. Each sample goes on its pad as if dropped there, in a Sampler. |
| `transpose` ✎ | `track`, `device` (0 or 1), `semitones` | `device`, `value`, `display`. Sets a Sampler's Transpose (fractions allowed) or a Note Transpose's Coarse (whole semitones). |
| `action` ✎ | `action` (an `id` from `actions`) | `{}` |

✎ needs changes turned on. `insert` with a track, `newtrack`, `clip`, `pads` and `transpose` wait for
Bitwig to make the change (up to 5 seconds for each step), and run one at a time in the order sent. In the
app this is `Core/DawLink/BitwigLink.cs`.

### Sending into Bitwig

`Core/DawLink/BitwigSend.cs` builds on these commands:

- **An FL Studio channel** (*To Bitwig* in the MIDI window) gets a new track named after it. A sample goes
  in a Sampler, whose Transpose makes up for the channel's root key and pitch knob (FL Studio plays a sample
  at its own pitch on the root key; a new Sampler does on C3), so it sounds as in FL Studio. A plugin loads
  with the settings the project saved: as a Bitwig preset when Bitwig has a saved preset of that plugin to
  build one from, else as a `.vstpreset`, or for VST2 a one-program `.fxb`. If Bitwig doesn't take those, the
  plugin loads with its default sound by the ID the plugin scanner read, and the channel says so. A moved
  root key puts a Note Transpose before the plugin. The notes go in as a clip in the first empty launcher
  slot. FL Studio's own plugins can't load in Bitwig; their channels still get their notes.
- **FL Studio's knobs**: when FL Studio has the project open, the app reads each plugin's knobs there
  first (a channel only when FL Studio has it with the same plugin), selects the plugin in Bitwig, sets
  the knobs whose value differs, matched by name (ignoring case, spaces and punctuation, since the plugin's
  format can differ), and reads them back to see which took. That brings tweaks made since the last save
  across, and most of the sound when Bitwig doesn't take the saved settings: anything that isn't a knob,
  like a wavetable or a sample, stays at its default, and the channel says so.
- **Read back**: after each channel, the app reads the project back from Bitwig and says if the track, its
  instrument, or the Note Transpose before it isn't there as sent.
- **A Sound Lab kit** (*Send to Bitwig* on the Kit builder) goes on a new track with a Drum Machine, kick on
  C1, snare on D1, clap on D#1, closed hat on F#1, tom on A1, open hat on A#1 and cymbal on C#2 (where
  General MIDI drum parts go), and the other pads on the free notes from C1 up. Adding a Drum Machine needs
  its ID, which the app reads from a Bitwig project or preset that has one (projects in the library first).
  Until it finds one, the kit goes on the Drum Machine selected in Bitwig, leaving pads that have sounds
  alone.

## FL Studio link

`device_MusicOrganiser.py`, for FL Studio's MIDI scripting (Python 3.12 in FL Studio 2024 and later; the
script keeps to 3.9's syntax for FL Studio 21). Plain ASCII, since FL Studio reads its first lines itself.

FL Studio doesn't let scripts open files (`open` fails with "SystemError: <class '_io.FileIO'> returned NULL
without setting an exception") or connect to anything, but they can send and receive MIDI, so the app and
the script talk in SysEx messages on the Music Organiser MIDI port. The app sends a request into the port;
FL Studio hands it to the script's `OnSysEx`, and the script answers through FL Studio's Music Organiser
MIDI output (`device.midiOutSysex`), which is why that output needs the same Port number as the input.
With loopMIDI, both ends hear everything on the port, so each side leaves the other kind of message alone.
With Windows' own loopback pair, the app uses the spare end (`Core/DawLink/FlMidi.cs`, through WinMM).

Each message is `F0 7D 4D 4F`, then the kind (`01` a request, `02` an answer), the request's id in three
7-bit bytes, the part's number and how many parts there are in two 7-bit bytes each (lowest first), up to
512 bytes of the JSON text (plain ASCII), and `F7` (`Core/DawLink/FlMessages.cs`). The script sends a long
answer 8 messages at a time, each time FL Studio calls `OnIdle`, and drops what's left of one when another
request comes in. The app gives up 5 seconds after its request or the last part of the answer, and the
script ignores requests sent more than 15 seconds ago.

Requests are `{"id": 1, "sent": <Unix time in seconds>, "cmd": "...", ...}`, and answers are as for
Bitwig. A plugin is named by `channel` (its place in the whole channel rack, from 0) or by mixer `track`
and `slot`.

| Command | Asks for | Result |
|---|---|---|
| `hello` | | `link`, `product`, `version` ("Producer Edition v25.1.6 [build 4997]"), `api` (FL Studio's scripting version), `title` (the window title, which names the project), `modified` (whether it has unsaved changes), `changes`, `platform`, `script` (the folder FL Studio loaded it from) |
| `project` | | `title`, `tempo`, `modified`, `grouped`, `channels` (`index`, `name`, `type`, `mixer` track, `plugin` or null, `params` count) and `mixer`: each track's `index`, `name` and `plugins` (`slot`, `name`, `label`, `params` count) |
| `params` | a plugin, `from` (optional), `midi` (optional) | `plugin`, `count`, `params` (`index`, `name`, `value` 0 to 1, `text` as the plugin shows it) and `next`: where to carry on, or null at the end. Each page takes about 40 ms at most, so FL Studio never stalls. VST plugins list 4240 parameters: their own 4096, then MIDI CC and aftertouch, which are left out unless `midi` is true. Unnamed parameters are skipped. |
| `set` ✎ | a plugin, `param`, `value` (0 to 1) | The parameter as it now is |

`project`, `params` and `set` need scripting version 26 or later. Before version 33,
FL Studio only lists the channels in the group the channel rack shows, and `grouped` says so. In the app
this is `Core/DawLink/FlLink.cs`.

## Checking the scripts

Each script runs in a stand-in DAW that copies the real API's calls and quirks:

```
cd extensions/bitwig && npm ci && npm test            # type-checked against Bitwig's API, then run
python -m unittest discover -s extensions/fl-studio/test
```

The app's own tests (`dotnet test`) also talk to both scripts end to end through `test/serve.js` and
`test/serve.py` (which stands in for the MIDI port: `sysex <hex>` lines in and out), when Node.js and
Python are installed; without them those tests return early. Each line
of JSON on their input changes the stand-in as if done in the DAW (another project, unsaved changes, the
channels in FL Studio's rack, a knob), which is how the tests follow saves and copy knobs. They never
reach a real DAW, its folders, or this computer's MIDI ports. CI runs all of it.

## Still to confirm in the real DAWs

The stand-ins follow the APIs' documentation; these are the things only the DAWs themselves can show.

Bitwig:

- [ ] Its remote connection puts the length in front of what the script sends (the app reads either way).
- [ ] Parameter IDs from `device` look like `CONTENTS/PID3e8`, and `set` takes them back.
- [ ] `insert` takes the scanner's VST3 class ID as Bitwig wants it, and a UUID for its own devices.
- [ ] The words `type` and `kind` use (e.g. `Instrument`, `note_to_audio`).
- [ ] `project` names the project as its file is named (Check with Bitwig relies on it, and so does
      finding the open project in the library).
- [ ] `modified` clears when the project is saved, and stays clear after Save As (the app re-reads the
      file then).
- [ ] Two connections at once (the app's look every 2 seconds, and a send or a check) work side by side.
- [ ] A plugin's parameter names in Bitwig match FL Studio's for the same plugin (VST3 or CLAP in Bitwig,
      often VST2 in FL Studio), which is how knobs are copied.
- [ ] It listens on 127.0.0.1 or ::1 (the app tries both), and what Windows' firewall asks the first time.
- [ ] A clip slot's `insertFile` makes a clip from a MIDI file.
- [ ] A device chain's `insertFile` loads `.bwpreset`, `.vstpreset` and `.fxb` files with their settings.
- [ ] A sample dropped in a new Sampler plays at its own pitch on C3 (the app tunes from there), even when
      the file says its own root key.
- [ ] The Sampler's `TRANSPOSE` and the Note Transpose's `COARSE` are known by those IDs, and their raw
      values are semitones.
- [ ] Whether adding a track opens the browser for an instrument (the script closes it if so).
- [ ] A Drum Machine pad's device list is empty until something is on it, and its insertion point takes a
      sample as dropping one on the pad does.
- [ ] Bitwig saves the Drum Machine with its name and ID as it does the Sampler (how the app finds the ID),
      and keeps its installed sound packages where the app looks (`installed-packages` under
      `%LOCALAPPDATA%\Bitwig Studio`, `~/Library/Application Support/Bitwig/Bitwig Studio` or
      `~/.BitwigStudio`).

FL Studio:

- [ ] The window title's wording (Check with FL Studio finds the project name in it, and so does
      finding the open project in the library).
- [ ] `general.getChangedFlag()` is non-zero exactly while the project has unsaved changes.
- [ ] `mixer.getCurrentTempo()` gives BPM or thousandths of one (the script handles both).
- [ ] A loopMIDI port called Music Organiser gets the script by itself (`supportedDevices`).
- [ ] loopMIDI installs as `Tobias Erichsen\loopMIDI\loopMIDI.exe` in Program Files (x86) or Program Files
      (or runs as `loopMIDI`), which is how Settings tells whether it's installed.
- [ ] Windows' MIDI system (from the late-November 2026 update for Windows 11 25H2): Windows PowerShell
      finds its API (`Windows.Devices.Midi2`, as Microsoft documents it), Install makes and saves the pair,
      the pair comes back after a restart, and FL Studio sees the A end with "Music Organiser" in its name
      (and gives it the script by itself). Whether saving needs administrator rights (if it does, the port
      lasts until a restart, and the app says so).
- [ ] FL Studio runs as `FL64.exe` (or `FL.exe`), which is how Check connection tells whether it's open.
- [x] Scripts can't open files (FL Studio 25, 2026-10-14), so the link talks over MIDI instead.
- [ ] `OnSysEx` gets the app's messages whole, `device.midiOutSysex` sends answers out of the output with
      the input's Port number, and `device.isAssigned()` says whether there is one.
- [ ] loopMIDI and Windows' MIDI system pass SysEx messages of about 525 bytes, 8 at a time, intact, and
      let the app and FL Studio have the port open together.
- [ ] Every user data folder has Settings, Presets and Projects in it (how the app finds them).
