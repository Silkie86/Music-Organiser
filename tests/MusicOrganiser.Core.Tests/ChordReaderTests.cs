using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Tests;

/// <summary>
/// The one chord reader (fix 6 of the 2026-10-20 harmony check-up): the chords the MIDI page, the Playground and the index
/// all read, beat by beat, the way players write them. Each case is one thing the half-bar readers before it got wrong.
/// The theory bench in tools/TheoryBench measures the same reader on 387 pop songs (--chords).
/// </summary>
public class ChordReaderTests
{
    const int Ppq = PhraseAnalyser.Ppq, Beat = Ppq, Bar = 4 * Ppq;

    static List<NoteEvent> Held(long start, long length, params int[] keys) =>
        keys.Select(k => new NoteEvent(start, length, k, 100)).ToList();

    static List<NoteEvent> Bars(params int[][] chords) =>
        chords.SelectMany((c, bar) => Held(bar * (long)Bar, Bar, c)).ToList();

    static string Read(IReadOnlyList<NoteEvent> notes, long length) =>
        string.Join(" ", ChordReader.Read(notes, length).Select(c => $"{c.Start / (double)Bar + 1}:{c.Chord.Name}"));

    static List<TimedChord> Chart(string chart) => ChordLane.ParseChart(chart, out _)!;

    // ---- one chord ----

    [Theory]
    [InlineData("Bm7b5", new[] { 47, 62, 65, 69 })]
    [InlineData("G13", new[] { 43, 53, 59, 64 })]
    [InlineData("C7alt", new[] { 48, 52, 58, 61, 68 })]
    [InlineData("G9sus4", new[] { 43, 53, 57, 60 })]     // F/G: an F triad over G is a G9sus4
    [InlineData("G7(b9)", new[] { 43, 53, 59, 62, 68 })]
    [InlineData("G7(#9)", new[] { 43, 53, 59, 62, 70 })]
    [InlineData("G7sus4", new[] { 43, 60, 62, 65 })]
    [InlineData("Bdim7", new[] { 47, 62, 65, 68 })]
    [InlineData("C/E", new[] { 40, 55, 60 })]
    [InlineData("Ebmaj7", new[] { 51, 55, 58, 62 })]
    [InlineData("Bbm7", new[] { 46, 61, 65, 68 })]
    public void A_chord_is_named_as_a_jazz_player_writes_it(string name, int[] keys)
    {
        // The MIDI page's 15 names called these Dm6/B, Em(b9)/G, Fadd9/G and so on.
        Assert.Equal($"1:{name}", Read(Held(0, Bar, keys), Bar));
    }

    [Fact]
    public void A_right_hand_on_its_own_has_no_bass()
    {
        // B3 C4 E4 G4 is Cmaj7 with B underneath, not Cmaj7/B; A C E round middle C is Am, not C6.
        Assert.Equal("1:Cmaj7", Read(Held(0, Bar, 59, 60, 64, 67), Bar));
        Assert.Equal("1:Am 2:F 3:C 4:G", Read(Bars([57, 60, 64], [53, 57, 60], [48, 52, 55], [55, 59, 62]), 4 * Bar));
        // G A C E and G B D E read as minor sevenths: with no bass to say C6 or G6, the m7 is the likelier.
        Assert.Equal("1:Am7 2:C", Read(Held(0, Bar, 55, 57, 60, 64).Concat(Held(Bar, Bar, 55, 60, 64)).ToList(), 2 * Bar));
        Assert.Equal("1:Em7", Read(Held(0, Bar, 55, 59, 62, 64), Bar));
    }

    // ---- in time ----

    [Fact]
    public void A_chord_pushed_an_eighth_early_belongs_to_the_bar_it_pushes_into()
    {
        var chart = Chart("Cmaj7 | Am7 | Dm7 | G7 |");
        var comp = Voicer.Voice(chart, VoicingStyle.Close, CompRhythm.Anticipate, 4 * Bar);
        Assert.Contains(comp, n => n.Start == Bar - Beat / 2);   // the Am7 is struck on the and of 4
        Assert.Equal("1:Cmaj7 2:Am7 3:Dm7 4:G7", Read(comp, 4 * Bar));
    }

    [Fact]
    public void Two_chords_a_bar_are_read_on_their_beats()
    {
        foreach (var rhythm in new[] { CompRhythm.Held, CompRhythm.Anticipate, CompRhythm.Charleston })
            Assert.Equal("1:Dm7 1.5:G7 2:Cmaj7", Read(Voicer.Voice(Chart("Dm7 G7 | Cmaj7 |"), VoicingStyle.Close, rhythm, 2 * Bar), 2 * Bar));
    }

    [Fact]
    public void A_walking_bass_passing_notes_do_not_change_the_chord()
    {
        // Dm7 stays Dm7 while the bass walks D E F F#: the E isn't a 9th and the F# isn't D7's third.
        var chart = Chart("Dm7 G7 | Cmaj7 |");
        var notes = Voicer.Voice(chart, VoicingStyle.Close, CompRhythm.Held, 2 * Bar).Concat(Voicer.Bass(chart, BassStyle.Walking, 2 * Bar)).ToList();
        Assert.Equal("1:Dm7 1.5:G7 2:Cmaj7", Read(notes, 2 * Bar));
        var walkUp = Chart("C Dm7 Em7 F#dim7 | G7 |");
        notes = Voicer.Voice(walkUp, VoicingStyle.Close, CompRhythm.Held, 2 * Bar).Concat(Voicer.Bass(walkUp, BassStyle.Walking, 2 * Bar)).ToList();
        Assert.Equal("1:C 1.25:Dm7 1.5:Em7 1.75:F#dim7 2:G7", Read(notes, 2 * Bar));
    }

    [Fact]
    public void A_gospel_walk_up_is_read_a_chord_a_beat()
    {
        // Each chord a beat long, in big gospel voicings that share most of their notes; the diminished chord is named
        // from the note under the chord it climbs to.
        var notes = Voicer.Voice(Chart("C Dm7 Em7 F#dim7 | G7 |"), VoicingStyle.Gospel, CompRhythm.Held, 2 * Bar);
        Assert.Equal("1:C6/9 1.25:Dm9 1.5:Em9 1.75:F#dim7 2:G13", Read(notes, 2 * Bar));
    }

    [Fact]
    public void A_melody_or_an_arpeggio_in_the_same_part_does_not_make_new_chords()
    {
        var chords = Held(0, Bar, 48, 52, 55).Concat(Held(Bar, Bar, 53, 57, 60));
        var tune = new[] { 76, 77, 79, 81, 81, 79, 77, 76 }.Select((k, i) => new NoteEvent(i * (long)Beat, Beat, k, 100));
        Assert.Equal("1:C 2:F", Read(chords.Concat(tune).OrderBy(n => n.Start).ToList(), 2 * Bar));
        var broken = new[] { 48, 52, 55, 60, 64, 60, 55, 52, 53, 57, 60, 65, 69, 65, 60, 57 }.Select((k, i) => new NoteEvent(i * (long)(Beat / 2), Beat / 2, k, 100)).ToList();
        Assert.Equal("1:C 2:F", Read(broken, 2 * Bar));
    }

    [Fact]
    public void A_note_left_held_by_mistake_is_read_without_filling_memory()
    {
        // A MIDI file whose note-off never comes holds the note to the end of the track, which can be days away.
        var notes = Held(0, Bar, 48, 52, 55).Append(new NoteEvent(Bar, int.MaxValue, 53, 100)).ToList();
        Assert.Equal("C", ChordReader.Read(notes, Bar + (long)int.MaxValue)[0].Chord.Name);
    }

    // ---- where the app keeps them ----

    [Fact]
    public void Melodies_and_basslines_have_no_chords_of_their_own()
    {
        var tune = new[] { 72, 74, 76, 77, 79, 77, 76, 74, 72, 71, 72, 74 }.Select((k, i) => new NoteEvent(i * (long)Beat, Beat, k, 100)).ToList();
        var analysis = PhraseAnalyser.Analyse(tune, "Lead");
        Assert.Equal(MidiRoles.Melody, analysis.Role);
        Assert.Empty(analysis.Chords);
        var chords = PhraseAnalyser.Analyse(Bars([45, 57, 60, 64], [41, 57, 60, 65], [36, 52, 55, 60], [43, 55, 59, 62]), "Keys");
        Assert.Equal(["Am", "F", "C", "G"], chords.Chords.Select(c => c.Name));
    }

    [Fact]
    public void A_songs_chords_leave_its_melody_out()
    {
        // Held C and F with a lead line leaning on D and B: the chords are still C and F, not Cadd9 and Fmaj7(#11).
        var keys = new MidiChannel("Keys", "", Ppq, Bars([36, 52, 55, 60], [41, 57, 60, 65]), Plugin: "Keys");
        var lead = new MidiChannel("Lead", "", Ppq, new[] { 74, 71, 74, 72, 71, 76, 71, 74 }.Select((k, i) => new NoteEvent(i * (long)Beat, Beat, k, 100)).ToList(),
                                   Plugin: "Lead");
        var song = HarmonyTarget.FromChannels("Song", [keys, lead])!;
        Assert.Equal("C – F", song.ChordText());
    }

    [Fact]
    public void The_Playground_hears_the_whole_bassline_under_its_chords()
    {
        // Chords pushed onto the and of 4 with the bass on the one: the bass still names them.
        var section = new PlaygroundSection
        {
            Bars = 2,
            Layers =
            [
                new PlaygroundLayer { Name = "Keys", Role = MidiRoles.Chords, Notes = Voicer.Voice(Chart("C | F |"), VoicingStyle.Close, CompRhythm.Anticipate, 2 * Bar) },
                new PlaygroundLayer { Name = "Bass", Role = MidiRoles.Bass, Notes = [new NoteEvent(0, Bar, 40, 100), new NoteEvent(Bar, Bar, 45, 100)] },
            ],
        };
        Assert.Equal(["C/E", "F/A"], SectionHarmony.Of(section).Lane.Select(c => c.Chord.Name));
    }

    [Fact]
    public void Bars_where_the_chord_part_rests_get_no_chords_from_the_bass_alone()
    {
        // F12 note, 20 Oct: an 808 line on its own in bars 2 and 3, where the chord clip rests, was read as Cm(b9)/Eb.
        var section = new PlaygroundSection
        {
            Bars = 4,
            Layers =
            [
                new PlaygroundLayer { Name = "Keys", Role = MidiRoles.Chords, Notes = Held(0, Bar, 53, 58, 61, 65).Concat(Held(3 * Bar, Bar, 51, 55, 58, 60)).ToList() },
                new PlaygroundLayer
                {
                    Name = "808", Role = MidiRoles.Bass,
                    Notes = new[] { 34, 34, 41, 39, 39, 37, 36, 39, 36, 34, 32, 31, 36, 36, 43, 39 }.Select((k, i) => new NoteEvent(i * (long)Beat, Beat, k, 100)).ToList(),
                },
            ],
        };
        var lane = SectionHarmony.Of(section).Lane;
        Assert.Equal(["Bbm", "Cm7"], lane.Select(c => c.Chord.Name));
        Assert.Equal(3L * Bar, lane[1].Start);
    }

    [Fact]
    public void A_chord_keeps_its_bass_in_the_index_and_reads_older_entries()
    {
        var chords = PhraseChord.Decode("9:291 0:091/4");
        Assert.Equal(["Am7", "C/E"], chords.Select(c => c.Name));
        Assert.Equal("9:291 0:091/4", PhraseChord.Encode(chords));
        Assert.Equal(4, chords[1].Bass);
        Assert.Equal("D/F#", chords[1].Transposed(2).Name);
        // A chord saved before it had a bass reads as in root position.
        Assert.Equal(9, PhraseChord.Decode("9:291")[0].Bass);
    }

    [Fact]
    public void Chord_names_are_spelled_the_way_the_key_is_written()
    {
        var cSharpMinor = ChordSpeller.Spell(0x112, 1)!.Value;   // C# E G#
        Assert.Equal("C#m", cSharpMinor.NameIn(new KeyEstimate(9, false, 1)));   // A major
        Assert.Equal("C#m", cSharpMinor.NameIn(new KeyEstimate(1, true, 1)));    // C# minor
        Assert.Equal("Dbm", cSharpMinor.NameIn(new KeyEstimate(6, false, 1)));   // Gb major
        Assert.Equal("Dbm", cSharpMinor.NameIn(null));
        var gFlat = ChordSpeller.Spell(0x442, 6)!.Value;          // Gb Bb Db
        Assert.Equal("Gb", gFlat.NameIn(new KeyEstimate(1, false, 1)));          // Db major: IV is Gb, not F#
        Assert.Equal("F#", gFlat.NameIn(new KeyEstimate(2, false, 1)));          // D major
        // Searching finds a chord by either spelling.
        Assert.Contains("C#m", cSharpMinor.Spellings);
        Assert.Contains("Dbm", cSharpMinor.Spellings);
    }

    [Theory]
    [InlineData("D/C")]      // a D triad over C: not D7/C
    [InlineData("C/Bb")]
    [InlineData("G7/F")]     // the F is the chord's own 7th
    [InlineData("C/E")]
    [InlineData("Am7/G")]
    public void A_slash_chord_keeps_its_name_in_the_index(string name)
    {
        var chord = PhraseChord.Of(ChordSymbol.Parse(name)!.Value);
        Assert.Equal(name, chord.Name);
        Assert.Equal(name, PhraseChord.Decode(PhraseChord.Encode([chord]))[0].Name);
        // Its bass still sounds: D/C and D7/C share all four notes.
        Assert.Equal(1.0, chord.Similarity(PhraseChord.Of(ChordSymbol.Parse(name)!.Value)));
    }

    [Fact]
    public void A_part_taken_for_a_melody_that_strikes_chords_still_has_them()
    {
        // A comping piano named "Lead": a chord on each bar's one and a tune on the other beats.
        var notes = new List<NoteEvent>();
        int[][] chords = [[48, 52, 55], [53, 57, 60], [55, 59, 62], [48, 52, 55]];
        for (var bar = 0; bar < 4; bar++)
        {
            notes.AddRange(Held(bar * (long)Bar, Beat, chords[bar]));
            for (var b = 1; b < 4; b++) notes.Add(new NoteEvent(bar * (long)Bar + b * Beat, Beat, 72 + 2 * b - bar, 100));
        }
        var analysis = PhraseAnalyser.Analyse(notes, "Lead");
        Assert.Equal(MidiRoles.Melody, analysis.Role);
        Assert.NotEmpty(analysis.Chords);
    }

    [Fact]
    public void A_stretch_cut_from_the_middle_of_a_bar_is_read_on_its_bar_lines()
    {
        // The walking bass under Dm7 G7 | Cmaj7, cut from beat 3 of the bar before: read from where its bars were, the
        // walk is still passing notes.
        var chart = Chart("Dm7 G7 | Cmaj7 |");
        var notes = Voicer.Voice(chart, VoicingStyle.Close, CompRhythm.Held, 2 * Bar).Concat(Voicer.Bass(chart, BassStyle.Walking, 2 * Bar))
                          .Select(n => n with { Start = n.Start + 2 * Beat }).ToList();
        Assert.Equal(["Dm7", "G7", "Cmaj7"], ChordReader.Read(notes, 2 * Bar + 2 * Beat, downbeat: 2 * Beat).Select(c => c.Chord.Name));
        Assert.Equal(2 * Beat, ChordReader.Read(notes, 2 * Bar + 2 * Beat, downbeat: 2 * Beat)[0].Start);
    }

    [Fact]
    public void Picked_bars_read_their_chords_without_the_tune_like_the_whole_song()
    {
        var keys = new MidiChannel("Keys", "", Ppq, Bars([36, 52, 55, 60], [41, 57, 60, 65]), Plugin: "Keys");
        var lead = new MidiChannel("Lead", "", Ppq, new[] { 74, 71, 74, 72, 71, 76, 71, 74 }.Select((k, i) => new NoteEvent(i * (long)Beat, Beat, k, 100)).ToList(),
                                   Plugin: "Lead");
        var song = HarmonyTarget.FromChannels("Song", [keys, lead])!;
        Assert.NotNull(song.HarmonyNotes);
        var stretch = HarmonyTarget.FromStretch("Bar 1", song.Notes.Where(n => n.Start < Bar).ToList(), Bar, song.Key, song.Registers,
                                                song.HarmonyNotes!.Where(n => n.Start < Bar).ToList())!;
        Assert.Equal("C", stretch.ChordText());
    }
}
