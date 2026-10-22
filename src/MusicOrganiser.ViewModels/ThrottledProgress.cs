using System.Diagnostics;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// A long job's progress, shown on the UI thread at most every <paramref name="everyMs"/> milliseconds, always ending on
/// the newest report. (<see cref="Progress{T}"/> sends every report to the window: reading 357,000 sounds sent 357,000,
/// the window spent its time redrawing the activity card, some cards came up blank, and it stopped answering.)
/// </summary>
public sealed class ThrottledProgress<T>(Action<T> show, int everyMs = 250) : IProgress<T>
{
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    readonly object _gate = new();
    T? _latest;
    bool _waiting, _closed;
    long _shown;

    public void Report(T value)
    {
        lock (_gate)
        {
            if (_closed) return;
            _latest = value;
            if (_waiting) return;
            _waiting = true;
        }
        var wait = everyMs - Stopwatch.GetElapsedTime(_shown).TotalMilliseconds;
        if (wait <= 0) Post();
        else _ = Task.Delay(TimeSpan.FromMilliseconds(wait)).ContinueWith(_ => Post(), TaskScheduler.Default);
    }

    /// <summary>The job has ended: a report still on its way isn't shown over the finished card.</summary>
    public void Close()
    {
        lock (_gate) _closed = true;
    }

    void Post()
    {
        if (_ui is null) Show();
        else _ui.Post(_ => Show(), null);
    }

    void Show()
    {
        T value;
        lock (_gate)
        {
            _waiting = false;
            if (_closed) return;
            value = _latest!;
            _shown = Stopwatch.GetTimestamp();
        }
        show(value);
    }
}
