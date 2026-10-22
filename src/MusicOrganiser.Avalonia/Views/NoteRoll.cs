using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// A tiny piano roll of one channel's notes, on a time scale shared by the whole song. With a
/// <see cref="SeekCommand"/> it plays from wherever you click: notes already played light up, a
/// playhead moves across, and the mouse shows a guide with the bar and beat under it.
/// </summary>
public sealed class NoteRoll : Control
{
    public static readonly StyledProperty<MidiChannel?> ChannelProperty = AvaloniaProperty.Register<NoteRoll, MidiChannel?>(nameof(Channel));
    public static readonly StyledProperty<long> SongEndProperty = AvaloniaProperty.Register<NoteRoll, long>(nameof(SongEnd));
    /// <summary>Playback position in song ticks, or below 0 when this channel isn't playing.</summary>
    public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<NoteRoll, double>(nameof(Position), -1);
    /// <summary>Runs with the clicked point in song ticks.</summary>
    public static readonly StyledProperty<ICommand?> SeekCommandProperty = AvaloniaProperty.Register<NoteRoll, ICommand?>(nameof(SeekCommand));

    /// <summary>For a phrase joined from clips: where each clip sits, drawn as a line where it starts, the one looked for lit up.</summary>
    public static readonly StyledProperty<IReadOnlyList<RollSpan>?> SpansProperty = AvaloniaProperty.Register<NoteRoll, IReadOnlyList<RollSpan>?>(nameof(Spans));

    static NoteRoll() => AffectsRender<NoteRoll>(ChannelProperty, SongEndProperty, PositionProperty, SpansProperty);

    public IReadOnlyList<RollSpan>? Spans { get => GetValue(SpansProperty); set => SetValue(SpansProperty, value); }

    public MidiChannel? Channel { get => GetValue(ChannelProperty); set => SetValue(ChannelProperty, value); }
    public long SongEnd { get => GetValue(SongEndProperty); set => SetValue(SongEndProperty, value); }
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public ICommand? SeekCommand { get => GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    double? _hoverX;
    readonly Ripple _ripple;

    public NoteRoll() => _ripple = new Ripple(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SeekCommandProperty) return;
        Cursor = SeekCommand is null ? null : new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, SeekCommand is null ? null : "Click anywhere to play from there");
    }

    long End => Math.Max(1, SongEnd > 0 ? SongEnd : Channel?.End ?? 1);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (SeekCommand is null) return;
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
        if (SeekCommand is null || w <= 4 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var at = e.GetPosition(this);
        var ticks = Math.Clamp((at.X - 2) / (w - 4), 0, 1) * End;
        if (SeekCommand.CanExecute(ticks)) SeekCommand.Execute(ticks);
        _ripple.Start(at);
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
                ctx.BeginFigure(new Point(x, y), true);
                ctx.LineTo(new Point(x + nw, y));
                ctx.LineTo(new Point(x + nw, y + rowH));
                ctx.LineTo(new Point(x, y + rowH));
                ctx.EndFigure(true);
            }
        _cache = (c, end, w, h, g);
        return g;
    }

    IBrush Res(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    public override void Render(DrawingContext dc)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        dc.DrawRectangle(Res("SurfaceRaisedBrush", Brushes.Transparent), null, new RoundedRect(new Rect(0, 0, w, h), 4));
        if (Channel is not { Notes.Count: > 0 } c || w < 4 || h < 4) return;

        var fill = Res("AccentBrush", Brushes.DodgerBlue);
        var end = End;
        if (Spans is { Count: > 0 } spans)
        {
            var line = Res("MutedBrush", Brushes.Gray);
            foreach (var s in spans)
            {
                var x0 = Math.Clamp(2 + (w - 4) * s.Start / end, 0, w);
                var x1 = Math.Clamp(2 + (w - 4) * (s.Start + s.Length) / end, 0, w);
                if (s.Lit && x1 > x0)
                    using (dc.PushOpacity(0.22)) dc.FillRectangle(fill, new Rect(x0, 0, x1 - x0, h));
                if (s.Start > 0 && x0 < w - 2)
                    using (dc.PushOpacity(0.6)) dc.FillRectangle(line, new Rect(x0, 0, 1, h));
            }
        }
        var notes = Notes(c, end, w, h);
        if (Position >= 0)
        {
            // Everything dim, then the part already played at full strength, clipped at the playhead.
            var px = Math.Clamp(2 + (w - 4) * Position / end, 0, w - 2);
            using (dc.PushOpacity(0.4)) dc.DrawGeometry(fill, null, notes);
            using (dc.PushClip(new Rect(0, 0, px, h))) dc.DrawGeometry(fill, null, notes);
            dc.FillRectangle(Res("TextBrush", Brushes.White), new Rect(px, 0, 2, h));
        }
        else dc.DrawGeometry(fill, null, notes);

        if (_hoverX is { } hx)
        {
            dc.FillRectangle(Res("MutedBrush", Brushes.Gray), new Rect(hx, 0, 1, h));
            var beat = Math.Clamp((hx - 2) / (w - 4), 0, 1) * end / c.Ppq;
            var label = new FormattedText($"Bar {(int)(beat / 4) + 1}.{(int)(beat % 4) + 1}", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 11, Res("TextBrush", Brushes.White));
            var lx = hx + 4 + label.Width > w ? hx - 4 - label.Width : hx + 4;
            dc.DrawRectangle(Res("SidebarBrush", Brushes.Black), null, new RoundedRect(new Rect(lx - 3, 2, label.Width + 6, label.Height + 2), 3));
            dc.DrawText(label, new Point(lx, 3));
        }

        _ripple.Draw(dc, fill);
    }
}
