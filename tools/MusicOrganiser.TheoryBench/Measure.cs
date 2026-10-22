using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Parsers;

/// <summary>Helpers shared by the POP909 bench (Program.cs) and the MIDI library bench (Library.cs).</summary>
static class Measure
{
    /// <summary>The notes starting in a stretch, moved to start at 0 and cut at its end.</summary>
    public static List<NoteEvent> Cut(IReadOnlyList<NoteEvent> notes, long from, long len) =>
        notes.Where(n => n.Start >= from && n.Start < from + len)
             .Select(n => n with { Start = n.Start - from, Length = Math.Max(1, Math.Min(n.Length, from + len - n.Start)) })
             .OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();

    /// <summary>How often a right one scores above a wrong one (ties count half): 0.5 is chance, 1 is always.</summary>
    public static double Auc(List<double> pos, List<double> neg)
    {
        double wins = 0;
        var sorted = neg.OrderBy(x => x).ToArray();
        foreach (var p in pos)
        {
            var below = LowerBound(sorted, p - 1e-12);
            var notAbove = LowerBound(sorted, p + 1e-12);
            wins += below + 0.5 * (notAbove - below);
        }
        return wins / ((double)pos.Count * neg.Count);

        static int LowerBound(double[] a, double x)
        {
            int lo = 0, hi = a.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (a[mid] < x) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
    }

    public static float[] Chroma(IReadOnlyList<NoteEvent> ns)
    {
        var ch = new float[12];
        foreach (var n in ns) ch[n.Key % 12] += (float)Math.Clamp(n.Length / 384.0, 1 / 16.0, 2);
        var t = ch.Sum();
        return t <= 0 ? ch : ch.Select(x => x / t).ToArray();
    }

    /// <summary>The MIDI page's list % for a melody fitted to an accompaniment.</summary>
    public static double PageFull(List<NoteEvent> mel, List<NoteEvent> acc) => PageParts(mel, acc).Score;

    /// <summary>The MIDI page's list %, its "plays together" part, and its first look (the best of the 12 keys, ignoring time).</summary>
    public static (double Score, double Together, double Move) PageParts(List<NoteEvent> mel, List<NoteEvent> acc)
    {
        var target = HarmonyTarget.FromPart("t", MidiRoles.Chords, acc, Chroma(acc), PhraseAnalyser.Progression(acc), null);
        var first = HarmonyMatcher.Fit(Chroma(mel), PhraseAnalyser.Progression(mel), target).Score;
        var f = HarmonyMatcher.Refine(Chroma(mel), PhraseAnalyser.Progression(mel), mel, MidiRoles.Melody, target);
        return (f.Score, f.Together ?? f.Score, first);
    }
}
