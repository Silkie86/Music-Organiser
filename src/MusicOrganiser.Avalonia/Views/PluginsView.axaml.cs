using Avalonia.Controls;
using Avalonia.Input;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class PluginsView : UserControl
{
    public PluginsView() => InitializeComponent();

    void OnDoubleTapped(object? sender, TappedEventArgs e) => (DataContext as PluginsViewModel)?.ShowProjectsCommand.Execute(null);
}
