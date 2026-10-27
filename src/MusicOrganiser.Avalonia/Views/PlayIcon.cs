using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// The symbol on every play button in the app: a triangle, or a square to stop it while it plays (Solomon, 2026-10-27:
/// "dont like blue play button make it consitant in th app"). A typed ▶ comes out as a blue emoji on Windows and at each
/// page's own size, so they're all drawn the one way, as the Playground's transport is, in the colour of the button's words:
/// dark on the accent, the accent when it's on, faint when it can't be used.
/// </summary>
public sealed class PlayIcon : Control
{
    public static readonly StyledProperty<bool> IsPlayingProperty = AvaloniaProperty.Register<PlayIcon, bool>(nameof(IsPlaying));
    /// <summary>It's playing: the square, to stop it.</summary>
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }

    static PlayIcon() => AffectsRender<PlayIcon>(IsPlayingProperty, TextElement.ForegroundProperty);

    const double Side = 12;
    static readonly Geometry Play = Geometry.Parse("M2,0.5 L11,6 L2,11.5 Z"), Stop = Geometry.Parse("M1.5,1.5 H10.5 V10.5 H1.5 Z");

    public PlayIcon()
    {
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Side, Side);

    public override void Render(DrawingContext dc)
    {
        var scale = Math.Min(Bounds.Width, Bounds.Height) / Side;
        var at = Matrix.CreateTranslation((Bounds.Width - Side * scale) / 2, (Bounds.Height - Side * scale) / 2);
        using (dc.PushTransform(Matrix.CreateScale(scale, scale) * at))
            dc.DrawGeometry(TextElement.GetForeground(this), null, IsPlaying ? Stop : Play);
    }
}
