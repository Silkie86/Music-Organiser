using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi.Playground;

/// <summary>
/// Notes struck together played on another chord the way a player would (Solomon, 2026-10-27: "reharm by default should
/// keep the same amount of notes", "doesnt seem to change the chord when i chose richer chords"): as many notes as there
/// were, each moving as little as it can, with the new chord's most telling notes first: its 3rd and 7th, then the
/// colours its name asks for (the 9 of a Dm9, the 13 of a G13), the 5th left out first. A voicing with its root at the
/// bottom keeps the new root there (or a slash chord's bass); one without stays without, as a pianist leaves the root to
/// the bass; and a low note standing apart, as a left hand plays it, becomes the new chord's bass.
/// </summary>
public static class Revoicer
{
    /// <summary>The most notes voiced as a whole: past this a hit is played the old way, a note at a time (two hands rarely hold more).</summary>
    const int MostVoices = 7;

    /// <summary>
    /// The chord's notes as pitch classes, the most telling first: its 3rd (or a sus chord's 4th), its 7th (or a 6 chord's
    /// 6), an altered 5th, the colours its name has from the top down (13, 11, 9), the 5th, and the root last. A bass that's
    /// only a bass (the C of D/C) isn't one of them. A power chord has its 5th and root.
    /// </summary>
    public static List<int> Telling(ChordSymbol chord)
    {
        var upper = ChordSymbol.Rotate(PhraseChord.Of(chord).Mask, -chord.Root) | 1;
        int? third = null, seventh = null, sixth = null, fifth = null, altered = null;
        var colours = new List<int>();
        for (var i = 1; i < 12; i++)
        {
            if ((upper >> i & 1) == 0) continue;
            switch (ChordSpeller.Role(chord.Intervals, chord.Family, i))
            {
                case ToneRole.Third when third is null: third = i; break;
                case ToneRole.Seventh when i is 10 or 11 && seventh is null: seventh = i; break;
                case ToneRole.Seventh: sixth ??= i; break;   // a 6 chord's 6, a dim7's 7
                case ToneRole.Fifth when i == 7: fifth = i; break;
                case ToneRole.Fifth: altered ??= i; break;
                default: colours.Add(i); break;
            }
        }
        // With a 7th as well, a 6 is the 13 it's named as (Cmaj13), so it's a colour.
        if (seventh is null) seventh = sixth;
        else if (sixth is { } six) colours.Add(six);
        var order = new List<int>();
        void Add(int? i) { if (i is { } v) order.Add(ChordSymbol.Mod(chord.Root + v)); }
        Add(third);
        Add(seventh);
        Add(altered);
        foreach (var c in colours.OrderByDescending(c => c)) Add(c);
        Add(fifth);
        Add(0);
        return order;
    }

    /// <summary>The notes a hit needs to be heard as the chord: its 3rd, 7th, altered 5th and colours, as many as it has notes.</summary>
    static List<int> Needed(ChordSymbol chord, int notes)
    {
        var telling = Telling(chord);
        var needed = telling.Where(pc => pc != chord.Root && pc != ChordSymbol.Mod(chord.Root + 7)).ToList();
        return needed.Take(notes).ToList();
    }

    /// <summary>
    /// Whether <paramref name="keys"/> (struck together, or an arp's notes over one chord) already voice
    /// <paramref name="chord"/>: every note one of its own or a colour that sits sweetly on it, and its most telling notes
    /// all there, as many as there are notes (Solomon, 2026-10-27: "its not favouring good choices just seems like if it
    /// has a number associated it will accept it"). A voicing built up from its root (<paramref name="fromRoot"/>) needs the
    /// new root too when the chord has no 7th: C E G isn't an A minor, though every note sits on it. A plain triad always
    /// does, as it's heard as its own chord: C E G isn't an Am7 with nothing under it, where E G B D is a Cmaj9 without
    /// its root, as a pianist plays it over a bass.
    /// </summary>
    public static bool Voices(IReadOnlyList<int> keys, ChordSymbol chord, bool fromRoot, HarmonyStyle style = HarmonyStyle.JazzGospel)
    {
        if (keys.Count == 0) return true;
        var pcs = keys.Select(ChordSymbol.Mod).ToHashSet();
        var tones = Telling(chord).Append(chord.Bass).ToHashSet();
        if (pcs.Any(pc => !tones.Contains(pc) && ChordScales.Sit(chord, pc, style) < 0.7)) return false;
        if (!Needed(chord, pcs.Count).All(pcs.Contains)) return false;
        var rooted = pcs.Contains(chord.Bass) || pcs.Contains(chord.Root);
        if (Triad(pcs) && !rooted) return false;
        var seventh = chord.Has(10) || chord.Has(11) || chord.Family == ChordFamily.Diminished && chord.Has(9);
        return !fromRoot || seventh || rooted;
    }

    /// <summary>Whether the notes are a major or minor triad and nothing else (in any order, any octave).</summary>
    static bool Triad(HashSet<int> pcs) =>
        pcs.Count == 3 && pcs.Any(r => pcs.Contains(ChordSymbol.Mod(r + 7)) && (pcs.Contains(ChordSymbol.Mod(r + 3)) || pcs.Contains(ChordSymbol.Mod(r + 4))));

    /// <summary>
    /// <paramref name="hit"/>'s notes, heard as <paramref name="from"/>, played on <paramref name="to"/>: as many notes, the
    /// new chord's most telling ones, each moving as little as it can (see the class).
    /// </summary>
    public static List<NoteEvent> Revoice(IReadOnlyList<NoteEvent> hit, ChordSymbol from, ChordSymbol to)
    {
        var sorted = hit.OrderBy(n => n.Key).ToList();
        var keys = sorted.Select(n => n.Key).Distinct().ToArray();
        if (keys.Length < 2 || keys.Length > MostVoices) return Groove.Revoice(hit, from, to);
        var placed = Keys(keys, from, to);
        // Each note keeps its timing, length and touch: the one that was in its place.
        var map = keys.Zip(placed).ToDictionary(p => p.First, p => p.Second);
        var used = new HashSet<int>();
        var list = new List<NoteEvent>(hit.Count);
        foreach (var n in sorted)
            if (used.Add(map[n.Key])) list.Add(n with { Key = map[n.Key] });
        return list;
    }

    /// <summary>
    /// The new keys for <paramref name="keys"/> (low to high, one each), in the same order. With <paramref name="slashBass"/>
    /// false a voicing built up from its root keeps the new root at the bottom on a slash chord too, as an arp does (the
    /// bass part plays the slash chord's bass).
    /// </summary>
    public static int[] Keys(int[] keys, ChordSymbol from, ChordSymbol to, bool slashBass = true)
    {
        var shift = ChordSymbol.Mod(to.Root - from.Root);
        if (shift > 6) shift -= 12;
        if (keys.Length > MostVoices) return ByRole(keys, from, to, shift);
        var power = from.Family == ChordFamily.Power;
        var telling = power ? Telling(to).Where(pc => pc == to.Root || pc == to.Bass || ChordSymbol.Mod(pc - to.Root) is 6 or 7 or 8).ToList() : Telling(to);
        if (telling.Count == 0) telling = [to.Root];
        // A left hand's bass: a note standing a fifth or more under the rest, the bass of the new chord.
        if (keys.Length >= 3 && keys[1] - keys[0] >= 7)
        {
            var upper = Lead(keys[1..], Pick(telling.Where(pc => pc != to.Bass).DefaultIfEmpty(to.Root).ToList(), telling, keys.Length - 1), low: null);
            var target = keys[0] + shift;
            if (shift == 6 && keys[0] - 6 >= 28) target = keys[0] - 6;   // a tritone either way: down, while it stays a bass
            var bass = Groove.Nearest(to.Bass, target);
            while (bass >= upper[0] && bass - 12 >= 0) bass -= 12;
            return [bass, .. upper];
        }
        // Built up from its root (or a slash chord's bass): the new chord's bass at the bottom.
        var slashed = from.Bass != from.Root && keys[0] % 12 == from.Bass;
        if (slashed || keys[0] % 12 == from.Root)
        {
            var bottom = slashBass || slashed ? to.Bass : to.Root;
            var rest = Pick(telling.Where(pc => pc != bottom).DefaultIfEmpty(to.Root).ToList(), telling, keys.Length - 1);
            return Lead(keys, [bottom, .. rest], low: bottom);
        }
        return Lead(keys, Pick(telling, telling, keys.Length), low: null);
    }

    /// <summary>Each key moved to its place in the new chord on its own, as <see cref="Groove.Revoice"/> does, in the same order.</summary>
    static int[] ByRole(int[] keys, ChordSymbol from, ChordSymbol to, int shift)
    {
        var apart = keys.Length > 1 && keys[1] - keys[0] >= 7;
        return keys.Select((k, i) =>
        {
            var bass = i == 0 && (k % 12 == from.Bass && from.Bass != from.Root || apart && k % 12 == from.Root);
            var pc = bass ? to.Bass : ChordSymbol.Mod(to.Root + Groove.Map(from, to, ChordSymbol.Mod(k - from.Root)));
            return Groove.Nearest(pc, k + shift);
        }).ToArray();
    }

    /// <summary>
    /// The first <paramref name="count"/> of <paramref name="order"/>; past its end, notes doubled from the root up
    /// (<paramref name="all"/> read from its end: root, 5th, 3rd), as a chord with more fingers than notes is played.
    /// </summary>
    static List<int> Pick(List<int> order, List<int> all, int count)
    {
        var picked = order.Take(count).ToList();
        for (var i = 0; picked.Count < count; i++) picked.Add(all[all.Count - 1 - i % all.Count]);
        return picked;
    }

    /// <summary>
    /// <paramref name="pcs"/> placed for the voices at <paramref name="keys"/> (low to high), rising, so the voices move as
    /// little as they can in all: each moved semitone costs one, the top voice's half again (so it sings a smooth line),
    /// and notes packed close low down cost more, as they sound muddy there. With <paramref name="low"/> the lowest is that
    /// pitch class (the first of <paramref name="pcs"/>).
    /// </summary>
    static int[] Lead(int[] keys, List<int> pcs, int? low)
    {
        var n = keys.Length;
        var best = new int[n];
        var bestCost = double.MaxValue;
        var now = new int[n];
        var used = new bool[n];

        void Search(int voice, int prev, int before, double cost)
        {
            if (cost >= bestCost) return;
            if (voice == n)
            {
                bestCost = cost;
                Array.Copy(now, best, n);
                return;
            }
            // Every pitch class still to place, at each key near this voice, the nearest tried first: the first way found
            // is already a good one, so most of the rest are given up on early.
            var choices = new List<(int J, int Key, double Step)>();
            var seen = 0;
            for (var j = 0; j < n; j++)
            {
                if (used[j] || (seen >> pcs[j] & 1) != 0) continue;   // the same pitch class twice is the same choice
                if (low is not null && (voice == 0) != (j == 0)) continue;
                seen |= 1 << pcs[j];
                foreach (var key in Near(pcs[j], keys[voice]))
                    if (key > prev && key is >= 0 and <= 127)
                        choices.Add((j, key, Math.Abs(key - keys[voice]) * (voice == n - 1 ? 1.5 : 1) + (voice > 0 ? Packed(prev, key) : 0) +
                                             (voice > 1 && key - before <= 4 ? 3 : 0)));   // three notes inside a major third: a cluster
            }
            foreach (var (j, key, step) in choices.OrderBy(c => c.Step))
            {
                used[j] = true;
                now[voice] = key;
                Search(voice + 1, key, prev, cost + step);
                used[j] = false;
            }
        }

        Search(0, -1, -1, 0);
        return bestCost == double.MaxValue ? keys.ToArray() : best;   // nowhere for them all to go rising: left as they were
    }

    /// <summary>The keys of pitch class <paramref name="pc"/> within an octave and a fifth of <paramref name="key"/>.</summary>
    static IEnumerable<int> Near(int pc, int key)
    {
        var below = key - ChordSymbol.Mod(key - pc);
        return new[] { below - 12, below, below + 12, below + 24 }.Where(k => Math.Abs(k - key) <= 19);
    }

    /// <summary>What two neighbouring voices cost for being close together: a semitone rubs anywhere, and anything close sounds muddy low down.</summary>
    static double Packed(int lower, int upper) => (upper - lower) switch
    {
        1 => lower < 55 ? 8 : 2,
        2 => lower < 50 ? 5 : 0,
        3 or 4 => lower < 45 ? 3 : 0,
        _ => 0,
    };
}
