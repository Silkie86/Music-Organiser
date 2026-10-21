using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// The playground's timeline (Solomon, 2026-10-19: "it should work like a mini DAW"): labels along the top, bar numbers
/// under them, then a row for each track with its clips drawn where they play, their notes inside and a faint line
/// where each go round starts. Click a clip to pick it and drag it to slide it along or onto another track (below
/// the last track makes a new one); drag its right edge to play it round more times, its left edge to trim its start.
/// Everything snaps to <see cref="Grid"/>, or to nothing while Shift is held (Solomon, 04:56). Drag across empty bars
/// (or the bar numbers, over clips too) to highlight them. Suggestions from the side panel and .mid files from Explorer
/// can be dropped onto a track. The track names and buttons are drawn beside it by the page, row for row.
/// </summary>
public sealed class TimelineView : Control
{
    /// <summary>Right-clicked on a clip ("clip") or on empty bars ("timeline"), for the page to show its menu.</summary>
    public event EventHandler<string>? MenuAsked;

    public const double LabelHeight = 20, RulerHeight = 18, Top = LabelHeight + RulerHeight, DefaultRowHeight = 46;

    /// <summary>How tall each track is (Solomon's design, point 18: dragged by the line under a track's name).</summary>
    public static readonly StyledProperty<double> RowHeightProperty = AvaloniaProperty.Register<TimelineView, double>(nameof(RowHeight), DefaultRowHeight);
    public double RowHeight { get => GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }

    public static readonly StyledProperty<IReadOnlyList<LayerRow>?> TracksProperty =
        AvaloniaProperty.Register<TimelineView, IReadOnlyList<LayerRow>?>(nameof(Tracks));
    public static readonly StyledProperty<IReadOnlyList<PlaygroundLabel>?> LabelsProperty =
        AvaloniaProperty.Register<TimelineView, IReadOnlyList<PlaygroundLabel>?>(nameof(Labels));
    public static readonly StyledProperty<long> LengthProperty = AvaloniaProperty.Register<TimelineView, long>(nameof(Length));
    /// <summary>Goes up after every change to the idea, so the clips are drawn again.</summary>
    public static readonly StyledProperty<int> RevisionProperty = AvaloniaProperty.Register<TimelineView, int>(nameof(Revision));
    public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<TimelineView, double>(nameof(Position), -1);
    public static readonly StyledProperty<RollRange?> HighlightProperty = AvaloniaProperty.Register<TimelineView, RollRange?>(nameof(Highlight));
    public static readonly StyledProperty<ICommand?> HighlightCommandProperty = AvaloniaProperty.Register<TimelineView, ICommand?>(nameof(HighlightCommand));
    public static readonly StyledProperty<PlaygroundClip?> SelectedProperty = AvaloniaProperty.Register<TimelineView, PlaygroundClip?>(nameof(Selected));
    public static readonly StyledProperty<ICommand?> SelectCommandProperty = AvaloniaProperty.Register<TimelineView, ICommand?>(nameof(SelectCommand));
    public static readonly StyledProperty<PlaygroundLabel?> SelectedLabelProperty = AvaloniaProperty.Register<TimelineView, PlaygroundLabel?>(nameof(SelectedLabel));
    public static readonly StyledProperty<ICommand?> LabelCommandProperty = AvaloniaProperty.Register<TimelineView, ICommand?>(nameof(LabelCommand));
    /// <summary>Takes a <see cref="ClipEdit"/> when a clip has been slid, stretched or trimmed.</summary>
    public static readonly StyledProperty<ICommand?> EditCommandProperty = AvaloniaProperty.Register<TimelineView, ICommand?>(nameof(EditCommand));
    /// <summary>Takes the picked clip's <see cref="ClipPick"/> when Delete is pressed.</summary>
    /// <summary>What clips snap to, in ticks: a bar, half a bar or a beat.</summary>
    public static readonly StyledProperty<long> GridProperty = AvaloniaProperty.Register<TimelineView, long>(nameof(Grid), 4 * PhraseAnalyser.Ppq);

    static TimelineView()
    {
        AffectsRender<TimelineView>(RowHeightProperty, TracksProperty, LabelsProperty, LengthProperty, RevisionProperty, PositionProperty, HighlightProperty,
                                    SelectedProperty, SelectedLabelProperty, ViewStartProperty, ViewLengthProperty, PlayFromProperty, TryingProperty);
        AffectsMeasure<TimelineView>(TracksProperty, RevisionProperty, RowHeightProperty);
    }

    public IReadOnlyList<LayerRow>? Tracks { get => GetValue(TracksProperty); set => SetValue(TracksProperty, value); }
    public IReadOnlyList<PlaygroundLabel>? Labels { get => GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }
    public long Length { get => GetValue(LengthProperty); set => SetValue(LengthProperty, value); }
    public int Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public RollRange? Highlight { get => GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    public ICommand? HighlightCommand { get => GetValue(HighlightCommandProperty); set => SetValue(HighlightCommandProperty, value); }
    public PlaygroundClip? Selected { get => GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public ICommand? SelectCommand { get => GetValue(SelectCommandProperty); set => SetValue(SelectCommandProperty, value); }
    public PlaygroundLabel? SelectedLabel { get => GetValue(SelectedLabelProperty); set => SetValue(SelectedLabelProperty, value); }
    public ICommand? LabelCommand { get => GetValue(LabelCommandProperty); set => SetValue(LabelCommandProperty, value); }
    public ICommand? EditCommand { get => GetValue(EditCommandProperty); set => SetValue(EditCommandProperty, value); }
    public long Grid { get => GetValue(GridProperty); set => SetValue(GridProperty, value); }
    /// <summary>Where the view starts, in ticks, when zoomed in (Ctrl + scroll); changed by the view as it's panned.</summary>
    public static readonly StyledProperty<long> ViewStartProperty =
        AvaloniaProperty.Register<TimelineView, long>(nameof(ViewStart), defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);
    /// <summary>How much of the timeline the view shows, in ticks: 0 (or all of it) fits the whole idea.</summary>
    public static readonly StyledProperty<long> ViewLengthProperty =
        AvaloniaProperty.Register<TimelineView, long>(nameof(ViewLength), defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);
    /// <summary>Where Play starts from: set by clicking the bar numbers, drawn as a marker on them.</summary>
    /// <summary>A suggestion being heard with the idea, drawn faintly where it would go.</summary>
    public static readonly StyledProperty<TryingPart?> TryingProperty = AvaloniaProperty.Register<TimelineView, TryingPart?>(nameof(Trying));
    public TryingPart? Trying { get => GetValue(TryingProperty); set => SetValue(TryingProperty, value); }
    public static readonly StyledProperty<long> PlayFromProperty = AvaloniaProperty.Register<TimelineView, long>(nameof(PlayFrom));
    public static readonly StyledProperty<ICommand?> CursorCommandProperty = AvaloniaProperty.Register<TimelineView, ICommand?>(nameof(CursorCommand));
    /// <summary>Makes the files when a clip or a label is dragged out of the timeline into a DAW.</summary>
    public static readonly StyledProperty<IFileDragSource?> DragSourceProperty = AvaloniaProperty.Register<TimelineView, IFileDragSource?>(nameof(DragSource));
    public long ViewStart { get => GetValue(ViewStartProperty); set => SetValue(ViewStartProperty, value); }
    public long ViewLength { get => GetValue(ViewLengthProperty); set => SetValue(ViewLengthProperty, value); }
    public long PlayFrom { get => GetValue(PlayFromProperty); set => SetValue(PlayFromProperty, value); }
    public ICommand? CursorCommand { get => GetValue(CursorCommandProperty); set => SetValue(CursorCommandProperty, value); }
    public IFileDragSource? DragSource { get => GetValue(DragSourceProperty); set => SetValue(DragSourceProperty, value); }

    public static readonly StyledProperty<bool> SnapGridProperty = AvaloniaProperty.Register<TimelineView, bool>(nameof(SnapGrid), true);
    public static readonly StyledProperty<bool> SnapRelativeProperty = AvaloniaProperty.Register<TimelineView, bool>(nameof(SnapRelative), true);
    public static readonly StyledProperty<bool> SnapObjectsProperty = AvaloniaProperty.Register<TimelineView, bool>(nameof(SnapObjects), true);
    /// <summary>Snap onto grid lines.</summary>
    public bool SnapGrid { get => GetValue(SnapGridProperty); set => SetValue(SnapGridProperty, value); }
    /// <summary>Snap in whole grid steps from where it was, keeping how far off the grid it sat (Bitwig's grid offset).</summary>
    public bool SnapRelative { get => GetValue(SnapRelativeProperty); set => SetValue(SnapRelativeProperty, value); }
    /// <summary>Snap to the edges of other clips, the highlight, labels and the play marker (Bitwig's snap to events).</summary>
    public bool SnapObjects { get => GetValue(SnapObjectsProperty); set => SetValue(SnapObjectsProperty, value); }

    /// <summary>Takes a <see cref="PartDrop"/> when a suggestion or .mid files are let go on a track.</summary>
    public static readonly StyledProperty<ICommand?> DropCommandProperty = AvaloniaProperty.Register<TimelineView, ICommand?>(nameof(DropCommand));
    public ICommand? DropCommand { get => GetValue(DropCommandProperty); set => SetValue(DropCommandProperty, value); }

    public TimelineView()
    {
        Focusable = true;
        ClipToBounds = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => { _drop = null; InvalidateVisual(); });
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(PointerTouchPadGestureMagnifyEvent, OnPinch);
    }

    // ---- dropping parts in ----

    /// <summary>A part being dragged over the timeline, where it would land.</summary>
    sealed record Landing(object Part, string Name, int Row, long Start, long Length);

    Landing? _drop;
    // A dragged file is read once, not on every move of the pointer.
    string _peekedFor = "";
    (string Name, long Length, int Parts) _peeked;

    Landing? LandingOf(DragEventArgs e)
    {
        object part;
        string name;
        long length;
        if (e.DataTransfer.TryGetValue(PartDrag.Format) is { } match)
        {
            part = match;
            name = match.Title;
            length = Math.Max(1, PhraseAnalyser.BarsOf(match.Match.Notes)) * (long)Bar;
        }
        else if (!FileDrag.Dragging && e.DataTransfer.TryGetFiles() is { Length: > 0 } files
                 && files.Select(f => f.TryGetLocalPath()).OfType<string>().Where(MidiFileReader.IsMidiPath).ToList() is { Count: > 0 } paths)
        {
            part = paths;
            var key = string.Join("|", paths);
            if (key != _peekedFor)
            {
                _peekedFor = key;
                _peeked = Peek(paths);
            }
            (name, length, _) = _peeked;
        }
        else return null;
        var p = e.GetPosition(this);
        // It starts at the grid step the pointer is in (anywhere with Shift); a file of several parts gets new tracks.
        var grid = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : Math.Max(1, Grid);
        var start = Math.Max(0, TickAt(p.X) / grid * grid);
        var row = part is List<string> && _peeked.Parts > 1 ? RowCount : Math.Clamp((int)Math.Floor((p.Y - Top) / RowHeight), 0, RowCount);
        return new Landing(part, name, row, start, length);
    }

    /// <summary>The name and length of .mid files dragged in, and how many parts they hold.</summary>
    static (string Name, long Length, int Parts) Peek(IReadOnlyList<string> paths)
    {
        try
        {
            var parts = paths.SelectMany(p => MidiFileReader.Read(p)).Select(p => PhraseAnalyser.Normalise(p.Notes, p.Ppq)).Where(n => n.Count > 0).ToList();
            var name = paths.Count == 1 ? Path.GetFileNameWithoutExtension(paths[0]) : $"{paths.Count} files";
            return (name, Math.Max(1, parts.Count == 0 ? 1 : parts.Max(n => PhraseAnalyser.BarsOf(n))) * (long)Bar, parts.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return (Path.GetFileName(paths[0]), Bar, 1);
        }
    }

    void OnDragOver(object? sender, DragEventArgs e)
    {
        _drop = LandingOf(e);
        e.DragEffects = _drop is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
        InvalidateVisual();
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        var landing = LandingOf(e);
        _drop = null;
        _peekedFor = "";
        InvalidateVisual();
        if (landing is null) return;
        var drop = new PartDrop(landing.Part, landing.Row, landing.Start);
        if (DropCommand?.CanExecute(drop) == true) DropCommand.Execute(drop);
        e.Handled = true;
    }

    const int Bar = 4 * PhraseAnalyser.Ppq;

    // Each clip's notes as they play, worked out once per change rather than on every frame of the playhead.
    readonly Dictionary<PlaygroundClip, List<NoteEvent>> _rendered = [];
    int _renderedFor = -1;

    List<NoteEvent> NotesOf(PlaygroundClip clip)
    {
        if (_renderedFor != Revision) { _rendered.Clear(); _renderedFor = Revision; }
        if (!_rendered.TryGetValue(clip, out var notes)) _rendered[clip] = notes = clip.Render();
        return notes;
    }

    int RowCount => Tracks?.Count ?? 0;

    int RowOf(LayerRow track)
    {
        for (var i = 0; i < RowCount; i++) if (Tracks![i] == track) return i;
        return -1;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, Top + Math.Max(1, RowCount + 1) * RowHeight);

    /// <summary>How many ticks the view shows: the whole idea, or less when zoomed in.</summary>
    long Span => ViewLength <= 0 || ViewLength >= Math.Max(Bar, Length) ? Math.Max(Bar, Length) : Math.Max(Bar, ViewLength);
    /// <summary>The first tick shown.</summary>
    long From => Math.Clamp(ViewStart, 0, Math.Max(0, Math.Max(Bar, Length) - Span));
    double Scale => Math.Max(1, Bounds.Width) / Span;
    double X(long tick) => (tick - From) * Scale;
    /// <summary>A length in ticks, as pixels.</summary>
    double W(long ticks) => ticks * Scale;
    long TickAt(double x) => (long)Math.Round(x / Scale) + From;

    /// <summary>Shows <paramref name="span"/> ticks from <paramref name="start"/> (all of it: back to fitting the whole idea).</summary>
    void ShowWindow(long start, long span)
    {
        var all = Math.Max(Bar, Length);
        span = Math.Clamp(span, Bar, all);
        ViewLength = span >= all ? 0 : span;
        ViewStart = span >= all ? 0 : Math.Clamp(start, 0, all - span);
    }

    // ---- zoom and pan ----

    /// <summary>Ctrl + scroll (or a trackpad pinch) zooms around the pointer, Shift + scroll (or sideways scroll) moves along the bars; plain scroll is left to the page.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var dy = e.Delta.Y;
        // Cmd on a Mac; a trackpad's sideways part only counts when it's mostly sideways
        if ((e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) && dy != 0)
        {
            var at = TickAt(e.GetPosition(this).X);
            var span = (long)Math.Round(Span * Math.Pow(0.8, dy));
            var share = (at - From) / (double)Span;
            span = Math.Clamp(span, Bar, Math.Max(Bar, Length));
            ShowWindow(at - (long)Math.Round(share * span), span);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && dy != 0 || Math.Abs(e.Delta.X) > Math.Abs(dy))
        {
            var by = Math.Abs(e.Delta.X) > Math.Abs(dy) ? -e.Delta.X : -dy;
            ShowWindow(From + (long)Math.Round(by * Span / 8), Span);
            e.Handled = true;
        }
    }

    double? _panFrom;
    long _panStart;
    static readonly global::Avalonia.Input.Cursor HandCursor = new(StandardCursorType.Hand), StretchCursor = new(StandardCursorType.SizeWestEast);

    /// <summary>A trackpad pinch zooms round the pointer, as Ctrl + scroll does.</summary>
    void OnPinch(object? sender, PointerDeltaEventArgs e)
    {
        if (e.Delta.X == 0) return;
        var at = TickAt(e.GetPosition(this).X);
        var span = Math.Clamp((long)Math.Round(Span / (1 + e.Delta.X)), Bar, Math.Max(Bar, Length));
        var share = (at - From) / (double)Span;
        ShowWindow(at - (long)Math.Round(share * span), span);
        e.Handled = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // While playing zoomed in, the view turns the page when the playhead runs off it.
        if (change.Property == PositionProperty && Position >= 0 && Span < Math.Max(Bar, Length) && (Position < From || Position >= From + Span))
            ShowWindow((long)Position / Bar * Bar, Span);
    }

    IBrush Res(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    (LayerRow Track, PlaygroundClip Clip)? ClipAt(Point p)
    {
        if (Tracks is not { } tracks || p.Y < Top) return null;
        var row = (int)((p.Y - Top) / RowHeight);
        if (row < 0 || row >= tracks.Count) return null;
        var tick = TickAt(p.X);
        // The last clip drawn is on top.
        var clip = tracks[row].Clips.LastOrDefault(c => tick >= c.Start && tick < c.End);
        return clip is null ? null : (tracks[row], clip);
    }

    long? _dragFrom;
    double _pressX;
    RollRange? _dragging;

    /// <summary>What a drag on a clip does: slide it, play it round more (its right edge) or trim its start (its left edge).</summary>
    enum Grab { Move, Stretch, Trim }

    /// <summary>A clip being dragged: where it was, and where it would go now.</summary>
    sealed class ClipDrag
    {
        public required LayerRow Track;
        public required PlaygroundClip Clip;
        public required Grab Grab;
        public required Point From;
        public bool Moved;
        public int Row, FromRow;
        public long Start, Length, Offset;
    }

    ClipDrag? _clip;
    const double Edge = 6;

    Grab GrabAt(PlaygroundClip clip, double x)
    {
        double left = X(clip.Start), right = X(clip.End);
        // A narrow clip keeps a middle to slide it by.
        var edge = Math.Min(Edge, (right - left) / 4);
        return x >= right - edge ? Grab.Stretch : x <= left + edge ? Grab.Trim : Grab.Move;
    }

    long SnapTo(long tick, KeyModifiers keys)
    {
        var grid = keys.HasFlag(KeyModifiers.Shift) ? 1 : Math.Max(1, Grid);
        return (long)Math.Round(tick / (double)grid) * grid;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        // The middle button drags the view along.
        if (props.IsMiddleButtonPressed)
        {
            _panFrom = p.X;
            _panStart = From;
            e.Handled = true;
            return;
        }
        // Right-click: the clip under it is picked, and the page shows the menu for a clip or for empty bars.
        if (props.IsRightButtonPressed)
        {
            var at = ClipAt(p);
            if (at is { } r)
            {
                var pick = new ClipPick(r.Track.Layer, r.Clip);
                if (SelectCommand?.CanExecute(pick) == true) SelectCommand.Execute(pick);
            }
            else
            {
                var tick = SnapTo(Math.Clamp(TickAt(p.X), 0, Length), e.KeyModifiers);   // Paste lands where you clicked
                if (CursorCommand?.CanExecute(tick) == true) CursorCommand.Execute(tick);
            }
            MenuAsked?.Invoke(this, at is null ? "timeline" : "clip");
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        _press = e;
        if (p.Y < LabelHeight)
        {
            var tick = TickAt(p.X);
            var label = Labels?.LastOrDefault(l => tick >= l.Start && tick < l.End);
            if (LabelCommand?.CanExecute(label) == true) LabelCommand.Execute(label);
            if (label is not null) _labelDrag = (label, p);
            e.Handled = true;
            return;
        }
        Focus();
        // Double-click the bar numbers: the whole idea fits the view again.
        if (p.Y < Top && e.ClickCount == 2)
        {
            ShowWindow(0, Math.Max(Bar, Length));
            _dragFrom = null;
            e.Handled = true;
            return;
        }
        if (ClipAt(p) is { } hit)
        {
            var pick = new ClipPick(hit.Track.Layer, hit.Clip);
            if (SelectCommand?.CanExecute(pick) == true) SelectCommand.Execute(pick);
            _clip = new ClipDrag
            {
                Track = hit.Track, Clip = hit.Clip, Grab = GrabAt(hit.Clip, p.X), From = p, Row = RowOf(hit.Track), FromRow = RowOf(hit.Track),
                Start = hit.Clip.Start, Length = hit.Clip.Length, Offset = hit.Clip.Offset,
            };
            e.Handled = true;
            return;
        }
        // Empty bars or the bar numbers: highlight from here (a click on the bar numbers moves where Play starts instead).
        _dragFrom = Math.Clamp((long)Math.Floor(TickAt(p.X) / (double)Bar) * Bar, 0, Math.Max(0, Length - Bar));
        _pressX = p.X;
        _onRuler = p.Y < Top;
        _dragging = null;
        e.Handled = true;   // the press captures the pointer, so the drag carries on outside the view
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_panFrom is { } panFrom)
        {
            ShowWindow(_panStart - (long)Math.Round((p.X - panFrom) / Scale), Span);
            return;
        }
        // A label pulled away drags its bars out to the DAW.
        if (_labelDrag is { } ld)
        {
            if (Math.Abs(p.X - ld.At.X) < 6 && Math.Abs(p.Y - ld.At.Y) < 6) return;
            _labelDrag = null;
            DragOut(ld.Label);
            return;
        }
        if (_clip is { } d)
        {
            if (!d.Moved && Math.Abs(p.X - d.From.X) < 4 && Math.Abs(p.Y - d.From.Y) < 4) return;
            // Pulled right out of the timeline: it goes out to the DAW as a MIDI clip instead.
            if (d.Grab == Grab.Move && (p.X < -OutLeft || p.X > Bounds.Width + OutBy || p.Y < -OutBy || p.Y > Bounds.Height + OutBy))
            {
                _clip = null;
                InvalidateVisual();
                DragOut(new ClipPick(d.Track.Layer, d.Clip));
                return;
            }
            d.Moved = true;
            DragTo(d, p, e.KeyModifiers);
            InvalidateVisual();
            return;
        }
        if (_dragFrom is null)
        {
            // The pointer says what a drag would do: slide a clip, or stretch or trim it at its edges.
            base.Cursor = ClipAt(p) is { } over ? GrabAt(over.Clip, p.X) == Grab.Move ? HandCursor : StretchCursor : global::Avalonia.Input.Cursor.Default;
            return;
        }
        var from = _dragFrom.Value;
        var x = e.GetPosition(this).X;
        if (Math.Abs(x - _pressX) < 4) return;
        var at = Math.Clamp(TickAt(x), 0, Length);
        // Whole bars, from the bar pressed to the bar the pointer is over, either way.
        long a = Math.Min(from, at / Bar * Bar), b = Math.Max(from + Bar, (at + Bar - 1) / Bar * Bar);
        _dragging = new RollRange(Math.Max(0, a), Math.Min(Math.Max(Length, Bar), b));
        InvalidateVisual();
    }

    /// <summary>The edges other things snap to: every clip but <paramref name="moving"/>, the highlight, labels and the play marker.</summary>
    List<long> SnapPoints(PlaygroundClip moving)
    {
        var points = new List<long> { PlayFrom };
        if (Tracks is { } tracks)
            foreach (var c in tracks.SelectMany(t => t.Clips))
                if (c != moving) { points.Add(c.Start); points.Add(c.End); }
        if (Highlight is { } hl) { points.Add(hl.Start); points.Add(hl.End); }
        if (Labels is { } labels)
            foreach (var l in labels) { points.Add(l.Start); points.Add(l.End); }
        return points;
    }

    /// <summary>
    /// Where an edge dragged from <paramref name="was"/> to <paramref name="raw"/> lands: on the grid, the same distance
    /// off the grid as it was (relative), or on another object's edge near it, whichever is nearest; anywhere with Shift.
    /// <paramref name="along"/> is how far the snapped edge is from the clip's start, so a clip's end can catch an edge too.
    /// </summary>
    long Snap(long raw, long was, KeyModifiers keys, IReadOnlyList<long> points, params long[] along)
    {
        if (keys.HasFlag(KeyModifiers.Shift)) return raw;
        var grid = Math.Max(1, Grid);
        var best = raw;
        var bestBy = long.MaxValue;
        void Try(long at)
        {
            var by = Math.Abs(at - raw);
            if (by < bestBy) { best = at; bestBy = by; }
        }
        if (SnapGrid) Try((long)Math.Round(raw / (double)grid) * grid);
        if (SnapRelative) Try(was + (long)Math.Round((raw - was) / (double)grid) * grid);
        if (SnapObjects)
        {
            // Only edges close by on screen catch it, so it doesn't jump across the view.
            var near = Math.Max(1, (long)(10 / Scale));
            foreach (var point in points)
                foreach (var a in along.Length == 0 ? [0L] : along)
                    if (Math.Abs(point - a - raw) <= near) Try(point - a);
        }
        return best;
    }

    void DragTo(ClipDrag d, Point p, KeyModifiers keys)
    {
        var c = d.Clip;
        var delta = TickAt(p.X) - TickAt(d.From.X);
        var min = Math.Max(1, keys.HasFlag(KeyModifiers.Shift) ? PhraseAnalyser.Ppq / 4 : Grid);
        var points = SnapPoints(c);
        switch (d.Grab)
        {
            case Grab.Move:
                // Its start or its end can catch an object's edge.
                d.Start = Math.Max(0, Snap(c.Start + delta, c.Start, keys, points, 0, c.Length));
                // Onto the row under the pointer; below the last one makes a new track.
                d.Row = Math.Clamp((int)Math.Floor((p.Y - Top) / RowHeight), 0, RowCount);
                break;
            case Grab.Stretch:
                d.Length = Math.Max(min, Snap(c.End + delta, c.End, keys, points) - c.Start);
                break;
            case Grab.Trim:
                // never past its end, even for a clip shorter than a grid step
                d.Start = Math.Clamp(Snap(c.Start + delta, c.Start, keys, points), 0, Math.Max(0, c.End - Math.Min(min, c.Length)));
                d.Length = c.End - d.Start;
                // Its notes stay where they were on the timeline: the go round starts further in (or further back).
                d.Offset = ((c.Offset + d.Start - c.Start) % c.Loop + c.Loop) % c.Loop;
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _labelDrag = null;
        if (_panFrom is not null) { _panFrom = null; return; }
        if (_clip is { } d)
        {
            _clip = null;
            if (d.Moved && (d.Start != d.Clip.Start || d.Length != d.Clip.Length || d.Offset != d.Clip.Offset || d.Row != d.FromRow))
            {
                var edit = new ClipEdit(d.Track.Layer, d.Clip, d.Row, d.Start, d.Length, d.Offset);
                if (EditCommand?.CanExecute(edit) == true) EditCommand.Execute(edit);
            }
            InvalidateVisual();
            return;
        }
        if (_dragFrom is null) return;
        _dragFrom = null;
        // A click on empty bars clears the highlight; a click on the bar numbers moves where Play starts; a drag sets the highlight.
        var range = _dragging;
        _dragging = null;
        if (range is null && _onRuler)
        {
            var at = SnapTo(Math.Clamp(TickAt(e.GetPosition(this).X), 0, Length), e.KeyModifiers);
            if (CursorCommand?.CanExecute(at) == true) CursorCommand.Execute(at);
            InvalidateVisual();
            return;
        }
        if (HighlightCommand?.CanExecute(range) == true) HighlightCommand.Execute(range);
        InvalidateVisual();
    }

    PointerPressedEventArgs? _press;
    (PlaygroundLabel Label, Point At)? _labelDrag;
    bool _onRuler;
    /// <summary>How far past its edges a clip is pulled before it counts as dragged out to the DAW.</summary>
    const double OutBy = 24;
    /// <summary>To the left it's past the track names too, so overshooting bar 1 still just moves the clip.</summary>
    const double OutLeft = 240;

    async void DragOut(object item)
    {
        if (_press is not { } press || DragSource is not { } source) return;
        await FileDrag.DragOut(this, press, source, [item]);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _panFrom = null;
        _labelDrag = null;
        _clip = null;
        _dragFrom = null;
        _dragging = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0) return;
        dc.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));   // so empty bars take clicks and drags
        var text = Res("TextBrush", Brushes.White);
        var muted = Res("MutedBrush", Brushes.Gray);
        var stroke = Res("StrokeBrush", Brushes.DimGray);
        var accent = Res("AccentBrush", Brushes.SeaGreen);
        var soft = Res("AccentSoftBrush", Brushes.Transparent);
        var faint = Res("AccentFaintBrush", Brushes.Transparent);
        var raised = Res("SurfaceRaisedBrush", Brushes.Transparent);
        var small = new Typeface(Typeface.Default.FontFamily);
        var bold = new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.SemiBold);
        FormattedText Text(string s, double size, IBrush brush, Typeface? face = null) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face ?? small, size, brush);

        var length = Math.Max(Bar, Length);
        var bars = (int)Math.Ceiling(length / (double)Bar);
        var rows = RowCount;

        // Rows, then bar lines across them.
        for (var r = 0; r <= rows; r++)
        {
            var y = Top + r * RowHeight;
            if (r % 2 == 0) dc.FillRectangle(raised, new Rect(0, y, w, RowHeight));
        }
        // The grid: beats faint (and half beats when zoomed right in), bars stronger, every 4th bar strongest, like Bitwig.
        int firstBar = (int)(From / Bar), lastBar = Math.Min(bars, (int)((From + Span) / Bar) + 1);
        var step = W(PhraseAnalyser.Ppq) >= 12 ? (W(PhraseAnalyser.Ppq / 2) >= 14 ? PhraseAnalyser.Ppq / 2 : PhraseAnalyser.Ppq) : 0;
        if (step > 0)
            using (dc.PushOpacity(0.35))
                for (long t = (long)firstBar * Bar; t < (long)lastBar * Bar; t += step)
                    if (t % Bar != 0) dc.FillRectangle(stroke, new Rect(X(t), Top, 1, h - Top));
        var every = Math.Max(1, (int)Math.Ceiling(28 / Math.Max(1, W(Bar))));   // bar numbers no closer than 28 px
        for (var b = firstBar; b <= lastBar; b++)
        {
            var x = X((long)b * Bar);
            dc.FillRectangle(stroke, new Rect(x, LabelHeight + (b % 4 == 0 ? 0 : RulerHeight / 2), b % 4 == 0 ? 1.5 : 1, h - LabelHeight));
            if (b < bars && b % every == 0) dc.DrawText(Text((b + 1).ToString(), 11, muted), new Point(x + 3, LabelHeight + 2));
        }

        // Labels.
        if (Labels is { Count: > 0 } labels)
            foreach (var l in labels)
            {
                var rect = new Rect(X(l.Start) + 1, 1, Math.Max(4, W(l.Length) - 2), LabelHeight - 3);
                dc.DrawRectangle(l == SelectedLabel ? soft : faint, l == SelectedLabel ? new Pen(accent, 1.2) : null, new RoundedRect(rect, 4));
                using (dc.PushClip(rect))
                {
                    var feel = l.Feel == SectionFeel.Any || l.Feel.ToString() == l.Name ? "" : $"  · {l.Feel}";
                    dc.DrawText(Text(l.Name + feel, 11.5, text, bold), new Point(rect.X + 6, rect.Y + 1));
                }
            }
        else dc.DrawText(Text("Labels go here: highlight some bars, then + Label", 11, muted), new Point(6, 3));

        // The highlight, under the clips.
        if ((_dragging ?? Highlight) is { } hl)
            dc.FillRectangle(soft, new Rect(X(hl.Start), LabelHeight, Math.Max(2, W(hl.End - hl.Start)), h - LabelHeight));

        // Clips.
        if (Tracks is { } tracks)
            for (var r = 0; r < tracks.Count; r++)
            {
                var track = tracks[r];
                var y = Top + r * RowHeight;
                var drums = track.Role == MidiRoles.Drums;
                using var _ = dc.PushOpacity(track.Muted ? 0.4 : 1);
                foreach (var clip in track.Clips)
                {
                    var rect = new Rect(X(clip.Start) + 0.5, y + 3, Math.Max(3, W(clip.Length) - 1), RowHeight - 6);
                    if (rect.Right < 0 || rect.X > w) continue;   // zoomed in: off the view
                    var picked = clip == Selected;
                    dc.DrawRectangle(picked ? soft : faint, new Pen(picked ? accent : stroke, picked ? 2 : 1), new RoundedRect(rect, 5));
                    using (dc.PushClip(rect))
                    {
                        // Where each go round starts.
                        for (var i = 1; i < clip.Repeats; i++)
                        {
                            var rx = X(clip.RepeatAt(i));
                            if (rx > rect.X && rx < rect.Right) dc.FillRectangle(stroke, new Rect(rx, rect.Y + 14, 1, rect.Height - 14));
                        }
                        dc.DrawText(Text(clip.Name, 11, text, bold), new Point(rect.X + 5, rect.Y + 1));
                        // How far each go round moved to fit the chords under it (or, set by hand, how far it's moved in all).
                        for (var i = 0; i < clip.Repeats && i < clip.Fits.Count; i++)
                        {
                            var by = clip.Pinned ? clip.Shift(i) : clip.Fits[i];
                            if (by == 0) continue;
                            var label = Text(by > 0 ? $"+{by}" : $"{by}", 10, clip.Pinned ? text : accent, bold);
                            dc.DrawText(label, new Point(Math.Max(rect.X, X(clip.RepeatAt(i))) + 3, rect.Bottom - label.Height - 1));
                        }
                        // Changed by hand: a corner mark (bottom right) says it won't move to fit.
                        if (clip.Pinned && clip.Role is not (MidiRoles.Chords or MidiRoles.Drums) && rect.Width > 24)
                        {
                            var corner = new StreamGeometry();
                            using (var gc = corner.Open())
                            {
                                gc.BeginFigure(new Point(rect.Right, rect.Bottom - 10), true);
                                gc.LineTo(new Point(rect.Right, rect.Bottom));
                                gc.LineTo(new Point(rect.Right - 10, rect.Bottom));
                                gc.EndFigure(true);
                            }
                            dc.DrawGeometry(accent, null, corner);
                        }
                        var notes = NotesOf(clip);
                        if (notes.Count > 0)
                        {
                            int lo = notes.Min(n => n.Key), hi = Math.Max(lo + 11, notes.Max(n => n.Key));
                            double top = rect.Y + 16, span = rect.Height - 19;
                            var brush = drums ? muted : accent;
                            foreach (var n in notes)
                            {
                                if (n.Start > From + Span || n.Start + n.Length < From) continue;
                                var ny = top + (hi - n.Key) / (double)(hi - lo) * span;
                                dc.FillRectangle(brush, new Rect(X(n.Start), ny, Math.Max(1.5, W(n.Length) - 1), 2));
                            }
                        }
                    }
                }
            }

        // A suggestion being heard with the idea: faint, dashed, where Add or Swap in would put it.
        if (Trying is { } tr)
        {
            var rect = new Rect(X(tr.Start) + 0.5, Top + tr.Row * RowHeight + 3, Math.Max(3, W(tr.Length) - 1), RowHeight - 6);
            dc.DrawRectangle(soft, new Pen(accent, 2, new DashStyle([4, 3], 0)), new RoundedRect(rect, 5));
            using (dc.PushClip(rect))
            {
                dc.DrawText(Text($"▶ Trying: {tr.Name}", 11, text, bold), new Point(rect.X + 5, rect.Y + 1));
                if (tr.Notes.Count > 0)
                {
                    int lo = tr.Notes.Min(n => n.Key), hi = Math.Max(lo + 11, tr.Notes.Max(n => n.Key));
                    double top = rect.Y + 16, span = rect.Height - 19;
                    using (dc.PushOpacity(0.6))
                        foreach (var n in tr.Notes)
                        {
                            if (n.Start > From + Span || n.Start + n.Length < From) continue;
                            dc.FillRectangle(accent, new Rect(X(n.Start), top + (hi - n.Key) / (double)(hi - lo) * span, Math.Max(1.5, W(n.Length) - 1), 2));
                        }
                }
            }
        }

        // The clip being dragged, where it would land.
        if (_clip is { Moved: true } g)
        {
            var rect = new Rect(X(g.Start) + 0.5, Top + g.Row * RowHeight + 3, Math.Max(3, W(g.Length) - 1), RowHeight - 6);
            dc.DrawRectangle(soft, new Pen(accent, 2, new DashStyle([4, 3], 0)), new RoundedRect(rect, 5));
            var where = $"Bar {g.Start / (double)Bar + 1:0.##}{(g.Grab == Grab.Move ? "" : $" · {g.Length / (double)Bar:0.##} bars")}{(g.Row >= rows ? " · new track" : "")}";
            dc.DrawText(Text(where, 11, text, bold), new Point(rect.X + 5, rect.Y + 1));
        }

        // A suggestion or .mid file being dragged in, where it would land.
        if (_drop is { } d)
        {
            var rect = new Rect(X(d.Start) + 0.5, Top + d.Row * RowHeight + 3, Math.Max(3, W(d.Length) - 1), RowHeight - 6);
            dc.DrawRectangle(soft, new Pen(accent, 2, new DashStyle([4, 3], 0)), new RoundedRect(rect, 5));
            var where = $"{d.Name} · bar {d.Start / (double)Bar + 1:0.##}{(d.Row >= rows ? " · new track" : "")}{(_peeked.Parts > 1 && d.Part is List<string> ? $" · {_peeked.Parts} new tracks" : "")}";
            dc.DrawText(Text(where, 11, text, bold), new Point(rect.X + 5, rect.Y + 1));
        }

        if (rows == 0)
            dc.DrawText(Text("Bring parts in from the right, or press To the playground on the MIDI page.", 12, muted), new Point(10, Top + 14));

        // Where Play starts: a marker on the bar numbers.
        var cx = X(PlayFrom);
        var marker = new StreamGeometry();
        using (var gc = marker.Open())
        {
            gc.BeginFigure(new Point(cx - 5, LabelHeight + 1), true);
            gc.LineTo(new Point(cx + 5, LabelHeight + 1));
            gc.LineTo(new Point(cx, LabelHeight + 9));
            gc.EndFigure(true);
        }
        dc.DrawGeometry(accent, null, marker);

        // The playhead.
        if (Position >= 0) dc.FillRectangle(text, new Rect(X((long)Position), LabelHeight, 2, h - LabelHeight));
    }
}
