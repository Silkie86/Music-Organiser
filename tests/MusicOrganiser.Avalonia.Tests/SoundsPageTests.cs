using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;
using MusicOrganiser.Avalonia.Views;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>Sound Lab's list on the headless screen, with a few made-up sounds and no sound.</summary>
public sealed class SoundsPageTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("mo-sounds-page-").FullName;
    readonly LibraryDb _db;
    readonly SoundLibrary _soundDb;
    readonly SoundOrganiser _sounds;

    public SoundsPageTests()
    {
        Ui.Current = new PlaygroundPageTests.SilentUi();
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
        UiWork.InBackground = true;
        PreviewWorker.InBackground = true;
    }

    static void Settle()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void A_header_click_sorts_in_the_view_model_and_Ctrl_Down_steps_down_the_rows_on_screen()
    {
        // Review: the grid sorted the whole library itself on the UI thread (seconds on 357k sounds), and Ctrl+Up/Down
        // walked the view model's order instead of the rows on screen.
        _soundDb.Save(new (string Name, double Bpm)[] { ("Loop A", 120), ("Loop B", 90), ("Loop C", 174), ("Loop D", 100) }.Select(s =>
            new SoundLibrary.Analysed(Path.Combine(_root, s.Name + ".wav"), 1, 1, new SoundAnalysis { Bpm = s.Bpm, Features = [1, 0, 0] }, null, null, null)));
        _sounds.Reload();
        var main = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 900 };
        window.Show();
        main.GoTo("Sound Lab");
        main.Sounds.ReloadAsync().GetAwaiter().GetResult();
        var lab = main.Sounds;
        lab.ViewMode = SoundsViewModel.List;
        Settle();
        var page = window.GetVisualDescendants().OfType<SoundsView>().First();
        var grid = page.FindControl<DataGrid>("SoundGrid")!;
        var bpm = grid.Columns.Single(c => c.SortMemberPath == "Bpm");

        bpm.Sort();
        Settle();
        Assert.Equal("Bpm", lab.ColumnSort);
        Assert.Equal("BPM ▲", bpm.Header);
        Assert.Empty(((DataGridCollectionView)grid.ItemsSource!).SortDescriptions);   // the grid didn't sort it again
        string[] OnScreen() => [.. grid.GetVisualDescendants().OfType<DataGridRow>().OrderBy(r => r.Bounds.Top).Select(r => ((Sound)r.DataContext!).Name)];
        Assert.Equal(["Loop B", "Loop D", "Loop A", "Loop C"], OnScreen());

        lab.Selected = lab.Shown[0];
        Settle();
        Assert.True(page.RunShortcut("app.nextItem", false));
        Assert.Equal("Loop D", lab.Selected!.Name);

        // Typing a search keeps the header's order; picking an Order takes the arrow off.
        lab.SearchText = "loop";
        Settle();
        Assert.Equal(["Loop B", "Loop D", "Loop A", "Loop C"], OnScreen());
        lab.SortBy = "Name";
        Settle();
        Assert.Equal("BPM", bpm.Header);
        Assert.Equal(["Loop A", "Loop B", "Loop C", "Loop D"], OnScreen());
        window.Close();
    }

    [AvaloniaFact]
    public void The_sound_menus_and_the_sidebars_rescan_menu_show_each_lines_key()
    {
        // Review: these menus showed no keys, and left out Favourite, Tag and Copy path; Rescan everything couldn't have a key.
        _soundDb.Save([new SoundLibrary.Analysed(Path.Combine(_root, "Kick.wav"), 1, 1, new SoundAnalysis { Features = [1, 0, 0] }, null, null, null)]);
        _sounds.Reload();
        var main = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 900 };
        window.Show();
        main.GoTo("Sound Lab");
        main.Sounds.ReloadAsync().GetAwaiter().GetResult();
        var lab = main.Sounds;
        lab.ViewMode = SoundsViewModel.List;
        Settle();
        lab.Selected = lab.Shown[0];
        Settle();
        var page = window.GetVisualDescendants().OfType<SoundsView>().First();
        var grid = page.FindControl<DataGrid>("SoundGrid")!;

        KeyGesture? Key(ContextMenu menu, string header) => menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == header).InputGesture;
        var menu = grid.ContextMenu!;
        grid.RaiseEvent(new ContextRequestedEventArgs());   // a right-click, which is when a menu says it is opening
        Settle();
        Assert.Equal(KeyGesture.Parse("Space"), Key(menu, "Play"));
        Assert.Equal(KeyGesture.Parse("Ctrl+B"), Key(menu, "To Bitwig"));
        Assert.Equal(KeyGesture.Parse("Ctrl+D"), Key(menu, "Favourite"));
        Assert.Equal(KeyGesture.Parse("Ctrl+T"), Key(menu, "Add a tag…"));
        Assert.Equal(KeyGesture.Parse("Ctrl+Shift+E"), Key(menu, "Show file"));
        Assert.Equal(KeyGesture.Parse("Ctrl+Shift+C"), Key(menu, "Copy path"));

        // Add a tag puts the keyboard in the tag box once the menu has gone.
        menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Add a tag…").RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Settle();
        Assert.True(page.FindControl<TextBox>("TagBox")!.IsFocused);

        // The tiles' menu is the same, and Rescan everything takes a key like any other action.
        lab.ViewMode = SoundsViewModel.Tiles;
        Settle();
        var tiles = page.FindControl<ListBox>("TileList")!;
        tiles.RaiseEvent(new ContextRequestedEventArgs());
        Settle();
        Assert.Equal(KeyGesture.Parse("Ctrl+D"), Key(tiles.ContextMenu!, "Favourite"));
        tiles.ContextMenu.Close();
        main.Shortcuts.Set("app.fullRescan", "Ctrl+F5");
        var rescan = window.GetVisualDescendants().OfType<Button>().Single(b => b.ContextMenu?.Items.OfType<MenuItem>().Any(i => (string?)i.Tag == "app.fullRescan") == true);
        rescan.RaiseEvent(new ContextRequestedEventArgs());
        Settle();
        Assert.Equal(KeyGesture.Parse("Ctrl+F5"), Key(rescan.ContextMenu!, "Rescan everything"));
        rescan.ContextMenu.Close();
        window.Close();
    }
}
