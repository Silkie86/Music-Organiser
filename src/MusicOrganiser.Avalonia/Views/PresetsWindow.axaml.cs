using Avalonia.Controls;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class PresetsWindow : Window
{
    public PresetsWindow() => InitializeComponent();

    public PresetsWindow(PresetsViewModel vm) : this()
    {
        DataContext = vm;
        Opened += async (_, _) => await vm.LoadAsync();
    }
}
