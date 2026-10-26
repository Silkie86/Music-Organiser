using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// A thin bar split into coloured parts by their share of the whole: where a plugin's or a song's room on disk goes.
/// Pressed into the surface like the app's other fields.
/// </summary>
public sealed class ShareBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<SpacePart>?> PartsProperty =
        AvaloniaProperty.Register<ShareBar, IReadOnlyList<SpacePart>?>(nameof(Parts));

    public IReadOnlyList<SpacePart>? Parts
    {
        get => GetValue(PartsProperty);
        set => SetValue(PartsProperty, value);
    }

    static ShareBar() => AffectsRender<ShareBar>(PartsProperty);

    public ShareBar() => Height = 10;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var track = this.TryFindResource("SurfaceBrush", ActualThemeVariant, out var t) && t is IBrush b ? b : Brushes.Black;
        var edge = this.TryFindResource("StrokeBrush", ActualThemeVariant, out var s) && s is IBrush e ? e : Brushes.Gray;
        context.DrawRectangle(track, new Pen(edge, 1), bounds.Deflate(0.5), 2, 2);
        if (Parts is not { Count: > 0 } parts) return;
        using (context.PushClip(new RoundedRect(bounds.Deflate(1), 2)))
        {
            var x = 1.0;
            var width = bounds.Width - 2;
            foreach (var p in parts)
            {
                var w = Math.Max(p.Share > 0 ? 2 : 0, p.Share * width);
                if (w <= 0) continue;
                var colour = Color.TryParse(p.Colour, out var c) ? c : Colors.Gray;
                context.FillRectangle(new SolidColorBrush(colour), new Rect(x, 1, Math.Min(w, bounds.Width - 1 - x), bounds.Height - 2));
                x += w;
                if (x >= bounds.Width - 1) break;
            }
        }
    }
}
