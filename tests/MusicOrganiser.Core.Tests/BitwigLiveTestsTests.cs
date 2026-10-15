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
        string[] newer = ["Link › Track IDs", "Link › Parameter details", "Link › Last clicked parameter", "Link › Self-check",
                          "Changes › Clip to arranger", "Changes › Sampler's parameter details", "Changes › Master recorder"];
        Assert.All(newer, n => Assert.Equal((LiveOutcome.Skipped, BitwigLiveTests.NeedsJava),
                                            run.Results.Where(r => r.Title == n).Select(r => (r.Outcome, r.Detail)).Single()));
        // The stand-in saves no file, so there's no saved project to compare: skipped, saying so.
        Assert.StartsWith("Bitwig saved “MO Live Test”, but the app didn't find a MO Live Test.bwproject saved since",
                          Find(run, "Saved file", "Find the saved project").Detail);
        Assert.Equal(LiveOutcome.Skipped, Find(run, "Saved file", "Compare with Bitwig").Outcome);
        var skipped = run.Results.Where(r => r.Outcome == LiveOutcome.Skipped).Select(r => r.Title).ToList();
        Assert.Equal([.. newer, "Saved file › Find the saved project", "Saved file › Compare with Bitwig"], skipped);
        Assert.Equal(9, run.Skipped);
        Assert.All(["Link", "Changes", "Send"], area => Assert.DoesNotContain(run.Results, r => r.Area == area && r.Outcome == LiveOutcome.Failed));

        // What each test saw.
        var tag = run.Stamp[9..15];
        Assert.Equal($"Bitwig added track 3, “Devices {tag}”, coloured #3366cc as asked.", Find(run, "Changes", "New track").Detail);
        Assert.Equal("Bitwig set the Sampler to -3.50 st, -3.5 semitones as asked, and read it back.", Find(run, "Changes", "Transpose a Sampler").Detail);
        Assert.Equal("Bitwig set the Note Transpose to +5 st, +5 semitones as asked, and read it back.", Find(run, "Changes", "Transpose a Note Transpose").Detail);
        Assert.Equal("Bitwig put each sample on its pad: Live Kick on C1, Live Snare on D1, Live Hat on F#1.", Find(run, "Changes", "Drum pads").Detail);
        Assert.StartsWith("Set “Gain” on the Sampler from 0.5 (“0.0 dB”) to 0.25, and Bitwig showed 0.25", Find(run, "Changes", "Set a parameter").Detail);
        var save = Find(run, "Changes", "Save");
        Assert.StartsWith("Bitwig saved “MO Live Test”: its unsaved changes cleared", save.Detail);
        // The script said which project was in front as Save ran; the first Save did it, so there was no second.
        Assert.Equal("MO Live Test", (string?)save.Data!["projectAtSave"]);
        Assert.Null(save.Data["secondSave"]);
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
                                          !r.Name.StartsWith("Last") && !r.Name.StartsWith("Self")),
                   r => Assert.Equal(LiveOutcome.Passed, r.Outcome));
        var changes = run.Results.Where(r => r.Area != "Link").ToList();
        Assert.Equal(28, changes.Count);
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

    [Fact]
    public async Task SavesASecondTimeOnlyWhenTheFirstSaveLeavesChanges()
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

        // Bitwig ignores the first Save, as Bitwig 6.1.3 once did: the run saves again, and that one works.
        bitwig.Change("""{"ignoreSaves": 1}""");
        var run = await BitwigLiveTests.RunAsync(options);
        var save = Find(run, "Changes", "Save");
        Assert.Equal(LiveOutcome.Passed, save.Outcome);
        Assert.StartsWith("Only the second Save worked. Bitwig still showed unsaved changes 2 s after the first Save (“Save”)", save.Detail);
        Assert.Contains("a second Save, run 1 s after that, cleared them", save.Detail);
        Assert.Equal("MO Live Test", (string?)save.Data!["projectAtSave"]);
        Assert.Equal(("MO Live Test", true), ((string?)save.Data["secondSave"]!["project"], (bool?)save.Data["secondSave"]!["cleared"]));
        Assert.Equal(2, await Settled(() => saves, 2));
        // What follows a Save that worked goes ahead: the stand-in writes no file, so it's only the file that isn't found.
        Assert.StartsWith("Bitwig saved “MO Live Test”, but the app didn't find", Find(run, "Saved file", "Find the saved project").Detail);

        // Bitwig ignores both: Failed, saying so, and that the test project was in front as Save ran.
        bitwig.Change("""{"ignoreSaves": 2}""");
        run = await BitwigLiveTests.RunAsync(options);
        save = Find(run, "Changes", "Save");
        Assert.Equal(LiveOutcome.Failed, save.Outcome);
        Assert.Contains("A second Save, run 1 s after that, didn't help either: Bitwig still showed unsaved changes 2 s after it.", save.Detail);
        Assert.Contains("Bitwig said the test project, “MO Live Test”, was the active project when Save ran.", save.Detail);
        Assert.Equal(false, (bool?)save.Data!["secondSave"]!["cleared"]);
        Assert.Equal(4, await Settled(() => saves, 4));
        Assert.Equal("Needs Changes › Save, which didn't work.", Find(run, "Saved file", "Find the saved project").Detail);
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
        // Changes are off there, so the rest is skipped.
        Assert.All(run.Results.Where(r => r.Area != "Link"), r => Assert.Equal(LiveOutcome.Skipped, r.Outcome));
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
        LiveVerdict Save(bool stillModified, string? path, DateTime? before, DateTime? after) =>
            BitwigLiveTests.SaveVerdict("MO Live Test", "Save", true, stillModified, TimeSpan.FromMilliseconds(120), wait, path, before, after);

        var cleared = Save(false, file, then, now);
        Assert.Equal(LiveOutcome.Passed, cleared.Outcome);
        Assert.Equal($"Bitwig saved “MO Live Test”: its unsaved changes cleared 120 ms after Save, and wrote {file}.", cleared.Detail);
        Assert.Equal("2026-10-15T08:43:00.000Z", (string?)cleared.Data!["writtenBefore"]);
        Assert.Equal("2026-10-15T08:43:25.000Z", (string?)cleared.Data!["writtenAfter"]);

        // Written, though the unsaved changes didn't clear: saved, and the detail says what looked odd.
        var stayed = Save(true, file, then, now);
        Assert.Equal(LiveOutcome.Passed, stayed.Outcome);
        Assert.StartsWith($"Bitwig wrote {file} after Save, but still showed unsaved changes 15 s later", stayed.Detail);
        Assert.Equal(true, (bool?)stayed.Data!["modifiedAfter"]);

        // A first save: no file before, one after.
        Assert.Equal(LiveOutcome.Passed, Save(true, file, null, now).Outcome);

        var unwritten = Save(true, file, then, then);
        Assert.Equal(LiveOutcome.Failed, unwritten.Outcome);
        Assert.StartsWith($"Bitwig still showed unsaved changes 15 s after Save (“Save”), and didn't write {file}.", unwritten.Detail);

        var unfound = Save(true, null, null, null);
        Assert.Equal(LiveOutcome.Failed, unfound.Outcome);
        Assert.Contains("the app didn't find the project's file to see whether it was written", unfound.Detail);
        Assert.Null(unfound.Data!["file"]);

        // Whether Bitwig's window was in front, which Save may need.
        LiveVerdict Front(bool? front) =>
            BitwigLiveTests.SaveVerdict("MO Live Test", "Save", true, true, TimeSpan.FromMilliseconds(120), wait, file, then, then, front);
        Assert.Contains($"didn't write {file}, though its window was in front.", Front(true).Detail);
        Assert.Contains($"didn't write {file}. The app couldn't bring Bitwig's window to the front, which Save may need: click on Bitwig while " +
                        "the run saves.", Front(false).Detail);
        Assert.Equal(false, (bool?)Front(false).Data!["front"]);
        Assert.Null(unfound.Data!["front"]);
    }

    [Fact]
    public void SaveSaysWhenOnlyTheSecondSaveWorked()
    {
        const string file = "K:/Bitwig Studio/Projects/MO Live Test/MO Live Test.bwproject";
        var then = new DateTime(2026, 10, 15, 8, 43, 0, DateTimeKind.Utc);
        var now = then.AddSeconds(32);
        var wait = TimeSpan.FromSeconds(15);
        LiveVerdict Save(bool stillModified, DateTime? after, string? active, BitwigLiveTests.SecondSave? second, bool? front = true) =>
            BitwigLiveTests.SaveVerdict("MO Live Test", "Save", true, stillModified, TimeSpan.FromMilliseconds(120), wait, file, then, after, front,
                                        active, second);

        // The first Save did it: no second one, and the project Bitwig had in front is in the data.
        var first = Save(false, now, "MO Live Test", null);
        Assert.Equal(LiveOutcome.Passed, first.Outcome);
        Assert.Equal($"Bitwig saved “MO Live Test”: its unsaved changes cleared 120 ms after Save, and wrote {file}.", first.Detail);
        Assert.Equal("MO Live Test", (string?)first.Data!["projectAtSave"]);
        Assert.Null(first.Data["secondSave"]);

        // The first left the changes and didn't write the file; a second, a second later, cleared them and wrote it.
        var again = new BitwigLiveTests.SecondSave("MO Live Test", true, TimeSpan.FromMilliseconds(300), now);
        var onlySecond = Save(true, then, "MO Live Test", again);
        Assert.Equal(LiveOutcome.Passed, onlySecond.Outcome);
        Assert.Equal($"Only the second Save worked. Bitwig still showed unsaved changes 15 s after the first Save (“Save”), and didn't write {file}, " +
                     $"with its window in front; a second Save, run 1 s after that, cleared them 300 ms after it ran, and wrote {file}. That matters " +
                     "for how the app saves projects in Bitwig: one Save wasn't enough.", onlySecond.Detail);
        Assert.Equal(true, (bool?)onlySecond.Data!["modifiedAfter"]);
        Assert.Equal("""{"project":"MO Live Test","cleared":true,"tookMs":300,"written":true,"writtenAfter":"2026-10-15T08:43:32.000Z"}""",
                     onlySecond.Data["secondSave"]!.ToJsonString());

        // A second Save that wrote the file, though the changes stayed, worked too.
        var wroteOnly = Save(true, then, "MO Live Test", again with { Cleared = false, Took = wait });
        Assert.Equal(LiveOutcome.Passed, wroteOnly.Outcome);
        Assert.Contains($"a second Save, run 1 s after that, wrote {file}, though Bitwig still showed unsaved changes 15 s after it.", wroteOnly.Detail);
        Assert.StartsWith("Only the second Save worked.", wroteOnly.Detail);

        // Both failed: it says so, and which project Bitwig had in front as Save ran (the test project here).
        var neither = again with { Cleared = false, Took = wait, WrittenAfter = then };
        var both = Save(true, then, "MO Live Test", neither);
        Assert.Equal(LiveOutcome.Failed, both.Outcome);
        Assert.Equal($"Bitwig still showed unsaved changes 15 s after Save (“Save”), and didn't write {file}, though its window was in front. A second " +
                     $"Save, run 1 s after that, didn't help either: Bitwig still showed unsaved changes 15 s after it, and {file} wasn't written. Bitwig " +
                     "said the test project, “MO Live Test”, was the active project when Save ran. If Bitwig opened a Save As window, the project hasn't " +
                     "been saved yet: save it as “MO Live Test”, then run again.", both.Detail);
        Assert.Equal(false, (bool?)both.Data!["secondSave"]!["cleared"]);
        Assert.Equal(false, (bool?)both.Data["secondSave"]!["written"]);

        // Another project in front: named, and the front message stays.
        var elsewhere = Save(true, then, "Night Drive", neither with { Project = "Night Drive" }, front: false);
        Assert.Equal(LiveOutcome.Failed, elsewhere.Outcome);
        Assert.Contains($"didn't write {file}. The app couldn't bring Bitwig's window to the front, which Save may need: click on Bitwig while the run " +
                        "saves. A second Save, run 1 s after that, didn't help either", elsewhere.Detail);
        Assert.Contains(" Bitwig said “Night Drive” was the active project when Save ran, not the test project, “MO Live Test”. If Bitwig opened",
                        elsewhere.Detail);
        Assert.Equal("Night Drive", (string?)elsewhere.Data!["projectAtSave"]);
        Assert.Contains(" At the second Save it said “MO Live Test”.", Save(true, then, "Night Drive", neither).Detail);

        // A link older than extension 3.7 doesn't say which project was in front: the verdicts still come, without it.
        var older = Save(false, now, null, null);
        Assert.Equal(first.Detail, older.Detail);
        Assert.Null(older.Data!["projectAtSave"]);
        Assert.StartsWith("Only the second Save worked.", Save(true, then, null, again with { Project = null }).Detail);
        Assert.DoesNotContain("active project", Save(true, then, null, again with { Project = null }).Detail);
        var olderBoth = Save(true, then, null, neither with { Project = null });
        Assert.Equal(LiveOutcome.Failed, olderBoth.Outcome);
        Assert.Contains("didn't help either", olderBoth.Detail);
        Assert.Contains(" The link in Bitwig didn't say which project was active when Save ran (an older extension doesn't). If Bitwig opened",
                        olderBoth.Detail);
        Assert.Null(olderBoth.Data!["secondSave"]!["project"]);
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
             "launcherClipsBefore":1,"launcherClipsAfter":1,"front":true}
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
/// for a command (<see cref="Errors"/>).
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
                await SendAsync(stream, $$"""{"event":"hello","result":{{_hello}}}""");
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
                    await SendAsync(stream, Errors.TryGetValue(cmd, out var error)
                        ? new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = error }.ToJsonString()
                        : (cmd == "hello" ? _hello : _answers.GetValueOrDefault(cmd)) is { } answer
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
