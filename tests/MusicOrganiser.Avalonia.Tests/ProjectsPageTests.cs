using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicOrganiser.Core.Library;
using System.Diagnostics;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Plugins;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;
using MusicOrganiser.Avalonia.Views;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>The real Projects page on the headless screen, with a library of its own in a temp folder.</summary>
public sealed class ProjectsPageTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("mo-projects-page-").FullName;
    readonly LibraryDb _db;
    readonly SoundLibrary _soundDb;
    readonly SoundOrganiser _sounds;

    public ProjectsPageTests()
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

    void Song(string name)
    {
        var file = Path.Combine(_root, name + ".flp");
        File.WriteAllBytes(file, [0]);
        _db.Upsert(new LibraryDb.IndexedFile(file, 1, 100, new ProjectInfo { Daw = "FL Studio" }, null, [], []));
    }

    static void Settle()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Each_song_keeps_its_own_rating_when_moving_between_songs()
    {
        Song("Alpha");
        Song("Bravo");
        Song("Charlie");
        var main = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        var page = main.Projects;
        page.ReloadAsync().GetAwaiter().GetResult();
        var window = new Window { Content = new ProjectsView { DataContext = page }, Width = 1400, Height = 900 };
        window.Show();
        Settle();

        ProjectItem Item(string name) => page.Items.Single(i => i.Name == name);
        StarRating RatingBox() => window.GetVisualDescendants().OfType<StarRating>().Single(c => c.DataContext == page.Detail);

        page.Selected = Item("Alpha");
        Settle();
        RatingBox().Value = 4;
        Settle();
        page.Selected = Item("Bravo");
        Settle();
        Assert.Equal(0, RatingBox().Value);
        RatingBox().Value = 2;
        Settle();
        page.Selected = Item("Charlie");
        Settle();
        page.Selected = Item("Alpha");
        Settle();

        Assert.Equal(4, RatingBox().Value);
        var saved = _db.Projects().ToDictionary(p => p.Name, p => p.Rating);
        Assert.Equal(4, saved["Alpha"]);
        Assert.Equal(2, saved["Bravo"]);
        Assert.Equal(0, saved["Charlie"]);
        window.Close();
    }

    [AvaloniaFact]
    public void A_huge_project_opens_and_shows_its_samples_and_plugins_without_freezing()
    {
        var info = new ProjectInfo { Daw = "Bitwig Studio" };
        info.Plugins.AddRange(Enumerable.Range(0, 739).Select(i => new PluginRef($"Plugin {i}", "Vendor", i % 3 == 0 ? PluginKind.Instrument : PluginKind.Effect, "VST3")));
        var samples = Enumerable.Range(0, 3730).Select(i => ($"K:/Samples/Pack {i / 50}/Hit {i}.wav", i % 4 != 0)).ToList();
        var file = Path.Combine(_root, "analog full set nb 36.bwproject");
        File.WriteAllBytes(file, [0]);
        _db.Upsert(new LibraryDb.IndexedFile(file, 1, 100, info, null, samples, []));
        var main = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        var page = main.Projects;
        page.ReloadAsync().GetAwaiter().GetResult();
        var window = new Window { Content = new ProjectsView { DataContext = page }, Width = 1400, Height = 900 };
        window.Show();
        Settle();

        var clock = Stopwatch.StartNew();
        page.Selected = page.Items.Single();
        Settle();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "Tabs");
        foreach (var header in new[] { "Samples", "Plugins", "Overview" })
        {
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => (string?)t.Header == header);
            Settle();
        }
        clock.Stop();

        var rows = window.GetVisualDescendants().Count(c => c is Border { DataContext: SampleRow or PluginRow } b && b.Classes.Contains("listRow"));
        Assert.True(clock.ElapsedMilliseconds < 3000, $"Took {clock.ElapsedMilliseconds} ms");
        Assert.True(rows < 200, $"{rows} rows were built");

        // Fix lists every missing sample; that list builds only what's in view too.
        var plan = page.Detail!.PlanFixCommand.ExecuteAsync(null);
        while (!plan.IsCompleted) Dispatcher.UIThread.RunJobs();
        Settle();
        Assert.True(page.Detail.FixRows.Count > 900);
        var fixRows = window.GetVisualDescendants().Count(c => c is Border { DataContext: FixRow } b && b.Classes.Contains("listRow"));
        Assert.InRange(fixRows, 1, 100);
        window.Close();
    }

    [AvaloniaFact]
    public void The_song_panel_puts_every_way_to_open_in_one_row_and_its_counts_open_their_tabs()
    {
        var folder = Path.Combine(_root, "Beat");
        Directory.CreateDirectory(folder);
        foreach (var (name, days) in new[] { ("Beat_1", 40), ("Beat_2", 1) })
        {
            var file = Path.Combine(folder, name + ".flp");
            File.WriteAllBytes(file, [0]);
            File.SetLastWriteTime(file, DateTime.Now.AddDays(-days));
            _db.Upsert(new LibraryDb.IndexedFile(file, 1, new DateTimeOffset(File.GetLastWriteTime(file)).ToUnixTimeSeconds(), new ProjectInfo { Daw = "FL Studio" }, null,
                [("K:/Samples/Gone.wav", false)], []));
        }
        var main = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        var page = main.Projects;
        page.ReloadAsync().GetAwaiter().GetResult();
        var window = new Window { Content = new ProjectsView { DataContext = page }, Width = 1400, Height = 900 };
        window.Show();
        page.Selected = page.Items.Single();
        Settle();
        var view = window.GetVisualDescendants().OfType<ProjectDetailView>().Single(v => v.DataContext == page.Detail);

        // Open in Bitwig, Open in FL Studio and Older saves sit side by side on one line.
        Button Labelled(string text) => view.GetVisualDescendants().OfType<Button>()
            .Single(b => b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text));
        var tops = new[] { "Open in Bitwig", "Open in FL Studio", "Older saves" }.Select(t => Labelled(t).TranslatePoint(default, view)!.Value.Y).ToList();
        Assert.All(tops, y => Assert.Equal(tops[0], y, 1));

        // The counts line opens each list's tab.
        var tabs = view.FindControl<TabControl>("Tabs")!;
        view.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("countLink") && (string?)b.Tag == "Samples")
            .RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle();
        Assert.Equal("Samples", (string?)((TabItem)tabs.SelectedItem!).Header);
        Assert.Equal("1 sample", page.Detail!.SamplesCountText);
        Assert.Equal("1 missing", page.Detail.MissingSamplesText);

        // Fix, pressed from another tab, opens its list on Overview under the arrangement, with the divider between them.
        var plan = page.Detail.PlanFixCommand.ExecuteAsync(null);
        while (!plan.IsCompleted) Dispatcher.UIThread.RunJobs();
        Settle();
        Assert.Equal("Overview", (string?)((TabItem)tabs.SelectedItem!).Header);
        Assert.True(view.FindControl<GridSplitter>("ArrangementDivider")!.IsEffectivelyVisible);

        // Squeezed narrow, the toolbar's groups wrap onto more lines instead of being cut off.
        window.Width = 900;
        Settle();
        var toolbar = view.FindControl<ToolbarPanel>("Toolbar")!;
        Assert.All(toolbar.Children.Where(c => c.IsVisible), c => Assert.True(c.Bounds.Right <= toolbar.Bounds.Width + 0.5, $"{c.Bounds} is cut off"));
        Assert.True(toolbar.Bounds.Height > Labelled("Open in Bitwig").Bounds.Height * 1.5, "The toolbar didn't wrap");
        window.Close();
    }

    [AvaloniaFact]
    public void The_search_box_shows_an_X_that_clears_it_whenever_there_is_something_to_clear()
    {
        var main = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        var page = main.Projects;
        var window = new Window { Content = new ProjectsView { DataContext = page }, Width = 1400, Height = 900 };
        window.Show();
        Settle();
        var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "Search");
        Button Clear() => search.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ClearSearch");
        Assert.False(Clear().IsVisible);

        // Typed, then the focus moved on to the list: the X is still there.
        page.SearchText = "serum";
        window.GetVisualDescendants().OfType<DataGrid>().First().Focus();
        Settle();
        Assert.False(search.IsFocused);
        Assert.True(Clear().IsVisible);
        Clear().RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle();
        Assert.Equal("", page.SearchText);
        Assert.False(Clear().IsVisible);
    }

    [AvaloniaFact]
    public void The_plugins_side_panel_lists_songs_and_its_DAW_chips_keep_to_one_DAW()
    {
        void Save(string file, string daw)
        {
            var info = new ProjectInfo { Daw = daw };
            info.Plugins.Add(new PluginRef("Serum", "Xfer Records", PluginKind.Instrument, "VST3"));
            var path = Path.Combine(_root, file);
            File.WriteAllBytes(path, [0]);
            _db.Upsert(new LibraryDb.IndexedFile(path, 1, 100, info, null, [], []));
        }
        foreach (var n in new[] { "", "_2", "_3" }) Save($"Teka{n}.flp", "FL Studio");
        Save("Heel_4.bwproject", "Bitwig Studio");
        var main = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        var page = main.Plugins;
        var window = new Window { Content = new PluginsView { DataContext = page }, Width = 1600, Height = 900 };
        window.Show();
        page.Selected = page.Items.Single(u => u.Name == "Serum");
        Settle();

        var songs = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "PluginSongs");
        Assert.Equal(["Heel", "Teka"], songs.Items.Cast<SongUse>().Select(s => s.Song).Order());
        var chips = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ProjectDawChips");
        Assert.True(chips.IsVisible);
        chips.SelectedItem = "Bitwig Studio";
        Settle();
        Assert.Equal(["Heel"], songs.Items.Cast<SongUse>().Select(s => s.Song));
        Assert.Equal("Used in 1 song in Bitwig Studio", page.ProjectsTitle);
    }

    [AvaloniaFact]
    public void Settings_are_in_tabs_and_their_numbers_reach_the_pages()
    {
        var main = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        var window = new Window { Content = new SettingsView { DataContext = main.Settings }, Width = 1400, Height = 900 };
        window.Show();
        Settle();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "Tabs");
        Assert.Equal(["General", "Projects", "Sounds", "Plugins", "MIDI", "DAW links", "Developer"],
                     tabs.Items.Cast<TabItem>().Select(t => (string)t.Header!));
        tabs.SelectedIndex = tabs.Items.Count - 1;   // Developer
        Settle();
        var headers = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("sectionHeader") && t.IsEffectivelyVisible)
                            .Select(t => t.Text).ToList();
        Assert.Contains("Live tests with Bitwig", headers);
        Assert.Contains("MO Probe", headers);
        // Settings opens on the tab last used, after a restart too.
        window.Close();
        var reopened = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        Assert.Equal("Developer", reopened.Settings.Tab);
        var view = new SettingsView { DataContext = reopened.Settings };
        Assert.Equal("Developer", (string)((TabItem)view.FindControl<TabControl>("Tabs")!.SelectedItem!).Header!);

        // Each number is stored, and the page uses it.
        main.Settings.SoundLab.Rediscover.HowMany = 250;
        main.Settings.SoundLab.SimilarCount = 24;
        main.Settings.PluginsPage.NeededCount = 9;
        var again = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        Assert.Equal((250, 24, 9), (again.Sounds.Rediscover.HowMany, again.Sounds.SimilarCount, again.Plugins.NeededCount));
        Assert.Equal(SongVersions.DefaultRunGapMonths, again.Settings.RunGapMonths);
    }
}
