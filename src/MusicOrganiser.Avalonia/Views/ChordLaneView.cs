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
/// The playground's chord track: each chord as wide as it lasts, its name large and its Roman numeral under it, tinted
/// by what it does (home, moving away, pulling home). Hovering one says what it's doing ("tritone sub: slides down a
/// semitone to Cmaj9"). A playhead follows playback. Clicking a chord picks it for Reharm (<see cref="PickCommand"/>); the
/// idea being tried is drawn striped over the chords it changes (<see cref="Proposed"/>).
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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (HitAt(e.GetPosition(this).X) is not { } hit || PickCommand is not { } pick || !pick.CanExecute(hit)) return;
        pick.Execute(hit);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) e.Handled = true;   // a right-click goes on to open the menu
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var hit = HitAt(e.GetPosition(this).X);
        if (hit == _hover) return;
        _hover = hit;
        Cursor = hit is null || PickCommand is null ? null : new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, hit is null ? null : PickCommand is null ? hit.Tip : $"{hit.Tip}\nClick for ways to change it");
        ToolTip.SetIsOpen(this, hit is not null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hover = null;
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
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
            var rect = new Rect(x + 1.5, 2, Math.Max(2, w - 3), h - 4);
            dc.DrawRectangle(fill, null, new RoundedRect(rect, 5));
            using (dc.PushClip(new RoundedRect(rect, 5)))
                for (var sx = rect.X - rect.Height; sx < rect.Right; sx += 9)
                    dc.DrawLine(new Pen(stripes, 3.5), new Point(sx, rect.Bottom), new Point(sx + rect.Height, rect.Y));
            dc.DrawRectangle(null, dash, new RoundedRect(rect, 5));
            using (dc.PushClip(rect))
            {
                var name = new FormattedText(c.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                             new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.Bold), 14, text);
                dc.DrawText(name, new Point(x + 7, 4));
                if (c.Roman.Length > 0)
                {
                    var roman = new FormattedText(c.Roman, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, muted);
                    dc.DrawText(roman, new Point(x + 7, h - roman.Height - 4));
                }
            }
        }
    }

    public override void Render(DrawingContext dc)
    {
        var h = Bounds.Height;
        var surface = Res("SurfaceRaisedBrush", Brushes.Transparent);
        dc.DrawRectangle(surface, null, new RoundedRect(new Rect(Inset, 0, Math.Max(0, Bounds.Width - Inset), h), 6));
        var text = Res("TextBrush", Brushes.White);
        var muted = Res("MutedBrush", Brushes.Gray);
        if (Items is not { Count: > 0 } items)
        {
            var empty = new FormattedText("No chords yet", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, muted);
            dc.DrawText(empty, new Point(Inset + 10, (h - empty.Height) / 2));
            return;
        }
        // Bar lines under the chords.
        if (Length > 0)
        {
            var bars = (int)Math.Ceiling(Length / (4.0 * 96));
            var line = Res("StrokeBrush", Brushes.DimGray);
            for (var b = 1; b < bars; b++)
            {
                var bx = At(b / (double)bars);
                if (bx > Inset && bx < Bounds.Width) dc.FillRectangle(line, new Rect(bx, 0, 1, h));
            }
        }
        var accent = Res("AccentBrush", Brushes.White);
        using var clip = dc.PushClip(new Rect(Inset, 0, Math.Max(0, Bounds.Width - Inset), h));
        foreach (var c in items)
        {
            if (Proposed is { Count: > 0 } && Covered(c)) continue;
            var (x, w) = Place(c);
            var tint = c.Kind switch
            {
                "T" => Res("AccentSoftBrush", Brushes.Transparent),
                "D" => Res("MissingSoftBrush", Brushes.Transparent),
                "S" => Res("AccentFaintBrush", Brushes.Transparent),
                _ => Brushes.Transparent,
            };
            var rect = new Rect(x + 1.5, 2, Math.Max(2, w - 3), h - 4);
            var picked = Picked is { } p && p.Timed.Start == c.Timed.Start;
            var pen = picked ? new Pen(accent, 2) : c == _hover ? new Pen(accent, 1.2) : null;
            if (Proposed is { Count: > 0 } && Covered(c)) { tint = Brushes.Transparent; pen = null; }   // the tried idea is drawn over it
            dc.DrawRectangle(tint, pen, new RoundedRect(rect, 5));
            using (dc.PushClip(rect))
            {
                var name = new FormattedText(c.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                             new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.SemiBold), 14, text);
                dc.DrawText(name, new Point(x + 7, 4));
                if (c.Roman.Length > 0)
                {
                    var roman = new FormattedText(c.Roman, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, muted);
                    dc.DrawText(roman, new Point(x + 7, h - roman.Height - 4));
                }
            }
        }
        if (Proposed is { Count: > 0 } tried) DrawProposed(dc, tried, accent, text, muted, h);
        if (Position >= 0 && Length > 0)
        {
            var px = At(Position / Length);
            dc.FillRectangle(text, new Rect(px, 0, 2, h));
        }
    }
}
