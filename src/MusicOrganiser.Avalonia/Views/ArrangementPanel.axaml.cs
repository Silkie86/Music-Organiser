using Avalonia.Controls;
using Avalonia.Interactivity;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class ArrangementPanel : UserControl
{
    public ArrangementPanel() => InitializeComponent();

    /// <summary>The same arrangement in a big window of its own, for a proper look.</summary>
    void OnFullSize(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectDetailViewModel d || d.Arrangement is null) return;
        var window = new Window
        {
            Title = $"{d.Item.Name} · arrangement",
            Width = 1280,
            Height = 760,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            DataContext = d,
            Content = new ArrangementPanel { Margin = new global::Avalonia.Thickness(8) },
        };
        ((ArrangementPanel)window.Content).FullSizeHolder.IsVisible = false;
        if (this.TryFindResource("AppBackgroundBrush", out var background) && background is global::Avalonia.Media.IBrush brush) window.Background = brush;
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else window.Show();
    }
}
