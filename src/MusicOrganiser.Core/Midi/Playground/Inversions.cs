using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi.Playground;

/// <summary>
/// Moves a chord part's chords into the inversions that flow best (Solomon, 2026-10-17: "snapping a part you drag in to
/// the smoothest inversion of the chords"). Each chord keeps its notes and its spacing; it's only turned over (its
/// bottom notes taken up an octave, or its top notes down) and moved by octaves, choosing for the whole run at once the
/// path where the voices move least (the same voice-leading distance <see cref="Voicer"/> writes with), kept near
/// <paramref name="centre"/>. A bass note well below the chord stays where it is, so a left-hand root under a right-hand
/// voicing keeps working.
/// </summary>
public static class Inversions
{
    const int Ppq = PhraseAnalyser.Ppq;

    sealed class Block
    {
        public required List<NoteEvent> Notes;   // the upper notes, low to high
        public NoteEvent? Bass;
        public long Start;
    }

    /// <summary>The part with its chords turned into their smoothest inversions; single notes are left alone.</summary>
    public static List<NoteEvent> Smooth(IReadOnlyList<NoteEvent> notes, double? centre = null)
    {
        var blocks = Blocks(notes);
        var chords = blocks.Where(b => b.Notes.Count >= 3).ToList();
        if (chords.Count < 2) return notes.ToList();
        var middle = centre ?? chords.Average(b => b.Notes.Average(n => n.Key));
        var cands = chords.Select(b => Candidates(b.Notes.Select(n => n.Key).ToArray(), middle)).ToList();
        double Range(int[] v) => 0.12 * Math.Abs(v.Average() - middle);
        var cost = cands[0].Select(Range).ToArray();
        var back = new List<int[]>();
        for (var k = 1; k < cands.Count; k++)
        {
            var next = new double[cands[k].Count];
            var from = new int[cands[k].Count];
            for (var j = 0; j < cands[k].Count; j++)
            {
                var best = double.MaxValue;
                for (var i = 0; i < cands[k - 1].Count; i++)
                {
                    var c = cost[i] + Voicer.Movement(cands[k - 1][i], cands[k][j], classical: false);
                    if (c < best) { best = c; from[j] = i; }
                }
                next[j] = best + Range(cands[k][j]);
            }
            back.Add(from);
            cost = next;
        }
        var pick = new int[cands.Count];
        pick[^1] = Array.IndexOf(cost, cost.Min());
        for (var k = cands.Count - 1; k > 0; k--) pick[k - 1] = back[k - 1][pick[k]];

        var moved = new Dictionary<NoteEvent, int>();
        for (var k = 0; k < chords.Count; k++)
        {
            var voicing = cands[k][pick[k]];
            var block = chords[k];
            // Never down into the bass note's register.
            if (block.Bass is { } b && voicing[0] <= b.Key) voicing = voicing.Select(x => x + 12).ToArray();
            for (var i = 0; i < block.Notes.Count; i++) moved[block.Notes[i]] = voicing[i];
        }
        return notes.Select(n => moved.TryGetValue(n, out var key) ? n with { Key = Math.Clamp(key, 0, 127) } : n)
                    .OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
    }

    /// <summary>
    /// Every chord turned over <paramref name="steps"/> times: up (its bottom note taken up an octave each time) or down
    /// (its top note down). A bass note well below the chord stays put; single notes and pairs are left alone.
    /// </summary>
    public static List<NoteEvent> Turn(IReadOnlyList<NoteEvent> notes, int steps)
    {
        if (steps == 0) return notes.ToList();
        var moved = new Dictionary<NoteEvent, int>();
        foreach (var block in Blocks(notes).Where(b => b.Notes.Count >= 3))
        {
            var keys = block.Notes.Select(n => n.Key).ToList();
            for (var i = 0; i < Math.Abs(steps); i++)
            {
                if (steps > 0) { keys.Add(keys[0] + 12); keys.RemoveAt(0); }
                else { keys.Insert(0, keys[^1] - 12); keys.RemoveAt(keys.Count - 1); }
            }
            for (var i = 0; i < block.Notes.Count; i++) moved[block.Notes[i]] = keys[i];
        }
        return notes.Select(n => moved.TryGetValue(n, out var key) ? n with { Key = Math.Clamp(key, 0, 127) } : n)
                    .OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
    }

    /// <summary>Notes struck together as chords, with a bass note split off when it sits well below the rest.</summary>
    static List<Block> Blocks(IReadOnlyList<NoteEvent> notes)
    {
        var sorted = notes.Where(n => !n.Muted).OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
        var list = new List<Block>();
        var i = 0;
        while (i < sorted.Count)
        {
            var start = sorted[i].Start;
            var group = new List<NoteEvent>();
            while (i < sorted.Count && sorted[i].Start - start <= Ppq / 8) group.Add(sorted[i++]);
            group = group.GroupBy(n => n.Key).Select(g => g.First()).OrderBy(n => n.Key).ToList();
            NoteEvent? bass = null;
            if (group.Count >= 4 && group[1].Key - group[0].Key >= 7)
            {
                bass = group[0];
                group.RemoveAt(0);
                // A left-hand octave goes with it.
                if (group.Count >= 4 && group[0].Key - bass.Value.Key == 12 && group[1].Key - group[0].Key >= 5) group.RemoveAt(0);
            }
            list.Add(new Block { Notes = group, Bass = bass, Start = start });
        }
        return list;
    }

    /// <summary>Every inversion of a voicing (bottom notes up an octave, or top notes down), at each octave near the centre.</summary>
    static List<int[]> Candidates(int[] keys, double centre)
    {
        var list = new List<int[]>();
        var n = keys.Length;
        var turns = new List<int[]> { keys };
        for (var k = 1; k < n; k++)
        {
            turns.Add(keys.Skip(k).Concat(keys.Take(k).Select(x => x + 12)).ToArray());
            turns.Add(keys.Skip(n - k).Select(x => x - 12).Concat(keys.Take(n - k)).ToArray());
        }
        foreach (var t in turns)
        {
            if (!Rising(t)) continue;
            for (var o = -2; o <= 2; o++)
            {
                var v = t.Select(x => x + 12 * o).ToArray();
                if (v[0] < 28 || v[^1] > 100 || Math.Abs(v.Average() - centre) > 10) continue;
                if (!list.Any(c => c.SequenceEqual(v))) list.Add(v);
            }
        }
        if (list.Count == 0) list.Add(keys);
        return list;
    }

    static bool Rising(int[] v)
    {
        for (var i = 1; i < v.Length; i++) if (v[i] <= v[i - 1]) return false;
        return true;
    }
}
