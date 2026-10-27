namespace MusicOrganiser.ViewModels;

/// <summary>
/// Starts quick previews (sweeping across the galaxy) off the UI thread, so opening each file never holds up the
/// mouse. Only the newest one asked for is played: stars passed while a file was still opening are skipped.
/// </summary>
public sealed class PreviewWorker(IAudioPlayer player, Action<string, Exception> failed)
{
    readonly object _gate = new();
    /// <summary>False plays straight away on the calling thread (the tests, which check what played in order).</summary>
    public static bool InBackground { get; set; } = true;

    readonly SynchronizationContext? _ui = InBackground ? SynchronizationContext.Current : null;
    string? _want;
    bool _stop, _running;

    public void Play(string path)
    {
        Latest = path;
        Ask(path, stop: false);
    }

    /// <summary>The last preview asked for (null after a stop).</summary>
    public string? Latest { get; private set; }

    /// <summary>A preview is still being opened.</summary>
    public bool Busy
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>Stops the preview, once any that's still opening has started.</summary>
    public void Stop()
    {
        Latest = null;
        Ask(null, stop: true);
    }

    /// <summary>Forgets a preview that hasn't started yet (something is being played on purpose instead).</summary>
    public void Cancel()
    {
        lock (_gate) { _want = null; _stop = false; }
        Latest = null;
    }

    // ---- warming up the files just ahead of the mouse ----------------------------------------------------

    /// <summary>How much of each file is read ahead: its header and the first moment, all a preview needs to start.</summary>
    const int WarmBytes = 128 * 1024;
    readonly object _warmGate = new();
    // The files to read, nearest the mouse first: each new batch replaces the last, which the mouse has passed by now.
    readonly Queue<string> _toWarm = new();
    // Files read lately, so a sweep back over the same stars doesn't read them again (the system keeps them now).
    readonly HashSet<string> _warmed = [];
    readonly Queue<string> _warmedOrder = new();
    // One warm-up thread for the whole run, woken when there's a batch; started the first time it's needed.
    readonly AutoResetEvent _warmWake = new(false);
    bool _warmStarted;
    // Clear while a preview is opening: the warm-up waits, so its reads never queue on the disk in front of the sound
    // that's due now (a low thread priority doesn't lower the disk's).
    readonly ManualResetEventSlim _notOpening = new(true);
    // Files whose start has been read, so the sweep log can say whether reading ahead got there first.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _read = new();

    /// <summary>
    /// Reads the start of files the mouse is about to reach, on a thread of its own at low priority, so the system has
    /// them in memory when they're asked for: on a hard drive, a file not read for a while takes tens of milliseconds
    /// to open (the disk finding its folder, then the file), which is the sound arriving that much after the mouse.
    /// </summary>
    public void Warm(IEnumerable<string> paths)
    {
        if (_ui is null) return;   // tests that play in order have nothing to wait for
        lock (_warmGate)
        {
            _toWarm.Clear();
            foreach (var p in paths)
                if (!_warmed.Contains(p) && !_toWarm.Contains(p)) _toWarm.Enqueue(p);
            if (_toWarm.Count == 0) return;
            if (!_warmStarted)
            {
                _warmStarted = true;
                new Thread(WarmWork) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Preview warm-up" }.Start();
            }
        }
        _warmWake.Set();
    }

    void WarmWork()
    {
        var buffer = new byte[WarmBytes];
        while (true)
        {
            _warmWake.WaitOne();
            while (true)
            {
                _notOpening.Wait();
                string path;
                lock (_warmGate)
                {
                    if (!_toWarm.TryDequeue(out path!)) break;
                    _warmed.Add(path);
                    _warmedOrder.Enqueue(path);
                    if (_warmedOrder.Count > 4096) _warmed.Remove(_warmedOrder.Dequeue());
                }
                if (path == Latest) continue;   // the preview is reading that one itself
                try
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                    _ = file.Read(buffer, 0, buffer.Length);
                    if (_read.Count > 4096) _read.Clear();
                    _read[path] = 0;
                }
                catch (Exception) { }   // only a head start: it plays (or says why) when asked
            }
        }
    }

    void Ask(string? path, bool stop)
    {
        if (_ui is null)
        {
            Run(path, stop);
            return;
        }
        lock (_gate)
        {
            // One asked for and not started yet is never played now: the sweep's timings stop waiting for it.
            if (_want is { } superseded && superseded != path) MusicOrganiser.Core.SweepTiming.Dropped(superseded);
            _want = path;
            _stop = stop;
            if (_running) return;
            _running = true;
        }
        // Its own thread, ahead of background work: on the shared thread pool a sample scan's work queued in front, so
        // sweeping the galaxy during a scan played sounds late, or not at all for a while.
        new Thread(Work) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "Preview" }.Start();
    }

    void Work()
    {
        while (true)
        {
            string? path;
            bool stop;
            lock (_gate)
            {
                (path, stop) = (_want, _stop);
                (_want, _stop) = (null, false);
                if (path is null && !stop)
                {
                    _running = false;
                    return;
                }
            }
            Run(path, stop);
        }
    }

    void Run(string? path, bool stop)
    {
        try
        {
            if (path is not null)
            {
                var readAhead = _read.ContainsKey(path);
                MusicOrganiser.Core.SweepTiming.Opening(path);
                _notOpening.Reset();
                try { player.Play(path); }
                finally { _notOpening.Set(); }
                MusicOrganiser.Core.SweepTiming.Started(path, readAhead);
            }
            else if (stop) player.Stop();
        }
        catch (Exception e) when (path is not null)
        {
            MusicOrganiser.Core.SweepTiming.Dropped(path);
            if (_ui is null) failed(path, e);
            else _ui.Post(_ => failed(path, e), null);
        }
        catch (Exception) { }   // a stop that fails leaves nothing playing anyway
    }
}
