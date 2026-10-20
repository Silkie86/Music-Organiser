// Theory bench: runs the app's own key finder, chord readers and fit scores over POP909
// (909 pop songs: MIDI with MELODY / BRIDGE / PIANO tracks, plus chord, key and beat annotations).
using System.Globalization;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

const int Beat = 96, Bar = 4 * Beat;
if (args.Length == 0 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: theorybench <POP909 folder> [--keysonly | --fitonly | --clips]. See tools/README.md for getting the songs.");
    Environment.ExitCode = 2;
    return;
}
var rootDir = args[0];
var songs = new List<Song>();
int skipped = 0, misaligned = 0;
var meters = new Dictionary<int, int>();
foreach (var dir in Directory.GetDirectories(rootDir).OrderBy(d => d))
{
    try
    {
        var s = Song.Load(dir);
        if (s is null) { skipped++; continue; }
        meters[s.BeatsPerBar] = meters.GetValueOrDefault(s.BeatsPerBar) + 1;
        if (!s.Aligned) { misaligned++; continue; }
        songs.Add(s);
    }
    catch (Exception e) { skipped++; Console.Error.WriteLine($"{Path.GetFileName(dir)}: {e.Message}"); }
}
Console.WriteLine($"songs used: {songs.Count} (skipped {skipped}, MIDI beats not lined up with the annotated beats: {misaligned})");
Console.WriteLine("beats per bar (from the downbeat annotations): " + string.Join(", ", meters.OrderBy(m => m.Key).Select(m => $"{m.Key}: {m.Value}")));

// ======================================================================= keys on 8-bar clips (what the MIDI page sees)
if (args.Contains("--clips"))
{
    double[] AEmaj = [17.7661, 0.145624, 14.9265, 0.160186, 19.8049, 11.3587, 0.291248, 22.062, 0.145624, 8.15494, 0.232998, 4.95122], AEmin = [18.2648, 0.737619, 14.0499, 16.8599, 0.702494, 14.4362, 0.702494, 18.6161, 4.56621, 1.93186, 7.37619, 1.75623];
    static double CorrC(double[] x, double[] prof, int tonic)
    {
        double mx = x.Average(), my = prof.Average(), sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < 12; i++) { var a = x[(i + tonic) % 12] - mx; var b = prof[i] - my; sxy += a * b; sxx += a * a; syy += b * b; }
        return sxx <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }
    static (int T, bool M) BestC(double[] ch, double[] maj, double[] min)
    {
        var best = (0, false); var r = double.MinValue;
        for (var t = 0; t < 12; t++)
        {
            var a = CorrC(ch, maj, t); if (a > r) { r = a; best = (t, false); }
            var b = CorrC(ch, min, t); if (b > r) { r = b; best = (t, true); }
        }
        return best;
    }
    static (int T, bool M) Flip((int T, bool M) k, IReadOnlyList<PhraseChord> chords)
    {
        if (chords.Count == 0) return k;
        var relative = k.M ? (k.T + 3) % 12 : (k.T + 9) % 12;
        return chords[0].Root == relative ? (relative, !k.M) : k;
    }
    Console.WriteLine();
    Console.WriteLine("=== KEYS on 8-bar clips, against the annotated key in the middle of the clip ===");
    foreach (var (partName, pick) in new (string, Func<Song, List<NoteEvent>>)[]
    {
        ("all parts together", s => s.Melody.Concat(s.Bridge).Concat(s.Piano).OrderBy(n => n.Start).ThenBy(n => n.Key).ToList()),
        ("accompaniment part alone", s => s.Piano),
        ("melody part alone", s => s.Melody),
    })
    {
        int n = 0, kkExact = 0, pageExact = 0, aeExact = 0, aeFlipExact = 0, kkRel = 0, pageRel = 0, aeRel = 0, aeFlipRel = 0;
        int fired = 0, firedFixed = 0, firedBroke = 0, firedNeither = 0;
        foreach (var s in songs)
        {
            var notes = pick(s);
            for (long at = 0; at + 8 * Bar <= s.Length; at += 8 * Bar)
            {
                var mid = at + 4 * Bar;
                var refKey = s.Keys.FirstOrDefault(k => k.Start <= mid && k.End > mid);
                if (refKey.End == 0) continue;
                var clip = Cut(notes, at, 8 * Bar);
                if (clip.Count < 8) continue;
                var a = PhraseAnalyser.Analyse(clip);
                if (a.Key is not { } page) continue;
                var chroma = a.Chroma.Select(c => (double)c).ToArray();
                if (MusicTheory.EstimateKey(chroma) is not { } kk) continue;
                var ae = BestC(chroma, AEmaj, AEmin);
                var aeFlip = Flip(ae, a.Chords);
                bool Exact(int t, bool m) => t == refKey.Tonic && m == refKey.Minor;
                bool Rel(int t, bool m) => m != refKey.Minor && (refKey.Minor ? Mod(refKey.Tonic + 3) == t : Mod(refKey.Tonic + 9) == t);
                n++;
                if (Exact(kk.Tonic, kk.Minor)) kkExact++; else if (Rel(kk.Tonic, kk.Minor)) kkRel++;
                if (Exact(page.Tonic, page.Minor)) pageExact++; else if (Rel(page.Tonic, page.Minor)) pageRel++;
                if (Exact(ae.T, ae.M)) aeExact++; else if (Rel(ae.T, ae.M)) aeRel++;
                if (Exact(aeFlip.T, aeFlip.M)) aeFlipExact++; else if (Rel(aeFlip.T, aeFlip.M)) aeFlipRel++;
                if (page.Tonic != kk.Tonic || page.Minor != kk.Minor)
                {
                    fired++;
                    if (Exact(page.Tonic, page.Minor)) firedFixed++;
                    else if (Exact(kk.Tonic, kk.Minor)) firedBroke++;
                    else firedNeither++;
                }
            }
        }
        string P(int x) => $"{100.0 * x / n:0.0}%";
        Console.WriteLine($"{partName,-26} clips {n,5} | profile alone (KK) exact {P(kkExact)} rel {P(kkRel)} | KK + first chord (MIDI page) exact {P(pageExact)} rel {P(pageRel)} | Aarden-Essen exact {P(aeExact)} rel {P(aeRel)} | AE + first chord exact {P(aeFlipExact)} rel {P(aeFlipRel)}");
        Console.WriteLine($"{"",-26} first-chord rule changed the key on {P(fired)} of clips: made it right {firedFixed}, made it wrong {firedBroke}, wrong either way {firedNeither}");
    }
    return;
}

static int Mod(int n) => ((n % 12) + 12) % 12;

// ======================================================================= keys
if (!args.Contains("--fitonly"))
{
    double Mirex(int t, bool m, int rt, bool rm)
    {
        if (t == rt && m == rm) return 1;
        if (m == rm && (Mod(t - rt) == 7 || Mod(t - rt) == 5)) return 0.5;
        if (m != rm && (rm ? Mod(rt + 3) == t : Mod(rt + 9) == t)) return 0.3;   // relative
        if (t == rt && m != rm) return 0.2;                                         // parallel
        return 0;
    }
    var rows = new List<(string Method, bool OneKey, double Score, bool Exact, bool Shown, string Relation)>();
    foreach (var s in songs)
    {
        var all = s.Melody.Concat(s.Bridge).Concat(s.Piano).OrderBy(n => n.Start).ToList();
        var chroma = new double[12];
        foreach (var n in all) chroma[n.Key % 12] += Math.Clamp(n.Length / (double)Bar, 1 / 16.0, 2);
        var project = MusicTheory.EstimateKey(chroma);
        var analysed = PhraseAnalyser.Analyse(all).Key;
        var (rt, rm) = s.MainKey;
        foreach (var (method, k, threshold) in new[] { ("profile (Projects page key)", project, ProjectKey.MinConfidence), ("profile + first chord (MIDI page key)", analysed, 0.05) })
        {
            var shown = k is { } kk && kk.Confidence >= threshold;
            var score = k is { } k2 ? Mirex(k2.Tonic, k2.Minor, rt, rm) : 0;
            var relation = k is not { } k3 ? "none" : score == 1 ? "exact" : score == 0.5 ? "fifth" : score == 0.3 ? "relative" : score == 0.2 ? "parallel" : "other";
            rows.Add((method, s.Keys.Count == 1, score, score == 1, shown, relation));
        }
    }
    Console.WriteLine();
    Console.WriteLine("=== KEYS (MIREX weighted score: exact 1, fifth 0.5, relative 0.3, parallel 0.2) ===");
    foreach (var g in rows.GroupBy(r => r.Method))
    {
        foreach (var (label, sel) in new (string, Func<(string Method, bool OneKey, double Score, bool Exact, bool Shown, string Relation), bool>)[] { ("all songs", _ => true), ("songs with one key", r => r.OneKey), ("songs that change key", r => !r.OneKey) })
        {
            var list = g.Where(sel).ToList();
            if (list.Count == 0) continue;
            Console.WriteLine($"{g.Key,-40} {label,-22} n={list.Count,4}  exact {100.0 * list.Count(r => r.Exact) / list.Count:0.0}%  MIREX {100 * list.Average(r => r.Score):0.0}  shown (confident) {100.0 * list.Count(r => r.Shown) / list.Count:0}%  " +
                              string.Join(" ", list.GroupBy(r => r.Relation).OrderByDescending(x => x.Count()).Select(x => $"{x.Key} {100.0 * x.Count() / list.Count:0}%")));
        }
    }
    var flatKeys = songs.Count(s => s.MainKeyName.Length > 1 && s.MainKeyName[1] == 'b');
    Console.WriteLine($"Main keys written with a flat in the annotations (the app names them with sharps): {flatKeys} of {songs.Count}");
}

// ======================================================================= keys, other ways
if (!args.Contains("--fitonly"))
{
    double[] KKmaj = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88], KKmin = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];
    double[] TKPmaj = [0.748, 0.060, 0.488, 0.082, 0.670, 0.460, 0.096, 0.715, 0.104, 0.366, 0.057, 0.400], TKPmin = [0.712, 0.084, 0.474, 0.618, 0.049, 0.460, 0.105, 0.747, 0.404, 0.067, 0.133, 0.330];
    double[] AEmaj = [17.7661, 0.145624, 14.9265, 0.160186, 19.8049, 11.3587, 0.291248, 22.062, 0.145624, 8.15494, 0.232998, 4.95122], AEmin = [18.2648, 0.737619, 14.0499, 16.8599, 0.702494, 14.4362, 0.702494, 18.6161, 4.56621, 1.93186, 7.37619, 1.75623];
    double[] BBmaj = [16.80, 0.86, 12.95, 1.41, 13.49, 11.93, 1.25, 20.28, 1.80, 8.04, 0.62, 10.57], BBmin = [18.16, 0.69, 12.99, 13.34, 1.07, 11.15, 1.38, 21.07, 7.49, 1.53, 0.92, 10.21];
    static double Corr(double[] x, double[] prof, int tonic)
    {
        double mx = x.Average(), my = prof.Average(), sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < 12; i++) { var a = x[(i + tonic) % 12] - mx; var b = prof[i] - my; sxy += a * b; sxx += a * a; syy += b * b; }
        return sxx <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }
    static (int T, bool M) Best(double[] ch, double[] maj, double[] min)
    {
        var best = (0, false); var r = double.MinValue;
        for (var t = 0; t < 12; t++)
        {
            var a = Corr(ch, maj, t); if (a > r) { r = a; best = (t, false); }
            var b = Corr(ch, min, t); if (b > r) { r = b; best = (t, true); }
        }
        return best;
    }
    static double[] ChromaOf(IEnumerable<NoteEvent> ns, bool bass)
    {
        var ch = new double[12];
        var list = ns.OrderBy(n => n.Start).ToList();
        // The bass: the lowest note sounding at each onset of the accompaniment.
        var low = new HashSet<NoteEvent>();
        if (bass)
            foreach (var g in list.GroupBy(n => n.Start / 24))
                low.Add(g.MinBy(n => n.Key));
        foreach (var n in list) ch[n.Key % 12] += Math.Clamp(n.Length / 384.0, 1 / 16.0, 2) * (low.Contains(n) ? 2 : 1);
        return ch;
    }
    double Mirex(int t, bool m, int rt, bool rm)
    {
        if (t == rt && m == rm) return 1;
        if (m == rm && (Mod(t - rt) == 7 || Mod(t - rt) == 5)) return 0.5;
        if (m != rm && (rm ? Mod(rt + 3) == t : Mod(rt + 9) == t)) return 0.3;
        if (t == rt && m != rm) return 0.2;
        return 0;
    }
    var methods = new (string Name, Func<Song, (int, bool)> Key)[]
    {
        ("Krumhansl-Kessler (the app's profile)", s => Best(ChromaOf(s.Melody.Concat(s.Bridge).Concat(s.Piano), false), KKmaj, KKmin)),
        ("Temperley / Kostka-Payne", s => Best(ChromaOf(s.Melody.Concat(s.Bridge).Concat(s.Piano), false), TKPmaj, TKPmin)),
        ("Aarden-Essen", s => Best(ChromaOf(s.Melody.Concat(s.Bridge).Concat(s.Piano), false), AEmaj, AEmin)),
        ("Bellman-Budge", s => Best(ChromaOf(s.Melody.Concat(s.Bridge).Concat(s.Piano), false), BBmaj, BBmin)),
        ("Krumhansl-Kessler, bass notes count double", s => Best(ChromaOf(s.Melody.Concat(s.Bridge).Concat(s.Piano), true), KKmaj, KKmin)),
        ("Temperley / Kostka-Payne, bass notes count double", s => Best(ChromaOf(s.Melody.Concat(s.Bridge).Concat(s.Piano), true), TKPmaj, TKPmin)),
    };
    Console.WriteLine();
    Console.WriteLine("=== KEYS: other key profiles (songs with one key) ===");
    var one = songs.Where(s => s.Keys.Count == 1).ToList();
    foreach (var (name, key) in methods)
    {
        var scores = one.Select(s => { var (t, m) = key(s); return Mirex(t, m, s.MainKey.Tonic, s.MainKey.Minor); }).ToList();
        Console.WriteLine($"{name,-52} exact {100.0 * scores.Count(x => x == 1) / scores.Count:0.0}%  relative {100.0 * scores.Count(x => x == 0.3) / scores.Count:0.0}%  MIREX {100 * scores.Average():0.0}");
    }

    // A key per stretch: 16 bars around each 4-bar step, against the annotated key there.
    Console.WriteLine();
    Console.WriteLine("=== KEYS: one key per song vs a key per stretch (time-weighted, songs that change key) ===");
    foreach (var (label, list) in new[] { ("songs that change key", songs.Where(s => s.Keys.Count > 1).ToList()), ("songs with one key", one) })
    {
        double whole = 0, windowed = 0, n = 0;
        foreach (var s in list)
        {
            var all = s.Melody.Concat(s.Bridge).Concat(s.Piano).ToList();
            var global = Best(ChromaOf(all, false), KKmaj, KKmin);
            for (long at = 0; at < s.Length; at += 4 * Bar)
            {
                var mid = at + 2 * Bar;
                var refKey = s.Keys.FirstOrDefault(k => k.Start <= mid && k.End > mid);
                if (refKey.End == 0) continue;
                var near = all.Where(x => x.Start >= mid - 8 * Bar && x.Start < mid + 8 * Bar).ToList();
                if (near.Count < 16) continue;
                var local = Best(ChromaOf(near, false), KKmaj, KKmin);
                n++;
                if (global.Item1 == refKey.Tonic && global.Item2 == refKey.Minor) whole++;
                if (local.Item1 == refKey.Tonic && local.Item2 == refKey.Minor) windowed++;
            }
        }
        Console.WriteLine($"{label,-24} one key for the song: exact {100 * whole / n:0.0}% of the time | a key per 16 bars: exact {100 * windowed / n:0.0}% of the time");
    }
}

// ======================================================================= chords
if (!args.Contains("--fitonly") && !args.Contains("--keysonly"))
{
    Console.WriteLine();
    Console.WriteLine("=== CHORDS (time-weighted, 16th-note samples inside each annotated chord, N skipped) ===");
    // How often the true harmony changes off the half-bar grid the readers use.
    long inside = 0, offGrid = 0;
    foreach (var s in songs)
        foreach (var c in s.Chords.Where(c => c.Root >= 0))
        {
            inside += c.End - c.Start;
            for (var t = c.Start; t < c.End; t += 24)
            {
                var slot = t / (2 * Beat);
                var others = s.Chords.Any(o => o.Root >= 0 && !ReferenceEquals(o, c) && o.Label != c.Label && o.Start < (slot + 1) * 2 * Beat && o.End > slot * 2 * Beat);
                if (others) offGrid += 24;
            }
        }
    Console.WriteLine($"Share of chord time in a half bar that holds two different annotated chords: {100.0 * offGrid / Math.Max(1, inside):0.0}%");

    var readers = new (string Name, Func<Song, Func<long, (int Root, string Cls, int Bass)?>> Make)[]
    {
        ("MIDI page reader, every part (melody too)", s => PageReader(s.Melody.Concat(s.Bridge).Concat(s.Piano).OrderBy(n => n.Start).ToList(), s.Length)),
        ("MIDI page reader, accompaniment only", s => PageReader(s.Piano, s.Length)),
        ("Playground reader, every part (melody too)", s => LaneReader(s.Melody.Concat(s.Bridge).Concat(s.Piano).OrderBy(n => n.Start).ToList(), s.Length)),
        ("Playground reader, accompaniment only", s => LaneReader(s.Piano, s.Length)),
    };
    foreach (var off in new long[] { -96, -48, 0, 48, 96 })
    {
        double tot = 0, ok = 0;
        foreach (var s in songs)
        {
            var at = LaneReader(s.Piano, s.Length);
            foreach (var c in s.Chords.Where(c => c.Root >= 0))
                for (var t = c.Start; t < c.End; t += 24) { tot++; if (at(t + off) is { } g && g.Item1 == c.Root) ok++; }
        }
        Console.WriteLine($"  alignment check: labels moved {off,4} ticks -> root {100 * ok / tot:0.0}%");
    }
    foreach (var (name, make) in readers)
    {
        double total = 0, root = 0, mmTotal = 0, mm = 0, bassTotal = 0, bass = 0, none = 0;
        foreach (var s in songs)
        {
            var at = make(s);
            foreach (var c in s.Chords.Where(c => c.Root >= 0))
                for (var t = c.Start; t < c.End; t += 24)
                {
                    total++;
                    var got = at(t);
                    if (got is null) { none++; continue; }
                    var g = got.Value;
                    if (g.Root == c.Root) root++;
                    if (c.Cls is "maj" or "min")
                    {
                        mmTotal++;
                        if (g.Root == c.Root && g.Cls == c.Cls) mm++;
                    }
                    bassTotal++;
                    if (g.Bass == c.Bass) bass++;
                }
        }
        Console.WriteLine($"{name,-44} root {100 * root / total:0.0}%  root+major/minor {100 * mm / mmTotal:0.0}%  bass note {100 * bass / bassTotal:0.0}%  no chord {100 * none / total:0.0}%");
    }
}

// ======================================================================= fit
if (!args.Contains("--keysonly"))
{
    Console.WriteLine();
    Console.WriteLine("=== FIT: does the score tell a song's own melody (right) from a wrong one, over the song's own accompaniment? ===");
    var rng = new Random(909);
    var cases = new List<(Song S, List<NoteEvent> Acc, List<NoteEvent> Mel, long Len)>();
    foreach (var s in songs)
    {
        if (s.Melody.Count < 8) continue;
        var first = s.Melody.Min(n => n.Start) / Bar * Bar;
        const long len = 8 * Bar;
        var mel = Cut(s.Melody, first, len);
        var acc = Cut(s.Piano, first, len);
        if (mel.Count < 8 || acc.Count < 8) continue;
        cases.Add((s, acc, mel, len));
    }
    Console.WriteLine($"8-bar windows: {cases.Count}");
    var negOther = new List<List<NoteEvent>>();
    var negShift = new List<List<NoteEvent>>();
    for (var i = 0; i < cases.Count; i++)
    {
        var a = cases[i];
        var pool = Enumerable.Range(0, cases.Count).Where(j => j != i && cases[j].S.MainKey.Minor == a.S.MainKey.Minor).ToList();
        var b = cases[pool[rng.Next(pool.Count)]];
        var d = Mod(a.S.MainKey.Tonic - b.S.MainKey.Tonic);
        if (d > 6) d -= 12;
        var moved = b.Mel.Select(n => n with { Key = n.Key + d }).ToList();
        var oct = (int)Math.Round((HarmonyMatcher.Centre(a.Mel) - HarmonyMatcher.Centre(moved)) / 12);
        negOther.Add(moved.Select(n => n with { Key = Math.Clamp(n.Key + 12 * oct, 0, 127) }).ToList());
        // Its own melody a bar late (going round), so only the timing against the chords is wrong.
        negShift.Add(a.Mel.Select(n => n with { Start = (n.Start + Bar) % a.Len }).OrderBy(n => n.Start).ToList());
    }

    var lanes = cases.Select(c => ChordLane.Detect(c.Acc, c.Len)).ToList();
    var scorers = new (string Name, Func<int, List<NoteEvent>, double> Score)[]
    {
        ("MIDI page 'plays together', as it is", (i, m) => HarmonyMatcher.SitsTogether(m, cases[i].Acc) ?? 0),
        ("  (the bench's copy, before 2026-10-20 fixes)", (i, m) => Together.Score(m, cases[i].Acc, cases[i].Len, Together.Symmetric)),
        ("  ...with the clash made directional (half step above)", (i, m) => Together.Score(m, cases[i].Acc, cases[i].Len, Together.Directional)),
        ("MIDI page list % (all 12 keys tried, then together)", (i, m) => PageFull(m, cases[i].Acc)),
        ("  list %, 'together' part alone (at the move it picked)", (i, m) => PageParts(m, cases[i].Acc).Together),
        ("  list % reweighted: 0.2 first look + 0.8 together", (i, m) => { var p = PageParts(m, cases[i].Acc); return 0.2 * p.Move + 0.8 * p.Together; }),
        ("Playground fit (chord-scale ear), as it is", (i, m) => SectionFit.Score(m, lanes[i], cases[i].Len, MidiRoles.Melody) ?? 0),
        ("  (the bench's copy, before 2026-10-20 fixes)", (i, m) => Fit.Score(m, lanes[i], cases[i].Len, fifth: false, split: false, worst: false)),
        ("  + the chord's 5th always counts as a chord note", (i, m) => Fit.Score(m, lanes[i], cases[i].Len, fifth: true, split: false, worst: false)),
        ("  + held notes judged on every chord they sound over", (i, m) => Fit.Score(m, lanes[i], cases[i].Len, fifth: true, split: true, worst: false)),
        ("  + the worst bar counts extra", (i, m) => Fit.Score(m, lanes[i], cases[i].Len, fifth: true, split: true, worst: true)),
        ("Playground fit, Classical ear, as it is", (i, m) => SectionFit.Score(m, lanes[i], cases[i].Len, MidiRoles.Melody, HarmonyStyle.Classical) ?? 0),
        ("5th + held notes + tensions on strong beats x0.6", (i, m) => Fit.Score(m, lanes[i], cases[i].Len, fifth: true, split: true, worst: false, strong: 0.6)),
        ("5th + held notes + tensions on strong beats x0.3", (i, m) => Fit.Score(m, lanes[i], cases[i].Len, fifth: true, split: true, worst: false, strong: 0.3)),
        ("half directional together + half (5th + held + x0.6)", (i, m) => 0.5 * Together.Score(m, cases[i].Acc, cases[i].Len, Together.Directional) + 0.5 * Fit.Score(m, lanes[i], cases[i].Len, fifth: true, split: true, worst: false, strong: 0.6)),
        ("half MIDI page together + half (5th + held + x0.6)", (i, m) => 0.5 * (HarmonyMatcher.SitsTogether(m, cases[i].Acc) ?? 0) + 0.5 * Fit.Score(m, lanes[i], cases[i].Len, fifth: true, split: true, worst: false, strong: 0.6)),
    };
    Console.WriteLine("score | right melody avg | other song's melody, same key: avg, AUC | own melody a bar late: avg, AUC");
    foreach (var (name, score) in scorers)
    {
        var pos = cases.Select((c, i) => score(i, c.Mel)).ToList();
        var other = negOther.Select((m, i) => score(i, m)).ToList();
        var shift = negShift.Select((m, i) => score(i, m)).ToList();
        Console.WriteLine($"{name,-54} {100 * pos.Average(),5:0.0}% | {100 * other.Average(),5:0.0}%  AUC {Auc(pos, other):0.000} | {100 * shift.Average(),5:0.0}%  AUC {Auc(pos, shift):0.000}");
    }

    Console.WriteLine();
    Console.WriteLine("=== RED MARKS on the songs' own melodies (professional arrangements: nearly all are false alarms) ===");
    long notes = 0, pageRed = 0, pgRed = 0, pgRedFixed = 0, aboveRed = 0;
    var aboveByDegree = new Dictionary<string, int>();
    var pageByDegree = new Dictionary<string, int>();
    var pgByDegree = new Dictionary<string, int>();
    for (var i = 0; i < cases.Count; i++)
    {
        var c = cases[i];
        foreach (var n in c.Mel)
        {
            notes++;
            var chord = ChordLane.At(lanes[i], n.Start + 1);
            var degree = chord is { } ch ? ChordScales.Degree(ch, n.Key % 12) : "?";
            if (HarmonyMatcher.Rubs(n.Start, n.Length, n.Key, c.Acc)) { pageRed++; pageByDegree[degree] = pageByDegree.GetValueOrDefault(degree) + 1; }
            if (Together.RubsAbove(n.Start, n.Length, n.Key, c.Acc)) { aboveRed++; aboveByDegree[degree] = aboveByDegree.GetValueOrDefault(degree) + 1; }
            if (chord is { } h && n.Length > Beat / 2)
            {
                if (ChordScales.Sit(h, n.Key % 12) < 0.05) { pgRed++; pgByDegree[degree] = pgByDegree.GetValueOrDefault(degree) + 1; }
                if (Fit.Sit(h, n.Key % 12, fifth: true) < 0.05) pgRedFixed++;
            }
        }
    }
    Console.WriteLine($"melody notes checked: {notes}");
    Console.WriteLine($"MIDI page red, as it is (without the chord lane): {100.0 * pageRed / notes:0.0}% of notes; as what over the chord heard: " + string.Join(", ", pageByDegree.OrderByDescending(x => x.Value).Take(8).Select(x => $"{x.Key} {100.0 * x.Value / pageRed:0}%")));
    Console.WriteLine($"Playground red (chord-scale ear): {100.0 * pgRed / notes:0.0}% of notes; as what: " + string.Join(", ", pgByDegree.OrderByDescending(x => x.Value).Take(8).Select(x => $"{x.Key} {100.0 * x.Value / Math.Max(1, pgRed):0}%")));
    Console.WriteLine($"MIDI page red, directional (only a half step ABOVE a sounding note): {100.0 * aboveRed / notes:0.0}% of notes; as what: " + string.Join(", ", aboveByDegree.OrderByDescending(x => x.Value).Take(8).Select(x => $"{x.Key} {100.0 * x.Value / Math.Max(1, aboveRed):0}%")));
    // Red rules on the right melodies (false alarms) and on wrong melodies (other song, same key: real clashes are likelier).
    var rules = new (string Name, Func<NoteEvent, List<NoteEvent>, IReadOnlyList<TimedChord>, bool> Red)[]
    {
        ("MIDI page as it is, with the chord lane", (n, acc, lane) => HarmonyMatcher.Rubs(n.Start, n.Length, n.Key, acc, lane)),
        ("half step above only", (n, acc, lane) => Together.RubsAbove(n.Start, n.Length, n.Key, acc)),
        ("half step above, notes longer than an 8th", (n, acc, lane) => n.Length > Beat / 2 && Together.RubsAbove(n.Start, n.Length, n.Key, acc)),
        ("half step above, longer than an 8th, not a chord note", (n, acc, lane) => n.Length > Beat / 2 && Together.RubsAbove(n.Start, n.Length, n.Key, acc) &&
                                                                     !(ChordLane.At(lane, n.Start + 1) is { } ch && Fit.Sit(ch, n.Key % 12, fifth: true) >= 1)),
        ("Playground as it is", (n, acc, lane) => ChordLane.Fights(lane, n.Start, n.Length, n.Key % 12)),
        ("chord-scale ear with the 5th fix", (n, acc, lane) => n.Length > Beat / 2 && ChordLane.At(lane, n.Start + 1) is { } ch && Fit.Sit(ch, n.Key % 12, fifth: true) < 0.05),
    };
    Console.WriteLine("red rule | right melodies (false alarms) | wrong melodies from another song in the key | ratio");
    foreach (var (name, red) in rules)
    {
        double rightRed = 0, rightN = 0, wrongRed = 0, wrongN = 0;
        for (var i = 0; i < cases.Count; i++)
        {
            foreach (var n in cases[i].Mel) { rightN++; if (red(n, cases[i].Acc, lanes[i])) rightRed++; }
            foreach (var n in negOther[i]) { wrongN++; if (red(n, cases[i].Acc, lanes[i])) wrongRed++; }
        }
        Console.WriteLine($"{name,-56} {100 * rightRed / rightN,5:0.0}% | {100 * wrongRed / wrongN,5:0.0}% | x{wrongRed / wrongN / Math.Max(1e-9, rightRed / rightN):0.0}");
    }
    Console.WriteLine($"Playground red with the 5th fix: {100.0 * pgRedFixed / notes:0.0}% of notes");
}

// ======================================================================= helpers
static List<NoteEvent> Cut(IReadOnlyList<NoteEvent> notes, long from, long len) =>
    notes.Where(n => n.Start >= from && n.Start < from + len)
         .Select(n => n with { Start = n.Start - from, Length = Math.Max(1, Math.Min(n.Length, from + len - n.Start)) })
         .OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();

static double Auc(List<double> pos, List<double> neg)
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
    static int LowerBound(double[] a, double x) { int lo = 0, hi = a.Length; while (lo < hi) { var mid = (lo + hi) / 2; if (a[mid] < x) lo = mid + 1; else hi = mid; } return lo; }
}

static float[] Chroma(IReadOnlyList<NoteEvent> ns)
{
    var ch = new float[12];
    foreach (var n in ns) ch[n.Key % 12] += (float)Math.Clamp(n.Length / 384.0, 1 / 16.0, 2);
    var t = ch.Sum();
    return t <= 0 ? ch : ch.Select(x => x / t).ToArray();
}

static double PageFull(List<NoteEvent> mel, List<NoteEvent> acc)
{
    var target = HarmonyTarget.FromPart("t", MidiRoles.Chords, acc, Chroma(acc), PhraseAnalyser.Progression(acc), null);
    return HarmonyMatcher.Refine(Chroma(mel), PhraseAnalyser.Progression(mel), mel, MidiRoles.Melody, target).Score;
}

static (double Score, double Together, double Move) PageParts(List<NoteEvent> mel, List<NoteEvent> acc)
{
    var target = HarmonyTarget.FromPart("t", MidiRoles.Chords, acc, Chroma(acc), PhraseAnalyser.Progression(acc), null);
    var f = HarmonyMatcher.Refine(Chroma(mel), PhraseAnalyser.Progression(mel), mel, MidiRoles.Melody, target);
    if (f.Together is not { } t) return (f.Score, f.Score, f.Score);
    return (f.Score, t, Math.Clamp((f.Score - 0.6 * t) / 0.4, 0, 1));
}

// The MIDI page's chord reading, half a bar at a time (what PhraseAnalyser.Progression does), with times kept.
static Func<long, (int, string, int)?> PageReader(List<NoteEvent> notes, long length)
{
    var slots = new List<(int, string, int)?>();
    (int, string, int)? last = null;
    var sorted = notes.OrderBy(n => n.Start).ToList();
    for (long w = 0; w < length; w += 192)
    {
        var win = sorted.Where(n => n.Start < w + 192 && n.Start + n.Length > w)
                        .Select(n => n with { Start = Math.Max(n.Start, w) - w, Length = Math.Min(n.Start + n.Length, w + 192) - Math.Max(n.Start, w) }).ToList();
        var prog = win.Count == 0 ? [] : PhraseAnalyser.Progression(win);
        if (prog.Count > 0)
        {
            var c = prog[0];
            var cls = (c.Mask >> ((c.Root + 4) % 12) & 1) != 0 ? "maj" : (c.Mask >> ((c.Root + 3) % 12) & 1) != 0 ? "min" : "other";
            var low = win.Where(n => n.Length >= 24).Select(n => n.Key).DefaultIfEmpty(-1).Min();
            last = (c.Root, cls, low < 0 ? c.Root : low % 12);
        }
        slots.Add(last);
    }
    return t => t < 0 ? null : slots.ElementAtOrDefault((int)(t / 192));
}

static Func<long, (int, string, int)?> LaneReader(List<NoteEvent> notes, long length)
{
    var lane = ChordLane.Detect(notes, length);
    return t =>
    {
        if (ChordLane.At(lane, t) is not { } c)
        {
            // like the chord lane on screen, a gap keeps the chord before
            var before = lane.LastOrDefault(x => x.Start <= t);
            if (before is null) return null;
            c = before.Chord;
        }
        var cls = c.Family switch
        {
            ChordFamily.Major or ChordFamily.Dominant or ChordFamily.Augmented when c.Has(4) => c.Family == ChordFamily.Augmented ? "aug" : "maj",
            ChordFamily.Minor => "min",
            ChordFamily.HalfDiminished or ChordFamily.Diminished => "dim",
            ChordFamily.Sus => "sus",
            _ => "other",
        };
        return (c.Root, cls, c.Bass);
    };
}

sealed class Song
{
    public required string Id;
    public required List<NoteEvent> Melody, Bridge, Piano;
    public required List<(long Start, long End, int Tonic, bool Minor)> Keys;
    public required (int Tonic, bool Minor) MainKey;
    public required string MainKeyName;
    public required List<RefChord> Chords;
    public required int BeatsPerBar;
    public required bool Aligned;
    public long Length => Math.Max(Piano.Concat(Melody).Concat(Bridge).Select(n => n.Start + n.Length).DefaultIfEmpty(0).Max(), Chords.Select(c => c.End).DefaultIfEmpty(0).Max());

    public static Song? Load(string dir)
    {
        var id = Path.GetFileName(dir);
        var mid = Path.Combine(dir, id + ".mid");
        if (!File.Exists(mid)) return null;
        var bytes = File.ReadAllBytes(mid);
        var (ppq, tempos) = Tempo.Map(bytes);
        double ToTicks(double sec) => Tempo.TicksAt(sec, ppq, tempos) * 96.0 / ppq;
        var parts = MidiFileReader.Read(bytes, id);
        List<NoteEvent> Part(string name) => parts.Where(p => p.TrackName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => PhraseAnalyser.Normalise(p.Notes, p.Ppq, keepPlace: true)).OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
        var melody = Part("MELODY");
        var piano = Part("PIANO");
        var bridge = Part("BRIDGE");
        if (piano.Count == 0) return null;

        var beats = File.ReadAllLines(Path.Combine(dir, "beat_midi.txt")).Select(l => l.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                        .Where(f => f.Length >= 3).Select(f => (T: double.Parse(f[0], CultureInfo.InvariantCulture), Down: double.Parse(f[2], CultureInfo.InvariantCulture) > 0.5)).ToList();
        var downs = beats.Select((b, i) => (b, i)).Where(x => x.b.Down).ToList();
        var perBar = downs.Zip(downs.Skip(1), (a, b) => b.i - a.i).GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(4);
        // The bar grid: shift so the annotated downbeats land on the app's bar lines (tick 0, 384, ...).
        var firstDown = downs.Count > 0 ? ToTicks(downs[0].b.T) : 0;
        var shift = (long)Math.Round(firstDown) % 384;
        var downTicks = downs.Select(d => ToTicks(d.b.T) - shift).ToList();
        var aligned = perBar == 4 && downTicks.Count > 4 && downTicks.Count(t => Math.Abs(t - Math.Round(t / 384) * 384) <= 24) >= 0.9 * downTicks.Count;
        List<NoteEvent> Shift(List<NoteEvent> ns) => ns.Select(n => n with { Start = n.Start - shift }).Where(n => n.Start >= 0).ToList();

        var keys = new List<(long, long, int, bool)>();
        foreach (var line in File.ReadAllLines(Path.Combine(dir, "key_audio.txt")))
        {
            var f = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 3 || RefChord.Note(f[2].Split(':')[0]) is not { } tonic) continue;
            keys.Add(((long)(ToTicks(double.Parse(f[0], CultureInfo.InvariantCulture)) - shift), (long)(ToTicks(double.Parse(f[1], CultureInfo.InvariantCulture)) - shift), tonic, f[2].Contains("min")));
        }
        if (keys.Count == 0) return null;
        var main = keys.GroupBy(k => (k.Item3, k.Item4)).MaxBy(g => g.Sum(k => k.Item2 - k.Item1))!.Key;
        var mainName = File.ReadAllLines(Path.Combine(dir, "key_audio.txt")).Select(l => l.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                           .Where(f => f.Length >= 3).Select(f => f[2]).FirstOrDefault(k => RefChord.Note(k.Split(':')[0]) == main.Item1) ?? "";

        var chords = new List<RefChord>();
        foreach (var line in File.ReadAllLines(Path.Combine(dir, "chord_midi.txt")))
        {
            var f = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 3) continue;
            var s = (long)Math.Round(ToTicks(double.Parse(f[0], CultureInfo.InvariantCulture))) - shift;
            var e = (long)Math.Round(ToTicks(double.Parse(f[1], CultureInfo.InvariantCulture))) - shift;
            if (e <= s || s < 0) continue;
            chords.Add(RefChord.Parse(s, e, f[2]));
        }
        return new Song
        {
            Id = id, Melody = Shift(melody), Bridge = Shift(bridge), Piano = Shift(piano), Keys = keys, MainKey = main, MainKeyName = mainName,
            Chords = chords, BeatsPerBar = perBar, Aligned = aligned,
        };
    }
}

sealed record RefChord(long Start, long End, string Label, int Root, string Cls, int Bass)
{
    public static int? Note(string s)
    {
        if (s.Length == 0) return null;
        int? n = char.ToUpperInvariant(s[0]) switch { 'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11, _ => null };
        if (n is null) return null;
        var v = n.Value;
        foreach (var ch in s[1..]) v += ch == '#' ? 1 : ch == 'b' ? -1 : 0;
        return ((v % 12) + 12) % 12;
    }

    static readonly Dictionary<string, int> Degrees = new()
    {
        ["1"] = 0, ["b2"] = 1, ["2"] = 2, ["b3"] = 3, ["3"] = 4, ["4"] = 5, ["#4"] = 6, ["b5"] = 6, ["5"] = 7, ["#5"] = 8, ["b6"] = 8, ["6"] = 9, ["b7"] = 10, ["7"] = 11,
    };

    public static RefChord Parse(long s, long e, string label)
    {
        if (label == "N" || !label.Contains(':')) return new(s, e, label, -1, "N", -1);
        var root = Note(label[..label.IndexOf(':')]) ?? -1;
        var rest = label[(label.IndexOf(':') + 1)..];
        var bass = root;
        var slash = rest.IndexOf('/');
        if (slash >= 0)
        {
            if (Degrees.TryGetValue(rest[(slash + 1)..], out var d)) bass = (root + d) % 12;
            rest = rest[..slash];
        }
        var cls = rest.StartsWith("maj") || rest.StartsWith("7") || rest.StartsWith("9") ? "maj"
                : rest.StartsWith("minmaj") || rest.StartsWith("min") ? "min"
                : rest.StartsWith("hdim") || rest.StartsWith("dim") ? "dim"
                : rest.StartsWith("aug") ? "aug"
                : rest.StartsWith("sus") ? "sus" : "other";
        return new(s, e, label, root, cls, bass);
    }
}

static class Tempo
{
    public static (int Ppq, List<(long Tick, int Us)> Tempos) Map(byte[] d)
    {
        int Be16(int p) => d[p] << 8 | d[p + 1];
        int Be32(int p) => d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3];
        var ppq = Be16(12);
        var tempos = new List<(long, int)>();
        var pos = 8 + Be32(4);
        while (pos + 8 <= d.Length)
        {
            var len = Be32(pos + 4);
            var isTrack = d[pos] == 'M' && d[pos + 1] == 'T' && d[pos + 2] == 'r' && d[pos + 3] == 'k';
            var p = pos + 8;
            var end = Math.Min(d.Length, p + len);
            long tick = 0;
            byte status = 0;
            int VarLen() { var v = 0; byte b; do { b = d[p++]; v = (v << 7) | (b & 0x7F); } while ((b & 0x80) != 0 && p < end); return v; }
            while (isTrack && p < end)
            {
                tick += VarLen();
                if (p >= end) break;
                if ((d[p] & 0x80) != 0) status = d[p++];
                if (status == 0xFF)
                {
                    var type = d[p++];
                    var l = VarLen();
                    if (type == 0x51 && l == 3) tempos.Add((tick, d[p] << 16 | d[p + 1] << 8 | d[p + 2]));
                    p += l;
                }
                else if (status is 0xF0 or 0xF7) p += VarLen();
                else p += (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
            }
            pos = 8 + pos + len;
        }
        tempos.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        if (tempos.Count == 0 || tempos[0].Item1 > 0) tempos.Insert(0, (0, 500000));
        return (ppq, tempos);
    }

    public static double TicksAt(double seconds, int ppq, List<(long Tick, int Us)> tempos)
    {
        double t = 0;
        for (var i = 0; i < tempos.Count; i++)
        {
            var secPerTick = tempos[i].Us / 1e6 / ppq;
            var segEnd = i + 1 < tempos.Count ? tempos[i + 1].Tick : long.MaxValue;
            var segSeconds = segEnd == long.MaxValue ? double.MaxValue : (segEnd - tempos[i].Tick) * secPerTick;
            if (seconds - t <= segSeconds) return tempos[i].Tick + (seconds - t) / secPerTick;
            t += segSeconds;
        }
        return 0;
    }
}

/// <summary>A copy of the MIDI page's HarmonyTimeline + Together (parts of the same length), with the rub table switchable.</summary>
static class Together
{
    const int Slot = 192;
    public static readonly float[] Symmetric = [0, 1f, 0.2f, 0, 0, 0, 0.35f, 0, 0, 0, 0.2f, 1f];
    // A half step ABOVE a sounding note rubs (the textbook avoid note: b9, natural 11 on major, b13, b6 on minor);
    // a half step BELOW it is colour (maj7 under the root, 9 on minor, 13 on a dominant, #11).
    public static readonly float[] Directional = [0, 1f, 0.2f, 0, 0, 0, 0.35f, 0, 0, 0, 0.2f, 0.25f];

    static float[] Weights(IReadOnlyList<NoteEvent> notes, int count)
    {
        var w = new float[count * 12];
        foreach (var n in notes)
        {
            long from = Math.Max(0, n.Start), to = n.Start + Math.Max(1, n.Length);
            for (var k = (int)(from / Slot); k < count && (long)k * Slot < to; k++)
            {
                var overlap = Math.Min(to, (k + 1L) * Slot) - Math.Max(from, (long)k * Slot);
                if (overlap > 0) w[k * 12 + n.Key % 12] += overlap / (float)Slot;
            }
        }
        return w;
    }

    public static double Score(IReadOnlyList<NoteEvent> mine, IReadOnlyList<NoteEvent> theirs, long length, float[] rub)
    {
        var count = (int)(length / Slot);
        var w = Weights(theirs, count);
        var mw = Weights(mine, count);
        double sum = 0, heard = 0;
        for (var k = 0; k < count; k++)
        {
            var at = k * 12;
            float max = 0;
            for (var p = 0; p < 12; p++) max = Math.Max(max, w[at + p]);
            double mineW = 0;
            for (var p = 0; p < 12; p++) mineW += mw[at + p];
            if (max <= 0 || mineW <= 0) continue;
            heard += mineW;
            for (var p = 0; p < 12; p++)
            {
                if (mw[at + p] <= 0) continue;
                var share = w[at + p] / max;
                float sits;
                if (share >= 0.25f) sits = 1;
                else
                {
                    float r = 0;
                    for (var q = 0; q < 12; q++)
                    {
                        var other = w[at + q] / max;
                        if (other >= 0.25f) r = Math.Max(r, other * rub[(p - q + 12) % 12]);
                    }
                    sits = Math.Clamp(0.6f + share * 1.6f - r, -0.5f, 1);
                }
                sum += mw[at + p] * sits;
            }
        }
        return heard <= 0 ? 0 : Math.Clamp(sum / heard, 0, 1);
    }

    /// <summary>The red rule, directional: a half step above a note sounding with it, when the chord doesn't have this note.</summary>
    public static bool RubsAbove(long start, long length, int key, IReadOnlyList<NoteEvent> harmony)
    {
        long a = start, b = start + Math.Max(1, length);
        var pc = key % 12;
        foreach (var g in harmony)
        {
            if (g.Start >= b || g.Start + g.Length <= a) continue;
            if (((pc - g.Key % 12) % 12 + 12) % 12 != 1) continue;
            long from = Math.Max(a, g.Start), to = Math.Min(b, g.Start + g.Length);
            var inChord = false;
            foreach (var h in harmony)
                if (h.Key % 12 == pc && h.Start < to && h.Start + h.Length > from) { inChord = true; break; }
            if (!inChord) return true;
        }
        return false;
    }
}

/// <summary>A copy of the playground's SectionFit.Score with the proposed fixes switchable, to measure them.</summary>
static class Fit
{
    const int Beat = 96, Bar = 384;

    static double Metric(long start)
    {
        var inBar = start % Bar;
        if (inBar == 0) return 1.5;
        if (inBar == 2 * Beat) return 1.25;
        if (inBar % Beat == 0) return 1.0;
        if (inBar % (Beat / 2) == 0) return 0.75;
        return 0.6;
    }

    public static double Sit(ChordSymbol chord, int pc, bool fifth)
    {
        if (fifth && ChordSymbol.Mod(pc - chord.Root) == 7 &&
            chord.Family is not (ChordFamily.Diminished or ChordFamily.HalfDiminished or ChordFamily.Augmented) &&
            !(chord.Family == ChordFamily.Dominant && (chord.Has(6) || chord.Has(8)) && !chord.Has(7)))
            return 1;
        return ChordScales.Sit(chord, pc);
    }

    public static double Score(IReadOnlyList<NoteEvent> notes, IReadOnlyList<TimedChord> lane, long length, bool fifth, bool split, bool worst, double strong = 1)
    {
        if (notes.Count == 0 || lane.Count == 0) return 0;
        var sorted = notes.Where(n => n.Start < length).OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
        var bars = (int)((length + Bar - 1) / Bar);
        var barSum = new double[bars];
        var barW = new double[bars];
        double sum = 0, total = 0;
        for (var i = 0; i < sorted.Count; i++)
        {
            var n = sorted[i];
            // The pieces of the note: one per chord it sounds over (or just where it starts).
            var pieces = new List<(long Start, long Len, bool Struck)>();
            if (split)
            {
                var at = n.Start;
                var end = Math.Min(length, n.Start + n.Length);
                while (at < end)
                {
                    var c = lane.FirstOrDefault(x => x.Start <= at && x.End > at);
                    var next = c is null ? lane.Where(x => x.Start > at).Select(x => x.Start).DefaultIfEmpty(end).Min() : Math.Min(c.End, end);
                    if (next <= at) next = end;
                    pieces.Add((at, next - at, at == n.Start));
                    at = next;
                }
            }
            else pieces.Add((n.Start, n.Length, true));
            foreach (var (start, len, struck) in pieces)
            {
                if (ChordLane.At(lane, start + 1) is not { } chord) continue;
                var pc = n.Key % 12;
                var metric = Metric(start);
                var w = Math.Clamp(len / (double)Beat, 0.25, 2) * metric * (struck ? 1 : 0.8);
                var v = Sit(chord, pc, fifth);
                if (strong != 1 && metric >= 1.25 && v > 0 && v < 1) v *= strong;   // a tension on a strong beat counts less than a chord note
                if (struck && v < 0.55 && n.Length <= Beat / 2 && metric < 1.25)
                {
                    var nx = sorted.Skip(i + 1).FirstOrDefault(m => m.Start > n.Start);
                    if (nx.Length > 0 && Math.Abs(nx.Key - n.Key) <= 2 && ChordLane.At(lane, nx.Start + 1) is { } then && Sit(then, nx.Key % 12, fifth) >= 0.75)
                        v = Math.Max(v, 0.65);
                    else v = Math.Max(v, 0.15);
                }
                sum += w * v;
                total += w;
                var b = (int)Math.Min(bars - 1, start / Bar);
                barSum[b] += w * v;
                barW[b] += w;
            }
        }
        if (total <= 0) return 0;
        var avg = Math.Clamp(sum / total, 0, 1);
        if (!worst) return avg;
        var filled = Enumerable.Range(0, bars).Where(b => barW[b] >= 1).Select(b => Math.Clamp(barSum[b] / barW[b], 0, 1)).ToList();
        return filled.Count == 0 ? avg : 0.7 * avg + 0.3 * filled.Min();
    }
}
