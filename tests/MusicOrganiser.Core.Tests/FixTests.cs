using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;
using Xunit;

namespace MusicOrganiser.Core.Tests;

/// <summary>Fix: a copy of a project with its missing samples relinked, next to the untouched original.</summary>
public class FixTests : IDisposable
{
    readonly string _root = Fixtures.TempDir();
    readonly string _lib;
    readonly LibraryDb _db;

    public FixTests()
    {
        _lib = Path.Combine(_root, "Sample Library");
        Directory.CreateDirectory(Path.Combine(_lib, "Drums"));
        File.WriteAllBytes(Path.Combine(_lib, "Drums", "Kick 01.wav"), [0]);
        File.WriteAllBytes(Path.Combine(_lib, "Drums", "Snare 808 v2.wav"), [0]);
        _db = new LibraryDb(Path.Combine(_root, "library.db"));
        _db.AddRoot(Path.Combine(_root, "Projects"));
        _db.SetList(Scanner.SampleFoldersKey, [_lib]);
    }

    public void Dispose() => _db.Dispose();

    ProjectDetail Scan(string daw)
    {
        var scanner = new Scanner(_db);
        scanner.ScanSampleFolders();
        scanner.ScanProjects();
        return _db.Project(_db.Projects().First(p => p.Daw == daw).Id)!;
    }

    [Fact]
    public void An_FL_project_gets_a_fixed_copy_with_the_sample_relinked_and_the_original_untouched()
    {
        var dir = Path.Combine(_root, "Projects");
        Directory.CreateDirectory(dir);
        var path = Fixtures.FlStudioProject(dir);
        var before = File.ReadAllBytes(path);

        var plan = ProjectFixer.Plan(_db, Scan("FL Studio"));
        var kick = plan.Single(l => l.IsSample);
        Assert.Equal((FixAction.Relink, "Kick 01.wav"), (kick.Action, kick.Name));
        Assert.Equal(Path.Combine(_lib, "Drums", "Kick 01.wav"), kick.Use);

        var (fixedPath, changed) = ProjectFixer.Write(path, plan);
        Assert.Equal(1, changed);
        Assert.Equal(Path.Combine(dir, "Night Drive (fixed).flp"), fixedPath);
        Assert.Equal(before, File.ReadAllBytes(path));

        var info = new FlStudioReader().Read(fixedPath);
        Assert.Equal([kick.Use!], info.Samples);
        Assert.Equal(new FlStudioReader().Read(path).Plugins.Select(p => p.Name), info.Plugins.Select(p => p.Name));

        // Every other event comes through as it was.
        var a = FlStudioReader.ReadEvents(before).Events;
        var b = FlStudioReader.ReadEvents(File.ReadAllBytes(fixedPath)).Events;
        Assert.Equal(a.Select(e => e.Id), b.Select(e => e.Id));
        Assert.All(a.Zip(b).Where(p => p.First.Id != 196), p => Assert.Equal(p.First.Payload.ToArray(), p.Second.Payload.ToArray()));

        // A second fix doesn't overwrite the first.
        Assert.Equal(Path.Combine(dir, "Night Drive (fixed 2).flp"), ProjectFixer.FixedPath(path));
    }

    [Fact]
    public void A_Bitwig_project_gets_a_ticked_suggestion_relinked()
    {
        var dir = Path.Combine(_root, "Projects");
        Directory.CreateDirectory(dir);
        var path = Fixtures.BitwigProject(dir);

        var plan = ProjectFixer.Plan(_db, Scan("Bitwig Studio"));
        var snare = plan.Single(l => l.IsSample && l.Name == "Snare 808.wav");
        // Only a similar name: offered, not used unless it's ticked.
        Assert.Equal(FixAction.Suggestion, snare.Action);
        Assert.EndsWith("Snare 808 v2.wav", snare.Use);

        var (fixedPath, changed) = ProjectFixer.Write(path, [snare]);
        Assert.Equal(1, changed);
        var samples = new BitwigReader().Read(fixedPath).Samples;
        Assert.Contains(snare.Use, samples);
        Assert.DoesNotContain(@"D:\Splice\Snare 808.wav", samples);
    }

    [Fact]
    public void Plugins_are_listed_as_swaps_to_make_by_hand_never_written()
    {
        var dir = Path.Combine(_root, "Projects");
        Directory.CreateDirectory(dir);
        var path = Fixtures.FlStudioProject(dir);
        _db.ReplaceInstalledPlugins(["Serum 2", "FabFilter Pro-Q 4"]);

        var plan = ProjectFixer.Plan(_db, Scan("FL Studio"));
        var serum = plan.Single(l => l.Name == "Serum");
        Assert.Equal((FixAction.Suggestion, "Serum 2"), (serum.Action, serum.Use));
        var proq = plan.Single(l => l.Name == "Pro-Q 3");
        Assert.Equal((FixAction.Suggestion, "Pro-Q 3", "FabFilter Pro-Q 4"), (proq.Action, proq.Name, proq.Use));
        Assert.False(proq.CanWrite);
        Assert.Throws<InvalidOperationException>(() => ProjectFixer.Write(path, [proq]));
        Assert.False(File.Exists(Path.Combine(dir, "Night Drive (fixed).flp")));
    }

    [Fact]
    public void Without_sample_folders_it_says_where_to_add_them()
    {
        _db.SetList(Scanner.SampleFoldersKey, []);
        var dir = Path.Combine(_root, "Projects");
        Directory.CreateDirectory(dir);
        Fixtures.FlStudioProject(dir);
        var line = ProjectFixer.Plan(_db, Scan("FL Studio")).Single(l => l.IsSample);
        Assert.Equal(FixAction.StillMissing, line.Action);
        Assert.Contains("Settings", line.Note);
    }
}
