using MusicOrganiser.Core.LiveTests;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Tests;

/// <summary>Settings › Self-check: the app checking itself with the real library, and its report for Claude.</summary>
public sealed class SelfCheckTests
{
    [Fact]
    public async Task A_run_reads_every_project_and_writes_its_report()
    {
        var dir = Fixtures.TempDir();
        var projects = Path.Combine(dir, "Projects");
        Directory.CreateDirectory(projects);
        var good = Fixtures.FlStudioProject(projects);
        var broken = Path.Combine(projects, "Broken.flp");
        File.WriteAllBytes(broken, [1, 2, 3, 4]);
        var gone = Path.Combine(projects, "Gone.flp");
        using var sounds = new SoundLibrary(Path.Combine(dir, "sounds.db"));

        var run = await SelfCheck.RunAsync(new SelfCheckOptions
        {
            ReportFolder = Path.Combine(dir, "reports"), DataFolder = Path.Combine(dir, "data"),
            ProjectFolders = [projects, Path.Combine(dir, "Unplugged drive")], Projects = [good, broken, gone],
            Sounds = sounds, ScannerExecutable = Path.Combine(dir, "no-scanner"),
            AppChecks = [("App", "Something the app adds", _ => Task.FromResult(LiveVerdict.Pass("fine")))],
        });

        LiveResult Result(string name) => run.Results.Single(r => r.Name == name);
        Assert.Equal(LiveOutcome.Passed, Result("App data folder can be written").Outcome);
        Assert.Equal(LiveOutcome.Failed, Result("Library folders are there").Outcome);
        Assert.Contains(Result("Library folders are there").Lines, l => l.EndsWith("Unplugged drive", StringComparison.Ordinal));

        var read = Result("Every project opens");
        Assert.Equal(LiveOutcome.Failed, read.Outcome);
        Assert.Equal("1 of 2 projects couldn't be read (1 in the library aren't on disk any more).", read.Detail);
        Assert.Contains(read.Lines, l => l.StartsWith("Failed: " + broken, StringComparison.Ordinal));
        Assert.Equal(1, (int)read.Data!["perDaw"]!["FL Studio"]!["failed"]!);

        Assert.Equal(LiveOutcome.Skipped, Result("Sound library loads").Outcome);   // empty
        Assert.Equal(LiveOutcome.Passed, Result("Something the app adds").Outcome);
        Assert.Equal(LiveOutcome.Failed, Result("Plugin scanner reads a plugin").Outcome);   // there's no scanner there

        Assert.NotNull(run.ReportMarkdown);
        Assert.StartsWith($"{run.Stamp}-{SelfCheck.Key}", Path.GetFileName(run.ReportMarkdown));
        Assert.Contains("Every project opens", File.ReadAllText(run.ReportMarkdown));
        Assert.True(File.Exists(run.ReportJson));
    }

    [Fact]
    public async Task With_nothing_set_up_it_skips_rather_than_fails()
    {
        var dir = Fixtures.TempDir();
        var run = await SelfCheck.RunAsync(new SelfCheckOptions
        {
            ReportFolder = Path.Combine(dir, "reports"), DataFolder = dir, ScannerExecutable = Path.Combine(dir, "no-scanner"),
        });

        Assert.Equal(LiveOutcome.Skipped, run.Results.Single(r => r.Name == "Library folders are there").Outcome);
        Assert.Equal(LiveOutcome.Skipped, run.Results.Single(r => r.Name == "Every project opens").Outcome);
        Assert.Equal(LiveOutcome.Skipped, run.Results.Single(r => r.Name == "Sound library loads").Outcome);
    }
}
