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
        // Which part of the setup is missing, as far as this computer can tell (see the next test).
        var why = FlSetup.WhyNotAnswering();
        var e = await Assert.ThrowsAsync<DawLinkException>(() => link.HelloAsync());
        Assert.Equal(why, e.Message);

        File.WriteAllText(Path.Combine(folder, "response-12.json"), """{"id":12,"ok":true,"result":{}}""");   // from an earlier run
        e = await Assert.ThrowsAsync<DawLinkException>(() => link.HelloAsync());
        Assert.Equal(why, e.Message);
        // Nothing is left for FL Studio to answer later.
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void SaysWhichPartOfTheFlStudioSetupIsMissing()
    {
        Assert.Equal(FlSetup.NotOpen, FlSetup.WhyNotAnswering(false, ["Music Organiser"]));
        // FL Studio only runs the script for a MIDI input.
        Assert.Equal(FlSetup.NoPort, FlSetup.WhyNotAnswering(true, []));
        Assert.Equal(FlSetup.NoPort, FlSetup.WhyNotAnswering(true, ["Digitakt II", "loopMIDI Port"]));
        Assert.Equal(FlSetup.NotPicked, FlSetup.WhyNotAnswering(true, ["Digitakt II", "Music Organiser"]));
        Assert.Equal(FlSetup.NotPicked, FlSetup.WhyNotAnswering(true, ["music organiser (A)"]));
        // Where the app can't tell, the general message.
        Assert.Equal(FlLink.NotRunning, FlSetup.WhyNotAnswering(null, null));
        Assert.Equal(FlLink.NotRunning, FlSetup.WhyNotAnswering(true, null));
        Assert.Equal(FlLink.NotRunning, FlSetup.WhyNotAnswering(null, ["Music Organiser"]));
        // With Windows' MIDI system chosen, Install makes the port.
        Assert.Equal(FlSetup.NoWindowsPort, FlSetup.WhyNotAnswering(true, ["Digitakt II"], FlMidiPort.WindowsMidi));
        Assert.Equal(FlSetup.NotPicked, FlSetup.WhyNotAnswering(true, ["Music Organiser"], FlMidiPort.WindowsMidi));
        Assert.Equal(FlSetup.NotOpen, FlSetup.WhyNotAnswering(false, [], FlMidiPort.WindowsMidi));

        if (OperatingSystem.IsWindows()) Assert.NotNull(FlSetup.MidiInputs());
        else Assert.Null(FlSetup.MidiInputs());
        Assert.Equal(OperatingSystem.IsWindows(), FlSetup.IsLoopMidiInstalled() is not null);
    }

    [Fact]
    public void ReadsWhatWindowsMidiSystemSays()
    {
        static WindowsMidiResult Read(string output, string errors = "") => WindowsMidiPort.Read(output, errors);

        Assert.Equal(new WindowsMidiResult(true, WindowsMidiPort.Made, "made"), Read("""{"step":"made","saved":true,"message":""}"""));
        Assert.Equal(new WindowsMidiResult(false, WindowsMidiPort.Ready, "ready"), Read("""{"step":"ready"}"""));
        Assert.Equal(new WindowsMidiResult(false, WindowsMidiPort.NoApi, "no-api"), Read("""{"step":"no-api"}"""));
        Assert.Equal(new WindowsMidiResult(false, WindowsMidiPort.Legacy, "legacy"), Read("""{"step":"legacy"}"""));
        Assert.Equal(new WindowsMidiResult(false, WindowsMidiPort.NoLoopback, "no-loopback"), Read("""{"step":"no-loopback"}"""));
        Assert.Equal(new WindowsMidiResult(false, WindowsMidiPort.SavedNotShowing, "exists"), Read("""{"step":"exists"}"""));

        // Made, but not saved: there's a port until the PC restarts.
        var unsaved = Read("""{"message":"Access denied","saved":false,"step":"made"}""");
        Assert.True(unsaved.HasPort);
        Assert.Equal("Made a MIDI port called Music Organiser with Windows' MIDI system, but Windows couldn't save it, so it goes " +
                     "when the PC restarts, and Install has to make it again. Windows said: Access denied.", unsaved.Message);

        // Windows' own words, escaped by Windows PowerShell (an apostrophe as \u0027, other letters as the script escapes them).
        Assert.Equal(new WindowsMidiResult(false, "Windows' MIDI system couldn't make the port. Windows said: A loopback with " +
                                                  "that id can't be made twice. Choose loopMIDI instead.", "failed"),
                     Read("""{"message":"A loopback with that id can\u0027t be made twice.","step":"failed"}"""));
        Assert.Equal("Something went wrong making the port with Windows' MIDI system. Windows said: Zugriff verweigert (ü). " +
                     "Choose loopMIDI instead.", Read("""{"message":"Zugriff verweigert (\u00fc)","step":"error"}""").Message);
        Assert.Equal("error", Read("""{"step":"what-now"}""").Step);

        // PowerShell's other output comes before the answer, and only the answer counts.
        Assert.True(Read("WARNING: something\r\n{\"step\":\"made\",\"saved\":true}\r\n").HasPort);
        // No answer: whatever Windows PowerShell said instead, without its own bookkeeping.
        var none = Read("", "#< CLIXML\r\nThe term 'x' is not recognized\r\nAt line:1 char:1");
        Assert.Equal((false, "unreadable"), (none.HasPort, none.Step));
        Assert.Equal(WindowsMidiPort.NoAnswer + " It said: The term 'x' is not recognized. Choose loopMIDI instead.", none.Message);
        Assert.Equal("unreadable", Read("{not json", "").Step);
    }

    [Fact]
    public async Task AsksWindowsMidiSystemWithoutMakingAPort()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(new WindowsMidiResult(false, WindowsMidiPort.OnlyWindows, "not-windows"), await WindowsMidiPort.CheckAsync());
            Assert.Equal(new WindowsMidiResult(false, WindowsMidiPort.OnlyWindows, "not-windows"), await WindowsMidiPort.MakeAsync());
            return;
        }
        // On Windows, run the real script in Windows PowerShell, stopping before it would make anything: it has to
        // run, find Windows' API or say it isn't there, and answer in a way the app reads.
        var check = await WindowsMidiPort.RunScriptAsync(make: false);
        Assert.Contains(check.Step, new[] { "ready", "no-api", "legacy", "no-loopback", "exists" });
        Assert.False(check.HasPort);
    }

    [Fact]
    public void TheWindowsMidiScriptNamesBothEnds()
    {
        Assert.Contains("$port = 'Music Organiser'", WindowsMidiPort.Script);
        Assert.Contains($"$spare = '{WindowsMidiPort.SpareEnd}'", WindowsMidiPort.Script);
        // The spare end mustn't look like the port FL Studio wants.
        Assert.False(FlSetup.HasOurPort([WindowsMidiPort.SpareEnd]));
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
