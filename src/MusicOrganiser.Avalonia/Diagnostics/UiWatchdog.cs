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

    /// <summary>A freeze this long is written while it is still going on, in case the app never comes back.</summary>
    static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(5);

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
        // Jobs already running (the start-up scan) are known from the start, not only those that start later.
        foreach (var job in jobs.Items.ToArray()) if (job.IsRunning) _running[job] = job.Title;
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
        // A one-off notice (a file saved) ends as it starts: no "after 0 ms".
        var took = job.Elapsed >= TimeSpan.FromMilliseconds(1) ? $" after {Seconds(job.Elapsed)}" : "";
        var line = $"{ended}: {job.Title}{Detail(job)}{took}";
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
            var paused = GC.GetTotalPauseDuration();
            var fullCollections = GC.CollectionCount(2);
            Dispatcher.UIThread.Post(answered.Set, DispatcherPriority.Send);
            if (!answered.Wait(Threshold))
            {
                // Stuck: note what was going on now, while it still is, then wait for it to come back.
                var page = _page();
                var jobs = string.Join(", ", _running.Values);
                var where = $" on {(page.Length > 0 ? page : "?")}" +
                            (jobs.Length > 0 ? $", while running: {jobs}" : ", with no job running (a click or the page itself)");
                // A hang that never ends (the app is closed from Task Manager) is written while it lasts, too.
                var saidStuck = false;
                while (!_stop && !answered.Wait(TimeSpan.FromSeconds(1)))
                {
                    if (saidStuck || Stopwatch.GetElapsedTime(knocked) < StuckAfter || Debugger.IsAttached) continue;
                    SessionLog.Current?.Warning("Freeze", $"The app has stopped responding for {Seconds(StuckAfter)} so far{where}");
                    saidStuck = true;
                }
                if (_stop) return;
                // Stopped at a breakpoint in Visual Studio: not a freeze.
                if (Debugger.IsAttached) { Thread.Sleep(200); continue; }
                var froze = Stopwatch.GetElapsedTime(knocked);
                SessionLog.Current?.Warning("Freeze",
                    $"The app didn't respond for {Seconds(froze)}{where}" +
                    // Whether the stall was the garbage collector tidying memory (it stops every thread) or work.
                    $" [memory clean-up: {Seconds(GC.GetTotalPauseDuration() - paused)} paused, " +
                    $"{GC.CollectionCount(2) - fullCollections} full]");
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
