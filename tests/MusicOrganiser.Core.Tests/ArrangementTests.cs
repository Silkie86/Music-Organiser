using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Preview;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Tests;

public class ArrangementTests
{
    [Fact]
    public void AnFl9SongsPatternsEachGetATrackInPatternOrderAsFl25GivesThem()
    {
        // FL 9 keeps a playlist row per pattern; FL 25 opens funk step_8 with each pattern played on a track of its own.
        var playlist = ArrangementReader.Read(Fixtures.FlStudio9Steps(Fixtures.TempDir(), pattern4AtStart: true))[0];
        Assert.Equal([("Track 1", 8.0), ("Track 2", 0.0)], playlist.Lanes.Select(l => (l.Name, l.Clips.Single().Start)));
    }

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
    public void EachPlacementOfAnFlAutomationClipDrawsTheWholeClip()
    {
        // The volume clip is in bar 2 and again in bar 4: both rise from 0 to 1, though the song holds 1 between them.
        var playlist = ArrangementReader.Read(Fixtures.FlStudio25Automation(Fixtures.TempDir(), volumePlacedTwice: true))[0];
        var volume = playlist.Lanes[1].Clips;
        Assert.Equal([4.0, 12.0], volume.Select(c => c.Start));
        Assert.All(volume, c => Assert.Equal((0.0, 1.0), (c.Points[0].Value, c.Points[^1].Value)));
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
    public void Reading_a_songs_notes_only_never_looks_on_disk_for_its_audio()
    {
        // The MIDI page only wants the notes: Solomon's biggest Bitwig song has 2,594 audio clips, and looking for each
        // one's file on his drives made reading its MIDI crawl.
        var dir = Fixtures.TempDir();
        var path = Fixtures.FlStudio25AudioClips(dir);
        WavWriter.Write(Path.Combine(dir, "Drop.wav"), [new float[4410]], 44100);
        var lanes = ArrangementReader.Read(path, findAudio: false)[1].Lanes.Where(l => l.Kind == LaneKind.Audio).ToList();
        Assert.Equal(5, lanes.Count);
        Assert.All(lanes.SelectMany(l => l.Clips).SelectMany(c => c.Audio), p => Assert.False(p.Found));
        // And the preview, which does want them, still finds it.
        Assert.True(ArrangementReader.Read(path)[1].Lanes.First(l => l.Kind == LaneKind.Audio).Clips.Single().Audio.Single().Found);
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
        Assert.Equal(["Keys"], playlist.Lanes.Select(l => l.Name));   // the empty, unnamed track 1 is left out
        Assert.Equal(0x0080FF, playlist.Lanes[0].Colour);
        Assert.Equal(("Verse", 3.0), (playlist.Lanes[0].Clips.Single().Name, playlist.Lanes[0].Clips.Single().Start));
        Assert.Equal([new PreviewMarker(3, "Verse 1")], playlist.Markers);
    }

    [Theory]
    // FL 11 had 199 playlist tracks and saved 198 for the first: a clip at 196 is on track 3.
    [InlineData("11.0.4", new[] { 196 }, new[] { "Track 3" })]
    // A 2012 song (Solomon's robotasiza) has its clips on tracks 1 to 4 in FL, counting down from 98.
    [InlineData("10.0.9", new[] { 98, 97, 96, 95 }, new[] { "Track 1", "Track 2", "Track 3", "Track 4" })]
    // From FL 12.9.1 there are 500 tracks and the first is 499.
    [InlineData("12.9.1", new[] { 499, 497 }, new[] { "Track 1", "Track 3" })]
    public void OldFlProjectsCountTheirPlaylistTracksTheirOwnWay(string version, int[] slots, string[] tracks)
    {
        var unicode = int.Parse(version.Split('.')[0]) >= 12;   // names are UTF-16 from FL 11.5
        var b = new Fixtures.FlpBuilder().Ascii(199, version).Dword(156, 140_000).Word(65, 1);
        b = unicode ? b.Text(193, "Beat") : b.Ascii(193, "Beat");
        b = b.Data(224, Fixtures.Note(0, 0, 96, 36, 100)).Word(64, 0).Byte(21, 0);
        var items = slots.SelectMany((slot, i) =>
        {
            var item = Fixtures.PlaylistItem((uint)(i * 384), 1, 384);
            BitConverter.GetBytes((ushort)slot).CopyTo(item, 12);
            return item;
        }).ToArray();
        var path = Path.Combine(Fixtures.TempDir(), "Old.flp");
        File.WriteAllBytes(path, b.Data(233, items).Build());

        var playlist = ArrangementReader.Read(path)[0];
        Assert.Equal(tracks, playlist.Lanes.Select(l => l.Name));
        Assert.All(playlist.Lanes, l => Assert.Equal(("Beat", 4.0), (l.Clips.Single().Name, l.Clips.Single().Length)));
        Assert.Single(playlist.Lanes[0].Clips.Single().Notes);
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
    public void StudioOneSongsShowTheirPartsPatternsAndAudio()
    {
        // Laid out like Solomon's "Garage baby" (Studio One 6.5): notes live in Performances/*.musicx, found by clipID.
        var dir = Fixtures.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "Media"));
        WavWriter.Write(Path.Combine(dir, "Media", "Vox.wav"), [new float[44100]], 44100);
        var path = Path.Combine(dir, "Garage.song");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            void Add(string entry, byte[] data) { using var s = zip.CreateEntry(entry).Open(); s.Write(data); }
            void Text(string entry, string text) => Add(entry, System.Text.Encoding.UTF8.GetBytes(text));
            Text("metainfo.xml", """<MetaInformation><Attribute x:id="Media:Tempo" value="130"/></MetaInformation>""");
            Text("Song/mediapool.xml", """
                <MediaPool><Attributes x:id="rootFolder">
                  <MediaFolder name="Music"><MusicClip mediaID="{K}" name="Keys"><Url x:id="dataPath" type="1" url="media:///Performances/Keys/Keys(1).musicx"/></MusicClip></MediaFolder>
                  <MediaFolder name="Pattern"><MusicPatternClip mediaID="{P}"><Url x:id="dataPath" type="1" url="media:///Performances/Impact/Impact(2).pattern"/></MusicPatternClip></MediaFolder>
                  <MediaFolder name="Audio"><AudioClip mediaID="{A}"><Url x:id="path" type="1" url="file:///F:/Studio%20One/Songs/Garage/Media/Vox.wav"/>
                    <Attributes x:id="format" frameCount="44100" sampleRate="44100" numChannels="1"/></AudioClip></MediaFolder>
                </Attributes></MediaPool>
                """);
            Text("Song/song.xml", """
                <Song><Attributes x:id="Root">
                  <Attributes x:id="timeContext">
                    <TempoMap x:id="tempoMap"><TempoMapSegment start="0" tempo="0.46153846153846156"/></TempoMap>
                    <TimeSignatureMap x:id="timeSignatureMap"><TimeSignatureMapSegment start="0" numerator="4" denominator="4"/></TimeSignatureMap>
                  </Attributes>
                  <List x:id="Tracks">
                    <MarkerTrack><MarkerEvent markerType="2" timeFormat="2" name="Start"/><MarkerEvent start="8" timeFormat="2" name="Drop"/></MarkerTrack>
                    <MediaTrack mediaType="Music" timeFormat="2" name="Impact" color="FFDD6105">
                      <List x:id="Events"><MusicPatternPart clipID="{P}" timeFormat="2" length="16" name="Pattern"/></List>
                    </MediaTrack>
                    <MediaTrack mediaType="Music" timeFormat="2" name="Keyscape" color="FFD89D00">
                      <List x:id="Events"><MusicPart clipID="{K}" start="8" timeFormat="2" length="8" offset="16" transpose="2" name="Keys"/></List>
                    </MediaTrack>
                    <MediaTrack mediaType="Audio" timeFormat="2" mute="1" name="Vox">
                      <List x:id="Events"><AudioEvent clipID="{A}" start="2.5" timeFormat="0" length="0.46153846153846156" offset="0.25" speed="1" name="Vox"/></List>
                    </MediaTrack>
                  </List>
                </Attributes></Song>
                """);
            Text("Performances/Impact/Impact(2).pattern", """
                <PatternBank><List x:id="performances">
                  <PatternPerformance stepCount="16" stepLength="0.25">
                    <PatternStep pitch="36" velocity="1"/><PatternStep start="4" pitch="38" velocity="0.5"/><PatternStep start="6" pitch="42" velocity="0.6" mute="1"/>
                  </PatternPerformance>
                </List></PatternBank>
                """);
            // {timeFormat: 2, events: [{pitch: 48, length: 1}, {start: 17.5, pitch: 55, length: 0.5, velocity: 0.5}, {start: 22, pitch: 60, length: 4}]}
            Add("Performances/Keys/Keys(1).musicx", Ubj(("timeFormat", 2L), ("events", new object[]
            {
                new (string, object)[] { ("pitch", 48L), ("length", 1.0) },
                new (string, object)[] { ("start", 17.5), ("pitch", 55L), ("length", 0.5), ("velocity", 0.5) },
                new (string, object)[] { ("start", 22.0), ("pitch", 60L), ("length", 4.0) },
            })));
        }

        var song = ArrangementReader.Read(path).Single();
        Assert.Equal(130, song.Tempo, 6);
        Assert.Equal(["Impact", "Keyscape", "Vox"], song.Lanes.Select(l => l.Name));
        Assert.Equal(0xDD6105, song.Lanes[0].Colour);
        Assert.Equal([new PreviewMarker(8, "Drop")], song.Markers);

        // The 4-beat pattern repeats through its 16-beat part; the muted step stays muted.
        var pattern = song.Lanes[0].Clips.Single();
        Assert.Equal(12, pattern.Notes.Count);
        Assert.Equal([0.0, 1.0, 4.0, 5.0], pattern.Notes.Where(n => !n.Muted).Select(n => n.Start).Take(4));

        // The part plays beats 16 to 24 of its clip, two semitones up, from beat 8 of the song.
        var keys = song.Lanes[1].Clips.Single();
        Assert.Equal((8.0, 8.0), (keys.Start, keys.Length));
        Assert.Equal([(1.5, 57, 64), (6.0, 62, 102)], keys.Notes.Select(n => (n.Start, n.Key, n.Velocity)));
        Assert.Equal(2.0, keys.Notes[1].Length, 6);   // cut off where the part ends

        // An audio event's length and offset are in seconds: one beat long, a quarter second in. Its file is in Media/.
        var vox = song.Lanes[2];
        Assert.True(vox.Muted);
        var clip = vox.Clips.Single();
        Assert.Equal((2.5, 1.0), (clip.Start, Math.Round(clip.Length, 6)));
        var part = clip.Audio.Single();
        Assert.True(part.Found);
        Assert.Equal(0.25, part.From, 6);
        Assert.Equal(60.0 / 130, part.SecondsPerBeat!.Value, 6);
    }

    /// <summary>Universal Binary JSON, as Studio One writes .musicx files.</summary>
    static byte[] Ubj(params (string Key, object Value)[] fields)
    {
        var o = new List<byte>();
        void Str(string s) { var b = System.Text.Encoding.UTF8.GetBytes(s); o.Add((byte)'i'); o.Add((byte)b.Length); o.AddRange(b); }
        void Val(object v)
        {
            switch (v)
            {
                case long l: o.Add((byte)'i'); o.Add((byte)(sbyte)l); break;
                case double d: o.Add((byte)'D'); var b = BitConverter.GetBytes(d); Array.Reverse(b); o.AddRange(b); break;
                case (string, object)[] obj: o.Add((byte)'{'); foreach (var (k, x) in obj) { Str(k); Val(x); } o.Add((byte)'}'); break;
                case object[] list: o.Add((byte)'['); foreach (var x in list) Val(x); o.Add((byte)']'); break;
            }
        }
        Val(fields);
        return [.. o];
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

    [Fact]
    public void A_long_recording_is_drawn_from_parts_read_in_turn_the_same_as_reading_it_whole()
    {
        // A 20 minute recording read whole took most of a gigabyte for one waveform: it's read 10 s at a time now.
        // 22,050 Hz makes the parts end part way through a hundredth of a second, so the slices that span two parts count.
        var path = Path.Combine(Fixtures.TempDir(), "Take.wav");
        const int rate = 22050;
        var rng = new Random(4);
        float[] Channel() => Enumerable.Range(0, rate * 25).Select(i => (float)(Math.Sin(i * 0.01) * (0.2 + 0.7 * ((i / 5000) % 3) / 2) * rng.NextDouble())).ToArray();
        WavWriter.Write(path, [Channel(), Channel()], rate);

        var whole = AudioFile.Read(path, AudioPeaks.MaxSeconds);
        var slice = (int)(rate * AudioPeaks.SliceSeconds);
        var expected = new float[(whole.Length + slice - 1) / slice];
        foreach (var channel in whole.Channels)
            for (var i = 0; i < channel.Length; i++) expected[i / slice] = Math.Max(expected[i / slice], Math.Abs(channel[i]));

        var peaks = AudioPeaks.For(path)!;
        Assert.Equal(expected, peaks.Peaks);
        Assert.Equal(25, peaks.Seconds, 3);

        // A part from further in is those frames of the whole.
        var part = AudioFile.Read(path, 1, fromFrame: rate * 12 + 7);
        Assert.Equal(rate, part.Length);
        Assert.Equal(whole.Channels[1].AsSpan(rate * 12 + 7, rate).ToArray(), part.Channels[1]);
        Assert.Equal(0, AudioFile.Read(path, 1, fromFrame: rate * 30).Length);
    }
}
