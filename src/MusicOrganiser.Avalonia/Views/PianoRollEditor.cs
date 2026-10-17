using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// A piano roll to try a part against another (Solomon, 2026-10-17: "be able to edit it with the original midi greyed
/// out in the background as a guide so i can see what fits and doesn't"). The <see cref="Guide"/> is drawn grey behind;
/// the <see cref="Notes"/> on top can be edited: click an empty spot to add a note, drag a note to move it, drag its
/// right end to make it longer or shorter, right-click or double-click it to take it away. Notes a semitone from a guide
/// note sounding at the same time are drawn in red. Everything snaps to sixteenths; hold Alt to place freely.
/// The part repeats faintly when the guide is longer, and editing a repeat edits the part.
/// </summary>
public sealed class PianoRollEditor : Control
{
    const int Ppq = 96;
    const long Sixteenth = Ppq / 4;
    const double Keys = 34;   // the keyboard down the left

    public static readonly StyledProperty<IReadOnlyList<NoteEvent>?> NotesProperty =
        AvaloniaProperty.Register<PianoRollEditor, IReadOnlyList<NoteEvent>?>(nameof(Notes));
    public static readonly StyledProperty<IReadOnlyList<NoteEvent>?> GuideProperty =
        AvaloniaProperty.Register<PianoRollEditor, IReadOnlyList<NoteEvent>?>(nameof(Guide));
    /// <summary>How long the part is before it repeats (ticks).</summary>
    public static readonly StyledProperty<long> PeriodProperty = AvaloniaProperty.Register<PianoRollEditor, long>(nameof(Period));
    /// <summary>How long a stretch is shown (ticks): both parts until they meet again.</summary>
    public static readonly StyledProperty<long> LengthProperty = AvaloniaProperty.Register<PianoRollEditor, long>(nameof(Length));
    public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<PianoRollEditor, double>(nameof(Position), -1);
    /// <summary>Runs with the part's new notes after each edit.</summary>
    public static readonly StyledProperty<ICommand?> EditedCommandProperty = AvaloniaProperty.Register<PianoRollEditor, ICommand?>(nameof(EditedCommand));
    /// <summary>Runs with the ticks clicked in the strip along the top: play from there.</summary>
    public static readonly StyledProperty<ICommand?> SeekCommandProperty = AvaloniaProperty.Register<PianoRollEditor, ICommand?>(nameof(SeekCommand));

    static PianoRollEditor() =>
        AffectsRender<PianoRollEditor>(NotesProperty, GuideProperty, PeriodProperty, LengthProperty, PositionProperty);

    public IReadOnlyList<NoteEvent>? Notes { get => GetValue(NotesProperty); set => SetValue(NotesProperty, value); }
    public IReadOnlyList<NoteEvent>? Guide { get => GetValue(GuideProperty); set => SetValue(GuideProperty, value); }
    public long Period { get => GetValue(PeriodProperty); set => SetValue(PeriodProperty, value); }
    public long Length { get => GetValue(LengthProperty); set => SetValue(LengthProperty, value); }
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public ICommand? EditedCommand { get => GetValue(EditedCommandProperty); set => SetValue(EditedCommandProperty, value); }
    public ICommand? SeekCommand { get => GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    public PianoRollEditor()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>The strip along the top that shows bars and plays from where it's clicked.</summary>
    const double Ruler = 16;

    enum Drag { None, Move, Resize }

    List<NoteEvent>? _working;   // the notes while a drag is under way
    int _index = -1;             // the note being dragged or picked
    Drag _drag;
    long _grabTick, _grabFrom;
    int _grabKey;
    NoteEvent _grabbed;
    long _lastLength = Ppq / 2;
    (long Tick, int Key)? _hover;

    IReadOnlyList<NoteEvent> Current => _working ?? Notes ?? [];
    long Shown => Math.Max(Math.Max(1, Length), Math.Max(1, Period));
    long Loop => Math.Max(Sixteenth, Period > 0 ? Period : Shown);

    (int Low, int High) Range()
    {
        int low = int.MaxValue, high = int.MinValue;
        foreach (var n in Current) { low = Math.Min(low, n.Key); high = Math.Max(high, n.Key); }
        if (Guide is { } guide) foreach (var n in guide) { low = Math.Min(low, n.Key); high = Math.Max(high, n.Key); }
        if (low > high) (low, high) = (48, 72);
        low -= 2;
        high += 2;
        // Two octaves at least, so there's room to move notes.
        if (high - low < 24) { var mid = (low + high) / 2; low = mid - 12; high = mid + 12; }
        return (Math.Max(0, low), Math.Min(127, high));
    }

    double RowHeight(int low, int high) => (Bounds.Height - Ruler) / (high - low + 1);
    double X(long tick) => Keys + (Bounds.Width - Keys) * tick / Shown;
    double Y(int key, int low, int high) => Ruler + (high - key) * RowHeight(low, high);

    long TickAt(double x, bool snap)
    {
        var t = (long)Math.Round(Math.Clamp((x - Keys) / Math.Max(1, Bounds.Width - Keys), 0, 1) * Shown);
        return snap ? t / Sixteenth * Sixteenth : t;
    }

    int KeyAt(double y, int low, int high) => Math.Clamp(high - (int)Math.Floor((y - Ruler) / RowHeight(low, high)), low, high);

    /// <summary>The note under the point, in the part's own time (a repeat counts as the part).</summary>
    int HitTest(Point p, out bool rightEnd) => HitTest(p, out rightEnd, out _);

    int HitTest(Point p, out bool rightEnd, out long repeat)
    {
        rightEnd = false;
        repeat = 0;
        var (low, high) = Range();
        var notes = Current;
        var loop = Loop;
        for (long from = 0; from < Shown; from += loop)
            for (var i = notes.Count - 1; i >= 0; i--)
            {
                var n = notes[i];
                double x0 = X(from + n.Start), x1 = Math.Max(x0 + 3, X(from + n.Start + n.Length)), y = Y(n.Key, low, high);
                if (p.X >= x0 && p.X <= x1 && p.Y >= y && p.Y <= y + RowHeight(low, high))
                {
                    rightEnd = x1 - p.X <= Math.Min(6, (x1 - x0) / 3);
                    repeat = from;
                    return i;
                }
            }
        return -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        var point = e.GetCurrentPoint(this).Properties;
        var snap = (e.KeyModifiers & KeyModifiers.Alt) == 0;
        if (p.Y < Ruler)
        {
            var at = TickAt(p.X, false);
            if (SeekCommand?.CanExecute((double)at) == true) SeekCommand.Execute((double)at);
            e.Handled = true;
            return;
        }
        if (p.X < Keys) return;
        var hit = HitTest(p, out var rightEnd, out var repeat);
        if (point.IsRightButtonPressed || (e.ClickCount == 2 && hit >= 0))
        {
            if (hit >= 0) Commit(Current.Where((_, i) => i != hit).ToList());
            e.Handled = true;
            return;
        }
        if (!point.IsLeftButtonPressed) return;
        var (low, high) = Range();
        _working = Current.ToList();
        if (hit < 0)
        {
            // A new note where you click, as long as the last one you made or stretched.
            var tick = TickAt(p.X, snap) % Loop;
            _grabFrom = TickAt(p.X, snap) - tick;
            var note = new NoteEvent(tick, Math.Min(_lastLength, Loop - tick), KeyAt(p.Y, low, high), 100);
            _working.Add(note);
            _index = _working.Count - 1;
            _drag = Drag.Resize;
            _grabbed = note;
            _grabTick = tick;
        }
        else
        {
            _index = hit;
            _grabFrom = repeat;
            _grabbed = _working[hit];
            _drag = rightEnd ? Drag.Resize : Drag.Move;
            _grabTick = TickAt(p.X, false);
            _grabKey = KeyAt(p.Y, low, high);
        }
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        var (low, high) = Range();
        var snap = (e.KeyModifiers & KeyModifiers.Alt) == 0;
        if (_working is null || _index < 0)
        {
            _hover = p.X > Keys && p.Y > Ruler ? (TickAt(p.X, snap), KeyAt(p.Y, low, high)) : null;
            Cursor = HitTest(p, out var end) >= 0 ? new Cursor(end ? StandardCursorType.SizeWestEast : StandardCursorType.SizeAll) : null;
            InvalidateVisual();
            return;
        }
        var loop = Loop;
        if (_drag == Drag.Resize)
        {
            var endTick = TickAt(p.X, false) - _grabFrom;
            if (snap) endTick = (endTick + Sixteenth - 1) / Sixteenth * Sixteenth;
            var length = Math.Max(snap ? Sixteenth : 4, endTick - _grabbed.Start);
            _working[_index] = _grabbed with { Length = Math.Min(length, loop - _grabbed.Start) };
        }
        else
        {
            var delta = TickAt(p.X, false) - _grabTick;
            var start = _grabbed.Start + delta;
            if (snap) start = (long)Math.Round(start / (double)Sixteenth) * Sixteenth;
            start = Math.Clamp(start, 0, Math.Max(0, loop - _grabbed.Length));
            var key = Math.Clamp(_grabbed.Key + KeyAt(p.Y, low, high) - _grabKey, 0, 127);
            _working[_index] = _grabbed with { Start = start, Key = key };
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_working is null) return;
        if (_index >= 0 && _index < _working.Count) _lastLength = _working[_index].Length;
        var changed = _working;
        _working = null;
        _index = -1;
        _drag = Drag.None;
        e.Pointer.Capture(null);
        if (!changed.SequenceEqual(Notes ?? [])) Commit(changed);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
    }

    void Commit(List<NoteEvent> notes)
    {
        var sorted = notes.OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
        if (EditedCommand?.CanExecute(sorted) == true) EditedCommand.Execute(sorted);
    }

    IBrush Res(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    static readonly bool[] Black = [false, true, false, true, false, false, true, false, true, false, true, false];

    public override void Render(DrawingContext dc)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < Keys + 10 || h < Ruler + 10) return;
        var (low, high) = Range();
        var rowH = RowHeight(low, high);
        var surface = Res("SurfaceRaisedBrush", Brushes.Black);
        var stroke = Res("StrokeBrush", Brushes.Gray);
        var text = Res("TextBrush", Brushes.White);
        var muted = Res("MutedBrush", Brushes.Gray);
        var accent = Res("AccentBrush", Brushes.DodgerBlue);
        var clash = Res("MissingBrush", Brushes.IndianRed);
        dc.DrawRectangle(surface, null, new RoundedRect(new Rect(0, 0, w, h), 4));

        // Rows: the black keys a little darker, a line under each C.
        for (var k = low; k <= high; k++)
        {
            var y = Y(k, low, high);
            if (Black[k % 12]) using (dc.PushOpacity(0.35)) dc.FillRectangle(Brushes.Black, new Rect(Keys, y, w - Keys, rowH));
            if (k % 12 == 0)
            {
                using (dc.PushOpacity(0.5)) dc.FillRectangle(stroke, new Rect(Keys, y + rowH - 1, w - Keys, 1));
                if (rowH >= 7)
                {
                    var label = new FormattedText(MidiChannel.KeyName(k), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                                  Typeface.Default, Math.Min(10, rowH + 1), muted);
                    dc.DrawText(label, new Point(3, y + (rowH - label.Height) / 2));
                }
            }
            dc.FillRectangle(Black[k % 12] ? Brushes.Black : Brushes.WhiteSmoke, new Rect(Keys - 10, y + 0.5, 9, Math.Max(1, rowH - 1)));
        }

        // Beats and bars, and the bar numbers along the top.
        var shown = Shown;
        var beatW = (w - Keys) * Ppq / shown;
        for (long t = 0; t <= shown; t += Ppq)
        {
            var x = X(t);
            var bar = t % (4 * Ppq) == 0;
            if (!bar && beatW < 8) continue;
            using (dc.PushOpacity(bar ? 0.7 : 0.25)) dc.FillRectangle(stroke, new Rect(x, Ruler, 1, h - Ruler));
            if (bar && t < shown && (w - Keys) * 4 * Ppq / shown >= 22)
                dc.DrawText(new FormattedText($"{t / (4 * Ppq) + 1}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, muted),
                            new Point(x + 3, 2));
        }
        // Where the part goes round again.
        var loop = Loop;
        for (var t = loop; t < shown; t += loop)
            using (dc.PushOpacity(0.9)) dc.FillRectangle(accent, new Rect(X(t), Ruler, 1.5, h - Ruler));

        // The guide, grey, behind.
        var guide = Guide ?? [];
        using (dc.PushOpacity(0.45))
            foreach (var n in guide)
                Note(dc, muted, null, n.Start, n.Length, n.Key, low, high);

        // The part: its own go full strength, repeats faint; red where it rubs a semitone against the guide.
        var notes = Current;
        for (long from = 0; from < shown; from += loop)
        {
            using var faint = from == 0 ? (DrawingContext.PushedState?)null : dc.PushOpacity(0.35);
            for (var i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                if (from + n.Start >= shown) continue;
                var rubs = Rubs(n, from, guide);
                var picked = _working is not null && i == _index;
                Note(dc, rubs ? clash : accent, picked ? new Pen(text, 1.5) : null, from + n.Start, n.Length, n.Key, low, high);
            }
        }

        // Where a click would put a note.
        if (_working is null && _hover is { } hov)
            using (dc.PushOpacity(0.25)) Note(dc, accent, null, hov.Tick, _lastLength, hov.Key, low, high);

        if (Position >= 0)
        {
            var px = Math.Clamp(X((long)(Position % shown)), Keys, w - 2);
            dc.FillRectangle(text, new Rect(px, 0, 2, h));
        }
    }

    void Note(DrawingContext dc, IBrush fill, IPen? pen, long start, long length, int key, int low, int high)
    {
        if (key < low || key > high) return;
        var x0 = X(start);
        var x1 = Math.Max(x0 + 2, X(start + length) - 1);
        var y = Y(key, low, high);
        dc.DrawRectangle(fill, pen, new RoundedRect(new Rect(x0, y + 0.5, x1 - x0, Math.Max(1.5, RowHeight(low, high) - 1)), 2));
    }

    /// <summary>A semitone (or a major seventh) from a guide note that sounds at the same time.</summary>
    static bool Rubs(NoteEvent n, long from, IReadOnlyList<NoteEvent> guide)
    {
        long a = from + n.Start, b = a + n.Length;
        foreach (var g in guide)
        {
            if (g.Start >= b || g.Start + g.Length <= a) continue;
            var ic = ((n.Key - g.Key) % 12 + 12) % 12;
            if (ic is 1 or 11) return true;
        }
        return false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Delete or Key.Back && _hover is { } hov)
        {
            var notes = Current;
            var i = notes.ToList().FindIndex(n => n.Key == hov.Key && n.Start <= hov.Tick % Loop && n.Start + n.Length > hov.Tick % Loop);
            if (i >= 0)
            {
                Commit(notes.Where((_, j) => j != i).ToList());
                e.Handled = true;
            }
        }
    }
}
