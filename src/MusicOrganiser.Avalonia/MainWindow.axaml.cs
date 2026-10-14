using Avalonia.Controls;

namespace MusicOrganiser.Avalonia;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Activated += (_, _) => (DataContext as ViewModels.MainViewModel)?.WindowActivated();
    }
}
