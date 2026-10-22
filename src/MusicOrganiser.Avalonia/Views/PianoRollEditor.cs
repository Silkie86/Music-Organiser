using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// A piano roll to try a part against another (Solomon, 2026-10-17: "be able to edit it with the original midi greyed
/// out in the background as a guide so i can see what fits and doesn't"). The <see cref="Guide"/> is drawn grey behind;
/// the <see cref="Notes"/> on top can be edited: click an empty spot to add a note, drag a note to move it, drag its
/// right end to make it longer or shorter, right-click or double-click it to take it away. Notes a semitone from a guide
/// note sounding at the same time are drawn in red. Notes snap like Bitwig (Solomon, 2026-10-19): onto sixteenths
/// (<see cref="SnapGrid"/>), in whole sixteenths keeping a push or a drag off the grid (<see cref="SnapRelative"/>), and to
/// other notes' starts and ends (<see cref="SnapObjects"/>), the nearest winning; hold Shift to place freely. New notes take
/// the length and velocity of the last note clicked, like FL Studio. <see cref="NoteLabels"/> writes names on the notes.
/// The part repeats faintly when the guide is longer, and editing a repeat edits the part.
/// Drag along the bar numbers to set loop points (Solomon, 2026-10-17: "there should be a way to set the loop points"):
/// they snap to bars (Shift: beats), their ends can be dragged, and a right-click there clears them. A click plays from there.
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
    /// <summary>
    /// A part being tried with the idea, drawn outlined over the grey guide (Solomon, 2026-10-21: "show you the proposed
    /// idea in the piano roll overlaid so you can see how the notes go together"); null when nothing is tried.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<NoteEvent>?> OverlayProperty =
        AvaloniaProperty.Register<PianoRollEditor, IReadOnlyList<NoteEvent>?>(nameof(Overlay));
    public IReadOnlyList<NoteEvent>? Overlay { get => GetValue(OverlayProperty); set => SetValue(OverlayProperty, value); }
    /// <summary>How long the part is before it repeats (ticks).</summary>
    public static readonly StyledProperty<long> PeriodProperty = AvaloniaProperty.Register<PianoRollEditor, long>(nameof(Period));
    /// <summary>How long a stretch is shown (ticks): both parts until they meet again.</summary>
    public static readonly StyledProperty<long> LengthProperty = AvaloniaProperty.Register<PianoRollEditor, long>(nameof(Length));
    public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<PianoRollEditor, double>(nameof(Position), -1);
    /// <summary>Runs with the part's new notes after each edit.</summary>
    public static readonly StyledProperty<ICommand?> EditedCommandProperty = AvaloniaProperty.Register<PianoRollEditor, ICommand?>(nameof(EditedCommand));
    /// <summary>Runs with the ticks clicked in the strip along the top: play from there.</summary>
    public static readonly StyledProperty<ICommand?> SeekCommandProperty = AvaloniaProperty.Register<PianoRollEditor, ICommand?>(nameof(SeekCommand));
    /// <summary>Where the part plays (ticks): from <see cref="PartStart"/> going round to <see cref="PartEnd"/> (−1: the end).</summary>
    public static readonly StyledProperty<long> PartStartProperty = AvaloniaProperty.Register<PianoRollEditor, long>(nameof(PartStart));
    public static readonly StyledProperty<long> PartEndProperty = AvaloniaProperty.Register<PianoRollEditor, long>(nameof(PartEnd), -1);
    /// <summary>The loop points, or null.</summary>
    public static readonly StyledProperty<RollRange?> SectionProperty = AvaloniaProperty.Register<PianoRollEditor, RollRange?>(nameof(Section));
    /// <summary>Runs with new loop points dragged along the bar numbers, or null when they're cleared.</summary>
    public static readonly StyledProperty<ICommand?> SectionCommandProperty = AvaloniaProperty.Register<PianoRollEditor, ICommand?>(nameof(SectionCommand));

    /// <summary>
    /// When set, says which notes (placed at their time on the roll) fight the harmony, in place of the plain semitone
    /// rule: the MIDI playground judges them against its chord lane, so a major 7th over the root or a b9 on a dominant
    /// isn't marked as a mistake.
    /// </summary>
    public static readonly StyledProperty<Func<NoteEvent, bool>?> ClashTestProperty =
        AvaloniaProperty.Register<PianoRollEditor, Func<NoteEvent, bool>?>(nameof(ClashTest));

    public static readonly StyledProperty<bool> SnapGridProperty = AvaloniaProperty.Register<PianoRollEditor, bool>(nameof(SnapGrid), true);
    public static readonly StyledProperty<bool> SnapRelativeProperty = AvaloniaProperty.Register<PianoRollEditor, bool>(nameof(SnapRelative), true);
    public static readonly StyledProperty<bool> SnapObjectsProperty = AvaloniaProperty.Register<PianoRollEditor, bool>(nameof(SnapObjects), true);
    public bool SnapGrid { get => GetValue(SnapGridProperty); set => SetValue(SnapGridProperty, value); }
    public bool SnapRelative { get => GetValue(SnapRelativeProperty); set => SetValue(SnapRelativeProperty, value); }
    public bool SnapObjects { get => GetValue(SnapObjectsProperty); set => SetValue(SnapObjectsProperty, value); }

    /// <summary>What's written on the notes and the keys: nothing, note names (C4, F#3), or what each note is over the chord (<see cref="DegreeOf"/>).</summary>
    public static readonly StyledProperty<NoteLabelKind> NoteLabelsProperty = AvaloniaProperty.Register<PianoRollEditor, NoteLabelKind>(nameof(NoteLabels));
    /// <summary>What a note (placed at its time on the roll) is over the chord there (3, ♭7, 9), or null with no chord.</summary>
    public static readonly StyledProperty<Func<NoteEvent, string?>?> DegreeOfProperty =
        AvaloniaProperty.Register<PianoRollEditor, Func<NoteEvent, string?>?>(nameof(DegreeOf));
    public NoteLabelKind NoteLabels { get => GetValue(NoteLabelsProperty); set => SetValue(NoteLabelsProperty, value); }
    public Func<NoteEvent, string?>? DegreeOf { get => GetValue(DegreeOfProperty); set => SetValue(DegreeOfProperty, value); }

    /// <summary>
    /// When set, how strongly a note (placed at its time on the roll) fits the chord under it: the notes are coloured by
    /// it, brightest for the strongest and red where it fights, and so is the note a click would put down.
    /// </summary>
    public static readonly StyledProperty<Func<NoteEvent, FitStrength?>?> StrengthOfProperty =
        AvaloniaProperty.Register<PianoRollEditor, Func<NoteEvent, FitStrength?>?>(nameof(StrengthOf));
    /// <summary>The chords under the roll, each with how strongly each note fits it: the rows of the strongest notes are lit faintly behind.</summary>
    public static readonly StyledProperty<IReadOnlyList<FitSpan>?> FitGuideProperty =
        AvaloniaProperty.Register<PianoRollEditor, IReadOnlyList<FitSpan>?>(nameof(FitGuide));
    /// <summary>The colour a note clicked in would take, for the preview under the pointer (<see cref="StrengthOf"/> when not set).</summary>
    public static readonly StyledProperty<Func<NoteEvent, FitStrength?>?> WouldBeProperty =
        AvaloniaProperty.Register<PianoRollEditor, Func<NoteEvent, FitStrength?>?>(nameof(WouldBe));
    public Func<NoteEvent, FitStrength?>? StrengthOf { get => GetValue(StrengthOfProperty); set => SetValue(StrengthOfProperty, value); }
    public Func<NoteEvent, FitStrength?>? WouldBe { get => GetValue(WouldBeProperty); set => SetValue(WouldBeProperty, value); }
    public IReadOnlyList<FitSpan>? FitGuide { get => GetValue(FitGuideProperty); set => SetValue(FitGuideProperty, value); }

    static PianoRollEditor() =>
        AffectsRender<PianoRollEditor>(NotesProperty, GuideProperty, PeriodProperty, LengthProperty, PositionProperty,
                                       PartStartProperty, PartEndProperty, SectionProperty, ClashTestProperty, NoteLabelsProperty, DegreeOfProperty,
                                       StrengthOfProperty, WouldBeProperty, FitGuideProperty, OverlayProperty);

    public Func<NoteEvent, bool>? ClashTest { get => GetValue(ClashTestProperty); set => SetValue(ClashTestProperty, value); }

    public IReadOnlyList<NoteEvent>? Notes { get => GetValue(NotesProperty); set => SetValue(NotesProperty, value); }
    public IReadOnlyList<NoteEvent>? Guide { get => GetValue(GuideProperty); set => SetValue(GuideProperty, value); }
    public long Period { get => GetValue(PeriodProperty); set => SetValue(PeriodProperty, value); }
    public long Length { get => GetValue(LengthProperty); set => SetValue(LengthProperty, value); }
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public ICommand? EditedCommand { get => GetValue(EditedCommandProperty); set => SetValue(EditedCommandProperty, value); }
    public ICommand? SeekCommand { get => GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }
    public long PartStart { get => GetValue(PartStartProperty); set => SetValue(PartStartProperty, value); }
    public long PartEnd { get => GetValue(PartEndProperty); set => SetValue(PartEndProperty, value); }
    public RollRange? Section { get => GetValue(SectionProperty); set => SetValue(SectionProperty, value); }
    public ICommand? SectionCommand { get => GetValue(SectionCommandProperty); set => SetValue(SectionCommandProperty, value); }

    public PianoRollEditor()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>The strip along the top that shows bars, plays from where it's clicked and sets loop points when dragged.</summary>
    const double Ruler = 20;
    const long Beat = Ppq, Bar = 4 * Ppq;

    enum Edge { None, New, Start, End }
    Edge _edge;              // what a press on the bar numbers is doing
    double _pressX;
    RollRange? _draft;       // loop points while they're being dragged

    enum Drag { None, Move, Resize }

    List<NoteEvent>? _working;   // the notes while a drag is under way
    int _index = -1;             // the note being dragged or picked
    Drag _drag;
    long _grabTick, _grabFrom;
    int _grabKey;
    NoteEvent _grabbed;
    // The last note clicked or stretched: new notes copy its length and velocity (one sixteenth before any).
    long _lastLength = Sixteenth;
    int _lastVelocity = 100;
    double? _newAt;   // where a note being put in was clicked, until it's dragged longer
    (long Tick, int Key)? _hover;

    IReadOnlyList<NoteEvent> Current => _working ?? Notes ?? [];
    long Shown => Math.Max(Math.Max(1, Length), Math.Max(1, Period));
    long Loop => Math.Max(Sixteenth, Period > 0 ? Period : Shown);
    long From => Math.Clamp(PartStart, 0, Shown);
    long To => PartEnd > From ? Math.Min(PartEnd, Shown) : Shown;

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
        low = Math.Max(0, low);
        high = Math.Min(127, high);
        // Zoomed in on the notes' height: fewer rows, from the top one scrolled to.
        if (_rows > 0 && _rows < high - low + 1)
        {
            var top = Math.Clamp(_top, low + _rows - 1, high);
            return (top - _rows + 1, top);
        }
        return (low, high);
    }

    // ---- zoom and pan (Solomon, 2026-10-19): Ctrl + scroll zooms in time, Alt (or Ctrl + Shift) + scroll the notes' height, plain
    // scroll moves up and down the notes (when zoomed), Shift + scroll along the bars, the middle button drags it about,
    // and a double-click on the bar numbers shows it all again.
    long _viewFrom, _viewSpan;   // ticks shown (0: all)
    int _top, _rows;             // keys shown (0 rows: all)
    Point? _panFrom;
    (long From, int Top) _panStart;

    long ViewSpan => _viewSpan <= 0 || _viewSpan >= Shown ? Shown : _viewSpan;
    long ViewFrom => Math.Clamp(_viewFrom, 0, Shown - ViewSpan);

    void ShowAll()
    {
        _viewFrom = _viewSpan = 0;
        _rows = 0;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        var dy = e.Delta.Y;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);   // Cmd on a Mac
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);   // as in FL Studio and Ableton (Solomon, 2026-10-21: zoom up and down with the wheel)
        var (low, high) = Range();
        if ((alt || ctrl && shift) && dy != 0)
        {
            // Taller or shorter notes, round the key under the pointer.
            var key = KeyAt(p.Y, low, high);
            var rows = Math.Clamp((int)Math.Round((high - low + 1) * Math.Pow(0.8, dy)), 8, 128);
            var share = (high - key) / (double)Math.Max(1, high - low);
            _rows = rows;
            _top = key + (int)Math.Round(share * (rows - 1));
        }
        else if (ctrl && dy != 0)
        {
            var at = TickAt(p.X, false);
            var span = Math.Clamp((long)Math.Round(ViewSpan * Math.Pow(0.8, dy)), Beat, Shown);
            var share = (at - ViewFrom) / (double)ViewSpan;
            _viewSpan = span >= Shown ? 0 : span;
            _viewFrom = at - (long)Math.Round(share * span);
        }
        else if (shift && dy != 0 || Math.Abs(e.Delta.X) > Math.Abs(dy))   // a trackpad's sideways part only counts when it's mostly sideways
            _viewFrom = ViewFrom + (long)Math.Round((Math.Abs(e.Delta.X) > Math.Abs(dy) ? -e.Delta.X : -dy) * ViewSpan / 8);
        else if (dy != 0 && _rows > 0)
            _top = high + (int)Math.Round(dy * 3);
        else return;   // all of it shows: the page scrolls instead
        e.Handled = true;
        InvalidateVisual();
    }

    double RowHeight(int low, int high) => (Bounds.Height - Ruler) / (high - low + 1);
    double X(long tick) => Keys + (Bounds.Width - Keys) * (tick - ViewFrom) / ViewSpan;
    double Y(int key, int low, int high) => Ruler + (high - key) * RowHeight(low, high);

    long TickAt(double x, bool snap)
    {
        var t = ViewFrom + (long)Math.Round(Math.Clamp((x - Keys) / Math.Max(1, Bounds.Width - Keys), 0, 1) * ViewSpan);
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
        for (long from = From; from < To; from += loop)
            for (var i = notes.Count - 1; i >= 0; i--)
            {
                var n = notes[i];
                if (from + n.Start >= To) continue;
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
        var snap = (e.KeyModifiers & KeyModifiers.Shift) == 0;
        if (point.IsMiddleButtonPressed)
        {
            _panFrom = p;
            _panStart = (ViewFrom, Range().High);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (p.Y < Ruler && e.ClickCount == 2 && point.IsLeftButtonPressed)
        {
            ShowAll();
            e.Handled = true;
            return;
        }
        if (p.Y < Ruler)
        {
            if (point.IsRightButtonPressed)
            {
                // Clear the loop points.
                if (Section is not null && SectionCommand?.CanExecute(null) == true) SectionCommand.Execute(null);
            }
            else if (point.IsLeftButtonPressed)
            {
                _pressX = p.X;
                _edge = Section is { } s && Math.Abs(p.X - X(s.Start)) <= 6 ? Edge.Start
                      : Section is { } t && Math.Abs(p.X - X(t.End)) <= 6 ? Edge.End
                      : Edge.New;
                e.Pointer.Capture(this);
            }
            e.Handled = true;
            return;
        }
        if (p.X < Keys) return;
        var hit = HitTest(p, out var rightEnd, out var repeat);
        if (point.IsRightButtonPressed || (e.ClickCount == 2 && hit >= 0))
        {
            if (hit >= 0) Commit(Current.Where((_, i) => i != hit).ToList());
            // Off the notes, a right-click lets go of the picked bars, as it does on the bar numbers and the timeline.
            else if (point.IsRightButtonPressed && Section is not null && SectionCommand?.CanExecute(null) == true) SectionCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (!point.IsLeftButtonPressed) return;
        var (low, high) = Range();
        _working = Current.ToList();
        if (hit < 0)
        {
            // A new note where you click, as long as the last one you made or stretched (only where the part plays).
            var at = TickAt(p.X, snap);
            if (at < From || at >= To)
            {
                _working = null;
                return;
            }
            var tick = (at - From) % Loop;
            _grabFrom = at - tick;
            var note = new NoteEvent(tick, Math.Min(_lastLength, Loop - tick), KeyAt(p.Y, low, high), _lastVelocity);
            _working.Add(note);
            _index = _working.Count - 1;
            _drag = Drag.Resize;
            _newAt = p.X;
            _grabbed = note;
            _grabTick = tick;
        }
        else
        {
            _index = hit;
            _grabFrom = repeat;
            _grabbed = _working[hit];
            _lastLength = _grabbed.Length;
            _lastVelocity = _grabbed.Velocity;
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
        if (_panFrom is { } pan)
        {
            _viewFrom = _panStart.From - (long)Math.Round((p.X - pan.X) / Math.Max(1, Bounds.Width - Keys) * ViewSpan);
            if (_rows > 0) _top = _panStart.Top + (int)Math.Round((p.Y - pan.Y) / RowHeight(low, high));
            InvalidateVisual();
            return;
        }
        var snap = (e.KeyModifiers & KeyModifiers.Shift) == 0;
        if (_edge != Edge.None)
        {
            if (_edge == Edge.New && Math.Abs(p.X - _pressX) < 4 && _draft is null) return;
            var unit = snap ? Bar : Beat;
            long Snap(double x) => (long)Math.Round(TickAt(x, false) / (double)unit) * unit;
            long a, b;
            if (_edge == Edge.New) (a, b) = (Snap(Math.Min(_pressX, p.X)), Snap(Math.Max(_pressX, p.X)));
            else if (_edge == Edge.Start) (a, b) = (Snap(p.X), Section!.Value.End);
            else (a, b) = (Section!.Value.Start, Snap(p.X));
            if (a > b) (a, b) = (b, a);
            a = Math.Clamp(a, 0, Shown);
            b = Math.Clamp(b, 0, Shown);
            if (b - a < unit) b = Math.Min(Shown, a + unit);
            if (b - a < unit) a = Math.Max(0, b - unit);
            _draft = new RollRange(a, b);
            InvalidateVisual();
            return;
        }
        if (_working is null || _index < 0)
        {
            var onRuler = p.Y <= Ruler && p.X > Keys;
            _hover = p.X > Keys && p.Y > Ruler ? (TickAt(p.X, snap), KeyAt(p.Y, low, high)) : null;
            Cursor = onRuler ? new Cursor(Section is { } s && (Math.Abs(p.X - X(s.Start)) <= 6 || Math.Abs(p.X - X(s.End)) <= 6)
                                              ? StandardCursorType.SizeWestEast : StandardCursorType.Hand)
                   : HitTest(p, out var end) >= 0 ? new Cursor(end ? StandardCursorType.SizeWestEast : StandardCursorType.SizeAll) : null;
            InvalidateVisual();
            return;
        }
        var loop = Loop;
        if (_drag == Drag.Resize)
        {
            // A hand that shakes a little as it clicks doesn't shrink the note it's putting in to a sixteenth.
            if (_newAt is { } x0 && Math.Abs(p.X - x0) < 4) return;
            _newAt = null;
            var raw = TickAt(p.X, false) - _grabFrom;
            var endTick = snap ? SnapTick(raw, _grabbed.Start + _grabbed.Length, [0]) : raw;
            var length = Math.Max(snap ? Sixteenth : 4, endTick - _grabbed.Start);
            _working[_index] = _grabbed with { Length = Math.Min(length, loop - _grabbed.Start) };
        }
        else
        {
            var delta = TickAt(p.X, false) - _grabTick;
            var start = _grabbed.Start + delta;
            // Its start, or its end, can catch another note's.
            if (snap) start = SnapTick(start, _grabbed.Start, [0, _grabbed.Length]);
            start = Math.Clamp(start, 0, Math.Max(0, loop - _grabbed.Length));
            var key = Math.Clamp(_grabbed.Key + KeyAt(p.Y, low, high) - _grabKey, 0, 127);
            _working[_index] = _grabbed with { Start = start, Key = key };
        }
        InvalidateVisual();
    }

    /// <summary>
    /// Where a note edge dragged from <paramref name="was"/> to <paramref name="raw"/> (the part's own ticks) lands: on a
    /// sixteenth, the same distance off the grid as it was, or on another note's start or end close by, whichever is
    /// nearest. <paramref name="along"/>: how far each edge that can catch is from the snapped one.
    /// </summary>
    long SnapTick(long raw, long was, long[] along)
    {
        long best = raw, bestBy = long.MaxValue;
        void Try(long at)
        {
            var by = Math.Abs(at - raw);
            if (by < bestBy) { best = at; bestBy = by; }
        }
        if (SnapGrid) Try((long)Math.Round(raw / (double)Sixteenth) * Sixteenth);
        if (SnapRelative) Try(was + (long)Math.Round((raw - was) / (double)Sixteenth) * Sixteenth);
        if (SnapObjects && _working is { } notes)
        {
            var near = Math.Max(1, (long)(8 * ViewSpan / Math.Max(1, Bounds.Width - Keys)));   // 8 px at this zoom
            for (var i = 0; i < notes.Count; i++)
            {
                if (i == _index) continue;
                foreach (var edge in new[] { notes[i].Start, notes[i].Start + notes[i].Length })
                    foreach (var a in along)
                        if (Math.Abs(edge - a - raw) <= near) Try(edge - a);
            }
        }
        return best;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _panFrom = null;   // an interrupted pan (Alt+Tab, a dialog) mustn't carry on when the pointer next moves
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_panFrom is not null)
        {
            _panFrom = null;
            e.Pointer.Capture(null);
            return;
        }
        if (_edge != Edge.None)
        {
            e.Pointer.Capture(null);
            var draft = _draft;
            _edge = Edge.None;
            _draft = null;
            if (draft is { } range)
            {
                if (SectionCommand?.CanExecute(range) == true) SectionCommand.Execute(range);
            }
            else
            {
                // A click: play from there.
                var at = (double)TickAt(_pressX, false);
                if (SeekCommand?.CanExecute(at) == true) SeekCommand.Execute(at);
            }
            InvalidateVisual();
            return;
        }
        if (_working is null) return;
        if (_index >= 0 && _index < _working.Count) _lastLength = _working[_index].Length;
        var changed = _working;
        _working = null;
        _newAt = null;
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

    /// <summary>
    /// The colours notes take by how strongly they fit, weakest first (by <see cref="FitStrength"/>): a colour each, far
    /// enough apart to tell at a glance (2026-10-21: shades of the theme's accent were too alike). Green for the strongest,
    /// blue for strong, violet for the tensions that colour a chord, grey for weak and the theme's red where a note fights
    /// the chord. The same in every theme, like traffic lights. <see cref="FitLegend"/> uses them too.
    /// </summary>
    internal static IBrush[] FitBrushes(Control c)
    {
        var fights = c.TryFindResource("MissingBrush", c.ActualThemeVariant, out var v) && v is ISolidColorBrush b ? b.Color : Color.FromRgb(0xF0, 0x61, 0x6D);
        // Made again only when the theme's red changes, not on every repaint.
        if (_fit is not { } fit || fit.Fights != fights) _fit = fit = (fights, [new SolidColorBrush(fights), Weak, ColourBrush, Strong, Strongest]);
        return fit.Brushes;
    }

    static (Color Fights, IBrush[] Brushes)? _fit;

    static readonly IBrush Strongest = new SolidColorBrush(Color.FromRgb(0x4A, 0xD0, 0x8A)),
                           Strong = new SolidColorBrush(Color.FromRgb(0x5C, 0xA4, 0xF5)),
                           ColourBrush = new SolidColorBrush(Color.FromRgb(0xB5, 0x8C, 0xF5)),
                           Weak = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C));

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

        // The keys whose notes are sounding under the playhead light up (Solomon, 2026-10-21).
        var lit = Sounding();

        // Rows: the black keys a little darker, a line under each C.
        for (var k = low; k <= high; k++)
        {
            var y = Y(k, low, high);
            if (Black[k % 12]) using (dc.PushOpacity(0.35)) dc.FillRectangle(Brushes.Black, new Rect(Keys, y, w - Keys, rowH));
            if (k % 12 == 0) using (dc.PushOpacity(0.5)) dc.FillRectangle(stroke, new Rect(Keys, y + rowH - 1, w - Keys, 1));
        }
        DrawKeyboard(dc, low, high, rowH, lit, accent);

        // Everything from here on stays right of the keyboard, however far it's zoomed and moved along.
        using var noteArea = dc.PushClip(new Rect(Keys, 0, w - Keys, h));

        // Under the chord the pointer is over, the rows of its strongest notes lit faintly (Solomon, 2026-10-20: "a system to
        // visually tell which notes are the strongest fit"), so it shows where a note would go best. Only there, so the roll
        // stays clean (2026-10-21: lit under every chord, the stripes looked "quite messy").
        var fit = StrengthOf is null ? null : FitBrushes(this);
        if (fit is not null && FitGuide is { } spans && _working is null && _hover is { } over
            && spans.FirstOrDefault(s => over.Tick >= s.Start && over.Tick < s.Start + s.Length) is { } span)
        {
            double a = Math.Max(Keys, X(span.Start)), b = Math.Min(w, X(span.Start + span.Length));
            for (var k = low; k <= high; k++)
                if (span.Strengths[k % 12] == FitStrength.Strongest)
                    using (dc.PushOpacity(0.14)) dc.FillRectangle(fit[(int)FitStrength.Strongest], new Rect(a, Y(k, low, high), b - a, rowH));
        }

        // Beats and bars, and the bar numbers along the top.
        var shown = Shown;
        var beatW = (w - Keys) * Ppq / ViewSpan;
        // Zoomed in, sixteenths too, fainter (Solomon's design, point 15: the grid gets finer as you zoom).
        if (beatW / 4 >= 12)
            using (dc.PushOpacity(0.15))
                for (var t = ViewFrom - ViewFrom % Sixteenth; t <= Math.Min(shown, ViewFrom + ViewSpan); t += Sixteenth)
                    if (t % Ppq != 0) dc.FillRectangle(stroke, new Rect(X(t), Ruler, 1, h - Ruler));
        for (long t = 0; t <= shown; t += Ppq)
        {
            var x = X(t);
            var bar = t % (4 * Ppq) == 0;
            if (!bar && beatW < 8) continue;
            using (dc.PushOpacity(bar ? 0.7 : 0.25)) dc.FillRectangle(stroke, new Rect(x, Ruler, 1, h - Ruler));
            if (bar && t < shown && (w - Keys) * 4 * Ppq / ViewSpan >= 22)
                dc.DrawText(new FormattedText($"{t / (4 * Ppq) + 1}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, muted),
                            new Point(x + 3, 2));
        }
        // Where the part goes round again.
        var loop = Loop;
        for (var t = From + loop; t < To; t += loop)
            using (dc.PushOpacity(0.9)) dc.FillRectangle(accent, new Rect(X(t), Ruler, 1.5, h - Ruler));

        // The loop points: lit along the bar numbers, the rest of the roll dimmed.
        if ((_draft ?? Section) is { } section)
        {
            double a = X(section.Start), b = X(section.End);
            using (dc.PushOpacity(0.5)) dc.FillRectangle(Brushes.Black, new Rect(Keys, Ruler, Math.Max(0, a - Keys), h - Ruler));
            using (dc.PushOpacity(0.5)) dc.FillRectangle(Brushes.Black, new Rect(b, Ruler, Math.Max(0, w - b), h - Ruler));
            using (dc.PushOpacity(0.45)) dc.FillRectangle(accent, new Rect(a, 1, Math.Max(2, b - a), Ruler - 2));
            dc.FillRectangle(accent, new Rect(a, 0, 2, h));
            dc.FillRectangle(accent, new Rect(b - 2, 0, 2, h));
        }
        else if (From > 0 || To < shown)
        {
            using (dc.PushOpacity(0.35)) dc.FillRectangle(Brushes.Black, new Rect(Keys, Ruler, Math.Max(0, X(From) - Keys), h - Ruler));
            using (dc.PushOpacity(0.35)) dc.FillRectangle(Brushes.Black, new Rect(X(To), Ruler, Math.Max(0, w - X(To)), h - Ruler));
        }

        // The guide, grey, behind.
        var guide = Guide ?? [];
        using (dc.PushOpacity(0.45))
            foreach (var n in guide)
                Note(dc, muted, null, n.Start, n.Length, n.Key, low, high);

        // The part being tried with the idea: outlined in the text colour, over the guide and under the part.
        if (Overlay is { Count: > 0 } tried)
        {
            var outline = new Pen(text, 1.2);
            using (dc.PushOpacity(0.3))
                foreach (var n in tried) Note(dc, text, null, n.Start, n.Length, n.Key, low, high);
            foreach (var n in tried) Note(dc, Brushes.Transparent, outline, n.Start, n.Length, n.Key, low, high);
        }

        // The part: its own go full strength, repeats faint; red where it rubs a half step above the guide.
        var notes = Current;
        for (long from = From; from < To; from += loop)
        {
            using var faint = from == From ? (DrawingContext.PushedState?)null : dc.PushOpacity(0.35);
            for (var i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                if (from + n.Start >= To) continue;
                var strength = fit is null ? null : StrengthOf!(n with { Start = from + n.Start });
                var rubs = strength is null && (ClashTest is { } test ? test(n with { Start = from + n.Start }) : Rubs(n, from, guide));
                var picked = _working is not null && i == _index;
                var fill = strength is { } s ? fit![(int)s] : rubs ? clash : accent;
                // Names stay readable: dark on the bright notes, the page's text colour on the faded ones.
                var ink = strength is FitStrength.Colour or FitStrength.Weak ? text : Brushes.Black;
                // Shaded by velocity (Solomon, 2026-10-22): loud notes bright, quiet ones faded, each with a light attack edge.
                using (dc.PushOpacity(0.4 + 0.6 * Math.Clamp(n.Velocity / 110.0, 0, 1)))
                    Note(dc, fill, picked ? new Pen(text, 1.5) : null, from + n.Start, Math.Min(n.Length, To - from - n.Start), n.Key, low, high,
                         LabelOf(n, from + n.Start), ink, attack: true);
            }
        }

        // Where a click would put a note.
        if (_working is null && _hover is { } hov)
        {
            // Coloured as the note a click puts there will be (its length cut at the end of the go round, judged with it in),
            // and shaded as it will be, a little fainter.
            var length = Math.Min(_lastLength, Loop - ((hov.Tick - From) % Loop + Loop) % Loop);
            var ghost = new NoteEvent(hov.Tick, length, hov.Key, _lastVelocity);
            var would = fit is null ? null : (WouldBe ?? StrengthOf)!(ghost);
            using (dc.PushOpacity(would is null ? 0.25 : 0.7 * (0.4 + 0.6 * Math.Clamp(_lastVelocity / 110.0, 0, 1))))
                Note(dc, would is { } s ? fit![(int)s] : accent, null, hov.Tick, length, hov.Key, low, high);
        }

        if (Position >= 0)
        {
            var px = Math.Clamp(X((long)(Position % shown)), Keys, w - 2);
            dc.FillRectangle(text, new Rect(px, 0, 2, h));
        }
    }

    // The keyboard: grey keys like a real one (Solomon's picture, 2026-10-21), white keys meeting halfway along the black
    // ones, shorter black keys with rounded ends; the keys whose notes are playing lit in the accent.
    static readonly IBrush WhiteKey = new SolidColorBrush(Color.Parse("#FFBDBDBD")), WhiteKeyLine = new SolidColorBrush(Color.Parse("#FF6E6E6E")),
                           BlackKey = new SolidColorBrush(Color.Parse("#FF161616")), KeyText = new SolidColorBrush(Color.Parse("#FF3A3A3A"));

    void DrawKeyboard(DrawingContext dc, int low, int high, double rowH, HashSet<int> lit, IBrush accent)
    {
        var area = new Rect(0, Ruler, Keys, Math.Max(0, Bounds.Height - Ruler));
        using var clip = dc.PushClip(area);
        dc.FillRectangle(WhiteKey, area);
        for (var k = low - 1; k <= high + 1; k++)
        {
            if (Black[(k % 12 + 12) % 12]) continue;
            // a white key reaches halfway into the black keys either side of it
            var top = Y(k, low, high) - (Black[((k + 1) % 12 + 12) % 12] ? rowH / 2 : 0);
            var bottom = Y(k, low, high) + rowH + (Black[((k - 1) % 12 + 12) % 12] ? rowH / 2 : 0);
            if (lit.Contains(k)) dc.FillRectangle(accent, new Rect(0, top, Keys, bottom - top));
            dc.FillRectangle(WhiteKeyLine, new Rect(0, bottom - 0.5, Keys, 1));
            // the Cs named, and every key when names are asked for and there's room
            if (rowH >= 7 && (k % 12 == 0 || NoteLabels != NoteLabelKind.Off && rowH >= 8) && k >= 0)
            {
                var label = new FormattedText(MidiChannel.KeyName(k), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                              Typeface.Default, Math.Min(9.5, rowH + 1), KeyText);
                dc.DrawText(label, new Point(Keys - label.Width - 3, Y(k, low, high) + (rowH - label.Height) / 2));
            }
        }
        for (var k = low; k <= high; k++)
        {
            if (!Black[k % 12]) continue;
            var key = new Rect(0, Y(k, low, high) + rowH * 0.08, Keys * 0.6, rowH * 0.84);
            dc.DrawRectangle(lit.Contains(k) ? accent : BlackKey, null, new RoundedRect(key, 0, Math.Min(2.5, rowH / 3), Math.Min(2.5, rowH / 3), 0));
        }
        dc.FillRectangle(Brushes.Black, new Rect(Keys - 1, Ruler, 1, area.Height));
    }

    /// <summary>The keys of the part's notes (and their repeats) playing where the playhead is.</summary>
    HashSet<int> Sounding()
    {
        var keys = new HashSet<int>();
        if (Position < 0 || Shown <= 0 || Loop <= 0) return keys;
        var at = (long)(Position % Shown);
        if (at < From || at >= To) return keys;
        var from = From + (at - From) / Loop * Loop;   // the go round the playhead is in
        foreach (var n in Current)
            if (at >= from + n.Start && at < from + n.Start + n.Length) keys.Add(n.Key);
        return keys;
    }

    static readonly IBrush Attack = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));

    void Note(DrawingContext dc, IBrush fill, IPen? pen, long start, long length, int key, int low, int high, string? label = null, IBrush? ink = null,
              bool attack = false)
    {
        if (key < low || key > high) return;
        var x0 = X(start);
        var x1 = Math.Max(x0 + 2, X(start + length) - 1);
        var y = Y(key, low, high);
        var rowH = RowHeight(low, high);
        dc.DrawRectangle(fill, pen, new RoundedRect(new Rect(x0, y + 0.5, x1 - x0, Math.Max(1.5, rowH - 1)), 1.5));
        if (attack && x1 - x0 > 5) dc.FillRectangle(Attack, new Rect(x0, y + 0.5, 1.5, Math.Max(1.5, rowH - 1)));
        // Its name (or what it is over the chord), when it fits.
        if (label is null || rowH < 8) return;
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, Math.Min(10, rowH), Brushes.Black);
        if (text.Width + 4 <= x1 - x0) using (dc.PushOpacity(0.8)) dc.DrawText(text, new Point(x0 + 2, y + (rowH - text.Height) / 2));
    }

    string? LabelOf(NoteEvent n, long at) => NoteLabels switch
    {
        NoteLabelKind.Names => MidiChannel.KeyName(n.Key),
        NoteLabelKind.Degrees => DegreeOf?.Invoke(n with { Start = at }) ?? MidiChannel.KeyName(n.Key),
        _ => null,
    };

    IReadOnlyList<NoteEvent>? _laneOf;
    List<TimedChord> _lane = [];

    /// <summary>
    /// A half step above a guide note that sounds at the same time, and not a note of the chord the guide plays then
    /// (the chords are worked out once per guide).
    /// </summary>
    bool Rubs(NoteEvent n, long from, IReadOnlyList<NoteEvent> guide)
    {
        if (!ReferenceEquals(guide, _laneOf))
        {
            _laneOf = guide;
            _lane = guide.Count == 0 ? [] : ChordLane.Detect(guide, guide.Max(g => g.Start + g.Length));
        }
        return HarmonyMatcher.Rubs(from + n.Start, n.Length, n.Key, guide, _lane);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Delete or Key.Back && _hover is { } hov)
        {
            var notes = Current;
            var t = (hov.Tick - From) % Loop;
            var i = hov.Tick < From || hov.Tick >= To ? -1 : notes.ToList().FindIndex(n => n.Key == hov.Key && n.Start <= t && n.Start + n.Length > t);
            if (i >= 0)
            {
                Commit(notes.Where((_, j) => j != i).ToList());
                e.Handled = true;
            }
        }
    }
}
