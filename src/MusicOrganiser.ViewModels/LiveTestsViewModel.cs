using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.LiveTests;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// A DAW's live tests in Settings (for Bitwig, <see cref="BitwigLiveTests"/>): runs them against the DAW open now while it
/// answers, shows each test as it goes and the summary at the end, and opens the run's readable report. The reports stay
/// in their folder (reports\live in the app's data folder), so the newest is there to open after a restart too.
/// </summary>
public sealed partial class LiveTestsViewModel : ObservableObject
{
    /// <summary>Runs the tests, writing the reports into <paramref name="folder"/>.</summary>
    public delegate Task<LiveRun> Runner(string folder, IProgress<LiveProgress> progress, CancellationToken ct);

    /// <summary>How many failed tests the summary names; the report has them all.</summary>
    const int Named = 5;

    readonly Runner _run;
    /// <summary>Keeps the progress line, which the run's progress and Stop both set, from going back to an older one.</summary>
    readonly object _gate = new();
    CancellationTokenSource? _stop;
    bool _ran;

    /// <param name="key">The DAW in the report files' names: "bitwig".</param>
    /// <param name="folder">Where the reports go.</param>
    public LiveTestsViewModel(string key, string folder, Runner run)
    {
        Key = key;
        Folder = folder;
        _run = run;
    }

    public string Key { get; }
    public string Folder { get; }

    /// <summary>Whether the DAW answers right now, from the app's last look: the tests only start while it does.</summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand))] bool _answering;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand))] bool _running;
    /// <summary>The test running now, or the last to end, and how many are done: empty when no run is going.</summary>
    [ObservableProperty] string _progressLine = "";
    /// <summary>How the last run went, and where its report is.</summary>
    [ObservableProperty] string _summary = "";
    /// <summary>The newest run's readable report (.md), or null when there's none yet.</summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(OpenReportCommand))] string? _report;

    /// <summary>Finds the newest report, from a run before the app started. Settings calls it each time it opens.</summary>
    public async Task RefreshAsync()
    {
        if (Running || _ran) return;
        var (folder, key) = (Folder, Key);
        var (newest, last) = await Task.Run(() =>
        {
            try
            {
                return LiveReports.Newest(folder, key) is { } md ? (md, LastRun(md)) : ((string?)null, "");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, ""); }
        });
        if (Running || _ran) return;   // a run started meanwhile, and has its own
        Report = newest;
        Summary = last;
    }

    /// <summary>
    /// Runs every test, against the DAW open now. Changes are only made in a project made for the tests (for Bitwig, one
    /// whose name starts with “MO Live Test”, with changes allowed); the rest is only read.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task Run()
    {
        using var stop = _stop = new CancellationTokenSource();
        Running = true;
        _ran = true;
        Summary = "";
        ProgressLine = "Starting…";
        var (finished, latest) = (false, -1);
        var progress = new Progress<LiveProgress>(p =>
        {
            // Progress arrives a moment after it's reported, and where nothing queues it (no UI thread, as in tests) not
            // always in order. So a line never replaces a newer one (a test's end counts it done, then the next one starts),
            // nor Stop's, nor the summary.
            var order = p.Done * 2 + (p.Result is null ? 1 : 0);
            lock (_gate)
            {
                if (finished || stop.IsCancellationRequested || order < latest) return;
                latest = order;
                ProgressLine = Describe(p);
            }
        });
        try
        {
            Show(await _run(Folder, progress, stop.Token));
        }
        catch (OperationCanceledException)
        {
            Summary = "Stopped before any test ran.";
        }
        catch (Exception e)
        {
            // The runner catches what its tests throw, and writes its report regardless; this is it failing to start.
            Summary = $"The live tests couldn't run: {e.Message}";
        }
        finally
        {
            lock (_gate)
            {
                finished = true;
                _stop = null;
                ProgressLine = "";
            }
            Running = false;
        }
    }

    bool CanRun() => Answering && !Running;

    /// <summary>Stops the run after the test running now, which gets a moment to tidy up. The report is still written.</summary>
    [RelayCommand]
    void Stop()
    {
        lock (_gate)
        {
            if (_stop is not { IsCancellationRequested: false } stop) return;
            stop.Cancel();
            ProgressLine = "Stopping: the test running now is tidying up, then the report is written…";
        }
    }

    /// <summary>Opens the readable report, in whatever opens .md files (or shows it, when nothing does).</summary>
    [RelayCommand(CanExecute = nameof(HasReport))]
    void OpenReport()
    {
        if (Report is not { } file) return;
        if (!File.Exists(file))
        {
            Report = null;
            Summary = $"The report isn't there any more ({file}).";
            return;
        }
        try { Shell.Open(file); }
        catch (Win32Exception)
        {
            // Nothing is set to open .md files (Windows says so, rather than asking which app to use).
            try { Shell.Reveal(file); }
            catch (Win32Exception) { }
            const string note = "Nothing on this computer is set to open .md files: open the report with Notepad, or any text editor.";
            if (!Summary.StartsWith(note, StringComparison.Ordinal)) Summary = note + Environment.NewLine + Summary;
        }
    }

    bool HasReport() => Report is not null;

    /// <summary>E.g. "Changes › New track… (12 done, 1 failed)".</summary>
    static string Describe(LiveProgress p) =>
        $"{p.Text} ({p.Done} done{(p.Failed > 0 ? $", {p.Failed} failed" : "")})";

    /// <summary>The run's counts, the tests that failed, what to do when changes weren't allowed, and where the report is.</summary>
    void Show(LiveRun run)
    {
        var lines = new List<string> { run.Summary + "." };
        var failed = run.Results.Where(r => r.Outcome == LiveOutcome.Failed).Select(r => r.Title).ToList();
        if (failed.Count > 0)
            lines.Add("Failed: " + string.Join(", ", failed.Take(Named)) + (failed.Count > Named ? $", and {failed.Count - Named} more" : "") + ".");
        // Every change skipped for the same reason says once what to do about it.
        if (run.Results.FirstOrDefault(r => r.Outcome == LiveOutcome.Skipped && r.Detail.EndsWith(BitwigLiveTests.HowToAllowChanges, StringComparison.Ordinal))
            is { } closed)
            lines.Add(closed.Detail);
        lines.Add(run.ReportMarkdown is { } md ? $"The report: {md}" : run.ReportProblem ?? "");
        Summary = string.Join(Environment.NewLine, lines.Where(l => l.Length > 0));
        if (run.ReportMarkdown is not null) Report = run.ReportMarkdown;
    }

    /// <summary>The newest run in a line, from its JSON report: "The last run, 14 Oct 15:30: 20 passed, …". Empty if it can't be read.</summary>
    static string LastRun(string markdown)
    {
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(markdown, ".json")));
            if (json?["summary"]?.GetValue<string>() is { Length: > 0 } summary &&
                DateTimeOffset.TryParse(json["started"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var started))
                return $"The last run, {started.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture)}: {summary}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
        return "";
    }
}
