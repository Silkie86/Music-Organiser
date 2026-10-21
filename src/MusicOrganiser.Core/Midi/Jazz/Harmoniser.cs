using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi.Jazz;

/// <summary>
/// Chords for a melody, a bassline, or both, when there are no chords yet (Solomon, 2026-10-21: "should be able to
/// reharmonise a melody on its own in the arranger", "or harmonise a bassline or bass an melody"). Each way of hearing
/// it (plain, jazz, gospel, classical, and jazz moving twice a bar) picks, bar by bar, the chords that hold the
/// melody's long and on-the-beat notes and sit on the bass's notes, joined the way players join chords: roots falling
/// a 5th, dominants resolving, home at the start and the end. What follows what can also come from your own chord
/// parts (<see cref="Playground.ProgressionModel"/>). Each comes as a whole chord lane, to try and keep or drop.
/// </summary>
public static class Harmoniser
{
    const int Bar = ChordLane.Bar, Beat = Bar / 4;

    public enum Flavour { Plain, Jazz, Gospel, Classical, JazzMoving }

    /// <param name="melody">Every pitched part's notes but the bass (ticks from the start).</param>
    /// <param name="bass">The bassline's notes; empty when there isn't one.</param>
    /// <param name="learned">How often your chord parts go from one chord to the next (0 to 1), when known.</param>
    public static IReadOnlyList<Reharm> Harmonise(IReadOnlyList<NoteEvent> melody, IReadOnlyList<NoteEvent> bass, long length,
                                                  int tonic, bool minor, Func<ChordSymbol, ChordSymbol, double>? learned = null)
    {
        var ideas = new List<Reharm>();
        if (length <= 0 || melody.Count + bass.Count == 0) return ideas;
        var what = bass.Count == 0 ? "your melody" : melody.Count == 0 ? "your bassline" : "your bass and melody";
        var seen = new HashSet<string>();
        foreach (var flavour in Enum.GetValues<Flavour>())
        {
            var step = flavour == Flavour.JazzMoving ? Bar / 2 : Bar;
            if (flavour == Flavour.JazzMoving && length < 2 * Bar) continue;
            var lane = Merge(Best(melody, bass, length, step, Vocabulary(flavour, tonic, minor), flavour, tonic, minor, learned));
            if (lane.Count == 0) continue;
            var after = string.Join(" ", lane.Select(c => c.Chord.Name));
            if (!seen.Add(after)) continue;   // two ways of hearing it can land on the same chords
            var (title, why) = Words(flavour, what);
            ideas.Add(new Reharm(title, why, what, after, 1, lane) { Whole = true });
        }
        return ideas;
    }

    static (string Title, string Why) Words(Flavour f, string what) => f switch
    {
        Flavour.Plain => ("Harmonise: plain", $"Triads from the key that hold {what}'s main notes, home at both ends."),
        Flavour.Jazz => ("Harmonise: jazz", $"Seventh chords joined by falling 5ths (ii–V–I), with secondary dominants where {what} leans that way."),
        Flavour.Gospel => ("Harmonise: gospel", "Ninths and sus chords, the minor iv, the passing #iv°7 and the IV-over-V lift."),
        Flavour.Classical => ("Harmonise: classical", "Triads and the V7 joined as in a hymn: tonic, pre-dominant, dominant, home."),
        _ => ("Harmonise: jazz, two a bar", $"A chord every half bar, moving with {what}: more ii–Vs and approach chords."),
    };

    /// <summary>The chords a flavour picks from, in the key.</summary>
    static List<ChordSymbol> Vocabulary(Flavour f, int t, bool minor)
    {
        var list = new List<(int Step, string Quality)>();
        if (!minor)
            switch (f)
            {
                case Flavour.Plain:
                    list.AddRange([(0, ""), (2, "m"), (4, "m"), (5, ""), (7, ""), (9, "m")]);
                    break;
                case Flavour.Classical:
                    list.AddRange([(0, ""), (2, "m"), (4, "m"), (5, ""), (7, ""), (7, "7"), (9, "m"), (11, "dim")]);
                    break;
                case Flavour.Gospel:
                    list.AddRange([(0, "add9"), (0, "maj9"), (2, "m9"), (4, "m7"), (5, "maj9"), (5, "m6"), (6, "dim7"), (7, "9sus4"), (7, "13"),
                                   (9, "m9"), (4, "7"), (9, "7"), (2, "9"), (10, "9")]);
                    break;
                default:
                    list.AddRange([(0, "maj7"), (2, "m7"), (4, "m7"), (5, "maj7"), (7, "7"), (9, "m7"), (11, "m7b5"),
                                   (9, "7"), (2, "7"), (4, "7"), (0, "7"), (1, "7"), (5, "m7"), (10, "7")]);
                    break;
            }
        else
            switch (f)
            {
                case Flavour.Plain:
                    list.AddRange([(0, "m"), (3, ""), (5, "m"), (7, "m"), (7, ""), (8, ""), (10, "")]);
                    break;
                case Flavour.Classical:
                    list.AddRange([(0, "m"), (2, "dim"), (3, ""), (5, "m"), (7, ""), (7, "7"), (8, ""), (11, "dim")]);
                    break;
                case Flavour.Gospel:
                    list.AddRange([(0, "m9"), (2, "m7b5"), (3, "maj9"), (5, "m9"), (7, "7b9"), (7, "9sus4"), (8, "maj9"), (10, "9"), (0, "7")]);
                    break;
                default:
                    list.AddRange([(0, "m7"), (2, "m7b5"), (3, "maj7"), (5, "m7"), (7, "7"), (7, "m7"), (8, "maj7"), (10, "7"), (1, "7"), (0, "7")]);
                    break;
            }
        var chords = new List<ChordSymbol>();
        foreach (var (step, quality) in list)
            if (ChordSymbol.Parse(ChordSymbol.Names[ChordSymbol.Mod(t + step)] + quality) is { } c && chords.All(o => o.Name != c.Name))
                chords.Add(c);
        return chords;
    }

    /// <summary>The best chord for each step, all at once (Viterbi): how well each holds the notes, and how well each follows the one before.</summary>
    static List<TimedChord> Best(IReadOnlyList<NoteEvent> melody, IReadOnlyList<NoteEvent> bass, long length, long step,
                                 List<ChordSymbol> vocab, Flavour f, int tonic, bool minor, Func<ChordSymbol, ChordSymbol, double>? learned)
    {
        var steps = (int)((length + step - 1) / step);
        if (steps == 0 || vocab.Count == 0) return [];
        var style = f is Flavour.Plain or Flavour.Classical ? HarmonyStyle.Classical : HarmonyStyle.JazzGospel;
        var fit = new double[steps, vocab.Count];
        var sounding = new bool[steps];
        for (var s = 0; s < steps; s++)
        {
            long a = s * step, b = Math.Min(length, a + step);
            sounding[s] = melody.Concat(bass).Any(n => n.Start < b && n.Start + n.Length > a);
            // A chord from the key wins a tie with one borrowed from outside it (a secondary dominant needs the notes to ask for it).
            for (var c = 0; c < vocab.Count; c++) fit[s, c] = Holds(vocab[c], melody, bass, a, b, style) + (InKey(vocab[c], tonic, minor) ? 0.1 : 0);
        }
        var score = new double[steps, vocab.Count];
        var from = new int[steps, vocab.Count];
        for (var c = 0; c < vocab.Count; c++) score[0, c] = fit[0, c] + (Home(vocab[c], tonic) ? 0.35 : 0);
        for (var s = 1; s < steps; s++)
            for (var c = 0; c < vocab.Count; c++)
            {
                var best = double.MinValue;
                for (var p = 0; p < vocab.Count; p++)
                {
                    var v = score[s - 1, p] + 0.6 * Joins(vocab[p], vocab[c], f, learned, sounding[s]);
                    if (v > best) { best = v; from[s, c] = p; }
                }
                score[s, c] = best + fit[s, c] + (s == steps - 1 && Home(vocab[c], tonic) ? 0.35 : 0);
            }
        var at = 0;
        for (var c = 1; c < vocab.Count; c++) if (score[steps - 1, c] > score[steps - 1, at]) at = c;
        var picked = new int[steps];
        for (var s = steps - 1; s >= 0; s--)
        {
            picked[s] = at;
            at = from[s, at];
        }
        return Enumerable.Range(0, steps).Select(s => new TimedChord(s * step, Math.Min(step, length - s * step), vocab[picked[s]])).ToList();
    }

    /// <summary>
    /// How well a chord holds the notes from <paramref name="a"/> to <paramref name="b"/>, about −1 to 1: each melody note
    /// by how long it sounds there and more on the beat (a quick passing note hardly counts), the bass by its first note
    /// being the root.
    /// </summary>
    static double Holds(ChordSymbol chord, IReadOnlyList<NoteEvent> melody, IReadOnlyList<NoteEvent> bass, long a, long b, HarmonyStyle style)
    {
        double sum = 0, weight = 0;
        foreach (var n in melody)
        {
            long s = Math.Max(a, n.Start), e = Math.Min(b, n.Start + n.Length);
            if (e <= s) continue;
            var w = (e - s) / (double)Beat * (n.Start >= a && n.Start % Beat == 0 ? n.Start % (2 * Beat) == 0 ? 1.6 : 1.25 : 1);
            if (n.Length < Beat / 2) w *= 0.4;   // passing notes
            sum += w * Value(NoteStrength.Of(chord, n.Key % 12, MidiRoles.Melody, style), style);
            weight += w;
        }
        var held = weight > 0 ? sum / weight : 0;
        var bassNotes = bass.Where(n => n.Start < b && n.Start + n.Length > a).OrderBy(n => n.Start).ToList();
        if (bassNotes.Count == 0) return held;
        var first = bassNotes[0].Key % 12;
        var low = ChordSymbol.Mod(first - chord.Root) == 0 ? 1 : chord.Has(ChordSymbol.Mod(first - chord.Root)) ? 0.2 : -0.6;
        double bs = 0, bw = 0;
        foreach (var n in bassNotes)
        {
            var w = Math.Min(b, n.Start + n.Length) - Math.Max(a, n.Start);
            bs += w * Value(NoteStrength.Of(chord, n.Key % 12, MidiRoles.Bass, style), style);
            bw += w;
        }
        var line = bw > 0 ? bs / bw : 0;
        return weight > 0 ? 0.55 * held + 0.3 * low + 0.15 * line : 0.65 * low + 0.35 * line;
    }

    /// <summary>The home chord: on the key's tonic, and not a dominant leaning away from it.</summary>
    static bool Home(ChordSymbol chord, int tonic) => chord.Root == tonic && chord.Family != ChordFamily.Dominant;

    /// <summary>Whether every note of the chord is in the key's scale (major, or minor with its raised 7th).</summary>
    static bool InKey(ChordSymbol chord, int tonic, bool minor)
    {
        int[] scale = minor ? [0, 2, 3, 5, 7, 8, 10, 11] : [0, 2, 4, 5, 7, 9, 11];
        for (var pc = 0; pc < 12; pc++)
            if ((chord.Mask >> pc & 1) != 0 && !scale.Contains(ChordSymbol.Mod(pc - tonic))) return false;
        return true;
    }

    static double Value(FitStrength s, HarmonyStyle style) => s switch
    {
        FitStrength.Strongest => 1,
        FitStrength.Strong => 0.8,
        FitStrength.Colour => style == HarmonyStyle.Classical ? 0.1 : 0.5,
        FitStrength.Weak => -0.1,
        _ => -1,
    };

    /// <summary>How well <paramref name="next"/> follows <paramref name="last"/>, about −0.5 to 1.</summary>
    static double Joins(ChordSymbol last, ChordSymbol next, Flavour f, Func<ChordSymbol, ChordSymbol, double>? learned, bool sounding)
    {
        var fall = ChordSymbol.Mod(last.Root - next.Root);   // 7: down a 5th
        double v;
        if (last.Name == next.Name) v = sounding ? (f == Flavour.Plain ? 0.15 : f == Flavour.Classical ? 0 : -0.15) : 0.4;   // held through a rest
        else if (fall == 7) v = last.Family is ChordFamily.Dominant or ChordFamily.Sus ? 0.9 : 0.5;   // V–I, ii–V
        else if (fall == 1 && last.Family == ChordFamily.Dominant) v = 0.6;   // the tritone sub falling a half step
        else if (fall is 2 or 10 || fall is 1 or 11) v = 0.2;   // a step
        else if (fall is 5) v = f == Flavour.Classical ? 0.1 : 0.25;   // up a 5th: the plagal IV–I and its kin
        else v = 0;
        if (last.Family == ChordFamily.Dominant && fall != 7 && fall != 1 && last.Name != next.Name) v -= 0.3;   // a dominant left hanging
        if (f == Flavour.Classical && last.Family == ChordFamily.Dominant && next.Family is ChordFamily.Major or ChordFamily.Minor && fall == 2) v -= 0.3;   // V–IV
        if (learned?.Invoke(last, next) is > 0 and var share) v = 0.6 * v + 0.4 * Math.Min(1, share * 4);
        return v;
    }

    /// <summary>The same chord in the steps next to each other, as one.</summary>
    static List<TimedChord> Merge(List<TimedChord> lane)
    {
        var merged = new List<TimedChord>();
        foreach (var c in lane)
            if (merged.Count > 0 && merged[^1].Chord.Name == c.Chord.Name && merged[^1].End == c.Start)
                merged[^1] = merged[^1] with { Length = merged[^1].Length + c.Length };
            else merged.Add(c);
        return merged;
    }
}
