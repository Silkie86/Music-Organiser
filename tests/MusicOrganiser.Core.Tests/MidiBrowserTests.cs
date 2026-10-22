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
    public void A_note_the_chord_itself_plays_is_never_marked_as_rubbing()
    {
        // Em9 (E G B D F#) for a bar, then Baug (B D# G).
        var chords = Chords([40, 43, 47, 50, 54], [47, 51, 55]);
        // F#, the 9th, is a semitone from G, but the chord plays it too: it sits in the chord.
        Assert.False(HarmonyMatcher.Rubs(0, Bar, 66, chords));
        // F isn't in the chord and rubs against E and F#.
        Assert.True(HarmonyMatcher.Rubs(0, Bar, 65, chords));
        // E over Baug rubs against its D#, even though the bar before had an E.
        Assert.True(HarmonyMatcher.Rubs(Bar, Bar, 64, chords));
        // A note over a chord in the whole bar before ends where that chord changes, so it's judged on that chord only.
        Assert.False(HarmonyMatcher.Rubs(0, Bar, 64, chords));
    }

    [Fact]
    public void A_midi_file_reads_back_track_by_track()
    {
        var clip = new MidiClip { Name = "Idea", Ppq = 480 };
        clip.Track("Keys").AddRange(Chords(AMinor).Select(n => n with { Start = n.Start * 5, Length = n.Length * 5 }));
        clip.Track("Bass").AddRange(Line(480, 45, 41, 36, 43));
        var parts = MidiFileReader.Read(MidiWriter.Write(clip, 128), "Idea");

        Assert.Equal(["Keys", "Bass"], parts.Select(p => p.Name));
        Assert.All(parts, p => Assert.Equal(480, p.Ppq));
        Assert.Null(parts[0].Tempo);   // the app's MIDI files carry no tempo, so a DAW never asks to import one
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
    public void Running_status_goes_on_after_a_lyric()
    {
        // Karaoke files put lyrics between the notes and carry on with running status after them.
        byte[] track =
        [
            0x00, 0x90, 60, 80,          // C4 on
            0x00, 0xFF, 0x05, 2, (byte)'L', (byte)'a',   // a lyric
            0x60, 60, 0,                 // C4 off, running status
            0x00, 62, 80,                // D4 on
            0x00, 0xF0, 1, 0xF7,         // a sysex
            0x60, 62, 0,                 // D4 off
            0x00, 0xFF, 0x2F, 0x00,
        ];
        byte[] file =
        [
            .. "MThd"u8, 0, 0, 0, 6, 0, 0, 0, 1, 0, 96,
            .. "MTrk"u8, 0, 0, 0, (byte)track.Length, .. track,
        ];
        var notes = MidiFileReader.Read(file, "Song").Single().Notes;
        Assert.Equal([new NoteEvent(0, 96, 60, 80), new NoteEvent(96, 96, 62, 80)], notes);
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
        // Kick and snare on the beats, hats in between: nothing hit at once, still drums.
        var offbeats = Enumerable.Range(0, 16).Select(i => new NoteEvent(i * Beat / 2, Beat / 4, i % 2 == 1 ? 42 : i % 4 == 2 ? 38 : 36, 100)).ToList();
        Assert.Equal(MidiRoles.Drums, PhraseAnalyser.Analyse(offbeats).Role);
    }

    [Fact]
    public void Low_basslines_and_chords_on_the_drum_notes_are_not_taken_for_drums()
    {
        // C minor around C2 is C, Eb, G and Bb: 36, 39, 43 and 46, the General MIDI kick, snare and hat notes.
        Assert.Equal(MidiRoles.Bass, PhraseAnalyser.Analyse(Line(Beat / 2, 36, 39, 43, 46, 43, 39, 36, 36, 39, 43, 46, 48, 46, 43, 39, 36)).Role);
        Assert.Equal(MidiRoles.Bass, PhraseAnalyser.Analyse(Line(Beat, 36, 36, 39, 39, 43, 43, 46, 46)).Role);
        // In octaves.
        var octaves = Line(Beat / 2, 36, 39, 43, 46, 43, 39, 36, 39).SelectMany(n => new[] { n, n with { Key = n.Key + 12 } }).ToList();
        Assert.NotEqual(MidiRoles.Drums, PhraseAnalyser.Analyse(octaves).Role);
        // Low Cm7 chords.
        Assert.Equal(MidiRoles.Chords, PhraseAnalyser.Analyse(Chords([36, 39, 43, 46], [36, 39, 43, 46], [34, 38, 41, 46], [34, 38, 41, 46])).Role);
        // Named as a bassline: a bassline, whatever its notes.
        var hatty = Line(Beat / 2, 36, 46, 39, 46, 36, 46, 39, 46);
        Assert.Equal(MidiRoles.Drums, PhraseAnalyser.Analyse(hatty).Role);
        Assert.Equal(MidiRoles.Bass, PhraseAnalyser.Analyse(hatty, "Sub bass").Role);
    }

    [Fact]
    public void A_track_named_after_a_drum_sound_is_drums_wherever_its_notes_sit()
    {
        // A tambourine on a sampler or drum grid, played low: the notes alone look like a bassline.
        var low = Line(Beat / 2, 36, 36, 38, 36, 36, 36, 38, 36);
        Assert.Equal(MidiRoles.Bass, PhraseAnalyser.Analyse(low).Role);
        Assert.Equal(MidiRoles.Drums, PhraseAnalyser.Analyse(low, "Tamb Selector Grid Tamb Selector Grid 79").Role);
        Assert.Equal(MidiRoles.Drums, PhraseAnalyser.Analyse(low, "Chh Selector Grid").Role);
        Assert.Equal(MidiRoles.Drums, PhraseAnalyser.Analyse(Line(Beat / 4, 60, 62, 60, 64, 60, 62, 60, 64), "Congas").Role);
        // A style word isn't a drum sound: a tune in a "Beats" folder stays a tune.
        Assert.Equal(MidiRoles.Melody, PhraseAnalyser.Analyse(Line(Beat, 69, 72, 71, 67, 69, 64, 65, 67, 69, 72, 74, 72), "Loop 01 Beats").Role);
        // A drum word and a tuned one: the notes decide, as before.
        Assert.Equal(MidiRoles.Bass, PhraseAnalyser.Analyse(low, "Kick bass").Role);
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

    // A tune over Am – F – C – G that follows the chords (chord notes, bar by bar), and the same bars in the opposite
    // order: the same notes overall, so only playing them against the chords in time tells them apart.
    static readonly int[][] Following = [[69, 72, 76, 72], [65, 69, 72, 69], [67, 72, 76, 72], [67, 71, 74, 71]];
    static List<NoteEvent> Tune(params int[][] bars) =>
        bars.SelectMany((b, bar) => b.Select((k, i) => new NoteEvent(bar * (long)Bar + i * Beat, Beat, k, 100))).ToList();

    [Fact]
    public void A_tune_that_follows_the_chords_in_time_beats_the_same_notes_in_the_wrong_places()
    {
        var song = HarmonyTarget.FromChannels("Song", [new MidiChannel("Keys", "", Ppq, Chords(AMinor), Plugin: "Serum")])!;
        var follows = Tune(Following);
        var against = Tune(Following.Reverse().ToArray());
        var a = PhraseAnalyser.Analyse(follows);
        var b = PhraseAnalyser.Analyse(against);
        // Ignoring time, they're the same.
        Assert.Equal(HarmonyMatcher.Fit(a.Chroma, a.Chords, song).Score, HarmonyMatcher.Fit(b.Chroma, b.Chords, song).Score, 2);

        var good = HarmonyMatcher.Refine(a.Chroma, a.Chords, follows, MidiRoles.Melody, song);
        var bad = HarmonyMatcher.Refine(b.Chroma, b.Chords, against, MidiRoles.Melody, song);
        Assert.Equal(0, good.Semitones);
        Assert.True(good.Together > 0.95, $"together {good.Together}");
        Assert.True(good.Score > bad.Score + 0.1, $"{good.Score} vs {bad.Score}");
    }

    [Fact]
    public void A_part_is_lined_up_where_it_fits_in_the_song()
    {
        // The chords only come in at bar 5.
        var later = Chords(AMinor).Select(n => n with { Start = n.Start + 4 * Bar }).ToList();
        var song = HarmonyTarget.FromChannels("Song", [new MidiChannel("Keys", "", Ppq, later, Plugin: "Serum")])!;
        var tune = Tune(Following);
        var a = PhraseAnalyser.Analyse(tune);
        var fit = HarmonyMatcher.Refine(a.Chroma, a.Chords, tune, MidiRoles.Melody, song);
        Assert.Equal(4 * Bar, fit.At);
        Assert.True(fit.Together > 0.95);
    }

    [Fact]
    public void Parts_are_offered_in_the_octave_that_kind_of_part_plays_in_the_song()
    {
        var song = HarmonyTarget.FromChannels("Song",
        [
            new MidiChannel("Bass", "", Ppq, Line(Bar, 45, 41, 36, 43), Plugin: "Serum"),
            new MidiChannel("Keys", "", Ppq, Chords(AMinor), Plugin: "Serum"),
        ])!;
        Assert.InRange(song.Registers[MidiRoles.Bass], 40, 43);

        // A bassline saved two octaves up goes down next to the song's bass.
        var high = Line(Bar, 69, 65, 60, 67);
        var bass = PhraseAnalyser.Analyse(high);
        Assert.Equal(-2, HarmonyMatcher.Refine(bass.Chroma, bass.Chords, high, MidiRoles.Bass, song).Octaves);

        // A tune saved low goes up above the chords, where melodies sit.
        var low = Tune(Following).Select(n => n with { Key = n.Key - 24 }).ToList();
        var tune = PhraseAnalyser.Analyse(low);
        Assert.Equal(2, HarmonyMatcher.Refine(tune.Chroma, tune.Chords, low, MidiRoles.Melody, song).Octaves);

        // Fitting to one part: the same kind of part goes where it is.
        var part = HarmonyTarget.FromPart("Keys", MidiRoles.Chords, Chords(AMinor), PhraseAnalyser.Analyse(Chords(AMinor)).Chroma, [], null);
        var upTwo = Chords(AMinor).Select(n => n with { Key = n.Key + 24 }).ToList();
        var chords = PhraseAnalyser.Analyse(upTwo);
        Assert.Equal(-2, HarmonyMatcher.Refine(chords.Chroma, chords.Chords, upTwo, MidiRoles.Chords, part).Octaves);
    }

    [Fact]
    public void The_closer_look_reranks_the_best_fits_with_their_notes_and_shuffle_picks_at_random()
    {
        var notes = new Dictionary<long, List<NoteEvent>>
        {
            [1] = Tune(Following.Reverse().ToArray()),
            [2] = Tune(Following),
        };
        var catalogue = new MidiCatalogue([Stored(1, @"K:.flp", "Backwards", notes[1]), Stored(2, @"K:.flp", "Follows", notes[2])]);
        var song = HarmonyTarget.FromChannels("Song", [new MidiChannel("Keys", "", Ppq, Chords(AMinor), Plugin: "Serum")])!;
        var hits = catalogue.Search(new MidiQuery(Fit: song, Notes: e => notes[e.Id]), out _);
        Assert.Equal([2L, 1L], hits.Select(h => h.Entry.Id));
        Assert.NotNull(hits[0].Fit!.Together);

        var many = new MidiCatalogue(Enumerable.Range(1, 40).Select(i => Stored(i, $@"K:\{i}.flp", $"Part {i}", Line(Beat, 40 + i, 62, 64))).ToList());
        var first = many.Search(new MidiQuery(Limit: 10, Shuffle: 1), out var n).Select(h => h.Entry.Id).ToList();
        Assert.Equal(40, n);
        Assert.Equal(10, first.Distinct().Count());
        Assert.NotEqual(Enumerable.Range(1, 10).Select(i => (long)i), first);   // not just the newest ten
        Assert.Equal(first, many.Search(new MidiQuery(Limit: 10, Shuffle: 1), out _).Select(h => h.Entry.Id));
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

    [Fact]
    public void Empty_bars_either_side_are_cut_off_and_long_gaps_split_a_part()
    {
        // Bars 3–4 play, then 6 empty bars, then bars 11–12: two phrases, each starting on its first bar.
        var notes = Chords(AMinor[0], AMinor[1]).Select(n => n with { Start = n.Start + 2 * Bar })
            .Concat(Chords(AMinor[2], AMinor[3]).Select(n => n with { Start = n.Start + 10 * Bar })).ToList();
        var parts = PhraseAnalyser.Split(notes).ToList();
        Assert.Equal(["bars 3–4", "bars 11–12"], parts.Select(p => p.Where));
        Assert.All(parts, p => Assert.Equal(0, p.Notes[0].Start));
        Assert.All(parts, p => Assert.Equal(2, PhraseAnalyser.BarsOf(p.Notes)));

        // A short gap stays inside the phrase; a part on its own is the whole part.
        var close = Chords(AMinor[0]).Concat(Chords(AMinor[1]).Select(n => n with { Start = n.Start + 3 * Bar })).ToList();
        Assert.Equal([""], PhraseAnalyser.Split(close).Select(p => p.Where));

        // A long part's 8-bar pieces lose their empty bars too: notes only in bars 15–16 of 9–16 come out as "bars 15–16".
        var song = Chords([.. AMinor, .. AMinor]).Concat(Chords(CMinor[0], CMinor[1]).Select(n => n with { Start = n.Start + 14 * Bar })).ToList();
        var pieces = PhraseAnalyser.Split(song).ToList();
        Assert.Equal(["bars 1–8", "bars 15–16"], pieces.Select(p => p.Where));
        Assert.Equal(2, PhraseAnalyser.BarsOf(pieces[1].Notes));
    }

    // ---- longer phrases ----

    static TrackClip Clip(string name, int bar, List<NoteEvent> notes) => new(name, bar * (long)Bar, 4L * Bar, notes);

    [Fact]
    public void Clips_that_play_back_to_back_on_a_track_join_into_one_phrase()
    {
        var verse = Chords(AMinor);
        var hook = Chords(CMinor);
        TrackClip[] clips =
        [
            Clip("Verse", 0, verse), Clip("Hook", 4, hook), Clip("Verse", 8, verse), Clip("Hook", 12, hook),
            // After a long gap, the verse twice: the same clip again, which is listed on its own already.
            Clip("Verse", 22, verse), Clip("Verse", 26, verse),
        ];

        var joined = PhraseJoiner.Phrases("Keys", clips).Single();
        Assert.Equal("Verse → Hook → Verse → Hook", joined.Clip);
        Assert.Equal(16, PhraseAnalyser.BarsOf(joined.Notes));
        Assert.Equal([0L, 4L * Bar, 8L * Bar, 12L * Bar], joined.Members.Select(m => m.Start));
        // Each clip is known by the hash it has when it's listed on its own.
        var alone = PhraseAnalyser.HashOf(PhraseAnalyser.Split(PhraseAnalyser.Normalise(verse, Ppq)).Single().Notes);
        Assert.Equal(alone, joined.Members[0].Hash);
        Assert.Equal(joined.Members, PhraseMember.Decode(PhraseMember.Encode(joined.Members)));
    }

    [Fact]
    public void A_long_part_is_cut_where_its_phrases_end_not_every_8_bars()
    {
        // A 4-bar intro on a low A, then one 8-bar progression three times: Am F C G | Am F E Am.
        int[] e = [52, 56, 59];
        var intro = Line(Beat, Enumerable.Repeat(45, 16).ToArray());
        var progression = Chords([.. AMinor, AMinor[0], AMinor[1], e, AMinor[0]]);
        var song = intro.Concat(Enumerable.Range(0, 3).SelectMany(i => progression.Select(n => n with { Start = n.Start + (4 + 8 * i) * Bar }))).ToList();

        var pieces = PhraseAnalyser.Split(song).ToList();
        // Every 8 bars would give bars 1–8 (the intro and half the progression), 9–16 and so on.
        Assert.Equal(["bars 1–4", "bars 5–12"], pieces.Select(p => p.Where));
        Assert.Equal(PhraseAnalyser.HashOf(progression), PhraseAnalyser.HashOf(pieces[1].Notes));
        Assert.Equal(4L * Bar, pieces[1].From);
    }

    [Fact]
    public async Task The_windows_notes_never_wait_behind_listening_reading_every_parts_notes()
    {
        // Listening to the MIDI reads every part's notes; the window reads them as rows come into view (2026-10-22: the
        // window froze up to 1.3 s while it listened). Each has its own way in.
        var dir = Fixtures.TempDir();
        var notes = Enumerable.Range(0, 8).Select(i => new NoteEvent(i * 96L, 96, 60 + i, 100)).ToList();
        var phrase = new IndexedPhrase("Keys", "Verse", notes, PhraseAnalyser.Analyse(notes, "Keys"), false);
        using var index = new MidiIndex(Path.Combine(dir, "midi.db"));
        index.Replace(new MidiSource(Path.Combine(dir, "a.mid"), MidiSource.File, "MIDI", 1, 1, null), MidiIndexer.Version, [phrase]);
        var id = index.All().Single().Id;
        Assert.Equal(8, index.NotesInBackground(id).Count);

        var busy = typeof(MidiIndex).GetField("_backgroundNotesLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(index)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var listening = Task.Run(() =>
        {
            lock (busy)
            {
                held.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });
        held.Wait();
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(8, index.Notes(id).Count);
            Assert.True(clock.ElapsedMilliseconds < 1000, $"took {clock.ElapsedMilliseconds} ms");
        }
        finally
        {
            release.Set();
            await listening;
        }
    }

    [Fact]
    public void An_fl_songs_patterns_played_one_after_another_become_a_phrase_and_each_knows_it()
    {
        var dir = Fixtures.TempDir();
        var path = FlProjectWithPhrases(dir);

        var phrases = MidiIndexer.FromProject(path);
        Assert.Equal(["Verse", "Hook", "Verse → Hook → Verse → Hook"], phrases.Select(p => p.Clip));
        Assert.Equal(16, phrases[2].Analysis.Bars);
        Assert.Equal([phrases[0].Analysis.Hash, phrases[1].Analysis.Hash], phrases[2].Members.Select(m => m.Hash).Distinct());

        // Saved and read back, the verse and the hook each know the longer phrase they're part of.
        using var index = new MidiIndex(Path.Combine(dir, "midi.db"));
        index.Replace(new MidiSource(path, MidiSource.Project, "FL Studio", 1, 1, 120), MidiIndexer.Version, phrases);
        var catalogue = new MidiCatalogue(index.All());
        var whole = catalogue.Entries.Single(e => e.IsJoined);
        Assert.Equal(2, whole.ClipCount);
        var verseEntry = catalogue.Entries.Single(e => e.Phrase.Clip == "Verse");
        Assert.Equal([whole], verseEntry.Within!);
        Assert.Equal([whole], catalogue.Search(new MidiQuery(Within: verseEntry), out _).Select(h => h.Entry));
        Assert.Null(whole.Within);
    }

    [Fact]
    public void Clips_back_to_back_in_bitwigs_arranger_become_a_phrase_too()
    {
        // The FL song above, as the app writes it for Bitwig: the verse and hook clips one after another on the Keys track.
        var dir = Fixtures.TempDir();
        var song = Readers.FlStudio.ReadSong(FlProjectWithPhrases(dir));
        var path = Path.Combine(dir, "Song", "Song.bwproject");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Export.BitwigProject.FromFl(song, Fixtures.BitwigTemplate(), _ => null).Bytes);

        var joined = MidiIndexer.FromProject(path).Single(p => p.Members.Count > 0);
        Assert.Equal("Keys", joined.Part);
        Assert.Equal(16, joined.Analysis.Bars);
        Assert.Equal(4, joined.Members.Count);
    }

    [Fact]
    public void A_song_is_read_from_disk_once_while_its_midi_is_read()
    {
        // Each big read is memory to clean up, and those clean-ups pause the window: reading a song's clips, playlist and
        // channels shares one read of the file.
        var path = FlProjectWithPhrases(Fixtures.TempDir());
        var reader = new FlStudioReader();
        using (ReadOnce.Scope())
        {
            var clips = reader.ExtractMidi(path).Count;
            File.Move(path, path + ".moved");
            Assert.Equal(clips, reader.ExtractMidi(path).Count);
            Assert.Equal(4, reader.ReadPlaylist(path).Items.Count);
        }
        Assert.Throws<FileNotFoundException>(() => reader.ExtractMidi(path));
    }

    [Fact]
    public void A_bitwig_song_read_into_a_borrowed_buffer_reads_the_same()
    {
        // While MIDI is read, a Bitwig song goes into a buffer borrowed from a pool, which is longer than the file and
        // holds whatever the song before left in it.
        var dir = Fixtures.TempDir();
        var song = Readers.FlStudio.ReadSong(FlProjectWithPhrases(dir));
        var path = Path.Combine(dir, "Song", "Song.bwproject");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Export.BitwigProject.FromFl(song, Fixtures.BitwigTemplate(), _ => null).Bytes);
        Assert.NotEqual(0, new FileInfo(path).Length & (new FileInfo(path).Length - 1));   // not a power of two: the buffer is longer

        var reader = new BitwigReader();
        var alone = reader.ExtractMidi(path);
        var aloneInfo = reader.Read(path);
        var aloneLanes = reader.ReadArrangement(path).Lanes.Count;
        var junk = System.Buffers.ArrayPool<byte>.Shared.Rent((int)new FileInfo(path).Length);
        Array.Fill(junk, (byte)0x78);
        System.Buffers.ArrayPool<byte>.Shared.Return(junk);
        using (ReadOnce.Scope())
        {
            var borrowed = reader.ExtractMidi(path);
            Assert.Equal(alone.Count, borrowed.Count);
            Assert.Equal(alone.SelectMany(c => c.Tracks.SelectMany(t => t.Value)), borrowed.SelectMany(c => c.Tracks.SelectMany(t => t.Value)));
            Assert.Equal(aloneLanes, reader.ReadArrangement(path).Lanes.Count);
            var info = reader.Read(path);
            Assert.Equal(aloneInfo.Plugins.Count, info.Plugins.Count);
            Assert.Equal(aloneInfo.Version, info.Version);
        }
    }

    [Fact]
    public void A_bitwig_songs_parts_are_fitted_against_where_they_play_in_the_arranger()
    {
        // Read clip by clip, a track's clips sat end to end, launcher clips too, so tracks didn't line up in time
        // (Solomon, 2026-10-19: "it shows midi with 96 percent but doesnt fit").
        var dir = Fixtures.TempDir();
        var flp = FlProjectWithPhrases(dir);
        var song = Readers.FlStudio.ReadSong(flp);
        var path = Path.Combine(dir, "Song", "Song.bwproject");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Export.BitwigProject.FromFl(song, Fixtures.BitwigTemplate(), _ => null).Bytes);

        var keys = Assert.Single(PhraseJoiner.ArrangedChannels(path)!);
        Assert.Equal("Keys", keys.Name);
        Assert.Equal(PhraseAnalyser.Ppq, keys.Ppq);
        // Verse, Hook, Verse, Hook, a bar apart each chord: as FL's playlist plays them.
        var fl = Readers.FlStudio.ExtractChannels(flp).Single(c => c.Name == "Keys");
        Assert.Equal(PhraseAnalyser.Normalise(fl.Notes, fl.Ppq, keepPlace: true).Select(n => (n.Start, n.Key)), keys.Notes.Select(n => (n.Start, n.Key)));
        Assert.Equal(15 * Bar, keys.Notes.Max(n => n.Start));

        var target = HarmonyTarget.FromChannels("Song", PhraseJoiner.ArrangedChannels(path)!)!;
        Assert.Equal("Keys", Assert.Single(target.Parts).Name);
        Assert.Null(PhraseJoiner.ArrangedChannels(flp));   // FL Studio's channels follow its playlist already
    }

    [Fact]
    public void A_parts_notes_never_wait_behind_a_long_read_of_every_part()
    {
        // The window asks for a row's notes as it comes into view; a read of the whole list (after a song is read again)
        // held the same lock, and Projects froze for 5 s (Solomon's log, 2026-10-19).
        var dir = Fixtures.TempDir();
        using var db = new LibraryDb(Path.Combine(dir, "library.db"));
        db.Upsert(Scanner.Analyse(FlProjectWithChords(dir)));
        using var index = new MidiIndex(MidiIndex.PathBeside(db.FilePath));
        new MidiIndexer(index, db).Run();
        var part = index.All().First();

        var readLock = typeof(MidiIndex).GetField("_readLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(index)!;
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var longRead = Task.Run(() =>
        {
            lock (readLock)
            {
                holding.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });
        holding.Wait();
        var notes = Task.Run(() => index.Notes(part.Id, part.Hash));
        Assert.True(notes.Wait(TimeSpan.FromSeconds(2)), "a part's notes waited for the long read");
        Assert.NotEmpty(notes.Result);
        release.Set();
        longRead.Wait();
    }

    [Fact]
    public void A_midi_db_from_before_phrases_were_joined_gets_the_new_column()
    {
        var path = Path.Combine(Fixtures.TempDir(), "midi.db");
        using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE sources (path TEXT PRIMARY KEY COLLATE NOCASE, kind TEXT NOT NULL, daw TEXT, mtime INTEGER, size INTEGER,
                                      tempo REAL, version INTEGER, error TEXT);
                CREATE TABLE phrases (id INTEGER PRIMARY KEY, source TEXT NOT NULL COLLATE NOCASE REFERENCES sources(path) ON DELETE CASCADE,
                    part TEXT, clip TEXT, role TEXT, role_why TEXT, drums INTEGER, bars INTEGER, notes INTEGER, lowest INTEGER, highest INTEGER,
                    key_tonic INTEGER, key_minor INTEGER, key_conf REAL, chroma BLOB, chords TEXT, features BLOB, hash TEXT, data BLOB);
                """;
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var index = new MidiIndex(path);
        var notes = Chords(AMinor);
        index.Replace(new MidiSource("a.mid", MidiSource.File, "", 1, 1, null), MidiIndexer.Version,
                      [new IndexedPhrase("Keys", "A", notes, PhraseAnalyser.Analyse(notes), false) { Members = [new("ABC", 0, Bar)] }]);
        Assert.Equal("ABC@0+384", index.All().Single().Members);
    }

    [Fact]
    public void Each_track_of_a_midi_file_is_its_own_part_to_fit()
    {
        var dir = Fixtures.TempDir();
        var clip = new MidiClip { Name = "Full idea", Ppq = 96 };
        clip.Track("Keys").AddRange(Chords(CMinor));
        clip.Track("Bass").AddRange(Line(Bar, 36, 32, 39, 34));
        clip.Track("Lead").AddRange(Line(Beat, 72, 75, 79, 77, 75, 72, 70, 72, 75, 79, 82, 79));
        var file = Path.Combine(dir, "Full idea.mid");
        File.WriteAllBytes(file, MidiWriter.Write(clip, 120));

        var phrases = MidiIndexer.FromFile(file, [dir]);
        Assert.Equal(["Keys", "Bass", "Lead"], phrases.Select(p => p.Part));
        Assert.Equal([MidiRoles.Chords, MidiRoles.Bass, MidiRoles.Melody], phrases.Select(p => p.Analysis.Role));
        // Each one fits a song on its own: the chords and the bassline both move down 3 into A minor.
        var song = HarmonyTarget.FromChannels("Song", [new MidiChannel("Keys", "", Ppq, Chords(AMinor), Plugin: "Serum")])!;
        Assert.Equal(-3, HarmonyMatcher.Fit(phrases[0].Analysis.Chroma, phrases[0].Analysis.Chords, song).Semitones);
        Assert.Equal(-3, HarmonyMatcher.Fit(phrases[1].Analysis.Chroma, phrases[1].Analysis.Chords, song).Semitones);
    }

    [Fact]
    public void Only_the_newest_save_of_a_song_is_read_and_failed_files_can_be_tried_again()
    {
        var dir = Fixtures.TempDir();
        var first = FlProjectWithChords(dir);
        var second = Path.Combine(dir, "Chords Song_2.flp");
        File.Copy(first, second);
        File.SetLastWriteTimeUtc(second, File.GetLastWriteTimeUtc(first).AddHours(1));
        var lib = Path.Combine(dir, "MIDI");
        Directory.CreateDirectory(lib);
        var broken = Path.Combine(lib, "Broken.mid");
        File.WriteAllText(broken, "RIFF0000WAVEfmt , a sound file named as MIDI");
        File.WriteAllBytes(Path.Combine(lib, "Empty.mid"), []);   // empty files are skipped, not failures

        using var db = new LibraryDb(Path.Combine(dir, "library.db"));
        db.SetList(MidiIndexer.FoldersKey, [lib]);
        db.Upsert(Scanner.Analyse(first));
        db.Upsert(Scanner.Analyse(second));
        using var index = new MidiIndex(MidiIndex.PathBeside(db.FilePath));
        var indexer = new MidiIndexer(index, db);

        var run = indexer.Run();
        Assert.Equal(1, run.Failed);
        Assert.Equal([second], index.All().Select(p => p.Source.Path).Distinct());
        var failed = Assert.Single(index.Failures());
        Assert.Equal("Broken.mid", failed.Name);
        Assert.Contains("damaged", failed.Why);

        // An unchanged failure isn't read again on its own, only when asked; once fixed it leaves the list.
        Assert.Equal(0, indexer.Run().Read);
        var retry = indexer.Run(retryFailed: true);
        Assert.Equal((1, 1), (retry.Read, retry.Failed));
        var fixedClip = new MidiClip { Name = "x", Ppq = 96 };
        fixedClip.Track("Keys").AddRange(Chords(AMinor));
        File.WriteAllBytes(broken, MidiWriter.Write(fixedClip));
        Assert.Equal(1, indexer.Run().Read);
        Assert.Empty(index.Failures());

        // Every save, when asked for.
        db.SetSetting(MidiIndexer.OlderSavesKey, "1");
        Assert.Equal(1, indexer.Run().Read);
        Assert.Contains(index.All(), p => p.Source.Path == first);
        db.SetSetting(MidiIndexer.OlderSavesKey, "0");
        Assert.Equal(1, indexer.Run().Forgotten);
    }

    /// <summary>An FL song whose "Keys" plays pattern Verse (Am F C G), then Hook (Cm Ab Eb Bb), then both again, 4 bars each.</summary>
    internal static string FlProjectWithPhrases(string dir)
    {
        static byte[] Pattern(int[][] chords) =>
            chords.SelectMany((c, bar) => c.Select(k => Fixtures.Note((uint)(bar * Bar), 0, Bar, (ushort)k, 100))).SelectMany(b => b).ToArray();
        const uint Four = 4 * Bar;
        var b = new Fixtures.FlpBuilder()
            .Ascii(199, "25.1.0.4868")
            .Word(64, 0).Byte(21, 2)
            .Text(201, "Fruity Wrapper").Text(203, "Keys")
            .Data(213, Fixtures.WrapperBlob("Serum", @"C:\Program Files\VstPlugins\Serum_x64.dll", "Xfer Records", [1, 2, 3, 4]))
            .Word(65, 1).Text(193, "Verse").Data(224, Pattern(AMinor))
            .Word(65, 2).Text(193, "Hook").Data(224, Pattern(CMinor))
            .Data(233, [.. Fixtures.PlaylistItem25(0, 20481, Four), .. Fixtures.PlaylistItem25(Four, 20482, Four),
                        .. Fixtures.PlaylistItem25(2 * Four, 20481, Four), .. Fixtures.PlaylistItem25(3 * Four, 20482, Four)]);
        var path = Path.Combine(dir, "Phrases.flp");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    /// <summary>Am F C G on Keys, and a lead in C minor on Lead that clashes with it: a song with two parts to pick from.</summary>
    internal static string FlProjectWithChordsAndLead(string dir)
    {
        var chords = AMinor.SelectMany((c, bar) => c.Select(k => Fixtures.Note((uint)(bar * Bar), 0, Bar, (ushort)k, 100)));
        int[] lead = [75, 72, 75, 79, 80, 79, 75, 72];
        var melody = lead.Select((k, i) => Fixtures.Note((uint)(i * 2 * Beat), 1, 2 * Beat, (ushort)k, 100));
        var b = new Fixtures.FlpBuilder()
            .Ascii(199, "21.2.3.4004")
            .Word(64, 0).Byte(21, 2)
            .Text(201, "Fruity Wrapper").Text(203, "Keys")
            .Data(213, Fixtures.WrapperBlob("Serum", @"C:\Program Files\VstPlugins\Serum_x64.dll", "Xfer Records", [1, 2, 3, 4]))
            .Word(64, 1).Byte(21, 2)
            .Text(201, "Fruity Wrapper").Text(203, "Lead")
            .Data(213, Fixtures.WrapperBlob("Serum", @"C:\Program Files\VstPlugins\Serum_x64.dll", "Xfer Records", [1, 2, 3, 4]))
            .Word(65, 1).Text(193, "Verse")
            .Data(224, chords.Concat(melody).SelectMany(n => n).ToArray());
        var path = Path.Combine(dir, "Two Parts.flp");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    internal static string FlProjectWithChords(string dir)
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

    [Fact]
    public void A_bass_whose_sound_has_a_drum_word_inside_another_word_is_still_a_bass()
    {
        // "Phat" has "hat" in it and "808 Sub" has 808: those basslines were taken for drums and left out of the song's
        // harmony (code review, 2026-10-19).
        NoteEvent[] line = [new(0, 96, 36, 100), new(96, 96, 39, 100), new(192, 96, 43, 100)];
        Assert.Equal("Bass", new MidiChannel("Phat Bass", "", 96, line).Kind);
        Assert.Equal("Bass", new MidiChannel("Low end", "808 Sub", 96, line).Kind);
        Assert.Equal("Bass", new MidiChannel("808", "", 96, line).Kind);   // an 808 that moves is a bassline
        Assert.NotEqual("Drums", new MidiChannel("Percussive Keys", "", 96, [new(0, 96, 60, 100), new(0, 96, 64, 100), new(0, 96, 67, 100)]).Kind);
        // Real drums still are.
        NoteEvent[] hit = [new(0, 24, 36, 100), new(96, 24, 36, 100)];
        Assert.Equal("Drums", new MidiChannel("808", "", 96, hit).Kind);
        Assert.Equal("Drums", new MidiChannel("Hihat 2", "", 96, line).Kind);
        Assert.Equal("Drums", new MidiChannel("Open hats", "", 96, line).Kind);
        Assert.Equal("Drums", new MidiChannel("Perc1", "", 96, line).Kind);
        Assert.Equal("Drums", new MidiChannel("Kick2", "", 96, line).Kind);
    }

    [Fact]
    public void A_bitwig_file_cut_short_is_never_read_on_into_whatever_its_borrowed_buffer_held_before()
    {
        // The pool's buffer can be twice the file and still hold the last song read; a half-synced file must stop at
        // its own end (code review, 2026-10-19).
        var whole = File.ReadAllBytes(Fixtures.BitwigProject(Fixtures.TempDir()));
        var cut = whole.Length * 3 / 4;
        var borrowed = (byte[])whole.Clone();   // what was read before: this same song, whole
        var doc = BitwigFormat.Decode(borrowed, cut);
        Assert.NotNull(doc.BodyError);
        Assert.Contains("past end", doc.BodyError);
    }

    [Fact]
    public void A_note_said_to_last_millions_of_bars_is_cut_to_a_length_a_part_can_hold()
    {
        // Some FL Studio 6 patterns give a note a length of about two billion ticks; cutting that part into phrases went
        // through every bar, 6 to 17 GB of memory for one old song (Solomon's log, 2026-10-21).
        NoteEvent[] notes = [new(24, 96, 60, 100), new(120, 2_156_314_664, 64, 100), new(400, 96, 67, 100), new(500, 96, 72, 100)];
        var normal = PhraseAnalyser.Normalise(notes, 96);
        Assert.Equal(PhraseAnalyser.MaxHold, normal.Max(n => n.Length));
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.NotEmpty(PhraseAnalyser.Split(normal).ToList());
        // A part that didn't come through Normalise (a Bitwig song's track) is held to the same limit.
        var raw = notes.Select(n => n with { Start = n.Start * 4, Length = n.Length }).ToList();
        Assert.All(PhraseAnalyser.Split(raw).SelectMany(p => p.Notes), n => Assert.True(n.Length <= PhraseAnalyser.MaxHold));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 50_000_000);

        // A real long note, an 80-bar pad dragged into the Playground, keeps every bar.
        var pad = PhraseAnalyser.Normalise([new NoteEvent(0, 80 * Bar, 48, 90), new(0, 80 * Bar, 55, 90), new(0, 80 * Bar, 60, 90)], 96);
        Assert.All(pad, n => Assert.Equal(80 * Bar, n.Length));
    }

    [Fact]
    public void A_songs_midi_is_read_again_when_its_daws_reader_gets_better()
    {
        // A reader fix (FL Studio 8 and older notes at their right size, 2026-10-22) has to reach the MIDI index too, not
        // only the library, so a song's stored version counts its reader's.
        var dir = Fixtures.TempDir();
        using var db = new LibraryDb(Path.Combine(dir, "library.db"));
        db.Upsert(Scanner.Analyse(FlProjectWithChords(dir)));
        using var index = new MidiIndex(MidiIndex.PathBeside(db.FilePath));
        Assert.Equal(1, new MidiIndexer(index, db).Run().Read);
        var (path, stored) = Assert.Single(index.Sources());
        Assert.Equal(MidiIndexer.Version * 100 + Readers.For(path)!.Version, stored.Version);
        Assert.Equal(0, new MidiIndexer(index, db).Run().Read);   // nothing changed: nothing read again

        // As if it was read before the reader's last fix.
        var source = index.All().First(p => p.Source.Path == path).Source;
        index.Replace(source, stored.Version - 1, []);
        Assert.Equal(1, new MidiIndexer(index, db).Run().Read);
    }
}

