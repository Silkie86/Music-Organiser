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
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ViewModels.MainViewModel main) return;
            ScalePage(main);
            main.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ViewModels.MainViewModel.PageScale)) ScalePage(main); };
        };
    }

    /// <summary>A page sized on its own is scaled again inside the window (which Settings' size scales as a whole).</summary>
    void ScalePage(ViewModels.MainViewModel main) =>
        PageScaler.LayoutTransform = Math.Abs(main.PageScale - 1) < 0.001 ? null : new global::Avalonia.Media.ScaleTransform(main.PageScale, main.PageScale);
}
