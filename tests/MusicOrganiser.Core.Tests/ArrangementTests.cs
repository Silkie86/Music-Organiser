using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Preview;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Tests;

public class ArrangementTests
{
    [Fact]
    public void FlProjectsShowTheirPlaylistAndTheTracksTheyBecomeInBitwig()
    {
        var views = ArrangementReader.Read(Fixtures.FlStudio25Automation(Fixtures.TempDir()));
        Assert.Equal(["FL Studio playlist", "As it opens in Bitwig"], views.Select(v => v.View));

        // The playlist: the pattern on track 1 in bar 2, the volume automation clip on track 2, the Serum one on track 3.
        var playlist = views[0];
        Assert.Equal(132, playlist.Tempo);
        Assert.Equal(["Track 1", "Track 2", "Track 3"], playlist.Lanes.Select(l => l.Name));
        Assert.Equal([LaneKind.Instrument, LaneKind.Automation, LaneKind.Automation], playlist.Lanes.Select(l => l.Kind));
        var pattern = playlist.Lanes[0].Clips.Single();
        Assert.Equal((4.0, 4.0), (pattern.Start, pattern.Length));
        // Both channels' notes are in the pattern clip, in beats from its start.
        Assert.Equal([(0.0, 60), (0.0, 72)], pattern.Notes.Select(n => (n.Start, n.Key)).OrderBy(n => n.Key));
        var volume = playlist.Lanes[1].Clips.Single();
        Assert.Equal(("Snare - Channel volume", 4.0), (volume.Name, volume.Start));
        Assert.Equal((0.0, 1.0), (volume.Points[0].Value, volume.Points[^1].Value));

        // In Bitwig: a track per channel that plays, named after it, then the automation.
        var bitwig = views[1];
        Assert.Equal(["Snare", "Serum", "Snare - Channel volume", "Serum - Cutoff"], bitwig.Lanes.Select(l => l.Name));
        Assert.Equal("Serum", bitwig.Lanes[1].Detail);
        Assert.Equal(4.0, bitwig.Lanes[0].Clips.Single().Start);
        Assert.Equal(8, bitwig.Length);
    }

    [Fact]
    public void FlAudioClipsKnowWhichPartOfTheirSampleTheyPlayAndHowFast()
    {
        var dir = Fixtures.TempDir();
        var path = Fixtures.FlStudio25AudioClips(dir);
        var lanes = ArrangementReader.Read(path)[1].Lanes.Where(l => l.Kind == LaneKind.Audio).ToList();
        Assert.Equal(["Drop", "Drop stretched", "Drop stretched up", "Drop trimmed up", "Drop resampled"], lanes.Select(l => l.Name));

        // At 120 bpm a beat is half a second of the sample as recorded; stretched to twice as long, a quarter.
        var parts = lanes.Select(l => l.Clips.Single().Audio.Single()).ToList();
        Assert.Equal(0.5, parts[0].SecondsPerBeat!.Value, 6);
        Assert.Equal(0.25, parts[1].SecondsPerBeat!.Value, 6);
        Assert.Equal(16, parts[3].From, 6);
        Assert.True(parts[4].Reversed);
        Assert.True(lanes[4].Clips.Single().Muted);
        // The sample isn't on this computer, so it's drawn without a waveform.
        Assert.All(parts, p => Assert.False(p.Found));
    }

    [Fact]
    public void AudioFilesAreFoundBesideTheProjectWhenTheSavedPathIsGone()
    {
        var dir = Fixtures.TempDir();
        var path = Fixtures.FlStudio25AudioClips(dir);
        WavWriter.Write(Path.Combine(dir, "Drop.wav"), [new float[4410]], 44100);
        var part = ArrangementReader.Read(path)[1].Lanes.First(l => l.Kind == LaneKind.Audio).Clips.Single().Audio.Single();
        Assert.True(part.Found);
        Assert.Equal(Path.Combine(dir, "Drop.wav"), part.Path);
    }

    [Fact]
    public void FlPlaylistTracksKeepTheirNamesAndColoursAndTheTimelineItsMarkers()
    {
        var track = new byte[70];
        BitConverter.GetBytes(2).CopyTo(track, 0);              // track 2
        BitConverter.GetBytes(0x00FF8000u).CopyTo(track, 4);    // blue 0xFF, green 0x80, red 0
        var b = new Fixtures.FlpBuilder()
            .Ascii(199, "21.2.3.4004").Dword(156, 120_000).Byte(17, 3)
            .Word(65, 1).Text(193, "Verse").Data(224, Fixtures.Note(0, 0, 96, 60, 100))
            .Word(64, 0).Byte(21, 0).Text(203, "Piano").Text(196, @"C:\Samples\Piano.wav")
            .Word(99, 0).Text(241, "Arrangement")
            .Data(233, Fixtures.PlaylistItem25(288, 20481, 288, track: 498))
            .Data(238, new byte[70]).Data(238, track).Text(239, "Keys")
            .Dword(148, 288).Text(205, "Verse 1");
        var path = Path.Combine(Fixtures.TempDir(), "Named.flp");
        File.WriteAllBytes(path, b.Build());

        var playlist = ArrangementReader.Read(path)[0];
        Assert.Equal(3, playlist.BeatsPerBar);
        Assert.Equal(["Track 1", "Keys"], playlist.Lanes.Select(l => l.Name));
        Assert.Equal(0x0080FF, playlist.Lanes[1].Colour);
        Assert.Equal(("Verse", 3.0), (playlist.Lanes[1].Clips.Single().Name, playlist.Lanes[1].Clips.Single().Start));
        Assert.Equal([new PreviewMarker(3, "Verse 1")], playlist.Markers);
    }

    [Fact]
    public void BitwigProjectsShowTheirTracksClipsNotesAndAudio()
    {
        // An FL song written as a Bitwig project by the app, then read back as Bitwig shows it.
        var dir = Fixtures.TempDir();
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25AudioClips(dir));
        var drop = new BitwigProject.SampleFile(8467244, Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(), 48000, 2, 32);
        var bytes = BitwigProject.FromFl(song, Fixtures.BitwigTemplate(), p => p.EndsWith("Drop.wav") ? drop : null).Bytes;
        var path = Path.Combine(dir, "Song", "Song.bwproject");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);

        var view = ArrangementReader.Read(path).Single();
        Assert.Equal(120, view.Tempo);
        Assert.Equal(["Kick", "Drop", "Drop stretched", "Drop stretched up", "Drop trimmed up", "Drop resampled"],
                     view.Lanes.Select(l => l.Name).Take(6));
        Assert.Equal(LaneKind.Instrument, view.Lanes[0].Kind);
        Assert.Single(view.Lanes[0].Clips.Single().Notes);

        // The same stretches as in FL: a beat is half a second of the sample, or a quarter when stretched to twice as long;
        // the trimmed clip starts halfway in.
        var audio = view.Lanes.Skip(1).Take(5).Select(l => l.Clips.Single()).ToList();
        Assert.All(audio, c => Assert.Equal(LaneKind.Audio, view.Lanes.First(l => l.Clips.Contains(c)).Kind));
        Assert.Equal(0.5, audio[0].Audio.Single().SecondsPerBeat!.Value, 6);
        Assert.Equal(0.25, audio[1].Audio.Single().SecondsPerBeat!.Value, 6);
        Assert.Equal(16, audio[3].Audio.Single().From, 6);
        Assert.Equal(("Drop trimmed up", 320.0, 32.0), (audio[3].Name, audio[3].Start, audio[3].Length));
        Assert.True(audio[4].Muted);
    }

    [Fact]
    public void PeaksFollowTheLoudnessOfTheFile()
    {
        var path = Path.Combine(Fixtures.TempDir(), "Swell.wav");
        // One second quiet, one second loud.
        var samples = Enumerable.Range(0, 88200).Select(i => (float)((i < 44100 ? 0.1 : 0.9) * Math.Sin(i * 0.05))).ToArray();
        WavWriter.Write(path, [samples], 44100);
        var peaks = AudioPeaks.For(path)!;
        Assert.Equal(2, peaks.Seconds, 2);
        Assert.InRange(peaks.Max(0, 0.9), 0.08, 0.12);
        Assert.InRange(peaks.Max(1.1, 2), 0.85, 0.95);
    }
}
