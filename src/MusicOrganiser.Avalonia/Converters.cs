using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia;

/// <summary>Each DAW's colour; "Soft" as the parameter gives the faint version for a label's background.</summary>
public sealed class DawBrush : IValueConverter
{
    static readonly Dictionary<string, Color> Colours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bitwig Studio"] = Color.FromRgb(0xF2, 0x99, 0x4A),
        ["FL Studio"] = Color.FromRgb(0x5E, 0xA8, 0xFF),
        ["Studio One"] = Color.FromRgb(0x4A, 0xCB, 0x8B),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var known = Colours.TryGetValue(value as string ?? "", out var found);
        var c = known ? found : Color.FromRgb(0x67, 0x6A, 0x74);
        // "Light": a DAW light's fill, lit in the DAW's colour, hollow when it isn't answering.
        if (parameter as string == "Light") return known ? new ImmutableSolidColorBrush(c) : Brushes.Transparent;
        if (parameter as string == "Soft") c = Color.FromArgb(0x26, c.R, c.G, c.B);
        return new ImmutableSolidColorBrush(c);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>The short label under a rail icon: "Sound Lab" fits as "Sounds".</summary>
public sealed class NavLabel : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value as string == "Sound Lab" ? "Sounds" : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>Settings docks at the bottom of the rail; every other place stacks from the top.</summary>
public sealed class NavDock : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is NavItem { Title: "Settings" } ? Dock.Bottom : Dock.Top;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>The DAW chips: "Any" reads "All DAWs".</summary>
public sealed class DawChipText : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value as string == ProjectsViewModel.Any ? "All DAWs" : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

public sealed class NotAny : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value as string != ProjectsViewModel.Any;
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
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is double s ? Format(s) : "";

    // Rounded to what's shown before it's split up, so 119.6 s is "2:00", not "1:60", and 59.96 s is "1:00", not "60s".
    static string Format(double s)
    {
        if (Math.Round(s, 1, MidpointRounding.AwayFromZero) is var tenths and < 10) return $"{tenths:0.0}s";
        var whole = (int)Math.Round(s, MidpointRounding.AwayFromZero);
        return whole < 60 ? $"{whole}s" : $"{whole / 60}:{whole % 60:00}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>True for zero, e.g. to show an "empty list" hint.</summary>
public sealed class Zero : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is 0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>A sidebar page's icon: the NavIcon.&lt;title without spaces&gt; geometry in App.axaml.</summary>
public sealed class NavIcon : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        global::Avalonia.Application.Current?.TryGetResource("NavIcon." + (value as string ?? "").Replace(" ", ""), null, out var icon) == true
            ? icon
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>A percentage as the sweep of a progress ring, in degrees.</summary>
public sealed class PercentAngle : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? Math.Clamp(d, 0, 100) * 3.6 : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>A page's own size against the rest of the window (<see cref="NavItem.Scale"/>) as its scale; 1 draws it as is.</summary>
public sealed class PageScaleTransform : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d && Math.Abs(d - 1) >= 0.001 ? new ScaleTransform(d, d) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>"#RRGGBB" as a brush, for previews of a theme's colours; an empty string draws nothing.</summary>
public sealed class HexBrush : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } hex && Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>An outline only for the "Theme's own" swatch, which has no colour of its own to fill it.</summary>
public sealed class HexStroke : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string { Length: > 0 } ? 0.0 : 1.5;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>A number as even corners.</summary>
public sealed class Radius : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new global::Avalonia.CornerRadius(value is double d ? d : 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>A share (0 to 1) as a width: the parameter is the full width, e.g. a liked bar's track.</summary>
public sealed class Scale : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d && double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var full)
            ? Math.Clamp(d, 0, 1) * full : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>
/// A colour per kind of sound, so a page of tiles reads at a glance: drums warm, tonal sounds cool, the rest muted.
/// Types not listed get a steady colour from their name.
/// </summary>
public sealed class TypeBrush : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new ImmutableSolidColorBrush(Color.FromUInt32(StarColours.Type(value as string)));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
