using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;
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
            .Single(c => c.ItemsSource == ProjectsViewModel.RatingChoices && c.IsEffectivelyVisible);

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
}
