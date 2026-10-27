using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// A sound's waveform you can play from anywhere: click a point to start there. The part
/// already played is drawn brighter, a playhead moves across while it plays, and the mouse
/// shows a guide line with the time under it. A click sends a soft ripple out from the point.
/// </summary>
public sealed class WaveformView : Control
{
    public static readonly StyledProperty<float[]?> PeaksProperty = AvaloniaProperty.Register<WaveformView, float[]?>(nameof(Peaks));
    /// <summary>Seconds the peaks cover (the analysis reads up to the first minute).</summary>
    public static readonly StyledProperty<double> DurationProperty = AvaloniaProperty.Register<WaveformView, double>(nameof(Duration));
    /// <summary>Playback position in seconds, or below 0 when not playing.</summary>
    public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<WaveformView, double>(nameof(Position), -1);
    /// <summary>Runs with the clicked time in seconds.</summary>
    public static readonly StyledProperty<ICommand?> SeekCommandProperty = AvaloniaProperty.Register<WaveformView, ICommand?>(nameof(SeekCommand));

    static WaveformView() => AffectsRender<WaveformView>(PeaksProperty, DurationProperty, PositionProperty);

    public float[]? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public ICommand? SeekCommand { get => GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    double? _hoverX;
    readonly Ripple _ripple;

    public WaveformView()
    {
        _ripple = new Ripple(this);
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Click anywhere to play from there");
        ClipToBounds = true;
    }

    IBrush Res(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hoverX = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var w = Bounds.Width;
        if (w <= 0 || Duration <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var at = e.GetPosition(this);
        var seconds = Math.Clamp(at.X / w, 0, 1) * Duration;
        if (SeekCommand?.CanExecute(seconds) == true) SeekCommand.Execute(seconds);
        _ripple.Start(at);
        e.Handled = true;
    }

    public override void Render(DrawingContext dc)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        dc.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));   // the whole area takes clicks
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
                ctx.BeginFigure(new Point(x, mid - half), true);
                ctx.LineTo(new Point(x + bar, mid - half));
                ctx.LineTo(new Point(x + bar, mid + half));
                ctx.LineTo(new Point(x, mid + half));
                ctx.EndFigure(true);
            }
        }
        dc.DrawGeometry(playedTo >= 0 ? dim : accent, null, rest);
        dc.DrawGeometry(accent, null, played);

        if (playedTo >= 0)
            dc.FillRectangle(Res("TextBrush"), new Rect(Math.Min(playedTo, w - 2), 0, 2, h));

        if (_hoverX is { } hx)
        {
            dc.FillRectangle(Res("MutedBrush"), new Rect(hx, 0, 1, h));
            var t = Math.Clamp(hx / w, 0, 1) * Duration;
            var label = new FormattedText(TimeLabel(t), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 11, Res("TextBrush"));
            var lx = hx + 4 + label.Width > w ? hx - 4 - label.Width : hx + 4;
            dc.DrawRectangle(Res("SidebarBrush"), null, new RoundedRect(new Rect(lx - 3, 2, label.Width + 6, label.Height + 2), 3));
            dc.DrawText(label, new Point(lx, 3));
        }

        _ripple.Draw(dc, accent);
    }

    /// <summary>
    /// The time under the pointer: "12.34s", or "1:05.2" from a minute on. Rounded to what's shown before it's split up,
    /// so 119.96 s is "2:00.0", not "1:60.0".
    /// </summary>
    internal static string TimeLabel(double t)
    {
        if (Math.Round(t, 2, MidpointRounding.AwayFromZero) is var hundredths and < 60) return $"{hundredths:0.00}s";
        var tenths = Math.Round(t, 1, MidpointRounding.AwayFromZero);
        var minutes = (int)(tenths / 60);
        return $"{minutes}:{tenths - minutes * 60:00.0}";
    }
}
