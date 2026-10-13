using Avalonia.Controls;
using Avalonia.Input;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class ProjectsView : UserControl
{
    public ProjectsView() => InitializeComponent();

    ProjectsViewModel? Vm => DataContext as ProjectsViewModel;

    void OnDoubleTapped(object? sender, TappedEventArgs e) => Vm?.OpenSelectedCommand.Execute(null);

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Vm?.OpenSelectedCommand.Execute(null);
        e.Handled = true;
    }
}
