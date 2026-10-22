using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class ProjectsView : UserControl, IShortcutPage
{
    public ProjectsView()
    {
        InitializeComponent();
        // The grid takes Enter itself (to move down a row) before a KeyDown handler would see it, so
        // catch it on the way down: Enter opens the project, as in the Windows app.
        Grid.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Search.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
    }

    // A right-click selects the song under the pointer first, so its menu is about that song.
    void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Grid).Properties.IsRightButtonPressed || Vm is not { } vm) return;
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is ProjectItem item && vm.Selected != item)
            vm.Selected = item;
    }

    // The right-click menu follows the selected song. A menu with nothing in it doesn't open, so it's filled as the
    // selection changes (a right-click selects first), not when it opens.
    ProjectsViewModel? _watched;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_watched is not null) _watched.PropertyChanged -= OnVmChanged;
        _watched = Vm;
        if (_watched is not null) _watched.PropertyChanged += OnVmChanged;
        FillRowMenu();
        if (Vm is { } vm) ShowPanelShare(vm.PanelShare);
    }

    ColumnDefinition ListColumn => Page.ColumnDefinitions[0];
    ColumnDefinition PanelColumn => Page.ColumnDefinitions[2];

    // The divider between the list and the song's panel: kept as a share of the width, so it suits any window size.
    void ShowPanelShare(double share)
    {
        ListColumn.Width = new GridLength(1 - share, GridUnitType.Star);
        PanelColumn.Width = new GridLength(share, GridUnitType.Star);
    }

    void OnSplitterMoved(object? sender, global::Avalonia.Input.VectorEventArgs e)
    {
        var (list, panel) = (ListColumn.ActualWidth, PanelColumn.ActualWidth);
        if (Vm is not { } vm || list + panel <= 0) return;
        vm.PanelShare = panel / (list + panel);
        ShowPanelShare(vm.PanelShare);
    }

    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectsViewModel.Detail)) FillRowMenu();
    }

    void FillRowMenu()
    {
        if (Grid.ContextFlyout is not MenuFlyout menu) return;
        if (Vm?.Detail is { } detail) OpenMenu.Fill(menu.Items, detail, row: true);
        else
        {
            menu.Items.Clear();
            menu.Items.Add(new MenuItem { Header = "Pick a song first", IsEnabled = false });
        }
    }

    // Finding an old project is what the app is opened for most, so the search box has the keyboard as soon as
    // the page shows (just start typing), and Ctrl+F (⌘F on a Mac) brings it back from anywhere in the window (the
    // app's shortcuts, as the box has the pageSearch class).
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => Search.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>The Projects page's keys (Settings › Shortcuts), for the song picked in the list.</summary>
    public bool RunShortcut(string id, bool repeat)
    {
        if (Vm?.Detail is not { } song) return false;
        var command = id switch
        {
            "projects.open" => song.OpenMainCommand,
            // Bitwig songs open in Bitwig; FL Studio songs are turned into one first
            "projects.openInBitwig" => song.Item.Daw.StartsWith("Bitwig") ? song.OpenInDawCommand : song.IsFlStudio ? song.OpenInBitwigCommand : null,
            "projects.fix" => song.CanFix ? song.PlanFixCommand : null,
            "projects.midi" => song.ShowMidiCommand,
            "projects.reveal" => song.RevealCommand,
            "projects.copyPath" => song.CopyPathCommand,
            _ => (System.Windows.Input.ICommand?)null,
        };
        if (command?.CanExecute(null) != true) return false;
        command.Execute(null);
        return true;
    }

    // Down arrow moves into the list; Esc empties the search; Enter opens the selected project (or the top one).
    void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (Grid.SelectedItem is null && HasRows) Grid.SelectedIndex = 0;
                Grid.Focus();
                e.Handled = true;
                break;
            case Key.Escape when !string.IsNullOrEmpty(Search.Text):
                Search.Text = "";
                e.Handled = true;
                break;
            case Key.Enter when Vm is { } vm:
                if (vm.Selected is null && HasRows) Grid.SelectedIndex = 0;
                vm.OpenSelectedCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    ProjectsViewModel? Vm => DataContext as ProjectsViewModel;

    void OnClearSearch(object? sender, RoutedEventArgs e)
    {
        Search.Text = "";
        Search.Focus();
    }

    bool HasRows => Vm?.View.GetEnumerator().MoveNext() == true;

    void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.IsOnRow()) Vm?.OpenSelectedCommand.Execute(null);
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Vm?.OpenSelectedCommand.Execute(null);
        e.Handled = true;
    }
}
