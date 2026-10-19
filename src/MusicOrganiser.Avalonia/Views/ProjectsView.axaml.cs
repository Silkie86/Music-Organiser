using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
