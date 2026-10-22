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
    }

    static void Score(string name, List<Tune>? train, List<Tune> test, Func<List<Tune>, List<Tune>>? trainFor = null, List<List<Tune>>? folds = null)
    {
        folds ??= [test];
        int n = 0, h1 = 0, h3 = 0, h6 = 0;
        foreach (var fold in folds)
        {
            var learnFrom = train ?? trainFor!(fold);
            var model = ProgressionModel.Learn(learnFrom.Select(t => (IReadOnlyList<ChordSymbol>)Changes(t.Chords)));
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

    // ---------------------------------------------------------------- key from chords, tried out

    /// <summary>
    /// How long each chord (its kind, on each note of the scale) sounds in major and minor tunes, and which chord tunes
    /// end on: learned from charts with a written key.
    /// </summary>
    sealed class ChordKeyModel
    {
        static readonly string[] Kinds = ["", "6", "maj7", "m", "m6", "m7", "7", "m7b5", "dim", "dim7", "sus4", "7sus4", "+", "5"];
        readonly double[,,] _held = new double[2, 12, Kinds.Length];
        readonly double[,,] _last = new double[2, 12, Kinds.Length];
        readonly double[,,] _first = new double[2, 12, Kinds.Length];
        readonly double[] _tunes = new double[2];
        readonly Dictionary<(int, int, int, int, int), double> _moves = [];
        readonly double[] _movesTotal = new double[2];

        static int K(ChordSymbol c) => Array.IndexOf(Kinds, ProgressionModel.Kind(c));

        public static ChordKeyModel Learn(IEnumerable<Tune> tunes)
        {
            var m = new ChordKeyModel();
            foreach (var t in tunes.Where(t => t.HasKey))
            {
                var mode = t.Minor ? 1 : 0;
                m._tunes[mode]++;
                for (var i = 0; i < t.Chords.Count; i++)
                    m._held[mode, ChordSymbol.Mod(t.Chords[i].Root - t.Tonic), K(t.Chords[i])] += t.Beats[i];
                for (var i = 0; i + 1 < t.Chords.Count; i++)
                {
                    var key = (mode, ChordSymbol.Mod(t.Chords[i].Root - t.Tonic), K(t.Chords[i]), ChordSymbol.Mod(t.Chords[i + 1].Root - t.Tonic), K(t.Chords[i + 1]));
                    m._moves[key] = m._moves.GetValueOrDefault(key) + 1;
                    m._movesTotal[mode]++;
                }
                m._last[mode, ChordSymbol.Mod(t.Chords[^1].Root - t.Tonic), K(t.Chords[^1])]++;
                m._first[mode, ChordSymbol.Mod(t.Chords[0].Root - t.Tonic), K(t.Chords[0])]++;
            }
            return m;
        }

        static double LogShare(double[,,] table, int mode, int degree, int kind, double smooth)
        {
            double total = 0;
            for (var d = 0; d < 12; d++) for (var k = 0; k < Kinds.Length; k++) total += table[mode, d, k];
            return Math.Log((table[mode, degree, kind] + smooth) / (total + smooth * 12 * Kinds.Length));
        }

        /// <param name="lastWeight">How much the last chord counts, against the chords' shares (per beat, averaged).</param>
        public (int Tonic, bool Minor, double Margin) Guess(IReadOnlyList<ChordSymbol> chords, IReadOnlyList<double> beats,
                                                             double lastWeight, double firstWeight, double moveWeight = 0)
        {
            var scores = new List<(int, bool, double)>();
            var total = beats.Sum();
            for (var mode = 0; mode < 2; mode++)
                for (var tonic = 0; tonic < 12; tonic++)
                {
                    double s = 0;
                    for (var i = 0; i < chords.Count; i++)
                        s += beats[i] / total * LogShare(_held, mode, ChordSymbol.Mod(chords[i].Root - tonic), K(chords[i]), 0.5);
                    s += lastWeight * LogShare(_last, mode, ChordSymbol.Mod(chords[^1].Root - tonic), K(chords[^1]), 0.5);
                    s += firstWeight * LogShare(_first, mode, ChordSymbol.Mod(chords[0].Root - tonic), K(chords[0]), 0.5);
                    s += Math.Log(_tunes[mode] + 1) * 0.1;
                    if (moveWeight > 0 && chords.Count > 1)
                    {
                        double m = 0;
                        for (var i = 0; i + 1 < chords.Count; i++)
                        {
                            var key = (mode, ChordSymbol.Mod(chords[i].Root - tonic), K(chords[i]), ChordSymbol.Mod(chords[i + 1].Root - tonic), K(chords[i + 1]));
                            m += Math.Log((_moves.GetValueOrDefault(key) + 0.1) / (_movesTotal[mode] + 0.1 * 28224));
                        }
                        s += moveWeight * m / (chords.Count - 1);
                    }
                    scores.Add((tonic, mode == 1, s));
                }
            scores.Sort((a, b) => b.Item3.CompareTo(a.Item3));
            return (scores[0].Item1, scores[0].Item2, scores[0].Item3 - scores[1].Item3);
        }
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

    static KeyEstimate? HeardKey(List<ChordSymbol> chords, List<double> beats)
    {
        var section = new PlaygroundSection();
        long at = 0;
        for (var i = 0; i < chords.Count; i++)
        {
            var length = (long)Math.Max(1, Math.Round(beats[i] * Ppq));
            section.ChordTrack.Add(new PlaygroundChord { Start = at, Length = length, Chord = chords[i] });
            at += length;
        }
        section.Bars = (int)Math.Max(1, (at + 4 * Ppq - 1) / (4 * Ppq));
        return SectionHarmony.Of(section).Key;
    }

    static void KeyLab(List<Tune> tunes)
    {
        Console.WriteLine();
        Console.WriteLine("=== KEY FROM CHORDS, tried out: % right (whole chart / first 8 bars / 8 bars from the middle) ===");
        var testTitles = tunes.Where(t => t.Source == "treebank").Select(t => Norm(t.Title)).ToHashSet();
        var jazzTrain = tunes.Where(t => t.Source is "real-book" or "ireal-playlists" && !testTitles.Contains(Norm(t.Title))).ToList();
        var model = ChordKeyModel.Learn(jazzTrain);
        var gospel = tunes.Where(t => t.IsGospel && t.HasKey).ToList();
        var folds = Enumerable.Range(0, 5).Select(k => gospel.Where((_, i) => i % 5 == k).ToList()).ToList();
        var gospelModels = folds.Select(f => ChordKeyModel.Learn(jazzTrain.Concat(gospel.Except(f)))).ToList();
        var tests = new (string Name, List<Tune> Tunes, Func<Tune, ChordKeyModel> Model)[]
        {
            ("treebank", tunes.Where(t => t.Source == "treebank" && t.HasKey).ToList(), _ => model),
            ("weimar", tunes.Where(t => t.Source == "weimar" && t.HasKey && !testTitles.Contains(Norm(t.Title))).ToList(), _ => model),
            ("gospel (5 folds)", gospel, t => gospelModels[gospel.IndexOf(t) % 5]),
        };
        var ways = new List<(string, Func<Tune, List<ChordSymbol>, List<double>, (int, bool)?>)>
        {
            ("the app now", (_, c, b) => HeardKey(c, b) is { } k ? (k.Tonic, k.Minor) : null),
        };
        foreach (var (last, first, moves) in new[] { (0.0, 0.0, 0.0), (0.0, 0.1, 0.0), (0.0, 0.0, 0.3), (0.0, 0.0, 0.6), (0.0, 0.0, 1.0), (0.0, 0.0, 2.0) })
            ways.Add(($"shares, last {last}, first {first}, moves {moves}", (t, c, b) => { var g = tests.First(x => x.Tunes.Contains(t)).Model(t).Guess(c, b, last, first, moves); return (g.Tonic, g.Minor); }));
        foreach (var (name, guess) in ways)
        {
            var line = $"{name,-40}";
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
                        if (guess(t, c, b) is { } k && k.Item1 == t.Tonic && k.Item2 == t.Minor) right++;
                    }
                    cells.Add(Pct(right, n));
                }
                line += $"  {test.Name}: {string.Join("/", cells)}";
            }
            Console.WriteLine(line);
        }
    }
}
