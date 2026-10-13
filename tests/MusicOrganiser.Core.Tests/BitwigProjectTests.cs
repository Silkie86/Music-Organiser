using System.IO.Compression;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Parsers;
using static MusicOrganiser.Core.Parsers.BitwigFormat;

namespace MusicOrganiser.Core.Tests;

public class BitwigProjectTests
{
    static readonly byte[] Md5 = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

    static BitwigProject.SampleFile? Snare(string path) =>
        path.EndsWith("Snare 01.wav") ? new BitwigProject.SampleFile(1234, Md5, 48000, 2, 1.5) : null;

    static BwObject Def(BwObject device) => device.Obj(0x197)!;
    static IEnumerable<BwObject> Chain(BwObject track) => track.Obj(0x164)!.List(0x144);
    static BwObject Param(BwObject device, string name) => Def(device).Obj(0xa4)!.List(0x20c).Single(p => p.Str(0x2b9) == name);
    static IEnumerable<BwObject> All(Document doc) => doc.Objects;

    [Fact]
    public void BuildsTracksFromTheTemplateWithSamplerRootKeysAndTransposedPlugins()
    {
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25Automation(Fixtures.TempDir()));
        var result = BitwigProject.FromFl(song, Fixtures.BitwigTemplate(), Snare);
        var doc = Decode(result.Bytes);
        Assert.Null(doc.BodyError);

        var group = doc.Root!.Obj(0x4dd)!;
        var tracks = group.List(0x4de).ToList();
        Assert.Equal(["Snare", "Serum"], tracks.Select(t => t.Str(0x15b)));
        Assert.Empty(group.List(0x4df));

        // The Sampler: its sample (with what Bitwig checks it by), root key A4 (57) and FL's -2 semitone pitch.
        var sampler = Assert.Single(Chain(tracks[0]));
        Assert.Equal("Snare 01", Def(sampler).Str(0x12de));
        Assert.Equal(-2.0, Param(sampler, "TRANSPOSE").Num(0x136));
        var zone = Param(sampler, "SAMPLE").Obj(0x74c)!;
        Assert.Equal(57, zone.Num(0x75c));
        Assert.Equal(1.5, zone.Num(0x761));
        var sample = zone.Obj(0x4112)!.Obj(0x748)!;
        Assert.Equal(("Snare 01.wav", 48000.0, 1.5, 2.0), (sample.Str(0x129f), sample.Num(0x94), sample.Num(0x95), sample.Num(0x96)));
        var file = sample.Obj(0x129e)!;
        Assert.Equal(@"C:\Samples\Snare 01.wav", file.Obj(0xd3b)!.Str(0x3b));
        Assert.Equal(1234, file.Num(0x17));
        var md5 = (BwBlob)file.Obj(0x513)![0x16]!;
        Assert.Equal(Md5, result.Bytes[md5.Offset..(md5.Offset + md5.Length)]);

        // Volume 100% and pan 25% on the mixer: Bitwig's fader holds the cube root of the gain, pan runs -1..1.
        var mixer = tracks[0].Obj(0x165)!;
        Assert.Equal(Math.Cbrt(DawProject.Gain(1.0)), mixer.Obj(0x1a4)!.Num(0x2c8)!.Value, 6);
        Assert.Equal(-0.5, mixer.Obj(0x1a5)!.Num(0x2c8)!.Value, 6);

        // Serum (VST2), root key E5: a Note Transpose of -4 semitones (an octave down, 8 up) in front of it.
        var (transpose, serum) = (Chain(tracks[1]).First(), Chain(tracks[1]).Last());
        Assert.Equal("Note Transpose", Def(transpose).Str(0x9a));
        Assert.Equal((2.0, 8.0), (Param(transpose, "OCTAVES").Num(0x273)!.Value, Param(transpose, "COARSE").Num(0x330)!.Value));
        var serumDef = Def(serum);
        Assert.Equal(("Serum", "Xfer Records"), (serumDef.Str(0x9a), serumDef.Str(0x9b)));
        Assert.Equal(song.Channels[1].Instrument!.DeviceId, serumDef[0xce4]!.ToString());
        Assert.Equal(@"C:\VstPlugins\Serum_x64.dll", serumDef.Obj(0xc5a)!.List(0xc5b).Single().Str(0x3b));
        Assert.Empty(serumDef.Obj(0x137c)!.List(0x8e0));   // Drumazon's outputs and their send don't come along
        using (var zip = doc.OpenZip()!)
        {
            Assert.Equal([$"plugin-states/{serumDef.Str(0xbe5)}"], zip.Entries.Select(e => e.FullName));
            using var state = zip.Entries[0].Open();
            var head = new byte[12];
            state.ReadExactly(head);
            Assert.Equal("FBCh", System.Text.Encoding.ASCII.GetString(head, 8, 4));
        }

        // Notes in one clip per track from the start of the song, in whole bars; the pattern is in bar 2.
        var clip = tracks[1].Obj(0x2bf8)!.Obj(0x2d8e)!.List(0x21f).Single();
        Assert.Equal(8.0, clip.Num(0x26));
        var lane = clip.Obj(0x2ae9)!.Obj(0x10e9)!.Obj(0x49c)!.List(0x18cb).Single();
        Assert.Equal(60, lane.Num(0xee));
        var note = lane.List(0x21f).Single();
        Assert.Equal((4.0, 1.0, song.Channels[1].Notes.Single().Velocity / 127.0), (note.Num(0x2af)!.Value, note.Num(0x26)!.Value, note.Num(0xef)!.Value));
        Assert.Same(clip.Obj(0x2ae9)!.Obj(0x10e9), tracks[1].Obj(0x2aed)!.List(0x10e6).Single());
        Assert.NotSame(lane, tracks[0].Obj(0x2bf8)!.Obj(0x2d8e)!.List(0x21f).Single().Obj(0x2ae9)!.Obj(0x10e9)!.Obj(0x49c)!.List(0x18cb).Single());

        // The template's own tracks, the Amp, the effect track and its sends are gone; the master stays.
        Assert.DoesNotContain(All(doc), o => o.Str(0x15b) == "FX 1" || o.Str(0x9a) is "Drumazon" or "Amp" or "Serum 2");
        Assert.DoesNotContain(All(doc), o => o.Str(0x2b9).StartsWith("TRACK_SEND(") || o.Str(0x2bd) == "AMOUNT");
        var master = group.Obj(0x4e0)!;
        Assert.Equal("Master", master.Str(0x15b));
        Assert.All(tracks, t => Assert.Same(master, t.Obj(0x897)!.Resolve(((KeyValuePair<object?, string>)t.Obj(0x897)![0x89b]!).Key)));
        Assert.Equal(2, tracks.Select(t => t[0xa51]).Distinct().Count());
        Assert.Equal(song.Tempo, All(doc).Single(o => o.Str(0x2bd) == "TEMPO").Num(0x2c8));

        // The snare's volume automation as a lane of points on its fader, in the fader's units; the
        // template's automation clip and points don't come along. Plugin settings aren't automated yet.
        var volumeLane = tracks[0].Obj(0x2dc9)!.List(0x4ba).Single();
        Assert.Same(mixer.Obj(0x1a4), volumeLane.Obj(0x2a3)!.Obj(0x41));
        Assert.Equal(("Mixer", "Volume"), (volumeLane.Obj(0x2a3)!.Str(0x133b), volumeLane.Obj(0x2a3)!.Str(0x133a)));
        Assert.Empty(volumeLane.Obj(0x2d8c)!.List(0x21f));
        var points = volumeLane.Obj(0x2d8c)!.Obj(0x35ac)!.List(0x21f).ToList();
        Assert.Equal(song.Automation[0].Points.Select(p => (p.Beat, Math.Cbrt(DawProject.Gain(p.Value)))),
                     points.Select(p => (p.Num(0x2af)!.Value, p.Num(0x28f)!.Value)));
        Assert.All(points, p => Assert.Equal((1.0, 0.0), (p.Num(0x359e)!.Value, p.Num(0xba6)!.Value)));
        Assert.Empty(tracks[1].Obj(0x2dc9)!.List(0x4ba));
        Assert.Equal(["Serum - Cutoff: automation left out (plugin settings aren't automated in Bitwig projects yet)"],
                     result.Notes.Where(n => n.Contains("automation")));

        // The browser's summary lists the new tracks.
        var bytes = (BwBlob)doc.Meta["structure"]!;
        var summary = Decode(result.Bytes[bytes.Offset..(bytes.Offset + bytes.Length)]);
        Assert.Equal(["Snare", "Serum"], summary.Root!.Obj(0xad2)!.List(0xace).Select(e => e.Str(0xad1)));
    }

    [Fact]
    public void PutsAnInsertsEffectsAfterTheInstrumentAndTheMasterInsertsOnTheMaster()
    {
        var keys = new FlPlugin("Keys", "Spectrasonics", "VST3", "565354536B657973", new PresetBlob("Keys", "Keys", ".vstpreset", [1, 2, 3]));
        var eq = new FlPlugin("Pro-Q 3", "FabFilter", "VST3", "72C4DB717A4D459AB97E51745D84B39D", new PresetBlob("Pro-Q 3", "Pro-Q 3", ".vstpreset", [4, 5]),
                              @"C:\Program Files\Common Files\VST3\FabFilter Pro-Q 3.vst3");
        var limiter = new FlPlugin("Pro-L 2", "FabFilter", "VST2", "1181700674", new PresetBlob("Pro-L 2", "Pro-L 2", ".fxp", Fxp.WrapChunk([6, 7], "Pro-L 2")));
        var reverb = new FlPlugin("Fruity Reeverb 2", "Image-Line", "FL", "", null);
        NoteEvent[] notes = [new(0, 96, 60, 100)];
        var song = new FlSong("", 96, 120,
            [new FlChannel(0, "Keys", keys, "", 3, notes), new FlChannel(1, "Pad", keys, "", 3, notes)],
            [new FlInsert(0, "", [limiter]), new FlInsert(3, "Keys bus", [eq, reverb])],
            []);

        var result = BitwigProject.FromFl(song, Fixtures.BitwigTemplate(), _ => null);
        var doc = Decode(result.Bytes);
        Assert.Null(doc.BodyError);
        var group = doc.Root!.Obj(0x4dd)!;
        foreach (var track in group.List(0x4de))
        {
            Assert.Equal(["Keys", "Pro-Q 3"], Chain(track).Select(d => Def(d).Str(0x9a)));
            var effect = Def(Chain(track).Last());
            Assert.Equal((0.0, "Audio FX", 0.0), (effect.Num(0x9d)!.Value, effect.Str(0x9c), effect.Num(0x1809)!.Value));
            Assert.Equal("72C4DB717A4D459AB97E51745D84B39D", effect.Str(0xce4));
            Assert.Equal("", effect.Str(0xc5e));   // not Serum 2's version
        }
        Assert.NotSame(Chain(group.List(0x4de).First()).Last(), Chain(group.List(0x4de).Last()).Last());
        Assert.Equal(["Pro-L 2"], Chain(group.Obj(0x4e0)!).Select(d => Def(d).Str(0x9a)));
        Assert.Equal(1181700674.0, Def(Chain(group.Obj(0x4e0)!).Single()).Num(0xce4));

        Assert.Contains(result.Notes, n => n.StartsWith("Keys: left out Fruity Reeverb 2 (FL Studio plugin)"));
        Assert.Contains(result.Notes, n => n.Contains("own copy of Keys bus's effects"));
        using var zip = doc.OpenZip()!;
        Assert.Equal(5, zip.Entries.Count);   // two synths, two equalisers, one limiter
    }

    [Fact]
    public void AutomatesVolumeAndPanWithOneLaneEachInSongOrder()
    {
        var keys = new FlPlugin("Keys", "Spectrasonics", "VST3", "565354536B657973", null);
        FlAutomation Clip(string name, int channel, FlControl control, params (double, double)[] points) =>
            new(name, channel, control, 0, points.Select(p => new FlPoint(p.Item1, p.Item2)).ToList());
        var song = new FlSong("", 96, 120,
            [new FlChannel(0, "Keys", keys, "", 0, [new(0, 96, 60, 100)])],
            [],
            [Clip("Fade", 0, FlControl.Volume, (0, 0.78125), (1, 0.1667), (1.5, 0.8798), (3.25, 0.1744)),
             Clip("Pan later", 0, FlControl.Pan, (8, 0.5), (12, 0)),
             Clip("Pan first", 0, FlControl.Pan, (0, 0), (4, 1)),
             Clip("Bend", 0, FlControl.Pitch, (0, 0.5)),
             Clip("Elsewhere", 7, FlControl.Volume, (0, 1))]);

        var result = BitwigProject.FromFl(song, Fixtures.BitwigTemplate(), _ => null);
        var doc = Decode(result.Bytes);
        Assert.Null(doc.BodyError);
        var track = doc.Root!.Obj(0x4dd)!.List(0x4de).Single();
        var lanes = track.Obj(0x2dc9)!.List(0x4ba).ToList();
        Assert.Equal(["Volume", "Pan"], lanes.Select(l => l.Obj(0x2a3)!.Str(0x133a)));
        Assert.Same(track.Obj(0x165)!.Obj(0x1a5), lanes[1].Obj(0x2a3)!.Obj(0x41));
        IEnumerable<(double, double)> Points(BwObject lane) =>
            lane.Obj(0x2d8c)!.Obj(0x35ac)!.List(0x21f).Select(p => (p.Num(0x2af)!.Value, Math.Round(p.Num(0x28f)!.Value, 4)));
        Assert.Equal([(0, 1.0), (1, 0.3571), (1.5, 1.0824), (3.25, 0.368)], Points(lanes[0]));   // FL's 78% fader is Bitwig's 0 dB
        Assert.Equal([(0, -1.0), (4, 1.0), (8, 0.0), (12, -1.0)], Points(lanes[1]));

        Assert.Equal(["Bend: automation left out (pitch has no equivalent here)",
                      "Elsewhere: automation left out (what it moves isn't converted)"],
                     result.Notes.Where(n => n.Contains("automation")));
    }

    [Fact]
    public void AutomationIsListedWhenTheTemplateHasNoLaneToCopy()
    {
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25Automation(Fixtures.TempDir()));
        var result = BitwigProject.FromFl(song, Fixtures.BitwigTemplate(automationLane: false), _ => null);
        Assert.Null(Decode(result.Bytes).BodyError);
        Assert.Contains("Snare - Channel volume: automation left out (the Bitwig template has no automation lane to copy)", result.Notes);
    }

    [Fact]
    public void ATemplateWithoutTheTracksItNeedsIsTurnedDown()
    {
        var song = new FlSong("", 96, 120, [], [], []);
        var notATemplate = File.ReadAllBytes(Fixtures.BitwigProject(Fixtures.TempDir()));
        var e = Assert.Throws<InvalidDataException>(() => BitwigProject.FromFl(song, notATemplate));
        Assert.StartsWith("The Bitwig template needs a Sampler track", e.Message);
    }

    [Fact]
    public void AnEmptySongStillWritesAProjectBitwigCanRead()
    {
        var doc = Decode(BitwigProject.FromFl(new FlSong("", 96, 140, [], [], []), Fixtures.BitwigTemplate()).Bytes);
        Assert.Null(doc.BodyError);
        Assert.Empty(doc.Root!.Obj(0x4dd)!.List(0x4de));
        Assert.Equal("Master", doc.Root.Obj(0x4dd)!.Obj(0x4e0)!.Str(0x15b));
        Assert.Null(doc.OpenZip());
    }
}
