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

        // A OneDrive file only online ("offline", or "recall on data access") takes no room, and moving it would download it.
        Assert.True(DiskSpace.IsOnlineOnly(FileAttributes.Archive | FileAttributes.Offline));
        Assert.True(DiskSpace.IsOnlineOnly(FileAttributes.ReparsePoint | (FileAttributes)0x00400000));
        Assert.False(DiskSpace.IsOnlineOnly(FileAttributes.Archive | FileAttributes.ReparsePoint));   // kept on this PC
        Assert.False(DiskSpace.HasOnlineOnly(Path.Combine(_root, "lib")));

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
    public void The_administrator_bin_step_does_nothing_with_a_list_changed_after_the_app_wrote_it()
    {
        // Review: the list sits in Temp while Windows asks, and the step binned whatever it said by then.
        if (OperatingSystem.IsWindows()) return;   // the real Recycle Bin: not in a test
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "share"));
        try
        {
            var ticked = File("Plugins/Old.vst3");
            var other = File("Plugins/Keep.vst3");
            var list = Path.Combine(_root, "list.txt");
            var result = Path.Combine(_root, "result.txt");
            System.IO.File.WriteAllText(list, ticked + "\n");
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(list)));

            // Another line slipped in while Windows was asking: nothing goes, not even the ticked one.
            System.IO.File.AppendAllText(list, other + "\n");
            Assert.True(RecycleBin.RunHelper([RecycleBin.HelperFlag, list, result, fingerprint]));
            Assert.True(System.IO.File.Exists(ticked));
            Assert.True(System.IO.File.Exists(other));
            Assert.False(System.IO.File.Exists(result));

            // The list as written: the ticked file goes, and the step says so.
            System.IO.File.WriteAllText(list, ticked + "\n");
            Assert.True(RecycleBin.RunHelper([RecycleBin.HelperFlag, list, result, fingerprint]));
            Assert.False(System.IO.File.Exists(ticked));
            Assert.True(System.IO.File.Exists(other));
            Assert.Equal([$"{ticked}\t"], System.IO.File.ReadAllLines(result));

            // Started for this some other way: nothing, and not the app's window either.
            Assert.True(RecycleBin.RunHelper([RecycleBin.HelperFlag, list, result]));
            Assert.False(RecycleBin.RunHelper(["--something-else"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", home);
        }
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
        Assert.DoesNotContain(page.Main.Activities.Items, a => a.Title.Contains("UVI"));   // it stays anyway: not walked
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
        var vital = Path.GetDirectoryName(File("C/Vital/pad.vital", 70))!;
        Directory.CreateDirectory(Path.Combine(_root, "I"));
        string DriveOf(string path) => Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar)[0];
        var moved = FolderMove.Move(vital, Path.Combine(_root, "I"), 70, driveOf: DriveOf) with { Key = "vital" };
        page.Db.SaveMovedFolders([
            moved,
            new MovedFolder(Path.Combine(_root, "C", "Arcade"), Path.Combine(_root, "I", "Arcade"), Path.Combine(_root, "C", "Arcade (old copy)"), 90, DateTime.Now),
        ]);
        var plugins = page.Main.Plugins;
        plugins.Reload();
        Assert.True(plugins.HasOldCopies);   // only Vital's old copy is still there
        Assert.Equal("1 old copy left after a move", plugins.OldCopiesTitle);
        plugins.ReviewOldCopiesCommand.Execute(null);
        var item = Assert.Single(plugins.Card!.Items);
        Assert.Equal(moved.OldCopy, item.Folder.Path);
        Assert.True(item.IsTicked);
        Assert.Equal("Delete 70 bytes", plugins.Card.ConfirmText);
    }

    [Fact]
    public async Task An_old_copy_is_never_offered_while_its_moved_copy_cant_be_reached()
    {
        using var page = new Page(_root);
        var vital = Path.GetDirectoryName(File("C/Vital/pad.vital", 70))!;
        Directory.CreateDirectory(Path.Combine(_root, "I"));
        string DriveOf(string path) => Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar)[0];
        var moved = FolderMove.Move(vital, Path.Combine(_root, "I"), 70, driveOf: DriveOf) with { Key = "vital" };
        page.Db.SaveMovedFolders([moved]);
        Assert.True(FolderMove.OldCopyCanGo(moved));
        var plugins = page.Main.Plugins;
        plugins.Reload();
        plugins.ReviewOldCopiesCommand.Execute(null);
        var card = plugins.Card!;

        // The drive is unplugged (or the folder renamed) after the card opened: the old copy is the only one left to use.
        Directory.Move(moved.To, moved.To + " renamed");
        Assert.False(FolderMove.OldCopyCanGo(moved));
        await card.RunCommand.ExecuteAsync(null);
        Assert.True(Directory.Exists(moved.OldCopy));
        Assert.Equal(70, new FileInfo(Path.Combine(moved.OldCopy, "pad.vital")).Length);
        // Nor is it on the card, or offered from the plugin's Space tab.
        plugins.Reload();
        Assert.False(plugins.HasOldCopies);
        plugins.DeleteOldCopy(moved);
        Assert.Null(plugins.Card);

        // An update put a real folder where the link was: not offered either.
        Directory.Move(moved.To + " renamed", moved.To);
        Directory.Delete(vital);
        File("C/Vital/new.vital", 5);
        Assert.False(FolderMove.OldCopyCanGo(moved));
    }

    [Fact]
    public void The_old_place_of_moved_folders_waits_while_a_link_still_points_into_it()
    {
        using var page = new Page(_root);
        var home = Path.Combine(_root, "I", "Plugin Data");
        File("I/Plugin Data/Vital/pad.vital", 70);
        File("J/Plugin Data/Vital/pad.vital", 70);
        var still = new MovedFolder(Path.Combine(_root, "C", "Arcade"), Path.Combine(home, "Arcade"), "", 90, DateTime.Now);
        var repointed = new MovedFolder(Path.Combine(_root, "C", "Vital"), Path.Combine(_root, "J", "Plugin Data", "Vital"), "", 70, DateTime.Now);
        var old = new OldHome(home, 160);
        Assert.False(FolderMove.OldHomeCanGo(old, [still, repointed]));   // Arcade's link couldn't be pointed at J
        Assert.True(FolderMove.OldHomeCanGo(old, [repointed]));

        page.Db.SaveMovedFolders([still, repointed]);
        page.Db.SaveOldHomes([old]);
        var plugins = page.Main.Plugins;
        plugins.Reload();
        Assert.False(plugins.HasOldCopies);
        page.Db.SaveMovedFolders([repointed]);
        plugins.Reload();
        Assert.True(plugins.HasOldCopies);
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

    // ---- found by the whole-app review (2026-10-26) -----------------------------------------------------------------

    [Fact]
    public void Size_walks_skip_links_to_other_places_but_count_real_folders()
    {
        File("lib/a.wav", 100);
        File("lib/presets/b.fxp", 30);
        var elsewhere = Path.GetDirectoryName(File("elsewhere/big.wav", 5000))!;
        var lib = Path.Combine(_root, "lib");
        Directory.CreateSymbolicLink(Path.Combine(lib, "linked"), elsewhere);
        Directory.CreateSymbolicLink(Path.Combine(lib, "gone"), Path.Combine(_root, "nowhere"));
        Assert.Equal(130, DiskSpace.Measure(lib).Bytes);   // the linked folder's 5000 bytes live elsewhere
        Assert.True(FolderMove.IsLink(Path.Combine(lib, "linked")));
        Assert.True(FolderMove.IsLink(Path.Combine(lib, "gone")));   // a link whose place has gone is still a link
        Assert.False(FolderMove.IsLink(Path.Combine(lib, "presets")));
        Assert.False(FolderMove.IsLink(lib));
    }

    [Fact]
    public async Task Clean_up_never_takes_a_makers_folder_holding_sounds_a_project_still_needs()
    {
        using var page = new Page(_root);
        var db = page.Db;
        var maker = Path.Combine(_root, "Docs", "Arturia");
        File("Docs/Arturia/Shared/settings.bin", 300);
        var pigments = Path.GetDirectoryName(File("Docs/Arturia/Pigments/pad.wav", 5000))!;
        Song(db, "old", DateTime.Now.AddYears(-4), "Pigments");
        db.ReplaceInstalledPlugins(["Pigments", "Analog Lab"]);
        var (pk, ak) = (LibraryDb.NameKey("Pigments"), LibraryDb.NameKey("Analog Lab"));
        // As PluginSpace.Measure gives them: the maker's folder is measured less Pigments' own folder inside it.
        db.SavePluginSpaces([
            new(pk, "Pigments", "Arturia", [new(@"C:\VST3\Pigments.vst3", SpaceKind.Program, 100, []), new(pigments, SpaceKind.Sounds, 5000, []),
                                            new(maker, SpaceKind.Presets, 300, [ak])], DateTime.Now),
            new(ak, "Analog Lab", "Arturia", [new(@"C:\VST3\Analog Lab.vst3", SpaceKind.Program, 100, []), new(maker, SpaceKind.Presets, 300, [pk])], DateTime.Now),
        ], all: true);
        var plugins = page.Main.Plugins;
        plugins.Reload();

        await plugins.CleanUpCommand.ExecuteAsync(plugins.Items.Single(p => p.Name == "Pigments"));
        Assert.Contains("Sounds stay while a project uses Pigments", plugins.Card!.Items.Single(i => i.Path == pigments).Why);
        // Binning the maker's folder would bin Pigments' sounds with it.
        var whole = plugins.Card.Items.Single(i => i.Path == maker);
        Assert.False(whole.CanTick);
        Assert.Contains("It holds Pigments", whole.Why);
        // Move measures the folder whole, the folders in it too.
        Assert.Equal([pigments], PluginsViewModel.FoldersInside(maker, db.PluginSpaces().Values.ToList()).Select(x => x.Folder.Path).Distinct());
    }

    [Fact]
    public void A_folder_with_another_song_inside_isnt_the_songs_own()
    {
        var day = new DateTime(2026, 1, 1);
        var intro = File("Songs/Album/Intro.song", 300, day);
        File("Songs/Album/Remix/Remix.song", 200, day);
        for (var i = 0; i < 7; i++) File($"Songs/Album/Remix/History/Remix {i}.song", 20, day.AddDays(i));
        var r = SongSpace.Measure([new(intro, 300, day)], [Path.Combine(_root, "Songs")], new HashSet<string>(), keepBackups: 0);
        Assert.False(r.OwnFolder);
        Assert.DoesNotContain(r.Items, i => i.Kind == CompactKind.Backup);   // the Remix's autosaves aren't Intro's
        Assert.Equal(300, r.Bytes);

        // The song's own autosave folder doesn't count as another song.
        var remix = Path.Combine(_root, "Songs", "Album", "Remix", "Remix.song");
        Assert.True(SongSpace.Measure([new(remix, 200, day)], [Path.Combine(_root, "Songs")], new HashSet<string>(), keepBackups: 0).OwnFolder);
    }

    [Fact]
    public void A_folder_with_another_songs_saves_inside_isnt_measured_as_the_songs_own()
    {
        using var page = new Page(_root);
        var db = page.Db;
        db.AddRoot(Path.Combine(_root, "Bitwig"));
        foreach (var f in new[] { File("Bitwig/Album/Intro.bwproject", 5), File("Bitwig/Album/Remix/Remix.bwproject", 6) })
            db.Upsert(new LibraryDb.IndexedFile(f, 5, 1, new ProjectInfo { Daw = "Bitwig Studio" }, null, [], []));
        var projects = page.Main.Projects;
        projects.Reload();
        Assert.Equal([Path.Combine(_root, "Bitwig", "Album", "Remix")], projects.OwnFolderList().Select(f => f.Folder));
    }

    [Fact]
    public void Audio_in_a_backups_folder_is_never_an_old_autosave()
    {
        var day = new DateTime(2026, 1, 1);
        var song = File("Songs/Rae/Rae.song", 300, day);
        File("Songs/Rae/History/Rae 1.song", 20, day);
        var stem = File("Songs/Rae/Backups/stem.wav", 900, day);
        File("Songs/Rae/Samples/Backup/kick.wav", 50, day);
        var r = SongSpace.Measure([new(song, 300, day)], [Path.Combine(_root, "Songs")], new HashSet<string> { @"D:\Old\kick.wav" }, keepBackups: 0);
        Assert.Equal([Path.Combine(_root, "Songs", "Rae", "History", "Rae 1.song")], r.Items.Where(i => i.Kind == CompactKind.Backup).SelectMany(i => i.Paths));
        // The stem is audio no save uses: listed to pick by hand, never ticked. The kick is used, so not listed at all.
        Assert.Equal([stem], r.Items.Where(i => i.Kind == CompactKind.UnusedAudio).SelectMany(i => i.Paths));
        Assert.Equal(20, r.Freeable);
    }

    [Fact]
    public void Fl_autosaves_named_like_another_songs_are_left_alone()
    {
        var a = File("Projects/A/Beat.flp", 100, new DateTime(2025, 5, 1));
        File("Projects/Backup/Beat (autosave).flp", 40);
        File("Projects/Backup/Beat (overwritten at 2213h05).flp", 40);
        var shared = new SongSpace.Elsewhere([], _ => new HashSet<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "beat" });
        var r = SongSpace.Measure([new(a, 100, new DateTime(2025, 5, 1))], [Path.Combine(_root, "Projects")], new HashSet<string>(), keepBackups: 0, elsewhere: shared);
        Assert.Equal(0, r.Backups);
        Assert.DoesNotContain(r.Items, i => i.Kind == CompactKind.Backup);

        using var page = new Page(_root);
        var b = File("Projects/B/Beat.flp", 7);
        foreach (var f in new[] { a, b }) page.Db.Upsert(new LibraryDb.IndexedFile(f, 5, 1, new ProjectInfo { Daw = "FL Studio" }, null, [], []));
        Assert.Equal(new[] { a, b }.Order(), page.Db.FlProjectsNamed(["beat"]).Order());
    }

    [Fact]
    public void Audio_another_project_uses_or_in_a_sample_folder_isnt_offered()
    {
        var day = new DateTime(2026, 1, 1);
        var song = File("Songs/Rae/Rae.song", 300, day);
        File("Songs/Rae/Bounces/bounce.wav", 700);
        File("Songs/Rae/My Samples/snare.wav", 50);
        var take = File("Songs/Rae/Media/take.wav", 300);
        var elsewhere = new SongSpace.Elsewhere([Path.Combine(_root, "Songs", "Rae", "My Samples")],
                                                names => names.Where(n => n == "bounce.wav").ToHashSet(StringComparer.OrdinalIgnoreCase), new HashSet<string>());
        var r = SongSpace.Measure([new(song, 300, day)], [Path.Combine(_root, "Songs")], new HashSet<string>(), elsewhere: elsewhere);
        Assert.Equal([take], r.Items.Where(i => i.Kind == CompactKind.UnusedAudio).SelectMany(i => i.Paths));

        using var page = new Page(_root);
        var remix = File("FL/remix.flp", 5);
        page.Db.Upsert(new LibraryDb.IndexedFile(remix, 5, 1, new ProjectInfo { Daw = "FL Studio" }, null, [(@"C:\Users\Old PC\Bounces\bounce.wav", false)], []));
        Assert.Equal(["bounce.wav"], page.Db.SampleNamesUsed(["bounce.wav", "take.wav"]));
    }

    [Fact]
    public async Task Clean_up_keeps_a_folder_holding_a_sample_a_project_names_by_an_old_path()
    {
        using var page = new Page(_root);
        var db = page.Db;
        var tank = Path.GetDirectoryName(Path.GetDirectoryName(File("Libraries/SampleTank Library/Kicks/kick.wav", 50)))!;
        db.ReplaceInstalledPlugins(["SampleTank"]);
        db.SavePluginSpaces([Space("SampleTank", 50, tank)], all: true);
        // Saved on another PC: the project's path is that PC's, and the sample was found here since.
        db.Upsert(new LibraryDb.IndexedFile(File("FL/kicks.flp", 5), 5, 1, new ProjectInfo { Daw = "FL Studio" }, null,
                                            [(@"C:\Users\OldPC\Documents\SampleTank Library\Kicks\kick.wav", true)], []));
        var plugins = page.Main.Plugins;
        plugins.Reload();
        await plugins.CleanUpCommand.ExecuteAsync(plugins.Items.Single(p => p.Name == "SampleTank"));
        var item = plugins.Card!.Items.Single();
        Assert.False(item.CanTick);
        Assert.Contains("A project uses a sample in it", item.Why);
        // Walked as a job you can stop, and only because the folder would otherwise be offered.
        Assert.Contains(page.Main.Activities.Items, a => a.Title == "Checked SampleTank's folders");
    }

    [Fact]
    public void Thinning_and_identical_saves_together_keep_the_last_save_of_a_sitting()
    {
        var day = new DateTime(2026, 3, 1, 19, 0, 0);
        SongSpace.Save S(string path, DateTime when) => new(path, new FileInfo(path).Length, when);
        var s0 = S(File("Thin2/song_0.flp", 41, day), day);
        var s1 = S(File("Thin2/song_1.flp", 300, day.AddMinutes(30)), day.AddMinutes(30));   // the end of the first sitting
        // Two days on: opened and saved at once with nothing changed, then worked on.
        var s2a = S(Copy(s1.Path, "Thin2/song_2a.flp", day.AddDays(2)), day.AddDays(2));
        var s2b = S(File("Thin2/song_2b.flp", 52, day.AddDays(2).AddMinutes(30)), day.AddDays(2).AddMinutes(30));
        var s2c = S(File("Thin2/song_2c.flp", 63, day.AddDays(2).AddMinutes(60)), day.AddDays(2).AddMinutes(60));
        var r = SongSpace.Measure([s0, s1, s2a, s2b, s2c], [Path.Combine(_root, "Thin2")], new HashSet<string>(), sittingHours: 3);
        var gone = r.Items.Where(i => i.Kind is CompactKind.SameSave or CompactKind.ThinnedSave).SelectMany(i => i.Paths).ToList();
        Assert.Contains(s1.Path, gone);   // the copy goes…
        Assert.DoesNotContain(s2a.Path, gone);   // …and the save just like it stays, so the sitting's last state does
        Assert.Contains(s2b.Path, gone);
    }

    [Fact]
    public void Moving_back_puts_the_link_back_when_the_old_copy_cant_take_its_place()
    {
        var from = Path.GetDirectoryName(File("C/Docs/Hive/Sounds/pad.h2p", 300))!;
        Directory.CreateDirectory(Path.Combine(_root, "D"));
        string DriveOf(string path) => Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar)[0];
        var moved = FolderMove.Move(from, Path.Combine(_root, "D"), 300, driveOf: DriveOf);
        var result = Path.Combine(_root, "result.txt");
        // The old copy can't be renamed into place (here: it isn't there).
        Assert.True(FolderMove.RunHelper([FolderMove.HelperFlag, "unswap", from, Path.Combine(_root, "C", "nothing here"), result]));
        Assert.Contains("wouldn't move the folder back", System.IO.File.ReadAllText(result));
        Assert.True(FolderMove.IsLink(from));
        Assert.Equal(300, new FileInfo(Path.Combine(from, "pad.h2p")).Length);   // the plugin still finds its files
    }

    [Fact]
    public void Another_versions_uninstaller_is_never_the_one_picked()
    {
        Uninstaller ozone11 = new("iZotope Ozone 11 Advanced", "iZotope, Inc.", "x.exe"), ozone9 = new("iZotope Ozone 9", "iZotope, Inc.", "y.exe"),
                    serum2 = new("Xfer Serum 2", "Xfer Records", "z.exe");
        Assert.True(Uninstallers.IsOtherVersion("Ozone 9", ozone11));
        Assert.False(Uninstallers.IsOtherVersion("Ozone 9", ozone9));
        Assert.Equal([ozone9, ozone11], Uninstallers.For("Ozone 9", "iZotope", [ozone11, ozone9]));
        // A plugin named without a number could be any version: Kontakt 6 installs as "Kontakt", and its uninstaller is picked.
        Uninstaller kontakt6 = new("Native Instruments Kontakt 6", "Native Instruments", "k6.exe"), kontakt7 = new("Native Instruments Kontakt 7", "Native Instruments", "k7.exe");
        Assert.False(Uninstallers.IsOtherVersion("Kontakt", kontakt6));
        Assert.Equal(kontakt6, Uninstallers.For("Kontakt", "Native Instruments", [kontakt6])[0]);
        // …unless that version is installed beside it: then it's that one's (Serum 2's beside Serum, Kontakt 7's beside Kontakt).
        Assert.False(Uninstallers.IsOtherVersion("Serum", serum2));
        Assert.True(Uninstallers.IsOtherVersion("Serum", serum2, ["Serum", "Serum2"]));
        Assert.False(Uninstallers.IsOtherVersion("Serum 2", serum2, ["Serum", "Serum2"]));
        Assert.Equal([kontakt6, kontakt7], Uninstallers.For("Kontakt", "Native Instruments", [kontakt7, kontakt6], ["Kontakt", "Kontakt 7"]));
        Assert.False(Uninstallers.IsOtherVersion("Kontakt", kontakt6, ["Kontakt", "Kontakt 7"]));
        Assert.False(Uninstallers.IsOtherVersion("Diva", new("u-he Diva v1.4.5", "u-he", "d.exe")));
        Assert.False(Uninstallers.IsOtherVersion("Ozone 9", new("Ozone", "iZotope", "o.exe")));   // no number: can't tell
    }

    [Fact]
    public async Task The_waveshell_counts_as_used_when_projects_use_waves_plugins_and_is_never_binned()
    {
        static PluginSpaceInfo Shell() => new(LibraryDb.NameKey("WaveShell1-VST3 14.12"), "WaveShell1-VST3 14.12", "Waves",
                                               [new(@"C:\VST3\WaveShell1-VST3 14.12.vst3", SpaceKind.Program, 500, [])], DateTime.Now);
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        Directory.CreateDirectory(Path.Combine(_root, "b"));
        using (var page = new Page(Path.Combine(_root, "a")))
        {
            var info = new ProjectInfo { Daw = "Bitwig Studio" };
            info.Plugins.Add(new PluginRef("CLA-76 Stereo", "Waves", PluginKind.Effect, "VST3"));
            page.Db.Upsert(new LibraryDb.IndexedFile(File("Bitwig/mix.bwproject", 5), 5, 1, info, null, [], []));
            page.Db.ReplaceInstalledPlugins(["WaveShell1-VST3 14.12"]);
            page.Db.SavePluginSpaces([Shell()], all: true);
            var plugins = page.Main.Plugins;
            plugins.Reload();
            Assert.DoesNotContain(plugins.Items, p => p.Name.StartsWith("WaveShell"));   // not a "never used" plugin
            Assert.Empty(plugins.Candidates);
        }
        using (var page = new Page(Path.Combine(_root, "b")))
        {
            page.Db.ReplaceInstalledPlugins(["WaveShell1-VST3 14.12"]);
            page.Db.SavePluginSpaces([Shell()], all: true);
            var plugins = page.Main.Plugins;
            plugins.Reload();
            await plugins.UninstallCommand.ExecuteAsync(plugins.Items.Single(p => p.Name.StartsWith("WaveShell")));
            if (plugins.Card!.HasUninstallers) return;   // a real Waves uninstaller on this computer
            Assert.Empty(plugins.Card.Items);
            Assert.True(plugins.Card.HasEmpty);
            Assert.False(plugins.Card.RunCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task Spare_copies_are_checked_again_against_the_projects_before_they_go()
    {
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "share"));
        try
        {
            using var page = new Page(_root);
            var db = page.Db;
            var vst2 = File("Plugins/Serum.dll", 10);
            File("Plugins/Serum.vst3", 10);
            db.SetList(Scanner.PluginFoldersKey, [Path.Combine(_root, "Plugins")]);
            // Measured a week ago, when projects used only its VST3.
            db.SaveSpareCopies([new SpareCopy("Serum", "VST2", vst2, 10, "Your projects use its VST3 (4 projects); none use this VST2.")]);
            // Since then a song uses its VST2.
            var info = new ProjectInfo { Daw = "FL Studio" };
            info.Plugins.Add(new PluginRef("Serum", "Xfer Records", PluginKind.Instrument, "VST2"));
            db.Upsert(new LibraryDb.IndexedFile(File("FL/new.flp", 5), 5, 1, info, null, [], []));
            var plugins = page.Main.Plugins;
            plugins.Reload();
            Assert.True(plugins.HasSpares);
            plugins.ReviewSparesCommand.Execute(null);
            await plugins.Card!.RunCommand.ExecuteAsync(null);
            Assert.True(System.IO.File.Exists(vst2));
            Assert.False(plugins.HasSpares);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", home);
        }
    }

    // ---- found by the final review (2026-10-26) ----------------------------------------------------------------------

    [Fact]
    public async Task Keep_pressed_while_a_delete_is_checked_again_removes_nothing()
    {
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "share"));
        try
        {
            using var page = new Page(_root);
            var vital = Path.GetDirectoryName(File("C/Vital/pad.vital", 70))!;
            Directory.CreateDirectory(Path.Combine(_root, "I"));
            string DriveOf(string path) => Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar)[0];
            var moved = FolderMove.Move(vital, Path.Combine(_root, "I"), 70, driveOf: DriveOf) with { Key = "vital" };
            page.Db.SaveMovedFolders([moved]);
            // A spare VST2: the one song uses Serum's VST3.
            var vst2 = File("Plugins/Serum.dll", 10);
            File("Plugins/Serum.vst3", 10);
            page.Db.SetList(Scanner.PluginFoldersKey, [Path.Combine(_root, "Plugins")]);
            page.Db.SaveSpareCopies([new SpareCopy("Serum", "VST2", vst2, 10, "Your projects use its VST3 (1 project); none use this VST2.")]);
            var info = new ProjectInfo { Daw = "FL Studio" };
            info.Plugins.Add(new PluginRef("Serum", "Xfer Records", PluginKind.Instrument, "VST3"));
            page.Db.Upsert(new LibraryDb.IndexedFile(File("FL/song.flp", 5), 5, 1, info, null, [], []));
            var plugins = page.Main.Plugins;
            plugins.Reload();
            Assert.True(plugins.HasOldCopies);
            Assert.True(plugins.HasSpares);

            // Delete, then Keep while it looks again (a sleeping USB drive takes seconds to answer), then another card opens.
            using var ui = new MoProbeLiveTests.HeldUpContext();
            foreach (var open in new Action[] { () => plugins.ReviewOldCopiesCommand.Execute(null), () => plugins.DeleteOldCopy(moved),
                                                () => plugins.ReviewSparesCommand.Execute(null) })
            {
                var other = await ui.Run(async () =>
                {
                    open();
                    var card = plugins.Card!;
                    var deleting = card.RunCommand.ExecuteAsync(null);   // looking: the rest waits for this thread
                    card.KeepCommand.Execute(null);
                    Assert.Null(plugins.Card);
                    plugins.MoveBack(moved);
                    var other = plugins.Card;
                    await deleting;
                    return other;
                });
                Assert.Same(other, plugins.Card);   // still open
                plugins.CloseCard();
            }
            Assert.True(Directory.Exists(moved.OldCopy));
            Assert.Equal(70, new FileInfo(Path.Combine(moved.OldCopy, "pad.vital")).Length);
            Assert.True(System.IO.File.Exists(vst2));
            Assert.DoesNotContain(page.Main.Activities.Items, a => a.Title.StartsWith("Freed") || a.Title.StartsWith("Kept"));

            // Once things are going to the Recycle Bin, Keep can't call them back.
            plugins.ReviewOldCopiesCommand.Execute(null);
            var going = plugins.Card!;
            Assert.True(going.KeepCommand.CanExecute(null));
            going.Removing = true;
            Assert.False(going.KeepCommand.CanExecute(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", home);
        }
    }

    [Fact]
    public async Task Stop_while_a_move_is_checked_opens_no_card()
    {
        using var page = new Page(_root);
        var hive = Path.GetDirectoryName(File("C/Hive/pad.h2p", 300))!;
        page.Db.ReplaceInstalledPlugins(["Hive"]);
        page.Db.SavePluginSpaces([Space("Hive", 300, hive)], all: true);
        var plugins = page.Main.Plugins;
        plugins.Reload();
        plugins.Selected = plugins.Items.Single(p => p.Name == "Hive");
        ((ViewModelTests.HeadlessUi)Ui.Current).PickedFolder = Directory.CreateDirectory(Path.Combine(_root, "D")).FullName;
        using var ui = new MoProbeLiveTests.HeldUpContext();
        await ui.Run(async () =>
        {
            var moving = plugins.MoveFolderAsync(new SpaceFolder(hive, SpaceKind.Sounds, 300, []));
            Thread.Sleep(200);   // measured by now: Stop comes while it looks for what would stop the move
            page.Main.Activities.Items.Single(a => a.Title == "Checking Hive").CancelCommand.Execute(null);
            await moving;
            return true;
        });
        Assert.Null(plugins.Card);
        Assert.Contains(page.Main.Activities.Items, a => a.Title == "Stopped checking Hive");
        Assert.DoesNotContain(page.Main.Activities.Items, a => a.Title.StartsWith("Can't move"));
        Assert.True(System.IO.File.Exists(Path.Combine(hive, "pad.h2p")));
    }

    [Fact]
    public async Task Clean_up_never_offers_a_folder_with_files_only_online_in_onedrive()
    {
        // The Recycle Bin can't keep a OneDrive file that's only online: removing it deletes it from OneDrive everywhere.
        using var page = new Page(_root);
        var tank = Path.GetDirectoryName(File("Docs/SampleTank Library/kick.wav", 50))!;
        page.Db.ReplaceInstalledPlugins(["SampleTank"]);
        page.Db.SavePluginSpaces([Space("SampleTank", 50, tank)], all: true);
        var plugins = page.Main.Plugins;
        plugins.Reload();
        var was = PluginsViewModel.HasOnlineOnly;
        PluginsViewModel.HasOnlineOnly = (folder, _) => folder == tank;   // only Windows has OneDrive
        try
        {
            await plugins.CleanUpCommand.ExecuteAsync(plugins.Items.Single(p => p.Name == "SampleTank"));
            var item = plugins.Card!.Items.Single();
            Assert.False(item.CanTick);
            Assert.Equal(PluginsViewModel.OnlineOnlyWhy, item.Why);
            Assert.False(plugins.Card.RunCommand.CanExecute(null));
        }
        finally
        {
            PluginsViewModel.HasOnlineOnly = was;
        }
    }

    [Fact]
    public void Whats_in_a_folder_counts_files_that_take_no_room_here()
    {
        // A OneDrive file that's only online takes no room, so the size walks leave it out. It's still there, though: a
        // sample a project uses holds its folder, and another song's save means a folder isn't this song's own. OneDrive
        // is Windows' only; here a link to a file, which the size walks leave out too, stands in for one.
        var cloud = File("Cloud/kick.wav", 50);
        var kicks = Directory.CreateDirectory(Path.Combine(_root, "Docs", "SampleTank Library", "Kicks")).FullName;
        System.IO.File.CreateSymbolicLink(Path.Combine(kicks, "kick.wav"), cloud);
        var tank = Path.GetDirectoryName(kicks)!;
        if (!OperatingSystem.IsWindows()) Assert.Empty(DiskSpace.Files(tank, (ref System.IO.Enumeration.FileSystemEntry e) => e.ToFullPath()));
        Assert.Equal([Path.Combine(kicks, "kick.wav")], DiskSpace.AllFiles(tank, (ref System.IO.Enumeration.FileSystemEntry e) => e.ToFullPath()));

        using var page = new Page(_root);
        var used = @"C:\Users\Rose\Documents\SampleTank Library\Kicks\kick.wav";   // saved before OneDrive took over Documents
        page.Db.Upsert(new LibraryDb.IndexedFile(File("FL/kicks.flp", 5), 5, 1, new ProjectInfo { Daw = "FL Studio" }, null, [(used, false)], []));
        Assert.Equal(used, page.Db.SamplesNamedIn([tank])[tank]);

        var day = new DateTime(2026, 1, 1);
        var a = File("Songs/Album/Song A.song", 300, day);
        System.IO.File.CreateSymbolicLink(Path.Combine(_root, "Songs", "Album", "Song B.song"), File("Cloud/Song B.song", 200, day));
        Assert.False(SongSpace.OwnsFolder(Path.Combine(_root, "Songs", "Album"), new HashSet<string> { a }, [Path.Combine(_root, "Songs")]));
    }

    [Fact]
    public void The_administrator_steps_act_only_on_the_list_the_app_wrote()
    {
        var keep = File("Old Library/a.wav");
        var list = Path.Combine(_root, "list.txt");
        var result = Path.Combine(_root, "result.txt");
        // Another program swapped the list after the app wrote it: the fingerprint on the command line doesn't match.
        var said = AdminStep.WriteList(list, [Path.Combine(_root, "what the app said.wav")]);
        System.IO.File.WriteAllLines(list, [keep]);
        Assert.True(RecycleBin.RunHelper([RecycleBin.HelperFlag, list, result, said]));
        Assert.True(System.IO.File.Exists(keep));
        Assert.False(System.IO.File.Exists(result));
        var relink = AdminStep.WriteList(list, ["x\ty"]);
        System.IO.File.WriteAllLines(list, [keep]);
        Assert.True(FolderMove.RunHelper([FolderMove.HelperFlag, "relink", list, result, relink]));
        Assert.False(System.IO.File.Exists(result));
        // A result file already there (put there to point somewhere else) is never written through.
        var fingerprint = AdminStep.WriteList(list, [keep]);
        System.IO.File.WriteAllText(result, "theirs");
        Assert.True(RecycleBin.RunHelper([RecycleBin.HelperFlag, list, result, fingerprint]));
        Assert.Equal("theirs", System.IO.File.ReadAllText(result));
        Assert.True(System.IO.File.Exists(keep));

        if (OperatingSystem.IsWindows()) return;   // the real Recycle Bin: not in a test
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "share"));
        try
        {
            System.IO.File.Delete(result);
            Assert.True(RecycleBin.RunHelper([RecycleBin.HelperFlag, list, result, fingerprint]));
            Assert.False(System.IO.File.Exists(keep));
            Assert.Equal([$"{keep}\t"], System.IO.File.ReadAllLines(result));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", home);
        }
    }

    // ---- F12 notes of 27 Oct: moved folders inside moved folders, spare copies by format, the app's own files ----------

    [Fact]
    public void A_folder_inside_a_moved_one_isnt_moved_again_and_its_old_copy_stays_with_the_outer_one()
    {
        var reaktor = Path.GetDirectoryName(File("C/Native Instruments/Reaktor 6/factory.ens", 300))!;
        var kontakt = Path.GetDirectoryName(File("C/Native Instruments/Kontakt/lib.nki", 200))!;
        var ni = Path.GetDirectoryName(reaktor)!;
        var home = Directory.CreateDirectory(Path.Combine(_root, "I", "Plugin Data")).FullName;
        string DriveOf(string path) => Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar)[0];
        var r6 = FolderMove.Move(reaktor, home, 300, driveOf: DriveOf);

        // Native Instruments, the folder around it, moves after it: Reaktor 6's link goes as a link, and its old copy
        // stays in Native Instruments' own old copy rather than going to I: too.
        var outer = FolderMove.Move(ni, home, 500, driveOf: DriveOf, alreadyMoved: [r6]);
        IReadOnlyList<MovedFolder> moved = [r6, outer];
        Assert.True(FolderMove.IsLink(Path.Combine(outer.To, "Reaktor 6")));
        Assert.False(Directory.Exists(Path.Combine(outer.To, "Reaktor 6" + FolderMove.OldSuffix)));
        Assert.True(System.IO.File.Exists(Path.Combine(outer.OldCopy, "Reaktor 6" + FolderMove.OldSuffix, "factory.ens")));
        Assert.False(r6.HasOldCopy);   // it goes when Native Instruments' old copy does
        Assert.Equal(300, new FileInfo(Path.Combine(reaktor, "factory.ens")).Length);   // the plugin still finds it
        Assert.Empty(FolderMove.Replaced(moved));
        Assert.Empty(FolderMove.Doubled(moved));

        // A folder inside a moved one is on the other drive already: moving it would free nothing here.
        Directory.CreateDirectory(Path.Combine(_root, "J"));
        Assert.Equal("It's inside Native Instruments, which is on another drive already, so moving it frees nothing here.",
                     FolderMove.Problem(kontakt, Path.Combine(_root, "J"), 200, driveOf: DriveOf, moved: moved));
    }

    /// <summary>
    /// Before links inside a moved folder stayed links (F12 note, 27 Oct): Reaktor 6 moved, then Native Instruments around
    /// it, and that move copied what Reaktor 6's link pointed at. So I: has it twice, and the plugin uses the second copy,
    /// which looks like a real folder again. Returns Reaktor 6's folder, its move and the one around it.
    /// </summary>
    (string Reaktor, MovedFolder Inner, MovedFolder Outer) MovedTwice()
    {
        var reaktor = Path.GetDirectoryName(File("C/Native Instruments/Reaktor 6/factory.ens", 300))!;
        var ni = Path.GetDirectoryName(reaktor)!;
        var home = Directory.CreateDirectory(Path.Combine(_root, "I", "Plugin Data")).FullName;
        string DriveOf(string path) => Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar)[0];
        var inner = FolderMove.Move(reaktor, home, 300, driveOf: DriveOf) with { Key = "reaktor6" };
        // The old move: everything copied as it read, the links followed, the old copy too.
        var copy = Path.Combine(home, "Native Instruments");
        Copy(Path.Combine(inner.To, "factory.ens"), "I/Plugin Data/Native Instruments/Reaktor 6/factory.ens", DateTime.Now.AddDays(-1));
        Copy(Path.Combine(inner.OldCopy, "factory.ens"), "I/Plugin Data/Native Instruments/Reaktor 6 (old copy)/factory.ens", DateTime.Now.AddDays(-1));
        Directory.Move(ni, ni + FolderMove.OldSuffix);
        Directory.CreateSymbolicLink(ni, copy);
        return (reaktor, inner, new MovedFolder(ni, copy, ni + FolderMove.OldSuffix, 600, DateTime.Now) { Key = "kontakt" });
    }

    [Fact]
    public void A_folder_on_the_other_drive_twice_is_tidied_up_to_one_copy()
    {
        var (reaktor, r6, outer) = MovedTwice();
        IReadOnlyList<MovedFolder> moved = [r6, outer];
        Assert.Empty(FolderMove.Replaced(moved));   // no update did this
        Assert.Equal([(r6, outer.From)], FolderMove.Doubled(moved));

        // Since then the plugin saved a preset: it's in the second copy.
        System.IO.File.WriteAllBytes(Path.Combine(reaktor, "mine.ens"), new byte[42]);
        var aside = FolderMove.LinkAgain(r6, 342, second: true);
        Assert.True(FolderMove.IsLink(reaktor));
        Assert.Empty(FolderMove.Doubled(moved));
        Assert.Equal(42, new FileInfo(Path.Combine(r6.To, "mine.ens")).Length);   // kept in the copy that stays
        Assert.Equal(300, new FileInfo(Path.Combine(reaktor, "factory.ens")).Length);
        Assert.Equal(reaktor + FolderMove.SecondSuffix, aside.Path);
        Assert.Equal("a second copy", aside.Note);
        // It's really on I:, inside Native Instruments' copy: binned there, not copied back to C: to be binned.
        var real = Path.Combine(outer.To, "Reaktor 6" + FolderMove.SecondSuffix);
        Assert.Equal(real, FolderMove.RealPlace(aside.Path, moved));
        Assert.True(Directory.Exists(real));
        Assert.Equal(r6.To, FolderMove.RealPlace(r6.To, moved));   // a path in no moved folder is where it looks
    }

    [Fact]
    public async Task The_plugins_page_tidies_up_a_folder_on_the_other_drive_twice()
    {
        var (reaktor, r6, outer) = MovedTwice();
        using var page = new Page(_root);
        page.Db.SetList(Scanner.PluginFoldersKey, [Directory.CreateDirectory(Path.Combine(_root, "Plugins")).FullName]);
        page.Db.SaveMovedFolders([r6, outer]);
        var plugins = page.Main.Plugins;
        plugins.Reload();
        Assert.False(plugins.HasReplaced);   // it was "a real folder here again" (F12 note, 27 Oct)
        Assert.True(plugins.HasDoubled);
        Assert.Equal("Reaktor 6 is on the other drive twice: it had moved already when Native Instruments, the folder around it, moved too. " +
                     "Nothing's broken. Tidy up… keeps one copy.", plugins.DoubledText);
        plugins.DeleteOldCopy(r6);   // not before it's tidied up
        Assert.Null(plugins.Card);
        Assert.Contains(page.Main.Activities.Items, a => a.Detail.Contains("Tidy up"));

        plugins.TidyUpDoubledCommand.Execute(null);
        Assert.Equal("Tidy up Reaktor 6?", plugins.Card!.Title);
        await plugins.Card.RunCommand.ExecuteAsync(null);
        Assert.Null(plugins.Card);
        Assert.False(plugins.HasDoubled);
        Assert.True(FolderMove.IsLink(reaktor));
        Assert.Contains(page.Main.Activities.Items, a => a.Title == "Tidied it up");
        // The second copy and Reaktor 6's old copy are on the old copies card, by where they really are (I:).
        var second = Path.Combine(outer.To, "Reaktor 6" + FolderMove.SecondSuffix);
        Assert.Equal([second], page.Db.OldHomes().Select(h => h.Path));
        plugins.ReviewOldCopiesCommand.Execute(null);
        Assert.Equal([outer.OldCopy, Path.Combine(outer.To, "Reaktor 6" + FolderMove.OldSuffix), second],
                     plugins.Card!.Items.Select(i => i.Folder.Path).Order());
    }

    [Fact]
    public async Task A_moved_folder_shows_on_every_plugin_that_shared_it_and_their_sizes_are_measured_again()
    {
        var toontrack = Path.GetDirectoryName(Path.GetDirectoryName(File("C/Toontrack/Core/piano.dat", 300)))!;
        Directory.CreateDirectory(Path.Combine(_root, "I"));
        string DriveOf(string path) => Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar)[0];
        string sd3 = LibraryDb.NameKey("Superior Drummer 3"), ezkeys = LibraryDb.NameKey("EZkeys 2");
        // Moved from Superior Drummer 3 before the plugins sharing it were kept with the move.
        var moved = FolderMove.Move(toontrack, Path.Combine(_root, "I"), 300, driveOf: DriveOf) with { Key = sd3 };
        PluginSpaceInfo Shared(string name, string with) =>
            Space(name, 300, toontrack) is var s ? s with { Folders = [s.Folders[0], s.Folders[1] with { SharedWith = [with] }] } : s;
        // Their sizes from before the move: EZkeys 2's still lists Toontrack on C:.
        var spaces = new[] { Shared("EZkeys 2", sd3) }.ToDictionary(s => s.Key);
        var (found, stale) = FolderMove.FindSharers([moved], spaces);
        Assert.Equal([ezkeys], found.Single().SharedWith);
        Assert.Equal([ezkeys], stale);
        Assert.Empty(FolderMove.FindSharers(found, spaces).Stale);   // looked at once
        Assert.True(found.Single().Belongs(ezkeys) && found.Single().Belongs(sd3) && !found.Single().Belongs(""));

        using var page = new Page(_root);
        page.Db.SetList(Scanner.PluginFoldersKey, [Directory.CreateDirectory(Path.Combine(_root, "Plugins")).FullName]);
        page.Db.ReplaceInstalledPlugins(["Superior Drummer 3", "EZkeys 2"]);
        page.Db.SavePluginSpaces([Shared("Superior Drummer 3", ezkeys), Shared("EZkeys 2", sd3)], all: true);
        page.Db.SaveMovedFolders([moved]);
        var plugins = page.Main.Plugins;
        plugins.Reload();
        await plugins.SharersMeasured;
        Assert.Equal([ezkeys], page.Db.MovedFolders().Single().SharedWith);
        plugins.Selected = plugins.Items.Single(p => p.Name == "EZkeys 2");
        Assert.True(plugins.HasMoved);   // its Space tab shows Toontrack moved, not on C:
        Assert.Equal(moved.To, plugins.MovedFolders.Single().Where);
    }

    [Fact]
    public async Task Spare_copies_say_when_they_havent_been_looked_for_and_go_a_format_at_a_time()
    {
        using var page = new Page(_root);
        var db = page.Db;
        db.SetList(Scanner.PluginFoldersKey, [Path.Combine(_root, "Plugins")]);
        var serum2 = File("Plugins/Serum.dll", 10);
        File("Plugins/Serum.vst3", 10);
        Song(db, "song", DateTime.Now.AddDays(-3), "Serum");   // its VST3
        // Plugins measured today, by an app from before spare copies were looked for.
        db.SavePluginSpaces([Space("Serum", 10)], all: true);
        var plugins = page.Main.Plugins;
        plugins.Reload();
        // Nothing said there were any to look for (F12 note, 27 Oct: "its not obvious what you have to do").
        Assert.True(plugins.SparesUnknown);
        Assert.False(plugins.HasSpares);
        var meter = new SpaceMeter(db);
        Assert.True(meter.PluginsStale());   // so the measure after the next scan looks for them too
        await plugins.LookForSparesCommand.ExecuteAsync(null);
        Assert.False(plugins.SparesUnknown);
        Assert.False(meter.PluginsStale());
        Assert.Equal("1 spare plugin copy", plugins.SpareTitle);
        Assert.Equal(("VST2", serum2), (plugins.SpareFormats.Single().Format, db.SpareCopyList().Single().Path));
        Assert.Contains(page.Main.Activities.Items, a => a.Title == "Found 1 spare plugin copy");

        // Several formats: each its own chip, biggest first, and its own tick on the card.
        db.SaveSpareCopies([
            new SpareCopy("Serum", "VST2", serum2, 10, ""), new SpareCopy("Diva", "VST2", File("Plugins/Diva.dll", 20), 20, ""),
            new SpareCopy("Serum", "AAX", File("Plugins/Serum.aaxplugin", 50), 50, ""), new SpareCopy("Vital", "CLAP", File("Plugins/Vital.clap", 5), 5, ""),
        ]);
        await plugins.RefreshSparesAsync();
        Assert.Equal(["AAX · 1 · 50 bytes", "VST2 · 2 · 30 bytes", "CLAP · 1 · 5 bytes"], plugins.SpareFormats.Select(f => f.Text));
        plugins.SpareFormats[1].ReviewCommand.Execute(null);
        var card = plugins.Card!;
        Assert.Equal("Remove the spare VST2 copies?", card.Title);
        Assert.Equal(["Diva · VST2", "Serum · VST2", "Serum · AAX", "Vital · CLAP"], card.Items.Select(i => i.Name));   // the format picked first
        Assert.Equal(["Diva · VST2", "Serum · VST2"], card.Items.Where(i => i.IsTicked).Select(i => i.Name));
        Assert.Equal([("VST2", (bool?)true), ("AAX", false), ("CLAP", false)], card.Groups.Select(g => (g.Name, g.IsTicked)));
        Assert.Equal("Delete 30 bytes", card.ConfirmText);

        card.Groups[1].IsTicked = true;   // every AAX copy
        Assert.Equal("Delete 80 bytes", card.ConfirmText);
        card.Items.Single(i => i.Name == "Diva · VST2").IsTicked = false;
        Assert.Null(card.Groups[0].IsTicked);   // some of the VST2 copies
        Assert.Equal("Delete 60 bytes", card.ConfirmText);
        card.Groups[0].IsTicked = false;
        Assert.Equal((bool?)false, card.Groups[0].IsTicked);
        Assert.Equal("Delete 50 bytes", card.ConfirmText);
        plugins.CloseCard();

        plugins.ReviewSparesCommand.Execute(null);   // Review…: every one
        Assert.All(plugins.Card!.Groups, g => Assert.Equal(true, g.IsTicked));
        Assert.Equal("Delete 85 bytes", plugins.Card.ConfirmText);
    }

    [Fact]
    public async Task The_apps_own_files_are_measured_and_a_nearly_full_drive_is_said_once_until_theres_room_again()
    {
        var data = Path.Combine(_root, "data");
        File("data/library.db", 100);
        File("data/library.db-wal", 20);
        File("data/sounds.db", 50);
        File("data/midi.db", 30);
        File("data/models/clap/model.onnx", 400);
        File("data/logs/run.log", 5);
        File("data/notes/note.txt", 7);
        File("data/Bitwig template/template.bwproject", 9);
        File("data/playground/idea.json", 11);
        Directory.CreateSymbolicLink(Path.Combine(data, "linked"), Path.GetDirectoryName(File("Elsewhere/big.wav", 5000))!);
        var parts = AppFiles.Measure(data, AppFiles.Databases(Path.Combine(data, "library.db"), Path.Combine(data, "sounds.db"), Path.Combine(data, "midi.db")));
        Assert.Equal(["Search models 400", "Your library 120", "Sound Lab 50", "MIDI parts 30", "Playground ideas 11", "Everything else 9",
                      "Notes for Claude 7", "Logs 5"], parts.Select(p => $"{p.Name} {p.Bytes}"));
        Assert.True(AppFiles.IsLow(5L << 30, 1L << 20));
        Assert.False(AppFiles.IsLow(50L << 30, 1L << 20));
        Assert.True(AppFiles.IsLow(15L << 30, 20L << 30));   // less room than the app's files take: a big scan could fill it
        Assert.False(AppFiles.IsLow(-1, 0));                   // a drive that couldn't be asked

        using var page = new Page(_root);
        var files = page.Main.Settings.AppFiles;
        var was = AppFilesViewModel.DriveSpace;
        try
        {
            AppFilesViewModel.DriveSpace = _ => ("C:", 300L << 30, 2000L << 30);
            await files.MeasureAsync();
            Assert.Contains(files.Parts, p => p.Name == "Your library" && p.Colour == "#5B9CF2");   // the page's own library
            Assert.Contains(files.Parts, p => p.Name == "Search models" && p.Bytes == 400);
            Assert.StartsWith("C: has 300 GB free of", files.DriveText);
            Assert.False(files.IsLow);

            AppFilesViewModel.DriveSpace = _ => ("C:", 3L << 30, 2000L << 30);
            await files.CheckAsync();
            await files.CheckAsync();   // checked every few minutes, said once
            Assert.True(files.IsLow);
            Assert.Equal("C:", files.RailText);
            Assert.StartsWith("C: is nearly full: 3 GB left.", files.LowText);
            Assert.Single(page.Main.Activities.Items, a => a.Title == "C: is nearly full");

            // The left bar's sign opens Settings › General; Free up space… opens Plugins.
            files.ShowCommand.Execute(null);
            Assert.Same(page.Main.Settings, page.Main.SelectedNav.Page);
            Assert.Equal("General", page.Main.Settings.Tab);
            files.FreeUpSpaceCommand.Execute(null);
            Assert.Same(page.Main.Plugins, page.Main.SelectedNav.Page);

            // Room again, then almost full: said again.
            AppFilesViewModel.DriveSpace = _ => ("C:", 300L << 30, 2000L << 30);
            await files.CheckAsync();
            Assert.False(files.IsLow);
            AppFilesViewModel.DriveSpace = _ => ("C:", 1L << 30, 2000L << 30);
            await files.CheckAsync();
            Assert.StartsWith("C: is almost full", files.LowText);
            Assert.Equal(2, page.Main.Activities.Items.Count(a => a.Title == "C: is nearly full"));
        }
        finally
        {
            AppFilesViewModel.DriveSpace = was;
        }
    }
}
