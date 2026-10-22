using Avalonia.Controls;

namespace MusicOrganiser.Avalonia;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Activated += (_, _) =>
        {
            Core.Parallelism.AppInBackground = false;
            (DataContext as ViewModels.MainViewModel)?.WindowActivated();
        };
        // Settings › Performance › Fast uses every core only while you're in another app or the window is minimised.
        Deactivated += (_, _) => Core.Parallelism.AppInBackground = true;
    }
}
