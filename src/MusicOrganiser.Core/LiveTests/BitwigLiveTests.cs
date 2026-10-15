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
/// Each run adds a handful of tracks named with the time it started (“Devices 143012”), then saves.
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
    /// What Save did: Passed when Bitwig's unsaved changes cleared, or when it wrote the project's file (if the app found
    /// it) even though they didn't; else Failed. The file says more than the flag: on Bitwig 6.1.3 a run saw the flag stay
    /// on after Save, with no Save As window, and couldn't tell whether the project was saved.
    /// </summary>
    /// <param name="file">The project's file as the app found it before Save (or after, for a first save), or null.</param>
    /// <param name="before">When <paramref name="file"/> was written before Save (null if it wasn't there), and after the wait.</param>
    /// <param name="front">Whether Bitwig's window was in front when Save ran, or null when that couldn't be told.</param>
    public static LiveVerdict SaveVerdict(string project, string action, bool wasModified, bool stillModified, TimeSpan took, TimeSpan wait,
                                          string? file, DateTime? before, DateTime? after, bool? front = null)
    {
        var written = file is not null && after is { } a && a != before;
        var data = new JsonObject
        {
            ["action"] = action, ["modifiedBefore"] = wasModified, ["modifiedAfter"] = stillModified, ["file"] = file,
            ["writtenBefore"] = Utc(before), ["writtenAfter"] = Utc(after), ["front"] = front,
        };
        var wrote = file is null ? "" : written ? $", and wrote {file}" : $", though {file} wasn't written again";
        if (!stillModified)
            return LiveVerdict.Pass(wasModified
                ? $"Bitwig saved “{project}”: its unsaved changes cleared {Ms(took.TotalMilliseconds)} after Save{wrote}."
                : $"Bitwig ran Save. “{project}” had no unsaved changes before, so this only shows they stayed clear{wrote}.", data);
        if (written)
            return LiveVerdict.Pass($"Bitwig wrote {file} after Save, but still showed unsaved changes {wait.TotalSeconds:0} s later: something " +
                                    $"changed “{project}” again straight away, or Bitwig didn't clear them.", data);
        return LiveVerdict.Fail($"Bitwig still showed unsaved changes {wait.TotalSeconds:0} s after Save (“{action}”)" +
                                (file is null ? ", and the app didn't find the project's file to see whether it was written" : $", and didn't write {file}") +
                                (front switch
                                {
                                    true => ", though its window was in front",
                                    false => ". The app couldn't bring Bitwig's window to the front, which Save may need: click on Bitwig while the run saves",
                                    null => "",
                                }) +
                                $". If Bitwig opened a Save As window, the project hasn't been saved yet: save it as “{TestProject}”, then run again.", data);

        static string? Utc(DateTime? t) => t?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    static string Ms(double ms) => ms >= 1000 ? (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : ms.ToString(ms < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + " ms";

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
        DateTime _savedAt;
        LiveFiles? _files;

        string Stamp => LiveReports.Stamp(_started);
        /// <summary>The time the run started, which names its tracks: "143012".</summary>
        string Tag => Stamp[9..15];
        string DevicesTrack => $"Devices {Tag}";
        string SampleTrack => $"Sample {Tag}";
        string KitTrack => $"Kit {Tag}";

        /// <summary>Whether Bitwig has the Java extension, which gives more than the script (link 3, runtime "java").</summary>
        bool Newer => _link?.Hello is { } h && (h.Link >= 3 || h.Runtime == "java");

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
                    detail += $" It has {p.TrackCount} tracks, and each run adds about six: make a fresh “{TestProject}” project now and then, " +
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
                return LiveVerdict.Pass($"Bitwig made a clip from {Path.GetFileName(Files.Clip)} in launcher slot {slot + 1} of “{SampleTrack}”.",
                                        new JsonObject { ["slot"] = slot });
            });

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
                var wasModified = link.Hello.Modified;
                // The project's file, if the app can find it: whether Save wrote it says more than Bitwig's unsaved changes.
                var file = FindSaved(project, DateTime.MinValue, options.LibraryProjects(), SavedFolders());
                DateTime? Written() => file is not null && File.Exists(file) ? File.GetLastWriteTimeUtc(file) : null;
                var before = Written();
                var front = options.BringBitwigToFront();
                _savedAt = DateTime.UtcNow;
                var took = Stopwatch.StartNew();
                await link.RunActionAsync(_save, t);
                bool modified;
                while ((modified = (await link.HelloAsync(t)).Modified) && took.Elapsed <= options.SaveWait) await Task.Delay(100, t);
                // A project saved for the first time has no file until Save writes one.
                file ??= FindSaved(project, _savedAt.AddSeconds(-2), options.LibraryProjects(), SavedFolders());
                var verdict = SaveVerdict(project, _save, wasModified, modified, took.Elapsed, options.SaveWait, file, before, Written(), front);
                if (verdict.Outcome == LiveOutcome.Passed)
                {
                    _saved = true;
                    _savedProject = project;
                }
                return verdict;
            });

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
                var ago = File.GetLastWriteTimeUtc(path) - _savedAt;
                return Task.FromResult(LiveVerdict.Pass($"{path}, written {Ms(Math.Max(0, ago.TotalMilliseconds))} after Save ran.",
                                                        new JsonObject { ["file"] = path }));
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

        static LiveVerdict Needs(string what) => LiveVerdict.Skip($"This needs {what}, which an earlier test didn't get (see above).");

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
