using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>A small, still picture of a sound's waveform for a tile: just its peaks, mirrored, in one colour.</summary>
public sealed class MiniWave : Control
{
    public static readonly StyledProperty<float[]?> PeaksProperty = AvaloniaProperty.Register<MiniWave, float[]?>(nameof(Peaks));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<MiniWave, IBrush?>(nameof(Fill));

    static MiniWave() => AffectsRender<MiniWave>(PeaksProperty, FillProperty);

    public float[]? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (Peaks is not { Length: > 0 } peaks || w < 2 || h < 2) return;
        var brush = Fill ?? Brushes.Gray;
        // One bar every 3 pixels, each the loudest peak it covers.
        var bars = Math.Max(1, (int)(w / 3));
        var mid = h / 2;
        var max = Math.Max(0.05f, peaks.Max());
        using (context.PushOpacity(0.85))
            for (var i = 0; i < bars; i++)
            {
                var from = i * peaks.Length / bars;
                var to = Math.Max(from + 1, (i + 1) * peaks.Length / bars);
                var p = 0f;
                for (var j = from; j < to && j < peaks.Length; j++) p = Math.Max(p, peaks[j]);
                var half = Math.Max(0.5, p / max * mid);
                context.FillRectangle(brush, new Rect(i * 3, mid - half, 2, half * 2));
            }
    }
}
