using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.LiveTests;
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

    /// <summary>The window, with the app's data folder in the test's own folder, so opening Settings never reaches the real one.</summary>
    MainViewModel Main(DawWatch? watch = null, PluginScanner? pluginScanner = null) =>
        new(_db, _sounds, watch, pluginScanner, Path.Combine(_root, "data"));

    [Fact]
    public async Task Activity_cards_show_a_job_start_progress_and_finish_then_go()
    {
        var feed = new ActivityFeed { Linger = TimeSpan.Zero, FadeOut = TimeSpan.Zero };
        var stopped = false;
        var card = feed.Start("Scanning", cancel: () => stopped = true);
        Assert.True(card.IsRunning);
        Assert.True(card.CanCancel);
        Assert.False(card.HasPercent);

        card.Report(40, "4 of 10");
        Assert.True(card.HasPercent);
        Assert.Equal("4 of 10", card.Detail);
        card.CancelCommand.Execute(null);
        Assert.True(stopped);

        card.Finish("Library up to date", "7 songs");
        Assert.True(card.IsDone);
        Assert.False(card.HasPercent);
        await PollUntil(() => feed.Items.Count == 0);

        // A failed job's card stays until it's read (three times as long), or closed.
        feed.Linger = TimeSpan.FromMinutes(1);
        var failed = feed.Failed("Scan failed", "Disk full");
        Assert.True(failed.IsFailed);
        Assert.Single(feed.Items);
        failed.DismissCommand.Execute(null);
        Assert.Empty(feed.Items);
    }

    static async Task PollUntil(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++) await Task.Delay(20);
        Assert.True(done());
    }

    [Fact]
    public void Project_search_matches_each_word_and_a_year()
    {
        var item = new ProjectItem(_db, new ProjectSummary
        {
            Path = "/music/Dark Roller_3.flp", Name = "Dark Roller_3", Daw = "FL Studio", Folder = "/music",
            Modified = new DateTime(2019, 6, 1), PluginNames = ["Serum"], Tags = ["dnb"], MusicalKey = "F minor",
        }, "Dark Roller");

        Assert.True(item.Matches("roller"));
        Assert.True(item.Matches("serum dark"));        // words in any order, each matching something different
        Assert.True(item.Matches("dnb 2019"));
        Assert.True(item.Matches("F minor"));            // the whole text still counts on its own
        Assert.True(item.Matches("fl studio"));
        Assert.False(item.Matches("serum 2020"));
        Assert.False(item.Matches("serum bright"));
    }

    [Fact]
    public void FirstRunOpensSettings()
    {
        var main = Main();
        Assert.Same(main.Settings, main.SelectedNav.Page);
    }

    [Fact]
    public void OpensOnThePagePickedInSettings()
    {
        _db.AddRoot(_root);
        var first = Main();
        Assert.Same(first.Projects, first.SelectedNav.Page);   // nothing picked yet

        first.Settings.StartPage = "Sound Lab";
        var next = Main();
        Assert.Same(next.Sounds, next.SelectedNav.Page);
    }

    [Fact]
    public void CanOpenOnTheLastPageUsed()
    {
        _db.AddRoot(_root);
        var main = Main();
        main.Settings.StartPage = SettingsViewModel.LastPageChoice;
        main.SelectedNav = main.Nav.First(n => n.Page == main.Plugins);

        var next = Main();
        Assert.Same(next.Plugins, next.SelectedNav.Page);
        Assert.Equal(SettingsViewModel.LastPageChoice, next.Settings.StartPage);
    }

    [Fact]
    public async Task SettingsKeepsTheAppsOwnCopyOfTheBitwigTemplate()
    {
        var data = Path.Combine(_root, "data");
        var picked = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "Bitwig", "template")).FullName, "template.bwproject");
        File.WriteAllBytes(picked, Fixtures.BitwigTemplate());

        // A template picked before the app kept its own is copied in the first time the app looks.
        _db.SetSetting(ProjectDetailViewModel.BitwigTemplateKey, picked);
        var settings = Main().Settings;
        var kept = BitwigTemplateStore.PathIn(data);
        Assert.Equal(kept, settings.BitwigTemplate);
        Assert.Equal(File.ReadAllBytes(picked), File.ReadAllBytes(kept));
        Assert.Equal($"The app's own copy, taken from {picked}.", settings.BitwigTemplateNote);
        Assert.True(settings.EditBitwigTemplateCommand.CanExecute(null));

        // Moving or deleting the original doesn't lose it.
        File.Delete(picked);
        Assert.Equal(kept, Main().Settings.BitwigTemplate);

        // Replace refuses a project that can't be the template, and keeps the one it has.
        var ui = (HeadlessUi)Ui.Current;
        var before = File.ReadAllBytes(kept);
        var song = Directory.GetFiles(_root, "*.bwproject", SearchOption.AllDirectories).First(f => f != kept);
        ui.PickedFile = song;
        await settings.ChooseBitwigTemplateCommand.ExecuteAsync(null);
        Assert.Equal(kept, settings.BitwigTemplate);
        Assert.StartsWith($"{Path.GetFileNameWithoutExtension(song)} can't be the template. The Bitwig template", settings.BitwigTemplateNote);
        Assert.Equal(before, File.ReadAllBytes(kept));

        // ...and copies in one that can, over the app's copy.
        var other = Path.Combine(_root, "other.bwproject");
        File.WriteAllBytes(other, Fixtures.BitwigTemplate(automationLane: false));
        ui.PickedFile = other;
        await settings.ChooseBitwigTemplateCommand.ExecuteAsync(null);
        Assert.Equal(kept, settings.BitwigTemplate);
        Assert.Equal(File.ReadAllBytes(other), File.ReadAllBytes(kept));
        Assert.Equal(other, _db.GetSetting(ProjectDetailViewModel.BitwigTemplateKey));
        Assert.False(File.Exists(kept + ".part"));
    }

    [Fact]
    public void TheBitwigTemplateSavedLastIsUsed()
    {
        var data = Path.Combine(_root, "data");
        var picked = Path.Combine(_root, "template.bwproject");
        File.WriteAllBytes(picked, Fixtures.BitwigTemplate(automationLane: false));
        // Kept read-only so it isn't saved over by accident: the app's copy can still be saved and replaced.
        File.SetAttributes(picked, FileAttributes.ReadOnly);
        _db.SetSetting(ProjectDetailViewModel.BitwigTemplateKey, picked);
        var kept = BitwigTemplateStore.Find(_db, data);
        Assert.Equal(BitwigTemplateStore.PathIn(data), kept);
        Assert.False(File.GetAttributes(kept!).HasFlag(FileAttributes.ReadOnly));

        // The original changed and saved again (opened from Bitwig's recent projects) is copied in again.
        File.SetAttributes(picked, FileAttributes.Normal);
        var saved = Fixtures.BitwigTemplate();
        File.WriteAllBytes(picked, saved);
        File.SetLastWriteTimeUtc(picked, File.GetLastWriteTimeUtc(kept!).AddMinutes(1));
        Assert.Equal(kept, BitwigTemplateStore.Find(_db, data));
        Assert.Equal(saved, File.ReadAllBytes(kept!));

        // The app's copy changed after that (Edit in Bitwig) is kept.
        var edited = Fixtures.BitwigTemplate(automationLane: false);
        File.WriteAllBytes(kept!, edited);
        File.SetLastWriteTimeUtc(kept!, File.GetLastWriteTimeUtc(picked).AddMinutes(1));
        Assert.Equal(kept, BitwigTemplateStore.Find(_db, data));
        Assert.Equal(edited, File.ReadAllBytes(kept!));
    }

    [Fact]
    public void SettingsSaysWhatTheBitwigTemplateIsMissing()
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.GetDirectoryName(BitwigTemplateStore.PathIn(data))!);
        File.Copy(Directory.GetFiles(_root, "*.bwproject", SearchOption.AllDirectories).First(), BitwigTemplateStore.PathIn(data));
        var settings = Main().Settings;
        Assert.Matches("^The Bitwig template needs .+\\. Add it with Edit in Bitwig, or Replace the template with another project\\.$",
                       settings.BitwigTemplateNote);

        // With no template yet, there's nothing to open or show.
        File.Delete(BitwigTemplateStore.PathIn(data));
        settings.RefreshTemplate();
        Assert.Equal("", settings.BitwigTemplate);
        Assert.Equal("", settings.BitwigTemplateNote);
        Assert.False(settings.EditBitwigTemplateCommand.CanExecute(null));
        Assert.False(settings.ShowBitwigTemplateCommand.CanExecute(null));
    }

    [Fact]
    public void ProjectsListFiltersByDaw()
    {
        _db.AddRoot(_root);
        new Scanner(_db).ScanProjects();
        _db.RemoveRoot(_root);   // keeps the projects, and stops the screen starting its own background scan
        var main = Main();
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
        var projects = Main().Projects;
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
        var settings = Main().Settings;
        await settings.RefreshLinksAsync();
        Assert.Equal(["Bitwig Studio", "FL Studio"], settings.Links.Select(l => l.Daw));
        Assert.All(settings.Links, l => Assert.Matches("^(Not installed|Installed)", l.Status));
        // Both DAWs can have more than one user folder, so either can be pointed at the right one.
        Assert.Equal([true, true], settings.Links.Select(l => l.CanChooseFolder));
        // Only FL Studio needs a MIDI port, and only Windows offers two ways to make it.
        Assert.Equal([false, OperatingSystem.IsWindows()], settings.Links.Select(l => l.CanChoosePort));
        Assert.Null(settings.Links[0].MidiPort);
        // Bitwig has live tests; FL Studio's come once its link works. No report yet, in the test's own data folder.
        Assert.Equal([true, false], settings.Links.Select(l => l.HasLiveTests));
        var live = settings.Links[0].LiveTests!;
        Assert.Equal((Path.Combine(_root, "data", "reports", "live"), null, ""), (live.Folder, live.Report, live.Summary));
    }

    [Fact]
    public async Task SettingsRunsTheLiveTestsWhileBitwigAnswers()
    {
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        if (bitwig is null) return;
        using var watch = new DawWatch(bitwig.Port, new FlLinkTests.QuietPort(), flOpen: () => false, bitwigOpen: () => true) { Retry = TimeSpan.Zero };
        var main = Main(watch);
        var tests = main.Settings.Links[0].LiveTests!;
        Assert.False(tests.RunCommand.CanExecute(null));   // until the app has seen Bitwig answer
        await PollUntil(main.Live, () => main.Live.IsAnswering(DawState.Bitwig));
        Assert.True(tests.RunCommand.CanExecute(null));
        using var link = await watch.ConnectBitwigAsync();
        var before = await link.ProjectAsync();

        // Bitwig has “Night Drive” open, which isn't a project for the tests, so they only read.
        await tests.RunCommand.ExecuteAsync(null);

        var lines = tests.Summary.Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.Matches(@"^[1-9]\d* passed, 0 failed, [1-9]\d* skipped, in ", lines[0]);
        Assert.StartsWith("Bitwig has “Night Drive” open, so the run changed nothing", lines[1]);
        Assert.EndsWith(BitwigLiveTests.HowToAllowChanges, lines[1]);
        Assert.Equal($"The report: {tests.Report}", lines[2]);
        Assert.Equal(Path.Combine(_root, "data", "reports", "live"), Path.GetDirectoryName(tests.Report));
        Assert.Contains("Bitwig Studio 6.1.2 · JavaScript script, link 2 · “Night Drive”", File.ReadAllText(tests.Report!));
        Assert.True(tests.OpenReportCommand.CanExecute(null));
        var after = await link.ProjectAsync();
        Assert.Equal(before.Tracks.Select(t => t.Name), after.Tracks.Select(t => t.Name));
        Assert.False(after.Modified);

        // The next time Settings opens, the report is there to open.
        var again = Main(watch).Settings;
        await again.RefreshLinksAsync();
        Assert.Equal(tests.Report, again.Links[0].LiveTests!.Report);
        Assert.EndsWith($": {lines[0]}", again.Links[0].LiveTests!.Summary);
    }

    [Fact]
    public void LiveTestsGoThroughWhatTheAppKnows()
    {
        ScanFixtures();
        var drumMachine = Guid.NewGuid();
        _db.SetSetting(KitViewModel.DrumMachineKey, drumMachine.ToString());
        var user = Path.Combine(_root, "Bitwig Studio");
        _db.SetSetting(SettingsViewModel.BitwigUserKey, user);
        using var watch = new DawWatch(ClosedPort(), new FlLinkTests.QuietPort(), flOpen: () => false, bitwigOpen: () => false);
        var settings = Main(watch).Settings;
        settings.PluginFolders.Add(Path.Combine(_root, "plugins"));

        var options = settings.LiveTestOptions();

        // The app's own way to Bitwig, and its folders, so a run from Settings finds what the app finds.
        Assert.Equal(new Func<CancellationToken, Task<BitwigLink>>(watch.ConnectBitwigAsync), options.Connect);
        Assert.Equal(Path.Combine(_root, "data", "reports", "live"), options.ReportFolder);
        Assert.Equal(Path.Combine(_root, "data", "probe"), options.ProbeFolder);
        Assert.Equal(settings.Probe.Folder, options.ProbeFolder);
        Assert.Contains(Path.Combine(_root, "plugins"), options.PluginFolders());
        Assert.Equal([user], options.BitwigUserFolders());
        Assert.Equal(_db.Projects().Where(p => p.Daw == "Bitwig Studio").Select(p => p.Path), options.LibraryProjects());
        Assert.Single(options.LibraryProjects());
        Assert.Equal(drumMachine, options.DrumMachine!(default));
        Assert.Empty(options.ScannedProbes);
    }

    [Fact]
    public void SettingsChoosesHowFlStudiosPortIsMade()
    {
        var before = FlSetup.Port;
        try
        {
            var fl = Main().Settings.Links.Single(l => l.Daw == "FL Studio");
            var port = fl.MidiPort!;
            Assert.Equal((0, FlMidiPort.LoopMidi, FlMidiPort.LoopMidi), (port.Choice, port.Port, FlSetup.Port));
            // Off Windows the app has no MIDI yet, so Settings says the link is Windows only for now.
            Assert.Contains(OperatingSystem.IsWindows() ? "loopMIDI" : "only does on Windows", fl.Steps);
            var changed = new List<string?>();
            fl.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            port.Choice = 1;
            // Check connection's advice follows the choice, and it's remembered.
            Assert.Equal((FlMidiPort.WindowsMidi, FlMidiPort.WindowsMidi, "windows"), (port.Port, FlSetup.Port, _db.GetSetting(FlPortSetting.Key)));
            Assert.Contains(nameof(DawLinkSetting.Steps), changed);
            if (OperatingSystem.IsWindows()) Assert.Contains("Install also makes a port called Music Organiser", fl.Steps);
            Assert.Equal(1, Main().Settings.Links[1].MidiPort!.Choice);

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
    public void AutoplayPlaysTheSoundYouPickButNotOnesTheListSelectsByItself()
    {
        var played = new List<string>();
        var player = new Recorder(played);
        ((HeadlessUi)Ui.Current).Player = () => player;
        var lab = new MainViewModel(_db, _sounds).Sounds;
        var (kick, snare) = (new Sound { Path = "kick.wav" }, new Sound { Path = "snare.wav" });

        lab.Selected = kick;
        lab.PlayPicked();
        Assert.Empty(played);   // off by default

        lab.AutoPlay = true;
        lab.Selected = snare;   // as when a sort or filter moves the selection
        Assert.Empty(played);
        lab.PlayPicked();       // a click on the row, or arrowing onto it
        lab.PlayPicked();       // clicking it again while it plays doesn't restart it
        Assert.Equal(["snare.wav"], played);
        Assert.Equal(0, snare.Plays);   // a preview isn't a play Rediscover learns from...

        lab.AuditionCommand.Execute(null);   // ...until you double-click it, which keeps it going
        lab.AuditionCommand.Execute(null);
        Assert.Equal(["snare.wav"], played);
        Assert.Equal(1, snare.Plays);

        player.Stop();          // it finished; clicking the selected row plays it again
        lab.PlayPicked();
        Assert.Equal(["snare.wav", "snare.wav"], played);

        Assert.True(new MainViewModel(_db, _sounds).Sounds.AutoPlay);
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
            var kit = Main().Sounds.Kit;
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
        var main = Main(pluginScanner: SlowPlugins(asked, go));
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
        Assert.False(main.Plugins.CanReadAll);
        // (Not the status line: without a UI thread a progress report can land after the last word.)
    }

    [Fact]
    public async Task ClosingTheAppEndsPluginsStillLoading()
    {
        var asked = Directory.CreateDirectory(Path.Combine(_root, "asked")).FullName;
        var main = Main(pluginScanner: SlowPlugins(asked, Path.Combine(_root, "go")));
        var reading = main.ReadAllParametersAsync();
        await Until(() => Directory.GetFiles(asked).Length == 2);

        main.Shutdown();
        Assert.Same(reading, await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(20))));
        Assert.Equal((0, 2), _db.ParameterCounts());   // not kept as plugins that couldn't be loaded
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
        using var watch = new DawWatch(ClosedPort(), new FlLinkTests.QuietPort(), flOpen: () => false, bitwigOpen: () => false) { Retry = TimeSpan.Zero };
        var main = Main(watch);
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
        using var watch = new DawWatch(bitwig.Port, fl, flOpen: () => true, bitwigOpen: () => true) { Retry = TimeSpan.Zero };
        var main = Main(watch);
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
        using var watch = new DawWatch(ClosedPort(), fl, flOpen: () => true, bitwigOpen: () => false) { Retry = TimeSpan.Zero };
        var main = Main(watch);
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
        using var watch = new DawWatch(bitwig.Port, fl, flOpen: () => true, bitwigOpen: () => true) { Retry = TimeSpan.Zero };
        var main = Main(watch);
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

    [Fact]
    public async Task SettingsShowsTheProbesRunningNow()
    {
        var reports = Path.Combine(_root, "probe");
        var live = Path.Combine(reports, "live");
        var now = DateTimeOffset.UtcNow;
        ProbeStatus(live, "AAAA000000000001", now, "1-instrument-AAAA0000.jsonl", playing: true, bar: 5);
        ProbeStatus(live, "AAAA000000000002", now.AddSeconds(-10), "2-instrument-AAAA0000.jsonl");   // its DAW went away
        ProbeStatus(live, "AAAA000000000003", now, "3-instrument-AAAA0000.jsonl", host: "FL Studio", version: "25.1.6", format: "VST3",
                    track: "", bar: 1, tempo: 140);
        var probe = new ProbeReportsViewModel(reports, () => []);
        Assert.Equal("", probe.RunningNote);   // before the first look

        await probe.RefreshRunningAsync();
        Assert.Equal(["AAAA000000000001", "AAAA000000000003"], probe.Running.Select(r => r.Instance));
        var row = probe.Running[0];
        Assert.Equal(("MO Probe Instrument on “Inst 3”", "Bitwig Studio 6.1.3 · CLAP · playing, bar 5 · 110 BPM", true),
                     (row.Title, row.Details, row.CanCheckKnobs));
        Assert.Equal("Knob A 0.250 · Gain 0.0 dB · Frequency 440.0 Hz · Steps 3 · Switch Off · Choice Bravo · Ramp 50.0 % · Bypass Off", row.Knobs);
        var fl = probe.Running[1];
        Assert.Equal(("MO Probe Instrument", "FL Studio 25.1.6 · VST3 · stopped at bar 1 · 140 BPM", false), (fl.Title, fl.Details, fl.CanCheckKnobs));
        Assert.Equal("", probe.RunningNote);

        // The next look changes the rows in place, so what their buttons said stays.
        row.Reply = "Answered in 85 ms";
        ProbeStatus(live, "AAAA000000000001", DateTimeOffset.UtcNow, "1-instrument-AAAA0000.jsonl", playing: true, bar: 6, gain: -12);
        await probe.RefreshRunningAsync();
        Assert.Same(row, probe.Running[0]);
        Assert.Equal(("Bitwig Studio 6.1.3 · CLAP · playing, bar 6 · 110 BPM", "Answered in 85 ms"), (row.Details, row.Reply));
        Assert.StartsWith("Knob A 0.250 · Gain -12.0 dB · ", row.Knobs);

        // Their DAWs closed, taking them off.
        File.Delete(Path.Combine(live, "AAAA000000000001.json"));
        File.Delete(Path.Combine(live, "AAAA000000000003.json"));
        await probe.RefreshRunningAsync();
        Assert.Empty(probe.Running);
        Assert.Equal(ProbeReportsViewModel.NoneRunning, probe.RunningNote);
    }

    [Fact]
    public async Task ProbeButtonsSendCommandsAndSayWhenThereIsNoAnswer()
    {
        // A probe whose status is there, but nothing takes what's dropped in its command folder.
        var reports = Path.Combine(_root, "probe");
        var live = Path.Combine(reports, "live");
        ProbeStatus(live, "AAAA000000000001", DateTimeOffset.UtcNow, "1-instrument-AAAA0000.jsonl");
        var probe = new ProbeReportsViewModel(reports, () => []) { AnswerWait = TimeSpan.FromMilliseconds(300) };
        await probe.RefreshRunningAsync();
        var row = Assert.Single(probe.Running);

        await row.BeepCommand.ExecuteAsync(null);
        var sent = Assert.Single(Directory.GetFiles(Path.Combine(live, "AAAA000000000001.in")));
        Assert.EndsWith(".json", sent);
        Assert.Equal("beep", (string?)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(sent))!["cmd"]);
        Assert.StartsWith("No answer", row.Reply);

        await row.PingCommand.ExecuteAsync(null);
        Assert.Equal("No answer in 0.3 s, though it's still running.", row.Reply);
    }

    [Fact]
    public async Task ProbeButtonsHearBackFromARunningProbe()
    {
        var reports = Path.Combine(_root, "probe");
        // The knob check gets as far as asking Bitwig, which a stand-in says isn't answering.
        var probe = new ProbeReportsViewModel(reports, () => [], _ => Task.FromException<BitwigLink>(new DawLinkException(BitwigLink.NotRunning)));
        await using (new FakeProbe(reports))
        {
            await probe.RefreshRunningAsync();
            var row = Assert.Single(probe.Running);
            await row.PingCommand.ExecuteAsync(null);
            Assert.Matches("^Answered in [0-9]+ ms$", row.Reply);
            await row.BeepCommand.ExecuteAsync(null);
            Assert.Equal("Beeped: listen for a short tone on its track.", row.Reply);

            Assert.True(row.CanCheckKnobs);
            await row.CheckKnobsCommand.ExecuteAsync(null);
            Assert.Equal((BitwigLink.NotRunning, false), (row.CheckResult, row.Checking));
        }
        await probe.RefreshRunningAsync();
        Assert.Empty(probe.Running);
    }

    /// <summary>A status as MO Probe writes it while it runs (native/README.md, "MO Probe"), with its command folder.</summary>
    static void ProbeStatus(string live, string instance, DateTimeOffset t, string report, string host = "Bitwig Studio",
                            string version = "6.1.3", string format = "CLAP", string track = "Inst 3", bool playing = false,
                            int bar = 4, double tempo = 110, double gain = 0)
    {
        Directory.CreateDirectory(Path.Combine(live, instance + ".in"));
        var status = new System.Text.Json.Nodes.JsonObject
        {
            ["probe"] = "instrument", ["instance"] = instance, ["identity"] = instance, ["label"] = "", ["host"] = host,
            ["hostVersion"] = version, ["format"] = format, ["track"] = track, ["trackColor"] = "", ["report"] = report,
            ["t"] = t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["pid"] = 1234, ["active"] = true, ["playing"] = playing, ["beats"] = (bar - 1) * 4.0, ["bar"] = bar, ["tempo"] = tempo,
            ["commands"] = 0,
            ["params"] = new System.Text.Json.Nodes.JsonObject
            {
                ["1001"] = 0.25, ["1002"] = gain, ["1003"] = 440, ["1004"] = 3, ["1005"] = 0, ["1006"] = 1, ["1007"] = 50, ["1008"] = 0,
            },
        };
        File.WriteAllText(Path.Combine(live, instance + ".json"), status.ToJsonString());
    }

    /// <summary>Puts the three test projects in the library, without the screens starting a scan of their own.</summary>
    void ScanFixtures()
    {
        _db.AddRoot(_root);
        new Scanner(_db).ScanProjects();
        _db.RemoveRoot(_root);
    }

    /// <summary>A port on this computer that nothing listens on, standing in for a Bitwig that isn't open.</summary>
    internal static int ClosedPort()
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
        public Func<IAudioPlayer> Player { get; set; } = () => new Silence();
        public IAudioPlayer CreateAudioPlayer() => Player();
        public Task<string?> PickFolder(string title) => Task.FromResult<string?>(null);
        public string? PickedFile { get; set; }
        public Task<string?> PickFile(string title, string filterName, string extension) => Task.FromResult(PickedFile);
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

    sealed class Recorder(List<string> played) : IAudioPlayer
    {
        public event Action? Stopped { add { } remove { } }
        public string? Playing { get; private set; }
        public double? Position => Playing is null ? null : 0;
        public void Seek(double seconds) { }
        public void Play(string path, double startSeconds = 0) { played.Add(path); Playing = path; }
        public void Play(float[] mono, int sampleRate, string key, double startSeconds = 0) { played.Add(key); Playing = key; }
        public void Stop() => Playing = null;
        public void Dispose() { }
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
