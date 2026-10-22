using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

/// <summary>
/// Key finders side by side (--keylab): the app's two, three other published key profiles, and ways of hearing the
/// tonic from the bass, scored against labelled keys. Built to choose fix 5 on both pop and gospel/jazz music.
/// </summary>
static class KeyLab
{
    const int Beat = 96, Bar = 4 * Beat;

    public sealed record Item(string Group, string Pack, List<NoteEvent> Notes, (int Tonic, bool Minor) Label);

    // Major and minor profiles, tonic first.
    static readonly double[] KkMaj = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88],
                             KkMin = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17],
                             AeMaj = [17.7661, 0.145624, 14.9265, 0.160186, 19.8049, 11.3587, 0.291248, 22.062, 0.145624, 8.15494, 0.232998, 4.95122],
                             AeMin = [18.2648, 0.737619, 14.0499, 16.8599, 0.702494, 14.4362, 0.702494, 18.6161, 4.56621, 1.93186, 7.37619, 1.75623],
                             TkpMaj = [5.0, 2.0, 3.5, 2.0, 4.5, 4.0, 2.0, 4.5, 2.0, 3.5, 1.5, 4.0],
                             TkpMin = [5.0, 2.0, 3.5, 4.5, 2.0, 4.0, 2.0, 4.5, 3.5, 2.0, 1.5, 4.0],
                             BbMaj = [16.80, 0.86, 12.95, 1.41, 13.49, 11.93, 1.25, 20.28, 1.80, 8.04, 0.62, 10.57],
                             BbMin = [18.16, 0.69, 12.99, 13.34, 1.07, 11.15, 1.38, 21.07, 7.49, 1.53, 0.92, 10.21];

    static int Mod(int n) => ((n % 12) + 12) % 12;

    static double Corr(double[] x, double[] prof, int tonic)
    {
        double mx = x.Average(), my = prof.Average(), sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < 12; i++)
        {
            var a = x[(i + tonic) % 12] - mx;
            var b = prof[i] - my;
            sxy += a * b; sxx += a * a; syy += b * b;
        }
        return sxx <= 0 || syy <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }

    static double[] Chroma(IEnumerable<NoteEvent> notes)
    {
        var c = new double[12];
        foreach (var n in notes) c[n.Key % 12] += Math.Clamp(n.Length / (double)Bar, 1 / 16.0, 2);
        return c;
    }

    /// <summary>The lowest note sounding in each eighth, as a chroma, and the lowest at each bar's downbeat.</summary>
    static (double[] Bass, double[] Down) Bass(List<NoteEvent> notes)
    {
        double[] bass = new double[12], down = new double[12];
        if (notes.Count == 0) return (bass, down);
        var end = notes.Max(n => n.Start + n.Length);
        var sorted = notes.OrderBy(n => n.Start).ToList();
        var from = 0;
        for (long t = 0; t < end; t += Beat / 2)
        {
            while (from < sorted.Count && sorted[from].Start + 4 * Bar < t) from++;
            var low = int.MaxValue;
            for (var i = from; i < sorted.Count && sorted[i].Start <= t; i++)
                if (sorted[i].Start + sorted[i].Length > t) low = Math.Min(low, sorted[i].Key);
            if (low == int.MaxValue) continue;
            bass[low % 12]++;
            if (t % Bar == 0) down[low % 12]++;
        }
        return (bass, down);
    }

    static (int, bool) Best(double[] chroma, double[] maj, double[] min, Func<int, bool, double>? bonus = null)
    {
        var best = (0, false);
        var top = double.MinValue;
        for (var t = 0; t < 12; t++)
            foreach (var m in new[] { false, true })
            {
                var r = Corr(chroma, m ? min : maj, t) + (bonus?.Invoke(t, m) ?? 0);
                if (r > top) { top = r; best = (t, m); }
            }
        return best;
    }

    static double[] Norm(double[] x)
    {
        var s = x.Sum();
        return s <= 0 ? x : x.Select(v => v / s).ToArray();
    }

    static double Mirex((int T, bool M)? k, (int T, bool M) r) => k is { } f ? Measure.Mirex(f.T, f.M, r.T, r.M) : 0;

    static (int Root, bool Minor)? FirstOf(IReadOnlyList<PhraseChord> chords) =>
        chords.Count > 0 ? (chords[0].Root, MusicTheory.IsMinorChord(chords[0].Root, chords[0].Mask)) : null;

    /// <summary>Profiles learned from the other packs' labelled files (never the file's own pack): all packs' sums minus its own.</summary>
    static Func<string, (double[] Maj, double[] Min)> Learner(List<Item> items)
    {
        double[] Sum(IEnumerable<Item> its, bool minor)
        {
            var p = new double[12];
            foreach (var it in its.Where(i => i.Label.Minor == minor))
            {
                var c = Norm(Chroma(it.Notes));
                for (var i = 0; i < 12; i++) p[i] += c[(i + it.Label.Tonic) % 12];
            }
            return p;
        }
        var all = (Maj: Sum(items, false), Min: Sum(items, true));
        var byPack = items.GroupBy(i => i.Pack).ToDictionary(g => g.Key, g => (Maj: Sum(g, false), Min: Sum(g, true)));
        return pack => byPack.TryGetValue(pack, out var own)
            ? (all.Maj.Zip(own.Maj, (a, b) => a - b).ToArray(), all.Min.Zip(own.Min, (a, b) => a - b).ToArray())
            : all;
    }

    /// <summary>Profiles learned elsewhere (--profiles a.txt,b.txt: files a run wrote with --save-profile), tried as they are.</summary>
    public static readonly List<(string Name, double[] Maj, double[] Min)> Extra = [];
    public static string? SaveTo;

    public static void Load(string files)
    {
        foreach (var f in files.Split(','))
        {
            var lines = File.ReadAllLines(f);
            double[] Row(int i) => lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(double.Parse).ToArray();
            Extra.Add((Path.GetFileNameWithoutExtension(f), Row(0), Row(1)));
        }
        if (Extra.Count > 1)
            Extra.Add(("both averaged", Enumerable.Range(0, 12).Select(i => Extra.Average(e => Norm(e.Maj)[i])).ToArray(),
                                        Enumerable.Range(0, 12).Select(i => Extra.Average(e => Norm(e.Min)[i])).ToArray()));
    }

    public static void Run(string title, List<Item> items, List<Item>? trainOn = null)
    {
        Console.WriteLine();
        Console.WriteLine($"=== KEY LAB: {title}, {items.Count} labelled ({string.Join(", ", items.GroupBy(i => i.Group).Select(g => $"{g.Key} {g.Count()}"))}) ===");
        var learned = Learner(trainOn ?? items);
        if (SaveTo is not null)
        {
            var (maj, min) = learned("\0");
            File.WriteAllLines(SaveTo, [string.Join(" ", Norm(maj).Select(v => v.ToString("0.0000"))), string.Join(" ", Norm(min).Select(v => v.ToString("0.0000")))]);
        }
        var finders = new List<(string Name, Func<Item, (int, bool)?> Find)>
        {
            ("Krumhansl (Projects page now)", it => Best(Chroma(it.Notes), KkMaj, KkMin)),
            // A finder that gives no key scores nothing for that file (not a free C major).
            ("MIDI page (PhraseAnalyser.Analyse)", it => PhraseAnalyser.Analyse(it.Notes).Key is { } k ? (k.Tonic, k.Minor) : null),
            ("Aarden-Essen", it => Best(Chroma(it.Notes), AeMaj, AeMin)),
            ("Temperley", it => Best(Chroma(it.Notes), TkpMaj, TkpMin)),
            ("Bellman-Budge", it => Best(Chroma(it.Notes), BbMaj, BbMin)),
        };
        foreach (var w in new[] { 0.5, 1.0, 2.0 })
            finders.Add(($"Temperley, bass counted x{w} more", it =>
            {
                var c = Norm(Chroma(it.Notes));
                var b = Norm(Bass(it.Notes).Bass);
                return Best(c.Zip(b, (x, y) => x + w * y).ToArray(), TkpMaj, TkpMin);
            }));
        foreach (var lam in new[] { 0.2, 0.4 })
        {
            finders.Add(($"Temperley + downbeat bass on the tonic x{lam}", it =>
            {
                var c = Chroma(it.Notes);
                var d = Norm(Bass(it.Notes).Down);
                return Best(c, TkpMaj, TkpMin, (t, m) => lam * d[t]);
            }));
            finders.Add(($"Aarden-Essen + downbeat bass on the tonic x{lam}", it =>
            {
                var c = Chroma(it.Notes);
                var d = Norm(Bass(it.Notes).Down);
                return Best(c, AeMaj, AeMin, (t, m) => lam * d[t]);
            }));
        }
        finders.Add(("learned from the other packs (leave pack out)", it =>
        {
            var (maj, min) = learned(it.Pack);
            return Best(Chroma(it.Notes), maj, min);
        }));
        foreach (var e in Extra)
            finders.Add(($"learned from {e.Name}", it => Best(Chroma(it.Notes), e.Maj, e.Min)));
        if (Extra.FirstOrDefault(e => e.Name == "pop") is { Maj: not null } pop)
        {
            finders.Add(("pop averaged with the other packs", it =>
            {
                var (maj, min) = learned(it.Pack);
                return Best(Chroma(it.Notes), Norm(maj).Zip(Norm(pop.Maj), (a, b) => a + b).ToArray(), Norm(min).Zip(Norm(pop.Min), (a, b) => a + b).ToArray());
            }));
            finders.Add(("App's MIDI key finder now (EstimateMidiKey)", it =>
            {
                var chords = PhraseAnalyser.Progression(it.Notes);
                return MusicTheory.EstimateMidiKey(Chroma(it.Notes), FirstOf(chords)) is { } k ? (k.Tonic, k.Minor) : null;
            }));
            finders.Add(("CHOSEN (honest): pop + other packs, first chord when close (0.05)", it =>
            {
                var (maj0, min0) = learned(it.Pack);
                double[] maj = Norm(maj0).Zip(Norm(pop.Maj), (a, b) => a + b).ToArray(), min = Norm(min0).Zip(Norm(pop.Min), (a, b) => a + b).ToArray();
                var c = Chroma(it.Notes);
                var k = Best(c, maj, min);
                var rel = k.Item2 ? (k.Item1 + 3) % 12 : (k.Item1 + 9) % 12;
                var chords = PhraseAnalyser.Progression(it.Notes);
                if (chords.Count == 0 || chords[0].Root != rel) return k;
                return Corr(c, k.Item2 ? min : maj, k.Item1) - Corr(c, k.Item2 ? maj : min, rel) <= 0.05 ? (rel, !k.Item2) : k;
            }));
            finders.Add(("learned from pop + first chord rule", it =>
            {
                var k = Best(Chroma(it.Notes), pop.Maj, pop.Min);
                var chords = PhraseAnalyser.Progression(it.Notes);
                var rel = k.Item2 ? (k.Item1 + 3) % 12 : (k.Item1 + 9) % 12;
                return chords.Count > 0 && chords[0].Root == rel ? (rel, !k.Item2) : k;
            }));
        }
        foreach (var pp in Extra.Where(e => e.Name is "pop" or "both averaged"))
            foreach (var margin in new[] { 0.02, 0.05, 0.1, 0.15 })
                finders.Add(($"{pp.Name} + first chord rule when close ({margin})", it =>
                {
                    var c = Chroma(it.Notes);
                    var k = Best(c, pp.Maj, pp.Min);
                    var rel = k.Item2 ? (k.Item1 + 3) % 12 : (k.Item1 + 9) % 12;
                    var chords = PhraseAnalyser.Progression(it.Notes);
                    if (chords.Count == 0 || chords[0].Root != rel) return k;
                    var top = Corr(c, k.Item2 ? pp.Min : pp.Maj, k.Item1);
                    var other = Corr(c, k.Item2 ? pp.Maj : pp.Min, rel);
                    return top - other <= margin ? (rel, !k.Item2) : k;
                }));
        finders.Add(("learned + downbeat bass on the tonic x0.2", it =>
        {
            var (maj, min) = learned(it.Pack);
            var d = Norm(Bass(it.Notes).Down);
            return Best(Chroma(it.Notes), maj, min, (t, m) => 0.2 * d[t]);
        }));

        var groups = items.Select(i => i.Group).Distinct().Order().ToList();
        Console.WriteLine($"{"finder",-50} " + string.Join(" ", groups.Select(g => $"{g,14}")) + $" {"ALL",14}   (exact % / MIREX score)");
        foreach (var (name, find) in finders)
        {
            var found = items.Select(i => (i, k: find(i))).ToList();
            string Cell(IEnumerable<(Item i, (int, bool)? k)> rows)
            {
                var r = rows.ToList();
                if (r.Count == 0) return "-";
                return $"{100.0 * r.Count(x => x.k == x.i.Label) / r.Count,5:0.0} / {100 * r.Average(x => Mirex(x.k, x.i.Label)),4:0}";
            }
            Console.WriteLine($"{name,-50} " + string.Join(" ", groups.Select(g => $"{Cell(found.Where(f => f.i.Group == g)),14}")) + $" {Cell(found),14}");
        }

        // How sure each finder says it is, against how often it's right: what a "show the key from this confidence" cut keeps.
        Console.WriteLine();
        Console.WriteLine("confidence cut: share of keys shown / exact among those shown");
        foreach (var (name, est) in new (string, Func<Item, KeyEstimate?>)[]
        {
            ("Krumhansl (EstimateKey)", it => MusicTheory.EstimateKey(Chroma(it.Notes))),
            ("MIDI finder (EstimateMidiKey)", it =>
            {
                var chords = PhraseAnalyser.Progression(it.Notes);
                return MusicTheory.EstimateMidiKey(Chroma(it.Notes), FirstOf(chords));
            }),
        })
            foreach (var g in groups)
            {
                var rows = items.Where(i => i.Group == g).Select(i => (i, k: est(i))).ToList();
                Console.WriteLine($"  {name,-32} {g,-16} " + string.Join("  ", new[] { 0, 0.02, 0.05, 0.08, 0.12, 0.2 }.Select(cut =>
                {
                    var shown = rows.Where(r => r.k is { } k && k.Confidence >= cut).ToList();
                    return $">={cut:0.00}: {100.0 * shown.Count / rows.Count,3:0}% / {(shown.Count == 0 ? 0 : 100.0 * shown.Count(r => (r.k!.Tonic, r.k.Minor) == r.i.Label) / shown.Count),3:0}%";
                })));
            }
    }
}
