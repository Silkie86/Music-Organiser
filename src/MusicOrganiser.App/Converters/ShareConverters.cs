using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MusicOrganiser.Core.Sharing;

namespace MusicOrganiser.App.Converters;

/// <summary>Green for fine, amber for worth a look, red for won't load.</summary>
public sealed class ShareStatusToBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ShareStatus.Ok or Readiness.Ready => "OkBrush",
            ShareStatus.Unknown or ShareStatus.Note or Readiness.Check => "WarningBrush",
            null => "",
            _ => "MissingBrush",
        };
        return key.Length > 0 && Application.Current.TryFindResource(key) is Brush b ? b : Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A friend column cell with no friend list yet.</summary>
public sealed class FriendText : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is string { Length: > 0 } s ? s : "Add their list to check";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
