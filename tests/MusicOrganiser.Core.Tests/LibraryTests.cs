using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;
using Xunit;

namespace MusicOrganiser.Core.Tests;

public class LibraryTests : IDisposable
{
    readonly string _root = Fixtures.TempDir();
    readonly LibraryDb _db;

    public LibraryTests()
    {
        Fixtures.FlStudioProject(_root);
        Fixtures.StudioOneSong(_root);
        Fixtures.BitwigProject(_root);
        // Autosaves must be skipped.
        Directory.CreateDirectory(Path.Combine(_root, "Loop Idea", "auto-backups"));
        Fixtures.FlStudioProject(Path.Combine(_root, "Loop Idea", "auto-backups"), "old copy");
        Fixtures.FlStudioProject(Path.Combine(_root, "Loop Idea"), "Night Drive (autosave)");
        Fixtures.FlStudioProject(Path.Combine(_root, "Loop Idea"), "Night Drive (overwritten at 2213h05)");
        _db = new LibraryDb(Path.Combine(_root, "library.db"));
        _db.AddRoot(_root);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void A_scan_reads_each_song_once_into_borrowed_memory_and_lists_the_same_as_reading_it_on_its_own()
    {
        // Solomon's log (2026-10-22): re-reading every old FL song took the app to 14.4 GB, because each song was read
        // twice into new memory and every plugin's sound copied out just to list its name. A scan now reads each song
        // once into memory it hands back for the next song: nothing it lists may still point into that memory.
        var folder = Path.Combine(_root, "Borrowed");
        Directory.CreateDirectory(folder);
        string[] songs =
        [
            Fixtures.FlStudioProject(folder, "Lead"), Fixtures.FlStudio25NothingSaved(folder),
            Fixtures.FlStudio25Unplayed(folder, "Beat"), Fixtures.FlStudio9Project(folder, [.. Enumerable.Range(0, 5000).Select(i => (byte)i)]),
        ];
        static string Listed(LibraryDb.IndexedFile f) => string.Join("|",
            f.Info!.Title, f.Info.Version, string.Join(",", f.Info.Plugins.Select(p => $"{p.Name}/{p.Vendor}/{p.Kind}/{p.Format}")),
            string.Join(",", f.Info.Samples), string.Join(",", f.Info.Tracks), string.Join(",", f.Info.Warnings),
            string.Join(",", f.Info.Unplayed.Select(u => $"{u.Key}={u.Value}")), f.DetectedKey, f.NoteCount, f.LengthBars);
        var alone = songs.Select(path =>
        {
            var info = Readers.For(path)!.Read(path);
            var notes = ProjectKey.Read(path);
            return Listed(new LibraryDb.IndexedFile(path, 0, 0, info, null, [], [])
                { DetectedKey = notes?.Key, NoteCount = notes?.Notes ?? -1, LengthBars = notes?.Bars ?? -1 });
        }).ToList();

        // Read one after another on this thread, so each song's memory is the one the song before handed back.
        var scanned = songs.Select(path => Scanner.Analyse(path)).ToList();

        Assert.All(scanned, f => Assert.Null(f.Error));
        Assert.Equal(alone, scanned.Select(Listed));
        Assert.Contains(scanned[1].Info!.Warnings, w => w.StartsWith("LegacyCell has no saved sound", StringComparison.Ordinal));
        Assert.Contains(scanned[0].Info!.Plugins, p => p is { Name: "Serum", Vendor: "Xfer Records" });
    }

    [Fact]
    public void Used_in_can_leave_out_samples_without_notes_and_songs_whose_newest_save_mutes_them()
    {
        // Two saves of one song: the hat plays in the first and is muted in the newest. The kick never has notes.
        var folder = Path.Combine(_root, "Beat");
        var first = Fixtures.FlStudio25Unplayed(folder, "Beat_1", hatPlays: true);
        var newest = Fixtures.FlStudio25Unplayed(folder, "Beat_2");
        File.SetLastWriteTime(first, DateTime.Now.AddHours(-2));
        foreach (var f in new[] { first, newest }) _db.Upsert(Scanner.Analyse(f));
        int Used(string name, UsageRules rules) =>
            _db.SampleUsageByAge(DateTime.Now.AddYears(-1), rules).Where(u => u.Path.EndsWith(name, StringComparison.Ordinal)).Sum(u => u.Projects);

        Assert.Equal((2, 2, 2), (Used("Snare 01.wav", default), Used("Kick 01.wav", default), Used("Hat 01.wav", default)));
        Assert.Equal((2, 0, 2), (Used("Snare 01.wav", new(NeedsNotes: true)), Used("Kick 01.wav", new(NeedsNotes: true)), Used("Hat 01.wav", new(NeedsNotes: true))));
        Assert.Equal((2, 2, 0), (Used("Snare 01.wav", new(NotMutedInNewest: true)), Used("Kick 01.wav", new(NotMutedInNewest: true)),
                                 Used("Hat 01.wav", new(NotMutedInNewest: true))));
        // The years still come through when the newest save is looked at.
        Assert.Equal([DateTime.Now.Year], _db.SampleUsageByAge(DateTime.Now.AddYears(-1), new(NotMutedInNewest: true))
                                            .Single(u => u.Path.EndsWith("Snare 01.wav", StringComparison.Ordinal)).Years.Distinct());
    }

    [Fact]
    public void ScansEveryProjectOnceAndSkipsAutosaves()
    {
        new Scanner(_db).ScanProjects();
        var projects = _db.Projects();
        Assert.Equal(["Bitwig Studio", "FL Studio", "Studio One"], projects.Select(p => p.Daw).Order());
        Assert.All(projects, p => Assert.Null(p.Error));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_project_folder_taken_out_while_a_scan_reads_it_stays_off_the_list(bool full)
    {
        // The scan makes its to-do list once, at the start: a folder taken out (and its projects with it) while it is still
        // reading must not have them put back by the scan's later batches, on a first read or on "Re-read everything".
        var other = Fixtures.TempDir();
        Fixtures.FlStudioProject(other, "Keeper");
        _db.AddRoot(other);
        var scanner = new Scanner(_db);
        if (full) scanner.ScanProjects();

        scanner.ScanProjects(new Each<ScanProgress>(p =>
        {
            if (p.Stage != "Finding projects" || !_db.Roots().Contains(_root)) return;
            _db.RemoveRoot(_root);
            _db.RemoveProjects(_db.ProjectsOnlyIn(_root, _db.Roots()));
        }), full);

        Assert.Equal(["Keeper"], _db.Projects().Select(p => p.Name));
    }

    [Fact]
    public void The_file_list_has_the_same_projects_and_details_as_the_full_list()
    {
        new Scanner(_db).ScanProjects();
        var full = _db.Projects();
        var files = _db.ProjectFiles();
        Assert.Equal(full.Select(p => (p.Id, p.Path, p.Name, p.Daw, p.Folder, p.Size, p.Mtime, p.Modified, p.Tempo, p.FileMissing, p.Error)),
                     files.Select(p => (p.Id, p.Path, p.Name, p.Daw, p.Folder, p.Size, p.Mtime, p.Modified, p.Tempo, p.FileMissing, p.Error)));
    }

    [Fact]
    public void ScansManyProjectsAcrossCoresInOneGo()
    {
        // Enough projects to fill several save batches, spread over nested folders.
        var expected = new List<string>();
        for (var i = 0; i < 120; i++)
        {
            var dir = Path.Combine(_root, "Bulk", $"Folder {i % 7}", $"Deeper {i % 3}");
            Directory.CreateDirectory(dir);
            Fixtures.FlStudioProject(dir, $"Idea {i:000}");
            expected.Add(Path.Combine(dir, $"Idea {i:000}.flp"));
        }

        var found = Scanner.FindProjects([_root]);
        Assert.Equal(found.Order(StringComparer.OrdinalIgnoreCase), found);
        Assert.Subset(found.ToHashSet(), expected.ToHashSet());
        Assert.DoesNotContain(found, f => f.Contains("auto-backups") || f.Contains("(autosave)"));
        // The size and modified time come with the folder listing, the same as asking for each file.
        Assert.All(Scanner.FindProjectStamps([_root]), f =>
        {
            var fi = new FileInfo(f.Path);
            Assert.Equal((fi.Length, new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds()), (f.Size, f.Mtime));
        });

        new Scanner(_db).ScanProjects();
        var paths = _db.Projects().Select(p => p.Path).ToHashSet();
        Assert.Subset(paths, expected.ToHashSet());
        Assert.Equal(found.Count, paths.Count);
    }

    [Fact]
    public void DropsFilesThatNoLongerCountAsProjects()
    {
        // A library scanned before FL autosave copies were skipped still lists one.
        var autosave = Path.Combine(_root, "Loop Idea", "Night Drive (autosave).flp");
        _db.Upsert(Scanner.Analyse(autosave));

        new Scanner(_db).ScanProjects();
        Assert.DoesNotContain(_db.Projects(), p => p.Path == autosave);
        Assert.All(_db.Projects(), p => Assert.False(p.FileMissing));
    }

    [Fact]
    public void KeepsYourOrganisationAcrossRescans()
    {
        var scanner = new Scanner(_db);
        scanner.ScanProjects();
        var p = _db.Projects().First(x => x.Daw == "FL Studio");
        p.Status = "Mixing";
        p.Rating = 4;
        p.Notes = "needs a better drop";
        _db.SaveOrganisation(p);
        _db.SetTags(p.Id, ["Techno", "dark ", "techno"]);

        scanner.ScanProjects(full: true);
        var again = _db.Projects().First(x => x.Id == p.Id);
        Assert.Equal(("Mixing", 4, "needs a better drop"), (again.Status, again.Rating, again.Notes));
        Assert.Equal(["dark", "techno"], again.Tags);
    }

    [Fact]
    public void SkipsDemoTemplateAndTutorialFolders()
    {
        foreach (var dir in new[] { "Cool stuff", "Templates", "Tutorial", "Demo songs", Path.Combine("Data", "Patches", "Packs") })
        {
            Directory.CreateDirectory(Path.Combine(_root, "FL 12 Projects", dir));
            Fixtures.FlStudioProject(Path.Combine(_root, "FL 12 Projects", dir), "factory");
        }
        // A library scanned before these were skipped still lists one; the rescan drops it.
        var demo = Path.Combine(_root, "FL 12 Projects", "Cool stuff", "factory.flp");
        _db.Upsert(Scanner.Analyse(demo));

        new Scanner(_db).ScanProjects();
        Assert.DoesNotContain(_db.Projects(), p => p.Name == "factory");
        Assert.Equal(3, _db.Projects().Count);
    }

    [Theory]
    [InlineData("Fruity Balance", "", @"C:\Program Files\FL Studio 10\Plugins\VST\Fruity Balance.dll", true)]
    [InlineData("Pitcher", "", "", true)]
    [InlineData("IL Harmor", "", "", true)]
    [InlineData("Serum", "Xfer Records", @"C:\VST\Serum.dll", false)]
    [InlineData("Some Synth", "", "/Library/Audio/Plug-Ins/VST3/Image-Line/Some Synth.vst3", true)]
    public void KnowsWhatComesWithFlStudio(string name, string vendor, string path, bool bundled) =>
        Assert.Equal(bundled, PluginCatalog.IsFlStudioBundled(name, vendor, path));

    [Fact]
    public void ScansMacStylePluginBundlesAsOnePluginEach()
    {
        var folder = Path.Combine(_root, "Plug-Ins");
        foreach (var bundle in new[] { "Serum.component", "Vital.clap", "Diva.vst3", "Old Synth.vst" })
        {
            var inner = Path.Combine(folder, bundle, "Contents", "MacOS");
            Directory.CreateDirectory(inner);
            File.WriteAllText(Path.Combine(inner, "helper.dll"), "");
        }
        var names = PluginCatalog.Scan([folder]).Except(PluginCatalog.AlwaysKnown).Order().ToList();
        Assert.Equal(["Diva", "Old Synth", "Serum", "Vital"], names);
    }

    [Fact]
    public void KeepsTwoLetterPluginNames()
    {
        // KORG's M1, as it installs on Windows: a VST3 bundle and a VST2 file.
        var korg = Path.Combine(_root, "Plugins", "KORG");
        Directory.CreateDirectory(Path.Combine(korg, "M1.vst3", "Contents", "x86_64-win"));
        File.WriteAllText(Path.Combine(korg, "M1.dll"), "");
        File.WriteAllText(Path.Combine(korg, "Polysix.dll"), "");

        var files = PluginCatalog.ScanFiles([Path.Combine(_root, "Plugins")]);
        Assert.Equal(["M1 VST2", "M1 VST3", "Polysix VST2"], files.Select(f => $"{f.Name} {f.Format}").Order());
        // So a project naming it, with no path or an old one, finds it installed.
        _db.ReplaceInstalledPlugins(PluginCatalog.Scan([Path.Combine(_root, "Plugins")]));
        Assert.Contains(LibraryDb.NameKey("M1"), _db.InstalledKeys());
    }

    [Theory]
    [InlineData("LFOTool", "LFOTool_x64")]
    [InlineData("Omnisphere (Automap)", "Omnisphere")]
    [InlineData("SaurusX64", "Saurus")]
    [InlineData("chipsounds VST_x64", "chipsounds")]
    public void MatchesPluginNameVariants(string inProject, string installed) =>
        Assert.Equal(LibraryDb.NameKey(installed), LibraryDb.NameKey(inProject));

    [Theory]
    [InlineData("Ozone 8", "Ozone 12")]
    [InlineData("Battery 3", "Battery 4")]
    [InlineData("Mini V", "Mini V4")]
    [InlineData("Toraverb", "Toraverb2")]
    [InlineData("M1", null)]
    [InlineData("Kore2", null)]
    public void NamesTheVersionYouHave(string missing, string? expected) =>
        Assert.Equal(expected, PluginCatalog.OtherVersionInstalled(missing,
            ["Ozone 11", "Ozone 12", "Ozone 12 Imager", "Battery 4", "Mini V3", "Mini V4", "Toraverb2", "M12-Filter"]));

    [Theory]
    [InlineData("Serum", "0.0")]
    [InlineData("Serum 2", "2.0")]
    [InlineData("Kontakt 8", "8.0")]
    [InlineData("Mini V4", "4.0")]
    [InlineData("Ozone 11.1 (x64)", "11.1")]
    public void ReadsAPluginsVersionFromItsName(string name, string version) =>
        Assert.Equal(Version.Parse(version), PluginCatalog.VersionOf(name));

    [Theory]
    [InlineData("Didums_70", "Didums", 70)]
    [InlineData("Didums", "Didums", 1)]
    [InlineData("Beauty bitch (orch)_9", "Beauty bitch (orch)", 9)]
    [InlineData("a_2_3", "a_2", 3)]
    [InlineData("130", "130", 1)]
    [InlineData("_5", "_5", 1)]
    [InlineData("civil war_5 (fixed)", "civil war", 5)]   // Fix's copy sits with the save it was made from
    [InlineData("Didums (Fixed 2)", "Didums", 1)]
    [InlineData("Didums_70 (fixed) (fixed)", "Didums", 70)]   // a fixed copy fixed again by an older build
    [InlineData("Proj_5[1]", "Proj_5[1]", 1)]
    // Solomon's rule, every DAW: the same name with a number on the end, glued on or after a space or a dash.
    [InlineData("Track 2", "Track", 2)]
    [InlineData("track2", "track", 2)]
    [InlineData("track nd 1", "track nd", 1)]
    [InlineData("song-3", "song", 3)]
    [InlineData("song - 3", "song", 3)]
    [InlineData("analog full set nb 36", "analog full set nb", 36)]
    [InlineData("MO Live Test 1", "MO Live Test", 1)]
    [InlineData("Swu 10 year oct 2026", "Swu 10 year oct 2026", 1)]   // a year is part of the name
    [InlineData("Untitled 3", "Untitled 3", 1)]   // a DAW's name for a new project: songs of their own
    [InlineData("Untitled_2", "Untitled", 2)]     // but FL Studio's "Save new version" of it is a save
    // The usual marks count too.
    [InlineData("fleck athens FLP19", "fleck athens FLP", 19)]   // FLP is part of the name (Solomon, 19 Oct)
    [InlineData("fleck athens FLP", "fleck athens FLP", 1)]
    [InlineData("fleck athens flp_15", "fleck athens flp", 15)]
    [InlineData("Night Drive v2", "Night Drive", 2)]
    [InlineData("Night Drive_V3", "Night Drive", 3)]
    [InlineData("Night Drive v2.1", "Night Drive", 2)]
    [InlineData("Night DriveV4", "Night Drive", 4)]
    [InlineData("Night Drive take 4", "Night Drive", 4)]
    [InlineData("Night Drive - mix 2", "Night Drive", 2)]
    [InlineData("Night Drive rev7", "Night Drive", 7)]
    [InlineData("Night Drive final", "Night Drive", 1_000_000)]   // the last save, ahead of any number
    [InlineData("Night Drive FINAL FINAL", "Night Drive", 1_000_000)]
    [InlineData("Night Drive final2", "Night Drive", 1_000_002)]
    [InlineData("Night Drive v2 final v3", "Night Drive", 1_000_003)]
    [InlineData("Night Drive - Copy", "Night Drive", 1)]
    [InlineData("Night Drive_12 - Copy (2)", "Night Drive", 12)]
    [InlineData("Night Drive (2)", "Night Drive", 1)]
    [InlineData("Night Drive wip", "Night Drive", 1)]
    [InlineData("Night Drive 2024-03-01", "Night Drive", 1)]
    [InlineData("Night Drive_20240301", "Night Drive", 1)]
    [InlineData("Track final", "Track", 1_000_000)]
    [InlineData("Untitled final", "Untitled final", 1)]
    [InlineData("Swu oct 2026", "Swu oct 2026", 1)]
    public void SplitsNumberedSaves(string name, string song, int number) =>
        Assert.Equal((song, number), SongVersions.Split(name));

    [Fact]
    public void GroupsNumberedSavesUnderTheNewest()
    {
        ProjectSummary P(string name, string folder = @"C:\dub", int day = 1) =>
            new() { Name = name, Path = $@"{folder}\{name}.flp", Daw = "FL Studio", Folder = folder, Modified = new DateTime(2020, 1, day) };
        var groups = SongVersions.Group([P("Didums", day: 9), P("Didums_10", day: 2), P("Didums_2", day: 3),
                                         P("Night Drive"), P("Didums_3", @"C:\other")]);
        Assert.Equal(3, groups.Count);
        var didums = groups.Single(g => g.Song == "Didums" && g.Latest.Folder == @"C:\dub");
        Assert.Equal("Didums_10", didums.Latest.Name);
        Assert.Equal(["Didums_2", "Didums"], didums.Older.Select(o => o.Name));
    }

    [Fact]
    public void Bitwig_saves_each_in_a_folder_of_its_own_name_are_one_song()
    {
        ProjectSummary P(string name, string folder) =>
            new() { Name = name, Path = $@"{folder}\{name}.bwproject", Daw = "Bitwig Studio", Folder = folder, Modified = new DateTime(2020, 1, 1) };
        const string projects = @"K:\Bitwig Studio\Projects";
        var groups = SongVersions.Group([P("bored_57", $@"{projects}\bored_57"), P("bored_58", $@"{projects}\bored_58"),
                                         P("bored", $@"{projects}\bored"), P("bored_59", $@"{projects}\bored"),
                                         P("MO Live Test 1", $@"{projects}\MO Live Test 1"), P("bored_2", $@"{projects}\Old\bored stuff")]);
        Assert.Equal(3, groups.Count);
        var bored = groups.Single(g => g.Latest.Name == "bored_59");
        Assert.Equal(["bored_58", "bored_57", "bored"], bored.Older.Select(o => o.Name));
        // A folder named otherwise is a song of its own.
        Assert.Contains(groups, g => g.Latest.Name == "bored_2");
    }

    [Fact]
    public void Saves_numbered_FLP1_to_FLP20_join_the_unnumbered_FLP_save_as_one_song()
    {
        // Solomon's Bitwig "fleck athens FLP" and "fleck athens FLP1" … "FLP20" in one folder (19 Oct): one song, called
        // "fleck athens FLP". A save in a folder of its own name counts as in the folder around it.
        const string folder = @"K:\Bitwig Studio\Projects\fleck athens";
        ProjectSummary P(string name, int day, string? own = null) =>
            new() { Name = name, Path = $@"{own ?? folder}\{name}.bwproject", Daw = "Bitwig Studio", Folder = own ?? folder,
                    Modified = new DateTime(2025, 3, 1).AddDays(day) };
        var groups = SongVersions.Group([.. Enumerable.Range(1, 19).Select(n => P($"fleck athens FLP{n}", n)), P("fleck athens FLP", 0),
                                         P("fleck athens FLP20", 20, $@"{folder}\fleck athens FLP20")]);
        var song = Assert.Single(groups);
        Assert.Equal("fleck athens FLP", song.Song);
        Assert.Equal("fleck athens FLP20", song.Latest.Name);
        Assert.Equal(20, song.Older.Count);
        Assert.Equal("fleck athens FLP", song.Older[^1].Name);
    }

    [Fact]
    public void Saves_named_alike_apart_from_their_number_are_one_song_only_in_the_same_folder()
    {
        // Solomon, 19 Oct 2026: "track1" and "track2" go together, "track nd 1" and "track 2" don't, and in another folder
        // they're another song.
        ProjectSummary P(string name, string folder, int day) =>
            new() { Name = name, Path = $@"{folder}\{name}.flp", Daw = "FL Studio", Folder = folder, Modified = new DateTime(2025, 3, day) };
        var groups = SongVersions.Group([P("track1", @"C:\beats", 1), P("track2", @"C:\beats", 2), P("track nd 1", @"C:\beats", 3),
                                         P("track3", @"C:\other", 4)]);
        Assert.Equal(3, groups.Count);
        var track = groups.Single(g => g.Latest.Folder == @"C:\beats" && g.Song == "track");
        Assert.Equal("track2", track.Latest.Name);
        Assert.Equal(["track1"], track.Older.Select(o => o.Name));
    }

    static ProjectSummary Song(string name, string folder, int day) =>
        new() { Name = name, Path = $@"{folder}\{name}.flp", Daw = "FL Studio", Folder = folder, Modified = new DateTime(2025, 3, day) };

    [Fact]
    public void Songs_whose_names_are_close_are_asked_about_but_not_ones_you_said_are_different()
    {
        // Solomon, 19 Oct 2026: "if there is a doubt the app should let you choose".
        var songs = SongVersions.Group([Song("track nd 1", @"C:\beats", 1), Song("track 2", @"C:\beats", 2), Song("Sunrise", @"C:\beats", 3),
                                        Song("Sunrize", @"C:\beats", 4), Song("Sunrise", @"C:\other", 5), Song("bass", @"C:\beats", 6),
                                        Song("bossa", @"C:\beats", 7), Song("Untitled", @"C:\beats", 8), Song("Untitled 2", @"C:\beats", 9)]);
        var doubts = SongVersions.Doubts(songs);
        Assert.Equal(2, doubts.Count);
        Assert.Equal(("Sunrize", "Sunrise"), (doubts[0].A.Song, doubts[0].B.Song));   // newest first
        Assert.Equal("The names are one letter apart", doubts[0].Why);
        Assert.Equal(("track", "track nd"), (doubts[1].A.Song, doubts[1].B.Song));
        Assert.Equal("The names differ only by \"nd\"", doubts[1].Why);

        // Across folders only when asked for.
        Assert.Contains(SongVersions.Doubts(songs, acrossFolders: true), d => d.Why == "Same name in different folders");

        _db.KeepApart(doubts[1].A.Key, doubts[1].B.Key);
        Assert.Equal(["Sunrize"], SongVersions.Doubts(songs, _db.SongChoices()).Select(d => d.A.Song));
    }

    [Fact]
    public void An_old_final_save_doesnt_open_over_newer_saves_and_the_app_asks_about_it()
    {
        // Code review U1 (19 Oct): "Night Drive final" from 2023 next to Night Drive_2 … _14 from 2025.
        ProjectSummary P(string name, DateTime saved) =>
            new() { Name = name, Path = $@"C:\beats\{name}.flp", Daw = "FL Studio", Folder = @"C:\beats", Modified = saved };
        var saves = new List<ProjectSummary> { P("Night Drive final", new(2023, 5, 1)), P("Night Drive_70 - Copy", new(2025, 6, 1)) };
        saves.AddRange(Enumerable.Range(2, 13).Select(n => P($"Night Drive_{n}", new DateTime(2025, 1, 1).AddDays(n))));
        saves.Add(P("Night Drive_70", new(2025, 6, 1)));
        var song = Assert.Single(SongVersions.Group(saves));
        Assert.Equal("Night Drive_70", song.Latest.Name);   // the newest by date; its copy, saved with it, comes after

        var asked = SongVersions.Doubts([song]);
        Assert.Equal(["Night Drive final", "Night Drive_70 - Copy"], asked.Select(d => d.B.Song).Order());
        Assert.All(asked, d => Assert.True(d.OneSave));
        Assert.Contains(asked, d => d.Why == "It was put with Night Drive because its name adds \"final\"");

        // Answered: final is a save of the song (not asked again); the copy is its own song.
        _db.KeepApart(song.Key, asked.Single(d => d.B.Song == "Night Drive final").B.Key);
        _db.SplitSave(saves[1].Path);
        var choices = _db.SongChoices();
        Assert.Empty(SongVersions.Doubts(SongVersions.Group(saves, choices), choices));
        Assert.Equal(2, SongVersions.Group(saves, choices).Count);

        // Saves that all carry the same mark have nothing to ask.
        Assert.Empty(SongVersions.Doubts(SongVersions.Group([P("Dub v2", new(2025, 1, 1)), P("Dub v3", new(2025, 2, 1))])));
    }

    [Fact]
    public void Saves_at_a_drives_root_group_with_ones_in_a_folder_of_their_own_name()
    {
        ProjectSummary P(string name, string folder) =>
            new() { Name = name, Path = $@"{folder.TrimEnd('\\')}\{name}.bwproject", Daw = "Bitwig Studio", Folder = folder, Modified = new DateTime(2025, 1, 1) };
        Assert.Single(SongVersions.Group([P("beat", @"C:\beat"), P("beat_2", @"C:\")]));
    }

    [Fact]
    public void A_name_ending_in_a_non_English_digit_is_a_name_not_a_crash()
    {
        Assert.Equal(("beat_٢", 1), SongVersions.Split("beat_٢"));
        Assert.Equal(("曲１", 1), SongVersions.Split("曲１"));
    }

    [Fact]
    public void A_new_Bitwig_save_in_a_folder_of_its_own_keeps_the_songs_rating_notes_and_tags()
    {
        // Code review U4 (19 Oct): Projects\bored_57\bored_57.bwproject, then Bitwig saves Projects\bored_58\bored_58.bwproject.
        string Save(string name)
        {
            var folder = Path.Combine(_root, "Projects", name);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, name + ".bwproject");
            File.WriteAllBytes(file, [0]);
            _db.Upsert(new LibraryDb.IndexedFile(file, 1, 100, new ProjectInfo { Daw = "Bitwig Studio" }, null, [], []));
            return file;
        }
        Save("bored_57");
        var first = _db.Projects().Single();
        first.Rating = 4;
        first.Notes = "needs a drop";
        _db.SaveOrganisation(first);
        _db.SetTags(first.Id, ["techno"]);
        Save("other_3");   // another song beside it isn't a save of it

        Save("bored_58");
        var newer = _db.Projects().Single(p => p.Name == "bored_58");
        Assert.Equal((4, "needs a drop"), (newer.Rating, newer.Notes));
        Assert.Contains("techno", newer.Tags);
        Assert.Equal((0, ""), (_db.Projects().Single(p => p.Name == "other_3").Rating, _db.Projects().Single(p => p.Name == "other_3").Notes));
    }

    [Fact]
    public void You_can_join_songs_split_a_save_off_and_move_a_save_and_Reset_undoes_it()
    {
        var saves = new[] { Song("track nd 1", @"C:\beats", 1), Song("track 2", @"C:\beats", 2), Song("track 3", @"C:\beats", 3),
                            Song("bossa", @"C:\beats", 4) };
        IReadOnlyList<SongGroup> Groups() => SongVersions.Group(saves, _db.SongChoices());
        var track = Groups().Single(g => g.Song == "track");
        var nd = Groups().Single(g => g.Song == "track nd");

        // "track nd" goes under "track": one song, called track, newest track 3.
        Assert.True(_db.JoinSong(nd.Key, track.Key));
        var joined = Assert.Single(Groups(), g => g.Song == "track");
        Assert.Equal(["track 2", "track nd 1"], joined.Older.Select(o => o.Name));
        Assert.Equal(2, Groups().Count);
        Assert.False(_db.JoinSong(track.Key, nd.Key));   // a loop
        Assert.True(_db.SongChoices().Changed(joined.Key, joined.Older.Append(joined.Latest).Select(p => p.Path)));

        // track 2 split off into a song of its own, then moved under bossa.
        _db.SplitSave(saves[1].Path);
        Assert.Equal(3, Groups().Count);
        Assert.True(_db.JoinSong(SongChoices.SplitKey(saves[1].Path), Groups().Single(g => g.Song == "bossa").Key));
        var bossa = Groups().Single(g => g.Song == "bossa");
        Assert.Equal(("bossa", "track 2"), (bossa.Latest.Name, bossa.Older.Single().Name));   // saved last, whatever the numbers

        // Reset on track puts its join back; Reset on bossa takes track 2 back to track.
        _db.ForgetSongChoices(joined.Key, [saves[0].Path, saves[2].Path]);
        _db.ForgetSongChoices(bossa.Key, [saves[1].Path, saves[3].Path]);
        Assert.Equal(3, Groups().Count);
        Assert.Equal(["track 2"], Groups().Single(g => g.Song == "track").Older.Select(o => o.Name));
        Assert.True(_db.SongChoices().IsEmpty);
    }

    [Fact]
    public void Bitwig_saves_numbered_with_a_space_are_one_song()
    {
        // Solomon's "analog full set nb 26" … "nb 36", each in its own Bitwig project folder (F12 note, 19 Oct 2026).
        ProjectSummary P(string name) => new()
        {
            Name = name, Path = $@"K:\Bitwig Studio\Projects\{name}\{name}.bwproject", Daw = "Bitwig Studio",
            Folder = $@"K:\Bitwig Studio\Projects\{name}", Modified = new DateTime(2026, 10, 2),
        };
        var groups = SongVersions.Group([.. Enumerable.Range(26, 11).Select(n => P($"analog full set nb {n}")), P("Swu 10 year oct 2026"),
                                         P("Untitled 1"), P("Untitled 2")]);
        Assert.Equal(4, groups.Count);
        var song = groups.Single(g => g.Song == "analog full set nb");
        Assert.Equal("analog full set nb 36", song.Latest.Name);
        Assert.Equal(10, song.Older.Count);
    }

    static ProjectSummary Save(string name, DateTime saved, int notes = 100, int bars = 32, int tracks = 6, string[]? plugins = null,
                               DateTime? created = null) =>
        new()
        {
            Name = name, Path = $@"C:\dub\{name}.flp", Daw = "FL Studio", Folder = @"C:\dub", Modified = saved, Created = created,
            NoteCount = notes, LengthBars = bars, TrackCount = tracks, PluginNames = plugins ?? ["Serum", "Fruity Reverb 2"],
        };

    [Fact]
    public void SavesThreeMonthsApartStartANewRun()
    {
        var runs = SongVersions.Runs([
            Save("Didums_77", new(2024, 8, 3), notes: 400, bars: 96),
            Save("Didums", new(2019, 1, 10), notes: 20),
            Save("Didums_12", new(2019, 2, 2)),
            Save("Didums_13", new(2019, 4, 30)),          // under 3 months after the last save: same run
            Save("Didums_40", new(2024, 7, 20), notes: 380, bars: 90),
        ]);
        Assert.Equal([("2019 original", "Didums_13", 3), ("2024 rework", "Didums_77", 2)],
                     runs.Select(r => (r.Label, r.Last.Name, r.Saves)));
        Assert.Equal("Didums_77 · 3 Aug 2024 · 2 saves", runs[1].Details);
    }

    [Fact]
    public void A_shorter_gap_from_Settings_splits_saves_into_more_versions()
    {
        ProjectSummary[] saves = [Save("Didums", new(2019, 1, 10)), Save("Didums_12", new(2019, 2, 20)), Save("Didums_13", new(2019, 4, 30))];
        Assert.Single(SongVersions.Runs(saves, gapMonths: 3));
        Assert.Equal(2, SongVersions.Runs(saves, gapMonths: 2).Count);   // Feb 20 to Apr 30 is over two months
        Assert.Equal(3, SongVersions.Runs(saves, gapMonths: 1).Count);
    }

    [Fact]
    public void ResavingInANewerDawIsNotARework()
    {
        var original = Save("Didums_12", new(2019, 2, 2), plugins: ["Serum", "Fruity Reverb 2"]);
        // FL 26 opened and saved it again: newer versions of the same plugins, a note or two different.
        var resaved = Save("Didums_13", new(2024, 8, 3), notes: 104, bars: 34, plugins: ["Serum 2", "Fruity Reverb 2 (x64)"]);
        Assert.False(SongVersions.IsReworked(original, resaved));
        Assert.Equal("2024 re-save", SongVersions.Runs([original, resaved])[1].Label);

        Assert.True(SongVersions.IsReworked(original, Save("x", default, plugins: ["Serum", "Fruity Reverb 2", "Vital"])));   // a plugin added
        Assert.True(SongVersions.IsReworked(original, Save("x", default, tracks: 7)));      // a channel added
        Assert.True(SongVersions.IsReworked(original, Save("x", default, notes: 121)));     // over a fifth more notes
        Assert.False(SongVersions.IsReworked(original, Save("x", default, notes: 120)));
        Assert.True(SongVersions.IsReworked(original, Save("x", default, bars: 41)));       // over 8 bars longer
        Assert.False(SongVersions.IsReworked(original, Save("x", default, notes: -1, bars: -1)));   // unknown counts are left out
    }

    [Theory]
    [InlineData("Serum 2", "serum")]
    [InlineData("Serum (x64)", "serum")]
    [InlineData("Pro-Q 3", "pro-q")]
    [InlineData("Kontakt 7.10.1", "kontakt")]
    [InlineData("Sylenth1_x64", "sylenth")]
    [InlineData("Tau", "tau")]
    [InlineData("3x Osc", "3x osc")]
    [InlineData("808", "808")]
    public void PluginVersionsAreTheSamePlugin(string name, string family) => Assert.Equal(family, SongVersions.PluginFamily(name));

    [Fact]
    public void A_song_is_as_old_as_its_first_save_or_the_date_its_file_keeps()
    {
        Assert.Equal(new DateTime(2019, 1, 10), SongVersions.FirstMade([Save("Didums_77", new(2024, 8, 3)), Save("Didums", new(2019, 1, 10))]));
        // Every save was re-saved in 2024, but FL Studio kept the day it was made.
        Assert.Equal(new DateTime(2018, 11, 5), SongVersions.FirstMade([Save("Didums", new(2024, 8, 3), created: new(2018, 11, 5))]));
    }

    [Fact]
    public void FlStudioKeepsWhenAProjectWasMadeAndHowLongItWasOpen()
    {
        var payload = new byte[16];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 8), (new DateTime(2019, 2, 2, 14, 30, 0) - new DateTime(1899, 12, 30)).TotalDays);
        BitConverter.TryWriteBytes(payload.AsSpan(8, 8), 3.5 / 24);
        var (created, spent) = FlStudioReader.ProjectTime(payload);
        Assert.Equal(new DateTime(2019, 2, 2, 14, 30, 0), created!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal("3 h 30 min", spent);
        Assert.Equal((null, null), FlStudioReader.ProjectTime(new byte[16]));
    }

    [Fact]
    public void A_scan_keeps_how_many_notes_bars_and_channels_each_save_has()
    {
        new Scanner(_db).ScanProjects();
        var p = _db.Projects().Single(x => x.Name == "Night Drive");
        Assert.True(p.NoteCount > 0);
        Assert.True(p.LengthBars > 0);
        Assert.True(p.TrackCount > 0);
    }

    [Fact]
    public void NewSaveKeepsTheSongsRatingTagsAndNotes()
    {
        var scanner = new Scanner(_db);
        scanner.ScanProjects();
        var p = _db.Projects().Single(x => x.Name == "Night Drive");
        p.Status = "Mixing";
        p.Rating = 4;
        p.Notes = "needs a better drop";
        _db.SaveOrganisation(p);
        _db.SetTags(p.Id, ["dub"]);

        Fixtures.FlStudioProject(_root, "Night Drive_2");
        scanner.ScanProjects();
        var next = _db.Projects().Single(x => x.Name == "Night Drive_2");
        Assert.Equal(("Mixing", 4, "needs a better drop"), (next.Status, next.Rating, next.Notes));
        Assert.Equal(["dub"], next.Tags);
    }

    [Fact]
    public void TagsElektronBoxesFromOverbridgePlugins()
    {
        new Scanner(_db).ScanProjects();
        var bw = _db.Projects().First(x => x.Daw == "Bitwig Studio");
        Assert.Equal(["Analog Rytm MKII"], bw.Hardware);
        Assert.Contains("analog rytm mkii", bw.Tags);
    }

    [Fact]
    public void FlagsMissingPluginsOnceInstalledPluginsAreKnown()
    {
        var scanner = new Scanner(_db);
        scanner.ScanProjects();
        Assert.All(_db.Projects(), p => Assert.Equal(0, p.MissingPlugins));   // nothing flagged before a plugin scan

        _db.ReplaceInstalledPlugins(["FabFilter Pro-Q 3", "Serum (x64)", "Diva"]);
        var fl = _db.Project(_db.Projects().First(x => x.Daw == "FL Studio").Id)!;
        Assert.All(fl.Plugins, p => Assert.True(p.Installed, p.Name));
        var s1 = _db.Project(_db.Projects().First(x => x.Daw == "Studio One").Id)!;
        Assert.False(s1.Plugins.Single(p => p.Name == "Pro-C 2").Installed);
        Assert.True(s1.Plugins.Single(p => p.Name == "Mai Tai").Installed);   // native

        var suggestions = MatchFinder.ForPlugin(_db, "Pro-Q 4");
        Assert.Equal("FabFilter Pro-Q 3", suggestions.First().Candidate);
    }

    [Fact]
    public void FindsLocalMatchesForMissingSamples()
    {
        // Outside the projects' folders: a sample in a project's folder tree counts as found.
        var lib = Path.Combine(Fixtures.TempDir(), "Sample Library");
        Directory.CreateDirectory(Path.Combine(lib, "Drums"));
        File.WriteAllBytes(Path.Combine(lib, "Drums", "Kick 01.wav"), [0]);
        File.WriteAllBytes(Path.Combine(lib, "Drums", "Kick 02.wav"), [0]);
        File.WriteAllBytes(Path.Combine(lib, "Drums", "Hat.wav"), [0]);
        _db.SetList(Scanner.SampleFoldersKey, [lib]);
        var scanner = new Scanner(_db);
        Assert.Equal(3, scanner.ScanSampleFolders());
        scanner.ScanProjects();

        var fl = _db.Project(_db.Projects().First(x => x.Daw == "FL Studio").Id)!;
        var missing = fl.Samples.Single(s => !s.Found);
        var matches = MatchFinder.ForSample(_db, missing.Path);
        // The project looked in a "Drums" folder too, which counts in their favour.
        Assert.Equal(("Same file name, in matching folders", 1.0), (matches[0].Reason, matches[0].Score));
        Assert.EndsWith("Kick 01.wav", matches[0].Candidate);
        Assert.Contains(matches, m => m.Candidate.EndsWith("Kick 02.wav") && m.Reason == "Similar name, in matching folders");
        Assert.DoesNotContain(matches, m => m.Candidate.EndsWith("Hat.wav"));
        Assert.Contains("Kick%2001.wav", MatchFinder.SampleSearchUrl(missing.Path));
    }

    [Fact]
    public void AScanFindsASampleAnywhereInTheProjectsFolderEvenOnePutBackSinceTheLastScan()
    {
        var scanner = new Scanner(_db);
        scanner.ScanProjects();
        var fl = _db.Projects().Single(x => x.Daw == "FL Studio");
        Assert.Equal(1, fl.MissingSamples);

        // Solomon's rule: look in the project's folders, not just beside it (here Bitwig's bounce folder).
        Directory.CreateDirectory(Path.Combine(_root, "bounce"));
        File.WriteAllBytes(Path.Combine(_root, "bounce", "Kick 01.wav"), [0]);
        scanner.ScanProjects(full: true);
        Assert.True(_db.Project(fl.Id)!.Samples.Single(s => s.Path.EndsWith("Kick 01.wav")).Found);
        Assert.Equal(0, _db.Projects().Single(x => x.Daw == "FL Studio").MissingSamples);
    }

    [Fact]
    public void AScanReadsADawsProjectsAgainWhenItsReaderHasGotBetter()
    {
        var scanner = new Scanner(_db);
        scanner.ScanProjects();
        Assert.Equal("7", _db.GetSetting("reader_version:FL Studio"));
        Directory.CreateDirectory(Path.Combine(_root, "bounce"));
        File.WriteAllBytes(Path.Combine(_root, "bounce", "Kick 01.wav"), [0]);

        scanner.ScanProjects();   // the project hasn't changed, so it isn't read again
        Assert.Equal(1, _db.Projects().Single(x => x.Daw == "FL Studio").MissingSamples);

        _db.SetSetting("reader_version:FL Studio", "1");   // as a library last read with the older FL reader
        scanner.ScanProjects();
        Assert.Equal(0, _db.Projects().Single(x => x.Daw == "FL Studio").MissingSamples);
        Assert.Equal("7", _db.GetSetting("reader_version:FL Studio"));
    }

    [Fact]
    public void MarksDeletedProjectsAsMissing()
    {
        var scanner = new Scanner(_db);
        scanner.ScanProjects();
        File.Delete(Path.Combine(_root, "Night Drive.flp"));
        scanner.ScanProjects();
        Assert.True(_db.Projects().Single(p => p.Daw == "FL Studio").FileMissing);
    }

    [Fact]
    public void Finding_matches_reads_the_sample_index_once_until_it_changes_and_still_finds_longer_names()
    {
        // Fix looks for every missing sample in turn: the 350,000-file index was read again for each one.
        var lib = Path.Combine(Fixtures.TempDir(), "Samples");
        _db.ReplaceSampleIndex([Path.Combine(lib, "Big Room Festival Kick 01.wav"), Path.Combine(lib, "Snare.wav")]);
        var first = _db.SampleIndex();
        Assert.Same(first, _db.SampleIndex());
        _db.UpdateSampleIndex([Path.Combine(lib, "Hat.wav")]);   // gone, as the folder watcher says
        Assert.NotSame(first, _db.SampleIndex());

        // A name much longer than the missing one still matches by how it ends.
        var matches = MatchFinder.ForSample(_db, @"C:\Old Drive\Kick 01.wav");
        Assert.Equal([Path.Combine(lib, "Big Room Festival Kick 01.wav")], matches.Select(m => m.Candidate));
        Assert.Equal(MatchFinder.Similarity("kick01", "bigroomfestivalkick01"), matches[0].Score);
    }

    [Fact]
    public void A_folder_holds_a_projects_sample_saved_under_the_drives_old_letter()
    {
        // Clean up never offers a folder holding a sample a project uses. Songs saved when the drive was F: name
        // F:\Splice\Sounds, and the app finds those samples on K: now, so K:\Splice\Sounds holds them too.
        var info = new ProjectInfo { Daw = "FL Studio" };
        _db.Upsert(new LibraryDb.IndexedFile(Path.Combine(_root, "Old.flp"), 5, 1, info, null, [(@"F:\Splice\Sounds\Kick_01.wav", true)], []));
        Assert.Equal(@"F:\Splice\Sounds\Kick_01.wav", _db.SampleInside(@"K:\Splice\Sounds"));
        Assert.Equal(@"F:\Splice\Sounds\Kick_01.wav", _db.SampleInside("k:/Splice/Sounds/"));
        Assert.Equal(@"F:\Splice\Sounds\Kick_01.wav", _db.SampleInside(@"F:\Splice"));
        Assert.Null(_db.SampleInside(@"K:\Splice\Sound"));   // a folder whose name only starts the same
        Assert.Null(_db.SampleInside(@"K:\Splice\Other"));
        Assert.Null(_db.SampleInside(@"K:\Other\Splice\Sounds"));
    }

    [Fact]
    public void Projects_using_a_sample_are_found_by_its_path_without_reading_every_sample()
    {
        // "Projects using it" on the Sounds page: with no index on the path, every sample of 16,700 projects was read.
        new Scanner(_db).ScanProjects();
        var fl = _db.Project(_db.Projects().Single(x => x.Daw == "FL Studio").Id)!;
        Assert.Equal([fl.Summary.Id], _db.ProjectsUsingSample(fl.Samples[0].Path.ToUpperInvariant()));
        string Plan()
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db.FilePath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN SELECT DISTINCT project_id FROM samples WHERE path = 'x' COLLATE NOCASE";
            using var r = cmd.ExecuteReader();
            var plan = "";
            while (r.Read()) plan += r.GetString(3);
            return plan;
        }
        // Built once the window shows, not as the library opens: the first time it takes seconds with nothing on screen.
        Assert.DoesNotContain("ix_samples_path", Plan());
        _db.BuildNewIndexes();
        Assert.Contains("ix_samples_path", Plan());
        Assert.Equal([fl.Summary.Id], _db.ProjectsUsingSample(fl.Samples[0].Path.ToUpperInvariant()));
    }

    [Fact]
    public void A_project_marked_missing_is_back_when_its_file_comes_back_unchanged()
    {
        // Put back from the Recycle Bin, or moved out and back: the same size and time as when it was read.
        var scanner = new Scanner(_db);
        scanner.ScanProjects();
        var song = Path.Combine(_root, "Night Drive.flp");
        var away = Path.Combine(Fixtures.TempDir(), "Night Drive.flp");
        bool Missing() => _db.Projects().Single(p => p.Path == song).FileMissing;

        File.Move(song, away);
        scanner.ScanProjects();
        Assert.True(Missing());
        File.Move(away, song);
        scanner.ScanProjects();
        Assert.False(Missing());

        // The same through the folder watcher.
        File.Move(song, away);
        Assert.Equal((0, 1), scanner.Update([song]));
        Assert.True(Missing());
        File.Move(away, song);
        Assert.Equal((1, 0), scanner.Update([song]));
        Assert.False(Missing());
    }

    [Fact]
    public void A_renamed_song_folder_keeps_the_songs_status_rating_notes_and_tags()
    {
        var scanner = new Scanner(_db);
        scanner.ScanProjects();
        void Organise(ProjectSummary p, string status)
        {
            p.Status = status;
            p.Rating = 4;
            p.Notes = "keep the bass";
            _db.SaveOrganisation(p);
            _db.SetTags(p.Id, ["dub"]);
        }
        var bitwig = _db.Projects().Single(p => p.Daw == "Bitwig Studio");
        Organise(bitwig, "Mixing");

        // Renamed in Explorer: the watcher gives up on so many changes and a scan runs.
        var folder = Path.GetDirectoryName(bitwig.Path)!;
        Directory.Move(folder, folder + " (done)");
        scanner.ScanProjects();
        var moved = _db.Projects().Single(p => p.Daw == "Bitwig Studio");
        Assert.Equal((bitwig.Id, Path.Combine(folder + " (done)", Path.GetFileName(bitwig.Path)), false),
                     (moved.Id, moved.Path, moved.FileMissing));
        Assert.Equal(("Mixing", 4, "keep the bass"), (moved.Status, moved.Rating, moved.Notes));
        Assert.Contains("dub", moved.Tags);

        // Moved into another folder, as the folder watcher sees it: gone from one place, new in the other.
        var fl = _db.Projects().Single(p => p.Daw == "FL Studio");
        Organise(fl, "Finished");
        var to = Path.Combine(_root, "Finished", Path.GetFileName(fl.Path));
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(fl.Path, to);
        scanner.Update([fl.Path, to]);
        var flMoved = _db.Projects().Single(p => p.Daw == "FL Studio");
        Assert.Equal((fl.Id, to, false, "Finished"), (flMoved.Id, flMoved.Path, flMoved.FileMissing, flMoved.Status));

        // A copy, with the first still there, is a song of its own.
        File.Copy(to, Path.Combine(_root, Path.GetFileName(to)));
        scanner.ScanProjects();
        Assert.Equal(2, _db.Projects().Count(p => p.Daw == "FL Studio" && !p.FileMissing));
    }

    [Fact]
    public void A_scan_stopped_while_reading_a_renamed_folder_marks_none_of_its_songs_missing()
    {
        // Stopped half way through a renamed folder, the songs whose new place wasn't read yet still count where they
        // were: marked missing, a plugin only they use would look unused, and Clean up would tick its sounds.
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Album")).FullName;
        for (var i = 0; i < 120; i++) Fixtures.FlStudioProject(folder, $"Tune {(char)('a' + i % 26)}{(char)('a' + i / 26)}");
        var scanner = new Scanner(_db);
        scanner.ScanProjects();
        var before = _db.Projects().Where(p => p.Name.StartsWith("Tune ")).ToDictionary(p => p.Name, p => p.Id);
        Directory.Move(folder, folder + " (done)");
        using var stop = new CancellationTokenSource();
        var reading = new Each<ScanProgress>(p => { if (p.Stage == "Reading projects") stop.Cancel(); });
        Assert.ThrowsAny<OperationCanceledException>(() => scanner.ScanProjects(reading, ct: stop.Token));
        var songs = _db.Projects().Where(p => p.Name.StartsWith("Tune ")).ToList();
        Assert.Equal(120, songs.Count);
        Assert.DoesNotContain(songs, p => p.FileMissing);
        Assert.Contains(songs, p => p.Path.StartsWith(folder + " (done)"));   // what it read was found moved
        Assert.Contains(songs, p => p.Path.StartsWith(folder + Path.DirectorySeparatorChar));   // the rest wait for the next scan

        // The next scan finishes: each song carries on as itself in its new place.
        scanner.ScanProjects();
        songs = _db.Projects().Where(p => p.Name.StartsWith("Tune ")).ToList();
        Assert.Equal(120, songs.Count);
        Assert.All(songs, p => Assert.Equal((before[p.Name], false, true), (p.Id, p.FileMissing, p.Path.StartsWith(folder + " (done)"))));
    }

    [Fact]
    public void Projects_in_a_folder_the_scan_couldnt_read_arent_marked_missing()
    {
        new Scanner(_db).ScanProjects();
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _db.MarkMissingFiles(none, [_root], unreadable: [Path.Combine(_root, "Loop Idea"), _root]);
        Assert.All(_db.Projects(), p => Assert.False(p.FileMissing));
        // Only that folder's projects keep how they were.
        var unread = Path.Combine(_root, "Loop Idea");
        _db.MarkMissingFiles(none, [_root], unreadable: [unread]);
        Assert.All(_db.Projects(), p => Assert.Equal(!p.Path.StartsWith(unread + Path.DirectorySeparatorChar), p.FileMissing));
        Assert.Contains(_db.Projects(), p => !p.FileMissing);
    }

    [Fact]
    public void A_folder_whose_name_only_starts_like_a_project_folder_isnt_inside_it()
    {
        // A folder taken out of the list keeps its projects. "Projects Old" isn't inside "Projects", so a scan of
        // "Projects" mustn't mark them missing, nor read "Old\Templates" as a templates folder of its own.
        var old = _root + " Old";
        Directory.CreateDirectory(old);
        var song = Fixtures.FlStudioProject(old, "Old Song");
        _db.Upsert(Scanner.Analyse(song));
        var deleted = Path.Combine(_root, "Night Drive.flp");
        new Scanner(_db).ScanProjects();
        File.Delete(deleted);

        new Scanner(_db).ScanProjects();
        Assert.False(_db.Projects().Single(p => p.Path == song).FileMissing);
        Assert.True(_db.Projects().Single(p => p.Path == deleted).FileMissing);   // one really gone still is
        Assert.False(Scanner.IsIgnored(Path.Combine(old, "Templates", "Song.flp"), [_root]));
        Assert.True(Scanner.IsIgnored(Path.Combine(_root, "Templates", "Song.flp"), [_root]));
    }

    [Fact]
    public void ExportsMidiAndPresetsToFiles()
    {
        var flp = Path.Combine(_root, "Night Drive.flp");
        var outRoot = Path.Combine(_root, "exports");
        var midi = Exporter.Midi(Readers.FlStudio, flp, outRoot, 128);
        Assert.Equal(["01 Lead (Serum).mid", "02 Kick (Kick 01.wav).mid", "03 Bass (3x Osc).mid"], midi.Files);
        Assert.True(File.Exists(Path.Combine(midi.Folder, "01 Lead (Serum).mid")));
        var presets = Exporter.Presets(Readers.FlStudio, flp, outRoot);
        Assert.Contains("Serum - Lead.fxp", presets.Files);
    }

    [Fact]
    public void The_same_plugins_and_sample_files_again_change_nothing()
    {
        _db.ReplaceInstalledPlugins(["Serum", "Vital"]);
        _db.ReplaceSampleIndex(["/s/Kick.wav", "/s/Snare.wav"]);
        var before = (_db.ProjectsRevision, _db.PluginsRevision, _db.SampleIndexRevision);

        _db.ReplaceInstalledPlugins(["Vital", "Serum"]);
        _db.ReplaceSampleIndex(["/s/Snare.wav", "/s/Kick.wav"]);
        Assert.Equal(before, (_db.ProjectsRevision, _db.PluginsRevision, _db.SampleIndexRevision));

        _db.ReplaceSampleIndex(["/s/Snare.wav", "/s/Hat.wav"]);
        Assert.Equal(before.SampleIndexRevision + 1, _db.SampleIndexRevision);
        Assert.Equal(["/s/Hat.wav", "/s/Snare.wav"], _db.SampleIndex().Select(s => s.Path).Order());
        _db.ReplaceInstalledPlugins(["Vital"]);
        Assert.Equal(before.PluginsRevision + 1, _db.PluginsRevision);
        Assert.Equal(["Vital"], _db.InstalledNames());
    }
}

public class SampleLocatorTests
{
    const string Project = @"K:\Bitwig Studio\Projects\analog full set\analog full set nb 36.bwproject";

    [Fact]
    public void TriesTheSavedPathThenTheProjectsDriveThenFoldersThatMovedWithTheProject()
    {
        Assert.Equal([@"F:\Bitwig Studio\Projects\analog full set\recordings\Syntakt 2-1.wav",
                      @"K:\Bitwig Studio\Projects\analog full set\recordings\Syntakt 2-1.wav"],
            SampleLocator.Candidates(@"F:\Bitwig Studio\Projects\analog full set\recordings\Syntakt 2-1.wav", Project));
        // Another project's folder, from when the projects were kept in Documents.
        Assert.Equal([@"C:\Users\silki\Documents\Bitwig Studio\Projects\what you wanna do\samples\BDsub808_Long_C2.wav",
                      @"K:\Users\silki\Documents\Bitwig Studio\Projects\what you wanna do\samples\BDsub808_Long_C2.wav",
                      @"K:\Bitwig Studio\Projects\what you wanna do\samples\BDsub808_Long_C2.wav"],
            SampleLocator.Candidates(@"C:\Users\silki\Documents\Bitwig Studio\Projects\what you wanna do\samples\BDsub808_Long_C2.wav", Project));
        Assert.Equal([@"K:\Bitwig Studio\Projects\analog full set\recordings\BD 1-2.wav"],
            SampleLocator.Candidates("recordings/BD 1-2.wav", Project));
        Assert.Equal(["/Users/sol/Music/Bitwig Studio/Projects/song/recordings/a.wav", @"K:\Bitwig Studio\Projects\song\recordings\a.wav"],
            SampleLocator.Candidates("/Users/sol/Music/Bitwig Studio/Projects/song/recordings/a.wav", Project));
        Assert.Equal([@"F:\a.wav", @"K:\a.wav"], SampleLocator.Candidates(@"F:\a.wav", Project));
    }

    [Fact]
    public void LooksInTheProjectsFoldersAndTheDawsContentFolders()
    {
        var drive = Path.Combine(Fixtures.TempDir(), "K");
        var song = Path.Combine(drive, "Bitwig Studio", "Projects", "This Song");
        var project = Path.Combine(song, "This Song.bwproject");
        var content = Path.Combine(Fixtures.TempDir(), "installed-packages");
        string Make(params string[] parts)
        {
            var file = Path.Combine(parts);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, [0]);
            return file;
        }
        Make(project);
        var recording = Make(song, "recordings", "Syntakt 2-1.wav");
        var bounce = Make(song, "bounce", "Kontakt 7-bounce-6.wav");
        var other = Make(drive, "Bitwig Studio", "Projects", "Other Song", "samples", "BDsub808.wav");
        var kick = Make(content, "Drums", "Kicks", "Kick 9.wav");
        Make(content, "Other", "Snare.wav");
        Make(content, "Projects", "Old Song", "recordings", "Audio 1-1.wav");
        var locator = new SampleLocator(_ => [content]);

        Assert.Equal(recording, locator.Locate(@"F:\Bitwig Studio\Projects\This Song\recordings\Syntakt 2-1.wav", project));
        Assert.Equal(bounce, locator.Locate(@"F:\Old exports\Kontakt 7-bounce-6.wav", project));   // anywhere in the project's folder
        Assert.Equal(other, locator.Locate(@"C:\Users\silki\Documents\Bitwig Studio\Projects\Other Song\samples\BDsub808.wav", project));
        Assert.Equal(kick, locator.Locate(@"D:\Packs\Kicks\Kick 9.wav", project));   // the DAW's own sounds, in a folder of the same name
        Assert.Null(locator.Locate(@"D:\Packs\Snares\Snare.wav", project));
        Assert.Null(locator.Locate(@"E:\Old Song\recordings\Audio 1-1.wav", project));   // another project's recording isn't this one's
        Assert.Null(locator.Locate(@"D:\Gone\Missing.wav", project));
        Assert.True(locator.Found(@"%FLStudioFactoryData%\Data\Patches\Packs\Drums\Kick.wav", project));
        Assert.False(locator.Found(@"D:\Gone\Missing.wav", project));

        // A locator reads each folder once: a file added since is found by the next one (the next scan), and a file that
        // has gone since isn't found.
        var added = Make(song, "bounce", "New.wav");
        Assert.Null(locator.Locate(@"D:\Gone\New.wav", project));
        Assert.Equal(added, new SampleLocator(_ => []).Locate(@"D:\Gone\New.wav", project));
        File.Delete(bounce);
        Assert.Null(locator.Locate(@"F:\Old exports\Kontakt 7-bounce-6.wav", project));
    }

    [Fact]
    public void FindsTheLibraryBesideABitwigProjectsFolder()
    {
        var bitwig = Path.Combine(Fixtures.TempDir(), "Bitwig Studio");
        Directory.CreateDirectory(Path.Combine(bitwig, "Library", "Samples"));
        var project = Path.Combine(bitwig, "Projects", "Song", "Song.bwproject");
        Assert.Contains(Path.Combine(bitwig, "Library"), SampleLocator.DawContentFolders(project));
        Assert.All(SampleLocator.DawContentFolders(project), f => Assert.True(Directory.Exists(f)));
        Assert.All(SampleLocator.DawContentFolders("Song.flp"), f => Assert.True(Directory.Exists(f)));
        Assert.Empty(SampleLocator.DawContentFolders("Song.als"));
    }
}
