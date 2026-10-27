using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

    /// <summary>A freeze this long gets the code the window's thread is in written down (<see cref="StackSnapshot"/>).</summary>
    static readonly TimeSpan StackAfter = TimeSpan.FromSeconds(1);

    /// <summary>Reading the stack snapshots the app, so a run that keeps freezing does it only this often.</summary>
    int _stacksLeft = 20;

    readonly ActivityFeed _jobs;
    readonly Func<string> _page;
    /// <summary>The jobs running now, kept as they start and end, so the watchdog can name them while the UI thread is stuck.</summary>
    readonly ConcurrentDictionary<Activity, string> _running = new();
    readonly Thread _thread;
    /// <summary>Windows' own number for the UI thread (made on it), to ask Windows what it's waiting for while it's stuck.</summary>
    readonly int _uiThread;
    /// <summary>.NET's number for the UI thread, to find its stack in a snapshot.</summary>
    readonly int _uiManagedThread;
    volatile bool _stop;

    public UiWatchdog(MainViewModel main) : this(main.Activities, () => main.SelectedNav?.Title ?? "") { }

    /// <param name="page">The page showing; read from the watchdog's thread, so it mustn't touch the UI.</param>
    public UiWatchdog(ActivityFeed jobs, Func<string> page)
    {
        _jobs = jobs;
        _page = page;
        _uiThread = OperatingSystem.IsWindows() ? (int)GetCurrentThreadId() : 0;
        _uiManagedThread = Environment.CurrentManagedThreadId;
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
                // What the window's thread is doing, and how hard the app works meanwhile, say which kind of freeze it is:
                // its own work (the processor busy), or waiting for another thread, the disk, or memory Windows had put on
                // disk (on 2026-10-22 two freezes of 6 s with no clean-up couldn't be told apart from the log).
                var state = UiThreadState();
                using var app = Process.GetCurrentProcess();
                var cpu = app.TotalProcessorTime;
                var stuckFor = Stopwatch.GetTimestamp();
                var where = $" on {(page.Length > 0 ? page : "?")}" +
                            (jobs.Length > 0 ? $", while running: {jobs}" : ", with no job running (a click or the page itself)");
                // A hang that never ends (the app is closed from Task Manager) is written while it lasts, too.
                var saidStuck = false;
                string? stack = null;
                // Dragging files out to a DAW or Explorer: on Windows the window's thread stays in the drag until the
                // drop, however long the mouse is held (on 2026-10-27 a 9.3 s drag was logged as a freeze). Not a freeze.
                var dragged = Views.FileDrag.Dragging;
                while (!_stop && !answered.Wait(TimeSpan.FromSeconds(1)))
                {
                    dragged |= Views.FileDrag.Dragging;
                    if (Debugger.IsAttached || dragged) continue;
                    // Still stuck after a second: which code it's in, so the line names the cause (on 2026-10-22 a 5 s
                    // freeze said only "waiting (Executive)").
                    if (stack is null && _stacksLeft > 0 && Stopwatch.GetElapsedTime(knocked) >= StackAfter)
                    {
                        _stacksLeft--;
                        stack = StackSnapshot.Of(_uiManagedThread) ?? "";
                    }
                    if (saidStuck || Stopwatch.GetElapsedTime(knocked) < StuckAfter) continue;
                    SessionLog.Current?.Warning("Freeze", $"The app has stopped responding for {Seconds(StuckAfter)} so far{where}{In(stack)}");
                    saidStuck = true;
                }
                if (_stop) return;
                // Stopped at a breakpoint in Visual Studio: not a freeze.
                if (Debugger.IsAttached || dragged || Views.FileDrag.Dragging) { Thread.Sleep(200); continue; }
                var froze = Stopwatch.GetElapsedTime(knocked);
                SessionLog.Current?.Warning("Freeze",
                    $"The app didn't respond for {Seconds(froze)}{where}" +
                    // Whether the stall was the garbage collector tidying memory (it stops every thread) or work.
                    $" [memory clean-up: {Seconds(GC.GetTotalPauseDuration() - paused)} paused, " +
                    $"{GC.CollectionCount(2) - fullCollections} full; {Busy(app, cpu, stuckFor)}{state}]{In(stack)}");
            }
            Thread.Sleep(200);
        }
    }

    /// <summary>
    /// How much processor time the whole app used while stuck, as a share of one core (100% = one core flat out), and its
    /// memory: near nothing means everything was waiting, not working.
    /// </summary>
    static string Busy(Process app, TimeSpan cpuBefore, long since)
    {
        try
        {
            app.Refresh();
            var used = app.TotalProcessorTime - cpuBefore;
            var memory = GC.GetGCMemoryInfo();
            var load = memory.TotalAvailableMemoryBytes > 0 ? $", the computer's memory {100.0 * memory.MemoryLoadBytes / memory.TotalAvailableMemoryBytes:0}% full" : "";
            return $"app working {100 * used / Stopwatch.GetElapsedTime(since):0}% of a core, {app.WorkingSet64 / 1e9:0.0} GB in memory{load}";
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return "app use unknown";
        }
    }

    /// <summary>"; window's thread waiting (UserRequest)": what Windows says the UI thread is doing, or nothing elsewhere.</summary>
    string UiThreadState()
    {
        if (_uiThread == 0) return "";
        try
        {
            foreach (ProcessThread t in Process.GetCurrentProcess().Threads)
            {
                if (t.Id != _uiThread) continue;
                // A wait's reason: UserRequest is another thread (a lock), PageIn memory coming back from disk, Executive
                // Windows itself (a file, a device), ExecutionDelay a sleep.
                return t.ThreadState == System.Diagnostics.ThreadState.Wait ? $"; window's thread waiting ({t.WaitReason})"
                                                                            : $"; window's thread {t.ThreadState}";
            }
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { }
        return "";
    }

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    static string In(string? stack) => stack is { Length: > 0 } ? $"; the window's thread was in {stack}" : "";

    static string Seconds(TimeSpan t) => t.TotalSeconds >= 1 ? $"{t.TotalSeconds:0.0} s" : $"{t.TotalMilliseconds:0} ms";

    public void Dispose()
    {
        _stop = true;
        _jobs.Changed -= OnJob;
    }
}
