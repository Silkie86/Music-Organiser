using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Tests;

public class JazzHarmonyTests
{
    const int Ppq = PhraseAnalyser.Ppq, Beat = Ppq, Bar = 4 * Ppq;

    static string Name(params int[] midi) => ChordSpeller.Spell(midi)?.Name ?? "?";

    // ---- naming chords ----

    [Theory]
    [InlineData("Cmaj7", "Δ7", "")]
    [InlineData("Dm7", "m7", "m")]
    [InlineData("F#m7b5", "ø", "ø")]
    [InlineData("C#dim7", "°7", "°")]
    [InlineData("G7(b9)", "7", "")]
    [InlineData("G7sus4", "7sus", "")]
    [InlineData("Cmaj9", "Δ9", "")]
    public void A_crowded_chord_lane_writes_qualities_the_way_a_lead_sheet_does(string name, string shorter, string least)
    {
        var chord = ChordSymbol.Parse(name)!.Value;
        Assert.Equal(shorter, ChordSymbol.ShortQuality(chord.Quality));
        Assert.Equal(least, ChordSymbol.LeastQuality(chord.Quality));
        Assert.Equal(name, chord.RootName + chord.Quality + (chord.BassName.Length > 0 ? "/" + chord.BassName : ""));
    }

    [Theory]
    [InlineData("C", 48, 52, 55)]
    [InlineData("Am7", 45, 52, 55, 60)]
    [InlineData("C6", 48, 52, 55, 57)]
    [InlineData("Cmaj7", 48, 52, 55, 59)]
    [InlineData("Cmaj9", 48, 52, 59, 62)]
    [InlineData("Dm9", 50, 53, 57, 60, 64)]
    [InlineData("G13", 43, 53, 59, 64)]             // root, b7, 3, 13: a pianist's G13
    [InlineData("G7(b9)", 43, 53, 59, 62, 68)]
    [InlineData("G7alt", 43, 53, 59, 63, 68)]       // b7, 3, b13, #9... plus the root
    [InlineData("F#m7b5", 42, 48, 52, 57)]
    [InlineData("C#dim7", 49, 52, 55, 58)]
    [InlineData("C6/9", 48, 52, 57, 62, 67)]
    [InlineData("G7sus4", 43, 48, 50, 53)]
    [InlineData("Cm(maj7)", 48, 51, 55, 59)]
    [InlineData("Ebmaj7(#11)", 51, 55, 58, 62, 69)]
    [InlineData("C/E", 40, 48, 55, 60, 64)]
    [InlineData("C7(#9)", 48, 52, 58, 63)]
    public void Chords_are_named_as_jazz_players_name_them(string name, params int[] notes) =>
        Assert.Equal(name, Name(notes));

    [Fact]
    public void A_rootless_voicing_is_named_from_the_bass_under_it()
    {
        // F A C E alone is Fmaj7; with D in the bass it's Dm9.
        Assert.Equal("Fmaj7", Name(53, 57, 60, 64));
        Assert.Equal("Dm9", Name(38, 53, 57, 60, 64));
        // B D# G# A (3, 7... of F7) over F: the tritone-sub voicing names F13.
        Assert.Equal("F13", Name(41, 57, 62, 63));
    }

    [Theory]
    [InlineData("Dm9")] [InlineData("G13")] [InlineData("Cmaj9")] [InlineData("A7alt")] [InlineData("Bm7b5")]
    [InlineData("C#dim7")] [InlineData("C6/9")] [InlineData("F#m11")] [InlineData("Ab13(#11)")] [InlineData("G9sus4")]
    [InlineData("Bbmaj7/D")] [InlineData("Cm(maj7)")] [InlineData("E7(b9)")] [InlineData("Db7(#9)")]
    public void A_typed_chord_reads_back_to_the_same_name(string name) =>
        Assert.Equal(name, ChordSymbol.Parse(name)!.Value.Name);

    [Theory]
    [InlineData("CΔ9", "Cmaj9")] [InlineData("D-7", "Dm7")] [InlineData("Bø", "Bm7b5")] [InlineData("C°7", "Cdim7")]
    [InlineData("C11", "C9sus4")] [InlineData("Gsus", "Gsus4")] [InlineData("F#7b9", "F#7(b9)")]
    public void Chord_spellings_players_type_are_understood(string typed, string name) =>
        Assert.Equal(name, ChordSymbol.Parse(typed)!.Value.Name);

    // ---- what notes sit on a chord ----

    [Fact]
    public void Tensions_sit_and_avoid_notes_do_not()
    {
        var cmaj7 = ChordSymbol.Parse("Cmaj7")!.Value;
        Assert.Equal(1, ChordScales.Sit(cmaj7, 11));            // B: its own note
        Assert.True(ChordScales.Sit(cmaj7, 2) >= 0.7);          // D: the 9
        Assert.True(ChordScales.Sit(cmaj7, 6) > 0.5);           // F#: the #11
        Assert.True(ChordScales.Sit(cmaj7, 5) < 0.2);           // F: the avoid note
        Assert.True(ChordScales.Sit(cmaj7, 1) < 0);             // Db: fights it

        var g7 = ChordSymbol.Parse("G7")!.Value;
        Assert.True(ChordScales.Sit(g7, 8) > 0.5);              // Ab: b9 on a dominant is colour
        Assert.True(ChordScales.Sit(g7, 6) < 0);                // F#: a major 7 on a dominant

        // Classical ears: the b9 on a dominant is no longer a free colour.
        Assert.True(ChordScales.Sit(g7, 8, HarmonyStyle.Classical) < ChordScales.Sit(g7, 8));
    }

    [Fact]
    public void A_blue_third_over_a_major_chord_is_heard_as_gospel_not_a_mistake()
    {
        var c = ChordSymbol.Parse("C")!.Value;
        Assert.True(ChordScales.Sit(c, 3) > 0);
        Assert.True(ChordScales.Sit(c, 3, HarmonyStyle.Classical) < 0);
    }

    // ---- what chords do ----

    static IReadOnlyList<ChordSymbol> Chart(string text) => text.Split(' ').Select(t => ChordSymbol.Parse(t)!.Value).ToList();

    [Fact]
    public void A_two_five_one_is_found()
    {
        var f = ChordFunctions.Analyse(Chart("Dm9 G13 Cmaj9"), tonic: 0, minor: false, loops: false);
        Assert.Equal("ii7", f[0].Roman);
        Assert.Equal("ii–V–I", f[0].Label);
        Assert.Equal("V7", f[1].Roman);
        Assert.Equal('D', f[1].Kind);
        Assert.Equal('T', f[2].Kind);
    }

    [Fact]
    public void Jazz_and_gospel_moves_are_named()
    {
        var f = ChordFunctions.Analyse(Chart("Cmaj7 A7 Dm7 Db7 Cmaj7 C#dim7 Dm7 Bb7 Cmaj7 C7 F Fm6 C"), tonic: 0, minor: false, loops: false);
        Assert.Equal("V7/ii", f[1].Roman);                      // A7 → Dm7
        Assert.Contains("tritone sub", f[3].Label);             // Db7 → C
        Assert.Contains("passing diminished", f[5].Label);      // C#dim7 → Dm7
        Assert.Contains("backdoor", f[7].Label);                // Bb7 → C
        Assert.Contains("gospel", f[9].Label);                  // C7 → F
        Assert.Contains("amen", f[11].Label);                   // Fm6 in C
    }

    [Fact]
    public void Substitutes_count_as_alike()
    {
        double Sim(string a, string b) => ChordKinship.Similarity(ChordSymbol.Parse(a)!.Value, ChordSymbol.Parse(b)!.Value);
        Assert.True(Sim("G7", "Db7") >= 0.8);                   // tritone sub
        Assert.True(Sim("Cmaj7", "C6/9") >= 0.9);               // the same chord, coloured
        Assert.True(Sim("Bdim7", "G7(b9)") >= 0.75);            // rootless 7b9
        Assert.True(Sim("Bdim7", "Ddim7") >= 0.9);              // dim7 a minor third apart
        Assert.True(Sim("C", "Am7") >= 0.6);
        Assert.True(Sim("C", "F#") < 0.3);
    }

    // ---- the chord lane ----

    [Fact]
    public void A_chart_is_read_bar_by_bar()
    {
        var lane = ChordLane.ParseChart("Dm9 G13 | Cmaj9 | % | A7alt", out var why)!;
        Assert.Equal("", why);
        Assert.Equal(["Dm9", "G13", "Cmaj9", "A7alt"], lane.Select(c => c.Chord.Name));
        Assert.Equal([0L, 2 * Beat, Bar, 3 * Bar], lane.Select(c => c.Start));
        Assert.Equal(2L * Bar, lane[2].Length);
        Assert.Equal("Dm9 G13 | Cmaj9 | % | A7alt", ChordLane.ToChart(lane, 4 * Bar));
        Assert.Null(ChordLane.ParseChart("Dm9 Hello", out why));
        Assert.Contains("Hello", why);
    }

    [Fact]
    public void Chords_are_heard_from_notes_with_the_bass_naming_them()
    {
        // Rootless Dm9 and G13 voicings over a bassline playing D and G.
        var notes = new List<NoteEvent>
        {
            new(0, Bar, 53, 90), new(0, Bar, 57, 90), new(0, Bar, 60, 90), new(0, Bar, 64, 90), new(0, Bar, 38, 90),
            new(Bar, Bar, 53, 90), new(Bar, Bar, 57, 90), new(Bar, Bar, 59, 90), new(Bar, Bar, 64, 90), new(Bar, Bar, 43, 90),
        };
        var lane = ChordLane.Detect(notes, 2 * Bar);
        Assert.Equal(["Dm9", "G13"], lane.Select(c => c.Chord.Name));
        Assert.Equal(Bar, lane[0].Length);
    }

    // ---- writing parts ----

    [Theory]
    [InlineData(VoicingStyle.Close)] [InlineData(VoicingStyle.Rootless)] [InlineData(VoicingStyle.Drop2)]
    [InlineData(VoicingStyle.Gospel)] [InlineData(VoicingStyle.Classical)] [InlineData(VoicingStyle.Shell)]
    public void Voicings_hold_the_chord_and_move_smoothly(VoicingStyle style)
    {
        var lane = ChordLane.ParseChart("Dm9 | G13 | Cmaj9 | A7(b9)", out _)!;
        var notes = Voicer.Voice(lane, style, CompRhythm.Held, 4 * Bar);
        var chords = lane.Select(c => notes.Where(n => n.Start == c.Start).Select(n => n.Key).OrderBy(k => k).ToArray()).ToList();
        Assert.All(chords, v => Assert.True(v.Length >= 3));
        for (var k = 0; k < lane.Count; k++)
            // Every note is the chord's own or a tension it carries.
            Assert.All(chords[k], n => Assert.True(ChordScales.Sit(lane[k].Chord, n % 12) >= 0.6, $"{style}: {n} on {lane[k].Chord.Name}"));
        // Smooth: the top voice never leaps more than a fifth between chords.
        for (var k = 1; k < chords.Count; k++)
            Assert.True(Math.Abs(chords[k][^1] - chords[k - 1][^1]) <= 7, $"{style}: top leaps {chords[k - 1][^1]} → {chords[k][^1]}");
    }

    [Fact]
    public void Classical_voicing_keeps_clear_of_parallel_fifths()
    {
        var lane = ChordLane.ParseChart("C | F | G | C | Am | Dm | G7 | C", out _)!;
        var path = Voicer.VoicePath(lane.Select(c => c.Chord).ToList(), VoicingStyle.Classical);
        for (var k = 1; k < path.Count; k++)
        {
            int[] a = path[k - 1], b = path[k];
            for (var i = 0; i < a.Length; i++)
                for (var j = i + 1; j < a.Length; j++)
                {
                    var parallel = (b[j] - b[i]) % 12 == (a[j] - a[i]) % 12 && (a[j] - a[i]) % 12 is 7 or 0 && a[i] != b[i];
                    Assert.False(parallel, $"Parallel {(a[j] - a[i]) % 12} from {string.Join(",", a)} to {string.Join(",", b)}");
                }
        }
    }

    [Fact]
    public void A_walking_bass_starts_on_roots_and_approaches_by_a_semitone()
    {
        var lane = ChordLane.ParseChart("Dm7 | G7 | Cmaj7 | A7", out _)!;
        var bass = Voicer.Bass(lane, BassStyle.Walking, 4 * Bar);
        Assert.Equal(16, bass.Count);
        Assert.Equal([2, 7, 0, 9], lane.Select(c => bass.First(n => n.Start == c.Start).Key % 12));
        // The fourth beat of each bar sits a semitone from the next bar's root.
        for (var b = 0; b < 3; b++)
            Assert.Equal(1, Math.Abs(bass[b * 4 + 3].Key - bass[(b + 1) * 4].Key));
    }

    // ---- reharmonising ----

    [Fact]
    public void Reharm_ideas_include_the_classic_moves()
    {
        var lane = ChordLane.ParseChart("Cmaj7 | Am7 | Dm7 | G7 | Cmaj7 | % ", out _)!;
        var ideas = Reharmoniser.Suggest(lane, 0, false, most: 40);
        var titles = ideas.Select(i => i.Title).ToList();
        Assert.Contains("Tritone sub", titles);
        Assert.Contains("Turnaround", titles);
        Assert.Contains("Richer chords", titles);
        Assert.Contains(titles, t => t.StartsWith("Secondary dominant"));
        var sub = ideas.First(i => i.Title == "Tritone sub");
        Assert.Equal("G7", sub.Before);
        Assert.Equal("Db9", sub.After);
        Assert.Equal(4, sub.Bar);
        // Every idea keeps the lane the same length.
        Assert.All(ideas, i => Assert.Equal(lane[^1].End, i.Lane[^1].End));
    }
}
