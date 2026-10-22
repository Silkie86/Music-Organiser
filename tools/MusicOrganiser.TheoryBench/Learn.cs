using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;

/// <summary>
/// --learn: what the Playground could learn from a library's chord parts, each pack tested with what was learned from the
/// other packs only. Chord moves: how often the app's "what chord next?" (ProgressionModel's counting) names the chord a
/// player really went to. Voicings: given a chord as typed (Cm7), which notes a player really put in it, against each
/// Voicer style and the voicing most used for that kind of chord in the other packs.
/// </summary>
static class LearnBench
{
    const int Beat = 96, Bar = 4 * Beat;

    /// <param name="Chords">A chord per half bar where three or more notes are struck, with <paramref name="Voicings"/> the notes struck: for the voicing and groove checks.</param>
    /// <param name="AppChords">The chords as the app reads and learns them (its chord reader, via PhraseAnalyser): for the chord moves.</param>
    public sealed record Part(string Style, string Pack, string Name, List<NoteEvent> Notes, List<ChordSymbol> Chords, List<int[]> Voicings, int Tonic, bool Minor,
                              List<ChordSymbol> AppChords);

    public static List<Part> Load(string root)
    {
        var parts = new List<Part>();
        foreach (var path in Directory.EnumerateFiles(root, "*.mid", SearchOption.AllDirectories).Order())
        {
            var rel = Path.GetRelativePath(root, path);
            var segs = rel.Split(Path.DirectorySeparatorChar);
            var style = segs.Length > 1 ? segs[0] : "(top)";
            var pack = segs.Length > 2 ? segs[1] : segs[0];
            IReadOnlyList<MidiFilePart> file;
            try { file = MidiFileReader.Read(path); }
            catch { continue; }
            foreach (var p in file.Where(p => !p.Drums && p.Notes.Count >= 8))
            {
                var name = p.TrackName.Length > 0 ? p.TrackName : p.Name;
                var notes = PhraseAnalyser.Normalise(p.Notes, p.Ppq, keepPlace: true);
                if (notes.Count < 8) continue;
                var analysis = PhraseAnalyser.Analyse(notes, name);
                if (analysis.Role != MidiRoles.Chords) continue;
                var appChords = analysis.Chords.Select(c => ChordSpeller.Spell(c.Mask, c.Root)).OfType<ChordSymbol>().ToList();
                // A chord per half bar, as the app reads them, with the notes struck there as the voicing.
                var chords = new List<ChordSymbol>();
                var voicings = new List<int[]>();
                var end = notes.Max(n => n.Start + n.Length);
                for (long at = 0; at < end; at += 2 * Beat)
                {
                    var struck = notes.Where(n => n.Start >= at - Beat / 8 && n.Start < at + Beat / 2).Select(n => n.Key).Distinct().Order().ToArray();
                    if (struck.Length < 3 || ChordSpeller.Spell(struck) is not { } c) continue;
                    if (chords.Count > 0 && chords[^1].Mask == c.Mask && chords[^1].Root == c.Root) continue;
                    chords.Add(c);
                    voicings.Add(struck);
                }
                var key = analysis.Key;
                if (chords.Count >= 3) parts.Add(new Part(style, pack, rel, notes, chords, voicings, key?.Tonic ?? 0, key?.Minor ?? false, appChords));
            }
        }
        return parts;
    }

    public static void Run(string root)
    {
        var parts = Load(root);
        if (parts.Count == 0)
        {
            Console.WriteLine("No chord parts found (three or more chords each) to learn from.");
            return;
        }
        Console.WriteLine($"chord parts: {parts.Count} from {parts.Select(p => p.Pack).Distinct().Count()} packs ({string.Join(", ", parts.GroupBy(p => p.Style).Select(g => $"{g.Key} {g.Count()}"))})");
        Moves(parts);
        Voicings(parts);
        Grooves(parts);
    }

    // ---------------------------------------------------------------- chord moves

    sealed class Model(Func<Part, ChordSymbol, string> kind, bool blend = false)
    {
        Part _p = null!;
        public readonly Dictionary<string, Dictionary<string, int>> One = new(), Two = new();
        public string Next(ChordSymbol from, ChordSymbol to) => $"{ChordSymbol.Mod(to.Root - from.Root)}:{ProgressionModel.Kind(to)}";
        public string Ctx2(ChordSymbol a, ChordSymbol b) => $"{kind(_p, a)}>{ChordSymbol.Mod(b.Root - a.Root)}:{kind(_p, b)}";
        public void For(Part p) => _p = p;

        public void Learn(IEnumerable<Part> parts)
        {
            foreach (var p in parts)
                for (var i = 1; i < p.AppChords.Count; i++)
                {
                    _p = p;
                    var c = p.AppChords;
                    if (c[i].Root == c[i - 1].Root && ProgressionModel.Kind(c[i]) == ProgressionModel.Kind(c[i - 1])) continue;
                    Add(One, kind(p, c[i - 1]), Next(c[i - 1], c[i]));
                    if (i >= 2) Add(Two, Ctx2(c[i - 2], c[i - 1]), Next(c[i - 1], c[i]));
                }
        }

        static void Add(Dictionary<string, Dictionary<string, int>> t, string ctx, string next)
        {
            if (!t.TryGetValue(ctx, out var row)) t[ctx] = row = new();
            row[next] = row.GetValueOrDefault(next) + 1;
        }

        /// <summary>The guesses in order: two chords of context when it has 8 or more (the old rule), or both blended as ProgressionModel.Next does.</summary>
        public List<string> Guess(ChordSymbol last, ChordSymbol? before)
        {
            Dictionary<string, int>? two = null, one;
            if (before is { } b) Two.TryGetValue(Ctx2(b, last), out two);
            One.TryGetValue(kind(_p, last), out one);
            if (!blend)
            {
                var row = two is null || two.Values.Sum() < 8 ? one : two;
                return row is null ? [] : row.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
            }
            // Both, the longer context counting by how much it has seen.
            var mix = new Dictionary<string, double>();
            double nt = two?.Values.Sum() ?? 0, no = one?.Values.Sum() ?? 0, w = nt / (nt + 5);
            if (two is not null) foreach (var kv in two) mix[kv.Key] = mix.GetValueOrDefault(kv.Key) + w * kv.Value / nt;
            if (one is not null) foreach (var kv in one) mix[kv.Key] = mix.GetValueOrDefault(kv.Key) + (1 - w) * kv.Value / no;
            return mix.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        }
    }

    static void Moves(List<Part> parts)
    {
        Console.WriteLine();
        Console.WriteLine("=== CHORD MOVES: the chord a player went to, among the app's first 1 / 3 / 6 guesses (each pack guessed from the other packs) ===");
        Console.WriteLine("    (chords as the app reads them; a change only counts when the root or the app's kind of chord changes; a guess counts when its root step and kind are right)");
        var styles = parts.Select(p => p.Style).Distinct().Order().Append("ALL").ToList();
        static string Degree(Part p, ChordSymbol c) => $"{ChordSymbol.Mod(c.Root - p.Tonic)}{(p.Minor ? "m" : "M")}{ProgressionModel.Kind(c)}";
        static string Step(ChordSymbol from, ChordSymbol to) => $"{ChordSymbol.Mod(to.Root - from.Root)}:{ProgressionModel.Kind(to)}";
        // The app's own model, learned and asked exactly as the Playground does; then ideas tried beside it.
        var rows = new List<(string Name, Func<List<Part>, Func<Part, ChordSymbol, ChordSymbol?, List<string>>> Train)>
        {
            ("app's model (ProgressionModel)", train =>
            {
                var model = ProgressionModel.Learn(train.Select(p => p.AppChords));
                return (_, last, before) => model.Next(last, before).Select(g => Step(last, g.Chord)).ToList();
            }),
        };
        foreach (var (name, kind, blend) in new (string, Func<Part, ChordSymbol, string>, bool)[]
        {
            ("kind only, two chords when they've 8+ (the old rule)", (_, c) => ProgressionModel.Kind(c), false),
            ("kind + where it sits in the key", Degree, false),
            ("kind + where it sits in the key, blended", Degree, true),
        })
            rows.Add((name, train =>
            {
                var model = new Model(kind, blend);
                model.Learn(train);
                return (p, last, before) => { model.For(p); return model.Guess(last, before); };
            }));
        foreach (var (name, train) in rows)
        {
            var hits = styles.ToDictionary(s => s, _ => new int[4]);
            foreach (var pack in parts.Select(p => p.Pack).Distinct())
            {
                var guess = train(parts.Where(p => p.Pack != pack).ToList());
                foreach (var p in parts.Where(p => p.Pack == pack))
                    for (var i = 1; i < p.AppChords.Count; i++)
                    {
                        var c = p.AppChords;
                        if (c[i].Root == c[i - 1].Root && ProgressionModel.Kind(c[i]) == ProgressionModel.Kind(c[i - 1])) continue;
                        var at = guess(p, c[i - 1], i >= 2 ? c[i - 2] : null).IndexOf(Step(c[i - 1], c[i]));
                        foreach (var s in new[] { p.Style, "ALL" })
                        {
                            hits[s][0]++;
                            if (at == 0) hits[s][1]++;
                            if (at is >= 0 and < 3) hits[s][2]++;
                            if (at is >= 0 and < 6) hits[s][3]++;
                        }
                    }
            }
            Console.WriteLine($"{name,-52} " + string.Join("  ", styles.Select(s => $"{s} {100.0 * hits[s][1] / Math.Max(1, hits[s][0]):0}/{100.0 * hits[s][2] / Math.Max(1, hits[s][0]):0}/{100.0 * hits[s][3] / Math.Max(1, hits[s][0]):0} (n {hits[s][0]})")));
        }
    }

    // ---------------------------------------------------------------- voicings

    /// <summary>The chord as someone would type it: its root and the app's kind, nothing added (Cm9 typed as Cm7).</summary>
    static ChordSymbol Typed(ChordSymbol c) => ChordSymbol.Parse(ChordSymbol.Names[c.Root] + ProgressionModel.Kind(c)) ?? c;

    static void Voicings(List<Part> parts)
    {
        Console.WriteLine();
        Console.WriteLine("=== VOICINGS: given the chord as typed (Cm9 typed as Cm7), how close each way of voicing it is to what the player played ===");
        Console.WriteLine("    notes: share of the player's notes (as pitch classes) the voicing has, and of the voicing's notes the player has (F1); top: the top note's pitch class is the player's");
        var styles = parts.Select(p => p.Style).Distinct().Order().Append("ALL").ToList();
        var ways = new List<(string Name, Func<Part, List<ChordSymbol>, Func<ChordSymbol, int[]>?, List<int[]>> Voice)>();
        foreach (var st in Enum.GetValues<VoicingStyle>())
            ways.Add(($"Voicer {st}", (p, typed, _) => Voicer.VoicePath(typed, st)));
        ways.Add(("learned: most used notes for that kind (other packs)", (p, typed, book) => typed.Select(c => book!(c)).ToList()));

        foreach (var (name, voice) in ways)
        {
            var f1 = styles.ToDictionary(s => s, _ => new List<double>());
            var top = styles.ToDictionary(s => s, _ => new List<double>());
            foreach (var pack in parts.Select(p => p.Pack).Distinct())
            {
                var book = Book(parts.Where(p => p.Pack != pack));
                foreach (var p in parts.Where(p => p.Pack == pack))
                {
                    var typed = p.Chords.Select(Typed).ToList();
                    var voiced = voice(p, typed, book);
                    for (var i = 0; i < typed.Count; i++)
                    {
                        var real = p.Voicings[i].Select(k => k % 12).ToHashSet();
                        var mine = voiced[i].Select(k => ChordSymbol.Mod(k)).ToHashSet();
                        var both = real.Intersect(mine).Count();
                        var score = both == 0 ? 0 : 2.0 * both / (real.Count + mine.Count);
                        var t = voiced[i].Length > 0 && ChordSymbol.Mod(voiced[i].Max()) == p.Voicings[i].Max() % 12 ? 1 : 0;
                        foreach (var s in new[] { p.Style, "ALL" }) { f1[s].Add(score); top[s].Add(t); }
                    }
                }
            }
            Console.WriteLine($"{name,-52} " + string.Join("  ", styles.Select(s => $"{s} {100 * f1[s].DefaultIfEmpty(0).Average():0}/{100 * top[s].DefaultIfEmpty(0).Average():0}")));
        }

        // What the learned book says, on all packs: per kind, the commonest sets of notes above the root.
        var all = Book(parts, show: true);
        Console.WriteLine("top note per kind, by style (interval above the root):");
        foreach (var g in parts.GroupBy(p => p.Style).OrderBy(g => g.Key))
        {
            var tops = g.SelectMany(p => p.Chords.Select((c, i) => (Kind: ProgressionModel.Kind(c), Top: ChordSymbol.Mod(p.Voicings[i].Max() - c.Root)))).ToList();
            Console.WriteLine($"  {g.Key}: " + string.Join("   ", tops.GroupBy(t => t.Kind).OrderByDescending(k => k.Count()).Take(6).Select(k =>
                $"{(k.Key.Length == 0 ? "maj" : k.Key)}: " + string.Join(" ", k.GroupBy(t => t.Top).OrderByDescending(t => t.Count()).Take(3).Select(t => $"{t.Key}={100 * t.Count() / k.Count()}%")))));
        }
    }

    /// <summary>
    /// For each kind of chord as typed, the set of notes above its root players used most; a chord kind it never saw
    /// gets its plain notes. With <paramref name="show"/>, prints each kind's top sets.
    /// </summary>
    static Func<ChordSymbol, int[]> Book(IEnumerable<Part> parts, bool show = false)
    {
        var counts = new Dictionary<string, Dictionary<int, int>>();
        foreach (var p in parts)
            for (var i = 0; i < p.Chords.Count; i++)
            {
                var c = p.Chords[i];
                var kind = ProgressionModel.Kind(c);
                var set = 0;
                foreach (var k in p.Voicings[i]) set |= 1 << ChordSymbol.Mod(k - c.Root);
                if (!counts.TryGetValue(kind, out var row)) counts[kind] = row = new();
                row[set] = row.GetValueOrDefault(set) + 1;
            }
        if (show)
        {
            Console.WriteLine();
            Console.WriteLine("most used note sets per kind (all packs), as intervals above the root:");
            foreach (var (kind, row) in counts.OrderByDescending(kv => kv.Value.Values.Sum()).Take(8))
            {
                var total = (double)row.Values.Sum();
                Console.WriteLine($"  {(kind.Length == 0 ? "maj" : kind),-6} {total,5}: " + string.Join("   ", row.OrderByDescending(kv => kv.Value).Take(5).Select(kv =>
                    $"{string.Join(" ", Enumerable.Range(0, 12).Where(i => (kv.Key >> i & 1) != 0).Select(i => Deg(i)))} {100 * kv.Value / total:0}%")));
            }
        }
        var best = counts.ToDictionary(kv => kv.Key, kv => kv.Value.OrderByDescending(x => x.Value).First().Key);
        // The note players put on top, per kind, so the learned voicing's top note is a real one too.
        var tops = new Dictionary<string, Dictionary<int, int>>();
        foreach (var p in parts)
            for (var i = 0; i < p.Chords.Count; i++)
            {
                var kind = ProgressionModel.Kind(p.Chords[i]);
                if (!tops.TryGetValue(kind, out var row)) tops[kind] = row = new();
                var t = ChordSymbol.Mod(p.Voicings[i].Max() - p.Chords[i].Root);
                row[t] = row.GetValueOrDefault(t) + 1;
            }
        return c =>
        {
            var kind = ProgressionModel.Kind(c);
            var set = best.TryGetValue(kind, out var s) ? s : c.Intervals | 1;
            var top = tops.TryGetValue(kind, out var tr) ? tr.OrderByDescending(x => x.Value).First().Key : -1;
            return Enumerable.Range(0, 12).Where(i => (set >> i & 1) != 0).Select(i => 60 + c.Root + i + (i == top ? 24 : 0)).ToArray();
        };

        static string Deg(int i) => i switch
        {
            0 => "1", 1 => "b9", 2 => "9", 3 => "b3", 4 => "3", 5 => "11", 6 => "#11", 7 => "5", 8 => "b13", 9 => "13", 10 => "b7", _ => "7",
        };
    }

    // ---------------------------------------------------------------- groove

    /// <summary>The 16th steps a bar's chords are struck on (a chord struck once per hit), bar by bar.</summary>
    static List<int> BarPatterns(List<NoteEvent> notes)
    {
        var bars = new Dictionary<long, int>();
        foreach (var g in notes.GroupBy(n => (long)Math.Round(n.Start / (Beat / 4.0))))
        {
            var bar = g.Key / 16;
            bars[bar] = bars.GetValueOrDefault(bar) | 1 << (int)(g.Key % 16);
        }
        var last = notes.Max(n => n.Start) / Bar;
        return Enumerable.Range(0, (int)last + 1).Select(b => bars.GetValueOrDefault(b)).Where(m => m != 0).ToList();
    }

    static void Grooves(List<Part> parts)
    {
        Console.WriteLine();
        Console.WriteLine("=== GROOVE of the chord parts, per style (the app's own reading, GrooveTemplate) ===");
        foreach (var g in parts.GroupBy(p => p.Style).OrderBy(g => g.Key).Append(parts.GroupBy(_ => "ALL").Single()))
        {
            var t = g.Select(p => Groove.Of(p.Notes)).OfType<GrooveTemplate>().ToList();
            var swung = t.Where(x => x.Swing is >= 54).ToList();
            // Pushes: a hit on the last 8th or 16th of a bar that holds over the bar line, like the Anticipate rhythm.
            int pushes = 0, downs = 0, by8 = 0, by16 = 0;
            foreach (var p in g)
                foreach (var n in p.Notes.GroupBy(n => n.Start).Select(h => h.First()))
                {
                    var inBar = n.Start % Bar;
                    if (inBar >= Bar - Beat / 2 - Beat / 8 && inBar < Bar - Beat / 8 && n.Start + n.Length > (n.Start / Bar + 1) * Bar)
                    {
                        pushes++;
                        if (Bar - inBar > Beat * 3 / 8) by8++; else by16++;
                    }
                    if (inBar < Beat / 8 || inBar >= Bar - Beat / 8) downs++;
                }
            Console.WriteLine($"  {g.Key,-9} parts {t.Count,4} | swung {100.0 * swung.Count / t.Count,3:0}% (avg {swung.Select(x => x.Swing!.Value).DefaultIfEmpty(0).Average():0}%) | lean {t.Average(x => x.Lean),5:0.0} ticks | " +
                              $"pushed into the bar: {pushes} hits vs {downs} on the downbeat ({100.0 * pushes / Math.Max(1, pushes + downs):0}%; an 8th early {100.0 * by8 / Math.Max(1, pushes):0}%, a 16th {100.0 * by16 / Math.Max(1, pushes):0}%) | " +
                              string.Join(", ", t.GroupBy(x => x.Describe()).OrderByDescending(x => x.Count()).Take(4).Select(x => $"{x.Key} {100 * x.Count() / t.Count}%")));
        }

        Console.WriteLine();
        Console.WriteLine("=== COMP RHYTHM: how well a bar of each rhythm matches a bar the player played (onset F1 on 16ths; each pack against the other packs' commonest bars) ===");
        int Mask(params int[] steps) => steps.Aggregate(0, (m, s) => m | 1 << s);
        var presets = new (string, int)[]
        {
            ("Held (once a bar)", Mask(0)), ("Held, chords every half bar", Mask(0, 8)), ("Beats", Mask(0, 4, 8, 12)),
            ("Charleston", Mask(0, 6)), ("Two and four", Mask(4, 12)), ("Anticipate (half-bar chords)", Mask(6, 14)),
        };
        static double F1(int a, int b)
        {
            var both = System.Numerics.BitOperations.PopCount((uint)(a & b));
            return both == 0 ? 0 : 2.0 * both / (System.Numerics.BitOperations.PopCount((uint)a) + System.Numerics.BitOperations.PopCount((uint)b));
        }
        var styles = parts.Select(p => p.Style).Distinct().Order().Append("ALL").ToList();
        var rows = presets.Select(x => (x.Item1, (Func<Part, int>)(_ => x.Item2))).ToList();
        foreach (var (name, pick) in rows.Append(("learned: the style's commonest bar, other packs", (Func<Part, int>)(p =>
                 parts.Where(o => o.Pack != p.Pack && o.Style == p.Style).SelectMany(o => BarPatterns(o.Notes)).GroupBy(m => m).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault(1)))))   // a style with one pack: once a bar
        {
            var score = styles.ToDictionary(x => x, _ => new List<double>());
            var cache = new Dictionary<(string, string), int>();
            foreach (var p in parts)
            {
                var mine = cache.TryGetValue((p.Pack, p.Style), out var m) ? m : cache[(p.Pack, p.Style)] = pick(p);
                foreach (var bar in BarPatterns(p.Notes)) { score[p.Style].Add(F1(mine, bar)); score["ALL"].Add(F1(mine, bar)); }
            }
            Console.WriteLine($"  {name,-48} " + string.Join("  ", styles.Select(x => $"{x} {100 * score[x].Average():0}")));
        }
        Console.WriteLine("  commonest bars per style (16ths, x = struck):");
        foreach (var g in parts.GroupBy(p => p.Style).OrderBy(g => g.Key))
        {
            var bars = g.SelectMany(p => BarPatterns(p.Notes)).ToList();
            Console.WriteLine($"    {g.Key,-9} " + string.Join("   ", bars.GroupBy(m => m).OrderByDescending(x => x.Count()).Take(4)
                .Select(x => $"{string.Concat(Enumerable.Range(0, 16).Select(i => (x.Key >> i & 1) != 0 ? 'x' : i % 4 == 0 ? '|' : '.'))} {100 * x.Count() / bars.Count}%")));
        }
    }
}
