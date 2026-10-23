using System.Numerics;

namespace MusicOrganiser.Core.Midi.Jazz;

/// <summary>
/// Names chords from their notes the way jazz and gospel players do, extensions and all. Every note could be the root;
/// each is tried, and the notes are read from it: is there a third, which seventh, which fifth, and are the other
/// notes tensions (9, 11, 13, b9, #9, #11, b13) the chord can carry, or notes that don't belong (a natural 11 on a
/// major chord, a major seventh on a dominant)? The reading that explains the notes best wins, the bass note
/// leaning it (C E G A over C is C6; over A it's Am7). A missing root is allowed when the third and seventh are
/// there, as in a pianist's rootless voicing, and a note in the bass that isn't the root makes a slash chord.
/// This is the "segment and template" idea (Pardo and Birmingham's HarmAn, music21's root finder) with a jazz
/// dictionary, written as rules so it says why.
/// </summary>
public static class ChordSpeller
{
    static bool Bit(int intervals, int i) => (intervals >> ChordSymbol.Mod(i) & 1) != 0;

    // Every set of intervals' family and what each note does in it, worked out once: the chord reader spells thousands
    // of note sets a phrase.
    static readonly ChordFamily[] Families = Enumerable.Range(0, 4096).Select(FamilyOf).ToArray();
    static readonly ToneRole[] Roles = Enumerable.Range(0, 4096 * 12).Select(x => Role(x / 12, Families[x / 12], x % 12)).ToArray();

    /// <summary>What sort of chord these intervals (from the root) make.</summary>
    public static ChordFamily FamilyOf(int iv)
    {
        bool m3 = Bit(iv, 3), M3 = Bit(iv, 4), b7 = Bit(iv, 10), M7 = Bit(iv, 11), P5 = Bit(iv, 7), d5 = Bit(iv, 6), a5 = Bit(iv, 8);
        if (M3 && b7) return ChordFamily.Dominant;   // a minor third here is a #9
        if (M3) return a5 && !P5 ? ChordFamily.Augmented : ChordFamily.Major;
        if (m3)
        {
            if (d5 && !P5)
            {
                if (b7) return ChordFamily.HalfDiminished;
                return ChordFamily.Diminished;
            }
            return ChordFamily.Minor;
        }
        if (Bit(iv, 5) || (Bit(iv, 2) && (P5 || b7))) return ChordFamily.Sus;
        return ChordFamily.Power;
    }

    /// <summary>What the note this far above the root does in a chord of this family.</summary>
    public static ToneRole Role(int iv, ChordFamily family, int interval)
    {
        var i = ChordSymbol.Mod(interval);
        if (i == 0) return ToneRole.Root;
        bool P5 = Bit(iv, 7), b7 = Bit(iv, 10), M7 = Bit(iv, 11);
        switch (family)
        {
            case ChordFamily.Major:
                return i switch
                {
                    4 => ToneRole.Third, 7 => ToneRole.Fifth, 11 => ToneRole.Seventh,
                    9 => ToneRole.Seventh,                                   // the 6 of a 6 chord (a 13 with a maj7 still counts)
                    2 or 6 => ToneRole.Tension,                              // 9, #11
                    8 when !P5 => ToneRole.Fifth,                            // #5
                    _ => ToneRole.Avoid,                                     // natural 11, b9, #9, b13
                };
            case ChordFamily.Augmented:
                return i switch { 4 => ToneRole.Third, 8 => ToneRole.Fifth, 11 or 10 => ToneRole.Seventh, 2 or 6 => ToneRole.Tension, _ => ToneRole.Avoid };
            case ChordFamily.Minor:
                return i switch
                {
                    3 => ToneRole.Third, 7 => ToneRole.Fifth, 10 or 11 => ToneRole.Seventh,
                    9 when !b7 && !M7 => ToneRole.Seventh,                   // m6
                    2 or 5 or 9 => ToneRole.Tension,                         // 9, 11, 13
                    8 when P5 => ToneRole.Tension,                           // b13 (Aeolian)
                    _ => ToneRole.Avoid,
                };
            case ChordFamily.Dominant:
                return i switch
                {
                    4 => ToneRole.Third, 7 => ToneRole.Fifth, 10 => ToneRole.Seventh,
                    6 when !P5 => ToneRole.Fifth,                            // b5
                    8 when !P5 => ToneRole.Fifth,                            // #5
                    1 or 2 or 3 or 6 or 8 or 9 => ToneRole.Tension,          // b9 9 #9 #11 b13 13
                    _ => ToneRole.Avoid,                                     // natural 11 under a third, major 7
                };
            case ChordFamily.HalfDiminished:
                return i switch { 3 => ToneRole.Third, 6 => ToneRole.Fifth, 10 => ToneRole.Seventh, 2 or 5 or 8 => ToneRole.Tension, _ => ToneRole.Avoid };
            case ChordFamily.Diminished:
                return i switch
                {
                    3 => ToneRole.Third, 6 => ToneRole.Fifth, 9 or 11 => ToneRole.Seventh,
                    2 or 5 or 8 => ToneRole.Tension,                         // a whole step above a chord note
                    _ => ToneRole.Avoid,
                };
            case ChordFamily.Sus:
                return i switch
                {
                    5 => ToneRole.Third, 2 when !Bit(iv, 5) => ToneRole.Third, 7 => ToneRole.Fifth, 10 => ToneRole.Seventh,
                    11 when !b7 => ToneRole.Seventh,
                    2 or 9 => ToneRole.Tension,                              // 9, 13
                    1 => ToneRole.Tension,                                   // sus b9, the gospel and Phrygian sound
                    _ => ToneRole.Avoid,
                };
            default:
                return i == 7 ? ToneRole.Fifth : ToneRole.Avoid;
        }
    }

    /// <summary>
    /// How well a root explains the notes: points for a third, a seventh, the root itself, the root in the bass and a
    /// fifth; a little for each tension; off for notes that don't belong, a missing root and a bass note that isn't
    /// part of the chord.
    /// </summary>
    static double Score(int mask, int root, int bass, float[]? weights, out ChordFamily family, int lowest = -1)
    {
        var iv = root == 0 ? mask : ((mask >> root) | (mask << (12 - root))) & 0xFFF;
        family = Families[iv];
        var roles = Roles.AsSpan(iv * 12, 12);
        var rootless = (iv & 1) == 0;
        // A 6 makes a 6 chord with a fifth or a seventh beside it; on its own it's an added note: C E A is Am before
        // C6, A C F# is F#dim before Am6.
        var bareSix = family is ChordFamily.Major or ChordFamily.Minor && (iv & (1 << 7 | 1 << 9 | 1 << 10 | 1 << 11)) == 1 << 9;
        double score = 0;
        var thirdOrSeventh = 0;
        for (var bits = iv; bits != 0; bits &= bits - 1)
        {
            var i = BitOperations.TrailingZeroCount(bits);
            var pc = root + i;
            var w = weights is null ? 1.0 : 0.6 + 0.4 * weights[pc >= 12 ? pc - 12 : pc];
            switch (roles[i])
            {
                case ToneRole.Root: score += 2 * w; break;
                case ToneRole.Third: score += 2.5 * w; thirdOrSeventh++; break;
                case ToneRole.Seventh when bareSix && i == 9: score += 0.25 * w; break;
                case ToneRole.Seventh: score += 1.6 * w; thirdOrSeventh++; break;
                case ToneRole.Fifth: score += 0.6 * w; break;
                case ToneRole.Tension: score += 0.25 * w; break;
                default: score -= 1.8 * w; break;
            }
        }
        if (family is ChordFamily.Power) score -= 1.5;
        if (family is ChordFamily.Sus) score -= 0.4;   // a third, when there is one, is the likelier reading
        if (family is ChordFamily.Diminished && (iv >> 9 & 1) != 0) score -= 0.3;   // dim7 is the same from four roots: let the bass pick
        if (rootless)
        {
            // A rootless voicing (3-7-9-13) only when the notes make a chord without it.
            if (thirdOrSeventh < 2 || ChordSymbol.Count(mask) < 3) return double.NegativeInfinity;
            score -= 2.2;
        }
        if (bass >= 0)
        {
            if (bass == root) score += 1.6;
            else
            {
                var role = roles[bass >= root ? bass - root : bass - root + 12];
                score -= role switch
                {
                    ToneRole.Third or ToneRole.Fifth or ToneRole.Seventh => 0.5,   // an inversion
                    ToneRole.Tension => 1.0,
                    _ => 1.6,
                };
            }
        }
        else
        {
            // A right hand on its own has no bass, but its lowest note still leans the root (E G B D is Em7).
            if (lowest >= 0 && lowest == root) score += 0.3;
            // With no bass to say, the same four notes read as a minor seventh before a major sixth: A C E G and G A C E
            // are Am7, G B D E is Em7 (C6 and G6 need the bass to say so).
            if (family is ChordFamily.Major && (iv >> 9 & 7) == 1) score -= 0.4;
        }
        // Simpler chords win near ties (C6 over Am7/C needs the bass to choose, Am7 over C6/A).
        score -= 0.02 * ChordSymbol.Count(mask);
        return score;
    }

    /// <summary>
    /// The chord these notes make, or null when they don't make one (fewer than two notes, or nothing reads as a chord).
    /// </summary>
    /// <param name="mask">The pitch classes sounding (bit 0 = C), the bass note among them.</param>
    /// <param name="bass">The lowest note's pitch class, or -1 when unknown.</param>
    /// <param name="weights">How strongly each pitch class sounds (0 to 1), to lean the choice; null when all count the same.</param>
    /// <param name="lowest">With no bass: the lowest note's pitch class, which leans the root a little (no slash chord); -1 when unknown.</param>
    public static ChordSymbol? Spell(int mask, int bass = -1, float[]? weights = null, int lowest = -1)
    {
        mask &= 0xFFF;
        if (bass >= 0) mask |= 1 << bass;
        var count = ChordSymbol.Count(mask);
        if (count < 2) return null;
        var best = double.NegativeInfinity;
        ChordSymbol? chord = null;
        for (var root = 0; root < 12; root++)
        {
            var s = Score(mask, root, bass, weights, out var family, lowest);
            if (s <= best) continue;
            best = s;
            chord = new ChordSymbol(root, mask, bass >= 0 ? bass : root, family);
        }
        if (chord is { } c && count == 2 && c.Family is not (ChordFamily.Power or ChordFamily.Major or ChordFamily.Minor)) return null;
        return best > 0 ? chord : null;
    }

    /// <summary>
    /// Every name the same notes could have, the likeliest first: one for each of the notes as the root, when it makes a
    /// chord of them (Solomon, 2026-10-23: "if the harmony engine gets it wrong should be able to tell it what the root
    /// is"). With a bass, a reading on another root is written over it (A6/F#). Readings whose name would leave a note
    /// out are skipped.
    /// </summary>
    public static List<ChordSymbol> Readings(int mask, int bass = -1, int lowest = -1)
    {
        mask &= 0xFFF;
        if (bass >= 0) mask |= 1 << bass;
        if (ChordSymbol.Count(mask) < 2) return [];
        var list = new List<(ChordSymbol Chord, double Score)>();
        for (var root = 0; root < 12; root++)
        {
            if ((mask >> root & 1) == 0) continue;
            var s = Score(mask, root, bass, null, out var family, lowest);
            if (s <= 0) continue;
            var chord = new ChordSymbol(root, mask, bass >= 0 ? bass : root, family);
            // Only a name that says every note: C E G is never offered as an "Em" that leaves its C out.
            if (IntervalsOf(chord.Quality) is not { } named || (chord.Intervals & ~named) != 0) continue;
            list.Add((chord, s));
        }
        return list.OrderByDescending(p => p.Score).Select(p => p.Chord).ToList();
    }

    /// <summary>
    /// <paramref name="chord"/>'s notes heard on <paramref name="root"/>, the root added when it isn't one of them. A bass
    /// under another root stays under it; a chord with none of its own takes the new root as its bass.
    /// </summary>
    public static ChordSymbol On(ChordSymbol chord, int root)
    {
        root = ChordSymbol.Mod(root);
        var mask = chord.Mask | 1 << root;
        var iv = ((mask >> root) | (mask << (12 - root))) & 0xFFF;
        return new ChordSymbol(root, mask, chord.Bass != chord.Root ? chord.Bass : root, Families[iv]);
    }

    /// <summary>The chord in a set of MIDI notes, the lowest as the bass.</summary>
    public static ChordSymbol? Spell(IEnumerable<int> midiNotes)
    {
        var notes = midiNotes.ToList();
        if (notes.Count == 0) return null;
        var mask = 0;
        foreach (var n in notes) mask |= 1 << (n % 12);
        return Spell(mask, notes.Min() % 12);
    }

    // ---- names ----

    /// <summary>The quality as written after the root.</summary>
    public static string QualityOf(int iv, ChordFamily family)
    {
        bool b9 = Bit(iv, 1), n9 = Bit(iv, 2), s9 = Bit(iv, 3), n11 = Bit(iv, 5), s11 = Bit(iv, 6), b13 = Bit(iv, 8), n13 = Bit(iv, 9);
        bool P5 = Bit(iv, 7), b7 = Bit(iv, 10), M7 = Bit(iv, 11);
        switch (family)
        {
            case ChordFamily.Major:
            {
                if (!P5 && b13) return M7 ? "maj7#5" : "+";
                string q;
                if (M7) q = n13 ? "maj13" : n9 ? "maj9" : "maj7";
                else if (n13) q = n9 ? "6/9" : "6";
                else q = n9 ? "add9" : "";
                if (s11) q += M7 || n13 ? "(#11)" : "(add#11)";
                if (n11) q += "(add11)";
                if (b9) q += "(b9)";
                if (s9 && !M7) q += "(add#9)";
                return q;
            }
            case ChordFamily.Augmented:
                return M7 ? "maj7#5" : b7 ? "7#5" : "+";
            case ChordFamily.Minor:
            {
                string q;
                if (M7) q = n9 ? "m(maj9)" : "m(maj7)";
                else if (b7) q = n11 ? (n13 ? "m13" : "m11") : n13 ? (n9 ? "m13" : "m7(13)") : n9 ? "m9" : "m7";
                else if (n13) q = n9 ? "m6/9" : "m6";
                else q = n9 ? (n11 ? "m(add9,11)" : "m(add9)") : n11 ? "m(add11)" : "m";
                if (b13 && P5) q += b7 ? "(b13)" : "(addb13)";
                if (b9) q += "(b9)";
                if (s11 && P5) q += "(#11)";
                return q;
            }
            case ChordFamily.Dominant:
            {
                // An altered dominant: two or more altered tensions and no natural 9 or 13.
                var alterations = new List<string>();
                if (b9) alterations.Add("b9");
                if (s9) alterations.Add("#9");
                if (s11) alterations.Add(P5 ? "#11" : "b5");
                if (b13) alterations.Add(P5 ? "b13" : "#5");
                if (alterations.Count >= 2 && !n9 && !n13 && (b9 || s9) && (b13 || s11)) return "7alt";
                var q = n13 ? "13" : n9 ? "9" : "7";
                if (n11) alterations.Add("11");
                return alterations.Count == 0 ? q : q + "(" + string.Join(",", alterations) + ")";
            }
            case ChordFamily.HalfDiminished:
                return (n11 ? "m11b5" : n9 ? "m9b5" : "m7b5") + (b13 ? "(b13)" : "");
            case ChordFamily.Diminished:
                return Bit(iv, 9) ? (M7 ? "dim7(maj7)" : "dim7") : M7 ? "dim(maj7)" : "dim";
            case ChordFamily.Sus:
            {
                var sus = n11 ? "sus4" : "sus2";
                string q;
                if (b7) q = n13 ? "13" + sus : n9 && n11 ? "9sus4" : "7" + sus;
                else if (M7) q = "maj7" + sus;
                else q = n11 && n9 ? "sus4(add9)" : sus;
                if (b9) q += "(b9)";
                return q;
            }
            default:
                return "5";
        }
    }

    /// <summary>
    /// The intervals a typed quality means: "", "m", "7", "maj9", "m7b5", "dim7", "13(#11)", "7alt", "9sus4", "6/9",
    /// "add9", "+", "ø", "°7", "Δ", "-7". Null when it can't be read.
    /// </summary>
    public static int? IntervalsOf(string quality)
    {
        // Brackets and commas stay in, so "7(9)" and "69" don't run together into one big number; they're skipped below.
        var q = quality.Replace(" ", "")
                       .Replace("♭", "b").Replace("♯", "#").Replace("Δ", "maj").Replace("^", "maj");
        int iv = 1 | 1 << 4 | 1 << 7;   // a major triad unless told otherwise
        void Set(params int[] i) { foreach (var x in i) iv |= 1 << x; }
        void Clear(params int[] i) { foreach (var x in i) iv &= ~(1 << x); }
        int Extend(int n, bool major7)
        {
            // 9, 11 and 13 bring the notes below them (an 11 chord leaves the third out, as players do).
            Set(major7 ? 11 : 10);
            if (n >= 9) Set(2);
            if (n >= 11) Set(5);
            if (n >= 13) { Set(9); if (n == 13) Clear(5); }
            return n;
        }
        var i0 = 0;
        bool Take(string s)
        {
            if (string.Compare(q, i0, s, 0, s.Length, StringComparison.Ordinal) != 0) return false;
            i0 += s.Length;
            return true;
        }
        int Number()
        {
            // Only chord numbers: 11 and 13 are the two-digit ones, so "69" is a 6 then a 9, not sixty-nine.
            if (Take("11")) return 11;
            if (Take("13")) return 13;
            return i0 < q.Length && char.IsDigit(q[i0]) ? q[i0++] - '0' : 0;
        }

        var minor = false;
        if (Take("maj") || Take("Maj") || Take("M"))
        {
            var n = Number();
            Extend(n == 0 ? 7 : n, major7: true);
            if (n == 11) Set(4);
        }
        else if (Take("m7b5") || Take("ø") || Take("-7b5") || Take("min7b5"))
        {
            iv = 1 | 1 << 3 | 1 << 6 | 1 << 10;
            if (Take("7")) { }
        }
        else if (Take("dim") || Take("°") || Take("o"))
        {
            iv = 1 | 1 << 3 | 1 << 6;
            if (Take("7")) Set(9);
        }
        else if (Take("aug") || Take("+"))
        {
            Clear(7);
            Set(8);
            var n = Number();
            if (n >= 7) Extend(n, major7: false);
        }
        else if (Take("min") || Take("mi") || Take("m") || Take("-"))
        {
            minor = true;
            Clear(4);
            Set(3);
            _ = Take("(");   // "Cm(maj7)"
            if (Take("maj") || Take("M"))
            {
                var n = Number();
                Extend(n == 0 ? 7 : n, major7: true);
            }
            else
            {
                var n = Number();
                if (n == 6) Set(9);
                else if (n >= 7) Extend(n, major7: false);
                if (n == 11) Clear(9);
            }
        }
        else
        {
            var n = Number();
            if (n == 5) iv = 1 | 1 << 7;
            else if (n == 6) Set(9);
            else if (n == 2) Set(2);
            else if (n >= 7)
            {
                Extend(n, major7: false);
                if (n == 11) Clear(4);   // "C11" is a sus chord with a 9: C, F, Bb, D
            }
        }
        // Then any of: 6/9, sus, sus2, sus4, add9, alt, b9, #9, #11, b13, b5, #5, 11, 13, omit3.
        while (i0 < q.Length)
        {
            if (Take("(") || Take(")") || Take(",")) { }
            else if (Take("/9")) Set(2);
            else if (Take("sus2")) { Clear(3, 4); Set(2); }
            else if (Take("sus4") || Take("sus")) { Clear(3, 4); Set(5); }
            else if (Take("add")) { var a = AddInterval(); if (a < 0) return null; Set(a); }
            else if (Take("alt")) { Clear(7, 2, 9); Set(10, 1, 3, 8); }
            else if (Take("b9")) Set(1);
            else if (Take("#9")) Set(3);
            else if (Take("#11")) Set(6);
            else if (Take("b13")) Set(8);
            else if (Take("b5")) { Clear(7); Set(6); }
            else if (Take("#5")) { Clear(7); Set(8); }
            else if (Take("13")) Set(9);
            else if (Take("11")) Set(5);
            else if (Take("9")) Set(2);
            else if (Take("7")) { if (!Bit(iv, 11)) Set(10); }
            else if (Take("6")) Set(9);
            else if (Take("no3") || Take("omit3")) Clear(3, 4);
            else return null;
        }
        _ = minor;
        return iv;

        int AddInterval()
        {
            if (Take("b9")) return 1;
            if (Take("#9")) return 3;
            if (Take("#11")) return 6;
            if (Take("b13")) return 8;
            return Number() switch { 2 or 9 => 2, 4 or 11 => 5, 6 or 13 => 9, _ => -1 };
        }
    }
}
