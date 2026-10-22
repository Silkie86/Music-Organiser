using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Parsers;

/// <summary>
/// A chord reader to try ideas on (--chords): the app's reader (ChordReader) with a knob for each of its ideas, so one
/// can be turned off or changed and measured. Its defaults are what the app does (2026-10-20); --knobs starts each
/// variant from them.
/// </summary>
static class ChordLab
{
    const int Beat = 96, Bar = 4 * Beat;

    public sealed record Knobs
    {
        public string Name { get; init; } = "";
        /// <summary>A note struck in the last eighth before a beat and held an eighth past it counts from the beat.</summary>
        public bool Push { get; init; } = true;
        public int Slot { get; init; } = Beat;
        public int MaxSlots { get; init; } = 8;
        /// <summary>Share of the span's strongest note a note needs to count in its chord.</summary>
        public double Theta { get; init; } = 0.001;
        /// <summary>Above 0: the chord is named from notes this share of the strongest or more (same root and bass).</summary>
        public double NameTheta { get; init; } = 0.3;
        /// <summary>Name it by spelling the stronger notes afresh (the root may change) rather than keeping the root.</summary>
        public bool NameFree { get; init; } = false;
        /// <summary>Name it from the stronger notes plus the root found; keep the found chord when they'd give another root.</summary>
        public bool NameRooted { get; init; } = true;
        public double Tension { get; init; } = 0.5;
        public double Out { get; init; } = -1;
        public double MissRoot { get; init; } = 0;
        public double MissThird { get; init; } = 0;
        /// <summary>A chord note (root, third, fifth, seventh) of the span's chord not heard in a beat (or the one before), per note, × the beat's playing / chord notes.</summary>
        public double MissCore { get; init; } = 3;
        public double MissTension { get; init; } = 0.5;
        public bool MissShare { get; init; } = true;
        public bool Fullness { get; init; } = true;
        public bool RingThin { get; init; } = true;
        public bool LowestHint { get; init; } = true;
        public bool DimByNext { get; init; } = true;
        /// <summary>Change costs scale with the playing around the change rather than the part's average.</summary>
        public bool LocalBusy { get; init; } = false;
        /// <summary>A change where the notes above the bass are held over (not struck again) costs up to this much more.</summary>
        public double HeldCost { get; init; } = 4;
        /// <summary>How much a bass approach note (beat 2 or 4, a half step from the next bass note) counts.</summary>
        public double Approach { get; init; } = 0.25;
        /// <summary>A new bass note struck after the span's first beat (not the span's bass), × the beat's playing / chord notes.</summary>
        public double BassMove { get; init; } = 1;
        /// <summary>A pushed bass note's length is its whole length, the part before the beat too.</summary>
        public bool BassLenOrig { get; init; } = true;
        /// <summary>A new bass struck with three or more notes above it, some not sounding at the span's start, moves in full.</summary>
        public bool BassStrike { get; init; } = false;
        /// <summary>Each span's chord is spelled with how strongly each note sounds; off, from which notes sound only.</summary>
        public bool SpanWeights { get; init; } = true;
        /// <summary>A pitch class sounds as long as its longest note in the beat: doubling a note (an octave bass) doesn't make it count more.</summary>
        public bool PcMax { get; init; } = true;
        /// <summary>Beats of ringing: a note heard this many beats back in the span still counts as heard.</summary>
        public int Ring { get; init; } = 0;
        /// <summary>Change costs, in beats of typical playing.</summary>
        public double ChangeBar { get; init; } = 0.1;
        public double ChangeHalf { get; init; } = 0.3;
        public double ChangeBeat { get; init; } = 0.6;
        public double ChangeOff { get; init; } = 1.2;
        /// <summary>The lowest note is a bass when it's this low or this far below the next note up; otherwise there's no bass.</summary>
        public bool BassRule { get; init; } = true;
        public int BassBelow { get; init; } = 53;
        public int BassGap { get; init; } = 7;
        public double NullOut { get; init; } = -0.5;
        /// <summary>This many empty slots or more end a chord (a rest shorter than this keeps the chord going).</summary>
        public int GapSlots { get; init; } = 8;
    }

    public static readonly Knobs Default = new() { Name = "lab (the app's reader)" };

    /// <summary>The readers compared: the two half-bar readers before (Before.cs), the app's one chord reader as it is, and the
    /// lab's with its knobs (--knobs).</summary>
    public static List<(string Name, Func<IReadOnlyList<NoteEvent>, long, List<TimedChord>> Read)> Readers()
    {
        var list = new List<(string, Func<IReadOnlyList<NoteEvent>, long, List<TimedChord>>)>
        {
            ("MIDI page before (half bars)", Before.PageLane),
            ("Playground before (half bars)", (n, l) => Before.Detect(n, l)),
            ("App now (one chord reader)", (n, l) => ChordReader.Read(n, l)),
        };
        foreach (var k in Variants) list.Add((k.Name, (n, l) => Read(n, l, k)));
        return list;
    }

    public static List<Knobs> Variants = [Default];

    /// <summary>Variants from the command line: "name:Knob=value,Knob=value;name2:..." (each starts from the default).</summary>
    public static void Parse(string text)
    {
        Variants = [];
        foreach (var v in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = v.IndexOf(':');
            var k = Default with { Name = colon > 0 ? v[..colon] : v };
            if (colon > 0)
                foreach (var kv in v[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = kv.IndexOf('=');
                    var prop = typeof(Knobs).GetProperty(kv[..eq].Trim()) ?? throw new ArgumentException(kv);
                    var raw = kv[(eq + 1)..].Trim();
                    object val = prop.PropertyType == typeof(bool) ? (object)bool.Parse(raw) : prop.PropertyType == typeof(int) ? (object)int.Parse(raw)
                               : (object)double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
                    k = k with { };
                    prop.SetValue(k, val);
                }
            Variants.Add(k);
        }
    }

    public static List<NoteEvent> Pushed(IReadOnlyList<NoteEvent> notes) => notes.Select(Push).ToList();

    static NoteEvent Push(NoteEvent n)
    {
        var inBeat = n.Start % Beat;
        var next = n.Start - inBeat + Beat;
        return inBeat >= Beat / 2 && n.Start + n.Length >= next + Beat / 2 ? n with { Start = next, Length = n.Start + n.Length - next } : n;
    }

    public static List<TimedChord> Read(IReadOnlyList<NoteEvent> input, long length, Knobs k)
    {
        var lane = new List<TimedChord>();
        if (input.Count == 0 || length <= 0) return lane;
        var notes = input.Where(n => !n.Muted).Select(n => (N: k.Push ? Push(n) : n, Len: n.Length)).OrderBy(x => x.N.Start).ToList();
        var slot = k.Slot;
        var count = (int)((length + slot - 1) / slot);
        var w = new double[count, 12];
        var low = new int[count];
        var bassOk = new bool[count];
        var active = new double[count];
        for (var b = 0; b < count; b++) low[b] = int.MaxValue;
        var struckLow = new bool[count];
        var bassLen = new long[count];
        var held = new double[count];
        // Each slot's notes: key, how long it sounds in the slot, whether it's struck there, and how long it is.
        var inSlot = new List<(int Key, long Overlap, bool Struck, long Length)>[count];
        foreach (var (n, origLen) in notes)
        {
            if (n.Start >= length) break;
            var end = Math.Min(n.Start + n.Length, length);
            for (var b = (int)(n.Start / slot); b < count && (long)b * slot < end; b++)
            {
                long from = (long)b * slot, to = from + slot;
                var overlap = Math.Min(end, to) - Math.Max(n.Start, from);
                if (overlap < slot / 8) continue;
                (inSlot[b] ??= []).Add((n.Key, overlap, n.Start >= from && n.Start < to, k.BassLenOrig ? origLen : end - n.Start));
            }
        }
        var upperSound = new int[count];
        var struckUpper = new int[count];
        for (var b = 0; b < count; b++)
        {
            if (inSlot[b] is not { Count: > 0 } ns) continue;
            var keys = ns.Where(x => x.Overlap >= slot / 4).Select(x => x.Key).Distinct().Order().ToList();
            if (keys.Count > 0)
            {
                low[b] = keys[0];
                var bassNote = ns.Where(x => x.Key == keys[0]).MaxBy(x => x.Overlap);
                struckLow[b] = bassNote.Struck;
                bassLen[b] = bassNote.Length;
                bassOk[b] = !k.BassRule || keys[0] < k.BassBelow || keys.Count == 1 || keys[1] - keys[0] >= k.BassGap;
            }
            // How much of what's above the bass is held over from before rather than struck here.
            double upper = 0, struck = 0;
            foreach (var x in ns)
            {
                if (x.Key == low[b]) continue;
                upper += x.Overlap;
                if (x.Struck) struck += x.Overlap;
                if (x.Overlap >= slot / 4) upperSound[b] |= 1 << x.Key % 12;
                if (x.Struck) struckUpper[b] |= 1 << x.Key % 12;
            }
            held[b] = upper > 0 ? 1 - struck / upper : 0;
        }
        // A bass note on beat 2 or 4, a beat or shorter, a half step from the bass struck next: an approach note.
        var approach = new bool[count];
        if (k.Approach < 1 && slot == Beat)
            for (var b = 0; b + 1 < count; b++)
                if (b % 4 is 1 or 3 && struckLow[b] && bassOk[b] && bassLen[b] <= Beat && low[b + 1] != int.MaxValue && struckLow[b + 1] &&
                    Math.Abs(low[b + 1] - low[b]) == 1)
                    approach[b] = true;
        for (var b = 0; b < count; b++)
        {
            if (inSlot[b] is not { } ns) continue;
            foreach (var x in ns)
            {
                var weight = approach[b] && x.Key == low[b] ? k.Approach : 1;
                w[b, x.Key % 12] = k.PcMax ? Math.Max(w[b, x.Key % 12], x.Overlap * weight) : w[b, x.Key % 12] + x.Overlap * weight;
            }
            for (var p = 0; p < 12; p++) active[b] += w[b, p];
        }
        var busy = Enumerable.Range(0, count).Where(b => active[b] > 0).Select(b => active[b]).DefaultIfEmpty(1).Average();

        // Runs of slots with music, split where GapSlots or more go by empty.
        var runs = new List<(int From, int To)>();
        int? start = null;
        var lastBusy = -1;
        for (var b = 0; b < count; b++)
        {
            if (active[b] <= 0) continue;
            if (start is null) start = b;
            else if (b - lastBusy > k.GapSlots) { runs.Add((start.Value, lastBusy + 1)); start = b; }
            lastBusy = b;
        }
        if (start is not null) runs.Add((start.Value, lastBusy + 1));

        var W = new double[12];
        var full = new double[count];
        var wf = new float[12];
        var values = new double[12];
        foreach (var (from, to) in runs)
        {
            var n = to - from;
            var best = new double[n + 1];
            var cut = new int[n + 1];
            var chordOf = new ChordSymbol?[n + 1];
            for (var e = 1; e <= n; e++)
            {
                best[e] = double.NegativeInfinity;
                Array.Clear(W);
                for (var a = e - 1; a >= Math.Max(0, e - k.MaxSlots); a--)
                {
                    for (var p = 0; p < 12; p++) W[p] += w[from + a, p];
                    var (score, chord) = Span(from + a, from + e);
                    var near = k.LocalBusy ? Math.Max(1, (active[from + a - (a > 0 ? 1 : 0)] + active[from + a]) / 2) : busy;
                    var change = a == 0 ? 0 : near * ChangeCost((from + a) * (long)slot) * (1 + k.HeldCost * held[from + a]);
                    var total = best[a] + score - change;
                    if (total > best[e]) { best[e] = total; cut[e] = a; chordOf[e] = chord; }
                }
            }
            var spans = new List<(int A, int E, ChordSymbol? C)>();
            for (var e = n; e > 0; e = cut[e]) spans.Add((cut[e], e, chordOf[e]));
            spans.Reverse();
            var named = spans.Select(x => x.C is { } f ? k.NameTheta > 0 ? Named(x.A + from, x.E + from, f) : f : (ChordSymbol?)null).ToList();
            // A diminished seventh is the same four notes from four roots: with no bass to say, it's named from the note a
            // half step below the chord it leads to (C Dm7 Em7 F#dim7 G7), the way a passing diminished chord is written.
            if (k.DimByNext)
                for (var i = 0; i + 1 < named.Count; i++)
                {
                    if (named[i] is not { Family: ChordFamily.Diminished } d || !d.Has(9) || named[i + 1] is not { } next) continue;
                    if (BassOf(from + spans[i].A, from + spans[i].E).Bass >= 0) continue;
                    for (var r = d.Root; r < d.Root + 12; r += 3)
                        if (ChordSymbol.Mod(next.Root - r) == 1) { named[i] = new ChordSymbol(r % 12, d.Mask, r % 12, ChordFamily.Diminished); break; }
                }
            for (var si = 0; si < spans.Count; si++)
            {
                var (a, e, _) = spans[si];
                if (named[si] is not { } chord) continue;
                long s = (from + a) * (long)slot, len = Math.Min(length, (from + e) * (long)slot) - s;
                if (lane.Count > 0 && lane[^1].End == s && lane[^1].Chord.Name == chord.Name)
                    lane[^1] = lane[^1] with { Length = lane[^1].Length + len };
                else lane.Add(new TimedChord(s, len, chord));
            }
        }
        return lane;

        // The chord named from its stronger notes only (passing notes left out), keeping the root and bass the reading found.
        ChordSymbol Named(int a, int e, ChordSymbol found)
        {
            var sum = new double[12];
            for (var b = a; b < e; b++) for (var p = 0; p < 12; p++) sum[p] += w[b, p];
            var max = sum.Max();
            var mask = 0;
            var weights = new float[12];
            for (var p = 0; p < 12; p++)
            {
                weights[p] = (float)(sum[p] / max);
                if (sum[p] >= k.NameTheta * max) mask |= 1 << p;
            }
            if (k.NameFree) return ChordSpeller.Spell(mask, found.Bass == found.Root && (mask >> found.Root & 1) == 0 ? -1 : found.Bass, weights) ?? found;
            if (k.NameRooted)
            {
                // The stronger notes with the root the reading found: the same chord, unless they say otherwise.
                var bassKnown = found.Bass != found.Root || (found.Mask >> found.Root & 1) != 0;
                var again = ChordSpeller.Spell(mask | (found.Mask >> found.Root & 1) << found.Root, BassOf(a, e).Bass, weights, k.LowestHint ? BassOf(a, e).Lowest : -1);
                return again is { } x && x.Root == found.Root ? x : found;
            }
            mask |= 1 << found.Bass;
            return new ChordSymbol(found.Root, mask, found.Bass, ChordSpeller.FamilyOf(ChordSymbol.Rotate(mask, -found.Root)));
        }

        (int Bass, int Lowest) BassOf(int a, int e)
        {
            for (var b = a; b < e; b++)
                if (low[b] != int.MaxValue) return bassOk[b] ? (low[b] % 12, -1) : (-1, low[b] % 12);
            return (-1, -1);
        }

        double ChangeCost(long at)
        {
            var inBar = at % Bar;
            return inBar == 0 ? k.ChangeBar : inBar == 2 * Beat ? k.ChangeHalf : inBar % Beat == 0 ? k.ChangeBeat : k.ChangeOff;
        }

        (double Score, ChordSymbol? Chord) Span(int a, int e)
        {
            var max = W.Max();
            var total = W.Sum();
            if (max <= 0) return (0, null);
            var mask = 0;
            for (var p = 0; p < 12; p++)
            {
                wf[p] = (float)(W[p] / max);
                if (W[p] >= k.Theta * max) mask |= 1 << p;
            }
            var (bass, lowest) = BassOf(a, e);
            var chord = ChordSpeller.Spell(mask, bass, k.SpanWeights ? wf : null, k.LowestHint ? lowest : -1);
            if (chord is not { } c) return (k.NullOut * total, null);
            var iv = c.Intervals;
            var third = -1;
            for (var p = 0; p < 12; p++)
            {
                if ((c.Mask >> p & 1) == 0) { values[p] = k.Out; continue; }
                var role = ChordSpeller.Role(iv, c.Family, p - c.Root);
                values[p] = role switch
                {
                    ToneRole.Root or ToneRole.Third or ToneRole.Fifth or ToneRole.Seventh => 1,
                    ToneRole.Tension => k.Tension,
                    _ => p == c.Bass ? 1 : k.Out / 2,
                };
                if (role == ToneRole.Third && third < 0) third = p;
            }
            var tones = ChordSymbol.Count(c.Mask);
            // How full each slot is: a slot playing one or two notes (an arpeggio, a line) can't hold the whole chord.
            if (k.Fullness)
            {
                var solid = 0;
                for (var p = 0; p < 12; p++) if (W[p] >= 0.25 * max) solid++;
                for (var b = a; b < e; b++)
                {
                    var here = 0;
                    for (var p = 0; p < 12; p++) if (w[b, p] > 0) here++;
                    full[b] = Math.Min(1, here / (double)Math.Max(1, solid));
                }
            }
            double score = 0;
            for (var b = a; b < e; b++)
            {
                if (active[b] <= 0) continue;
                for (var p = 0; p < 12; p++) score += w[b, p] * values[p];
                var ring = k.Ring;
                if (k.RingThin && ring == 0)
                {
                    var here = 0;
                    for (var p = 0; p < 12; p++) if (w[b, p] > 0) here++;
                    if (here <= 2) ring = 1;   // one or two notes (an arpeggio, a line): the notes just before still ring
                }
                bool Heard(int pc)
                {
                    for (var x = b; x >= Math.Max(a, b - ring); x--) if (w[x, pc] > 0) return true;
                    return false;
                }
                if (k.MissRoot > 0 && !Heard(c.Root)) score -= k.MissRoot * active[b];
                if (k.MissThird > 0 && third >= 0 && !Heard(third)) score -= k.MissThird * active[b];
                if (k.MissCore > 0 || k.MissTension > 0)
                    for (var p = 0; p < 12; p++)
                    {
                        if ((c.Mask >> p & 1) == 0 || Heard(p)) continue;
                        // Share mode: a note is missed in proportion to how much of the span it fills (a passing note hardly at all).
                        var expected = k.MissShare ? W[p] / total : 1.0 / tones;
                        score -= (values[p] >= 1 ? k.MissCore : k.MissTension) * active[b] * expected * (k.Fullness ? full[b] : 1);
                    }
                if (k.BassMove > 0 && b > a && bassOk[b] && low[b] != int.MaxValue && low[b] % 12 != c.Bass && struckLow[b] && !approach[b])
                {
                    var f = Math.Min(1, bassLen[b] / (2.0 * Beat));
                    if (k.BassStrike && System.Numerics.BitOperations.PopCount((uint)struckUpper[b]) >= 3 && (struckUpper[b] & ~upperSound[a]) != 0) f = 1;
                    score -= k.BassMove * busy * f;
                }
            }
            return (score, c);
        }
    }

    /// <summary>
    /// --chords: chord readers side by side on POP909's chord labels (made by software, so rough) and on what sits on top
    /// of them: the Playground's fit and red marks.
    /// </summary>
    public static void Pop(List<Song> songs, IReadOnlyList<(string Name, Func<IReadOnlyList<NoteEvent>, long, List<TimedChord>> Read)> readers)
    {
        Console.WriteLine();
        Console.WriteLine("=== CHORD READERS on POP909's chord labels (accompaniment part; time-weighted, 16th-note samples; N skipped) ===");
        Console.WriteLine("reader | root | root+maj/min | bass | no chord | segments: over, under, score | changes per bar (labels " +
                          $"{songs.Sum(s => s.Chords.Count(c => c.Root >= 0)) / (double)songs.Sum(s => s.Length / Bar):0.00}) | ms per song");
        foreach (var (name, read) in readers)
        {
            double total = 0, root = 0, mmTotal = 0, mm = 0, bass = 0, none = 0, overSum = 0, underSum = 0, segTotal = 0, changes = 0, bars = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lanes = songs.Select(s => read(s.Piano, s.Length)).ToList();
            var ms = sw.Elapsed.TotalMilliseconds / songs.Count;
            for (var si = 0; si < songs.Count; si++)
            {
                var s = songs[si];
                var lane = lanes[si];
                ChordSymbol? At(long t)
                {
                    if (ChordLane.At(lane, t) is { } c) return c;
                    return lane.LastOrDefault(x => x.Start <= t)?.Chord;
                }
                foreach (var c in s.Chords.Where(c => c.Root >= 0))
                    for (var t = c.Start; t < c.End; t += 24)
                    {
                        total++;
                        if (At(t) is not { } g) { none++; continue; }
                        if (g.Root == c.Root) root++;
                        if (c.Cls is "maj" or "min")
                        {
                            mmTotal++;
                            if (g.Root == c.Root && Cls(g) == c.Cls) mm++;
                        }
                        if (g.Bass == c.Bass) bass++;
                    }
                // Segmentation (directional Hamming distance, as MIREX): how much of each labelled chord the best-matching
                // read chord covers (over-cutting), and the other way round (under-cutting).
                var refs = s.Chords.Where(c => c.Root >= 0).Select(c => (c.Start, c.End)).ToList();
                var est = lane.Select(c => (c.Start, c.End)).ToList();
                var inRef = refs.Sum(r => r.End - r.Start);
                overSum += refs.Sum(r => r.End - r.Start - est.Select(x => Overlap(r, x)).DefaultIfEmpty(0).Max());
                underSum += est.Sum(x => Math.Max(0, Clip(x, refs) - refs.Select(r => Overlap(r, x)).DefaultIfEmpty(0).Max()));
                segTotal += inRef;
                changes += lane.Count;
                bars += s.Length / (double)Bar;
            }
            double over = overSum / segTotal, under = underSum / segTotal;
            Console.WriteLine($"{name,-44} {100 * root / total,5:0.0}% {100 * mm / mmTotal,5:0.0}% {100 * bass / total,5:0.0}% {100 * none / total,4:0.0}% | " +
                              $"{100 * over,4:0.0} {100 * under,4:0.0} {100 * (1 - Math.Max(over, under)),5:0.0} | {changes / bars,5:0.00} | {ms,6:0.00}");
        }

        static long Overlap((long Start, long End) a, (long Start, long End) b) => Math.Max(0, Math.Min(a.End, b.End) - Math.Max(a.Start, b.Start));
        // The part of an estimated span inside labelled chords.
        static long Clip((long Start, long End) x, List<(long Start, long End)> refs) => refs.Sum(r => Overlap(r, x));
    }

    /// <summary>Some songs bar by bar: the labels, then each reader's chords.</summary>
    public static void Show(List<Song> songs, IReadOnlyList<(string Name, Func<IReadOnlyList<NoteEvent>, long, List<TimedChord>> Read)> readers, int count = 3, int bars = 12)
    {
        foreach (var s in songs.Take(count))
        {
            Console.WriteLine();
            Console.WriteLine($"--- song {s.Id}, first {bars} bars ---");
            var refs = s.Chords.Where(c => c.Start < bars * Bar).Select(c => $"[{c.Start / (double)Bar + 1:0.##}] {c.Label}");
            Console.WriteLine($"  {"labels",-30} {string.Join(" ", refs)}");
            foreach (var (name, read) in readers)
            {
                var lane = read(s.Piano, s.Length).Where(c => c.Start < bars * Bar);
                Console.WriteLine($"  {name,-30} {string.Join(" ", lane.Select(c => $"[{c.Start / (double)Bar + 1:0.##}] {c.Chord.Name}"))}");
            }
        }
    }

    public static string Cls(ChordSymbol c) => c.Family switch
    {
        ChordFamily.Major or ChordFamily.Dominant when c.Has(4) => "maj",
        ChordFamily.Augmented => "aug",
        ChordFamily.Minor => "min",
        ChordFamily.HalfDiminished or ChordFamily.Diminished => "dim",
        ChordFamily.Sus => "sus",
        _ => "other",
    };

    /// <summary>The hand-made cases from the 2026-10-20 check-up: what each reader names them.</summary>
    public static void Cases(IReadOnlyList<(string Name, Func<IReadOnlyList<NoteEvent>, long, List<TimedChord>> Read)> readers)
    {
        static NoteEvent N(long s, long l, int k) => new(s, l, k, 90);
        string Names(IEnumerable<TimedChord> lane) => string.Join(" ", lane.Select(c => $"[{c.Start / (double)Bar + 1:0.##}] {c.Chord.Name}"));
        var cases = new List<(string, List<NoteEvent>, long)>();
        foreach (var (label, keys) in new (string, int[])[]
        {
            ("Bm7b5   B2 D4 F4 A4", [47, 62, 65, 69]), ("G13     G2 F3 B3 E4", [43, 53, 59, 64]), ("C7alt   C3 E3 Bb3 Db4 Ab4", [48, 52, 58, 61, 68]),
            ("F/G     G2 F3 A3 C4", [43, 53, 57, 60]), ("G7b9    G2 F3 B3 D4 Ab4", [43, 53, 59, 62, 68]), ("G7#9    G2 F3 B3 D4 Bb4", [43, 53, 59, 62, 70]),
            ("G7sus4  G2 C4 D4 F4", [43, 60, 62, 65]), ("Bdim7   B2 D4 F4 Ab4", [47, 62, 65, 68]), ("C/E     E2 G3 C4", [40, 55, 60]),
            ("Ebmaj7  Eb3 G3 Bb3 D4", [51, 55, 58, 62]), ("Bbm7    Bb2 Db4 F4 Ab4", [46, 61, 65, 68]),
            ("Cmaj7 close, no bass B3 C4 E4 G4", [59, 60, 64, 67]),
        })
            cases.Add((label, keys.Select(x => N(0, Bar, x)).ToList(), Bar));
        foreach (var (chart, label) in new[] { ("Cmaj7 | Am7 | Dm7 | G7 |", "one a bar"), ("C Dm7 Em7 F#dim7 | G7 |", "walk-up"), ("Dm7 G7 | Cmaj7 |", "two a bar") })
        {
            var lane = ChordLane.ParseChart(chart, out _)!;
            var len = (long)Math.Ceiling(lane[^1].End / (double)Bar) * Bar;
            foreach (var rhythm in new[] { CompRhythm.Held, CompRhythm.Anticipate, CompRhythm.Charleston })
                foreach (var style in new[] { VoicingStyle.Close, VoicingStyle.Gospel })
                {
                    var notes = Voicer.Voice(lane, style, rhythm, len);
                    cases.Add(($"{label} {rhythm} {style} ({chart})", notes, len));
                }
            cases.Add(($"{label} + roots bass ({chart})", Voicer.Voice(lane, VoicingStyle.Close, CompRhythm.Anticipate, len).Concat(Voicer.Bass(lane, BassStyle.Roots, len)).ToList(), len));
            cases.Add(($"{label} + walking bass ({chart})", Voicer.Voice(lane, VoicingStyle.Close, CompRhythm.Held, len).Concat(Voicer.Bass(lane, BassStyle.Walking, len)).ToList(), len));
        }
        var cf = new List<NoteEvent> { N(0, Bar, 48), N(0, Bar, 52), N(0, Bar, 55), N(Bar, Bar, 53), N(Bar, Bar, 57), N(Bar, Bar, 60) };
        var tune = new[] { 76, 77, 79, 81, 81, 79, 77, 76 }.Select((x, i) => N(i * Beat, Beat, x)).ToList();
        cases.Add(("C | F with the melody E F G A | A G F E in it", cf.Concat(tune).OrderBy(n => n.Start).ToList(), 2 * Bar));
        cases.Add(("C | F arpeggiated in eighths", new[] { 48, 52, 55, 60, 64, 60, 55, 52, 53, 57, 60, 65, 69, 65, 60, 57 }.Select((x, i) => N(i * 48, 48, x)).ToList(), 2 * Bar));
        cases.Add(("C/E | F/A, the bass doubling the third (E2 + C4 E4 G4)", new[] { new[] { 40, 60, 64, 67 }, new[] { 45, 65, 69, 72 } }
                       .SelectMany((c, bar) => c.Select(x => N(bar * Bar, Bar, x))).ToList(), 2 * Bar));
        cases.Add(("C/E | F/A, an octave bass on the third (E2 E3 + C4 E4 G4)", new[] { new[] { 40, 52, 60, 64, 67 }, new[] { 45, 57, 65, 69, 72 } }
                       .SelectMany((c, bar) => c.Select(x => N(bar * Bar, Bar, x))).ToList(), 2 * Bar));
        cases.Add(("Am | F | C | G, triads round middle C (no bass)", new[] { new[] { 57, 60, 64 }, new[] { 53, 57, 60 }, new[] { 48, 52, 55 }, new[] { 55, 59, 62 } }
                       .SelectMany((c, bar) => c.Select(x => N(bar * Bar, Bar, x))).ToList(), 4 * Bar));
        cases.Add(("C/E | F/C | G/B | Am, inversions an octave up (no bass)", new[] { new[] { 64, 67, 72 }, new[] { 60, 65, 69 }, new[] { 59, 62, 67 }, new[] { 57, 60, 64 } }
                       .SelectMany((c, bar) => c.Select(x => N(bar * Bar, Bar, x))).ToList(), 4 * Bar));
        cases.Add(("F#dim (A C F#) | G, no bass", new[] { new[] { 57, 60, 66 }, new[] { 55, 59, 62 } }
                       .SelectMany((c, bar) => c.Select(x => N(bar * Bar, Bar, x))).ToList(), 2 * Bar));
        cases.Add(("Am7 | C, close voicings, no bass", new[] { 55, 57, 60, 64 }.Select(x => N(0, Bar, x)).Concat(new[] { 55, 60, 64 }.Select(x => N(Bar, Bar, x))).ToList(), 2 * Bar));
        cases.Add(("C | C, root on 1 then fifth on 3 in the bass", new[] { N(0, 2 * Beat, 36), N(2 * Beat, 2 * Beat, 43), N(Bar, 2 * Beat, 36), N(Bar + 2 * Beat, 2 * Beat, 43) }
                       .Concat(new[] { 64, 67, 72 }.SelectMany(x => new[] { N(0, Bar, x), N(Bar, Bar, x) })).ToList(), 2 * Bar));

        foreach (var (label, notes, len) in cases)
        {
            Console.WriteLine($"  {label}");
            foreach (var (name, read) in readers) Console.WriteLine($"      {name,-30} {Names(read(notes, len))}");
        }
    }
}
