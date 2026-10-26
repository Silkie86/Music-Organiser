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
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, Enumerable.Range(0, bytes).Select(i => (byte)(i * 7 + relative.Length)).ToArray());
        if (modified is { } t) System.IO.File.SetLastWriteTime(path, t);
        return path;
    }

    string Copy(string from, string relative, DateTime modified)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
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
        // An uninstaller whose file is gone (I:\Current\unins000.exe) is never offered; one Windows finds itself is.
        Assert.False(Uninstallers.CanRun(Path.Combine(_root, "Current", "unins000.exe") + " /SILENT"));
        Assert.True(Uninstallers.CanRun(File("Current/unins000.exe")));
        Assert.True(Uninstallers.CanRun("MsiExec.exe /X{1234}"));
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
    public void Compact_can_let_every_autosave_go_and_lists_unused_audio_one_file_at_a_time()
    {
        var day = new DateTime(2026, 1, 1);
        var song = File("Songs/Rae/Rae.song", 300, day);
        for (var i = 0; i < 3; i++) File($"Songs/Rae/History/Rae {i}.song", 20, day.AddDays(i));
        File("Songs/Rae/Media/take 1.wav", 500);
        File("Songs/Rae/Media/take 2.wav", 900);
        var r = SongSpace.Measure([new(song, 300, day)], [Path.Combine(_root, "Songs")], new HashSet<string>(), keepBackups: 0);
        Assert.Equal(3, r.Items.Count(i => i.Kind == CompactKind.Backup));
        var audio = r.Items.Where(i => i.Kind == CompactKind.UnusedAudio).ToList();
        Assert.Equal(["take 2.wav", "take 1.wav"], audio.Select(a => Path.GetFileName(a.Paths.Single())));   // largest first
        Assert.Equal([900L, 500L], audio.Select(a => a.Bytes));
    }

    [Fact]
    public void Spare_copies_are_formats_no_project_uses_while_one_they_use_stays()
    {
        InstalledPlugin P(string name, string format) => new(name, format, $@"C:\Plugins\{format}\{name}.{format.ToLowerInvariant()}");
        var files = new[]
        {
            P("Serum", "VST2"), P("Serum", "VST3"), P("Serum", "AAX"),
            P("Pigments", "VST2"), P("Pigments", "VST3"),        // FL's "VST": no one knows which, so both stay
            P("Vital", "VST3"), P("Vital", "CLAP"),              // never used: the Never used chip has it, whole
            P("Diva", "VST3"),                                   // projects use its VST2, which isn't installed
        };
        IReadOnlyDictionary<string, int> F(params (string, int)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);
        var used = new Dictionary<string, IReadOnlyDictionary<string, int>>
        {
            ["serum"] = F(("VST3", 4)),
            ["pigments"] = F(("VST", 2)),
            ["diva"] = F(("VST2", 1)),
        };

        var spare = SpareCopies.Find(files, used, proTools: false, _ => 10);
        Assert.Equal(["Serum · AAX", "Serum · VST2"], spare.Select(s => $"{s.Name} · {s.Format}").Order());
        Assert.Contains("Pro Tools", spare.Single(s => s.Format == "AAX").Why);
        Assert.Equal("Your projects use its VST3 (4 projects); none use this VST2.", spare.Single(s => s.Format == "VST2").Why);

        // With Pro Tools on the computer, AAX copies are in use.
        Assert.DoesNotContain(SpareCopies.Find(files, used, proTools: true, _ => 10), s => s.Format == "AAX");
        Assert.Equal("AAX", TextScan.PluginFormatFromPath(@"C:\Program Files\Common Files\Avid\Audio\Plug-Ins\Serum.aaxplugin"));
    }

    [Fact]
    public void Thinning_keeps_the_first_save_the_newest_and_the_last_of_each_sitting()
    {
        var day = new DateTime(2026, 3, 1, 20, 0, 0);
        // Each a different length, so none is an identical copy of another.
        SongSpace.Save S(string name, DateTime when) => new(File($"Thin/{name}.flp", 40 + name[^1], when), 40 + name[^1], when);
        var saves = new[]
        {
            S("song_1", day), S("song_2", day.AddMinutes(20)), S("song_3", day.AddMinutes(50)),            // one evening
            S("song_4", day.AddDays(2)), S("song_5", day.AddDays(2).AddHours(1)),                            // two days on
            S("song_6", day.AddDays(30)), S("song_7", day.AddDays(30).AddMinutes(5)),                        // a month on: the newest
        };
        // song_1 is the first; song_3, song_5 end their sittings; song_7 is the newest. song_6 is a minute before it.
        Assert.Equal(["song_2", "song_4", "song_6"], SongSpace.Thin(saves, 3).Select(s => Path.GetFileNameWithoutExtension(s.Path)));
        Assert.Empty(SongSpace.Thin(saves, 0));
        Assert.Equal(["song_2"], SongSpace.Thin(saves, 1).Take(1).Select(s => Path.GetFileNameWithoutExtension(s.Path)));

        var r = SongSpace.Measure(saves, [Path.Combine(_root, "Thin")], new HashSet<string>(), sittingHours: 3);
        Assert.Equal(3, r.Items.Count(i => i.Kind == CompactKind.ThinnedSave));
        Assert.Equal(3 * 40 + '2' + '4' + '6', r.Thinnable);
        Assert.Equal(0, r.Freeable);   // thinning is only ever picked by hand
    }

    [Fact]
    public void A_folder_moves_to_another_drive_behind_a_link_and_moves_back()
    {
        var from = Path.GetDirectoryName(File("C/Docs/Hive/Sounds/pad.h2p", 300))!;
        File("C/Docs/Hive/Sounds/deep/bass.h2p", 200);
        var drive = Path.Combine(_root, "D", "Plugin Data");
        Directory.CreateDirectory(drive);
        Directory.CreateDirectory(Path.Combine(_root, "E"));
        // The folders C, D and E stand in for drives.
        string DriveOf(string path) => Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar)[0];

        Assert.Contains("inside", FolderMove.Problem(from, Path.Combine(from, "deep"), 500, driveOf: DriveOf));
        Assert.Contains("same drive", FolderMove.Problem(from, Path.Combine(_root, "C", "Elsewhere"), 500, driveOf: DriveOf));
        Assert.Contains("free", FolderMove.Problem(from, drive, 500, _ => 1000, DriveOf));

        Assert.Equal((false, ""), FolderMove.CheckSwap(from));   // the rename it tries first is undone
        Assert.True(Directory.Exists(from));
        var moved = FolderMove.Move(from, drive, 500, driveOf: DriveOf);
        Assert.True(FolderMove.IsLink(from));
        Assert.True(moved.HasOldCopy);
        Assert.Equal(Path.Combine(drive, "Sounds"), moved.To);
        // The plugin still finds its files where they were.
        Assert.Equal(200, new FileInfo(Path.Combine(from, "deep", "bass.h2p")).Length);
        Assert.Contains("moved already", FolderMove.Problem(from, drive, 500, driveOf: DriveOf));

        FolderMove.MoveBack(moved);
        Assert.False(FolderMove.IsLink(from));
        Assert.False(moved.HasOldCopy);
        Assert.Equal(300, new FileInfo(Path.Combine(from, "pad.h2p")).Length);

        // With its old copy deleted, moving back copies the folder home first.
        moved = FolderMove.Move(from, Path.Combine(_root, "E"), 500, driveOf: DriveOf);
        Directory.Delete(moved.OldCopy, true);
        FolderMove.MoveBack(moved);
        Assert.False(FolderMove.IsLink(from));
        Assert.Equal(200, new FileInfo(Path.Combine(from, "deep", "bass.h2p")).Length);
    }

    [Fact]
    public void Links_inside_a_moved_folder_stay_links_and_moving_back_keeps_what_was_saved_since()
    {
        var from = Path.GetDirectoryName(File("C/Arturia/Presets/Pigments/lead.pgtx", 300))!;
        from = Path.GetDirectoryName(from)!;
        // Arturia's FactoryEdit: a link to a place that isn't there (F12 log, 26 Oct). And one to a real folder elsewhere.
        Directory.CreateSymbolicLink(Path.Combine(from, "FactoryEdit"), Path.Combine(_root, "C", "nowhere"));
        var elsewhere = Path.GetDirectoryName(File("C/Big Library/huge.wav", 5000))!;
        Directory.CreateSymbolicLink(Path.Combine(from, "Library"), elsewhere);
        Directory.CreateDirectory(Path.Combine(_root, "D"));
        string DriveOf(string path) => Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar)[0];

        var moved = FolderMove.Move(from, Path.Combine(_root, "D"), 300, driveOf: DriveOf);
        Assert.True(FolderMove.IsLink(Path.Combine(moved.To, "FactoryEdit")));
        Assert.True(FolderMove.IsLink(Path.Combine(moved.To, "Library")));   // not a 5000-byte copy of the library
        Assert.Equal(5000, new FileInfo(Path.Combine(from, "Library", "huge.wav")).Length);

        // The plugin saves a preset after the move: it lands on the other drive, through the link.
        System.IO.File.WriteAllBytes(Path.Combine(from, "Pigments", "new.pgtx"), new byte[42]);
        Assert.True(System.IO.File.Exists(Path.Combine(moved.To, "Pigments", "new.pgtx")));
        Assert.False(System.IO.File.Exists(Path.Combine(moved.OldCopy, "Pigments", "new.pgtx")));
        FolderMove.MoveBack(moved);
        Assert.False(FolderMove.IsLink(from));
        Assert.Equal(42, new FileInfo(Path.Combine(from, "Pigments", "new.pgtx")).Length);   // kept, not lost with the old copy
        Assert.Equal(300, new FileInfo(Path.Combine(from, "Pigments", "lead.pgtx")).Length);
    }

    [Fact]
    public void The_folder_moved_folders_live_in_moves_and_their_links_follow()
    {
        var vital = Path.GetDirectoryName(File("C/Vital/pad.vital", 70))!;
        var arcade = Path.GetDirectoryName(File("C/Arcade/loop.wav", 90))!;
        var home = Path.Combine(_root, "I", "Plugin Data");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(Path.Combine(_root, "J"));
        string DriveOf(string path) => Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar)[0];
        IReadOnlyList<MovedFolder> moved = [FolderMove.Move(vital, home, 70, driveOf: DriveOf), FolderMove.Move(arcade, home, 90, driveOf: DriveOf)];
        Assert.Equal([home, home], moved.Select(FolderMove.HomeOf));

        // To another drive: copied, every link points at the copy, and the old place stays to delete later.
        var (now, problems) = FolderMove.MoveHome(home, Path.Combine(_root, "J"), moved, 160, driveOf: DriveOf);
        Assert.Empty(problems);
        var newHome = Path.Combine(_root, "J", "Plugin Data");
        Assert.All(now, m => Assert.Equal(newHome, FolderMove.HomeOf(m)));
        Assert.Equal(newHome, Path.GetDirectoryName(new DirectoryInfo(vital).LinkTarget));
        Assert.True(Directory.Exists(home));
        Assert.Equal(70, new FileInfo(Path.Combine(vital, "pad.vital")).Length);

        // Moved by hand in Explorer: the links point at nothing until the app is told where it went.
        var byHand = Path.Combine(_root, "J", "Moved by hand");
        Directory.Move(newHome, byHand);
        Assert.Equal(2, FolderMove.Lost(now).Count);
        (now, problems) = FolderMove.Repoint(newHome, byHand, now);
        Assert.Empty(problems);
        Assert.Empty(FolderMove.Lost(now));
        Assert.Equal(90, new FileInfo(Path.Combine(arcade, "loop.wav")).Length);

        // On the same drive it's just renamed.
        Directory.CreateDirectory(Path.Combine(_root, "J", "Sub"));
        (now, problems) = FolderMove.MoveHome(byHand, Path.Combine(_root, "J", "Sub"), now, 160, driveOf: DriveOf);
        Assert.Empty(problems);
        Assert.False(Directory.Exists(byHand));
        Assert.Equal(70, new FileInfo(Path.Combine(vital, "pad.vital")).Length);
    }

    [Fact]
    public void A_folder_an_update_made_in_place_of_the_link_is_linked_again()
    {
        var from = Path.GetDirectoryName(File("C/Kontakt/Lib/a.nki", 10))!;
        Directory.CreateDirectory(Path.Combine(_root, "D"));
        string DriveOf(string path) => Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar)[0];
        var moved = FolderMove.Move(from, Path.Combine(_root, "D"), 10, driveOf: DriveOf);
        Directory.Delete(moved.OldCopy, true);
        // The updater deletes the folder (the link) and makes a real one with its new files.
        Directory.Delete(from);
        File("C/Kontakt/Lib/b.nki", 20);
        Assert.Equal([moved], FolderMove.Replaced([moved]));

        var aside = FolderMove.LinkAgain(moved, 20);
        Assert.True(FolderMove.IsLink(from));
        Assert.Empty(FolderMove.Replaced([moved]));
        Assert.Equal(20, new FileInfo(Path.Combine(moved.To, "b.nki")).Length);   // the update's files joined the copy
        Assert.Equal(10, new FileInfo(Path.Combine(from, "a.nki")).Length);
        Assert.Equal(from + FolderMove.UpdateSuffix, aside.Path);
        Assert.Equal("left by an update", aside.Note);
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
        Song(db, "needs", DateTime.Now.AddDays(-9), "Not Installed Synth");
        db.ReplaceInstalledPlugins(["UVI Workstation", "FabFilter Pro-Q 3", "SampleTank", "Serum", "Serum 2"]);
        db.SavePluginSpaces([Space("Serum", 1000), Space("UVI Workstation", 38L << 30), Space("FabFilter Pro-Q 3", 0), Space("SampleTank", 6L << 30) is var t ? t with { Folders = [.. t.Folders, new(@"D:\IK Multimedia", SpaceKind.Sounds, 900, ["amplitube"])] } : t,
                             Space("Serum 2", 5000)], all: true);

        var plugins = page.Main.Plugins;
        plugins.Reload();
        // SampleTank is installed and no project uses it: it's listed now, with a size.
        var tank = plugins.Items.Single(p => p.Name == "SampleTank");
        Assert.Equal(0, tank.Projects);
        Assert.Equal("Never", tank.LastUsedText);
        // What it shares with its maker's other plugins has its own column, left out of its size (F12 note, 26 Oct).
        Assert.Equal(900, tank.SharedBytes);
        Assert.Equal("900 bytes", tank.SharedText);
        Assert.Equal((6L << 30) + 100, tank.Bytes);
        Assert.True(tank.Unused);
        Assert.True(plugins.Items.Single(p => p.Name == "UVI Workstation").Unused);   // last used 4 years ago
        // "Pro-Q 3" in a project is the installed "FabFilter Pro-Q 3": not a never-used plugin of its own.
        Assert.DoesNotContain(plugins.Items, p => p.Name == "FabFilter Pro-Q 3");
        Assert.False(plugins.Items.Single(p => p.Name == "Pro-Q 3").Unused);
        // Serum 2's own size, not Serum 1's; and Serum 1, whose name is that close, isn't offered for removal.
        Assert.Equal(5100, plugins.Items.Single(p => p.Name == "Serum2").Bytes);
        Assert.DoesNotContain(plugins.Items, p => p.Name == "Serum");
        // Never used comes first, then the biggest (Solomon's F12 note).
        Assert.Equal(["SampleTank", "UVI Workstation"], plugins.Candidates.Select(c => c.Name));
        Assert.True(tank.NeverUsed);
        Assert.False(tank.UnusedLately);
        Assert.True(plugins.Items.Single(p => p.Name == "UVI Workstation").UnusedLately);
        plugins.Filter = PluginsViewModel.BigUnused;
        Assert.Equal(2, plugins.View!.Cast<PluginUsage>().Count());
        plugins.Filter = PluginsViewModel.NeverUsed;
        Assert.Equal(["SampleTank"], plugins.View!.Cast<PluginUsage>().Select(p => p.Name));

        // The Install these cards' ✕: no cards, and no "everything's installed" line either.
        Assert.True(plugins.HasNeeded);
        plugins.HideNeededCommand.Execute(null);
        Assert.Equal(0, plugins.NeededCount);
        Assert.False(plugins.HasNeeded);
        Assert.False(plugins.AllInstalled);

        // The side panel's divider: remembered, never so narrow the panel can't be read.
        Assert.Equal(380, plugins.PanelWidth);
        plugins.PanelWidth = 120;
        Assert.Equal(300, plugins.PanelWidth);
        plugins.PanelWidth = 640;
        Assert.Equal(640, plugins.PanelWidth);

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
    public async Task A_songs_unused_audio_is_picked_one_file_at_a_time_and_none_is_ticked_for_you()
    {
        using var page = new Page(_root);
        var db = page.Db;
        db.AddRoot(Path.Combine(_root, "Bitwig"));
        var file = File("Bitwig/rae/rae.bwproject", 5);
        File("Bitwig/rae/bounces/old mix.wav", 700);
        File("Bitwig/rae/bounces/take.wav", 300);
        db.Upsert(new LibraryDb.IndexedFile(file, 5, 1, new ProjectInfo { Daw = "Bitwig Studio" }, null, [], []));
        var projects = page.Main.Projects;
        projects.Reload();
        projects.Selected = projects.Items.Single(i => i.Name == "rae");
        var space = projects.Detail!.Space;
        await space.LoadAsync();

        var audio = space.Rows.Single(r => r.Kind == CompactKind.UnusedAudio);
        Assert.False(audio.CanTick);   // the whole lot never goes at once
        Assert.Equal(["old mix.wav", "take.wav"], audio.Files.Select(f => f.Name));
        Assert.DoesNotContain(audio.Files, f => f.IsTicked);
        Assert.False(space.CompactCommand.CanExecute(null));

        audio.Files[1].IsTicked = true;
        Assert.Equal("Delete 300 bytes", space.ConfirmText);
        Assert.Contains("1 audio file you picked goes too", space.ConfirmNote);
        Assert.True(space.CompactCommand.CanExecute(null));
        Assert.Equal(SongSpace.DefaultSittingHours, space.Sitting!.Hours);   // thinning is offered, never ticked
        Assert.DoesNotContain(space.Rows, r => r.Kind == CompactKind.ThinnedSave && r.IsTicked);
    }

    [Fact]
    public void Old_copies_left_after_moves_are_on_one_card()
    {
        using var page = new Page(_root);
        var old = Path.GetDirectoryName(File("C/Vital (old copy)/pad.vital", 70))!;
        page.Db.SaveMovedFolders([
            new MovedFolder(Path.Combine(_root, "C", "Vital"), Path.Combine(_root, "I", "Vital"), old, 70, DateTime.Now) { Key = "vital" },
            new MovedFolder(Path.Combine(_root, "C", "Arcade"), Path.Combine(_root, "I", "Arcade"), Path.Combine(_root, "C", "Arcade (old copy)"), 90, DateTime.Now),
        ]);
        var plugins = page.Main.Plugins;
        plugins.Reload();
        Assert.True(plugins.HasOldCopies);   // only Vital's old copy is still there
        Assert.Equal("1 old copy left after a move", plugins.OldCopiesTitle);
        plugins.ReviewOldCopiesCommand.Execute(null);
        var item = Assert.Single(plugins.Card!.Items);
        Assert.Equal(old, item.Folder.Path);
        Assert.True(item.IsTicked);
        Assert.Equal("Delete 70 bytes", plugins.Card.ConfirmText);
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
