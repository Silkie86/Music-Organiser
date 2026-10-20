using System.Text.RegularExpressions;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;
using static Measure;

/// <summary>
/// The same checks on a folder of whole-song MIDI files (one track per instrument), such as gospel, jazz and soul
/// construction kits: each file's melody (a lead, flute, sax or talk box track the app calls a melody) against the
/// rest of its parts, and the key written in the file or folder name ("Ebmin", "F#maj") against the app's key.
/// Files have no chord labels, so chord reading isn't checked. The first folder level names the style.
/// </summary>
static class LibraryBench
{
    const int Beat = 96, Bar = 4 * Beat;

    sealed record Piece(string Style, string Name, List<NoteEvent> Mel, List<NoteEvent> Acc, (int Tonic, bool Minor) Key, bool Labelled);

    static readonly Regex Drum = new(@"drum|kick|snare|hat|perc|clap|loop|redrum|ride|cymbal|crash|tamb|shaker|conga|bongo|tom\b|click|fx",
                                     RegexOptions.IgnoreCase);
    static readonly Regex Lead = new(@"lead|melody|flute|sax|talk ?box|vocal|vox|trumpet|whistle|solo|clarinet|harmonica|violin|topline",
                                     RegexOptions.IgnoreCase);
    static readonly Regex KeyName = new(@"(?<![A-Za-z])([A-G])(b|#)?[ _-]?(maj|min|dom|m)(?![a-z])", RegexOptions.IgnoreCase);
    static readonly Regex SharpName = new(@"(?<![A-Za-z])([A-G]) (SHARP|FLAT)", RegexOptions.IgnoreCase);

    public static void Run(string root)
    {
        var pieces = new List<Piece>();
        int files = 0, noMelody = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*.mid", SearchOption.AllDirectories).Order())
        {
            files++;
            var rel = Path.GetRelativePath(root, path);
            var style = rel.Contains(Path.DirectorySeparatorChar) ? rel[..rel.IndexOf(Path.DirectorySeparatorChar)] : "(top)";
            IReadOnlyList<MidiFilePart> parts;
            try { parts = MidiFileReader.Read(path); }
            catch { continue; }
            var tracks = parts.Where(p => !p.Drums && p.Notes.Count >= 4)
                .Select(p => (Name: p.TrackName.Length > 0 ? p.TrackName : p.Name, Notes: PhraseAnalyser.Normalise(p.Notes, p.Ppq, keepPlace: true)))
                .Where(t => !Drum.IsMatch(t.Name) && t.Notes.Count >= 4).ToList();
            // The melody: a named lead or a part the app calls a melody, that mostly plays one note at a time.
            var leads = tracks.Where(t => (Lead.IsMatch(t.Name) || PhraseAnalyser.Analyse(t.Notes, t.Name).Role == MidiRoles.Melody) && Chordal(t.Notes) < 0.25)
                              .OrderByDescending(t => Lead.IsMatch(t.Name)).ThenByDescending(t => t.Notes.Count).ToList();
            if (leads.Count == 0) { noMelody++; continue; }
            var mel = leads[0];
            var acc = tracks.Where(t => !leads.Contains(t)).SelectMany(t => t.Notes).OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
            if (acc.Count < 8) { noMelody++; continue; }
            var all = mel.Notes.Concat(acc).ToList();
            var label = Label(rel);
            var key = label ?? (MusicTheory.EstimateKey(Chroma(all).Select(x => (double)x).ToArray()) is { } k ? (k.Tonic, k.Minor) : (0, false));
            // Up to two 8-bar windows from where the melody comes in.
            var first = mel.Notes.Min(n => n.Start) / Bar * Bar;
            for (var w = 0; w < 2; w++)
            {
                var from = first + w * 8L * Bar;
                var m = Cut(mel.Notes, from, 8 * Bar);
                var a = Cut(acc, from, 8 * Bar);
                if (m.Count >= 8 && a.Count >= 8) pieces.Add(new Piece(style, rel, m, a, key, label is not null));
            }
            if (label is not null) KeyRows.Add((style, rel, label.Value, all));
            if (label is not null)
            {
                var parts2 = rel.Split(Path.DirectorySeparatorChar);
                var pack = parts2.Length > 2 ? parts2[1] : parts2[0];
                LabItems.Add(new KeyLab.Item(style, pack, all, label.Value));
                var cut = Cut(all, first, 8 * Bar);
                if (cut.Count >= 8) LabItems.Add(new KeyLab.Item(style + " 8 bars", pack, cut, label.Value));
            }
        }
        Console.WriteLine($"MIDI files: {files}; with a melody and parts to play it over: {files - noMelody}; 8-bar windows: {pieces.Count}");

        Keys();
        if (KeyLabToo) { KeyLab.Run("your library", LabItems, LabItems.Where(i => !i.Group.EndsWith(" 8 bars")).ToList()); return; }
        foreach (var group in pieces.GroupBy(p => p.Style).Where(g => g.Count() >= 20).OrderBy(g => g.Key))
            Fits(group.Key, group.ToList());
        Fits("ALL", pieces);
    }

    static readonly List<(string Style, string Name, (int Tonic, bool Minor) Label, List<NoteEvent> All)> KeyRows = [];
    public static bool Verbose, KeyLabToo;
    static readonly List<KeyLab.Item> LabItems = [];

    /// <summary>The share of notes that start with another note (a chord part is near 1, a tune near 0).</summary>
    static double Chordal(List<NoteEvent> notes)
    {
        var together = 0;
        for (var i = 0; i < notes.Count; i++)
            if ((i > 0 && notes[i].Start - notes[i - 1].Start < 12) || (i + 1 < notes.Count && notes[i + 1].Start - notes[i].Start < 12)) together++;
        return together / (double)notes.Count;
    }

    /// <summary>The key written in the file or folder names, nearest the file first ("Ebmin", "F#maj", "D SHARP").</summary>
    static (int, bool)? Label(string rel)
    {
        foreach (var piece in rel.Split(Path.DirectorySeparatorChar).Reverse())
        {
            var name = Path.GetFileNameWithoutExtension(piece);
            var ms = KeyName.Matches(name);
            if (ms.Count == 1)
            {
                var m = ms[0];
                if (Note(m.Groups[1].Value, m.Groups[2].Value) is not { } t) continue;
                var q = m.Groups[3].Value;
                // "m" alone must be lower case to mean minor (an upper-case M is too often a word).
                var minor = q.Equals("min", StringComparison.OrdinalIgnoreCase) || q == "m";
                return (t, minor);
            }
            if (ms.Count > 1) return null;   // "Dbmaj-Ebmaj": changes key
            var s = SharpName.Match(name);
            if (s.Success && Note(s.Groups[1].Value, s.Groups[2].Value.ToUpperInvariant() == "SHARP" ? "#" : "b") is { } t2) return (t2, false);
        }
        return null;
    }

    static int? Note(string letter, string accidental)
    {
        int? n = char.ToUpperInvariant(letter[0]) switch { 'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11, _ => null };
        if (n is null) return null;
        return ((n.Value + (accidental == "#" ? 1 : accidental == "b" ? -1 : 0)) % 12 + 12) % 12;
    }

    static int Mod(int n) => ((n % 12) + 12) % 12;

    static string NoteName(int pc) => ((string[])["C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"])[pc];

    static void Keys()
    {
        Console.WriteLine();
        Console.WriteLine("=== KEYS: the key in the file name against the app's (whole files; MIREX: exact 1, fifth 0.5, relative 0.3, parallel 0.2) ===");
        static double Mirex(int t, bool m, int rt, bool rm)
        {
            if (t == rt && m == rm) return 1;
            if (m == rm && (Mod(t - rt) == 7 || Mod(t - rt) == 5)) return 0.5;
            if (m != rm && (rm ? Mod(rt + 3) == t : Mod(rt + 9) == t)) return 0.3;
            if (t == rt && m != rm) return 0.2;
            return 0;
        }
        foreach (var group in KeyRows.GroupBy(r => r.Style).OrderBy(g => g.Key).Append(KeyRows.GroupBy(_ => "ALL").Single()))
        {
            double profile = 0, page = 0, profileExact = 0, pageExact = 0;
            var n = 0;
            foreach (var (_, name, (rt, rm), all) in group)
            {
                var chroma = new double[12];
                foreach (var note in all) chroma[note.Key % 12] += Math.Clamp(note.Length / (double)Bar, 1 / 16.0, 2);
                var p = MusicTheory.EstimateKey(chroma);
                var g = PhraseAnalyser.Analyse(all).Key;
                var a = p is { } pk ? Mirex(pk.Tonic, pk.Minor, rt, rm) : 0;
                var b = g is { } gk ? Mirex(gk.Tonic, gk.Minor, rt, rm) : 0;
                profile += a; page += b;
                if (a == 1) profileExact++;
                if (b == 1) pageExact++;
                n++;
                if (Verbose && group.Key != "ALL" && b < 1)
                    Console.WriteLine($"    {name}: label {NoteName(rt)}{(rm ? "m" : "")}, MIDI page {(g is { } x ? NoteName(x.Tonic) + (x.Minor ? "m" : "") : "-")}, Projects {(p is { } y ? NoteName(y.Tonic) + (y.Minor ? "m" : "") : "-")}");
            }
            Console.WriteLine($"{group.Key,-12} files {n,4} | Projects page key: exact {100 * profileExact / n,5:0.0}%  score {100 * profile / n,5:0.0} | MIDI page key: exact {100 * pageExact / n,5:0.0}%  score {100 * page / n,5:0.0}");
        }
    }

    static void Fits(string style, List<Piece> cases)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {style}: {cases.Count} windows from {cases.Select(c => c.Name).Distinct().Count()} files ===");
        var rng = new Random(909);
        var negOther = new List<List<NoteEvent>>();
        for (var i = 0; i < cases.Count; i++)
        {
            var a = cases[i];
            var pool = Enumerable.Range(0, cases.Count).Where(j => cases[j].Name != a.Name && cases[j].Key.Minor == a.Key.Minor).ToList();
            if (pool.Count == 0) pool = Enumerable.Range(0, cases.Count).Where(j => cases[j].Name != a.Name).ToList();
            var b = cases[pool[rng.Next(pool.Count)]];
            var d = Mod(a.Key.Tonic - b.Key.Tonic);
            if (d > 6) d -= 12;
            var moved = b.Mel.Select(n => n with { Key = n.Key + d }).ToList();
            var oct = (int)Math.Round((HarmonyMatcher.Centre(a.Mel) - HarmonyMatcher.Centre(moved)) / 12);
            negOther.Add(moved.Select(n => n with { Key = Math.Clamp(n.Key + 12 * oct, 0, 127) }).ToList());
        }
        const long len = 8 * Bar;
        var lanes = cases.Select(c => ChordLane.Detect(c.Acc, len)).ToList();
        var parts = new Dictionary<(int, bool), (double Score, double Together, double Move)>();
        (double Score, double Together, double Move) Page(int i, List<NoteEvent> m, bool right) =>
            parts.TryGetValue((i, right), out var p) ? p : parts[(i, right)] = PageParts(m, cases[i].Acc);
        var scorers = new (string Name, Func<int, List<NoteEvent>, bool, double> Score)[]
        {
            ("MIDI page list %, before (0.4 first look + 0.6 together)", (i, m, r) => { var p = Page(i, m, r); return 0.4 * p.Move + 0.6 * p.Together; }),
            ("MIDI page list %, now (together)", (i, m, r) => Page(i, m, r).Score),
            ("MIDI page 'plays together', before (half step either way)", (i, m, r) => Together.Score(m, cases[i].Acc, len, Together.Symmetric)),
            ("MIDI page 'plays together', now", (i, m, r) => HarmonyMatcher.SitsTogether(m, cases[i].Acc) ?? 0),
            ("Playground fit, before", (i, m, r) => Fit.Score(m, lanes[i], len, fifth: false, split: false, worst: false)),
            ("Playground fit, now (jazz/gospel ear)", (i, m, r) => SectionFit.Score(m, lanes[i], len, MidiRoles.Melody) ?? 0),
            ("Playground fit, now (classical ear)", (i, m, r) => SectionFit.Score(m, lanes[i], len, MidiRoles.Melody, HarmonyStyle.Classical) ?? 0),
        };
        Console.WriteLine("score | right melody avg | another song's melody in the same key: avg, times in 100 the right one wins");
        foreach (var (name, score) in scorers)
        {
            var pos = cases.Select((c, i) => score(i, c.Mel, true)).ToList();
            var other = negOther.Select((m, i) => score(i, m, false)).ToList();
            Console.WriteLine($"{name,-58} {100 * pos.Average(),5:0.0}% | {100 * other.Average(),5:0.0}%  {100 * Auc(pos, other),5:0.0}");
        }

        var rules = new (string Name, Func<NoteEvent, List<NoteEvent>, IReadOnlyList<TimedChord>, bool> Red)[]
        {
            ("MIDI page red, before (half step either way)", (n, acc, lane) => OldRubs(n, acc)),
            ("MIDI page red, now", (n, acc, lane) => HarmonyMatcher.Rubs(n.Start, n.Length, n.Key, acc, lane)),
            ("Playground red, before", (n, acc, lane) => n.Length > Beat / 2 && ChordLane.At(lane, n.Start + 1) is { } ch && Fit.Sit(ch, n.Key % 12, fifth: false) < 0.05),
            ("Playground red, now", (n, acc, lane) => ChordLane.Fights(lane, n.Start, n.Length, n.Key % 12)),
        };
        Console.WriteLine("red marks | on the right melodies (wrong alarms) | on another song's melody | ratio");
        foreach (var (name, red) in rules)
        {
            double rightRed = 0, rightN = 0, wrongRed = 0, wrongN = 0;
            for (var i = 0; i < cases.Count; i++)
            {
                foreach (var n in cases[i].Mel) { rightN++; if (red(n, cases[i].Acc, lanes[i])) rightRed++; }
                foreach (var n in negOther[i]) { wrongN++; if (red(n, cases[i].Acc, lanes[i])) wrongRed++; }
            }
            Console.WriteLine($"{name,-58} {100 * rightRed / rightN,5:0.0}% | {100 * wrongRed / wrongN,5:0.0}% | x{wrongRed / wrongN / Math.Max(1e-9, rightRed / rightN):0.0}");
        }
    }

    /// <summary>The MIDI page's red rule before 2026-10-20: a semitone either way from a note sounding with it, any length.</summary>
    static bool OldRubs(NoteEvent n, List<NoteEvent> harmony)
    {
        long a = n.Start, b = n.Start + Math.Max(1, n.Length);
        var pc = n.Key % 12;
        foreach (var g in harmony)
        {
            if (g.Start >= b || g.Start + g.Length <= a) continue;
            if (Mod(pc - g.Key % 12) is not (1 or 11)) continue;
            long from = Math.Max(a, g.Start), to = Math.Min(b, g.Start + g.Length);
            if (!harmony.Any(h => h.Key % 12 == pc && h.Start < to && h.Start + h.Length > from)) return true;
        }
        return false;
    }
}
