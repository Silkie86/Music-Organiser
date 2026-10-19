using Avalonia.Controls;
using Avalonia.Input;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class PluginsView : UserControl
{
    public PluginsView() => InitializeComponent();

    void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.IsOnRow()) (DataContext as PluginsViewModel)?.ShowProjectsCommand.Execute(null);
    }

    void OnNeededPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is NeededPlugin n) n.PickCommand.Execute(null);
    }
}
