using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// The playground's timeline (Solomon, 2026-10-19: "it should work like a mini DAW"): labels along the top, bar numbers
/// under them, then a row for each track with its clips drawn where they play, their notes inside and a faint line
/// where each go round starts. Click a clip to pick it; drag across empty bars (or the bar numbers) to highlight them.
/// The track names and buttons are drawn beside it by the page, row for row.
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
        if (ClipAt(p) is { } hit)
        {
            var pick = new ClipPick(hit.Track.Layer, hit.Clip);
            if (SelectCommand?.CanExecute(pick) == true) SelectCommand.Execute(pick);
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
        if (_dragFrom is not { } from) return;
        var x = e.GetPosition(this).X;
        if (Math.Abs(x - _pressX) < 4) return;
        var at = Math.Clamp(TickAt(x), 0, Length);
        // Whole bars, from the bar pressed to the bar the pointer is over, either way.
        long a = Math.Min(from, at / Bar * Bar), b = Math.Max(from + Bar, (at + Bar - 1) / Bar * Bar);
        _dragging = new RollRange(Math.Max(0, a), Math.Min(Math.Max(Length, Bar), b));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragFrom is null) return;
        _dragFrom = null;
        // A click on empty bars clears the highlight; a drag sets it.
        var range = _dragging;
        _dragging = null;
        if (HighlightCommand?.CanExecute(range) == true) HighlightCommand.Execute(range);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
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

        if (rows == 0)
            dc.DrawText(Text("Bring parts in from the right, or press To the playground on the MIDI page.", 12, muted), new Point(10, Top + 14));

        // The playhead.
        if (Position >= 0) dc.FillRectangle(text, new Rect(X((long)Position), LabelHeight, 2, h - LabelHeight));
    }
}
