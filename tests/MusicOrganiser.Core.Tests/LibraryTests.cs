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
    public void ScansEveryProjectOnceAndSkipsAutosaves()
    {
        new Scanner(_db).ScanProjects();
        var projects = _db.Projects();
        Assert.Equal(["Bitwig Studio", "FL Studio", "Studio One"], projects.Select(p => p.Daw).Order());
        Assert.All(projects, p => Assert.Null(p.Error));
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
    [InlineData("fleck athens FLP19", "fleck athens", 19)]
    [InlineData("fleck athens flp_15", "fleck athens", 15)]
    [InlineData("fleckathensFLP19", "fleckathens", 19)]
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
    public void Saves_marked_FLP15_to_FLP19_each_in_their_own_Bitwig_folder_are_one_song()
    {
        ProjectSummary P(string name, int day) =>
            new() { Name = name, Path = $@"K:\Bitwig Studio\Projects\{name}\{name}.bwproject", Daw = "Bitwig Studio",
                    Folder = $@"K:\Bitwig Studio\Projects\{name}", Modified = new DateTime(2025, 3, day) };
        var groups = SongVersions.Group(Enumerable.Range(15, 5).Select(n => P($"fleck athens FLP{n}", n)).Append(P("fleck athens", 1)));
        var song = Assert.Single(groups);
        Assert.Equal("fleck athens", song.Song);
        Assert.Equal("fleck athens FLP19", song.Latest.Name);
        Assert.Equal(5, song.Older.Count);
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
        var lib = Path.Combine(_root, "Sample Library");
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
    public void MarksDeletedProjectsAsMissing()
    {
        var scanner = new Scanner(_db);
        scanner.ScanProjects();
        File.Delete(Path.Combine(_root, "Night Drive.flp"));
        scanner.ScanProjects();
        Assert.True(_db.Projects().Single(p => p.Daw == "FL Studio").FileMissing);
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
