using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Threading;
using MusicOrganiser.ViewModels;
using Activity = MusicOrganiser.ViewModels.Activity;

namespace MusicOrganiser.Avalonia.Diagnostics;

/// <summary>
/// Notices when the app stops answering (Windows' "Not responding"): a background thread knocks on the UI thread every
/// fifth of a second, and when the answer takes longer than <see cref="Threshold"/>, the run log gets a warning with how
/// long it froze, the page it was on and the jobs running then. The UI thread's own stack can't be read from another
/// thread in .NET, so the running jobs are what say what froze.
/// </summary>
sealed class UiWatchdog : IDisposable
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(250);

    readonly ActivityFeed _jobs;
    readonly Func<string> _page;
    /// <summary>The jobs running now, kept as they start and end, so the watchdog can name them while the UI thread is stuck.</summary>
    readonly ConcurrentDictionary<Activity, string> _running = new();
    readonly Thread _thread;
    volatile bool _stop;

    public UiWatchdog(MainViewModel main) : this(main.Activities, () => main.SelectedNav?.Title ?? "") { }

    /// <param name="page">The page showing; read from the watchdog's thread, so it mustn't touch the UI.</param>
    public UiWatchdog(ActivityFeed jobs, Func<string> page)
    {
        _jobs = jobs;
        _page = page;
        jobs.Changed += OnJob;
        _thread = new Thread(Watch) { IsBackground = true, Name = "UI watchdog", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    void OnJob(Activity job, ActivityState? ended)
    {
        var log = SessionLog.Current;
        if (ended is null)
        {
            _running[job] = job.Title;
            log?.Info("Job", $"Started: {job.Title}{Detail(job)}");
            return;
        }
        _running.TryRemove(job, out _);
        var line = $"{ended}: {job.Title}{Detail(job)} after {Seconds(job.Elapsed)}";
        if (ended == ActivityState.Failed) log?.Warning("Job", line); else log?.Info("Job", line);
    }

    static string Detail(Activity job) => job.Detail.Length > 0 ? $" ({job.Detail})" : "";

    void Watch()
    {
        using var answered = new ManualResetEventSlim();
        while (!_stop)
        {
            answered.Reset();
            var knocked = Stopwatch.GetTimestamp();
            Dispatcher.UIThread.Post(answered.Set, DispatcherPriority.Send);
            if (!answered.Wait(Threshold))
            {
                // Stuck: note what was going on now, while it still is, then wait for it to come back.
                var page = _page();
                var jobs = string.Join(", ", _running.Values);
                while (!_stop && !answered.Wait(TimeSpan.FromSeconds(1))) { }
                if (_stop) return;
                var froze = Stopwatch.GetElapsedTime(knocked);
                SessionLog.Current?.Warning("Freeze",
                    $"The app didn't respond for {Seconds(froze)} on {(page.Length > 0 ? page : "?")}" +
                    (jobs.Length > 0 ? $", while running: {jobs}" : ", with no job running (a click or the page itself)"));
            }
            Thread.Sleep(200);
        }
    }

    static string Seconds(TimeSpan t) => t.TotalSeconds >= 1 ? $"{t.TotalSeconds:0.0} s" : $"{t.TotalMilliseconds:0} ms";

    public void Dispose()
    {
        _stop = true;
        _jobs.Changed -= OnJob;
    }
}
