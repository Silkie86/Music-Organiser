using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using MusicOrganiser.Core.DawLink;

namespace MusicOrganiser.Core.LiveTests;

/// <summary>How a live test ended.</summary>
public enum LiveOutcome
{
    Passed,
    Failed,
    /// <summary>Not run, or not run to the end, for a reason it gives (changes not allowed, a plugin not installed...).</summary>
    Skipped,
    /// <summary>A finding that isn't a pass or a fail, such as how a DAW maps a plugin's knobs.</summary>
    Noted,
}

/// <summary>What a live test found: how it ended, a line saying what, and anything worth keeping for the report.</summary>
/// <param name="Detail">One line, in words to show: what was seen, or why it failed or was skipped.</param>
/// <param name="Data">Anything machine-readable to keep in the report's JSON: what the DAW answered, timings, a table.</param>
public sealed record LiveVerdict(LiveOutcome Outcome, string Detail, JsonNode? Data = null)
{
    /// <summary>More to show under the detail in the readable report: a table, or a line for each difference.</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];

    public static LiveVerdict Pass(string detail, JsonNode? data = null) => new(LiveOutcome.Passed, detail, data);
    public static LiveVerdict Fail(string detail, JsonNode? data = null) => new(LiveOutcome.Failed, detail, data);
    public static LiveVerdict Skip(string why, JsonNode? data = null) => new(LiveOutcome.Skipped, why, data);
    public static LiveVerdict Note(string detail, JsonNode? data = null) => new(LiveOutcome.Noted, detail, data);
}

/// <summary>One live test's result.</summary>
/// <param name="Area">The part of the DAW's link it tests, e.g. "Changes".</param>
/// <param name="Took">From starting it to its verdict.</param>
public sealed record LiveResult(string Area, string Name, LiveOutcome Outcome, string Detail, DateTimeOffset Started, TimeSpan Took)
{
    /// <inheritdoc cref="LiveVerdict.Data"/>
    public JsonNode? Data { get; init; }

    /// <inheritdoc cref="LiveVerdict.Lines"/>
    public IReadOnlyList<string> Lines { get; init; } = [];

    public string Mark => Outcome switch
    {
        LiveOutcome.Passed => "✓",
        LiveOutcome.Failed => "✗",
        LiveOutcome.Skipped => "–",
        _ => "•",
    };

    /// <summary>E.g. "Changes › New track".</summary>
    public string Title => $"{Area} › {Name}";

    /// <summary>"0.4 s", or "12 ms" for a quick one.</summary>
    public string Time => Took.TotalSeconds >= 1 ? Took.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : $"{Math.Round(Took.TotalMilliseconds):0} ms";
}

/// <summary>How a run is going, for the app to show as it goes: a test starting (no result yet) or ending.</summary>
/// <param name="Done">How many tests have ended so far.</param>
/// <param name="Failed">How many of those failed.</param>
public sealed record LiveProgress(string Area, string Name, LiveResult? Result, int Done, int Failed)
{
    /// <summary>E.g. "Changes › New track…" while it runs, then "✓ Changes › New track".</summary>
    public string Text => Result is null ? $"{Area} › {Name}…" : $"{Result.Mark} {Area} › {Name}";
}

/// <summary>A finished run, as written to its reports.</summary>
public sealed record LiveRun
{
    /// <summary>The DAW, as the app names it: "Bitwig Studio".</summary>
    public required string Daw { get; init; }
    /// <summary>The DAW in the report files' names: "bitwig".</summary>
    public required string Key { get; init; }
    /// <summary>When it started, in UTC, as the report files are named: "20261014T143012Z".</summary>
    public required string Stamp { get; init; }
    public required DateTimeOffset Started { get; init; }
    public required DateTimeOffset Ended { get; init; }
    public required IReadOnlyList<LiveResult> Results { get; init; }
    /// <summary>The app's version, to tell which build a report came from.</summary>
    public string AppVersion { get; init; } = "";
    /// <summary>What the DAW said when the run connected, as it came; null if it didn't answer.</summary>
    public JsonNode? Hello { get; init; }
    /// <summary>The DAW and its link in a few words: "Bitwig Studio 6.1.3 · JavaScript script, link 2 · “MO Live Test”".</summary>
    public string Setup { get; init; } = "";
    /// <summary>Whether the run was stopped before the end (its remaining tests are Skipped).</summary>
    public bool Cancelled { get; init; }
    /// <summary>The reports, once written: everything as JSON, and a readable summary.</summary>
    public string? ReportJson { get; init; }
    public string? ReportMarkdown { get; init; }
    /// <summary>Why the reports couldn't be written, if they couldn't.</summary>
    public string? ReportProblem { get; init; }

    public int Passed => Results.Count(r => r.Outcome == LiveOutcome.Passed);
    public int Failed => Results.Count(r => r.Outcome == LiveOutcome.Failed);
    public int Skipped => Results.Count(r => r.Outcome == LiveOutcome.Skipped);
    public int Noted => Results.Count(r => r.Outcome == LiveOutcome.Noted);
    public TimeSpan Took => Ended - Started;

    /// <summary>E.g. "20 passed, 1 failed, 3 skipped, 2 noted, in 42 s", with "Stopped part way: " in front when it was.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{Passed} passed", $"{Failed} failed", $"{Skipped} skipped" };
            if (Noted > 0) parts.Add($"{Noted} noted");
            var text = string.Join(", ", parts) + $", in {Took.TotalSeconds.ToString(Took.TotalSeconds < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)} s";
            return Cancelled ? "Stopped part way: " + text : text;
        }
    }
}

/// <summary>
/// Runs a DAW's live tests one by one, as a suite's code calls them: each has its own timeout, one failing never stops
/// the run, and each result is reported as it comes. Once the run is cancelled, the test running then and every one
/// after it are Skipped, so the run still ends with a full list of what was and wasn't done.
/// </summary>
public sealed class LiveRunner
{
    public const string StoppedBefore = "The run was stopped before this test.";
    public const string StoppedDuring = "The run was stopped during this test.";
    /// <summary>How long a test that doesn't stop when its time is up gets before the run moves on without it.</summary>
    static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    readonly IProgress<LiveProgress>? _progress;
    readonly List<LiveResult> _results = [];

    public LiveRunner(IProgress<LiveProgress>? progress = null, CancellationToken ct = default)
    {
        _progress = progress;
        Token = ct;
    }

    /// <summary>The run's own token: cancelled when the run is stopped.</summary>
    public CancellationToken Token { get; }
    public bool Stopped => Token.IsCancellationRequested;
    /// <summary>How long a test gets when it doesn't say.</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public IReadOnlyList<LiveResult> Results => _results;

    /// <summary>
    /// Runs one test. Its token is cancelled when its time is up or the run is stopped; anything it throws makes it
    /// Failed, with what went wrong.
    /// </summary>
    public async Task<LiveResult> RunAsync(string area, string name, Func<CancellationToken, Task<LiveVerdict>> test, TimeSpan? timeout = null)
    {
        if (Stopped) return Add(area, name, LiveVerdict.Skip(StoppedBefore), DateTimeOffset.UtcNow, TimeSpan.Zero);
        var limit = timeout ?? DefaultTimeout;
        _progress?.Report(new LiveProgress(area, name, null, _results.Count, Failures));
        var started = DateTimeOffset.UtcNow;
        var took = Stopwatch.StartNew();
        using var time = CancellationTokenSource.CreateLinkedTokenSource(Token);
        time.CancelAfter(limit);
        LiveVerdict verdict;
        Task<LiveVerdict>? running = null;
        // A test that doesn't notice its token still can't hold up the run. When the run is stopped, the test gets a moment
        // to tidy up (the knob check puts knobs back) before the run ends without it.
        using var giveUp = new CancellationTokenSource(limit + Grace);
        using var stopping = Token.Register(() => giveUp.CancelAfter(Grace));
        try
        {
            running = Task.Run(() => test(time.Token), time.Token);
            var late = Task.Delay(Timeout.Infinite, giveUp.Token);
            if (await Task.WhenAny(running, late) == late)
                verdict = Stopped ? LiveVerdict.Skip(StoppedDuring)
                    : LiveVerdict.Fail($"It didn't finish within {Seconds(limit)}, so the run went on without it.");
            else verdict = await running;
        }
        catch (OperationCanceledException) when (Stopped)
        {
            verdict = LiveVerdict.Skip(StoppedDuring);
        }
        catch (OperationCanceledException) when (time.IsCancellationRequested)
        {
            verdict = LiveVerdict.Fail($"It didn't finish within {Seconds(limit)}.");
        }
        catch (DawLinkException e)
        {
            verdict = LiveVerdict.Fail(e.Message, e.InnerException is { } inner ? new JsonObject { ["error"] = inner.ToString() } : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file the test writes or reads: the system's words say what.
            verdict = LiveVerdict.Fail(e.Message, new JsonObject { ["error"] = e.ToString() });
        }
        catch (Exception e)
        {
            verdict = LiveVerdict.Fail($"{e.GetType().Name}: {e.Message}", new JsonObject { ["error"] = e.ToString() });
        }
        if (running is { IsCompleted: false })
            _ = running.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);   // so it can't go unobserved
        return Add(area, name, verdict, started, took.Elapsed);
    }

    /// <summary>Adds a test that wasn't run (or was worked out from another's findings) with its verdict as given.</summary>
    public LiveResult Record(string area, string name, LiveVerdict verdict) => Add(area, name, verdict, DateTimeOffset.UtcNow, TimeSpan.Zero);

    int Failures => _results.Count(r => r.Outcome == LiveOutcome.Failed);

    LiveResult Add(string area, string name, LiveVerdict verdict, DateTimeOffset started, TimeSpan took)
    {
        var result = new LiveResult(area, name, verdict.Outcome, verdict.Detail, started, took) { Data = verdict.Data, Lines = verdict.Lines };
        _results.Add(result);
        _progress?.Report(new LiveProgress(area, name, result, _results.Count, Failures));
        return result;
    }

    static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
}
