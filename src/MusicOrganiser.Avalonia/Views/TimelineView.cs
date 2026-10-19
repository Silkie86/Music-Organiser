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
    public const double LabelHeight = 20, RulerHeight = 18, Top = LabelHeight + RulerHeight, RowHeight = 46;

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
    public static readonly StyledProperty<ICommand?> DeleteCommandProperty = AvaloniaProperty.Register<TimelineView, ICommand?>(nameof(DeleteCommand));
    /// <summary>What clips snap to, in ticks: a bar, half a bar or a beat.</summary>
    public static readonly StyledProperty<long> GridProperty = AvaloniaProperty.Register<TimelineView, long>(nameof(Grid), 4 * PhraseAnalyser.Ppq);

    static TimelineView()
    {
        AffectsRender<TimelineView>(TracksProperty, LabelsProperty, LengthProperty, RevisionProperty, PositionProperty, HighlightProperty,
                                    SelectedProperty, SelectedLabelProperty);
        AffectsMeasure<TimelineView>(TracksProperty, RevisionProperty);
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
    public ICommand? DeleteCommand { get => GetValue(DeleteCommandProperty); set => SetValue(DeleteCommandProperty, value); }
    public long Grid { get => GetValue(GridProperty); set => SetValue(GridProperty, value); }
    /// <summary>Takes a <see cref="PartDrop"/> when a suggestion or .mid files are let go on a track.</summary>
    public static readonly StyledProperty<ICommand?> DropCommandProperty = AvaloniaProperty.Register<TimelineView, ICommand?>(nameof(DropCommand));
    public ICommand? DropCommand { get => GetValue(DropCommandProperty); set => SetValue(DropCommandProperty, value); }

    public TimelineView()
    {
        Focusable = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => { _drop = null; InvalidateVisual(); });
        AddHandler(DragDrop.DropEvent, OnDrop);
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
        else if (e.DataTransfer.TryGetFiles() is { Length: > 0 } files
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

    double Scale => Math.Max(1, Bounds.Width) / Math.Max(Bar, Length);
    double X(long tick) => tick * Scale;
    long TickAt(double x) => (long)Math.Round(x / Scale);

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
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        if (p.Y < LabelHeight)
        {
            var tick = TickAt(p.X);
            var label = Labels?.LastOrDefault(l => tick >= l.Start && tick < l.End);
            if (LabelCommand?.CanExecute(label) == true) LabelCommand.Execute(label);
            e.Handled = true;
            return;
        }
        Focus();
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
        // Empty bars or the bar numbers: highlight from here.
        _dragFrom = Math.Clamp((long)Math.Floor(TickAt(p.X) / (double)Bar) * Bar, 0, Math.Max(0, Length - Bar));
        _pressX = p.X;
        _dragging = null;
        e.Handled = true;   // the press captures the pointer, so the drag carries on outside the view
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_clip is { } d)
        {
            if (!d.Moved && Math.Abs(p.X - d.From.X) < 4 && Math.Abs(p.Y - d.From.Y) < 4) return;
            d.Moved = true;
            DragTo(d, p, e.KeyModifiers);
            InvalidateVisual();
            return;
        }
        if (_dragFrom is null)
        {
            // The pointer says what a drag would do: slide a clip, or stretch or trim it at its edges.
            Cursor = ClipAt(p) is { } over ? new Cursor(GrabAt(over.Clip, p.X) == Grab.Move ? StandardCursorType.Hand : StandardCursorType.SizeWestEast) : Cursor.Default;
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

    void DragTo(ClipDrag d, Point p, KeyModifiers keys)
    {
        var c = d.Clip;
        var delta = TickAt(p.X) - TickAt(d.From.X);
        var min = Math.Max(1, keys.HasFlag(KeyModifiers.Shift) ? PhraseAnalyser.Ppq / 4 : Grid);
        switch (d.Grab)
        {
            case Grab.Move:
                d.Start = Math.Max(0, SnapTo(c.Start + delta, keys));
                // Onto the row under the pointer; below the last one makes a new track.
                d.Row = Math.Clamp((int)Math.Floor((p.Y - Top) / RowHeight), 0, RowCount);
                break;
            case Grab.Stretch:
                d.Length = Math.Max(min, SnapTo(c.End + delta, keys) - c.Start);
                break;
            case Grab.Trim:
                d.Start = Math.Clamp(SnapTo(c.Start + delta, keys), 0, c.End - min);
                d.Length = c.End - d.Start;
                // Its notes stay where they were on the timeline: the go round starts further in (or further back).
                d.Offset = ((c.Offset + d.Start - c.Start) % c.Loop + c.Loop) % c.Loop;
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
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
        // A click on empty bars clears the highlight; a drag sets it.
        var range = _dragging;
        _dragging = null;
        if (HighlightCommand?.CanExecute(range) == true) HighlightCommand.Execute(range);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Delete or Key.Back) || Selected is not { } clip || Tracks?.FirstOrDefault(t => t.Clips.Contains(clip)) is not { } track) return;
        var pick = new ClipPick(track.Layer, clip);
        if (DeleteCommand?.CanExecute(pick) == true) DeleteCommand.Execute(pick);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
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
        var every = Math.Max(1, (int)Math.Ceiling(28 / Math.Max(1, X(Bar))));   // bar numbers no closer than 28 px
        for (var b = 0; b <= bars; b++)
        {
            var x = X((long)b * Bar);
            dc.FillRectangle(stroke, new Rect(x, LabelHeight + (b % 4 == 0 ? 0 : RulerHeight / 2), 1, h - LabelHeight));
            if (b < bars && b % every == 0) dc.DrawText(Text((b + 1).ToString(), 11, muted), new Point(x + 3, LabelHeight + 2));
        }

        // Labels.
        if (Labels is { Count: > 0 } labels)
            foreach (var l in labels)
            {
                var rect = new Rect(X(l.Start) + 1, 1, Math.Max(4, X(l.Length) - 2), LabelHeight - 3);
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
            dc.FillRectangle(soft, new Rect(X(hl.Start), LabelHeight, Math.Max(2, X(hl.End - hl.Start)), h - LabelHeight));

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
                    var rect = new Rect(X(clip.Start) + 0.5, y + 3, Math.Max(3, X(clip.Length) - 1), RowHeight - 6);
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
                                var ny = top + (hi - n.Key) / (double)(hi - lo) * span;
                                dc.FillRectangle(brush, new Rect(X(n.Start), ny, Math.Max(1.5, X(n.Length) - 1), 2));
                            }
                        }
                    }
                }
            }

        // The clip being dragged, where it would land.
        if (_clip is { Moved: true } g)
        {
            var rect = new Rect(X(g.Start) + 0.5, Top + g.Row * RowHeight + 3, Math.Max(3, X(g.Length) - 1), RowHeight - 6);
            dc.DrawRectangle(soft, new Pen(accent, 2, new DashStyle([4, 3], 0)), new RoundedRect(rect, 5));
            var where = $"Bar {g.Start / (double)Bar + 1:0.##}{(g.Grab == Grab.Move ? "" : $" · {g.Length / (double)Bar:0.##} bars")}{(g.Row >= rows ? " · new track" : "")}";
            dc.DrawText(Text(where, 11, text, bold), new Point(rect.X + 5, rect.Y + 1));
        }

        // A suggestion or .mid file being dragged in, where it would land.
        if (_drop is { } d)
        {
            var rect = new Rect(X(d.Start) + 0.5, Top + d.Row * RowHeight + 3, Math.Max(3, X(d.Length) - 1), RowHeight - 6);
            dc.DrawRectangle(soft, new Pen(accent, 2, new DashStyle([4, 3], 0)), new RoundedRect(rect, 5));
            var where = $"{d.Name} · bar {d.Start / (double)Bar + 1:0.##}{(d.Row >= rows ? " · new track" : "")}{(_peeked.Parts > 1 && d.Part is List<string> ? $" · {_peeked.Parts} new tracks" : "")}";
            dc.DrawText(Text(where, 11, text, bold), new Point(rect.X + 5, rect.Y + 1));
        }

        if (rows == 0)
            dc.DrawText(Text("Bring parts in from the right, or press To the playground on the MIDI page.", 12, muted), new Point(10, Top + 14));

        // The playhead.
        if (Position >= 0) dc.FillRectangle(text, new Rect(X((long)Position), LabelHeight, 2, h - LabelHeight));
    }
}
