using Avalonia;
using Avalonia.Controls;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// A toolbar of button groups: the first group on the left, the rest pushed to the right and split by thin lines.
/// When they don't all fit on one line, the right-hand groups wrap onto the lines below (still on the right), so
/// nothing is ever cut off.
/// </summary>
/// <remarks>
/// A group that follows another on its line gets the class <c>divided</c>; the page draws the line (a left border).
/// </remarks>
public sealed class ToolbarPanel : Panel
{
    /// <summary>Space between groups, before the dividing line.</summary>
    public double GroupGap { get; set; } = 10;
    public double LineGap { get; set; } = 8;

    IEnumerable<Control> Shown => Children.Where(c => c.IsVisible);

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var c in Children) c.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var lines = Lay(availableSize.Width);
        var width = double.IsInfinity(availableSize.Width) ? lines.Max(l => l.Width) : availableSize.Width;
        return new Size(width, lines.Sum(l => l.Height) + LineGap * Math.Max(0, lines.Count - 1));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var lines = Lay(finalSize.Width);
        var y = 0.0;
        foreach (var (i, line) in lines.Index())
        {
            // Everything but the first group sits at the right edge, on the lines it wraps onto too.
            var rest = i == 0 ? line.Width - line.Items[0].DesiredSize.Width - GroupGap : line.Width;
            var x = double.IsInfinity(finalSize.Width) ? 0 : finalSize.Width - rest;
            foreach (var (j, c) in line.Items.Index())
            {
                c.Classes.Set("divided", j > 0);
                if (i == 0 && j == 0) { c.Arrange(new Rect(0, y, c.DesiredSize.Width, line.Height)); continue; }
                c.Arrange(new Rect(x, y, c.DesiredSize.Width, line.Height));
                x += c.DesiredSize.Width + GroupGap;
            }
            y += line.Height + LineGap;
        }
        return finalSize;
    }

    sealed record Line(List<Control> Items)
    {
        public double Width { get; set; }
        public double Height { get; set; }
    }

    List<Line> Lay(double width)
    {
        var lines = new List<Line>();
        Line? line = null;
        foreach (var c in Shown)
        {
            var w = c.DesiredSize.Width;
            if (line is null || line.Width + GroupGap + w > width + 0.5)
            {
                line = new Line([]) { Width = -GroupGap };
                lines.Add(line);
            }
            line.Items.Add(c);
            line.Width += GroupGap + w;
            line.Height = Math.Max(line.Height, c.DesiredSize.Height);
        }
        if (lines.Count == 0) lines.Add(new Line([]));
        return lines;
    }
}
