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
        await link.RunActionAsync("Save");
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
    public async Task SaysWhenSomethingElseAnswers()
    {
        using var other = new TcpListener(IPAddress.Loopback, 0);
        other.Start();
        var accepting = other.AcceptTcpClientAsync();
        var e = await Assert.ThrowsAsync<DawLinkException>(() =>
            BitwigLink.ConnectAsync(((IPEndPoint)other.LocalEndpoint).Port, TimeSpan.FromMilliseconds(500)));
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

        public static ScriptServer? Start(bool raw)
        {
            var script = Fixtures.RepoFile(Path.Combine("extensions", "bitwig", "test", "serve.js"));
            if (script is null) return null;
            var psi = new ProcessStartInfo("node")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(script);
            if (raw) psi.ArgumentList.Add("--raw");
            Process node;
            try { node = Process.Start(psi)!; }
            catch (Win32Exception) { return null; }   // no Node.js here
            var line = node.StandardOutput.ReadLine() ?? "";
            if (!line.StartsWith("port ")) throw new InvalidOperationException($"serve.js said: {line}");
            return new ScriptServer(node, int.Parse(line[5..]));
        }

        /// <summary>Changes the stand-in as if done in Bitwig, e.g. {"project": "Other Song", "modified": true}.</summary>
        public void Change(string json)
        {
            _node.StandardInput.WriteLine(json);
            _node.StandardInput.Flush();
            var line = _node.StandardOutput.ReadLine();
            if (line != "ok") throw new InvalidOperationException($"serve.js said: {line}");
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
