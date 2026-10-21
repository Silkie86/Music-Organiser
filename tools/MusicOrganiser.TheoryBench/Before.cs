using System.Numerics;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

/// <summary>
/// The app's two chord readers as they were before the one chord reader (2026-10-20), kept as a fixed point to compare
/// against: the MIDI page's (a half bar at a time, named from 15 templates) and the Playground's (a half bar at a time,
/// spelled by ChordSpeller). Copies, so they stay as they were whatever the app does now.
/// </summary>
static class Before
{
    const int Ppq = PhraseAnalyser.Ppq, Beat = Ppq, Bar = 4 * Ppq;

    /// <summary>The MIDI page's chords (PhraseAnalyser.Progression before), a half bar at a time.</summary>
    public static IReadOnlyList<PhraseChord> Progression(IReadOnlyList<NoteEvent> notes, bool twoNoteChords = false, int window = 2 * Ppq)
    {
        if (notes.Count == 0) return [];
        var sorted = notes.OrderBy(n => n.Start).ToList();
        var end = sorted.Max(n => n.Start + n.Length);
        var chords = new List<PhraseChord>();
        var longest = sorted.Max(n => n.Length);
        var first = 0;
        var weights = new double[12];
        for (long w = 0; w < end; w += window)
        {
            Array.Clear(weights);
            var lowest = int.MaxValue;
            while (first < sorted.Count && sorted[first].Start + longest <= w) first++;
            for (var i = first; i < sorted.Count && sorted[i].Start < w + window; i++)
            {
                var n = sorted[i];
                var overlap = Math.Min(n.Start + n.Length, w + window) - Math.Max(n.Start, w);
                if (overlap < window / 16) continue;
                weights[n.Key % 12] += overlap;
                lowest = Math.Min(lowest, n.Key);
            }
            var max = weights.Max();
            if (max <= 0) continue;
            var mask = 0;
            for (var p = 0; p < 12; p++) if (weights[p] >= 0.25 * max) mask |= 1 << p;
            var count = BitOperations.PopCount((uint)mask);
            if (count < (twoNoteChords ? 2 : 3)) continue;
            var chord = new PhraseChord(RootOf(weights, lowest % 12), mask);
            if (chords.Count == 0 || chords[^1].Mask != chord.Mask) chords.Add(chord);
        }
        return chords;
    }

    static int RootOf(double[] weights, int bass)
    {
        var chroma = (double[])weights.Clone();
        chroma[bass] += chroma.Max() * 0.5;
        if (MusicTheory.NameChord(chroma, 0.6) is { } named)
        {
            var name = named.Name;
            var root = Array.IndexOf(MusicTheory.NoteNames, name.Length > 1 && name[1] == '#' ? name[..2] : name[..1]);
            if (root >= 0) return root;
        }
        return bass;
    }

    /// <summary>The MIDI page's chords as a lane: Progression a half bar at a time, with times kept.</summary>
    public static List<TimedChord> PageLane(IReadOnlyList<NoteEvent> notes, long length)
    {
        var lane = new List<TimedChord>();
        var sorted = notes.OrderBy(n => n.Start).ToList();
        for (long w = 0; w < length; w += 2 * Beat)
        {
            var win = sorted.Where(n => n.Start < w + 2 * Beat && n.Start + n.Length > w)
                            .Select(n => n with { Start = Math.Max(n.Start, w) - w, Length = Math.Min(n.Start + n.Length, w + 2 * Beat) - Math.Max(n.Start, w) }).ToList();
            var prog = win.Count == 0 ? [] : Progression(win);
            if (prog.Count == 0) continue;
            var c = prog[0];
            var low = win.Where(n => n.Length >= 24).Select(n => n.Key).DefaultIfEmpty(-1).Min();
            var bass = low < 0 ? c.Root : low % 12;
            var chord = new ChordSymbol(c.Root, c.Mask | 1 << bass, bass, ChordSpeller.FamilyOf(ChordSymbol.Rotate(c.Mask, -c.Root)));
            if (lane.Count > 0 && lane[^1].End == w && lane[^1].Chord == chord) lane[^1] = lane[^1] with { Length = lane[^1].Length + 2 * Beat };
            else lane.Add(new TimedChord(w, Math.Min(2 * Beat, length - w), chord));
        }
        return lane;
    }

    /// <summary>The Playground's chord lane (ChordLane.Detect before): a half bar at a time, the lowest note as the bass.</summary>
    public static List<TimedChord> Detect(IReadOnlyList<NoteEvent> notes, long length, int slot = 2 * Ppq)
    {
        var lane = new List<TimedChord>();
        if (notes.Count == 0 || length <= 0) return lane;
        var sorted = notes.Where(n => !n.Muted).OrderBy(n => n.Start).ToList();
        var weights = new float[12];
        for (long at = 0; at < length; at += slot)
        {
            Array.Clear(weights);
            var lowest = int.MaxValue;
            long end = Math.Min(length, at + slot), lowestHeld = 0;
            foreach (var n in sorted)
            {
                if (n.Start >= end) break;
                var overlap = Math.Min(n.Start + n.Length, end) - Math.Max(n.Start, at);
                if (overlap < slot / 8) continue;
                weights[n.Key % 12] += overlap;
                if (overlap >= slot / 4 && (n.Key < lowest || (n.Key == lowest && overlap > lowestHeld)))
                {
                    lowest = n.Key;
                    lowestHeld = overlap;
                }
            }
            var max = weights.Max();
            ChordSymbol? chord = null;
            if (max > 0)
            {
                var mask = 0;
                for (var p = 0; p < 12; p++)
                {
                    weights[p] /= max;
                    if (weights[p] >= 0.2f) mask |= 1 << p;
                }
                chord = ChordSpeller.Spell(mask, lowest == int.MaxValue ? -1 : lowest % 12, weights);
                if (chord is null && lane.Count > 0 && Fits(lane[^1].Chord, mask)) chord = lane[^1].Chord;
            }
            if (chord is not { } c) continue;
            if (lane.Count > 0 && lane[^1].End == at && lane[^1].Chord.Name == c.Name)
                lane[^1] = lane[^1] with { Length = lane[^1].Length + (end - at) };
            else lane.Add(new TimedChord(at, end - at, c));
        }
        return lane;
    }

    static bool Fits(ChordSymbol chord, int mask)
    {
        for (var p = 0; p < 12; p++)
            if ((mask >> p & 1) != 0 && ChordScales.Sit(chord, p) < 0.3) return false;
        return true;
    }
}
