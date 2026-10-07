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
        Assert.Equal(["01 Intro.mid", "02 Drop.mid"], midi.Files);
        Assert.True(File.Exists(Path.Combine(midi.Folder, "01 Intro.mid")));
        var presets = Exporter.Presets(Readers.FlStudio, flp, outRoot);
        Assert.Contains("01 Serum - Lead.fxp", presets.Files);
    }
}
