using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class ProjectsView : UserControl
{
    public ProjectsView()
    {
        InitializeComponent();
        // The grid takes Enter itself (to move down a row) before a KeyDown handler would see it, so
        // catch it on the way down: Enter opens the project, as in the Windows app.
        Grid.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Search.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
    }

    TopLevel? _top;

    // Finding an old project is what the app is opened for most, so the search box has the keyboard as soon as
    // the page shows (just start typing), and Ctrl+F (⌘F on a Mac) brings it back from anywhere in the window.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Dispatcher.UIThread.Post(() => Search.Focus(), DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _top?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key != Key.F || e.KeyModifiers != command) return;
        Search.Focus();
        Search.SelectAll();
        e.Handled = true;
    }

    // Down arrow moves into the list; Esc empties the search; Enter opens the selected project (or the top one).
    void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (Grid.SelectedItem is null) Grid.SelectedIndex = 0;
                Grid.Focus();
                e.Handled = true;
                break;
            case Key.Escape when !string.IsNullOrEmpty(Search.Text):
                Search.Text = "";
                e.Handled = true;
                break;
            case Key.Enter when Vm is { } vm:
                if (vm.Selected is null) Grid.SelectedIndex = 0;
                vm.OpenSelectedCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    ProjectsViewModel? Vm => DataContext as ProjectsViewModel;

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
