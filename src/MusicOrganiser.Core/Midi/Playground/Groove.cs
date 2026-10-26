using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi.Playground;

/// <summary>How much of another part's groove to copy, from a touch to everything.</summary>
public enum GrooveDepth
{
    /// <summary>Its swing, push and pull, and accents: the notes stay where they are, they just move and lean like its.</summary>
    Feel,
    /// <summary>Its rhythm too: your chords, as they're voiced, struck when and for as long as it strikes, as hard.</summary>
    Rhythm,
    /// <summary>Its whole playing: its rhythm and how its voicings move, played on your chords (block chords played like a pro).</summary>
    Playing,
}

/// <summary>
/// A part's groove on a sixteenth grid, one bar round: how early or late each step lands (ticks), how hard it's played
/// against the part's average, and how often anything plays there. Swung eighths written as triplets are heard as
/// late off-beats, not as sixteenths, so a swing feel copies as swing.
/// </summary>
public sealed record GrooveTemplate(double[] Offset, double[] Accent, int[] Count)
{
    public const int Steps = 16;

    /// <summary>The swing of its eighth off-beats: 50% straight, 67% triplet swing; null when it plays none.</summary>
    public double? Swing
    {
        get
        {
            double sum = 0;
            var n = 0;
            for (var s = 2; s < Steps; s += 4) { sum += Offset[s] * Count[s]; n += Count[s]; }
            return n == 0 ? null : 50 + 100 * (sum / n) / PhraseAnalyser.Ppq;
        }
    }

    /// <summary>On average how early (negative) or late its beats land, in ticks.</summary>
    public double Lean
    {
        get
        {
            double sum = 0;
            var n = 0;
            for (var s = 0; s < Steps; s += 4) { sum += Offset[s] * Count[s]; n += Count[s]; }
            return n == 0 ? 0 : sum / n;
        }
    }

    /// <summary>In a few words: "Swing 64% · behind the beat · strong 2 and 4".</summary>
    public string Describe()
    {
        var bits = new List<string>();
        if (Swing is { } sw) bits.Add(sw < 54 ? "Straight" : $"Swing {sw:0}%");
        var lean = Lean;
        if (lean > 2.5) bits.Add("behind the beat");
        else if (lean < -2.5) bits.Add("ahead of the beat");
        if (Count[4] + Count[12] > 0 && Count[0] + Count[8] > 0)
        {
            var backbeat = (Accent[4] + Accent[12]) / 2 - (Accent[0] + Accent[8]) / 2;
            if (backbeat > 0.08) bits.Add("strong 2 and 4");
            else if (backbeat < -0.08) bits.Add("strong 1 and 3");
        }
        return bits.Count == 0 ? "On the grid" : string.Join(" · ", bits);
    }
}

/// <summary>
/// Copying the groove of one part onto another (Solomon, 2026-10-17: "take the swing and timing of one part and put it on
/// another", and "the block chord thing should just be a part of the groove copying"). Three depths: just the feel
/// (timing and accents, the GrooVAE idea of a groove as offsets and velocities on a grid), its rhythm too (your chords
/// struck on its hits), or its whole playing: each note of the source is heard as a part of its own chord (root, third,
/// seventh, ninth…) and played as the same part of your chord at that moment, near where it was, so a pro's gospel comp
/// over Eb–Ab plays your Dm9–G13 with the same moves, timing and touch.
/// </summary>
public static class Groove
{
    const int Ppq = PhraseAnalyser.Ppq, Bar = 4 * Ppq, Step = Ppq / 4;

    /// <summary>Whether a part's off-beats are swung eighths rather than sixteenths: it plays almost nothing on the "e".</summary>
    static bool Swung(IReadOnlyList<NoteEvent> notes)
    {
        int e = 0, off = 0;
        foreach (var n in notes)
        {
            var phase = n.Start % Ppq / (double)Ppq;
            if (phase is >= 0.15 and < 0.37) e++;
            else if (phase is >= 0.37 and < 0.8) off++;
        }
        return off >= 2 && e <= off * 0.15;
    }

    /// <summary>The grid step a note belongs to (counted from the start), and how far off it lands.</summary>
    static (long Step, double Offset) Place(long start, bool swung)
    {
        var beat = start / Ppq;
        var inBeat = start - beat * Ppq;
        var phase = inBeat / (double)Ppq;
        if (swung && phase is >= 0.37 and < 0.8)
            return (beat * 4 + 2, inBeat - Ppq / 2.0);
        var step = (long)Math.Round(start / (double)Step);
        return (step, start - step * (double)Step);
    }

    /// <summary>The groove of a part, or null when it has too few notes to say.</summary>
    public static GrooveTemplate? Of(IReadOnlyList<NoteEvent> notes)
    {
        var played = notes.Where(n => !n.Muted).ToList();
        if (played.Count < 3) return null;
        var swung = Swung(played);
        var offset = new double[GrooveTemplate.Steps];
        var velocity = new double[GrooveTemplate.Steps];
        var count = new int[GrooveTemplate.Steps];
        // A chord counts once per hit, at its earliest note and loudest velocity.
        foreach (var hit in Hits(played))
        {
            var (step, off) = Place(hit.Start, swung);
            var s = (int)(step % GrooveTemplate.Steps);
            offset[s] += off;
            velocity[s] += hit.Velocity;
            count[s]++;
        }
        var mean = velocity.Sum() / Math.Max(1, count.Sum());
        var accent = new double[GrooveTemplate.Steps];
        for (var s = 0; s < GrooveTemplate.Steps; s++)
        {
            if (count[s] == 0) { accent[s] = 1; continue; }
            offset[s] /= count[s];
            accent[s] = mean <= 0 ? 1 : velocity[s] / count[s] / mean;
        }
        return new GrooveTemplate(offset, accent, count);
    }

    /// <summary>Notes struck together (within a 32nd) as one hit: when, how loud the loudest, how long the longest.</summary>
    internal static IEnumerable<(long Start, int Velocity, long Length, List<NoteEvent> Notes)> Hits(IReadOnlyList<NoteEvent> notes)
    {
        var sorted = notes.OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
        var i = 0;
        while (i < sorted.Count)
        {
            var start = sorted[i].Start;
            var group = new List<NoteEvent>();
            while (i < sorted.Count && sorted[i].Start - start <= Ppq / 8) group.Add(sorted[i++]);
            yield return (start, group.Max(n => n.Velocity), group.Max(n => n.Length), group);
        }
    }

    /// <summary>The notes moved and leaned like the groove; <paramref name="amount"/> 0 to 1 says how far.</summary>
    public static List<NoteEvent> ApplyFeel(IReadOnlyList<NoteEvent> notes, GrooveTemplate groove, double amount = 1)
    {
        amount = Math.Clamp(amount, 0, 1);
        var swung = Swung(notes);
        // Off-beats it never plays still swing like the ones it does.
        double offbeat = 0;
        var offbeats = 0;
        for (var s = 2; s < GrooveTemplate.Steps; s += 4) { offbeat += groove.Offset[s] * groove.Count[s]; offbeats += groove.Count[s]; }
        offbeat = offbeats == 0 ? 0 : offbeat / offbeats;
        var list = new List<NoteEvent>(notes.Count);
        foreach (var n in notes)
        {
            var (step, off) = Place(n.Start, swung);
            var s = (int)(step % GrooveTemplate.Steps);
            var grid = step * Step;
            var want = groove.Count[s] > 0 ? groove.Offset[s] : s % 4 == 2 ? offbeat : 0;
            var start = Math.Max(0, (long)Math.Round(grid + off * (1 - amount) + want * amount));
            var end = n.Start + n.Length;
            // Short notes keep their length; long ones keep their end, so a held chord doesn't run into the next.
            var length = n.Length <= 2 * Step ? n.Length : Math.Max(1, end - start);
            var velocity = (int)Math.Round(n.Velocity * (1 + (groove.Accent[s] - 1) * amount));
            list.Add(n with { Start = start, Length = length, Velocity = Math.Clamp(velocity, 1, 127) });
        }
        return list.OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
    }

    /// <summary>
    /// Your notes struck on the source's hits: at each hit, whatever your part plays at that moment (or the chord it's
    /// about to change to, when the hit pushes ahead of the change), as long and as hard as the source plays it.
    /// </summary>
    public static List<NoteEvent> ApplyRhythm(IReadOnlyList<NoteEvent> notes, IReadOnlyList<NoteEvent> source, long length)
    {
        var mine = Hits(notes.Where(n => !n.Muted).ToList()).ToList();
        if (mine.Count == 0) return [];
        var hits = Hits(Looped(source, length)).ToList();
        var list = new List<NoteEvent>();
        foreach (var hit in hits)
        {
            // The block sounding here, or the next one if this hit is a push into it.
            var block = mine.LastOrDefault(b => b.Start <= hit.Start + Ppq / 8);
            var next = mine.FirstOrDefault(b => b.Start > hit.Start + Ppq / 8);
            if (next.Notes is not null && next.Start - hit.Start <= Ppq / 2 && hit.Start + hit.Length > next.Start) block = next;
            if (block.Notes is null) block = mine[0];
            var keys = block.Notes.Select(n => n.Key).Distinct().OrderBy(k => k).ToList();
            var theirs = hit.Notes.OrderBy(n => n.Key).ToList();
            for (var i = 0; i < keys.Count; i++)
            {
                // Each voice takes the touch of the source voice in the same place, top to top.
                var src = theirs[Math.Clamp(theirs.Count - keys.Count + i, 0, theirs.Count - 1)];
                list.Add(new NoteEvent(hit.Start, Math.Max(1, Math.Min(src.Length, length - hit.Start)), keys[i], src.Velocity));
            }
        }
        return list.OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
    }

    /// <summary>
    /// Your chords played the way the source plays its own: each source note becomes the same part of your chord at
    /// that moment (its third to your third, its ninth to your ninth, its bass to your bass), placed nearest to where it
    /// was, with its timing, length and velocity kept.
    /// </summary>
    public static List<NoteEvent> ApplyPlaying(IReadOnlyList<TimedChord> lane, IReadOnlyList<NoteEvent> source, long length)
    {
        if (lane.Count == 0) return [];
        var period = PeriodOf(source);
        var theirLane = ChordLane.Detect(source, period);
        if (theirLane.Count == 0) return [];
        var list = new List<NoteEvent>();
        foreach (var hit in Hits(Looped(source, length)))
        {
            var theirs = ChordAround(theirLane, hit.Start % period, hit.Length, period);
            var ours = ChordAround(lane, hit.Start, hit.Length, length);
            if (theirs is not { } s || ours is not { } t) continue;
            list.AddRange(Revoice(hit.Notes, s, t).Select(n => n with { Length = Math.Max(1, Math.Min(n.Length, length - n.Start)) }));
        }
        return list.Where(n => n.Start < length).OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
    }

    /// <summary>
    /// Notes struck together over chord <paramref name="from"/> played on chord <paramref name="to"/>: each one the same
    /// part of the new chord (<see cref="Map"/>), nearest to where it was moved by the change of root, the bass staying
    /// the bass. Two notes landing on the same key are played once.
    /// </summary>
    public static List<NoteEvent> Revoice(IReadOnlyList<NoteEvent> hit, ChordSymbol from, ChordSymbol to)
    {
        var list = new List<NoteEvent>();
        if (hit.Count == 0) return list;
        var shift = ChordSymbol.Mod(to.Root - from.Root);
        if (shift > 6) shift -= 12;
        var lowest = hit.Min(n => n.Key);
        var used = new HashSet<int>();
        foreach (var n in hit.OrderBy(n => n.Key))
        {
            var bass = n.Key == lowest && hit.Count > 1 && n.Key % 12 == from.Bass && from.Bass != from.Root;
            var pc = bass ? to.Bass : ChordSymbol.Mod(to.Root + Map(from, to, ChordSymbol.Mod(n.Key - from.Root)));
            if (n.Key == lowest && n.Key % 12 == from.Root && hit.Count > 1) pc = to.Bass;   // the root in the bass stays the bass
            var key = Nearest(pc, n.Key + shift);
            if (used.Add(key)) list.Add(n with { Key = key });
        }
        return list;
    }

    /// <summary>The chord a hit belongs to: the one sounding, or the next when the hit pushes into it and holds over the change.</summary>
    static ChordSymbol? ChordAround(IReadOnlyList<TimedChord> lane, long at, long held, long loop)
    {
        var a = loop > 0 ? at % loop : at;
        foreach (var c in lane)
            foreach (var start in loop > 0 ? new[] { c.Start, c.Start + loop } : [c.Start])
                if (start > a && start - a <= Ppq / 2 && a + held > start) return c.Chord;
        return ChordLane.At(lane, a) ?? lane.LastOrDefault(c => c.Start <= a)?.Chord ?? lane[0].Chord;
    }

    /// <summary>
    /// What an interval above their chord's root is on ours: the same chord member (third, fifth, seventh), and for a
    /// tension (9, 11, 13) the version of it our chord's scale likes best.
    /// </summary>
    public static int Map(ChordSymbol from, ChordSymbol to, int interval)
    {
        var f = Members(from);
        var t = Members(to);
        if (interval == 0) return 0;
        // The fifth first: a power chord's "third" is its fifth too, and should stay the fifth.
        if (interval == f.Fifth) return t.Fifth;
        if (interval == f.Third) return t.Third;
        if (f.Seventh is { } s7 && interval == s7) return t.Seventh ?? 0;
        int[] options = interval switch
        {
            1 or 2 => [2, 1, 3],
            3 => f.Third == 4 ? [3, 2, 1] : [t.Third],
            4 => [t.Third],
            7 => [t.Fifth],
            5 or 6 => [5, 6],
            8 or 9 => [9, 8],
            _ => [t.Seventh ?? 0],
        };
        if (options.Length == 1) return options[0];
        // The tension as written when our chord takes it, else the one its scale likes best.
        if (options.Contains(interval) && ChordScales.Sit(to, ChordSymbol.Mod(to.Root + interval)) >= 0.6) return interval;
        return options.MaxBy(o => ChordScales.Sit(to, ChordSymbol.Mod(to.Root + o)) - (o == interval ? 0 : 0.01));
    }

    static (int Third, int Fifth, int? Seventh) Members(ChordSymbol c)
    {
        var third = c.Family switch
        {
            ChordFamily.Minor or ChordFamily.HalfDiminished or ChordFamily.Diminished => 3,
            ChordFamily.Sus => c.Has(5) ? 5 : 2,
            ChordFamily.Power => 7,
            _ => 4,
        };
        var fifth = c.Has(7) ? 7 : c.Has(6) && c.Family is ChordFamily.HalfDiminished or ChordFamily.Diminished ? 6 : c.Has(8) ? 8 : 7;
        int? seventh = c.Has(10) ? 10 : c.Has(11) ? 11 : c.Family == ChordFamily.Diminished && c.Has(9) ? 9 : c.Has(9) ? 9 : null;
        return (third, fifth, seventh);
    }

    /// <summary>The key of pitch class <paramref name="pc"/> nearest to <paramref name="around"/>.</summary>
    public static int Nearest(int pc, int around)
    {
        var k = around - ChordSymbol.Mod(around - pc);
        if (around - k > 6) k += 12;
        return Math.Clamp(k, 0, 127);
    }

    /// <summary>A part going round to fill the length.</summary>
    static List<NoteEvent> Looped(IReadOnlyList<NoteEvent> source, long length)
    {
        var played = source.Where(n => !n.Muted).ToList();
        return SectionMatcher.Loop(played, PeriodOf(played), length);
    }

    /// <summary>How long a part goes round: whole bars up to its last hit (a note held over the bar line doesn't add one).</summary>
    static long PeriodOf(IReadOnlyList<NoteEvent> notes) =>
        notes.Count == 0 ? Bar : (notes.Max(n => n.Start) / Bar + 1) * Bar;

    /// <summary>One part with another's groove, as deep as asked: the target's chords for <see cref="GrooveDepth.Playing"/> are its own, or the lane's.</summary>
    public static List<NoteEvent> Copy(IReadOnlyList<NoteEvent> target, IReadOnlyList<NoteEvent> source, GrooveDepth depth, long length,
                                       IReadOnlyList<TimedChord>? lane = null, double amount = 1)
    {
        switch (depth)
        {
            case GrooveDepth.Rhythm:
                return ApplyRhythm(target, source, length);
            case GrooveDepth.Playing:
            {
                var chords = lane is { Count: > 0 } ? lane : ChordLane.Detect(target, length);
                return ApplyPlaying(chords, source, length);
            }
            default:
                return Of(source) is { } g ? ApplyFeel(target, g, amount) : target.ToList();
        }
    }
}
