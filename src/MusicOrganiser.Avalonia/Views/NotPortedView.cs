using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>Stands in for a screen that hasn't been moved over from the Windows app yet.</summary>
public sealed class NotPortedView : UserControl
{
    public static readonly StyledProperty<string> PageNameProperty =
        AvaloniaProperty.Register<NotPortedView, string>(nameof(PageName), "This screen");

    public string PageName
    {
        get => GetValue(PageNameProperty);
        set => SetValue(PageNameProperty, value);
    }

    public NotPortedView()
    {
        var title = new TextBlock { FontSize = 26, FontWeight = FontWeight.SemiBold };
        title.Bind(TextBlock.TextProperty, this.GetObservable(PageNameProperty));
        Content = new StackPanel
        {
            Children =
            {
                title,
                new TextBlock
                {
                    Text = "This screen hasn't been moved to the Mac and Linux version yet. It works in the Windows app in the meantime.",
                    Margin = new Thickness(0, 6, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#9A9DA7")),
                },
            },
        };
    }
}
