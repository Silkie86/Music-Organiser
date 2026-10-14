using System.Diagnostics;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.Core.Tests;

/// <summary>
/// The app keeping up with Bitwig and FL Studio: what each has open, and when it saves. The end-to-end test runs both
/// scripts in their stand-ins (it needs Node.js and Python, and returns early without them).
/// </summary>
public class DawWatchTests
{
    [Theory]
    [InlineData("Night Drive - FL Studio 2025", "Night Drive")]
    [InlineData("FL Studio 2025 - Night Drive.flp *", "Night Drive")]
    [InlineData("Night Drive - v2.flp - FL Studio 2025", "Night Drive - v2")]
    [InlineData("FL Studio 2025", "")]
    [InlineData("", "")]
    public void ReadsTheProjectNameInFlStudiosTitle(string title, string name) =>
        Assert.Equal(name, DawState.FlProjectName(title));

    [Fact]
    public void SeesWhenADawHasSaved()
    {
        var edited = new DawState(DawState.Bitwig, true, true, "Bitwig Studio 6.1.2", "Night Drive", "Night Drive", Modified: true);
        var saved = edited with { Modified = false };
        Assert.True(saved.SavedSince(edited));
        Assert.True((saved with { Project = "Night Drive v2" }).SavedSince(saved));   // Save As
        Assert.False(edited.SavedSince(saved));                                       // edited, not saved
        Assert.False(saved.SavedSince(saved));
        Assert.False(saved.SavedSince(null));                                         // the first look
        Assert.False(saved.SavedSince(new DawState(DawState.Bitwig, false)));        // just connected
        Assert.False((saved with { Project = "" }).SavedSince(edited));               // a new project, not saved yet
        Assert.False((edited with { Modified = null }).SavedSince(edited));           // an older script, which doesn't say
    }

    [Fact]
    public void FindsTheOpenProjectInTheLibrary()
    {
        var root = Fixtures.TempDir();
        var older = Fixtures.FlStudioProject(Directory.CreateDirectory(Path.Combine(root, "2025")).FullName);
        var newer = Fixtures.FlStudioProject(Directory.CreateDirectory(Path.Combine(root, "2026")).FullName);
        File.SetLastWriteTimeUtc(older, new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        var loop = Fixtures.BitwigProject(root);
        using var db = new LibraryDb(Path.Combine(root, "library.db"));
        db.AddRoot(root);
        new Scanner(db).ScanProjects();

        var fl = new DawState(DawState.FlStudio, true, true, "FL Studio 25", "Night Drive", "Night Drive - FL Studio 2025", false);
        Assert.Equal(newer, fl.FindIn(db)?.Path);   // the newest save of that name
        var bitwig = new DawState(DawState.Bitwig, true, true, "Bitwig Studio 6.1.2", "loop idea", "loop idea", false);
        Assert.Equal(loop, bitwig.FindIn(db)?.Path);
        Assert.Null((bitwig with { Project = "Night Drive" }).FindIn(db));   // that's an FL Studio project
        Assert.Null((fl with { Project = "", Title = "FL Studio 2025" }).FindIn(db));
        Assert.Null((fl with { Answering = false }).FindIn(db));
    }

    [Fact]
    public async Task KeepsUpWithBothDaws()
    {
        using var fl = FlLinkTests.ScriptRunner.Start();
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        if (fl is null || bitwig is null) return;
        var bitwigOpen = true;
        using var watch = new DawWatch(bitwig.Port, fl.Folder, flOpen: () => true, bitwigOpen: () => bitwigOpen) { Retry = TimeSpan.Zero };

        var states = await PollUntil(watch, s => s.All(d => d.Answering));
        Assert.Equal([
            new DawState(DawState.Bitwig, true, true, "Bitwig Studio 6.1.2", "Night Drive", "Night Drive", false, true),
            new DawState(DawState.FlStudio, true, true, "FL Studio Producer Edition v25.1.6 [build 4997]", "Night Drive",
                         "Night Drive - FL Studio 2025", false, true),
        ], states);

        // Both projects are changed, then Bitwig saves its project under another name and FL Studio opens another.
        bitwig.Change("""{"modified": true}""");
        fl.Change("""{"modified": 1}""");
        var edited = await PollUntil(watch, s => s.All(d => d.Modified == true));
        bitwig.Change("""{"project": "Night Drive v2", "modified": false}""");
        fl.Change("""{"title": "Other Song.flp - FL Studio 2025", "modified": 0}""");
        states = await PollUntil(watch, s => s[0].Project == "Night Drive v2" && s[1].Project == "Other Song");
        Assert.True(states[0].SavedSince(edited[0]));
        Assert.True(states[1].SavedSince(edited[1]));

        // Bitwig closes.
        bitwigOpen = false;
        bitwig.Dispose();
        states = await PollUntil(watch, s => !s[0].Answering);
        Assert.Equal(new DawState(DawState.Bitwig, false, false), states[0]);
        Assert.True(states[1].Answering);
    }

    /// <summary>Asks the DAWs (as the app does every couple of seconds) until they show what's expected.</summary>
    internal static async Task<IReadOnlyList<DawState>> PollUntil(DawWatch watch, Func<IReadOnlyList<DawState>, bool> done)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var states = await watch.PollAsync();
            if (done(states)) return states;
            if (waited.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail($"The DAWs didn't get there: {string.Join(", ", states)}");
            await Task.Delay(50);
        }
    }
}
