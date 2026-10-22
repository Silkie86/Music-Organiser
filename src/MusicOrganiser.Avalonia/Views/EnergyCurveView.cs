using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// The section's tension curve under its chord lane: a thin line, high where a bar plays with more energy (Solomon,
/// 2026-10-22: quiet blocks, no boxes), and dashed, the curve the section's feel asks for (a flat verse, a rising build, a full drop), so you can
/// see where it sits low or high.
/// </summary>
public sealed class EnergyCurveView : Control
{
    public static readonly StyledProperty<double[]?> ValuesProperty = AvaloniaProperty.Register<EnergyCurveView, double[]?>(nameof(Values));
    public static readonly StyledProperty<double[]?> TargetProperty = AvaloniaProperty.Register<EnergyCurveView, double[]?>(nameof(Target));
    /// <summary>Lines up with the piano roll's keyboard, like the chord lane.</summary>
    public static readonly StyledProperty<double> InsetProperty = AvaloniaProperty.Register<EnergyCurveView, double>(nameof(Inset));
    /// <summary>The part shown when the timeline under it is zoomed in, as shares of the whole (0 to 1: all of it).</summary>
    public static readonly StyledProperty<double> ViewFromProperty = AvaloniaProperty.Register<EnergyCurveView, double>(nameof(ViewFrom));
    public static readonly StyledProperty<double> ViewToProperty = AvaloniaProperty.Register<EnergyCurveView, double>(nameof(ViewTo), 1);

    static EnergyCurveView() => AffectsRender<EnergyCurveView>(ValuesProperty, TargetProperty, InsetProperty, ViewFromProperty, ViewToProperty);

    public double ViewFrom { get => GetValue(ViewFromProperty); set => SetValue(ViewFromProperty, value); }
    public double ViewTo { get => GetValue(ViewToProperty); set => SetValue(ViewToProperty, value); }

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
        double h = Bounds.Height, width = Math.Max(1, Bounds.Width - Inset);
        // Zoomed in, the whole curve is wider than the view and slid along to the part shown.
        double full = width / Math.Max(1e-6, ViewTo - ViewFrom), origin = Inset - ViewFrom * full, w = full / count;
        double Y(double v) => h - 2 - Math.Clamp(v, 0, 1) * (h - 4);
        var accent = Res("AccentBrush", Brushes.SeaGreen);
        using var clip = dc.PushClip(new Rect(Inset, 0, width, h));
        if (values is { Length: > 0 })
        {
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                for (var i = 0; i < values.Length; i++)
                {
                    var p = new Point(origin + (i + 0.5) * w, Y(values[i]));
                    if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
                }
                g.EndFigure(false);
            }
            dc.DrawGeometry(null, new Pen(accent, 1.3), line);
        }
        if (target is { Length: > 0 })
        {
            // Bars with no target (NaN: no label asking for a feel there) break the line.
            var path = new StreamGeometry();
            using (var g = path.Open())
            {
                var open = false;
                double step = full / target.Length;
                for (var i = 0; i < target.Length; i++)
                {
                    if (double.IsNaN(target[i]))
                    {
                        if (open) g.EndFigure(false);
                        open = false;
                        continue;
                    }
                    var y = Y(target[i]);
                    if (!open) { g.BeginFigure(new Point(origin + i * step + 2, y), false); open = true; }
                    g.LineTo(new Point(origin + (i + 0.5) * step, y));
                    if (i + 1 == target.Length || double.IsNaN(target[i + 1])) g.LineTo(new Point(origin + (i + 1) * step - 2, y));
                }
                if (open) g.EndFigure(false);
            }
            dc.DrawGeometry(null, new Pen(Res("MutedBrush", Brushes.Gray), 1.2, new DashStyle([4, 3], 0)), path);
        }
    }
}
