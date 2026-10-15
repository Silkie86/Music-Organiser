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

    public void Play(string path) => Ask(path, stop: false);

    /// <summary>Stops the preview, once any that's still opening has started.</summary>
    public void Stop() => Ask(null, stop: true);

    /// <summary>Forgets a preview that hasn't started yet (something is being played on purpose instead).</summary>
    public void Cancel()
    {
        lock (_gate) { _want = null; _stop = false; }
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
            _want = path;
            _stop = stop;
            if (_running) return;
            _running = true;
        }
        ThreadPool.QueueUserWorkItem(_ => Work());
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
            if (path is not null) player.Play(path);
            else if (stop) player.Stop();
        }
        catch (Exception e) when (path is not null)
        {
            if (_ui is null) failed(path, e);
            else _ui.Post(_ => failed(path, e), null);
        }
        catch (Exception) { }   // a stop that fails leaves nothing playing anyway
    }
}
