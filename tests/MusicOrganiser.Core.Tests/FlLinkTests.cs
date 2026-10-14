using System.ComponentModel;
using System.Diagnostics;
using MusicOrganiser.Core.DawLink;

namespace MusicOrganiser.Core.Tests;

/// <summary>
/// The app's side of the FL Studio link. The end-to-end test talks to the real FL Studio script, running in
/// the stand-in FL Studio from extensions/fl-studio/test (it needs Python, and returns early without it).
/// </summary>
public class FlLinkTests
{
    [Fact]
    public async Task TalksToTheFlStudioScript()
    {
        using var fl = ScriptRunner.Start();
        if (fl is null) return;
        var link = new FlLink(fl.Folder);

        var hello = await link.HelloAsync();
        Assert.Equal(("FL Studio", 37, "Night Drive - FL Studio 2025", true), (hello.Product, hello.Api, hello.Title, hello.Changes));
        Assert.Equal("Producer Edition v25.1.6 [build 4997]", hello.Version);
        Assert.True(File.Exists(Path.Combine(hello.Script!, FlLink.Script)));

        var project = await link.ProjectAsync();
        Assert.Equal(124, project.Tempo);
        Assert.Equal(["Kick", "Bass", "Chops", "Café pad"], project.Channels.Select(c => c.Name));
        Assert.Equal(("Serum", 4240, 2), (project.Channels[1].Plugin, project.Channels[1].Params, project.Channels[1].Mixer));
        Assert.Null(project.Channels[0].Plugin);
        Assert.Equal(["Master", "Kick", "Bass"], project.Mixer.Select(t => t.Name));
        Assert.Equal([("Fruity Parametric EQ 2", 0), ("Fruity Reeverb 2", 3)], project.Mixer[2].Plugins.Select(p => (p.Name, p.Slot)));
        Assert.Equal("Room", project.Mixer[2].Plugins[1].Label);

        var serum = await link.ParamsAsync(FlPlugin.OnChannel(1));
        Assert.Equal(["Master Volume", "A Octave", "Filter Cutoff"], serum.Select(p => p.Name));
        Assert.Equal((3, 0.25, "440 Hz"), (serum[2].Index, serum[2].Value ?? -1, serum[2].Text));
        Assert.Equal(3 + 128, (await link.ParamsAsync(FlPlugin.OnChannel(1), midi: true)).Count);

        var mix = await link.SetParameterAsync(FlPlugin.OnMixer(2, 3), 0, 0.5);
        Assert.Equal(("Mix", 0.5, "50%"), (mix.Name, mix.Value ?? -1, mix.Text));

        // FL Studio's own words come back as the error.
        var e = await Assert.ThrowsAsync<DawLinkException>(() => link.ParamsAsync(FlPlugin.OnChannel(0)));
        Assert.Equal("There's no plugin on channel 0.", e.Message);

        // Each request and its answer are cleared away.
        Assert.Empty(Directory.GetFiles(fl.Folder));
    }

    [Fact]
    public async Task SaysWhenFlStudioIsntAnswering()
    {
        var folder = Path.Combine(Fixtures.TempDir(), "FL Studio link");
        var link = new FlLink(folder) { Timeout = TimeSpan.FromMilliseconds(300) };
        var e = await Assert.ThrowsAsync<DawLinkException>(() => link.HelloAsync());
        Assert.Equal(FlLink.NotRunning, e.Message);

        File.WriteAllText(Path.Combine(folder, "response-12.json"), """{"id":12,"ok":true,"result":{}}""");   // from an earlier run
        e = await Assert.ThrowsAsync<DawLinkException>(() => link.HelloAsync());
        Assert.Equal(FlLink.NotRunning, e.Message);
        // Nothing is left for FL Studio to answer later.
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void ComparesTheFileWithWhatFlStudioShows()
    {
        var live = new FlProject("Night Drive - FL Studio 2025", 124, false, false,
            [new(0, "Kick", 0, 1, null, 0), new(1, "Bass", 2, 2, "Serum", 4240), new(2, "Chops", 2, 3, "Slicex", 12)],
            [new(0, "Master", [new(0, "Fruity Limiter", "Fruity Limiter", 20)]), new(1, "Kick", []),
             new(2, "Bass", [new(3, "Fruity Reeverb 2", "Room", 30)])]);
        const string path = @"C:\Music\FL\Night Drive.flp";

        var same = FlCheck.Compare(path, 124, ["Kick", "Bass", "Chops"], ["Serum", "slicex", "Fruity Limiter", "Fruity Reeverb 2"], live);
        Assert.Equal([CheckResult.Same, CheckResult.Same, CheckResult.Same], same.Select(l => l.Result));
        Assert.Equal("Channels: 3 channels, named the same in both.", same[1].Text);
        Assert.Equal("Plugins: all 4 plugins FL Studio shows are in the app's list, and no others.", same[2].Text);

        var off = FlCheck.Compare(path, 140, ["Kick", "Bass", "Channel 3"], ["Serum", "Fruity Limiter", "Fruity Delay 3"],
                                  live with { Modified = true });
        Assert.Equal([
            "Tempo: the app read 140 BPM, FL Studio says 124 BPM.",
            "Channels FL Studio shows that the app didn't read: “Chops”.",
            "Channels the app read that FL Studio doesn't show: “Channel 3”.",
            "Plugins FL Studio shows that the app didn't find: “Slicex” on the “Chops” channel, “Fruity Reeverb 2” on mixer track “Bass”.",
            "Plugins the app found that FL Studio doesn't show: “Fruity Delay 3”.",
        ], off.Take(5).Select(l => l.Text));
        Assert.Contains("unsaved changes", off[5].Text);

        var grouped = FlCheck.Compare(path, null, ["Bass"], ["Serum"], live with { Grouped = true, Channels = [live.Channels[1]], Mixer = [] });
        Assert.Equal([CheckResult.Same, CheckResult.Note, CheckResult.Same], grouped.Select(l => l.Result));

        var other = FlCheck.Compare(@"C:\Music\FL\Another Song.flp", 124, [], [], live);
        Assert.Equal("FL Studio's window says “Night Drive - FL Studio 2025”, so it has another project open. " +
                     "Open this one in FL Studio, then check again.", other.Single().Text);
    }

    [Theory]
    [InlineData("Night Drive - FL Studio 2025", "Night Drive", true)]
    [InlineData("FL Studio 2025 - Night Drive.flp *", "Night Drive", true)]
    [InlineData("night drive - v2.flp - FL Studio 2025", "Night Drive - v2", true)]
    [InlineData("Night Drive 2 - FL Studio 2025", "Night Drive", false)]
    [InlineData("FL Studio 2025", "Night Drive", false)]
    [InlineData("", "Night Drive", false)]
    public void ReadsWhichProjectFlStudioHasOpen(string title, string name, bool shows) =>
        Assert.Equal(shows, FlCheck.ShowsProject(title, name));

    /// <summary>extensions/fl-studio/test/serve.py, run with Python in a folder of its own.</summary>
    sealed class ScriptRunner : IDisposable
    {
        readonly Process _python;
        public string Folder { get; }

        ScriptRunner(Process python, string folder)
        {
            _python = python;
            Folder = folder;
        }

        public static ScriptRunner? Start()
        {
            var script = Fixtures.RepoFile(Path.Combine("extensions", "fl-studio", "test", "serve.py"));
            if (script is null) return null;
            var folder = Fixtures.TempDir();
            foreach (var python in OperatingSystem.IsWindows() ? ["python", "python3"] : new[] { "python3", "python" })
            {
                var psi = new ProcessStartInfo(python)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add(script);
                psi.ArgumentList.Add(folder);
                psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
                Process p;
                try { p = Process.Start(psi)!; }
                catch (Win32Exception) { continue; }   // not installed under this name
                if (p.StandardOutput.ReadLine() == "ready") return new ScriptRunner(p, folder);
                p.WaitForExit(10_000);
                var error = p.StandardError.ReadToEnd();
                p.Dispose();
                // A Python that runs the script and fails is a real failure; one that isn't really there (like
                // Windows' python3 that offers to install it from the Store) isn't.
                if (error.Contains("Traceback", StringComparison.Ordinal)) throw new InvalidOperationException($"serve.py failed:\n{error}");
            }
            return null;
        }

        public void Dispose()
        {
            try
            {
                _python.StandardInput.Close();
                if (!_python.WaitForExit(5000)) _python.Kill(true);
            }
            catch (InvalidOperationException) { }
            _python.Dispose();
            try { Directory.Delete(Folder, true); }
            catch (IOException) { }
        }
    }
}
