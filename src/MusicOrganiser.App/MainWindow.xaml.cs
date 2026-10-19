using Wpf.Ui.Controls;

namespace MusicOrganiser.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Activated += (_, _) => (DataContext as ViewModels.MainViewModel)?.WindowActivated();
    }
}
