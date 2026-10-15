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
    public void A_new_save_keeps_the_songs_rating_and_clearing_it_sticks()
    {
        void Save(string name, int day)
        {
            var file = Path.Combine(_root, name + ".flp");
            File.WriteAllBytes(file, [0]);
            File.SetLastWriteTime(file, new DateTime(2026, 10, day));
            _db.Upsert(new LibraryDb.IndexedFile(file, day, 100, new ProjectInfo { Daw = "FL Studio" }, null, [], []));
        }
        ProjectItem Song() => Main().Projects.Items.Single(i => i.Name == "Didums");
        Save("Didums_1", 1);
        var first = Song();
        first.Rating = 4;
        first.Status = "Mixing";
        first.Favourite = true;
        first.Notes = "needs vocals";

        Save("Didums_2", 2);
        var song = Song();
        Assert.Equal("Didums_2", song.FileName);
        Assert.Equal((4, "Mixing", true, "needs vocals"), (song.Rating, song.Status, song.Favourite, song.Notes));

        song.Rating = 0;
        song.Favourite = false;
        song = Song();
        Assert.Equal((0, false), (song.Rating, song.Favourite));
        Assert.All(_db.Projects(), p => Assert.Equal(0, p.Rating));
    }

    [Fact]
    public void A_song_sits_under_the_year_it_was_first_made_and_shows_its_rework()
    {
        ProjectSummary Save(string name, DateTime saved, int notes, string[] plugins) => new()
        {
            Name = name, Path = $"/music/{name}.flp", Daw = "FL Studio", Folder = "/music", Modified = saved,
            NoteCount = notes, LengthBars = 32, TrackCount = 4, PluginNames = plugins,
        };
        var newest = Save("Didums_77", DateTime.Now.AddDays(-1), 400, ["Serum 2", "Vital"]);
        var item = new ProjectItem(_db, newest, "Didums", [Save("Didums_12", new(2019, 2, 2), 120, ["Serum"])]);

        Assert.Equal("2019", item.WhenGroup);
        Assert.Equal("2 Feb 2019", item.ShortDate);
        Assert.EndsWith($"2 saves · reworked {newest.Modified.Year}", item.DetailsLine);

        var detail = new ProjectDetailViewModel(_db, item, Main());
        Assert.True(detail.HasOpenChoices);
        Assert.Equal([$"{newest.Modified.Year} rework (newest) · Didums_77 · {newest.Modified:d MMM yyyy}", "2019 original · Didums_12 · 2 Feb 2019"],
                     detail.RunChoices.Select(r => r.Header));
        Assert.StartsWith("First made 2 Feb 2019 · saved ", detail.DatesText);

        // Overview's glance: the newest save first, then the older ones by date.
        Assert.Equal(["Didums_77", "Didums_12"], detail.GlanceSaves.Select(s => s.Name));
        Assert.True(detail.GlanceSaves[0].Newest);
        Assert.Equal("2 Feb 2019", detail.GlanceSaves[1].WhenText);
        Assert.Equal("None used", detail.GlanceSamples);
    }

    [Fact]
    public async Task A_fixed_copy_is_listed_with_its_song_and_still_asks_whether_it_loaded_after_leaving_it()
    {
        var lib = Path.Combine(_root, "Lib", "Drums");
        Directory.CreateDirectory(lib);
        File.WriteAllBytes(Path.Combine(lib, "Kick 01.wav"), [0]);
        _db.SetList(Scanner.SampleFoldersKey, [Path.Combine(_root, "Lib")]);
        new Scanner(_db).ScanSampleFolders();
        ScanFixtures();
        var projects = Main().Projects;
        projects.Reload();
        projects.Selected = projects.Items.Single(i => i.Daw == "FL Studio");
        var detail = projects.Detail!;

        await detail.PlanFixCommand.ExecuteAsync(null);
        var kick = detail.FixRows.Single(r => r.HasLookedFor);
        Assert.Equal(@"FL Studio looked for: C:\Samples\Drums\Kick 01.wav", kick.LookedFor);
        await detail.SaveFixedCommand.ExecuteAsync(null);

        // Listed straight away as the song's newest save, still picked, still asking.
        var song = projects.Items.Single(i => i.Daw == "FL Studio");
        Assert.Equal("Night Drive (fixed)", song.FileName);
        Assert.Equal(["Night Drive"], song.OlderSaves.Select(o => o.Name));
        Assert.Same(song, projects.Selected);
        Assert.True(projects.Detail!.HasPendingFix);

        // Leave it and come back: the question is still there until it's answered.
        projects.Selected = projects.Items.First(i => i.Daw != "FL Studio");
        projects.Selected = song;
        Assert.True(projects.Detail!.HasPendingFix);
        projects.Detail.KeepFixedCommand.Execute(null);
        projects.Selected = projects.Items.First(i => i.Daw != "FL Studio");
        projects.Selected = song;
        Assert.False(projects.Detail!.HasPendingFix);
    }

    [Fact]
    public void The_song_panel_takes_two_thirds_until_its_divider_is_moved_and_then_keeps_its_place()
    {
        Assert.Equal(2 / 3.0, Main().Projects.PanelShare, 3);
        Main().Projects.PanelShare = 0.55;
        Assert.Equal(0.55, Main().Projects.PanelShare, 3);
        Main().Projects.PanelShare = 1;   // never squeezes the list to nothing
        Assert.Equal(0.9, Main().Projects.PanelShare, 3);
    }

    [Fact]
    public void Appearance_is_remembered_and_light_accents_get_dark_text()
    {
        var look = new AppearanceViewModel(_db);
        Assert.Equal("Graphite", look.Theme.Name);
        Assert.Equal("#06180F", look.AccentText);

        look.Theme = AppearanceViewModel.Themes.Single(t => t.Name == "Retro");
        Assert.Equal(AppearanceViewModel.SpaceFont, look.FontName);   // the theme's own font
        look.Accent = AppearanceViewModel.Accents.Single(a => a.Name == "Amber");
        look.Size = 110;
        look.LessMotion = true;

        var again = new AppearanceViewModel(_db);
        Assert.Equal("Retro", again.Theme.Name);
        Assert.Equal("#F2A03D", again.AccentColour);
        Assert.True(AppearanceViewModel.IsLight(again.AccentColour));
        Assert.NotEqual("#FFFFFF", again.AccentText);
        Assert.Equal(110, again.Size);
        Assert.True(again.LessMotion);

        again.ResetCommand.Execute(null);
        Assert.Equal("Graphite", new AppearanceViewModel(_db).Theme.Name);
    }

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
    public async Task SettingsKeepsTheBitwigUserFolderAPickedExtensionsFolderIsIn()
    {
        var bitwig = Path.Combine(_root, "K", "Bitwig Studio");
        foreach (var sub in new[] { "Extensions", "Library", "Projects" }) Directory.CreateDirectory(Path.Combine(bitwig, sub));
        var link = Main().Settings.Links.Single(l => l.Daw == "Bitwig Studio");
        var ui = (HeadlessUi)Ui.Current;

        ui.PickedFolder = Path.Combine(bitwig, "Extensions");
        await link.ChooseFolderCommand.ExecuteAsync(null);
        Assert.Equal(bitwig, _db.GetSetting(SettingsViewModel.BitwigUserKey));
        Assert.StartsWith($"Using {bitwig}.", link.Result);

        ui.PickedFolder = Path.Combine(_root, "K");
        await link.ChooseFolderCommand.ExecuteAsync(null);
        Assert.Equal(bitwig, _db.GetSetting(SettingsViewModel.BitwigUserKey));
        Assert.StartsWith("That isn't Bitwig's user folder", link.Result);
        ui.PickedFolder = null;
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
        // The chosen one, then each Bitwig Studio folder found in Documents (where Bitwig may be keeping other things).
        Assert.Equal([user, .. DawScripts.BitwigUserFolders()], options.BitwigUserFolders());
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
    public void Sounds_open_shuffled_and_can_be_ordered_by_how_much_you_like_them()
    {
        ((HeadlessUi)Ui.Current).Player = () => new Recorder(new List<string>());
        _soundDb.Save(new[] { "Kick 01", "Snare 02", "Hat 03", "Pad 04", "Bass 05" }.Select(n =>
            new SoundLibrary.Analysed($"/samples/{n}.wav", 1, 1, null, null, null, null)));
        _sounds.Reload();
        var lab = Main().Sounds;
        Assert.Equal(SoundsViewModel.Shuffled, lab.SortBy);
        Assert.Equal(SoundsViewModel.Tiles, lab.ViewMode);
        Sound Named(string name) => lab.Items.Single(s => s.Name == name);

        // Played three times, dragged out once, a favourite: most liked first, and the tooltip says why.
        lab.Selected = Named("Pad 04");
        foreach (var _ in Enumerable.Range(0, 3)) { lab.PlayCommand.Execute(null); lab.StopPlayingCommand.Execute(null); }
        lab.MakeDrag([Named("Hat 03")]);
        lab.Selected = Named("Bass 05");
        lab.ToggleFavouriteCommand.Execute(null);
        lab.SortBy = SoundsViewModel.MostLiked;
        Assert.Equal(["Bass 05", "Pad 04", "Hat 03"], lab.Shown.Take(3).Select(s => s.Name));
        Assert.Equal("Played 3 times", Named("Pad 04").LikedTip);
        Assert.Equal(1, Named("Bass 05").LikedShare);

        // History: what you played on purpose, newest first.
        lab.ViewMode = SoundsViewModel.History;
        Assert.Equal(["Pad 04"], lab.HistoryItems.Select(h => h.Sound.Name));

        // Settings can make a play count for more than a favourite.
        Main().Settings.Liked.Play = 20;
        var again = Main().Sounds;
        again.SortBy = SoundsViewModel.MostLiked;
        again.ViewMode = SoundsViewModel.Tiles;
        Assert.Equal("Pad 04", again.Shown[0].Name);

        // The page remembers tiles or list, and four tiles to a row make two rows of five sounds.
        again.ViewMode = SoundsViewModel.List;
        Assert.Equal(SoundsViewModel.List, Main().Sounds.ViewMode);
        again.ViewMode = SoundsViewModel.Tiles;
        again.TileColumns = 4;
        Assert.Equal([4, 1], again.TileRows.Select(r => r.Tiles.Count));
    }

    [Fact]
    public void The_galaxy_places_sounds_plays_what_you_sweep_and_turns_the_trail_into_a_kit()
    {
        var played = new List<string>();
        ((HeadlessUi)Ui.Current).Player = () => new Recorder(played);
        float[] Kickish(int i) => [1, 0.1f * i, 0, 0, 0, 0];
        float[] Padish(int i) => [0, 0, 0, 1, 0.1f * i, 0];
        _soundDb.Save(Enumerable.Range(0, 6).SelectMany(i => new[]
        {
            new SoundLibrary.Analysed($"/samples/Kicks/Kick {i}.wav", 1, 1, new SoundAnalysis { Features = Kickish(i) }, null, null, "Kick"),
            new SoundLibrary.Analysed($"/samples/Pads/Pad {i}.wav", 1, 1, new SoundAnalysis { Features = Padish(i) }, null, null, "Pad"),
        }));
        _sounds.Reload();
        _sounds.UpdateGalaxy(SoundOrganiser.Both);
        Assert.False(_sounds.GalaxyOutOfDate(SoundOrganiser.Both));

        var lab = Main().Sounds;
        lab.ViewMode = SoundsViewModel.Galaxy;
        Assert.Equal(12, lab.Stars.Count);
        Assert.Equal(12, lab.ShownStars);
        // No CLAP here: Both falls back to the app's own measurements, and says so.
        Assert.True(lab.HasGalaxyWarning);
        Assert.Contains("own measurements", lab.GalaxyWarning);
        lab.PlacedBy = SoundOrganiser.OwnOnly;
        Assert.False(lab.HasGalaxyWarning);

        // Filters dim stars rather than removing them, so the galaxy keeps its shape.
        lab.SearchText = "Kick";
        Assert.Equal(12, lab.Stars.Count);
        Assert.Equal(6, lab.ShownStars);
        lab.SearchText = "";

        // Colours: by type, then by pack, with a key to match.
        Assert.Contains(lab.Legend, l => l.Label == "Kick");
        lab.ColourBy = "Pack";
        Assert.Contains(lab.Legend, l => l.Label == "Pads");
        Assert.Equal("Pack", Main().Sounds.ColourBy);   // remembered

        // Sweeping previews (not a deliberate play) and builds a trail, newest first, without repeats.
        Sound Named(string n) => lab.Items.Single(s => s.Name == n);
        lab.SweepCommand.Execute(Named("Kick 1"));
        lab.SweepCommand.Execute(Named("Pad 2"));
        lab.SweepCommand.Execute(Named("Kick 1"));
        Assert.Equal(["Kick 1", "Pad 2"], lab.Trail.Select(s => s.Name));
        Assert.Equal(0, Named("Kick 1").Plays);
        Assert.Equal(3, played.Count);
        // Letting go after sweeping stops the preview; it doesn't count.
        lab.EndSweepCommand.Execute(null);
        Assert.Equal(0, Named("Kick 1").Plays);
        lab.SweepCommand.Execute(Named("Kick 1"));
        // Clicking the star that's previewing makes it count.
        lab.PickStarCommand.Execute(Named("Kick 1"));
        Assert.Equal(1, Named("Kick 1").Plays);
        Assert.Equal(Named("Kick 1"), lab.Selected);

        // The trail becomes a kit, oldest first, with every pad locked, and the Kit builder opens.
        lab.TrailToKitCommand.Execute(null);
        Assert.Equal(["Pad 2", "Kick 1"], lab.Kit.Slots.Select(s => s.SoundName));
        Assert.All(lab.Kit.Slots, s => Assert.True(s.Locked));
        Assert.Equal(2, lab.TabIndex);
        Assert.Equal(2, lab.TrailDrag.Sounds.Count);
    }

    [Fact]
    public void The_plugins_page_puts_first_what_would_open_the_most_projects()
    {
        void Song(string name, params string[] plugins)
        {
            var info = new ProjectInfo { Daw = "FL Studio" };
            info.Plugins.AddRange(plugins.Select(n => new PluginRef(n, "Vendor", PluginKind.Instrument, "VST2")));
            var file = Path.Combine(_root, name + ".flp");
            File.WriteAllBytes(file, [0]);
            _db.Upsert(new LibraryDb.IndexedFile(file, 1, 100, info, null, [], []));
        }
        Song("A", "Bass Master");
        Song("B", "Bass Master");
        Song("C", "Bass Master", "Old Verb");
        Song("D", "Old Verb", "Rare Synth");
        Song("E", "Serum");
        _db.ReplaceInstalledPlugins(["Serum", "Old Verb 2"]);

        var page = Main().Plugins;
        // Bass Master is the last missing plugin of A and B; Old Verb of none (C and D miss others too).
        Assert.Equal("Bass Master", page.Needed[0].Name);
        Assert.Equal(2, page.Needed[0].Count);
        Assert.Equal("projects it opens", page.Needed[0].CountLabel);
        Assert.Contains("used in 3", page.Needed[0].Details);
        Assert.Equal(["Bass Master", "Old Verb", "Rare Synth"], page.Needed.Select(n => n.Name));
        Assert.Contains("4 of your projects are missing a plugin", page.NeededSummary);
        Assert.Contains("these 3 and 4 of them open", page.NeededSummary);
        Assert.True(page.Needed[1].HasOtherVersion);   // Old Verb 2 is installed

        // Chips filter the full list; picking a plugin lists its projects, newest first.
        page.Filter = PluginsViewModel.Missing;
        Assert.Equal(3, page.View!.Cast<PluginUsage>().Count());
        page.Filter = PluginsViewModel.Installed_;
        Assert.Equal(["Serum"], page.View!.Cast<PluginUsage>().Select(p => p.Name));
        page.Needed[0].PickCommand.Execute(null);
        Assert.Equal("Used in 3 projects", page.ProjectsTitle);
        Assert.Equal(["A", "B", "C"], page.Projects.Select(p => p.Name).Order());
    }

    [Fact]
    public void A_click_always_plays_and_Autoplay_plays_what_you_arrow_onto_but_not_what_the_list_selects_by_itself()
    {
        var played = new List<string>();
        var player = new Recorder(played);
        ((HeadlessUi)Ui.Current).Player = () => player;
        var lab = new MainViewModel(_db, _sounds).Sounds;
        var (kick, snare) = (new Sound { Path = "kick.wav" }, new Sound { Path = "snare.wav" });

        lab.Selected = kick;
        lab.PlayPicked();
        Assert.Empty(played);   // arrowing onto it: Autoplay is off by default
        lab.PlayClicked();      // a click always plays, Autoplay or not
        Assert.Equal(["kick.wav"], played);
        player.Stop();
        played.Clear();

        lab.AutoPlay = true;
        lab.Selected = snare;   // as when a sort or filter moves the selection
        Assert.Empty(played);
        lab.PlayPicked();       // arrowing onto it
        lab.PlayClicked();      // clicking it while it plays doesn't restart it
        Assert.Equal(["snare.wav"], played);
        Assert.Equal(0, snare.Plays);   // a preview isn't a play Rediscover learns from...

        lab.AuditionCommand.Execute(null);   // ...until you double-click it, which keeps it going
        lab.AuditionCommand.Execute(null);
        Assert.Equal(["snare.wav"], played);
        Assert.Equal(1, snare.Plays);

        player.Stop();          // it finished; clicking the selected row plays it again
        lab.PlayClicked();
        Assert.Equal(["snare.wav", "snare.wav"], played);

        Assert.True(new MainViewModel(_db, _sounds).Sounds.AutoPlay);
    }

    [Fact]
    public async Task A_folder_can_be_added_while_the_last_one_added_is_still_being_scanned()
    {
        var ui = (HeadlessUi)Ui.Current;
        var picks = new Queue<TaskCompletionSource<string?>>();
        ui.Picking = () => { var p = new TaskCompletionSource<string?>(); picks.Enqueue(p); return p.Task; };
        try
        {
            var settings = new MainViewModel(_db, _sounds).Settings;
            var (a, b) = (Path.Combine(_root, "Projects A"), Path.Combine(_root, "Projects B"));
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);

            var first = settings.AddProjectFolderCommand.ExecuteAsync(null);
            Assert.True(settings.AddProjectFolderCommand.CanExecute(null));   // the button doesn't wait for the first add
            var second = settings.AddProjectFolderCommand.ExecuteAsync(null);
            picks.Dequeue().SetResult(a);
            picks.Dequeue().SetResult(b);
            await Task.WhenAll(first, second);

            Assert.Contains(a, settings.ProjectFolders);
            Assert.Contains(b, settings.ProjectFolders);
            Assert.Contains(a, _db.Roots());
            Assert.Contains(b, _db.Roots());
            Assert.True(settings.AddSampleFolderCommand.CanExecute(null));
        }
        finally { ui.Picking = null; }
    }

    [Fact]
    public void A_held_arrow_key_plays_the_sounds_it_passes_a_little_each_and_the_last_in_full()
    {
        var played = new List<string>();
        ((HeadlessUi)Ui.Current).Player = () => new Recorder(played);
        var lab = new MainViewModel(_db, _sounds).Sounds;
        lab.AutoPlay = true;
        var (kick, snare, hat) = (new Sound { Path = "kick.wav" }, new Sound { Path = "snare.wav" }, new Sound { Path = "hat.wav" });

        // Key repeats come every 30 ms or so: the first sound plays at once, the ones passed straight after wait their turn...
        lab.Selected = kick;
        lab.PlayPassing();
        lab.Selected = snare;
        lab.PlayPassing();
        lab.Selected = hat;
        lab.PlayPassing();
        Assert.Equal(["kick.wav"], played);

        // ...and the sound the key is let go on plays.
        lab.PlayPicked();
        Assert.Equal(["kick.wav", "hat.wav"], played);
        Assert.Equal(0, hat.Plays);   // still only a preview
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
    public async Task SettingsSaysWhichExtensionBitwigRunsAndWhichInstallPutsIn()
    {
        var user = Path.Combine(_root, "Bitwig Studio");
        var built = DawScriptTests.Extension(Path.Combine(_root, "built", "MusicOrganiser.bwextension"), "3.2");
        var link = new DawLinkSetting("Bitwig Studio", () => "", () => DawScripts.BitwigExtension([user], built),
                                      () => Task.FromResult(""), found: () => true);

        await link.RefreshAsync();
        Assert.Equal("Not installed. Install puts in extension 3.2.", link.Status);
        link.ShowLive(new DawState(DawState.Bitwig, true, true, "Bitwig Studio 6.1.3", "MO Live Test 1", Changes: true, Extension: "3.0"));
        Assert.Equal("✓ Answering now: Bitwig Studio 6.1.3, with “MO Live Test 1” open. Bitwig is running extension 3.0, and Install puts in 3.2: " +
                     "close Bitwig, press Install, then open Bitwig again.", link.Live);

        DawScriptTests.Extension(Path.Combine(user, "Extensions", "MusicOrganiser.bwextension"), "3.0");
        await link.RefreshAsync();
        Assert.Equal("Installed, but an older version (3.0). Install it again to put in extension 3.2.", link.Status);

        await link.InstallCommand.ExecuteAsync(null);
        Assert.Equal($"Installed in {Path.Combine(user, "Extensions")} (extension 3.2).", link.Status);
        link.ShowLive(new DawState(DawState.Bitwig, true, true, "Bitwig Studio 6.1.3", "", Changes: true, Extension: "3.2"));
        Assert.Equal("✓ Answering now: Bitwig Studio 6.1.3.", link.Live);
        // Installed, but Bitwig hasn't restarted, or loads extensions from another user folder.
        link.ShowLive(new DawState(DawState.Bitwig, true, true, "Bitwig Studio 6.1.3", "", Changes: true, Extension: "3.0"));
        Assert.Equal("✓ Answering now: Bitwig Studio 6.1.3. Bitwig is running extension 3.0, but 3.2 is installed: restart Bitwig to load it. " +
                     "If it still runs 3.0 after that, Bitwig loads extensions from another folder: use Choose folder… to pick the one Bitwig " +
                     "shows in Settings > Locations.", link.Live);

        // A newer one, from a newer build of the app, isn't called older, and Install isn't asked for.
        DawScriptTests.Extension(Path.Combine(user, "Extensions", "MusicOrganiser.bwextension"), "3.10");
        await link.RefreshAsync();
        Assert.Equal("Installed, and newer (3.10) than the extension 3.2 this app has: Install would put that older one back.", link.Status);
        link.ShowLive(new DawState(DawState.Bitwig, true, true, "Bitwig Studio 6.1.3", "", Changes: true, Extension: "3.10"));
        Assert.Equal("✓ Answering now: Bitwig Studio 6.1.3. Bitwig is running extension 3.10, newer than the 3.2 this app has.", link.Live);
    }

    [Fact]
    public async Task SettingsSaysWhichOldCopiesInstallRemoved()
    {
        // The script, which the extension takes over from as the same controller.
        var user = Path.Combine(_root, "Bitwig Studio");
        var old = Path.Combine(user, "Controller Scripts", "Music Organiser");
        var built = DawScriptTests.Extension(Path.Combine(_root, "built", "MusicOrganiser.bwextension"), "3.4");
        var link = new DawLinkSetting("Bitwig Studio", () => "", () => DawScripts.BitwigExtension([user], built),
                                      () => Task.FromResult("Connected to Bitwig Studio 6.1.3."), found: () => true);
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "MusicOrganiser.control.js"), "// the script");

        await link.RefreshAsync();
        Assert.Equal($"Not installed. Install puts in extension 3.4. There's an old copy in {old} too, which Install removes so Bitwig Studio " +
                     "can't load two.", link.Status);
        await link.InstallCommand.ExecuteAsync(null);
        Assert.Equal($"Installed. Now set it up in Bitwig Studio as described above. Removed the old copy in {old}, so Bitwig Studio can't load two.",
                     link.Result);
        Assert.Equal($"Installed in {Path.Combine(user, "Extensions")} (extension 3.4).", link.Status);

        // Set up both takes out one that turns up again, though the extension is installed.
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "MusicOrganiser.control.js"), "// the script");
        await link.SetUpAsync();
        Assert.Equal($"The extension was already installed. Removed the old copy in {old}, so Bitwig Studio can't load two. " +
                     "Connected to Bitwig Studio 6.1.3.", link.Result);
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public async Task SettingsRemembersWhereBitwigLoadsTheExtensionFrom()
    {
        // Bitwig may load extensions from Documents\Bitwig Studio while the projects (the chosen folder) are elsewhere.
        var extensions = Path.Combine(_root, "Documents", "Bitwig Studio", "Extensions");
        var file = System.Text.Json.JsonSerializer.Serialize(Path.Combine(extensions, "MusicOrganiser.bwextension"));
        await using var bitwig = new FakeJavaBitwig($$"""{"link":3,"product":"Bitwig Studio","version":"6.1.3","api":23,"platform":"WINDOWS","port":19791,"project":"","modified":false,"engine":true,"changes":true,"runtime":"java","extension":"3.4","onlyIn":true,"loadedFrom":{{file}}}""");
        using var watch = new DawWatch(bitwig.Port, flOpen: () => false, bitwigOpen: () => true) { Retry = TimeSpan.Zero };
        var main = Main(watch);
        await PollUntil(main.Live, () => main.Live.Rows[0].Answering);

        Assert.StartsWith($"✓ Answering now: Bitwig Studio 6.1.3. Bitwig loads the extension from {extensions}.", main.Settings.Links[0].Live);
        Assert.Equal(extensions, _db.GetSetting(SettingsViewModel.BitwigLoadsFromKey));
    }

    [Fact]
    public async Task SettingsDoesntSayBitwigLoadsTheExtensionFromACopyOfItsOwn()
    {
        // Bitwig ran the extension from a copy it made in a temp folder, which isn't where Install puts the next version.
        var copy = System.Text.Json.JsonSerializer.Serialize(Path.Combine(_root, "Temp", "bitwig-extensions", "MusicOrganiser.bwextension"));
        await using var bitwig = new FakeJavaBitwig($$"""{"link":3,"product":"Bitwig Studio","version":"6.1.3","api":23,"platform":"WINDOWS","port":19791,"project":"","modified":false,"engine":true,"changes":true,"runtime":"java","extension":"3.4","onlyIn":true,"loadedFrom":{{copy}}}""");
        using var watch = new DawWatch(bitwig.Port, flOpen: () => false, bitwigOpen: () => true) { Retry = TimeSpan.Zero };
        var main = Main(watch);
        await PollUntil(main.Live, () => main.Live.Rows[0].Answering);

        Assert.StartsWith("✓ Answering now: Bitwig Studio 6.1.3.", main.Settings.Links[0].Live);
        Assert.DoesNotContain("loads the extension from", main.Settings.Links[0].Live);
        Assert.Null(_db.GetSetting(SettingsViewModel.BitwigLoadsFromKey));
    }

    [Fact]
    public async Task SettingsShowsTheProbesRunningNow()
    {
        var reports = Path.Combine(_root, "probe");
        var live = Path.Combine(reports, "live");
        var now = DateTimeOffset.UtcNow;
        var fresh = now.AddMinutes(1);   // can't go stale however slow the run
        ProbeStatus(live, "AAAA000000000001", fresh, "1-instrument-AAAA0000.jsonl", playing: true, bar: 5);
        ProbeStatus(live, "AAAA000000000002", now.AddSeconds(-10), "2-instrument-AAAA0000.jsonl");   // its DAW went away
        ProbeStatus(live, "AAAA000000000003", fresh, "3-instrument-AAAA0000.jsonl", host: "FL Studio", version: "25.1.6", format: "VST3",
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
        ProbeStatus(live, "AAAA000000000001", fresh, "1-instrument-AAAA0000.jsonl", playing: true, bar: 6, gain: -12);
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
        // Written once, so dated ahead: a busy run can take longer than a probe's status stays fresh.
        ProbeStatus(live, "AAAA000000000001", DateTimeOffset.UtcNow.AddMinutes(1), "1-instrument-AAAA0000.jsonl");
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
        // Sweep previews and the Sounds list are worked out straight away here, so a test can check them at once.
        static HeadlessUi()
        {
            PreviewWorker.InBackground = false;
            UiWork.InBackground = false;
        }

        public IItemsView CreateView(IList items) => new ListView(items);
        public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new NoTimer();
        public Func<IAudioPlayer> Player { get; set; } = () => new Silence();
        public IAudioPlayer CreateAudioPlayer() => Player();
        public string? PickedFolder { get; set; }
        /// <summary>When set, picking a folder waits on this instead (a test holds the dialog open).</summary>
        public Func<Task<string?>>? Picking { get; set; }
        public Task<string?> PickFolder(string title) => Picking?.Invoke() ?? Task.FromResult(PickedFolder);
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
        public IEnumerator GetEnumerator()
        {
            var shown = items.Cast<object>().Where(i => Filter?.Invoke(i) ?? true).ToList();
            if (CustomSort is { } sort) shown.Sort(sort.Compare);
            return shown.GetEnumerator();
        }
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
