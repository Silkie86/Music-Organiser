using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// The section's tension curve under its chord lane: a soft bar per bar of music, as tall as its energy, a line
/// through them, and dashed, the curve the section's feel asks for (a flat verse, a rising build, a full drop), so you can
/// see where it sits low or high.
/// </summary>
public sealed class EnergyCurveView : Control
{
    public static readonly StyledProperty<double[]?> ValuesProperty = AvaloniaProperty.Register<EnergyCurveView, double[]?>(nameof(Values));
    public static readonly StyledProperty<double[]?> TargetProperty = AvaloniaProperty.Register<EnergyCurveView, double[]?>(nameof(Target));
    /// <summary>Lines up with the piano roll's keyboard, like the chord lane.</summary>
    public static readonly StyledProperty<double> InsetProperty = AvaloniaProperty.Register<EnergyCurveView, double>(nameof(Inset));

    static EnergyCurveView() => AffectsRender<EnergyCurveView>(ValuesProperty, TargetProperty, InsetProperty);

    public double[]? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double[]? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    public double Inset { get => GetValue(InsetProperty); set => SetValue(InsetProperty, value); }

    IBrush Res(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    public override void Render(DrawingContext dc)
    {
        var values = Values;
        var target = Target;
        var count = Math.Max(values?.Length ?? 0, target?.Length ?? 0);
        if (count == 0) return;
        double h = Bounds.Height, width = Math.Max(1, Bounds.Width - Inset), w = width / count;
        double Y(double v) => h - 2 - Math.Clamp(v, 0, 1) * (h - 4);
        dc.DrawRectangle(Res("SurfaceRaisedBrush", Brushes.Transparent), null, new RoundedRect(new Rect(Inset, 0, width, h), 6));
        var accent = Res("AccentBrush", Brushes.SeaGreen);
        var soft = Res("AccentSoftBrush", Brushes.Transparent);
        if (values is { Length: > 0 })
        {
            for (var i = 0; i < values.Length; i++)
            {
                var top = Y(values[i]);
                dc.DrawRectangle(soft, null, new RoundedRect(new Rect(Inset + i * w + 2, top, Math.Max(1, w - 4), h - top), 3));
            }
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                for (var i = 0; i < values.Length; i++)
                {
                    var p = new Point(Inset + (i + 0.5) * w, Y(values[i]));
                    if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
                }
                g.EndFigure(false);
            }
            dc.DrawGeometry(null, new Pen(accent, 1.6), line);
        }
        if (target is { Length: > 0 })
        {
            // Bars with no target (NaN: no label asking for a feel there) break the line.
            var path = new StreamGeometry();
            using (var g = path.Open())
            {
                var open = false;
                double step = width / target.Length;
                for (var i = 0; i < target.Length; i++)
                {
                    if (double.IsNaN(target[i]))
                    {
                        if (open) g.EndFigure(false);
                        open = false;
                        continue;
                    }
                    var y = Y(target[i]);
                    if (!open) { g.BeginFigure(new Point(Inset + i * step + 2, y), false); open = true; }
                    g.LineTo(new Point(Inset + (i + 0.5) * step, y));
                    if (i + 1 == target.Length || double.IsNaN(target[i + 1])) g.LineTo(new Point(Inset + (i + 1) * step - 2, y));
                }
                if (open) g.EndFigure(false);
            }
            dc.DrawGeometry(null, new Pen(Res("MutedBrush", Brushes.Gray), 1.2, new DashStyle([4, 3], 0)), path);
        }
    }
}
