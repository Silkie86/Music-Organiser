using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Tests;

/// <summary>
/// Hand-built musical cases from the harmony check-up (2026-10-20), one per thing the app's ears got wrong: the 5th
/// left out of a shell voicing, which way a half step rubs, a held note over a chord change, a tension on the downbeat.
/// The theory bench in tools/TheoryBench measures the same rules on 387 pop songs.
/// </summary>
public class HarmonyTheoryTests
{
    const int Ppq = PhraseAnalyser.Ppq, Beat = Ppq, Bar = 4 * Ppq;

    static ChordSymbol Chord(string name) => ChordSymbol.Parse(name)!.Value;

    static List<NoteEvent> Held(long start, long length, params int[] keys) =>
        keys.Select(k => new NoteEvent(start, length, k, 100)).ToList();

    // ---- the 5th ----

    [Fact]
    public void The_5th_is_a_chord_note_even_when_a_shell_voicing_leaves_it_out()
    {
        // C, E and Bb: a shell C7 with no G.
        var shell = ChordSpeller.Spell([48, 52, 58])!.Value;
        Assert.False(shell.Has(7));
        Assert.Equal(1, ChordScales.Sit(shell, 7));
        Assert.Equal(1, ChordScales.Sit(ChordSpeller.Spell([50, 53, 60])!.Value, 9));   // A over a shell Dm7
        Assert.False(ChordLane.Fights(ChordLane.ParseChart("C7", out _)!, 0, Bar, 7));
    }

    [Fact]
    public void A_note_pushed_ahead_of_the_bar_is_heard_against_the_chord_it_rings_into()
    {
        // A 16th of G+ read where the next chord is played early, then Fmaj13: a D pushed a 16th ahead is the 13th of F,
        // not a clash with the sliver (Solomon, 2026-10-21: "it didnt rub against the chord tho").
        var lane = new List<TimedChord> { new(0, Bar - Beat / 4, Chord("Gsus4")), new(Bar - Beat / 4, Beat / 4, Chord("G+")), new(Bar, Bar, Chord("Fmaj13")) };
        long start = Bar - Beat / 4, length = Beat;
        Assert.Equal("Fmaj13", ChordLane.Heard(lane, start, length)!.Value.Name);
        Assert.Equal("13", ChordScales.Degree(ChordLane.Heard(lane, start, length)!.Value, 2));
        Assert.False(ChordLane.Fights(lane, start, length, 2));
    }

    [Fact]
    public void An_altered_or_diminished_5th_stays_out()
    {
        // G7alt has a ♭5 or ♯5 in place of the 5th, so D still clashes with it; B° and Bø have F, not F#.
        Assert.True(ChordScales.Sit(Chord("G7alt"), 2) < 1);
        Assert.True(ChordScales.Sit(Chord("Bdim7"), 6) < 1);
        Assert.True(ChordScales.Sit(Chord("Bm7b5"), 6) < 1);
        Assert.True(ChordScales.Sit(Chord("Caug"), 7) < 1);
    }

    [Fact]
    public void A_melody_on_the_5ths_fits_the_apps_own_shell_comping()
    {
        var chart = ChordLane.ParseChart("Dm7 | G7 | Cmaj7 | %", out _)!;
        var comp = Voicer.Voice(chart, VoicingStyle.Shell, CompRhythm.Held, 4 * Bar);
        // The chords as heard from the comping alone, the way the Playground hears them.
        var lane = ChordLane.Detect(comp, 4 * Bar);
        var tune = Held(0, Bar, 69).Concat(Held(Bar, Bar, 74)).Concat(Held(2 * Bar, 2 * Bar, 67)).ToList();
        Assert.True(SectionFit.Score(tune, lane, 4 * Bar, MidiRoles.Melody) >= 0.9);
        Assert.DoesNotContain(tune, n => ChordLane.Fights(lane, n.Start, n.Length, n.Key % 12));
    }

    // ---- which way a half step rubs (MIDI page red marks) ----

    [Fact]
    public void Only_a_half_step_above_a_chord_note_is_marked_red()
    {
        var c = Held(0, Bar, 48, 52, 55);
        Assert.False(HarmonyMatcher.Rubs(0, Bar, 71, c));        // B, a major 7th over C: a colour
        Assert.False(HarmonyMatcher.Rubs(0, Bar, 62, c));        // D, the 9th
        Assert.True(HarmonyMatcher.Rubs(0, Bar, 65, c));         // F, a half step above E: the avoid note
        Assert.True(HarmonyMatcher.Rubs(0, Bar, 61, c));         // Db, the ♭9 over a major chord
        Assert.False(HarmonyMatcher.Rubs(0, Beat / 2, 65, c));   // a quick F is a passing note
    }

    [Fact]
    public void A_note_of_the_chord_is_not_red_while_an_arpeggio_has_moved_past_it()
    {
        // Cmaj7 broken in eighths, C E G B C E G B, and a tune's C struck as the arpeggio reaches B.
        var arpeggio = Enumerable.Range(0, 8).Select(i => new NoteEvent(i * (long)(Beat / 2), Beat / 2, new[] { 48, 52, 55, 59 }[i % 4], 100)).ToList();
        var lane = ChordLane.Detect(arpeggio, Bar);
        Assert.True(HarmonyMatcher.Rubs(3 * Beat / 2, Beat, 72, arpeggio));         // note against note, C over B rubs
        Assert.False(HarmonyMatcher.Rubs(3 * Beat / 2, Beat, 72, arpeggio, lane));  // but C is the chord's root
    }

    [Fact]
    public void Playing_together_a_major_7th_over_the_root_sits_better_than_a_flat_9()
    {
        var c = Held(0, Bar, 48, 52, 55);
        var maj7 = HarmonyMatcher.SitsTogether(Held(0, Bar, 71), c)!.Value;
        var flat9 = HarmonyMatcher.SitsTogether(Held(0, Bar, 61), c)!.Value;
        Assert.True(maj7 > flat9 + 0.2, $"{maj7:0.00} vs {flat9:0.00}");
    }

    [Fact]
    public void The_list_percent_is_how_they_sound_played_together()
    {
        int[][] aMinor = [[57, 60, 64], [53, 57, 60], [48, 52, 55], [55, 59, 62]];
        var keys = aMinor.SelectMany((c, bar) => Held(bar * (long)Bar, Bar, c)).ToList();
        var song = HarmonyTarget.FromChannels("Song", [new MidiChannel("Keys", "", Ppq, keys, Plugin: "Serum")])!;
        int[][] bars = [[69, 72, 76, 72], [65, 69, 72, 69], [67, 72, 76, 72], [67, 71, 74, 71]];
        var tune = bars.SelectMany((b, bar) => b.Select((k, i) => new NoteEvent(bar * (long)Bar + i * Beat, Beat, k, 100))).ToList();
        var a = PhraseAnalyser.Analyse(tune);
        var fit = HarmonyMatcher.Refine(a.Chroma, a.Chords, tune, MidiRoles.Melody, song);
        Assert.NotNull(fit.Together);
        Assert.Equal(fit.Together!.Value, fit.Score, 2);
    }

    // ---- held notes and strong beats (Playground fit and red marks) ----

    [Fact]
    public void A_note_held_into_a_chord_it_fights_is_caught()
    {
        var lane = ChordLane.ParseChart("C7 | F7", out _)!;
        // E is the 3rd of C7 but a major 7th on F7.
        Assert.True(ChordLane.Fights(lane, 0, 2 * Bar, 4));
        Assert.False(ChordLane.Fights(lane, 0, Bar, 4));
        // Moving to Eb, the ♭7 of F7, as the chord changes fits better than holding the E.
        var held = Held(0, 2 * Bar, 64);
        var moves = Held(0, Bar, 64).Concat(Held(Bar, Bar, 63)).ToList();
        Assert.True(SectionFit.Score(moves, lane, 2 * Bar, MidiRoles.Melody) > SectionFit.Score(held, lane, 2 * Bar, MidiRoles.Melody) + 0.2);
    }

    [Fact]
    public void Held_notes_are_judged_round_the_loop()
    {
        // A two-bar lane looping: a note from bar 4 into bar 5 is over F7 then C7 again.
        var lane = ChordLane.ParseChart("C7 | F7", out _)!;
        var pieces = ChordLane.Under(lane, 3 * Bar, 2 * Bar, loop: 2 * Bar).ToList();
        Assert.Equal(["F7", "C7"], pieces.Select(p => p.Chord.Name));
        Assert.True(pieces[0].Struck && !pieces[1].Struck);
        Assert.Equal(Bar, pieces[1].Length);
    }

    [Fact]
    public void A_tune_lands_on_chord_notes_on_the_downbeat_and_colours_the_weak_beats()
    {
        var lane = ChordLane.ParseChart("Cmaj7", out _)!;
        // E (the 3rd) on beat 1 and D (the 9th) on beat 2, against the same notes the other way round.
        var landing = new List<NoteEvent> { new(0, Beat, 76, 100), new(Beat, Beat, 74, 100), new(2 * Beat, 2 * Beat, 76, 100) };
        var leaning = new List<NoteEvent> { new(0, Beat, 74, 100), new(Beat, Beat, 76, 100), new(2 * Beat, 2 * Beat, 76, 100) };
        var a = SectionFit.Score(landing, lane, Bar, MidiRoles.Melody)!.Value;
        var b = SectionFit.Score(leaning, lane, Bar, MidiRoles.Melody)!.Value;
        Assert.True(a > b + 0.05, $"{a:0.00} vs {b:0.00}");
    }

    // ---- keys ----

    static double[] ChromaOf(IEnumerable<NoteEvent> notes)
    {
        var c = new double[12];
        foreach (var n in notes) c[n.Key % 12] += n.Length / (double)Bar;
        return c;
    }

    [Fact]
    public void Flat_keys_are_written_with_flats()
    {
        Assert.Equal("Eb major", MusicTheory.KeyName(3, false));
        Assert.Equal("Bbm", MusicTheory.KeyName(10, true, true));
        Assert.Equal("Gb", MusicTheory.KeyName(6, false, true));
        Assert.Equal("F#m", MusicTheory.KeyName(6, true, true));
        Assert.Equal("C#m", MusicTheory.KeyName(1, true, true));
        Assert.Equal("Db", new KeyEstimate(1, false, 1).Short);
        // A key stored the old way is shown the new way.
        Assert.Equal("Eb minor", MusicTheory.Respell("D# minor"));
        Assert.Equal("Bb", MusicTheory.Respell("A#"));
        Assert.Equal("", MusicTheory.Respell(""));
    }

    [Fact]
    public void A_gospel_vamp_in_Ab_is_heard_in_Ab()
    {
        // Ab – Db/Ab – Eb/Ab – Ab, over an Ab pedal: the kind of 8 bars a gospel keys pack opens with.
        int[][] vamp = [[56, 60, 63, 68], [56, 61, 65, 68], [56, 58, 63, 67], [56, 60, 63, 68]];
        var notes = vamp.SelectMany((c, i) => Held(i * 2L * Bar, 2 * Bar, c)).ToList();
        var key = MusicTheory.EstimateMidiKey(ChromaOf(notes))!;
        Assert.Equal((8, false), (key.Tonic, key.Minor));
        Assert.Equal("Ab", PhraseAnalyser.Analyse(notes).Key!.Short);
    }

    [Fact]
    public void The_first_chord_only_settles_a_toss_up_between_a_key_and_its_relative()
    {
        // Am – Dm – G – C: C major by a hair on the notes alone, so the opening Am decides.
        int[][] tossUp = [[57, 60, 64], [50, 53, 57], [55, 59, 62], [48, 52, 55]];
        var even = ChromaOf(tossUp.SelectMany((c, i) => Held(i * Bar, Bar, c)));
        Assert.Equal((0, false), MusicTheory.EstimateMidiKey(even) is { } k0 ? (k0.Tonic, k0.Minor) : default);
        Assert.Equal((9, true), MusicTheory.EstimateMidiKey(even, 9) is { } k1 ? (k1.Tonic, k1.Minor) : default);
        // C – G – C – F – C, leaning hard on C, E and G: starting on an Am chord doesn't make it A minor.
        int[][] home = [[57, 60, 64], [48, 52, 55], [55, 59, 62], [48, 52, 55], [53, 57, 60], [48, 52, 55], [48, 52, 55]];
        var major = ChromaOf(home.SelectMany((c, i) => Held(i * Bar, Bar, c)));
        Assert.Equal((0, false), MusicTheory.EstimateMidiKey(major, 9) is { } k2 ? (k2.Tonic, k2.Minor) : default);
    }

    [Fact]
    public void Notes_that_point_at_no_key_are_too_unsure_for_the_playground()
    {
        // The whole-tone scale, every note as long: each of its keys fits as well as the next, so there's no key to give.
        var notes = new[] { 60, 62, 64, 66, 68, 70 }.SelectMany((k, i) => Held(i * Bar, Bar, k)).ToList();
        var key = MusicTheory.EstimateMidiKey(ChromaOf(notes));
        Assert.True(key is null || key.Confidence < SectionHarmony.MinKeyConfidence, $"{key?.Short} at {key?.Confidence}");
    }
}
