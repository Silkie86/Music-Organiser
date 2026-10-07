using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MusicOrganiser.App.Converters;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

public sealed class NullToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        (value is null || value is string { Length: 0 }) ^ Invert ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ZeroToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n != 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class Stars : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n > 0 ? new string('★', Math.Min(n, 5)) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class FileSize : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        long b when b >= 1 << 20 => $"{b / 1048576.0:0.0} MB",
        long b => $"{Math.Max(1, b / 1024)} KB",
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToStar : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? "★" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class RatingText : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n > 0 ? new string('★', n) + new string('☆', 5 - n) : "No rating";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Draws a sample's peak levels (0..1) as a mirrored waveform, 100 units high.</summary>
public sealed class WaveformGeometry : IValueConverter
{
    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not float[] { Length: > 0 } peaks) return null;
        var g = new System.Windows.Media.StreamGeometry();
        using (var ctx = g.Open())
        {
            for (var i = 0; i < peaks.Length; i++)
            {
                var h = Math.Max(0.5, peaks[i] * 50);
                ctx.BeginFigure(new Point(i * 2 + 1, 50 - h), false, false);
                ctx.LineTo(new Point(i * 2 + 1, 50 + h), true, false);
            }
        }
        g.Freeze();
        return g;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Sample length: "0.4s", "12s", "1:05".</summary>
public sealed class Seconds : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double s ? s >= 60 ? $"{(int)s / 60}:{s % 60:00}" : s >= 10 ? $"{s:0}s" : $"{s:0.0}s" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Index → bool for a group of radio buttons bound to one int.</summary>
public sealed class IndexToBool : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && int.TryParse(parameter as string, out var p) && i == p;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && int.TryParse(parameter as string, out var p) ? p : Binding.DoNothing;
}
