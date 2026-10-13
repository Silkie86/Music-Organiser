using Avalonia.Controls;
using Avalonia.Input;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class SamplesView : UserControl
{
    public SamplesView() => InitializeComponent();

    void OnDoubleTapped(object? sender, TappedEventArgs e) => (DataContext as SamplesViewModel)?.ShowProjectsCommand.Execute(null);
}
