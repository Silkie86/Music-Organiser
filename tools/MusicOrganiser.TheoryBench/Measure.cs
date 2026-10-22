using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Parsers;

/// <summary>Helpers shared by the POP909 bench (Program.cs) and the MIDI library bench (Library.cs).</summary>
static class Measure
{
    /// <summary>The Aarden-Essen key profiles (major, minor), C first.</summary>
    public static readonly double[] AEmaj = [17.7661, 0.145624, 14.9265, 0.160186, 19.8049, 11.3587, 0.291248, 22.062, 0.145624, 8.15494, 0.232998, 4.95122],
                                    AEmin = [18.2648, 0.737619, 14.0499, 16.8599, 0.702494, 14.4362, 0.702494, 18.6161, 4.56621, 1.93186, 7.37619, 1.75623];

    static int Mod12(int n) => ((n % 12) + 12) % 12;

    /// <summary>A guessed key against the right one, MIREX style: exact 1, a fifth off 0.5, relative 0.3, parallel 0.2.</summary>
    public static double Mirex(int t, bool m, int rt, bool rm)
    {
        if (t == rt && m == rm) return 1;
        if (m == rm && (Mod12(t - rt) == 7 || Mod12(t - rt) == 5)) return 0.5;
        if (m != rm && (rm ? Mod12(rt + 3) == t : Mod12(rt + 9) == t)) return 0.3;   // relative
        if (t == rt && m != rm) return 0.2;                                             // parallel
        return 0;
    }

    /// <summary>How closely a chroma, read from <paramref name="tonic"/>, follows a key profile (Pearson).</summary>
    public static double Corr(double[] x, double[] prof, int tonic)
    {
        double mx = x.Average(), my = prof.Average(), sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < 12; i++) { var a = x[(i + tonic) % 12] - mx; var b = prof[i] - my; sxy += a * b; sxx += a * a; syy += b * b; }
        return sxx <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }

    /// <summary>The key whose profile the chroma follows best.</summary>
    public static (int T, bool M) Best(double[] ch, double[] maj, double[] min)
    {
        var best = (0, false); var r = double.MinValue;
        for (var t = 0; t < 12; t++)
        {
            var a = Corr(ch, maj, t); if (a > r) { r = a; best = (t, false); }
            var b = Corr(ch, min, t); if (b > r) { r = b; best = (t, true); }
        }
        return best;
    }

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
