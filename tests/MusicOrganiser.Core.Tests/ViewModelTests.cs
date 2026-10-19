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
[Collection("Global UI")]
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
    public void Changing_one_field_of_a_song_leaves_its_older_saves_other_fields_alone()
    {
        void Save(string name, int day)
        {
            var file = Path.Combine(_root, name + ".flp");
            File.WriteAllBytes(file, [0]);
            File.SetLastWriteTime(file, new DateTime(2026, 10, day));
            _db.Upsert(new LibraryDb.IndexedFile(file, day, 100, new ProjectInfo { Daw = "FL Studio" }, null, [], []));
        }
        Save("Didums_1", 1);
        Save("Didums_2", 2);
        // The older save has notes and a key of its own (set before the newer save had any).
        var older = _db.Projects().Single(p => p.Name == "Didums_1");
        older.Notes = "the first take had the vocal";
        older.MusicalKey = "Am";
        _db.SaveOrganisation(older);
        var newer = _db.Projects().Single(p => p.Name == "Didums_2");
        newer.Notes = "the vocal is gone";
        newer.MusicalKey = "Cm";
        _db.SaveOrganisation(newer);

        var song = Main().Projects.Items.Single(i => i.Name == "Didums");
        song.Rating = 3;

        older = _db.Projects().Single(p => p.Name == "Didums_1");
        Assert.Equal((3, "the first take had the vocal", "Am"), (older.Rating, older.Notes, older.MusicalKey));
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
        Assert.True(detail.HasRunChoices);
        Assert.Equal([$"{newest.Modified.Year} rework (newest) · Didums_77 · {newest.Modified:d MMM yyyy}", "2019 original · Didums_12 · 2 Feb 2019"],
                     detail.RunChoices.Select(r => r.Header));
        Assert.StartsWith("First made 2 Feb 2019 · saved ", detail.DatesText);

        // Overview's line of counts.
        Assert.Equal("2 saves", detail.SavesCountText);
        Assert.Equal("No samples", detail.SamplesCountText);
        Assert.Equal("", detail.MissingSamplesText);
    }

    [Fact]
    public async Task A_fixed_copy_waits_out_of_the_songs_saves_until_kept_and_still_asks_whether_it_loaded_after_leaving_it()
    {
        // The sample library sits outside the projects' folder: a kick inside it would count as found (SampleLocator).
        var libRoot = Fixtures.TempDir();
        var lib = Path.Combine(libRoot, "Drums");
        Directory.CreateDirectory(lib);
        File.WriteAllBytes(Path.Combine(lib, "Kick 01.wav"), [0]);
        _db.SetList(Scanner.SampleFoldersKey, [libRoot]);
        new Scanner(_db).ScanSampleFolders();
        ScanFixtures();
        var main = Main();
        await Until(() => !main.IsScanning);   // the scan the app starts with (the sample folder) reloads the list when it ends
        var projects = main.Projects;
        projects.Reload();
        projects.Selected = projects.Items.Single(i => i.Daw == "FL Studio");
        var detail = projects.Detail!;

        await detail.PlanFixCommand.ExecuteAsync(null);
        var kick = detail.FixRows.Single(r => r.HasLookedFor);
        Assert.Equal(@"FL Studio looked for: C:\Samples\Drums\Kick 01.wav", kick.LookedFor);
        await detail.SaveFixedCommand.ExecuteAsync(null);

        // Until it's kept, the song still opens the original and Older saves leave the copy out; still picked, still asking.
        var fl = projects.Items.Where(i => i.Daw == "FL Studio").ToList();
        Assert.True(fl.Count == 1, "FL songs: " + string.Join("; ", fl.Select(i => $"{i.Path} (in {i.Folder})")));
        var song = fl[0];
        Assert.Equal("Night Drive", song.FileName);
        Assert.Empty(song.OlderSaves);
        Assert.Same(song, projects.Selected);
        Assert.True(projects.Detail!.HasPendingFix);

        // The finished card's "Open it" opens it from the panel showing now, which then asks whether it loaded,
        // and still does after leaving the song and coming back.
        // A program that's there on each system and exits at once ("/bin/true" isn't there on macOS).
        _db.SetSetting(ProjectDetailViewModel.DawExeKey("FL Studio"),
                       OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "where.exe") : "/usr/bin/true");
        main.Activities.Items.First(a => a.Title.StartsWith("Saved Night Drive (fixed)")).RunActionCommand.Execute(null);
        Assert.True(projects.Detail.PendingFixOpened);
        projects.Selected = projects.Items.First(i => i.Daw != "FL Studio");
        projects.Selected = song;
        Assert.True(projects.Detail!.PendingFixOpened);

        // Leave it and come back: the question is still there until it's answered.
        projects.Selected = projects.Items.First(i => i.Daw != "FL Studio");
        projects.Selected = song;
        Assert.True(projects.Detail!.HasPendingFix);
        await projects.Detail.KeepFixedCommand.ExecuteAsync(null);

        // Kept: now it's the song's newest save, the one Open uses, with the original under Older saves.
        song = projects.Items.Single(i => i.Daw == "FL Studio");
        Assert.Equal("Night Drive (fixed)", song.FileName);
        Assert.Equal(["Night Drive"], song.OlderSaves.Select(o => o.Name));
        Assert.Same(song, projects.Selected);
        projects.Selected = projects.Items.First(i => i.Daw != "FL Studio");
        projects.Selected = song;
        Assert.False(projects.Detail!.HasPendingFix);
    }

    [Fact]
    public async Task Another_project_being_saved_leaves_the_open_songs_panel_as_it_was()
    {
        ScanFixtures();
        var projects = Main().Projects;
        projects.Reload();
        projects.Selected = projects.Items.Single(i => i.Daw == "FL Studio");
        var panel = projects.Detail!;
        await panel.PlanFixCommand.ExecuteAsync(null);
        Assert.NotEmpty(panel.FixRows);

        // Another song saved in its DAW: the watcher reads it and the list reloads.
        var other = projects.Items.First(i => i.Daw != "FL Studio");
        File.SetLastWriteTime(other.Path, DateTime.Now.AddMinutes(1));
        _db.Upsert(Scanner.Analyse(other.Path));
        await projects.ReloadAsync();

        Assert.Same(panel, projects.Detail);
        Assert.NotEmpty(panel.FixRows);
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
    public async Task A_scan_that_finds_nothing_new_leaves_the_pages_as_they_are()
    {
        _db.AddRoot(_root);
        var main = Main();
        await main.ScanAsync(false, plugins: false);
        var row = main.Projects.Items.First(i => i.Name == "Night Drive");
        var plugins = main.Plugins.Items;

        await main.ScanAsync(false, plugins: false);
        Assert.Same(row, main.Projects.Items.First(i => i.Name == "Night Drive"));
        Assert.Same(plugins, main.Plugins.Items);

        // A changed project is read again, and the pages with it.
        File.SetLastWriteTime(Path.Combine(_root, "Night Drive.flp"), DateTime.Now.AddMinutes(5));
        await main.ScanAsync(false, plugins: false);
        Assert.NotSame(row, main.Projects.Items.First(i => i.Name == "Night Drive"));
        Assert.NotSame(plugins, main.Plugins.Items);
    }

    [Fact]
    public async Task Projects_and_sounds_saved_while_the_app_is_open_are_taken_in_without_a_scan()
    {
        var quiet = (MainViewModel.ProjectWatchQuiet, SoundsViewModel.SampleWatchQuiet);
        MainViewModel.ProjectWatchQuiet = SoundsViewModel.SampleWatchQuiet = TimeSpan.FromMilliseconds(200);
        try
        {
            var songs = Directory.CreateDirectory(Path.Combine(_root, "Songs")).FullName;
            var lib = Directory.CreateDirectory(Path.Combine(_root, "Lib")).FullName;
            _db.AddRoot(songs);
            _db.SetList(Scanner.SampleFoldersKey, [lib]);
            var main = Main();
            await main.ScanAsync(false, plugins: false);
            await Until(() => !main.Sounds.IsBusy);
            Assert.Empty(main.Projects.Items);

            // A song saved in a DAW, and a sound bounced into the sample folder (one in a hidden folder is left out).
            Fixtures.FlStudioProject(songs, "Fresh Idea");
            Synth.Write(Path.Combine(lib, "Bounce 01.wav"), Synth.Kick(1));
            Directory.CreateDirectory(Path.Combine(lib, ".cache"));
            Synth.Write(Path.Combine(lib, ".cache", "Temp.wav"), Synth.Kick(2));
            await Until(() => main.Projects.Items.Any(i => i.Name == "Fresh Idea") && main.Sounds.Org.Sounds.Count == 1);
            Assert.EndsWith("Bounce 01.wav", main.Sounds.Org.Sounds[0].Path);

            // Bitwig saving a song and its auto-backup copy at once: the card names the song, not the backup.
            var cards = new System.Collections.Concurrent.ConcurrentQueue<string>();
            main.Activities.Items.CollectionChanged += (_, e) => { foreach (MusicOrganiser.ViewModels.Activity a in e.NewItems ?? Array.Empty<MusicOrganiser.ViewModels.Activity>()) cards.Enqueue(a.Title); };
            Fixtures.FlStudioProject(Directory.CreateDirectory(Path.Combine(songs, "auto-backups")).FullName, "Another");
            Fixtures.FlStudioProject(songs, "Zed");
            await Until(() => cards.Any(t => t.StartsWith("Updated ")));
            Assert.Equal("Updated Zed", cards.First(t => t.StartsWith("Updated ")));

            // Deleted: the song stays listed, marked missing; the sound is forgotten.
            File.Delete(Path.Combine(songs, "Fresh Idea.flp"));
            File.Delete(Path.Combine(lib, "Bounce 01.wav"));
            await Until(() => main.Projects.Items.Single(i => i.Name == "Fresh Idea").FileMissing && main.Sounds.Org.Sounds.Count == 0);
            main.Shutdown();
        }
        finally { (MainViewModel.ProjectWatchQuiet, SoundsViewModel.SampleWatchQuiet) = quiet; }
    }

    [Fact]
    public async Task Sounds_seen_changing_while_another_job_starts_are_taken_in_once_it_ends()
    {
        var lib = Directory.CreateDirectory(Path.Combine(_root, "Lib")).FullName;
        _db.SetList(Scanner.SampleFoldersKey, [lib]);
        var main = Main();
        var sounds = main.Sounds;
        // The page's own first scan, of the empty folder, is done before the sound turns up.
        await Until(() => main.Activities.Items.Any(a => a.Title.Contains("ample folders")) && !sounds.IsBusy);
        var bounce = Path.Combine(lib, "Bounce 01.wav");
        Synth.Write(bounce, Synth.Kick(1));

        // The watcher reports a sound while a job is running, then a take-in starts before that job has let go.
        sounds.IsBusy = true;
        sounds.SampleFilesChanged(new([bounce], []));
        await sounds.TakeInChangedSoundsAsync();
        Assert.Empty(sounds.Org.Sounds);
        sounds.IsBusy = false;
        await sounds.TakeInChangedSoundsAsync();
        Assert.EndsWith("Bounce 01.wav", Assert.Single(sounds.Org.Sounds).Path);
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
    public void Sounds_can_be_filtered_by_the_year_a_project_using_them_was_saved()
    {
        _soundDb.Save(new[] { "/samples/Kick A.wav", "/samples/Snare B.wav", "/samples/Hat C.wav" }.Select(p =>
            new SoundLibrary.Analysed(p, 1, 1, new SoundAnalysis { Features = [1, 0, 0] }, null, null, null)));
        _sounds.Reload();
        void Project(string name, int year, params string[] samples)
        {
            var file = Path.Combine(_root, name + ".flp");
            File.WriteAllBytes(file, [0]);
            var saved = new DateTimeOffset(new DateTime(year, 6, 1, 12, 0, 0, DateTimeKind.Local)).ToUnixTimeSeconds();
            _db.Upsert(new LibraryDb.IndexedFile(file, 1, saved, new ProjectInfo { Daw = "FL Studio" }, null,
                samples.Select(p => (p, true)).ToList(), []));
        }
        Project("Old one", 2019, "/samples/Kick A.wav");
        Project("New one", 2024, "/samples/Snare B.wav", "/samples/Kick A.wav");

        var lab = Main().Sounds;
        Assert.Equal([SoundsViewModel.AnyYear, "2024", "2019"], lab.YearChoices);
        lab.YearFilter = "2019";
        Assert.Equal(["Kick A"], lab.Shown.Select(s => s.Name));
        lab.YearFilter = "2024";
        Assert.Equal(["Kick A", "Snare B"], lab.Shown.Select(s => s.Name).Order());
        lab.ClearFiltersCommand.Execute(null);
        Assert.Equal(3, lab.Shown.Count);
    }

    [Fact]
    public async Task Rediscover_shows_as_many_as_asked_and_picks_again_when_the_slider_moves()
    {
        var rng = new Random(5);
        _soundDb.Save(Enumerable.Range(0, 200).Select(i => new SoundLibrary.Analysed($"/samples/Kick {i}.wav", 1, 1,
            new SoundAnalysis { Seconds = 0.5, Features = Enumerable.Range(0, 12).Select(_ => (float)rng.NextDouble()).ToArray() }, null, null, "Kick")));
        _sounds.Reload();
        var delay = RediscoverViewModel.ShuffleDelay;
        RediscoverViewModel.ShuffleDelay = TimeSpan.Zero;
        try
        {
            var page = Main().Sounds.Rediscover;
            await page.ShuffleCommand.ExecuteAsync(null);
            Assert.Equal(60, page.Suggestions.Count);

            page.HowMany = 30;
            await Until(() => page.Suggestions.Count == 30 && !page.IsBusy);
            var before = page.Suggestions;
            page.Adventure = 1;
            await Until(() => !ReferenceEquals(before, page.Suggestions) && !page.IsBusy);
            Assert.Equal(30, page.Suggestions.Count);
        }
        finally { RediscoverViewModel.ShuffleDelay = delay; }
    }

    [Fact]
    public async Task A_project_re_saved_in_another_year_moves_its_sounds_to_that_year_after_a_scan()
    {
        _soundDb.Save(new[] { "/samples/Kick A.wav", "/samples/Snare B.wav" }.Select(p =>
            new SoundLibrary.Analysed(p, 1, 1, new SoundAnalysis { Features = [1, 0, 0] }, null, null, null)));
        _sounds.Reload();
        var file = Path.Combine(_root, "Old one.flp");
        File.WriteAllBytes(file, [0]);
        void Save(int year) => _db.Upsert(new LibraryDb.IndexedFile(file, 1,
            new DateTimeOffset(new DateTime(year, 6, 1, 12, 0, 0, DateTimeKind.Local)).ToUnixTimeSeconds(),
            new ProjectInfo { Daw = "FL Studio" }, null, [("/samples/Kick A.wav", true)], []));
        Save(2019);
        var other = Path.Combine(_root, "Other.flp");
        File.WriteAllBytes(other, [0]);
        _db.Upsert(new LibraryDb.IndexedFile(other, 1, new DateTimeOffset(new DateTime(2019, 3, 1, 12, 0, 0, DateTimeKind.Local)).ToUnixTimeSeconds(),
            new ProjectInfo { Daw = "FL Studio" }, null, [("/samples/Snare B.wav", true)], []));
        var lab = Main().Sounds;
        lab.YearFilter = "2019";
        Assert.Equal(["Kick A", "Snare B"], lab.Shown.Select(s => s.Name).Order());

        // Same sounds, same number of projects: only the year changed.
        Save(2021);
        await lab.RefreshUsageAsync();
        Assert.Equal(["Snare B"], lab.Shown.Select(s => s.Name));
        Assert.Contains("2021", lab.YearChoices);
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
    public void A_DAWs_own_device_is_its_own_row_and_a_plugin_shows_as_its_usual_kind()
    {
        void Song(string name, string daw, params PluginRef[] plugins)
        {
            var info = new ProjectInfo { Daw = daw };
            info.Plugins.AddRange(plugins);
            var file = Path.Combine(_root, name);
            File.WriteAllBytes(file, [0]);
            _db.Upsert(new LibraryDb.IndexedFile(file, 1, 100, info, null, [], []));
        }
        var kontakt = new PluginRef("Kontakt 5", "Native Instruments", PluginKind.Instrument, "VST2");
        Song("A.bwproject", "Bitwig Studio", new PluginRef("Arpeggiator", "Bitwig", PluginKind.NoteEffect, "Native"), kontakt);
        Song("B.song", "Studio One", new PluginRef("Arpeggiator", "PreSonus", PluginKind.NoteEffect, "Native"), kontakt);
        Song("C.flp", "FL Studio", kontakt with { Kind = PluginKind.Effect });
        _db.ReplaceInstalledPlugins(["Something Else"]);

        var usage = _db.PluginUsage();
        Assert.Equal(["Bitwig Studio", "Studio One"], usage.Where(u => u.Name == "Arpeggiator").Select(u => u.Daws).Order());
        Assert.All(usage.Where(u => u.Name == "Arpeggiator"), u => Assert.True(u.Installed));
        var k = Assert.Single(usage, u => u.Name == "Kontakt 5");
        Assert.Equal((3, PluginKind.Instrument), (k.Projects, k.Kind));

        // Picking Studio One's Arpeggiator lists only Studio One's projects, and stays picked when the page reloads.
        var page = Main().Plugins;
        page.Selected = page.Items.Single(u => u.Name == "Arpeggiator" && u.Daws == "Studio One");
        Assert.Equal(["B"], page.Projects.Select(p => p.Song));
        Assert.Equal("Used in 1 song", page.ProjectsTitle);
        Assert.Single(_db.ProjectsUsingPlugin("Arpeggiator", page.Selected.Own));
        page.Reload();
        Assert.Equal("Studio One", page.Selected?.Daws);
        // A plugin's row still lists all its projects.
        page.Selected = page.Items.Single(u => u.Name == "Kontakt 5");
        Assert.Equal(3, page.Projects.Count);
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
        Assert.Equal("Used in 3 songs", page.ProjectsTitle);
        Assert.Equal(["A", "B", "C"], page.Projects.Select(p => p.Song).Order());

        // Fewer cards come from what the page already read: a song added since only shows after a reload.
        Song("F", "New Synth");
        page.NeededCount = 1;
        Assert.Equal(["Bass Master"], page.Needed.Select(n => n.Name));
        Assert.Contains("this one and 2 of them open", page.NeededSummary);
        Assert.Equal("1", _db.GetSetting(PluginsViewModel.NeededCountKey));
        page.NeededCount = 6;
        Assert.DoesNotContain(page.Needed, n => n.Name == "New Synth");
        page.Reload();
        Assert.Contains(page.Needed, n => n.Name == "New Synth");
    }

    [Fact]
    public void A_plugins_side_panel_shows_each_songs_newest_save_and_can_keep_to_one_DAW()
    {
        void Save(string file, string daw, int day, params string[] plugins)
        {
            var info = new ProjectInfo { Daw = daw };
            info.Plugins.AddRange(plugins.Select(n => new PluginRef(n, "Xfer Records", PluginKind.Instrument, "VST3")));
            var path = Path.Combine(_root, file);
            File.WriteAllBytes(path, [0]);
            var mtime = new DateTimeOffset(2026, 4, day, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            _db.Upsert(new LibraryDb.IndexedFile(path, 100, mtime, info, null, [], []));
        }
        for (var i = 1; i <= 5; i++) Save(i == 1 ? "Teka.flp" : $"Teka_{i}.flp", "FL Studio", i, "Serum");
        Save("Teka_6.flp", "FL Studio", 6, "Serum 2");   // the newest save swapped it out
        Save("Heel_3.bwproject", "Bitwig Studio", 10, "Serum");
        Save("Heel_4.bwproject", "Bitwig Studio", 11, "Serum");
        Save("Garage.song", "Studio One", 2, "Serum");

        var main = Main();
        var page = main.Plugins;
        page.Selected = page.Items.Single(u => u.Name == "Serum");
        Assert.Equal("Used in 3 songs", page.ProjectsTitle);
        Assert.Equal([("Heel", "Heel_4", 2), ("Teka", "Teka_5", 5), ("Garage", "Garage", 1)],
                     page.Projects.Select(p => (p.Song, p.Latest.Name, p.Saves)));
        Assert.Equal("8 saves in all. Each song shows its newest save that uses it.", page.ProjectsNote);
        Assert.Equal([ProjectsViewModel.Any, "Bitwig Studio", "FL Studio", "Studio One"], page.ProjectDaws);
        Assert.True(page.HasProjectDaws);

        page.EverySave = true;
        Assert.Equal(8, page.Projects.Count);
        Assert.Equal("Heel_4", page.Projects[0].Song);
        page.EverySave = false;

        // One DAW: the list, its title and Show in Projects keep to it.
        page.ProjectsDaw = "FL Studio";
        Assert.Equal(["Teka"], page.Projects.Select(p => p.Song));
        Assert.Equal("Used in 1 song in FL Studio", page.ProjectsTitle);
        page.ShowProjectsCommand.Execute(null);
        Assert.Equal("Projects using Serum in FL Studio", main.Projects.IdFilterLabel);
        Assert.Equal(["Teka"], main.Projects.View!.Cast<ProjectItem>().Select(i => i.Name));

        // Another plugin without FL Studio saves goes back to every DAW.
        Save("Solo.bwproject", "Bitwig Studio", 12, "Pigments");
        page.Reload();
        page.Selected = page.Items.Single(u => u.Name == "Pigments");
        Assert.Equal(ProjectsViewModel.Any, page.ProjectsDaw);
        Assert.False(page.HasProjectDaws);
        Assert.Equal(["Solo"], page.Projects.Select(p => p.Song));
    }

    [Fact]
    public void Deep_listening_to_new_sounds_is_on_until_turned_off_and_stays_off()
    {
        Assert.True(new MainViewModel(_db, _sounds).Sounds.ListenToNew);
        new MainViewModel(_db, _sounds).Sounds.ListenToNew = false;
        Assert.False(new MainViewModel(_db, _sounds).Sounds.ListenToNew);
    }

    [Fact]
    public void A_click_plays_or_stops_and_Autoplay_plays_what_you_arrow_onto_but_not_what_the_list_selects_by_itself()
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
        lab.AuditionCommand.Execute(null);   // and a double-click keeps it going as a play, Autoplay or not
        Assert.Equal(["kick.wav"], played);
        Assert.Equal(1, kick.Plays);
        player.Stop();
        played.Clear();

        lab.AutoPlay = true;
        lab.Selected = snare;   // as when a sort or filter moves the selection
        Assert.Empty(played);
        lab.PlayPicked();       // arrowing onto it
        Assert.Equal(["snare.wav"], played);
        Assert.Equal(0, snare.Plays);   // a preview isn't a play Rediscover learns from...

        lab.AuditionCommand.Execute(null);   // ...until you double-click it, which keeps it going
        lab.AuditionCommand.Execute(null);
        Assert.Equal(["snare.wav"], played);
        Assert.Equal(1, snare.Plays);

        lab.PlayClicked();      // clicking it while it plays stops it
        Assert.Null(player.Playing);
        lab.PlayClicked();      // and clicking it again plays it again
        Assert.Equal(["snare.wav", "snare.wav"], played);
        Assert.Equal("snare.wav", player.Playing);

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
    public void A_held_arrow_key_plays_every_sound_it_lands_on_and_waits_for_each_before_moving_on()
    {
        var played = new List<string>();
        ((HeadlessUi)Ui.Current).Player = () => new Recorder(played);
        var lab = new MainViewModel(_db, _sounds).Sounds;
        lab.AutoPlay = true;
        Assert.Equal(250, lab.StepMs);   // until changed in Settings › Sounds
        var (kick, snare, hat) = (new Sound { Path = "kick.wav" }, new Sound { Path = "snare.wav" }, new Sound { Path = "hat.wav" });

        // A press always moves; the key's repeats straight after (every 30 ms or so) don't, so the highlight stays on
        // the sound that's playing...
        Assert.True(lab.MayStep(held: false));
        lab.Selected = kick;
        lab.PlayPassing();
        Assert.False(lab.MayStep(held: true));
        Assert.False(lab.MayStep(held: true));
        Assert.Equal(["kick.wav"], played);

        // ...and once it has played its share, the next one it lands on plays too: none is skipped.
        lab.StepMs = 0;
        Assert.True(lab.MayStep(held: true));
        lab.Selected = snare;
        lab.PlayPassing();
        Assert.True(lab.MayStep(held: true));
        lab.Selected = hat;
        lab.PlayPassing();
        Assert.Equal(["kick.wav", "snare.wav", "hat.wav"], played);
        Assert.Equal(0, hat.Plays);   // still only a preview
        lab.StepMs = 600;
        Assert.Equal(600, new MainViewModel(_db, _sounds).Sounds.StepMs);   // kept
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
    public async Task A_sound_goes_into_Bitwig_on_a_Sampler_on_a_track_of_its_own()
    {
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        if (bitwig is null) return;
        var port = BitwigLink.Port;
        BitwigLink.Port = bitwig.Port;
        try
        {
            var file = Path.Combine(_root, "Snare Tight.wav");
            File.WriteAllBytes(file, [0]);
            var lab = Main().Sounds;
            lab.Selected = new Sound { Path = file };
            await lab.ToBitwigCommand.ExecuteAsync(null);

            using var link = await BitwigLink.ConnectAsync();
            var track = (await link.ProjectAsync()).Tracks.Single(t => t.Name == "Snare Tight");
            Assert.Contains("Sampler", track.Devices.Single().Name);
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
        var ready = true;
        Task<(string, bool)> Check() => answers
            ? Task.FromResult(("Connected to Bitwig Studio 6.1.2, with “Night Drive” open.", ready))
            : Task.FromException<(string, bool)>(new DawLinkException("Bitwig isn't answering."));
        var link = new DawLinkSetting("Bitwig Studio", () => "", () => DawScripts.Bitwig(user), Check, found: () => found);

        Assert.Equal("Bitwig Studio: not found on this computer, so it was skipped.", await link.SetUpAsync());
        Assert.Equal("Skipped: the app didn't find Bitwig Studio on this computer.", link.Result);
        Assert.False(Directory.Exists(user));

        found = true;
        Assert.Equal("✓ Bitwig Studio: set up and answering.", await link.SetUpAsync());
        Assert.Equal("Installed the script. Connected to Bitwig Studio 6.1.2, with “Night Drive” open.", link.Result);
        Assert.Equal(ScriptState.Installed, DawScripts.Bitwig(user).State);
        Assert.StartsWith("Installed in ", link.Status);

        // It answers, but says something's still to set up (the extension's MIDI ports, say).
        ready = false;
        Assert.Equal("Bitwig Studio: answering, but there's still something to set up. What's left is below.", await link.SetUpAsync());

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
                                      () => Task.FromResult(("", true)), found: () => true);

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
                                      () => Task.FromResult(("Connected to Bitwig Studio 6.1.3.", true)), found: () => true);
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

    [Fact]
    public async Task The_midi_page_finds_parts_and_fits_them_to_a_project_already_moved()
    {
        // A MIDI pack with the song's progression in C minor, and a bassline; and a project in A minor.
        const int bar = 4 * 96;
        int[][] aMinor = [[57, 60, 64], [53, 57, 60], [48, 52, 55], [55, 59, 62]];
        int[][] cMinor = [[60, 63, 67], [56, 60, 63], [51, 55, 58], [58, 62, 65]];
        var pack = Path.Combine(_root, "Packs", "Chords");
        Directory.CreateDirectory(pack);
        var chords = new Core.Parsers.MidiClip { Name = "Moody", Ppq = 96 };
        chords.Track("Piano").AddRange(cMinor.SelectMany((c, i) => c.Select(k => new Core.Parsers.NoteEvent(i * bar, bar, k, 100))));
        File.WriteAllBytes(Path.Combine(pack, "Moody Cm.mid"), Core.Export.MidiWriter.Write(chords));
        var song = new Core.Parsers.MidiChannel("Keys", "", 96,
            aMinor.SelectMany((c, i) => c.Select(k => new Core.Parsers.NoteEvent(i * bar, bar, k, 100))).ToList(), Plugin: "Serum");

        _db.SetList(Core.Midi.MidiIndexer.FoldersKey, [Path.Combine(_root, "Packs")]);
        _db.SetSetting(ProjectDetailViewModel.ExportFolderKey, Path.Combine(_root, "exports"));
        var midi = Main().Midi;
        midi.Opened();
        await midi.IndexAsync();
        Assert.Contains(midi.Results, r => r.Title == "Moody Cm");

        midi.SelectedRole = midi.RoleChips.First(c => c.Role == Core.Midi.MidiRoles.Chords);
        midi.Search = "moody";
        await midi.RefreshAsync();
        var row = Assert.Single(midi.Results);
        var key = row.KeyText;   // C minor or its relative, E flat major: the same notes
        Assert.Contains(key, new[] { "Cm", "D#" });

        // Fitted to the A minor song: moved down 3, and shown, played and dragged out that way.
        midi.FitToHarmony(Core.Midi.HarmonyTarget.FromChannels("Fit me", [song])!);
        await midi.RefreshAsync();
        row = Assert.Single(midi.Results);
        Assert.Equal("down 3 semitones", row.MoveText);
        Assert.Equal(key == "Cm" ? "Cm → Am" : "D# → C", row.KeyText);
        Assert.Equal(51 - 3, row.Channel.Notes.Min(n => n.Key));
        var drag = midi.MakeDrag([row])!;
        var written = Core.Midi.MidiFileReader.Read(drag.Files.Single());
        Assert.Equal(51 - 3, written.Single().Notes.Min(n => n.Key));
        Assert.EndsWith("(-3).mid", drag.Files.Single());
        Assert.StartsWith(Path.Combine(_root, "exports"), drag.Files.Single());

        // The pack's file is untouched, and saying what a part is sticks.
        midi.Selected = row;
        await midi.SetRoleCommand.ExecuteAsync(Core.Midi.MidiRoles.Melody);
        Assert.Equal(Core.Midi.MidiRoles.Melody, Assert.Single(midi.Index.All()).YourRole);
    }

    [Fact]
    public async Task A_part_that_comes_in_late_in_its_bar_shows_and_plays_from_its_first_note_but_drags_out_from_the_bar()
    {
        // A pickup: three notes on the last beat of the bar, then a held chord in the next bar.
        var pack = Path.Combine(_root, "Packs");
        Directory.CreateDirectory(pack);
        var clip = new Core.Parsers.MidiClip { Name = "Pickup", Ppq = 96 };
        clip.Track("Keys").AddRange([
            new(3 * 96, 32, 60, 100), new(3 * 96 + 32, 32, 62, 100), new(3 * 96 + 64, 32, 64, 100),
            new(4 * 96, 384, 60, 100), new(4 * 96, 384, 64, 100), new(4 * 96, 384, 67, 100)]);
        File.WriteAllBytes(Path.Combine(pack, "Pickup.mid"), Core.Export.MidiWriter.Write(clip));
        _db.SetList(Core.Midi.MidiIndexer.FoldersKey, [pack]);
        _db.SetSetting(ProjectDetailViewModel.ExportFolderKey, Path.Combine(_root, "exports"));
        var midi = Main().Midi;
        midi.Opened();
        await midi.IndexAsync();

        var row = Assert.Single(midi.Results);
        Assert.Equal(0, row.Channel.Notes.Min(n => n.Start));   // shown and played from its first note, with no silence first
        var written = Core.Midi.MidiFileReader.Read(midi.MakeDrag([row])!.Files.Single()).Single();
        Assert.Equal(3 * 96, written.Notes.Min(n => n.Start));   // the file keeps the pickup before the bar line
    }

    [Fact]
    public async Task A_clip_shows_the_longer_phrase_it_plays_in_with_itself_lit_up()
    {
        var songs = Path.Combine(_root, "Songs");
        Directory.CreateDirectory(songs);
        _db.Upsert(Core.Library.Scanner.Analyse(MidiBrowserTests.FlProjectWithPhrases(songs)));
        var midi = Main().Midi;
        midi.Opened();
        await midi.IndexAsync();

        var hook = midi.Results.Single(r => r.Title == "Keys · Hook");
        Assert.Equal("Part of a 16-bar phrase", hook.PhraseText);
        var whole = midi.Results.Single(r => r.Entry.IsJoined);
        Assert.Equal("Joined from 2 clips", whole.PhraseText);
        Assert.Equal([0L, 4 * 384, 8 * 384, 12 * 384], whole.Spans!.Select(s => s.Start));
        Assert.DoesNotContain(whole.Spans!, s => s.Lit);

        midi.Selected = hook;
        midi.ShowWholePhraseCommand.Execute(null);
        var shown = Assert.Single(midi.Results);
        Assert.True(shown.Entry.IsJoined);
        Assert.Equal([false, true, false, true], shown.Spans!.Select(s => s.Lit));   // the hook, both times it plays
        Assert.Contains("longer phrase", midi.ResultText);

        midi.ClearPhrasesCommand.Execute(null);
        Assert.Equal(3, midi.Results.Count);
    }

    [Fact]
    public async Task The_playground_lists_suggestions_as_soon_as_it_opens()
    {
        const int bar = 4 * 96;
        int[][] aMinor = [[57, 60, 64], [53, 57, 60], [48, 52, 55], [55, 59, 62]];
        var pack = Path.Combine(_root, "Packs");
        Directory.CreateDirectory(pack);
        var chords = new Core.Parsers.MidiClip { Name = "Keys", Ppq = 96 };
        chords.Track("Piano").AddRange(aMinor.SelectMany((c, i) => c.Select(k => new Core.Parsers.NoteEvent(i * bar, bar, k, 100))));
        File.WriteAllBytes(Path.Combine(pack, "Keys Am.mid"), Core.Export.MidiWriter.Write(chords));
        _db.SetList(Core.Midi.MidiIndexer.FoldersKey, [pack]);

        var main = Main();
        await main.Midi.IndexAsync();
        var playground = main.Playground;
        Assert.Equal(Core.Midi.MidiRoles.Chords, playground.SuggestRole.Role);
        main.SelectedNav = main.Nav.First(n => n.Page == playground);
        for (var i = 0; i < 300 && playground.Matches.Count == 0; i++) await Task.Delay(10);
        Assert.True(playground.Matches.Count > 0, playground.MatchStatus);
        Assert.Contains(playground.Matches, m => m.Match.Entry.Title.Contains("Keys"));
    }

    [Fact]
    public async Task What_goes_with_this_works_in_a_small_playground_as_long_as_the_longer_part()
    {
        const int bar = 4 * 96;
        int[][] aMinor = [[57, 60, 64], [53, 57, 60], [48, 52, 55], [55, 59, 62]];
        int[][] tune = [[69, 72, 76, 72], [65, 69, 72, 69], [67, 72, 76, 72], [67, 71, 74, 71]];
        var pack = Path.Combine(_root, "Packs");
        Directory.CreateDirectory(pack);
        var chords = new Core.Parsers.MidiClip { Name = "Keys", Ppq = 96 };
        chords.Track("Piano").AddRange(aMinor.SelectMany((c, i) => c.Select(k => new Core.Parsers.NoteEvent(i * bar, bar, k, 100))));
        File.WriteAllBytes(Path.Combine(pack, "Keys Am.mid"), Core.Export.MidiWriter.Write(chords));
        var lead = new Core.Parsers.MidiClip { Name = "Lead", Ppq = 96 };
        lead.Track("Lead").AddRange(tune.Concat(tune).SelectMany((b, i) => b.Select((k, j) => new Core.Parsers.NoteEvent(i * bar + j * 96, 96, k, 100))));
        File.WriteAllBytes(Path.Combine(pack, "Lead melody.mid"), Core.Export.MidiWriter.Write(lead));
        _db.SetList(Core.Midi.MidiIndexer.FoldersKey, [pack]);

        var main = Main();
        var midi = main.Midi;
        midi.Opened();
        await midi.IndexAsync();
        midi.Selected = midi.Results.Single(r => r.Title.Contains("Keys"));
        Assert.Null(midi.Quick);   // not fitting: no small playground
        midi.FitToSelectedCommand.Execute(null);
        midi.Selected = midi.Results.Single(r => r.Title.Contains("Lead"));
        for (var i = 0; i < 100 && midi.Quick?.Layers.Count != 2; i++) await Task.Delay(10);

        // 4 bars of chords under an 8-bar tune: the sketch runs all 8 bars, the chords going round again to fill them.
        var quick = midi.Quick!;
        Assert.True(quick.IsQuick);
        Assert.Equal(8, quick.Bars);
        Assert.Contains("Lead", quick.Layers[1].Name);
        var keys = quick.Layers[0].Layer.Notes;
        Assert.Contains(keys, n => n.Start >= quick.Length / 2);
        Assert.True(main.Playground.IsEmpty);   // the Playground's own idea is left alone

        // The Playground's settings work on it: longer, and heard as classical.
        quick.Bars = 12;
        Assert.Equal(12, quick.Bars);
        quick.IsClassical = true;
        Assert.True(quick.CanUndo);
    }

    [Fact]
    public async Task What_goes_with_this_plays_a_suggestion_on_top_looping_and_octaves_and_kinds_are_remembered()
    {
        const int bar = 4 * 96;
        int[][] aMinor = [[57, 60, 64], [53, 57, 60], [48, 52, 55], [55, 59, 62]];
        int[][] tune = [[69, 72, 76, 72], [65, 69, 72, 69], [67, 72, 76, 72], [67, 71, 74, 71]];
        var pack = Path.Combine(_root, "Packs");
        Directory.CreateDirectory(pack);
        var chords = new Core.Parsers.MidiClip { Name = "Keys", Ppq = 96 };
        chords.Track("Piano").AddRange(aMinor.SelectMany((c, i) => c.Select(k => new Core.Parsers.NoteEvent(i * bar, bar, k, 100))));
        File.WriteAllBytes(Path.Combine(pack, "Keys Am.mid"), Core.Export.MidiWriter.Write(chords));
        var lead = new Core.Parsers.MidiClip { Name = "Lead", Ppq = 96 };
        // Saved two octaves low, an 8-bar tune (the 4 bars twice).
        lead.Track("Lead").AddRange(tune.Concat(tune).SelectMany((b, i) => b.Select((k, j) => new Core.Parsers.NoteEvent(i * bar + j * 96, 96, k - 24, 100))));
        File.WriteAllBytes(Path.Combine(pack, "Lead melody.mid"), Core.Export.MidiWriter.Write(lead));
        _db.SetList(Core.Midi.MidiIndexer.FoldersKey, [pack]);
        var played = new List<string>();
        ((HeadlessUi)Ui.Current).Player = () => new Recorder(played);

        var midi = Main().Midi;
        midi.Opened();
        await midi.IndexAsync();
        midi.Selected = midi.Results.Single(r => r.Title.Contains("Keys"));
        midi.FitToSelectedCommand.Execute(null);
        Assert.True(midi.IsPairing);
        var melody = midi.Results.Single(r => r.Title.Contains("Lead"));
        Assert.Equal(0, melody.Semitones);
        Assert.Equal(2, melody.Octaves);   // up above the chords, where a melody sits
        Assert.NotNull(melody.Fit!.Together);
        Assert.Equal(8 * bar, melody.PairLength);   // 4 bars of chords twice under 8 bars of tune
        Assert.Equal(8 * bar, melody.PairPartner!.End);
        Assert.StartsWith("With Keys", melody.PairText);

        // Its play button plays it on top of the chords; the card's Play button plays it alone.
        midi.Selected = melody;
        await melody.PlayCommand.ExecuteAsync(null);
        Assert.EndsWith(":together", played[^1]);
        Assert.True(melody.IsPlayingTogether);
        await midi.PlaySelectedAloneCommand.ExecuteAsync(null);
        Assert.False(played[^1].EndsWith(":together"));
        Assert.True(melody.IsPlayingAlone);

        // Looping is remembered.
        midi.Loop = true;
        Assert.Equal("1", _db.GetSetting(MidiBrowserViewModel.LoopKey));
        Assert.Equal([0L, 4 * bar], MidiBrowserViewModel.Repeat([new Core.Parsers.NoteEvent(0, 96, 60, 100)], 4 * bar, 8 * bar).Select(n => n.Start));

        // An octave you pick sticks, for every copy, after the app starts again too.
        await melody.OctaveDownCommand.ExecuteAsync(null);
        Assert.Equal(1, melody.Octaves);
        Assert.True(melody.IsYourOctave);
        Assert.Equal(melody.Entry.Phrase.Lowest + 12 + 0, melody.Channel.Lowest);
        using (var again = new Core.Midi.MidiIndex(midi.Index.FilePath))
            Assert.Equal(1, again.All().Single(p => p.Hash == melody.Entry.Phrase.Hash).YourOctave);

        // The kind, in one click on the card.
        await midi.SetRoleCommand.ExecuteAsync(Core.Midi.MidiRoles.Arp);
        Assert.True(melody.RoleOptions.Single(o => o.Role == Core.Midi.MidiRoles.Arp).IsCurrent);
        Assert.False(melody.RoleOptions.Single(o => o.Role == Core.Midi.MidiRoles.Melody).IsCurrent);

        // Shuffle: a random order until the search changes.
        midi.ClearFitCommand.Execute(null);
        Assert.False(midi.IsPairing);
        midi.SelectedRole = midi.RoleChips[0];
        await midi.ShuffleCommand.ExecuteAsync(null);
        Assert.True(midi.IsShuffled);
        Assert.StartsWith("Shuffled", midi.ResultText);
        midi.Search = "lead";
        Assert.False(midi.IsShuffled);
    }

    [Fact]
    public async Task Trying_an_idea_edits_on_the_piano_roll_moves_each_part_and_stops_when_you_move_on()
    {
        const int bar = 4 * 96;
        int[][] aMinor = [[57, 60, 64], [53, 57, 60], [48, 52, 55], [55, 59, 62]];
        int[][] tune = [[69, 72, 76, 72], [65, 69, 72, 69], [67, 72, 76, 72], [67, 71, 74, 71]];
        var pack = Path.Combine(_root, "Packs");
        Directory.CreateDirectory(pack);
        var chords = new Core.Parsers.MidiClip { Name = "Keys", Ppq = 96 };
        chords.Track("Piano").AddRange(aMinor.SelectMany((c, i) => c.Select(k => new Core.Parsers.NoteEvent(i * bar, bar, k, 100))));
        File.WriteAllBytes(Path.Combine(pack, "Keys Am.mid"), Core.Export.MidiWriter.Write(chords));
        var lead = new Core.Parsers.MidiClip { Name = "Lead", Ppq = 96 };
        lead.Track("Lead").AddRange(tune.SelectMany((b, i) => b.Select((k, j) => new Core.Parsers.NoteEvent(i * bar + j * 96, 96, k, 100))));
        File.WriteAllBytes(Path.Combine(pack, "Lead melody.mid"), Core.Export.MidiWriter.Write(lead));
        _db.SetList(Core.Midi.MidiIndexer.FoldersKey, [pack]);
        var played = new List<string>();
        var player = new Recorder(played);
        ((HeadlessUi)Ui.Current).Player = () => player;

        var main = Main();
        var midi = main.Midi;
        midi.Opened();
        await midi.IndexAsync();
        midi.Selected = midi.Results.Single(r => r.Title.Contains("Keys"));
        midi.FitToSelectedCommand.Execute(null);
        var melody = midi.Results.Single(r => r.Title.Contains("Lead"));
        midi.Selected = melody;
        Assert.Contains("100% of its notes fit", melody.TogetherText);

        // A note moved a semitone off: the score shows it, and Undo puts it back.
        var edited = melody.BarNotes.Select((n, i) => i == 0 ? n with { Key = n.Key + 1 } : n).ToList();
        await melody.EditCommand.ExecuteAsync(edited);
        Assert.True(melody.IsEdited);
        Assert.Contains("rubs a semitone", melody.TogetherText);
        Assert.Equal(edited[0].Key, melody.BarNotes[0].Key);
        Assert.Equal(edited[0].Key, melody.FileClip().Tracks.Single().Value[0].Key);   // dragged out with the edit
        await melody.UndoEditCommand.ExecuteAsync(null);
        Assert.False(melody.IsEdited);
        Assert.False(melody.CanUndoEdit);

        // Each part's octave on its own; the edits follow this part's octave.
        await melody.EditCommand.ExecuteAsync(edited);
        var before = melody.EditorGuide!.Min(n => n.Key);
        await midi.WithOctaveDownCommand.ExecuteAsync(null);
        Assert.Equal(before - 12, melody.EditorGuide!.Min(n => n.Key));
        Assert.Equal("Octave -1", midi.WithOctaveText);
        await melody.OctaveUpCommand.ExecuteAsync(null);
        Assert.Equal(edited[0].Key + 12, melody.BarNotes[0].Key);

        // Changing the octave while it plays carries on playing.
        await melody.PlayCommand.ExecuteAsync(null);
        Assert.True(melody.IsPlayingTogether);
        await melody.OctaveDownCommand.ExecuteAsync(null);
        Assert.True(melody.IsPlayingTogether);
        Assert.EndsWith(":together", player.Playing);

        // Tempo and mute take effect; picking another part stops it, and so does leaving the page.
        midi.TempoValue = 90;
        Assert.Equal("90 BPM", midi.TempoText);
        midi.MuteWith = true;
        Assert.True(melody.IsPlaying);
        midi.Selected = null;   // another part picked (here, none)
        Assert.False(melody.IsPlaying);
        Assert.Null(player.Playing);
        midi.Selected = melody;
        await midi.PlaySelectedTogetherCommand.ExecuteAsync(null);
        Assert.True(melody.IsPlaying);
        main.SelectedNav = main.Nav.First(n => n.Page != midi);
        Assert.False(melody.IsPlaying);

        // A sound for each kind, remembered.
        melody.Sound = Core.Sounds.GmSounds.Choices.Single(c => c.Name == "Flute");
        Assert.Equal("73", _db.GetSetting(MidiBrowserViewModel.SoundKey + melody.Role));
        Assert.Equal("Flute", melody.Sound.Name);
    }

    [Fact]
    public async Task Loop_points_keep_both_parts_in_time_score_what_loops_and_find_parts_for_just_those_bars()
    {
        const int bar = 4 * 96;
        // Eight bars of chords, and a melody seven bars long: together they go round every eight bars, as shown.
        int[][] eight = [[57, 60, 64], [53, 57, 60], [48, 52, 55], [55, 59, 62], [50, 53, 57], [52, 56, 59], [57, 60, 64], [52, 56, 59]];
        var pack = Path.Combine(_root, "Packs");
        Directory.CreateDirectory(pack);
        var chords = new Core.Parsers.MidiClip { Name = "Keys", Ppq = 96 };
        chords.Track("Piano").AddRange(eight.SelectMany((c, i) => c.Select(k => new Core.Parsers.NoteEvent(i * bar, bar, k, 100))));
        File.WriteAllBytes(Path.Combine(pack, "Keys Am.mid"), Core.Export.MidiWriter.Write(chords));
        var lead = new Core.Parsers.MidiClip { Name = "Lead", Ppq = 96 };
        lead.Track("Lead").AddRange(Enumerable.Range(0, 7).SelectMany(i => new[] { 69, 72, 76, 72 }.Select((k, j) => new Core.Parsers.NoteEvent(i * bar + j * 96, 96, k + (i % 2), 100))));
        File.WriteAllBytes(Path.Combine(pack, "Lead melody.mid"), Core.Export.MidiWriter.Write(lead));
        _db.SetList(Core.Midi.MidiIndexer.FoldersKey, [pack]);
        var played = new List<string>();
        var player = new Recorder(played);
        ((HeadlessUi)Ui.Current).Player = () => player;

        var main = Main();
        var midi = main.Midi;
        midi.Opened();
        await midi.IndexAsync();
        Assert.Equal("Newest first", midi.OrderText);
        midi.Selected = midi.Results.Single(r => r.Title.Contains("Keys"));
        midi.FitToSelectedCommand.Execute(null);
        Assert.Equal("Best match first", midi.OrderText);
        await midi.ShuffleCommand.ExecuteAsync(null);
        Assert.True(midi.IsShuffled);
        midi.UnshuffleCommand.Execute(null);
        Assert.False(midi.IsShuffled);
        Assert.StartsWith("Best match first", midi.ResultText);

        var melody = midi.Results.Single(r => r.Title.Contains("Lead"));
        midi.Selected = melody;
        Assert.Equal(7 * bar, melody.LoopLength);
        Assert.Equal(8 * bar, melody.PairLength);
        // On the roll, and so as played, the melody starts again with the chords at bar 9 (its bar 8 is its first again).
        Assert.Equal(melody.BarNotes.Where(n => n.Start < bar).Select(n => n.Key),
                     melody.MineOnRoll.Where(n => n.Start >= 7 * bar).Select(n => n.Key));
        Assert.DoesNotContain(melody.MineOnRoll, n => n.Start >= 8 * bar);

        // Loop points: bars 5–6 loop, and the score is for what loops there.
        midi.Loop = false;
        midi.SetSectionCommand.Execute(new RollRange(4 * bar, 6 * bar));
        Assert.True(midi.Loop);
        Assert.Equal("Looping bars 5–6", midi.SectionText);
        Assert.StartsWith("Looping bars 5–6: ", melody.TogetherText);
        midi.TempoValue = 120;
        await midi.PlaySelectedTogetherCommand.ExecuteAsync(null);
        var twoBars = Core.Sounds.MidiRender.TicksToSeconds(2 * bar, 96, 120) * Core.Sounds.MidiRender.Rate;
        Assert.Equal(0, player.LastSamples % (int)Math.Round(twoBars));
        // A click inside them plays from there; outside, from their start.
        await melody.PairSeekCommand.ExecuteAsync(5.0 * bar);
        Assert.Equal(Core.Sounds.MidiRender.TicksToSeconds(bar, 96, 120), player.LastStart, 3);
        await melody.PairSeekCommand.ExecuteAsync(1.0 * bar);
        Assert.Equal(0, player.LastStart, 3);

        // Parts for just those bars: ranked for what plays there, each played in them.
        midi.FindForSectionCommand.Execute(null);
        Assert.True(midi.IsFittingSection);
        Assert.Equal("bars 5–6 of Keys Am", midi.FitName);
        Assert.StartsWith("Best match first for bars 5–6", midi.ResultText);
        var tried = midi.Results.Single(r => r.Title.Contains("Lead"));
        Assert.Equal(4 * bar, tried.PartStart);
        Assert.Equal(6 * bar, tried.PartEnd);
        Assert.Equal(8 * bar, tried.PairLength);
        Assert.All(tried.MineOnRoll, n => Assert.InRange(n.Start, 4 * bar, 6 * bar - 1));
        // New loop points: ranked for them instead; cleared, back to all of it.
        midi.SetSectionCommand.Execute(new RollRange(0, 2 * bar));
        Assert.Equal("bars 1–2 of Keys Am", midi.FitName);
        midi.ClearSectionCommand.Execute(null);
        Assert.False(midi.IsFittingSection);
        Assert.Equal("Keys Am", midi.FitName);
        Assert.DoesNotContain("Looping", midi.Results.Single(r => r.Title.Contains("Lead")).TogetherText);
    }

    [Fact]
    public async Task The_mini_playground_changes_a_part_in_a_click_and_loops_some_bars_of_it_on_its_own()
    {
        const int bar = 4 * 96;
        var pack = Path.Combine(_root, "Packs");
        Directory.CreateDirectory(pack);
        var lead = new Core.Parsers.MidiClip { Name = "Lead", Ppq = 96 };
        int[][] tune = [[69, 72, 76, 72], [65, 69, 72, 69]];
        lead.Track("Lead").AddRange(tune.SelectMany((b, i) => b.Select((k, j) => new Core.Parsers.NoteEvent(i * bar + j * 96 + 5, 90, k, 100))));
        File.WriteAllBytes(Path.Combine(pack, "Lead melody.mid"), Core.Export.MidiWriter.Write(lead));
        _db.SetList(Core.Midi.MidiIndexer.FoldersKey, [pack]);
        var played = new List<string>();
        var player = new Recorder(played);
        ((HeadlessUi)Ui.Current).Player = () => player;

        var midi = Main().Midi;
        midi.Opened();
        await midi.IndexAsync();
        var row = midi.Selected = midi.Results.Single();
        Assert.Equal(2 * bar, row.LoopLength);

        await row.ToolCommand.ExecuteAsync("up");
        Assert.Equal(70, row.BarNotes[0].Key);
        await row.ToolCommand.ExecuteAsync("quantise");
        Assert.All(row.BarNotes, n => Assert.Equal(0, n.Start % 24));
        await row.ToolCommand.ExecuteAsync("twice");
        Assert.Equal(4 * bar, row.LoopLength);
        Assert.Equal(16, row.BarNotes.Count);
        Assert.Equal(16, row.FileClip().Tracks.Single().Value.Count);   // dragged out as it is now
        await row.ToolCommand.ExecuteAsync("half");
        await row.ToolCommand.ExecuteAsync("half");
        Assert.Equal(bar, row.LoopLength);
        Assert.Equal(4, row.BarNotes.Count);
        // Undo goes back a step at a time, length and all.
        await row.UndoEditCommand.ExecuteAsync(null);
        Assert.Equal(2 * bar, row.LoopLength);
        await row.ResetEditsCommand.ExecuteAsync(null);
        Assert.Equal(69, row.BarNotes[0].Key);

        // Loop points with nothing fitted: just those bars play, round and round.
        midi.Loop = false;
        Assert.False(midi.CanFindForSection);
        midi.SetSectionCommand.Execute(new RollRange(bar, 2 * bar));
        await midi.PlaySelectedAloneCommand.ExecuteAsync(null);
        var oneBar = Core.Sounds.MidiRender.TicksToSeconds(bar, 96, midi.TempoValue) * Core.Sounds.MidiRender.Rate;
        Assert.Equal(0, player.LastSamples % (int)Math.Round(oneBar));
        Assert.True(row.IsPlayingAlone);
    }

    [Fact]
    public async Task The_midi_pages_numbers_are_options_that_are_remembered_and_take_effect()
    {
        const int bar = 4 * 96;
        var pack = Path.Combine(_root, "Packs");
        Directory.CreateDirectory(pack);
        var chords = new Core.Parsers.MidiClip { Name = "Keys", Ppq = 96 };
        int[][] eight = [[57, 60, 64], [53, 57, 60], [48, 52, 55], [55, 59, 62], [50, 53, 57], [52, 56, 59], [57, 60, 64], [52, 56, 59]];
        chords.Track("Piano").AddRange(eight.SelectMany((c, i) => c.Select(k => new Core.Parsers.NoteEvent(i * bar, bar, k, 100))));
        File.WriteAllBytes(Path.Combine(pack, "Keys Am.mid"), Core.Export.MidiWriter.Write(chords));
        var lead = new Core.Parsers.MidiClip { Name = "Lead", Ppq = 96 };
        lead.Track("Lead").AddRange(Enumerable.Range(0, 7).SelectMany(i => new[] { 69, 72, 76, 72 }.Select((k, j) => new Core.Parsers.NoteEvent(i * bar + j * 96 + 7, 90, k, 100))));
        File.WriteAllBytes(Path.Combine(pack, "Lead melody.mid"), Core.Export.MidiWriter.Write(lead));
        _db.SetList(Core.Midi.MidiIndexer.FoldersKey, [pack]);

        var main = Main();
        var midi = main.Settings.MidiPage;
        Assert.Same(main.Midi, midi);
        Assert.Equal(500, midi.ListLimit);
        midi.Opened();
        await midi.IndexAsync();
        midi.Selected = midi.Results.Single(r => r.Title.Contains("Keys"));
        midi.FitToSelectedCommand.Execute(null);
        var melody = midi.Selected = midi.Results.Single(r => r.Title.Contains("Lead"));
        // 7 bars over 8 meet again after 56: past the 32-bar most, they go round with the longer one.
        Assert.Equal(8 * bar, melody.PairLength);
        midi.LongestLoopBars = 64;
        Assert.Equal(56 * bar, melody.PairLength);

        midi.QuantiseTo = 8;
        await melody.ToolCommand.ExecuteAsync("quantise");
        Assert.All(melody.BarNotes, n => Assert.Equal(0, n.Start % 48));

        midi.ListLimit = 100;
        midi.SongTabLimit = 10;
        midi.GoodFitPercent = 90;

        // Remembered for next time.
        var again = Main().Midi;
        Assert.Equal((100, 10, 90, 8, 64), (again.ListLimit, again.SongTabLimit, again.GoodFitPercent, again.QuantiseTo, again.LongestLoopBars));
    }

    [Fact]
    public async Task Edits_picks_and_what_plays_outlast_the_list_being_read_again()
    {
        const int bar = 4 * 96;
        int[][] aMinor = [[57, 60, 64], [53, 57, 60], [48, 52, 55], [55, 59, 62]];
        int[][] tune = [[69, 72, 76, 72], [65, 69, 72, 69], [67, 72, 76, 72], [67, 71, 74, 71]];
        var pack = Path.Combine(_root, "Packs");
        Directory.CreateDirectory(pack);
        void Write(string file, string name, IEnumerable<Core.Parsers.NoteEvent> notes)
        {
            var clip = new Core.Parsers.MidiClip { Name = name, Ppq = 96 };
            clip.Track(name).AddRange(notes);
            File.WriteAllBytes(Path.Combine(pack, file), Core.Export.MidiWriter.Write(clip));
        }
        Write("Keys Am.mid", "Piano", aMinor.SelectMany((c, i) => c.Select(k => new Core.Parsers.NoteEvent(i * bar, bar, k, 100))));
        Write("Lead melody.mid", "Lead", tune.SelectMany((b, i) => b.Select((k, j) => new Core.Parsers.NoteEvent(i * bar + j * 96, 96, k, 100))));
        _db.SetList(Core.Midi.MidiIndexer.FoldersKey, [pack]);
        var played = new List<string>();
        var player = new Recorder(played);
        ((HeadlessUi)Ui.Current).Player = () => player;

        var main = Main();
        var midi = main.Midi;
        midi.Opened();
        await midi.IndexAsync();
        midi.Selected = midi.Results.Single(r => r.Title.Contains("Keys"));
        midi.FitToSelectedCommand.Execute(null);
        var melody = midi.Results.Single(r => r.Title.Contains("Lead"));
        midi.Selected = melody;
        await melody.EditCommand.ExecuteAsync(melody.BarNotes.Select((n, i) => i == 0 ? n with { Key = n.Key + 1 } : n).ToList());
        await midi.PlaySelectedTogetherCommand.ExecuteAsync(null);

        // A new file turns up (or a DAW saves): the list is read again, and nothing you were doing is lost.
        Write("Lead 2.mid", "Lead", tune.SelectMany((b, i) => b.Select((k, j) => new Core.Parsers.NoteEvent(i * bar + j * 96, 96, k - 12, 100))));
        await midi.IndexAsync();
        var again = midi.Results.Single(r => r.Title == melody.Title);
        Assert.NotSame(melody, again);
        Assert.Same(again, midi.Selected);
        Assert.True(again.IsEdited);
        Assert.True(again.CanUndoEdit);
        Assert.True(again.IsPlayingTogether);
        Assert.False(melody.IsPlaying);

        // "More like this" still leaves out the part itself after a reload.
        midi.MoreLikeSelectedCommand.Execute(null);
        Write("Lead 3.mid", "Lead", tune.SelectMany((b, i) => b.Select((k, j) => new Core.Parsers.NoteEvent(i * bar + j * 96, 48, k, 100))));
        await midi.IndexAsync();
        Assert.True(midi.IsLiking);
        Assert.DoesNotContain(midi.Results, r => r.Title == melody.Title);
    }

    [Fact]
    public async Task A_songs_midi_tab_lists_parts_that_fit_it_already_moved_and_not_its_own()
    {
        const int bar = 4 * 96;
        int[][] cMinor = [[60, 63, 67], [56, 60, 63], [51, 55, 58], [58, 62, 65]];
        var pack = Path.Combine(_root, "Packs");
        Directory.CreateDirectory(pack);
        var chords = new Core.Parsers.MidiClip { Name = "Moody", Ppq = 96 };
        chords.Track("Piano").AddRange(cMinor.SelectMany((c, i) => c.Select(k => new Core.Parsers.NoteEvent(i * bar, bar, k, 100))));
        File.WriteAllBytes(Path.Combine(pack, "Moody Cm.mid"), Core.Export.MidiWriter.Write(chords));
        var songs = Path.Combine(_root, "Songs");
        Directory.CreateDirectory(songs);
        var project = MidiBrowserTests.FlProjectWithChords(songs);   // Am F C G on Serum
        _db.Upsert(Core.Library.Scanner.Analyse(project));
        _db.SetList(Core.Midi.MidiIndexer.FoldersKey, [pack]);

        var played = new List<string>();
        var player = new Recorder(played);
        ((HeadlessUi)Ui.Current).Player = () => player;
        var main = Main();
        // Opened before any MIDI has been read: it says so, and works it out when opened again after.
        var tab = new SongMidiViewModel(main.Midi, main, project, "Chords Song");
        await tab.LoadAsync();
        Assert.StartsWith("No MIDI has been read yet", tab.Status);
        await main.Midi.IndexAsync();
        await tab.LoadAsync();

        var row = Assert.Single(tab.Rows);   // the song's own chords are left out
        Assert.Equal("Moody Cm", row.Title);
        Assert.Equal("down 3 semitones", row.MoveText);
        Assert.Equal(1, tab.GoodCount);
        Assert.Equal("1 good fit", tab.CountText);
        Assert.Contains("Am", tab.Status);

        // The MIDI page's tempo, mutes and loop points are for its own list: the tab plays with the song, all of it.
        await row.PlayCommand.ExecuteAsync(null);
        var ownTempo = player.LastSamples;
        main.Midi.Left();
        main.Midi.TempoValue = 60;
        main.Midi.SetSectionCommand.Execute(new RollRange(0, bar));
        main.Midi.Loop = false;   // looping is everywhere's (setting loop points turned it on)
        Assert.Equal(new RollRange(0, row.PairLength), row.Region);
        await row.PlayCommand.ExecuteAsync(null);
        Assert.Equal(ownTempo, player.LastSamples);
        main.Midi.Left();

        tab.Kind = SongMidiViewModel.Kinds.First(k => k.Role == Core.Midi.MidiRoles.Melody);
        await Task.Yield();
        Assert.Empty(tab.Rows);

        // The same list on the MIDI page, fitted to this song.
        await tab.OpenInMidiPageCommand.ExecuteAsync(null);
        Assert.Same(main.Midi, main.SelectedNav.Page);
        Assert.Equal("Chords Song", main.Midi.Fit!.Name);
    }

    [Fact]
    public async Task A_part_clicked_on_a_songs_midi_tab_opens_in_a_quick_view_to_try_with_the_song()
    {
        const int bar = 4 * 96;
        int[][] cMinor = [[60, 63, 67], [56, 60, 63], [51, 55, 58], [58, 62, 65]];
        var pack = Path.Combine(_root, "Packs");
        Directory.CreateDirectory(pack);
        var chords = new Core.Parsers.MidiClip { Name = "Moody", Ppq = 96 };
        chords.Track("Piano").AddRange(cMinor.SelectMany((c, i) => c.Select(k => new Core.Parsers.NoteEvent(i * bar, bar, k, 100))));
        File.WriteAllBytes(Path.Combine(pack, "Moody Cm.mid"), Core.Export.MidiWriter.Write(chords));
        var songs = Path.Combine(_root, "Songs");
        Directory.CreateDirectory(songs);
        var project = MidiBrowserTests.FlProjectWithChords(songs);
        _db.Upsert(Core.Library.Scanner.Analyse(project));
        _db.SetList(Core.Midi.MidiIndexer.FoldersKey, [pack]);
        ((HeadlessUi)Ui.Current).Player = () => new Recorder([]);
        var main = Main();
        await main.Midi.IndexAsync();
        var tab = new SongMidiViewModel(main.Midi, main, project, "Chords Song");
        await tab.LoadAsync();

        // Picked by itself when the list shows, but only a click opens it.
        var row = Assert.Single(tab.Rows);
        Assert.Same(row, tab.Selected);
        Assert.False(tab.IsOpen);
        tab.OpenPartCommand.Execute(row);
        Assert.True(tab.IsOpen);
        Assert.Same(row, tab.Open);
        Assert.True(row.CanPlayTogether);
        Assert.NotEmpty(row.EditorGuide!);   // the song's own chords, grey behind it

        // With the song, then on its own, then stopped.
        await row.PlayTogetherCommand.ExecuteAsync(null);
        Assert.True(row.IsPlayingTogether);
        await row.PlayAloneCommand.ExecuteAsync(null);
        Assert.True(row.IsPlayingAlone);
        Assert.False(row.IsPlayingTogether);
        await row.PlayAloneCommand.ExecuteAsync(null);
        Assert.False(row.IsPlaying);

        // A one-click change, undone like any edit; the file is never changed.
        var before = row.BarNotes.Select(n => n.Key).ToList();
        await row.ToolCommand.ExecuteAsync("up");
        Assert.Equal(before.Select(k => k + 1), row.BarNotes.Select(n => n.Key));
        Assert.True(row.IsEdited);
        await row.UndoEditCommand.ExecuteAsync(null);
        Assert.Equal(before, row.BarNotes.Select(n => n.Key));

        tab.CloseCommand.Execute(null);
        Assert.False(tab.IsOpen);
        main.Midi.Left();
    }

    [Fact]
    public async Task The_small_playground_mutes_the_song_parts_switched_off()
    {
        const int bar = 4 * 96;
        int[][] aMinor = [[57, 60, 64], [53, 57, 60], [48, 52, 55], [55, 59, 62]];
        var pack = Path.Combine(_root, "Packs");
        Directory.CreateDirectory(pack);
        var chords = new Core.Parsers.MidiClip { Name = "Same", Ppq = 96 };
        chords.Track("Piano").AddRange(aMinor.SelectMany((c, i) => c.Select(k => new Core.Parsers.NoteEvent(i * bar, bar, k + 12, 100))));
        File.WriteAllBytes(Path.Combine(pack, "Same Am.mid"), Core.Export.MidiWriter.Write(chords));
        var songs = Path.Combine(_root, "Songs");
        Directory.CreateDirectory(songs);
        var project = MidiBrowserTests.FlProjectWithChordsAndLead(songs);
        _db.Upsert(Core.Library.Scanner.Analyse(project));
        _db.SetList(Core.Midi.MidiIndexer.FoldersKey, [pack]);
        var main = Main();
        await main.Midi.IndexAsync();
        var tab = new SongMidiViewModel(main.Midi, main, project, "Two Parts");
        await tab.LoadAsync();
        tab.OpenPartCommand.Execute(tab.Rows.Single(r => r.Title == "Same Am"));

        // Opening a part opens the small playground: every part on, the song's keys and lead come in under it.
        for (var i = 0; i < 100 && tab.Quick?.Layers.Count != 3; i++) await Task.Delay(10);
        Assert.Contains(tab.Quick!.Layers, l => l.Name == "Lead");
        Assert.Contains(tab.Quick.Layers, l => l.Name == "Keys");

        // The lead switched off in "Fit to": muted in the small playground at once, still there to see and turn back on.
        tab.SongParts.Single(p => p.Name == "Lead").IsOn = false;
        Assert.True(tab.Quick.Layers.Single(l => l.Name == "Lead").Muted);
        Assert.False(tab.Quick.Layers.Single(l => l.Name == "Keys").Muted);
        for (var i = 0; i < 100 && tab.Rows.FirstOrDefault()?.With?.Parts.Count != 1; i++) await Task.Delay(10);
        Assert.NotNull(tab.Quick);   // a refit keeps the small playground

        // The same part opened again after the refit keeps what's been done; started again with the lead still off, it comes in muted.
        tab.Quick.Layers.Single(l => l.Name == "Keys").MuteCommand.Execute(null);
        Assert.True(tab.Quick.Layers.Single(l => l.Name == "Keys").Muted);
        await tab.StartAgainCommand.ExecuteAsync(null);
        Assert.False(tab.Quick.Layers.Single(l => l.Name == "Keys").Muted);
        Assert.True(tab.Quick.Layers.Single(l => l.Name == "Lead").Muted);
        tab.AllSongPartsCommand.Execute(null);
        Assert.False(tab.Quick.Layers.Single(l => l.Name == "Lead").Muted);
    }

    [Fact]
    public async Task A_songs_midi_tab_fits_to_just_the_song_parts_picked()
    {
        const int bar = 4 * 96;
        int[][] aMinor = [[57, 60, 64], [53, 57, 60], [48, 52, 55], [55, 59, 62]];
        var pack = Path.Combine(_root, "Packs");
        Directory.CreateDirectory(pack);
        var chords = new Core.Parsers.MidiClip { Name = "Same", Ppq = 96 };
        // The song's chords, voiced an octave up: other notes, so it isn't taken for the song's own part.
        chords.Track("Piano").AddRange(aMinor.SelectMany((c, i) => c.Select(k => new Core.Parsers.NoteEvent(i * bar, bar, k + 12, 100))));
        File.WriteAllBytes(Path.Combine(pack, "Same Am.mid"), Core.Export.MidiWriter.Write(chords));
        var songs = Path.Combine(_root, "Songs");
        Directory.CreateDirectory(songs);
        var project = MidiBrowserTests.FlProjectWithChordsAndLead(songs);
        _db.Upsert(Core.Library.Scanner.Analyse(project));
        _db.SetList(Core.Midi.MidiIndexer.FoldersKey, [pack]);
        var main = Main();
        await main.Midi.IndexAsync();
        var tab = new SongMidiViewModel(main.Midi, main, project, "Two Parts");
        await tab.LoadAsync();

        // Both of the song's parts, picked to start with; the clashing lead pulls the fit down.
        Assert.True(tab.HasSongParts);
        Assert.Equal(["Keys", "Lead"], tab.SongParts.Select(p => p.Name));
        var both = tab.Rows.Single(r => r.Title == "Same Am");
        Assert.Equal(2, both.With!.Parts.Count);
        tab.OpenPartCommand.Execute(both);

        // Just the chords: fitted and played against those alone, and the part stays open.
        tab.SongParts.Single(p => p.Name == "Lead").IsOn = false;
        for (var i = 0; i < 100 && tab.Rows.FirstOrDefault()?.With?.Parts.Count != 1; i++) await Task.Delay(10);
        var keysOnly = tab.Rows.Single(r => r.Title == "Same Am");
        Assert.Equal("Keys", Assert.Single(keysOnly.With!.Parts).Name);
        Assert.True(keysOnly.Fit!.Score > both.Fit!.Score);
        Assert.Same(keysOnly, tab.Open);
        Assert.Equal(keysOnly.With.Notes.Count, keysOnly.With.Parts[0].Notes.Count);

        // None picked: it says so. All parts brings them all back.
        tab.SongParts.Single(p => p.Name == "Keys").IsOn = false;
        await Task.Delay(20);
        Assert.StartsWith("Pick at least one", tab.Status);
        tab.AllSongPartsCommand.Execute(null);
        for (var i = 0; i < 100 && tab.Rows.FirstOrDefault()?.With?.Parts.Count != 2; i++) await Task.Delay(10);
        Assert.All(tab.SongParts, p => Assert.True(p.IsOn));
        Assert.Equal(2, tab.Rows.First().With!.Parts.Count);
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
        /// <summary>How long the last sound played was (samples), and where it was started (seconds).</summary>
        public int LastSamples { get; private set; }
        public double LastStart { get; private set; }
        public void Seek(double seconds) { }
        public void Play(string path, double startSeconds = 0) { played.Add(path); Playing = path; }
        public void Play(float[] mono, int sampleRate, string key, double startSeconds = 0)
        {
            played.Add(key);
            Playing = key;
            LastSamples = mono.Length;
            LastStart = startSeconds;
        }
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
