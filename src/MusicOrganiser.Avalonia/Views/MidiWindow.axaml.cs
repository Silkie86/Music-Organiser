using Avalonia.Controls;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class MidiWindow : Window
{
    public MidiWindow() => InitializeComponent();

    public MidiWindow(MidiPreviewViewModel vm) : this()
    {
        DataContext = vm;
        Opened += async (_, _) => await vm.LoadAsync();
        Closed += (_, _) => vm.Dispose();
    }
}
