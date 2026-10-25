using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.Core.Space;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Core.Tests;

/// <summary>Freeing space: plugin sizes and "unused", song sizes and Compact. Nothing may lose music or a sample.</summary>
[Collection("Global UI")]
public class SpaceTests : IDisposable
{
    readonly string _root = Fixtures.TempDir();

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    string File(string relative, int bytes = 10, DateTime? modified = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, Enumerable.Range(0, bytes).Select(i => (byte)(i * 7 + relative.Length)).ToArray());
        if (modified is { } t) System.IO.File.SetLastWriteTime(path, t);
        return path;
    }

    string Copy(string from, string relative, DateTime modified)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.Copy(from, path);
        System.IO.File.SetLastWriteTime(path, modified);
        return path;
    }

    // ---- sizes ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Sizes_read_like_explorer_and_folders_add_up()
    {
        Assert.Equal("512 bytes", DiskSpace.Text(512));
        Assert.Equal("1.5 KB", DiskSpace.Text(1536));
        Assert.Equal("38.2 GB", DiskSpace.Text((long)(38.2 * (1L << 30))));
        Assert.Equal("610 MB", DiskSpace.Text(610L << 20));

        File("lib/a.wav", 100);
        File("lib/presets/b.fxp", 30);
        var m = DiskSpace.Measure(Path.Combine(_root, "lib"));
        Assert.Equal(130, m.Bytes);
        Assert.Equal(100, m.SoundBytes);   // the .wav is a sound, the preset isn't
        Assert.Equal(2, m.Files);

        Assert.True(DiskSpace.IsInside(@"C:\A\B\c.wav", @"C:\A"));
        Assert.True(DiskSpace.IsInside(@"C:\A", @"C:\A\"));
        Assert.False(DiskSpace.IsInside(@"C:\AB\c.wav", @"C:\A"));
    }

    // ---- plugins ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_folder_is_a_plugins_when_named_after_it()
    {
        Assert.True(PluginSpace.NameMatches("Serum Presets", "serum"));
        Assert.True(PluginSpace.NameMatches("Omnisphere Library", "omnisphere"));
        Assert.True(PluginSpace.NameMatches("Kontakt 8", "kontakt"));
        Assert.False(PluginSpace.NameMatches("Serum Backups From Friends", "serum"));
        Assert.False(PluginSpace.NameMatches("Serumania", "serum"));
        // Short names only match exactly.
        Assert.True(PluginSpace.NameMatches("M1", "m1"));
        Assert.False(PluginSpace.NameMatches("M1 Presets", "m1"));
    }

    [Fact]
    public void Plugins_get_their_own_folders_and_share_their_makers_folder()
    {
        var vst3 = Path.Combine(_root, "VST3");
        File("VST3/UVI/UVIWorkstation.vst3/Contents/x86_64-win/UVIWorkstation.vst3", 600);
        File("VST3/Falcon.vst3/Contents/x86_64-win/Falcon.vst3", 400);
        File("Docs/UVI/UVI Workstation/Soundbanks/Piano.ufs", 5000);
        File("Docs/UVI/Shared Licences/a.lic", 50);
        File("Docs/Falcon Presets/one.uvip", 70);
        File("Docs/Projects/my song/song.flp", 999);   // your project folder: never a plugin's
        var installed = PluginSpace.Group(PluginCatalog.ScanFiles([vst3]), [vst3], new Dictionary<string, string> { ["falcon"] = "UVI" });
        Assert.Equal("UVI", installed.Single(i => i.Name == "UVIWorkstation").Vendor);   // from the folder it sits in

        var spaces = PluginSpace.Measure(installed, [Path.Combine(_root, "Docs")], [vst3], [Path.Combine(_root, "Docs", "Projects")], _ => [], _ => [])
                                .ToDictionary(s => s.Name);
        var uvi = spaces["UVIWorkstation"];
        Assert.Equal(600, uvi.Program);
        Assert.Equal(5000, uvi.Sounds);
        // UVI's folder, less the plugin's own, belongs to both of UVI's plugins: shown, never counted twice.
        var shared = uvi.Folders.Single(f => f.Shared);
        Assert.Equal(50, shared.Bytes);
        Assert.Equal(["falcon"], shared.SharedWith);
        Assert.Equal(70, spaces["Falcon"].Presets);
        Assert.DoesNotContain(spaces.Values.SelectMany(s => s.Folders), f => f.Path.Contains("Projects"));
    }

    [Fact]
    public void A_folder_you_rule_out_or_add_wins_over_the_guess()
    {
        var vst3 = Path.Combine(_root, "VST3");
        File("VST3/Chroma.vst3/Contents/x86_64-win/Chroma.vst3", 100);
        var wrong = Path.GetDirectoryName(File("Docs/Chroma/keyboard-lights.cfg", 30))!;
        var library = Path.GetDirectoryName(File("D/Chroma Sounds On D/kick.wav", 900))!;
        var installed = PluginSpace.Group(PluginCatalog.ScanFiles([vst3]), [vst3], new Dictionary<string, string>());
        var space = PluginSpace.Measure(installed, [Path.Combine(_root, "Docs")], [vst3], [], _ => [library], _ => [wrong]).Single();
        Assert.DoesNotContain(space.Folders, f => f.Path == wrong);
        var added = space.Folders.Single(f => f.Added);
        Assert.Equal(SpaceKind.Sounds, added.Kind);
        Assert.Equal(1000, space.Bytes);
    }

    [Fact]
    public void Uninstallers_are_matched_by_name_and_never_a_daws()
    {
        Uninstaller[] all =
        [
            new("Xfer Serum 2", "Xfer Records", "\"C:\\Program Files\\Xfer\\unins2.exe\""),
            new("Serum", "Xfer Records", "\"C:\\Program Files\\Xfer\\unins1.exe\""),
            new("FL Studio 2026", "Image-Line", "C:\\Program Files\\Image-Line\\uninstall.exe"),
            new("M1 Backups", "Someone", "x.exe"),
            new("Korg M1", "KORG", "MsiExec.exe /X{1234}"),
        ];
        Assert.Equal(["Serum", "Xfer Serum 2"], Uninstallers.For("Serum", "Xfer Records", all).Select(u => u.Name));
        Assert.Equal("Xfer Serum 2", Uninstallers.For("Serum 2", "Xfer Records", all)[0].Name);
        Assert.Empty(Uninstallers.For("Fruity Reverb", "Image-Line", all));
        Assert.Empty(Uninstallers.For("M1", "KORG", all));   // two letters: only a program named exactly that
        Assert.Equal((@"C:\Program Files\Xfer\unins2.exe", ""), Uninstallers.Split(all[0].Command));
        Assert.Equal(("MsiExec.exe", "/X{1234}"), Uninstallers.Split("MsiExec.exe /X{1234}"));
        Assert.Equal((@"C:\Program Files\UVI\unins000.exe", "/SILENT"), Uninstallers.Split(@"C:\Program Files\UVI\unins000.exe /SILENT"));
    }

    [Fact]
    public void The_bin_keeps_what_it_takes()
    {
        if (OperatingSystem.IsWindows()) return;   // the real Recycle Bin: not in a test
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "share"));
        try
        {
            var folder = Path.GetDirectoryName(File("Old Library/a.wav"))!;
            Assert.Equal("", RecycleBin.Send(folder));
            Assert.False(Directory.Exists(folder));
            var bin = OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash")
                                                : Path.Combine(_root, "share", "Trash", "files");
            Assert.True(System.IO.File.Exists(Path.Combine(bin, "Old Library", "a.wav")));
            if (OperatingSystem.IsMacOS()) Directory.Delete(Path.Combine(bin, "Old Library"), true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", home);
        }
    }

    // ---- songs ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Compact_offers_copies_old_autosaves_and_caches_but_never_the_newest_save_or_audio()
    {
        var day = new DateTime(2026, 1, 1);
        var v1 = File("Songs/Zeki/Zeki.song", 300, day);
        var v2 = Copy(v1, "Songs/Zeki/Zeki (2).song", day.AddDays(1));        // the same bytes
        var v3 = Copy(v1, "Songs/Zeki/Zeki (3).song", day.AddDays(2));        // the same again, and the newest
        for (var i = 0; i < 7; i++) File($"Songs/Zeki/History/Zeki {i}.song", 20, day.AddDays(i));
        File("Songs/Zeki/Cache/overview.cache", 80);
        File("Songs/Zeki/Media/vocal.wav", 1000);
        File("Songs/Zeki/Media/unused take.wav", 500);
        var saves = new[] { v1, v2, v3 }.Select(p => new SongSpace.Save(p, 300, System.IO.File.GetLastWriteTime(p))).ToList();

        var r = SongSpace.Measure(saves, [Path.Combine(_root, "Songs")], new HashSet<string> { @"D:\Old PC\Media\vocal.wav" });
        Assert.True(r.OwnFolder);
        Assert.Equal(900 + 140 + 80 + 1500, r.Bytes);
        var same = r.Items.Where(i => i.Kind == CompactKind.SameSave).SelectMany(i => i.Paths).ToList();
        Assert.Equal(new[] { v1, v2 }.Order(), same.Order());   // the newest stays
        Assert.Equal(2, r.Items.Count(i => i.Kind == CompactKind.Backup));   // 7 autosaves, the newest 5 stay
        Assert.Contains(r.Items, i => i.Kind == CompactKind.Cache && i.Bytes == 80);
        // The take nothing names is shown, never offered: samples are never removed. The vocal counts as used by name.
        var audio = r.Items.Single(i => i.Kind == CompactKind.UnusedAudio);
        Assert.False(audio.CanRemove);
        Assert.Equal([Path.Combine(_root, "Songs", "Zeki", "Media", "unused take.wav")], audio.Paths);
        Assert.Equal(600 + 40 + 80, r.Freeable);
    }

    [Fact]
    public void A_song_sharing_its_folder_counts_its_saves_and_fl_studios_autosaves()
    {
        var a = File("Projects/garage.flp", 100, new DateTime(2025, 5, 1));
        File("Projects/other song.flp", 999);
        File("Projects/Backup/garage (autosave).flp", 40);
        File("Projects/Backup/garage (overwritten at 2213h05).flp", 40);
        File("Projects/Backup/other song (autosave).flp", 40);
        var r = SongSpace.Measure([new(a, 100, new DateTime(2025, 5, 1))], [Path.Combine(_root, "Projects")], new HashSet<string>(), keepBackups: 1);
        Assert.False(r.OwnFolder);
        Assert.Equal(100, r.Saves);
        Assert.Equal(80, r.Backups);
        Assert.Single(r.Items, i => i.Kind == CompactKind.Backup);
    }

    // ---- the pages --------------------------------------------------------------------------------------------------

    sealed class Page : IDisposable
    {
        public readonly LibraryDb Db;
        readonly SoundLibrary _soundDb;
        readonly SoundOrganiser _sounds;
        public readonly MainViewModel Main;

        public Page(string root)
        {
            Ui.Current = new ViewModelTests.HeadlessUi();
            Db = new LibraryDb(Path.Combine(root, "library.db"));
            _soundDb = new SoundLibrary(Path.Combine(root, "sounds.db"));
            _sounds = new SoundOrganiser(_soundDb);
            Main = new MainViewModel(Db, _sounds, null, null, Path.Combine(root, "data"));
        }

        public void Dispose()
        {
            _sounds.Dispose();
            _soundDb.Dispose();
            Db.Dispose();
        }
    }

    void Song(LibraryDb db, string name, DateTime saved, params string[] plugins)
    {
        var info = new ProjectInfo { Daw = "FL Studio" };
        info.Plugins.AddRange(plugins.Select(n => new PluginRef(n, "", PluginKind.Instrument, "VST3")));
        var file = File($"FL/{name}.flp", 5, saved);
        db.Upsert(new LibraryDb.IndexedFile(file, 5, new DateTimeOffset(saved).ToUnixTimeSeconds(), info, null, [], []));
    }

    static PluginSpaceInfo Space(string name, long sounds, string? soundsFolder = null) =>
        new(LibraryDb.NameKey(name), name, "", [
            new($@"C:\VST3\{name}.vst3", SpaceKind.Program, 100, []),
            new(soundsFolder ?? $@"D:\{name} Library", SpaceKind.Sounds, sounds, []),
        ], DateTime.Now);

    [Fact]
    public void Big_unused_plugins_are_flagged_and_one_a_project_names_differently_is_not()
    {
        using var page = new Page(_root);
        var db = page.Db;
        Song(db, "old", DateTime.Now.AddYears(-4), "UVI Workstation");
        Song(db, "new", DateTime.Now.AddDays(-3), "Pro-Q 3", "Serum2");
        db.ReplaceInstalledPlugins(["UVI Workstation", "FabFilter Pro-Q 3", "SampleTank", "Serum", "Serum 2"]);
        db.SavePluginSpaces([Space("Serum", 1000), Space("UVI Workstation", 38L << 30), Space("FabFilter Pro-Q 3", 0), Space("SampleTank", 6L << 30),
                             Space("Serum 2", 5000)], all: true);

        var plugins = page.Main.Plugins;
        plugins.Reload();
        // SampleTank is installed and no project uses it: it's listed now, with a size.
        var tank = plugins.Items.Single(p => p.Name == "SampleTank");
        Assert.Equal(0, tank.Projects);
        Assert.Equal("Never", tank.LastUsedText);
        Assert.True(tank.Unused);
        Assert.True(plugins.Items.Single(p => p.Name == "UVI Workstation").Unused);   // last used 4 years ago
        // "Pro-Q 3" in a project is the installed "FabFilter Pro-Q 3": not a never-used plugin of its own.
        Assert.DoesNotContain(plugins.Items, p => p.Name == "FabFilter Pro-Q 3");
        Assert.False(plugins.Items.Single(p => p.Name == "Pro-Q 3").Unused);
        // Serum 2's own size, not Serum 1's; and Serum 1, whose name is that close, isn't offered for removal.
        Assert.Equal(5100, plugins.Items.Single(p => p.Name == "Serum2").Bytes);
        Assert.DoesNotContain(plugins.Items, p => p.Name == "Serum");
        Assert.Equal(["UVI Workstation", "SampleTank"], plugins.Candidates.Select(c => c.Name));
        plugins.Filter = PluginsViewModel.BigUnused;
        Assert.Equal(2, plugins.View!.Cast<PluginUsage>().Count());

        // Five years instead of two: UVI's last song (4 years ago) is recent enough.
        plugins.UnusedYears = 5;
        Assert.False(plugins.Items.Single(p => p.Name == "UVI Workstation").Unused);
    }

    [Fact]
    public async Task Clean_up_keeps_sounds_a_project_still_needs()
    {
        using var page = new Page(_root);
        var db = page.Db;
        var sounds = Path.GetDirectoryName(File("Libraries/UVI Soundbanks/piano.ufs", 50))!;
        var tank = Path.GetDirectoryName(File("Libraries/SampleTank Library/kick.wav", 50))!;
        Song(db, "old", DateTime.Now.AddYears(-4), "UVI Workstation");
        db.ReplaceInstalledPlugins(["UVI Workstation", "SampleTank"]);
        db.SavePluginSpaces([Space("UVI Workstation", 50, sounds), Space("SampleTank", 50, tank)], all: true);
        var plugins = page.Main.Plugins;
        plugins.Reload();

        // UVI is unused for years, but a project still uses it: its sounds stay (Solomon's rule).
        await plugins.CleanUpCommand.ExecuteAsync(plugins.Items.Single(p => p.Name == "UVI Workstation"));
        var item = Assert.Single(plugins.Card!.Items);
        Assert.False(item.CanTick);
        Assert.Contains("Sounds stay while a project uses", item.Why);
        Assert.False(plugins.Card.RunCommand.CanExecute(null));
        plugins.Card.KeepCommand.Execute(null);
        Assert.Null(plugins.Card);

        // No project uses SampleTank: its sounds are ticked, and Delete names the size.
        await plugins.CleanUpCommand.ExecuteAsync(plugins.Items.Single(p => p.Name == "SampleTank"));
        Assert.True(plugins.Card!.Items.Single().IsTicked);
        Assert.Equal("Delete 50 bytes", plugins.Card.ConfirmText);
        Assert.True(plugins.Card.RunCommand.CanExecute(null));

        // A folder holding a sample any project uses is never offered, whatever the plugin.
        var info = new ProjectInfo { Daw = "FL Studio" };
        var song = File("FL/kicks.flp", 5);
        db.Upsert(new LibraryDb.IndexedFile(song, 5, 1, info, null, [(Path.Combine(tank, "kick.wav"), true)], []));
        plugins.Card.KeepCommand.Execute(null);
        await plugins.CleanUpCommand.ExecuteAsync(plugins.Items.Single(p => p.Name == "SampleTank"));
        Assert.Contains("A project uses a sample in it", plugins.Card!.Items.Single().Why);
    }

    [Fact]
    public void Songs_with_their_own_folder_show_its_measured_size()
    {
        using var page = new Page(_root);
        var db = page.Db;
        db.AddRoot(Path.Combine(_root, "Bitwig"));
        var folder = Path.Combine(_root, "Bitwig", "garage sept");
        var file = File("Bitwig/garage sept/garage sept.bwproject", 5);
        db.Upsert(new LibraryDb.IndexedFile(file, 5, 1, new ProjectInfo { Daw = "Bitwig Studio" }, null, [], []));
        var shared = File("FL/a.flp", 7);
        db.Upsert(new LibraryDb.IndexedFile(shared, 7, 1, new ProjectInfo { Daw = "FL Studio" }, null, [], []));
        db.Upsert(new LibraryDb.IndexedFile(File("FL/b.flp", 9), 9, 1, new ProjectInfo { Daw = "FL Studio" }, null, [], []));

        var projects = page.Main.Projects;
        projects.Reload();
        Assert.Equal([folder], projects.OwnFolderList().Select(f => f.Folder));   // FL's folder holds two songs
        Assert.Equal(5, projects.Items.Single(i => i.Name == "garage sept").SpaceBytes);   // not measured yet: its save

        new SpaceMeter(db).MeasureFolders(projects.OwnFolderList());
        File("Bitwig/garage sept/samples/kick.wav", 95);
        new SpaceMeter(db).MeasureFolders(projects.OwnFolderList());   // measured already this week: left alone
        projects.ApplySpace(db.FolderSpaces());
        Assert.Equal(5, projects.Items.Single(i => i.Name == "garage sept").SpaceBytes);
        db.SaveFolderSpaces([(folder, 100)]);
        projects.ApplySpace(db.FolderSpaces());
        Assert.Equal(100, projects.Items.Single(i => i.Name == "garage sept").SpaceBytes);
        Assert.Contains("100 bytes", projects.Items.Single(i => i.Name == "garage sept").DetailsLine);
        Assert.Equal(7, projects.Items.Single(i => i.Name == "a").SpaceBytes);

        projects.BiggestFirst = true;
        Assert.Equal("garage sept", projects.View.Cast<ProjectItem>().First().Name);
    }
}
