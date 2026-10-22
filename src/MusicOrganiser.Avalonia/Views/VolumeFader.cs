using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// A track's volume as a mixer's small fader (Solomon, 2026-10-22: "should be able to change the volume of the tracks
/// individually"): a thin sunk bar filled to the level, with a mark at 0 dB. Click or drag along it to set it, scroll over
/// it for a decibel at a time, double-click for 0 dB again. Each move runs <see cref="Command"/> with a
/// <see cref="FaderMove"/>, the last one of a drag marked done.
/// </summary>
public sealed class VolumeFader : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<VolumeFader, double>(nameof(Value), 1);
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<VolumeFader, double>(nameof(Maximum), 1.27);
    public static readonly StyledProperty<ICommand?> CommandProperty = AvaloniaProperty.Register<VolumeFader, ICommand?>(nameof(Command));
    /// <summary>The fill: the track's colour.</summary>
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<VolumeFader, IBrush?>(nameof(Fill));

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    static VolumeFader() => AffectsRender<VolumeFader>(ValueProperty, MaximumProperty, FillProperty);

    public VolumeFader()
    {
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        ToolTip.SetTip(this, "Volume: drag along it, scroll over it, or double-click for 0 dB");
    }

    static int _gestures;
    int _gesture;
    bool _dragging;

    void Send(double value, bool done)
    {
        var move = new FaderMove(Math.Clamp(value, 0, Maximum), _gesture, done);
        if (Command is { } c && c.CanExecute(move)) c.Execute(move);
    }

    double ValueAt(Point p) => Bounds.Width <= 2 ? Value : Math.Clamp((p.X - 1) / (Bounds.Width - 2), 0, 1) * Maximum;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _gesture = ++_gestures;
        e.Handled = true;
        if (e.ClickCount == 2) { Send(1, done: true); return; }
        _dragging = true;
        e.Pointer.Capture(this);
        Send(ValueAt(e.GetPosition(this)), done: false);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) Send(ValueAt(e.GetPosition(this)), done: false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        Send(ValueAt(e.GetPosition(this)), done: true);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_dragging) return;
        _dragging = false;
        Send(Value, done: true);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;
        _gesture = ++_gestures;
        // a decibel a notch (the volume sounds as its square: 40·log10)
        var v = Math.Max(Value, 0.05) * Math.Pow(10, Math.Sign(e.Delta.Y) / 40.0);
        Send(v < 0.06 && e.Delta.Y < 0 ? 0 : v, done: true);
        e.Handled = true;
    }

    IBrush Res(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var r) && r is IBrush b ? b : fallback;

    public override void Render(DrawingContext dc)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 2 || h <= 0) return;
        var well = new Rect(0, 0, w, h);
        dc.DrawRectangle(Res("StudioWellBrush", Brushes.Black), new Pen(Res("StudioEdgeBrush", Brushes.Black), 1), well.Deflate(0.5), 2, 2);
        var level = Math.Clamp(Value / Math.Max(0.01, Maximum), 0, 1);
        if (level > 0)
            using (dc.PushOpacity(0.85))
                dc.DrawRectangle(Fill ?? Res("AccentBrush", Brushes.White), null, new Rect(1, 1, (w - 2) * level, h - 2), 1.5, 1.5);
        // 0 dB
        var zero = 1 + (w - 2) / Maximum;
        dc.FillRectangle(Res("TextBrush", Brushes.White), new Rect(Math.Round(zero) - 0.5, 0, 1, h));
    }
}
