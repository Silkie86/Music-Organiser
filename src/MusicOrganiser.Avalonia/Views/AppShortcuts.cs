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

    /// <summary>Whether the control the key was pressed in keeps <paramref name="key"/> for itself (the piano roll its Delete).</summary>
    bool KeepsKey(KeyEventArgs e, string key) => false;
}

/// <summary>
/// The window's keyboard shortcuts (Solomon, 2026-10-21: "logical shortcuts and function keys around the app"). Keys are
/// looked at by the window on the way in, so they work wherever the focus is: the ones that work everywhere are done
/// here, and a page's own go to its view (<see cref="IShortcutPage"/>). While typing in a box only Ctrl and function keys
/// count, lists and sliders keep their arrows, a focused button keeps Space, and F12 stays the note for Claude.
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

    Control? _view;

    /// <summary>The page on show (not the one fading out): found once per visit, not on every key.</summary>
    Control? CurrentView(MainViewModel main)
    {
        if (main.SelectedNav?.Page is not { } page) return null;
        if (_view is { } kept && kept.DataContext == page && TopLevel.GetTopLevel(kept) == _window) return kept;
        return _view = _pages.GetVisualDescendants().OfType<UserControl>().FirstOrDefault(u => u.DataContext == page);
    }

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

    /// <summary>
    /// Keys that do their job again and again while held: stepping through a list, moving an octave, and on the Playground
    /// all but the ones that would flip back and forth or pile things up.
    /// </summary>
    static bool Repeats(string id) => id.StartsWith("playground.")
        ? id is not ("playground.play" or "playground.help" or "playground.loop" or "playground.save" or "playground.split"
            or "playground.duplicate" or "playground.paste" or "playground.new" or "playground.ideas" or "playground.rename")
        : id is "app.prevItem" or "app.nextItem" or "midi.octaveUp" or "midi.octaveDown";

    /// <summary>Whether the key was pressed on a button, toggle or tick box, which Space presses.</summary>
    static bool OnButton(KeyEventArgs e) => e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null;

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
            // Esc, ? or F1 close it. Other shortcuts wait until it's shut (Ctrl+1 doesn't change the page behind it), while
            // plain keys go on to the sheet: Tab to a key, Space or Enter to press it.
            if (key == "Esc" || id is "app.help" or "app.helpF1" or "playground.help") { if (!repeat) keys.SheetOpen = false; Take(e); }
            else if (id is not null && ShortcutsViewModel.WorksWhileTyping(key)) Take(e);
            return;
        }
        if (id is null) return;
        // the cheap checks first, so typing in a box costs nothing
        var box = ShortcutKeys.Box(e);
        if (box is not null && (!ShortcutsViewModel.WorksWhileTyping(key) || ShortcutKeys.IsTextKey(key))) return;
        if (ShortcutKeys.UsedByControl(e, key)) return;
        if (key == "Space" && id != "playground.play" && OnButton(e)) return;   // the Playground's transport keeps Space, as in a DAW
        var everywhere = ShortcutsViewModel.IsEverywhere(id);
        var view = CurrentView(main);
        var own = view as IShortcutPage;
        if (!everywhere && own is null) return;
        if (own is not null && own.KeepsKey(e, key)) return;
        if (repeat && !Repeats(id))
        {
            if (_taken.Contains(e.Key)) e.Handled = true;   // held down, it did its job once
            return;
        }
        if (box is not null) BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();   // what's typed counts first
        var done = own is not null && own.RunShortcut(id, repeat) || everywhere && RunEverywhere(main, id, view, page);
        if (done) Take(e);
    }

    bool RunEverywhere(MainViewModel main, string id, Control? view, string page)
    {
        switch (id)
        {
            case "app.help" or "app.helpF1": main.Shortcuts.OpenSheet(page); return true;
            case "app.goProjects": return main.GoTo(ShortcutsViewModel.Projects);
            case "app.goSounds": return main.GoTo(ShortcutsViewModel.Sounds);
            case "app.goMidi": return main.GoTo(ShortcutsViewModel.Midi);
            case "app.goPlayground": return main.GoTo(ShortcutsViewModel.Playground);
            case "app.goPlugins": return main.GoTo(ShortcutsViewModel.Plugins);
            case "app.goSettings": return main.GoTo(ShortcutsViewModel.Settings);
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
            // Just the page on show, as the rail's size button does; Settings › General sizes every page.
            case "app.sizeUp": main.PageBiggerCommand.Execute(null); return true;
            case "app.sizeDown": main.PageSmallerCommand.Execute(null); return true;
            case "app.sizeReset": main.PageSizeAsRestCommand.Execute(null); return true;
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
                // the rows as shown (sorted by a column, maybe), counted without copying 16,000 songs on every step
                if ((grid.CollectionView as System.Collections.IList ?? grid.ItemsSource as System.Collections.IList) is not { Count: > 0 } rows) return false;
                grid.SelectedIndex = grid.SelectedIndex < 0 ? 0 : Math.Clamp(grid.SelectedIndex + by, 0, rows.Count - 1);
                if (grid.SelectedItem is { } picked) grid.ScrollIntoView(picked, null);
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
