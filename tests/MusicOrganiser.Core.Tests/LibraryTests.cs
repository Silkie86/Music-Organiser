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
    [InlineData("Didums_70", "Didums", 70)]
    [InlineData("Didums", "Didums", 1)]
    [InlineData("Beauty bitch (orch)_9", "Beauty bitch (orch)", 9)]
    [InlineData("a_2_3", "a_2", 3)]
    [InlineData("130", "130", 1)]
    [InlineData("_5", "_5", 1)]
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
        Assert.Equal(("Same file name", 1.0), (matches[0].Reason, matches[0].Score));
        Assert.EndsWith("Kick 01.wav", matches[0].Candidate);
        Assert.Contains(matches, m => m.Candidate.EndsWith("Kick 02.wav") && m.Reason == "Similar name");
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
}
