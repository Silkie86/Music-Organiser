using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class PlaygroundView : UserControl
{
    public PlaygroundView()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        // Shortcuts are looked at on the way in, so a focused button doesn't take Space first (as in a DAW)
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// The Playground's keys, from the app's shortcut list (Settings › Shortcuts): Bitwig's unless changed. While typing in
    /// a box only Ctrl keys count, and lists and the piano roll keep their own arrows and Delete.
    /// </summary>
    void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (DataContext is not PlaygroundViewModel vm || ShortcutKeys.Of(e) is not { } key) return;
        if (vm.CheatSheetOpen && key == "Esc") { vm.CheatSheetOpen = false; e.Handled = true; return; }
        var plain = !ShortcutsViewModel.WorksWhileTyping(key);
        if (plain && (ShortcutKeys.InTextBox(e) || ShortcutKeys.InList(e))) return;
        if (plain && e.Source is PianoRollEditor && key is "Delete" or "Backspace") return;   // deletes the note under the pointer
        if (vm.Shortcuts.Find(ShortcutsViewModel.Playground, key) is { } id && vm.RunShortcut(id)) e.Handled = true;
    }

    /// <summary>Enter in the chord chart or a part's name puts what's typed in (those boxes take it when you leave them,
    /// not on every key, so a half-typed chord isn't read).</summary>
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.Source is TextBox { Classes: var classes } box && classes.Contains("commitOnEnter"))
        {
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    void OnCheatSheetBackdrop(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is PlaygroundViewModel vm) vm.CheatSheetOpen = false;
    }

    void OnCheatSheetCard(object? sender, PointerPressedEventArgs e) => e.Handled = true;   // a click on the sheet itself leaves it open
}
