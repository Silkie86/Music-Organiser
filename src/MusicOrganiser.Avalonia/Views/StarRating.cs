using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// Five stars to click for a song's rating. Hovering shows what a click would give; clicking the lit star again clears
/// the rating. Left and right arrows (or 0 to 5) change it from the keyboard.
/// </summary>
public sealed class StarRating : Control
{
    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<StarRating, int>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public int Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    const double StarSize = 18, Gap = 4;
    static readonly Geometry Star = Geometry.Parse("M12,2.5 L14.8,8.7 L21.5,9.4 L16.5,13.9 L17.9,20.6 L12,17.2 L6.1,20.6 L7.5,13.9 L2.5,9.4 L9.2,8.7 Z");
    int _hover;

    static StarRating()
    {
        AffectsRender<StarRating>(ValueProperty);
        FocusableProperty.OverrideDefaultValue<StarRating>(true);
        CursorProperty.OverrideDefaultValue<StarRating>(new Cursor(StandardCursorType.Hand));
    }

    public StarRating() => ToolTip.SetTip(this, "Your rating: click a star, or click the lit one again to clear it");

    protected override Size MeasureOverride(Size availableSize) => new(5 * StarSize + 4 * Gap, StarSize);

    static int StarAt(Point p) => Math.Clamp((int)(p.X / (StarSize + Gap)) + 1, 1, 5);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var star = StarAt(e.GetPosition(this));
        if (star == _hover) return;
        _hover = star;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = 0;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var star = StarAt(e.GetPosition(this));
        Value = star == Value ? 0 : star;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int? to = e.Key switch
        {
            Key.Left or Key.Down => Value - 1,
            Key.Right or Key.Up => Value + 1,
            >= Key.D0 and <= Key.D5 => e.Key - Key.D0,
            >= Key.NumPad0 and <= Key.NumPad5 => e.Key - Key.NumPad0,
            _ => null,
        };
        if (to is not { } v) return;
        Value = Math.Clamp(v, 0, 5);
        e.Handled = true;
    }

    IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var b) && b is IBrush brush ? brush : fallback;

    public override void Render(DrawingContext context)
    {
        var lit = Brush("WarningBrush", Brushes.Goldenrod);
        var faint = Brush("FaintBrush", Brushes.Gray);
        var shown = _hover > 0 ? _hover : Value;
        var outline = new Pen(faint, 1.4, lineJoin: PenLineJoin.Round);
        var scale = StarSize / 24;
        for (var i = 0; i < 5; i++)
        {
            using var _ = context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(i * (StarSize + Gap), 0));
            if (i < shown)
            {
                // A hover preview is drawn lighter than the rating itself.
                using var __ = context.PushOpacity(_hover > 0 && i >= Value ? 0.55 : 1);
                context.DrawGeometry(lit, null, Star);
            }
            else context.DrawGeometry(null, outline, Star);
        }
        // Somewhere to click between the stars too.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
    }
}
