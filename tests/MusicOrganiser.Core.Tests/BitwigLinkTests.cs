using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MusicOrganiser.Core.DawLink;

namespace MusicOrganiser.Core.Tests;

/// <summary>
/// The app's side of the Bitwig link. The end-to-end tests talk to the real controller script, running
/// in the stand-in Bitwig from extensions/bitwig/test (they need Node.js, and return early without it).
/// </summary>
public class BitwigLinkTests
{
    [Fact]
    public async Task TalksToTheBitwigScript()
    {
        using var bitwig = ScriptServer.Start(raw: false);
        if (bitwig is null) return;
        await TalkTo(bitwig.Port);
    }

    [Fact]
    public async Task CopesWhenBitwigSendsWithoutLengths()
    {
        using var bitwig = ScriptServer.Start(raw: true);
        if (bitwig is null) return;
        await TalkTo(bitwig.Port);
    }

    static async Task TalkTo(int port)
    {
        using var link = await BitwigLink.ConnectAsync(port);
        Assert.Equal(("Bitwig Studio", "6.1.2", "Night Drive", true), (link.Hello.Product, link.Hello.Version, link.Hello.Project, link.Hello.Changes));
        // The stand-in runs the script; the extension says so in its hello.
        Assert.Equal("the Music Organiser script", link.Hello.Answering);
        Assert.Equal("the Music Organiser extension 3.0", (link.Hello with { Link = 3, Runtime = "java", Extension = "3.0" }).Answering);

        var project = await link.ProjectAsync();
        Assert.Equal(["Drums", "Kick", "Bass", "Master"], project.Tracks.Select(t => t.Name));
        Assert.Equal(124, project.Tempo);
        var bass = project.Tracks[2];
        Assert.Equal(("#ff8000", "-6.0 dB", 0.6), (bass.Color, bass.Volume, bass.VolumeValue ?? -1));
        Assert.Equal(["Serum", "Compressor"], bass.Devices.Select(d => d.Name));
        Assert.True(bass.Devices[0].Plugin);

        var selected = await link.SelectedDeviceAsync();
        Assert.Equal(("Bass", "Serum"), (selected.Track, selected.Device?.Name));
        Assert.Equal([("CONTENTS/PID0", "Master Volume", 0.7, "-3.1 dB"), ("CONTENTS/PID3e8", "Filter Cutoff", 0.25, "440 Hz")],
                     selected.Params.Take(2).Select(p => (p.Id, p.Name, p.Value ?? -1, p.Display)));
        Assert.Null(selected.Params[2].Value);   // Bitwig couldn't read it

        Assert.Equal("Save", (await link.ActionsAsync()).Single().Id);
        // The project in front as Save ran, and that the script ran it straight away.
        Assert.Equal(new BitwigActionRun("Night Drive", "direct", true), await link.RunActionAsync("Save"));
        Assert.Equal(new BitwigActionRun("Night Drive", "direct", true), await link.RunActionAsync("Save", "direct"));
        // The script runs actions straight away only: the other routes are the Java extension's.
        var refused = await Assert.ThrowsAsync<DawLinkException>(() => link.RunActionAsync("Save", "button"));
        Assert.Equal("This needs the Java extension 3.7.", refused.Message);
        await link.SetParameterAsync("CONTENTS/PID3e8", 0.5);
        await link.InsertPluginAsync("VST2", "MoB2");
        await link.InsertVst2Async(1234);
        await link.InsertPluginAsync("CLAP", "org.musicorganiser.probe.effect");
        await link.InsertFileAsync(@"C:\Samples\Café\Kick.wav");
        await link.SelectAsync(2, 1);

        // Bitwig's own words come back as the error.
        var e = await Assert.ThrowsAsync<DawLinkException>(() => link.SelectAsync(9));
        Assert.Equal("There's no track 9 in the project listing.", e.Message);
        e = await Assert.ThrowsAsync<DawLinkException>(() => link.SetParameterAsync("CONTENTS/PID999", 0.5));
        Assert.Contains("no parameter", e.Message);

        // Requests sent together each get their own answer.
        var answers = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => link.ProjectAsync()));
        Assert.All(answers, a => Assert.Equal(4, a.Tracks.Count));
    }

    [Fact]
    public async Task RunsAnActionThroughALinkThatDoesntSayWhichProjectWasInFront()
    {
        // An extension before 3.7 answers an action with {}, and one that couldn't read the name leaves it out too: it ran the
        // action straight away, the only way it has.
        await using (var older = new FakeJavaBitwig(answers: new Dictionary<string, string> { ["action"] = "{}" }))
        {
            using var link = await BitwigLink.ConnectAsync(older.Port);
            Assert.Equal(new BitwigActionRun(null, "direct", true), await link.RunActionAsync("Save"));
            Assert.Equal("Save", (string?)older.Requests.Last()["action"]);
            // Without a route, the request carries none, so an older link isn't sent a field it doesn't know.
            Assert.False(older.Requests.Last().AsObject().ContainsKey("via"));
        }
        await using (var newer = new FakeJavaBitwig(answers: new Dictionary<string, string> { ["action"] = """{"project":"MO Live Test"}""" }))
        {
            using var link = await BitwigLink.ConnectAsync(newer.Port);
            Assert.Equal(new BitwigActionRun("MO Live Test", "direct", true), await link.RunActionAsync("Save"));
        }
    }

    [Fact]
    public async Task RunsAnActionByTheRouteAsked()
    {
        // The extension 3.7 says the route, and whether it ran the action by it.
        await using var java = new FakeJavaBitwig(answers: new Dictionary<string, string>
        {
            ["action"] = """{"project":"MO Live Test","via":"midi","ran":false}""",
        });
        using var link = await BitwigLink.ConnectAsync(java.Port);
        Assert.Equal(new BitwigActionRun("MO Live Test", "midi", false), await link.RunActionAsync("Save", "midi"));
        Assert.Equal(("Save", "midi"), ((string?)java.Requests.Last()["action"], (string?)java.Requests.Last()["via"]));

        // Arrange takes the same routes, and says whether each step ran.
        java.Answer = request => (string)request["cmd"]! == "arrange"
            ? """{"arranged":false,"appeared":false,"sameAsSource":false,"copiedFromTimeline":false,"at":16,"layout":"ARRANGE","launcher":{"before":true,"after":true},"mark":{"before":"#3366CC","asked":"#416DD9","got":"#416DD9","took":true,"exact":true},"launcherFocus":0,"timeline":{"before":true,"after":true},"focus":0,"before":{"exists":false,"track":null},"beforePaste":{"exists":false,"track":null},"after":{"exists":false,"track":null},"pastedColor":null,"launcherClipsBefore":1,"launcherClipsAfter":1,"via":"button","ran":{"launcherFocus":false,"copy":false,"focus":null,"paste":null}}"""
            : null;
        var arranged = await link.ArrangeAsync(0, 0, 16, "button");
        Assert.Equal("button", (string?)java.Requests.Last()["via"]);
        Assert.Equal(("button", new BitwigArrangeRan(false, false, null, null)), (arranged.Via, arranged.Ran));
        Assert.Null(arranged.Checks);
    }

    [Fact]
    public async Task AsksForArrangesChecksAndReadsThem()
    {
        // The extension 3.8 checks a paste into an empty launcher slot and Bitwig's Duplicate when asked, after the clip
        // didn't land in the arranger.
        await using var java = new FakeJavaBitwig();
        java.Answer = request => (string)request["cmd"]! == "arrange"
            ? """{"arranged":false,"appeared":false,"sameAsSource":false,"copiedFromTimeline":false,"at":16,"layout":"ARRANGE","launcher":{"before":true,"after":true},"mark":{"before":"#3366CC","asked":"#416DD9","got":"#416DD9","took":true,"exact":true},"launcherFocus":1,"timeline":{"before":true,"after":true},"focus":1,"before":{"exists":false,"track":null},"beforePaste":{"exists":false,"track":null},"after":{"exists":false,"track":null},"pastedColor":null,"launcherClipsBefore":1,"launcherClipsAfter":1,"via":"flush","ran":{"launcherFocus":true,"copy":true,"focus":true,"paste":true},"checks":{"launcherPaste":{"slot":2,"selected":true,"focus":1,"ran":{"focus":true,"paste":true},"made":true,"colour":"#416DD9","sameAsMark":true,"removed":true},"duplicate":{"selected":true,"focus":1,"ran":{"focus":true,"duplicate":null},"launcher":0,"tracks":0,"removed":false},"error":"Bitwig has “My Song” open now."}}"""
            : null;
        using var link = await BitwigLink.ConnectAsync(java.Port);

        var arranged = await link.ArrangeAsync(0, 0, 16, checks: true);

        var sent = java.Requests.Last().AsObject();
        Assert.True((bool)sent["checks"]!);
        Assert.False(sent.ContainsKey("via"));
        Assert.Equal(new BitwigLauncherPasteCheck(2, true, 1, new(true, true), true, "#416DD9", true, true), arranged.Checks!.LauncherPaste);
        Assert.Equal(new BitwigDuplicateCheck(true, 1, new(true, null), 0, 0, false), arranged.Checks.Duplicate);
        Assert.Equal("Bitwig has “My Song” open now.", arranged.Checks.Error);

        // Not asked, the request carries no checks.
        await link.ArrangeAsync(0, 0, 16);
        Assert.False(java.Requests.Last().AsObject().ContainsKey("checks"));
    }

    [Fact]
    public async Task InsertsAFileAsATrackInTheProjectGiven()
    {
        await using var java = new FakeJavaBitwig(answers: new Dictionary<string, string>
        {
            ["filetrack"] = """{"added":1,"index":3,"name":"Live Clip","type":"Instrument","tracksBefore":4,"tracksAfter":5,"launcherClips":0,"selected":true,"arranger":{"exists":true,"track":"Live Clip"},"arrangerBefore":{"exists":false,"track":null},"at":16,"onTrack":{"before":1,"after":1}}""",
        });
        using var link = await BitwigLink.ConnectAsync(java.Port);
        link.OnlyIn = "MO Live Test";

        var placed = await link.FileTrackAsync(2, @"C:\Tests\Live Clip.mid", 16);

        var sent = java.Requests.Last();
        Assert.Equal(("filetrack", 2, @"C:\Tests\Live Clip.mid", 16.0, "MO Live Test"),
                     ((string)sent["cmd"]!, (int)sent["track"]!, (string)sent["path"]!, (double)sent["at"]!, (string?)sent["onlyIn"]));
        Assert.Equal(new BitwigFileTrack(1, 3, "Live Clip", "Instrument", 4, 5, 0, true, new(true, "Live Clip"), new(false, null), 16, new(1, 1)),
                     placed);
    }

    [Fact]
    public async Task ReadsWhetherTheLoopbackWorksFromTheHello()
    {
        await using (var java = new FakeJavaBitwig(FakeJavaBitwig.Hello("""{"extension":"3.7","loopback":true}""")))
        {
            using var link = await BitwigLink.ConnectAsync(java.Port);
            Assert.True(link.Hello.Loopback);
        }
        // An older link doesn't say.
        await using (var older = new FakeJavaBitwig())
        {
            using var link = await BitwigLink.ConnectAsync(older.Port);
            Assert.Null(link.Hello.Loopback);
        }
    }

    [Fact]
    public void SaysToSetTheMidiPortsWhenTheLoopbackDoesntWork()
    {
        var hello = new BitwigHello(3, "Bitwig Studio", "6.1.3", 23, "WINDOWS", 19791, "MO Live Test", false, true, true, "java", "3.8", true,
                                    Loopback: false);
        var setUp = BitwigLink.LoopbackMissing(hello)!;
        Assert.StartsWith("The extension's own MIDI hasn't come back to it through the MIDI port “Music Organiser Bitwig”, so its MIDI in and out " +
                          "may not be set to that port yet (or the port isn't working), and Bitwig has only saved for the app once they were: ", setUp);
        Assert.EndsWith("in Bitwig, Settings > Controllers > Music Organiser, set both MIDI in and MIDI out to it (never to FL Studio's port, " +
                        "“Music Organiser”).", setUp);
        Assert.Equal(OperatingSystem.IsWindows(), setUp.Contains("in loopMIDI"));
        // It works, or the link doesn't say (the script, or an extension before 3.7).
        Assert.Null(BitwigLink.LoopbackMissing(hello with { Loopback = true }));
        Assert.Null(BitwigLink.LoopbackMissing(hello with { Loopback = null }));
    }

    [Fact]
    public async Task SaysWhenBitwigIsntListening()
    {
        var unused = new TcpListener(IPAddress.Loopback, 0);
        unused.Start();
        var port = ((IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();
        var e = await Assert.ThrowsAsync<DawLinkException>(() => BitwigLink.ConnectAsync(port, TimeSpan.FromSeconds(2)));
        Assert.Equal(BitwigLink.NotRunning, e.Message);
    }

    [Fact]
    public async Task KeepsChangesInTheProjectTheyWereMeantFor()
    {
        using var bitwig = ScriptServer.Start(raw: false);
        if (bitwig is null) return;
        bitwig.Change("""{"project": "MO Live Test"}""");
        using var link = await BitwigLink.ConnectAsync(bitwig.Port);
        Assert.True(link.Hello.OnlyIn);
        link.OnlyIn = "MO Live Test";
        await link.NewTrackAsync("Kept");
        const string Elsewhere = "Bitwig has \"Night Drive\" open, not \"MO Live Test\", so the link stopped before changing anything there.";

        // Another project in front: refused before anything is done.
        bitwig.Change("""{"project": "Night Drive"}""");
        Assert.Equal(Elsewhere, (await Assert.ThrowsAsync<DawLinkException>(() => link.NewTrackAsync("Stray"))).Message);
        Assert.Equal(Elsewhere, (await Assert.ThrowsAsync<DawLinkException>(() => link.SelectAsync(0))).Message);

        // Another project comes to the front while Bitwig makes the track: the link stops before naming it.
        bitwig.Change("""{"project": "MO Live Test"}""");
        bitwig.Change("""{"switchDuring": "newtrack", "project": "Night Drive"}""");
        Assert.Equal(Elsewhere, (await Assert.ThrowsAsync<DawLinkException>(() => link.NewTrackAsync("Half"))).Message);

        var names = (await link.ProjectAsync()).Tracks.Select(t => t.Name).ToList();
        Assert.Contains("Kept", names);
        Assert.DoesNotContain("Stray", names);
        Assert.DoesNotContain("Half", names);
    }

    [Fact]
    public async Task SaysWhenSomethingElseAnswers()
    {
        using var other = new TcpListener(IPAddress.Loopback, 0);
        other.Start();
        var accepting = other.AcceptTcpClientAsync();
        // Long enough to connect while the other tests keep the machine busy; then it waits that long for a hello that never comes.
        var e = await Assert.ThrowsAsync<DawLinkException>(() =>
            BitwigLink.ConnectAsync(((IPEndPoint)other.LocalEndpoint).Port, TimeSpan.FromSeconds(2)));
        Assert.Contains("not the Music Organiser script", e.Message);
        (await accepting).Dispose();
    }

    [Fact]
    public async Task NoticesWhenBitwigGoesAway()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var serving = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync();
            var hello = """{"event":"hello","result":{"link":1,"product":"Bitwig Studio","version":"6.1","project":"X"}}"""u8.ToArray();
            var frame = new byte[4 + hello.Length];
            BinaryPrimitives.WriteInt32BigEndian(frame, hello.Length);
            hello.CopyTo(frame, 4);
            await client.GetStream().WriteAsync(frame);
            // Read the one request, then close without answering, as when Bitwig quits.
            await client.GetStream().ReadAtLeastAsync(new byte[256], 1, throwOnEndOfStream: false);
        });
        using var link = await BitwigLink.ConnectAsync(((IPEndPoint)server.LocalEndpoint).Port);
        Assert.Equal("X", link.Hello.Project);
        var e = await Assert.ThrowsAsync<DawLinkException>(() => link.ProjectAsync());
        Assert.Equal("Bitwig closed the connection.", e.Message);
        await serving;
        Assert.False(link.IsOpen);
    }

    [Fact]
    public void ReadsVst2Ids()
    {
        Assert.Equal(1299137074, BitwigLink.Vst2Id("MoB2"));
        Assert.Equal(825373492, BitwigLink.Vst2Id("1234"));   // four printable characters are letters, as the scanner writes them
        Assert.Equal(12345, BitwigLink.Vst2Id("12345"));
        Assert.Equal(-1294, BitwigLink.Vst2Id("-1294"));
        Assert.Null(BitwigLink.Vst2Id("Serum"));
    }

    [Fact]
    public void ComparesTheFileWithWhatBitwigShows()
    {
        static BitwigTrack Track(string name, string type = "Instrument", params string[] devices) =>
            new(0, name, type, false, "#000000", true, false, false, false, "0.0 dB", 0.8, "C", 0.5, devices.Length,
                devices.Select((d, i) => new BitwigDevice(i, d, "", "", false, true)).ToList());
        var live = new BitwigProject("Night Drive", false, 124, "4/4", false, 0, 4,
            [Track("Drums", "Group"), Track("Bass", devices: ["Serum", "EQ+"]), Track("Pad", devices: ["Diva"]), Track("Master", "Master", "Peak Limiter")]);
        const string path = @"C:\Music\Night Drive.bwproject";

        var same = BitwigCheck.Compare(path, 124, ["Drums", "Bass", "Pad"], ["Serum", "EQ+", "Diva", "Peak Limiter", "Delay+"], live);
        Assert.Equal([CheckResult.Same, CheckResult.Same, CheckResult.Same, CheckResult.Note], same.Select(l => l.Result));
        Assert.Equal("Tracks: 3 tracks, named the same in both.", same[1].Text);
        Assert.Contains("“Delay+”", same[3].Text);   // inside a container, as far as the link can tell

        var off = BitwigCheck.Compare(path, 120, ["Drums", "Reese", "Pad"], ["serum", "Diva", "Peak Limiter"], live);
        Assert.Equal("Tempo: the app read 120 BPM, Bitwig says 124 BPM.", off[0].Text);
        Assert.Equal("Tracks Bitwig shows that the app didn't read: “Bass”.", off[1].Text);
        Assert.Equal("Tracks the app read that Bitwig doesn't show: “Reese”.", off[2].Text);
        Assert.Equal("Devices Bitwig shows that the app didn't find: “EQ+” on Bass.", off[3].Text);

        var order = BitwigCheck.Compare(path, null, ["Bass", "Drums", "Pad"], [], live);
        Assert.StartsWith("Tracks: the same 3 tracks, but in a different order.", order[0].Text);
        Assert.DoesNotContain(order, l => l.Text.Contains("unsaved"));
        var unsaved = BitwigCheck.Compare(path, null, ["Bass", "Drums", "Pad"], [], live with { Modified = true });
        Assert.Contains("unsaved changes", unsaved[^1].Text);

        var other = BitwigCheck.Compare(@"C:\Music\Another Song.bwproject", 124, [], [], live);
        Assert.Equal(CheckResult.Different, other.Single().Result);
        Assert.Contains("Bitwig has “Night Drive” open", other[0].Text);
    }

    /// <summary>extensions/bitwig/test/serve.js, run with Node.js.</summary>
    internal sealed class ScriptServer : IDisposable
    {
        readonly Process _node;
        public int Port { get; }

        ScriptServer(Process node, int port)
        {
            _node = node;
            Port = port;
        }

        /// <param name="events">Hears what the stand-in says happened (serve.js --events): an MO Probe added, one of its knobs set.</param>
        public static ScriptServer? Start(bool raw, Action<string>? events = null)
        {
            var script = Fixtures.RepoFile(Path.Combine("extensions", "bitwig", "test", "serve.js"));
            if (script is null) return null;
            var psi = new ProcessStartInfo("node")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = events is not null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(script);
            if (raw) psi.ArgumentList.Add("--raw");
            if (events is not null) psi.ArgumentList.Add("--events");
            Process node;
            try { node = Process.Start(psi)!; }
            catch (Win32Exception) { return null; }   // no Node.js here
            if (events is not null)
            {
                node.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is { Length: > 0 } line) events(line);
                };
                node.BeginErrorReadLine();
            }
            var line = node.StandardOutput.ReadLine() ?? "";
            if (!line.StartsWith("port ")) throw new InvalidOperationException($"serve.js said: {line}");
            return new ScriptServer(node, int.Parse(line[5..]));
        }

        /// <summary>Changes the stand-in as if done in Bitwig, e.g. {"project": "Other Song", "modified": true}.</summary>
        public void Change(string json)
        {
            // A stand-in probe turns knobs from its own thread.
            lock (_node)
            {
                _node.StandardInput.WriteLine(json);
                _node.StandardInput.Flush();
                var line = _node.StandardOutput.ReadLine();
                if (line != "ok") throw new InvalidOperationException($"serve.js said: {line}");
            }
        }

        /// <summary>Stops the stand-in, as if Bitwig closed. Safe to call again.</summary>
        public void Dispose()
        {
            if (_stopped) return;
            _stopped = true;
            try
            {
                _node.StandardInput.Close();
                if (!_node.WaitForExit(5000)) _node.Kill(true);
            }
            catch (InvalidOperationException) { }
            _node.Dispose();
        }

        bool _stopped;
    }
}
