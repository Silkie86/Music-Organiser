namespace MusicOrganiser.Core.Sounds;

public sealed record KeyEstimate(int Tonic, bool Minor, double Confidence)
{
    public string Name => MusicTheory.NoteNames[Tonic] + (Minor ? " minor" : " major");
    public string Short => MusicTheory.NoteNames[Tonic] + (Minor ? "m" : "");
    public string Camelot => MusicTheory.Camelot(Tonic, Minor);
}

public sealed record ChordSpan(double Start, double End, string Name);

/// <summary>Notes, keys and chords: names, key finding from chroma, chord recognition.</summary>
public static class MusicTheory
{
    public static readonly string[] NoteNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public static string NoteName(int midi) => NoteNames[((midi % 12) + 12) % 12] + (midi / 12 - 1);

    public static double MidiToHz(double midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);
    public static double HzToMidi(double hz) => 69 + 12 * Math.Log2(hz / 440.0);

    /// <summary>Parses "C", "F#m", "Bb minor", "A min" into (tonic, minor). Null if not a key.</summary>
    public static (int Tonic, bool Minor)? ParseKey(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;
        var idx = Array.IndexOf(NoteNames, char.ToUpperInvariant(t[0]).ToString());
        if (idx < 0) return null;
        var rest = t[1..];
        if (rest.StartsWith('#') || rest.StartsWith('♯')) { idx++; rest = rest[1..]; }
        else if (rest.StartsWith('b') || rest.StartsWith('♭')) { idx--; rest = rest[1..]; }
        rest = rest.Trim().ToLowerInvariant();
        var minor = rest.StartsWith('m') && !rest.StartsWith("maj");
        return (((idx % 12) + 12) % 12, minor);
    }

    // Krumhansl–Kessler key profiles.
    static readonly double[] MajorProfile = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    static readonly double[] MinorProfile = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    /// <summary>Best-matching key for a 12-bin chroma vector, with a 0..1 confidence.</summary>
    public static KeyEstimate? EstimateKey(double[] chroma)
    {
        if (chroma.Sum() <= 1e-9) return null;
        var scores = new List<(int Tonic, bool Minor, double R)>();
        for (var t = 0; t < 12; t++)
        {
            scores.Add((t, false, Correlate(chroma, MajorProfile, t)));
            scores.Add((t, true, Correlate(chroma, MinorProfile, t)));
        }
        scores.Sort((a, b) => b.R.CompareTo(a.R));
        var best = scores[0];
        // Confidence: how strongly it matches, and how clearly it beats the runner-up
        // that isn't its relative major/minor (those share every note).
        var relative = best.Minor ? ((best.Tonic + 3) % 12, false) : ((best.Tonic + 9) % 12, true);
        var runner = scores.Skip(1).First(s => (s.Tonic, s.Minor) != relative);
        var conf = Math.Clamp(best.R, 0, 1) * Math.Clamp((best.R - runner.R) * 5, 0, 1);
        return new KeyEstimate(best.Tonic, best.Minor, Math.Round(conf, 3));
    }

    static double Correlate(double[] chroma, double[] profile, int tonic)
    {
        double mx = chroma.Average(), my = profile.Average(), sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < 12; i++)
        {
            var x = chroma[(i + tonic) % 12] - mx;
            var y = profile[i] - my;
            sxy += x * y;
            sxx += x * x;
            syy += y * y;
        }
        return sxx <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }

    /// <summary>Camelot wheel code (e.g. A minor = 8A, C major = 8B), handy for harmonic mixing.</summary>
    public static string Camelot(int tonic, bool minor)
    {
        // Position of each major key on the wheel, starting at C = 8.
        var major = (8 + 7 * tonic) % 12;
        if (major == 0) major = 12;
        if (!minor) return major + "B";
        var rel = (tonic + 3) % 12;
        var n = (8 + 7 * rel) % 12;
        if (n == 0) n = 12;
        return n + "A";
    }

    /// <summary>Keys that mix well with a key: same, relative, and neighbours on the Camelot wheel.</summary>
    public static IReadOnlyList<(int Tonic, bool Minor)> CompatibleKeys(int tonic, bool minor)
    {
        var relTonic = minor ? (tonic + 3) % 12 : (tonic + 9) % 12;
        return
        [
            (tonic, minor), (relTonic, !minor),
            ((tonic + 7) % 12, minor), ((tonic + 5) % 12, minor),
        ];
    }

    // ---- chords ------------------------------------------------------------------

    static readonly (string Suffix, int[] Intervals)[] ChordShapes =
    [
        ("", [0, 4, 7]), ("m", [0, 3, 7]), ("7", [0, 4, 7, 10]), ("maj7", [0, 4, 7, 11]), ("m7", [0, 3, 7, 10]),
        ("dim", [0, 3, 6]), ("sus2", [0, 2, 7]), ("sus4", [0, 5, 7]), ("aug", [0, 4, 8]), ("m9", [0, 3, 7, 10, 2]),
        ("maj9", [0, 4, 7, 11, 2]), ("add9", [0, 4, 7, 2]), ("6", [0, 4, 7, 9]), ("m6", [0, 3, 7, 9]),
        ("5", [0, 7]),
    ];

    static readonly (int Root, string Name, double[] Template)[] Templates = BuildTemplates();

    static (int, string, double[])[] BuildTemplates()
    {
        var list = new List<(int, string, double[])>();
        foreach (var (suffix, iv) in ChordShapes)
            for (var root = 0; root < 12; root++)
            {
                var t = new double[12];
                foreach (var i in iv) t[(root + i) % 12] = 1;
                t[root] = 1.3;   // the root tends to be loudest
                list.Add((root, NoteNames[root] + suffix, t));
            }
        return list.ToArray();
    }

    /// <summary>Names the chord that best explains a chroma vector (or a set of note pitch classes). Null if it's not chord-like.</summary>
    public static (string Name, double Score)? NameChord(double[] chroma, double minScore = 0.75)
    {
        var active = chroma.Count(c => c > 0.25 * chroma.Max());
        if (chroma.Max() <= 0 || active < 2) return null;
        var best = ("", -1.0);
        foreach (var (_, name, t) in Templates)
        {
            var s = Cosine(chroma, t);
            // Prefer simpler chords on near ties.
            s -= name.Length * 0.002;
            if (s > best.Item2) best = (name, s);
        }
        return best.Item2 >= minScore ? (best.Item1, Math.Round(best.Item2, 3)) : null;
    }

    public static (string Name, double Score)? NameChord(IEnumerable<int> midiNotes)
    {
        var chroma = new double[12];
        var notes = midiNotes.ToList();
        if (notes.Count == 0) return null;
        var lowest = notes.Min();
        foreach (var n in notes) chroma[n % 12] += 1;
        chroma[lowest % 12] += 0.5;   // the bass note hints at the root
        return NameChord(chroma, 0.7);
    }

    /// <summary>
    /// Chord sequence from per-frame chroma, smoothed with a Viterbi pass so chords
    /// don't flicker frame to frame. Frames are <paramref name="frameSeconds"/> long.
    /// </summary>
    public static IReadOnlyList<ChordSpan> ChordSequence(IReadOnlyList<double[]> frames, double frameSeconds, double minSeconds = 0.4)
    {
        if (frames.Count == 0) return [];
        var n = Templates.Length + 1;  // + "no chord"
        var T = frames.Count;
        var prev = new double[n];
        var back = new int[T, n];
        const double stay = 0.0, change = -0.9;   // log-domain transition costs
        for (var t = 0; t < T; t++)
        {
            var cur = new double[n];
            var obs = Observe(frames[t]);
            // Every change costs the same, so the best place to come from is either
            // this chord itself or the overall best chord of the previous frame.
            var argMax = t == 0 ? 0 : Array.IndexOf(prev, prev.Max());
            for (var j = 0; j < n; j++)
            {
                if (t == 0) { cur[j] = obs[j]; continue; }
                var bestPrev = j;
                var bestVal = prev[j] + stay;
                if (argMax != j && prev[argMax] + change > bestVal) { bestVal = prev[argMax] + change; bestPrev = argMax; }
                cur[j] = bestVal + obs[j];
                back[t, j] = bestPrev;
            }
            prev = cur;
        }
        var path = new int[T];
        path[T - 1] = Array.IndexOf(prev, prev.Max());
        for (var t = T - 1; t > 0; t--) path[t - 1] = back[t, path[t]];

        var spans = new List<ChordSpan>();
        var start = 0;
        for (var t = 1; t <= T; t++)
        {
            if (t < T && path[t] == path[start]) continue;
            if (path[start] < Templates.Length)
            {
                var span = new ChordSpan(Math.Round(start * frameSeconds, 2), Math.Round(t * frameSeconds, 2), Templates[path[start]].Name);
                if (span.End - span.Start >= minSeconds)
                {
                    if (spans.Count > 0 && spans[^1].Name == span.Name) spans[^1] = spans[^1] with { End = span.End };
                    else spans.Add(span);
                }
            }
            start = t;
        }
        return spans;
    }

    static double[] Observe(double[] chroma)
    {
        var n = Templates.Length;
        var o = new double[n + 1];
        var energy = chroma.Sum();
        for (var i = 0; i < n; i++) o[i] = energy < 1e-6 ? -3 : 4 * (Cosine(chroma, Templates[i].Template) - 1);
        o[n] = energy < 1e-6 ? 0 : -1.6;  // "no chord" wins in silence and noise
        return o;
    }

    public static double Cosine(double[] a, double[] b)
    {
        double ab = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++) { ab += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        return aa <= 0 || bb <= 0 ? 0 : ab / Math.Sqrt(aa * bb);
    }
}
