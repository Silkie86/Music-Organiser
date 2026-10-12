using MusicOrganiser.App.ViewModels;
using Wpf.Ui.Controls;

namespace MusicOrganiser.App.Views;

public partial class PresetsWindow : FluentWindow
{
    public PresetsWindow(PresetsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => await vm.LoadAsync();
    }
}
