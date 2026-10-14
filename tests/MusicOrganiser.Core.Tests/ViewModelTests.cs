using System.Collections;
using System.ComponentModel;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;
using Xunit;

namespace MusicOrganiser.Core.Tests;

/// <summary>The screens' logic runs without any UI framework, which is what lets WPF and Avalonia share it.</summary>
public class ViewModelTests : IDisposable
{
    readonly string _root = Fixtures.TempDir();
    readonly LibraryDb _db;
    readonly SoundLibrary _soundDb;
    readonly SoundOrganiser _sounds;

    public ViewModelTests()
    {
        Ui.Current = new HeadlessUi();
        Fixtures.FlStudioProject(_root);
        Fixtures.StudioOneSong(_root);
        Fixtures.BitwigProject(_root);
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

    [Fact]
    public void FirstRunOpensSettings()
    {
        var main = new MainViewModel(_db, _sounds);
        Assert.Same(main.Settings, main.SelectedNav.Page);
    }

    [Fact]
    public void ProjectsListFiltersByDaw()
    {
        _db.AddRoot(_root);
        new Scanner(_db).ScanProjects();
        _db.RemoveRoot(_root);   // keeps the projects, and stops the screen starting its own background scan
        var main = new MainViewModel(_db, _sounds);
        var projects = main.Projects;
        projects.Reload();
        Assert.Equal(3, projects.View.Count);

        projects.DawFilter = "Bitwig Studio";
        Assert.Equal(1, projects.View.Count);
        Assert.Equal("1 of 3 songs", projects.CountText);
    }

    [Fact]
    public void ProjectPanelOffersACheckWithTheDaw()
    {
        _db.AddRoot(_root);
        new Scanner(_db).ScanProjects();
        _db.RemoveRoot(_root);
        var projects = new MainViewModel(_db, _sounds).Projects;
        projects.Reload();
        var offered = projects.View.Cast<ProjectItem>().OrderBy(p => p.Daw).Select(p =>
        {
            projects.Selected = p;
            return (p.Daw, projects.Detail!.CanCheckWithDaw ? projects.Detail.CheckWithDawText : "");
        });
        Assert.Equal([("Bitwig Studio", "Check with Bitwig"), ("FL Studio", "Check with FL Studio"), ("Studio One", "")], offered);
    }

    [Fact]
    public async Task SettingsShowsTheDawLinks()
    {
        var settings = new MainViewModel(_db, _sounds).Settings;
        await settings.RefreshLinksAsync();
        Assert.Equal(["Bitwig Studio", "FL Studio"], settings.Links.Select(l => l.Daw));
        Assert.All(settings.Links, l => Assert.Matches("^(Not installed|Installed)", l.Status));
        Assert.Equal([false, true], settings.Links.Select(l => l.CanChooseFolder));
    }

    sealed class HeadlessUi : IPlatformUi
    {
        public IItemsView CreateView(IList items) => new ListView(items);
        public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new NoTimer();
        public IAudioPlayer CreateAudioPlayer() => new Silence();
        public Task<string?> PickFolder(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFile(string title, string filterName, string extension) => Task.FromResult<string?>(null);
        public Task<string?> PickProgram(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveFile(string fileName, string filterName, string extension, string? initialFolder) => Task.FromResult<string?>(null);
        public Task<bool> Confirm(string message, string title) => Task.FromResult(false);
        public void CopyText(string text) { }
        public void ShowMidi(MidiPreviewViewModel vm) { }
        public void ShowPresets(PresetsViewModel vm) { }
    }

    sealed class ListView(IList items) : IItemsView
    {
        public Predicate<object>? Filter { get; set; }
        public IComparer? CustomSort { get; set; }
        public void SortBy(string property, ListSortDirection direction) { }
        public void GroupBy(string? property) { }
        public void Refresh() { }
        public int Count => items.Cast<object>().Count(i => Filter?.Invoke(i) ?? true);
        public IEnumerator GetEnumerator() => items.Cast<object>().Where(i => Filter?.Invoke(i) ?? true).GetEnumerator();
    }

    sealed class NoTimer : IUiTimer
    {
        public void Start() { }
        public void Stop() { }
    }

    sealed class Silence : IAudioPlayer
    {
        public event Action? Stopped { add { } remove { } }
        public string? Playing => null;
        public double? Position => null;
        public void Seek(double seconds) { }
        public void Play(string path, double startSeconds = 0) { }
        public void Play(float[] mono, int sampleRate, string key, double startSeconds = 0) { }
        public void Stop() { }
        public void Dispose() { }
    }
}
