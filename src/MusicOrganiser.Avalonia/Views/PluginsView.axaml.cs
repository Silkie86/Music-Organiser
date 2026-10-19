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
}
