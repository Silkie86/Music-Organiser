using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace MusicOrganiser.Avalonia;

/// <summary>Each DAW's colour; "Soft" as the parameter gives the faint version for a label's background.</summary>
public sealed class DawBrush : IValueConverter
{
    static readonly Dictionary<string, Color> Colours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bitwig Studio"] = Color.FromRgb(0xFF, 0x8A, 0x4C),
        ["FL Studio"] = Color.FromRgb(0x9C, 0xD0, 0x6A),
        ["Studio One"] = Color.FromRgb(0x5A, 0xAE, 0xFF),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var c = Colours.TryGetValue(value as string ?? "", out var found) ? found : Color.FromRgb(0x9A, 0x9D, 0xA7);
        if (parameter as string == "Soft") c = Color.FromArgb(0x26, c.R, c.G, c.B);
        return new ImmutableSolidColorBrush(c);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

public sealed class BoolToStar : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "★" : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

public sealed class Stars : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n and > 0 ? new string('★', Math.Min(n, 5)) : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>True for a number above zero, e.g. to show something only when there's a count.</summary>
public sealed class NonZero : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int n && n > 0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>A rating as stars for a picker: "Unrated" for 0.</summary>
public sealed class RatingText : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n and > 0 ? new string('★', n) + new string('☆', 5 - n) : "No rating";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>The grid-ready view behind a view model's list view.</summary>
public sealed class ItemsViewSource : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AvaloniaItemsView v ? v.Inner : value;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>Index → bool for a group of radio buttons bound to one int.</summary>
public sealed class IndexToBool : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && int.TryParse(parameter as string, out var p) && i == p;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && int.TryParse(parameter as string, out var p) ? p : BindingOperations.DoNothing;
}

/// <summary>A length in seconds, short: "0.4s", "12s", "1:05".</summary>
public sealed class Seconds : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double s ? s >= 60 ? $"{(int)s / 60}:{s % 60:00}" : s >= 10 ? $"{s:0}s" : $"{s:0.0}s" : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>True for zero, e.g. to show an "empty list" hint.</summary>
public sealed class Zero : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is 0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
