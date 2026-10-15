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
        ComboBox RatingBox() => window.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => c.ItemsSource == ProjectsViewModel.RatingChoices && c.DataContext == page.Detail);

        page.Selected = Item("Alpha");
        Settle();
        RatingBox().SelectedItem = 4;
        Settle();
        page.Selected = Item("Bravo");
        Settle();
        Assert.Equal(0, RatingBox().SelectedItem);
        RatingBox().SelectedItem = 2;
        Settle();
        page.Selected = Item("Charlie");
        Settle();
        page.Selected = Item("Alpha");
        Settle();

        Assert.Equal(4, RatingBox().SelectedItem);
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
}
