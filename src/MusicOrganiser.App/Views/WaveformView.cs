using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MusicOrganiser.App.Views;

/// <summary>
/// A sound's waveform you can play from anywhere: click a point to start there. The part
/// already played is drawn brighter, a playhead moves across while it plays, and the mouse
/// shows a guide line with the time under it. A click sends a soft ripple out from the point.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = Reg(nameof(Peaks), typeof(float[]), null);
    /// <summary>Seconds the peaks cover (the analysis reads up to the first minute).</summary>
    public static readonly DependencyProperty DurationProperty = Reg(nameof(Duration), typeof(double), 0.0);
    /// <summary>Playback position in seconds, or below 0 when not playing.</summary>
    public static readonly DependencyProperty PositionProperty = Reg(nameof(Position), typeof(double), -1.0);
    /// <summary>Runs with the clicked time in seconds.</summary>
    public static readonly DependencyProperty SeekCommandProperty = DependencyProperty.Register(
        nameof(SeekCommand), typeof(ICommand), typeof(WaveformView));

    static readonly DependencyProperty RippleProperty = Reg("Ripple", typeof(double), 0.0);

    static DependencyProperty Reg(string name, Type type, object? fallback) => DependencyProperty.Register(
        name, type, typeof(WaveformView), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    public float[]? Peaks { get => (float[]?)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public ICommand? SeekCommand { get => (ICommand?)GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    double? _hoverX;
    Point _rippleAt;

    public WaveformView()
    {
        Cursor = Cursors.Hand;
        ToolTip = "Click anywhere to play from there";
        ClipToBounds = true;
    }

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverX = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (ActualWidth <= 0 || Duration <= 0) return;
        var at = e.GetPosition(this);
        var seconds = Math.Clamp(at.X / ActualWidth, 0, 1) * Duration;
        if (SeekCommand?.CanExecute(seconds) == true) SeekCommand.Execute(seconds);
        _rippleAt = at;
        BeginAnimation(RippleProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // the whole area takes clicks
        if (Peaks is not { Length: > 0 } peaks || w <= 0 || h <= 0) return;

        var playedTo = Position >= 0 && Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) * w : -1;
        var accent = Res("AccentBrush");
        var dim = Res("FaintBrush");
        var mid = h / 2;
        var step = w / peaks.Length;
        var bar = Math.Max(1, step * 0.6);
        var played = new StreamGeometry();
        var rest = new StreamGeometry();
        using (var p = played.Open())
        using (var r = rest.Open())
        {
            for (var i = 0; i < peaks.Length; i++)
            {
                var x = i * step + (step - bar) / 2;
                var half = Math.Max(0.75, peaks[i] * (mid - 2));
                var ctx = playedTo >= 0 && x <= playedTo ? p : r;
                ctx.BeginFigure(new Point(x, mid - half), true, true);
                ctx.LineTo(new Point(x + bar, mid - half), false, false);
                ctx.LineTo(new Point(x + bar, mid + half), false, false);
                ctx.LineTo(new Point(x, mid + half), false, false);
            }
        }
        dc.DrawGeometry(playedTo >= 0 ? dim : accent, null, rest);
        dc.DrawGeometry(accent, null, played);

        if (playedTo >= 0)
            dc.DrawRectangle(Res("TextBrush"), null, new Rect(Math.Min(playedTo, w - 2), 0, 2, h));

        if (_hoverX is { } hx)
        {
            dc.DrawRectangle(Res("MutedBrush"), null, new Rect(hx, 0, 1, h));
            var t = Math.Clamp(hx / w, 0, 1) * Duration;
            var label = new FormattedText(t >= 60 ? $"{(int)t / 60}:{t % 60:00.0}" : $"{t:0.00}s", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Res("TextBrush"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var lx = hx + 4 + label.Width > w ? hx - 4 - label.Width : hx + 4;
            dc.DrawRoundedRectangle(Res("SidebarBrush"), null, new Rect(lx - 3, 2, label.Width + 6, label.Height + 2), 3, 3);
            dc.DrawText(label, new Point(lx, 3));
        }

        var ripple = (double)GetValue(RippleProperty);
        if (ripple is > 0 and < 1)
        {
            var pen = new Pen(accent, 2) { };
            dc.PushOpacity(1 - ripple);
            dc.DrawEllipse(null, pen, _rippleAt, 6 + ripple * 30, 6 + ripple * 30);
            dc.Pop();
        }
    }
}
