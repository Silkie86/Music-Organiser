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
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) OnWindowState(); };
    }

    // Full screen hides the window's own buttons, and F11 sits beside F12, so it's easy to land in by accident
    // (Solomon, 2026-10-23: "i can't see how to get it out of this mode"): say how to leave, and keep a way out in the sidebar.
    void OnWindowState()
    {
        if (DataContext is not ViewModels.MainViewModel main) return;
        var full = WindowState == WindowState.FullScreen;
        if (full == main.IsFullScreen) return;
        main.IsFullScreen = full;
        if (!full) return;
        var key = ViewModels.ShortcutsViewModel.Display(main.Shortcuts.KeyOf("app.fullScreen"));
        main.Activities.Done("Full screen", key.Length > 0 ? $"Press {key}, or the button at the bottom of the sidebar, to leave it."
                                                            : "The button at the bottom of the sidebar leaves it.",
                             "Leave", Shortcuts.ToggleFullScreen);
    }

    void OnLeaveFullScreen(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) => Shortcuts.ToggleFullScreen();

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
