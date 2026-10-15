using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Plugins;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Tests;

/// <summary>
/// Sending FL Studio channels and kits into the project open in Bitwig. The end-to-end tests run the real
/// script in the stand-in Bitwig (extensions/bitwig/test/serve.js; they need Node.js, and return early without it).
/// </summary>
public class BitwigSendTests
{
    const string SerumId = "805ECA49563E43EA8119369F0ED6096D";
    /// <summary>The stand-in's Drum Machine. Bitwig's own ID isn't known: the app learns it from Bitwig's files.</summary>
    static readonly Guid StandInDrumMachine = new("d7d7d7d7-0000-4000-8000-000000000001");

    static MidiChannel Channel(string name, string plugin = "", string sample = "", int root = 60, int cents = 0, int? index = null) =>
        new(name, plugin.Length > 0 ? plugin : Path.GetFileName(sample), 96,
            [new NoteEvent(0, 96, 60, 100), new NoteEvent(96, 96, 64, 100)], plugin, sample, root, cents) { Channel = index };

    [Fact]
    public async Task SendsChannelsIntoBitwig()
    {
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        if (bitwig is null) return;
        using var link = await BitwigLink.ConnectAsync(bitwig.Port);
        var dir = Fixtures.TempDir();
        var files = new BitwigSend.Files(Path.Combine(dir, "Night Drive.flp"), dir, 124, new Dictionary<string, BitwigPreset.Template>());

        // A sample whose root key is A4 in FL Studio (57) plays 3 semitones higher there than on C5, plus the pitch knob.
        var wav = Path.Combine(dir, "Pluck.wav");
        WavWriter.Write(wav, [new float[64]], 44100);
        var pluck = await BitwigSend.ChannelAsync(link, Channel("Pluck", sample: wav, root: 57, cents: 50), null, [], files);
        Assert.Equal(("Pluck", 3, "Sampler", true), (pluck.Name, pluck.Track, pluck.Instrument, pluck.Clip));
        Assert.Empty(pluck.Problems);
        Assert.True(File.Exists(Path.Combine(dir, "Night Drive", "MIDI", "Pluck.mid")));

        // A plugin with the settings the project saved, and its root key moved to E5: a Note Transpose goes before it.
        var state = new PresetBlob("Lead", "Serum", ".vstpreset", "VST3 state"u8.ToArray()) { Kind = PluginKind.Instrument, Channel = 2 };
        var lead = await BitwigSend.ChannelAsync(link, Channel("Lead", "Serum", root: 64, index: 2), state, [], files);
        Assert.Equal(("Serum with the project's settings", true), (lead.Instrument, lead.Clip));
        Assert.Empty(lead.Problems);

        // Without saved settings, an installed plugin loads by the ID the plugin scan read.
        var installed = new ScannedPlugin { Format = "VST3", Id = SerumId, Name = "Serum", Kind = "instrument" };
        var bass = await BitwigSend.ChannelAsync(link, Channel("Bass", "Serum"), null, [installed], files);
        Assert.Equal("Serum", bass.Instrument);
        Assert.Empty(bass.Problems);

        // What can't come across is said, and the notes still go in.
        var sytrus = new PresetBlob("Pad", "Sytrus", ".flstate", [1, 2, 3]) { Kind = PluginKind.Instrument, Native = true };
        var pad = await BitwigSend.ChannelAsync(link, Channel("Pad", "Sytrus"), sytrus, [], files);
        Assert.Equal(("", true), (pad.Instrument, pad.Clip));
        Assert.Equal(["Sytrus is one of FL Studio's own plugins, which Bitwig can't load"], pad.Problems);
        var diva = await BitwigSend.ChannelAsync(link, Channel("Keys", "Diva"), null, [], files);
        Assert.StartsWith("couldn't load Diva: the app hasn't found it", Assert.Single(diva.Problems));
        var gone = await BitwigSend.ChannelAsync(link, Channel("Vox", sample: Path.Combine(dir, "Vox.wav")), null, [], files);
        Assert.Equal($"its sample isn't at {Path.Combine(dir, "Vox.wav")}", Assert.Single(gone.Problems));

        var tracks = (await link.ProjectAsync()).Tracks;
        Assert.Equal(["Drums", "Kick", "Bass", "Pluck", "Lead", "Bass", "Pad", "Keys", "Vox", "Master"], tracks.Select(t => t.Name));
        Assert.Equal(["Sampler"], tracks[3].Devices.Select(d => d.Name));
        Assert.Equal([("Note Transpose", ""), ("Serum", "Lead")], tracks[4].Devices.Select(d => (d.Name, d.Preset)));
        Assert.Equal(["Serum"], tracks[5].Devices.Select(d => d.Name));
        Assert.Empty(tracks[6].Devices);
        // Each was read back from Bitwig afterwards.
        Assert.All([pluck, lead, bass, pad, diva, gone], c => Assert.True(c.Checked));
    }

    [Fact]
    public async Task CopiesFlStudiosKnobsWhenBitwigCantLoadTheSettings()
    {
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        if (bitwig is null) return;
        bitwig.Change("""{"presets": false}""");   // as if this Bitwig can't load the plugin's settings saved in the project
        using var link = await BitwigLink.ConnectAsync(bitwig.Port);
        var dir = Fixtures.TempDir();
        var files = new BitwigSend.Files(Path.Combine(dir, "Night Drive.flp"), dir, 124, new Dictionary<string, BitwigPreset.Template>());
        var state = new PresetBlob("Lead", "Serum", ".vstpreset", "VST3 state"u8.ToArray()) { Kind = PluginKind.Instrument, Channel = 2 };
        var installed = new ScannedPlugin { Format = "VST3", Id = SerumId, Name = "Serum", Kind = "instrument" };
        // Serum's knobs as FL Studio has them. A new Serum in Bitwig has every knob half way, and its Osc B Level doesn't move.
        FlParam[] knobs =
        [
            new(0, "Master Volume", 0.7, "-3.1 dB"), new(1, "A Octave", 0.5, "0"), new(2, "Osc B Level", 0.3, "30%"),
            new(3, "Filter cutoff", 0.25, "440 Hz"), new(4, "Noise Level", 0.1, "10%"),
        ];

        var lead = await BitwigSend.ChannelAsync(link, Channel("Lead", "Serum", index: 2), state, [installed], files, knobs);
        Assert.Equal(("Serum", true, true), (lead.Instrument, lead.Clip, lead.Checked));
        Assert.Equal((4, 3, 1), (lead.Knobs!.Matched, lead.Knobs.Changed, lead.Knobs.Unmatched));
        Assert.Equal(["Osc B Level"], lead.Knobs.NotTaken);
        Assert.Equal([
            "Bitwig didn't take the settings saved in the project, so its knobs were set to FL Studio's one by one: " +
            "anything that isn't a knob, like a wavetable or a sample, has its default",
            "1 of FL Studio's knobs has no knob of the same name in Bitwig, so it has its default",
            "1 knob didn't take FL Studio's value in Bitwig: “Osc B Level”",
        ], lead.Problems);

        // Bitwig shows the plugin with FL Studio's knobs.
        var shown = await link.SelectedDeviceAsync();
        Assert.Equal(("Lead", "Serum"), (shown.Track, shown.Device?.Name));
        Assert.Equal([("Master Volume", 0.7), ("A Octave", 0.5), ("Osc B Level", 0.5), ("Filter Cutoff", 0.25)],
                     shown.Params.Select(p => (p.Name, Math.Round(p.Value ?? -1, 3))));

        // Without FL Studio's knobs, the channel says how to get them.
        var bass = await BitwigSend.ChannelAsync(link, Channel("Bass", "Serum", index: 3), state with { Channel = 3 }, [installed], files);
        Assert.Null(bass.Knobs);
        Assert.Equal("Serum has its default sound: Bitwig didn't take the settings saved in the project. Open the project in FL Studio " +
                     "and send it again to copy its knobs across", Assert.Single(bass.Problems));
    }

    [Fact]
    public void MatchesKnobsByName()
    {
        static FlParam Fl(string name) => new(0, name, 0.5, "");
        static BitwigParam Bitwig(string id, string name) => new(id, name, 0.5, null);
        var pairs = BitwigSend.MatchKnobs(
            [Fl("Filter Cutoff"), Fl("LFO 1 Rate"), Fl("Mix"), Fl("Mix"), Fl("Mix"), Fl("Drive")],
            [Bitwig("a", "filter cutoff"), Bitwig("b", "LFO1 Rate"), Bitwig("c", "Mix"), Bitwig("d", "Mix"), Bitwig("e", "")]);
        // Case, spaces and punctuation aside; a name used twice pairs up in order; no knob for a third Mix or Drive.
        Assert.Equal([("Filter Cutoff", "a"), ("LFO 1 Rate", "b"), ("Mix", "c"), ("Mix", "d")], pairs.Select(p => (p.Fl.Name, p.Bitwig.Id)));
    }

    [Fact]
    public async Task PutsAKitOnADrumMachine()
    {
        using var bitwig = BitwigLinkTests.ScriptServer.Start(raw: false);
        if (bitwig is null) return;
        using var link = await BitwigLink.ConnectAsync(bitwig.Port);
        var pads = BitwigSend.KitPads([
            new("Kick", "Kick", @"C:\Kit\Kick.wav"), new("Snare", "Snare", @"C:\Kit\Snare.wav"),
            new("Closed hat", "Closed hat", @"C:\Kit\Hat.wav"), new("FX", "FX", @"C:\Kit\Riser.wav"),
        ]);

        var kit = await BitwigSend.KitAsync(link, "Kit #12", pads, StandInDrumMachine);
        Assert.True(kit.DrumMachine);
        Assert.Equal([36, 37, 38, 42], kit.Filled.Select(p => p.Note));
        Assert.Empty(kit.Skipped);
        var track = (await link.ProjectAsync()).Tracks[kit.Track!.Value];
        Assert.Equal(("Kit #12", "Drum Machine"), (track.Name, track.Devices.Single().Name));

        // Without the Drum Machine's ID, the kit goes on the one selected in Bitwig, leaving its sounds alone.
        var again = await BitwigSend.KitAsync(link, "Kit #13", [.. pads, new BitwigPad(49, @"C:\Kit\Crash.wav", "Cymbal")], null);
        Assert.Null(again.Track);
        Assert.Equal([49], again.Filled.Select(p => p.Note));
        Assert.Equal([36, 37, 38, 42], again.Skipped.Select(p => p.Note));

        // The link says what's on a pad it leaves alone.
        var kick = await link.FillPadsAsync([new BitwigPad(36, @"C:\Kit\Kick 2.wav")]);
        Assert.Equal([new BitwigPadContents(36, "Kick", 1, "Sampler")], kick.Taken!);
    }

    [Fact]
    public void PlacesAKitTheGeneralMidiWay()
    {
        var kit = KitBuilder.DefaultDrumKit().Select(s => new BitwigSend.KitSound(s.Name, s.Types[0], $"{s.Name}.wav")).ToList();
        Assert.Equal([
            (36, "Kick"), (37, "Percussion"), (38, "Snare"), (39, "Clap"), (40, "Percussion 2"), (41, "808 / Bass"),
            (42, "Closed hat"), (43, "FX"), (46, "Open hat"), (49, "Cymbal"),
        ], BitwigSend.KitPads(kit).Select(p => (p.Note, p.Name)));

        // A second kick can't share the first's note, so it takes the next free one.
        var two = BitwigSend.KitPads([new("Kick", "Kick", "a.wav"), new("Kick 2", "Kick", "b.wav"), new("Tom", "", "c.wav")]);
        Assert.Equal([(36, "Kick"), (37, "Kick 2"), (45, "Tom")], two.Select(p => (p.Note, p.Name)));
    }

    [Fact]
    public void FindsTheSettingsSavedForAChannel()
    {
        PresetBlob Saved(string name, string plugin, int? channel, PluginKind kind = PluginKind.Instrument) =>
            new(name, plugin, ".fxp", [0]) { Kind = kind, Channel = channel };
        var presets = new[] { Saved("Serum", "Serum", 1), Saved("Serum", "Serum", 4), Saved("Reverb", "Valhalla", null, PluginKind.Effect) };
        Assert.Same(presets[1], BitwigSend.PresetFor(Channel("Channel 5", "Serum", index: 4), presets));
        Assert.Null(BitwigSend.PresetFor(Channel("Channel 3", "Serum", index: 2), presets));

        // Saved settings that don't say their channel are matched by plugin and name.
        var unplaced = new[] { Saved("Lead", "Serum_x64", null), Saved("Pad", "Serum", null) };
        Assert.Same(unplaced[1], BitwigSend.PresetFor(Channel("Pad", "Serum", index: 3), unplaced));
        Assert.Same(unplaced[0], BitwigSend.PresetFor(Channel("Lead", "Serum"), unplaced));
    }

    [Fact]
    public void FindsBitwigDeviceIdsInItsFiles()
    {
        var dir = Fixtures.TempDir();
        var project = Path.Combine(dir, "Template.bwproject");
        File.WriteAllBytes(project, Fixtures.BitwigTemplate());
        var other = Path.Combine(dir, "Notes.bwpreset");
        File.WriteAllText(other, "Sampler, but not a Bitwig file");
        Assert.Equal(BitwigSend.Sampler, BitwigSend.FindDeviceId("Sampler", [other, Path.Combine(dir, "gone.bwpreset"), project]));
        Assert.Equal(BitwigSend.NoteTranspose, BitwigSend.FindDeviceId("Note Transpose", [project]));
        Assert.Null(BitwigSend.FindDeviceId(BitwigSend.DrumMachine, [project]));
    }

    [Fact]
    public async Task WontSendThroughAnOldScriptOrWithChangesOff()
    {
        foreach (var (hello, error) in new[]
        {
            ("""{"link":1,"product":"Bitwig Studio","version":"6.1","project":"X","changes":true}""", BitwigLink.OldScript),
            ("""{"link":2,"product":"Bitwig Studio","version":"6.1","project":"X","changes":false}""", BitwigSend.ChangesOff),
        })
        {
            using var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            var asked = new MemoryStream();
            var serving = Task.Run(async () =>
            {
                using var client = await server.AcceptTcpClientAsync();
                var body = Encoding.UTF8.GetBytes($$"""{"event":"hello","result":{{hello}}}""");
                var frame = new byte[4 + body.Length];
                BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
                body.CopyTo(frame, 4);
                await client.GetStream().WriteAsync(frame);
                await client.GetStream().CopyToAsync(asked);
            });
            using (var link = await BitwigLink.ConnectAsync(((IPEndPoint)server.LocalEndpoint).Port))
            {
                var files = new BitwigSend.Files("Song.flp", Fixtures.TempDir(), null, new Dictionary<string, BitwigPreset.Template>());
                var e = await Assert.ThrowsAsync<DawLinkException>(() => BitwigSend.ChannelAsync(link, Channel("Lead", "Serum"), null, [], files));
                Assert.Equal(error, e.Message);
                e = await Assert.ThrowsAsync<DawLinkException>(() => BitwigSend.KitAsync(link, "Kit", [new BitwigPad(36, "Kick.wav")], null));
                Assert.Equal(error, e.Message);
            }
            await serving;
            Assert.Equal(0, asked.Length);   // nothing was sent
        }
    }
}
