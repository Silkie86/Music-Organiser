using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.App.Views;

/// <summary>
/// A tiny piano roll of one channel's notes, on a time scale shared by the whole song. With a
/// <see cref="SeekCommand"/> it plays from wherever you click: notes already played light up, a
/// playhead moves across, and the mouse shows a guide with the bar and beat under it.
/// </summary>
public sealed class NoteRoll : FrameworkElement
{
    public static readonly DependencyProperty ChannelProperty = Reg(nameof(Channel), typeof(MidiChannel), null);
    public static readonly DependencyProperty SongEndProperty = Reg(nameof(SongEnd), typeof(long), 0L);
    /// <summary>Playback position in song ticks, or below 0 when this channel isn't playing.</summary>
    public static readonly DependencyProperty PositionProperty = Reg(nameof(Position), typeof(double), -1.0);
    /// <summary>Runs with the clicked point in song ticks.</summary>
    public static readonly DependencyProperty SeekCommandProperty = DependencyProperty.Register(
        nameof(SeekCommand), typeof(ICommand), typeof(NoteRoll), new PropertyMetadata(null, (d, _) => ((NoteRoll)d).OnSeekable()));

    static readonly DependencyProperty RippleProperty = Reg("Ripple", typeof(double), 0.0);

    static DependencyProperty Reg(string name, Type type, object? fallback) => DependencyProperty.Register(
        name, type, typeof(NoteRoll), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    public MidiChannel? Channel { get => (MidiChannel?)GetValue(ChannelProperty); set => SetValue(ChannelProperty, value); }
    public long SongEnd { get => (long)GetValue(SongEndProperty); set => SetValue(SongEndProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public ICommand? SeekCommand { get => (ICommand?)GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    double? _hoverX;
    Point _rippleAt;

    void OnSeekable()
    {
        Cursor = SeekCommand is null ? null : Cursors.Hand;
        ToolTip = SeekCommand is null ? null : "Click anywhere to play from there";
    }

    long End => Math.Max(1, SongEnd > 0 ? SongEnd : Channel?.End ?? 1);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (SeekCommand is null) return;
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
        if (SeekCommand is null || ActualWidth <= 4) return;
        var at = e.GetPosition(this);
        var ticks = Math.Clamp((at.X - 2) / (ActualWidth - 4), 0, 1) * End;
        if (SeekCommand.CanExecute(ticks)) SeekCommand.Execute(ticks);
        _rippleAt = at;
        BeginAnimation(RippleProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        e.Handled = true;
    }

    (MidiChannel Channel, long End, double W, double H, Geometry Shape)? _cache;

    /// <summary>The notes as one shape, made once per size, so the playhead can move every frame cheaply.</summary>
    Geometry Notes(MidiChannel c, long end, double w, double h)
    {
        if (_cache is { } k && ReferenceEquals(k.Channel, c) && k.End == end && k.W == w && k.H == h) return k.Shape;
        var lo = c.Lowest;
        var rows = Math.Max(1, c.Highest - lo + 1);
        var rowH = Math.Max(1.5, (h - 4) / rows);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
            foreach (var n in c.Notes)
            {
                var x = 2 + (w - 4) * n.Start / end;
                var nw = Math.Max(1.0, (w - 4) * n.Length / end);
                var y = 2 + (h - 4 - rowH) * (rows == 1 ? 0.5 : 1 - (double)(n.Key - lo) / (rows - 1));
                ctx.BeginFigure(new Point(x, y), true, true);
                ctx.LineTo(new Point(x + nw, y), false, false);
                ctx.LineTo(new Point(x + nw, y + rowH), false, false);
                ctx.LineTo(new Point(x, y + rowH), false, false);
            }
        g.Freeze();
        _cache = (c, end, w, h, g);
        return g;
    }

    Brush Find(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var back = Find("ControlFillColorDefaultBrush", Brushes.Transparent);
        dc.DrawRoundedRectangle(back, null, new Rect(0, 0, w, h), 4, 4);
        if (Channel is not { Notes.Count: > 0 } c || w < 4 || h < 4) return;

        var fill = Find("SystemAccentColorPrimaryBrush", Brushes.DodgerBlue);
        var end = End;
        var notes = Notes(c, end, w, h);
        if (Position >= 0)
        {
            // Everything dim, then the part already played at full strength, clipped at the playhead.
            var px = Math.Clamp(2 + (w - 4) * Position / end, 0, w - 2);
            dc.PushOpacity(0.4);
            dc.DrawGeometry(fill, null, notes);
            dc.Pop();
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, px, h)));
            dc.DrawGeometry(fill, null, notes);
            dc.Pop();
            dc.DrawRectangle(Find("TextFillColorPrimaryBrush", Brushes.White), null, new Rect(px, 0, 2, h));
        }
        else dc.DrawGeometry(fill, null, notes);

        if (_hoverX is { } hx)
        {
            dc.DrawRectangle(Find("TextFillColorSecondaryBrush", Brushes.Gray), null, new Rect(hx, 0, 1, h));
            var beat = Math.Clamp((hx - 2) / (w - 4), 0, 1) * end / c.Ppq;
            var label = new FormattedText($"Bar {(int)(beat / 4) + 1}.{(int)(beat % 4) + 1}", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Find("TextFillColorPrimaryBrush", Brushes.White),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var lx = hx + 4 + label.Width > w ? hx - 4 - label.Width : hx + 4;
            dc.DrawRoundedRectangle(Find("SidebarBrush", Brushes.Black), null, new Rect(lx - 3, 2, label.Width + 6, label.Height + 2), 3, 3);
            dc.DrawText(label, new Point(lx, 3));
        }

        var ripple = (double)GetValue(RippleProperty);
        if (ripple is > 0 and < 1)
        {
            dc.PushOpacity(1 - ripple);
            dc.DrawEllipse(null, new Pen(fill, 2), _rippleAt, 6 + ripple * 30, 6 + ripple * 30);
            dc.Pop();
        }
    }
}
