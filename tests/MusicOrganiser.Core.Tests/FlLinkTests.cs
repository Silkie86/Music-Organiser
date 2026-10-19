using System.Collections.Concurrent;
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
        var link = new FlLink(fl);

        var hello = await link.HelloAsync();
        Assert.Equal(("FL Studio", 37, "Night Drive - FL Studio 2025", true), (hello.Product, hello.Api, hello.Title, hello.Changes));
        Assert.Equal("Producer Edition v25.1.6 [build 4997]", hello.Version);
        Assert.True(File.Exists(Path.Combine(hello.Script!, FlLink.Script)));

        // The project comes back in several messages.
        var sent = fl.Heard;
        var project = await link.ProjectAsync();
        Assert.True(fl.Heard - sent > 1);
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
    }

    [Fact]
    public async Task SaysWhenFlStudioIsntAnswering()
    {
        var port = new QuietPort();
        var link = new FlLink(port) { Timeout = TimeSpan.FromMilliseconds(300) };
        // Which part of the setup is missing, as far as this computer can tell (see the next test).
        var why = FlSetup.WhyNotAnswering();
        var e = await Assert.ThrowsAsync<DawLinkException>(() => link.HelloAsync());
        Assert.Equal(why, e.Message);
        var asked = FlMessages.Read(port.Sent.Single())!;
        Assert.Equal((FlMessages.Request, 0, 1), (asked.Kind, asked.Part, asked.Parts));

        // An answer to an earlier request, and other SysEx messages, aren't taken for this one's.
        port.Answer = id => [.. FlMessages.Write(FlMessages.Answer, (id + 1) % FlMessages.Ids, """{"id":0,"ok":true,"result":{}}"""),
                             [0xF0, 0x43, 0x10, 0x4C, 0x00, 0xF7]];
        e = await Assert.ThrowsAsync<DawLinkException>(() => link.HelloAsync());
        Assert.Equal(why, e.Message);

        // Part of an answer, and then nothing.
        port.Answer = id => [FlMessages.Write(FlMessages.Answer, id, $$$"""{"id":{{{id}}},"ok":true,"result":{"link":2,"pad":"{{{new string('x', 600)}}}"}}""")[0]];
        e = await Assert.ThrowsAsync<DawLinkException>(() => link.HelloAsync());
        Assert.Equal(FlLink.PartAnswer, e.Message);
    }

    [Fact]
    public async Task StopsWaitingAsSoonAsARequestIsCancelled()
    {
        var link = new FlLink(new QuietPort()) { Timeout = TimeSpan.FromSeconds(30) };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var waited = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => link.HelloAsync(cancel.Token));
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), $"It took {waited.Elapsed} to stop.");
    }

    [Fact]
    public void WritesAndReadsTheLinksMessages()
    {
        var text = "{\"cmd\":\"hello\",\"pad\":\"" + new string('a', 1100) + "\"}";
        var messages = FlMessages.Write(FlMessages.Request, 0x1FFFFF, text);
        Assert.Equal(3, messages.Count);
        Assert.All(messages, m => Assert.True(m[0] == 0xF0 && m[^1] == 0xF7 && m.Skip(1).SkipLast(1).All(b => b < 0x80)));
        Assert.Equal([0xF0, 0x7D, 0x4D, 0x4F, 1, 0x7F, 0x7F, 0x7F, 2, 0, 3, 0], messages[2][..12]);
        Assert.Equal(12 + FlMessages.PartBytes + 1, messages[0].Length);

        var answer = new FlAnswer(0x1FFFFF);
        Assert.Null(answer.Take(FlMessages.Read(messages[0])!));   // a request isn't an answer
        var parts = FlMessages.Write(FlMessages.Answer, 0x1FFFFF, text).Select(m => FlMessages.Read(m)!).ToList();
        Assert.Null(answer.Take(parts[2]));
        Assert.Null(answer.Take(parts[0]));
        Assert.True(answer.Started);
        Assert.Equal(text, answer.Take(parts[1]));

        // Someone else's SysEx, and broken ones, aren't the link's.
        Assert.Null(FlMessages.Read([0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF7]));
        Assert.Null(FlMessages.Read(messages[0][..^1]));
        Assert.Null(FlMessages.Read([.. messages[0][..20], 0x80, 0xF7]));
        Assert.Throws<ArgumentException>(() => FlMessages.Write(FlMessages.Request, 1, "Café"));
        Assert.Throws<ArgumentOutOfRangeException>(() => FlMessages.Write(FlMessages.Request, FlMessages.Ids, "{}"));
    }

    /// <summary>A MIDI port nothing answers on, unless told what to send back.</summary>
    internal sealed class QuietPort : IFlMidi
    {
        public List<byte[]> Sent { get; } = [];
        public Func<int, IEnumerable<byte[]>>? Answer { get; set; }
        public event Action<byte[]>? Received;

        public void Send(byte[] message)
        {
            Sent.Add(message);
            if (Answer is { } answer && FlMessages.Read(message) is { } m)
                foreach (var back in answer(m.Id)) Received?.Invoke(back);
        }
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
    public void NeverTakesTheBitwigLinksLoopbackPortForFlStudios()
    {
        // The Bitwig link's loopback port has Music Organiser in its name too, but it isn't FL Studio's: a Bitwig user with
        // only that port has no port for FL Studio.
        Assert.Equal("Music Organiser Bitwig", BitwigLink.LoopbackPort);
        Assert.False(FlSetup.HasOurPort([BitwigLink.LoopbackPort]));
        Assert.False(FlSetup.HasOurPort(["music organiser bitwig", "Digitakt II"]));
        Assert.Equal(FlSetup.NoPort, FlSetup.WhyNotAnswering(true, [BitwigLink.LoopbackPort]));
        Assert.Equal(FlSetup.NoWindowsPort, FlSetup.WhyNotAnswering(true, [BitwigLink.LoopbackPort], FlMidiPort.WindowsMidi));
        Assert.True(FlSetup.HasOurPort([BitwigLink.LoopbackPort, "Music Organiser"]));

        // With both, the app opens FL Studio's, whichever Windows lists first, in and out alike.
        Assert.Equal(1, FlSetup.FindPort(["Music Organiser Bitwig", "Music Organiser"], FlSetup.PortName));
        Assert.Equal(0, FlSetup.FindPort(["Music Organiser", "Music Organiser Bitwig"], FlSetup.PortName));
        Assert.Equal(2, FlSetup.FindPort(["Digitakt II", "Music Organiser Bitwig", "MUSIC ORGANISER"], FlSetup.PortName));
        // A name Windows added to still counts, the one called exactly that first.
        Assert.Equal(1, FlSetup.FindPort(["Music Organiser Bitwig", "Music Organiser (A)"], FlSetup.PortName));
        Assert.Equal(2, FlSetup.FindPort(["Music Organiser (A)", "Music Organiser Bitwig", "Music Organiser"], FlSetup.PortName));
        Assert.Equal(-1, FlSetup.FindPort(["Music Organiser Bitwig", "Digitakt II"], FlSetup.PortName));
        Assert.Equal(-1, FlSetup.FindPort([], FlSetup.PortName));
        // The spare end of the pair Windows' MIDI system makes, which the app talks through when there's one.
        Assert.Equal(1, FlSetup.FindPort(["Music Organiser", WindowsMidiPort.SpareEnd, "Music Organiser Bitwig"], WindowsMidiPort.SpareEnd));
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

    /// <summary>
    /// extensions/fl-studio/test/serve.py, run with Python: the FL Studio script in a stand-in FL Studio, in place of the
    /// Music Organiser MIDI port.
    /// </summary>
    internal sealed class ScriptRunner : IFlMidi, IDisposable
    {
        readonly Process _python;
        readonly BlockingCollection<string> _lines = [];
        readonly object _writing = new();
        int _heard;

        ScriptRunner(Process python)
        {
            _python = python;
            new Thread(Listen) { IsBackground = true }.Start();
        }

        public event Action<byte[]>? Received;

        /// <summary>How many messages the script has sent.</summary>
        public int Heard => Volatile.Read(ref _heard);

        public static ScriptRunner? Start()
        {
            var script = Fixtures.RepoFile(Path.Combine("extensions", "fl-studio", "test", "serve.py"));
            if (script is null) return null;
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
                psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
                Process p;
                try { p = Process.Start(psi)!; }
                catch (Win32Exception) { continue; }   // not installed under this name
                if (p.StandardOutput.ReadLine() == "ready") return new ScriptRunner(p);
                p.WaitForExit(10_000);
                var error = p.StandardError.ReadToEnd();
                p.Dispose();
                // A Python that runs the script and fails is a real failure; one that isn't really there (like
                // Windows' python3 that offers to install it from the Store) isn't.
                if (error.Contains("Traceback", StringComparison.Ordinal)) throw new InvalidOperationException($"serve.py failed:\n{error}");
            }
            return null;
        }

        /// <summary>A message into the script's MIDI input.</summary>
        public void Send(byte[] message) => Write("sysex " + Convert.ToHexString(message));

        /// <summary>
        /// Changes the stand-in as if done in FL Studio: {"title": "Other Song - FL Studio 2025", "modified": 1}, or
        /// {"knob": [channel, param, value]}.
        /// </summary>
        public void Change(string json)
        {
            Write(json);
            if (!_lines.TryTake(out var line, TimeSpan.FromSeconds(10))) throw new InvalidOperationException("serve.py didn't answer.");
            if (line != "ok") throw new InvalidOperationException($"serve.py said: {line}");
        }

        void Write(string line)
        {
            lock (_writing)
            {
                _python.StandardInput.WriteLine(line);
                _python.StandardInput.Flush();
            }
        }

        // What the script sends goes to whoever's listening; anything else is serve.py's answer to a change.
        void Listen()
        {
            while (_python.StandardOutput.ReadLine() is { } line)
            {
                if (!line.StartsWith("sysex ", StringComparison.Ordinal))
                {
                    _lines.Add(line);
                    continue;
                }
                Interlocked.Increment(ref _heard);
                Received?.Invoke(Convert.FromHexString(line.AsSpan(6)));
            }
            _lines.CompleteAdding();
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
        }
    }
}
