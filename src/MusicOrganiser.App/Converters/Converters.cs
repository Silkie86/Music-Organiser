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
