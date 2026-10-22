using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

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
        KeySheet.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) OnSheetShown(KeySheet.IsVisible); };
    }

    /// <summary>The keys that work on every page, and each page's own (Settings › Shortcuts).</summary>
    public Views.AppShortcuts Shortcuts { get; }

    IInputElement? _beforeSheet;

    /// <summary>The keyboard goes onto the keys sheet while it's open (Tab goes round its keys), then back where it was.</summary>
    void OnSheetShown(bool shown)
    {
        if (shown)
        {
            _beforeSheet = FocusManager?.GetFocusedElement();
            Dispatcher.UIThread.Post(() => SheetClose.Focus());
        }
        else if (_beforeSheet is Control { IsEffectivelyVisible: true } was) was.Focus();
        if (!shown) _beforeSheet = null;
    }

    void OnSheetBackdrop(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel main) main.Shortcuts.SheetOpen = false;
    }

    void OnSheetCard(object? sender, PointerPressedEventArgs e) => e.Handled = true;   // a click on the sheet itself leaves it open
}
