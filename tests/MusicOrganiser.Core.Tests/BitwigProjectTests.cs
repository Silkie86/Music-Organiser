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
    static int Semitones(BwObject transpose) => (sbyte)(byte)Param(transpose, "COARSE").Num(0x330)!.Value;
    static List<BwObject> Clips(BwObject track) => track.Obj(0x2bf8)!.Obj(0x2d8e)!.List(0x21f).ToList();
    static IEnumerable<BwObject> Notes(BwObject clip) =>
        clip.Obj(0x2ae9)!.Obj(0x10e9)!.Obj(0x49c)!.List(0x18cb).SelectMany(l => l.List(0x21f)).OrderBy(n => n.Num(0x2af));

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

        // Serum (VST2), root key E5: a Note Transpose of -4 semitones in front of it (octaves ±0, stored as 3).
        var (transpose, serum) = (Chain(tracks[1]).First(), Chain(tracks[1]).Last());
        Assert.Equal("Note Transpose", Def(transpose).Str(0x9a));
        Assert.Equal((3.0, -4), (Param(transpose, "OCTAVES").Num(0x273)!.Value, Semitones(transpose)));
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

        // A clip where the pattern sits in the playlist (bar 2, one bar long), its notes timed from the clip's start.
        var clip = tracks[1].Obj(0x2bf8)!.Obj(0x2d8e)!.List(0x21f).Single();
        Assert.Equal((4.0, 4.0), (clip.Num(0x2af), clip.Num(0x26)));
        var lane = clip.Obj(0x2ae9)!.Obj(0x10e9)!.Obj(0x49c)!.List(0x18cb).Single();
        Assert.Equal(60, lane.Num(0xee));
        var note = lane.List(0x21f).Single();
        Assert.Equal((0.0, 1.0, song.Channels[1].Notes.Single().Velocity / 127.0), (note.Num(0x2af)!.Value, note.Num(0x26)!.Value, note.Num(0xef)!.Value));
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
        // template's automation clips and points don't come along.
        var volumeLane = tracks[0].Obj(0x2dc9)!.List(0x4ba).Single();
        Assert.Same(mixer.Obj(0x1a4), volumeLane.Obj(0x2a3)!.Obj(0x41));
        Assert.Equal(("Mixer", "Volume"), (volumeLane.Obj(0x2a3)!.Str(0x133b), volumeLane.Obj(0x2a3)!.Str(0x133a)));
        Assert.Empty(volumeLane.Obj(0x2d8c)!.List(0x21f));
        var points = volumeLane.Obj(0x2d8c)!.Obj(0x35ac)!.List(0x21f).ToList();
        Assert.Equal(song.Automation[0].Points.Select(p => (p.Beat, Math.Cbrt(DawProject.Gain(p.Value)))),
                     points.Select(p => (p.Num(0x2af)!.Value, p.Num(0x28f)!.Value)));
        Assert.All(points, p => Assert.Equal((1.0, 0.0), (p.Num(0x359e)!.Value, p.Num(0xba6)!.Value)));

        // Serum's cutoff (VST2 setting 205) on a lane for that setting of the plugin, which lists it as automated.
        var cutoffLane = tracks[1].Obj(0x2dc9)!.List(0x4ba).Single();
        var target = cutoffLane.Obj(0x2a3)!;
        Assert.Same(serum, target.Obj(0xec));
        Assert.Equal(("CONTENTS/ROOT_GENERIC_MODULE/PARAM205", "Serum", "Cutoff"), (target.Str(0xed), target.Str(0x133b), target.Str(0x133a)));
        Assert.Equal([(0.0, 0.2), (2.0, 1.0), (4.0, 0.5)],
                     cutoffLane.Obj(0x2d8c)!.Obj(0x35ac)!.List(0x21f).Select(p => (p.Num(0x2af)!.Value, p.Num(0x28f)!.Value)));
        Assert.Empty(cutoffLane.Obj(0x2d8c)!.List(0x21f));
        var listed = serumDef.List(0x1318).Single();
        Assert.Equal((205.0, 0.2), (listed.Num(0x1319)!.Value, listed.Num(0x131a)!.Value));
        Assert.DoesNotContain(result.Notes, n => n.Contains("automation"));

        // The browser's summary lists the new tracks.
        var bytes = (BwBlob)doc.Meta["structure"]!;
        var summary = Decode(result.Bytes[bytes.Offset..(bytes.Offset + bytes.Length)]);
        Assert.Equal(["Snare", "Serum"], summary.Root!.Obj(0xad2)!.List(0xace).Select(e => e.Str(0xad1)));
    }

    [Fact]
    public void SerumCanBeSwappedForTheTemplatesSerum2AtItsDefaultSound()
    {
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25Automation(Fixtures.TempDir()));
        var serum1 = song.Channels[1].Instrument!;
        var result = BitwigProject.FromFl(song, Fixtures.BitwigTemplate(), Snare, upgrade: true);
        var doc = Decode(result.Bytes);
        Assert.Null(doc.BodyError);

        // The template's Serum 2, as it is (VST3, its id and its file), on the Serum channel's track.
        var track = doc.Root!.Obj(0x4dd)!.List(0x4de).Single(t => t.Str(0x15b) == "Serum");
        var serum2 = Def(Chain(track).Last());
        Assert.Equal(("Serum 2", "56534558667350736572756D20320000"), (serum2.Str(0x9a), serum2[0xce4]));

        // No state handed to it: Serum 2 refuses Serum's sound as its own and then doesn't load at all.
        Assert.Equal("", serum2.Str(0xbe5));
        Assert.DoesNotContain(result.Notes, n => n.Contains("opens at its defaults"));

        // Serum 1's settings are numbered differently from Serum 2's, so its automation is left out and says why.
        Assert.Empty(track.Obj(0x2dc9)!.List(0x4ba));
        Assert.Contains(result.Notes, n => n.StartsWith("Serum - Cutoff: automation left out") && n.Contains("newer version"));
        Assert.Contains(result.Notes, n => n.StartsWith("Serum: Serum swapped for Serum 2, which opens with its default sound") &&
                                           n.Contains("\"Serum (Serum)\""));
        var (channel, sound) = Assert.Single(result.Upgraded!);
        Assert.Equal(("Serum", serum1.State!.Data), (channel, sound.Data));
    }

    [Fact]
    public void ConvertingWithTheSwapSavesTheOldSoundsNextToTheProject()
    {
        var dir = Fixtures.TempDir();
        var project = Fixtures.FlStudio25Automation(dir);
        var (file, _) = Exporter.BitwigFromFl(project, Path.Combine(dir, "Exports"), Fixtures.BitwigTemplate(), upgrade: true);
        var saved = Assert.Single(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(file)!, BitwigProject.UpgradedFolder)));
        Assert.Equal("Serum (Serum).fxp", Path.GetFileName(saved));
        Assert.Equal("CcnK", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(saved), 0, 4));

        // Without it, Serum stays Serum and nothing extra is saved.
        var (plain, _) = Exporter.BitwigFromFl(project, Path.Combine(dir, "Plain"), Fixtures.BitwigTemplate());
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(plain)!, BitwigProject.UpgradedFolder)));
    }

    [Fact]
    public void ANewConvertGetsAFreshNameWhileBitwigMayHaveTheOldOneOpen()
    {
        // Bitwig only brings a project it has open forward, so a convert saved over it would look unchanged.
        Assert.Equal("civil_18", Exporter.BitwigName("civil_18.flp", _ => false));
        Assert.Equal("civil_18 (2)", Exporter.BitwigName("civil_18.flp", n => n == "civil_18"));
        Assert.Equal("civil_18 (3)", Exporter.BitwigName("civil_18.flp", n => n is "civil_18" or "civil_18 (2)"));
        // Only the names it may have open count: a later number that's closed is used again.
        Assert.Equal("civil_18", Exporter.BitwigName("civil_18.flp", n => n == "civil_18 (2)"));

        var dir = Fixtures.TempDir();
        var project = Fixtures.FlStudio25Automation(dir);
        var name = Path.GetFileNameWithoutExtension(project);
        var exports = Path.Combine(dir, "Exports");
        var (first, _) = Exporter.BitwigFromFl(project, exports, Fixtures.BitwigTemplate());
        var (second, _) = Exporter.BitwigFromFl(project, exports, Fixtures.BitwigTemplate(), saveAs: $"{name} (2)");
        // Each in its own folder named like it, the layout Bitwig opens without asking, next to the first.
        Assert.Equal(Path.Combine(exports, name, "Bitwig", $"{name} (2)", $"{name} (2).bwproject"), second);
        Assert.True(File.Exists(first));
        Assert.Equal(Path.GetDirectoryName(Path.GetDirectoryName(first)), Path.GetDirectoryName(Path.GetDirectoryName(second)));
    }

    [Fact]
    public void KeepsMutedNotesAndClipsMutedAndPlaysStepsUntilTheirSoundEnds()
    {
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25MutesAndSteps(Fixtures.TempDir()));
        // The kick's sample lasts 0.25 s: 0.58 beats at 140 bpm.
        var result = BitwigProject.FromFl(song, Fixtures.BitwigTemplate(),
            p => p.EndsWith("Kick 01.wav") ? new BitwigProject.SampleFile(1234, Md5, 48000, 2, 0.25) : Snare(p));
        var doc = Decode(result.Bytes);
        Assert.Null(doc.BodyError);
        Assert.Equal(["Kick: its volume envelope (ADSR) isn't carried over yet, so it plays without one"], result.Notes);
        var tracks = doc.Root!.Obj(0x4dd)!.List(0x4de).ToList();

        // One clip per placement: the pattern in bar 1, then muted in bar 2; the snare's second note muted in both.
        // (Unmuted is no mute field where the template has none.)
        var snare = Clips(tracks[0]);
        Assert.Equal([(0.0, 4.0, 0.0), (4.0, 4.0, 1.0)], snare.Select(c => (c.Num(0x2af)!.Value, c.Num(0x26)!.Value, c.Num(0x10f8) ?? 0)));
        Assert.All(snare, c => Assert.Equal([(0.0, 0.5, 0.0), (1.0, 0.5, 1.0)],
                                            Notes(c).Select(n => (n.Num(0x2af)!.Value, n.Num(0x26)!.Value, n.Num(0x10f8) ?? 0))));
        Assert.Equal(2, tracks[0].Obj(0x2aed)!.List(0x10e6).Distinct().Count());   // each clip has its own notes

        // Steps last until the next one or the end of the clip, but no longer than the sample.
        var kick = Clips(tracks[1]);
        Assert.Equal(2, kick.Count);
        Assert.Equal([(0.0, 56 / 96.0), (1.0, 56 / 96.0), (2.0, 56 / 96.0), (3.5, 0.5)],
                     Notes(kick[0]).Select(n => (n.Num(0x2af)!.Value, n.Num(0x26)!.Value)));
    }

    [Fact]
    public void StepsCanPlayUntilTheNextNoteOrTheClipEndInsteadOfStoppingWithTheirSample()
    {
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25MutesAndSteps(Fixtures.TempDir()));
        var doc = Decode(BitwigProject.FromFl(song, Fixtures.BitwigTemplate(),
            p => p.EndsWith("Kick 01.wav") ? new BitwigProject.SampleFile(1234, Md5, 48000, 2, 0.25) : Snare(p),
            stepsUntilNextNote: true).Bytes);
        Assert.Null(doc.BodyError);
        // The 0.25 s kick no longer cuts them short (0.58 beats): the third step lasts until beat 4½, the last to the bar's end.
        Assert.All(Clips(doc.Root!.Obj(0x4dd)!.List(0x4de).ToList()[1]), clip =>
            Assert.Equal([(0.0, 1.0), (1.0, 1.0), (2.0, 1.5), (3.5, 0.5)], Notes(clip).Select(n => (n.Num(0x2af)!.Value, n.Num(0x26)!.Value))));
    }

    [Fact]
    public void StepsOnAPitchedSampleLastAsLongAsTheSampleDoesAtThatPitch()
    {
        // A 0.25 s kick (0.58 beats at 140 bpm) with its root at C5 and its pitch knob down a semitone: steps an
        // octave above the root play it twice as fast, an octave below half as fast.
        var steps = new NoteEvent[] { new(0, 1, 73, 100, Step: true), new(0, 1, 49, 100, Step: true) };
        var kick = new FlChannel(0, "Kick", null, @"C:\Samples\Kick 01.wav", 0, steps, PitchCents: -100)
        {
            Clips = [new FlClip("Beat", 0, 384, steps)],
        };
        var doc = Decode(BitwigProject.FromFl(new FlSong("", 96, 140, [kick], [], []), Fixtures.BitwigTemplate(),
            _ => new BitwigProject.SampleFile(1234, Md5, 48000, 2, 0.25)).Bytes);
        var lanes = Clips(doc.Root!.Obj(0x4dd)!.List(0x4de).Single()).Single().Obj(0x2ae9)!.Obj(0x10e9)!.Obj(0x49c)!.List(0x18cb);
        Assert.Equal([(73.0, 28 / 96.0), (49.0, 112 / 96.0)],
                     lanes.Select(l => (l.Num(0xee)!.Value, l.List(0x21f).Single().Num(0x26)!.Value)).OrderByDescending(n => n.Item1));
    }

    [Fact]
    public void ClipsThatOverlapInFlBecomeOneClip()
    {
        // On two playlist tracks in FL: a bar from the start, and a muted bar from beat 3.
        var lead = new FlChannel(0, "Lead", new FlPlugin("Serum", "Xfer Records", "VST2", "1483109208", null), "", 0,
                                 [new NoteEvent(0, 96, 60, 100)])
        {
            Clips = [new FlClip("A", 0, 384, [new NoteEvent(0, 96, 60, 100)]),
                     new FlClip("B", 192, 384, [new NoteEvent(0, 96, 64, 100)], Muted: true)],
        };
        var doc = Decode(BitwigProject.FromFl(new FlSong("", 96, 120, [lead], [], []), Fixtures.BitwigTemplate(), Snare).Bytes);
        var clip = Clips(doc.Root!.Obj(0x4dd)!.List(0x4de).Single()).Single();
        Assert.Equal((0.0, 6.0, 0.0), (clip.Num(0x2af)!.Value, clip.Num(0x26)!.Value, clip.Num(0x10f8) ?? 0));
        Assert.Equal([(0.0, 0.0), (2.0, 1.0)], Notes(clip).Select(n => (n.Num(0x2af)!.Value, n.Num(0x10f8) ?? 0)));
    }

    [Fact]
    public void TransposesByOctavesAndSemitonesTheSameWay()
    {
        int[] roots = [57, 64, 74, 48, 72, 12];   // A4, E5, D6, C4, C6 and C0 (four octaves up: three on the octave knob)
        var song = new FlSong("", 96, 120, roots.Select((root, i) =>
            new FlChannel(i, $"Synth {i}", new FlPlugin("Serum", "Xfer Records", "VST2", "1483109208", null), "", 0,
                          [new NoteEvent(0, 96, 60, 100)], RootKey: root)).ToList(), [], []);
        var doc = Decode(BitwigProject.FromFl(song, Fixtures.BitwigTemplate(), Snare).Bytes);
        var transposes = doc.Root!.Obj(0x4dd)!.List(0x4de).Select(t => Chain(t).First()).ToList();
        Assert.Equal([(0, 3), (0, -4), (-1, -2), (1, 0), (-1, 0), (3, 12)],
                     transposes.Select(t => ((int)Param(t, "OCTAVES").Num(0x273)!.Value - 3, Semitones(t))));
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
        var keys = new FlPlugin("Keys", "Spectrasonics", "VST3", "565354536B657973", null, ParameterIds: [10, 20, 2000003]);
        FlAutomation Clip(string name, int channel, FlControl control, params (double, double)[] points) =>
            new(name, channel, control, 0, points.Select(p => new FlPoint(p.Item1, p.Item2)).ToList());
        FlAutomation Setting(string name, int setting, params (double, double)[] points) =>
            Clip(name, 0, FlControl.PluginParameter, points) with { Parameter = setting };
        var song = new FlSong("", 96, 120,
            [new FlChannel(0, "Keys", keys, "", 0, [new(0, 96, 60, 100)])],
            [],
            [Clip("Fade", 0, FlControl.Volume, (0, 0.78125), (1, 0.1667), (1.5, 0.8798), (3.25, 0.1744)),
             Clip("Pan later", 0, FlControl.Pan, (8, 0.5), (12, 0)),
             Clip("Pan first", 0, FlControl.Pan, (0, 0), (4, 1)),
             Clip("Bend", 0, FlControl.Pitch, (0, 0.5)),
             Setting("Keys - Brightness", 2, (0, 0.25), (2, 0.75)),
             Setting("Keys - Unknown", 3, (0, 1)),
             Clip("Elsewhere", 7, FlControl.Volume, (0, 1))]);

        var result = BitwigProject.FromFl(song, Fixtures.BitwigTemplate(), _ => null);
        var doc = Decode(result.Bytes);
        Assert.Null(doc.BodyError);
        var track = doc.Root!.Obj(0x4dd)!.List(0x4de).Single();
        var lanes = track.Obj(0x2dc9)!.List(0x4ba).ToList();
        Assert.Equal(["Volume", "Pan", "Brightness"], lanes.Select(l => l.Obj(0x2a3)!.Str(0x133a)));
        Assert.Same(track.Obj(0x165)!.Obj(0x1a5), lanes[1].Obj(0x2a3)!.Obj(0x41));
        IEnumerable<(double, double)> Points(BwObject lane) =>
            lane.Obj(0x2d8c)!.Obj(0x35ac)!.List(0x21f).Select(p => (p.Num(0x2af)!.Value, Math.Round(p.Num(0x28f)!.Value, 4)));
        Assert.Equal([(0, 1.0), (1, 0.3571), (1.5, 1.0824), (3.25, 0.368)], Points(lanes[0]));   // FL's 78% fader is Bitwig's 0 dB
        Assert.Equal([(0, -1.0), (4, 1.0), (8, 0.0), (12, -1.0)], Points(lanes[1]));
        // FL's VST3 setting 2 is the plugin's id 2000003, which Bitwig writes in hex.
        Assert.Equal("CONTENTS/ROOT_GENERIC_MODULE/PID1e8483", lanes[2].Obj(0x2a3)!.Str(0xed));
        Assert.Equal([(0, 0.25), (2, 0.75)], Points(lanes[2]));

        Assert.Equal(["Bend: automation left out (pitch has no equivalent here)",
                      "Keys - Unknown: automation left out (FL didn't save which of the plugin's settings it moves)",
                      "Elsewhere: automation left out (what it moves isn't converted)"],
                     result.Notes.Where(n => n.Contains("automation")));
    }

    static BwObject AudioOf(BwObject clip) => clip.Obj(0x2ae9)!.Obj(0x10e9)!.Obj(0x49c)!.List(0x18cb).Single().List(0x21f).Single();

    /// <summary>
    /// An audio clip: where it is and how long (beats), its audio's length, where the warp marker at the sample's end is
    /// (beat, second; the other is at the start), semitones, and the part it plays.
    /// </summary>
    static (double Start, double Length, double Audio, (double, double) Warp, double Semitones, double From, double To) Audio(BwObject clip)
    {
        var audio = AudioOf(clip);
        var play = audio.Obj(0x288)!;
        var range = clip.Obj(0x2adb)!;
        var markers = play.Obj(0x192)!.List(0x21f).Select(m => (m.Num(0x2af)!.Value, m.Num(0xa6)!.Value)).ToList();
        Assert.Equal(2, markers.Count);
        Assert.Equal((0.0, 0.0), markers[0]);
        return (clip.Num(0x2af)!.Value, clip.Num(0x26)!.Value, audio.Num(0x26)!.Value, markers[1],
                play.Obj(0xc33)?.List(0x21f).Single().Num(0x28f) ?? 0,
                range.Obj(0x98c)!.Num(0x2af)!.Value, range.Obj(0x98d)!.Num(0x2af)!.Value);
    }

    [Fact]
    public void MutesSwitchedOffChannelsAndClipsOnMutedPlaylistTracks()
    {
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25MutedTracks(Fixtures.TempDir()));
        var result = BitwigProject.FromFl(song, Fixtures.BitwigTemplate(),
            p => new BitwigProject.SampleFile(1234, Md5, 48000, 2, p.EndsWith("Acapella.wav") ? 8 : 0.25));
        var doc = Decode(result.Bytes);
        Assert.Null(doc.BodyError);
        var tracks = doc.Root!.Obj(0x4dd)!.List(0x4de).ToList();
        Assert.Equal(["Snare", "Hat", "Acapella", "Vox", "Vox 2", "Pad"], tracks.Select(t => t.Str(0x15b)));

        // The hat and the vox are switched off in FL, so their tracks' mute buttons are on, on both of the vox's tracks.
        // The Acapella's and the Pad's clips are all on muted playlist tracks, so their tracks are muted, not their clips.
        Assert.Equal([0.0, 1.0, 1.0, 1.0, 1.0, 1.0], tracks.Select(t => t.Obj(0x165)!.Obj(0x1a6)!.Num(0xd2)!.Value));
        Assert.Equal([0.0, 0.0], Clips(tracks[2]).Select(c => c.Num(0x10f8) ?? 0));
        Assert.All(tracks.Skip(3), t => Assert.Equal([0.0], Clips(t).Select(c => c.Num(0x10f8) ?? 0)));
        // The snare and the hat also play on track 1, so only their clip from muted track 2 is muted.
        Assert.All(tracks.Take(2), t => Assert.Equal([0.0, 1.0], Clips(t).Select(c => c.Num(0x10f8) ?? 0)));
        Assert.Equal(["Snare: its clips on a muted playlist track in FL Studio (Acapellas, playlist track 2) are muted, as it also has clips from other playlist tracks",
                      "Hat: its clips on a muted playlist track in FL Studio (Acapellas, playlist track 2) are muted, as it also has clips from other playlist tracks",
                      "Hat: switched off in FL Studio's Channel Rack, so its track is muted",
                      "Acapella: muted, because its clips are all on muted playlist tracks in FL Studio (Acapellas, playlist track 2; playlist track 4)",
                      "Vox: its audio clips overlap in FL Studio, so they're on 2 tracks",
                      "Vox: switched off in FL Studio's Channel Rack, so its tracks are muted",
                      "Pad: muted, because its clips are all on a muted playlist track in FL Studio (playlist track 4)"],
                     result.Notes);
    }

    [Fact]
    public void AudioClipsComeAcrossStretchedPitchedAndTrimmedAsInFl()
    {
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25AudioClips(Fixtures.TempDir()));
        var drop = new BitwigProject.SampleFile(8467244, Md5, 48000, 2, 32);
        var result = BitwigProject.FromFl(song, Fixtures.BitwigTemplate(), path => path.EndsWith("Drop.wav") ? drop : null);
        var doc = Decode(result.Bytes);
        Assert.Null(doc.BodyError);

        // In channel order: the Kick's Sampler track, then an audio track for each audio clip channel, with no devices.
        var tracks = doc.Root!.Obj(0x4dd)!.List(0x4de).ToList();
        Assert.Equal(["Kick", "Drop", "Drop stretched", "Drop stretched up", "Drop trimmed up", "Drop resampled"], tracks.Select(t => t.Str(0x15b)));
        Assert.All(tracks.Skip(1), t => Assert.Empty(Chain(t)));
        var clips = tracks.Skip(1).Select(t => Clips(t).Single()).ToList();

        // At 120 bpm the 32 second sample is 64 beats: warp markers pin its end where it ends when stretched, and
        // the trimmed clip plays its second half. Resampled to twice as long, it plays an octave lower.
        Assert.Equal((0, 64, 64, (64, 32), 0, 0, 64), Audio(clips[0]));
        Assert.Equal((64, 128, 128, (128, 32), 0, 0, 128), Audio(clips[1]));
        Assert.Equal((192, 128, 128, (128, 32), 3, 0, 128), Audio(clips[2]));
        Assert.Equal((320, 32, 64, (64, 32), 3, 32, 64), Audio(clips[3]));
        Assert.Equal((352, 32, 128, (128, 32), -12, 0, 32), Audio(clips[4]));
        Assert.Equal([0, 0, 0, 0, 1], clips.Select(c => c.Num(0x10f8)));
        Assert.Equal("Drop trimmed up", clips[3].Obj(0x2ae9)!.Obj(0x10e9)!.Str(0x2d24));

        // Each plays the FL sample, recognised by its MD5, with the transients Bitwig found in the template's emptied.
        foreach (var clip in clips)
        {
            var play = AudioOf(clip).Obj(0x288)!;
            var sample = play.Obj(0x20d)!.Obj(0x4b4)!;
            Assert.Equal(("Drop.wav", 48000.0, 32.0), (sample.Str(0x129f), sample.Num(0x94), sample.Num(0x95)));
            Assert.Equal(@"C:\Samples\Drop.wav", sample.Obj(0x129e)!.Obj(0xd3b)!.Str(0x3b));
            var md5 = (BwBlob)sample.Obj(0x129e)!.Obj(0x513)![0x16]!;
            Assert.Equal(Md5, result.Bytes[md5.Offset..(md5.Offset + md5.Length)]);
            var transients = play.Obj(0x10ea)!.Obj(0x10e9)!;
            Assert.Equal((0, 0.0), (transients.List(0x21f).Count(), transients.Num(0x4406)!.Value));
        }
        // Each track lists its clip's content, as Bitwig saves it.
        Assert.All(tracks.Skip(1).Zip(clips), p => Assert.Same(p.Second.Obj(0x2ae9)!.Obj(0x10e9), p.First.Obj(0x2aed)!.List(0x10e6).Single()));

        // The first one's volume automation is on its audio track, and the browser's summary lists audio tracks as such.
        Assert.Equal("Volume", tracks[1].Obj(0x2dc9)!.List(0x4ba).Single().Obj(0x2a3)!.Str(0x133a));
        var bytes = (BwBlob)doc.Meta["structure"]!;
        var entries = Decode(result.Bytes[bytes.Offset..(bytes.Offset + bytes.Length)]).Root!.Obj(0xad2)!.List(0xace).ToList();
        Assert.Equal(tracks.Select(t => t.Str(0x15b)), entries.Select(e => e.Str(0xad1)));
        Assert.Equal([0x288, 0x287, 0x287, 0x287, 0x287, 0x287], entries.Select(e => e.Class));

        Assert.Contains("Drop resampled: plays its sample backwards in FL Studio; reverse its clips in Bitwig", result.Notes);
        Assert.DoesNotContain(result.Notes, n => n.Contains("automation") || n.StartsWith("Drop:"));
    }

    [Fact]
    public void AudioClipsWithASampleThatCantBeReadAreGuessedFromTheClip()
    {
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25AudioClips(Fixtures.TempDir()));
        var result = BitwigProject.FromFl(song, Fixtures.BitwigTemplate(), _ => null);
        var tracks = Decode(result.Bytes).Root!.Obj(0x4dd)!.List(0x4de).ToList();
        // As if each clip plays to the sample's end: the trimmed one from 16 seconds for 32 beats, so 32 seconds.
        Assert.Equal((320, 32, 64, (64, 32), 3, 32, 64), Audio(Clips(tracks[4]).Single()));
        Assert.Contains(@"Drop: couldn't read C:\Samples\Drop.wav, so Bitwig will ask where it is, and how far its clips are stretched is a guess",
                        result.Notes);
    }

    [Fact]
    public void AudioClipsAreListedWhenTheTemplateHasNoAudioTrack()
    {
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25AudioClips(Fixtures.TempDir()));
        var result = BitwigProject.FromFl(song, Fixtures.BitwigTemplate(audioTrack: false), _ => null);
        Assert.Equal(["Kick"], Decode(result.Bytes).Root!.Obj(0x4dd)!.List(0x4de).Select(t => t.Str(0x15b)));
        Assert.Contains("Drop stretched: its audio clips are left out (the Bitwig template has no audio track with an audio clip on it " +
                        "that can be copied; it can't be in a group)", result.Notes);
        Assert.Contains("Drop - Volume: automation left out (what it moves isn't converted)", result.Notes);
    }

    [Fact]
    public void OverlappingAudioClipsGoOnTracksOfTheirOwnThatTheChannelsVolumeMoves()
    {
        // A riser placed twice, the second starting before the first ends (on another playlist track in FL), then again after both.
        var riser = new FlChannel(1, "Riser", null, @"C:\Samples\Riser.wav", 2, [])
        {
            AudioClips = [new FlAudioClip(0, 384), new FlAudioClip(192, 384), new FlAudioClip(576, 96)],
            Stretch = new FlStretch(Mode: 1),
        };
        var volume = new FlAutomation("Riser - Volume", 1, FlControl.Volume, 0, [new FlPoint(0, 0), new FlPoint(4, 1)]);
        var eq = new FlPlugin("Pro-Q 3", "FabFilter", "VST3", "72C4DB717A4D459AB97E51745D84B39D", new PresetBlob("Pro-Q 3", "Pro-Q 3", ".vstpreset", [4, 5]));
        var result = BitwigProject.FromFl(new FlSong("", 96, 120, [riser], [new FlInsert(2, "Riser bus", [eq])], [volume]), Fixtures.BitwigTemplate(),
                                          _ => new BitwigProject.SampleFile(10, Md5, 44100, 2, 2));
        var tracks = Decode(result.Bytes).Root!.Obj(0x4dd)!.List(0x4de).ToList();
        Assert.Equal(["Riser", "Riser 2"], tracks.Select(t => t.Str(0x15b)));
        // Each has its own copy of the insert's effects, which FL runs once on the riser's sum, and that's said.
        Assert.All(tracks, t => Assert.Equal(["Pro-Q 3"], Chain(t).Select(d => Def(d).Str(0x9a))));
        Assert.Contains("Riser: each of its 2 tracks has its own copy of Riser bus's effects, which FL Studio runs once on all of them", result.Notes);
        Assert.Equal([[0.0, 6.0], [2.0]], tracks.Select(t => Clips(t).Select(c => c.Num(0x2af)!.Value).ToArray()));
        Assert.All(tracks, t => Assert.Equal("Volume", t.Obj(0x2dc9)!.List(0x4ba).Single().Obj(0x2a3)!.Str(0x133a)));
        Assert.Contains("Riser: its audio clips overlap in FL Studio, so they're on 2 tracks", result.Notes);
    }

    [Fact]
    public void ASamplersSampleTheAppCantDecodeIsLeftForBitwigToRead()
    {
        // Found, but in a format the app can't decode, so how long it is isn't known: rather than a made-up length and a
        // zone that ends where it starts, Bitwig is asked where it is (an MD5 of zeros) and the template's zone is kept.
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25Automation(Fixtures.TempDir()));
        var template = Decode(Fixtures.BitwigTemplate()).Objects.Single(o => o.Class == 0x210 && o.Obj(0x4112) is not null);
        var result = BitwigProject.FromFl(song, Fixtures.BitwigTemplate(),
                                          p => p.EndsWith("Snare 01.wav") ? new BitwigProject.SampleFile(1234, Md5, 48000, 2, 0) : null);
        var zone = Param(Chain(Decode(result.Bytes).Root!.Obj(0x4dd)!.List(0x4de).First()).Single(), "SAMPLE").Obj(0x74c)!;
        Assert.Equal((template.Num(0x75f), template.Num(0x761)), (zone.Num(0x75f), zone.Num(0x761)));
        var file = zone.Obj(0x4112)!.Obj(0x748)!.Obj(0x129e)!;
        var md5 = (BwBlob)file.Obj(0x513)![0x16]!;
        Assert.Equal(new byte[16], result.Bytes[md5.Offset..(md5.Offset + md5.Length)]);
        Assert.Equal(@"C:\Samples\Snare 01.wav", file.Obj(0xd3b)!.Str(0x3b));
        Assert.Contains(@"Snare: couldn't tell how long C:\Samples\Snare 01.wav is, so Bitwig will ask where it is", result.Notes);
    }

    [Fact]
    public void AudioClipsSayWhatTheyCantCarry()
    {
        var song = new FlSong("", 96, 120,
        [
            // A sample the app can't decode: Bitwig is asked where it is, and how long it is is a guess.
            new FlChannel(1, "Loop", null, @"C:\Samples\Loop.rx2", 0, [], PitchCents: 200) { AudioClips = [new FlAudioClip(0, 384)] },
            new FlChannel(2, "Lost", null, "", 0, []) { AudioClips = [new FlAudioClip(0, 384)] },
        ], [], []);
        var result = BitwigProject.FromFl(song, Fixtures.BitwigTemplate(), _ => new BitwigProject.SampleFile(10, Md5, 44100, 2, 0));
        Assert.Equal(["Loop"], Decode(result.Bytes).Root!.Obj(0x4dd)!.List(0x4de).Select(t => t.Str(0x15b)));
        Assert.Equal((0, 4, 4, (4, 2), 0, 0, 4), Audio(Clips(Decode(result.Bytes).Root!.Obj(0x4dd)!.List(0x4de).Single()).Single()));
        Assert.Equal([@"Loop: couldn't tell how long C:\Samples\Loop.rx2 is, so Bitwig will ask where it is, and how far its clips are stretched is a guess",
                      "Loop: its channel pitch knob (+2 semitones) isn't carried over to its audio clips; the pitch in its time stretching is",
                      "Lost: its audio clips are left out (FL Studio didn't save which sample they play)"], result.Notes);
    }

    [Fact]
    public void OnlyTheAudioFilledInIsInAnAudioClip()
    {
        // The template's audio clip plays its sample twice; only the first is filled in, so the other isn't copied.
        var template = Decode(Fixtures.BitwigTemplate());
        var audio = template.Objects.Single(o => o.Class == 0xd4);
        var second = BitwigFormat.Clone(template, audio);
        second.Set(0x2af, 64.0);
        ((List<object?>)audio.Parent![0x21f]!).Add(second);
        Assert.Equal(2, audio.Parent.List(0x21f).Count());

        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25AudioClips(Fixtures.TempDir()));
        var result = BitwigProject.FromFl(song, BitwigFormat.Encode(template), _ => new BitwigProject.SampleFile(10, Md5, 44100, 2, 32));
        var doc = Decode(result.Bytes);
        Assert.Null(doc.BodyError);
        Assert.Equal(5, doc.Objects.Count(o => o.Class == 0xd4));
        Assert.DoesNotContain(doc.Objects, o => o.Class == 0x4a6 && o.Str(0x512) == "Drops.wav");
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
