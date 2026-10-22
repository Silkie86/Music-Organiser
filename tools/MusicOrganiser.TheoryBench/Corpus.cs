using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Sounds;

/// <summary>
/// --corpus: the app's harmony ear checked against chord charts other people labelled (Solomon, 2026-10-22: "find jazz and
/// gospel chord libraries that are already labeled correctly so you can improve the engine"). The charts come from ChoCo
/// (Real Book, iReal Pro, Weimar Jazz Database, Jazz Corpus; CC BY 4.0) and the Jazz Harmony Treebank (CC BY-NC-SA 4.0:
/// used here to measure, never shipped), turned into one TSV by a script (see tools/README.md). Three checks:
/// "what chord next?" (each tune guessed from tunes it isn't in), the key the app hears in a chart, and which chord each
/// chord leads to, against the Treebank's experts.
/// </summary>
static class CorpusBench
{
    const int Ppq = 96;

    /// <param name="Beats">How long each chord lasts, in beats.</param>
    /// <param name="Targets">Treebank tunes with a tree: for each chord, the chord it leads to (prepares or prolongs), -1 for the last.</param>
    public sealed record Tune(string Source, string Id, string Title, string Genre, int Tonic, bool Minor, bool HasKey,
                              List<ChordSymbol> Chords, List<double> Beats, int[]? Targets)
    {
        public bool IsGospel => Source == "ireal-forum-gospel";
        public bool IsJazz => !IsGospel;
    }

    public static List<Tune> Load(string path)
    {
        var tunes = new List<Tune>();
        foreach (var line in File.ReadLines(path))
        {
            var f = line.TrimEnd('\r').Split('\t');
            if (f.Length < 6) continue;
            var (tonic, minor, hasKey) = (0, false, false);
            if (f[4].Split(':') is [var t, var mode] && int.TryParse(t, out var pc)) (tonic, minor, hasKey) = (pc, mode == "minor", true);
            var chords = new List<ChordSymbol>();
            var beats = new List<double>();
            foreach (var item in f[5].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var bar = item.IndexOf('|');
                if (PhraseChord.Decode(item[(bar + 1)..]) is not [var c]) continue;
                chords.Add(c.Symbol);
                beats.Add(double.Parse(item[..bar], System.Globalization.CultureInfo.InvariantCulture));
            }
            int[]? targets = f.Length > 6 && f[6].Trim().Length > 0 ? f[6].Trim().Split(' ').Select(int.Parse).ToArray() : null;
            tunes.Add(new Tune(f[0], f[1], f[2], f[3], tonic, minor, hasKey, chords, beats, targets));
        }
        return tunes;
    }

    public static void Run(string path)
    {
        var tunes = Load(path);
        Console.WriteLine($"tunes: {tunes.Count} ({string.Join(", ", tunes.GroupBy(t => t.Source).Select(g => $"{g.Key} {g.Count()}"))})");
        if (Only is null or "moves") Moves(tunes);
        if (Only is null or "keys") Keys(tunes);
        if (Only is null or "keys") KeyLab(tunes);
        if (Only is null or "leads") Leads(tunes);
    }

    /// <summary>--only moves|keys|leads: one check.</summary>
    public static string? Only;

    // ---------------------------------------------------------------- what chord next?

    static string Kind(ChordSymbol c) => ProgressionModel.Kind(c);
    static string Step(ChordSymbol from, ChordSymbol to) => $"{ChordSymbol.Mod(to.Root - from.Root)}:{Kind(to)}";

    /// <summary>The chords as "what chord next?" learns them: a change only when the root or the kind of chord changes.</summary>
    static List<ChordSymbol> Changes(IReadOnlyList<ChordSymbol> chords)
    {
        var list = new List<ChordSymbol>();
        foreach (var c in chords)
            if (list.Count == 0 || list[^1].Root != c.Root || Kind(list[^1]) != Kind(c)) list.Add(c);
        return list;
    }

    static string Norm(string title) => new(title.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    static void Moves(List<Tune> tunes)
    {
        Console.WriteLine();
        Console.WriteLine("=== WHAT CHORD NEXT: the chord the chart goes to, among the app's first 1 / 3 / 6 guesses ===");
        var treebank = tunes.Where(t => t.Source == "treebank").ToList();
        var testTitles = treebank.Select(t => Norm(t.Title)).ToHashSet();
        var jazzTrain = tunes.Where(t => t.IsJazz && t.Source != "treebank" && !testTitles.Contains(Norm(t.Title))).ToList();
        var gospel = tunes.Where(t => t.IsGospel).ToList();
        Console.WriteLine($"    jazz: learned from {jazzTrain.Count} charts (ChoCo, the Treebank's tunes left out), guessed on the Treebank's {treebank.Count}");
        Console.WriteLine($"    gospel, worship, soul: {gospel.Count} iReal Pro forum charts, guessed in 5 folds");

        Score("jazz, learned from ChoCo jazz", jazzTrain, treebank);
        var folds = Enumerable.Range(0, 5).Select(k => gospel.Where((_, i) => i % 5 == k).ToList()).ToList();
        Score("gospel, learned from the other gospel folds", null, gospel, test => gospel.Except(test).ToList(), folds);
        Score("gospel, learned from ChoCo jazz only", jazzTrain, gospel);
        Score("gospel, learned from the other folds + ChoCo jazz", null, gospel, test => gospel.Except(test).Concat(jazzTrain).ToList(), folds);
        // A small library: what a starting set of counts does for someone with only a few chord parts.
        var rng = new Random(7);
        var few = folds.Select(test => gospel.Except(test).OrderBy(_ => rng.Next()).Take(40).ToList()).ToList();
        var i2 = 0;
        Score("gospel, learned from 40 other gospel charts", null, gospel, _ => few[i2++ % 5], folds);
        i2 = 0;
        Score("gospel, learned from 40 + ChoCo jazz", null, gospel, _ => few[i2++ % 5].Concat(jazzTrain).ToList(), folds);
        // ChoCo's jazz as a starting point (ProgressionModel.Prior) the library's own counts outweigh as they grow.
        var prior = MovesOf(jazzTrain);
        foreach (var strength in new[] { 20.0, 200, 1000, 3000 })
        {
            i2 = 0;
            Score($"gospel, 40 charts, ChoCo jazz as a prior ({strength})", null, gospel, _ => few[i2++ % 5], folds, prior, strength);
            Score($"gospel, the other folds, ChoCo jazz as a prior ({strength})", null, gospel, test => gospel.Except(test).ToList(), folds, prior, strength);
        }
        Score("jazz, nothing of its own, ChoCo jazz as a prior", [], treebank, null, null, prior);
    }

    static ProgressionModel MovesOf(IEnumerable<Tune> tunes) => ProgressionModel.Learn(tunes.Select(t => (IReadOnlyList<ChordSymbol>)Changes(t.Chords)));

    /// <summary>--save-chord-moves: learns "what chord next?" from every ChoCo chart (never the Treebank) and writes its counts (ChordMoves.tsv).</summary>
    public static void SaveChordMoves(string corpus, string to)
    {
        var tunes = Load(corpus).Where(t => t.Source != "treebank").ToList();
        File.WriteAllText(to, MovesOf(tunes).Save(least: 2));
        Console.WriteLine($"learned from {tunes.Count} charts, wrote {to}");
    }

    static void Score(string name, List<Tune>? train, List<Tune> test, Func<List<Tune>, List<Tune>>? trainFor = null, List<List<Tune>>? folds = null,
                      ProgressionModel? prior = null, double strength = ProgressionModel.DefaultPriorStrength)
    {
        folds ??= [test];
        int n = 0, h1 = 0, h3 = 0, h6 = 0;
        foreach (var fold in folds)
        {
            var learnFrom = train ?? trainFor!(fold);
            var model = MovesOf(learnFrom).With(prior, strength);
            foreach (var t in fold)
            {
                var c = Changes(t.Chords);
                for (var i = 1; i < c.Count; i++)
                {
                    var guesses = model.Next(c[i - 1], i >= 2 ? c[i - 2] : null).Select(g => Step(c[i - 1], g.Chord)).ToList();
                    var at = guesses.IndexOf(Step(c[i - 1], c[i]));
                    n++;
                    if (at == 0) h1++;
                    if (at is >= 0 and < 3) h3++;
                    if (at is >= 0 and < 6) h6++;
                }
            }
        }
        Console.WriteLine($"{name,-55} {Pct(h1, n)} / {Pct(h3, n)} / {Pct(h6, n)}   (n {n})");
    }

    static string Pct(int a, int n) => $"{100.0 * a / Math.Max(1, n),3:0}";

    // ---------------------------------------------------------------- keys

    /// <summary>The chart on the Playground's chord track (no MIDI), timed as written: the key the app hears in it.</summary>
    static KeyEstimate? HeardKey(Tune t)
    {
        var section = new PlaygroundSection();
        long at = 0;
        for (var i = 0; i < t.Chords.Count; i++)
        {
            var length = (long)Math.Max(1, Math.Round(t.Beats[i] * Ppq));
            section.ChordTrack.Add(new PlaygroundChord { Start = at, Length = length, Chord = t.Chords[i] });
            at += length;
        }
        section.Bars = (int)Math.Max(1, (at + 4 * Ppq - 1) / (4 * Ppq));
        return SectionHarmony.Of(section).Key;
    }

    static void Keys(List<Tune> tunes)
    {
        Console.WriteLine();
        Console.WriteLine("=== KEYS: the key the app hears in a chart (on the chord track, no MIDI) against the one written ===");
        Console.WriteLine("    right / same home note but major-minor wrong / relative major or minor / a fifth away / other / no key heard");
        foreach (var group in tunes.Where(t => t.HasKey).GroupBy(t => t.Source))
        {
            int n = 0, right = 0, mode = 0, relative = 0, fifth = 0, other = 0, none = 0;
            foreach (var t in group)
            {
                n++;
                if (HeardKey(t) is not { } k) { none++; continue; }
                var gap = ChordSymbol.Mod(k.Tonic - t.Tonic);
                if (gap == 0 && k.Minor == t.Minor) right++;
                else if (gap == 0) mode++;
                else if (k.Minor != t.Minor && gap == (t.Minor ? 3 : 9)) relative++;
                else if (gap is 5 or 7) fifth++;
                else other++;
            }
            Console.WriteLine($"{group.Key,-20} {Pct(right, n)} / {Pct(mode, n)} / {Pct(relative, n)} / {Pct(fifth, n)} / {Pct(other, n)} / {Pct(none, n)}   (n {n})");
        }
    }

    // ---------------------------------------------------------------- what each chord leads to

    /// <summary>Whether the app's numerals say this chord leads into the next one (a dominant resolving, a ii going to its V, a passing chord).</summary>
    static bool LeadsOn(ChordFunction f) =>
        f.Label.StartsWith("dominant") && !f.Label.StartsWith("dominant colour") && !f.Label.StartsWith("dominant held") ||
        f.Label.StartsWith("secondary dominant") || f.Label.StartsWith("tritone sub") || f.Label.StartsWith("backdoor") ||
        f.Label.StartsWith("I7 to IV") || f.Label.StartsWith("ii") && f.Kind == 'S' && f.Label.Contains('V') ||
        f.Label.StartsWith("passing diminished") || f.Label.StartsWith("side-slip");

    static void Leads(List<Tune> tunes)
    {
        Console.WriteLine();
        Console.WriteLine("=== WHAT EACH CHORD LEADS TO: the app's numerals against the Treebank's trees (experts) ===");
        var trees = tunes.Where(t => t.Targets is not null).ToList();
        int both = 0, appOnly = 0, treeOnly = 0, neither = 0;
        var missed = new Dictionary<string, int>();
        var wrong = new Dictionary<string, int>();
        foreach (var t in trees)
        {
            var n = t.Targets!.Length;
            var chords = t.Chords.Take(n).ToList();
            var funcs = ChordFunctions.Analyse(chords, t.Tonic, t.Minor, loops: false);
            for (var i = 0; i + 1 < n; i++)
            {
                var tree = t.Targets[i] == i + 1;
                var app = LeadsOn(funcs[i]);
                var pattern = $"{ChordFunctions.RomanOf(chords[i], t.Tonic)} → {ChordFunctions.RomanOf(chords[i + 1], t.Tonic)}";
                if (tree && app) both++;
                else if (app) { appOnly++; wrong[pattern] = wrong.GetValueOrDefault(pattern) + 1; }
                else if (tree) { treeOnly++; missed[pattern] = missed.GetValueOrDefault(pattern) + 1; }
                else neither++;
            }
        }
        var total = both + appOnly + treeOnly + neither;
        Console.WriteLine($"    {trees.Count} tunes with trees, {total} chord to chord steps; the tree says the chord leads into the next one in {both + treeOnly}");
        Console.WriteLine($"    the app says so too: {Pct(both, both + treeOnly)}% of those (recall); when the app says so, the tree agrees {Pct(both, both + appOnly)}% (precision)");
        Console.WriteLine("    most missed (the tree says it leads on, the app's numerals don't): " +
                          string.Join(", ", missed.OrderByDescending(kv => kv.Value).Take(15).Select(kv => $"{kv.Key} ×{kv.Value}")));
        Console.WriteLine("    most often wrong (the app says it leads on, the tree doesn't): " +
                          string.Join(", ", wrong.OrderByDescending(kv => kv.Value).Take(15).Select(kv => $"{kv.Key} ×{kv.Value}")));
    }

    // ---------------------------------------------------------------- key from chords (ChordKey), on tunes it didn't learn from

    /// <summary>The ChoCo charts with a written key the shipped ChordKey learns from (never the Treebank: NC-SA).</summary>
    static IEnumerable<Tune> ChoCo(IEnumerable<Tune> tunes) => tunes.Where(t => t.HasKey && t.Source != "treebank");

    static ChordKey Learn(IEnumerable<Tune> tunes) =>
        ChordKey.Learn(tunes.Select(t => ((IReadOnlyList<ChordSymbol>)t.Chords, (IReadOnlyList<double>)t.Beats, t.Tonic, t.Minor)));

    /// <summary>--save-chord-key: learns ChordKey from every ChoCo chart with a key and writes its table (ChordKey.tsv).</summary>
    public static void SaveChordKey(string corpus, string to)
    {
        var tunes = ChoCo(Load(corpus)).ToList();
        File.WriteAllText(to, Learn(tunes).Save());
        Console.WriteLine($"learned from {tunes.Count} charts, wrote {to}");
    }

    /// <summary>A stretch of a chart: the first <paramref name="beats"/> beats (all of it when 0).</summary>
    static (List<ChordSymbol>, List<double>) Window(Tune t, double beats, double from = 0)
    {
        var c = new List<ChordSymbol>();
        var b = new List<double>();
        double at = 0;
        for (var i = 0; i < t.Chords.Count; i++)
        {
            double s = Math.Max(at, from), e = at + t.Beats[i];
            if (beats > 0) e = Math.Min(e, from + beats);
            if (e > s) { c.Add(t.Chords[i]); b.Add(e - s); }
            at += t.Beats[i];
        }
        return (c, b);
    }

    /// <summary>
    /// The key ChordKey names, learned without the tunes it's tried on: jazz from the Real Book and iReal Pro playlists
    /// (the Treebank's titles left out), gospel in 5 folds (plus that jazz).
    /// </summary>
    static void KeyLab(List<Tune> tunes)
    {
        Console.WriteLine();
        Console.WriteLine("=== KEY FROM CHORDS (ChordKey, learned without the tunes tried): % right (whole chart / first 8 bars / 8 bars from the middle) ===");
        var testTitles = tunes.Where(t => t.Source == "treebank").Select(t => Norm(t.Title)).ToHashSet();
        var jazzTrain = tunes.Where(t => t.HasKey && t.Source is "real-book" or "ireal-playlists" && !testTitles.Contains(Norm(t.Title))).ToList();
        var jazz = Learn(jazzTrain);
        var gospel = tunes.Where(t => t.IsGospel && t.HasKey).ToList();
        var folds = Enumerable.Range(0, 5).Select(k => gospel.Where((_, i) => i % 5 == k).ToList()).ToList();
        var gospelModels = folds.Select(f => Learn(jazzTrain.Concat(gospel.Except(f)))).ToList();
        var tests = new (string Name, List<Tune> Tunes, Func<Tune, ChordKey> Model)[]
        {
            ("treebank", tunes.Where(t => t.Source == "treebank" && t.HasKey).ToList(), _ => jazz),
            ("weimar", tunes.Where(t => t.Source == "weimar" && t.HasKey && !testTitles.Contains(Norm(t.Title))).ToList(), _ => jazz),
            ("gospel (5 folds)", gospel, t => gospelModels[gospel.IndexOf(t) % 5]),
        };
        var line = "";
        foreach (var test in tests)
        {
            var cells = new List<string>();
            foreach (var (beats, middle) in new[] { (0.0, false), (32.0, false), (32.0, true) })
            {
                int n = 0, right = 0;
                foreach (var t in test.Tunes)
                {
                    var from = middle ? Math.Floor(t.Beats.Sum() / 2 / 4) * 4 : 0;
                    var (c, b) = Window(t, beats, from);
                    if (c.Count == 0) continue;
                    n++;
                    if (test.Model(t).Guess(c, b) is { } k && k.Tonic == t.Tonic && k.Minor == t.Minor) right++;
                }
                cells.Add(Pct(right, n));
            }
            line += $"  {test.Name}: {string.Join("/", cells)}";
        }
        Console.WriteLine(line.Trim());
    }
}
