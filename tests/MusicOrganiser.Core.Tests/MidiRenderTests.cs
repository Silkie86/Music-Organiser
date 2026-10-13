using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Tests;

public class MidiRenderTests
{
    static MidiChannel Channel(string name, IReadOnlyList<NoteEvent> notes, string sample = "") =>
        new(name, "", 96, notes, SamplePath: sample);

    [Fact]
    public void PlaysNotesWhereTheySitInTheSong()
    {
        // At 120 BPM a beat is half a second: a note on beat 3 starts at one second.
        var c = Channel("Lead", [new NoteEvent(2 * 96, 96, 69, 100)]);
        var r = MidiRender.Render([c], 120, "song.flp");

        Assert.InRange(r.Seconds, 1.5, 4);
        var rate = MidiRender.Rate;
        Assert.True(r.Audio.Take(rate - 100).All(v => v == 0), "silent before the note");
        Assert.Contains(r.Audio.Skip(rate).Take(rate / 4), v => Math.Abs(v) > 0.05f);
        Assert.All(r.Audio, v => Assert.InRange(v, -0.9f, 0.9f));
        Assert.Equal("a stand-in tone", r.Voices[0]);
    }

    [Fact]
    public void ASamplerChannelPlaysItsOwnSamplePitchedFromItsRootKey()
    {
        var dir = Fixtures.TempDir();
        var wav = Path.Combine(dir, "pluck.wav");
        var x = Synth.Silence(1);
        Synth.Tone(x, 60, 0, 1);
        Synth.Write(wav, x);

        // Played an octave up, the one-second sample lasts half a second.
        var atRoot = MidiRender.Render([Channel("Pluck", [new NoteEvent(0, 24, 60, 100)], wav)], 120, "song.flp");
        var octaveUp = MidiRender.Render([Channel("Pluck", [new NoteEvent(0, 24, 72, 100)], wav)], 120, "song.flp");

        static double Sounding(float[] a) => (double)Array.FindLastIndex(a, v => Math.Abs(v) > 1e-4f) / MidiRender.Rate;
        Assert.InRange(Sounding(atRoot.Audio), 0.8, 1.05);
        Assert.InRange(Sounding(octaveUp.Audio), 0.4, 0.55);
        Assert.StartsWith("its sample", atRoot.Voices[0]);

        // With the root key moved to C4 (48), C5 is an octave up; pitched down an octave from there, it's back to normal.
        var c5 = new NoteEvent(0, 24, 60, 100);
        var rootC4 = MidiRender.Render([new MidiChannel("Pluck", "", 96, [c5], SamplePath: wav, RootKey: 48)], 120, "song.flp");
        var pitchedDown = MidiRender.Render([new MidiChannel("Pluck", "", 96, [c5], SamplePath: wav, RootKey: 48, PitchCents: -1200)], 120, "song.flp");
        Assert.InRange(Sounding(rootC4.Audio), 0.4, 0.55);
        Assert.InRange(Sounding(pitchedDown.Audio), 0.8, 1.05);
    }

    [Fact]
    public void AMissingSampleFallsBackToAStandInAndSaysSo()
    {
        var c = Channel("Pluck", [new NoteEvent(0, 96, 60, 100)], @"Z:\gone\pluck.wav");
        var r = MidiRender.Render([c], null, "song.flp");
        Assert.Equal("a stand-in tone (sample not found)", r.Voices[0]);
        Assert.Contains(r.Audio, v => Math.Abs(v) > 0.05f);
    }

    [Fact]
    public void TicksAndSecondsConvertBothWays()
    {
        Assert.Equal(2.0, MidiRender.TicksToSeconds(4 * 96, 96, 120), 6);
        Assert.Equal(4 * 96, MidiRender.SecondsToTicks(2, 96, 120), 6);
    }
}

public class MidiDragExportTests
{
    static MidiChannel Channel(string name) => new(name, "", 96, [new NoteEvent(0, 96, 60, 100)]);

    [Fact]
    public void SeparateFilesKeepTheirOrderAndSameNamesDontOverwrite()
    {
        var root = Fixtures.TempDir();
        var files = Export.Exporter.MidiFiles([Channel("Keys"), Channel("Bass"), Channel("Keys")], Path.Combine(root, "Song.flp"), root, 120);

        Assert.Equal(["Keys.mid", "Bass.mid", "Keys 2.mid"], files.Select(Path.GetFileName));
        Assert.All(files, f => Assert.True(File.Exists(f)));
    }

    [Fact]
    public void TogetherIsOneFileWithATrackPerChannel()
    {
        var root = Fixtures.TempDir();
        var file = Export.Exporter.MidiTogether([Channel("Keys"), Channel("Bass"), Channel("Keys")], Path.Combine(root, "Song.flp"), root, 120);

        Assert.Equal("Song - 3 tracks.mid", Path.GetFileName(file));
        var bytes = File.ReadAllBytes(file);
        var tracks = Enumerable.Range(0, bytes.Length - 3).Count(i => bytes[i] == 'M' && bytes[i + 1] == 'T' && bytes[i + 2] == 'r' && bytes[i + 3] == 'k');
        Assert.True(tracks >= 3, $"{tracks} tracks");
    }
}
