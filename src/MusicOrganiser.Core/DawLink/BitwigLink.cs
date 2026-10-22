using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MusicOrganiser.Core.DawLink;

/// <summary>What the Bitwig script says when the app connects.</summary>
/// <param name="Engine">Whether Bitwig's audio engine is on.</param>
/// <param name="Changes">Whether "Let the app make changes" is on in the controller's settings in Bitwig.</param>
/// <param name="Runtime">"java" when the Bitwig extension answers (link version 3); null for the script.</param>
/// <param name="Extension">The extension's version, when it's the extension.</param>
/// <param name="OnlyIn">Whether the link honours <see cref="BitwigLink.OnlyIn"/> (older scripts don't).</param>
/// <param name="Loopback">
/// Whether the extension's own MIDI has come back to it through the loopback (extension 3.7), which the routes through
/// MIDI need (see <see cref="BitwigLink.RunActionAsync"/>); Bitwig 6.1.3 also saved from its flush only once the
/// loopback's ports were set (<see cref="BitwigLink.LoopbackMissing"/>). Null when the link doesn't say.
/// </param>
public sealed record BitwigHello(int Link, string Product, string Version, int Api, string Platform, int Port,
                                 string Project, bool Modified, bool Engine, bool Changes, string? Runtime = null,
                                 string? Extension = null, bool OnlyIn = false, string? LoadedFrom = null, bool? Loopback = null)
{
    /// <summary>What answers in Bitwig: the extension or the script.</summary>
    public string Answering => Runtime == "java" ? $"the Music Organiser extension {Extension}".TrimEnd() : "the Music Organiser script";
}

/// <summary>
/// A clip selected in Bitwig with its notes (the Java extension 3.15: see <see cref="BitwigLink.ClipNotesAsync"/>).
/// </summary>
/// <param name="From">"arranger" or "launcher": where it's selected.</param>
/// <param name="OnSelectedTrack">Whether it's on the track selected in Bitwig.</param>
/// <param name="Start">The clip's loop start, in beats.</param>
/// <param name="Length">The clip's loop length, in beats.</param>
/// <param name="Cut">The clip is longer than the extension reads (256 beats): notes past that aren't in <see cref="Notes"/>.</param>
/// <param name="Notes">Every note, from the clip's start.</param>
public sealed record BitwigClipNotes(string From, string Track, bool OnSelectedTrack, double Start, double Length, bool Cut,
                                     IReadOnlyList<BitwigNote> Notes);

/// <summary>A note in a Bitwig clip: where it starts and how long it lasts in beats, its key and velocity (0 to 127).</summary>
public sealed record BitwigNote(double Start, double Length, int Key, int Velocity);

/// <summary>The project open in Bitwig, as Bitwig shows it.</summary>
/// <param name="TrackCount">Every track in the project; <see cref="Tracks"/> holds the first 128.</param>
public sealed record BitwigProject(string Name, bool Modified, double Tempo, string TimeSignature, bool Playing,
                                   double Position, int TrackCount, IReadOnlyList<BitwigTrack> Tracks);

/// <summary>One track in Bitwig's flat track list (tracks inside groups included, then effect tracks and the master).</summary>
/// <param name="DeviceCount">Devices at the top of the track's chain; <see cref="Devices"/> holds the first 16.</param>
public sealed record BitwigTrack(int Index, string Name, string Type, bool Group, string Color, bool Active, bool Mute,
                                 bool Solo, bool Arm, string Volume, double? VolumeValue, string Pan, double? PanValue,
                                 int DeviceCount, IReadOnlyList<BitwigDevice> Devices);

/// <param name="Index">Its place in the track's chain (-1 for the selected device).</param>
/// <param name="Kind">Bitwig's device type, e.g. note_to_audio for an instrument.</param>
public sealed record BitwigDevice(int Index, string Name, string Preset, string Kind, bool Plugin, bool Enabled);

/// <summary>The device selected in Bitwig, with every parameter Bitwig lists for it.</summary>
/// <param name="DrumPads">Whether it has drum pads (a Drum Machine).</param>
public sealed record BitwigSelection(string Track, BitwigDevice? Device, IReadOnlyList<BitwigParam> Params, bool DrumPads = false);

/// <param name="Id">The ID Bitwig keeps the parameter under, e.g. "CONTENTS/PID3e8" for a plugin's parameter 1000.</param>
/// <param name="Value">0 to 1, or null when Bitwig can't read it.</param>
/// <param name="Display">The value as Bitwig shows it ("-6.0 dB"), or null if it didn't say in time.</param>
/// <param name="Stale">
/// Bitwig shows a new value (its text has changed) but hasn't told the link the number since, so <paramref name="Value"/>
/// is null (the Java extension from 3.13). Bitwig 6.1.5 did that with a plugin's knobs that aren't on its remote controls
/// page, whether the plugin or the link turned them.
/// </param>
public sealed record BitwigParam(string Id, string Name, double? Value, string? Display, bool Stale = false);

public sealed record BitwigAction(string Id, string Name, string Category, string Menu);

/// <summary>How one of Bitwig's actions ran (<see cref="BitwigLink.RunActionAsync"/>).</summary>
/// <param name="Project">
/// The project Bitwig had in front just before the action ran, as the link says it (the script, and the Java extension
/// from 3.7), or null when an older link doesn't say.
/// </param>
/// <param name="Via">The route the link ran it by: "direct" (straight away) from a link that doesn't say.</param>
/// <param name="Ran">
/// Whether the link ran it by that route. A route through Bitwig's flush or the MIDI loopback may not come back (the
/// loopback isn't set up, say), and then the link didn't run it. True from a link that doesn't say, as it ran it straight
/// away. Whether Bitwig then did what the action does is another matter: Bitwig 6.1.3 ignored Save run straight away.
/// </param>
public sealed record BitwigActionRun(string? Project, string Via, bool Ran);

/// <summary>What Bitwig added to a track's chain. Added is false when it ignored the plugin, device or file.</summary>
/// <param name="Index">Where the new device is in the chain.</param>
public sealed record BitwigInserted(bool Added, int? Index, BitwigDevice? Device);

/// <summary>A sample for a Drum Machine pad: the MIDI note the pad plays on, and the name to give the pad.</summary>
public sealed record BitwigPad(int Note, string Path, string Name = "");

/// <summary>
/// The pads (by note) that got their sample, that already had something on them, and that Bitwig didn't fill. The
/// link also says what was on each pad left alone (since extension 3.2), and which samples went in place of an empty
/// Sampler a Drum Machine preset put on the pad (since 3.3; they're among the filled).
/// </summary>
public sealed record BitwigPadsFilled(IReadOnlyList<int> Filled, IReadOnlyList<int> Skipped, IReadOnlyList<int> Failed,
                                      IReadOnlyList<BitwigPadContents>? Taken = null, IReadOnlyList<int>? Replaced = null);

/// <summary>What Bitwig showed on a pad: its name, how many devices, the first one's name and its sample's file name.</summary>
public sealed record BitwigPadContents(int Note, string Name, int Devices, string First, string Sample = "");

/// <summary>A pitch Bitwig set: on which device, in semitones, and as Bitwig shows it ("-3.50 st").</summary>
public sealed record BitwigPitch(string Device, double Value, string Display);

/// <summary>
/// What the extension saw while it tried to put a launcher clip in Bitwig's arranger (an experiment, extension 3.6: see
/// <see cref="BitwigLink.ArrangeAsync"/>).
/// </summary>
/// <param name="Arranged">
/// Whether it worked: a clip <paramref name="Appeared"/> on the clip's track, and it's a copy of the launcher clip
/// (<paramref name="SameAsSource"/>), marked in the very colour the extension asked for (<see cref="BitwigClipMark.Exact"/>).
/// Where the clip starts can't be read.
/// </param>
/// <param name="Appeared">
/// Whether, after the paste, the clip selected in the arranger had moved to the clip's track (it wasn't there just before
/// the paste), with the track's launcher clips as they were. That alone doesn't say which clip it is: paste puts down
/// whatever is on Bitwig's clipboard, which is whatever was copied before (in any project) when the extension's copy
/// didn't take.
/// </param>
/// <param name="SameAsSource">
/// Whether the clip selected in the arranger after the paste has the colour the extension marked the launcher clip with
/// (<paramref name="Mark"/>), so it's a copy of that clip. Always false when the mark didn't take. When Bitwig rounded the
/// mark to one of its own colours (it isn't <see cref="BitwigClipMark.Exact"/>), other clips can have that colour too, so
/// then it doesn't say for sure.
/// </param>
/// <param name="CopiedFromTimeline">
/// Whether the arranger timeline had the focus when the extension copied, so Bitwig copied from the timeline rather than
/// the launcher clip: <paramref name="Focus"/> is 2 then. The extension doesn't paste when it is.
/// </param>
/// <param name="At">Where in the arranger it was meant to go, in beats (quarter notes) from the start.</param>
/// <param name="Layout">The panel layout Bitwig showed before: ARRANGE, MIX or EDIT. The extension switches to ARRANGE.</param>
/// <param name="Launcher">Whether Bitwig showed the clip launcher in its Arrange layout before (the extension shows it), and at the end.</param>
/// <param name="Mark">
/// How the extension gave the launcher clip a fresh colour before it copied, so it could tell that clip from whatever else
/// paste might put down.
/// </param>
/// <param name="LauncherFocus">
/// How many times the extension ran Bitwig's focus_or_toggle_clip_launcher action to give the clip launcher the focus
/// before it copied: 0 when Bitwig has no such action, twice when the first hid the launcher (it was in front already).
/// </param>
/// <param name="Timeline">Whether Bitwig showed its arranger timeline before, and at the end.</param>
/// <param name="Focus">
/// How many times the extension ran Bitwig's focus_or_toggle_arranger action to bring the timeline to the front: twice when
/// the first hid it, since it hides the timeline when that's in front already.
/// </param>
/// <param name="Before">The clip selected in the arranger before the extension did anything.</param>
/// <param name="BeforePaste">The same just before the paste (or where the paste would have been), which <paramref name="After"/> is compared with.</param>
/// <param name="After">The same once Bitwig had pasted, or once the extension had waited long enough.</param>
/// <param name="PastedColor">The colour (#RRGGBB) of the clip selected in the arranger at the end, or null when none is.</param>
/// <param name="LauncherClipsBefore">
/// How many of the track's first 8 launcher slots had a clip before; more in <paramref name="LauncherClipsAfter"/> means
/// Bitwig pasted into the launcher instead.
/// </param>
/// <param name="Via">The route the extension copied, pasted and ran the focus actions by (extension 3.7), or null when it doesn't say.</param>
/// <param name="Ran">Whether each of those steps ran by that route (extension 3.7), or null when the extension doesn't say.</param>
/// <param name="Checks">
/// What the extension's checks found (extension 3.8), when they were asked for and could tell something: arrange's paste
/// ran but put nothing in the arranger, and Bitwig has the launcher's focus action. Else null.
/// </param>
public sealed record BitwigArranged(bool Arranged, bool Appeared, bool SameAsSource, bool CopiedFromTimeline, double At, string Layout,
                                    BitwigShown Launcher, BitwigClipMark Mark, int LauncherFocus, BitwigShown Timeline, int Focus,
                                    BitwigArrangerClip Before, BitwigArrangerClip BeforePaste, BitwigArrangerClip After, string? PastedColor,
                                    int LauncherClipsBefore, int LauncherClipsAfter, string? Via = null, BitwigArrangeRan? Ran = null,
                                    BitwigArrangeChecks? Checks = null);

/// <summary>
/// What the arrange experiment's checks found (extension 3.8), run by the same route once arrange's paste ran but put
/// nothing in the arranger, to tell which part failed: the copy, or the arranger's part.
/// </summary>
/// <param name="LauncherPaste">
/// A paste into an empty launcher slot on the track, which needs no arranger: null when the track had no empty slot the
/// link could reach.
/// </param>
/// <param name="Duplicate">Bitwig's Duplicate on the launcher clip, which needs no clipboard: null when Bitwig has no Duplicate action.</param>
/// <param name="Error">What stopped the checks part way (another project came to the front, say), or null.</param>
public sealed record BitwigArrangeChecks(BitwigLauncherPasteCheck? LauncherPaste, BitwigDuplicateCheck? Duplicate, string? Error = null);

/// <summary>
/// The arrange experiment's paste into an empty launcher slot (extension 3.8): the link selects the slot, gives the clip
/// launcher the focus and pastes. When the mark took (and is exact), a clip in the launcher clip's marking colour there
/// means the copy took, so the arranger's part is what fails, and a clip in another colour means Bitwig pasted what its
/// clipboard held before. The check stops before the paste when Bitwig doesn't select the slot or the route doesn't run
/// the launcher's focus action.
/// </summary>
/// <param name="Slot">The empty slot (0 is the first).</param>
/// <param name="Selected">Whether Bitwig selected it. When it didn't, the check stopped there.</param>
/// <param name="Focus">How many times the link ran the launcher's focus action (twice when the first hid it).</param>
/// <param name="Made">Whether a clip appeared in the slot.</param>
/// <param name="Colour">That clip's colour (#RRGGBB), or null when none appeared.</param>
/// <param name="SameAsMark">
/// Whether the mark took and that's the colour the launcher clip got from it. When the mark is also exact, that makes it
/// the clip's copy; a colour Bitwig rounded the mark to, other clips can have too.
/// </param>
/// <param name="Removed">Whether the link deleted that clip again, so the next go starts as this one did.</param>
public sealed record BitwigLauncherPasteCheck(int Slot, bool Selected, int Focus, BitwigLauncherPasteRan Ran, bool Made, string? Colour,
                                              bool SameAsMark, bool Removed);

/// <summary>Whether the launcher's focus action and the paste ran by the route: null for a step the link didn't get to.</summary>
public sealed record BitwigLauncherPasteRan(bool? Focus, bool? Paste);

/// <summary>
/// The arrange experiment's Duplicate on the launcher clip (extension 3.8): the link selects the clip again, gives the clip
/// launcher the focus and runs Bitwig's Duplicate action. A new launcher clip means Bitwig's editing actions work in the
/// launcher by this route; a new track means Duplicate went to the track, so the launcher didn't have the focus.
/// </summary>
/// <param name="Selected">Whether Bitwig selected the clip. When it didn't, the check stopped there.</param>
/// <param name="Focus">How many times the link ran the launcher's focus action.</param>
/// <param name="Launcher">How many more launcher clips the track had after it.</param>
/// <param name="Tracks">How many more tracks the project had after it. A new track is left in the project.</param>
/// <param name="Removed">Whether the link deleted the new launcher clip again.</param>
public sealed record BitwigDuplicateCheck(bool Selected, int Focus, BitwigDuplicateRan Ran, int Launcher, int Tracks, bool Removed);

/// <summary>Whether the launcher's focus action and Duplicate ran by the route: null for a step the link didn't get to.</summary>
public sealed record BitwigDuplicateRan(bool? Focus, bool? Duplicate);

/// <summary>
/// What Bitwig did with a file the link inserted after a track, as a new track (extension 3.8: see <see
/// cref="BitwigLink.FileTrackAsync"/>).
/// </summary>
/// <param name="Added">How many tracks Bitwig added (0 when it did nothing with the file).</param>
/// <param name="Index">The new track's place in the track list, or null when there's none.</param>
/// <param name="Name">The new track's name (Bitwig names it after the file), or null.</param>
/// <param name="Type">The new track's type ("Instrument" for a MIDI file), or null.</param>
/// <param name="LauncherClips">
/// How many clips the new track has in its first 8 launcher slots: more than 0 means Bitwig put the file in the launcher,
/// not the arranger. Null when there's no new track.
/// </param>
/// <param name="Selected">Whether Bitwig selected the new track.</param>
/// <param name="Arranger">
/// The clip selected in the arranger after: on the new track when Bitwig put the file in the arranger and selected it there.
/// </param>
/// <param name="ArrangerBefore">The same before, which <paramref name="Arranger"/> is compared with.</param>
/// <param name="At">Where the play start was when the file went in, in beats from the start.</param>
/// <param name="OnTrack">The launcher clips on the track the file went after, before and after, in case Bitwig put it there.</param>
public sealed record BitwigFileTrack(int Added, int? Index, string? Name, string? Type, int TracksBefore, int TracksAfter, int? LauncherClips,
                                     bool Selected, BitwigArrangerClip Arranger, BitwigArrangerClip ArrangerBefore, double At,
                                     BitwigClipCount OnTrack);

/// <summary>How many launcher clips a track had, before and after (null after when the track can't be found).</summary>
public sealed record BitwigClipCount(int Before, int? After);

/// <summary>
/// Whether each step of the arrange experiment ran by its route (extension 3.7): giving the clip launcher the focus, the
/// copy, bringing the arranger timeline to the front and the paste. Null for a step the extension didn't get to (or, for
/// the launcher's focus, when Bitwig has no action for it); false when the route didn't run it.
/// </summary>
public sealed record BitwigArrangeRan(bool? LauncherFocus, bool? Copy, bool? Focus, bool? Paste);

/// <summary>
/// How the arrange experiment marked the launcher clip with a fresh colour before it copied it, so it could tell its own
/// clip from whatever else was on Bitwig's clipboard. Colours are written #RRGGBB.
/// </summary>
/// <param name="Before">The launcher clip's colour before.</param>
/// <param name="Asked">The colour the extension asked for.</param>
/// <param name="Got">The colour the clip showed then, which a pasted clip is compared with: Bitwig may round a colour to one of its own.</param>
/// <param name="Took">Whether the clip's colour changed. When it didn't, the extension can't tell its clip from another once pasted.</param>
/// <param name="Exact">
/// Whether the clip got the very colour asked for (give or take 2 of 255 in each of red, green and blue). When Bitwig rounded
/// it to one of its own colours instead, other clips can have that colour too (one already on Bitwig's clipboard, say), so
/// the extension can't be sure a pasted clip in it is the launcher clip's copy.
/// </param>
public sealed record BitwigClipMark(string Before, string Asked, string Got, bool Took, bool Exact);

/// <summary>Whether Bitwig showed one of its panels (the arranger timeline, or the clip launcher), before and after.</summary>
public sealed record BitwigShown(bool Before, bool After);

/// <summary>The clip selected in Bitwig's arranger: whether there is one, and the name of the track it's on (null when there's none).</summary>
public sealed record BitwigArrangerClip(bool Exists, string? Track);

/// <summary>A track Bitwig added (<see cref="BitwigLink.NewTrackWithIdAsync"/>): its place in the listing, its name and its ID (null from a link before the extension 3.9).</summary>
public sealed record BitwigNewTrack(int Index, string Name, string? Id = null);

/// <summary>
/// What the record job is asked to do (the Java extension 3.9: see <see cref="BitwigLink.RecordAsync"/>): record the clip in
/// launcher slot <paramref name="Slot"/> of the track with ID <paramref name="TrackId"/> into the arranger at <paramref
/// name="At"/> beats (quarter notes) from the start, <paramref name="Length"/> beats long. Both are whole bars.
/// </summary>
public sealed record BitwigRecordRequest(string TrackId, int Slot, double At, double Length)
{
    /// <summary>False to play the clip without recording it, to tell whether the link hears the track's notes.</summary>
    public bool Record { get; init; } = true;
    /// <summary>Whether to arm the track while it records (every other track is disarmed either way).</summary>
    public bool Arm { get; init; }
    /// <summary>Whether to delete the launcher clip once it's recorded, before the replay: what's heard then comes from the arranger alone.</summary>
    public bool DeleteLauncherClip { get; init; }
    /// <summary>Whether to play the arranger back afterwards to hear what went in (null: when it records).</summary>
    public bool? Replay { get; init; }
    /// <summary>Where the replay starts, in beats (null: at <see cref="At"/>); earlier takes in an earlier recording on the way.</summary>
    public double? ReplayFrom { get; init; }
    /// <summary>The tempo to record at (BPM, 20 to 666), or null for the project's: a faster one records the same beats sooner.</summary>
    public double? Tempo { get; init; }
    /// <summary>A name for the go, which the answer gives back.</summary>
    public string? Pass { get; init; }
}

/// <summary>
/// What the record job did (the Java extension 3.9: see <see cref="BitwigLink.RecordAsync"/>).
/// </summary>
/// <param name="Decided">
/// What it made of it: "recorded" (the replay heard the clip's notes where it was recorded, with the launcher clip not
/// playing), "nothing" (it heard the clip play but not in the replay), "cantTell" (it heard no notes either time, so only a
/// look tells), "heard" or "cantHear" (it played without recording, and heard the notes or not), "notChecked" (no replay),
/// "didntStart" (Bitwig didn't play), "refused" (a setting didn't take, so it recorded nothing), "aborted", "failed",
/// or "abandoned" (another project came to the front, so it left Bitwig alone).
/// </param>
/// <param name="Bar">A bar's length in beats, from the project's time signature.</param>
/// <param name="Refused">The settings that didn't take (by the extension's names, such as "pre-roll"), so it didn't record.</param>
/// <param name="Played">The clip's play: where from, whether and when Bitwig started, how it ended, and the notes heard. Null when it didn't launch.</param>
/// <param name="Heard">How many notes the link heard the track play while the clip played.</param>
/// <param name="LauncherClipDeleted">Whether the launcher clip was deleted after recording, or null when that wasn't asked for (or it didn't record).</param>
/// <param name="Replay">The replay of the arranger after, or null when there was none.</param>
/// <param name="After">What Bitwig showed at the end.</param>
/// <param name="Diff">What changed in the tracks and launcher slots since the job started, by track ID; null when it couldn't look.</param>
/// <param name="Ms">How long the job took.</param>
/// <param name="Trace">
/// From extension 3.11, a line for each time the job looked (every 25 ms) and each command it sent, with Bitwig's play
/// position and what played ("1234 look @16.042 playing slot notes 1"), for working out what Bitwig did; null before.
/// </param>
public sealed record BitwigRecorded(string Decided, string TrackId, string? Track, int Slot, double At, double Length, double Bar, bool Record,
                                    bool Arm, BitwigRecordTempo Tempo, IReadOnlyList<string> Refused, BitwigPlayed? Played, BitwigRecordSeen Seen,
                                    bool? LauncherClipDeleted, BitwigPlayed? Replay, BitwigRecordAfter After, BitwigRecordDiff? Diff, double Ms,
                                    string? Pass = null, string? AbortWhy = null, string? Error = null, int Heard = 0,
                                    IReadOnlyList<string>? Trace = null);

/// <summary>The project's tempo, and the one the job recorded at (BPM).</summary>
public sealed record BitwigRecordTempo(double Project, double Used);

/// <summary>
/// One time the record job played the clip, or the arranger back: where from (beats), whether Bitwig started and how long
/// after it was asked, how it ended ("done", "timedOut", "stoppedEarly", "aborted"...), whether where it was came from
/// Bitwig ("bitwig") or the clock ("clock", when Bitwig's play position didn't move), and the notes the link heard the
/// track play. A replay also says where it played to, and how many notes it heard in the recording's bars (<paramref
/// name="InPass"/>), before them (<paramref name="Before"/>, earlier recordings), and whether the launcher clip played.
/// </summary>
public sealed record BitwigPlayed(double From, bool Started, double? StartedAfterMs, string? Ended, string PositionFrom, int Heard,
                                  IReadOnlyList<BitwigHeardNote> Notes, double? To = null, int? InPass = null, int? Before = null,
                                  bool? LauncherPlayed = null);

/// <summary>A note the link heard a track play: its key (60 = C3) and where it started, in beats.</summary>
public sealed record BitwigHeardNote(int Key, double At);

/// <summary>
/// What the record job saw while it recorded: whether the launcher slot played, whether Bitwig said it was recording into
/// it (it shouldn't), whether Bitwig turned automation write on (its "Write automation when recording" preference), and
/// where the link asked the track to stop (beats), or null when it didn't.
/// </summary>
public sealed record BitwigRecordSeen(bool ClipPlayed, bool SlotRecording, bool AutomationWriteSeenOn, double? StopSentAt);

/// <summary>
/// What Bitwig showed at the end of the record job, when its project was still in front (<paramref name="Checked"/>):
/// whether arranger record is off, whether it plays, where the play start is (beats), and the settings that didn't come
/// back as they were (null when the job didn't wait to see).
/// </summary>
public sealed record BitwigRecordAfter(bool Checked, bool? RecordOff, bool? Playing, double? PlayStart, IReadOnlyList<string>? RestoreFailed);

/// <summary>What changed while the record job ran: the tracks added (by ID, with their names) and removed (IDs), and the launcher slots whose clip came or went.</summary>
public sealed record BitwigRecordDiff(IReadOnlyList<BitwigTrackRef> TracksAdded, IReadOnlyList<string> TracksRemoved,
                                      IReadOnlyList<BitwigClipChange> ClipsChanged);

/// <summary>A track by its ID and name.</summary>
public sealed record BitwigTrackRef(string Id, string Name);

/// <summary>A launcher slot whose clip came or went: whether it had one before and after.</summary>
public sealed record BitwigClipChange(string TrackId, int Slot, bool Before, bool After);

/// <summary>
/// What the probe of Bitwig's panel commands found (the Java extension 3.9: see <see cref="BitwigLink.ProbeAsync"/>): the
/// route, the panel layout before, and a step for each command.
/// </summary>
public sealed record BitwigProbed(string Via, string Layout, IReadOnlyList<BitwigProbeStep> Steps);

/// <summary>
/// One of the probe's commands: its action ID, whether the route ran each of its two goes, whether its panel showed before
/// and after each (three values), and whether the link showed or hid the panel as it was afterwards (null when it needn't),
/// or <paramref name="Missing"/> when Bitwig has no such action.
/// </summary>
public sealed record BitwigProbeStep(string Action, IReadOnlyList<bool>? Ran = null, IReadOnlyList<bool>? Visible = null, bool? Restored = null,
                                     bool Missing = false);

/// <summary>
/// Talks to Bitwig Studio through the Music Organiser controller script
/// (extensions/bitwig/MusicOrganiser.control.js), which listens on port 19791 on this computer.
/// Each message is JSON with a 4-byte big-endian length in front.
/// </summary>
public sealed class BitwigLink : IDisposable
{
    public const int DefaultPort = 19791;
    /// <summary>The script version this app talks to. Version 1 scripts can be read but can't take tracks, clips or kits.</summary>
    public const int LinkVersion = 2;
    public const string OldScript = "Bitwig has an older copy of the Music Organiser script. Install it again from Settings > DAW links " +
                                    "(Bitwig picks up the new one by itself), then try again.";
    /// <summary>
    /// The MIDI port the Java extension (3.7 and later) has its MIDI in and out set to, for its loopback (made in loopMIDI
    /// on Windows). Never the FL Studio link's port (<see cref="FlSetup.PortName"/>), which has to stay clear of it: FL
    /// Studio must never get what the Bitwig link sends itself, and the FL Studio link must never take this port for its own.
    /// </summary>
    public const string LoopbackPort = "Music Organiser Bitwig";
    /// <remarks>
    /// Bitwig starts the extension 3.7 and later, and the link answers, with its MIDI in and out unassigned (confirmed on
    /// Bitwig 6.1.3): <see cref="LoopbackMissing"/> says when they're needed. Whether it starts the extension while they're
    /// set to a port that isn't there (loopMIDI not running) isn't known, and Bitwig stops a controller whose device goes
    /// away, so this says to check that port too.
    /// </remarks>
    public const string NotRunning = "Bitwig isn't answering. Check that Bitwig Studio is open, with the Music Organiser " +
                                     "controller added (Settings > Controllers > Add controller > Music Organiser > DAW link). " +
                                     $"If that controller's MIDI in and out are set to “{LoopbackPort}”, check that the port is there " +
                                     "(on Windows, that loopMIDI is running): Bitwig may not start a controller whose port is missing.";

    /// <summary>
    /// What to set up when the extension's MIDI loopback doesn't work (its hello's <see cref="BitwigHello.Loopback"/> is
    /// false), as a sentence to add after saying the app connected; null when it works, or the link doesn't say (the
    /// script, or an extension before 3.7). Bitwig 6.1.3 answered the link without it, but has only saved for it once the
    /// extension's MIDI in and out were set (from its flush, and once straight away), so the app asks for them. The extension can't
    /// tell unset ports from a port that doesn't hand its MIDI back, so this says it may be either.
    /// </summary>
    public static string? LoopbackMissing(BitwigHello hello) => hello.Loopback is false
        ? $"The extension's own MIDI hasn't come back to it through the MIDI port “{LoopbackPort}”, so its MIDI in and out may not be " +
          "set to that port yet (or the port isn't working), and Bitwig has only saved for the app once they were: " +
          (OperatingSystem.IsWindows() ? $"make a port called “{LoopbackPort}” in loopMIDI, then "
                                       : $"make a virtual MIDI port called “{LoopbackPort}” (on a Mac, an IAC bus in Audio MIDI Setup may do), then ") +
          "in Bitwig, Settings > Controllers > Music Organiser, set both MIDI in and MIDI out to it (never to FL Studio's port, " +
          "“Music Organiser”)."
        : null;
    const int MaxMessage = 64 << 20;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly BufferedStream _incoming;
    readonly SemaphoreSlim _writing = new(1, 1);
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _waiting = new();
    readonly TaskCompletionSource<JsonElement> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationTokenSource _closing = new();
    volatile bool _open = true;
    int _lastId;

    /// <summary>How long to wait for each answer. Reading a big plugin's parameters can take a couple of seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long to wait for a change that Bitwig makes and the script waits to see: a new track, a device, a
    /// clip, a kit. The script gives up on each step after 5 seconds, and a kit has a few.
    /// </summary>
    public TimeSpan ChangeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The port the app connects on when not told one: the script's, or a stand-in Bitwig's in tests.</summary>
    public static int Port { get; set; } = DefaultPort;

    /// <summary>What Bitwig said when the link connected.</summary>
    public BitwigHello Hello { get; private set; } = null!;

    /// <summary>The same as it came, with any fields newer than <see cref="BitwigHello"/> (the live tests look at those).</summary>
    public JsonElement HelloJson { get; private set; }

    public bool IsOpen => _open;

    /// <summary>
    /// Asked just before each request that changes or selects something in Bitwig's project (the changes, which need "Let
    /// the app make changes", and select); it throws to stop the request. The live tests use it to check, right before each
    /// one, that Bitwig still has their test project open.
    /// </summary>
    public Func<CancellationToken, Task>? BeforeChange { get; set; }

    /// <summary>
    /// A project's name, or null: when set, each request that changes or selects something carries it ("onlyIn"), and the
    /// link in Bitwig refuses the request, or stops a change part way, unless that project is the one open. Bitwig makes
    /// some changes in steps, and only the link sees each step, so this is what keeps a change in the project it was meant
    /// for when another one comes to the front meanwhile (<see cref="BitwigHello.OnlyIn"/> says whether the link does it).
    /// </summary>
    public string? OnlyIn { get; set; }

    /// <summary>The commands that change or select something in the project, as the script and the extension list them.</summary>
    /// <remarks>Not recordabort, which must reach the record job whatever project is in front.</remarks>
    static readonly HashSet<string> ProjectCommands = ["set", "insert", "newtrack", "clip", "pads", "transpose", "action", "recorder", "arrange", "select",
                                                       "filetrack", "record", "probe"];

    BitwigLink(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _incoming = new BufferedStream(_stream, 1 << 16);
        _ = Task.Run(ReadAllAsync);
    }

    /// <summary>Connects to the Bitwig script and waits for its greeting.</summary>
    public static async Task<BitwigLink> ConnectAsync(int? port = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var at = port ?? Port;
        var limit = timeout ?? TimeSpan.FromSeconds(3);
        TcpClient? tcp = null;
        Exception? failure = null;
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            TcpClient? attempt = null;
            try
            {
                attempt = new TcpClient(address.AddressFamily) { NoDelay = true };
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(limit);
                await attempt.ConnectAsync(address, at, cts.Token);
                tcp = attempt;
                break;
            }
            catch (Exception e) when (e is SocketException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                attempt?.Dispose();
                failure = e;
            }
        }
        ct.ThrowIfCancellationRequested();
        if (tcp is null) throw new DawLinkException(NotRunning, failure);

        var link = new BitwigLink(tcp);
        try
        {
            // The script greets every connection, which also shows it's the Music Organiser script listening.
            link.Greeted(await link._hello.Task.WaitAsync(limit, ct));
            return link;
        }
        catch (TimeoutException)
        {
            link.Dispose();
            throw new DawLinkException($"Something answered on port {at}, but not the Music Organiser script in Bitwig.");
        }
        catch
        {
            link.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _open = false;
        _closing.Cancel();
        _tcp.Dispose();
    }

    public async Task<BitwigHello> HelloAsync(CancellationToken ct = default)
    {
        Greeted(await RequestAsync(new() { ["cmd"] = "hello" }, ct));
        return Hello;
    }

    void Greeted(JsonElement hello)
    {
        Hello = Parse<BitwigHello>(hello);
        HelloJson = hello;
    }

    public async Task<BitwigProject> ProjectAsync(CancellationToken ct = default) =>
        Parse<BitwigProject>(await RequestAsync(new() { ["cmd"] = "project" }, ct));

    /// <summary>The device selected in Bitwig with all its parameters (Device is null if none is selected).</summary>
    public async Task<BitwigSelection> SelectedDeviceAsync(CancellationToken ct = default) =>
        Parse<BitwigSelection>(await RequestAsync(new() { ["cmd"] = "device" }, ct));

    /// <summary>What an older script or extension says to <see cref="ClipNotesAsync"/>.</summary>
    public const string NeedsClipNotes = "Getting chords from Bitwig needs the Music Organiser extension 3.15 in Bitwig. " +
                                         "Install it again from Settings > DAW links, then restart Bitwig.";

    /// <summary>
    /// The clips selected in Bitwig's arranger and clip launcher with their notes (the Java extension 3.15), the arranger's
    /// first; none selected fails saying so. For the Playground's "Get chords from Bitwig": Bitwig doesn't tell extensions
    /// its chords, so they're read from the notes.
    /// </summary>
    public async Task<IReadOnlyList<BitwigClipNotes>> ClipNotesAsync(CancellationToken ct = default)
    {
        try
        {
            return Parse<SelectedClips>(await RequestAsync(new() { ["cmd"] = "notes" }, ct)).Clips;
        }
        catch (DawLinkException e) when (e.Message.StartsWith("Unknown command", StringComparison.Ordinal))
        {
            throw new DawLinkException(NeedsClipNotes, e);
        }
    }

    sealed record SelectedClips(IReadOnlyList<BitwigClipNotes> Clips);

    /// <summary>Every action Bitwig offers (its menu commands and shortcuts), to run with <see cref="RunActionAsync"/>.</summary>
    public async Task<IReadOnlyList<BitwigAction>> ActionsAsync(CancellationToken ct = default) =>
        Parse<List<BitwigAction>>(await RequestAsync(new() { ["cmd"] = "actions" }, ct));

    /// <summary>Selects a track (its index in <see cref="BitwigProject.Tracks"/>) and, optionally, one of its devices.</summary>
    public Task SelectAsync(int track, int? device = null, CancellationToken ct = default) =>
        RequestAsync(new() { ["cmd"] = "select", ["track"] = track, ["device"] = device }, ct);

    // Changes: these need "Let the app make changes" turned on in Bitwig.

    /// <summary>Sets one of the selected device's parameters (by its Bitwig ID) to a value from 0 to 1.</summary>
    public Task SetParameterAsync(string param, double value, CancellationToken ct = default) =>
        RequestAsync(new() { ["cmd"] = "set", ["param"] = param, ["value"] = value }, ct);

    /// <summary>
    /// Adds a plugin at the end of the selected track's chain, by the ID the plugin scanner reads: a VST3
    /// class ID, a CLAP ID or a VST2 ID (four letters or a number). Bitwig ignores IDs it doesn't know.
    /// </summary>
    public Task InsertPluginAsync(string format, string id, CancellationToken ct = default) =>
        RequestAsync(new() { ["cmd"] = "insert", ["kind"] = format.ToLowerInvariant(), ["plugin"] = PluginId(format, id) }, ct);

    /// <summary>Adds a VST2 plugin by its ID as a number (the scanner's uniqueId), which is never ambiguous.</summary>
    public Task InsertVst2Async(int uniqueId, CancellationToken ct = default) =>
        RequestAsync(new() { ["cmd"] = "insert", ["kind"] = "vst2", ["plugin"] = uniqueId }, ct);

    /// <summary>Adds one of Bitwig's own devices at the end of the selected track's chain.</summary>
    public Task InsertBitwigDeviceAsync(Guid id, CancellationToken ct = default) =>
        RequestAsync(new() { ["cmd"] = "insert", ["kind"] = "bitwig", ["device"] = id.ToString() }, ct);

    /// <summary>Adds a preset or a sample at the end of the selected track's chain.</summary>
    public Task InsertFileAsync(string path, CancellationToken ct = default) =>
        RequestAsync(new() { ["cmd"] = "insert", ["kind"] = "file", ["path"] = path }, ct);

    /// <summary>
    /// Runs one of Bitwig's actions by its ID, e.g. to save the project, and says how it went: which project Bitwig had
    /// in front just before it ran, the route, and whether the route ran it (see <see cref="BitwigActionRun"/>).
    /// </summary>
    /// <param name="via">
    /// The route (the Java extension 3.7; null leaves it to the link, which runs it from Bitwig's flush from the extension
    /// 3.8, as Save worked there, and straight away before): "direct" runs it straight away, "flush" from Bitwig's next flush, "midi" when a SysEx message the extension sends itself comes back
    /// through the MIDI loopback, "buttonCode" when a press of one of its own buttons comes back that way (the extension
    /// runs the action), and "button" the same with Bitwig running the action bound to the button. Bitwig 6.1.3 ignored
    /// Save, copy and paste run straight away, while it carried them out for a controller's hardware button, and saved
    /// from its flush. The script, and older extensions, refuse or ignore any route but "direct".
    /// </param>
    public async Task<BitwigActionRun> RunActionAsync(string id, string? via = null, CancellationToken ct = default)
    {
        var request = new JsonObject { ["cmd"] = "action", ["action"] = id };
        if (via is not null) request["via"] = via;
        // A route other than "direct" waits for the action to come back, and the extension runs actions one change at a time.
        var answer = await RequestAsync(request, ct, ChangeTimeout);
        string? Text(string name) =>
            answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        var ran = !(answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("ran", out var r) && r.ValueKind == JsonValueKind.False);
        return new BitwigActionRun(Text("project"), Text("via") ?? "direct", ran);
    }

    /// <summary>
    /// Sends any command, with <paramref name="fields"/> added to it, and returns Bitwig's answer as it came. For commands
    /// and fields newer than this app's records, such as the Java extension's selftest, which the live tests look at.
    /// </summary>
    public Task<JsonElement> CommandAsync(string cmd, JsonObject? fields = null, CancellationToken ct = default)
    {
        var request = fields?.DeepClone().AsObject() ?? new JsonObject();
        request["cmd"] = cmd;
        return RequestAsync(request, ct);
    }

    // Sending things in (link version 2): these wait for Bitwig to make each change.

    /// <summary>
    /// Adds an instrument track after the project's last track (before the effect tracks), names it and selects
    /// it. Returns its index in <see cref="BitwigProject.Tracks"/>, which the methods below take.
    /// </summary>
    /// <param name="color">"#rrggbb", or null for Bitwig's choice.</param>
    public async Task<int> NewTrackAsync(string name, string? color = null, CancellationToken ct = default) =>
        (await NewTrackWithIdAsync(name, color, ct)).Index;

    /// <summary>The same, saying the new track's ID too, which the Java extension 3.9 says (null from an older link).</summary>
    public async Task<BitwigNewTrack> NewTrackWithIdAsync(string name, string? color = null, CancellationToken ct = default) =>
        Parse<BitwigNewTrack>(await ChangeAsync(new() { ["cmd"] = "newtrack", ["name"] = name, ["color"] = color }, ct));

    /// <summary>
    /// Adds a file to a track's chain, as if it were dropped there: a preset (.bwpreset, .vstpreset...) loads its
    /// device, and a sample loads into a new Sampler.
    /// </summary>
    public Task<BitwigInserted> InsertFileAsync(int track, string path, bool atStart = false, CancellationToken ct = default) =>
        InsertOnAsync(track, atStart, new() { ["kind"] = "file", ["path"] = path }, ct);

    /// <summary>Adds one of Bitwig's own devices to a track's chain, by the ID Bitwig saves it under.</summary>
    public Task<BitwigInserted> InsertBitwigDeviceAsync(int track, Guid id, bool atStart = false, CancellationToken ct = default) =>
        InsertOnAsync(track, atStart, new() { ["kind"] = "bitwig", ["device"] = id.ToString() }, ct);

    /// <summary>Adds a plugin to a track's chain by the ID the plugin scanner reads (see <see cref="InsertPluginAsync(string, string, CancellationToken)"/>).</summary>
    public Task<BitwigInserted> InsertPluginAsync(int track, string format, string id, bool atStart = false, CancellationToken ct = default) =>
        InsertOnAsync(track, atStart, new() { ["kind"] = format.ToLowerInvariant(), ["plugin"] = PluginId(format, id) }, ct);

    /// <summary>Adds a VST2 plugin to a track's chain by its ID as a number.</summary>
    public Task<BitwigInserted> InsertVst2Async(int track, int uniqueId, bool atStart = false, CancellationToken ct = default) =>
        InsertOnAsync(track, atStart, new() { ["kind"] = "vst2", ["plugin"] = uniqueId }, ct);

    /// <summary>Puts a MIDI file in a track's clip launcher slot (the first empty one unless given), and returns the slot.</summary>
    public async Task<int> AddClipAsync(int track, string path, int? slot = null, CancellationToken ct = default) =>
        Parse<AddedClip>(await ChangeAsync(new() { ["cmd"] = "clip", ["track"] = track, ["path"] = path, ["slot"] = slot }, ct)).Slot;

    /// <summary>
    /// The same on the track with the ID given (the Java extension 3.9), which stays the same when a track goes in above it
    /// meanwhile, as its place in the listing doesn't.
    /// </summary>
    public async Task<int> AddClipAsync(string trackId, string path, int? slot = null, CancellationToken ct = default) =>
        Parse<AddedClip>(await ChangeAsync(new() { ["cmd"] = "clip", ["trackId"] = trackId, ["path"] = path, ["slot"] = slot }, ct)).Slot;

    /// <summary>
    /// Puts samples on a Drum Machine's pads, leaving pads that already have something on them alone. The Drum
    /// Machine is <paramref name="device"/> on <paramref name="track"/> (which this selects), or else the device
    /// selected in Bitwig.
    /// </summary>
    public async Task<BitwigPadsFilled> FillPadsAsync(IReadOnlyList<BitwigPad> pads, int? track = null, int? device = null,
                                                      CancellationToken ct = default)
    {
        var list = new JsonArray();
        foreach (var p in pads) list.Add(new JsonObject { ["note"] = p.Note, ["path"] = p.Path, ["name"] = p.Name.Length > 0 ? p.Name : null });
        return Parse<BitwigPadsFilled>(await ChangeAsync(
            new() { ["cmd"] = "pads", ["track"] = track, ["device"] = device, ["pads"] = list }, ct));
    }

    /// <summary>
    /// Sets the pitch of a Sampler (its Transpose, which can be a fraction of a semitone) or a Note Transpose (whole
    /// semitones), one of the first two devices on a track. Returns what Bitwig set, which a device's range may limit.
    /// </summary>
    public async Task<BitwigPitch> TransposeAsync(int track, int device, double semitones, CancellationToken ct = default) =>
        Parse<BitwigPitch>(await ChangeAsync(
            new() { ["cmd"] = "transpose", ["track"] = track, ["device"] = device, ["semitones"] = semitones }, ct));

    /// <summary>
    /// An experiment, for the live tests only (the Java extension, 3.6 and later): puts a copy of the clip in a launcher
    /// slot into Bitwig's arranger, on the same track, <paramref name="at"/> beats (quarter notes) from the start, or where
    /// the play start is when not given. Bitwig's API has no way to put a clip in the arranger, so the extension does what
    /// a person would: show the Arrange layout and its clip launcher, select the clip, give it a fresh colour (so it can be
    /// told from whatever else paste might put down), give the launcher the focus, copy it, move the play start there,
    /// bring the arranger timeline to the front and paste. Nobody knows yet whether Bitwig goes along with that from an
    /// extension, so the answer says what Bitwig showed either way (<see cref="BitwigArranged.Arranged"/> says whether it
    /// worked). Copy replaces whatever was on Bitwig's clipboard, the launcher clip keeps its new colour, nothing is undone
    /// when it doesn't work, and the play start stays where it was moved, which is why only a test project gets this.
    /// </summary>
    /// <param name="via">
    /// The route for the copy, the paste and the focus actions (the extension 3.7; null leaves it to the extension, which
    /// runs them from Bitwig's flush from 3.8, and straight away before): see <see cref="RunActionAsync"/>.
    /// </param>
    /// <param name="checks">
    /// Whether to check, when arrange's paste ran but put nothing in the arranger, whether a paste into an empty launcher
    /// slot and Bitwig's Duplicate work by the same route (the extension 3.8; <see cref="BitwigArranged.Checks"/>). A clip
    /// either makes in the launcher is deleted again; a track Duplicate made, or anything that went elsewhere, is left.
    /// </param>
    public async Task<BitwigArranged> ArrangeAsync(int track, int slot, double? at = null, string? via = null, bool checks = false,
                                                   CancellationToken ct = default)
    {
        var request = new JsonObject { ["cmd"] = "arrange", ["track"] = track, ["slot"] = slot, ["at"] = at };
        if (via is not null) request["via"] = via;
        if (checks) request["checks"] = true;
        // The checks wait for Bitwig as many times again.
        return Parse<BitwigArranged>(await ChangeAsync(request, ct, checks ? ChangeTimeout * 2 : null));
    }

    /// <summary>
    /// An experiment, for the live tests only (the Java extension 3.8): inserts a file after a track, as a new track, the way
    /// Bitwig's own API inserts one there, with the play start moved to <paramref name="at"/> beats first when given, and
    /// says where Bitwig put it (<see cref="BitwigFileTrack"/>). Whether Bitwig puts a MIDI file's clip in the arranger
    /// that way, which nothing else in its API does, is what this is for. The play start stays where it was moved.
    /// </summary>
    public async Task<BitwigFileTrack> FileTrackAsync(int track, string path, double? at = null, CancellationToken ct = default) =>
        Parse<BitwigFileTrack>(await ChangeAsync(new() { ["cmd"] = "filetrack", ["track"] = track, ["path"] = path, ["at"] = at }, ct));

    /// <summary>How long the record job gets after <see cref="RecordAsync"/>'s time runs out, once it's asked to stop (recordabort), to stop, put everything back and answer.</summary>
    public TimeSpan RecordAbortGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// An experiment, for the live tests only (the Java extension 3.9): records a launcher clip into Bitwig's arranger, as a
    /// person does (Bitwig documents it): the extension turns arranger record on and launches the clip, stops its track on
    /// the clip's last bar, then turns record off. Before, it sets Bitwig up so only that clip records and nothing is heard
    /// (the master muted, every other track disarmed, no metronome, pre-roll, loop, punch or overdub, touch automation),
    /// reading each setting back, and refuses to record when one doesn't take. After, it plays the arranger back to hear
    /// what went in (<see cref="BitwigRecorded.Decided"/>), and puts every setting back as it was. It runs in real time, so
    /// it takes the clip's length and the replay's at the tempo used.
    ///
    /// When <paramref name="timeout"/> passes with no answer, or <paramref name="ct"/> stops it, the app asks the job to
    /// stop (recordabort), so Bitwig isn't left recording; the job then has <see cref="RecordAbortGrace"/> to answer.
    /// </summary>
    public async Task<BitwigRecorded> RecordAsync(BitwigRecordRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        var send = new JsonObject
        {
            ["cmd"] = "record", ["trackId"] = request.TrackId, ["slot"] = request.Slot, ["at"] = request.At, ["length"] = request.Length,
            ["record"] = request.Record, ["arm"] = request.Arm, ["deleteLauncherClip"] = request.DeleteLauncherClip,
        };
        if (request.Replay is { } replay) send["replay"] = replay;
        if (request.ReplayFrom is { } from) send["replayFrom"] = from;
        if (request.Tempo is { } tempo) send["tempo"] = tempo;
        if (request.Pass is { } pass) send["pass"] = pass;
        var answer = ChangeAsync(send, ct, timeout + RecordAbortGrace);
        await Task.WhenAny(answer, Task.Delay(timeout, ct));
        // Out of time, or stopped: asked to stop, the job puts Bitwig back and answers. Harmless when none is running.
        if (!answer.IsCompletedSuccessfully) await AbortRecordingAsync();
        return Parse<BitwigRecorded>(await answer);
    }

    /// <summary>
    /// Asks the record job to stop (recordabort; the Java extension 3.9), whatever project is in front. With its own project
    /// in front, it then turns record off, stops Bitwig and puts everything back; with another, it has already answered
    /// "abandoned" and left Bitwig as it was. Whether one was running; false when the link couldn't be asked.
    /// </summary>
    public async Task<bool> AbortRecordingAsync()
    {
        try
        {
            using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var answer = await RequestAsync(new() { ["cmd"] = "recordabort" }, quick.Token);
            return answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("running", out var running) && running.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is DawLinkException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// A probe, for the live tests only (the Java extension 3.9): runs Bitwig's panel commands (toggle_arranger, then the
    /// focus actions of the clip launcher and the arranger) twice each, <paramref name="gap"/> milliseconds apart, by a route
    /// (null for the extension's default: see <see cref="RunActionAsync"/>), and says whether each panel showed before and
    /// after each go. A panel that shows and hides with them means Bitwig carried them out for the link. It switches to the
    /// Arrange layout first, and shows or hides each panel as it was afterwards.
    /// </summary>
    public async Task<BitwigProbed> ProbeAsync(string? via = null, double? gap = null, CancellationToken ct = default)
    {
        var request = new JsonObject { ["cmd"] = "probe" };
        if (via is not null) request["via"] = via;
        if (gap is not null) request["gap"] = gap;
        // Six goes, each a gap apart, and the waits for the layout and for each panel put back.
        return Parse<BitwigProbed>(await ChangeAsync(request, ct, ChangeTimeout + TimeSpan.FromMilliseconds(6 * (gap ?? 1500))));
    }

    async Task<BitwigInserted> InsertOnAsync(int track, bool atStart, JsonObject request, CancellationToken ct)
    {
        request["cmd"] = "insert";
        request["track"] = track;
        if (atStart) request["at"] = "start";
        return Parse<BitwigInserted>(await ChangeAsync(request, ct));
    }

    Task<JsonElement> ChangeAsync(JsonObject request, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (Hello.Link < LinkVersion) throw new DawLinkException(OldScript);
        return RequestAsync(request, ct, timeout ?? ChangeTimeout);
    }

    static JsonNode PluginId(string format, string id) => format.ToUpperInvariant() switch
    {
        "VST3" or "CLAP" => id,
        "VST2" => Vst2Id(id) ?? throw new ArgumentException($"Not a VST2 plugin ID: {id}", nameof(id)),
        _ => throw new ArgumentException($"Bitwig can't insert {format} plugins by ID.", nameof(format)),
    };

    sealed record AddedClip(int Slot);

    /// <summary>
    /// A VST2 ID as a number, read the way the plugin scanner writes it: four printable characters are the
    /// ID's own letters ("MoB2"), and anything else is the number itself ("-1294"). A number such as 1234
    /// reads as the letters "1234", so where the scanner's uniqueId is to hand, use that instead.
    /// </summary>
    public static int? Vst2Id(string id)
    {
        if (id.Length == 4 && id.All(c => c is >= ' ' and <= '~')) return BinaryPrimitives.ReadInt32BigEndian(Encoding.ASCII.GetBytes(id));
        return int.TryParse(id, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    static T Parse<T>(JsonElement result)
    {
        try
        {
            return result.Deserialize<T>(Json) ?? throw new DawLinkException("Bitwig's answer was empty.");
        }
        catch (JsonException e)
        {
            throw new DawLinkException("Bitwig's answer wasn't what the app expected. Install the script again from Settings, " +
                                       "in case Bitwig has an older version.", e);
        }
    }

    async Task<JsonElement> RequestAsync(JsonObject request, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (request["cmd"] is JsonValue cmd && cmd.TryGetValue<string>(out var name) && ProjectCommands.Contains(name))
        {
            if (BeforeChange is { } before) await before(ct);
            if (OnlyIn is { } project) request["onlyIn"] = project;
        }
        var id = Interlocked.Increment(ref _lastId);
        request["id"] = id;
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = answer;
        try
        {
            if (!_open) throw new DawLinkException("The connection to Bitwig is closed.");
            await SendAsync(Encoding.UTF8.GetBytes(request.ToJsonString()), ct);
            return await answer.Task.WaitAsync(timeout ?? Timeout, ct);
        }
        catch (TimeoutException)
        {
            throw new DawLinkException("Bitwig didn't answer in time.");
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw new DawLinkException("Lost the connection to Bitwig.", e);
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }

    async Task SendAsync(byte[] json, CancellationToken ct)
    {
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        await _writing.WaitAsync(ct);
        try { await _stream.WriteAsync(frame, ct); }
        finally { _writing.Release(); }
    }

    async Task ReadAllAsync()
    {
        Exception? failure = null;
        try
        {
            while (await ReadMessageAsync(_closing.Token) is { } message)
                Dispatch(message);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException or SocketException)
        {
            failure = e;
        }
        _open = false;
        var closed = new DawLinkException("Bitwig closed the connection.", failure);
        _hello.TrySetException(closed);
        foreach (var id in _waiting.Keys)
            if (_waiting.TryRemove(id, out var waiting)) waiting.TrySetException(closed);
    }

    /// <summary>
    /// One message from Bitwig. Bitwig puts a 4-byte big-endian length in front of each; the script also
    /// ends each with a newline, so a message that arrives without the length still reads.
    /// </summary>
    async Task<byte[]?> ReadMessageAsync(CancellationToken ct)
    {
        var first = new byte[1];
        if (await _incoming.ReadAsync(first, ct) == 0) return null;
        if (first[0] == (byte)'{')
        {
            var line = new MemoryStream();
            line.WriteByte(first[0]);
            while (true)
            {
                // Buffered, so most of these return straight away.
                if (await _incoming.ReadAsync(first, ct) == 0) return null;
                if (first[0] == '\n') return line.ToArray();
                line.WriteByte(first[0]);
                if (line.Length > MaxMessage) throw new InvalidDataException("Bitwig sent a message that never ends.");
            }
        }
        var header = new byte[4];
        header[0] = first[0];
        await _incoming.ReadExactlyAsync(header.AsMemory(1), ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 0 or > MaxMessage) throw new InvalidDataException($"Bitwig sent a message of {length} bytes.");
        var body = new byte[length];
        await _incoming.ReadExactlyAsync(body, ct);
        return body;
    }

    void Dispatch(byte[] message)
    {
        // Something we can't use: the request it answers will time out.
        try { DispatchJson(message); }
        catch (Exception e) when (e is JsonException or DawLinkException or InvalidOperationException) { }
    }

    void DispatchJson(byte[] message)
    {
        using (var doc = JsonDocument.Parse(message))
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.String)
            {
                if (ev.GetString() == "hello" && root.TryGetProperty("result", out var hello))
                    _hello.TrySetResult(hello.Clone());
                return;
            }
            if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id)) return;
            if (!_waiting.TryRemove(id, out var waiting)) return;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                waiting.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            else
                waiting.TrySetException(new DawLinkException(
                    root.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } text ? text : "Bitwig said no."));
        }
    }
}
