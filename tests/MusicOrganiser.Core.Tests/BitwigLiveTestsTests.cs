using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Formats;
using MusicOrganiser.Core.LiveTests;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Tests;

/// <summary>
/// The Bitwig live tests, run against the stand-in Bitwig (extensions/bitwig/test/serve.js, with the real script; they need
/// Node.js, and return early without it) and stand-in MO Probes. Everything they touch is in a temp folder: no real DAW,
/// plugin folder or app data folder is reached.
/// </summary>
// Not alongside ProbeKnobCheckTests: both time knob turns, which a machine busy with the other's stand-ins can make late.
[Collection("Knob checks")]
public class BitwigLiveTestsTests
{
    const string StandInDrumMachine = "d7d7d7d7-0000-4000-8000-000000000001";

    /// <summary>A run's every folder in <paramref name="dir"/>, connecting to the stand-in on <paramref name="port"/>.</summary>
    static BitwigLiveOptions Options(string dir, int port, bool probeInstalled = true)
    {
        var plugins = Directory.CreateDirectory(Path.Combine(dir, "plugins")).FullName;
        if (probeInstalled)
        {
            File.WriteAllBytes(Path.Combine(plugins, "MO Probe.clap"), [0]);
            Directory.CreateDirectory(Path.Combine(plugins, "MO Probe.vst3"));
        }
        return new BitwigLiveOptions
        {
            Connect = ct => BitwigLink.ConnectAsync(port, ct: ct),
            ReportFolder = Path.Combine(dir, "reports"),
            ProbeFolder = Directory.CreateDirectory(Path.Combine(dir, "probe")).FullName,
            PluginFolders = () => [plugins],
            BitwigUserFolders = () => [Directory.CreateDirectory(Path.Combine(dir, "Bitwig Studio")).FullName],
            DrumMachine = _ => new Guid(StandInDrumMachine),
            AppVersion = "test",
            // The stand-in probe writes its status every 20 milliseconds, not every half second. A turn that arrives counts at
            // once, so the wait only runs out on one that doesn't; it's long enough for a machine busy with the other tests.
            ProbeStart = TimeSpan.FromSeconds(10),
            ProbeWait = TimeSpan.FromSeconds(3),
            ProbeSettle = TimeSpan.FromMilliseconds(50),
        };
    }

    static LiveResult Find(LiveRun run, string area, string name) => run.Results.Single(r => r.Area == area && r.Name == name);

    static string Listing(BitwigProject p) => string.Join("; ", p.Tracks.Select(t => $"{t.Name}: {string.Join(", ", t.Devices.Select(d => d.Name))}"));

    /// <summary>Whether a line serve.js wrote on stderr (with --events) says Save ran in the stand-in.</summary>
    static bool IsSave(string line) => line.StartsWith('{') && (string?)JsonNode.Parse(line)!["event"] == "save";

    /// <summary>
    /// A count the stand-in's stderr lines keep, once it has reached <paramref name="expected"/> or had 2 seconds to: those
    /// lines come in on their own thread, a moment after what they tell of.
    /// </summary>
    static async Task<int> Settled(Func<int> count, int expected)
    {
        for (var waited = 0; count() < expected && waited < 2000; waited += 20) await Task.Delay(20);
        return count();
    }

    [Fact]
    public void LooksForTheSavedProjectWhereverTheLibrarysBitwigProjectsAre()
    {
        // Seen: Bitwig's extensions in Documents\Bitwig Studio, its projects on another drive, which the library has.
        var dir = Fixtures.TempDir();
        var documents = Directory.CreateDirectory(Path.Combine(dir, "Documents", "Bitwig Studio", "Projects")).Parent!.FullName;
        var projects = Directory.CreateDirectory(Path.Combine(dir, "K", "Bitwig Studio", "Projects")).FullName;
        string[] library =
        [
            Path.Combine(projects, "Night Drive", "Night Drive.bwproject"),
            Path.Combine(projects, "Loop Idea", "Loop Idea.bwproject"),
            Path.Combine(dir, "Exports", "Bounce.bwproject"),   // not in a Projects folder, so its folder isn't searched
        ];
        Assert.Equal([Path.Combine(documents, "Projects"), projects],
                     BitwigLiveTests.ProjectFolders([documents, Path.Combine(dir, "Gone")], library));
    }

    [Fact]
    public async Task RunsEveryAreaInATestProject()
    {
        var dir = Fixtures.TempDir();
        ProbeBridge? bridge = null;
        var saves = 0;
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false, line =>
        {
            bridge?.Heard(line);
            if (IsSave(line)) Interlocked.Increment(ref saves);
        });
        if (bitwig is null) return;
        var options = Options(dir, bitwig.Port);
        await using var probes = bridge = new ProbeBridge(options.ProbeFolder, bitwig);
        bitwig.Change("""{"project": "MO Live Test"}""");
        var seen = new List<LiveProgress>();

        var run = await BitwigLiveTests.RunAsync(options, new Each<LiveProgress>(seen.Add));

        Assert.True(run.Failed == 0, string.Join(Environment.NewLine, run.Results.Where(r => r.Outcome == LiveOutcome.Failed).Select(r => $"{r.Title}: {r.Detail}")));
        Assert.False(run.Cancelled);
        Assert.Equal("Bitwig Studio 6.1.2 · JavaScript script, link 2 · “MO Live Test”", run.Setup);

        // The script has none of the Java extension's newer fields, so those say they need it.
        string[] newer = ["Link › Track IDs", "Link › Parameter details", "Link › Last clicked parameter", "Link › Self-check", "Link › MIDI loopback",
                          "Changes › Clip to arranger", "Changes › MIDI file as a track", "Changes › Sampler's parameter details",
                          "Changes › Master recorder"];
        Assert.All(newer, n => Assert.Equal((LiveOutcome.Skipped, BitwigLiveTests.NeedsJava),
                                            run.Results.Where(r => r.Title == n).Select(r => (r.Outcome, r.Detail)).Single()));
        // The stand-in saves no file, so there's no saved project to compare: skipped, saying so.
        Assert.StartsWith("Bitwig saved “MO Live Test”, but the app didn't find a MO Live Test.bwproject saved since",
                          Find(run, "Saved file", "Find the saved project").Detail);
        Assert.Equal(LiveOutcome.Skipped, Find(run, "Saved file", "Compare with Bitwig").Outcome);
        var skipped = run.Results.Where(r => r.Outcome == LiveOutcome.Skipped).Select(r => r.Title).ToList();
        Assert.Equal([.. newer, "Saved file › Find the saved project", "Saved file › Compare with Bitwig"], skipped);
        Assert.Equal(11, run.Skipped);
        Assert.All(["Link", "Changes", "Send"], area => Assert.DoesNotContain(run.Results, r => r.Area == area && r.Outcome == LiveOutcome.Failed));

        // What each test saw.
        var tag = run.Stamp[9..15];
        Assert.Equal($"Bitwig added track 3, “Devices {tag}”, coloured #3366cc as asked.", Find(run, "Changes", "New track").Detail);
        Assert.Equal("Bitwig set the Sampler to -3.50 st, -3.5 semitones as asked, and read it back.", Find(run, "Changes", "Transpose a Sampler").Detail);
        Assert.Equal("Bitwig set the Note Transpose to +5 st, +5 semitones as asked, and read it back.", Find(run, "Changes", "Transpose a Note Transpose").Detail);
        Assert.Equal("Bitwig put each sample on its pad: Live Kick on C1, Live Snare on D1, Live Hat on F#1.", Find(run, "Changes", "Drum pads").Detail);
        Assert.StartsWith("Set “Gain” on the Sampler from 0.5 (“0.0 dB”) to 0.25, and Bitwig showed 0.25", Find(run, "Changes", "Set a parameter").Detail);
        var save = Find(run, "Changes", "Save");
        Assert.StartsWith("Bitwig saved “MO Live Test” when the link ran Save straight away: its unsaved changes cleared", save.Detail);
        // The script runs Save straight away only, so that's the one route tried, and it said which project was in front.
        Assert.EndsWith(OnlyDirect, save.Detail);
        Assert.Equal("MO Live Test", (string?)save.Data!["projectAtSave"]);
        Assert.Equal(("direct", 1), ((string?)save.Data["via"], save.Data["attempts"]!.AsArray().Count));
        Assert.Equal(1, await Settled(() => saves, 1));
        Assert.StartsWith($"“Send {tag}”, a sample on root key A4, went in as a Sampler tuned up 3 semitones", Find(run, "Send", "FL Studio channel").Detail);

        // MO Probe, both ways in: as a VST3 it doesn't know its track (as through the wrapper), so the run says where it put it.
        foreach (var format in new[] { "CLAP", "VST3" })
        {
            Assert.Equal(LiveOutcome.Passed, Find(run, "MO Probe", $"Knob check ({format})").Outcome);
            Assert.StartsWith("Every knob moved both ways, and each stepped knob landed on its step. MO Probe Instrument on “Probe " + format,
                              Find(run, "MO Probe", $"Knob check ({format})").Detail);
            var mapping = Find(run, "MO Probe", $"Knob mapping ({format})");
            Assert.Equal(LiveOutcome.Noted, mapping.Outcome);
            Assert.Equal($"How Bitwig maps MO Probe's knobs ({format}): it spreads 0 to 1 evenly over each knob's range, both ways, in all 64 turns.", mapping.Detail);
            Assert.Equal(10, mapping.Lines.Count);   // a heading, a rule and a row for each knob
            Assert.Contains("| MO Gain | 0 → -60.0 dB, 0.25 → -42.0 dB, 0.5 → -24.0 dB, 1 → 12.0 dB | -60.0 dB → 0.000, -42.0 dB → 0.250, -24.0 dB → 0.500, " +
                            "12.0 dB → 1.000 |", mapping.Lines);
            Assert.Equal($"{BitwigLiveTests.TestProject} {run.Stamp} {format}", probes.Probe(format).Label);
        }
        Assert.EndsWith("Bitwig didn't tell it which track it's on, so the app goes by the track it put it on.", Find(run, "MO Probe", "Live status (VST3)").Detail);
        Assert.All(MoProbe.Knobs, k => Assert.Equal(k.Default, probes.Probe("CLAP")[k.Id]));   // put back as they were

        // Progress came as each test started and ended.
        Assert.Equal(run.Results.Count, seen.Count(p => p.Result is not null));
        Assert.Equal(run.Results.Select(r => r.Title), seen.Where(p => p.Result is not null).Select(p => p.Result!.Title));

        // The stand-in now has the run's tracks, saved.
        using var link = await BitwigLink.ConnectAsync(bitwig.Port);
        var project = await link.ProjectAsync();
        Assert.Equal($"Drums: ; Kick: EQ+; Bass: Serum, Compressor; Devices {tag}: Note Transpose, Sampler; Sample {tag}: Sampler; Kit {tag}: Drum Machine; " +
                     $"Probe CLAP {tag}: MO Probe Instrument; Probe VST3 {tag}: MO Probe Instrument; Send {tag}: Sampler; Master: Peak Limiter", Listing(project));
        Assert.False(project.Modified);
        Assert.True(File.Exists(Path.Combine(dir, "reports", "files", "Live Pluck.wav")));

        // The reports, in the folder given.
        Assert.Equal(Path.Combine(dir, "reports", $"{run.Stamp}-bitwig.md"), run.ReportMarkdown);
        var json = JsonNode.Parse(File.ReadAllText(run.ReportJson!))!;
        Assert.Equal(("MO Live Test", "test", run.Results.Count), ((string)json["hello"]!["project"]!, (string)json["app"]!["version"]!, json["tests"]!.AsArray().Count));
        Assert.Equal(run.Passed, (int)json["counts"]!["passed"]!);
        var roundTrip = json["tests"]!.AsArray().Single(t => (string)t!["name"]! == "Round trip")!;
        Assert.Equal(20, roundTrip["data"]!["ms"]!.AsArray().Count);
        var actions = json["tests"]!.AsArray().Single(t => (string)t!["name"]! == "Actions")!["data"]!["actions"]!.AsArray();
        Assert.Contains(actions, a => (string)a!["id"]! == "Save" && (string)a["category"]! == "File");
        var md = File.ReadAllText(run.ReportMarkdown!);
        Assert.Contains($"**{run.Summary}**", md);
        Assert.DoesNotContain("## Failed", md);
        Assert.Contains("### • MO Probe › Knob mapping (CLAP)", md);
        Assert.Contains("| Knob | Bitwig → probe (0 to 1 sent → value the probe got) | Probe → Bitwig (value set → Bitwig's 0 to 1) |", md);
    }

    [Fact]
    public async Task ChangesNothingInAProjectThatIsntForTheTests()
    {
        var dir = Fixtures.TempDir();
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        if (bitwig is null) return;
        using var link = await BitwigLink.ConnectAsync(bitwig.Port);
        var before = Listing(await link.ProjectAsync());

        var run = await BitwigLiveTests.RunAsync(Options(dir, bitwig.Port));

        // Only Link ran (it only reads); everything else says why not, and what to do.
        Assert.Equal(0, run.Failed);
        Assert.All(run.Results.Where(r => r.Area == "Link" && !r.Name.StartsWith("Track IDs") && !r.Name.StartsWith("Param") &&
                                          !r.Name.StartsWith("Last") && !r.Name.StartsWith("Self") && !r.Name.StartsWith("MIDI")),
                   r => Assert.Equal(LiveOutcome.Passed, r.Outcome));
        var changes = run.Results.Where(r => r.Area != "Link").ToList();
        Assert.Equal(29, changes.Count);
        Assert.All(changes, r => Assert.Equal(LiveOutcome.Skipped, r.Outcome));
        Assert.Equal("Bitwig has “Night Drive” open, so the run changed nothing: it only changes a project whose name starts with “MO Live Test”, " +
                     "never your own work. " + BitwigLiveTests.HowToAllowChanges, Find(run, "Changes", "Changes allowed").Detail);
        Assert.All(changes.Where(r => r.Area != "Saved file"), r => Assert.Equal(Find(run, "Changes", "Changes allowed").Detail, r.Detail));

        var after = await link.ProjectAsync();
        Assert.Equal(before, Listing(after));
        Assert.False(after.Modified);
        Assert.False(Directory.Exists(Path.Combine(dir, "reports", "files")));   // nothing written to send in
        Assert.True(File.Exists(run.ReportMarkdown));
    }

    [Fact]
    public async Task ChangesNothingWhileBitwigDoesntAllowChanges()
    {
        var dir = Fixtures.TempDir();
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        if (bitwig is null) return;
        bitwig.Change("""{"project": "MO Live Test 2", "changes": false}""");
        using var link = await BitwigLink.ConnectAsync(bitwig.Port);
        var before = Listing(await link.ProjectAsync());

        var run = await BitwigLiveTests.RunAsync(Options(dir, bitwig.Port));

        Assert.Equal(0, run.Failed);
        Assert.Equal("Bitwig isn't letting the app make changes, so the run changed nothing. " + BitwigLiveTests.HowToAllowChanges,
                     Find(run, "Changes", "Changes allowed").Detail);
        Assert.All(run.Results.Where(r => r.Area != "Link"), r => Assert.Equal(LiveOutcome.Skipped, r.Outcome));
        Assert.Equal(before, Listing(await link.ProjectAsync()));
        Assert.Contains("Skipped, as above.", File.ReadAllText(run.ReportMarkdown!));
    }

    [Fact]
    public async Task NeverChangesAnotherProjectOpenedPartWayThroughATest()
    {
        var dir = Fixtures.TempDir();
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        if (bitwig is null) return;
        bitwig.Change("""{"project": "MO Live Test"}""");
        // Another project comes to the front while the run looks for the Drum Machine, after the test's first check.
        var options = Options(dir, bitwig.Port, probeInstalled: false) with
        {
            DrumMachine = _ =>
            {
                bitwig.Change("""{"project": "Night Drive"}""");
                return new Guid(StandInDrumMachine);
            },
        };

        var run = await BitwigLiveTests.RunAsync(options);

        var drum = Find(run, "Changes", "Drum Machine by ID");
        Assert.Equal(LiveOutcome.Skipped, drum.Outcome);
        Assert.StartsWith("Stopped part way through: Bitwig has “Night Drive” open, so the run made no more changes: it only changes a project " +
                          "whose name starts with “MO Live Test”", drum.Detail);
        Assert.All(run.Results.SkipWhile(r => r != drum).Skip(1), r => Assert.Equal(LiveOutcome.Skipped, r.Outcome));
        using var link = await BitwigLink.ConnectAsync(bitwig.Port);
        Assert.DoesNotContain($"Kit {run.Stamp[9..15]}", Listing(await link.ProjectAsync()));   // the track it was about to add
    }

    [Fact]
    public async Task StopsWhenAnotherProjectComesToTheFrontWhileBitwigMakesAChange()
    {
        var dir = Fixtures.TempDir();
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        if (bitwig is null) return;
        bitwig.Change("""{"project": "MO Live Test"}""");
        // After the app's own check, while Bitwig is adding the run's first track.
        bitwig.Change("""{"switchDuring": "newtrack", "project": "Night Drive"}""");

        var run = await BitwigLiveTests.RunAsync(Options(dir, bitwig.Port, probeInstalled: false));

        var track = Find(run, "Changes", "New track");
        Assert.Equal(LiveOutcome.Skipped, track.Outcome);
        Assert.StartsWith("Stopped part way through: Bitwig has “Night Drive” open, so the run made no more changes", track.Detail);
        Assert.All(run.Results.SkipWhile(r => r != track).Skip(1), r => Assert.Equal(LiveOutcome.Skipped, r.Outcome));
        using var link = await BitwigLink.ConnectAsync(bitwig.Port);
        Assert.DoesNotContain($"Devices {run.Stamp[9..15]}", Listing(await link.ProjectAsync()));   // never named
    }

    [Fact]
    public async Task ChangesNothingThroughALinkThatCantKeepChangesInTheTestProject()
    {
        var dir = Fixtures.TempDir();
        // As an extension from before onlyIn would say hello: changes on, the test project open, but no "onlyIn".
        await using var old = new FakeJavaBitwig("""{"link":3,"product":"Bitwig Studio","version":"6.1.3","api":23,"platform":"WINDOWS","port":19791,"project":"MO Live Test","modified":false,"engine":true,"changes":true,"runtime":"java","extension":"3.0"}""");

        var run = await BitwigLiveTests.RunAsync(Options(dir, old.Port));

        Assert.Equal("The Music Organiser extension in Bitwig is older than the app, and can't make sure each change stays in “MO Live Test”, so the run " +
                     "changed nothing. " + BitwigLiveTests.Reinstall, Find(run, "Changes", "Changes allowed").Detail);
        Assert.All(run.Results.Where(r => r.Area != "Link"), r => Assert.Equal(LiveOutcome.Skipped, r.Outcome));
    }

    [Fact]
    public async Task KeepsWhatTheKnobCheckFoundWhenStoppedDuringIt()
    {
        var dir = Fixtures.TempDir();
        ProbeBridge? bridge = null;
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false, line => bridge?.Heard(line));
        if (bitwig is null) return;
        var options = Options(dir, bitwig.Port);
        await using var probes = bridge = new ProbeBridge(options.ProbeFolder, bitwig);
        bitwig.Change("""{"project": "MO Live Test"}""");
        using var stop = new CancellationTokenSource();
        var progress = new Each<LiveProgress>(p =>
        {
            if (p is { Name: "Knob check (CLAP)", Result: null }) stop.CancelAfter(TimeSpan.FromSeconds(1));
        });

        var run = await BitwigLiveTests.RunAsync(options, progress, stop.Token);

        var check = Find(run, "MO Probe", "Knob check (CLAP)");
        Assert.Equal(LiveOutcome.Skipped, check.Outcome);
        Assert.Matches(@"^The run was stopped during this test\. \d+ turns? had been made\.$", check.Detail);
        Assert.NotNull(check.Data);
        var mapping = Find(run, "MO Probe", "Knob mapping (CLAP)");
        Assert.Equal(LiveOutcome.Noted, mapping.Outcome);
        Assert.EndsWith("The check stopped part way, so these are only the turns it made.", mapping.Detail);
        Assert.All(MoProbe.Knobs, k => Assert.Equal(k.Default, probes.Probe("CLAP")[k.Id]));   // put back as they were
    }

    [Fact]
    public async Task SaysHowToGetMoProbeWhenItIsntInstalled()
    {
        var dir = Fixtures.TempDir();
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        if (bitwig is null) return;
        bitwig.Change("""{"project": "MO Live Test"}""");

        var run = await BitwigLiveTests.RunAsync(Options(dir, bitwig.Port, probeInstalled: false));

        Assert.Equal(0, run.Failed);
        var probe = run.Results.Where(r => r.Area == "MO Probe").ToList();
        Assert.Equal(10, probe.Count);
        Assert.All(probe, r => Assert.Equal(LiveOutcome.Skipped, r.Outcome));
        Assert.Equal($"MO Probe isn't installed as a CLAP. To test it: copy “MO Probe.clap” from a build (native/README.md, “MO Probe”, says how) " +
                     $"into your CLAP plugin folder, rescan plugins in Bitwig, then run again.", Find(run, "MO Probe", "Insert MO Probe (CLAP)").Detail);
        Assert.StartsWith("This needs MO Probe (CLAP) on a track", Find(run, "MO Probe", "Live status (CLAP)").Detail);
        Assert.Equal(Find(run, "MO Probe", "Knob check (VST3)").Detail, Find(run, "MO Probe", "Knob mapping (VST3)").Detail);
        Assert.Equal(LiveOutcome.Passed, Find(run, "Send", "FL Studio channel").Outcome);
    }

    /// <summary>What a Save verdict ends with when the link in Bitwig runs Save straight away only.</summary>
    const string OnlyDirect = " Only Save straight away was tried: the other routes (from Bitwig's flush, and through a MIDI loopback) need the " +
                              "Music Organiser extension 3.7 or later.";

    [Fact]
    public async Task SavesStraightAwayOnlyThroughTheScript()
    {
        var dir = Fixtures.TempDir();
        var saves = 0;
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false, line =>
        {
            if (IsSave(line)) Interlocked.Increment(ref saves);
        });
        if (bitwig is null) return;
        bitwig.Change("""{"project": "MO Live Test"}""");
        var options = Options(dir, bitwig.Port, probeInstalled: false) with { SaveWait = TimeSpan.FromSeconds(2) };

        // Bitwig ignores Save, as Bitwig 6.1.3 did: the script has no other route, so that's Failed, saying the other routes
        // need the extension 3.7, and that the test project was in front as Save ran.
        bitwig.Change("""{"ignoreSaves": 1}""");
        var run = await BitwigLiveTests.RunAsync(options);
        var save = Find(run, "Changes", "Save");
        Assert.Equal(LiveOutcome.Failed, save.Outcome);
        Assert.StartsWith("Bitwig still showed unsaved changes 2 s after Save (“Save”), and the app didn't find the project's file", save.Detail);
        Assert.Contains(OnlyDirect + " Bitwig said the test project, “MO Live Test”, was the active project when Save ran.", save.Detail);
        Assert.Equal(["- Straight away (direct): ran, but Bitwig still showed unsaved changes 2 s later"], save.Lines);
        Assert.Equal(1, await Settled(() => saves, 1));
        Assert.Equal("Needs Changes › Save, which didn't work.", Find(run, "Saved file", "Find the saved project").Detail);
    }

    /// <summary>The extension 3.7's hello, with a test project open and changes allowed: unsaved, unless <paramref name="modified"/> is false.</summary>
    static string RoutesHello(bool modified = true, string extension = "3.7", bool loopback = true) =>
        FakeJavaBitwig.Hello($$"""{"project":"MO Live Test","modified":{{(modified ? "true" : "false")}},"changes":true,"extension":"{{extension}}","loopback":{{(loopback ? "true" : "false")}}}""");

    /// <summary>The extension's answer to an action: the test project in front, the route, and whether it ran.</summary>
    static string ActionRun(string? via, bool ran) => $$"""{"project":"MO Live Test","via":"{{via}}","ran":{{(ran ? "true" : "false")}}}""";

    /// <summary>The routes the actions a stand-in Bitwig was sent asked for.</summary>
    static List<string?> ActionRoutes(FakeJavaBitwig java) =>
        java.Requests.Where(r => (string)r["cmd"]! == "action").Select(r => (string?)r["via"]).ToList();

    [Fact]
    public async Task SavesByTheFirstRouteThatWorks()
    {
        // Bitwig ignores Save straight away and from the flush, the loopback doesn't bring the link's own MIDI message back,
        // and Save on a press of the link's own button clears the unsaved changes.
        var saved = 0;
        string? Answer(JsonNode request)
        {
            switch ((string)request["cmd"]!)
            {
                case "hello":
                    return RoutesHello(modified: Volatile.Read(ref saved) == 0);
                case "action":
                    var via = (string?)request["via"];
                    if (via == "buttonCode") Interlocked.Exchange(ref saved, 1);
                    return ActionRun(via, via != "midi");
                default:
                    return null;
            }
        }
        await using var java = new FakeJavaBitwig(RoutesHello()) { Answer = Answer };
        using var link = await BitwigLink.ConnectAsync(java.Port);
        var front = 0;
        var saving = new List<DateTime>();

        var verdict = await BitwigLiveTests.SaveByRoutesAsync(link, "Save", TimeSpan.FromMilliseconds(300), _ => null, () => { front++; return true; },
                                                              saving: saving.Add);

        Assert.Equal(LiveOutcome.Passed, verdict.Outcome);
        Assert.StartsWith("Bitwig saved “MO Live Test” when the link ran Save for a press of its own hardware button: its unsaved changes cleared ",
                          verdict.Detail);
        // Told as each Save went out, the last being the one that worked, after the two routes that ran were waited for.
        Assert.Equal(4, saving.Count);
        Assert.True(saving[3] - saving[0] >= TimeSpan.FromMilliseconds(600), $"{saving[3] - saving[0]}");
        Assert.EndsWith(" after Save. The routes before it didn't work: Save run straight away (direct) and from Bitwig's flush (flush) left the " +
                        "unsaved changes; Save on the link's own MIDI message (midi) didn't run.", verdict.Detail);
        Assert.Equal(1, front);
        // The routes in order, stopping at the one that worked: the button Bitwig runs Save for wasn't tried.
        Assert.Equal(["direct", "flush", "midi", "buttonCode"], ActionRoutes(java));
        // The route that didn't run wasn't waited for: the next Save went straight after it.
        var sent = java.Requests.ToList();
        var midi = sent.FindIndex(r => (string?)r["via"] == "midi");
        Assert.Equal("buttonCode", (string?)sent[midi + 1]["via"]);

        var data = verdict.Data!;
        Assert.Equal(("buttonCode", "MO Live Test", false), ((string?)data["via"], (string?)data["projectAtSave"], (bool)data["modifiedAfter"]!));
        var attempts = data["attempts"]!.AsArray();
        Assert.Equal([("direct", true, false), ("flush", true, false), ("midi", false, false), ("buttonCode", true, true)],
                     attempts.Select(a => ((string)a!["via"]!, (bool)a["ran"]!, (bool)a["cleared"]!)));
        Assert.All(attempts, a => Assert.Equal(("MO Live Test", false), ((string?)a!["project"], (bool)a["written"]!)));
        Assert.True((double)attempts[0]!["tookMs"]! >= 300, "a route that ran is given the whole wait");
        Assert.True((double)attempts[2]!["tookMs"]! < 300, "a route that didn't run isn't");
    }

    [Fact]
    public async Task SaysWhatEachRouteDidWhenNoneSaves()
    {
        // The loopback isn't set up, so the routes through it don't run, and Bitwig ignores the others.
        await using var java = new FakeJavaBitwig(RoutesHello(loopback: false));
        java.Answer = request => (string)request["cmd"]! == "action"
            ? ActionRun((string?)request["via"], (string?)request["via"] is "direct" or "flush")
            : null;
        using var link = await BitwigLink.ConnectAsync(java.Port);

        var verdict = await BitwigLiveTests.SaveByRoutesAsync(link, "Save", TimeSpan.FromMilliseconds(200), _ => null, () => null);

        Assert.Equal(LiveOutcome.Failed, verdict.Outcome);
        Assert.Equal(["direct", "flush", "midi", "buttonCode", "button"], ActionRoutes(java));
        Assert.Contains(" The link tried Save 5 ways, one after another, and none worked: straight away (direct), from Bitwig's flush (flush), on the " +
                        "link's own MIDI message (midi), on a press of the link's own button, run by the link (buttonCode) and by the link's own " +
                        "hardware button, run by Bitwig (button). The routes through the MIDI loopback didn't run. Bitwig 6.1.3 has only saved for " +
                        "the extension once its MIDI in and out were set (from its flush, and once straight away), so set them whichever route " +
                        "the app uses. " + BitwigLiveTests.LoopbackSetup,
                        verdict.Detail);
        Assert.Equal(["- Straight away (direct): ran, but Bitwig still showed unsaved changes 0 s later",
                      "- From Bitwig's flush (flush): ran, but Bitwig still showed unsaved changes 0 s later",
                      "- On the link's own MIDI message (midi): didn't run, as nothing came back through the loopback within a second",
                      "- On a press of the link's own button, run by the link (buttonCode): didn't run, as nothing came back through the loopback within a second",
                      "- By the link's own hardware button, run by Bitwig (button): didn't run, as nothing came back through the loopback within a second"],
                     verdict.Lines);
        Assert.Null((string?)verdict.Data!["via"]);
        Assert.Equal(5, verdict.Data["attempts"]!.AsArray().Count);
    }

    [Fact]
    public async Task NotesARouteTheLinkRefusesUnlessTheRunMayNoLongerChangeTheProject()
    {
        const string noPorts = "This Bitwig didn't let the link set up its MIDI ports and buttons, so it can't run a command via midi. A selftest says why.";
        await using var java = new FakeJavaBitwig(RoutesHello());
        java.Answer = request => (string)request["cmd"]! == "action" ? ActionRun((string?)request["via"], true) : null;
        java.Refuse = request => (string?)request["via"] == "midi" ? noPorts : null;
        using var link = await BitwigLink.ConnectAsync(java.Port);

        // Refused while the run may still change the project: noted, and the next route tried.
        var verdict = await BitwigLiveTests.SaveByRoutesAsync(link, "Save", TimeSpan.FromMilliseconds(100), _ => null, () => true,
                                                              closed: () => Task.FromResult(false));
        Assert.Equal(LiveOutcome.Failed, verdict.Outcome);
        Assert.Equal(["direct", "flush", "midi", "buttonCode", "button"], ActionRoutes(java));
        Assert.Equal("- On the link's own MIDI message (midi): the link refused it: " + noPorts, verdict.Lines[2]);
        Assert.Equal(noPorts, (string?)verdict.Data!["attempts"]![2]!["error"]);
        Assert.Null(verdict.Data["attempts"]![1]!["error"]);
        // The routes through the loopback all ran but midi, which was refused: so they didn't all run.
        Assert.Contains(" The routes through the MIDI loopback didn't run. ", verdict.Detail);

        // Refused once the run may no longer change the project (another project came to the front, say): the test stops there.
        var e = await Assert.ThrowsAsync<DawLinkException>(() => BitwigLiveTests.SaveByRoutesAsync(
            link, "Save", TimeSpan.FromMilliseconds(100), _ => null, () => true, closed: () => Task.FromResult(true)));
        Assert.Equal(noPorts, e.Message);
    }

    const string AppsOwnSave = " That's the way the link runs a command when the app names no route, so the app's own Save works the same way.";

    [Fact]
    public async Task SavesFromTheFlushFirstThroughTheExtension38()
    {
        // The extension 3.8 runs Save from Bitwig's flush when the app names no route, and Bitwig saves there.
        var saved = 0;
        string? Answer(JsonNode request)
        {
            switch ((string)request["cmd"]!)
            {
                case "hello":
                    return RoutesHello(modified: Volatile.Read(ref saved) == 0, extension: "3.8");
                case "action":
                    Interlocked.Exchange(ref saved, 1);
                    return ActionRun((string?)request["via"] ?? "flush", true);
                default:
                    return null;
            }
        }
        await using var java = new FakeJavaBitwig(RoutesHello(extension: "3.8")) { Answer = Answer };
        using var link = await BitwigLink.ConnectAsync(java.Port);

        var verdict = await BitwigLiveTests.SaveByRoutesAsync(link, "Save", TimeSpan.FromMilliseconds(300), _ => null, () => true);

        Assert.Equal(LiveOutcome.Passed, verdict.Outcome);
        Assert.StartsWith("Bitwig saved “MO Live Test” when the link ran Save from Bitwig's flush: its unsaved changes cleared ", verdict.Detail);
        Assert.EndsWith(AppsOwnSave, verdict.Detail);
        // One Save, naming no route, as the app's own Save does.
        Assert.Equal(new string?[] { null }, ActionRoutes(java));
        Assert.False(java.Requests.Single(r => (string)r["cmd"]! == "action").AsObject().ContainsKey("via"));
        Assert.Equal("flush", (string?)verdict.Data!["via"]);
    }

    [Fact]
    public async Task TriesTheOtherRoutesAfterTheFlushThroughTheExtension38()
    {
        // Bitwig ignores Save by every route.
        await using var java = new FakeJavaBitwig(RoutesHello(extension: "3.8"));
        java.Answer = request => (string)request["cmd"]! == "action" ? ActionRun((string?)request["via"] ?? "flush", true) : null;
        using var link = await BitwigLink.ConnectAsync(java.Port);

        var verdict = await BitwigLiveTests.SaveByRoutesAsync(link, "Save", TimeSpan.FromMilliseconds(100), _ => null, () => true);

        Assert.Equal(LiveOutcome.Failed, verdict.Outcome);
        Assert.Equal([null, "direct", "midi", "buttonCode", "button"], ActionRoutes(java));
        Assert.Equal(["flush", "direct", "midi", "buttonCode", "button"], verdict.Data!["attempts"]!.AsArray().Select(a => (string?)a!["via"]));
        Assert.Equal("- From Bitwig's flush (flush): ran, but Bitwig still showed unsaved changes 0 s later", verdict.Lines[0]);
        Assert.DoesNotContain(AppsOwnSave, verdict.Detail);

        // Saved by a route the app has to name: nothing said about the app's own Save.
        java.Requests.Clear();
        var saved = 0;
        string? SavesStraightAway(JsonNode request)
        {
            switch ((string)request["cmd"]!)
            {
                case "hello":
                    return RoutesHello(modified: Volatile.Read(ref saved) == 0, extension: "3.8");
                case "action":
                    var via = (string?)request["via"];
                    if (via == "direct") Interlocked.Exchange(ref saved, 1);
                    return ActionRun(via ?? "flush", true);
                default:
                    return null;
            }
        }
        java.Answer = SavesStraightAway;
        verdict = await BitwigLiveTests.SaveByRoutesAsync(link, "Save", TimeSpan.FromMilliseconds(100), _ => null, () => true);
        Assert.Equal(LiveOutcome.Passed, verdict.Outcome);
        Assert.Equal([null, "direct"], ActionRoutes(java));
        Assert.DoesNotContain(AppsOwnSave, verdict.Detail);
    }

    [Fact]
    public async Task SavesStraightAwayOnlyThroughAnExtensionOlderThan37()
    {
        // The extension 3.6 knows no route, so the run sends none, and says the other routes need 3.7.
        await using var java = new FakeJavaBitwig(RoutesHello(extension: "3.6"),
                                                  new Dictionary<string, string> { ["action"] = """{"project":"MO Live Test"}""" });
        using var link = await BitwigLink.ConnectAsync(java.Port);

        var verdict = await BitwigLiveTests.SaveByRoutesAsync(link, "Save", TimeSpan.FromMilliseconds(200), _ => null, () => true);

        Assert.Equal(LiveOutcome.Failed, verdict.Outcome);
        Assert.Equal(new string?[] { null }, ActionRoutes(java));
        Assert.False(java.Requests.Single(r => (string)r["cmd"]! == "action").AsObject().ContainsKey("via"));
        Assert.Contains(", though its window was in front." + OnlyDirect + " Bitwig said the test project", verdict.Detail);
        Assert.DoesNotContain("loopMIDI", verdict.Detail);
        Assert.Equal("direct", (string?)verdict.Data!["attempts"]![0]!["via"]);
    }

    [Fact]
    public void TimesTheSavedFileFromTheSaveThatWorked()
    {
        const string path = "/p/MO Live Test/MO Live Test.bwproject";
        var first = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        // Four routes were each waited for before the fifth saved: the file is timed from the Save that worked.
        var worked = first.AddSeconds(61);
        var found = BitwigLiveTests.FoundSaved(path, worked.AddMilliseconds(180), first, worked);
        Assert.Equal((LiveOutcome.Passed, path + ", written 180 ms after Save ran."), (found.Outcome, found.Detail));
        Assert.Equal(path, (string?)found.Data!["file"]);
        // The first Save worked.
        Assert.Equal(path + ", written 1.2 s after Save ran.", BitwigLiveTests.FoundSaved(path, first.AddMilliseconds(1200), first, first).Detail);
        // A file system that keeps times to the nearest 2 seconds may put it a moment before.
        Assert.Equal(path + ", written 0 ms after Save ran.", BitwigLiveTests.FoundSaved(path, worked.AddSeconds(-1.5), first, worked).Detail);
        // Written well before the Save that worked went out: by an earlier route's Save, later than the run waited for it.
        Assert.Equal(path + ", written 16.0 s after the run's first Save (the Save that worked went out 61.0 s after the first).",
                     BitwigLiveTests.FoundSaved(path, first.AddSeconds(16), first, worked).Detail);
    }

    [Fact]
    public void LoopbackSaysWhetherTheExtensionsOwnMidiCameBack()
    {
        BitwigHello Hello(string extension, bool? loopback) =>
            new(3, "Bitwig Studio", "6.1.3", 23, "WINDOWS", 19791, "MO Live Test", false, true, true, "java", extension, true, null, loopback);

        var works = BitwigLiveTests.LoopbackVerdict(Hello("3.7", true));
        Assert.Equal(LiveOutcome.Passed, works.Outcome);
        Assert.Equal("The extension's own MIDI came back to it through the loopback, so it can run Bitwig's commands on its own MIDI messages and by " +
                     "pressing its own buttons.", works.Detail);
        Assert.Equal(true, (bool?)works.Data!["loopback"]);

        var broken = BitwigLiveTests.LoopbackVerdict(Hello("3.7", false));
        Assert.Equal(LiveOutcome.Failed, broken.Outcome);
        Assert.Equal("The extension's own MIDI hasn't come back to it through the loopback, so its MIDI in and out aren't set to “Music Organiser " +
                     "Bitwig”, or that port isn't working. The routes through MIDI need them, and Bitwig 6.1.3 has only saved for the extension once " +
                     "they were set: without them it ignored Save run straight away. " + BitwigLiveTests.LoopbackSetup, broken.Detail);
        // The setup names the loopMIDI port, and warns off FL Studio's.
        Assert.Contains("set both MIDI in and MIDI out to “Music Organiser Bitwig” (the loopMIDI port), never “Music Organiser” (FL Studio's)",
                        BitwigLiveTests.LoopbackSetup);

        // An older extension doesn't have the loopback.
        Assert.Equal((LiveOutcome.Skipped, "This needs the Music Organiser extension 3.7 or later, and Bitwig runs 3.6. " + BitwigLiveTests.Reinstall),
                     (BitwigLiveTests.LoopbackVerdict(Hello("3.6", null)).Outcome, BitwigLiveTests.LoopbackVerdict(Hello("3.6", null)).Detail));
        Assert.Equal(LiveOutcome.Skipped, BitwigLiveTests.LoopbackVerdict(Hello("", null)).Outcome);
    }

    [Fact]
    public async Task SaysWhenBitwigIsntAnswering()
    {
        var dir = Fixtures.TempDir();
        var run = await BitwigLiveTests.RunAsync(Options(dir, 0) with
        {
            Connect = _ => Task.FromException<BitwigLink>(new DawLinkException(BitwigLink.NotRunning)),
        });

        Assert.Equal((LiveOutcome.Failed, BitwigLink.NotRunning), (run.Results[0].Outcome, run.Results[0].Detail));
        Assert.All(run.Results.Skip(1).Where(r => r.Area != "Saved file"),
                   r => Assert.Equal("Bitwig isn't answering (see Link › Hello), so this wasn't tried.", r.Detail));
        Assert.Equal("Bitwig didn't answer.", run.Setup);
        Assert.Null(JsonNode.Parse(File.ReadAllText(run.ReportJson!))!["hello"]);
    }

    [Fact]
    public async Task StopsWhenAskedAndStillReports()
    {
        var dir = Fixtures.TempDir();
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        if (bitwig is null) return;
        bitwig.Change("""{"project": "MO Live Test"}""");
        using var stop = new CancellationTokenSource();
        var progress = new Each<LiveProgress>(p =>
        {
            if (p is { Name: "New track", Result: not null }) stop.Cancel();
        });

        var run = await BitwigLiveTests.RunAsync(Options(dir, bitwig.Port), progress, stop.Token);

        Assert.True(run.Cancelled);
        Assert.StartsWith("Stopped part way: ", run.Summary);
        var after = run.Results.SkipWhile(r => r.Name != "New track").Skip(1).ToList();
        Assert.NotEmpty(after);
        Assert.All(after, r => Assert.Equal((LiveOutcome.Skipped, LiveRunner.StoppedBefore), (r.Outcome, r.Detail)));
        Assert.True(File.Exists(run.ReportMarkdown));
    }

    [Fact]
    public async Task ChecksTheJavaExtensionsNewerFields()
    {
        var dir = Fixtures.TempDir();
        await using var java = new FakeJavaBitwig();

        var run = await BitwigLiveTests.RunAsync(Options(dir, java.Port));

        Assert.Equal("Bitwig Studio 6.1.3 · Java extension 3.0, link 3 · “Night Drive”", run.Setup);
        Assert.Equal("java", (string)run.Hello!["runtime"]!);
        Assert.Equal((LiveOutcome.Passed, "All 2 tracks came with an ID, each different (“Bass” is 7f2c)."),
                     (Find(run, "Link", "Track IDs").Outcome, Find(run, "Link", "Track IDs").Detail));
        Assert.Equal("All 2 of “Serum”'s remote controls (page “Main”) say their steps, origin and whether they're automated: 1 stepped " +
                     "(“Mode”: Saw, Square, Noise), 1 automated. The link matched 1 of them to its parameter.", Find(run, "Link", "Parameter details").Detail);
        Assert.Equal("The last parameter clicked in Bitwig is “Cutoff”, at 0.25 (“440 Hz”).", Find(run, "Link", "Last clicked parameter").Detail);
        var self = Find(run, "Link", "Self-check");
        Assert.Equal((LiveOutcome.Failed, "1 of the extension's 3 own checks failed: “reports folder”. Its report: C:\\reports\\bitwig\\x-selfcheck.json."),
                     (self.Outcome, self.Detail));
        Assert.Equal(["- reports folder: Access is denied."], self.Lines);
        // The extension 3.0 has no MIDI loopback.
        Assert.Equal((LiveOutcome.Skipped, "This needs the Music Organiser extension 3.7 or later, and Bitwig runs 3.0. " + BitwigLiveTests.Reinstall),
                     (Find(run, "Link", "MIDI loopback").Outcome, Find(run, "Link", "MIDI loopback").Detail));
        // Changes are off there, so the rest is skipped.
        Assert.All(run.Results.Where(r => r.Area != "Link"), r => Assert.Equal(LiveOutcome.Skipped, r.Outcome));

        // The extension 3.7 says whether its own MIDI came back through the loopback: here it didn't, so the run says how to set it up.
        await using var newer = new FakeJavaBitwig(FakeJavaBitwig.Hello("""{"extension":"3.7","loopback":false}"""));
        run = await BitwigLiveTests.RunAsync(Options(Fixtures.TempDir(), newer.Port));
        var loopback = Find(run, "Link", "MIDI loopback");
        Assert.Equal(LiveOutcome.Failed, loopback.Outcome);
        Assert.EndsWith(BitwigLiveTests.LoopbackSetup, loopback.Detail);
    }

    [Fact]
    public void ComparesTheSavedFileWithWhatBitwigShows()
    {
        var path = Fixtures.BitwigProject(Fixtures.TempDir());
        var read = Readers.Bitwig.Read(path);
        static BitwigTrack Track(int i, string name, string type, params string[] devices) =>
            new(i, name, type, false, "#000000", true, false, false, false, "0.0 dB", 0.8, "C", 0.5, devices.Length,
                devices.Select((d, n) => new BitwigDevice(n, d, "", "", false, true)).ToList());
        var tracks = read.Tracks.Select((t, i) => Track(i, t, "Instrument")).ToList();
        var live = new BitwigProject("Loop Idea", false, read.Tempo ?? 120, "4/4", false, 0, tracks.Count, tracks);

        var same = BitwigLiveTests.CompareSaved(path, read, live);
        Assert.Equal(LiveOutcome.Passed, same.Outcome);
        Assert.StartsWith("What the app read from the saved file matches what Bitwig shows.", same.Detail);
        Assert.All(same.Lines, l => Assert.StartsWith("- ", l));

        var more = live with { Tracks = [.. tracks, Track(tracks.Count, "Vocals", "Audio", "EQ+")], TrackCount = tracks.Count + 1 };
        var different = BitwigLiveTests.CompareSaved(path, read, more);
        Assert.Equal(LiveOutcome.Failed, different.Outcome);
        Assert.Contains("Tracks Bitwig shows that the app didn't read: “Vocals”.", different.Detail);
        Assert.Contains("Devices Bitwig shows that the app didn't find: “EQ+” on Vocals.", different.Detail);
        Assert.Equal(path, (string)different.Data!["file"]!);
    }

    [Fact]
    public void SaveSaysWhetherBitwigWroteTheProject()
    {
        const string file = "K:/Bitwig Studio/Projects/MO Live Test/MO Live Test.bwproject";
        var then = new DateTime(2026, 10, 15, 8, 43, 0, DateTimeKind.Utc);
        var now = then.AddSeconds(25);
        var wait = TimeSpan.FromSeconds(15);
        // Through a link that runs Save straight away only (older than the extension 3.7), as the run finds it.
        LiveVerdict Save(bool stillModified, string? path, DateTime? before, DateTime? after, bool? front = null) =>
            BitwigLiveTests.SaveVerdict("MO Live Test", "Save", true,
                                        [new("direct", "MO Live Test", true, !stillModified, path is not null && after is { } a && a != before,
                                             TimeSpan.FromMilliseconds(120))],
                                        wait, path, before, after, front, routes: false);

        var cleared = Save(false, file, then, now);
        Assert.Equal(LiveOutcome.Passed, cleared.Outcome);
        Assert.Equal($"Bitwig saved “MO Live Test” when the link ran Save straight away: its unsaved changes cleared 120 ms after Save, and wrote {file}." +
                     OnlyDirect, cleared.Detail);
        Assert.Equal("2026-10-15T08:43:00.000Z", (string?)cleared.Data!["writtenBefore"]);
        Assert.Equal("2026-10-15T08:43:25.000Z", (string?)cleared.Data!["writtenAfter"]);

        // Written, though the unsaved changes didn't clear: saved, and the detail says what looked odd.
        var stayed = Save(true, file, then, now);
        Assert.Equal(LiveOutcome.Passed, stayed.Outcome);
        Assert.StartsWith($"Bitwig wrote {file} when the link ran Save straight away, but still showed unsaved changes 15 s later", stayed.Detail);
        Assert.Equal(true, (bool?)stayed.Data!["modifiedAfter"]);

        // A first save: no file before, one after.
        Assert.Equal(LiveOutcome.Passed, Save(true, file, null, now).Outcome);

        var unwritten = Save(true, file, then, then);
        Assert.Equal(LiveOutcome.Failed, unwritten.Outcome);
        Assert.StartsWith($"Bitwig still showed unsaved changes 15 s after Save (“Save”), and didn't write {file}." + OnlyDirect, unwritten.Detail);

        var unfound = Save(true, null, null, null);
        Assert.Equal(LiveOutcome.Failed, unfound.Outcome);
        Assert.Contains("the app didn't find the project's file to see whether it was written", unfound.Detail);
        Assert.Null(unfound.Data!["file"]);

        // Whether Bitwig's window was in front, which Save may need.
        Assert.Contains($"didn't write {file}, though its window was in front.", Save(true, file, then, then, true).Detail);
        var behind = Save(true, file, then, then, false);
        Assert.Contains($"didn't write {file}. The app couldn't bring Bitwig's window to the front, which Save may need: click on Bitwig while " +
                        "the run saves.", behind.Detail);
        Assert.Equal(false, (bool?)behind.Data!["front"]);
        Assert.Null(unfound.Data!["front"]);

        // A project with no unsaved changes before only shows they stayed clear.
        Assert.Equal($"Bitwig ran Save straight away (direct). “MO Live Test” had no unsaved changes before, so this only shows they stayed clear, " +
                     $"and wrote {file}.",
                     BitwigLiveTests.SaveVerdict("MO Live Test", "Save", false, [new("direct", "MO Live Test", true, true, true, TimeSpan.FromMilliseconds(5))],
                                                 wait, file, then, now).Detail);
    }

    [Fact]
    public void SaveNamesTheRouteThatWorked()
    {
        const string file = "K:/Bitwig Studio/Projects/MO Live Test/MO Live Test.bwproject";
        var then = new DateTime(2026, 10, 15, 8, 43, 0, DateTimeKind.Utc);
        var now = then.AddSeconds(32);
        var wait = TimeSpan.FromSeconds(15);
        static BitwigLiveTests.SaveAttempt Ran(string via, bool cleared = false, bool written = false, int ms = 15000, string project = "MO Live Test") =>
            new(via, project, true, cleared, written, TimeSpan.FromMilliseconds(ms));
        static BitwigLiveTests.SaveAttempt NotRun(string via, string? error = null) =>
            new(via, "MO Live Test", false, false, false, TimeSpan.FromMilliseconds(1000), error);
        LiveVerdict Save(IReadOnlyList<BitwigLiveTests.SaveAttempt> attempts, DateTime? after, bool? front = true) =>
            BitwigLiveTests.SaveVerdict("MO Live Test", "Save", true, attempts, wait, file, then, after, front);

        // Straight away worked: nothing tried after it, and nothing said of the others.
        var first = Save([Ran("direct", cleared: true, written: true, ms: 120)], now);
        Assert.Equal(LiveOutcome.Passed, first.Outcome);
        Assert.Equal($"Bitwig saved “MO Live Test” when the link ran Save straight away: its unsaved changes cleared 120 ms after Save, and wrote {file}.",
                     first.Detail);
        Assert.Equal(("direct", "MO Live Test"), ((string?)first.Data!["via"], (string?)first.Data["projectAtSave"]));

        // Only the button Bitwig runs Save for worked: it's named, and so are the routes before it.
        var button = Save([Ran("direct"), Ran("flush"), NotRun("midi"), Ran("buttonCode"), Ran("button", cleared: true, written: true, ms: 300)], now);
        Assert.Equal(LiveOutcome.Passed, button.Outcome);
        Assert.Equal($"Bitwig saved “MO Live Test” when the link pressed its own hardware button for Save: its unsaved changes cleared 300 ms after Save, " +
                     $"and wrote {file}. The routes before it didn't work: Save run straight away (direct), from Bitwig's flush (flush) and on a press of " +
                     "the link's own button, run by the link (buttonCode) left the unsaved changes; Save on the link's own MIDI message (midi) didn't run.",
                     button.Detail);
        Assert.Equal(("button", false, 300.0), ((string?)button.Data!["via"], (bool)button.Data["modifiedAfter"]!, (double)button.Data["tookMs"]!));
        Assert.Equal("""{"via":"button","project":"MO Live Test","ran":true,"cleared":true,"written":true,"tookMs":300}""",
                     button.Data["attempts"]![4]!.ToJsonString());
        Assert.Equal("""{"via":"midi","project":"MO Live Test","ran":false,"cleared":false,"written":false,"tookMs":1000}""",
                     button.Data["attempts"]![2]!.ToJsonString());

        // The flush wrote the file, though the changes stayed: that counts.
        var flush = Save([Ran("direct"), Ran("flush", written: true)], now);
        Assert.Equal(LiveOutcome.Passed, flush.Outcome);
        Assert.StartsWith($"Bitwig wrote {file} when the link ran Save from Bitwig's flush, but still showed unsaved changes 15 s later", flush.Detail);
        Assert.EndsWith(" The routes before it didn't work: Save run straight away (direct) left the unsaved changes.", flush.Detail);
        Assert.Equal(true, (bool?)flush.Data!["modifiedAfter"]);

        // None worked: every route's outcome, and how to set up the loopback when the routes through it didn't run.
        var none = Save([Ran("direct"), Ran("flush"), NotRun("midi"), NotRun("buttonCode"), NotRun("button")], then);
        Assert.Equal(LiveOutcome.Failed, none.Outcome);
        Assert.Equal($"Bitwig still showed unsaved changes 15 s after Save (“Save”), and didn't write {file}, though its window was in front. The link " +
                     "tried Save 5 ways, one after another, and none worked: straight away (direct), from Bitwig's flush (flush), on the link's own MIDI " +
                     "message (midi), on a press of the link's own button, run by the link (buttonCode) and by the link's own hardware button, run by " +
                     "Bitwig (button). The routes through the MIDI loopback didn't run. Bitwig 6.1.3 has only saved for the extension once its MIDI " +
                     "in and out were set (from its flush, and once straight away), so set them whichever route the app uses. " +
                     BitwigLiveTests.LoopbackSetup + " Bitwig said the test " +
                     "project, “MO Live Test”, was the active project when Save ran. If Bitwig opened a Save As window, the project hasn't been saved " +
                     "yet: save it as “MO Live Test”, then run again.", none.Detail);
        Assert.Equal(["- Straight away (direct): ran, but Bitwig still showed unsaved changes 15 s later",
                      "- From Bitwig's flush (flush): ran, but Bitwig still showed unsaved changes 15 s later",
                      "- On the link's own MIDI message (midi): didn't run, as nothing came back through the loopback within a second",
                      "- On a press of the link's own button, run by the link (buttonCode): didn't run, as nothing came back through the loopback within a second",
                      "- By the link's own hardware button, run by Bitwig (button): didn't run, as nothing came back through the loopback within a second"],
                     none.Lines);
        Assert.Null((string?)none.Data!["via"]);

        // Every route ran (the loopback works) and none saved: no loopback steps. A flush that never came, and a route the
        // link refused, say so.
        var ranAll = Save([Ran("direct"), NotRun("flush"), NotRun("midi", "No MIDI ports."), Ran("buttonCode"), Ran("button")], then, front: false);
        Assert.DoesNotContain("loopMIDI", Save([Ran("direct"), Ran("flush"), Ran("midi"), Ran("buttonCode"), Ran("button")], then).Detail);
        Assert.Contains(". The app couldn't bring Bitwig's window to the front, which Save may need: click on Bitwig while the run saves. The link tried",
                        ranAll.Detail);
        Assert.Equal("- From Bitwig's flush (flush): didn't run, as Bitwig didn't call the link's flush in time", ranAll.Lines[1]);
        Assert.Equal("- On the link's own MIDI message (midi): the link refused it: No MIDI ports.", ranAll.Lines[2]);
        Assert.Equal("No MIDI ports.", (string?)ranAll.Data!["attempts"]![2]!["error"]);

        // Another project in front: named, and a different one at a later Save too.
        var elsewhere = Save([Ran("direct", project: "Night Drive"), Ran("flush", project: "MO Live Test")], then);
        Assert.Contains(" Bitwig said “Night Drive” was the active project when Save ran, not the test project, “MO Live Test”. At a later Save it said " +
                        "“MO Live Test”. If Bitwig opened", elsewhere.Detail);
        Assert.Equal("Night Drive", (string?)elsewhere.Data!["projectAtSave"]);
        Assert.Contains(" Bitwig said “Night Drive” was the active project when Save ran",
                        Save([Ran("direct", project: "Night Drive"), Ran("flush", cleared: true, ms: 200)], now).Detail);

        // A link that doesn't say which project was in front (older than the extension 3.7): the verdicts still come, without it.
        var older = BitwigLiveTests.SaveVerdict("MO Live Test", "Save", true, [new("direct", null, true, false, false, wait)], wait, file, then, then,
                                                true, routes: false);
        Assert.Equal(LiveOutcome.Failed, older.Outcome);
        Assert.Contains(OnlyDirect + " The link in Bitwig didn't say which project was active when Save ran (an older extension doesn't). If Bitwig " +
                        "opened", older.Detail);
        Assert.Null(older.Data!["projectAtSave"]);
    }

    [Fact]
    public void FailsAKnobThatDoesntMoveOrLandsOnTheWrongStep()
    {
        static KnobCheckLine ToProbe(int knob, double sent, double? got) =>
            new(knob, KnobWay.ToProbe, sent, ProbeKnobs.Plain(knob, sent), got, null, TimeSpan.FromMilliseconds(80),
                got is { } g && ProbeKnobs.Same(knob, g, ProbeKnobs.Plain(knob, sent)));
        static KnobCheckLine ToDaw(int knob, double plain, double? got) =>
            new(knob, KnobWay.ToDaw, plain, ProbeKnobs.Normalized(knob, plain), got, null, TimeSpan.FromMilliseconds(80),
                got is { } g && Math.Abs(g - ProbeKnobs.Normalized(knob, plain)) <= ProbeKnobs.Tolerance);
        var good = new KnobCheck("MO Probe Instrument on “Probe CLAP”", [.. ProbeKnobCheck.Values(1001).Select(v => ToProbe(1001, v, v)),
                                                                       .. ProbeKnobCheck.Values(1001).Select(v => ToDaw(1001, v, v))], []);
        Assert.Equal(LiveOutcome.Passed, BitwigLiveTests.KnobVerdict(good).Outcome);

        var bad = new KnobCheck("MO Probe Instrument on “Probe CLAP”",
        [
            .. ProbeKnobCheck.Values(1002).Select(v => ToProbe(1002, v, 0)),                                   // stuck at 0 dB
            .. ProbeKnobCheck.Values(1004).Select(v => ToProbe(1004, v, Math.Min(7, Math.Round(v * 8)))),    // over 9 steps, not 8
            .. ProbeKnobCheck.Values(1007).Select(v => ToProbe(1007, v, v * v * 100)),                        // curved, which is only noted
        ], []);
        var verdict = BitwigLiveTests.KnobVerdict(bad);
        Assert.Equal(LiveOutcome.Failed, verdict.Outcome);
        Assert.StartsWith("MO Probe's knobs don't all come through Bitwig as BitwigSend needs them: 4 problems.", verdict.Detail);
        Assert.Equal("- MO Gain doesn't move when Bitwig turns it: the probe got 0.0 dB every time.", verdict.Lines[0]);
        Assert.StartsWith("- MO Steps lands on the wrong step: MO Steps, Bitwig → probe: sent 0.571, expected 4, got 5", verdict.Lines[1]);
        Assert.Equal(4, verdict.Lines.Count);

        var mapping = BitwigLiveTests.KnobMapping(bad, "CLAP");
        Assert.Equal(LiveOutcome.Noted, mapping.Outcome);
        Assert.StartsWith("How Bitwig maps MO Probe's knobs (CLAP): 7 of 16 turns landed where an even spread", mapping.Detail);
        Assert.Equal("| MO Ramp | 0 → 0.0 %, 0.25 → 6.3 % ✗, 0.5 → 25.0 % ✗, 1 → 100.0 % | not checked |", mapping.Lines[^1]);
        Assert.Equal(16, mapping.Data!.AsArray().Count);
    }

    [Fact]
    public async Task ReadsTheJavaExtensionsHello()
    {
        await using var java = new FakeJavaBitwig();
        using var link = await BitwigLink.ConnectAsync(java.Port);
        Assert.Equal((3, true, "java", "3.0"), (link.Hello.Link, link.Hello.Engine, link.Hello.Runtime, link.Hello.Extension));
        Assert.Equal("the Music Organiser extension 3.0", link.Hello.Answering);
        var clicked = await link.CommandAsync("clicked");
        Assert.Equal("Cutoff", clicked.GetProperty("name").GetString());
        var e = await Assert.ThrowsAsync<DawLinkException>(() => link.CommandAsync("explode"));
        Assert.Equal("Unknown command: explode", e.Message);
    }

    [Fact]
    public async Task ReadsTheJavaExtensionsArrangeAnswer()
    {
        await using var java = new FakeJavaBitwig();
        using var link = await BitwigLink.ConnectAsync(java.Port);
        var arranged = await link.ArrangeAsync(4, 0, 16);
        var sent = java.Requests.Last();
        Assert.Equal(("arrange", 4, 0, 16.0), ((string)sent["cmd"]!, (int)sent["track"]!, (int)sent["slot"]!, (double)sent["at"]!));
        Assert.Equal(new BitwigArranged(true, true, true, false, 16, "ARRANGE", new(false, true), new("#3366CC", "#416DD9", "#416DD9", true, true), 1,
                                        new(true, true), 1, new(false, null), new(false, null), new(true, "Sample 143012"), "#416DD9", 1, 1),
                     arranged);

        // What the live test makes of it: what the link saw, where the clip was meant to go (which only a look at Bitwig can
        // confirm), and all the extension said, with whether Bitwig was in front.
        var verdict = BitwigLiveTests.ArrangeVerdict(arranged, "Sample 143012", 0, 5, true);
        Assert.Equal(LiveOutcome.Passed, verdict.Outcome);
        Assert.Equal("Bitwig pasted a copy of the clip in launcher slot 1 into its arranger on “Sample 143012”: a clip in the launcher clip's own " +
                     "marking colour (#416DD9) appeared in the arranger on “Sample 143012”, so it's the run's MIDI clip, and the track kept its 1 " +
                     "clip in the launcher. It was meant to go in at bar 5 (beat 16), where the " +
                     "link moved the play start, but the link can't read where an arranger clip starts: look at bar 5 on “Sample 143012” in " +
                     "Bitwig's arranger and say whether it's there.", verdict.Detail);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"arranged":true,"appeared":true,"sameAsSource":true,"copiedFromTimeline":false,"at":16,"layout":"ARRANGE",
             "launcher":{"before":false,"after":true},"mark":{"before":"#3366CC","asked":"#416DD9","got":"#416DD9","took":true,"exact":true},
             "launcherFocus":1,"timeline":{"before":true,"after":true},"focus":1,"before":{"exists":false,"track":null},
             "beforePaste":{"exists":false,"track":null},"after":{"exists":true,"track":"Sample 143012"},"pastedColor":"#416DD9",
             "launcherClipsBefore":1,"launcherClipsAfter":1,"via":null,"ran":null,"checks":null,"front":true}
            """), verdict.Data), verdict.Data!.ToJsonString());
        Assert.Contains(" in the launcher (giving the clip launcher the focus took two goes: the first hid it). ",
                        BitwigLiveTests.ArrangeVerdict(arranged with { LauncherFocus = 2 }, "Sample 143012", 0, 5, null).Detail);
        Assert.Contains(" in the launcher (Bitwig has no focus_or_toggle_clip_launcher action to give the clip launcher the focus). ",
                        BitwigLiveTests.ArrangeVerdict(arranged with { LauncherFocus = 0 }, "Sample 143012", 0, 5, null).Detail);
    }

    [Fact]
    public void ArrangeSaysWhatBitwigDidInstead()
    {
        const string track = "Sample 143012";
        const string start = "Bitwig didn't put the clip into its arranger at bar 5 (beat 16) on “Sample 143012”: ";
        BitwigArrangerClip none = new(false, null), there = new(true, track), elsewhere = new(true, "Bass");
        static BitwigArranged Answer(BitwigArrangerClip beforePaste, BitwigArrangerClip after, int launcherAfter = 1, bool timelineAfter = true) =>
            new(false, false, false, false, 16, "ARRANGE", new(true, true), Marked, 1, new(true, timelineAfter), 1, beforePaste, beforePaste, after,
                after.Exists ? "#416DD9" : null, 1, launcherAfter);
        string Detail(BitwigArranged a, bool? front = true)
        {
            var verdict = BitwigLiveTests.ArrangeVerdict(a, track, 0, 5, front);
            Assert.Equal(LiveOutcome.Failed, verdict.Outcome);
            Assert.Equal(front, (bool?)verdict.Data!["front"]);
            // Only the person can see where the clip went, so each failure asks them to look.
            Assert.EndsWith(Ask, verdict.Detail);
            return verdict.Detail[..^Ask.Length];
        }

        Assert.Equal(start + "it pasted into the clip launcher instead (“Sample 143012” has 2 clips there now, 1 before); no clip is selected in the arranger.",
                     Detail(Answer(none, none, launcherAfter: 2)));
        Assert.Equal(start + "it pasted into the clip launcher instead (“Sample 143012” has 2 clips there now, 1 before); the clip selected in the arranger " +
                     "did move to “Sample 143012” as well.", Detail(Answer(none, there, launcherAfter: 2)));
        Assert.Equal(start + "nothing appeared: no clip is selected in the arranger, and the launcher has the same clips.", Detail(Answer(none, none)));
        Assert.Equal(start + "nothing new was selected in the arranger: the clip selected there is still the one on “Bass”.", Detail(Answer(elsewhere, elsewhere)));
        Assert.Equal(start + "the clip selected in the arranger is on “Bass”, not “Sample 143012”.", Detail(Answer(none, elsewhere)));
        Assert.Equal(start + "a clip on “Sample 143012” was already selected in the arranger before the paste, so the link couldn't tell whether a new " +
                     "one appeared.", Detail(Answer(there, there)));
        Assert.Equal(start + "nothing appeared: no clip is selected in the arranger, and the launcher has the same clips; the arranger timeline is hidden " +
                     "now. The app couldn't bring Bitwig's window to the front, which copy and paste may need.",
                     Detail(Answer(none, none, timelineAfter: false), front: false));

        // When no clip appeared on the track, it's Failed whether the mark took or not, rounded or not: a Note is only for a
        // clip that appeared there when the link can't tell which clip it is.
        Assert.Equal(start + "nothing appeared: no clip is selected in the arranger, and the launcher has the same clips.",
                     Detail(Answer(none, none) with { Mark = Untaken }));
        Assert.Equal(start + "it pasted into the clip launcher instead (“Sample 143012” has 2 clips there now, 1 before); no clip is selected in the arranger.",
                     Detail(Answer(none, none, launcherAfter: 2) with { Mark = Untaken }));
        // The clip selected in the arranger has the colour Bitwig rounded the mark to.
        static BitwigArranged InRounded(BitwigArranged a) => a with { Mark = Rounded, SameAsSource = true, PastedColor = "#4060D0" };
        Assert.Equal(start + "it pasted into the clip launcher instead (“Sample 143012” has 2 clips there now, 1 before); the clip selected in the arranger " +
                     "did move to “Sample 143012” as well.", Detail(InRounded(Answer(none, there, launcherAfter: 2))));
        Assert.Equal(start + "a clip on “Sample 143012” was already selected in the arranger before the paste, so the link couldn't tell whether a new " +
                     "one appeared.", Detail(InRounded(Answer(there, there))));

        // The timeline had the focus when the link copied: Bitwig copied from there, so the link didn't paste. Never Passed,
        // even with the clip selected in the arranger on the track.
        const string copied = "the arranger timeline had the focus when the link copied (bringing the timeline to the front hid it, which happens only " +
                              "when it's in front already), so Bitwig copied from the timeline, not launcher slot 1, and the link didn't paste; ";
        var fromTimeline = Answer(elsewhere, elsewhere) with { CopiedFromTimeline = true, Focus = 2, LauncherFocus = 0 };
        Assert.Equal(start + copied + "Bitwig has no focus_or_toggle_clip_launcher action to give the clip launcher the focus.", Detail(fromTimeline));
        Assert.Equal(start + copied + "the link ran focus_or_toggle_clip_launcher first, which didn't give the clip launcher the focus.",
                     Detail(fromTimeline with { LauncherFocus = 1, Arranged = true, After = there }));
    }

    const string Ask = " Look at Bitwig's arranger at bar 5 on “Sample 143012” and say what you see there (the clip, nothing, or the clip somewhere " +
                       "else), so the link can be put right.";

    /** The launcher clip, marked with a fresh colour before the copy, as the extension says it. */
    static readonly BitwigClipMark Marked = new("#3366CC", "#416DD9", "#416DD9", true, true);

    /** The same, when Bitwig rounded the colour asked for to one of its own. */
    static readonly BitwigClipMark Rounded = new("#3366CC", "#416DD9", "#4060D0", true, false);

    /** The same, when Bitwig didn't change the launcher clip's colour. */
    static readonly BitwigClipMark Untaken = new("#3366CC", "#416DD9", "#3366CC", false, false);

    [Fact]
    public void ArrangeTellsTheRunsClipFromWhatWasOnTheClipboardByItsColour()
    {
        const string track = "Sample 143012";
        // A clip appeared on the track after the paste, as when it worked. Bitwig rounded the launcher clip's mark to one of
        // its own colours, as it may.
        var appeared = new BitwigArranged(false, true, false, false, 16, "ARRANGE", new(true, true), Rounded, 1, new(true, true), 1, new(false, null),
                                          new(false, null), new(true, track), "#2E8B57", 1, 1);

        // In another colour than the launcher clip got: Bitwig pasted whatever its clipboard held before (a clip copied in
        // someone's own song, say), so the copy didn't take, rounded colour or not. Failed, whatever the clip's place, naming
        // the colour the launcher clip got.
        var other = BitwigLiveTests.ArrangeVerdict(appeared, track, 0, 5, true);
        Assert.Equal(LiveOutcome.Failed, other.Outcome);
        const string start = "Bitwig didn't put the clip into its arranger at bar 5 (beat 16) on “Sample 143012”: a clip appeared on “Sample 143012” " +
                             "after the paste, but it isn't the launcher clip (its colour is #2E8B57, and the link coloured the launcher clip #4060D0). " +
                             "Bitwig pasted whatever was already on its clipboard, so copying from the launcher didn't take: ";
        const string end = ". The test project now has that other clip on “Sample 143012” at bar 5, which can be deleted.";
        Assert.Equal(start + "the link ran focus_or_toggle_clip_launcher first (once), so either that didn't give the clip launcher the focus or Bitwig " +
                     "doesn't copy launcher clips for an extension" + end, other.Detail);
        // The data is the whole answer, with the mark and the pasted clip's colour.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"before":"#3366CC","asked":"#416DD9","got":"#4060D0","took":true,"exact":false}"""),
                                        other.Data!["mark"]),
                    other.Data!.ToJsonString());
        Assert.Equal("#2E8B57", (string?)other.Data["pastedColor"]);
        Assert.True((bool?)other.Data["appeared"]);
        Assert.True((bool?)other.Data["front"]);
        Assert.Equal(start + "Bitwig has no focus_or_toggle_clip_launcher action, so the link copied from whichever panel had the focus" + end +
                     " The app couldn't bring Bitwig's window to the front, which copy and paste may need.",
                     BitwigLiveTests.ArrangeVerdict(appeared with { LauncherFocus = 0 }, track, 0, 5, false).Detail);
        Assert.Equal(start + "the link ran focus_or_toggle_clip_launcher first (twice, as the first go hid the launcher), so either that didn't give " +
                     "the clip launcher the focus or Bitwig doesn't copy launcher clips for an extension" + end,
                     BitwigLiveTests.ArrangeVerdict(appeared with { LauncherFocus = 2 }, track, 0, 5, null).Detail);
        // The same when the launcher clip got the very colour asked for.
        var exact = BitwigLiveTests.ArrangeVerdict(appeared with { Mark = Marked }, track, 0, 5, true);
        Assert.Equal(LiveOutcome.Failed, exact.Outcome);
        Assert.Contains(" (its colour is #2E8B57, and the link coloured the launcher clip #416DD9). ", exact.Detail);

        // Bitwig didn't let the link colour the launcher clip, so the clip that appeared can't be told from another: Noted,
        // asking the person to look.
        var unmarked = appeared with { Mark = Untaken, PastedColor = "#3366CC" };
        var noted = BitwigLiveTests.ArrangeVerdict(unmarked, track, 0, 5, true);
        Assert.Equal(LiveOutcome.Noted, noted.Outcome);
        Assert.Equal("A clip appeared in Bitwig's arranger on “Sample 143012” after the paste, but Bitwig didn't let the link colour the launcher clip " +
                     "in slot 1, so the link can't tell whether it's the run's MIDI clip or whatever was on Bitwig's clipboard before. Look at bar 5 " +
                     "on “Sample 143012” in Bitwig's arranger and say which clip it is.", noted.Detail);
        Assert.False((bool)noted.Data!["mark"]!["took"]!);
        Assert.EndsWith(" The app couldn't bring Bitwig's window to the front, which copy and paste may need.",
                        BitwigLiveTests.ArrangeVerdict(unmarked, track, 0, 5, false).Detail);

        // In the colour the launcher clip got, but Bitwig rounded that to one of its own, which other clips can have too (one
        // already on its clipboard, say): the extension doesn't say it arranged the clip, and the link can't tell which clip it
        // is either, so Noted, asking the person to look.
        var rounded = appeared with { SameAsSource = true, PastedColor = "#4060D0" };
        var maybe = BitwigLiveTests.ArrangeVerdict(rounded, track, 0, 5, true);
        Assert.Equal(LiveOutcome.Noted, maybe.Outcome);
        Assert.Equal("A clip appeared in Bitwig's arranger on “Sample 143012” after the paste, in the colour the link gave the launcher clip in slot 1, " +
                     "but Bitwig rounded that colour to one of its own (the link asked for #416DD9 and got #4060D0), which other clips can have too, so " +
                     "the link can't tell whether it's the run's MIDI clip or whatever was on Bitwig's clipboard before. Look at bar 5 on " +
                     "“Sample 143012” in Bitwig's arranger and say which clip it is.", maybe.Detail);
        Assert.False((bool)maybe.Data!["mark"]!["exact"]!);
        Assert.True((bool)maybe.Data!["sameAsSource"]!);
        Assert.EndsWith(" The app couldn't bring Bitwig's window to the front, which copy and paste may need.",
                        BitwigLiveTests.ArrangeVerdict(rounded, track, 0, 5, false).Detail);
        // Never Noted when the timeline had the focus as the link copied, since the link didn't paste then.
        Assert.Equal(LiveOutcome.Failed, BitwigLiveTests.ArrangeVerdict(rounded with { CopiedFromTimeline = true, Focus = 2 }, track, 0, 5, true).Outcome);

        // Passed only with the clip in the mark's very colour, as the extension says when it's the same as the launcher clip.
        var passed = BitwigLiveTests.ArrangeVerdict(appeared with { Arranged = true, SameAsSource = true, Mark = Marked, PastedColor = "#416DD9" }, track,
                                                    0, 5, true);
        Assert.Equal(LiveOutcome.Passed, passed.Outcome);
        Assert.Contains("a clip in the launcher clip's own marking colour (#416DD9) appeared in the arranger on “Sample 143012”", passed.Detail);
    }

    /// <summary>The Java extension 3.6 with a test project open, changes allowed.</summary>
    const string ArrangingHello =
        """{"link":3,"product":"Bitwig Studio","version":"6.1.3","api":23,"platform":"WINDOWS","port":19791,"project":"MO Live Test","modified":false,"engine":true,"changes":true,"runtime":"java","extension":"3.6","onlyIn":true}""";

    /// <summary>A test project in 7/8 with the run's Sample track third (index 2).</summary>
    static readonly Dictionary<string, string> SevenEight = new()
    {
        ["project"] = """{"name":"MO Live Test","modified":true,"tempo":124,"timeSignature":"7/8","playing":false,"position":0,"trackCount":4,"tracks":[""" +
                      string.Join(",", new[] { "Devices 143012", "Bass", "Sample 143012", "Master" }.Select((name, i) =>
                          $$"""{"index":{{i}},"name":"{{name}}","type":"Instrument","group":false,"color":"#3366cc","active":true,"mute":false,"solo":false,"arm":false,"volume":"0.0 dB","volumeValue":0.79,"pan":"C","panValue":0.5,"deviceCount":0,"devices":[]}""")) +
                      "]}",
    };

    [Fact]
    public async Task ClipToArrangerSendsTheClipToBar5()
    {
        await using var java = new FakeJavaBitwig(ArrangingHello, SevenEight);
        using var link = await BitwigLink.ConnectAsync(java.Port);
        var asked = 0;
        var verdict = await BitwigLiveTests.ClipToArrangerAsync(link, "Sample 143012", 3, () => { asked++; return false; });

        // The Sample track's index, the slot the MIDI clip went in, and bar 5 in 7/8: four bars of 3.5 beats.
        var sent = java.Requests.Last();
        Assert.Equal(("arrange", 2, 3, 14.0), ((string)sent["cmd"]!, (int)sent["track"]!, (int)sent["slot"]!, (double)sent["at"]!));
        Assert.Equal(1, asked);
        Assert.Equal(LiveOutcome.Passed, verdict.Outcome);
        Assert.StartsWith("Bitwig pasted a copy of the clip in launcher slot 4 into its arranger on “Sample 143012”", verdict.Detail);
        Assert.False((bool?)verdict.Data!["front"]);
        Assert.True((bool)verdict.Data["arranged"]!);
    }

    [Fact]
    public async Task ClipToArrangerFailsAtAStepBitwigDidntGoAlongWith()
    {
        await using var java = new FakeJavaBitwig(ArrangingHello, SevenEight);
        java.Errors["arrange"] = "Bitwig hid its arranger timeline, which was in front already, and didn't show it again.";
        using var link = await BitwigLink.ConnectAsync(java.Port);

        var verdict = await BitwigLiveTests.ClipToArrangerAsync(link, "Sample 143012", 0, () => false);
        Assert.Equal(LiveOutcome.Failed, verdict.Outcome);
        Assert.Equal("Bitwig hid its arranger timeline, which was in front already, and didn't show it again. The app couldn't bring Bitwig's window " +
                     "to the front, which copy and paste may need." + Ask, verdict.Detail);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"error":"Bitwig hid its arranger timeline, which was in front already, and didn't show it again.","front":false}
            """), verdict.Data), verdict.Data!.ToJsonString());

        // With Bitwig's window in front, or when that can't be told, the error and the ask alone.
        java.Errors["arrange"] = "Bitwig didn't switch to its Arrange layout.";
        verdict = await BitwigLiveTests.ClipToArrangerAsync(link, "Sample 143012", 0, () => null);
        Assert.Equal("Bitwig didn't switch to its Arrange layout." + Ask, verdict.Detail);
        Assert.Null(verdict.Data!["front"]);
    }

    [Fact]
    public async Task ClipToArrangerSkipsWhatCantArrange()
    {
        // An extension older than 3.6 lets changes through, but has no arrange: it says to install it again, and sends nothing.
        await using (var older = new FakeJavaBitwig(ArrangingHello.Replace("\"3.6\"", "\"3.5\""), SevenEight))
        {
            using var link = await BitwigLink.ConnectAsync(older.Port);
            var verdict = await BitwigLiveTests.ClipToArrangerAsync(link, "Sample 143012", 0, () => true);
            Assert.Equal((LiveOutcome.Skipped, "This needs the Music Organiser extension 3.6 or later, and Bitwig runs 3.5. " + BitwigLiveTests.Reinstall),
                         (verdict.Outcome, verdict.Detail));
            Assert.DoesNotContain(older.Requests, r => (string)r["cmd"]! == "arrange");
        }

        // An extension that doesn't say its version, and answers "Unknown command": the same.
        await using (var unknown = new FakeJavaBitwig(ArrangingHello.Replace("\"3.6\"", "\"\""), SevenEight))
        {
            unknown.Errors["arrange"] = "Unknown command: \"arrange\"";
            using var link = await BitwigLink.ConnectAsync(unknown.Port);
            var verdict = await BitwigLiveTests.ClipToArrangerAsync(link, "Sample 143012", 0, () => true);
            Assert.Equal((LiveOutcome.Skipped, "This needs the Music Organiser extension 3.6 or later, and Bitwig runs an older one. " +
                                               BitwigLiveTests.Reinstall),
                         (verdict.Outcome, verdict.Detail));
        }

        await using var java = new FakeJavaBitwig(ArrangingHello, SevenEight);
        using (var link = await BitwigLink.ConnectAsync(java.Port))
        {
            // Without the MIDI clip, or its track, there's nothing to copy.
            var front = 0;
            Assert.Equal("This needs the MIDI clip, which an earlier test didn't get (see above).",
                         (await BitwigLiveTests.ClipToArrangerAsync(link, "Sample 143012", null, () => { front++; return true; })).Detail);
            Assert.Equal("This needs the MIDI clip, which an earlier test didn't get (see above).",
                         (await BitwigLiveTests.ClipToArrangerAsync(link, "Sample 999999", 0, () => { front++; return true; })).Detail);
            Assert.Equal(0, front);
            Assert.DoesNotContain(java.Requests, r => (string)r["cmd"]! == "arrange");
        }
    }

    /// <summary>The extension 3.7's arrange answer by a route: whether it put the clip there, and which steps ran (as JSON).</summary>
    static string ArrangeAnswer(string? via, bool arranged, string ran)
    {
        var a = JsonNode.Parse(FakeJavaBitwig.Canned("arrange"))!.AsObject();
        if (!arranged)
        {
            a["arranged"] = false;
            a["appeared"] = false;
            a["sameAsSource"] = false;
            a["after"] = new JsonObject { ["exists"] = false, ["track"] = null };
            a["pastedColor"] = null;
        }
        a["at"] = 14;   // bar 5 in 7/8 (see SevenEight)
        a["via"] = via;
        a["ran"] = JsonNode.Parse(ran);
        return a.ToJsonString();
    }

    const string AllRan = """{"launcherFocus":true,"copy":true,"focus":true,"paste":true}""";
    const string CopyDidntRun = """{"launcherFocus":false,"copy":false,"focus":null,"paste":null}""";

    /// <summary>The routes the arrange requests a stand-in Bitwig was sent asked for.</summary>
    static List<string?> ArrangeRoutes(FakeJavaBitwig java) =>
        java.Requests.Where(r => (string)r["cmd"]! == "arrange").Select(r => (string?)r["via"]).ToList();

    [Fact]
    public async Task ClipToArrangerTriesEachRouteUntilOneArrangesTheClip()
    {
        // The extension 3.7: Bitwig ignores copy and paste straight away and from the flush, the loopback doesn't bring the
        // link's own MIDI message back, and copy and paste on presses of the link's own buttons put the clip there.
        await using var java = new FakeJavaBitwig(ArrangingHello.Replace("\"3.6\"", "\"3.7\""), SevenEight);
        java.Answer = request => (string)request["cmd"]! != "arrange" ? null : (string?)request["via"] switch
        {
            "buttonCode" => ArrangeAnswer("buttonCode", true, AllRan),
            "midi" => ArrangeAnswer("midi", false, CopyDidntRun),
            var via => ArrangeAnswer(via, false, AllRan),
        };
        using var link = await BitwigLink.ConnectAsync(java.Port);

        var verdict = await BitwigLiveTests.ClipToArrangerAsync(link, "Sample 143012", 0, () => true);

        Assert.Equal(LiveOutcome.Passed, verdict.Outcome);
        Assert.StartsWith("Bitwig pasted a copy of the clip in launcher slot 1 into its arranger on “Sample 143012”", verdict.Detail);
        Assert.EndsWith(" The link ran the copy, the paste and the focus actions on a press of the link's own button, run by the link (buttonCode), " +
                        "after straight away (direct), from Bitwig's flush (flush) and on the link's own MIDI message (midi) didn't put the clip there.",
                        verdict.Detail);
        // Each go is an arrange of its own, which marks the launcher clip with a fresh colour, until one works.
        Assert.Equal(["direct", "flush", "midi", "buttonCode"], ArrangeRoutes(java));
        var attempts = verdict.Data!["attempts"]!.AsArray();
        Assert.Equal(["direct", "flush", "midi", "buttonCode"], attempts.Select(a => (string?)a!["via"]));
        Assert.Equal(false, (bool?)attempts[2]!["answer"]!["ran"]!["copy"]);
        Assert.Equal(("buttonCode", true), ((string?)verdict.Data["via"], (bool)verdict.Data["arranged"]!));
    }

    [Fact]
    public async Task ClipToArrangerSaysWhatEachRouteDidWhenNoneArrangesTheClip()
    {
        // Bitwig ignores copy and paste straight away and from the flush, and the loopback doesn't bring anything back.
        await using var java = new FakeJavaBitwig(ArrangingHello.Replace("\"3.6\"", "\"3.7\""), SevenEight);
        java.Answer = request => (string)request["cmd"]! != "arrange" ? null
            : (string?)request["via"] is "direct" or "flush" ? ArrangeAnswer((string?)request["via"], false, AllRan)
            : ArrangeAnswer((string?)request["via"], false, CopyDidntRun);
        using var link = await BitwigLink.ConnectAsync(java.Port);

        var verdict = await BitwigLiveTests.ClipToArrangerAsync(link, "Sample 143012", 0, () => true);

        Assert.Equal(LiveOutcome.Failed, verdict.Outcome);
        Assert.Equal(["direct", "flush", "midi", "buttonCode", "button"], ArrangeRoutes(java));
        // What the last go found, as for one go, then what each route did.
        Assert.StartsWith("Bitwig didn't put the clip into its arranger at bar 5 (beat 14) on “Sample 143012”: nothing appeared", verdict.Detail);
        Assert.Contains(Ask + " The link tried the copy, the paste and the focus actions 5 ways, one after another, and none put the clip there (this " +
                        "is what the last, by the link's own hardware button, run by Bitwig (button), found). The routes through the MIDI loopback " +
                        "didn't all run. " + BitwigLiveTests.LoopbackSetup, verdict.Detail);
        Assert.Equal(["- Straight away (direct): nothing appeared in the arranger",
                      "- From Bitwig's flush (flush): nothing appeared in the arranger",
                      "- On the link's own MIDI message (midi): giving the clip launcher the focus and the copy didn't run",
                      "- On a press of the link's own button, run by the link (buttonCode): giving the clip launcher the focus and the copy didn't run",
                      "- By the link's own hardware button, run by Bitwig (button): giving the clip launcher the focus and the copy didn't run"],
                     verdict.Lines);
        Assert.Equal(5, verdict.Data!["attempts"]!.AsArray().Count);

        // A step the extension stops at is noted for its route, and the next tried.
        java.Refuse = request => (string?)request["via"] == "flush" ? "Bitwig didn't show the clip launcher in its Arrange layout." : null;
        verdict = await BitwigLiveTests.ClipToArrangerAsync(link, "Sample 143012", 0, () => true, closed: () => Task.FromResult(false));
        Assert.Equal("- From Bitwig's flush (flush): the extension stopped: Bitwig didn't show the clip launcher in its Arrange layout.", verdict.Lines[1]);
        Assert.Equal("Bitwig didn't show the clip launcher in its Arrange layout.", (string?)verdict.Data!["attempts"]![1]!["error"]);
        // Unless the run may no longer change the project: then the test stops there.
        await Assert.ThrowsAsync<DawLinkException>(() =>
            BitwigLiveTests.ClipToArrangerAsync(link, "Sample 143012", 0, () => true, closed: () => Task.FromResult(true)));
    }

    /// <summary>The extension 3.8's checks when the copy took: its paste into an empty launcher slot made the marked clip, and Duplicate worked there.</summary>
    const string CopyTook = """{"launcherPaste":{"slot":2,"selected":true,"focus":1,"ran":{"focus":true,"paste":true},"made":true,"colour":"#416DD9","sameAsMark":true,"removed":true},"duplicate":{"selected":true,"focus":1,"ran":{"focus":true,"duplicate":true},"launcher":1,"tracks":0,"removed":true}}""";

    /// <summary>The same when the copy didn't take, and Duplicate copied the whole track, so the launcher didn't have the focus.</summary>
    const string CopyDidntTake = """{"launcherPaste":{"slot":2,"selected":true,"focus":1,"ran":{"focus":true,"paste":true},"made":true,"colour":"#3366CC","sameAsMark":false,"removed":false},"duplicate":{"selected":true,"focus":1,"ran":{"focus":true,"duplicate":true},"launcher":0,"tracks":1,"removed":false}}""";

    [Fact]
    public async Task ClipToArrangerChecksEachGoThatDidntWorkThroughTheExtension38()
    {
        // The extension 3.8: no route puts the clip there. From the flush the copy took and Duplicate worked in the launcher;
        // straight away neither did; on the link's own MIDI message the checks stopped. By the buttons the checks' colours
        // say nothing for sure: Bitwig didn't colour the launcher clip, then rounded its colour.
        await using var java = new FakeJavaBitwig(ArrangingHello.Replace("\"3.6\"", "\"3.8\""), SevenEight);
        java.Answer = request =>
        {
            if ((string)request["cmd"]! != "arrange") return null;
            var via = (string?)request["via"] ?? "flush";
            var a = JsonNode.Parse(ArrangeAnswer(via, false, AllRan))!.AsObject();
            a["checks"] = via switch
            {
                "flush" => JsonNode.Parse(CopyTook),
                "direct" => JsonNode.Parse(CopyDidntTake),
                "midi" => JsonNode.Parse("""{"launcherPaste":null,"duplicate":{"selected":false,"focus":0,"ran":{"focus":null,"duplicate":null},"launcher":0,"tracks":0,"removed":false},"error":"Bitwig has “My Song” open now."}"""),
                "buttonCode" => JsonNode.Parse(CopyDidntTake),
                _ => JsonNode.Parse(CopyTook),
            };
            a["mark"] = via switch
            {
                "buttonCode" => JsonNode.Parse("""{"before":"#3366CC","asked":"#416DD9","got":"#3366CC","took":false,"exact":false}"""),
                "button" => JsonNode.Parse("""{"before":"#3366CC","asked":"#416DD9","got":"#416DD9","took":true,"exact":false}"""),
                _ => a["mark"]!.DeepClone(),
            };
            return a.ToJsonString();
        };
        using var link = await BitwigLink.ConnectAsync(java.Port);

        var verdict = await BitwigLiveTests.ClipToArrangerAsync(link, "Sample 143012", 0, () => true);

        Assert.Equal(LiveOutcome.Failed, verdict.Outcome);
        // The flush first, naming no route, as the app's own commands do; then the others. Each go asks for the checks.
        Assert.Equal([null, "direct", "midi", "buttonCode", "button"], ArrangeRoutes(java));
        Assert.All(java.Requests.Where(r => (string)r["cmd"]! == "arrange"), r => Assert.True((bool)r["checks"]!));
        Assert.Equal(["flush", "direct", "midi", "buttonCode", "button"], verdict.Data!["attempts"]!.AsArray().Select(a => (string?)a!["via"]));
        Assert.Equal(["- From Bitwig's flush (flush): nothing appeared in the arranger",
                      "  - Paste into the launcher: a paste into empty slot 3 put the launcher clip's copy there, in its marking colour (#416DD9), so " +
                      "the copy took and pasting works in the launcher: it's putting it in the arranger that fails",
                      "  - Duplicate in the launcher: Duplicate made a copy of the clip in the launcher, so Bitwig's editing actions work in the " +
                      "launcher by this route (the link deleted the copy again)",
                      "- Straight away (direct): nothing appeared in the arranger",
                      "  - Paste into the launcher: a paste into empty slot 3 put down a clip in another colour (#3366CC) than the launcher clip's " +
                      "mark, so the copy didn't take: Bitwig pasted what its clipboard held before (the link couldn't delete it again, so it's " +
                      "still in slot 3)",
                      "  - Duplicate in the launcher: Duplicate copied the whole track instead, so the clip launcher didn't have the focus (the copy " +
                      "is left in the test project)",
                      "- On the link's own MIDI message (midi): nothing appeared in the arranger",
                      "  - Duplicate in the launcher: Bitwig didn't select the launcher clip again, so the check stopped there",
                      "  - The checks stopped: Bitwig has “My Song” open now.",
                      "- On a press of the link's own button, run by the link (buttonCode): nothing appeared in the arranger",
                      "  - Paste into the launcher: a paste into empty slot 3 put down a clip (#3366CC), but Bitwig didn't let the link colour the " +
                      "launcher clip, so it can't tell whether that's the launcher clip's copy or what Bitwig's clipboard held before (the link " +
                      "couldn't delete it again, so it's still in slot 3)",
                      "  - Duplicate in the launcher: Duplicate copied the whole track instead, so the clip launcher didn't have the focus (the copy " +
                      "is left in the test project)",
                      "- By the link's own hardware button, run by Bitwig (button): nothing appeared in the arranger",
                      "  - Paste into the launcher: a paste into empty slot 3 put down a clip in the launcher clip's colour (#416DD9), but Bitwig " +
                      "rounded that colour to one of its own, which other clips can have too, so it can't tell for sure whether the copy took",
                      "  - Duplicate in the launcher: Duplicate made a copy of the clip in the launcher, so Bitwig's editing actions work in the " +
                      "launcher by this route (the link deleted the copy again)"],
                     verdict.Lines);
        Assert.Equal("#416DD9", (string?)verdict.Data["attempts"]![0]!["answer"]!["checks"]!["launcherPaste"]!["colour"]);
    }

    /// <summary>
    /// The extension 3.7's arrange answer by a route when a clip in the launcher clip's colour appeared on the track, but
    /// Bitwig rounded the mark to one of its own colours, so the extension doesn't say it arranged the clip.
    /// </summary>
    static string RoundedAnswer(string? via)
    {
        var a = JsonNode.Parse(ArrangeAnswer(via, true, AllRan))!.AsObject();
        a["arranged"] = false;
        a["mark"] = JsonNode.Parse("""{"before":"#3366CC","asked":"#416DD9","got":"#4060D0","took":true,"exact":false}""");
        a["pastedColor"] = "#4060D0";
        return a.ToJsonString();
    }

    /// <summary>
    /// What a route after one that pasted would answer: the clip that paste left selected on the track hides whether a new
    /// one appeared.
    /// </summary>
    static string AfterAPaste(string? via)
    {
        var a = JsonNode.Parse(ArrangeAnswer(via, false, AllRan))!.AsObject();
        a["beforePaste"] = JsonNode.Parse("""{"exists":true,"track":"Sample 143012"}""");
        a["after"] = JsonNode.Parse("""{"exists":true,"track":"Sample 143012"}""");
        a["pastedColor"] = "#4060D0";
        return a.ToJsonString();
    }

    const string NotTried = " Bitwig's paste put something down by that route, so the link didn't try ";
    const string LandsThere = ": another paste would land where this one did, and the clip this one left selected there would hide whether a new " +
                              "one appeared.";

    [Fact]
    public async Task ClipToArrangerStopsAtTheFirstRouteWhosePastePutSomethingDown()
    {
        // Straight away, Bitwig pasted a clip in the launcher clip's colour, rounded to one of its own: the link can't be sure
        // it's the run's clip, so the person is asked to look, and no other route is tried, as its paste would only go on top.
        await using var java = new FakeJavaBitwig(ArrangingHello.Replace("\"3.6\"", "\"3.7\""), SevenEight);
        java.Answer = request => (string)request["cmd"]! != "arrange" ? null
            : (string?)request["via"] == "direct" ? RoundedAnswer("direct") : AfterAPaste((string?)request["via"]);
        using var link = await BitwigLink.ConnectAsync(java.Port);

        var verdict = await BitwigLiveTests.ClipToArrangerAsync(link, "Sample 143012", 0, () => true);

        Assert.Equal(["direct"], ArrangeRoutes(java));
        Assert.Equal(LiveOutcome.Noted, verdict.Outcome);
        Assert.StartsWith("A clip appeared in Bitwig's arranger on “Sample 143012” after the paste, in the colour the link gave the launcher clip in " +
                          "slot 1, but Bitwig rounded that colour to one of its own", verdict.Detail);
        Assert.EndsWith(" The link ran the copy, the paste and the focus actions straight away (direct)." + NotTried + "from Bitwig's flush (flush), " +
                        "on the link's own MIDI message (midi), on a press of the link's own button, run by the link (buttonCode) and by the " +
                        "link's own hardware button, run by Bitwig (button)" + LandsThere, verdict.Detail);
        Assert.Equal(["- Straight away (direct): a clip appeared on the track, but the extension couldn't be sure it was the run's"], verdict.Lines);
        Assert.Equal(("direct", true), ((string?)verdict.Data!["via"], (bool)verdict.Data["appeared"]!));
        Assert.Single(verdict.Data["attempts"]!.AsArray());

        // A later route the same: the routes before it are named, and the verdict is that route's, not a later one's.
        java.Requests.Clear();
        java.Answer = request => (string)request["cmd"]! != "arrange" ? null : (string?)request["via"] switch
        {
            "direct" or "flush" => ArrangeAnswer((string?)request["via"], false, AllRan),
            "midi" => RoundedAnswer("midi"),
            var via => AfterAPaste(via),
        };
        verdict = await BitwigLiveTests.ClipToArrangerAsync(link, "Sample 143012", 0, () => true);
        Assert.Equal(["direct", "flush", "midi"], ArrangeRoutes(java));
        Assert.Equal(LiveOutcome.Noted, verdict.Outcome);
        Assert.EndsWith(" The link ran the copy, the paste and the focus actions on the link's own MIDI message (midi), after straight away (direct) " +
                        "and from Bitwig's flush (flush) didn't put the clip there." + NotTried + "on a press of the link's own button, run by the " +
                        "link (buttonCode) and by the link's own hardware button, run by Bitwig (button)" + LandsThere, verdict.Detail);
        Assert.Equal(["- Straight away (direct): nothing appeared in the arranger",
                      "- From Bitwig's flush (flush): nothing appeared in the arranger",
                      "- On the link's own MIDI message (midi): a clip appeared on the track, but the extension couldn't be sure it was the run's"],
                     verdict.Lines);
        Assert.Equal("midi", (string?)verdict.Data!["via"]);

        // Pasted into the clip launcher instead: Failed, saying so, and no other route tried.
        java.Requests.Clear();
        java.Answer = request =>
        {
            if ((string)request["cmd"]! != "arrange") return null;
            var via = (string?)request["via"];
            if (via != "flush") return ArrangeAnswer(via, false, AllRan);
            var a = JsonNode.Parse(ArrangeAnswer(via, false, AllRan))!.AsObject();
            a["launcherClipsAfter"] = 2;
            return a.ToJsonString();
        };
        verdict = await BitwigLiveTests.ClipToArrangerAsync(link, "Sample 143012", 0, () => true);
        Assert.Equal(["direct", "flush"], ArrangeRoutes(java));
        Assert.Equal(LiveOutcome.Failed, verdict.Outcome);
        Assert.StartsWith("Bitwig didn't put the clip into its arranger at bar 5 (beat 14) on “Sample 143012”: it pasted into the clip launcher " +
                          "instead (“Sample 143012” has 2 clips there now, 1 before)", verdict.Detail);
        Assert.Contains(" The link ran the copy, the paste and the focus actions from Bitwig's flush (flush), after straight away (direct) didn't " +
                        "put the clip there." + NotTried + "on the link's own MIDI message (midi), ", verdict.Detail);
        Assert.Equal("- From Bitwig's flush (flush): Bitwig pasted into the clip launcher instead", verdict.Lines[^1]);

        // Pasted on another track: the same.
        java.Requests.Clear();
        java.Answer = request =>
        {
            if ((string)request["cmd"]! != "arrange") return null;
            var a = JsonNode.Parse(ArrangeAnswer((string?)request["via"], false, AllRan))!.AsObject();
            a["after"] = JsonNode.Parse("""{"exists":true,"track":"Bass"}""");
            return a.ToJsonString();
        };
        verdict = await BitwigLiveTests.ClipToArrangerAsync(link, "Sample 143012", 0, () => true);
        Assert.Equal(["direct"], ArrangeRoutes(java));
        Assert.Contains("the clip selected in the arranger is on “Bass”, not “Sample 143012”", verdict.Detail);
        Assert.Equal(["- Straight away (direct): the clip selected in the arranger is on “Bass” now, not the clip's track"], verdict.Lines);

        // A paste that didn't run put nothing down, whatever the extension saw: the next route is tried.
        java.Requests.Clear();
        java.Answer = request =>
        {
            if ((string)request["cmd"]! != "arrange") return null;
            var via = (string?)request["via"];
            if (via == "buttonCode") return ArrangeAnswer(via, true, AllRan);
            var a = JsonNode.Parse(ArrangeAnswer(via, false, """{"launcherFocus":true,"copy":true,"focus":true,"paste":false}"""))!.AsObject();
            a["after"] = JsonNode.Parse("""{"exists":true,"track":"Bass"}""");
            return a.ToJsonString();
        };
        verdict = await BitwigLiveTests.ClipToArrangerAsync(link, "Sample 143012", 0, () => true);
        Assert.Equal(["direct", "flush", "midi", "buttonCode"], ArrangeRoutes(java));
        Assert.Equal(LiveOutcome.Passed, verdict.Outcome);
        Assert.DoesNotContain(NotTried, verdict.Detail);
    }

    /// <summary>The extension 3.8's answer when Bitwig put the MIDI file on a new track, in the arranger, and selected its clip there.</summary>
    const string FileInArranger = """{"added":1,"index":3,"name":"Live Clip","type":"Instrument","tracksBefore":4,"tracksAfter":5,"launcherClips":0,"selected":true,"arranger":{"exists":true,"track":"Live Clip"},"arrangerBefore":{"exists":false,"track":null},"at":14,"onTrack":{"before":1,"after":1}}""";

    static BitwigFileTrack FileTrack(string json) => JsonSerializer.Deserialize<BitwigFileTrack>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public async Task InsertsTheMidiFileAsATrackAfterTheSampleTrack()
    {
        await using var java = new FakeJavaBitwig(ArrangingHello.Replace("\"3.6\"", "\"3.8\""),
                                                  new Dictionary<string, string>(SevenEight) { ["filetrack"] = FileInArranger });
        using var link = await BitwigLink.ConnectAsync(java.Port);

        var verdict = await BitwigLiveTests.FileAsTrackAsync(link, "Sample 143012", Path.Combine("files", "Live Clip.mid"));

        // After the Sample track (index 2), with the play start at bar 5 in 7/8: four bars of 3.5 beats.
        var sent = java.Requests.Last();
        Assert.Equal(("filetrack", 2, Path.Combine("files", "Live Clip.mid"), 14.0),
                     ((string)sent["cmd"]!, (int)sent["track"]!, (string)sent["path"]!, (double)sent["at"]!));
        Assert.Equal(LiveOutcome.Passed, verdict.Outcome);
        Assert.Equal("Bitwig put Live Clip.mid on a new track, “Live Clip” (track 4), with no clip in its launcher, and the clip selected in the " +
                     "arranger is on that track now, so the clip went in the arranger. The link can't read where an arranger clip starts: look at " +
                     "“Live Clip” in Bitwig's arranger and say whether the clip is there and which bar it starts at (it was meant to start at bar 5 " +
                     "(beat 14), where the link moved the play start), and where the play start marker is.", verdict.Detail);
        Assert.Equal(("Live Clip", 0), ((string?)verdict.Data!["name"], (int)verdict.Data["launcherClips"]!));

        // An older extension, or the script, hasn't got it: skipped, sending nothing.
        foreach (var (hello, skipped) in new[]
                 {
                     (ArrangingHello.Replace("\"3.6\"", "\"3.7\""), "This needs the Music Organiser extension 3.8 or later, and Bitwig runs 3.7. " +
                                                                    BitwigLiveTests.Reinstall),
                     (FakeJavaBitwig.Hello("""{"link":2,"runtime":null,"extension":null}"""), BitwigLiveTests.NeedsJava),
                 })
        {
            await using var older = new FakeJavaBitwig(hello, SevenEight);
            using var olderLink = await BitwigLink.ConnectAsync(older.Port);
            var v = await BitwigLiveTests.FileAsTrackAsync(olderLink, "Sample 143012", "Live Clip.mid");
            Assert.Equal((LiveOutcome.Skipped, skipped), (v.Outcome, v.Detail));
            Assert.DoesNotContain(older.Requests, r => (string)r["cmd"]! == "filetrack");
        }
    }

    [Fact]
    public void SaysWhereBitwigPutTheMidiFile()
    {
        LiveVerdict Verdict(Action<JsonObject> change)
        {
            var answer = JsonNode.Parse(FileInArranger)!.AsObject();
            change(answer);
            return BitwigLiveTests.FileTrackVerdict(FileTrack(answer.ToJsonString()), "Live Clip.mid", "Sample 143012", 5);
        }
        const string look = "Look at “Live Clip” in Bitwig's arranger and say whether the clip is there and which bar it starts at (it was meant to " +
                            "start at bar 5 (beat 14), where the link moved the play start), and where the play start marker is.";

        var launcher = Verdict(a => a["launcherClips"] = 1);
        Assert.Equal((LiveOutcome.Failed, "Bitwig put Live Clip.mid on a new track, “Live Clip” (track 4), but in its clip launcher (1 clip there), " +
                                          "not the arranger, so inserting a file between tracks doesn't put it in the arranger."),
                     (launcher.Outcome, launcher.Detail));

        var nothing = Verdict(a =>
        {
            a["added"] = 0;
            a["index"] = a["name"] = a["type"] = a["launcherClips"] = null;
            a["tracksAfter"] = 4;
        });
        Assert.Equal((LiveOutcome.Failed, "Bitwig didn't add a track for Live Clip.mid after “Sample 143012”: it still has 4 tracks."),
                     (nothing.Outcome, nothing.Detail));
        var onTheTrack = Verdict(a =>
        {
            a["added"] = 0;
            a["index"] = a["name"] = null;
            a["onTrack"] = JsonNode.Parse("""{"before":1,"after":2}""");
        });
        Assert.EndsWith(": it still has 4 tracks, though “Sample 143012” has 2 clips in the launcher now, 1 before.", onTheTrack.Detail);

        var unseen = Verdict(a => a["arranger"] = JsonNode.Parse("""{"exists":false,"track":null}"""));
        Assert.Equal((LiveOutcome.Noted, "Bitwig put Live Clip.mid on a new track, “Live Clip” (track 4), with no clip in its launcher, but no clip on " +
                                         "it is selected in the arranger, so the link can't tell whether the clip went there. " + look),
                     (unseen.Outcome, unseen.Detail));
        // An earlier run's “Live Clip” clip was selected already: a clip selected after on a track of that name says nothing.
        var same = Verdict(a => a["arrangerBefore"] = JsonNode.Parse("""{"exists":true,"track":"Live Clip"}"""));
        Assert.Equal((LiveOutcome.Noted, "Bitwig put Live Clip.mid on a new track, “Live Clip” (track 4), with no clip in its launcher, but a clip on a " +
                                         "track called “Live Clip” was selected in the arranger before too, and the link tells those clips apart only " +
                                         "by their track's name, so it can't tell whether a new one appeared. " + look),
                     (same.Outcome, same.Detail));
        // Clip to arranger's clip on the Sample track stayed selected: no clip on “Live Clip” is.
        var elsewhere = Verdict(a =>
        {
            a["arrangerBefore"] = JsonNode.Parse("""{"exists":true,"track":"Sample 143012"}""");
            a["arranger"] = JsonNode.Parse("""{"exists":true,"track":"Sample 143012"}""");
        });
        Assert.Equal((LiveOutcome.Noted, unseen.Detail), (elsewhere.Outcome, elsewhere.Detail));
        var lost = Verdict(a => a["index"] = null);
        Assert.Equal((LiveOutcome.Noted, "Bitwig added 1 track for Live Clip.mid, but the link couldn't find the new one in its listing. Look after " +
                                         "“Sample 143012” in Bitwig and say what's there."),
                     (lost.Outcome, lost.Detail));
    }

    [Theory]
    [InlineData("4/4", 4)]
    [InlineData("7/8", 3.5)]
    [InlineData("6/8", 3)]
    [InlineData("3/4", 3)]
    [InlineData("5/16", 1.25)]
    [InlineData("", 4)]
    [InlineData("x/4", 4)]
    [InlineData("4/0", 4)]
    public void CountsABarsBeatsAsBitwigCountsThePlayStart(string timeSignature, double beats) =>
        Assert.Equal(beats, BitwigLiveTests.BarBeats(timeSignature));

    [Fact]
    public async Task FormatLabSaysWhatItNeedsBeforeConnecting()
    {
        Assert.Contains("formatlab live bitwig [--port N] [--reports <folder>]", (await FormatLab("help")).Out);
        var nothing = await FormatLab("live");
        Assert.Equal(2, nothing.Code);
        Assert.StartsWith("Format Lab: live needs bitwig (FL Studio's live tests come once its link works).", nothing.Err);
        Assert.Equal(2, (await FormatLab("live", "fl")).Code);
        Assert.Equal(2, (await FormatLab("live", "bitwig", "now")).Code);
        var port = await FormatLab("live", "bitwig", "--port", "70000");
        Assert.Equal(2, port.Code);
        Assert.StartsWith("Format Lab: “70000” isn't a port number.", port.Err);
        Assert.Equal(2, (await FormatLab("live", "bitwig", "--reports")).Code);
    }

    [Fact]
    public async Task FormatLabRunsTheLiveTests()
    {
        var dir = Fixtures.TempDir();

        // Bitwig isn't answering: Hello fails, the rest say why they weren't tried, and the report is still written.
        var closed = await FormatLab("live", "bitwig", "--port", ViewModelTests.ClosedPort().ToString(CultureInfo.InvariantCulture),
                                     "--reports", Path.Combine(dir, "closed"));
        Assert.Equal(1, closed.Code);
        Assert.Contains("Ctrl+C stops them", closed.Err);
        var lines = closed.Out.Split(Environment.NewLine);
        Assert.Matches(@"^  ✗ Link › Hello( \([^)]+\))?: " + BitwigLink.NotRunning[..20], lines[0]);   // no time when under half a millisecond
        Assert.Matches(@"^  – Link › Project listing( \(.+\))?: Bitwig isn't answering \(see Link › Hello\), so this wasn't tried\.$", lines[1]);
        Assert.Matches(@"^  – Link › Selected device( \(.+\))?$", lines[2]);   // the same reason again is said once
        Assert.Contains(Environment.NewLine + Environment.NewLine + "0 passed, 1 failed, ", closed.Out);
        var report = Directory.GetFiles(Path.Combine(dir, "closed"), "*-bitwig.md").Single();
        Assert.Contains($"Report: {report}{Environment.NewLine}", closed.Out);
        Assert.Contains($"Everything it saw, as JSON: {Path.ChangeExtension(report, ".json")}", closed.Out);

        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        if (bitwig is null) return;
        using var link = await BitwigLink.ConnectAsync(bitwig.Port);
        var before = Listing(await link.ProjectAsync());

        // Bitwig has “Night Drive” open, which isn't a project for the tests, so they only read; nothing outside the
        // report folder given is reached.
        var run = await FormatLab("live", "bitwig", "--port", bitwig.Port.ToString(CultureInfo.InvariantCulture), "--reports", Path.Combine(dir, "night"));
        Assert.True(run.Code == 0, run.Out + run.Err);
        Assert.Matches(@"(?m)^  ✓ Link › Hello( \([^)]+\))?\r?$", run.Out);
        Assert.Matches(@"(?m)^  – Changes › Changes allowed( \(.+\))?: Bitwig has “Night Drive” open, so the run changed nothing", run.Out);
        Assert.Matches(@"(?m)^  – Changes › New track( \(.+\))?\r?$", run.Out);
        Assert.DoesNotContain("✗", run.Out);
        Assert.Matches(@"(?m)^\d+ passed, 0 failed, \d+ skipped, in [\d.]+ s\.\r?$", run.Out);
        Assert.Contains($"Report: {Directory.GetFiles(Path.Combine(dir, "night"), "*-bitwig.md").Single()}", run.Out);
        Assert.Equal(before, Listing(await link.ProjectAsync()));
    }

    /// <summary>
    /// Runs formatlab on a thread of its own, as it waits for Bitwig the way a terminal does: not on one of the shared
    /// threads, which the tests running alongside (the knob checks' timings) need.
    /// </summary>
    static Task<(int Code, string Out, string Err)> FormatLab(params string[] args) => Task.Factory.StartNew(() =>
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var code = FormatLabCommand.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}

/// <summary>
/// MO Probes standing in for the real one inside the stand-in Bitwig: one starts whenever serve.js says a probe was added to
/// a track, and follows its knobs both ways, as the real one does through Bitwig. As a VST3 it doesn't know its track.
/// </summary>
sealed class ProbeBridge(string folder, BitwigLinkTests.ScriptServer bitwig) : IAsyncDisposable
{
    readonly ConcurrentDictionary<string, FakeProbe> _onTrack = new();

    /// <summary>The probe added as <paramref name="format"/>.</summary>
    public FakeProbe Probe(string format) => _onTrack.Values.Single(p => p.Format == format);

    /// <summary>A line serve.js wrote on stderr.</summary>
    public void Heard(string line)
    {
        if (!line.StartsWith('{')) return;   // anything else Node.js says
        var e = JsonNode.Parse(line)!;
        var track = (string)e["track"]!;
        switch ((string)e["event"]!)
        {
            case "probe":
                var format = (string)e["format"]!;
                var probe = new FakeProbe(folder, Guid.NewGuid().ToString("N")[..16].ToUpperInvariant(), format == "VST3" ? "" : track, format);
                probe.Turned = (knob, value) => bitwig.Change(new JsonObject
                {
                    ["turn"] = new JsonObject { ["track"] = track, ["id"] = $"CONTENTS/PID{knob:x}", ["value"] = ProbeKnobs.Normalized(knob, value) },
                }.ToJsonString());
                _onTrack[track] = probe;
                break;
            case "set" when _onTrack.TryGetValue(track, out var on):
                var id = Convert.ToInt32(((string)e["id"]!).Split("PID")[1], 16);
                on[id] = ProbeKnobs.Plain(id, (double)e["value"]!);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var probe in _onTrack.Values) await probe.DisposeAsync();
    }
}

/// <summary>
/// The Java extension for Bitwig as the app sees it: the script's commands with its newer fields, and its own commands, in
/// canned answers. Changes are off, unless a test gives a hello of its own; a test can give other answers too, or an error
/// for a command (<see cref="Errors"/>), or work an answer or an error out from each request (<see cref="Answer"/>, <see
/// cref="Refuse"/>), as for the routes, whose answers depend on the route asked for.
/// </summary>
sealed class FakeJavaBitwig : IAsyncDisposable
{
    readonly TcpListener _server = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly Task _serving;
    readonly string _hello;
    readonly Dictionary<string, string> _answers = new(Answers);

    public FakeJavaBitwig(string? hello = null, IReadOnlyDictionary<string, string>? answers = null)
    {
        _hello = hello ?? Answers["hello"];
        foreach (var (cmd, answer) in answers ?? new Dictionary<string, string>()) _answers[cmd] = answer;
        _server.Start();
        _serving = Task.Run(ServeAsync);
    }

    /// <summary>Commands the stand-in answers with an error, by name: what the error says. Set before sending the command.</summary>
    public Dictionary<string, string> Errors { get; } = new();

    /// <summary>
    /// The answer to a request (its result, as JSON), worked out from the request, or null for the canned one. It's asked for
    /// the hello the stand-in sends as the app connects too, as a request {"cmd": "hello"}. It runs on the stand-in's own thread.
    /// </summary>
    public Func<JsonNode, string?>? Answer { get; set; }

    /// <summary>An error to answer a request with, worked out from the request, or null to answer it. On the stand-in's own thread.</summary>
    public Func<JsonNode, string?>? Refuse { get; set; }

    /// <summary>The canned answer to a command, as JSON, for a test to build its own on.</summary>
    public static string Canned(string cmd) => Answers[cmd];

    /// <summary>The canned hello with some of its fields changed or added, e.g. {"extension": "3.7", "loopback": true}.</summary>
    public static string Hello(string fields)
    {
        var hello = JsonNode.Parse(Answers["hello"])!.AsObject();
        foreach (var (name, value) in JsonNode.Parse(fields)!.AsObject()) hello[name] = value?.DeepClone();
        return hello.ToJsonString();
    }

    public int Port => ((IPEndPoint)_server.LocalEndpoint).Port;

    static readonly Dictionary<string, string> Answers = new()
    {
        ["hello"] = """{"link":3,"product":"Bitwig Studio","version":"6.1.3","api":23,"platform":"WINDOWS","port":19791,"project":"Night Drive","modified":false,"engine":true,"changes":false,"runtime":"java","extension":"3.0","onlyIn":true}""",
        ["project"] = """{"name":"Night Drive","modified":false,"tempo":124,"timeSignature":"4/4","playing":false,"position":0,"trackCount":2,"tracks":[{"index":0,"id":"7f2c","name":"Bass","type":"Instrument","group":false,"color":"#ff8000","active":true,"mute":false,"solo":false,"arm":false,"volume":"0.0 dB","volumeValue":0.79,"pan":"C","panValue":0.5,"deviceCount":1,"devices":[{"index":0,"name":"Serum","preset":"","kind":"note_to_audio","plugin":true,"enabled":true}]},{"index":1,"id":"7f2d","name":"Master","type":"Master","group":false,"color":"#000000","active":true,"mute":false,"solo":false,"arm":false,"volume":"0.0 dB","volumeValue":0.79,"pan":"C","panValue":0.5,"deviceCount":0,"devices":[]}]}""",
        ["device"] = """{"track":"Bass","device":{"index":-1,"name":"Serum","preset":"","kind":"note_to_audio","plugin":true,"enabled":true},"params":[{"id":"CONTENTS/PID0","name":"Cutoff","value":0.25,"display":"440 Hz","steps":-1,"origin":0,"automated":true},{"id":"CONTENTS/PID1","name":"Mode","value":0,"display":"Saw"},{"id":"CONTENTS/PID2","name":"Drive","value":0.1,"display":"10 %"}],"drumPads":false,"page":"Main","controls":[{"index":0,"name":"Cutoff","value":0.25,"display":"440 Hz","steps":-1,"origin":0,"automated":true,"param":"CONTENTS/PID0"},{"index":1,"name":"Mode","value":0,"display":"Saw","steps":3,"stepNames":["Saw","Square","Noise"],"origin":0,"automated":false,"param":null}]}""",
        ["actions"] = """[{"id":"Save","name":"Save","category":"File","menu":"Save"}]""",
        ["clicked"] = """{"exists":true,"name":"Cutoff","value":0.25,"display":"440 Hz","steps":-1,"origin":0,"automated":true,"locked":false}""",
        ["selftest"] = """{"time":"2026-10-14T14:30:12Z","why":"asked","ok":false,"extension":"3.0","link":3,"file":"C:\\reports\\bitwig\\x-selfcheck.json","checks":[{"name":"remote connection","ok":true,"detail":"listening on 19791","ms":1},{"name":"cursor device","ok":true,"detail":"","ms":0},{"name":"reports folder","ok":false,"detail":"Access is denied.","ms":2}]}""",
        // Extension 3.6's experiment, as it answers when Bitwig put the clip in its arranger: the clip that appeared there has
        // the colour the extension marked the launcher clip with.
        ["arrange"] = """{"arranged":true,"appeared":true,"sameAsSource":true,"copiedFromTimeline":false,"at":16,"layout":"ARRANGE","launcher":{"before":false,"after":true},"mark":{"before":"#3366CC","asked":"#416DD9","got":"#416DD9","took":true,"exact":true},"launcherFocus":1,"timeline":{"before":true,"after":true},"focus":1,"before":{"exists":false,"track":null},"beforePaste":{"exists":false,"track":null},"after":{"exists":true,"track":"Sample 143012"},"pastedColor":"#416DD9","launcherClipsBefore":1,"launcherClipsAfter":1}""",
    };

    /// <summary>Every request that came in, as it came.</summary>
    public ConcurrentQueue<JsonNode> Requests { get; } = new();

    async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                var client = await _server.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => TalkAsync(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    async Task TalkAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            try
            {
                await SendAsync(stream, $$"""{"event":"hello","result":{{Answer?.Invoke(new JsonObject { ["cmd"] = "hello" }) ?? _hello}}}""");
                var head = new byte[4];
                while (true)
                {
                    await stream.ReadExactlyAsync(head, _stop.Token);
                    var body = new byte[BinaryPrimitives.ReadInt32BigEndian(head)];
                    await stream.ReadExactlyAsync(body, _stop.Token);
                    var request = JsonNode.Parse(body)!;
                    Requests.Enqueue(request);
                    var id = (int)request["id"]!;
                    var cmd = (string)request["cmd"]!;
                    await SendAsync(stream, (Errors.TryGetValue(cmd, out var error) ? error : Refuse?.Invoke(request)) is { } refused
                        ? new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = refused }.ToJsonString()
                        : (Answer?.Invoke(request) ?? (cmd == "hello" ? _hello : _answers.GetValueOrDefault(cmd))) is { } answer
                        ? $$"""{"id":{{id}},"ok":true,"result":{{answer}}}"""
                        : $$"""{"id":{{id}},"ok":false,"error":"Unknown command: {{cmd}}"}""");
                }
            }
            catch (Exception e) when (e is IOException or EndOfStreamException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    static async Task SendAsync(NetworkStream stream, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _server.Stop();
        await _serving;
    }
}
