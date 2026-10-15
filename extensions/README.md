# DAW extensions

Small programs that run inside the DAWs, so the app can ask a DAW what it shows for the open project.
The app keeps up with both while they're open (see [Staying connected](#staying-connected)), and uses
them to check how it reads project files (**Check with Bitwig** and **Check with FL Studio** on a project,
which also run by themselves when the DAW has that project open), to send things into Bitwig (FL Studio
channels with their sounds, knobs and notes, and Sound Lab kits; see
[Sending into Bitwig](#sending-into-bitwig)), and later to learn what plugin settings and file changes
mean.

| Folder | Runs in | How the app talks to it |
|---|---|---|
| `bitwig-java` | Bitwig Studio, as a Java controller extension (`MusicOrganiser.bwextension`) | TCP on port 19791, on this computer only |
| `bitwig` | Bitwig Studio, as a controller script: the extension's fallback, the same link in JavaScript | TCP on port 19791, on this computer only |
| `fl-studio` | FL Studio, as a MIDI controller script | MIDI SysEx on the Music Organiser port |

None of them changes anything in a project unless you turn that on (see [Changes](#changes)).

## Installing

In the app: **Settings → DAW links → Set up both** installs (or updates) each DAW's script, skipping a DAW
the app doesn't find on this computer, makes FL Studio's MIDI port the way chosen in Settings, then checks
each link and says what's left to do. Each DAW also has its own **Install** button. The scripts ship next
to the app (`Extensions` in its folder), and installing copies them where the DAW looks:

- **Bitwig**: the extension, `MusicOrganiser.bwextension`, into `Extensions` in Bitwig's user folder, normally
  `Documents\Bitwig Studio` (on Linux, `~/Bitwig Studio`), taking out the script if it's there; or, when the app
  was built without a JDK and so has no extension, the script, into `Controller Scripts\Music Organiser`. A PC can
  have more than one Bitwig Studio folder (Windows may keep Documents in OneDrive), and Bitwig can load extensions
  from one while it keeps projects in another (seen: extensions from `Documents\Bitwig Studio`, projects on another
  drive). So Install puts the link in every Bitwig Studio folder it finds in a Documents folder, in the one
  **Choose folder…** points it at (which is also where the app looks for Bitwig projects), and in the Extensions
  folder the extension last said Bitwig loaded it from (`loadedFrom` in its hello). Every copy is the same, so it
  doesn't matter which Bitwig loads. Then in Bitwig: Settings → Controllers → Add controller → Music Organiser → DAW link.
  With the extension, also set that controller's MIDI in and MIDI out to the MIDI port **Music Organiser Bitwig**
  (make it in loopMIDI on Windows), never to FL Studio's port, **Music Organiser**: Bitwig has only saved for the
  app once they were set (see [Routes and the MIDI loopback](#routes-and-the-midi-loopback)).
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

  On a Mac or Linux the app can't reach FL Studio yet: it only talks MIDI on Windows so far.

Settings says when an installed copy is older than the app's, and Install updates it. **Check connection**
asks the DAW to answer (for Bitwig, it also says when the extension's own MIDI hasn't come back through its MIDI
in and out, as while they aren't set, and *Set up both* doesn't call that set up). When FL Studio doesn't, the app says which part is missing, on Windows: FL Studio
isn't open, there's no MIDI port called Music Organiser (and how to make one, the way chosen in Settings),
or there is one but FL Studio isn't running the script for it (`Core/DawLink/FlSetup.cs`).

## Changes

Reading is always allowed. Changing things needs your say-so in the DAW:

- **Bitwig**: Settings → Controllers → Music Organiser → *Let the app make changes*. Then the app can add
  tracks, insert plugins, devices, presets and samples, put MIDI files in clip slots, fill a Drum
  Machine's pads, set the pitch of a Sampler or Note Transpose, set the selected device's parameters,
  run actions such as Save, and (with the Java extension) start and stop Bitwig's master recorder.
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

Two builds of the same link, which talk to the app the same way:

- **`bitwig-java`**: a Java controller extension against Bitwig's controller API 23 (Bitwig 6.1 and later), built
  with Maven into `MusicOrganiser.bwextension`, which goes in `Extensions` in Bitwig's user folder. It's a port
  of the script, function by function, and adds what only an extension can do: Bitwig's newer API (link version
  3 below), reports it writes to files, with a self-check of every part of the API it uses, and (from 3.7) a MIDI
  loopback to run Bitwig's commands as a hardware controller would (see [its README](bitwig-java/README.md) and
  [Routes and the MIDI loopback](#routes-and-the-midi-loopback)).
- **`bitwig`**: `MusicOrganiser.control.js`, plain ES5 against Bitwig's controller API 18: the fallback, kept as
  it was (link version 2, without the version 3 additions).

They're the same controller to Bitwig (the same ID), so only one should be installed at a time.

The link listens on port 19791 (`host.createRemoteConnection`). Each message is UTF-8 JSON with a
4-byte big-endian length in front, which is how Bitwig's remote connections frame messages; the link
also ends each message with a newline, so the app reads them either way. Text outside ASCII is sent as
`\u` escapes. When the app connects, the link greets it with `{"event": "hello", "result": {...}}`.

Requests are `{"id": 1, "cmd": "...", ...}`. Answers are `{"id": 1, "ok": true, "result": ...}`, or
`{"id": 1, "ok": false, "error": "..."}` with the reason in words the app shows as they are.

| Command | Asks for | Result |
|---|---|---|
| `hello` | | `link` (this protocol's version: 3 from the extension, 2 from the script), `product`, `version`, `api`, `platform`, `port`, `project`, `modified`, `engine` (whether Bitwig's audio engine is on), `changes`, `onlyIn` (`true`: the link honours a request's `onlyIn`, [below](#only-in-one-project)). Version 3 adds `runtime` (`"java"`) and `extension` (its version), and from extension 3.4 `loadedFrom` (the file Bitwig loaded it from, when it can tell; 3.5 can tell in a folder with a space in its name, as Bitwig Studio's are). From extension 3.7 `loopback`: whether the extension's own MIDI has come back to it through the loopback, so the routes through it work ([below](#routes-and-the-midi-loopback)). |
| `project` | | `name`, `modified`, `tempo`, `timeSignature`, `playing`, `position`, `trackCount`, `tracks`: the first 128 tracks in Bitwig's flat list, each with `index`, `name`, `type`, `group`, `color`, `active`, `mute`, `solo`, `arm`, `volume`, `pan` (as shown, with `volumeValue` and `panValue` from 0 to 1), `deviceCount` and the first 16 `devices` (`name`, `preset`, `kind`, `plugin`, `enabled`). Version 3 adds each track's `id`, the ID Bitwig keeps it under, once Bitwig has said. |
| `device` | | The selected device: `track`, `device` (or null), `drumPads` (whether it has a Drum Machine's pads) and every parameter as `id` (what Bitwig saves it under), `name`, `value` (0 to 1) and `display` ("-6.0 dB"). Waits up to 2 seconds for the display texts. Version 3 adds the device's remote controls on the page Bitwig shows (`page`, and `controls` with `index`, `name`, `value`, `display`, `steps`, `stepNames`, `origin`, `automated` and `param`), and gives a parameter that is one of those controls the same `steps` (-1 for continuous), `stepNames`, `origin` and `automated`: see [below](#link-version-3). |
| `actions` | | Every Bitwig action: `id`, `name`, `category`, `menu` |
| `select` | `track` (its index in `project`), `device` (optional) | `{}` |
| `set` ✎ | `param` (an `id` from `device`), `value` (0 to 1) | `{}` |
| `insert` ✎ | `kind`: `vst3` or `clap` with `plugin` (the ID the plugin scanner reads), `vst2` with `plugin` as a number, `bitwig` with `device` (a UUID), or `file` with `path` (a preset, or a sample, which goes in a Sampler). Then `track` (optional) and `at`: `"end"` (the default) or `"start"` of its chain. | Without `track`, at the end of the selected track's chain: `{}`, since Bitwig quietly ignores what it can't use. With `track`, once Bitwig has added it: `added`, `index` and `device` (as in `project`), or `added: false`. |
| `newtrack` ✎ | `name`, `color` (optional, `#rrggbb`) | `index` and `name`. An instrument track after the last main track (before the effect tracks), selected. If adding it opened Bitwig's browser, the script closes it. |
| `clip` ✎ | `track`, `path` (a MIDI or audio file), `slot` (optional) | `slot`: where Bitwig made the clip, by default the first empty one in the first 8 scenes |
| `pads` ✎ | `pads`, each with `note` (0 to 127), `path` and `name` (optional). Then `track` and `device` for a Drum Machine's place, or nothing for the selected device. | `filled`, `replaced` (those of the filled that went in place of an empty Sampler), `skipped` (pads that already had something on them, left alone) and `failed`, as notes, and `taken`: what's on each skipped pad (`note`, `name`, `devices`, `first` device's name, its `sample`). Each sample goes on a free pad as if dropped there, in a Sampler. A pad whose first device is a Sampler with no sample, still named as it came (a Drum Machine preset can put one on every pad), with nothing after it but audio effects (up to 3), is free too: the sample goes in that Sampler's place, keeping the effects after it. |
| `transpose` ✎ | `track`, `device` (0 or 1), `semitones` | `device`, `value`, `display`. Sets a Sampler's Transpose (fractions allowed) or a Note Transpose's Coarse (whole semitones). |
| `action` ✎ | `action` (an `id` from `actions`), `via` (optional: the route to run it by; without one, `flush` from extension 3.8, as Bitwig 6.1.3 saved from there, and `direct` before; see [below](#routes-and-the-midi-loopback)) | `project`: the project Bitwig had in front just before the action ran, so the app can tell which one a Save went to with more than one open; `via`: the route; and `ran`: whether the link ran the action by it (a route through Bitwig's flush or the loopback that doesn't come back within a second doesn't, and the link then never runs it). From extension 3.7 and the script; older extensions answer `{}`, and the extension leaves `project` out if it can't read it. The script runs actions straight away only: it answers `via: "direct"` and `ran: true`, and refuses any other route with `This needs the Java extension 3.7.` |
| `clicked` (3) | | The parameter last clicked in Bitwig: `exists` (false until one is), `name`, `value`, `display`, `steps`, `stepNames`, `origin`, `automated`, `locked` |
| `recorder` ✎ (3) | `do`: `"start"`, `"stop"` or `"status"` (the default) | Bitwig's master recorder, once it has started or stopped: `active`, `duration` (milliseconds recorded) |
| `selftest` (3) | | The self-check (see [the extension's README](bitwig-java/README.md#reports)), written to the reports folder, with `file` |
| `arrange` ✎ (3) | `track`, `slot` (a launcher slot with a clip), `at` (optional: where in the arranger, in beats from the start; the play start by default), `via` (optional, from extension 3.7: the route for the copy, the paste and the focus actions, `flush` by default from 3.8 and `direct` before; [below](#routes-and-the-midi-loopback)), `checks` (optional, from 3.8: `true` to check, when its paste ran but put nothing in the arranger, whether a paste into an empty launcher slot and Bitwig's Duplicate work by the same route) | **An experiment**, from extension 3.6, which only the live tests send ([below](#clips-in-the-arranger-an-experiment)). Once its steps are through, whether it worked or not: `arranged` (both of the next two, with an `exact` mark), `appeared` (a clip showed up selected on the track after the paste, with the track's launcher clips as they were), `sameAsSource` (that clip has the launcher clip's marking colour, so it's a copy of that clip and not whatever was on Bitwig's clipboard before, unless Bitwig rounded the mark: see `exact`), `copiedFromTimeline` (the arranger timeline had the focus when the link copied, so it didn't paste), `at`, `layout` (before), `launcher` and `timeline` (`before` and `after`: whether the Arrange layout's clip launcher, and the arranger timeline, showed), `mark` (the fresh colour the link gave the launcher clip before it copied: the clip's colour `before`, the one `asked` for, the one it `got`, each `#RRGGBB`, whether it `took`, and whether it's `exact`: the very colour asked for, not one Bitwig rounded it to), `launcherFocus` and `focus` (how many times the link ran Bitwig's `focus_or_toggle_clip_launcher` and `focus_or_toggle_arranger`), `before`, `beforePaste` and `after` (the clip selected in the arranger: `exists`, and its `track`'s name), `pastedColor` (the colour of the clip selected in the arranger at the end, `#RRGGBB`, or null when none is), `launcherClipsBefore` and `launcherClipsAfter` (the track's clips in its first 8 launcher slots). From extension 3.7 also `via` (the route) and `ran`: for `launcherFocus`, `copy`, `focus` and `paste`, whether the link ran that step by the route (null for a step it didn't get to, and for `launcherFocus` when Bitwig has no such action). From 3.8, with `checks`, when its paste ran but put nothing in the arranger and Bitwig has `focus_or_toggle_clip_launcher`, also `checks`: `launcherPaste` and `duplicate`, each null when it couldn't run, and `error` when one stopped part way ([below](#clips-in-the-arranger-an-experiment)). |
| `filetrack` ✎ (3) | `track`, `path` (a file), `at` (optional: where to move the play start first, in beats from the start) | **An experiment**, from extension 3.8, which only the live tests send ([below](#clips-in-the-arranger-an-experiment)): inserts the file after the track, as a new track, through the track's `afterTrackInsertionPoint`, and answers once Bitwig settles: `added` (how many tracks), the new track's `index`, `name`, `type`, `launcherClips` (its clips in the first 8 launcher slots) and whether it's `selected` (null and false when there's none), `tracksBefore`, `tracksAfter`, `arranger` and `arrangerBefore` (the clip selected in the arranger, as `arrange` gives it), `at` (where the play start was), and `onTrack` (the given track's launcher clips `before` and `after`). |

✎ needs changes turned on. (3) only in link version 3, the extension. `insert` with a track, `newtrack`,
`clip`, `pads`, `transpose`, `recorder`, `arrange` and `filetrack` wait for Bitwig to make the change (up to 5 seconds for
each step), and run one at a time in the order sent; from extension 3.7 so does `action`, whose route may take up
to a second to come back. In the app this is `Core/DawLink/BitwigLink.cs`.

### Only in one project

Any request can carry `onlyIn`, a project's name. The link then refuses it unless Bitwig has exactly that
project open, with `Bitwig has "<open>" open, not "<onlyIn>", so the link stopped before changing anything
there.` A change that waits for Bitwig checks again when it starts (it may have waited behind others) and
before each of its steps after a wait, and stops with that error, since Bitwig's track bank, selected track
and selected device follow whichever project is in front: without this, a new track asked for in one project
could be named, coloured or selected in another that came to the front meanwhile. At the same points it checks
again that *Let the app make changes* is still on. An `onlyIn` that isn't text is refused (`onlyIn has to be a
project's name.`); null, or none, means no check. The live tests send it with every request that changes or
selects something, so a run only ever changes its own test project (see
[Only in a project made for them](#only-in-a-project-made-for-them)). Both the script and the extension do
it, and say so with `onlyIn: true` in their hello.

### Link version 3

The extension's additions are fields and commands the app can ignore, so the app talks to the script and the
extension alike. Bitwig's API tells a parameter's steps (and their names), its origin (where its value rests:
0.5 for a pan, 0 for a level) and whether it's automated only for a device's remote controls and the last
clicked parameter, not for every parameter by its ID, and it doesn't say which parameter a remote control is.
So `device` lists the remote controls of the page Bitwig shows for the selected device, and the link matches
each to the one parameter with its name and value (`param`), whose entry then carries the same details.

The extension also keeps reports in `%LOCALAPPDATA%\MusicOrganiser\reports\bitwig` (the app's data folder;
see [its README](bitwig-java/README.md#reports) for macOS and Linux): a line for each command (but the app's
regular hellos) in
`<date>-link.jsonl`, and a self-check of each part of Bitwig's API it uses in `<time>-selfcheck.json`, written
when Bitwig starts it and on `selftest`. A Bitwig problem in a real session can be read there afterwards.

### Routes and the MIDI loopback

Bitwig 6.1.3 ignored every command of its user interface the link ran (Save, copy, paste, bringing a panel to the
front), whether it ran them as a request came in or in a task it scheduled, while it carried out every other
change. The same Save works for controllers such as DrivenByMoss, which run it while Bitwig handles a press of a
hardware button. So from 3.7 the extension can run Bitwig's Save, Copy and Paste actions and the two focus actions
(`focus_or_toggle_arranger`, `focus_or_toggle_clip_launcher`), and from 3.8 Duplicate for `arrange`'s checks, by a route, which `action` and `arrange` take as
`via`. The live tests then found that Bitwig 6.1.3 saves from its flush once the extension's MIDI in and out are
set ([below](#routes-and-the-midi-loopback)), so from 3.8 that's the route the link takes when the request names
none (and so the app's own commands take it):

| Route | How the link runs the command |
|---|---|
| `direct` | Straight away, as the request comes in (as before 3.7, and the default before 3.8). |
| `flush` | From Bitwig's next flush, which the link asks for (the default from 3.8). |
| `midi` | When a SysEx message the link sent itself comes back through the loopback: `F0 7D 4D 4F 42`, three 7-bit bytes of the message's number, `F7` (`7D` is the ID for non-commercial use, so no maker's device takes it for its own). |
| `buttonCode` | When a press of one of the link's own hardware buttons comes back through the loopback: the link sends a note on MIDI channel 16 (from note 110, one for each command), Bitwig presses the button, and the link runs the command. |
| `button` | The same with another of the link's buttons (from note 100), which Bitwig runs the command for itself, as for a controller's button; the link only hears of the press. |

The last three need the loopback. In loopMIDI, add a port called **Music Organiser Bitwig**, then in Bitwig,
Settings > Controllers > Music Organiser, set both MIDI in and MIDI out to it. Never set them to **Music
Organiser**: that's the FL Studio link's port, and FL Studio must never get the notes and messages the Bitwig link
sends itself. The extension asks Bitwig for one MIDI in and one MIDI out, only for the loopback, and offers the
Music Organiser Bitwig port for both, so Bitwig can fill them in when it finds the port (auto-detection).
Bitwig 6.1.3 starts the extension with them unassigned, and the link answers. In the live runs without them
(extensions 3.3 and 3.6, which have no ports, so only `direct`) Save did nothing; with them set (3.7 and 3.8) Save
worked from the flush both times and straight away once in two. Nobody has tried the flush without them, so set
them after installing 3.7 or later, whichever route the app uses: the steps Settings shows after *Install* ask for them, and *Check
connection* says when the extension's own MIDI hasn't come back through them (the hello's `loopback` is false),
as while they aren't set; the extension can't tell that from a port that doesn't hand its MIDI back. A Mac or
Linux has no loopMIDI; a virtual MIDI port there (an IAC bus on a Mac) may do instead, which nobody has tried.

A route other than `direct` has a second to come back (through Bitwig's flush, or the loopback). When it doesn't,
the link drops it, so a message or press that turns up late never runs the command, and answers `ran: false`.
Before it runs a command that came back, it checks `onlyIn` and *Let the app make changes* again
([Only in one project](#only-in-one-project)). A press carries no number, so the link can't tell a late press from
the next one of the same button; and for `button`, Bitwig has run the command before the link hears of the press,
so the link can't hold that one back.

The extension sends itself a message as Bitwig starts it, and again each time the app connects, and says in its
hello (`loopback`) and its self-check (*command routes*) whether any of its own MIDI has come back, which is how
the app and the live tests know the loopback works. Without the loopback's ports, `action` and `arrange` refuse
the routes through it, saying so.

### Clips in the arranger (an experiment)

Bitwig's controller API (23) can put a clip in a launcher slot, but not on the arranger timeline: only launcher
slots have insertion points. The app would like what it sends (*To Bitwig* from FL Studio, the Kit builder) to
land in the arranger, so extension 3.6 tries the only other way, Bitwig's own editing, as a person would do it.
`arrange` switches to the Arrange layout if another one shows, shows its clip launcher if it's hidden, selects
the track and its launcher clip, gives the clip a fresh colour, gives the clip launcher the focus, copies the clip
(`Application.copy`), moves the play start to `at`, brings the arranger timeline to the front and pastes
(`Application.paste`), which puts the copy on the selected track. Each step waits for Bitwig to show it, and the
request fails, saying which step, when Bitwig doesn't (all but the colour: see below).

Paste puts down whatever is on Bitwig's clipboard, which every open project shares. When the copy didn't take,
that's whatever was copied before: a clip you copied in your own song, or the last run's copy. To tell its own
clip from that, the link marks the launcher clip with a fresh colour before it copies (`mark`): a new hue each
time, a golden-ratio step round the colour wheel from the last one, and clearly unlike the clip's colour then.
It waits for Bitwig to show the new colour (Bitwig may round it to one of its own, so any clear change counts)
and keeps the colour the clip `got`, which the pasted clip's colour is compared with. The mark is `exact` when
that's the colour asked for (within 2 of 255 in each of red, green and blue). A colour Bitwig rounded is one of
its own, which other clips can have too (one you copied, or an earlier run's copy whose mark Bitwig rounded the
same way), so the link can't be sure a pasted clip in it is the launcher clip's copy. When Bitwig doesn't change
the clip's colour, the link carries on, with `took` false, but can't tell its clip from another. The launcher
clip keeps its new colour: only the test project gets this.

Bitwig copies and pastes in whichever of its panels has the focus, and the API can't say which one has it. The
link brings a panel to the front with Bitwig's actions `focus_or_toggle_clip_launcher` (when Bitwig has one by
that ID: nobody has seen Bitwig's list yet, so without it the link copies from whichever panel has the focus) and
`focus_or_toggle_arranger`. Each hides its panel when that's in front already, so the link runs it a second time
when it does. When `focus_or_toggle_arranger` hid the timeline, the timeline had the focus as the link copied
(nothing in between moves it), so Bitwig copied from the timeline, not the launcher clip: the link doesn't paste,
and says `copiedFromTimeline`.

Nobody knows yet whether Bitwig goes along with copy and paste from an extension, so once the steps are through
the answer always says what Bitwig showed. `appeared` is true when the clip selected in the arranger (Bitwig's
arranger cursor clip, which follows a pasted clip) has moved to the track, where it wasn't just before the paste,
and the track's launcher clips are as they were; more launcher clips after than before means Bitwig pasted into
the launcher instead. `sameAsSource` is true when the mark took and the clip selected in the arranger has the
colour the launcher clip got (`pastedColor`, within 2 of 255 in each of red, green and blue), and `arranged` when
both are and the mark is `exact`. A clip that appeared in another colour is whatever Bitwig's clipboard held
before. The API can't say where an arranger clip starts, so `at` is where it was meant to go, and only a look at
Bitwig shows whether it's there. Copy replaces whatever was on Bitwig's clipboard (one for every project open),
and nothing is undone when it doesn't work (the layout, the play start and the launcher clip's colour stay as
the link left them), so only the live tests send it, in their own test project (*Changes › Clip to arranger*);
the app's sends don't use it yet. In the app it's `BitwigLink.ArrangeAsync`.

From 3.7, `arrange` can run its copy, its paste and the two focus actions by a route
([above](#routes-and-the-midi-loopback)): `direct` works as before, with the API's copy and paste; any other
route (`flush`, the default from 3.8) runs Bitwig's Copy and Paste actions, and the focus actions, by that route.
`ran` says which of those steps ran. A step that didn't run doesn't stop the request: the link answers with what
Bitwig shows, and doesn't paste when the copy didn't run, so it never puts down whatever was on Bitwig's
clipboard before (nor do the checks below, which only follow a paste that ran).

The 3.7 live run (on 6.1.3) put nothing in the arranger by any route, while Save worked from the flush. To tell
which part fails, `arrange` from 3.8 takes `checks: true`: when its own paste ran but put nothing in the arranger
(no clip appeared on the track, and the clip selected there is as it was and wasn't on the track already, where
a new one would look just like it), and Bitwig has
`focus_or_toggle_clip_launcher`, it then runs two checks by the same route, each starting as `arrange` does (it
selects the track and a launcher slot, and gives the clip launcher the focus). They don't run otherwise, since
their paste could then put down what Bitwig's clipboard held before, or a clip in the arranger that would hide
what a later go put there. A check stops before its paste or Duplicate when Bitwig doesn't select its slot or the
route doesn't run the focus action.

- `launcherPaste`: a paste into the track's first empty launcher slot, which needs no arranger. When the mark
  took and is `exact`, a clip there in the launcher clip's marking colour (`sameAsMark`) means the copy took, so
  it's the arranger's part that fails, and one in another colour means the copy didn't take; nothing means the
  paste didn't land in the launcher either (if it went anywhere else, the link doesn't see it, and it's left).
  It says which `slot`, whether it got `selected`, the launcher's `focus` count, which steps `ran` (`focus`,
  `paste`), whether a clip was `made`, its `colour`, and whether the link `removed` it again (it deletes it, so
  the next go starts as this one did).
- `duplicate`: Bitwig's Duplicate action on the launcher clip, which needs no clipboard. A new launcher clip
  (`launcher`, how many more) means Bitwig's editing actions work in the launcher by this route, and the link
  deletes it again (`removed`); a new track (`tracks`) means Duplicate went to the track, so the launcher didn't
  have the focus, and that track is left in the test project. With neither, Duplicate changed nothing the link
  can see.

A check that stops part way (another project came to the front, say) puts why in `checks.error`, and `arrange`
still answers with what it found.

`filetrack` (3.8) tries a way round the clipboard altogether. Bitwig's API can't put a clip on the arranger, but
a track can insert a file after itself (`afterTrackInsertionPoint().insertFile`), as when a file is dropped
between tracks, and Bitwig may make a new track with the file's clip in the arranger, at the play start, as it
does for a drop. The answer says whether a track came, and whether the clip went to its launcher instead or the
clip selected in the arranger moved to it; only a look at Bitwig shows where it starts. The live tests try it
with their MIDI file (*Changes › MIDI file as a track*).

What the live runs found (6.1.3, 2026-10-15). Everything `arrange` does through the API works: the clip takes
each mark colour, exactly as asked, and the play start moves to `at`. Bitwig's Copy, Paste and Duplicate don't
act on what the API selected, though: they act on Bitwig's own selection, whatever was last clicked. In the two
runs that started with nothing selected (3.7 at 18:52, 3.8), by every route, Copy, Paste and Duplicate changed
nothing at all, in the arranger, the launcher or the track list (3.8 had just restarted Bitwig, so its clipboard
was empty too). The 3.7 run at 22:19 started with Bitwig's selection on a track, and there Paste ran (from the
flush, then on the link's MIDI) and put whole tracks in the track list, copies of “Send 185248” and “Devices
221955”, just above the clip's track. The link, which reads tracks by their place in its track bank, then looked
at a pasted track and took the clip for gone. `filetrack` made no track: Bitwig ignores a MIDI
file inserted after a track, while the same file into a launcher slot works.

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
cd extensions/bitwig-java && ./mvnw package           # the extension's tests, then MusicOrganiser.bwextension
python -m unittest discover -s extensions/fl-studio/test
```

The extension needs a JDK (17 or later); `mvnw` (`mvnw.cmd` on Windows) fetches Maven itself, and Maven fetches
Bitwig's API from Bitwig's own repository to build against. Its tests run it in a stand-in Bitwig made from
Bitwig's own API, with every scenario of the script's tests, and, when Node.js is installed, compare its answers
with the script's for the same requests (see [its README](bitwig-java/README.md#tests)).

The app's own tests (`dotnet test`) also talk to both scripts end to end through `test/serve.js` and
`test/serve.py` (which stands in for the MIDI port: `sysex <hex>` lines in and out), when Node.js and
Python are installed; without them those tests return early. Each line
of JSON on their input changes the stand-in as if done in the DAW (another project, unsaved changes, the
channels in FL Studio's rack, a knob), which is how the tests follow saves and copy knobs. They never
reach a real DAW, its folders, or this computer's MIDI ports. CI runs all of it.

## Live tests

The live tests try the Bitwig link against the real Bitwig, through the app's own code (the link,
`BitwigSend`, `BitwigCheck`, the project reader and MO Probe's live link), and write a report of what worked
and what Bitwig really did. They're the quick way to tick off the list below, and to see what a change to
the script or the extension broke. Run them from **Settings → DAW links → Bitwig Studio → Run live tests**
(it works while Bitwig is answering; **Stop** ends a run after the test running then, and **Open
report** opens the newest report), or from a terminal with `formatlab live bitwig` (`tools/README.md`).
In the app they're `Core/LiveTests/` (`BitwigLiveTests.cs`, run by `LiveRunner.cs`, reported by
`LiveReports.cs`). FL Studio gets the same once its link works.

What runs, in this order:

- **Link**, which only reads, so it always runs: the hello (versions, `api`, `link` and whether it's the
  script or the Java extension), the project listing, the selected device, the actions (Save among them),
  and 20 hellos one after another for the round trip time. With the Java extension also the tracks' `id`,
  the remote controls' `steps`, `stepNames`, `origin` and `automated` (Bitwig gives these only for remote
  controls), `clicked`, the extension's own `selftest`, and (from 3.7) the *MIDI loopback*: Passed when the
  extension's own MIDI has come back to it (its hello's `loopback`), else Failed, saying how to set the loopback
  up ([Routes and the MIDI loopback](#routes-and-the-midi-loopback)). With the script, or an extension too old,
  those are Skipped, saying they need the extension.
- **Changes**: a new track with a name and colour; the Sampler, Note Transpose and Drum Machine by ID; the
  Sampler's and Note Transpose's pitch, read back; a sample into a Sampler; a MIDI clip; that clip copied
  into the arranger at bar 5 (Java extension 3.6 or later, and
  [an experiment](#clips-in-the-arranger-an-experiment): Passed when the link sees a clip in the launcher
  clip's marking colour appear selected in the arranger on the clip's track, though it can't see where the
  clip starts, so it asks you to look at bar 5; Failed when a clip in another colour appeared there, as Bitwig
  pasted what its clipboard held before, so the copy didn't take (that clip can be deleted); Noted when a clip
  appeared but Bitwig wouldn't recolour the launcher clip, or when it has the launcher clip's colour but Bitwig
  rounded that to one of its own (which other clips can have too), asking you to look and say which clip it is;
  else Failed, saying what Bitwig did instead, or the step it didn't go along with, and asking you to look at
  Bitwig's arranger and say what you see; from extension 3.7 it tries the copy, the paste and the focus actions
  by each [route](#routes-and-the-midi-loopback) in turn, in Save's order (below), until one puts the clip there
  or its paste puts anything down (a clip on the track, whichever it is, one in the launcher, or one on another
  track), since another paste would land in the same place, where the clip the last one left selected hides
  whether a new one appeared; the verdict is that route's, naming it, with a line for what each route did; from
  3.8 the flush comes first (asked for by naming no route, as the app's own commands do), and each route whose
  paste ran but put nothing in the arranger runs the [checks](#clips-in-the-arranger-an-experiment) (when Bitwig
  has the clip launcher's focus action), with a line under its own for each: whether the copy took and a paste
  works in the launcher, and whether Duplicate works there); the
  MIDI file inserted after the Sample track as a track of its own, with the play start at bar 5 (extension 3.8,
  another experiment: Passed when it made a new track with no launcher clip and the clip selected in the
  arranger moved to it, asking you which bar the clip starts at and where the play start marker is (the 3.7 run
  left the marker at bar 1, though the link read it back at bar 5); Failed when no track came or the clip went in
  its launcher; else Noted, asking you to look); Drum Machine pads; selecting a device, setting a parameter and reading it back (then setting it back); the
  master recorder's status (Java only; it's never started); and, after Send, Save, with Bitwig's window brought
  to the front first (on Windows; the report says whether it got there, as `front`). Bitwig 6.1.3 ignored Save run
  straight away from the link, so with extension 3.7 or later the run tries Save by each route in turn: `direct`, `flush`, `midi`,
  `buttonCode`, then `button` (from 3.8, `flush` first, asked for by naming no route, then the rest in that
  order; a Save that works that way says the app's own Save does too), each through the same checks as every change, until one clears the unsaved
  changes or writes the project's file within 15 seconds. A route that didn't run (nothing came back within a
  second) or that the link refused (no loopback ports) is noted, and the next tried at once. Passed names the
  route that worked and says what the ones before it did, since that's how the app should save; Failed has a
  line for each route, and says how to set the loopback up when the routes through it didn't run. With the
  script or an older extension, only `direct` is tried, and the test says so. The report keeps the route that
  worked (`via`), each go (`attempts`: its `via`, the `project` Bitwig said it had in front, whether it `ran`,
  whether the changes `cleared`, whether the file was `written`, `tookMs`, and the link's `error` if it refused
  it), and `projectAtSave`; a failure says whether that was the test project.
- **MO Probe**, as a CLAP and then as a VST3, each on a new track: it waits for the probe's live status,
  pings it and labels it with the run's stamp (“MO Live Test 20261014T143012Z CLAP”, so its own report
  can be matched with the run's), checks its knobs both ways (Failed when a knob doesn't move, or a stepped
  knob lands on the wrong step, which BitwigSend relies on), and notes how Bitwig maps each knob as a table.
  Skipped, saying how to get it, when MO Probe isn't installed.
- **Send**: a sample channel with a root key, as an FL Studio project would have it, through BitwigSend,
  then read back from Bitwig.
- **Saved file**: the saved `.bwproject`, found among the library's projects or in Bitwig's Projects
  folder, read with the app's reader and compared with what Bitwig lists (as Check with Bitwig does).

Each test ends Passed, Failed, Skipped (saying why) or Noted (a finding that isn't a pass or a fail, like
the knob table). One failing never stops the run, and each has its own time limit (30 seconds; 1 minute 45 for
Save, which may wait 15 seconds for each of its five routes; 5 minutes for Clip to arranger, a minute for each
route and its checks; 2 minutes for the Drum Machine, which may mean reading Bitwig's presets for its ID; and
5 minutes for a knob check).

### Only in a project made for them

A run only changes anything while Bitwig lets the app make changes **and** the open project's name starts
with “MO Live Test” (capitals or not), and only in the one test project open when it starts. It asks Bitwig
again just before every change or selection it sends (the link's `BeforeChange`), and sends each with that
project's name as `onlyIn` ([Only in one project](#only-in-one-project)), so the script or extension stops a
change part way if another project comes to the front while Bitwig makes it. Either way nothing more is
changed for the rest of the run, and the test says it stopped part way. A script or extension too old to
say `onlyIn: true` in its hello isn't trusted with changes: the run says to install it again. Otherwise
every test that would change something is Skipped, saying why and what to do, and only Link runs: your own
projects are never touched. A knob check that's stopped, or runs out of time, still reports the turns it
made and any knob it couldn't set back.

To make the test project, once: in Bitwig, make a new project and save it as “MO Live Test” (in Bitwig's
Projects folder, or in one of the app's project folders), then turn on *Let the app make changes*
(Settings → Controllers → Music Organiser). Each run adds about six tracks named with the time it started
(“Devices 143012”, “Kit 143012”…) and saves; with extension 3.8 also a “Live Clip” track (*MIDI file as a
track*, named after the file, so every run adds another), and a copy of the Sample track for each go of Clip to
arranger whose Duplicate check went to the track. With the Java extension, a run also copies its MIDI clip into
the arranger ([an experiment](#clips-in-the-arranger-an-experiment)): the copy replaces whatever you had
copied in Bitwig, in any project, since Bitwig has one clipboard; and the test project is left in its Arrange
layout, with its clip launcher showing, the play start at bar 5, the run's MIDI clip in the launcher in a new
colour (a different one each run) and a pasted clip on the run's Sample track.
The link can't delete tracks, so clear them out by hand now and then, or save a fresh “MO Live Test 2”
(Bitwig lists only the first 128 tracks to the app). The samples and the MIDI file a run sends in are in
`files` next to the reports, the same every run, so the project goes on playing them.

### Reports

Each run writes two files into `reports\live` in the app's data folder:
`%LOCALAPPDATA%\MusicOrganiser\reports\live` on Windows (on a Mac, under `~/Library/Application Support`;
on Linux, under `~/.local/share`). They're named by when the run started, in UTC:

- `20261014T143012Z-bitwig.md`, to read: the counts, then each failure and note in full, then every test
  by area with how long it took.
- `20261014T143012Z-bitwig.json`, everything: the app's version and the system, Bitwig's hello as it came,
  and each test's outcome, detail, timings and data (what Bitwig answered, each round trip, each knob turn).

The newest 50 runs are kept. They're plain files so that anyone helping with the app can read them
without running anything. That's how Claude reads them: it can read files on the PC but not run
programs, so after a run it lists `%LOCALAPPDATA%\MusicOrganiser\reports\live`, reads the newest
`-bitwig.md` (the names sort by time), and looks in the `.json` with the same stamp for the details of a
test. MO Probe's own reports, in `probe` next to `reports`, carry the run's stamp in their label.

## Still to confirm in the real DAWs

The stand-ins follow the APIs' documentation; these are the things only the DAWs themselves can show. The
live tests (above) check many of them in one go.

Bitwig:

- [ ] Its remote connection puts the length in front of what the script sends (the app reads either way).
- [ ] Bitwig loads `MusicOrganiser.bwextension` from `Extensions` as the same controller as the script (same
      ID), keeping *Let the app make changes*, and says something sensible when both are installed.
- [ ] The extension's remote connection, observers and insertion points behave as the script's did (the
      extension makes the selected track's insertion point in init, where the script made it when asked).
- [ ] The extension's first self-check, a second after Bitwig starts it, is all `ok`, and its reports land in
      `%LOCALAPPDATA%\MusicOrganiser\reports\bitwig`.
- [ ] A remote control's `name` matches its parameter's name from `device` (how `device` tells which
      parameter has which steps, origin and automation), and `discreteValueNames` gives the names Bitwig shows.
- [ ] `clicked` follows clicks on any parameter, and creating the last clicked parameter adds nothing unwanted
      to Bitwig's menus.
- [ ] The master recorder starts and stops from `recorder`, and `duration` is in milliseconds.
- [ ] `arrange` (an experiment): Bitwig selects a launcher clip, copies it, shows and focuses the arranger
      timeline and pastes the clip there at the play start when the extension asks (the live tests' *Changes ›
      Clip to arranger*); whether its window has to be in front for that; whether the arranger cursor clip
      follows the pasted clip, as the link expects; whether Bitwig has `focus_or_toggle_clip_launcher`, and
      whether selecting a launcher slot from the API gives the launcher the focus without it; whether a launcher
      clip's colour set from the API shows as asked (`exact`) or rounded to Bitwig's palette, and whether the
      pasted clip keeps it, which `sameAsSource` relies on.
- [ ] Parameter IDs from `device` look like `CONTENTS/PID3e8`, and `set` takes them back.
- [ ] `insert` takes the scanner's VST3 class ID as Bitwig wants it, and a UUID for its own devices.
- [ ] The words `type` and `kind` use (e.g. `Instrument`, `note_to_audio`).
- [ ] `project` names the project as its file is named (Check with Bitwig relies on it, and so does
      finding the open project in the library).
- [ ] `modified` clears when the project is saved, and stays clear after Save As (the app re-reads the
      file then).
- [ ] One Save (`action`) saves the project Bitwig has in front, and by which route. On 6.1.3 Bitwig ignored
      Save, copy and paste run straight away, from a request or a task the extension scheduled; the live tests'
      *Changes › Save* now tries each route in turn and names the one that works, recording the project in
      front as Save ran (`project` in the answer). The 3.7 run saved from the flush, which 3.8 makes the
      default, and the 3.8 run saved with no route named (2026-10-15).
- [x] Arrange's checks (3.8, Bitwig 6.1.3, 2026-10-15): by all five routes, a paste into an empty launcher slot
      put nothing there and Duplicate changed nothing, while the clip took every mark colour and the play start
      moved: Bitwig's Copy, Paste and Duplicate act on its own selection, not the one the API makes ([what the
      runs found](#clips-in-the-arranger-an-experiment)).
- [x] `filetrack` (3.8, 2026-10-15): a MIDI file inserted after a track made nothing, no track and no clip.
- [x] The MIDI loopback's ports (Bitwig 6.1.3, 2026-10-15): Bitwig starts the extension, and the link answers,
      with its MIDI in and out unassigned. Save has only worked for the link once they were set: from its flush
      (twice) and straight away (once in two). Without them only straight away was tried (3.3 and 3.6), and it
      didn't save; whether the flush saves without them is untried.
- [ ] Whether Bitwig still starts the extension, and the link answers, with its MIDI in and out set to “Music
      Organiser Bitwig” while that port isn't there (loopMIDI not running): Bitwig stops a controller whose device
      goes away, so it may not. *Bitwig isn't answering* says to check the port, in case.
- [ ] The rest of the MIDI loopback (extension 3.7): Bitwig fills the ports in with the loopMIDI port “Music
      Organiser Bitwig” by itself; the extension's SysEx message comes back whole on its MIDI in (*Link › MIDI
      loopback*); a note-on on channel 16 presses the extension's buttons; and Bitwig carries out Copy, Paste
      and the focus actions run from its flush, on the SysEx message, or for a button press, and Save run on the
      SysEx message or for a button press (which routes work, if any, decides how the app arranges).
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
