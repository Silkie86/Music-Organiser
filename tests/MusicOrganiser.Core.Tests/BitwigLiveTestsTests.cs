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

    [Fact]
    public async Task RunsEveryAreaInATestProject()
    {
        var dir = Fixtures.TempDir();
        ProbeBridge? bridge = null;
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false, line => bridge?.Heard(line));
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
                          "Changes › Sampler's parameter details", "Changes › Master recorder"];
        Assert.All(newer, n => Assert.Equal((LiveOutcome.Skipped, BitwigLiveTests.NeedsJava),
                                            run.Results.Where(r => r.Title == n).Select(r => (r.Outcome, r.Detail)).Single()));
        // The stand-in saves no file, so there's no saved project to compare: skipped, saying so.
        Assert.StartsWith("Bitwig saved “MO Live Test”, but the app didn't find a MO Live Test.bwproject saved since",
                          Find(run, "Saved file", "Find the saved project").Detail);
        Assert.Equal(LiveOutcome.Skipped, Find(run, "Saved file", "Compare with Bitwig").Outcome);
        var skipped = run.Results.Where(r => r.Outcome == LiveOutcome.Skipped).Select(r => r.Title).ToList();
        Assert.Equal([.. newer, "Saved file › Find the saved project", "Saved file › Compare with Bitwig"], skipped);
        Assert.Equal(8, run.Skipped);
        Assert.All(["Link", "Changes", "Send"], area => Assert.DoesNotContain(run.Results, r => r.Area == area && r.Outcome == LiveOutcome.Failed));

        // What each test saw.
        var tag = run.Stamp[9..15];
        Assert.Equal($"Bitwig added track 3, “Devices {tag}”, coloured #3366cc as asked.", Find(run, "Changes", "New track").Detail);
        Assert.Equal("Bitwig set the Sampler to -3.50 st, -3.5 semitones as asked, and read it back.", Find(run, "Changes", "Transpose a Sampler").Detail);
        Assert.Equal("Bitwig set the Note Transpose to +5 st, +5 semitones as asked, and read it back.", Find(run, "Changes", "Transpose a Note Transpose").Detail);
        Assert.Equal("Bitwig put each sample on its pad: Live Kick on C1, Live Snare on D1, Live Hat on F#1.", Find(run, "Changes", "Drum pads").Detail);
        Assert.StartsWith("Set “Gain” on the Sampler from 0.5 (“0.0 dB”) to 0.25, and Bitwig showed 0.25", Find(run, "Changes", "Set a parameter").Detail);
        Assert.StartsWith("Bitwig saved “MO Live Test”: its unsaved changes cleared", Find(run, "Changes", "Save").Detail);
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
        Assert.Equal(27, changes.Count);
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
        Assert.Matches(@"^  ✗ Link › Hello \(\d.*\): " + BitwigLink.NotRunning[..20], lines[0]);
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
        Assert.Matches(@"(?m)^  ✓ Link › Hello \(", run.Out);
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
/// canned answers. Changes are off.
/// </summary>
sealed class FakeJavaBitwig : IAsyncDisposable
{
    readonly TcpListener _server = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly Task _serving;

    public FakeJavaBitwig()
    {
        _server.Start();
        _serving = Task.Run(ServeAsync);
    }

    public int Port => ((IPEndPoint)_server.LocalEndpoint).Port;

    static readonly Dictionary<string, string> Answers = new()
    {
        ["hello"] = """{"link":3,"product":"Bitwig Studio","version":"6.1.3","api":23,"platform":"WINDOWS","port":19791,"project":"Night Drive","modified":false,"engine":true,"changes":false,"runtime":"java","extension":"3.0"}""",
        ["project"] = """{"name":"Night Drive","modified":false,"tempo":124,"timeSignature":"4/4","playing":false,"position":0,"trackCount":2,"tracks":[{"index":0,"id":"7f2c","name":"Bass","type":"Instrument","group":false,"color":"#ff8000","active":true,"mute":false,"solo":false,"arm":false,"volume":"0.0 dB","volumeValue":0.79,"pan":"C","panValue":0.5,"deviceCount":1,"devices":[{"index":0,"name":"Serum","preset":"","kind":"note_to_audio","plugin":true,"enabled":true}]},{"index":1,"id":"7f2d","name":"Master","type":"Master","group":false,"color":"#000000","active":true,"mute":false,"solo":false,"arm":false,"volume":"0.0 dB","volumeValue":0.79,"pan":"C","panValue":0.5,"deviceCount":0,"devices":[]}]}""",
        ["device"] = """{"track":"Bass","device":{"index":-1,"name":"Serum","preset":"","kind":"note_to_audio","plugin":true,"enabled":true},"params":[{"id":"CONTENTS/PID0","name":"Cutoff","value":0.25,"display":"440 Hz","steps":-1,"origin":0,"automated":true},{"id":"CONTENTS/PID1","name":"Mode","value":0,"display":"Saw"},{"id":"CONTENTS/PID2","name":"Drive","value":0.1,"display":"10 %"}],"drumPads":false,"page":"Main","controls":[{"index":0,"name":"Cutoff","value":0.25,"display":"440 Hz","steps":-1,"origin":0,"automated":true,"param":"CONTENTS/PID0"},{"index":1,"name":"Mode","value":0,"display":"Saw","steps":3,"stepNames":["Saw","Square","Noise"],"origin":0,"automated":false,"param":null}]}""",
        ["actions"] = """[{"id":"Save","name":"Save","category":"File","menu":"Save"}]""",
        ["clicked"] = """{"exists":true,"name":"Cutoff","value":0.25,"display":"440 Hz","steps":-1,"origin":0,"automated":true,"locked":false}""",
        ["selftest"] = """{"time":"2026-10-14T14:30:12Z","why":"asked","ok":false,"extension":"3.0","link":3,"file":"C:\\reports\\bitwig\\x-selfcheck.json","checks":[{"name":"remote connection","ok":true,"detail":"listening on 19791","ms":1},{"name":"cursor device","ok":true,"detail":"","ms":0},{"name":"reports folder","ok":false,"detail":"Access is denied.","ms":2}]}""",
    };

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
                await SendAsync(stream, $$"""{"event":"hello","result":{{Answers["hello"]}}}""");
                var head = new byte[4];
                while (true)
                {
                    await stream.ReadExactlyAsync(head, _stop.Token);
                    var body = new byte[BinaryPrimitives.ReadInt32BigEndian(head)];
                    await stream.ReadExactlyAsync(body, _stop.Token);
                    var request = JsonNode.Parse(body)!;
                    var id = (int)request["id"]!;
                    var cmd = (string)request["cmd"]!;
                    await SendAsync(stream, Answers.TryGetValue(cmd, out var answer)
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
