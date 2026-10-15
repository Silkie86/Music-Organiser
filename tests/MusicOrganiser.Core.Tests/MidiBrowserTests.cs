using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Tests;

public class MidiBrowserTests
{
    const int Ppq = PhraseAnalyser.Ppq;
    const int Beat = Ppq, Bar = 4 * Ppq;

    /// <summary>Chords a bar each, every note held for the bar.</summary>
    static List<NoteEvent> Chords(params int[][] chords) =>
        chords.SelectMany((c, bar) => c.Select(k => new NoteEvent(bar * (long)Bar, Bar, k, 100))).ToList();

    static List<NoteEvent> Line(int length, params int[] keys) =>
        keys.Select((k, i) => new NoteEvent(i * (long)length, length, k, 100)).ToList();

    // Am – F – C – G, and the same progression in C minor: Cm – Ab – Eb – Bb.
    static readonly int[][] AMinor = [[57, 60, 64], [53, 57, 60], [48, 52, 55], [55, 59, 62]];
    static readonly int[][] CMinor = [[60, 63, 67], [56, 60, 63], [51, 55, 58], [58, 62, 65]];

    // ---- reading .mid files ----

    [Fact]
    public void A_midi_file_reads_back_track_by_track()
    {
        var clip = new MidiClip { Name = "Idea", Ppq = 480 };
        clip.Track("Keys").AddRange(Chords(AMinor).Select(n => n with { Start = n.Start * 5, Length = n.Length * 5 }));
        clip.Track("Bass").AddRange(Line(480, 45, 41, 36, 43));
        var parts = MidiFileReader.Read(MidiWriter.Write(clip, 128), "Idea");

        Assert.Equal(["Keys", "Bass"], parts.Select(p => p.Name));
        Assert.All(parts, p => Assert.Equal(480, p.Ppq));
        Assert.Equal(128, parts[0].Tempo);
        Assert.Equal(12, parts[0].Notes.Count);
        Assert.Equal(new NoteEvent(0, 480, 45, 100), parts[1].Notes[0]);
    }

    [Fact]
    public void Running_status_note_on_at_zero_velocity_and_drum_channel_are_understood()
    {
        // One track: running status after the first note-on, and note-ons at velocity 0 to release (as many files do).
        byte[] track =
        [
            0x00, 0x99, 36, 100,     // kick on channel 10
            0x00, 38, 90,            // snare, running status
            0x60, 36, 0,             // kick off (velocity 0)
            0x00, 38, 0,             // snare off
            0x00, 0x90, 60, 80,      // C4 on channel 1
            0x83, 0x00, 0x80, 60, 0, // 384 ticks later: off
            0x00, 0xFF, 0x2F, 0x00,
        ];
        byte[] file =
        [
            .. "MThd"u8, 0, 0, 0, 6, 0, 0, 0, 1, 0, 96,
            .. "MTrk"u8, 0, 0, 0, (byte)track.Length, .. track,
        ];
        var parts = MidiFileReader.Read(file, "Loop");
        Assert.Equal(2, parts.Count);
        var drums = parts.Single(p => p.Drums);
        Assert.Equal([36, 38], drums.Notes.Select(n => n.Key));
        Assert.All(drums.Notes, n => Assert.Equal(96, n.Length));
        Assert.Equal(new NoteEvent(96, 384, 60, 80), parts.Single(p => !p.Drums).Notes.Single());
    }

    [Fact]
    public void Not_a_midi_file_says_so() =>
        Assert.Throws<InvalidDataException>(() => MidiFileReader.Read("RIFF0000WAVE"u8, "x"));

    // ---- what kind of part ----

    [Fact]
    public void Each_kind_of_part_is_recognised_from_its_notes()
    {
        Assert.Equal(MidiRoles.Chords, PhraseAnalyser.Analyse(Chords(AMinor)).Role);
        Assert.Equal(MidiRoles.Bass, PhraseAnalyser.Analyse(Line(Beat, 33, 33, 41, 41, 36, 36, 43, 43)).Role);
        Assert.Equal(MidiRoles.Melody, PhraseAnalyser.Analyse(Line(Beat, 69, 72, 71, 67, 69, 64, 65, 67, 69, 72, 74, 72)).Role);
        // A 16th-note arpeggio going up and down an A minor chord, four bars.
        var arp = Line(Beat / 4, Enumerable.Range(0, 64).Select(i => new[] { 57, 60, 64, 69, 64, 60 }[i % 6]).ToArray());
        Assert.Equal(MidiRoles.Arp, PhraseAnalyser.Analyse(arp).Role);
        // Kick, snare and hats on the General MIDI notes.
        var beat = Enumerable.Range(0, 16).SelectMany(i => new[]
        {
            new NoteEvent(i * Beat / 2, Beat / 4, 42, 90),
            new NoteEvent(i * Beat / 2, Beat / 4, i % 4 == 2 ? 38 : 36, 110),
        }).ToList();
        Assert.Equal(MidiRoles.Drums, PhraseAnalyser.Analyse(beat).Role);
    }

    [Fact]
    public void Names_say_what_a_part_is_unless_the_notes_plainly_disagree()
    {
        var line = Line(Beat, 60, 62, 64, 65, 67, 65, 64, 62);
        Assert.Equal(MidiRoles.Bass, PhraseAnalyser.Analyse(line, "Wobble bass").Role);
        Assert.Equal(MidiRoles.Arp, PhraseAnalyser.Analyse(line, @"Packs\Arps\Arp 03").Role);
        // A folder called Melodies doesn't make block chords a melody.
        Assert.Equal(MidiRoles.Chords, PhraseAnalyser.Analyse(Chords(AMinor), "Melodies").Role);
        Assert.Equal(MidiRoles.Drums, PhraseAnalyser.Analyse(line, "", drums: true).Role);
        // An 808 that plays a tune is a bass; one that only hits one note is a drum.
        Assert.Equal(MidiRoles.Bass, PhraseAnalyser.Analyse(Line(Beat, 36, 36, 39, 41), "808").Role);
        Assert.Equal(MidiRoles.Drums, PhraseAnalyser.Analyse(Line(Beat, 60, 60, 60, 60), "808").Role);
    }

    [Fact]
    public void A_progression_is_its_chords_in_order_however_long_each_is_held()
    {
        var held = Chords(AMinor);
        // The same chords, struck as eighth notes: the rhythm differs, the chords don't.
        var struck = AMinor.SelectMany((c, bar) => Enumerable.Range(0, 8).SelectMany(e =>
            c.Select(k => new NoteEvent(bar * (long)Bar + e * Beat / 2, Beat / 2, k, 100)))).ToList();
        var a = PhraseAnalyser.Analyse(held);
        var b = PhraseAnalyser.Analyse(struck);
        Assert.Equal(["Am", "F", "C", "G"], a.Chords.Select(c => c.Name));
        Assert.Equal(a.Chords, b.Chords);
        Assert.Contains(a.Key?.Name, new[] { "A minor", "C major" });   // the same notes: either is right
    }

    // ---- fitting a harmony ----

    [Fact]
    public void A_progression_in_another_key_fits_once_moved()
    {
        var song = HarmonyTarget.FromChannels("Song", [new MidiChannel("Keys", "", Ppq, Chords(AMinor), Plugin: "Serum")])!;
        Assert.Equal("Am – F – C – G", song.ChordText());

        var c = PhraseAnalyser.Analyse(Chords(CMinor));
        var fit = HarmonyMatcher.Fit(c.Chroma, c.Chords, song);
        Assert.Equal(-3, fit.Semitones);
        Assert.Equal("down 3 semitones", fit.MoveText);
        Assert.True(fit.Score > 0.9, $"score {fit.Score}");
        Assert.True(fit.ChordScore > 0.95);

        // The same chords starting from the third (C – G – Am – F): still follows the song, which loops.
        var rotated = PhraseAnalyser.Analyse(Chords(AMinor[2], AMinor[3], AMinor[0], AMinor[1]));
        var loop = HarmonyMatcher.Fit(rotated.Chroma, rotated.Chords, song);
        Assert.Equal(0, loop.Semitones);
        Assert.True(loop.ChordScore > 0.95);

        // Chromatic clusters fit nothing well.
        var clusters = PhraseAnalyser.Analyse(Chords([60, 61, 62], [63, 64, 65], [66, 67, 68], [69, 70, 71]));
        Assert.True(HarmonyMatcher.Fit(clusters.Chroma, clusters.Chords, song).Score < fit.Score - 0.3);
    }

    [Fact]
    public void A_melody_fits_by_its_notes_alone()
    {
        var song = HarmonyTarget.FromChannels("Song", [new MidiChannel("Keys", "", Ppq, Chords(AMinor), Plugin: "Serum")])!;
        // An E minor tune (E F# G A B C D) belongs a fifth up: down 7 is the same as up 5.
        var tune = PhraseAnalyser.Analyse(Line(Beat, 64, 66, 67, 71, 69, 67, 66, 64, 71, 72, 74, 71));
        var fit = HarmonyMatcher.Fit(tune.Chroma, tune.Chords, song);
        Assert.Null(fit.ChordScore);
        Assert.Equal(5, fit.Semitones);
        Assert.True(fit.Clash < 0.05);
    }

    // ---- the catalogue ----

    static StoredPhrase Stored(long id, string path, string part, List<NoteEvent> notes, string hints = "")
    {
        var a = PhraseAnalyser.Analyse(notes, hints);
        return new StoredPhrase(id, new MidiSource(path, MidiSource.Project, "FL Studio", 100 - id, 1, 120), part, "Pattern 1",
                                a.Role, a.RoleWhy, null, a.Bars, notes.Count, a.Lowest, a.Highest, a.Key,
                                a.Chroma.Select(c => (byte)(c / a.Chroma.Max() * 255)).ToArray(), PhraseChord.Encode(a.Chords),
                                a.Features.Select(f => (byte)(f * 255)).ToArray(), a.Hash);
    }

    [Fact]
    public void Copies_show_once_and_search_finds_by_name_kind_key_and_chord()
    {
        var catalogue = new MidiCatalogue(
        [
            Stored(1, @"K:\Songs\Night Drive v3.flp", "Keys", Chords(AMinor)),
            Stored(2, @"K:\Songs\Night Drive v2.flp", "Keys", Chords(AMinor)),
            Stored(3, @"K:\Songs\Sunday.flp", "Sub", Line(Beat, 33, 33, 41, 41, 36, 36, 43, 43)),
        ]);
        Assert.Equal(2, catalogue.Entries.Count);
        Assert.Equal(2, catalogue.Entries[0].Copies);
        Assert.Contains("v3", catalogue.Entries[0].SourceName);   // the newest copy

        int n;
        Assert.Equal(3, catalogue.Search(new MidiQuery("basslines"), out n).Single().Entry.Id);
        Assert.Equal(1, catalogue.Search(new MidiQuery("night am"), out n).Single().Entry.Id);
        Assert.Equal(1, catalogue.Search(new MidiQuery("progression"), out n).Single().Entry.Id);
        Assert.Equal(1, catalogue.Search(new MidiQuery(Role: MidiRoles.Chords), out n).Single().Entry.Id);
        Assert.Empty(catalogue.Search(new MidiQuery("nothing like it"), out n));

        MidiCatalogue.Relabel(catalogue.Entries[1], MidiRoles.Melody);
        Assert.Equal(3, catalogue.Search(new MidiQuery(Role: MidiRoles.Melody), out n).Single().Entry.Id);
    }

    [Fact]
    public void Fitting_ranks_the_progression_that_follows_the_song_first()
    {
        var catalogue = new MidiCatalogue(
        [
            Stored(1, @"K:\a.flp", "Clusters", Chords([60, 61, 62], [63, 64, 65], [66, 67, 68], [69, 70, 71])),
            Stored(2, @"K:\b.flp", "Keys", Chords(CMinor)),
            Stored(3, @"K:\c.flp", "Other keys", Chords([60, 64, 67], [62, 66, 69], [64, 68, 71], [66, 70, 73])),
        ]);
        var song = HarmonyTarget.FromChannels("Song", [new MidiChannel("Keys", "", Ppq, Chords(AMinor), Plugin: "Serum")])!;
        var hits = catalogue.Search(new MidiQuery(Fit: song), out _);
        Assert.Equal(2, hits[0].Entry.Id);
        Assert.Equal(-3, hits[0].Fit!.Semitones);
        // The project being fitted to is left out: its own parts fit it, which says nothing.
        Assert.DoesNotContain(catalogue.Search(new MidiQuery(Fit: song, Leave: @"k:\B.flp"), out _), h => h.Entry.Id == 2);
    }

    // ---- indexing ----

    [Fact]
    public void Indexing_reads_midi_libraries_in_place_and_only_reads_changes_again()
    {
        var dir = Fixtures.TempDir();
        var lib = Path.Combine(dir, "MIDI packs", "Trap Chords");
        Directory.CreateDirectory(lib);
        var clip = new MidiClip { Name = "Dark", Ppq = 96 };
        clip.Track("Piano").AddRange(Chords(AMinor));
        var file = Path.Combine(lib, "Dark 01.mid");
        var bytes = MidiWriter.Write(clip, 140);
        File.WriteAllBytes(file, bytes);
        var stamp = File.GetLastWriteTimeUtc(file);

        using var db = new LibraryDb(Path.Combine(dir, "library.db"));
        db.SetList(MidiIndexer.FoldersKey, [Path.Combine(dir, "MIDI packs")]);
        db.Upsert(Scanner.Analyse(FlProjectWithChords(dir)));
        using var index = new MidiIndex(MidiIndex.PathBeside(db.FilePath));
        var indexer = new MidiIndexer(index, db);

        var first = indexer.Run();
        Assert.Equal(2, first.Read);
        Assert.Equal(0, first.Failed);
        var all = index.All();
        var fromFile = all.Single(p => p.Source.Kind == MidiSource.File);
        Assert.Equal("Dark 01", fromFile.Clip);
        Assert.Equal(MidiRoles.Chords, fromFile.Role);
        var fromProject = all.Single(p => p.Source.Kind == MidiSource.Project);
        Assert.Equal("Keys", fromProject.Part);
        Assert.Equal(MidiRoles.Chords, fromProject.Role);
        Assert.Equal(fromFile.Hash, fromProject.Hash);   // the same chords at the same resolution
        Assert.Equal(12, index.Notes(fromFile.Id).Count);

        // Nothing changed: nothing read. The library file was only read, never written.
        Assert.Equal(0, indexer.Run().Read);
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));

        // Your word on what a part is sticks to its notes, for every copy.
        index.SetYourRole(fromFile.Hash, MidiRoles.Melody);
        Assert.All(index.All(), p => Assert.Equal(MidiRoles.Melody, p.YourRole));

        // A file taken out of the library folder is forgotten.
        File.Delete(file);
        Assert.Equal(1, indexer.Run().Forgotten);
        Assert.DoesNotContain(index.All(), p => p.Source.Kind == MidiSource.File);
    }

    static string FlProjectWithChords(string dir)
    {
        var notes = AMinor.SelectMany((c, bar) => c.Select(k => Fixtures.Note((uint)(bar * Bar), 0, Bar, (ushort)k, 100))).SelectMany(b => b).ToArray();
        var b = new Fixtures.FlpBuilder()
            .Ascii(199, "21.2.3.4004")
            .Word(64, 0).Byte(21, 2)
            .Text(201, "Fruity Wrapper").Text(203, "Keys")
            .Data(213, Fixtures.WrapperBlob("Serum", @"C:\Program Files\VstPlugins\Serum_x64.dll", "Xfer Records", [1, 2, 3, 4]))
            .Word(65, 1).Text(193, "Verse")
            .Data(224, notes);
        var path = Path.Combine(dir, "Chords Song.flp");
        File.WriteAllBytes(path, b.Build());
        return path;
    }
}
