using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class PluginsView : UserControl, IShortcutPage
{
    public PluginsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as PluginsViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
            if (_vm is not null) ShowPanelWidth(_vm.PanelWidth);
        };
    }

    // The divider between the list and the picked plugin's panel: the panel keeps its width as the window changes size.
    ColumnDefinition PanelColumn => Body.ColumnDefinitions[2];

    void ShowPanelWidth(double width)
    {
        PanelColumn.Width = new GridLength(width);
        PanelColumn.MinWidth = 300;
        Body.ColumnDefinitions[0].MinWidth = 420;
    }

    void OnSplitterMoved(object? sender, VectorEventArgs e)
    {
        if (_vm is null) return;
        _vm.PanelWidth = PanelColumn.ActualWidth;
        ShowPanelWidth(_vm.PanelWidth);
    }

    PluginsViewModel? _vm;

    /// <summary>A reload swaps in a new list: keep the picked plugin in sight rather than jumping back to the top.</summary>
    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PluginsViewModel.View)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm?.Selected is { } picked) PluginGrid.ScrollIntoView(picked, null);
        }, DispatcherPriority.Background);
    }

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
