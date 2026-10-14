using System.Text.Json;
using System.Text.Json.Nodes;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.LiveTests;

namespace MusicOrganiser.Core.Tests;

/// <summary>The live-test framework itself: running tests one by one whatever happens in each, and the reports.</summary>
public class LiveRunnerTests
{
    [Fact]
    public async Task OneTestFailingNeverStopsTheRun()
    {
        var seen = new List<LiveProgress>();
        var run = new LiveRunner(new Each<LiveProgress>(seen.Add));
        await run.RunAsync("Link", "Hello", _ => Task.FromResult(LiveVerdict.Pass("Bitwig answered.", new JsonObject { ["link"] = 2 })));
        await run.RunAsync("Link", "Broken", _ => throw new InvalidOperationException("Something the test didn't expect"));
        await run.RunAsync("Link", "Gone", async t =>
        {
            await Task.Yield();
            throw new DawLinkException("Bitwig closed the connection.");
        });
        await run.RunAsync("Link", "Slow", async t =>
        {
            await Task.Delay(Timeout.Infinite, t);
            return LiveVerdict.Pass("never");
        }, TimeSpan.FromMilliseconds(200));
        await run.RunAsync("Changes", "Not here", _ => Task.FromResult(LiveVerdict.Skip("Changes are off.")));
        await run.RunAsync("MO Probe", "Mapping", _ => Task.FromResult(LiveVerdict.Note("A finding.") with { Lines = ["| a | b |"] }));
        run.Record("MO Probe", "Worked out", LiveVerdict.Pass("From the one before."));

        Assert.Equal([LiveOutcome.Passed, LiveOutcome.Failed, LiveOutcome.Failed, LiveOutcome.Failed, LiveOutcome.Skipped, LiveOutcome.Noted, LiveOutcome.Passed],
                     run.Results.Select(r => r.Outcome));
        Assert.Equal("InvalidOperationException: Something the test didn't expect", run.Results[1].Detail);
        Assert.Contains("at MusicOrganiser", (string)run.Results[1].Data!["error"]!);   // where, for whoever reads the report
        Assert.Equal("Bitwig closed the connection.", run.Results[2].Detail);
        Assert.Equal("It didn't finish within 0.2 s.", run.Results[3].Detail);
        Assert.Equal(["| a | b |"], run.Results[5].Lines);
        Assert.Equal(2, (int)run.Results[0].Data!["link"]!);

        // Each test is reported as it starts and as it ends, with the count so far; one recorded without running only ends.
        Assert.Equal(13, seen.Count);
        Assert.Equal(("Link › Hello…", null, 0), (seen[0].Text, seen[0].Result, seen[0].Done));
        Assert.Equal(("✓ Link › Hello", 1, 0), (seen[1].Text, seen[1].Done, seen[1].Failed));
        Assert.Equal(("✗ Link › Slow", 4, 3), (seen[7].Text, seen[7].Done, seen[7].Failed));
        Assert.Equal("✓ MO Probe › Worked out", seen[^1].Text);
    }

    [Fact]
    public async Task StoppingEndsTheTestRunningAndSkipsTheRest()
    {
        using var stop = new CancellationTokenSource();
        var run = new LiveRunner(ct: stop.Token);
        await run.RunAsync("Link", "Hello", _ => Task.FromResult(LiveVerdict.Pass("Fine.")));
        var tidied = false;
        await run.RunAsync("MO Probe", "Knob check", async t =>
        {
            stop.Cancel();
            try { await Task.Delay(Timeout.Infinite, t); }
            finally { tidied = true; }   // as the knob check puts knobs back
            return LiveVerdict.Pass("never");
        });
        var ran = false;
        await run.RunAsync("Send", "FL Studio channel", _ =>
        {
            ran = true;
            return Task.FromResult(LiveVerdict.Pass("never"));
        });

        Assert.True(run.Stopped);
        Assert.True(tidied);
        Assert.False(ran);
        Assert.Equal([(LiveOutcome.Passed, "Fine."), (LiveOutcome.Skipped, LiveRunner.StoppedDuring), (LiveOutcome.Skipped, LiveRunner.StoppedBefore)],
                     run.Results.Select(r => (r.Outcome, r.Detail)));
    }

    static LiveRun Run(string stamp, params LiveResult[] results)
    {
        var started = DateTimeOffset.ParseExact(stamp, "yyyyMMdd'T'HHmmss'Z'", null, System.Globalization.DateTimeStyles.AssumeUniversal);
        return new LiveRun
        {
            Daw = "Bitwig Studio", Key = "bitwig", Stamp = stamp, Started = started, Ended = started.AddSeconds(42), Results = results,
            AppVersion = "1.2.3+abc", Hello = JsonNode.Parse("""{"link":2,"product":"Bitwig Studio","version":"6.1.3","project":"MO Live Test"}"""),
            Setup = "Bitwig Studio 6.1.3 · JavaScript script, link 2 · “MO Live Test”",
        };
    }

    static LiveResult Result(string area, string name, LiveOutcome outcome, string detail) =>
        new(area, name, outcome, detail, DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(12));

    [Fact]
    public void WritesAReportForProgramsAndOneToRead()
    {
        var dir = Fixtures.TempDir();
        const string steps = "Bitwig isn't letting the app make changes, so the run changed nothing.";
        var run = LiveReports.Write(Run("20261014T143012Z",
            Result("Link", "Hello", LiveOutcome.Passed, "Bitwig Studio 6.1.3 answered."),
            Result("Changes", "New track", LiveOutcome.Skipped, steps),
            Result("Changes", "Sampler by ID", LiveOutcome.Skipped, steps),
            Result("Link", "Actions", LiveOutcome.Failed, "No Save.") with { Data = new JsonObject { ["count"] = 3 } },
            Result("MO Probe", "Knob mapping (CLAP)", LiveOutcome.Noted, "How Bitwig maps the knobs.") with { Lines = ["| Knob | … |", "|---|---|"] }), dir);

        Assert.Null(run.ReportProblem);
        Assert.Equal(Path.Combine(dir, "20261014T143012Z-bitwig.json"), run.ReportJson);
        Assert.Equal(Path.Combine(dir, "20261014T143012Z-bitwig.md"), run.ReportMarkdown);
        Assert.Equal("1 passed, 1 failed, 2 skipped, 1 noted, in 42 s", run.Summary);

        var json = JsonNode.Parse(File.ReadAllText(run.ReportJson!))!;
        Assert.Equal(("Bitwig Studio", "20261014T143012Z", "1.2.3+abc", "MO Live Test"),
                     ((string)json["daw"]!, (string)json["stamp"]!, (string)json["app"]!["version"]!, (string)json["hello"]!["project"]!));
        Assert.Equal((1, 1, 2, 1), ((int)json["counts"]!["passed"]!, (int)json["counts"]!["failed"]!, (int)json["counts"]!["skipped"]!,
                                    (int)json["counts"]!["noted"]!));
        var tests = json["tests"]!.AsArray();
        Assert.Equal(5, tests.Count);
        Assert.Equal(("failed", 3, 12.0), ((string)tests[3]!["outcome"]!, (int)tests[3]!["data"]!["count"]!, (double)tests[3]!["ms"]!));
        Assert.Equal("2026-10-14T14:30:54.000Z", (string)json["ended"]!);

        // Failures, then notes, each with all they say, then every test by area, with a reason said again only once.
        var md = File.ReadAllText(run.ReportMarkdown!);
        Assert.StartsWith("# Bitwig Studio live tests, 14 October 2026 14:30:12 UTC", md);
        Assert.Contains("**1 passed, 1 failed, 2 skipped, 1 noted, in 42 s**", md);
        var failed = md.IndexOf("## Failed", StringComparison.Ordinal);
        var noted = md.IndexOf("## Noted", StringComparison.Ordinal);
        var every = md.IndexOf("## Every test", StringComparison.Ordinal);
        Assert.True(failed > 0 && failed < noted && noted < every, md);
        Assert.Contains("### ✗ Link › Actions\n\nNo Save.", md.ReplaceLineEndings("\n"));
        Assert.Contains("| Knob | … |", md);
        Assert.Contains($"- – **New track** (12 ms): {steps}", md);
        Assert.Contains("- – **Sampler by ID** (12 ms): Skipped, as above.", md);
        Assert.True(md.IndexOf("### Link", every, StringComparison.Ordinal) < md.IndexOf("### Changes", every, StringComparison.Ordinal));
        Assert.EndsWith("`20261014T143012Z-bitwig.json`.\n", md.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void KeepsTheNewestFiftyRunsOfEachDaw()
    {
        var dir = Fixtures.TempDir();
        var start = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 55; i++)
        {
            var stamp = LiveReports.Stamp(start.AddMinutes(i));
            File.WriteAllText(Path.Combine(dir, $"{stamp}-bitwig.json"), "{}");
            File.WriteAllText(Path.Combine(dir, $"{stamp}-bitwig.md"), "#");
        }
        File.WriteAllText(Path.Combine(dir, "20261001T090000Z-fl.md"), "#");
        File.WriteAllText(Path.Combine(dir, "notes-bitwig.md"), "mine");
        Directory.CreateDirectory(Path.Combine(dir, "files"));

        var run = LiveReports.Write(Run("20261014T143012Z"), dir);
        // A second run in the same second keeps both.
        var again = LiveReports.Write(Run("20261014T143012Z"), dir);

        Assert.Equal(Path.Combine(dir, "20261014T143012Z-2-bitwig.md"), again.ReportMarkdown);
        var kept = Directory.GetFiles(dir, "2026*-bitwig.*").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(100, kept.Count);
        Assert.Equal(LiveReports.Stamp(start.AddMinutes(7)) + "-bitwig.json", kept[0]);   // the 7 oldest went
        Assert.True(File.Exists(Path.Combine(dir, "20261001T090000Z-fl.md")));
        Assert.True(File.Exists(Path.Combine(dir, "notes-bitwig.md")));
        Assert.True(Directory.Exists(Path.Combine(dir, "files")));
        Assert.Equal(again.ReportMarkdown, LiveReports.Newest(dir, "bitwig"));
        Assert.Null(LiveReports.Newest(Path.Combine(dir, "nowhere"), "bitwig"));
        Assert.True(File.Exists(run.ReportJson));
    }

    [Fact]
    public void SaysWhenTheReportCantBeWritten()
    {
        var file = Path.Combine(Fixtures.TempDir(), "in the way");
        File.WriteAllText(file, "");
        var run = LiveReports.Write(Run("20261014T143012Z"), file);
        Assert.Null(run.ReportMarkdown);
        Assert.StartsWith($"The report couldn't be written to {file}: ", run.ReportProblem);
    }
}

/// <summary>Reports each value straight away, in order (Progress would post them to the thread pool).</summary>
sealed class Each<T>(Action<T> each) : IProgress<T>
{
    public void Report(T value)
    {
        lock (this) each(value);
    }
}
