using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// The playground's chord track: each chord a flat block as wide as it lasts, its name written the way a lead sheet does
/// and its Roman numeral beside it where there's room, with a line under it for what it does (green: home, red: pulling
/// home, none: moving away). Solomon picked these "quiet blocks" on 2026-10-22. Hovering one says what it's doing ("tritone sub: slides down a
/// semitone to Cmaj9"). A playhead follows playback. Clicking a chord picks it for Reharm (<see cref="PickCommand"/>); the
/// idea being tried is drawn striped over the chords it changes (<see cref="Proposed"/>). Chords on the chord track (no MIDI
/// of their own, tinted) stretch by either edge and slide by the middle, on the grid unless Shift is held (Solomon,
/// 2026-10-22: "resize the chord clips in the chord track and everything updates on the fly"): each step goes to
/// <see cref="DragCommand"/> as it's dragged.
/// </summary>
public sealed class ChordLaneView : Control
{
    public static readonly StyledProperty<IReadOnlyList<ChordCell>?> ItemsProperty =
        AvaloniaProperty.Register<ChordLaneView, IReadOnlyList<ChordCell>?>(nameof(Items));
    public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<ChordLaneView, double>(nameof(Position), -1);
    public static readonly StyledProperty<long> LengthProperty = AvaloniaProperty.Register<ChordLaneView, long>(nameof(Length));
    /// <summary>The piano roll's keyboard is this wide, so the chords line up with the notes under them.</summary>
    public static readonly StyledProperty<double> InsetProperty = AvaloniaProperty.Register<ChordLaneView, double>(nameof(Inset));
    /// <summary>The part shown when the timeline under it is zoomed in, as shares of the whole (0 to 1: all of it).</summary>
    public static readonly StyledProperty<double> ViewFromProperty = AvaloniaProperty.Register<ChordLaneView, double>(nameof(ViewFrom));
    public static readonly StyledProperty<double> ViewToProperty = AvaloniaProperty.Register<ChordLaneView, double>(nameof(ViewTo), 1);

    /// <summary>Run with the <see cref="ChordCell"/> pressed on, by either button (a right-click picks it before its menu opens).</summary>
    public static readonly StyledProperty<ICommand?> PickCommandProperty = AvaloniaProperty.Register<ChordLaneView, ICommand?>(nameof(PickCommand));
    public static readonly StyledProperty<ChordCell?> PickedProperty = AvaloniaProperty.Register<ChordLaneView, ChordCell?>(nameof(Picked));
    public static readonly StyledProperty<IReadOnlyList<ChordCell>?> ProposedProperty =
        AvaloniaProperty.Register<ChordLaneView, IReadOnlyList<ChordCell>?>(nameof(Proposed));

    /// <summary>Run with a <see cref="ChordDrag"/> at each step of stretching or sliding a chord-track chord, the last one marked done.</summary>
    public static readonly StyledProperty<ICommand?> DragCommandProperty = AvaloniaProperty.Register<ChordLaneView, ICommand?>(nameof(DragCommand));
    /// <summary>The grid dragged chords land on, in ticks, while <see cref="SnapGrid"/> is on.</summary>
    public static readonly StyledProperty<long> GridProperty = AvaloniaProperty.Register<ChordLaneView, long>(nameof(Grid), 4 * 96);
    public static readonly StyledProperty<bool> SnapGridProperty = AvaloniaProperty.Register<ChordLaneView, bool>(nameof(SnapGrid), true);

    public ICommand? DragCommand { get => GetValue(DragCommandProperty); set => SetValue(DragCommandProperty, value); }
    public long Grid { get => GetValue(GridProperty); set => SetValue(GridProperty, value); }
    public bool SnapGrid { get => GetValue(SnapGridProperty); set => SetValue(SnapGridProperty, value); }

    static ChordLaneView() => AffectsRender<ChordLaneView>(ItemsProperty, PositionProperty, LengthProperty, InsetProperty, ViewFromProperty, ViewToProperty,
                                                           PickedProperty, ProposedProperty);

    public ICommand? PickCommand { get => GetValue(PickCommandProperty); set => SetValue(PickCommandProperty, value); }
    public ChordCell? Picked { get => GetValue(PickedProperty); set => SetValue(PickedProperty, value); }
    public IReadOnlyList<ChordCell>? Proposed { get => GetValue(ProposedProperty); set => SetValue(ProposedProperty, value); }

    public double ViewFrom { get => GetValue(ViewFromProperty); set => SetValue(ViewFromProperty, value); }
    public double ViewTo { get => GetValue(ViewToProperty); set => SetValue(ViewToProperty, value); }
    double Shown => Math.Max(1e-6, ViewTo - ViewFrom);
    /// <summary>Where a share of the whole sits across the lane.</summary>
    double At(double share) => Inset + (share - ViewFrom) / Shown * Math.Max(1, Bounds.Width - Inset);

    public IReadOnlyList<ChordCell>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public long Length { get => GetValue(LengthProperty); set => SetValue(LengthProperty, value); }
    public double Inset { get => GetValue(InsetProperty); set => SetValue(InsetProperty, value); }

    ChordCell? _hover;

    // Listened to only while on screen: the page's chord list outlives this view, and would keep every old copy alive.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ItemsProperty || VisualRoot is null) return;
        if (change.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= OnItemsChanged;
        if (change.NewValue is INotifyCollectionChanged n) n.CollectionChanged += OnItemsChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Items is INotifyCollectionChanged n) n.CollectionChanged += OnItemsChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Items is INotifyCollectionChanged n) n.CollectionChanged -= OnItemsChanged;
    }

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    IBrush Res(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    (double X, double W) Place(ChordCell c)
    {
        var width = Math.Max(1, Bounds.Width - Inset);
        return (At(c.Start), c.Share / Shown * width);
    }

    ChordCell? HitAt(double x) => x < Inset ? null : Items?.FirstOrDefault(c => { var (cx, w) = Place(c); return x >= cx && x < cx + w; });

    /// <summary>Which part of a chord-track chord the pointer is on: its left edge, its right edge or its middle.</summary>
    enum Grip { Left, Right, Middle }

    /// <summary>Within this many pixels of a chord-track chord's edge, dragging stretches it.</summary>
    const double EdgeWidth = 6;

    /// <summary>A chord-track chord being dragged: how it was, where the pointer went down, and what was last sent.</summary>
    sealed class Drag(ChordCell cell, Grip grip, double x, int gesture)
    {
        public ChordCell Cell { get; } = cell;
        public Grip Grip { get; } = grip;
        public double X { get; } = x;
        public int Gesture { get; } = gesture;
        public bool Moved { get; set; }
        public (long Start, long Length) Sent { get; set; } = (cell.Timed.Start, cell.Timed.Length);
    }

    static int _gestures;
    Drag? _drag;

    Grip? GripAt(ChordCell c, double x)
    {
        if (!c.IsSet || DragCommand is null) return null;
        var (cx, w) = Place(c);
        var edge = Math.Min(EdgeWidth, w / 3);
        return x < cx + edge ? Grip.Left : x >= cx + w - edge ? Grip.Right : Grip.Middle;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var x = e.GetPosition(this).X;
        if (HitAt(x) is not { } hit) return;
        var point = e.GetCurrentPoint(this).Properties;
        if (point.IsLeftButtonPressed && GripAt(hit, x) is { } grip)
        {
            // Picked on letting go, if it wasn't dragged: a drag only stretches or moves it.
            _drag = new Drag(hit, grip, x, ++_gestures);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (PickCommand is not { } pick || !pick.CanExecute(hit)) return;
        pick.Execute(hit);
        if (point.IsLeftButtonPressed) e.Handled = true;   // a right-click goes on to open the menu
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var x = e.GetPosition(this).X;
        if (_drag is { } drag)
        {
            if (!drag.Moved && Math.Abs(x - drag.X) < 3) return;
            drag.Moved = true;
            Send(drag, x, e.KeyModifiers.HasFlag(KeyModifiers.Shift), done: false);
            return;
        }
        var hit = HitAt(x);
        Cursor = hit is null || PickCommand is null ? null
            : GripAt(hit, x) is Grip.Left or Grip.Right ? new Cursor(StandardCursorType.SizeWestEast)
            : new Cursor(StandardCursorType.Hand);
        if (hit == _hover) return;
        _hover = hit;
        ToolTip.SetTip(this, hit is null ? null : PickCommand is null ? hit.Tip : $"{hit.Tip}\nClick for ways to change it");
        ToolTip.SetIsOpen(this, hit is not null);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is not { } drag) return;
        _drag = null;
        e.Pointer.Capture(null);
        if (drag.Moved) Send(drag, e.GetPosition(this).X, e.KeyModifiers.HasFlag(KeyModifiers.Shift), done: true);
        else if (PickCommand is { } pick && pick.CanExecute(drag.Cell)) pick.Execute(drag.Cell);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag is not { Moved: true } drag) { _drag = null; return; }
        _drag = null;
        DragCommand?.Execute(new ChordDrag(drag.Cell.Timed.Start, drag.Sent.Start, drag.Sent.Length, drag.Gesture, true));
    }

    /// <summary>Where the dragged chord would be with the pointer at <paramref name="x"/>, sent on when it's somewhere new (or the drag is over).</summary>
    void Send(Drag drag, double x, bool free, bool done)
    {
        var width = Math.Max(1, Bounds.Width - Inset);
        var ticks = (x - drag.X) / width * Shown * Length;
        var grid = Math.Max(1, Grid);
        long Snap(double t) => free || !SnapGrid ? (long)Math.Round(t) : (long)Math.Round(t / grid) * grid;
        var (start, end) = (drag.Cell.Timed.Start, drag.Cell.Timed.End);
        (long Start, long Length) to = drag.Grip switch
        {
            Grip.Left => (Snap(start + ticks), end - Snap(start + ticks)),
            Grip.Right => (start, Snap(end + ticks) - start),
            _ => (Snap(start + ticks), end - start),
        };
        if (to == drag.Sent && !done) return;
        drag.Sent = to;
        var move = new ChordDrag(start, to.Start, to.Length, drag.Gesture, done);
        if (DragCommand is { } c && c.CanExecute(move)) c.Execute(move);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hover = null;
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }

    /// <summary>
    /// A chord's name the way a lead sheet writes it, the quality small and raised beside the root, as long as it fits the
    /// chord's width: the whole name, then the shorthand (CΔ7 for Cmaj7, no bracketed tensions), then without the bass under
    /// the slash, then just "m" or ° after the root, then the root alone. Never cut off (Solomon, 2026-10-22: "Cmaj", "F#di" and "A13(" on a crowded lane).
    /// The Roman numeral goes beside it (under it on a tall lane) where there's room. Hovering still shows the whole name.
    /// </summary>
    static void DrawName(DrawingContext dc, ChordCell c, Rect rect, IBrush text, IBrush muted)
    {
        const double pad = 2.5;
        var room = rect.Width - 2 * pad;
        var face = new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.Medium);
        FormattedText Text(string s, double size, IBrush brush, Typeface? f = null) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, f ?? face, size, brush);
        var root = Text(c.RootName, 12, text);
        var bass = c.BassName.Length > 0 ? "/" + c.BassName : "";
        string[] tries = [c.Quality + bass, c.ShortQuality + bass, c.ShortQuality, c.LeastQuality, ""];
        FormattedText? quality = null;
        foreach (var q in tries.Distinct())
        {
            quality = q.Length > 0 ? Text(q, 8.5, text) : null;
            if (root.Width + (quality?.Width ?? 0) <= room) break;
        }
        var top = rect.Height >= 34 ? rect.Y + 3 : rect.Y + (rect.Height - root.Height) / 2;
        using (dc.PushClip(rect))
        {
            dc.DrawText(root, new Point(rect.X + pad, top));
            if (quality is not null) dc.DrawText(quality, new Point(rect.X + pad + root.Width + 0.5, top + 0.5));
            if (c.Roman.Length == 0) return;
            var roman = Text(c.Roman, 10.5, muted, Typeface.Default);
            var nameWidth = root.Width + (quality?.Width ?? 0);
            if (rect.Height >= 34) { if (roman.Width <= room) dc.DrawText(roman, new Point(rect.X + pad, rect.Bottom - roman.Height - 2)); }
            else if (nameWidth + 6 + roman.Width <= room) dc.DrawText(roman, new Point(rect.X + pad + nameWidth + 6, top + (root.Height - roman.Height) / 2 + 1));
        }
    }

    /// <summary>Whether the tried idea changes this chord.</summary>
    bool Covered(ChordCell c) => Proposed!.Any(p => p.Start < c.Start + c.Share - 1e-9 && p.Start + p.Share > c.Start + 1e-9);

    /// <summary>The idea being tried, striped in the accent colour where the chords would change, with what they'd be.</summary>
    void DrawProposed(DrawingContext dc, IReadOnlyList<ChordCell> tried, IBrush accent, IBrush text, IBrush muted, double h)
    {
        var colour = (accent as ISolidColorBrush)?.Color ?? Colors.White;
        var stripes = new SolidColorBrush(colour, 0.22);
        var fill = new SolidColorBrush(colour, 0.10);
        var dash = new Pen(accent, 1.5, new DashStyle([3, 2], 0));
        foreach (var c in tried)
        {
            var (x, w) = Place(c);
            var rect = new Rect(x + 0.5, 1, Math.Max(2, w - 1), h - 2);
            dc.DrawRectangle(fill, null, new RoundedRect(rect, 2));
            using (dc.PushClip(new RoundedRect(rect, 2)))
                for (var sx = rect.X - rect.Height; sx < rect.Right; sx += 9)
                    dc.DrawLine(new Pen(stripes, 3.5), new Point(sx, rect.Bottom), new Point(sx + rect.Height, rect.Y));
            dc.DrawRectangle(null, dash, new RoundedRect(rect, 2));
            DrawName(dc, c, rect, text, muted);
        }
    }

    public override void Render(DrawingContext dc)
    {
        var h = Bounds.Height;
        var surface = Res("SurfaceRaisedBrush", Brushes.Transparent);
        var text = Res("TextBrush", Brushes.White);
        var muted = Res("MutedBrush", Brushes.Gray);
        // An idea being tried still shows on a lane with no chords yet: Harmonise is mostly for that.
        var items = Items ?? [];
        if (items.Count == 0 && Proposed is not { Count: > 0 })
        {
            var empty = new FormattedText("No chords yet", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, muted);
            dc.DrawText(empty, new Point(Inset + 10, (h - empty.Height) / 2));
            return;
        }
        var accent = Res("AccentBrush", Brushes.White);
        // Chords on the chord track are tinted with the accent and have grips at their edges: they can be stretched and moved.
        var accentColour = (accent as ISolidColorBrush)?.Color ?? Colors.White;
        var setFill = new SolidColorBrush(accentColour, 0.14);
        var grip = new SolidColorBrush(accentColour, 0.55);
        using var clip = dc.PushClip(new Rect(Inset, 0, Math.Max(0, Bounds.Width - Inset), h));
        foreach (var c in items)
        {
            if (Proposed is { Count: > 0 } && Covered(c)) continue;
            var (x, w) = Place(c);
            var line = c.Kind switch
            {
                "T" => accent,
                "D" => Res("MissingBrush", Brushes.IndianRed),
                _ => null,
            };
            var rect = new Rect(x + 0.5, 1, Math.Max(2, w - 1), h - 2);
            var picked = Picked is { } p && p.Timed.Start == c.Timed.Start;
            var pen = picked ? new Pen(accent, 1.5) : c == _hover ? new Pen(muted, 1) : null;
            dc.DrawRectangle(surface, pen, new RoundedRect(rect, 2));
            if (c.IsSet)
            {
                dc.DrawRectangle(setFill, null, new RoundedRect(rect, 2));
                dc.FillRectangle(grip, new Rect(rect.X + 1.5, rect.Y + h / 4, 1.5, h / 2 - 2));
                dc.FillRectangle(grip, new Rect(rect.Right - 3, rect.Y + h / 4, 1.5, h / 2 - 2));
            }
            if (line is not null) dc.FillRectangle(line, new Rect(rect.X + 1, rect.Bottom - 2, Math.Max(1, rect.Width - 2), 2));
            DrawName(dc, c, rect, text, muted);
        }
        if (Proposed is { Count: > 0 } tried) DrawProposed(dc, tried, accent, text, muted, h);
        if (Position >= 0 && Length > 0)
        {
            var px = At(Position / Length);
            dc.FillRectangle(text, new Rect(px, 0, 2, h));
        }
    }
}
