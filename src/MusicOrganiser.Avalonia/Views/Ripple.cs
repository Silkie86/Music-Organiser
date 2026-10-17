using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>The soft ring that spreads from a click on a waveform or note roll.</summary>
sealed class Ripple(Control owner)
{
    static readonly TimeSpan Length = TimeSpan.FromMilliseconds(450);
    DispatcherTimer? _timer;
    DateTime _started;
    Point _at;

    /// <summary>0 to 1 while the ring spreads, otherwise 0.</summary>
    double Progress
    {
        get
        {
            if (_timer is null) return 0;
            var t = Math.Clamp((DateTime.UtcNow - _started) / Length, 0, 1);
            return 1 - Math.Pow(1 - t, 3);   // ease out
        }
    }

    public void Start(Point at)
    {
        _at = at;
        _started = DateTime.UtcNow;
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            if (DateTime.UtcNow - _started >= Length) Stop();
            owner.InvalidateVisual();
        });
        _timer.Start();
    }

    void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    public void Draw(DrawingContext dc, IBrush brush)
    {
        var p = Progress;
        if (p is <= 0 or >= 1) return;
        using (dc.PushOpacity(1 - p))
            dc.DrawEllipse(null, new Pen(brush, 2), _at, 6 + p * 30, 6 + p * 30);
    }
}
