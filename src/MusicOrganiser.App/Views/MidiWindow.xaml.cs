using MusicOrganiser.App.ViewModels;
using Wpf.Ui.Controls;

namespace MusicOrganiser.App.Views;

public partial class MidiWindow : FluentWindow
{
    public MidiWindow(MidiPreviewViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => await vm.LoadAsync();
    }
}
