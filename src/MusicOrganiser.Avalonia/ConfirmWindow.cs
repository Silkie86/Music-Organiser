using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MusicOrganiser.Avalonia;

/// <summary>A small OK / Cancel question, since Avalonia has no message box of its own.</summary>
sealed class ConfirmWindow : Window
{
    public ConfirmWindow(string message, string title, bool okOnly = false)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Classes = { "accent" } };
        ok.Click += (_, _) => Close(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (!okOnly)
        {
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
            cancel.Click += (_, _) => Close(false);
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(ok);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, buttons },
        };
    }
}
