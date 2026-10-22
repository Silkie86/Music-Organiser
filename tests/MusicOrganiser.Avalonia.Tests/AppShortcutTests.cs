using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>The keys that work on every page, and each page's own, in the real window with a library of its own.</summary>
public sealed class AppShortcutTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("mo-app-keys-").FullName;
    readonly LibraryDb _db;
    readonly SoundLibrary _soundDb;
    readonly SoundOrganiser _sounds;

    public AppShortcutTests()
    {
        Ui.Current = new AvaloniaUi();
        UiWork.InBackground = false;
        PreviewWorker.InBackground = false;
        _db = new LibraryDb(Path.Combine(_root, "library.db"));
        _soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        _sounds = new SoundOrganiser(_soundDb);
    }

    public void Dispose()
    {
        _sounds.Dispose();
        _soundDb.Dispose();
        _db.Dispose();
    }

    static void Settle()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void Every_page_starts_with_one_job_per_key_and_its_own_key_wins_over_the_everywhere_one()
    {
        foreach (var page in ShortcutsViewModel.Actions.GroupBy(a => a.Page))
        {
            var used = page.Where(a => a.Default.Length > 0).Select(a => a.Default).ToList();
            Assert.True(used.Count == used.Distinct().Count(), $"{page.Key} gives a key two jobs");
            Assert.DoesNotContain(used, k => ShortcutsViewModel.Kept.Contains(k));
        }
        Assert.Equal(ShortcutsViewModel.Actions.Count, ShortcutsViewModel.Actions.Select(a => a.Id).Distinct().Count());

        var keys = new ShortcutsViewModel(null);
        Assert.Equal("playground.play", keys.Find(ShortcutsViewModel.Playground, "Space"));   // the page's own first
        Assert.Equal("app.play", keys.Find(ShortcutsViewModel.Sounds, "Space"));
        Assert.Equal("app.goSounds", keys.Find(ShortcutsViewModel.Playground, "ctrl+2"));     // then the everywhere ones
        Assert.Equal("playground.zoomIn", keys.Find(ShortcutsViewModel.Playground, "Ctrl+="));  // the timeline's zoom there
        Assert.Equal("app.sizeUp", keys.Find(ShortcutsViewModel.Projects, "Ctrl+Shift+="));     // the whole app's elsewhere
        Assert.Equal("playground.help", keys.Find(ShortcutsViewModel.Playground, "Shift+/"));
        Assert.Equal("app.help", keys.Find(ShortcutsViewModel.Projects, "Shift+/"));
        Assert.Null(keys.Find("Settings", "Ctrl+B"));
        Assert.Equal("projects.openInBitwig", keys.Find(ShortcutsViewModel.Projects, "Ctrl+B"));

        // The sheet: the page's keys, then the everywhere ones it doesn't use for a job of its own
        keys.OpenSheet(ShortcutsViewModel.Playground);
        Assert.True(keys.SheetOpen);
        Assert.Contains(keys.SheetRows, r => r.Action.Id == "playground.split");
        Assert.DoesNotContain(keys.SheetEverywhere, r => r.Key == "Space" || r.Key == "?");
        Assert.Contains(keys.SheetEverywhere, r => r.Action.Id == "app.goProjects");

        // F12 stays the note for Claude; a page key that's also an everywhere one says which wins
        keys.Set("sounds.favourite", "F12");
        Assert.Equal("Ctrl+D", keys.KeyOf("sounds.favourite"));
        Assert.Contains("F12", keys.Message);
        keys.Set("sounds.favourite", "Ctrl+F");
        Assert.Contains("Search this page", keys.Message);
        Assert.Equal("sounds.favourite", keys.Find(ShortcutsViewModel.Sounds, "Ctrl+F"));
        Assert.Equal("app.search", keys.Find(ShortcutsViewModel.Midi, "Ctrl+F"));
        keys.Set("app.fullScreen", "Ctrl+B");
        Assert.Contains($"keep {ShortcutsViewModel.Display("Ctrl+B")}", keys.Message);
    }

    void Song(string name)
    {
        var file = Path.Combine(_root, name + ".bwproject");
        File.WriteAllBytes(file, [0]);
        _db.Upsert(new LibraryDb.IndexedFile(file, 1, 100, new ProjectInfo { Daw = "Bitwig Studio" }, null, [], []));
    }

    [AvaloniaFact]
    public void Keys_move_between_pages_songs_tabs_and_the_search_box()
    {
        Song("Alpha");
        Song("Bravo");
        Song("Charlie");
        var main = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        main.Projects.ReloadAsync().GetAwaiter().GetResult();
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 900 };
        window.Show();
        Settle();
        main.GoTo("Projects");
        Settle();

        void Press(Key key, RawInputModifiers mods = RawInputModifiers.None, string? text = null)
        {
            var physical = key switch
            {
                Key.D1 => PhysicalKey.Digit1, Key.D0 => PhysicalKey.Digit0, Key.OemPlus => PhysicalKey.Equal, Key.OemMinus => PhysicalKey.Minus, Key.D2 => PhysicalKey.Digit2, Key.PageDown => PhysicalKey.PageDown,
                Key.Left => PhysicalKey.ArrowLeft, Key.Right => PhysicalKey.ArrowRight, Key.Up => PhysicalKey.ArrowUp,
                Key.Down => PhysicalKey.ArrowDown, Key.OemComma => PhysicalKey.Comma, Key.F => PhysicalKey.F, Key.F1 => PhysicalKey.F1,
                Key.Escape => PhysicalKey.Escape, Key.OemQuestion => PhysicalKey.Slash, Key.Tab => PhysicalKey.Tab, Key.Space => PhysicalKey.Space,
                _ => PhysicalKey.None,
            };
            window.KeyPress(key, mods, physical, text);
            window.KeyRelease(key, mods, physical, text);
            Settle();
        }

        // Pages: Ctrl+number, Ctrl+Page Down, and Alt+Left back the way you came
        Press(Key.D2, RawInputModifiers.Control, "2");
        Assert.Equal("Sound Lab", main.SelectedNav.Title);
        Press(Key.PageDown, RawInputModifiers.Control);
        Assert.Equal("MIDI", main.SelectedNav.Title);
        Press(Key.Left, RawInputModifiers.Alt);
        Assert.Equal("Sound Lab", main.SelectedNav.Title);
        Press(Key.Left, RawInputModifiers.Alt);
        Assert.Equal("Projects", main.SelectedNav.Title);
        // the search box has the keyboard here: Alt+arrows still work from it, except on a Mac (Option+arrow jumps a word)
        if (OperatingSystem.IsMacOS()) window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Classes.Contains("pageList")).Focus();
        Press(Key.Right, RawInputModifiers.Alt);
        Assert.Equal("Sound Lab", main.SelectedNav.Title);
        Press(Key.OemComma, RawInputModifiers.Control, ",");
        Assert.Equal("Settings", main.SelectedNav.Title);
        Press(Key.D1, RawInputModifiers.Control, "1");
        Assert.Equal("Projects", main.SelectedNav.Title);

        // Ctrl+F puts the keyboard in the search box; Ctrl+Down steps through the songs from there, as you type
        var search = window.GetVisualDescendants().OfType<TextBox>().First(b => b.Classes.Contains("pageSearch") && b.IsEffectivelyVisible);
        var list = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Classes.Contains("pageList") && g.IsEffectivelyVisible);
        list.Focus();
        Press(Key.F, RawInputModifiers.Control, "f");
        Assert.True(search.IsFocused);
        Press(Key.Down, RawInputModifiers.Control);
        var first = main.Projects.Selected;
        Assert.NotNull(first);
        Press(Key.Down, RawInputModifiers.Control);
        Assert.NotEqual(first, main.Projects.Selected);
        Press(Key.Up, RawInputModifiers.Control);
        Assert.Equal(first, main.Projects.Selected);
        Assert.True(search.IsFocused);

        // Ctrl+Right moves along the song's tabs (not while typing, where it jumps a word)
        list.Focus();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.IsEffectivelyVisible);
        var tab = tabs.SelectedIndex;
        Press(Key.Right, RawInputModifiers.Control);
        Assert.NotEqual(tab, tabs.SelectedIndex);
        Press(Key.Left, RawInputModifiers.Control);
        Assert.Equal(tab, tabs.SelectedIndex);

        // Ctrl+= and Ctrl+- make the page bigger and smaller, Ctrl+0 puts it back
        Press(Key.OemPlus, RawInputModifiers.Control, "=");
        Assert.Equal(110, main.Settings.Appearance.Size);
        Press(Key.OemMinus, RawInputModifiers.Control, "-");
        Press(Key.OemMinus, RawInputModifiers.Control, "-");
        Assert.Equal(90, main.Settings.Appearance.Size);
        Press(Key.D0, RawInputModifiers.Control, "0");
        Assert.Equal(100, main.Settings.Appearance.Size);

        // F1 shows this page's keys and everywhere's, even from the search box (function keys never type). The keyboard goes
        // onto the sheet, where Tab moves round it; other shortcuts wait until it's shut; Esc closes it and the keyboard goes back
        search.Focus();
        Press(Key.F1);
        Assert.True(main.Shortcuts.SheetOpen);
        Assert.Equal("Projects keys", main.Shortcuts.SheetTitle);
        var sheet = window.FindControl<Border>("KeySheet")!;
        bool OnSheet() => window.FocusManager?.GetFocusedElement() is Visual v && sheet.IsVisualAncestorOf(v);
        Assert.True(OnSheet());
        Press(Key.Tab);
        Assert.True(OnSheet());
        Press(Key.D2, RawInputModifiers.Control, "2");
        Assert.Equal("Projects", main.SelectedNav.Title);
        Press(Key.Escape);
        Assert.False(main.Shortcuts.SheetOpen);
        Assert.True(search.IsFocused);

        // The Playground's ? (and its Help menu) open the same sheet, on the Playground's keys
        Assert.True(main.Playground.RunShortcut("playground.help"));
        Assert.Equal("Playground keys", main.Shortcuts.SheetTitle);
        Assert.True(main.Playground.CheatSheetOpen);
        Press(Key.Escape);
        Assert.False(main.Playground.CheatSheetOpen);

        // Typing a plain key in the search box types it, even one that's a shortcut
        search.Focus();
        search.Text = "";
        window.KeyTextInput("?");
        Press(Key.OemQuestion, RawInputModifiers.Shift, "?");
        Assert.False(main.Shortcuts.SheetOpen);

        // Space on a focused button presses the button, even with a sound picked that Space would otherwise play
        main.GoTo(ShortcutsViewModel.Sounds);
        Settle();
        main.Sounds.Selected = new Sound { Id = 1, Path = Path.Combine(_root, "kick.wav") };
        Settle();
        var page = window.GetVisualDescendants().OfType<Views.SoundsView>().First();
        var button = page.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled && b.Focusable);
        button.Focus();
        bool? takenByShortcut = null;
        void Watch(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space) return;
            takenByShortcut = e.Handled;
            e.Handled = true;   // not clicked for real: only whether the app's keys left it alone matters here
        }
        // looked at on the button, after the window's keys have had their go
        button.AddHandler(InputElement.KeyDownEvent, Watch, global::Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        Press(Key.Space, text: " ");
        Assert.False(takenByShortcut);
        window.Close();
    }
}
