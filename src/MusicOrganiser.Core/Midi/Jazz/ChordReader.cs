using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi.Jazz;

/// <summary>
/// The app's one chord reader (fix 6 of the 2026-10-20 check-up): the chords in MIDI notes as a player would write them
/// over the bars, for the MIDI page, the Playground, the index and "what chord next?". The notes are heard a beat at a
/// time; then every way of cutting the beats into chords is scored and the best cut kept (dynamic programming, the
/// HarmAn idea of Pardo and Birmingham). A chord earns points for the notes it explains and loses them for notes it
/// doesn't, and for its own notes not heard in a beat; a change of chord costs a little, least on the bar line and most
/// where the notes above the bass are held over rather than struck again. Each chord is named by
/// <see cref="ChordSpeller"/>, extensions and all.
/// <para>
/// What players do that a half-bar reader got wrong: a chord pushed an eighth early belongs to the beat it's pushed
/// into; a walking bass's passing notes don't change the chord (Dm7 isn't D7#9); a right hand on its own has no bass
/// (B C E G is Cmaj7, not Cmaj7/B); a melody in the same part is heard as passing notes, not chord tones. Measured on 387
/// POP909 songs against their chord labels: the root right 89.9% of the time and the bass 92% (the half-bar readers
/// before it: 70 and 72% roots).
/// </para>
/// </summary>
public static class ChordReader
{
    const int Beat = PhraseAnalyser.Ppq, Bar = 4 * Beat;
    /// <summary>The most beats one reading covers. A chord held longer is read again and joined up, as it names the same.</summary>
    const int MaxBeats = 8;
    /// <summary>This many empty beats or more end a chord; a shorter rest keeps it going.</summary>
    const int GapBeats = 8;
    /// <summary>The lowest note is a bass when it's below F3, alone, or a fifth or more below the next note up.</summary>
    const int BassBelow = 53, BassGap = 7;
    /// <summary>The most beats read at all (about nine hours at 120 bpm): a note left held by mistake can't fill memory.</summary>
    const long MostBeats = 1 << 16;

    /// <summary>The chords in these notes (ticks at <see cref="PhraseAnalyser.Ppq"/>), the same chord running on as one.</summary>
    /// <param name="cuts">Where a chord must end and the next start (ticks, on a beat), such as where a chord clip begins.</param>
    /// <param name="downbeat">
    /// A tick a bar starts on, when that isn't tick 0 (a stretch cut from the middle of a bar): the beats, the bar lines
    /// chords change on and the walking bass's beats 2 and 4 are counted from there.
    /// </param>
    public static List<TimedChord> Read(IReadOnlyList<NoteEvent> notes, long length, IEnumerable<long>? cuts = null, long downbeat = 0)
    {
        var lane = new List<TimedChord>();
        if (notes.Count == 0 || length <= 0) return lane;
        // Read as if a bar started at tick 0, then moved back.
        var pad = ((-downbeat % Bar) + Bar) % Bar;
        if (pad != 0)
        {
            notes = notes.Select(n => n with { Start = n.Start + pad }).ToList();
            cuts = cuts?.Select(c => c + pad).ToList();
            length += pad;
        }
        length = Math.Min(length, MostBeats * Beat);
        var heard = new Heard(notes, length);
        var at = cuts?.Where(c => c > 0 && c < length).Select(c => (int)(c / Beat)).ToHashSet() ?? [];
        foreach (var (from, to) in heard.Runs(at)) heard.Cut(from, to, lane);
        if (pad == 0) return lane;
        for (var i = 0; i < lane.Count; i++)
        {
            var start = Math.Max(0, lane[i].Start - pad);
            lane[i] = lane[i] with { Start = start, Length = lane[i].Start + lane[i].Length - pad - start };
        }
        lane.RemoveAll(c => c.Length <= 0);
        return lane;
    }

    /// <summary>
    /// A note struck in the last eighth before a beat and held an eighth past it is pushed: it belongs to that beat
    /// (an anticipation, as in "Cmaj7 | Am7" with the Am7 struck on the and of 4).
    /// </summary>
    public static NoteEvent Unpush(NoteEvent n)
    {
        var inBeat = n.Start % Beat;
        var next = n.Start - inBeat + Beat;
        return inBeat >= Beat / 2 && n.Start + n.Length >= next + Beat / 2 ? n with { Start = next, Length = n.Start + n.Length - next } : n;
    }

    /// <summary>What sounds in each beat, and the cutting of the beats into chords.</summary>
    sealed class Heard
    {
        readonly int _count;
        readonly long _length;
        /// <summary>
        /// How long each pitch class sounds in each beat: its longest note there, so an octave or a doubled note counts once
        /// (E2 E3 under C4 E4 G4 is C/E, not Em); a walking bass's approach notes count a quarter.
        /// </summary>
        readonly double[,] _w;
        readonly double[] _active;
        /// <summary>How many pitch classes sound in each beat.</summary>
        readonly int[] _here;
        /// <summary>The lowest note held a quarter of the beat or more (int.MaxValue when none), and whether it's a bass.</summary>
        readonly int[] _low;
        readonly bool[] _bassOk, _struckLow;
        /// <summary>How long that lowest note is, all of it (a pushed note's part before the beat too).</summary>
        readonly long[] _bassLen;
        /// <summary>The share of the notes above the bass held over from before rather than struck in the beat.</summary>
        readonly double[] _held;
        /// <summary>A bass note on beat 2 or 4, a beat or shorter, a half step from the bass struck next: an approach note.</summary>
        readonly bool[] _approach;
        /// <summary>How much plays in a beat, on average: what a change of chord costs is measured in it.</summary>
        readonly double _busy;
        readonly float[] _weights = new float[12];
        /// <summary>
        /// The chords spelled so far, by notes, bass and how strongly each note sounds (to a thousandth): a held chord or a
        /// repeated pattern is spelled once.
        /// </summary>
        readonly Dictionary<(long, long, int), ChordSymbol?> _spelled = [];

        public Heard(IReadOnlyList<NoteEvent> input, long length)
        {
            _length = length;
            _count = (int)((length + Beat - 1) / Beat);
            _w = new double[_count, 12];
            _active = new double[_count];
            _here = new int[_count];
            _low = new int[_count];
            _bassOk = new bool[_count];
            _struckLow = new bool[_count];
            _bassLen = new long[_count];
            _held = new double[_count];
            _approach = new bool[_count];
            Array.Fill(_low, int.MaxValue);

            // A note that starts before tick 0 sounds from it.
            var notes = input.Where(n => !n.Muted && n.Start + n.Length > 0)
                             .Select(n => n.Start < 0 ? n with { Start = 0, Length = n.Start + n.Length } : n)
                             .Select(n => (Note: Unpush(n), n.Length)).OrderBy(x => x.Note.Start).ToList();
            var inBeat = new List<(int Key, long Overlap, bool Struck, long Length)>?[_count];
            foreach (var (n, whole) in notes)
            {
                if (n.Start >= length) break;
                var end = Math.Min(n.Start + n.Length, length);
                for (var b = (int)(n.Start / Beat); b < _count && (long)b * Beat < end; b++)
                {
                    long from = (long)b * Beat, to = from + Beat;
                    var overlap = Math.Min(end, to) - Math.Max(n.Start, from);
                    if (overlap < Beat / 8) continue;   // a grace note, or the tail of the note before
                    (inBeat[b] ??= []).Add((n.Key, overlap, n.Start >= from && n.Start < to, whole));
                }
            }
            for (var b = 0; b < _count; b++)
            {
                if (inBeat[b] is not { } ns) continue;
                // The two lowest notes held a quarter of the beat or more.
                int lowest = int.MaxValue, next = int.MaxValue;
                foreach (var x in ns)
                {
                    if (x.Overlap < Beat / 4) continue;
                    if (x.Key < lowest) { next = lowest; lowest = x.Key; }
                    else if (x.Key > lowest && x.Key < next) next = x.Key;
                }
                if (lowest != int.MaxValue)
                {
                    _low[b] = lowest;
                    var bass = ns.Where(x => x.Key == lowest).MaxBy(x => x.Overlap);
                    _struckLow[b] = bass.Struck;
                    _bassLen[b] = bass.Length;
                    // A right hand on its own has no bass: B3 C4 E4 G4 is Cmaj7, not Cmaj7/B.
                    _bassOk[b] = lowest < BassBelow || next == int.MaxValue || next - lowest >= BassGap;
                }
                double upper = 0, struck = 0;
                foreach (var x in ns)
                {
                    if (x.Key == _low[b]) continue;
                    upper += x.Overlap;
                    if (x.Struck) struck += x.Overlap;
                }
                _held[b] = upper > 0 ? 1 - struck / upper : 0;
            }
            for (var b = 0; b + 1 < _count; b++)
                _approach[b] = b % 4 is 1 or 3 && _struckLow[b] && _bassOk[b] && _bassLen[b] <= Beat && _low[b + 1] != int.MaxValue &&
                               _struckLow[b + 1] && Math.Abs(_low[b + 1] - _low[b]) == 1;
            for (var b = 0; b < _count; b++)
            {
                if (inBeat[b] is not { } ns) continue;
                foreach (var x in ns)
                {
                    var heard = x.Overlap * (_approach[b] && x.Key == _low[b] ? 0.25 : 1);
                    if (heard > _w[b, x.Key % 12]) _w[b, x.Key % 12] = heard;
                }
                for (var p = 0; p < 12; p++)
                {
                    _active[b] += _w[b, p];
                    if (_w[b, p] > 0) _here[b]++;
                }
            }
            double sum = 0;
            var playing = 0;
            for (var b = 0; b < _count; b++)
                if (_active[b] > 0) { sum += _active[b]; playing++; }
            _busy = playing == 0 ? 1 : sum / playing;
        }

        /// <summary>The stretches with music, split where <see cref="GapBeats"/> or more go by empty and at the cuts.</summary>
        public List<(int From, int To)> Runs(HashSet<int> cuts)
        {
            var runs = new List<(int, int)>();
            int start = -1, last = -1;
            for (var b = 0; b < _count; b++)
            {
                if (start >= 0 && cuts.Contains(b)) { runs.Add((start, last + 1)); start = -1; }
                if (_active[b] <= 0) continue;
                if (start < 0) start = b;
                else if (b - last > GapBeats) { runs.Add((start, last + 1)); start = b; }
                last = b;
            }
            if (start >= 0) runs.Add((start, last + 1));
            return runs;
        }

        /// <summary>The best cut of beats <paramref name="from"/> to <paramref name="to"/> into chords, added to the lane.</summary>
        public void Cut(int from, int to, List<TimedChord> lane)
        {
            var n = to - from;
            var best = new double[n + 1];
            var cut = new int[n + 1];
            var chordOf = new ChordSymbol?[n + 1];
            var sum = new double[12];
            for (var e = 1; e <= n; e++)
            {
                best[e] = double.NegativeInfinity;
                Array.Clear(sum);
                for (var a = e - 1; a >= Math.Max(0, e - MaxBeats); a--)
                {
                    for (var p = 0; p < 12; p++) sum[p] += _w[from + a, p];
                    var (score, chord) = Span(from + a, from + e, sum);
                    var change = a == 0 ? 0 : _busy * ChangeCost((from + a) * (long)Beat) * (1 + 4 * _held[from + a]);
                    var total = best[a] + score - change;
                    if (total > best[e]) { best[e] = total; cut[e] = a; chordOf[e] = chord; }
                }
            }
            var spans = new List<(int A, int E, ChordSymbol? Chord)>();
            for (var e = n; e > 0; e = cut[e]) spans.Add((from + cut[e], from + e, chordOf[e]));
            spans.Reverse();
            var named = spans.Select(s => s.Chord is { } found ? Named(s.A, s.E, found) : (ChordSymbol?)null).ToList();
            // A diminished seventh is the same four notes from four roots: with no bass to say, it's named from the note a
            // half step below the chord it leads to (C Dm7 Em7 F#dim7 G7), the way a passing diminished chord is written.
            for (var i = 0; i + 1 < named.Count; i++)
            {
                if (named[i] is not { Family: ChordFamily.Diminished } d || !d.Has(9) || named[i + 1] is not { } next) continue;
                if (BassOf(spans[i].A, spans[i].E).Bass >= 0) continue;
                // Only to a root that sounds, and that the same notes are a diminished chord on (an extra note can make them
                // something else from there).
                for (var r = d.Root; r < d.Root + 12; r += 3)
                    if (ChordSymbol.Mod(next.Root - r) == 1)
                    {
                        if ((d.Mask >> (r % 12) & 1) != 0 && ChordSpeller.FamilyOf(PhraseChord.Rotate(d.Mask, -r)) == ChordFamily.Diminished)
                            named[i] = new ChordSymbol(r % 12, d.Mask, r % 12, ChordFamily.Diminished);
                        break;
                    }
            }
            for (var i = 0; i < spans.Count; i++)
            {
                if (named[i] is not { } chord) continue;
                long start = spans[i].A * (long)Beat, length = Math.Min(_length, spans[i].E * (long)Beat) - start;
                if (lane.Count > 0 && lane[^1].End == start && lane[^1].Chord.Name == chord.Name)
                    lane[^1] = lane[^1] with { Length = lane[^1].Length + length };
                else lane.Add(new TimedChord(start, length, chord));
            }
        }

        static double ChangeCost(long at)
        {
            var inBar = at % Bar;
            return inBar == 0 ? 0.1 : inBar == 2 * Beat ? 0.3 : inBar % Beat == 0 ? 0.6 : 1.2;
        }

        /// <summary>
        /// The bass of beats a to e: the first beat's lowest note when it's a bass, else no bass and that note as the lowest
        /// (which leans the root a little, <see cref="ChordSpeller.Spell(int, int, float[], int)"/>).
        /// </summary>
        (int Bass, int Lowest) BassOf(int a, int e)
        {
            for (var b = a; b < e; b++)
                if (_low[b] != int.MaxValue) return _bassOk[b] ? (_low[b] % 12, -1) : (-1, _low[b] % 12);
            return (-1, -1);
        }

        /// <summary>
        /// How well one chord explains beats a to e (<paramref name="sum"/> is how long each pitch class sounds in them),
        /// and the chord: every note sounding is spelled together, and each beat scored against it.
        /// </summary>
        (double Score, ChordSymbol? Chord) Span(int a, int e, double[] sum)
        {
            double max = 0, total = 0;
            for (var p = 0; p < 12; p++) { max = Math.Max(max, sum[p]); total += sum[p]; }
            if (max <= 0) return (0, null);
            var mask = 0;
            long low6 = 0, high6 = 0;
            for (var p = 0; p < 12; p++)
            {
                var q = (long)Math.Round(sum[p] / max * 1023);
                _weights[p] = q / 1023f;
                if (sum[p] >= 0.001 * max) mask |= 1 << p;
                if (p < 6) low6 |= q << (10 * p);
                else high6 |= q << (10 * (p - 6));
            }
            var (bass, lowest) = BassOf(a, e);
            var key = (low6, high6, mask | (bass + 1) << 12 | (lowest + 1) << 17);
            if (!_spelled.TryGetValue(key, out var spelled)) _spelled[key] = spelled = ChordSpeller.Spell(mask, bass, _weights, lowest);
            if (spelled is not { } c) return (-0.5 * total, null);

            // What each pitch class is worth here: a chord note 1, a tension a half, a note outside the chord -1.
            Span<double> value = stackalloc double[12];
            var iv = c.Intervals;
            for (var p = 0; p < 12; p++)
            {
                if ((c.Mask >> p & 1) == 0) { value[p] = -1; continue; }
                value[p] = ChordSpeller.Role(iv, c.Family, p - c.Root) switch
                {
                    ToneRole.Root or ToneRole.Third or ToneRole.Fifth or ToneRole.Seventh => 1,
                    ToneRole.Tension => 0.5,
                    _ => p == c.Bass ? 1 : -0.5,
                };
            }
            // A beat playing one or two notes (an arpeggio, a line) can't hold the whole chord, so it misses less.
            var solid = 0;
            for (var p = 0; p < 12; p++) if (sum[p] >= 0.25 * max) solid++;
            double score = 0;
            for (var b = a; b < e; b++)
            {
                if (_active[b] <= 0) continue;
                for (var p = 0; p < 12; p++) score += _w[b, p] * value[p];
                var full = Math.Min(1, _here[b] / (double)Math.Max(1, solid));
                // In a thin beat the notes just before still ring.
                var back = _here[b] <= 2 ? Math.Max(a, b - 1) : b;
                for (var p = 0; p < 12; p++)
                {
                    if ((c.Mask >> p & 1) == 0) continue;
                    var heard = false;
                    for (var x = b; x >= back && !heard; x--) heard = _w[x, p] > 0;
                    if (heard) continue;
                    // A chord note not heard counts in proportion to how much of the chord it fills (a passing note hardly at all).
                    score -= (value[p] >= 1 ? 3 : 0.5) * _active[b] * (sum[p] / total) * full;
                }
                // A new bass struck after the chord's first beat says the chord has changed, unless it's an approach note;
                // the shorter it is, the less it says (a walking bass's passing notes).
                if (b > a && _bassOk[b] && _low[b] != int.MaxValue && _low[b] % 12 != c.Bass && _struckLow[b] && !_approach[b])
                    score -= _busy * Math.Min(1, _bassLen[b] / (2.0 * Beat));
            }
            return (score, c);
        }

        /// <summary>
        /// The chord named from its stronger notes (sounding 30% as long as the longest or more), with the root the reading
        /// found: the same chord unless they say otherwise, without the passing notes cluttering its name.
        /// </summary>
        ChordSymbol Named(int a, int e, ChordSymbol found)
        {
            var sum = new double[12];
            for (var b = a; b < e; b++) for (var p = 0; p < 12; p++) sum[p] += _w[b, p];
            var max = sum.Max();
            var mask = (found.Mask >> found.Root & 1) << found.Root;
            var weights = new float[12];
            for (var p = 0; p < 12; p++)
            {
                weights[p] = (float)(sum[p] / max);
                if (sum[p] >= 0.3 * max) mask |= 1 << p;
            }
            var (bass, lowest) = BassOf(a, e);
            return ChordSpeller.Spell(mask, bass, weights, lowest) is { } again && again.Root == found.Root ? again : found;
        }
    }
}
