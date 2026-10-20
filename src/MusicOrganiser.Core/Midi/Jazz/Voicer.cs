using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi.Jazz;

/// <summary>How a chord part is spread over the keyboard.</summary>
public enum VoicingStyle
{
    /// <summary>The chord's notes packed close together around middle C.</summary>
    Close,
    /// <summary>Root, third and seventh: a left hand or a guitar's shell.</summary>
    Shell,
    /// <summary>Bill Evans's A and B voicings: 3-5-7-9 and 7-9-3-5, no root (leave that to the bass).</summary>
    Rootless,
    /// <summary>Four close notes with the second from the top dropped an octave: open and even, a jazz guitar and big-band sound.</summary>
    Drop2,
    /// <summary>Root and octave in the left hand, a cluster with the 9th on top in the right: the church sound.</summary>
    Gospel,
    /// <summary>Four voices, bass on the root, the rest close, no parallel fifths or octaves: hymn and string writing.</summary>
    Classical,
    /// <summary>One line on the thirds and sevenths, moving as little as it can: the line a jazz solo is built on.</summary>
    GuideTones,
}

/// <summary>When the chords play.</summary>
public enum CompRhythm
{
    /// <summary>Each chord held for as long as it lasts.</summary>
    Held,
    /// <summary>On every beat.</summary>
    Beats,
    /// <summary>On 1 and the "and" of 2, every bar: the swing-era comp.</summary>
    Charleston,
    /// <summary>Short stabs on 2 and 4.</summary>
    TwoAndFour,
    /// <summary>Held, but each change comes an eighth early: the push gospel and R&amp;B players use.</summary>
    Anticipate,
}

/// <summary>How a bassline is written from the chords.</summary>
public enum BassStyle
{
    /// <summary>The root, held.</summary>
    Roots,
    /// <summary>Root and fifth, on 1 and 3.</summary>
    RootFifth,
    /// <summary>A walking line in quarter notes: root first, chord and scale notes, then a note a semitone from the next root.</summary>
    Walking,
    /// <summary>Roots with octaves, and a chromatic run up into each change.</summary>
    Gospel,
}

/// <summary>
/// Writes MIDI from chords: chord parts in a choice of voicings, bass lines and guide-tone lines. For each chord it
/// lists the voicings the style allows in a sensible range, then picks the path through them that moves the fewest
/// semitones overall (Tymoczko's voice-leading distance, chosen for the whole run at once, not chord by chord), with
/// the classical style also keeping clear of parallel fifths and octaves.
/// </summary>
public static class Voicer
{
    const int Ppq = PhraseAnalyser.Ppq, Beat = Ppq, Bar = 4 * Ppq;

    /// <summary>
    /// Each chord held for as long as it lasts, its root low and just its own notes above, nothing added: chords that
    /// are heard back as exactly the chords they are (a chord part to start from, or new chords put on one).
    /// </summary>
    public static List<NoteEvent> Plain(IReadOnlyList<TimedChord> lane, int velocity = 90)
    {
        var notes = new List<NoteEvent>();
        foreach (var c in lane)
        {
            notes.Add(new NoteEvent(c.Start, c.Length, 36 + c.Chord.Root, velocity));
            for (var p = 0; p < 12; p++)
                if ((c.Chord.Mask >> p & 1) != 0) notes.Add(new NoteEvent(c.Start, c.Length, 52 + ChordSymbol.Mod(p - 4), velocity));
        }
        return notes;
    }

    /// <summary>The intervals that matter for voicing, picked from what the chord has (or what suits it when it doesn't say).</summary>
    readonly record struct Parts(int Third, int Fifth, int? Seventh, int Ninth, int Top);

    static Parts PartsOf(ChordSymbol c)
    {
        bool Has(int i) => c.Has(i);
        var third = c.Family switch
        {
            ChordFamily.Minor or ChordFamily.HalfDiminished or ChordFamily.Diminished => 3,
            ChordFamily.Sus => Has(5) ? 5 : 2,
            ChordFamily.Power => 7,
            _ => 4,
        };
        var fifth = Has(7) ? 7 : Has(6) && c.Family is ChordFamily.HalfDiminished or ChordFamily.Diminished or ChordFamily.Dominant ? 6
                  : Has(8) ? 8 : 7;
        int? seventh = c.Family switch
        {
            ChordFamily.Diminished when Has(9) => 9,
            ChordFamily.Diminished => null,
            _ when Has(10) => 10,
            _ when Has(11) => 11,
            _ when Has(9) => 9,   // the 6 of a 6 chord
            _ => null,
        };
        // The colour on top: the chord's own tension if it has one.
        var ninth = Has(1) ? 1 : Has(3) && c.Family == ChordFamily.Dominant ? 3 : Has(2) ? 2
                  : c.Family is ChordFamily.HalfDiminished ? 5 : c.Family is ChordFamily.Diminished ? 2 : 2;
        var top = c.Family == ChordFamily.Dominant ? (Has(8) ? 8 : 9)
                : c.Family is ChordFamily.Major && seventh == 9 ? 2
                : fifth;
        return new Parts(third, fifth, seventh, ninth, top);
    }

    /// <summary>Every voicing of the chord the style allows, as MIDI notes from low to high.</summary>
    internal static List<int[]> Candidates(ChordSymbol c, VoicingStyle style)
    {
        var p = PartsOf(c);
        var r = c.Root;
        var list = new List<int[]>();
        // Places a stack of intervals (from the root) so its lowest note is in [low, high].
        void Place(int low, int high, params int[] stack)
        {
            // A power chord has no third or seventh, so its "third" and "fifth" can be the same note: played once.
            var sorted = stack.Distinct().OrderBy(x => x).ToArray();
            for (var bottom = low; bottom <= high; bottom++)
            {
                if (ChordSymbol.Mod(bottom - r - sorted[0]) != 0) continue;
                var shift = bottom - sorted[0];
                list.Add(sorted.Select(x => x + shift).ToArray());
            }
        }
        // All the close positions (each inversion) of a set of intervals within an octave.
        IEnumerable<int[]> Inversions(int[] set)
        {
            var notes = set.Select(ChordSymbol.Mod).Distinct().OrderBy(x => x).ToArray();
            for (var k = 0; k < notes.Length; k++)
            {
                var inv = new int[notes.Length];
                for (var j = 0; j < notes.Length; j++)
                {
                    inv[j] = notes[(k + j) % notes.Length];
                }
                // Make strictly rising.
                for (var j = 1; j < inv.Length; j++) while (inv[j] <= inv[j - 1]) inv[j] += 12;
                yield return inv;
            }
        }

        int[] core = p.Seventh is { } s7 ? [0, p.Third, p.Fifth, s7] : [0, p.Third, p.Fifth];
        switch (style)
        {
            case VoicingStyle.Close:
                foreach (var inv in Inversions(core)) Place(53, 64, inv);
                break;
            case VoicingStyle.Shell:
                if (p.Seventh is { } sh)
                {
                    Place(36, 50, 0, p.Third, sh);
                    Place(36, 50, 0, sh, p.Third + 12);
                }
                else Place(36, 50, 0, p.Third, p.Fifth);
                break;
            case VoicingStyle.Rootless:
            {
                var sev = p.Seventh ?? (c.Family == ChordFamily.Major ? 9 : 10);
                var fifthOr13 = c.Family == ChordFamily.Dominant ? p.Top : p.Fifth;
                Place(48, 60, p.Third, fifthOr13 < p.Third ? fifthOr13 + 12 : fifthOr13, sev < p.Third ? sev + 12 : sev, p.Ninth + 12 + (p.Ninth + 12 <= sev ? 12 : 0));
                Place(48, 60, sev, p.Ninth + 12 < sev ? p.Ninth + 24 : p.Ninth + 12, p.Third + 12 < sev ? p.Third + 24 : p.Third + 12,
                      fifthOr13 + 12 < sev ? fifthOr13 + 24 : fifthOr13 + 12);
                break;
            }
            case VoicingStyle.Drop2:
            {
                int[] four = p.Seventh is { } d7 ? [c.Has(2) || c.Has(1) || c.Has(3) ? p.Ninth : 0, p.Third, p.Fifth, d7] : [0, p.Third, p.Fifth, 0 + 12];
                foreach (var close in Inversions(four.Distinct().Count() == 4 ? four : [0, p.Third, p.Fifth, p.Ninth]))
                {
                    if (close.Length < 3) { Place(43, 57, close); continue; }
                    // A triad (or a sus chord) has three notes: its bottom note doubled on top makes the four to drop.
                    var inv = close.Length == 3 ? [close[0], close[1], close[2], close[0] + 12] : close;
                    var dropped = new[] { inv[2] - 12, inv[0], inv[1], inv[3] };
                    Place(43, 57, dropped);
                }
                break;
            }
            case VoicingStyle.Gospel:
            {
                // Right hand: third, seventh, ninth and the colour on top, close; left hand: root and octave.
                var sev = p.Seventh ?? (c.Family is ChordFamily.Minor ? 10 : c.Family is ChordFamily.Major ? 9 : 10);
                int[] upper = c.Family == ChordFamily.Sus ? [p.Third, sev, p.Ninth, p.Fifth] : [p.Third, sev, p.Ninth, p.Top];
                upper = upper.Select(ChordSymbol.Mod).Distinct().ToArray();
                var rights = new List<int[]>();
                foreach (var inv in Inversions(upper))
                    for (var bottom = 58; bottom <= 69; bottom++)
                        if (ChordSymbol.Mod(bottom - r - inv[0]) == 0) rights.Add(inv.Select(x => x + bottom - inv[0]).ToArray());
                var bass = c.Bass;
                foreach (var right in rights)
                    for (var low = 33; low <= 44; low++)
                        if (ChordSymbol.Mod(low - bass) == 0) list.Add([low, low + 12, .. right]);
                break;
            }
            case VoicingStyle.Classical:
            {
                var triad = new[] { 0, p.Third, p.Fifth };
                int[][] uppers = p.Seventh is { } c7 && c.Family is ChordFamily.Dominant or ChordFamily.Diminished or ChordFamily.HalfDiminished
                    ? [[p.Third, p.Fifth, c7], [0, p.Third, c7]]
                    : [triad];
                var basses = new List<int> { c.Bass };
                if (c.Bass == c.Root) basses.Add(ChordSymbol.Mod(r + p.Third));   // first inversion
                foreach (var up in uppers)
                    foreach (var inv in Inversions(up))
                        for (var bottom = 55; bottom <= 67; bottom++)
                        {
                            if (ChordSymbol.Mod(bottom - r - inv[0]) != 0) continue;
                            var right = inv.Select(x => x + bottom - inv[0]).ToArray();
                            if (right[^1] > 79) continue;
                            foreach (var b in basses)
                                for (var low = 40; low <= 55; low++)
                                    if (ChordSymbol.Mod(low - b) == 0 && low < right[0]) list.Add([low, .. right]);
                        }
                break;
            }
            case VoicingStyle.GuideTones:
                foreach (var i in new[] { p.Third, p.Seventh ?? p.Fifth })
                    for (var n = 58; n <= 72; n++)
                        if (ChordSymbol.Mod(n - r - i) == 0) list.Add([n]);
                break;
        }
        if (list.Count == 0) Place(53, 64, core);
        return list;
    }

    /// <summary>
    /// The semitones voices move going from one voicing to the next (each note to its nearest), the top voice
    /// counted again so it sings a smooth line.
    /// </summary>
    internal static double Movement(int[] a, int[] b, bool classical)
    {
        double cost = 0;
        if (a.Length == b.Length)
            for (var i = 0; i < a.Length; i++) cost += Math.Abs(a[i] - b[i]);
        else
        {
            foreach (var n in b) cost += a.Min(x => Math.Abs(x - n));
            foreach (var n in a) cost += 0.5 * b.Min(x => Math.Abs(x - n));
        }
        cost += 0.5 * Math.Abs(a[^1] - b[^1]);
        if (classical && a.Length == b.Length)
        {
            // Parallel fifths and octaves: two voices a fifth (or octave) apart that move together to another.
            for (var i = 0; i < a.Length; i++)
                for (var j = i + 1; j < a.Length; j++)
                {
                    int before = ChordSymbol.Mod(a[j] - a[i]), after = ChordSymbol.Mod(b[j] - b[i]);
                    if ((before == 7 && after == 7 || before == 0 && after == 0) && a[i] != b[i]) cost += 12;
                }
            // Voices crossing a big leap in the bass is fine; a leap in an upper voice is not.
            for (var i = 1; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > 7) cost += 4;
        }
        return cost;
    }

    /// <summary>The voicing for each chord, chosen for the smoothest run through them all.</summary>
    public static List<int[]> VoicePath(IReadOnlyList<ChordSymbol> chords, VoicingStyle style)
    {
        if (chords.Count == 0) return [];
        var classical = style == VoicingStyle.Classical;
        var cands = chords.Select(c => Candidates(c, style)).ToList();
        var centre = style switch
        {
            VoicingStyle.Shell => 46, VoicingStyle.Classical => 60, VoicingStyle.GuideTones => 65, VoicingStyle.Gospel => 58, _ => 60,
        };
        double Range(int[] v) => 0.15 * Math.Abs(v.Average() - centre);
        var cost = cands[0].Select(Range).ToArray();
        var from = new List<int[]>();
        for (var k = 1; k < cands.Count; k++)
        {
            var next = new double[cands[k].Count];
            var back = new int[cands[k].Count];
            for (var j = 0; j < cands[k].Count; j++)
            {
                var best = double.MaxValue;
                for (var i = 0; i < cands[k - 1].Count; i++)
                {
                    var c = cost[i] + Movement(cands[k - 1][i], cands[k][j], classical);
                    if (c < best) { best = c; back[j] = i; }
                }
                next[j] = best + Range(cands[k][j]);
            }
            from.Add(back);
            cost = next;
        }
        var path = new int[cands.Count];
        path[^1] = Array.IndexOf(cost, cost.Min());
        for (var k = cands.Count - 1; k > 0; k--) path[k - 1] = from[k - 1][path[k]];
        return path.Select((p, k) => cands[k][p]).ToList();
    }

    /// <summary>A chord part for the lane, in a voicing style and a rhythm, up to <paramref name="length"/> ticks.</summary>
    public static List<NoteEvent> Voice(IReadOnlyList<TimedChord> lane, VoicingStyle style, CompRhythm rhythm, long length, int velocity = 86)
    {
        var notes = new List<NoteEvent>();
        if (lane.Count == 0) return notes;
        var voicings = VoicePath(lane.Select(c => c.Chord).ToList(), style);
        for (var k = 0; k < lane.Count; k++)
        {
            var span = lane[k];
            var end = Math.Min(span.End, length);
            foreach (var (at, len) in Hits(span.Start, end, rhythm, k == 0))
            {
                var v = voicings[k];
                for (var i = 0; i < v.Length; i++)
                {
                    // The top voice a touch louder, so the line on top sings.
                    var vel = Math.Clamp(velocity + (i == v.Length - 1 && v.Length > 1 ? 8 : 0) - (style == VoicingStyle.Gospel && i < 2 ? 4 : 0), 1, 127);
                    notes.Add(new NoteEvent(at, Math.Max(1, len), v[i], vel));
                }
            }
        }
        return notes.OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
    }

    /// <summary>When a chord from <paramref name="start"/> to <paramref name="end"/> is struck, and for how long.</summary>
    static IEnumerable<(long At, long Length)> Hits(long start, long end, CompRhythm rhythm, bool first)
    {
        switch (rhythm)
        {
            case CompRhythm.Beats:
                for (var at = start; at < end; at += Beat) yield return (at, Math.Min(Beat * 9 / 10, end - at));
                break;
            case CompRhythm.Charleston:
            {
                var any = false;
                for (var bar = start / Bar * Bar; bar < end; bar += Bar)
                    foreach (var (o, l) in new[] { (0L, Beat * 3 / 2), (Beat * 3 / 2, Beat) })
                    {
                        var at = bar + o;
                        if (at < start || at >= end) continue;
                        any = true;
                        yield return (at, Math.Min(l, end - at));
                    }
                if (!any) yield return (start, end - start);
                break;
            }
            case CompRhythm.TwoAndFour:
            {
                var any = false;
                for (var at = start / Beat * Beat; at < end; at += Beat)
                {
                    if (at < start || (at / Beat) % 2 == 0) continue;
                    any = true;
                    yield return (at, Math.Min(Beat / 2, end - at));
                }
                if (!any) yield return (start, Math.Min(Beat / 2, end - start));
                break;
            }
            case CompRhythm.Anticipate:
            {
                var early = first || start == 0 ? start : start - Beat / 2;
                yield return (early, end - early - Beat / 2 > 0 ? end - early - Beat / 2 : end - early);
                break;
            }
            default:
                yield return (start, end - start);
                break;
        }
    }

    /// <summary>A bassline for the lane, in a style, up to <paramref name="length"/> ticks (going round when the lane is shorter).</summary>
    public static List<NoteEvent> Bass(IReadOnlyList<TimedChord> lane, BassStyle style, long length, int velocity = 96)
    {
        var notes = new List<NoteEvent>();
        if (lane.Count == 0) return notes;
        const int Low = 33, High = 52;
        var last = 40;
        int Near(int pc, int around)
        {
            var best = -1;
            for (var n = Low; n <= High; n++)
                if (ChordSymbol.Mod(n - pc) == 0 && (best < 0 || Math.Abs(n - around) < Math.Abs(best - around))) best = n;
            return best;
        }
        for (var k = 0; k < lane.Count; k++)
        {
            var span = lane[k];
            var c = span.Chord;
            var nextChord = lane[(k + 1) % lane.Count].Chord;
            var end = Math.Min(span.End, length);
            if (end <= span.Start) continue;
            var root = Near(c.Bass, last);
            var p = PartsOf(c);
            switch (style)
            {
                case BassStyle.Roots:
                    for (var at = span.Start; at < end; at += Bar) notes.Add(new NoteEvent(at, Math.Min(Bar, end - at) - Ppq / 8, root, velocity));
                    last = root;
                    break;
                case BassStyle.RootFifth:
                {
                    var fifth = Near(c.Root + p.Fifth, root);
                    var i = 0;
                    for (var at = span.Start; at < end; at += 2 * Beat, i++)
                        notes.Add(new NoteEvent(at, Math.Min(2 * Beat, end - at) - Ppq / 8, i % 2 == 0 ? root : fifth, velocity - (i % 2) * 6));
                    last = root;
                    break;
                }
                case BassStyle.Walking:
                {
                    var beats = (int)Math.Max(1, (end - span.Start) / Beat);
                    var target = Near(nextChord.Bass, root);
                    var scale = ChordScales.Scale(c);
                    var line = new List<int> { root };
                    // Approach the next root from a semitone below or above, whichever the line is nearer.
                    var approach = target + (target > root ? -1 : 1);
                    for (var b = 1; b < beats; b++)
                    {
                        if (b == beats - 1) { line.Add(approach); break; }
                        // Step towards the approach note through chord and scale notes.
                        var prev = line[^1];
                        var dir = Math.Sign(approach - prev);
                        if (dir == 0) dir = 1;
                        var chordTones = new[] { c.Root + p.Third, c.Root + p.Fifth, c.Root + (p.Seventh ?? 12) }.Select(ChordSymbol.Mod).ToHashSet();
                        int? pick = null;
                        for (var step = 1; step <= 5 && pick is null; step++)
                        {
                            var n = prev + dir * step;
                            if (n < Low || n > High) { dir = -dir; continue; }
                            if (chordTones.Contains(ChordSymbol.Mod(n)) || (step <= 2 && scale.Contains(ChordSymbol.Mod(n)))) pick = n;
                        }
                        line.Add(pick ?? prev);
                    }
                    for (var b = 0; b < line.Count; b++)
                    {
                        var at = span.Start + b * Beat;
                        if (at >= end) break;
                        notes.Add(new NoteEvent(at, Beat - Ppq / 10, line[b], velocity - (b % 2) * 8));
                    }
                    last = line[^1];
                    break;
                }
                case BassStyle.Gospel:
                {
                    var target = Near(nextChord.Bass, root);
                    for (var at = span.Start; at < end; at += Bar)
                    {
                        var barEnd = Math.Min(at + Bar, end);
                        var lastBar = barEnd == end;
                        notes.Add(new NoteEvent(at, Math.Min(Beat * 3 / 2, barEnd - at) - Ppq / 10, root, velocity));
                        if (at + Beat * 3 / 2 < barEnd && (!lastBar || barEnd - at > 2 * Beat))
                            notes.Add(new NoteEvent(at + Beat * 3 / 2, Beat / 2, root + 12 <= High + 12 ? root + 12 : root, velocity - 12));
                        if (lastBar && barEnd - at >= 2 * Beat)
                        {
                            // A chromatic run up (or down) into the next chord: three eighths.
                            var dir = target >= root ? 1 : -1;
                            for (var i = 3; i >= 1; i--)
                            {
                                var t = barEnd - i * Beat / 2;
                                if (t <= at + Beat * 2) continue;
                                notes.Add(new NoteEvent(t, Beat / 2 - Ppq / 16, target - dir * i, velocity - 6));
                            }
                        }
                    }
                    last = root;
                    break;
                }
            }
        }
        return notes.Where(n => n.Start < length).Select(n => n with { Length = Math.Max(1, n.Length) }).OrderBy(n => n.Start).ToList();
    }
}
