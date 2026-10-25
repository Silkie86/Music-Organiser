using Avalonia.Controls;
using Avalonia.Input;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class PluginsView : UserControl, IShortcutPage
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

    void OnCandidatePressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SpaceCandidate c) c.PickCommand.Execute(null);
    }

    /// <summary>The Plugins page's keys (Settings › Shortcuts), for the plugin picked in the list.</summary>
    public bool RunShortcut(string id, bool repeat)
    {
        if (DataContext is not PluginsViewModel { Selected: not null } vm) return false;
        switch (id)
        {
            case "plugins.projects": vm.ShowProjectsCommand.Execute(null); return true;
            case "plugins.searchOnline": vm.SearchOnlineCommand.Execute(null); return true;
            case "plugins.cleanUp": vm.CleanUpCommand.Execute(null); return true;
            case "plugins.uninstall": vm.UninstallCommand.Execute(null); return true;
        }
        return false;
    }
}
