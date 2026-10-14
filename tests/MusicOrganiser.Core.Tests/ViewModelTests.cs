using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Plugins;
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
        // Only FL Studio needs a MIDI port, and only Windows offers two ways to make it.
        Assert.Equal([false, OperatingSystem.IsWindows()], settings.Links.Select(l => l.CanChoosePort));
        Assert.Null(settings.Links[0].MidiPort);
    }

    [Fact]
    public void SettingsChoosesHowFlStudiosPortIsMade()
    {
        var before = FlSetup.Port;
        try
        {
            var fl = new MainViewModel(_db, _sounds).Settings.Links.Single(l => l.Daw == "FL Studio");
            var port = fl.MidiPort!;
            Assert.Equal((0, FlMidiPort.LoopMidi, FlMidiPort.LoopMidi), (port.Choice, port.Port, FlSetup.Port));
            Assert.Contains(OperatingSystem.IsMacOS() ? "IAC Driver" : "loopMIDI", fl.Steps);
            var changed = new List<string?>();
            fl.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            port.Choice = 1;
            // Check connection's advice follows the choice, and it's remembered.
            Assert.Equal((FlMidiPort.WindowsMidi, FlMidiPort.WindowsMidi, "windows"), (port.Port, FlSetup.Port, _db.GetSetting(FlPortSetting.Key)));
            Assert.Contains(nameof(DawLinkSetting.Steps), changed);
            if (!OperatingSystem.IsMacOS()) Assert.Contains("Install also makes a port called Music Organiser", fl.Steps);
            Assert.Equal(1, new MainViewModel(_db, _sounds).Settings.Links[1].MidiPort!.Choice);

            port.Choice = 0;
            Assert.Equal((FlMidiPort.LoopMidi, "loopmidi"), (FlSetup.Port, _db.GetSetting(FlPortSetting.Key)));
            if (!OperatingSystem.IsWindows()) Assert.False(fl.ShowGetLoopMidi);
        }
        finally
        {
            FlSetup.Port = before;
        }
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
                ("Lead", "In Bitwig: Serum with the project's settings, and its notes as a clip. Checked in Bitwig afterwards: it's all there."),
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

    [Fact]
    public async Task AScanGoesBeforeReadingParametersWhichCarriesOnAfter()
    {
        var (asked, go) = (Directory.CreateDirectory(Path.Combine(_root, "asked")).FullName, Path.Combine(_root, "go"));
        var main = new MainViewModel(_db, _sounds, pluginScanner: SlowPlugins(asked, go));
        Assert.True(main.Plugins.CanReadAll);
        var reading = main.ReadAllParametersAsync();
        await Until(() => Directory.GetFiles(asked).Length == 2);   // both plugins loading, and taking their time
        File.WriteAllText(go, "");

        // A scan asked for now (a folder added, a new project saved in a DAW) doesn't wait behind the whole read...
        var scan = main.ScanAsync(false, plugins: false);
        Assert.Same(scan, await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(20))));
        await reading;

        // ...which carries on after it, loading the two plugins it was loading again.
        await Until(() => _db.ParameterCounts() == (2, 0) && !main.IsScanning);
        Assert.All(new[] { "alpha", "beta" }, id => Assert.Equal(2, File.ReadAllLines(Path.Combine(asked, id)).Length));
        Assert.StartsWith("Parameters read for 2 plugins", main.ScanStatus);
        Assert.False(main.Plugins.CanReadAll);
    }

    [Fact]
    public async Task ClosingTheAppEndsPluginsStillLoading()
    {
        var asked = Directory.CreateDirectory(Path.Combine(_root, "asked")).FullName;
        var main = new MainViewModel(_db, _sounds, pluginScanner: SlowPlugins(asked, Path.Combine(_root, "go")));
        var reading = main.ReadAllParametersAsync();
        await Until(() => Directory.GetFiles(asked).Length == 2);

        main.Shutdown();
        Assert.Same(reading, await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(20))));
        Assert.Equal((0, 2), _db.ParameterCounts());   // not kept as plugins that couldn't be loaded
        Assert.StartsWith("Stopped reading parameters", main.ScanStatus);
    }

    /// <summary>
    /// Two installed plugins, alpha and beta, without their parameters, and a stand-in for mo-scan that loads one by
    /// ID: it adds a line to a file named after it in <paramref name="asked"/>, then takes a minute unless
    /// <paramref name="go"/> exists, when it reports a Gain knob.
    /// </summary>
    PluginScanner SlowPlugins(string asked, string go)
    {
        var file = Path.Combine(_root, "Plugins.clap");
        File.WriteAllText(file, "");
        string Line(string id, bool knob)
        {
            var line = new Dictionary<string, object> { ["format"] = "CLAP", ["path"] = file, ["id"] = id, ["name"] = id, ["vendor"] = "Tests" };
            if (knob) line["params"] = new[] { new { id = 0, name = "Gain", text = "0.5" } };
            return System.Text.Json.JsonSerializer.Serialize(line);
        }
        _db.SavePluginScan(new PluginFileScan(file, [ScannedPlugin.Parse(Line("alpha", false))!, ScannedPlugin.Parse(Line("beta", false))!]),
                           PluginScanner.Stamp(file));

        string path;
        if (OperatingSystem.IsWindows())
        {
            path = Path.Combine(_root, "slow.cmd");
            File.WriteAllLines(path, ["@echo off", $">>\"{asked}\\%~2\" echo %~2", $"if exist \"{go}\" goto read", "ping -n 60 127.0.0.1 >nul", "exit /b 0",
                                      ":read", "echo " + Line("@ID@", true).Replace("@ID@", "%~2")]);
        }
        else
        {
            path = Path.Combine(_root, "slow.sh");
            File.WriteAllLines(path, ["#!/bin/sh", $"printf '%s\\n' \"$2\" >> '{asked}/'\"$2\"", $"if [ ! -e '{go}' ]; then sleep 60; exit 0; fi",
                                      "printf '%s\\n' '" + Line("@ID@", true).Replace("@ID@", "'\"$2\"'") + "'"]);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return new PluginScanner(path);
    }

    [Fact]
    public async Task SidebarSaysWhenNoDawIsOpen()
    {
        using var watch = new DawWatch(ClosedPort(), Fixtures.TempDir(), flOpen: () => false, bitwigOpen: () => false) { Retry = TimeSpan.Zero };
        var main = new MainViewModel(_db, _sounds, watch);
        // (A look at FL Studio is skipped while another request to it is under way, as in other tests running alongside.)
        await PollUntil(main.Live, () => main.Live.Rows.All(r => r.Text != "Not connected"));
        Assert.Equal([("Bitwig", "Not open", false), ("FL Studio", "Not open", false)],
                     main.Live.Rows.Select(r => (r.Name, r.Text, r.Answering)));
        Assert.All(main.Settings.Links, l => Assert.Equal("Not answering right now.", l.Live));

        // Clicking one goes to Settings, where the links are set up.
        main.ShowProjects();
        main.Live.Rows[1].OpenCommand.Execute(null);
        Assert.Same(main.Settings, main.SelectedNav.Page);
    }

    [Fact]
    public async Task SidebarShowsWhatEachDawHasOpen()
    {
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        using var fl = FlLinkTests.ScriptRunner.Start();
        if (bitwig is null || fl is null) return;
        ScanFixtures();
        bitwig.Change("""{"project": "Loop Idea"}""");
        fl.Change("""{"modified": 1}""");
        using var watch = new DawWatch(bitwig.Port, fl.Folder, flOpen: () => true, bitwigOpen: () => true) { Retry = TimeSpan.Zero };
        var main = new MainViewModel(_db, _sounds, watch);
        await PollUntil(main.Live, () => main.Live.Rows.All(r => r.Answering));

        Assert.Equal([("Bitwig", "Loop Idea", ""), ("FL Studio", "Night Drive", "Unsaved changes")],
                     main.Live.Rows.Select(r => (r.Name, r.Text, r.Detail)));
        Assert.Equal([("Loop Idea", "Open in Bitwig"), ("Night Drive", "Open in FL Studio"), ("Sunday Song", "")],
                     main.Projects.Items.Select(i => (i.Name, i.OpenIn)).Order());
        Assert.Equal("✓ Answering now: Bitwig Studio 6.1.2, with “Loop Idea” open.", main.Settings.Links[0].Live);

        // Bitwig's row shows its project, which the app checks with Bitwig by itself.
        main.Live.Rows[0].OpenCommand.Execute(null);
        var page = main.Projects.Detail!;
        Assert.Equal(("Loop Idea", "Open in Bitwig now", true), (page.Item.Name, page.LiveText, page.IsLive));
        await Until(() => page.HasCheckLines);
        Assert.Equal("Bitwig has this project open, so the app checked it there by itself.", page.CheckLines[0].Text);

        main.Projects.Selected = main.Projects.Items.Single(i => i.Name == "Night Drive");
        Assert.Equal("Open in FL Studio now, with unsaved changes", main.Projects.Detail!.LiveText);
    }

    [Fact]
    public async Task RereadsAProjectWhenItsDawSavesIt()
    {
        using var fl = FlLinkTests.ScriptRunner.Start();
        if (fl is null) return;
        ScanFixtures();
        fl.Change("""{"modified": 1}""");
        using var watch = new DawWatch(ClosedPort(), fl.Folder, flOpen: () => true, bitwigOpen: () => false) { Retry = TimeSpan.Zero };
        var main = new MainViewModel(_db, _sounds, watch);
        await PollUntil(main.Live, () => main.Live.IsAnswering(DawState.FlStudio));

        // FL Studio saves the project: the file changes, and the project's unsaved changes clear.
        var path = Path.Combine(_root, "Night Drive.flp");
        var saved = DateTime.UtcNow.AddMinutes(1);
        File.SetLastWriteTimeUtc(path, saved);
        fl.Change("""{"modified": 0}""");
        await PollUntil(main.Live, () => main.ScanStatus.Length > 0);
        Assert.StartsWith("Re-read Night Drive after FL Studio saved it · ", main.ScanStatus);
        Assert.Equal(new DateTimeOffset(saved).ToUnixTimeSeconds(), _db.Projects().Single(p => p.Path == path).Mtime);
    }

    [Fact]
    public async Task MidiWindowSendsFlStudiosKnobsAsTheyAreNow()
    {
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        using var fl = FlLinkTests.ScriptRunner.Start();
        if (bitwig is null || fl is null) return;
        // FL Studio has the project open, with the Lead's Filter Cutoff turned up since it was saved.
        fl.Change("""{"channels": [["Lead", "Serum"], ["Kick", null], ["Bass", "3x Osc"]]}""");
        fl.Change("""{"knob": [0, 3, 0.9]}""");
        fl.Change("""{"modified": 1}""");
        using var watch = new DawWatch(bitwig.Port, fl.Folder, flOpen: () => true, bitwigOpen: () => true) { Retry = TimeSpan.Zero };
        var main = new MainViewModel(_db, _sounds, watch);
        await PollUntil(main.Live, () => main.Live.Rows.All(r => r.Answering));

        var path = Path.Combine(_root, "Night Drive.flp");
        var midi = new MidiPreviewViewModel(Readers.For(path)!, path, "Night Drive", Path.Combine(_root, "Exports"), 128, _db, main.Live);
        await midi.LoadAsync();
        await midi.SendAllToBitwigCommand.ExecuteAsync(null);

        Assert.Equal("Sent 3 channels to Bitwig, each on a new track named after it. 2 didn't come across whole: each says what under " +
                     "its name. FL Studio has unsaved changes: the knobs are as it has them now, but the notes and samples are from the last save.",
                     midi.Message);
        Assert.Equal("In Bitwig: Serum with the project's settings, and its notes as a clip. 2 knobs set to match FL Studio now. " +
                     "Checked in Bitwig afterwards: it's all there.", midi.Channels.Single(r => r.Name == "Lead").Sent);

        // The Lead's Serum in Bitwig has FL Studio's knobs. (Bitwig moves its selection a moment after being asked.)
        using var link = await watch.ConnectBitwigAsync();
        var lead = (await link.ProjectAsync()).Tracks.Single(t => t.Name == "Lead");
        await link.SelectAsync(lead.Index, lead.Devices.Single(d => d.Name == "Serum").Index);
        var shown = await link.SelectedDeviceAsync();
        for (var waited = Stopwatch.StartNew(); shown.Track != "Lead" && waited.Elapsed < TimeSpan.FromSeconds(5); )
        {
            await Task.Delay(100);
            shown = await link.SelectedDeviceAsync();
        }
        Assert.Equal(("Lead", "Serum"), (shown.Track, shown.Device?.Name));
        Assert.Equal([("Master Volume", 0.7), ("A Octave", 0.5), ("Osc B Level", 0.5), ("Filter Cutoff", 0.9)],
                     shown.Params.Select(p => (p.Name, Math.Round(p.Value ?? -1, 3))));
    }

    [Fact]
    public async Task SetUpInstallsTheScriptThenChecksTheLink()
    {
        // A Bitwig user folder of the test's own, and a check that answers or doesn't, so nothing reaches a real Bitwig.
        var user = Path.Combine(_root, "Bitwig Studio");
        var found = false;
        var answers = true;
        Task<string> Check() => answers
            ? Task.FromResult("Connected to Bitwig Studio 6.1.2, with “Night Drive” open.")
            : Task.FromException<string>(new DawLinkException("Bitwig isn't answering."));
        var link = new DawLinkSetting("Bitwig Studio", () => "", () => DawScripts.Bitwig(user), Check, found: () => found);

        Assert.Equal("Bitwig Studio: not found on this computer, so it was skipped.", await link.SetUpAsync());
        Assert.Equal("Skipped: the app didn't find Bitwig Studio on this computer.", link.Result);
        Assert.False(Directory.Exists(user));

        found = true;
        Assert.Equal("✓ Bitwig Studio: set up and answering.", await link.SetUpAsync());
        Assert.Equal("Installed the script. Connected to Bitwig Studio 6.1.2, with “Night Drive” open.", link.Result);
        Assert.Equal(ScriptState.Installed, DawScripts.Bitwig(user).State);
        Assert.StartsWith("Installed in ", link.Status);

        answers = false;
        Assert.Equal("Bitwig Studio: the script is in place, but Bitwig Studio isn't answering yet. What's left is below.",
                     await link.SetUpAsync());
        Assert.Equal("The script was already installed. Bitwig isn't answering.", link.Result);
    }

    /// <summary>Puts the three test projects in the library, without the screens starting a scan of their own.</summary>
    void ScanFixtures()
    {
        _db.AddRoot(_root);
        new Scanner(_db).ScanProjects();
        _db.RemoveRoot(_root);
    }

    /// <summary>A port on this computer that nothing listens on, standing in for a Bitwig that isn't open.</summary>
    static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Asks the DAWs (as the app does every couple of seconds) until <paramref name="done"/>.</summary>
    static async Task PollUntil(LiveDawsViewModel live, Func<bool> done)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            await live.PollAsync();
            if (done()) return;
            if (waited.Elapsed > TimeSpan.FromSeconds(20))
                Assert.Fail($"The DAWs didn't get there: {string.Join(", ", live.Rows.Select(r => $"{r.Name}: {r.Text}"))}");
            await Task.Delay(50);
        }
    }

    static async Task Until(Func<bool> done)
    {
        for (var waited = Stopwatch.StartNew(); !done(); await Task.Delay(50))
            if (waited.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail("Timed out.");
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
