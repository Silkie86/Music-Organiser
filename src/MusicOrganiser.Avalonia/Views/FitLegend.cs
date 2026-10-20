using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MusicOrganiser.Core.Midi.Jazz;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// The key to the piano roll's fit colours, strongest first, saying which notes each colour is for this kind of part: a
/// bassline's strongest are the root and 5th, any other part's the root, 3rd and 5th.
/// </summary>
public sealed class FitLegend : Control
{
    public static readonly StyledProperty<bool> BassProperty = AvaloniaProperty.Register<FitLegend, bool>(nameof(Bass));
    public bool Bass { get => GetValue(BassProperty); set => SetValue(BassProperty, value); }

    static FitLegend()
    {
        AffectsRender<FitLegend>(BassProperty);
        AffectsMeasure<FitLegend>(BassProperty);
    }

    const double Swatch = 10, Gap = 4, Space = 12;

    /// <summary>What each colour is for, strongest first.</summary>
    string[] Labels => Bass ? ["root, 5th", "3rd", "7th, 6th", "others", "fights"] : ["root, 3rd, 5th", "7th, 6th", "9, 11, 13", "others", "fights"];

    IBrush Ink => this.TryFindResource("MutedBrush", ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

    FormattedText Text(string s) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Ink);

    protected override Size MeasureOverride(Size availableSize) =>
        new(Labels.Sum(l => Swatch + Gap + Text(l).Width + Space) - Space, 16);

    public override void Render(DrawingContext dc)
    {
        var brushes = PianoRollEditor.FitBrushes(this);
        var x = 0.0;
        var labels = Labels;
        for (var i = 0; i < labels.Length; i++)
        {
            var strength = FitStrength.Strongest - i;
            dc.DrawRectangle(brushes[(int)strength], null, new RoundedRect(new Rect(x, (Bounds.Height - Swatch) / 2, Swatch, Swatch), 2));
            var text = Text(labels[i]);
            dc.DrawText(text, new Point(x + Swatch + Gap, (Bounds.Height - text.Height) / 2));
            x += Swatch + Gap + text.Width + Space;
        }
    }
}
