using System.Collections;
using System.ComponentModel;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;
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

    [Fact]
    public async Task MidiWindowSendsChannelsToBitwig()
    {
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        if (bitwig is null) return;
        var port = BitwigLink.Port;
        BitwigLink.Port = bitwig.Port;
        try
        {
            var path = Path.Combine(_root, "Night Drive.flp");
            var midi = new MidiPreviewViewModel(Readers.For(path)!, path, "Night Drive", Path.Combine(_root, "Exports"), 128, _db);
            await midi.LoadAsync();
            await midi.SendAllToBitwigCommand.ExecuteAsync(null);

            // Each channel says how it came across, and the window sums up.
            Assert.Equal("Sent 3 channels to Bitwig, each on a new track named after it. 2 didn't come across whole: each says what under its name.",
                         midi.Message);
            Assert.Equal([
                ("Lead", "In Bitwig: Serum with the project's settings, and its notes as a clip."),
                ("Kick", @"In Bitwig: its notes, as a clip. Its sample isn't at C:\Samples\Drums\Kick 01.wav."),
                ("Bass", "In Bitwig: its notes, as a clip. 3x Osc is one of FL Studio's own plugins, which Bitwig can't load."),
            ], midi.Channels.Select(r => (r.Name, r.Sent)));
            using var link = await BitwigLink.ConnectAsync();
            Assert.Equal(["Drums", "Kick", "Bass", "Lead", "Kick", "Bass", "Master"], (await link.ProjectAsync()).Tracks.Select(t => t.Name));
        }
        finally { BitwigLink.Port = port; }
    }

    [Fact]
    public async Task KitGoesOnADrumMachineInBitwig()
    {
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        if (bitwig is null) return;
        var port = BitwigLink.Port;
        BitwigLink.Port = bitwig.Port;
        try
        {
            // The stand-in's Drum Machine ID, as if the app had found it in a Bitwig project.
            _db.SetSetting(KitViewModel.DrumMachineKey, "d7d7d7d7-0000-4000-8000-000000000001");
            var kit = new MainViewModel(_db, _sounds).Sounds.Kit;
            foreach (var (slot, file) in kit.Slots.Take(4).Zip(["Kick 01.wav", "Snare 02.wav", "Clap.wav", "Hat.wav"]))
                slot.Slot.Sound = new Sound { Path = Path.Combine(_root, file) };
            kit.Seed = 42;
            await kit.SendToBitwigCommand.ExecuteAsync(null);

            Assert.Equal("Kit #42 is on a Drum Machine on a new track in Bitwig: Kick on C1, Snare on D1, Clap on D#1, Closed hat on F#1.",
                         kit.Status);
            using var link = await BitwigLink.ConnectAsync();
            var track = (await link.ProjectAsync()).Tracks.Single(t => t.Name == "Kit #42");
            Assert.Equal("Drum Machine", track.Devices.Single().Name);

            // Until the app finds the ID in Bitwig's files, a kit goes on the Drum Machine selected in Bitwig
            // (now the one above), leaving pads that have sounds alone.
            _db.SetSetting(KitViewModel.DrumMachineKey, "");
            kit.Slots[5].Slot.Sound = new Sound { Path = Path.Combine(_root, "Bongo.wav") };
            await kit.SendToBitwigCommand.ExecuteAsync(null);
            Assert.Equal("Kit #42 is on the Drum Machine selected in Bitwig: Percussion on C#1. The pads for Kick (C1), Snare (D1), " +
                         "Clap (D#1), Closed hat (F#1) already had sounds, so they were left alone.", kit.Status);
        }
        finally { BitwigLink.Port = port; }
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
