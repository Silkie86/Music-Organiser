using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MusicOrganiser.Core.Sharing;

namespace MusicOrganiser.App.Converters;

/// <summary>
/// Green for fine, amber for worth a look, red for won't load. With ConverterParameter=Soft,
/// the faint version for a pill's background.
/// </summary>
public sealed class ShareStatusToBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var soft = parameter is "Soft";
        if (value is null && soft) return Brushes.Transparent;
        var key = value switch
        {
            ShareStatus.Ok or Readiness.Ready => "OkBrush",
            ShareStatus.Unknown or ShareStatus.Note or Readiness.Check => "WarningBrush",
            null => "FaintBrush",
            _ => "MissingBrush",
        };
        if (Application.Current.TryFindResource(soft && key != "WarningBrush" ? key.Replace("Brush", "SoftBrush") : key) is not SolidColorBrush b)
            return Brushes.Gray;
        if (!soft || key != "WarningBrush") return b;
        // The palette has no soft amber; use the same 15% opacity as the other soft brushes.
        var faint = new SolidColorBrush(Color.FromArgb(0x26, b.Color.R, b.Color.G, b.Color.B));
        faint.Freeze();
        return faint;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A plugin's status or a project's readiness as a short pill label.</summary>
public sealed class ShareStatusLabel : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ShareStatus.Ok => "Loads",
        ShareStatus.Unknown => "Unsure",
        ShareStatus.Note => "Check",
        ShareStatus.WrongFormat => "Other format",
        ShareStatus.Missing => "Not installed",
        ShareStatus.WontLoad => "Won't load",
        Readiness.Ready => "Ready to share",
        Readiness.Check => "Worth a look",
        Readiness.Blocked => "Needs fixing",
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A friend column cell with no friend list yet.</summary>
public sealed class FriendText : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is string { Length: > 0 } s ? s : "Add their list to check";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
