using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Formats;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Plugins;
using MusicOrganiser.Core.Sounds;
using BitwigProject = MusicOrganiser.Core.DawLink.BitwigProject;

namespace MusicOrganiser.Core.LiveTests;

/// <summary>
/// What a run of the Bitwig live tests uses. Everything that reaches outside the app (Bitwig, plugin folders, the app's
/// data folder, Bitwig's own folders) can be given, so the app's own tests can point it all at stand-ins.
/// </summary>
public sealed record BitwigLiveOptions
{
    /// <summary>How the run reaches Bitwig: its usual port unless given (Settings passes its watch's, tests a stand-in's).</summary>
    public Func<CancellationToken, Task<BitwigLink>> Connect { get; init; } = ct => BitwigLink.ConnectAsync(ct: ct);

    /// <summary>Where the reports go: reports\live in the app's data folder unless given.</summary>
    public string ReportFolder { get; init; } = LiveReports.DefaultFolder;

    /// <summary>
    /// Where the run writes the samples and the MIDI file it sends into Bitwig: "files" next to the reports unless given.
    /// They keep their names and content from run to run, since the test project goes on playing them.
    /// </summary>
    public string? FilesFolder { get; init; }

    /// <summary>MO Probe's report folder, where running probes write their status (<see cref="MoProbe.ReportFolder"/> unless given).</summary>
    public string ProbeFolder { get; init; } = MoProbe.ReportFolder;

    /// <summary>The plugin folders to look for MO Probe in (<see cref="MoProbe.InstallFolders"/> unless given).</summary>
    public Func<IEnumerable<string>> PluginFolders { get; init; } = MoProbe.InstallFolders;

    /// <summary>MO Probe Instrument as the plugin scanner found it, if it has (the library's ScannedPluginsNamed); its IDs come first.</summary>
    public IReadOnlyList<ScannedPlugin> ScannedProbes { get; init; } = [];

    /// <summary>Bitwig's user folders, whose Projects folder is searched for the saved test project.</summary>
    public Func<IReadOnlyList<string>> BitwigUserFolders { get; init; } = () => DawScripts.BitwigUserFolders();

    /// <summary>
    /// Brings Bitwig's window to the front before the run saves (Bitwig may ignore Save otherwise), returning whether it's
    /// in front, or null when that can't be told. Does nothing unless given, so tests never touch a real window.
    /// </summary>
    public Func<bool?> BringBitwigToFront { get; init; } = () => null;

    /// <summary>The Bitwig projects in the library: searched for the saved test project, and for the Drum Machine's ID.</summary>
    public Func<IEnumerable<string>> LibraryProjects { get; init; } = () => [];

    /// <summary>
    /// Bitwig's Drum Machine ID, when it's known (Sound Lab keeps the one it found); null to look in
    /// <see cref="LibraryProjects"/>, then Bitwig's presets and sound packages.
    /// </summary>
    public Func<CancellationToken, Guid?>? DrumMachine { get; init; }

    /// <summary>How long a new MO Probe has to show it's running once Bitwig has added it.</summary>
    public TimeSpan ProbeStart { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long each turn of the knob check has to arrive, and to land (<see cref="ProbeKnobCheck"/>'s own unless given).</summary>
    public TimeSpan? ProbeWait { get; init; }
    public TimeSpan? ProbeSettle { get; init; }

    /// <summary>How long Bitwig has to clear its unsaved changes after Save.</summary>
    public TimeSpan SaveWait { get; init; } = TimeSpan.FromSeconds(15);

    public string AppVersion { get; init; } = LiveReports.AppVersion;
}

/// <summary>
/// The Bitwig live tests: run against the real Bitwig through the app's own code (its link, BitwigSend, BitwigCheck, the
/// reader, MO Probe's live link), to see quickly what works and what Bitwig really does. The reports go in the app's data
/// folder, to be read on the PC (<see cref="LiveReports"/>).
/// <list type="bullet">
/// <item><b>Link</b> only reads, and always runs.</item>
/// <item><b>Changes</b>, <b>MO Probe</b>, <b>Send</b> and <b>Saved file</b> change the project open in Bitwig, so they only
/// run while Bitwig lets the app make changes AND its project's name starts with “MO Live Test”: a run never touches
/// anything else. That's checked again before each test, in case another project was opened meanwhile.</item>
/// </list>
/// Each run adds a handful of tracks named with the time it started (“Devices 143012”), then saves. With the Java
/// extension, it also copies the MIDI clip into the arranger (<see cref="ClipToArrangerAsync"/>): that replaces whatever
/// was copied in Bitwig (one clipboard for every project open), and leaves the test project in its Arrange layout, with its
/// clip launcher showing, the play start moved, the MIDI clip in a new colour and a copy of it pasted on the run's Sample
/// track. With the extension 3.8, Clip to arranger's checks may leave a copy of the Sample track behind too, and the run
/// inserts its MIDI file as a track of its own (“Live Clip”, <see cref="FileAsTrackAsync"/>).
/// </summary>
public static class BitwigLiveTests
{
    /// <summary>
    /// Where Bitwig may save a project: Projects in each of its user folders, and each Projects folder the library's Bitwig
    /// projects are in. Bitwig's projects can be kept apart from the rest of its user folder (Settings > Locations): seen,
    /// with its extensions in Documents\Bitwig Studio and its projects on another drive.
    /// </summary>
    public static IEnumerable<string> ProjectFolders(IEnumerable<string> userFolders, IEnumerable<string> library) =>
        userFolders.Select(f => Path.Combine(f, "Projects"))
            // Bitwig keeps each project in a folder of its own name: Projects\<name>\<name>.bwproject.
            .Concat(library.Select(p => Path.GetDirectoryName(Path.GetDirectoryName(p))).OfType<string>()
                           .Where(d => Path.GetFileName(d).Equals("Projects", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists);

    public const string Daw = "Bitwig Studio";
    /// <summary>The DAW in the report files' names.</summary>
    public const string Key = "bitwig";
    /// <summary>The start of the name of the only projects a run changes.</summary>
    public const string TestProject = "MO Live Test";

    public const string ProbeName = "MO Probe Instrument";
    /// <summary>MO Probe Instrument's CLAP ID (native/mo-probe/src/probe.cpp).</summary>
    public const string ProbeClapId = "org.musicorganiser.probe.instrument";
    /// <summary>Its VST3 class ID, fixed in probe.cpp, as the plugin scanner writes it.</summary>
    public const string ProbeVst3Id = "B0DDF78C9D004BA9B2359D3E7B74D7F1";

    /// <summary>What to do so a run can make changes.</summary>
    public const string HowToAllowChanges =
        "To run the tests that change things: in Bitwig, make a new project and save it as “MO Live Test”, then turn on “Let the " +
        "app make changes” (Settings > Controllers > Music Organiser), and run the live tests again.";

    /// <summary>What to do when the link in Bitwig is older than the app.</summary>
    public const string Reinstall =
        "Install it again from Settings > DAW links > Bitwig Studio, restart Bitwig, then run the live tests again.";

    public const string NeedsJava =
        "This needs the Music Organiser extension for Bitwig (Java), which tells the app more than the JavaScript script Bitwig " +
        "has now. Building the app makes it when a JDK is installed; then Install in Settings > DAW links > Bitwig Studio puts it " +
        "in Bitwig in place of the script.";

    /// <summary>Whether a project is one made for the live tests, which a run may change.</summary>
    public static bool IsTestProject(string name) => name.Trim().StartsWith(TestProject, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs every test, reporting each as it goes, and writes the reports. Stopping it (<paramref name="ct"/>) ends it after
    /// the test running then, which gets a moment to tidy up; the reports are still written.
    /// </summary>
    public static Task<LiveRun> RunAsync(BitwigLiveOptions? options = null, IProgress<LiveProgress>? progress = null,
                                         CancellationToken ct = default) =>
        new Suite(options ?? new(), progress, ct).RunAsync();

    /// <summary>
    /// The routes the run tries Save by, in turn, until one works (see <see cref="BitwigLink.RunActionAsync"/>): straight
    /// away, from Bitwig's flush, then through the MIDI loopback (the extension's own MIDI message, a press of its own
    /// button that the extension runs Save for, and one Bitwig runs Save for itself). Bitwig 6.1.3 ignored Save run straight
    /// away, from a request or a task the extension scheduled, while it carried it out for a controller's hardware button,
    /// and saved from its flush. Clip to arranger tries its copy and paste by the same routes, in the same order. With the
    /// extension 3.8, the flush comes first, asked for by naming no route (<see cref="RouteOrder"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> Routes = ["direct", "flush", "midi", "buttonCode", "button"];

    /// <summary>The first extension that takes a route other than "direct", and has the MIDI loopback.</summary>
    static readonly Version RoutesFrom = new(3, 7);

    /// <summary>
    /// The first extension that runs Bitwig's commands from its flush when the app names no route (as Save worked there),
    /// checks what an arrange that didn't work can still do (<see cref="BitwigArrangeChecks"/>), and inserts a file as a
    /// track (<see cref="BitwigLink.FileTrackAsync"/>).
    /// </summary>
    static readonly Version FlushFrom = new(3, 8);

    /// <summary>Whether the link in Bitwig is the Java extension <paramref name="from"/> or later.</summary>
    static bool IsFrom(BitwigHello h, Version from) => IsNewer(h) && Version.TryParse(h.Extension, out var v) && v >= from;

    /// <summary>Whether the link in Bitwig takes routes: the Java extension 3.7 or later.</summary>
    static bool HasRoutes(BitwigHello h) => IsFrom(h, RoutesFrom);

    /// <summary>
    /// The routes a run tries, in turn, with the link in Bitwig: <see cref="Routes"/>, with the flush first from the
    /// extension 3.8, which takes it when the app names no route, so the first go is the way the app's own commands run.
    /// Only "direct" with a link that takes no routes.
    /// </summary>
    public static IReadOnlyList<string> RouteOrder(BitwigHello h) =>
        !HasRoutes(h) ? ["direct"] : IsFrom(h, FlushFrom) ? ["flush", .. Routes.Where(r => r != "flush")] : Routes;

    /// <summary>
    /// The route to name in the request for a go by <paramref name="via"/>: none for the route the link takes by itself (the
    /// flush from the extension 3.8), and none at all for a link that takes no routes, which would refuse (the script) or not
    /// know it.
    /// </summary>
    static string? Named(BitwigHello h, string via) =>
        !HasRoutes(h) || (via == "flush" && IsFrom(h, FlushFrom)) ? null : via;

    /// <summary>The route the link in Bitwig takes when the app names none: the flush from the extension 3.8, else straight away.</summary>
    static string DefaultRoute(BitwigHello h) => IsFrom(h, FlushFrom) ? "flush" : "direct";

    /// <summary>The routes through the MIDI loopback, which only run once it's set up (<see cref="LoopbackSetup"/>).</summary>
    static readonly HashSet<string> LoopbackRoutes = ["midi", "buttonCode", "button"];

    /// <summary>How to set up the MIDI loopback the routes through it need.</summary>
    public const string LoopbackSetup =
        $"To set up the loopback: in loopMIDI, add a port called “{BitwigLink.LoopbackPort}” (the FL Studio link has its own port, “Music " +
        "Organiser”, which Bitwig must never use). Then in Bitwig, Settings > Controllers > Music Organiser, set both MIDI in and MIDI out " +
        $"to “{BitwigLink.LoopbackPort}” (the loopMIDI port), never “Music Organiser” (FL Studio's), and run the live tests again.";

    /// <summary>
    /// What the 3.7 runs found about the loopback's ports: without them Bitwig 6.1.3 didn't save for the extension by any
    /// route, though the link answered; with them it saved from its flush (but still not straight away).
    /// </summary>
    const string PortsNeeded =
        "Bitwig 6.1.3 saved for the extension only once its MIDI in and out were set (then from its flush), so every route needs them. ";

    /// <summary>What a run says when only Save straight away was tried, as the link in Bitwig takes no other route.</summary>
    const string OnlyDirect =
        " Only Save straight away was tried: the other routes (from Bitwig's flush, and through a MIDI loopback) need the Music Organiser " +
        "extension 3.7 or later.";

    /// <summary>A route in plain words, with its name: "straight away (direct)".</summary>
    static string RouteName(string via) => via switch
    {
        "direct" => "straight away (direct)",
        "flush" => "from Bitwig's flush (flush)",
        "midi" => "on the link's own MIDI message (midi)",
        "buttonCode" => "on a press of the link's own button, run by the link (buttonCode)",
        "button" => "by the link's own hardware button, run by Bitwig (button)",
        _ => $"via {via}",
    };

    /// <summary>How the link ran Save by a route, in plain words: "when the link pressed its own hardware button for Save".</summary>
    static string SaveHow(string via) => via switch
    {
        "direct" => "when the link ran Save straight away",
        "flush" => "when the link ran Save from Bitwig's flush",
        "midi" => "when the link ran Save as its own MIDI message came back through the loopback",
        "buttonCode" => "when the link ran Save for a press of its own hardware button",
        "button" => "when the link pressed its own hardware button for Save",
        _ => $"when the link ran Save via {via}",
    };

    /// <summary>"a", "a and b", "a, b and c".</summary>
    static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Join("", items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    /// <summary>A sentence's first letter as a capital.</summary>
    static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>One go at Save by a route (see <see cref="Routes"/>).</summary>
    /// <param name="Via">The route.</param>
    /// <param name="Project">The project Bitwig said it had in front as Save ran, or null when the link didn't say.</param>
    /// <param name="Ran">
    /// Whether the link ran Save by that route: one through Bitwig's flush or the loopback may not come back, and then the run
    /// tries the next straight away.
    /// </param>
    /// <param name="Cleared">Whether Bitwig's unsaved changes cleared within the wait after it (false when it didn't run).</param>
    /// <param name="Written">Whether the project's file had been written since before the run's first Save, after the wait.</param>
    /// <param name="Took">How long the changes took to clear, else how long the run waited (for the route to run, and then for them).</param>
    /// <param name="Error">What the link said when it refused the route (the MIDI ports weren't set up, say), or null.</param>
    public sealed record SaveAttempt(string Via, string? Project, bool Ran, bool Cleared, bool Written, TimeSpan Took, string? Error = null);

    /// <summary>
    /// The live test Changes › Save: tries Save by one route after another (<see cref="Routes"/>; only "direct" with a link older
    /// than the extension 3.7), stopping at the first that clears Bitwig's unsaved changes or writes the project's file, and
    /// says how that went (<see cref="SaveVerdict"/>). A route that didn't run is noted and the next tried at once, without
    /// waiting for the changes to clear. Each Save is a change like any other, so it goes through the same checks
    /// (<see cref="BitwigLink.BeforeChange"/>) and carries the test project's name (<see cref="BitwigLink.OnlyIn"/>).
    /// </summary>
    /// <param name="wait">How long Bitwig has to clear its unsaved changes after each Save that ran.</param>
    /// <param name="findFile">The project's file written since the time given, or null when the app can't find one.</param>
    /// <param name="bringBitwigToFront">Brings Bitwig's window to the front: whether it's in front, or null when that can't be told.</param>
    /// <param name="closed">Whether the run may no longer change the project: a route the link refuses then stops the test.</param>
    /// <param name="saving">
    /// Told when each Save goes out (UTC), just before: the last is the Save that worked, when one did, which is what the
    /// time Bitwig took to write the file is counted from.
    /// </param>
    public static async Task<LiveVerdict> SaveByRoutesAsync(BitwigLink link, string action, TimeSpan wait, Func<DateTime, string?> findFile,
                                                            Func<bool?> bringBitwigToFront, Func<Task<bool>>? closed = null,
                                                            Action<DateTime>? saving = null, CancellationToken ct = default)
    {
        var project = link.Hello.Project;
        var wasModified = link.Hello.Modified;
        var routes = HasRoutes(link.Hello);
        // The project's file, if the app can find it: whether Save wrote it says more than Bitwig's unsaved changes.
        var file = findFile(DateTime.MinValue);
        DateTime? Written() => file is not null && File.Exists(file) ? File.GetLastWriteTimeUtc(file) : null;
        var before = Written();
        var front = bringBitwigToFront();
        var savedAt = DateTime.UtcNow;
        var attempts = new List<SaveAttempt>();
        foreach (var via in RouteOrder(link.Hello))
        {
            var took = Stopwatch.StartNew();
            saving?.Invoke(DateTime.UtcNow);
            BitwigActionRun run;
            try
            {
                run = await link.RunActionAsync(action, Named(link.Hello, via), ct);
            }
            catch (DawLinkException e) when (via != "direct")
            {
                // Refused because the run may no longer change the project: the test stops there, saying why.
                if (closed is not null && await closed()) throw;
                attempts.Add(new SaveAttempt(via, null, false, false, false, took.Elapsed, e.Message));
                continue;
            }
            if (!run.Ran)
            {
                attempts.Add(new SaveAttempt(via, run.Project, false, false, false, took.Elapsed));
                continue;
            }
            var modified = await StillModifiedAsync(link, took, wait, ct);
            var tookNow = took.Elapsed;
            // A project saved for the first time has no file until Save writes one.
            file ??= findFile(savedAt.AddSeconds(-2));
            var written = Wrote(file, before, Written());
            attempts.Add(new SaveAttempt(via, run.Project, true, !modified, written, tookNow));
            if (!modified || written) break;
        }
        return SaveVerdict(project, action, wasModified, attempts, wait, file, before, Written(), front, routes, DefaultRoute(link.Hello));
    }

    /// <summary>
    /// Saved file › Find the saved project once the app found the file Save wrote: Passed, saying how long after the Save
    /// that worked (<paramref name="saveRan"/>, the last the run sent) Bitwig wrote it. When it was written well before
    /// that Save ran (by an earlier route's Save, later than the run waited for it), it says how long after the run's first
    /// Save (<paramref name="firstSave"/>) instead. A file system that keeps times to the nearest 2 seconds (FAT) may put
    /// the time a little before the Save that wrote it, which counts as straight after it.
    /// </summary>
    public static LiveVerdict FoundSaved(string path, DateTime written, DateTime firstSave, DateTime saveRan)
    {
        var when = written >= saveRan - TimeSpan.FromSeconds(2) || saveRan <= firstSave
            ? $"written {Ms(Math.Max(0, (written - saveRan).TotalMilliseconds))} after Save ran"
            : $"written {Ms(Math.Max(0, (written - firstSave).TotalMilliseconds))} after the run's first Save (the Save that worked went out " +
              $"{Ms((saveRan - firstSave).TotalMilliseconds)} after the first)";
        return LiveVerdict.Pass($"{path}, {when}.", new JsonObject { ["file"] = path });
    }

    /// <summary>
    /// Asks Bitwig until its unsaved changes clear, or until <paramref name="wait"/> has gone by since Save ran (<paramref
    /// name="took"/>): whether they're still there.
    /// </summary>
    static async Task<bool> StillModifiedAsync(BitwigLink link, Stopwatch took, TimeSpan wait, CancellationToken ct)
    {
        bool modified;
        while ((modified = (await link.HelloAsync(ct)).Modified) && took.Elapsed <= wait) await Task.Delay(100, ct);
        return modified;
    }

    /// <summary>
    /// What Save did, tried by one route after another (<paramref name="attempts"/>, in order) until one worked: Passed when
    /// Bitwig's unsaved changes cleared, or when it wrote the project's file (if the app found it) even though they didn't,
    /// naming the route that did it and saying the routes before it didn't work; else Failed, with a line for each route, and
    /// how to set up the MIDI loopback when the routes through it didn't run. The file says more than the flag: on Bitwig
    /// 6.1.3 a run saw the flag stay on after Save, with no Save As window, and couldn't tell whether the project was saved.
    /// </summary>
    /// <param name="project">The test project, which the run saves.</param>
    /// <param name="attempts">Each go at Save, in the order the run tried the routes (at least one).</param>
    /// <param name="wait">How long the run waited for the unsaved changes to clear after each Save that ran.</param>
    /// <param name="file">The project's file as the app found it before Save (or after, for a first save), or null.</param>
    /// <param name="before">When <paramref name="file"/> was written before Save (null if it wasn't there), and after the last go.</param>
    /// <param name="front">Whether Bitwig's window was in front when Save ran, or null when that couldn't be told.</param>
    /// <param name="routes">Whether the link in Bitwig takes routes (the extension 3.7): when it doesn't, only "direct" was tried.</param>
    /// <param name="byDefault">
    /// The route the link takes when the app names none, which the app's own commands go by: a Save that worked by it says
    /// so. Null when that isn't known.
    /// </param>
    public static LiveVerdict SaveVerdict(string project, string action, bool wasModified, IReadOnlyList<SaveAttempt> attempts, TimeSpan wait,
                                          string? file, DateTime? before, DateTime? after, bool? front = null, bool routes = true,
                                          string? byDefault = null)
    {
        var worked = attempts.FirstOrDefault(a => a.Ran && (a.Cleared || a.Written));
        var last = worked ?? attempts[^1];
        var stillModified = worked is not { Cleared: true };
        var atSave = attempts[0].Project;
        var data = new JsonObject
        {
            ["action"] = action, ["modifiedBefore"] = wasModified, ["modifiedAfter"] = stillModified, ["tookMs"] = Math.Round(last.Took.TotalMilliseconds),
            ["file"] = file, ["writtenBefore"] = Utc(before), ["writtenAfter"] = Utc(after), ["front"] = front, ["projectAtSave"] = atSave,
            ["via"] = worked?.Via,
            ["attempts"] = new JsonArray(attempts.Select(a =>
            {
                var o = new JsonObject
                {
                    ["via"] = a.Via, ["project"] = a.Project, ["ran"] = a.Ran, ["cleared"] = a.Cleared, ["written"] = a.Written,
                    ["tookMs"] = Math.Round(a.Took.TotalMilliseconds),
                };
                if (a.Error is not null) o["error"] = a.Error;
                return (JsonNode)o;
            }).ToArray()),
        };
        var older = routes ? "" : OnlyDirect;
        var seconds = $"{wait.TotalSeconds:0} s";
        if (worked is not null)
        {
            var how = SaveHow(worked.Via);
            var earlier = Earlier(attempts.TakeWhile(a => a != worked).ToList()) + older + ActiveProject(project, attempts, onlyWhenOther: true) +
                          (routes && worked.Via == byDefault
                              ? " That's the way the link runs a command when the app names no route, so the app's own Save works the same way."
                              : "");
            var wrote = file is null ? "" : worked.Written ? $", and wrote {file}" : $", though {file} wasn't written again";
            if (worked.Cleared)
                return LiveVerdict.Pass(wasModified
                    ? $"Bitwig saved “{project}” {how}: its unsaved changes cleared {Ms(worked.Took.TotalMilliseconds)} after Save{wrote}." + earlier
                    : $"Bitwig ran Save {RouteName(worked.Via)}. “{project}” had no unsaved changes before, so this only shows they stayed " +
                      $"clear{wrote}." + earlier,
                    data);
            return LiveVerdict.Pass($"Bitwig wrote {file} {how}, but still showed unsaved changes {seconds} later: something changed “{project}” " +
                                    "again straight away, or Bitwig didn't clear them." + earlier, data);
        }
        var unwritten = file is null ? ", and the app didn't find the project's file to see whether it was written" : $", and didn't write {file}";
        var tried = routes
            ? $" The link tried Save {attempts.Count} ways, one after another, and none worked: {Join(attempts.Select(a => RouteName(a.Via)).ToList())}."
            : older;
        var loopback = attempts.Any(a => LoopbackRoutes.Contains(a.Via) && !a.Ran)
            ? " The routes through the MIDI loopback didn't run. " + PortsNeeded + LoopbackSetup
            : "";
        return LiveVerdict.Fail($"Bitwig still showed unsaved changes {seconds} after Save (“{action}”){unwritten}" +
                                (front switch
                                {
                                    true => ", though its window was in front",
                                    false => ". The app couldn't bring Bitwig's window to the front, which Save may need: click on Bitwig while the run saves",
                                    null => "",
                                }) + "." + tried + loopback + ActiveProject(project, attempts, onlyWhenOther: false) +
                                $" If Bitwig opened a Save As window, the project hasn't been saved yet: save it as “{TestProject}”, then run again.", data)
            with { Lines = attempts.Select(a => SaveLine(a, wait)).ToList() };

        static string? Utc(DateTime? t) => t?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>What the routes tried before the one that worked did, as a sentence (nothing when it was the first).</summary>
    static string Earlier(IReadOnlyList<SaveAttempt> earlier)
    {
        if (earlier.Count == 0) return "";
        var parts = new List<string>();
        var ran = earlier.Where(a => a.Ran).Select(a => RouteName(a.Via)).ToList();
        if (ran.Count > 0) parts.Add($"Save run {Join(ran)} left the unsaved changes");
        var notRun = earlier.Where(a => !a.Ran).Select(a => RouteName(a.Via)).ToList();
        if (notRun.Count > 0) parts.Add($"Save {Join(notRun)} didn't run");
        return $" The routes before it didn't work: {string.Join("; ", parts)}.";
    }

    /// <summary>A line for the report saying what Save by one route did.</summary>
    static string SaveLine(SaveAttempt a, TimeSpan wait) =>
        $"- {Capital(RouteName(a.Via))}: " + (a.Error is { } error ? $"the link refused it: {error}"
            : !a.Ran ? a.Via == "flush" ? "didn't run, as Bitwig didn't call the link's flush in time"
                                        : "didn't run, as nothing came back through the loopback within a second"
            : a.Cleared ? $"its unsaved changes cleared {Ms(a.Took.TotalMilliseconds)} after Save"
            : a.Written ? "Bitwig wrote the project's file, but still showed unsaved changes"
            : $"ran, but Bitwig still showed unsaved changes {wait.TotalSeconds:0} s later");

    /// <summary>Whether <paramref name="file"/> was written since <paramref name="before"/>, going by when it was written <paramref name="after"/>.</summary>
    static bool Wrote(string? file, DateTime? before, DateTime? after) => file is not null && after is { } a && a != before;

    /// <summary>
    /// Which project Bitwig said it had in front as Save ran, as a sentence: whether it was the test project, and another one
    /// if it said so at a later Save. With <paramref name="onlyWhenOther"/>, nothing when it was the test project throughout.
    /// </summary>
    static string ActiveProject(string project, IReadOnlyList<SaveAttempt> attempts, bool onlyWhenOther)
    {
        var atSave = attempts[0].Project;
        if (atSave is null)
            return onlyWhenOther ? "" : " The link in Bitwig didn't say which project was active when Save ran (an older extension doesn't).";
        var then = attempts.Skip(1).Select(a => a.Project).FirstOrDefault(p => p is not null && p != atSave) is { } again
            ? $" At a later Save it said “{again}”." : "";
        if (atSave != project)
            return $" Bitwig said “{atSave}” was the active project when Save ran, not the test project, “{project}”." + then;
        return onlyWhenOther ? then : $" Bitwig said the test project, “{project}”, was the active project when Save ran." + then;
    }

    static string Ms(double ms) => ms >= 1000 ? (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : ms.ToString(ms < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + " ms";

    static readonly JsonSerializerOptions AnswerJson = new(JsonSerializerDefaults.Web);

    /// <summary>The first extension with <see cref="BitwigLink.ArrangeAsync"/>.</summary>
    static readonly Version ArrangeFrom = new(3, 6);

    /// <summary>Whether Bitwig has the Java extension, which gives more than the script (link 3, runtime "java").</summary>
    static bool IsNewer(BitwigHello h) => h.Link >= 3 || h.Runtime == "java";

    static LiveVerdict Needs(string what) => LiveVerdict.Skip($"This needs {what}, which an earlier test didn't get (see above).");

    /// <summary>
    /// The live test Link › MIDI loopback (the Java extension 3.7 or later; Skipped with an older one): Passed when the
    /// extension's own MIDI has come back to it through the loopback (its hello's "loopback"), so it can run Bitwig's
    /// commands by the routes through it (see <see cref="Routes"/>); else Failed, saying how to set the loopback up.
    /// </summary>
    public static LiveVerdict LoopbackVerdict(BitwigHello h)
    {
        if (!HasRoutes(h) || h.Loopback is not { } loopback)
            return LiveVerdict.Skip($"This needs the Music Organiser extension {RoutesFrom} or later, and Bitwig runs " +
                                    $"{(h.Extension is { Length: > 0 } running ? running : "an older one")}. {Reinstall}");
        var data = new JsonObject { ["loopback"] = loopback, ["extension"] = h.Extension };
        if (loopback)
            return LiveVerdict.Pass("The extension's own MIDI came back to it through the loopback, so it can run Bitwig's commands on its own " +
                                    "MIDI messages and by pressing its own buttons.", data);
        return LiveVerdict.Fail("The extension's own MIDI hasn't come back to it through the loopback, so its MIDI in and out aren't set, " +
                                "and Bitwig may not carry out the commands the app runs by any route. Bitwig 6.1.3 ignored Save, copy and paste " +
                                "run straight away. " + PortsNeeded + LoopbackSetup, data);
    }

    static LiveVerdict NeedsArrange(string? running) => NeedsExtension(ArrangeFrom, running);

    /// <summary>Skipped, as the test needs the extension <paramref name="from"/> or later and Bitwig runs <paramref name="running"/>.</summary>
    static LiveVerdict NeedsExtension(Version from, string? running) =>
        LiveVerdict.Skip($"This needs the Music Organiser extension {from} or later, and Bitwig runs " +
                         $"{(running is { Length: > 0 } ? running : "an older one")}. {Reinstall}");

    /// <summary>
    /// The live test Changes › Clip to arranger, an experiment: copies the MIDI clip in launcher slot <paramref
    /// name="clipSlot"/> of the track called <paramref name="track"/> into Bitwig's arranger at bar 5 (worked out from the
    /// project's time signature), with Bitwig's window brought to the front first, and says what Bitwig did (<see
    /// cref="ArrangeVerdict"/>). Skipped without the Java extension 3.6 or later, or without the clip. When the extension
    /// stops at a step Bitwig didn't go along with, Failed with its reason, still asking the person to look.
    ///
    /// With the extension 3.7 or later, it tries the copy, the paste and the focus actions by one route after another (<see
    /// cref="Routes"/>), each go marking the launcher clip with a fresh colour, until one puts the clip there or its paste
    /// puts anything down (<see cref="Pasted"/>): another paste would land where that one did, and the clip it left
    /// selected there would hide whether a new one appeared, so the routes after it aren't tried. The verdict is that go's,
    /// naming its route (Passed when it put the clip there); else the last go's, with a line for each route. With the
    /// extension 3.8, the first go is by the flush (<see cref="RouteOrder"/>), and each go whose paste ran but put nothing
    /// in the arranger checks a paste into an empty launcher slot and Bitwig's Duplicate by the same route (<see
    /// cref="BitwigArrangeChecks"/>), whose lines say whether the copy took and whether editing works in the launcher.
    /// </summary>
    /// <param name="clipSlot">The launcher slot the MIDI clip test put the clip in, or null when it didn't.</param>
    /// <param name="bringBitwigToFront">Brings Bitwig's window to the front: whether it's in front, or null when that can't be told.</param>
    /// <param name="closed">Whether the run may no longer change the project: a route the link refuses then stops the test.</param>
    public static async Task<LiveVerdict> ClipToArrangerAsync(BitwigLink link, string track, int? clipSlot, Func<bool?> bringBitwigToFront,
                                                              CancellationToken ct = default, Func<Task<bool>>? closed = null)
    {
        var hello = link.Hello;
        if (!IsNewer(hello)) return LiveVerdict.Skip(NeedsJava);
        // Older extensions have no arrange, and let changes through all the same: Bitwig runs one until it's installed again.
        if (Version.TryParse(hello.Extension, out var running) && running < ArrangeFrom) return NeedsArrange(hello.Extension);
        var p = await link.ProjectAsync(ct);
        if (clipSlot is not { } slot || p.Tracks.LastOrDefault(x => x.Name == track) is not { } on) return Needs("the MIDI clip");
        const int bar = 5;
        // Bitwig counts the play start in beats (quarter notes) from the start.
        var at = (bar - 1) * BarBeats(p.TimeSignature);
        // Copy and paste go to the panel in front inside Bitwig, which may need Bitwig's own window in front too.
        var front = bringBitwigToFront();
        var routes = HasRoutes(hello);
        var checks = IsFrom(hello, FlushFrom);
        var attempts = new List<ArrangeAttempt>();
        foreach (var via in RouteOrder(hello))
        {
            try
            {
                var arranged = await link.ArrangeAsync(on.Index, slot, at, Named(hello, via), checks, ct);
                attempts.Add(new ArrangeAttempt(via, arranged, null));
                if (Decides(arranged)) break;
            }
            catch (DawLinkException e) when (e.Message.StartsWith("Unknown command", StringComparison.Ordinal))
            {
                return NeedsArrange(hello.Extension);
            }
            catch (DawLinkException e)
            {
                // A step Bitwig didn't go along with (the layout, the clip launcher, the selection, the play start, the timeline),
                // with what's needed to tell why. A change refused because another project came to the front is Failed here too,
                // and the suite's Change still turns that into Stopped part way.
                if (!routes)
                    return LiveVerdict.Fail(e.Message + Window(front) + LookAt(bar, track), new JsonObject { ["error"] = e.Message, ["front"] = front });
                if (closed is not null && await closed()) throw;
                attempts.Add(new ArrangeAttempt(via, null, e.Message));
            }
        }
        if (!routes) return ArrangeVerdict(attempts[0].Answer!, track, slot, bar, front);

        var tries = new JsonArray(attempts.Select(a =>
        {
            var o = new JsonObject { ["via"] = a.Via, ["error"] = a.Error };
            o["answer"] = a.Answer is null ? null : JsonSerializer.SerializeToNode(a.Answer, AnswerJson);
            return (JsonNode)o;
        }).ToArray());
        var lines = attempts.SelectMany(ArrangeLines).ToList();
        if (attempts.LastOrDefault(a => a.Answer is not null) is not { Answer: { } answered } seen)
        {
            var first = attempts[0].Error!;
            return LiveVerdict.Fail(first + Window(front) + LookAt(bar, track), new JsonObject { ["error"] = first, ["front"] = front, ["attempts"] = tries })
                with { Lines = lines };
        }
        var decided = attempts.FirstOrDefault(a => a.Answer is { } answer && Decides(answer));
        var verdict = ArrangeVerdict(decided?.Answer ?? answered, track, slot, bar, front);
        // The data is the deciding go's whole answer (its route among it), with every go's.
        var data = verdict.Data!.AsObject();
        data["attempts"] = tries;
        if (decided is not null)
        {
            var before = attempts.TakeWhile(a => a != decided).Select(a => RouteName(a.Via)).ToList();
            var ran = $" The link ran the copy, the paste and the focus actions {RouteName(decided.Via)}" +
                      (before.Count > 0 ? $", after {Join(before)} didn't put the clip there." : ".");
            if (decided.Answer is { Arranged: true, CopiedFromTimeline: false }) return verdict with { Detail = verdict.Detail + ran };
            // Its paste put something down, though not for sure the run's clip in the arranger: no other route was tried.
            var rest = RouteOrder(hello).Skip(attempts.Count).Select(RouteName).ToList();
            return verdict with
            {
                Detail = verdict.Detail + ran + (rest.Count == 0 ? "" :
                    $" Bitwig's paste put something down by that route, so the link didn't try {Join(rest)}: another paste would land " +
                    "where this one did, and the clip this one left selected there would hide whether a new one appeared."),
                Lines = [.. verdict.Lines, .. lines],
            };
        }
        var loopback = attempts.Any(a => LoopbackRoutes.Contains(a.Via) && (a.Error is not null || NotRun(a.Answer) is { Length: > 0 }))
            ? " The routes through the MIDI loopback didn't all run. " + LoopbackSetup
            : "";
        return verdict with
        {
            Detail = verdict.Detail + $" The link tried the copy, the paste and the focus actions {attempts.Count} ways, one after another, and " +
                     $"none put the clip there (this is what the last, {RouteName(seen.Via)}, found)." + loopback,
            Lines = [.. verdict.Lines, .. lines],
        };
    }

    /// <summary>One go at the arrange experiment by a route: what the extension answered, or what it said when it stopped.</summary>
    sealed record ArrangeAttempt(string Via, BitwigArranged? Answer, string? Error);

    /// <summary>
    /// Whether a go at the arrange experiment settles it, so the routes after it aren't tried: it put the clip in the
    /// arranger, or its paste put something down (<see cref="Pasted"/>).
    /// </summary>
    static bool Decides(BitwigArranged a) => a is { Arranged: true, CopiedFromTimeline: false } || Pasted(a);

    /// <summary>
    /// Whether the paste ran (as far as the extension says) and Bitwig put something down: a clip appeared on the track
    /// (whichever clip it is), the track's launcher clips changed (it pasted there), or the clip selected in the arranger
    /// is another one than just before the paste (it pasted on another track). Another paste would land in the same place,
    /// where the extension couldn't tell a new clip from the one this paste left selected.
    /// </summary>
    static bool Pasted(BitwigArranged a) =>
        !a.CopiedFromTimeline && (a.Ran is null || a.Ran.Paste == true) &&
        (a.Appeared || a.LauncherClipsAfter != a.LauncherClipsBefore || (a.After is { Exists: true } after && after != a.BeforePaste));

    /// <summary>The steps of an arrange the route didn't run, in plain words ("" when they all ran, or the extension doesn't say).</summary>
    static string NotRun(BitwigArranged? a)
    {
        if (a?.Ran is not { } ran) return "";
        var steps = new List<string>();
        if (ran.LauncherFocus == false) steps.Add("giving the clip launcher the focus");
        if (ran.Copy == false) steps.Add("the copy");
        if (ran.Focus == false) steps.Add("bringing the arranger timeline to the front");
        if (ran.Paste == false) steps.Add("the paste");
        return Join(steps);
    }

    /// <summary>
    /// The lines for the report saying what the arrange experiment did by one route: one for the arrange, then one for each
    /// of its checks (the extension 3.8), indented under it.
    /// </summary>
    static IEnumerable<string> ArrangeLines(ArrangeAttempt a)
    {
        yield return ArrangeLine(a);
        if (a.Answer?.Checks is not { } checks) yield break;
        if (checks.LauncherPaste is { } paste) yield return "  - Paste into the launcher: " + PasteCheck(paste, a.Answer.Mark);
        if (checks.Duplicate is { } duplicate) yield return "  - Duplicate in the launcher: " + DuplicateCheck(duplicate);
        if (checks.Error is { } error) yield return "  - The checks stopped: " + error;
    }

    /// <summary>
    /// What the check's paste into an empty launcher slot found, in plain words. Its colour only says whose clip it is when
    /// the link's mark on the launcher clip (<paramref name="mark"/>, arrange's) took, and is the very colour asked for.
    /// </summary>
    static string PasteCheck(BitwigLauncherPasteCheck c, BitwigClipMark? mark)
    {
        var slot = $"empty slot {c.Slot + 1}";
        if (!c.Selected) return $"Bitwig didn't select {slot}, so the check stopped there";
        if (c.Ran.Focus == false) return "giving the clip launcher the focus didn't run, so the check stopped there";
        if (c.Ran.Paste is not { } pasted) return "the check didn't get to the paste";
        if (!pasted) return $"the paste into {slot} didn't run";
        if (!c.Made) return $"a paste into {slot} put nothing there";
        var left = c.Removed ? "" : $" (the link couldn't delete it again, so it's still in slot {c.Slot + 1})";
        if (mark is not { Took: true })
            return $"a paste into {slot} put down a clip ({c.Colour ?? "unknown"}), but Bitwig didn't let the link colour the launcher clip, so " +
                   "it can't tell whether that's the launcher clip's copy or what Bitwig's clipboard held before" + left;
        if (c.SameAsMark && !mark.Exact)
            return $"a paste into {slot} put down a clip in the launcher clip's colour ({c.Colour}), but Bitwig rounded that colour to one of " +
                   "its own, which other clips can have too, so it can't tell for sure whether the copy took" + left;
        return c.SameAsMark
            ? $"a paste into {slot} put the launcher clip's copy there, in its marking colour ({c.Colour}), so the copy took and pasting " +
              "works in the launcher: it's putting it in the arranger that fails" + left
            : $"a paste into {slot} put down a clip in another colour ({c.Colour ?? "unknown"}) than the launcher clip's mark, so the copy " +
              "didn't take: Bitwig pasted what its clipboard held before" + left;
    }

    /// <summary>What the check's Duplicate on the launcher clip found, in plain words.</summary>
    static string DuplicateCheck(BitwigDuplicateCheck d)
    {
        if (!d.Selected) return "Bitwig didn't select the launcher clip again, so the check stopped there";
        if (d.Ran.Focus == false) return "giving the clip launcher the focus didn't run, so the check stopped there";
        if (d.Ran.Duplicate is not { } duplicated) return "the check didn't get to Duplicate";
        if (!duplicated) return "Duplicate didn't run";
        if (d.Launcher > 0)
            return "Duplicate made a copy of the clip in the launcher, so Bitwig's editing actions work in the launcher by this route" +
                   (d.Removed ? " (the link deleted the copy again)" : " (the link couldn't delete the copy again)");
        if (d.Tracks > 0)
            return "Duplicate copied the whole track instead, so the clip launcher didn't have the focus (the copy is left in the test project)";
        return "Duplicate changed nothing in the launcher or the track list";
    }

    /// <summary>A line for the report saying what the arrange experiment did by one route.</summary>
    static string ArrangeLine(ArrangeAttempt a) =>
        $"- {Capital(RouteName(a.Via))}: " + (a.Error is { } error ? $"the extension stopped: {error}"
            : NotRun(a.Answer) is { Length: > 0 } steps ? $"{steps} didn't run"
            : a.Answer is { Arranged: true, CopiedFromTimeline: false } ? "put the clip in the arranger"
            : a.Answer!.CopiedFromTimeline ? "Bitwig copied from the arranger timeline, so the extension didn't paste"
            : a.Answer.Appeared ? "a clip appeared on the track, but the extension couldn't be sure it was the run's"
            : a.Answer.LauncherClipsAfter > a.Answer.LauncherClipsBefore ? "Bitwig pasted into the clip launcher instead"
            : a.Answer.After is { Exists: true, Track: var other } && a.Answer.After != a.Answer.BeforePaste
                ? $"the clip selected in the arranger is on “{other}” now, not the clip's track"
            : "nothing appeared in the arranger");

    /// <summary>What a Failed arrange adds when Bitwig's window wasn't in front.</summary>
    static string Window(bool? front) =>
        front == false ? " The app couldn't bring Bitwig's window to the front, which copy and paste may need." : "";

    /// <summary>What a Failed arrange asks when the link can't tell what's there: only the person can see where the clip went, if anywhere.</summary>
    static string LookAt(int bar, string track) =>
        $" Look at Bitwig's arranger at bar {bar} on “{track}” and say what you see there (the clip, nothing, or the clip somewhere " +
        "else), so the link can be put right.";

    /// <summary>
    /// What the arrange experiment found (<see cref="BitwigLink.ArrangeAsync"/>):
    /// <list type="bullet">
    /// <item>Passed when the extension saw a clip in the launcher clip's own marking colour (the very one it asked for)
    /// appear in the arranger on the track, so it's the run's clip, saying that where it starts is only intended, as the
    /// link can't read it, and asking the person to look.</item>
    /// <item>Failed when a clip appeared on the track in another colour than the launcher clip got: Bitwig pasted whatever
    /// was on its clipboard before, so copying from the launcher didn't take.</item>
    /// <item>Noted when a clip appeared on the track but Bitwig didn't let the extension colour the launcher clip, or when
    /// the clip has the launcher clip's colour but Bitwig rounded that to one of its own, which other clips can have too: the
    /// extension can't tell which clip it is, so the person is asked to look.</item>
    /// <item>Else Failed, saying what Bitwig did instead and asking the person to look at Bitwig's arranger, since only they
    /// can see where the clip went, if anywhere.</item>
    /// </list>
    /// The data is the extension's whole answer, and whether Bitwig's window was in front.
    /// </summary>
    /// <param name="track">The track the clip is on, which is where it should appear in the arranger.</param>
    /// <param name="slot">The launcher slot it was copied from (0 is the first).</param>
    /// <param name="bar">The bar it was meant to start at (1 is the first).</param>
    /// <param name="front">Whether Bitwig's window was in front, or null when that couldn't be told.</param>
    public static LiveVerdict ArrangeVerdict(BitwigArranged a, string track, int slot, int bar, bool? front)
    {
        var data = JsonSerializer.SerializeToNode(a, AnswerJson)!.AsObject();
        data["front"] = front;
        var meant = $"bar {bar} (beat {Share(a.At)})";
        var launcher = Checks.Count(a.LauncherClipsAfter, "clip");
        var launcherFocus = a.LauncherFocus switch
        {
            0 => "Bitwig has no focus_or_toggle_clip_launcher action to give the clip launcher the focus",
            2 => "giving the clip launcher the focus took two goes: the first hid it",
            _ => "",
        };
        if (a.Arranged && !a.CopiedFromTimeline)
            return LiveVerdict.Pass($"Bitwig pasted a copy of the clip in launcher slot {slot + 1} into its arranger on “{track}”: a clip in the " +
                                    $"launcher clip's own marking colour ({a.PastedColor}) appeared in the arranger on “{track}”, so it's the run's " +
                                    $"MIDI clip, and the track kept its {launcher} in the launcher" +
                                    (launcherFocus.Length > 0 ? $" ({launcherFocus})" : "") + $". It was meant to go in at {meant}, where the " +
                                    $"link moved the play start, but the link can't read where an arranger clip starts: look at bar {bar} on " +
                                    $"“{track}” in Bitwig's arranger and say whether it's there.", data);
        if (a.Appeared && !a.CopiedFromTimeline && a.Mark is { Took: true } mark && !a.SameAsSource)
        {
            // Paste put down what Bitwig's clipboard held before: a clip copied in another song, or the last run's copy.
            var copying = a.LauncherFocus == 0
                ? "Bitwig has no focus_or_toggle_clip_launcher action, so the link copied from whichever panel had the focus"
                : $"the link ran focus_or_toggle_clip_launcher first ({(a.LauncherFocus == 2 ? "twice, as the first go hid the launcher" : "once")}), " +
                  "so either that didn't give the clip launcher the focus or Bitwig doesn't copy launcher clips for an extension";
            return LiveVerdict.Fail($"Bitwig didn't put the clip into its arranger at {meant} on “{track}”: a clip appeared on “{track}” after the " +
                                    $"paste, but it isn't the launcher clip (its colour is {a.PastedColor ?? "unknown"}, and the link coloured the " +
                                    $"launcher clip {mark.Got}). Bitwig pasted whatever was already on its clipboard, so copying from the launcher " +
                                    $"didn't take: {copying}. The test project now has that other clip on “{track}” at bar {bar}, which can be " +
                                    "deleted." + Window(front), data);
        }
        // Only the person can tell which clip appeared when the mark doesn't single the run's clip out.
        var which = $"so the link can't tell whether it's the run's MIDI clip or whatever was on Bitwig's clipboard before. Look at bar {bar} " +
                    $"on “{track}” in Bitwig's arranger and say which clip it is." + Window(front);
        if (a.Appeared && !a.CopiedFromTimeline && a.Mark is not { Took: true })
            return LiveVerdict.Note($"A clip appeared in Bitwig's arranger on “{track}” after the paste, but Bitwig didn't let the link colour the " +
                                    $"launcher clip in slot {slot + 1}, {which}", data);
        if (a.Appeared && !a.CopiedFromTimeline && a.SameAsSource && a.Mark is { Took: true, Exact: false } rounded)
            return LiveVerdict.Note($"A clip appeared in Bitwig's arranger on “{track}” after the paste, in the colour the link gave the launcher clip " +
                                    $"in slot {slot + 1}, but Bitwig rounded that colour to one of its own (the link asked for {rounded.Asked} and got " +
                                    $"{rounded.Got}), which other clips can have too, {which}", data);

        var instead = new List<string>();
        if (a.CopiedFromTimeline)
        {
            instead.Add("the arranger timeline had the focus when the link copied (bringing the timeline to the front hid it, which happens " +
                        $"only when it's in front already), so Bitwig copied from the timeline, not launcher slot {slot + 1}, and the link " +
                        "didn't paste");
            instead.Add(a.LauncherFocus == 0 ? launcherFocus : "the link ran focus_or_toggle_clip_launcher first, which didn't give the clip launcher the focus");
        }
        else
        {
            var on = a.After is { Exists: true } after && after.Track == track;
            var wasOn = a.BeforePaste is { Exists: true } before && before.Track == track;
            if (a.LauncherClipsAfter > a.LauncherClipsBefore)
                instead.Add($"it pasted into the clip launcher instead (“{track}” has {launcher} there now, {a.LauncherClipsBefore} before)");
            else if (a.LauncherClipsAfter < a.LauncherClipsBefore)
                instead.Add($"“{track}” has {launcher} in the launcher now, {a.LauncherClipsBefore} before");
            if (on && wasOn)
                instead.Add($"a clip on “{track}” was already selected in the arranger before the paste, so the link couldn't tell whether a new one appeared");
            else if (on)
                instead.Add($"the clip selected in the arranger did move to “{track}” as well");
            else if (a.After is { Exists: true } same && same == a.BeforePaste)
                instead.Add($"nothing new was selected in the arranger: the clip selected there is still the one on “{same.Track}”");
            else if (a.After is { Exists: true } other)
                instead.Add($"the clip selected in the arranger is on “{other.Track}”, not “{track}”");
            else
                instead.Add(instead.Count == 0 ? "nothing appeared: no clip is selected in the arranger, and the launcher has the same clips"
                                               : "no clip is selected in the arranger");
            if (a.Timeline is { After: false }) instead.Add("the arranger timeline is hidden now");
        }
        return LiveVerdict.Fail($"Bitwig didn't put the clip into its arranger at {meant} on “{track}”: {string.Join("; ", instead)}." +
                                Window(front) + LookAt(bar, track), data);
    }

    /// <summary>
    /// The live test Changes › MIDI file as a track, an experiment (the Java extension 3.8; Skipped with an older link):
    /// inserts the run's MIDI file after the track called <paramref name="track"/>, as a new track, with the play start at bar
    /// 5 (worked out from the project's time signature), the way Bitwig's API inserts a file between tracks, and says where
    /// Bitwig put its clip (<see cref="FileTrackVerdict"/>). Nothing else in Bitwig's API puts a clip in the arranger, so
    /// this is another way to try.
    /// </summary>
    public static async Task<LiveVerdict> FileAsTrackAsync(BitwigLink link, string track, string file, CancellationToken ct = default)
    {
        var hello = link.Hello;
        if (!IsNewer(hello)) return LiveVerdict.Skip(NeedsJava);
        if (!IsFrom(hello, FlushFrom)) return NeedsExtension(FlushFrom, hello.Extension);
        var p = await link.ProjectAsync(ct);
        if (p.Tracks.LastOrDefault(x => x.Name == track) is not { } after) return Needs("the sample's track");
        const int bar = 5;
        var placed = await link.FileTrackAsync(after.Index, file, (bar - 1) * BarBeats(p.TimeSignature), ct);
        return FileTrackVerdict(placed, Path.GetFileName(file), track, bar);
    }

    /// <summary>
    /// Where Bitwig put a MIDI file the link inserted after a track (<see cref="BitwigLink.FileTrackAsync"/>):
    /// <list type="bullet">
    /// <item>Passed when it made a new track with no clip in its launcher and the clip selected in the arranger moved to that
    /// track, so the clip went in the arranger, asking the person to look at where it starts, which the link can't read.</item>
    /// <item>Failed when Bitwig added no track, or put the clip in the new track's launcher.</item>
    /// <item>Else Noted, asking the person to look, since the link can't see a clip on the new track.</item>
    /// </list>
    /// The data is the extension's whole answer.
    /// </summary>
    /// <param name="file">The MIDI file's name.</param>
    /// <param name="track">The track it went after.</param>
    /// <param name="bar">Where the play start was moved to, so where the clip should start (1 is the first bar).</param>
    public static LiveVerdict FileTrackVerdict(BitwigFileTrack f, string file, string track, int bar)
    {
        var data = JsonSerializer.SerializeToNode(f, AnswerJson)!;
        var meant = $"bar {bar} (beat {Share(f.At)})";
        if (f.Added == 0)
        {
            var launcher = f.OnTrack is { After: { } now, Before: var was } && now > was
                ? $", though “{track}” has {Checks.Count(now, "clip")} in the launcher now, {was} before"
                : "";
            return LiveVerdict.Fail($"Bitwig didn't add a track for {file} after “{track}”: it still has {f.TracksBefore} tracks{launcher}.", data);
        }
        if (f.Index is not { } index || f.Name is not { } name)
            return LiveVerdict.Note($"Bitwig added {Checks.Count(f.Added, "track")} for {file}, but the link couldn't find the new one in its " +
                                    $"listing. Look after “{track}” in Bitwig and say what's there.", data);
        var made = $"Bitwig put {file} on a new track, “{name}” (track {index + 1})";
        if (f.LauncherClips > 0)
            return LiveVerdict.Fail($"{made}, but in its clip launcher ({Checks.Count(f.LauncherClips.Value, "clip")} there), not the arranger, so " +
                                    "inserting a file between tracks doesn't put it in the arranger.", data);
        // The 3.7 run left Bitwig's play start marker at bar 1 though the link read it back at bar 5, so where the clip starts is asked, not assumed.
        var look = $"look at “{name}” in Bitwig's arranger and say whether the clip is there and which bar it starts at (it was meant to start at " +
                   $"{meant}, where the link moved the play start), and where the play start marker is.";
        if (f.Arranger is { Exists: true } on && on.Track == name && on != f.ArrangerBefore)
            return LiveVerdict.Pass($"{made}, with no clip in its launcher, and the clip selected in the arranger is on that track now, so the " +
                                    $"clip went in the arranger. The link can't read where an arranger clip starts: {look}", data);
        var why = f.Arranger is { Exists: true } same && same.Track == name && same == f.ArrangerBefore
            ? $"a clip on a track called “{same.Track}” was selected in the arranger before too, and the link tells those clips apart only by " +
              "their track's name, so it can't tell whether a new one appeared"
            : "no clip on it is selected in the arranger, so the link can't tell whether the clip went there";
        return LiveVerdict.Note($"{made}, with no clip in its launcher, but {why}. {Capital(look)}", data);
    }

    /// <summary>
    /// How many beats (quarter notes, as Bitwig counts its play start) a bar has in a time signature such as "7/8": 3.5. Four
    /// when the time signature can't be read.
    /// </summary>
    public static double BarBeats(string timeSignature)
    {
        var parts = timeSignature.Split('/');
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var top) && top > 0 &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bottom) && bottom > 0
            ? top * 4.0 / bottom
            : 4;
    }

    /// <summary>
    /// Compares what the app read from the saved project with what Bitwig shows (<see cref="BitwigCheck"/>): Failed with
    /// what differs, else Passed.
    /// </summary>
    public static LiveVerdict CompareSaved(string path, ProjectInfo read, BitwigProject live)
    {
        var lines = BitwigCheck.Compare(path, read.Tempo, read.Tracks, read.Plugins.Select(p => p.Name).ToList(), live);
        var data = new JsonObject
        {
            ["file"] = path, ["tempo"] = read.Tempo, ["tracks"] = Strings(read.Tracks),
            ["devices"] = Strings(read.Plugins.Select(p => p.Name)),
            ["lines"] = new JsonArray(lines.Select(l => (JsonNode)new JsonObject { ["result"] = l.Result.ToString().ToLowerInvariant(), ["text"] = l.Text }).ToArray()),
        };
        var shown = lines.Select(l => $"- {l.Mark} {l.Text}").ToList();
        var different = lines.Where(l => l.Result == CheckResult.Different).Select(l => l.Text).ToList();
        if (different.Count > 0)
            return LiveVerdict.Fail($"The saved file and Bitwig differ: {string.Join(" ", different)}", data) with { Lines = shown };
        var same = lines.Where(l => l.Result == CheckResult.Same).Select(l => l.Text).ToList();
        return LiveVerdict.Pass("What the app read from the saved file matches what Bitwig shows. " + string.Join(" ", same), data) with { Lines = shown };
    }

    /// <summary>
    /// How Bitwig turns the knob check's results into what BitwigSend relies on: each knob moves both ways, and a stepped
    /// knob lands on the step its 0 to 1 value stands for. Failed with what doesn't, else Passed.
    /// </summary>
    public static LiveVerdict KnobVerdict(KnobCheck check)
    {
        var breaks = new List<string>();
        foreach (var knob in check.Lines.GroupBy(l => l.Knob))
        {
            foreach (var way in knob.GroupBy(l => l.Way))
            {
                var turns = way.ToList();
                var got = turns.Select(l => l.Got is { } g ? Math.Round(g, 4) : (double?)null).Distinct().ToList();
                if (turns.Count > 1 && got.Count == 1)
                    breaks.Add(way.Key == KnobWay.ToProbe
                        ? $"{turns[0].Name} doesn't move when Bitwig turns it: the probe got {Shown(turns[0], turns[0].Got)} every time."
                        : $"{turns[0].Name} doesn't move in Bitwig when the probe turns it: Bitwig showed {Shown(turns[0], turns[0].Got)} every time.");
                else if (way.Key == KnobWay.ToProbe && ProbeKnobs.Stepped.Contains(knob.Key))
                    breaks.AddRange(turns.Where(l => !l.Matched).Select(l => $"{l.Name} lands on the wrong step: {l.Text}"));
            }
        }
        var data = new JsonObject
        {
            ["probe"] = check.Probe, ["summary"] = check.Summary, ["matched"] = check.Matched, ["checks"] = check.Lines.Count,
            ["stopped"] = check.Stopped, ["notes"] = Strings(check.Notes),
        };
        var lines = breaks.Concat(check.Notes).Select(l => "- " + l).ToList();
        if (check.Stopped is { } why) return LiveVerdict.Fail($"The knob check stopped part way: {why}", data) with { Lines = lines };
        if (check.Lines.Count == 0) return LiveVerdict.Fail(check.Summary, data) with { Lines = lines };
        if (breaks.Count > 0)
            return LiveVerdict.Fail($"MO Probe's knobs don't all come through Bitwig as BitwigSend needs them: {Checks.Count(breaks.Count, "problem")}. " +
                                    check.Summary, data) with { Lines = lines };
        return LiveVerdict.Pass("Every knob moved both ways, and each stepped knob landed on its step. " + check.Summary, data) with { Lines = lines };
    }

    /// <summary>
    /// The knob check's turns as a table, one row a knob: what Bitwig's 0 to 1 values became at the probe, and what the
    /// probe's own values became in Bitwig. A turn that didn't land where an even spread over the knob's range puts it is
    /// marked ✗.
    /// </summary>
    public static LiveVerdict KnobMapping(KnobCheck check, string format)
    {
        static string Turn(KnobCheckLine l) =>
            (l.Way == KnobWay.ToProbe ? $"{Share(l.Sent)} → {Shown(l, l.Got)}" : $"{AtProbe(l.Knob, l.Sent)} → {Shown(l, l.Got)}") +
            (l.Matched ? "" : " ✗");
        var table = new List<string>
        {
            "| Knob | Bitwig → probe (0 to 1 sent → value the probe got) | Probe → Bitwig (value set → Bitwig's 0 to 1) |",
            "|---|---|---|",
        };
        foreach (var knob in check.Lines.GroupBy(l => l.Knob))
        {
            var toProbe = string.Join(", ", knob.Where(l => l.Way == KnobWay.ToProbe).Select(Turn));
            var toDaw = string.Join(", ", knob.Where(l => l.Way == KnobWay.ToDaw).Select(Turn));
            table.Add($"| {MoProbe.KnobName(knob.Key)} | {(toProbe.Length > 0 ? toProbe : "not checked")} | {(toDaw.Length > 0 ? toDaw : "not checked")} |");
        }
        var data = new JsonArray(check.Lines.Select(l => (JsonNode)new JsonObject
        {
            ["knob"] = l.Knob, ["name"] = l.Name, ["way"] = l.Way == KnobWay.ToProbe ? "bitwig-to-probe" : "probe-to-bitwig", ["sent"] = l.Sent,
            ["expected"] = l.Expected, ["got"] = l.Got, ["display"] = l.Display, ["matched"] = l.Matched, ["ms"] = Math.Round(l.Took.TotalMilliseconds),
            ["note"] = l.Note,
        }).ToArray());
        var detail = check.Matched == check.Lines.Count
            ? $"How Bitwig maps MO Probe's knobs ({format}): it spreads 0 to 1 evenly over each knob's range, both ways, in all {check.Lines.Count} turns."
            : $"How Bitwig maps MO Probe's knobs ({format}): {check.Matched} of {check.Lines.Count} turns landed where an even spread over each " +
              "knob's range puts them (✗ marks the others).";
        if (check.Stopped is not null) detail += " The check stopped part way, so these are only the turns it made.";
        return LiveVerdict.Note(detail, data) with { Lines = table };
    }

    static string Share(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A value of a knob as the probe shows it; a stepped knob between steps as the number, so it can be seen.</summary>
    static string AtProbe(int knob, double v) =>
        ProbeKnobs.Stepped.Contains(knob) && Math.Abs(v - Math.Round(v)) > 1e-6 ? v.ToString("0.###", CultureInfo.InvariantCulture) : ProbeKnobs.Text(knob, v);

    static string Shown(KnobCheckLine line, double? v) =>
        v is not { } value ? "nothing" : line.Way == KnobWay.ToProbe ? AtProbe(line.Knob, value) : value.ToString("0.000", CultureInfo.InvariantCulture);

    internal static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)s).ToArray());

    /// <summary>One run: the tests in order, sharing what earlier ones made (tracks, the probe running in Bitwig).</summary>
    sealed class Suite(BitwigLiveOptions options, IProgress<LiveProgress>? progress, CancellationToken ct)
    {
        const string Link = "Link", Changes = "Changes", Probe = "MO Probe", Send = "Send", Saved = "Saved file";
        const string NoLink = "Bitwig isn't answering (see Link › Hello), so this wasn't tried.";
        const string NoDrumMachine =
            "The app hasn't found Bitwig's Drum Machine ID: it reads it from a Bitwig project or preset with a Drum Machine in it " +
            "(the library's projects, then Bitwig's presets and sound packages). Save a project with a Drum Machine in one of the " +
            "app's project folders, scan, then run again.";
        /// <summary>The colour the new track is given, which reads back exactly from Bitwig's colours (0.2, 0.4, 0.8).</summary>
        const string Colour = "#3366cc";
        static readonly TimeSpan SelectionWait = TimeSpan.FromSeconds(4);
        /// <summary>How long a knob check gets: 64 turns, each of which may wait a few seconds for a value that doesn't come.</summary>
        static readonly TimeSpan KnobTime = TimeSpan.FromMinutes(5);

        readonly LiveRunner _run = new(progress, ct);
        readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
        BitwigLink? _link;
        JsonElement _hello;
        string? _closed, _save;
        /// <summary>The test project the run changes, once the gate has first let it: every change is for that one.</summary>
        string? _testProject;
        /// <summary>The project the run saved, whose file it then looks for.</summary>
        string? _savedProject;
        bool _changed, _saved, _sampleMade;
        int? _drum;
        /// <summary>The launcher slot the MIDI clip went in, on the sample's track.</summary>
        int? _clipSlot;
        /// <summary>When the run's first Save went out (the saved file is looked for since then), and the last, which is the one that worked when one did.</summary>
        DateTime _savedAt, _saveRanAt;
        LiveFiles? _files;

        string Stamp => LiveReports.Stamp(_started);
        /// <summary>The time the run started, which names its tracks: "143012".</summary>
        string Tag => Stamp[9..15];
        string DevicesTrack => $"Devices {Tag}";
        string SampleTrack => $"Sample {Tag}";
        string KitTrack => $"Kit {Tag}";

        /// <summary>Whether Bitwig has the Java extension, which gives more than the script (link 3, runtime "java").</summary>
        bool Newer => _link?.Hello is { } h && IsNewer(h);

        /// <summary>What answers in Bitwig: "Java extension 3.0, link 3" or "JavaScript script, link 2".</summary>
        string LinkText => _link?.Hello is not { } h ? ""
            : h.Runtime == "java" ? $"Java extension{(h.Extension is { Length: > 0 } v ? " " + v : "")}, link {h.Link}"
            : $"JavaScript script, link {h.Link}";

        LiveFiles Files => _files ??= LiveFiles.Write(options.FilesFolder ?? Path.Combine(options.ReportFolder, "files"));

        public async Task<LiveRun> RunAsync()
        {
            try
            {
                await LinkAsync();
                await ChangesAsync();
                await ProbeAsync("CLAP");
                await ProbeAsync("VST3");
                await SendAsync();
                await SaveAsync();
            }
            catch (Exception e)
            {
                // A mistake in the suite itself: the report still says what ran, and this.
                _run.Record("Live tests", "The run itself", LiveVerdict.Fail($"The run stopped with {e.GetType().Name}: {e.Message}",
                                                                            new JsonObject { ["error"] = e.ToString() }));
            }
            finally
            {
                _link?.Dispose();
            }
            var hello = _link?.Hello;
            var run = new LiveRun
            {
                Daw = Daw, Key = Key, Stamp = Stamp, Started = _started, Ended = DateTimeOffset.UtcNow, Results = _run.Results.ToList(),
                AppVersion = options.AppVersion, Hello = _hello.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(_hello.GetRawText()),
                Setup = hello is null ? "Bitwig didn't answer." : $"{hello.Product} {hello.Version} · {LinkText} · “{hello.Project}”",
                Cancelled = _run.Stopped,
            };
            return LiveReports.Write(run, options.ReportFolder);
        }

        // ---- Link: reads only ----

        async Task LinkAsync()
        {
            await _run.RunAsync(Link, "Hello", async t =>
            {
                _link = await options.Connect(t);
                _hello = _link.HelloJson;
                // Checked again just before every change, not only as each test starts: another project may be opened part
                // way through a test (while it looks for the Drum Machine, say), and the run must never change that one.
                var link = _link;
                link.BeforeChange = async c =>
                {
                    if (await GateAsync(link, c) is { } why) throw new DawLinkException(why);
                };
                var h = _link.Hello;
                var data = JsonNode.Parse(_hello.GetRawText());
                if (h.Link < BitwigLink.LinkVersion) return LiveVerdict.Fail(BitwigLink.OldScript, data);
                return LiveVerdict.Pass($"{h.Product} {h.Version} (API {h.Api}, {h.Platform}) answered through the {LinkText}, with " +
                                        $"“{h.Project}” open{(h.Modified ? ", which has unsaved changes" : "")}. It " +
                                        $"{(h.Changes ? "lets" : "doesn't let")} the app make changes.", data);
            });

            await Read(Link, "Project listing", async (link, t) =>
            {
                var p = await link.ProjectAsync(t);
                var data = new JsonObject
                {
                    ["name"] = p.Name, ["modified"] = p.Modified, ["tempo"] = p.Tempo, ["timeSignature"] = p.TimeSignature, ["playing"] = p.Playing,
                    ["trackCount"] = p.TrackCount, ["tracks"] = new JsonArray(p.Tracks.Select(x => (JsonNode)TrackJson(x)).ToArray()),
                };
                var listed = Math.Min(p.TrackCount, 128);
                if (p.Tracks.Count != listed)
                    return LiveVerdict.Fail($"Bitwig says the project has {Checks.Count(p.TrackCount, "track")}, but listed {p.Tracks.Count}.", data);
                if (p.Name != link.Hello.Project)
                    return LiveVerdict.Fail($"The listing names the project “{p.Name}”, but Bitwig's hello said “{link.Hello.Project}”.", data);
                return LiveVerdict.Pass($"“{p.Name}”: {Checks.Count(p.TrackCount, "track")}, {Checks.Bpm(p.Tempo)} BPM in {p.TimeSignature}" +
                                        (p.Playing ? ", playing" : "") + ".", data);
            });

            await Read(Link, "Selected device", async (link, t) =>
            {
                var s = await link.SelectedDeviceAsync(t);
                var data = SelectionJson(s);
                if (s.Device is null)
                    return LiveVerdict.Pass("Nothing is selected" + (s.Track.Length > 0 ? $" on “{s.Track}”." : "."), data);
                var noId = s.Params.Count(p => p.Id.Length == 0);
                if (noId > 0) return LiveVerdict.Fail($"{noId} of “{s.Device.Name}”'s parameters came without an ID, which setting one needs.", data);
                var shown = s.Params.Count(p => p.Display is not null);
                var unread = s.Params.Count(p => p.Value is null);
                return LiveVerdict.Pass($"“{s.Device.Name}” on “{s.Track}”: {Checks.Count(s.Params.Count, "parameter")}, {shown} with Bitwig's text for " +
                                        $"its value{(unread > 0 ? $", {unread} whose value Bitwig couldn't read" : "")}.", data);
            });

            await Read(Link, "Actions", async (link, t) =>
            {
                var actions = await link.ActionsAsync(t);
                var save = actions.FirstOrDefault(a => a.Id.Equals("Save", StringComparison.OrdinalIgnoreCase)) ??
                           actions.FirstOrDefault(a => a.Name.Equals("Save", StringComparison.OrdinalIgnoreCase));
                _save = save?.Id;
                var data = new JsonObject
                {
                    ["count"] = actions.Count,
                    ["save"] = save is null ? null : new JsonObject { ["id"] = save.Id, ["name"] = save.Name, ["category"] = save.Category, ["menu"] = save.Menu },
                    ["categories"] = Strings(actions.Select(a => a.Category).Where(c => c.Length > 0).Distinct().Order(StringComparer.Ordinal)),
                    // Every action, to look up what Bitwig can do that the API can't (such as putting a clip in the arranger).
                    ["actions"] = new JsonArray(actions.Select(a => (JsonNode)new JsonObject
                    {
                        ["id"] = a.Id, ["name"] = a.Name, ["category"] = a.Category, ["menu"] = a.Menu,
                    }).ToArray()),
                };
                if (save is null)
                    return LiveVerdict.Fail($"Bitwig listed {actions.Count:N0} actions, but none called Save, which the app runs to save a project.", data);
                return LiveVerdict.Pass($"{actions.Count:N0} action{(actions.Count == 1 ? "" : "s")}, Save among them (“{save.Id}”" +
                                        (save.Category.Length > 0 ? $", in {save.Category}" : "") + ").", data);
            });

            await Read(Link, "Round trip", async (link, t) =>
            {
                var times = new List<double>();
                for (var i = 0; i < 20; i++)
                {
                    var took = Stopwatch.StartNew();
                    await link.HelloAsync(t);
                    times.Add(Math.Round(took.Elapsed.TotalMilliseconds, 2));
                }
                var sorted = times.Order().ToList();
                var median = (sorted[9] + sorted[10]) / 2;
                var data = new JsonObject
                {
                    ["medianMs"] = median, ["worstMs"] = sorted[^1], ["ms"] = new JsonArray(times.Select(x => (JsonNode)x).ToArray()),
                };
                return LiveVerdict.Pass($"20 hellos one after another: {Ms(median)} median, {Ms(sorted[^1])} at worst.", data);
            });

            await ReadNewer(Link, "Track IDs", async (link, t) =>
            {
                var tracks = (await link.CommandAsync("project", ct: t)).GetProperty("tracks").EnumerateArray().ToList();
                if (tracks.Count == 0) return LiveVerdict.Skip("The project has no tracks to look at.");
                var ids = tracks.Select(x => (Name: Text(x, "name"), Id: x.TryGetProperty("id", out var id) ? IdText(id) : "")).ToList();
                var data = new JsonArray(ids.Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["id"] = x.Id }).ToArray());
                var without = ids.Where(x => x.Id.Length == 0).Select(x => x.Name).ToList();
                var twice = ids.Where(x => x.Id.Length > 0).GroupBy(x => x.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (twice.Count > 0) return LiveVerdict.Fail($"Some tracks share an ID: {Checks.List(twice)}.", data);
                if (without.Count == tracks.Count) return LiveVerdict.Fail("None of the tracks came with an ID.", data);
                if (without.Count > 0)
                    return LiveVerdict.Note($"{tracks.Count - without.Count} of {tracks.Count} tracks came with an ID, each different. Without one: " +
                                            $"{Checks.List(without)}.", data);
                return LiveVerdict.Pass($"All {tracks.Count} tracks came with an ID, each different (“{ids[0].Name}” is {ids[0].Id}).", data);
            });

            await ReadNewer(Link, "Parameter details", async (link, t) => ParamDetails(await link.CommandAsync("device", ct: t)));

            await ReadNewer(Link, "Last clicked parameter", async (link, t) =>
            {
                var clicked = await link.CommandAsync("clicked", ct: t);
                var data = JsonNode.Parse(clicked.GetRawText());
                if (clicked.ValueKind != JsonValueKind.Object) return LiveVerdict.Fail("Bitwig's answer wasn't what the app expected.", data);
                var name = Text(clicked, "name");
                if (name.Length == 0)
                    return LiveVerdict.Pass("Bitwig has no last-clicked parameter yet. (Click a knob in Bitwig and run again to see one read.)", data);
                var value = clicked.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? Share(v.GetDouble()) : "no value";
                var display = Text(clicked, "display");
                var steps = clicked.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Number && s.GetDouble() > 1
                    ? $", one of {s.GetDouble():0} steps" : "";
                return LiveVerdict.Pass($"The last parameter clicked in Bitwig is “{name}”, at {value}" + (display.Length > 0 ? $" (“{display}”)" : "") +
                                        steps + ".", data);
            });

            await ReadNewer(Link, "Self-check", async (link, t) =>
            {
                var answer = await link.CommandAsync("selftest", ct: t);
                var data = JsonNode.Parse(answer.GetRawText());
                if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("checks", out var list) || list.ValueKind != JsonValueKind.Array)
                    return LiveVerdict.Fail("The extension's self-check came back without its checks.", data);
                var checks = list.EnumerateArray().ToList();
                var failed = checks.Where(c => !(c.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)).ToList();
                var file = Text(answer, "file");
                var where = file.Length > 0 ? $" Its report: {file}." : "";
                if (checks.Count == 0) return LiveVerdict.Fail("The extension's self-check had no checks in it." + where, data);
                if (failed.Count > 0)
                    return LiveVerdict.Fail($"{failed.Count} of the extension's {checks.Count} own checks failed: " +
                                            Checks.List(failed.Select(c => Text(c, "name")).ToList()) + "." + where, data)
                        with { Lines = failed.Select(c => $"- {Text(c, "name")}: {Text(c, "detail")}").ToList() };
                return LiveVerdict.Pass($"All {checks.Count} of the extension's own checks passed.{where}", data);
            });

            // Extension 3.7: whether the MIDI loopback works, which Save and Clip to arranger try routes through.
            await ReadNewer(Link, "MIDI loopback", async (link, t) => LoopbackVerdict(await link.HelloAsync(t)));
        }

        // ---- Changes: only in a project made for the tests ----

        async Task ChangesAsync()
        {
            await _run.RunAsync(Changes, "Changes allowed", async t =>
            {
                if (_link is null) return LiveVerdict.Skip(NoLink);
                if (await GateAsync(_link, t) is { } why) return LiveVerdict.Skip(why);
                var p = await _link.ProjectAsync(t);
                var detail = $"Bitwig lets the app make changes, and has “{p.Name}” open, so the run can change it.";
                if (p.TrackCount > 100)
                    detail += $" It has {p.TrackCount} tracks, and each run adds " +
                              (IsFrom(_link.Hello, FlushFrom) ? "about seven (more when Clip to arranger's checks copy a track)" : "about six") +
                              $": make a fresh “{TestProject}” project now and then, " +
                              "since Bitwig lists only the first 128 to the app.";
                return LiveVerdict.Pass(detail);
            });

            await Change(Changes, "New track", async (link, t) =>
            {
                var index = await link.NewTrackAsync(DevicesTrack, Colour, t);
                var shown = (await link.ProjectAsync(t)).Tracks.FirstOrDefault(x => x.Index == index);
                if (shown is null) return LiveVerdict.Fail($"Bitwig said it added track {index}, but the listing has no track {index}.");
                var data = TrackJson(shown);
                if (shown.Name != DevicesTrack) return LiveVerdict.Fail($"The new track is called “{shown.Name}” in the listing, not “{DevicesTrack}”.", data);
                if (!SameColour(shown.Color, Colour))
                    return LiveVerdict.Fail($"The new track is coloured {shown.Color} in the listing, not {Colour} as asked.", data);
                return LiveVerdict.Pass($"Bitwig added track {index}, “{shown.Name}”, coloured {shown.Color} as asked.", data);
            });

            await Change(Changes, "Sampler by ID", async (link, t) =>
            {
                if (await TrackAsync(link, DevicesTrack, t) is not { } track) return Needs("the new track");
                return Inserted(await link.InsertBitwigDeviceAsync(track.Index, BitwigSend.Sampler, ct: t), "Sampler", $"the Sampler's ID {BitwigSend.Sampler}");
            });

            await Change(Changes, "Note Transpose by ID", async (link, t) =>
            {
                if (await TrackAsync(link, DevicesTrack, t) is not { } track) return Needs("the new track");
                var added = await link.InsertBitwigDeviceAsync(track.Index, BitwigSend.NoteTranspose, atStart: true, t);
                var verdict = Inserted(added, "Note Transpose", $"the Note Transpose's ID {BitwigSend.NoteTranspose}");
                if (verdict.Outcome == LiveOutcome.Passed && added.Index != 0)
                    return LiveVerdict.Fail($"Bitwig added the Note Transpose as device {added.Index}, not at the start of the chain as asked.", verdict.Data);
                return verdict;
            });

            await Change(Changes, "Transpose a Sampler", (link, t) => TransposeAsync(link, "Sampler", -3.5, t));
            await Change(Changes, "Transpose a Note Transpose", (link, t) => TransposeAsync(link, "Note Transpose", 5, t));

            await Change(Changes, "Sample into a Sampler", async (link, t) =>
            {
                var wav = Files.Pluck;
                var track = await link.NewTrackAsync(SampleTrack, ct: t);
                _sampleMade = true;
                var added = await link.InsertFileAsync(track, wav, ct: t);
                var file = Path.GetFileName(wav);
                var data = added.Device is { } d ? DeviceJson(d) : null;
                if (!added.Added) return LiveVerdict.Fail($"Bitwig didn't load {file} onto “{SampleTrack}”.");
                if (added.Device?.Name is not { } name || !name.Contains("Sampler", StringComparison.OrdinalIgnoreCase))
                    return LiveVerdict.Fail($"Bitwig loaded {file} as “{added.Device?.Name}”, not into a Sampler.", data);
                return LiveVerdict.Pass($"Bitwig put {file} in a Sampler on “{SampleTrack}”" +
                                        (added.Device.Preset.Length > 0 ? $", which shows “{added.Device.Preset}”." : "."), data);
            });

            await Change(Changes, "MIDI clip", async (link, t) =>
            {
                if (!_sampleMade || await TrackAsync(link, SampleTrack, t) is not { } track) return Needs("the sample's track");
                var slot = await link.AddClipAsync(track.Index, Files.Clip, ct: t);
                _clipSlot = slot;
                return LiveVerdict.Pass($"Bitwig made a clip from {Path.GetFileName(Files.Clip)} in launcher slot {slot + 1} of “{SampleTrack}”.",
                                        new JsonObject { ["slot"] = slot });
            });

            // An experiment: the API can't put a clip in the arranger, so the extension copies and pastes it there as a person
            // would, and says what Bitwig did.
            await Change(Changes, "Clip to arranger", (link, t) => ClipToArrangerAsync(link, SampleTrack, _clipSlot, options.BringBitwigToFront, t,
                                                                                       async () => await ClosedSinceAsync(link) is not null),
                         ArrangeTime);

            // Another experiment: Bitwig's API can insert a file between tracks, which may put a MIDI file's clip in the arranger.
            await Change(Changes, "MIDI file as a track", (link, t) =>
                _sampleMade ? FileAsTrackAsync(link, SampleTrack, Files.Clip, t) : Task.FromResult(Needs("the sample's track")));

            await Change(Changes, "Drum Machine by ID", async (link, t) =>
            {
                if ((options.DrumMachine is { } known ? known(t) : FindDrumMachine(t)) is not { } id) return LiveVerdict.Skip(NoDrumMachine);
                var track = await link.NewTrackAsync(KitTrack, ct: t);
                var added = await link.InsertBitwigDeviceAsync(track, id, ct: t);
                var verdict = Inserted(added, BitwigSend.DrumMachine, $"the Drum Machine's ID {id}");
                if (verdict.Outcome == LiveOutcome.Passed) _drum = added.Index;
                return verdict;
            }, TimeSpan.FromMinutes(2));

            await Change(Changes, "Drum pads", async (link, t) =>
            {
                if (_drum is not { } at || await TrackAsync(link, KitTrack, t) is not { } track) return Needs("the Drum Machine");
                var pads = Files.Pads;
                var filled = await link.FillPadsAsync(pads, track.Index, at, t);
                var data = new JsonObject
                {
                    ["filled"] = new JsonArray(filled.Filled.Select(n => (JsonNode)n).ToArray()),
                    ["skipped"] = new JsonArray(filled.Skipped.Select(n => (JsonNode)n).ToArray()),
                    ["failed"] = new JsonArray(filled.Failed.Select(n => (JsonNode)n).ToArray()),
                    ["replaced"] = filled.Replaced is { } replaced ? new JsonArray(replaced.Select(n => (JsonNode)n).ToArray()) : null,
                    ["taken"] = filled.Taken is { } taken
                        ? new JsonArray(taken.Select(p => (JsonNode)new JsonObject
                        {
                            ["note"] = p.Note, ["name"] = p.Name, ["devices"] = p.Devices, ["first"] = p.First, ["sample"] = p.Sample,
                        }).ToArray())
                        : null,
                };
                string On(IEnumerable<int> notes) => string.Join(", ", pads.Where(p => notes.Contains(p.Note)).Select(p => $"{p.Name} on {DawProject.NoteName(p.Note)}"));
                if (filled.Filled.Count == pads.Count)
                    return LiveVerdict.Pass($"Bitwig put each sample on its pad: {On(filled.Filled)}" +
                                            (filled.Replaced is { Count: > 0 } r ? $", {(r.Count == pads.Count ? "each" : On(r))} in place of an empty Sampler." : "."), data);
                var missed = new List<string>();
                if (filled.Failed.Count > 0) missed.Add($"it didn't load {On(filled.Failed)}");
                if (filled.Skipped.Count > 0)
                    missed.Add($"it said {On(filled.Skipped)} already had something on them" + (filled.Taken is { Count: > 0 } shown
                        ? " (" + string.Join("; ", shown.Select(p => $"{DawProject.NoteName(p.Note)} showed “{p.Name}” with {p.Devices} " +
                                                                     $"device{(p.Devices == 1 ? "" : "s")}, the first “{p.First}”" +
                                                                     (p.Sample.Length > 0 ? $" playing {p.Sample}" : ""))) + ")"
                        : ""));
                return LiveVerdict.Fail($"Not every sample went on its pad: {string.Join("; ", missed)}.", data);
            });

            await Change(Changes, "Set a parameter", async (link, t) =>
            {
                if (await TrackAsync(link, DevicesTrack, t) is not { Devices.Count: > 0 } track) return Needs("the new track with its devices");
                var device = track.Devices.FirstOrDefault(d => d.Name.Contains("Sampler", StringComparison.OrdinalIgnoreCase)) ?? track.Devices[0];
                await link.SelectAsync(track.Index, device.Index, t);
                // Bitwig fills in each value's text a moment after the value, so wait for the text too (or, without it, the full wait).
                var shown = await SelectedAsync(link, track.Name, device.Name, s => PickParam(s.Params, []) is { Display.Length: > 0 }, t);
                if (shown is null) return LiveVerdict.Fail($"Bitwig didn't select “{device.Name}” on “{track.Name}” within {SelectionWait.TotalSeconds:0} s.");
                if (shown.Params.Count == 0) return LiveVerdict.Fail($"Bitwig selected “{device.Name}” but listed none of its parameters.");
                var stepped = Newer ? SteppedIds(await link.CommandAsync("device", ct: t)) : [];
                if (PickParam(shown.Params, stepped) is not { Value: { } was } param)
                    return LiveVerdict.Fail($"Bitwig couldn't read the value of any of “{device.Name}”'s parameters.");
                var target = Math.Round(was < 0.5 ? was + 0.25 : was - 0.25, 3);
                await link.SetParameterAsync(param.Id, target, t);
                var after = await SelectedAsync(link, track.Name, device.Name,
                    s => s.Params.FirstOrDefault(p => p.Id == param.Id) is { Value: { } v } p && Math.Abs(v - target) <= 0.01 && p.Display != param.Display, t);
                var now = after?.Params.FirstOrDefault(p => p.Id == param.Id);
                // Back as it was, so the test project's sound doesn't drift from run to run.
                try { await link.SetParameterAsync(param.Id, was, t); }
                catch (DawLinkException) { }
                var data = new JsonObject
                {
                    ["device"] = device.Name, ["param"] = param.Id, ["name"] = param.Name, ["was"] = was, ["wasShown"] = param.Display,
                    ["set"] = target, ["got"] = now?.Value, ["gotShown"] = now?.Display,
                };
                if (now?.Value is not { } got || Math.Abs(got - target) > 0.01)
                    return LiveVerdict.Fail($"The app set “{param.Name}” on the {device.Name} to {Share(target)}, but Bitwig showed " +
                                            $"{(now?.Value is { } v ? Share(v) : "nothing")} afterwards.", data);
                return LiveVerdict.Pass($"Set “{param.Name}” on the {device.Name} from {Share(was)}{Quoted(param.Display)} to {Share(target)}, and Bitwig " +
                                        $"showed {Share(got)}{Quoted(now.Display)} afterwards. Then set it back.", data);
            });

            await Change(Changes, "Sampler's parameter details", async (link, t) =>
                Newer ? ParamDetails(await link.CommandAsync("device", ct: t)) : LiveVerdict.Skip(NeedsJava));

            await Change(Changes, "Master recorder", async (link, t) =>
            {
                if (!Newer) return LiveVerdict.Skip(NeedsJava);
                var status = await link.CommandAsync("recorder", new JsonObject { ["do"] = "status" }, t);
                var data = JsonNode.Parse(status.GetRawText());
                if (status.ValueKind != JsonValueKind.Object || !status.TryGetProperty("active", out var active) ||
                    active.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return LiveVerdict.Fail("The master recorder's status came back without saying whether it's recording.", data);
                return LiveVerdict.Pass(active.GetBoolean() ? "Bitwig's master recorder is recording." : "Bitwig's master recorder isn't recording.", data);
            });
        }

        async Task<LiveVerdict> TransposeAsync(BitwigLink link, string device, double semitones, CancellationToken t)
        {
            if (await TrackAsync(link, DevicesTrack, t) is not { } track) return Needs("the new track");
            if (track.Devices.FirstOrDefault(d => d.Name.Contains(device, StringComparison.OrdinalIgnoreCase)) is not { } on)
                return Needs($"the {device} on “{track.Name}”");
            var pitch = await link.TransposeAsync(track.Index, on.Index, semitones, t);
            var data = new JsonObject { ["device"] = pitch.Device, ["asked"] = semitones, ["value"] = pitch.Value, ["display"] = pitch.Display };
            var asked = semitones.ToString("+0.##;-0.##", CultureInfo.InvariantCulture);
            if (Math.Abs(pitch.Value - semitones) > 0.01)
                return LiveVerdict.Fail($"Bitwig set the {device} to {pitch.Display} ({pitch.Value:0.###} semitones), not {asked} as asked.", data);
            return LiveVerdict.Pass($"Bitwig set the {device} to {pitch.Display}, {asked} semitones as asked, and read it back.", data);
        }

        // ---- MO Probe: on a new track, through the probe's own live link ----

        async Task ProbeAsync(string format)
        {
            var track = $"Probe {format} {Tag}";
            HashSet<string> before = [];
            var inserted = false;
            MoProbeLive? probe = null;

            await Change(Probe, $"Insert MO Probe ({format})", async (link, t) =>
            {
                if (ProbeId(format) is not { } id) return LiveVerdict.Skip(NotInstalled(format));
                before = MoProbeLive.Running(options.ProbeFolder).Select(p => p.Instance).ToHashSet();
                var index = await link.NewTrackAsync(track, ct: t);
                var added = await link.InsertPluginAsync(index, format, id, ct: t);
                if (!added.Added)
                    return LiveVerdict.Fail($"Bitwig didn't add MO Probe by its {format} ID ({id}). Check that Bitwig has found it (in Bitwig, " +
                                            "Settings > Locations, rescan plugins), then run again.");
                inserted = true;
                return LiveVerdict.Pass($"Bitwig added {added.Device?.Name ?? ProbeName} ({format}, ID {id}) on “{track}”.",
                                        added.Device is { } d ? DeviceJson(d) : null);
            });

            await Change(Probe, $"Live status ({format})", async (link, t) =>
            {
                if (!inserted) return Needs($"MO Probe ({format}) on a track");
                var waited = Stopwatch.StartNew();
                while ((probe = NewProbe(format, track, before)) is null)
                {
                    if (waited.Elapsed > options.ProbeStart)
                        return LiveVerdict.Fail($"No new MO Probe ({format}) said it's running in Bitwig within {options.ProbeStart.TotalSeconds:0} s of being " +
                                                $"added (it writes its status in {Path.Combine(options.ProbeFolder, "live")}). " +
                                                ((await link.HelloAsync(t)).Engine ? "" : "Bitwig's audio engine is off, so plugins don't run: turn it on. ") +
                                                "An MO Probe from before the live link doesn't write one: install the new one.");
                    await Task.Delay(100, t);
                }
                var told = probe.Track.Length > 0 ? "" : " Bitwig didn't tell it which track it's on, so the app goes by the track it put it on.";
                return LiveVerdict.Pass($"MO Probe {probe.Instance} is running: {probe.Summary}{(probe.Track.Length > 0 ? $", on “{probe.Track}”" : "")}.{told}",
                                        new JsonObject { ["instance"] = probe.Instance, ["report"] = probe.Report, ["status"] = probe.File, ["track"] = probe.Track });
            }, options.ProbeStart + TimeSpan.FromSeconds(10));

            await Change(Probe, $"Ping and label ({format})", async (link, t) =>
            {
                if (probe is null) return Needs($"MO Probe ({format}) running");
                var took = Stopwatch.StartNew();
                var pong = await MoProbeLive.SendAsync(probe.Instance, ProbeCommand.Ping(), options.ProbeFolder, ct: t);
                var pinged = took.Elapsed.TotalMilliseconds;
                if (pong.Event != "pong") return LiveVerdict.Fail($"MO Probe answered the ping with “{pong.Event}”: {pong.Str("error")}");
                var label = ProbeCommand.CleanLabel($"{TestProject} {Stamp} {format}");
                var named = await MoProbeLive.SendAsync(probe.Instance, ProbeCommand.Label(label), options.ProbeFolder, ct: t);
                if (named.Event != "label") return LiveVerdict.Fail($"MO Probe answered the label with “{named.Event}”: {named.Str("error")}");
                return LiveVerdict.Pass($"It answered a ping in {Ms(pinged)}, and took the label “{label}”, which its report ({probe.Report}) " +
                                        "now carries too.", new JsonObject { ["pingMs"] = Math.Round(pinged, 1), ["label"] = label, ["report"] = probe.Report });
            });

            KnobCheck? check = null;
            var checking = await Change(Probe, $"Knob check ({format})", async (link, t) =>
            {
                if (probe is null) return Needs($"MO Probe ({format}) running");
                // Bitwig may not tell a probe its track (through the VST3 wrapper, say), but the run knows where it put it.
                var located = probe.Track.Length > 0 ? probe : probe with { Track = track };
                var device = await BitwigProbeDevice.FindAsync(link, located, t);
                var done = await ProbeKnobCheck.RunAsync(device, located, null, options.ProbeWait, options.ProbeSettle, t);
                if (done.Stopped != ProbeKnobCheck.YouStopped) return KnobVerdict(check = done);
                // Stopped by the run's token: the run was stopped, or the check ran out of time. The turns it made, and any knob
                // it couldn't set back, still go in the report.
                check = done with { Stopped = _run.Stopped ? "the run was stopped." : $"it ran out of time ({KnobTime.TotalMinutes:0} minutes)." };
                var verdict = KnobVerdict(check);
                return _run.Stopped ? LiveVerdict.Skip($"{LiveRunner.StoppedDuring} {Checks.Count(check.Lines.Count, "turn")} had been made.", verdict.Data)
                                      with { Lines = verdict.Lines }
                                    : verdict;
            }, KnobTime);
            _run.Record(Probe, $"Knob mapping ({format})", check is not null ? KnobMapping(check, format)
                : LiveVerdict.Skip(checking.Outcome == LiveOutcome.Skipped ? checking.Detail : "Needs the knob check, which didn't finish."));
        }

        /// <summary>The probe that started in Bitwig since the run added one: the one on its track, or the only new one that doesn't know its track.</summary>
        MoProbeLive? NewProbe(string format, string track, HashSet<string> before)
        {
            var fresh = MoProbeLive.Running(options.ProbeFolder)
                .Where(p => !before.Contains(p.Instance) && p.Host.Contains("Bitwig", StringComparison.OrdinalIgnoreCase) &&
                            p.Format.Equals(format, StringComparison.OrdinalIgnoreCase) && p.Kind != "effect")
                .ToList();
            return fresh.FirstOrDefault(p => p.Track == track) ??
                   (fresh.Count == 1 && fresh[0].Track.Length == 0 ? fresh[0] : null);
        }

        /// <summary>MO Probe Instrument's ID in a format, if it's installed: as the plugin scanner read it, else the probe's own.</summary>
        string? ProbeId(string format)
        {
            var scanned = options.ScannedProbes.FirstOrDefault(p => p.Format.Equals(format, StringComparison.OrdinalIgnoreCase) && p.Id.Length > 0 &&
                                                                    p.Name.Equals(ProbeName, StringComparison.OrdinalIgnoreCase));
            if (scanned is not null) return scanned.Id;
            return MoProbe.Installed(options.PluginFolders()).Contains(format) ? format == "CLAP" ? ProbeClapId : ProbeVst3Id : null;
        }

        string NotInstalled(string format)
        {
            var folder = options.PluginFolders().FirstOrDefault(f => Path.GetFileName(f.TrimEnd('\\', '/')).Equals(format, StringComparison.OrdinalIgnoreCase));
            return $"MO Probe isn't installed as a {format}. To test it: copy “MO Probe.{format.ToLowerInvariant()}” from a build (native/README.md, " +
                   $"“MO Probe”, says how) into {(folder is null ? $"your {format} plugin folder" : folder)}, rescan plugins in Bitwig, then run again.";
        }

        // ---- Send: an FL Studio channel, through BitwigSend ----

        Task SendAsync() => Change(Send, "FL Studio channel", async (link, t) =>
        {
            var name = $"Send {Tag}";
            var wav = Files.Pluck;
            // A sample on root key A4 (FL Studio's name), so the Sampler is tuned up three semitones.
            var channel = new MidiChannel(name, name, 96, LiveFiles.Notes, SamplePath: wav, RootKey: 57);
            var files = new BitwigSend.Files(Path.Combine(Files.Folder, TestProject + ".flp"), Files.Folder, 120,
                                             new Dictionary<string, BitwigPreset.Template>());
            var sent = await BitwigSend.ChannelAsync(link, channel, null, [], files, ct: t);
            var data = new JsonObject
            {
                ["name"] = sent.Name, ["track"] = sent.Track, ["instrument"] = sent.Instrument, ["clip"] = sent.Clip, ["checked"] = sent.Checked,
                ["problems"] = Strings(sent.Problems),
            };
            if (sent.Problems.Count > 0)
                return LiveVerdict.Fail($"“{name}” didn't come across whole: {string.Join("; ", sent.Problems)}.", data)
                    with { Lines = sent.Problems.Select(p => "- " + p).ToList() };
            if (!sent.Checked) return LiveVerdict.Fail("The app couldn't read the track back from Bitwig afterwards.", data);
            if (sent.Instrument != "Sampler" || !sent.Clip)
                return LiveVerdict.Fail($"“{name}” went in with {(sent.Instrument.Length > 0 ? sent.Instrument : "nothing")} playing it" +
                                        (sent.Clip ? "" : " and no clip") + ", where a Sampler and a clip were expected.", data);
            var shown = (await link.ProjectAsync(t)).Tracks.LastOrDefault(x => x.Name == name);
            if (shown is null) return LiveVerdict.Fail($"BitwigSend said “{name}” went in, but Bitwig doesn't list a track called that.", data);
            data["devices"] = Strings(shown.Devices.Select(d => d.Name));
            return LiveVerdict.Pass($"“{name}”, a sample on root key {DawProject.NoteName(57, flStudio: true)}, went in as a Sampler tuned up 3 semitones with its " +
                                    "notes as a clip, and read back from Bitwig as sent.", data);
        });

        // ---- Save, then the saved file against what Bitwig shows ----

        async Task SaveAsync()
        {
            await Change(Changes, "Save", async (link, t) =>
            {
                if (_save is null) return LiveVerdict.Fail("Bitwig listed no Save action (see Link › Actions), so the run couldn't save.");
                var project = link.Hello.Project;
                _savedAt = _saveRanAt = DateTime.UtcNow;
                var verdict = await SaveByRoutesAsync(link, _save, options.SaveWait,
                                                      since => FindSaved(project, since, options.LibraryProjects(), SavedFolders()),
                                                      options.BringBitwigToFront, async () => await ClosedSinceAsync(link) is not null,
                                                      saving: at => _saveRanAt = at, ct: t);
                if (verdict.Outcome == LiveOutcome.Passed)
                {
                    _saved = true;
                    _savedProject = project;
                }
                return verdict;
            }, SaveTime);

            string? path = null;
            await Read(Saved, "Find the saved project", (link, t) =>
            {
                if (!_saved || _savedProject is not { } name) return Task.FromResult(LiveVerdict.Skip(_closed ?? "Needs Changes › Save, which didn't work."));
                var looked = SavedFolders().ToList();
                path = FindSaved(name, _savedAt.AddSeconds(-2), options.LibraryProjects(), looked);
                if (path is null)
                    return Task.FromResult(LiveVerdict.Skip(
                        $"Bitwig saved “{name}”, but the app didn't find a {name}.bwproject saved since, among the library's projects or in " +
                        $"{(looked.Count == 0 ? "Bitwig's Projects folder, which it couldn't find" : string.Join(" or ", looked))}. If you " +
                        "saved it somewhere else, add that folder to the app's project folders."));
                return Task.FromResult(FoundSaved(path, File.GetLastWriteTimeUtc(path), _savedAt, _saveRanAt));
            });

            await Read(Saved, "Compare with Bitwig", async (link, t) =>
            {
                if (path is null) return LiveVerdict.Skip(_saved ? "Needs the saved project, which wasn't found." : _closed ?? "Needs Changes › Save, which didn't work.");
                var read = Readers.Bitwig.Read(path);
                var live = await link.ProjectAsync(t);
                if (live.Name != _savedProject)
                    return LiveVerdict.Skip($"Bitwig has “{live.Name}” open now, not “{_savedProject}”, so there was nothing to compare the saved file with.");
                return CompareSaved(path, read, live);
            });
        }

        /// <summary>How long Save gets: a wait for the unsaved changes to clear after Save by each route, and time to spare.</summary>
        TimeSpan SaveTime => options.SaveWait * Routes.Count + TimeSpan.FromSeconds(30);

        /// <summary>How long Clip to arranger gets: a go by each route, each with a few of the extension's waits, and its checks'.</summary>
        static TimeSpan ArrangeTime => TimeSpan.FromSeconds(60) * Routes.Count;

        IEnumerable<string> SavedFolders() => ProjectFolders(options.BitwigUserFolders(), options.LibraryProjects());

        /// <summary>
        /// The newest &lt;name&gt;.bwproject written since <paramref name="since"/>: among the library's projects, else in the
        /// folders given (Bitwig keeps each project in a folder of its own name there).
        /// </summary>
        static string? FindSaved(string name, DateTime since, IEnumerable<string> library, IEnumerable<string> folders)
        {
            var file = name + ".bwproject";
            var candidates = library.Where(p => Path.GetFileName(p).Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var folder in folders)
            {
                try
                {
                    candidates.AddRange(Directory.EnumerateFiles(folder, file, new EnumerationOptions
                    {
                        RecurseSubdirectories = true, MaxRecursionDepth = 3, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true,
                    }));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return candidates.Where(File.Exists).Select(p => (Path: p, Written: File.GetLastWriteTimeUtc(p)))
                             .Where(p => p.Written >= since).OrderByDescending(p => p.Written).Select(p => p.Path).FirstOrDefault();
        }

        // ---- How tests run ----

        Task<LiveResult> Read(string area, string name, Func<BitwigLink, CancellationToken, Task<LiveVerdict>> body, TimeSpan? timeout = null) =>
            _run.RunAsync(area, name, t => _link is null ? Task.FromResult(LiveVerdict.Skip(NoLink)) : body(_link, t), timeout);

        /// <summary>A read that needs the Java extension: Skipped saying so with the script.</summary>
        Task<LiveResult> ReadNewer(string area, string name, Func<BitwigLink, CancellationToken, Task<LiveVerdict>> body) =>
            Read(area, name, (link, t) => Newer ? body(link, t) : Task.FromResult(LiveVerdict.Skip(NeedsJava)));

        /// <summary>
        /// A test that changes the project: run only while <see cref="GateAsync"/> allows it, else Skipped saying why. The
        /// gate is asked again before each change the test makes (<see cref="BitwigLink.BeforeChange"/>); when it closes part
        /// way through, the change is refused and the test is Skipped, with what it had found by then.
        /// </summary>
        Task<LiveResult> Change(string area, string name, Func<BitwigLink, CancellationToken, Task<LiveVerdict>> body, TimeSpan? timeout = null) =>
            _run.RunAsync(area, name, async t =>
            {
                if (_link is null) return LiveVerdict.Skip(NoLink);
                if (await GateAsync(_link, t) is { } why) return LiveVerdict.Skip(why);
                _changed = true;
                LiveVerdict? verdict = null;
                DawLinkException? refused = null;
                try { verdict = await body(_link, t); }
                catch (DawLinkException e) { refused = e; }
                // A test that went wrong because another project came to the front (a change refused, a selection that went
                // astray) says that, rather than blaming Bitwig.
                if ((_closed is not null || refused is not null || verdict!.Outcome == LiveOutcome.Failed) &&
                    await ClosedSinceAsync(_link) is { } closed)
                    return PartWay(closed, verdict);
                if (refused is not null) ExceptionDispatchInfo.Throw(refused);
                return verdict!;
            }, timeout);

        /// <summary>Why the run may no longer change the project, asking Bitwig afresh if it hasn't said yet; null while it may.</summary>
        async Task<string?> ClosedSinceAsync(BitwigLink link)
        {
            if (_closed is not null) return _closed;
            try
            {
                using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                return await GateAsync(link, quick.Token);
            }
            catch (Exception e) when (e is DawLinkException or OperationCanceledException)
            {
                return null;
            }
        }

        static LiveVerdict PartWay(string why, LiveVerdict? found) =>
            LiveVerdict.Skip("Stopped part way through: " + why, found?.Data) with { Lines = found?.Lines ?? [] };

        /// <summary>
        /// Why the run may not change the project, or null when it may: Bitwig lets the app make changes and has a project
        /// made for the tests open, the same one all run, and its link can keep each change in that project (<see
        /// cref="BitwigLink.OnlyIn"/>, which the run then sets). Asked afresh before each change, since another project may
        /// have been opened meanwhile; once closed, it stays closed for the rest of the run.
        /// </summary>
        async Task<string?> GateAsync(BitwigLink link, CancellationToken t)
        {
            if (_closed is not null) return _closed;
            var hello = await link.HelloAsync(t);
            var changed = _changed ? "so the run made no more changes" : "so the run changed nothing";
            if (!hello.Changes) _closed = $"Bitwig isn't letting the app make changes, {changed}. {HowToAllowChanges}";
            else if (!IsTestProject(hello.Project))
                _closed = $"Bitwig has “{hello.Project}” open, {changed}: it only changes a project whose name starts with “{TestProject}”, " +
                          $"never your own work. {HowToAllowChanges}";
            else if (_testProject is { } first && hello.Project != first)
                _closed = $"Bitwig has “{hello.Project}” open now, not “{first}”, {changed}. Keep one test project in front while the tests run.";
            else if (!hello.OnlyIn)
                _closed = $"The {(hello.Runtime == "java" ? "Music Organiser extension" : "Music Organiser script")} in Bitwig is older than the " +
                          $"app, and can't make sure each change stays in “{hello.Project}”, {changed}. {Reinstall}";
            else if (_testProject is null)
            {
                _testProject = hello.Project;
                link.OnlyIn = hello.Project;
            }
            return _closed;
        }


        static async Task<BitwigTrack?> TrackAsync(BitwigLink link, string name, CancellationToken t) =>
            (await link.ProjectAsync(t)).Tracks.LastOrDefault(x => x.Name == name);

        /// <summary>The device Bitwig selected once it's <paramref name="device"/> on <paramref name="track"/> and ready, or as it last showed it.</summary>
        static async Task<BitwigSelection?> SelectedAsync(BitwigLink link, string track, string device, Func<BitwigSelection, bool> ready,
                                                          CancellationToken t)
        {
            BitwigSelection? last = null;
            for (var waited = Stopwatch.StartNew(); ; await Task.Delay(100, t))
            {
                var now = await link.SelectedDeviceAsync(t);
                if (now.Track == track && now.Device?.Name == device)
                {
                    last = now;
                    if (ready(now)) return now;
                }
                if (waited.Elapsed >= SelectionWait) return last;
            }
        }

        Guid? FindDrumMachine(CancellationToken t) =>
            BitwigSend.FindDeviceId(BitwigSend.DrumMachine, options.LibraryProjects().Concat(BitwigSend.BitwigPresets(t)), t);

        static LiveVerdict Inserted(BitwigInserted added, string expected, string what)
        {
            var data = added.Device is { } d ? DeviceJson(d) : null;
            if (!added.Added) return LiveVerdict.Fail($"Bitwig didn't add anything for {what}.");
            if (added.Device?.Name is not { } name || !name.Contains(expected, StringComparison.OrdinalIgnoreCase))
                return LiveVerdict.Fail($"Bitwig added “{added.Device?.Name}” for {what}, not a {expected}.", data);
            return LiveVerdict.Pass($"Bitwig added its {expected} by ID, as device {added.Index} on the track.", data);
        }

        /// <summary>
        /// The Java extension's details of the selected device's remote controls (the page Bitwig shows): each says its
        /// steps, where it starts from (origin) and whether it's automated; stepped ones may name their steps. Bitwig gives
        /// these only for remote controls, so a parameter carries them only when it's one of those controls.
        /// </summary>
        static LiveVerdict ParamDetails(JsonElement selection)
        {
            if (selection.ValueKind != JsonValueKind.Object || !selection.TryGetProperty("params", out var parameters) ||
                parameters.ValueKind != JsonValueKind.Array)
                return LiveVerdict.Fail("Bitwig's answer wasn't what the app expected.");
            var device = selection.TryGetProperty("device", out var d) && d.ValueKind == JsonValueKind.Object ? Text(d, "name") : "";
            if (device.Length == 0)
                return LiveVerdict.Skip("Nothing is selected in Bitwig, so there were no parameters to look at. Select a device and run again " +
                                        "(Changes › Sampler's parameter details also looks, on the Sampler the run adds).");
            if (!selection.TryGetProperty("controls", out var list) || list.ValueKind != JsonValueKind.Array)
                return LiveVerdict.Fail($"The extension didn't list “{device}”'s remote controls. Its self-check (Link › Self-check) says why.");
            var page = Text(selection, "page");
            var all = list.EnumerateArray().ToList();
            var data = new JsonObject
            {
                ["device"] = device, ["page"] = page, ["params"] = parameters.GetArrayLength(),
                ["controls"] = new JsonArray(all.Select(c => JsonNode.Parse(c.GetRawText())).ToArray()),
            };
            if (all.Count == 0) return LiveVerdict.Skip($"“{device}” has no remote controls on the page Bitwig shows, so there were none to look at.", data);
            var missing = new List<string>();
            foreach (var c in all)
            {
                var lacks = new List<string>();
                if (!(c.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Number)) lacks.Add("steps");
                if (!(c.TryGetProperty("origin", out var o) && o.ValueKind == JsonValueKind.Number)) lacks.Add("origin");
                if (!(c.TryGetProperty("automated", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False)) lacks.Add("automated");
                if (c.TryGetProperty("stepNames", out var n) && n.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null)) lacks.Add("stepNames as a list");
                if (lacks.Count > 0) missing.Add($"“{Text(c, "name")}” has no {string.Join(", ", lacks)}");
            }
            var on = page.Length > 0 ? $" (page “{page}”)" : "";
            if (missing.Count > 0)
                return LiveVerdict.Fail($"{missing.Count} of “{device}”'s {all.Count} remote controls{on} don't say all they should: " +
                                        $"{Checks.List(missing, quote: false)}.", data);
            var stepped = all.Where(c => c.GetProperty("steps").GetDouble() > 1).ToList();
            var example = stepped.FirstOrDefault(c => c.TryGetProperty("stepNames", out var n) && n.ValueKind == JsonValueKind.Array && n.GetArrayLength() > 0);
            var named = example.ValueKind == JsonValueKind.Object
                ? $" (“{Text(example, "name")}”: {string.Join(", ", example.GetProperty("stepNames").EnumerateArray().Take(6).Select(x => x.ToString()))})" : "";
            var automated = all.Count(c => c.GetProperty("automated").GetBoolean());
            var matched = all.Count(c => Text(c, "param").Length > 0);
            return LiveVerdict.Pass($"All {all.Count} of “{device}”'s remote controls{on} say their steps, origin and whether they're automated: " +
                                    $"{stepped.Count} stepped{named}, {automated} automated. The link matched {matched} of them to " +
                                    $"{(matched == 1 ? "its parameter" : "their parameters")}.", data);
        }

        /// <summary>
        /// The selected device's stepped parameters, by ID, as far as the link says (the Java extension, for the parameters
        /// that are remote controls).
        /// </summary>
        static HashSet<string> SteppedIds(JsonElement selection)
        {
            static IEnumerable<JsonElement> Each(JsonElement e, string name) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];
            static bool Stepped(JsonElement p) => p.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Number && s.GetDouble() > 1;
            return Each(selection, "params").Where(Stepped).Select(p => Text(p, "id"))
                .Concat(Each(selection, "controls").Where(Stepped).Select(c => Text(c, "param")))
                .Where(id => id.Length > 0).ToHashSet();
        }

        /// <summary>A parameter that's safe to move and read back: a level of some kind if there is one, never a stepped one when that's known.</summary>
        static BitwigParam? PickParam(IReadOnlyList<BitwigParam> all, HashSet<string> stepped)
        {
            var readable = all.Where(p => p.Value is not null && p.Id.Length > 0 && !stepped.Contains(p.Id)).ToList();
            string[] levels = ["gain", "volume", "level", "output", "amount", "mix"];
            return readable.FirstOrDefault(p => levels.Any(l => p.Name.Contains(l, StringComparison.OrdinalIgnoreCase))) ?? readable.FirstOrDefault();
        }

        /// <summary>Whether two "#rrggbb" colours are the same, give or take Bitwig's rounding.</summary>
        static bool SameColour(string a, string b)
        {
            static int[]? Rgb(string c) => c.Length == 7 && c[0] == '#' && int.TryParse(c[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
                ? [v >> 16, (v >> 8) & 0xff, v & 0xff] : null;
            return Rgb(a) is { } x && Rgb(b) is { } y && x.Zip(y).All(p => Math.Abs(p.First - p.Second) <= 2);
        }

        static JsonObject TrackJson(BitwigTrack t) => new()
        {
            ["index"] = t.Index, ["name"] = t.Name, ["type"] = t.Type, ["color"] = t.Color, ["deviceCount"] = t.DeviceCount,
            ["devices"] = Strings(t.Devices.Select(d => d.Name)),
        };

        static JsonObject DeviceJson(BitwigDevice d) => new()
        {
            ["index"] = d.Index, ["name"] = d.Name, ["preset"] = d.Preset, ["kind"] = d.Kind, ["plugin"] = d.Plugin, ["enabled"] = d.Enabled,
        };

        static JsonObject SelectionJson(BitwigSelection s) => new()
        {
            ["track"] = s.Track, ["device"] = s.Device is { } d ? DeviceJson(d) : null, ["drumPads"] = s.DrumPads, ["paramCount"] = s.Params.Count,
            ["params"] = new JsonArray(s.Params.Take(64).Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.Id, ["name"] = p.Name, ["value"] = p.Value, ["display"] = p.Display,
            }).ToArray()),
        };

        static string Text(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        static string IdText(JsonElement id) => id.ValueKind switch
        {
            JsonValueKind.String => id.GetString() ?? "",
            JsonValueKind.Number => id.GetRawText(),
            _ => "",
        };

        static string Quoted(string? display) => display is { Length: > 0 } d ? $" (“{d}”)" : "";
    }
}

/// <summary>
/// The samples and MIDI file a live run sends into Bitwig, written by the run itself. Their names and content are the same
/// every run, so a file a project still plays is never changed (and one Bitwig has open is never written over).
/// </summary>
sealed class LiveFiles
{
    const int Rate = 44100;

    /// <summary>The notes of the clip and of the FL Studio channel the run sends: A, C and E, a beat each.</summary>
    public static IReadOnlyList<NoteEvent> Notes { get; } = [new(0, 96, 57, 100), new(96, 96, 60, 100), new(192, 96, 64, 100)];

    public required string Folder { get; init; }
    /// <summary>A plucked A (440 Hz), for a Sampler.</summary>
    public required string Pluck { get; init; }
    public required string Clip { get; init; }
    /// <summary>A small kit for a Drum Machine's pads, where General MIDI puts each.</summary>
    public required IReadOnlyList<BitwigPad> Pads { get; init; }

    public static LiveFiles Write(string folder)
    {
        // Bitwig opens these files itself, so their paths can't be relative to the app's working folder.
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        TidyExports(folder);
        string Wav(string name, Func<int, double> sample, double seconds)
        {
            var frames = new float[(int)(Rate * seconds)];
            for (var i = 0; i < frames.Length; i++) frames[i] = (float)sample(i);
            var path = Path.Combine(folder, name + ".wav");
            var temp = path + ".tmp";
            WavWriter.Write(temp, [frames], Rate);
            var bytes = File.ReadAllBytes(temp);
            File.Delete(temp);
            Keep(path, bytes);
            return path;
        }
        // Noise from a fixed seed, so the files come out the same every time.
        var snareNoise = new Random(38);
        var hatNoise = new Random(42);
        var pluck = Wav("Live Pluck", i => 0.5 * Math.Sin(2 * Math.PI * 440 * i / Rate) * Math.Exp(-6.0 * i / Rate), 0.5);
        var kick = Wav("Live Kick", i =>
        {
            var t = (double)i / Rate;
            return 0.8 * Math.Sin(2 * Math.PI * (45 * t + 75 * (1 - Math.Exp(-30 * t)) / 30)) * Math.Exp(-12 * t);
        }, 0.3);
        var snare = Wav("Live Snare", i =>
        {
            var t = (double)i / Rate;
            return (0.4 * (snareNoise.NextDouble() * 2 - 1) + 0.3 * Math.Sin(2 * Math.PI * 180 * t)) * Math.Exp(-20 * t);
        }, 0.2);
        var hat = Wav("Live Hat", i => 0.3 * (hatNoise.NextDouble() * 2 - 1) * Math.Exp(-60.0 * i / Rate), 0.08);

        var clip = new MidiClip { Name = "Live Clip", Ppq = 96 };
        clip.Track("Live Clip").AddRange(Notes);
        var mid = Path.Combine(folder, "Live Clip.mid");
        Keep(mid, MidiWriter.Write(clip, 120));

        return new LiveFiles
        {
            Folder = folder, Pluck = pluck, Clip = mid,
            Pads = [new(36, kick, "Live Kick"), new(38, snare, "Live Snare"), new(42, hat, "Live Hat")],
        };
    }

    /// <summary>Writes a file unless it's already there as it should be.</summary>
    static void Keep(string path, byte[] bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Deletes the MIDI files earlier runs' sends exported (each run's channel has its own name). Bitwig took their notes
    /// into its clips long ago, so nothing needs them.
    /// </summary>
    static void TidyExports(string folder)
    {
        var exports = new DirectoryInfo(Path.Combine(folder, BitwigLiveTests.TestProject, "MIDI"));
        if (!exports.Exists) return;
        foreach (var f in exports.EnumerateFiles("*.mid"))
        {
            if (DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromHours(1)) continue;
            try { f.Delete(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
