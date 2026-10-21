using Avalonia.Controls;
using Avalonia.Input;

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
        Shortcuts = new Views.AppShortcuts(this, PageHost);
    }

    /// <summary>The keys that work on every page, and each page's own (Settings › Shortcuts).</summary>
    public Views.AppShortcuts Shortcuts { get; }

    void OnSheetBackdrop(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel main) main.Shortcuts.SheetOpen = false;
    }

    void OnSheetCard(object? sender, PointerPressedEventArgs e) => e.Handled = true;   // a click on the sheet itself leaves it open
}
