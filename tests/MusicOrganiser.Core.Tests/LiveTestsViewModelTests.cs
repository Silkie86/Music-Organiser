using System.Diagnostics;
using MusicOrganiser.Core.LiveTests;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Core.Tests;

/// <summary>
/// The live tests' card in Settings, with runs made in code through the real runner and reports, written to a temp folder
/// (SettingsRunsTheLiveTestsWhileBitwigAnswers runs the real suite against the stand-in Bitwig).
/// </summary>
public class LiveTestsViewModelTests
{
    /// <summary>
    /// A run of three tests: Hello passes, Save waits for <paramref name="go"/> then fails (or, stopped while it waits,
    /// tidies up until <paramref name="tidied"/>), and Send passes.
    /// </summary>
    static LiveTestsViewModel.Runner Held(Task go, Task? tidied = null) => async (folder, progress, ct) =>
    {
        var started = DateTimeOffset.UtcNow;
        var run = new LiveRunner(progress, ct);
        await run.RunAsync("Link", "Hello", _ => Task.FromResult(LiveVerdict.Pass("Bitwig answered.")));
        await run.RunAsync("Changes", "Save", async t =>
        {
            try { await go.WaitAsync(t); }
            catch (OperationCanceledException) when (tidied is not null)
            {
                await tidied;   // as the knob check puts the knobs back
                throw;
            }
            return LiveVerdict.Fail("Bitwig still showed unsaved changes 15 s after Save.");
        });
        await run.RunAsync("Send", "FL Studio channel", _ => Task.FromResult(LiveVerdict.Pass("It went in.")));
        return Written(folder, started, run.Results, run.Stopped);
    };

    /// <summary>A run that ends with these results straight away.</summary>
    static LiveTestsViewModel.Runner Ends(params LiveResult[] results) =>
        (folder, _, _) => Task.FromResult(Written(folder, DateTimeOffset.UtcNow, results, false));

    static LiveRun Written(string folder, DateTimeOffset started, IEnumerable<LiveResult> results, bool cancelled) =>
        LiveReports.Write(new LiveRun
        {
            Daw = BitwigLiveTests.Daw, Key = BitwigLiveTests.Key, Stamp = LiveReports.Stamp(started), Started = started, Ended = DateTimeOffset.UtcNow,
            Results = results.ToList(), Cancelled = cancelled, AppVersion = "test",
        }, folder);

    static LiveResult Result(string area, string name, LiveOutcome outcome, string detail) =>
        new(area, name, outcome, detail, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(5));

    static LiveTestsViewModel Card(string folder, LiveTestsViewModel.Runner run, bool answering = false) =>
        new(BitwigLiveTests.Key, folder, run) { Answering = answering };

    [Fact]
    public async Task RunsWhileTheDawAnswersAndSaysHowItWent()
    {
        var folder = Path.Combine(Fixtures.TempDir(), "reports", "live");
        var go = new TaskCompletionSource();
        var card = Card(folder, Held(go.Task));
        Assert.False(card.RunCommand.CanExecute(null));   // until the app has seen Bitwig answer
        Assert.False(card.OpenReportCommand.CanExecute(null));

        card.Answering = true;
        Assert.True(card.RunCommand.CanExecute(null));
        var running = card.RunCommand.ExecuteAsync(null);
        Assert.True(card.Running);
        Assert.False(card.RunCommand.CanExecute(null));
        await Until(() => card.ProgressLine == "Changes › Save… (1 done)");

        go.SetResult();
        await running;
        Assert.Equal(("", false), (card.ProgressLine, card.Running));
        var report = Path.Combine(folder, Path.GetFileName(card.Report)!);
        Assert.Equal(report, card.Report);
        Assert.True(File.Exists(report));
        Assert.EndsWith("-bitwig.md", report);
        var lines = card.Summary.Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("2 passed, 1 failed, 0 skipped, in ", lines[0]);
        Assert.Equal("Failed: Changes › Save.", lines[1]);
        Assert.Equal($"The report: {report}", lines[2]);
        Assert.True(card.OpenReportCommand.CanExecute(null));

        // After a restart, the newest report is there to open, with how its run went.
        var later = Card(folder, Held(Task.CompletedTask));
        await later.RefreshAsync();
        Assert.Equal(report, later.Report);
        Assert.StartsWith("The last run, ", later.Summary);
        Assert.EndsWith(": " + lines[0], later.Summary);
    }

    [Fact]
    public async Task StopsAndStillReports()
    {
        var folder = Fixtures.TempDir();
        var tidied = new TaskCompletionSource();
        var card = Card(folder, Held(new TaskCompletionSource().Task, tidied.Task), answering: true);
        var running = card.RunCommand.ExecuteAsync(null);
        await Until(() => card.ProgressLine.StartsWith("Changes › Save…", StringComparison.Ordinal));

        card.StopCommand.Execute(null);
        Assert.Equal("Stopping: the test running now is tidying up, then the report is written…", card.ProgressLine);
        Assert.True(card.Running);
        tidied.SetResult();
        await running;

        Assert.StartsWith("Stopped part way: 1 passed, 0 failed, 2 skipped, in ", card.Summary);
        Assert.True(File.Exists(card.Report));
        Assert.Contains(LiveRunner.StoppedDuring, File.ReadAllText(card.Report!));
        Assert.Equal("", card.ProgressLine);
    }

    [Fact]
    public async Task NamesTheFailuresAndSaysWhatToDoWhenChangesArentAllowed()
    {
        var closed = $"Bitwig has “Night Drive” open, so the run changed nothing: it only changes a project whose name starts with " +
                     $"“MO Live Test”, never your own work. {BitwigLiveTests.HowToAllowChanges}";
        var results = Enumerable.Range(1, 7).Select(i => Result("Link", $"Check {i}", LiveOutcome.Failed, "No."))
            .Append(Result("Changes", "Changes allowed", LiveOutcome.Skipped, closed))
            .Append(Result("Changes", "New track", LiveOutcome.Skipped, closed))
            .ToArray();
        var card = Card(Fixtures.TempDir(), Ends(results), answering: true);

        await card.RunCommand.ExecuteAsync(null);

        var lines = card.Summary.Split(Environment.NewLine);
        Assert.Equal("Failed: Link › Check 1, Link › Check 2, Link › Check 3, Link › Check 4, Link › Check 5, and 2 more.", lines[1]);
        Assert.Equal(closed, lines[2]);   // once, for every change it skipped
        Assert.StartsWith("The report: ", lines[3]);
    }

    [Fact]
    public async Task SaysWhenTheReportCantBeWrittenOrTheRunCantStart()
    {
        // A file where the reports folder should be.
        var blocked = Path.Combine(Fixtures.TempDir(), "live");
        File.WriteAllText(blocked, "");
        var card = Card(blocked, Ends(Result("Link", "Hello", LiveOutcome.Passed, "Fine.")), answering: true);
        await card.RunCommand.ExecuteAsync(null);
        Assert.Equal(2, card.Summary.Split(Environment.NewLine).Length);
        Assert.Contains($"The report couldn't be written to {blocked}: ", card.Summary);
        Assert.Null(card.Report);
        Assert.False(card.OpenReportCommand.CanExecute(null));

        var broken = Card(Fixtures.TempDir(), (_, _, _) => throw new InvalidOperationException("No way to reach Bitwig."), answering: true);
        await broken.RunCommand.ExecuteAsync(null);
        Assert.Equal(("The live tests couldn't run: No way to reach Bitwig.", false), (broken.Summary, broken.Running));
        Assert.True(broken.RunCommand.CanExecute(null));

        // No reports yet: nothing to open, and nothing said.
        var none = Card(Path.Combine(Fixtures.TempDir(), "none"), Ends());
        await none.RefreshAsync();
        Assert.Equal((null, ""), (none.Report, none.Summary));
    }

    static async Task Until(Func<bool> done)
    {
        for (var waited = Stopwatch.StartNew(); !done(); await Task.Delay(20))
            if (waited.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail("Timed out.");
    }
}
