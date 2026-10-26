using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Formats;
using MusicOrganiser.Core.LiveTests;

namespace MusicOrganiser.Core.Tests;

/// <summary>
/// MO Probe's live link: the app reading the status of probes running in a DAW and sending them commands, and formatlab's
/// probe commands. A stand-in probe plays the plugin's side, in a temp folder.
/// </summary>
public class MoProbeLiveTests
{
    const string Instance = "1683A80113A64904";

    /// <summary>A status as the probe writes it (native/README.md, "MO Probe").</summary>
    static string Status(string live, string instance, DateTimeOffset? t, string host = "Bitwig Studio", string vendor = "Bitwig GmbH",
                         string version = "6.1.3", string format = "CLAP", string report = "20261014T091855Z-instrument-1683A801.jsonl")
    {
        Directory.CreateDirectory(Path.Combine(live, instance + ".in"));
        var file = Path.Combine(live, instance + ".json");
        var time = t is { } at ? $"\"t\":\"{at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}\"," : "";
        File.WriteAllText(file,
            $"{{\"probe\":\"instrument\",\"instance\":\"{instance}\",\"identity\":\"{instance}\",\"label\":\"\",\"host\":\"{host}\"," +
            $"\"hostVendor\":\"{vendor}\",\"hostVersion\":\"{version}\",\"format\":\"{format}\",\"track\":\"Inst 3\",\"trackColor\":\"#C69F6F\"," +
            $"\"report\":\"{report}\",{time}\"pid\":1234,\"active\":true,\"playing\":false,\"beats\":12.5,\"bar\":4,\"tempo\":110," +
            "\"commands\":3,\"params\":{\"1001\":0.25,\"1002\":0,\"1003\":440,\"1004\":3,\"1005\":0,\"1006\":1,\"1007\":50,\"1008\":0}}");
        return file;
    }

    [Fact]
    public void ListsTheProbesRunningNowAndTidiesAwayOldOnes()
    {
        var dir = Fixtures.TempDir();
        var live = Path.Combine(dir, "live");
        var now = DateTimeOffset.UtcNow;
        Status(live, "AAAA000000000001", now.AddSeconds(-1), report: "2-instrument-AAAA0000.jsonl");
        Status(live, "AAAA000000000002", now.AddSeconds(-4));   // its DAW went away just now: left alone for a while
        Status(live, "AAAA000000000003", now.AddMinutes(-2));   // long gone: tidied away with its command folder
        // No time in it, so its file's time counts.
        File.SetLastWriteTimeUtc(Status(live, "AAAA000000000004", null, report: "1-instrument-AAAA0000.jsonl"), now.AddSeconds(-2).UtcDateTime);
        var orphan = Directory.CreateDirectory(Path.Combine(live, "AAAA000000000005.in")).FullName;
        Directory.SetLastWriteTimeUtc(orphan, now.AddMinutes(-5).UtcDateTime);
        var half = Path.Combine(live, "AAAA000000000006.json.tmp");
        File.WriteAllText(half, "{\"probe\":");
        File.SetLastWriteTimeUtc(half, now.AddMinutes(-5).UtcDateTime);
        var broken = Path.Combine(live, "AAAA000000000007.json");
        File.WriteAllText(broken, "not a status");
        var brokenOld = Path.Combine(live, "AAAA000000000008.json");
        File.WriteAllText(brokenOld, "not a status");
        File.SetLastWriteTimeUtc(brokenOld, now.AddMinutes(-5).UtcDateTime);

        var running = MoProbeLive.Running(dir, now);
        Assert.Equal(["AAAA000000000004", "AAAA000000000001"], running.Select(p => p.Instance));   // in the order they started
        Assert.True(File.Exists(Path.Combine(live, "AAAA000000000002.json")));
        Assert.True(Directory.Exists(Path.Combine(live, "AAAA000000000002.in")));
        Assert.False(File.Exists(Path.Combine(live, "AAAA000000000003.json")));
        Assert.False(Directory.Exists(Path.Combine(live, "AAAA000000000003.in")));
        Assert.False(Directory.Exists(orphan));
        Assert.False(File.Exists(half));
        Assert.True(File.Exists(broken));
        Assert.False(File.Exists(brokenOld));

        Assert.Empty(MoProbeLive.Running(Path.Combine(dir, "missing")));
        Assert.Equal(["AAAA000000000001"], MoProbeLive.Find(running, "aaaa000000000001").Select(p => p.Instance));
        Assert.Equal(2, MoProbeLive.Find(running, "AAAA0000").Count);   // the start of an instance, as report names have it
        Assert.Equal(2, MoProbeLive.Find(running, "inst 3").Count);
        Assert.Empty(MoProbeLive.Find(running, "AAA"));
    }

    [Fact]
    public void ReadsEverythingAStatusSays()
    {
        var dir = Fixtures.TempDir();
        var file = Status(Path.Combine(dir, "live"), Instance, DateTimeOffset.UtcNow);
        var p = MoProbeLive.Read(file)!;
        Assert.Equal(("instrument", Instance, Instance, "Bitwig Studio", "6.1.3", "CLAP", "Inst 3", "#C69F6F"),
                     (p.Kind, p.Instance, p.Identity, p.Host, p.HostVersion, p.Format, p.Track, p.TrackColour));
        Assert.Equal((1234, true, (bool?)false, (double?)12.5, (int?)4, (double?)110, 3), (p.Pid, p.Active, p.Playing, p.Beats, p.Bar, p.Tempo, p.Commands));
        Assert.Equal(8, p.Knobs.Count);
        Assert.Equal(440, p.Knobs[1003]);
        Assert.Equal(Path.Combine(dir, "20261014T091855Z-instrument-1683A801.jsonl"), p.ReportFile);
        Assert.Equal(Path.Combine(dir, "live", Instance + ".in"), p.CommandFolder);
        Assert.Equal("MO Probe Instrument on “Inst 3”", p.Title);
        Assert.Equal("Bitwig Studio 6.1.3 · CLAP · stopped · 110 BPM", p.Summary);
        Assert.Equal("MO Knob A 0.250, MO Gain 0.0 dB, MO Frequency 440.0 Hz, MO Steps 3, MO Switch Off, MO Choice Bravo, " +
                     "MO Ramp 50.0 %, MO Bypass Off", p.KnobsText);
        Assert.Null(MoProbeLive.Read(Path.Combine(dir, "live", "missing.json")));
    }

    [Fact]
    public void NamesTheDawRatherThanTheClapWrapper()
    {
        // The probe's VST3 is its CLAP in the CLAP wrapper, which names itself after the DAW and gives its own version.
        Assert.Equal(("Bitwig Studio", ""), MoProbe.Daw("Bitwig Studio (CLAP-as-VST3)", "The CLAP Wrapper Team", "0.16.0"));
        Assert.Equal(("Logic Pro", ""), MoProbe.Daw("Logic Pro (CLAP-as-AUv2)", "", "0.16.0"));
        Assert.Equal(("Some DAW", ""), MoProbe.Daw("Some DAW", "The CLAP Wrapper Team", "0.16.0"));
        Assert.Equal(("Bitwig Studio", "6.1.3"), MoProbe.Daw("Bitwig Studio", "Bitwig GmbH", "6.1.3"));

        var dir = Fixtures.TempDir();
        var create = MoProbeTests.SavingRun[0]
            .Replace("\"host\":\"Bitwig Studio\",\"hostVendor\":\"Bitwig GmbH\",\"hostVersion\":\"6.1.3\"",
                     "\"host\":\"Bitwig Studio (CLAP-as-VST3)\",\"hostVendor\":\"The CLAP Wrapper Team\",\"hostVersion\":\"0.16.0\"")
            .Replace("\"format\":\"CLAP\"", "\"format\":\"VST3\"");
        var report = ProbeReport.Read(MoProbeTests.Report(dir, "wrapped.jsonl", [create, .. MoProbeTests.SavingRun.Skip(1)]))!;
        Assert.Equal(("Bitwig Studio", ""), (report.Host, report.HostVersion));
        Assert.Equal("Bitwig Studio · VST3 · on “Bass” · 1 save · 1 note · 1 knob move", report.Summary);

        var live = MoProbeLive.Read(Status(Path.Combine(dir, "live"), Instance, DateTimeOffset.UtcNow, "Bitwig Studio (CLAP-as-VST3)",
                                           "The CLAP Wrapper Team", "0.16.0", "VST3"))!;
        Assert.Equal("Bitwig Studio · VST3 · stopped · 110 BPM", live.Summary);
    }

    [Fact]
    public void KnowsHowAZeroToOneValueLandsOnEachKnob()
    {
        Assert.Equal(-42, ProbeKnobs.Plain(1002, 0.25), 9);
        Assert.Equal(5015, ProbeKnobs.Plain(1003, 0.25), 9);
        Assert.Equal(4, ProbeKnobs.Plain(1004, 0.5));   // 3.5: the nearest step, up at halfway
        Assert.Equal(1, ProbeKnobs.Plain(1005, 0.5));
        Assert.Equal(2, ProbeKnobs.Plain(1006, 0.5));
        Assert.Equal(7, ProbeKnobs.Plain(1004, 2));     // past the end
        Assert.Equal(0.25, ProbeKnobs.Normalized(1002, -42), 9);
        Assert.True(ProbeKnobs.Same(1003, 440, 500));   // within half a percent of the range
        Assert.False(ProbeKnobs.Same(1004, 3, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbeKnobs.Plain(999, 0));

        Assert.Equal(["0.250", "-12.0 dB", "440.0 Hz", "1500 Hz", "3", "On", "Charlie", "50.0 %", "Off"],
                     [ProbeKnobs.Text(1001, 0.25), ProbeKnobs.Text(1002, -12), ProbeKnobs.Text(1003, 440), ProbeKnobs.Text(1003, 1500),
                      ProbeKnobs.Text(1004, 3), ProbeKnobs.Text(1005, 1), ProbeKnobs.Text(1006, 2), ProbeKnobs.Text(1007, 50), ProbeKnobs.Text(1008, 0)]);

        Assert.Equal([1002, 1002, 1002, 1001, null, null],
                     new[] { "gain", "MO Gain", "1002", "knob a", "999", "volume" }.Select(ProbeKnobs.Find));
        Assert.Equal([-12, -12, 440, 2, 1, 0, null],
                     new[] { (1002, "-12"), (1002, "-12 dB"), (1003, "440Hz"), (1006, "charlie"), (1008, "On"), (1005, "off"), (1003, "loud") }
                         .Select(x => ProbeKnobs.Parse(x.Item1, x.Item2)));

        Assert.Equal([0, 0.25, 0.5, 1], ProbeKnobCheck.Values(1002));
        Assert.Equal(8, ProbeKnobCheck.Values(1004).Count);
        Assert.Equal([0, 1], ProbeKnobCheck.Values(1008));

        var label = ProbeCommand.CleanLabel("Kick \"808\" \\ one\n" + new string('x', 100));
        Assert.Equal(64, label.Length);
        Assert.StartsWith("Kick 808  one", label);
    }

    [Fact]
    public void KnowsWhichKnobsAreSteppedAsThePluginDefinesThem()
    {
        var source = File.ReadAllText(Fixtures.RepoFile("native/mo-probe/src/probe.cpp")!);
        var stepped = Regex.Matches(source, @"\{\s*(\d{4}),[^}\n]*CLAP_PARAM_IS_STEPPED").Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
        Assert.Equal(stepped.Order(), ProbeKnobs.Stepped.Order());
        var choices = Regex.Match(source, @"kChoices\[\]\s*=\s*\{([^}]*)\}").Groups[1].Value;
        Assert.Equal(Regex.Matches(choices, "\"([^\"]+)\"").Select(m => m.Groups[1].Value), ProbeKnobs.Choices);
    }

    [Fact]
    public async Task SendsCommandsAndFindsTheAnswersInTheReport()
    {
        var dir = Fixtures.TempDir();
        await using var probe = new FakeProbe(dir);
        var running = Assert.Single(MoProbeLive.Running(dir));
        Assert.Equal(Instance, running.Instance);

        var pong = await MoProbeLive.SendAsync(Instance, ProbeCommand.Ping(), dir);
        Assert.Equal("pong", pong.Event);
        Assert.StartsWith("app-", pong.Str("for"));

        var set = await MoProbeLive.SendAsync(Instance, ProbeCommand.Set(1002, -12), dir);
        Assert.Equal(("app-set", 1002.0, "MO Gain", -12.0), (set.Event, set.Num("id"), set.Str("name"), set.Num("value")));
        var (shown, status) = await MoProbeLive.KnobAsync(Instance, 1002, -12, dir);
        Assert.True(shown);
        Assert.Equal(-12, status.Knobs[1002]);

        // An ID of the sender's own comes back as "for", and the answer can be looked for afterwards.
        Assert.Equal("mine-1", MoProbeLive.Send(Instance, new JsonObject { ["cmd"] = "label", ["label"] = "Kick", ["id"] = "mine-1" }, dir));
        var label = await MoProbeLive.AnswerAsync(Instance, "mine-1", dir);
        Assert.Equal(("label", "Kick"), (label.Event, label.Str("label")));
        Assert.True((await MoProbeLive.UntilAsync(Instance, s => s.Label == "Kick", dir)).Shown);

        // Several commands in one file, one a line, in order; a line that isn't a command is reported.
        var temp = Path.Combine(running.CommandFolder, "by hand.tmp");
        File.WriteAllText(temp, "{\"cmd\":\"beep\",\"id\":\"b1\"}\nnot json\n{\"cmd\":\"explode\",\"id\":\"b2\"}\n");
        File.Move(temp, Path.ChangeExtension(temp, ".json"));
        var beep = await MoProbeLive.AnswerAsync(Instance, "b1", dir);
        var unknown = await MoProbeLive.AnswerAsync(Instance, "b2", dir);
        Assert.Equal(("beep", "command-error"), (beep.Event, unknown.Event));
        Assert.True(beep.Number < unknown.Number);
        Assert.Contains(ProbeReport.Read(running.ReportFile)!.Lines, l => l.Event == "command-error" && l.Str("for") == "");
        Assert.Empty(Directory.GetFiles(running.CommandFolder));
        Assert.True((await MoProbeLive.UntilAsync(Instance, s => s.Commands == 6, dir)).Shown);
    }

    [Fact]
    public async Task AStatusBeingReplacedJustThenIsntTheProbeStopping()
    {
        var dir = Fixtures.TempDir();
        await using var probe = new FakeProbe(dir);
        var file = Assert.Single(MoProbeLive.Running(dir)).File;

        var back = GoneForAMoment(file);
        var pong = await MoProbeLive.SendAsync(Instance, ProbeCommand.Ping(), dir);
        Assert.Equal("pong", pong.Event);
        back.Join();

        back = GoneForAMoment(file);
        Assert.Equal(Instance, (await MoProbeLive.ReadRunningAsync(file, Instance)).Instance);
        back.Join();

        var none = Path.Combine(dir, "live", "0000000000000000.json");
        var clock = Stopwatch.StartNew();
        var gone = await Assert.ThrowsAsync<DawLinkException>(() => MoProbeLive.ReadRunningAsync(none, "0000000000000000"));
        Assert.Equal(MoProbeLive.NotRunning("0000000000000000"), gone.Message);
        Assert.True(clock.Elapsed.TotalSeconds >= 0.4, $"Gave up after {clock.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task AStatusUnreadableOnceAfterAPauseIsntTheProbeStopping()
    {
        var dir = Fixtures.TempDir();
        var file = Status(Path.Combine(dir, "live"), Instance, DateTimeOffset.UtcNow.AddMinutes(1));   // can't go stale however slow the run
        Thread? back = null;
        // The first look takes longer than a probe may go missing (a busy computer, or a slow check), and the status
        // is being swapped just as the wait reads it again.
        var (shown, _) = await MoProbeLive.UntilAsync(Instance, _ =>
        {
            if (back is not null) return true;
            back = GoneForAMoment(file, 700);
            Thread.Sleep(600);
            return false;
        }, dir);
        Assert.True(shown);
        back!.Join();
    }

    [Fact]
    public async Task AStatusMissingAcrossAPauseIsntTheProbeStopping()
    {
        var dir = Fixtures.TempDir();
        var file = Status(Path.Combine(dir, "live"), Instance, DateTimeOffset.UtcNow.AddMinutes(1));
        var status = File.ReadAllBytes(file);
        using var computer = new HeldUpContext();

        // Missing at the first look, then everything is held up for longer than a probe may go missing (a long garbage
        // collection, or a busy computer), the probe's next write with it: one look after that isn't the probe gone.
        foreach (var look in new Func<Task<MoProbeLive>>[]
                 {
                     () => MoProbeLive.ReadRunningAsync(file, Instance),
                     async () => (await MoProbeLive.UntilAsync(Instance, _ => true, dir)).Status,
                 })
        {
            File.Delete(file);
            var found = computer.Run(look);
            computer.HoldUp(TimeSpan.FromMilliseconds(700));
            computer.Post(_ => _ = Task.Delay(30).ContinueWith(_ =>
            {
                File.WriteAllBytes(file + ".tmp", status);
                File.Move(file + ".tmp", file);
            }, TaskScheduler.Default), null);
            Assert.Equal(Instance, (await found).Instance);
        }
    }

    [Fact]
    public async Task AProbeThatStopsAsTimeRunsOutIsSaidToHaveStopped()
    {
        var dir = Fixtures.TempDir();
        var file = Status(Path.Combine(dir, "live"), Instance, DateTimeOffset.UtcNow.AddMinutes(1));
        var gone = await Assert.ThrowsAsync<DawLinkException>(() => MoProbeLive.UntilAsync(Instance, _ =>
        {
            File.Delete(file);   // its DAW closed while the check ran past the time allowed
            Thread.Sleep(400);
            return false;
        }, dir, TimeSpan.FromMilliseconds(300)));
        Assert.Equal(MoProbeLive.NotRunning(Instance), gone.Message);
    }

    /// <summary>
    /// Takes a probe's status away, as Windows does for the instant the probe swaps in a new one, and puts it back on a
    /// thread of its own: the stand-in probe's next write waits on the thread pool, which a busy test run can hold up for
    /// longer than a probe may go missing.
    /// </summary>
    static Thread GoneForAMoment(string file, int ms = 100)
    {
        byte[] status;
        while (true)
        {
            try
            {
                status = File.ReadAllBytes(file);
                File.Delete(file);
                break;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(5); }   // being swapped just then
        }
        var back = new Thread(() =>
        {
            Thread.Sleep(ms);
            while (!File.Exists(file))
            {
                try { File.WriteAllBytes(file, status); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(5); }
            }
        }) { IsBackground = true };
        back.Start();
        return back;
    }

    /// <summary>
    /// A computer that can be held up: what's posted to it runs in turn on a thread of its own, and so does what an await
    /// in it carries on with.
    /// </summary>
    internal sealed class HeldUpContext : SynchronizationContext, IDisposable
    {
        readonly BlockingCollection<(SendOrPostCallback Run, object? State)> _queue = new();

        public HeldUpContext() =>
            new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (run, state) in _queue.GetConsumingEnumerable()) run(state);
            }) { IsBackground = true }.Start();

        // Once it's done with, what's still on its way (an activity card retiring after a while) runs on the thread pool.
        public override void Post(SendOrPostCallback d, object? state)
        {
            try { _queue.Add((d, state)); }
            catch (InvalidOperationException) { ThreadPool.QueueUserWorkItem(_ => d(state)); }
        }

        public Task<T> Run<T>(Func<Task<T>> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { done.SetResult(await work()); }
                catch (Exception e) { done.SetException(e); }
            }, null);
            return done.Task;
        }

        /// <summary>Nothing posted after this runs for <paramref name="time"/>.</summary>
        public void HoldUp(TimeSpan time) => Post(_ => Thread.Sleep(time), null);

        public void Dispose() => _queue.CompleteAdding();
    }

    [Fact]
    public async Task SaysWhenAProbeIsntRunningOrDoesntAnswer()
    {
        var dir = Fixtures.TempDir();
        var missing = Assert.Throws<DawLinkException>(() => MoProbeLive.Send(Instance, ProbeCommand.Ping(), dir));
        Assert.Equal(MoProbeLive.NotRunning(Instance), missing.Message);
        Assert.Throws<ArgumentException>(() => MoProbeLive.Send("../..", ProbeCommand.Ping(), dir));

        await using (var quiet = new FakeProbe(dir) { Answers = false })   // as a probe from before commands
        {
            var e = await Assert.ThrowsAsync<DawLinkException>(() =>
                MoProbeLive.SendAsync(Instance, ProbeCommand.Ping(), dir, TimeSpan.FromMilliseconds(300)));
            Assert.StartsWith("MO Probe Instrument on “Inst 3” didn't answer in time.", e.Message);
        }
        // Its DAW removed it: its status went with it.
        var gone = await Assert.ThrowsAsync<DawLinkException>(() => MoProbeLive.AnswerAsync(Instance, "x", dir, TimeSpan.FromSeconds(5)));
        Assert.Equal(MoProbeLive.NotRunning(Instance), gone.Message);
    }

    [Fact]
    public void FindsTheProbeOnItsTrackInBitwig()
    {
        static BitwigTrack Track(int index, string name, params string[] devices) =>
            new(index, name, "Instrument", false, "#000000", true, false, false, false, "0.0 dB", 0.8, "C", 0.5, devices.Length,
                devices.Select((d, i) => new BitwigDevice(i, d, "", "note_to_audio", true, true)).ToList());
        var project = new BitwigProject("Night Drive", false, 110, "4/4", false, 0, 4,
            [Track(0, "Drums"), Track(1, "Inst 3", "EQ+", "MO Probe Instrument"), Track(2, "Pad", "MO Probe Effect"), Track(3, "Pad", "Diva")]);
        var probe = new MoProbeLive { File = Path.Combine("p", "live", Instance + ".json"), Kind = "instrument", Instance = Instance,
                                      Host = "Bitwig Studio", Track = "Inst 3" };
        var (track, device) = BitwigProbeDevice.Locate(project, probe);
        Assert.Equal((1, 1, "MO Probe Instrument"), (track.Index, device.Index, device.Name));
        Assert.Equal(1, BitwigProbeDevice.Locate(project, probe with { Track = "inst 3" }).Track.Index);

        string Refused(MoProbeLive p) => Assert.Throws<DawLinkException>(() => BitwigProbeDevice.Locate(project, p)).Message;
        Assert.StartsWith("Bitwig has no track called “Bass”", Refused(probe with { Track = "Bass" }));
        Assert.StartsWith("Bitwig has 2 tracks called “Pad”", Refused(probe with { Track = "Pad", Kind = "effect" }));
        Assert.StartsWith("MO Probe Instrument isn't at the top of “Drums”'s chain", Refused(probe with { Track = "Drums" }));
        Assert.Contains("doesn't know which track it's on", Refused(probe with { Track = "" }));
        Assert.Equal("MO Probe Instrument on “Inst 3” is running in FL Studio, not Bitwig.", Refused(probe with { Host = "FL Studio" }));
    }

    [Fact]
    public async Task FormatLabListsRunningProbesAndSendsThemCommands()
    {
        var dir = Fixtures.TempDir();
        var none = await Run("probe", "live", "--reports", dir);
        Assert.Equal(0, none.Code);
        Assert.StartsWith("No MO Probe is running now", none.Out);
        var nobody = await Run("probe", "send", "Inst 3", "ping", "--reports", dir);
        Assert.Equal(1, nobody.Code);
        Assert.Contains("No MO Probe is running now", nobody.Err);
        Assert.Contains("No MO Probe is running now", (await Run("probe", "check-bitwig", "--reports", dir)).Err);

        await using var probe = new FakeProbe(dir);
        var live = await Run("probe", "live", "--reports", dir);
        Assert.Contains("MO Probes running now: 1", live.Out);
        Assert.Contains($"  {Instance} MO Probe Instrument on “Inst 3” · Bitwig Studio 6.1.3 · CLAP · stopped · 110 BPM · 0 commands", live.Out);
        Assert.Contains("    MO Knob A 0.250, MO Gain 0.0 dB,", live.Out);
        Assert.Contains("    report: 20261014T091855Z-instrument-1683A801.jsonl", live.Out);

        var ping = await Run("probe", "send", "inst 3", "ping", "--reports", dir);
        Assert.Equal(0, ping.Code);
        Assert.Equal("pong", JsonDocument.Parse(ping.Out).RootElement.GetProperty("event").GetString());
        Assert.StartsWith($"Sending ping to MO Probe Instrument on “Inst 3” ({Instance})", ping.Err);

        var set = await Run("probe", "send", Instance[..8], "set", "gain", "-12dB", "--reports", dir);
        Assert.Equal(0, set.Code);
        Assert.Contains("\"event\":\"app-set\"", set.Out);
        Assert.Equal(-12, probe[1002]);
        Assert.Equal(0, (await Run("probe", "send", Instance, "set", "1006", "Charlie", "--reports", dir)).Code);
        Assert.Equal(2, probe[1006]);
        Assert.Equal(0, (await Run("probe", "send", Instance, "label", "Kick", "2", "--reports", dir)).Code);
        Assert.Equal("Kick 2", probe.Label);
        Assert.True((await MoProbeLive.UntilAsync(Instance, s => s.Label == "Kick 2", dir)).Shown);
        Assert.Equal(0, (await Run("probe", "send", "kick 2", "beep", "0.5", "--reports", dir)).Code);

        Assert.Equal(2, (await Run("probe", "send", "Inst 3", "set", "volume", "1", "--reports", dir)).Code);
        Assert.Equal(2, (await Run("probe", "send", "Inst 3", "set", "gain", "loud", "--reports", dir)).Code);
        Assert.Equal(2, (await Run("probe", "send", "Inst 3", "wiggle", "--reports", dir)).Code);
        var bass = await Run("probe", "send", "Bass", "ping", "--reports", dir);
        Assert.Equal(1, bass.Code);
        Assert.Contains("No running MO Probe is “Bass”. The ones running now:", bass.Err);

        // Two running and neither named: it asks which, before it talks to Bitwig.
        await using var other = new FakeProbe(dir, "2E044C2E5009EFCE", track: "Bass");
        var which = await Run("probe", "check-bitwig", "--reports", dir);
        Assert.Equal(1, which.Code);
        Assert.Contains("2 MO Probes are running: say which", which.Err);
        Assert.Contains("probe check-bitwig", (await Run("help")).Out);
    }

    /// <summary>Runs formatlab off the test's own thread, as it waits for the probe the way a terminal does.</summary>
    static Task<(int Code, string Out, string Err)> Run(params string[] args) => Task.Run(() =>
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var code = FormatLabCommand.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    });
}

/// <summary>
/// The knob check through a DAW, against the stand-in probe and a stand-in DAW whose knobs turn the probe's (and follow
/// the probe's when it turns them).
/// </summary>
// Not alongside BitwigLiveTestsTests: both time knob turns, which a machine busy with the other's stand-ins can make late.
[Collection("Knob checks")]
public class ProbeKnobCheckTests
{
    const string Instance = "1683A80113A64904";
    // The stand-in probe writes its status every 20 milliseconds, not every half second, so turns settle quickly. The wait
    // for an answer stays as long as the app's own: on a busy CI runner the stand-in can take more than half a second.
    static readonly TimeSpan Quick = ProbeKnobCheck.DefaultWait, Settle = TimeSpan.FromMilliseconds(30);

    [Fact]
    public async Task PassesWhenTheDawSpreadsValuesEvenly()
    {
        var dir = Fixtures.TempDir();
        await using var probe = new FakeProbe(dir);
        probe[1002] = -6;
        await MoProbeLive.KnobAsync(Instance, 1002, -6, dir);
        var daw = new FakeDaw(probe);
        var reported = new List<KnobCheckLine>();
        var check = await ProbeKnobCheck.RunAsync(daw, Assert.Single(MoProbeLive.Running(dir)), new Each(reported.Add), Quick, Settle);

        Assert.True(check.AllMatched, check.Summary);
        Assert.Equal(64, check.Lines.Count);   // 4 turns of each smooth knob and every step of the others, each way
        Assert.Equal(check.Lines, reported);
        Assert.Equal("MO Probe Instrument on “Inst 3”: all 64 checks matched.", check.Summary);
        Assert.Empty(check.Notes);
        Assert.Null(check.ChangesOff);
        var gain = check.Lines.Single(l => l is { Knob: 1002, Way: KnobWay.ToProbe, Sent: 0.25 });
        Assert.StartsWith("MO Gain, Bitwig → probe: sent 0.250, expected -42.0 dB, got -42.0 dB (Bitwig shows “-42.0 dB”), ", gain.Text);
        var steps = check.Lines.Single(l => l is { Knob: 1004, Way: KnobWay.ToDaw, Sent: 5 });
        Assert.StartsWith("MO Steps, probe → Bitwig: sent 5, expected 0.714, got 0.714", steps.Text);

        // Back as they were.
        Assert.Equal(-6, probe[1002]);
        Assert.Equal(MoProbe.Knobs.Where(k => k.Id != 1002).Select(k => k.Default), MoProbe.Knobs.Where(k => k.Id != 1002).Select(k => probe[k.Id]));
    }

    [Fact]
    public async Task NotesKnobsTheDawShowsWithoutTellingTheirValues()
    {
        var dir = Fixtures.TempDir();
        await using var probe = new FakeProbe(dir);
        // Only MO Gain and MO Switch, so it's quick.
        var daw = new FakeDaw(probe) { Untold = true, Hidden = [.. MoProbe.Knobs.Select(k => k.Id).Where(k => k is not (1002 or 1005))] };
        var check = await ProbeKnobCheck.RunAsync(daw, Assert.Single(MoProbeLive.Running(dir)), wait: Quick, settle: Settle);

        Assert.All(check.Lines.Where(l => l.Way == KnobWay.ToProbe), l => Assert.True(l.Matched, l.Text));
        var toDaw = check.Lines.Where(l => l.Way == KnobWay.ToDaw).ToList();
        Assert.Equal(6, toDaw.Count);
        Assert.All(toDaw, l => Assert.True(l is { Untold: true, Got: null, Matched: false }, l.Text));
        // It stops waiting once Bitwig shows the probe's value, as no number is coming.
        Assert.All(toDaw, l => Assert.True(l.Took < Quick, l.Text));
        Assert.StartsWith("MO Gain, probe → Bitwig: sent -60.0 dB, expected 0.000, got nothing (Bitwig shows “-60.0 dB”), ", toDaw[0].Text);
        Assert.EndsWith("; Bitwig shows it, but didn't tell the app its value", toDaw[0].Text);
        Assert.Equal("MO Probe Instrument on “Inst 3”: 6 of 12 checks matched, and Bitwig showed 6 more without telling the app the value.",
                     check.Summary);

        var verdict = BitwigLiveTests.KnobVerdict(check);
        Assert.Equal(LiveOutcome.Noted, verdict.Outcome);
        Assert.StartsWith("Every knob moved both ways, and each stepped knob landed on its step, but Bitwig didn't tell the app the value " +
                          "of 2 knobs when the probe turned them. ", verdict.Detail);
        Assert.Equal("- Bitwig follows MO Gain, MO Switch when the probe turns them (it shows each new value), but didn't tell the app " +
                     "their values as a number, so the app can't read them back. The app reads a knob's number from Bitwig's remote controls " +
                     "when it's on the page Bitwig shows.", verdict.Lines[0]);
        var mapping = BitwigLiveTests.KnobMapping(check, "VST3");
        Assert.Contains("| MO Switch | 0 → Off, 1 → On | Off → shown, no number, On → shown, no number |", mapping.Lines);
        Assert.EndsWith(" Bitwig showed 6 of the probe's turns without telling the app their 0 to 1 values (“shown, no number”).", mapping.Detail);

        // Back as they were, through the probe.
        Assert.Equal((0, 0), (probe[1002], probe[1005]));
    }

    [Fact]
    public async Task WaitsForTheKnobsToBeBackWhenTheProbeIsHeldUp()
    {
        var dir = Fixtures.TempDir();
        await using var probe = new FakeProbe(dir);
        // Only MO Switch, so it's quick. The probe answers its last turn (to 1) and then stalls: the status from before
        // that turn still shows it at 0, as it was, while it isn't back yet.
        var daw = new FakeDaw(probe) { Hidden = [.. MoProbe.Knobs.Select(k => k.Id).Where(k => k != 1005)] };
        var turns = 0;
        probe.StallAfterSet = (knob, _) => knob == 1005 && ++turns == 2 ? TimeSpan.FromMilliseconds(500) : TimeSpan.Zero;
        var check = await ProbeKnobCheck.RunAsync(daw, Assert.Single(MoProbeLive.Running(dir)), wait: Quick, settle: Settle);

        Assert.True(check.AllMatched, check.Summary);
        Assert.Equal(4, check.Lines.Count);
        Assert.DoesNotContain(check.Notes, n => n.Contains("didn't go back", StringComparison.Ordinal));
        Assert.Equal(3, turns);   // the check's two and the one putting it back
        Assert.Equal(0, probe[1005]);
    }

    [Fact]
    public async Task FindsAStepThatLandsWrongly()
    {
        var dir = Fixtures.TempDir();
        await using var probe = new FakeProbe(dir);
        // A DAW that spreads MO Steps over 9 steps rather than 8.
        var daw = new FakeDaw(probe, (knob, v) => knob == 1004 ? Math.Min(7, Math.Round(v * 8)) : ProbeKnobs.Plain(knob, v));
        var check = await ProbeKnobCheck.RunAsync(daw, Assert.Single(MoProbeLive.Running(dir)), wait: Quick, settle: Settle);

        Assert.False(check.AllMatched);
        var wrong = check.Lines.Where(l => !l.Matched).ToList();
        Assert.Equal([4, 5, 6], wrong.Select(l => (int)l.Expected).Order());
        Assert.All(wrong, l => Assert.Equal((1004, KnobWay.ToProbe), (l.Knob, l.Way)));
        Assert.Contains(wrong, l => l.Text.StartsWith("MO Steps, Bitwig → probe: sent 0.571, expected 4, got 5", StringComparison.Ordinal));
        Assert.Equal("MO Probe Instrument on “Inst 3”: 61 of 64 checks matched. Not matching: MO Steps, Bitwig → probe (3).", check.Summary);
        Assert.Equal(3, probe[1004]);
    }

    [Fact]
    public async Task PutsTheKnobsBackWhenItStopsPartWay()
    {
        var dir = Fixtures.TempDir();
        await using var probe = new FakeProbe(dir);
        probe[1001] = 0.4;
        probe[1002] = -6;
        await MoProbeLive.UntilAsync(Instance, s => s.Knobs[1001] == 0.4 && s.Knobs[1002] == -6, dir);
        var daw = new FakeDaw(probe) { FailAfter = 6 };   // then Bitwig goes away
        var check = await ProbeKnobCheck.RunAsync(daw, Assert.Single(MoProbeLive.Running(dir)), wait: Quick, settle: Settle);

        Assert.Equal("Lost the connection to Bitwig.", check.Stopped);
        Assert.Equal(10, check.Lines.Count);   // MO Knob A both ways, then two turns of MO Gain
        Assert.EndsWith("It stopped part way: Lost the connection to Bitwig.", check.Summary);
        Assert.False(check.AllMatched);
        Assert.Equal((0.4, -6.0), (probe[1001], probe[1002]));
        Assert.Empty(check.Notes);

        // Cancelled part way: the knob being checked still goes back, and the check comes back saying so, with any notes.
        using var stop = new CancellationTokenSource();
        var cancelling = new Each(l => { if (l.Knob == 1003) stop.Cancel(); });
        check = await ProbeKnobCheck.RunAsync(new FakeDaw(probe), Assert.Single(MoProbeLive.Running(dir)), cancelling, Quick, Settle, stop.Token);
        Assert.Equal(ProbeKnobCheck.YouStopped, check.Stopped);
        Assert.False(check.AllMatched);
        Assert.Empty(check.Notes);
        Assert.Equal(440, probe[1003]);
    }

    [Fact]
    public async Task OnlyGoesThroughTheProbeWhenTheDawWontTakeChanges()
    {
        var dir = Fixtures.TempDir();
        await using var probe = new FakeProbe(dir);
        var daw = new FakeDaw(probe) { CantSet = BitwigProbeDevice.ChangesOff, Hidden = [1008] };
        var check = await ProbeKnobCheck.RunAsync(daw, Assert.Single(MoProbeLive.Running(dir)), wait: Quick, settle: Settle);

        Assert.True(check.AllMatched, check.Summary);
        Assert.Equal(30, check.Lines.Count);
        Assert.All(check.Lines, l => Assert.Equal(KnobWay.ToDaw, l.Way));
        Assert.Empty(daw.Sets);
        Assert.Equal(BitwigProbeDevice.ChangesOff, check.ChangesOff);
        Assert.EndsWith("then check again.", check.ChangesOff);
        Assert.Equal("Bitwig doesn't list MO Bypass among the probe's knobs, so it wasn't checked.", Assert.Single(check.Notes));
    }

    sealed class Each(Action<KnobCheckLine> each) : IProgress<KnobCheckLine>
    {
        public void Report(KnobCheckLine value) => each(value);
    }

    /// <summary>
    /// A DAW with MO Probe on a track: its knobs (by Bitwig's IDs for a plugin's) from 0 to 1, turning the probe's when set,
    /// and following the probe when it turns them itself.
    /// </summary>
    sealed class FakeDaw : IProbeDevice
    {
        readonly FakeProbe _probe;
        readonly Func<int, double, double> _toProbe;
        readonly Dictionary<int, double> _values;

        /// <param name="toProbe">What a 0 to 1 value becomes at the probe; evenly over the knob's range unless given.</param>
        public FakeDaw(FakeProbe probe, Func<int, double, double>? toProbe = null)
        {
            _probe = probe;
            _toProbe = toProbe ?? ProbeKnobs.Plain;
            _values = MoProbe.Knobs.ToDictionary(k => k.Id, k => ProbeKnobs.Normalized(k.Id, probe[k.Id]));
            probe.Turned = (knob, value) =>
            {
                lock (_values)
                    if (Untold) _moved.Add(knob);
                    else _values[knob] = ProbeKnobs.Normalized(knob, value);
            };
        }

        readonly HashSet<int> _moved = [];

        public string Daw => "Bitwig";
        public string? CantSet { get; init; }
        /// <summary>How many sets work before the DAW goes away.</summary>
        public int FailAfter { get; init; } = int.MaxValue;
        /// <summary>Knobs the DAW doesn't list.</summary>
        public int[] Hidden { get; init; } = [];
        /// <summary>
        /// Like Bitwig 6.1.5 with a plugin's knobs that aren't on its remote controls page: once a knob is turned, by the
        /// probe or by the app, it shows the new value but doesn't tell its number.
        /// </summary>
        public bool Untold { get; init; }
        public List<(string Id, double Value)> Sets { get; } = [];

        public Task<IReadOnlyList<BitwigParam>> KnobsAsync(CancellationToken ct = default)
        {
            lock (_values)
                return Task.FromResult<IReadOnlyList<BitwigParam>>(MoProbe.Knobs.Where(k => !Hidden.Contains(k.Id))
                    .Select(k => new BitwigParam($"CONTENTS/PID{k.Id:x}", k.Name, _moved.Contains(k.Id) ? null : _values[k.Id],
                                                 ProbeKnobs.Text(k.Id, _probe[k.Id]), _moved.Contains(k.Id))).ToList());
        }

        public Task SetAsync(string id, double value, CancellationToken ct = default)
        {
            if (CantSet is not null) throw new DawLinkException(CantSet);
            if (Sets.Count >= FailAfter) throw new DawLinkException("Lost the connection to Bitwig.");
            Sets.Add((id, value));
            var knob = Convert.ToInt32(id.Split("PID")[1], 16);
            lock (_values)
                if (Untold) _moved.Add(knob);
                else _values[knob] = value;
            _probe[knob] = _toProbe(knob, value);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// A stand-in for MO Probe running in a DAW, following its live link as native/README.md describes it: it writes its
/// status every few milliseconds, and takes the commands dropped in its command folder, answering in its report.
/// Disposing it is the DAW removing it: its status and command folder go. Like the real one, which runs in the DAW, it
/// has a thread of its own: on the thread pool a busy test run could hold it up for seconds, and its status would go
/// stale as if it had stopped.
/// </summary>
sealed class FakeProbe : IAsyncDisposable
{
    readonly Dictionary<int, double> _knobs = MoProbe.Knobs.ToDictionary(k => k.Id, k => k.Default);
    readonly CancellationTokenSource _stop = new();
    readonly string _status, _commands;
    readonly Thread _running;
    /// <summary>What went wrong on its thread, given to the test as it's disposed: thrown there, it would end the whole test run.</summary>
    ExceptionDispatchInfo? _failed;
    readonly string _host, _hostVersion;
    int _n, _handled;

    /// <param name="format">How the DAW loaded it. As a VST3 it's inside the CLAP wrapper, which names the DAW its own way.</param>
    public FakeProbe(string folder, string instance = "1683A80113A64904", string track = "Inst 3", string format = "CLAP")
    {
        Instance = instance;
        Track = track;
        Format = format;
        (_host, _hostVersion) = format == "VST3" ? ("Bitwig Studio (CLAP-as-VST3)", "0.16.0") : ("Bitwig Studio", "6.1.3");
        var live = Path.Combine(folder, "live");
        _status = Path.Combine(live, instance + ".json");
        _commands = Directory.CreateDirectory(Path.Combine(live, instance + ".in")).FullName;
        ReportFile = Path.Combine(folder, $"20261014T091855Z-instrument-{instance[..8]}.jsonl");
        Write("create", new() { ["plugin"] = "org.musicorganiser.probe.instrument", ["host"] = _host, ["hostVersion"] = _hostVersion, ["format"] = format });
        WriteStatus();
        _running = new Thread(Run) { IsBackground = true, Name = "Fake MO Probe " + instance };
        _running.Start();
    }

    public string Instance { get; }
    public string Track { get; }
    public string Format { get; }
    public string ReportFile { get; }
    public string Label { get; private set; } = "";
    /// <summary>Whether it takes commands: a probe from before commands doesn't.</summary>
    public bool Answers { get; init; } = true;
    /// <summary>Hears of each knob the probe turns itself (a set command), as the DAW does.</summary>
    public Action<int, double>? Turned { get; set; }
    /// <summary>How long it stops after answering a set command, as on a busy computer: it writes no status meanwhile.</summary>
    public Func<int, double, TimeSpan>? StallAfterSet { get; set; }

    /// <summary>A knob's value, in its own units; setting it is the DAW turning it.</summary>
    public double this[int knob]
    {
        get { lock (_knobs) return _knobs[knob]; }
        set { lock (_knobs) _knobs[knob] = value; }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _running.Join();
        File.Delete(_status);
        Directory.Delete(_commands, recursive: true);
        _failed?.Throw();
        return ValueTask.CompletedTask;
    }

    void Run()
    {
        var since = Stopwatch.StartNew();
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (Answers) TakeCommands();
                if (since.ElapsedMilliseconds >= 20)
                {
                    WriteStatus();
                    since.Restart();
                }
            }
            // A reader had the status open: Windows won't replace it then (Access denied), so next time, as the probe does.
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            catch (Exception e)
            {
                _failed = ExceptionDispatchInfo.Capture(e);
                return;
            }
            _stop.Token.WaitHandle.WaitOne(5);
        }
    }

    void TakeCommands()
    {
        foreach (var f in new DirectoryInfo(_commands).GetFiles("*.json").OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            // One changed a moment ago may still be being written.
            if (DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromMilliseconds(10)) continue;
            var text = File.ReadAllText(f.FullName);
            f.Delete();
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Handle(line);
        }
    }

    void Handle(string line)
    {
        JsonObject? c = null;
        try { c = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { }
        var id = c?["id"] is JsonValue i && i.TryGetValue<string>(out var given) ? given : "";
        _handled++;
        if (c?["cmd"] is not JsonValue cv || !cv.TryGetValue<string>(out var cmd))
        {
            Write("command-error", new() { ["error"] = "not a command" }, id);
            return;
        }
        switch (cmd)
        {
            case "ping":
                Write("pong", new(), id);
                WriteStatus();
                break;
            case "set":
                var knob = (int)c["param"]!;
                var k = MoProbe.Knobs.First(x => x.Id == knob);
                var value = Math.Clamp((double)c["value"]!, k.Min, k.Max);
                this[knob] = value;
                Turned?.Invoke(knob, value);
                Write("app-set", new() { ["id"] = knob, ["name"] = k.Name, ["value"] = value }, id);
                if (StallAfterSet?.Invoke(knob, value) is { } stall && stall > TimeSpan.Zero) Thread.Sleep(stall);
                break;
            case "label":
                Label = (string)c["label"]!;
                Write("label", new() { ["label"] = Label }, id);
                break;
            case "beep":
                Write("beep", new(), id);
                break;
            default:
                Write("command-error", new() { ["error"] = $"no command \"{cmd}\"" }, id);
                break;
        }
    }

    void Write(string ev, JsonObject data, string? answering = null)
    {
        var line = new JsonObject { ["t"] = Now(), ["event"] = ev, ["n"] = ++_n, ["instance"] = Instance };
        if (answering is not null) line["for"] = answering;
        foreach (var (key, value) in data) line[key] = value?.DeepClone();
        using var s = new FileStream(ReportFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        s.Write(System.Text.Encoding.UTF8.GetBytes(line.ToJsonString() + "\n"));
    }

    void WriteStatus()
    {
        var knobs = new JsonObject();
        lock (_knobs)
            foreach (var (id, v) in _knobs) knobs[id.ToString(CultureInfo.InvariantCulture)] = v;
        var status = new JsonObject
        {
            ["probe"] = "instrument", ["instance"] = Instance, ["identity"] = Instance, ["label"] = Label, ["host"] = _host,
            ["hostVersion"] = _hostVersion, ["format"] = Format, ["track"] = Track, ["trackColor"] = "#C69F6F",
            ["report"] = Path.GetFileName(ReportFile), ["t"] = Now(), ["pid"] = Environment.ProcessId, ["active"] = true,
            ["playing"] = false, ["beats"] = 12.5, ["bar"] = 4, ["tempo"] = 110, ["commands"] = _handled, ["params"] = knobs,
        };
        File.WriteAllText(_status + ".tmp", status.ToJsonString());
        File.Move(_status + ".tmp", _status, overwrite: true);
    }

    static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
