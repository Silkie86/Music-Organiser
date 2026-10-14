# DAW extensions

Two small scripts that run inside the DAWs, so the app can ask a DAW what it shows for the open project.
The app uses them to check how it reads project files (**Check with Bitwig** and **Check with FL Studio**
on a project), and later to learn what plugin settings and file changes mean.

| Folder | Runs in | How the app talks to it |
|---|---|---|
| `bitwig` | Bitwig Studio, as a controller script | TCP on port 19791, on this computer only |
| `fl-studio` | FL Studio, as a MIDI controller script | Files in the app's data folder |

Neither changes anything in a project unless you turn that on (see [Changes](#changes)).

## Installing

In the app: **Settings → DAW links → Install**, for each DAW. The scripts ship next to the app
(`Extensions` in its folder), and Install copies them where the DAW looks:

- **Bitwig**: `Documents\Bitwig Studio\Controller Scripts\Music Organiser` (on Linux, `~/Bitwig Studio/...`).
  Then in Bitwig: Settings → Controllers → Add controller → Music Organiser → DAW link.
- **FL Studio**: `Settings\Hardware\Music Organiser` in FL Studio's user data folder (normally
  `Documents\Image-Line\FL Studio`). The app also finds user data folders elsewhere from where your FL
  projects are, and **Choose folder…** points it at one. Then restart FL Studio and, in Options → MIDI
  settings, set a MIDI input's Controller type to Music Organiser. FL Studio only runs a script for a MIDI
  input, so with none to spare, make a port called Music Organiser with
  [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html) on Windows, or the IAC Driver (Audio
  MIDI Setup) on a Mac.

Settings says when an installed copy is older than the app's, and Install updates it. **Check connection**
asks the DAW to answer.

## Changes

Reading is always allowed. Changing things needs your say-so in the DAW:

- **Bitwig**: Settings → Controllers → Music Organiser → *Let the app make changes*. Then the app can set
  the selected device's parameters, insert plugins, devices, presets and samples, and run actions such as
  Save.
- **FL Studio**: set `LET_THE_APP_MAKE_CHANGES = True` near the top of `device_MusicOrganiser.py` (in the
  folder above) and reload the script. Then the app can set plugin parameters. Updating the script from
  Settings keeps this line as you set it.

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
| `hello` | | `link` (this protocol's version), `product`, `version`, `api`, `platform`, `port`, `project`, `modified`, `engine`, `changes` |
| `project` | | `name`, `modified`, `tempo`, `timeSignature`, `playing`, `position`, `trackCount`, `tracks`: the first 128 tracks in Bitwig's flat list, each with `index`, `name`, `type`, `group`, `color`, `active`, `mute`, `solo`, `arm`, `volume`, `pan` (as shown, with `volumeValue` and `panValue` from 0 to 1), `deviceCount` and the first 16 `devices` (`name`, `preset`, `kind`, `plugin`, `enabled`) |
| `device` | | The selected device: `track`, `device` (or null) and every parameter as `id` (what Bitwig saves it under), `name`, `value` (0 to 1) and `display` ("-6.0 dB"). Waits up to 2 seconds for the display texts. |
| `actions` | | Every Bitwig action: `id`, `name`, `category`, `menu` |
| `select` | `track` (its index in `project`), `device` (optional) | `{}` |
| `set` ✎ | `param` (an `id` from `device`), `value` (0 to 1) | `{}` |
| `insert` ✎ | `kind`: `vst3` or `clap` with `plugin` (the ID the plugin scanner reads), `vst2` with `plugin` as a number, `bitwig` with `device` (a UUID), or `file` with `path` | `{}`. Bitwig quietly ignores IDs it doesn't know, so check `project` afterwards. |
| `action` ✎ | `action` (an `id` from `actions`) | `{}` |

✎ needs changes turned on. In the app this is `Core/DawLink/BitwigLink.cs`.

## FL Studio link

`device_MusicOrganiser.py`, for FL Studio's MIDI scripting (Python 3.12 in FL Studio 2024 and later; the
script keeps to 3.9's syntax for FL Studio 21). Plain ASCII, since FL Studio reads its first lines itself.

FL Studio's Python is stripped down and can't be counted on for network connections, so the app and
the script talk through files in
`MusicOrganiser/FL Studio link` in the app's data folder (`%LOCALAPPDATA%` on Windows,
`~/Library/Application Support` on a Mac). The app writes `request.json` (written aside and moved into
place); the script looks for it five times a second, removes it and writes `response-<id>.json` the same
way. The app gives up after 5 seconds, and the script ignores requests sent more than 15 seconds ago.

Requests are `{"id": 1, "sent": <Unix time in seconds>, "cmd": "...", ...}`, and answers are as for
Bitwig. A plugin is named by `channel` (its place in the whole channel rack, from 0) or by mixer `track`
and `slot`.

| Command | Asks for | Result |
|---|---|---|
| `hello` | | `link`, `product`, `version` ("Producer Edition v25.1.6 [build 4997]"), `api` (FL Studio's scripting version), `title` (the window title, which names the project), `changes`, `platform`, `script` (the folder FL Studio loaded it from) |
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
`test/serve.py`, when Node.js and Python are installed; without them those tests return early. CI runs
all of it.

## Still to confirm in the real DAWs

The stand-ins follow the APIs' documentation; these are the things only the DAWs themselves can show.

Bitwig:

- [ ] Its remote connection puts the length in front of what the script sends (the app reads either way).
- [ ] Parameter IDs from `device` look like `CONTENTS/PID3e8`, and `set` takes them back.
- [ ] `insert` takes the scanner's VST3 class ID as Bitwig wants it, and a UUID for its own devices.
- [ ] The words `type` and `kind` use (e.g. `Instrument`, `note_to_audio`).
- [ ] `project` names the project as its file is named (Check with Bitwig relies on it).
- [ ] It listens on 127.0.0.1 or ::1 (the app tries both), and what Windows' firewall asks the first time.

FL Studio:

- [ ] The window title's wording (Check with FL Studio finds the project name in it).
- [ ] `mixer.getCurrentTempo()` gives BPM or thousandths of one (the script handles both).
- [ ] A loopMIDI port called Music Organiser gets the script by itself (`supportedDevices`).
- [ ] Opening files and the standard library work in each FL Studio version's Python.
- [ ] Every user data folder has Settings, Presets and Projects in it (how the app finds them).
