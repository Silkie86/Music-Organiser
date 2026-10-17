using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Tests;

/// <summary>The playground's tension curve, groove copying and smooth inversions.</summary>
public class PlaygroundToolsTests
{
    const int Ppq = PhraseAnalyser.Ppq, Beat = Ppq, Bar = 4 * Ppq;

    static ChordSymbol C(string name) => ChordSymbol.Parse(name) ?? throw new ArgumentException(name);

    static List<NoteEvent> Block(long at, long length, int velocity, params int[] keys) =>
        keys.Select(k => new NoteEvent(at, length, k, velocity)).ToList();

    // ---- tension curve ----

    [Fact]
    public void A_busy_loud_tense_bar_has_more_energy_than_a_calm_one()
    {
        var key = new KeyEstimate(0, false, 1);
        // Bar 1: a soft C major chord held. Bar 2: loud sixteenths on an altered dominant, up high.
        var calm = Block(0, Bar, 60, 48, 52, 55);
        int[] alt = [67, 71, 73, 75, 78, 80];
        var busy = Enumerable.Range(0, 16).Select(i => new NoteEvent(Bar + i * (long)(Beat / 4), Beat / 4, alt[i % alt.Length], 115)).ToList();
        var curve = Energy.Curve([.. calm, .. busy], 2 * Bar, MidiRoles.Arp, key);
        Assert.True(curve[1] > curve[0] + 0.3, $"{curve[0]:0.00} → {curve[1]:0.00}");
    }

    [Fact]
    public void Home_harmony_is_less_tense_than_far_harmony()
    {
        var home = Energy.HarmonicTension(Block(0, Bar, 90, 60, 64, 67), 0, false);
        var far = Energy.HarmonicTension(Block(0, Bar, 90, 61, 66, 68, 70), 0, false);   // F# and Db: the other side of the circle
        Assert.True(far > home + 0.2, $"{home:0.00} vs {far:0.00}");
    }

    [Fact]
    public void A_build_suits_a_rising_part_better_than_a_flat_one()
    {
        var build = Energy.Target(SectionFeel.Build, 8);
        Assert.True(build[^1] > build[0] + 0.4);
        var rising = Enumerable.Range(0, 8).Select(i => 0.3 + 0.07 * i).ToArray();
        var flat = Enumerable.Repeat(0.55, 8).ToArray();
        Assert.True(Energy.Match(rising, build) > Energy.Match(flat, build) + 0.15);
        // And a drop wants it full all the way.
        var drop = Energy.Target(SectionFeel.Drop, 8);
        Assert.True(Energy.Match(Enumerable.Repeat(0.8, 8).ToArray(), drop) > Energy.Match(rising, drop));
    }

    // ---- groove copying ----

    /// <summary>Swung eighths, triplet feel (off-beats at 2/3 of the beat), off-beats softer, beats 2 and 4 harder.</summary>
    static List<NoteEvent> SwungRide()
    {
        var list = new List<NoteEvent>();
        for (var beat = 0; beat < 8; beat++)
        {
            list.Add(new NoteEvent(beat * (long)Beat, Beat / 3, 51, beat % 2 == 1 ? 110 : 90));
            list.Add(new NoteEvent(beat * (long)Beat + 2 * Beat / 3, Beat / 3, 51, 70));
        }
        return list;
    }

    [Fact]
    public void A_swung_part_reads_as_triplet_swing_with_strong_two_and_four()
    {
        var g = Groove.Of(SwungRide())!;
        Assert.InRange(g.Swing!.Value, 64, 70);
        Assert.Contains("strong 2 and 4", g.Describe());
        Assert.Contains("Swing", g.Describe());
    }

    [Fact]
    public void Copying_the_feel_swings_straight_eighths_and_keeps_the_beats_put()
    {
        var straight = Enumerable.Range(0, 16).Select(i => new NoteEvent(i * (long)(Beat / 2), Beat / 2, 60, 90)).ToList();
        var swung = Groove.ApplyFeel(straight, Groove.Of(SwungRide())!);
        Assert.Equal(16, swung.Count);
        foreach (var n in swung)
        {
            var inBeat = n.Start % Beat;
            Assert.True(inBeat == 0 || Math.Abs(inBeat - 2 * Beat / 3) <= 2, $"note at {n.Start} ({inBeat} into the beat)");
        }
        // Accents come across: off-beats softer than beat 2.
        Assert.True(swung.First(n => n.Start == Beat).Velocity > swung.First(n => n.Start % Beat != 0).Velocity);
        // Half the amount goes half way.
        var half = Groove.ApplyFeel(straight, Groove.Of(SwungRide())!, 0.5);
        Assert.Contains(half, n => Math.Abs(n.Start % Beat - (Beat / 2 + (2 * Beat / 3 - Beat / 2) / 2)) <= 2);
    }

    [Fact]
    public void Copying_the_rhythm_strikes_your_chords_on_its_hits()
    {
        // Your block chords: Dm7 for a bar, G7 for a bar.
        var blocks = Block(0, Bar, 80, 50, 53, 57, 60).Concat(Block(Bar, Bar, 80, 50, 53, 55, 59)).ToList();
        // Its hits: on 1, and pushed on the "and" of 4 into the next bar, loud then soft.
        var source = Block(0, Beat, 110, 60, 64, 67).Concat(Block(3 * Beat + Beat / 2, Beat, 70, 60, 64, 67)).ToList();
        var result = Groove.ApplyRhythm(blocks, source, 2 * Bar);
        var starts = result.Select(n => n.Start).Distinct().OrderBy(s => s).ToList();
        Assert.Equal([0L, 3L * Beat + Beat / 2, Bar, Bar + 3L * Beat + Beat / 2], starts);
        Assert.Equal([50, 53, 57, 60], result.Where(n => n.Start == 0).Select(n => n.Key).OrderBy(k => k));
        // The push before bar 2 already plays G7, as a pianist anticipating the change would.
        Assert.Equal([50, 53, 55, 59], result.Where(n => n.Start == 3 * Beat + Beat / 2).Select(n => n.Key).OrderBy(k => k));
        Assert.All(result.Where(n => n.Start == 0), n => Assert.Equal(110, n.Velocity));
    }

    [Fact]
    public void Copying_the_whole_playing_moves_its_voicings_onto_your_chords()
    {
        // A pro comp in Eb: Ebmaj9 voiced G-Bb-D-F over Eb, played as a stab then a soft repeat, then Ab13 the same way.
        var source = new List<NoteEvent>();
        void Hit(long at, int vel, params int[] keys) => source.AddRange(keys.Select(k => new NoteEvent(at, Beat / 2, k, vel)));
        Hit(0, 108, 39, 55, 58, 62, 65);
        Hit(Beat + Beat / 2, 72, 55, 58, 62, 65);
        Hit(Bar, 108, 44, 60, 66, 70, 77);
        Hit(Bar + Beat + Beat / 2, 72, 60, 66, 70, 77);
        var lane = ChordLane.ParseChart("Dm9 | G13", out _)!;
        var played = Groove.ApplyPlaying(lane, source, 2 * Bar);
        // Same hits, same touch.
        Assert.Equal(source.Select(n => n.Start).Distinct().OrderBy(s => s), played.Select(n => n.Start).Distinct().OrderBy(s => s));
        Assert.Equal(108, played.First(n => n.Start == 0).Velocity);
        // Every note belongs to the chord of its bar, and the bass plays the roots.
        foreach (var n in played)
        {
            var chord = n.Start < Bar ? C("Dm9") : C("G13");
            Assert.True(ChordScales.Sit(chord, n.Key % 12) >= 0.6, $"{n.Key} over {chord.Name}");
        }
        Assert.Equal(2, played.Where(n => n.Start == 0).Min(n => n.Key) % 12);
        Assert.Equal(7, played.Where(n => n.Start == Bar).Min(n => n.Key) % 12);
        // Its third goes to our third: G over Ebmaj9 becomes F over Dm9.
        Assert.Contains(played, n => n.Start == 0 && n.Key % 12 == 5);
    }

    [Fact]
    public void Chord_members_map_to_the_same_members()
    {
        Assert.Equal(3, Groove.Map(C("C7"), C("Cm7"), 4));      // third to third
        Assert.Equal(11, Groove.Map(C("C7"), C("Cmaj7"), 10));  // seventh to seventh
        Assert.Equal(6, Groove.Map(C("C7"), C("Cm7b5"), 7));    // fifth to fifth
        Assert.Equal(2, Groove.Map(C("Cmaj9"), C("Dm9").Transposed(-2), 2));   // a ninth stays a ninth
    }

    // ---- smooth inversions ----

    static double Moves(IReadOnlyList<NoteEvent> notes)
    {
        var blocks = notes.GroupBy(n => n.Start).OrderBy(g => g.Key).Select(g => g.Select(n => n.Key).OrderBy(k => k).ToArray()).ToList();
        double total = 0;
        for (var i = 1; i < blocks.Count; i++) total += blocks[i].Zip(blocks[i - 1], (a, b) => Math.Abs(a - b)).Sum();
        return total;
    }

    [Fact]
    public void Chords_in_root_position_turn_into_smooth_inversions()
    {
        // C – F – G – C, all in root position: the hands jump about.
        var part = Block(0, Bar, 90, 60, 64, 67).Concat(Block(Bar, Bar, 90, 65, 69, 72))
                   .Concat(Block(2 * Bar, Bar, 90, 67, 71, 74)).Concat(Block(3 * Bar, Bar, 90, 60, 64, 67)).ToList();
        var smooth = Inversions.Smooth(part);
        Assert.True(Moves(smooth) < Moves(part) / 2, $"{Moves(part)} → {Moves(smooth)}");
        // Each chord keeps its notes.
        foreach (var bar in Enumerable.Range(0, 4))
            Assert.Equal(part.Where(n => n.Start == bar * Bar).Select(n => n.Key % 12).OrderBy(k => k),
                         smooth.Where(n => n.Start == bar * Bar).Select(n => n.Key % 12).OrderBy(k => k));
    }

    [Fact]
    public void A_bass_note_under_the_chord_stays_put()
    {
        var part = Block(0, Bar, 90, 36, 60, 64, 67).Concat(Block(Bar, Bar, 90, 41, 65, 69, 72)).Concat(Block(2 * Bar, Bar, 90, 43, 67, 71, 74)).ToList();
        var smooth = Inversions.Smooth(part);
        Assert.Equal([36, 41, 43], smooth.GroupBy(n => n.Start).OrderBy(g => g.Key).Select(g => g.Min(n => n.Key)));
        Assert.True(Moves(smooth.Where(n => n.Key > 50).ToList()) < Moves(part.Where(n => n.Key > 50).ToList()));
    }
}
