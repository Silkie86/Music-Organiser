# Music Organiser extension for Bitwig Studio

The Bitwig link as a Java controller extension (`MusicOrganiser.bwextension`), for Bitwig Studio 6.1 and later
(controller API 23). It replaces the JavaScript controller script in `../bitwig`, which stays in the repository
for now as the fallback. Both are the same controller to Bitwig (same vendor, model and ID), so a Music Organiser
controller already added in Bitwig stays added, with its *Let the app make changes* setting. Install one or the
other, not both.

What it does, its commands and their answers are in [the extensions README](../README.md#bitwig-link). It talks
to the app exactly as the script does, and adds what only an extension can do: Bitwig's newer API, and reports
written to files.

## Building

Needs a JDK (17 or later; Bitwig runs the extension on its own Java).

```
cd extensions/bitwig-java
./mvnw package          # mvnw.cmd package on Windows; or mvn package with Maven installed
```

That runs the tests and makes `target/MusicOrganiser.bwextension`. Maven gets Bitwig's API from Bitwig's own
repository (`com.bitwig:extension-api:23` from maven.bitwig.com); it's only needed to build, since Bitwig has it
when the extension runs, so nothing of Bitwig's goes in the extension or the repository. CI builds it too
(the `bitwig-extension` job) and keeps the file as a download called `music-organiser-bitwig-extension`.

## Installing

The app does it: building the app also builds the extension when a JDK is installed (on Windows,
`winget install Microsoft.OpenJDK.21`; see `MusicOrganiser.BitwigExtension.targets`), and then **Install** in
Settings → DAW links → Bitwig Studio puts the extension in place of the script. By hand: copy
`MusicOrganiser.bwextension` into `Extensions` in Bitwig's user folder (normally
`Documents\Bitwig Studio\Extensions`; on Linux `~/Bitwig Studio/Extensions`), and take
`Controller Scripts\Music Organiser` out of that folder if the JavaScript script is there. Bitwig picks the
extension up by itself (restart Bitwig if it doesn't). If the controller isn't added yet: Settings → Controllers → Add controller →
Music Organiser → DAW link.

## Reports

The extension writes to the app's data folder, next to MO Probe's reports:

| System | Folder |
|---|---|
| Windows | `%LOCALAPPDATA%\MusicOrganiser\reports\bitwig` |
| macOS | `~/Library/Application Support/MusicOrganiser/reports/bitwig` |
| Linux | `$XDG_DATA_HOME/MusicOrganiser/reports/bitwig` (or `~/.local/share/...`) |

- **`<date>-link.jsonl`** (UTC date): one line for each command the app sent (but a `hello` that went well: the
  app says hello every couple of seconds), with `time`, `cmd`, `ms` (how long
  the answer took), `ok` and `error`, and a line when the extension starts (`event: "start"`, with Bitwig's
  version), stops, and when the app connects or goes. Nothing from a project goes in beyond names, and the
  paths the app sent when an error mentions one.
- **`<time>-selfcheck.json`**: a self-check, written a second after Bitwig starts the extension (once Bitwig
  has sent the values it watches) and whenever the app sends `selftest`. It has Bitwig's version and API
  version, the Java version, the extension's version, whether changes are allowed, and a check for each part
  of Bitwig's API the link uses or offers (`name`, `ok`, `detail`, `ms`): the application, project, transport,
  the selected track and device and its parameters, drum pads, the 128 tracks with their devices, clip slots
  and IDs, the Sampler and Note Transpose pitch, the popup browser, the settings, the remote controls, the last
  clicked parameter, parameter steps, origins and automation, the master recorder, the arranger (its timeline
  and clip launcher, the panel layout, the play start, the clip selected there and launcher clips' colours, for
  `arrange`) and Bitwig's
  actions, then whether the app can connect (port 19791) and whether the reports folder takes files. Each part
  is set up on its own, so one that fails in a future Bitwig leaves the rest working, and the self-check says
  which and why.

Reports older than 14 days (by the date their names start with) are deleted when the extension starts; other
files in the folder are left alone. Writing is best effort: a report that can't be written never stops the link,
and the self-check says what went wrong.

## What's new in link version 3

`hello` says `link: 3`, `runtime: "java"`, `extension` (this extension's version) and `loadedFrom` (the file Bitwig loaded it from, when it can tell, so the app can put the next version there). (`engine` still says
whether Bitwig's audio engine is on.) The rest is additions, which the app can ignore:

- `project`: each track's `id`, the ID Bitwig keeps it under, once Bitwig has said.
- `device`: the selected device's remote controls on the page Bitwig shows, as `page` and `controls` (`index`,
  `name`, `value`, `display`, `steps`, `stepNames`, `origin`, `automated`, and `param`: the parameter it is,
  when the link can tell). A parameter that is one of those controls carries the same `steps` (-1 for a
  continuous value), `stepNames` (when Bitwig names them), `origin` (0 to 1: where the value rests, 0.5 for a
  pan) and `automated`. Bitwig's API gives these only for remote controls and the last clicked parameter, not
  for every parameter by its ID, and doesn't say which parameter a control is; the link matches a control to
  the one parameter with its name and value.
- `clicked`: the parameter last clicked in Bitwig: `exists` (false until one is clicked), `name`, `value`,
  `display`, `steps`, `stepNames`, `origin`, `automated`, and `locked` (whether it's pinned in Bitwig).
- `recorder` ✎ with `do`: `"start"`, `"stop"` or `"status"` (the default): Bitwig's master recorder, answering
  once it has started or stopped with `active` and `duration` (in milliseconds).
- `selftest`: runs the self-check, writes it to the reports folder and answers with it, plus `file`.
- `arrange` ✎ with `track`, `slot` and `at` (optional, in beats): **an experiment** (extension 3.6), which only
  the live tests send. Bitwig's API can't put a clip on the arranger timeline, so the link copies a launcher
  clip and pastes it there, as a person would. Paste puts down whatever is on Bitwig's clipboard, which may be
  something copied before when the copy didn't take, so before it copies the link gives the launcher clip a
  fresh colour (the test project's clip keeps it) and checks the pasted clip has it. The answer says what
  Bitwig showed before and after: `arranged`, whether it worked (`appeared` and `sameAsSource` both true);
  `appeared`, when a clip showed up selected on the track after the paste; `sameAsSource`, when that clip has
  the launcher clip's marking colour; `copiedFromTimeline`, when the timeline had the focus as it copied, so it
  didn't paste; `layout`, `launcher`, `mark` (the clip's colour `before`, the one `asked` for, the one it `got`
  and whether it `took`), `launcherFocus`, `timeline`, `focus`, the clip selected in the arranger `before`,
  `beforePaste` and `after`, its `pastedColor`, and the track's `launcherClipsBefore` and
  `launcherClipsAfter`. See [the extensions README](../README.md#clips-in-the-arranger-an-experiment).

Not new to version 3, since the script does it too: any request can carry `onlyIn`, a project's name, and the
link refuses it, or stops a change part way, unless Bitwig has that project open; `hello` says `onlyIn: true`.
The live tests use it to keep their changes in their own project (see
[the extensions README](../README.md#only-in-one-project)).

## Code

`src/main/java/org/musicorganiser/bitwig`:

- `MusicOrganiserExtensionDefinition`: who the extension is to Bitwig (listed in
  `META-INF/services/com.bitwig.extension.ExtensionDefinition`).
- `Link`: the link itself, a port of `MusicOrganiser.control.js` function by function, plus the newer API
  and the self-check. Everything runs on Bitwig's controller thread.
- `Json`, `Framing`: a small JSON reader and writer (so the extension needs nothing but the JDK) and how
  messages travel.
- `Reports`: the reports folder and its files. The folder and the clock are handed in, so tests use their own.

## Tests

`mvn test` (or `./mvnw test`) runs the extension without Bitwig, in `src/test/java/org/musicorganiser/bitwig`:

- `FakeBitwig`: a stand-in Bitwig, a port of the script's (`../bitwig/test/fake-bitwig.js`) made from Bitwig's
  own API interfaces with `java.lang.reflect.Proxy`. As in Bitwig, reading a value the extension didn't mark as
  interesting in init fails, changes happen a moment after they're asked for and reach the extension bit by
  bit, and time only passes when a test says so. Calling anything it doesn't stand in for fails and names it.
  The tests talk to the extension as the app does: requests in through the connection's receive callback,
  answers checked as plain ASCII JSON lines.
- `LinkTest`: every scenario of the script's tests (`../bitwig/test/link.test.js`), under the same names, plus
  two apps at once and a connection that breaks.
- `NewerApiTest`: link version 3's additions: track IDs, remote controls' `steps`, `stepNames`, `origin` and
  `automated`, `clicked` and `recorder`.
- `ArrangerTest`: the `arrange` experiment, with the stand-in's arranger, clipboard and focused panel (copy and
  paste work in the panel in front, and carry the clip's colour): a clip copied into the arranger at bar 5 in
  its marking colour, the Arrange layout switched to and a hidden clip launcher and timeline shown, the
  launcher given the focus first (twice when the first go hid it), no paste when the timeline had the focus as
  the link copied, each step Bitwig doesn't go along with (with nothing pasted), and the answers when Bitwig
  pastes into the launcher instead or ignores paste, without its focus action, or without an arranger for
  extensions at all. Also the colour mark: a clip already on the clipboard pasted when the copy didn't take
  (appeared, but not the same clip), a colour Bitwig ignores or rounds to its own, and a fresh colour for each
  arrange.
- `SelfCheckTest`, `LinkLogTest`, `ReportsTest`: the self-check and the reports, in a temporary folder,
  including a Bitwig missing part of the API (each part fails on its own), another port, an older API and a
  folder that can't be written.
- `ParityTest`: sends the same few hundred requests, plenty of them odd, to the script (in its own stand-in, run
  with Node.js by `src/test/js/run-script.js`) and to the extension, and checks every answer is the same apart
  from version 3's additions. It's skipped without Node.js, except on CI.
- `JsonTest`, `FramingTest`: the JSON reader and writer, and how messages travel.
