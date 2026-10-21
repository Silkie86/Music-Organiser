using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// A page that answers its own keys from the app's shortcut list (Settings › Shortcuts): its page's actions, and any of
/// the everywhere ones it does in a way of its own (Space plays its picked item, Ctrl+Down steps its list).
/// </summary>
public interface IShortcutPage
{
    /// <summary>Does <paramref name="id"/>; false when it doesn't apply now, so the key goes on to whatever has the focus.</summary>
    /// <param name="repeat">The key is being held down.</param>
    bool RunShortcut(string id, bool repeat);
}

/// <summary>
/// The window's keyboard shortcuts (Solomon, 2026-10-21: "logical shortcuts and function keys around the app"). Keys are
/// looked at by the window on the way in, so they work wherever the focus is: the ones that work everywhere are done
/// here, and a page's own go to its view (<see cref="IShortcutPage"/>). The Playground still answers its own keys.
/// While typing in a box only Ctrl keys count, lists and sliders keep their arrows, and F12 stays the note for Claude.
/// </summary>
public sealed class AppShortcuts
{
    readonly Window _window;
    readonly ContentControl _pages;
    readonly HashSet<Key> _held = [];
    readonly HashSet<Key> _taken = [];   // keys whose press did a shortcut, so a focused button doesn't click as they come up
    WindowState _beforeFullScreen = WindowState.Maximized;

    public AppShortcuts(Window window, ContentControl pages)
    {
        _window = window;
        _pages = pages;
        window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.Deactivated += (_, _) => { _held.Clear(); _taken.Clear(); };
    }

    MainViewModel? Main => _window.DataContext as MainViewModel;

    /// <summary>The page's name in the shortcut list: its title in the sidebar.</summary>
    static string PageOf(MainViewModel main) => main.SelectedNav?.Title ?? ShortcutsViewModel.Everywhere;

    /// <summary>The page on show (not the one fading out).</summary>
    Control? CurrentView() =>
        Main?.SelectedNav?.Page is { } page ? _pages.GetVisualDescendants().OfType<UserControl>().FirstOrDefault(u => u.DataContext == page) : null;

    void OnKeyUp(object? sender, KeyEventArgs e)
    {
        _held.Remove(e.Key);
        if (_taken.Remove(e.Key)) e.Handled = true;
    }

    void Take(KeyEventArgs e)
    {
        e.Handled = true;
        _taken.Add(e.Key);
    }

    /// <summary>Keys that do their job again and again while held: stepping through a list, moving an octave.</summary>
    static bool Repeats(string id) => id is "app.prevItem" or "app.nextItem" or "midi.octaveUp" or "midi.octaveDown";

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Main is not { } main || ShortcutKeys.Of(e) is not { } key) return;
        if (e.Source is ShortcutBox { IsListening: true }) return;   // that key is being given to an action in Settings
        var keys = main.Shortcuts;
        var repeat = !_held.Add(e.Key);
        var page = PageOf(main);
        var id = keys.Find(page, key);
        if (keys.SheetOpen)
        {
            // only closing it works while the sheet covers the window
            if (key == "Esc" || id is "app.help" or "app.helpF1" or "playground.help") { if (!repeat) keys.SheetOpen = false; Take(e); }
            else if (key != "F12") Take(e);
            return;
        }
        if (id is null) return;
        var view = CurrentView();
        var everywhere = ShortcutsViewModel.IsEverywhere(id);
        if (!everywhere && view is not IShortcutPage) return;   // the Playground answers its own keys
        var box = ShortcutKeys.Box(e);
        if (box is not null && (!ShortcutsViewModel.WorksWhileTyping(key) || ShortcutKeys.IsTextKey(key))) return;
        if (ShortcutKeys.UsedByControl(e, key)) return;
        if (repeat && !Repeats(id))
        {
            if (_taken.Contains(e.Key)) e.Handled = true;   // held down, it did its job once
            return;
        }
        if (box is not null) BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();   // what's typed counts first
        var done = view is IShortcutPage own && own.RunShortcut(id, repeat) || everywhere && RunEverywhere(main, id, view, page);
        if (done) Take(e);
    }

    bool RunEverywhere(MainViewModel main, string id, Control? view, string page)
    {
        switch (id)
        {
            case "app.help" or "app.helpF1": main.Shortcuts.OpenSheet(page); return true;
            case "app.goProjects": return main.GoTo("Projects");
            case "app.goSounds": return main.GoTo("Sound Lab");
            case "app.goMidi": return main.GoTo("MIDI");
            case "app.goPlayground": return main.GoTo("Playground");
            case "app.goPlugins": return main.GoTo("Plugins");
            case "app.goSettings": return main.GoTo("Settings");
            case "app.prevPage": main.StepPage(-1); return true;
            case "app.nextPage": main.StepPage(1); return true;
            case "app.back": main.Back(); return true;
            case "app.forward": main.Forward(); return true;
            case "app.search": return view is not null && FocusSearch(view);
            case "app.prevItem": return view is not null && StepList(view, -1);
            case "app.nextItem": return view is not null && StepList(view, 1);
            case "app.prevTab": return view is not null && StepTab(view, -1);
            case "app.nextTab": return view is not null && StepTab(view, 1);
            case "app.rescan":
                if (!main.IsScanning) main.RescanCommand.Execute(null);
                return true;
            case "app.sizeUp": main.Settings.Appearance.StepSize(1); return true;
            case "app.sizeDown": main.Settings.Appearance.StepSize(-1); return true;
            case "app.sizeReset": main.Settings.Appearance.Size = 100; return true;
            case "app.fullScreen":
                if (_window.WindowState == WindowState.FullScreen) _window.WindowState = _beforeFullScreen;
                else
                {
                    _beforeFullScreen = _window.WindowState;
                    _window.WindowState = WindowState.FullScreen;
                }
                return true;
        }
        return false;
    }

    /// <summary>The first thing on show in <paramref name="view"/> of type <typeparamref name="T"/>, in reading order.</summary>
    static T? Shown<T>(Control view, Func<T, bool>? also = null) where T : Control =>
        view.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.IsEffectivelyVisible && (also?.Invoke(c) ?? true));

    /// <summary>Puts the keyboard in the page's search box (the one with the pageSearch class), with what's in it picked.</summary>
    static bool FocusSearch(Control view)
    {
        if (Shown<TextBox>(view, b => b.Classes.Contains("pageSearch")) is not { } box) return false;
        box.Focus();
        box.SelectAll();
        return true;
    }

    /// <summary>Moves the page's main list (the one with the pageList class) on or back by one.</summary>
    public static bool StepList(Control view, int by) =>
        Shown<Control>(view, c => c.Classes.Contains("pageList") && c is DataGrid or ListBox) is { } list && Step(list, by);

    /// <summary>Moves <paramref name="list"/>'s pick on or back by one, keeping it in sight; the first when none is picked.</summary>
    public static bool Step(Control list, int by)
    {
        switch (list)
        {
            case DataGrid grid:
            {
                if (grid.ItemsSource?.Cast<object>().ToList() is not { Count: > 0 } items) return false;
                var at = grid.SelectedItem is { } picked ? items.IndexOf(picked) : -1;
                var next = at < 0 ? 0 : Math.Clamp(at + by, 0, items.Count - 1);
                grid.SelectedItem = items[next];
                grid.ScrollIntoView(items[next], null);
                return true;
            }
            case ListBox box when box.ItemCount > 0:
            {
                var next = box.SelectedIndex < 0 ? 0 : Math.Clamp(box.SelectedIndex + by, 0, box.ItemCount - 1);
                box.SelectedIndex = next;
                box.ScrollIntoView(next);
                return true;
            }
        }
        return false;
    }

    /// <summary>The page's first set of tabs (Projects: the song's; Settings: its own) to the next one along, round the ends.</summary>
    static bool StepTab(Control view, int by)
    {
        if (Shown<TabControl>(view) is not { ItemCount: > 1 } tabs) return false;
        var count = tabs.ItemCount;
        for (int i = 1, at = tabs.SelectedIndex; i < count; i++)
        {
            var next = ((at + by * i) % count + count) % count;
            if (tabs.ContainerFromIndex(next) is Control { IsVisible: false } or Control { IsEnabled: false }) continue;   // a hidden tab is skipped
            tabs.SelectedIndex = next;
            return true;
        }
        return false;
    }
}
