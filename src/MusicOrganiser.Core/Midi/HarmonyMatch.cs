using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Midi;

/// <summary>
/// The harmony MIDI is matched against: how long each pitch class sounds (shares, the strongest = 1), the chords in
/// order, and the notes themselves in time, so a part can be checked against what sounds at the same moment. Made from
/// a project's pitched parts, or from one MIDI part ("what goes with this?").
/// </summary>
public sealed record HarmonyTarget(string Name, float[] Chroma, IReadOnlyList<PhraseChord> Chords, KeyEstimate? Key)
{
    /// <summary>The notes, timed at <see cref="PhraseAnalyser.Ppq"/>: a part's from its first bar, a song's from its start.</summary>
    public IReadOnlyList<NoteEvent> Notes { get; init; } = [];

    /// <summary>
    /// A part loops (it and a part fitted to it play round and round together, from the same bar); a song plays once,
    /// and a part fitted to it is lined up where it fits best.
    /// </summary>
    public bool Loops { get; init; }

    /// <summary>Where each kind of part sits in it: the middle of its notes (MIDI note numbers), by kind (<see cref="MidiRoles"/>).</summary>
    public IReadOnlyDictionary<string, double> Registers { get; init; } = new Dictionary<string, double>();

    /// <summary>How long it plays before it goes round again (a part) or ends (a song), in ticks.</summary>
    public long Length => (long)PhraseAnalyser.BarsOf(Notes) * 4 * PhraseAnalyser.Ppq;

    HarmonyTimeline? _timeline;
    internal HarmonyTimeline? Timeline => Notes.Count == 0 ? null : _timeline ??= HarmonyTimeline.Of(Notes);

    /// <summary>The chords as names, a few at most: "Am – F – C – G".</summary>
    public string ChordText(int most = 8) =>
        string.Join(" – ", Chords.Take(most).Select(c => c.Name)) + (Chords.Count > most ? " …" : "");

    /// <summary>
    /// A project's harmony from its channels: the pitched ones (no drums, slices or samples at their own pitch), or, when
    /// none is pitched, everything but the drums.
    /// </summary>
    public static HarmonyTarget? FromChannels(string name, IReadOnlyList<MidiChannel> channels)
    {
        var pitched = channels.Where(ProjectKey.IsPitched).ToList();
        if (pitched.Count == 0) pitched = channels.Where(c => c.Kind is not ("Drums" or "")).ToList();
        var notes = pitched.SelectMany(c => c.Notes.Where(n => !n.Muted).Select(n => Scale(n, c.Ppq))).OrderBy(n => n.Start).ToList();
        if (notes.Count < 3) return null;
        var chroma = new float[12];
        foreach (var n in notes) chroma[n.Key % 12] += (float)Math.Clamp(n.Length / (4.0 * PhraseAnalyser.Ppq), 1 / 16.0, 2);
        var key = ProjectKey.Estimate(pitched);
        // Where its bassline, chords and melody sit, so parts fitted to it are offered in the octave they'd play in there.
        var registers = new Dictionary<string, List<double>>();
        foreach (var c in pitched)
        {
            var own = PhraseAnalyser.Normalise(c.Notes, c.Ppq);
            if (own.Count < 3) continue;
            var role = PhraseAnalyser.Analyse(own.Take(PhraseAnalyser.MaxNotes).ToList(), c.Name).Role;
            if (role is MidiRoles.Drums) continue;
            (registers.TryGetValue(role, out var list) ? list : registers[role] = []).Add(HarmonyMatcher.Centre(own));
        }
        return new HarmonyTarget(name, Strongest(chroma), PhraseAnalyser.Progression(notes), key)
        {
            Notes = notes,
            // Two basslines: the lower one is the bass.
            Registers = registers.ToDictionary(kv => kv.Key, kv => kv.Key == MidiRoles.Bass ? kv.Value.Min() : kv.Value.Average()),
        };
    }

    public static HarmonyTarget FromPhrase(string name, float[] chroma, IReadOnlyList<PhraseChord> chords, KeyEstimate? key) =>
        new(name, Strongest(chroma), chords, key);

    /// <summary>One part to fit others to, with its notes as they'll play (moved, from its first bar), looping.</summary>
    public static HarmonyTarget FromPart(string name, string role, IReadOnlyList<NoteEvent> notes, float[] chroma,
                                         IReadOnlyList<PhraseChord> chords, KeyEstimate? key) =>
        new(name, Strongest(chroma), chords, key)
        {
            Notes = notes,
            Loops = true,
            Registers = notes.Count == 0 ? new Dictionary<string, double>() : new Dictionary<string, double> { [role] = HarmonyMatcher.Centre(notes) },
        };

    static NoteEvent Scale(NoteEvent n, int ppq)
    {
        if (ppq == PhraseAnalyser.Ppq) return n;
        var k = (double)PhraseAnalyser.Ppq / Math.Max(1, ppq);
        return n with { Start = (long)Math.Round(n.Start * k), Length = Math.Max(1, (long)Math.Round(n.Length * k)) };
    }

    static float[] Strongest(float[] chroma)
    {
        var max = chroma.Max();
        return max <= 0 ? new float[12] : chroma.Select(c => c / max).ToArray();
    }

    /// <summary>Where a part of this kind usually sits (the middle of its notes), when nothing says otherwise.</summary>
    static readonly IReadOnlyDictionary<string, double> Usual = new Dictionary<string, double>
    {
        [MidiRoles.Bass] = 40, [MidiRoles.Chords] = 60, [MidiRoles.Arp] = 67, [MidiRoles.Melody] = 72, [MidiRoles.Other] = 62,
    };

    /// <summary>
    /// How many octaves to move a part of this kind, whose notes' middle is <paramref name="centre"/> once moved into
    /// key, so it plays where that kind of part plays here: next to the same kind of part when there is one, otherwise
    /// where it usually sits, nudged by how high or low this song or part is (a bassline under low chords goes lower).
    /// </summary>
    public int OctavesFor(string role, double centre)
    {
        if (role is MidiRoles.Drums || centre <= 0) return 0;
        if (!Registers.TryGetValue(role, out var wanted))
        {
            var known = Registers.Where(r => Usual.ContainsKey(r.Key)).Select(r => r.Value - Usual[r.Key]).ToList();
            wanted = Usual.GetValueOrDefault(role, 62) + (known.Count == 0 ? 0 : Math.Clamp(known.Average(), -12, 12));
        }
        return (int)Math.Clamp(Math.Round((wanted - centre) / 12), -3, 3);
    }
}

/// <summary>
/// What sounds when, half a bar at a time: for each half bar, how well each of the 12 notes would sit against what's
/// sounding then (1: it's being played; about 0.6: a note that's not there but doesn't rub; below 0: a semitone against
/// a note that's held). Built once per harmony; every part is then checked against it.
/// </summary>
internal sealed class HarmonyTimeline
{
    /// <summary>Half a bar, in ticks.</summary>
    public const int Slot = 2 * PhraseAnalyser.Ppq;

    public int Count { get; }
    /// <summary>12 per half bar; NaN in the first of a half bar where nothing sounds.</summary>
    public float[] Sits { get; }

    HarmonyTimeline(int count, float[] sits) { Count = count; Sits = sits; }

    /// <summary>How much each pitch class sounds in each half bar (12 per slot), in half bars.</summary>
    public static float[] Weights(IReadOnlyList<NoteEvent> notes, int count)
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

    public static int SlotsOf(IReadOnlyList<NoteEvent> notes) =>
        notes.Count == 0 ? 0 : (int)Math.Min(4096, (notes.Max(n => n.Start + Math.Max(1, n.Length)) + Slot - 1) / Slot);

    /// <summary>How much two notes this far apart rub: a semitone most, then a tritone, then a whole tone a little.</summary>
    static readonly float[] Rub = [0, 1f, 0.2f, 0, 0, 0, 0.35f, 0, 0, 0, 0.2f, 1f];

    public static HarmonyTimeline Of(IReadOnlyList<NoteEvent> notes)
    {
        // Bar-long slots for the count, so a part's last bar is whole.
        var count = (int)Math.Min(4096, PhraseAnalyser.BarsOf(notes) * 2);
        var w = Weights(notes, count);
        var sits = new float[count * 12];
        for (var k = 0; k < count; k++)
        {
            var at = k * 12;
            float max = 0;
            for (var p = 0; p < 12; p++) max = Math.Max(max, w[at + p]);
            if (max <= 0) { sits[at] = float.NaN; continue; }
            for (var p = 0; p < 12; p++)
            {
                var share = w[at + p] / max;
                if (share >= 0.25f) { sits[at + p] = 1; continue; }
                float rub = 0;
                for (var q = 0; q < 12; q++)
                {
                    var other = w[at + q] / max;
                    if (other >= 0.25f) rub = Math.Max(rub, other * Rub[(p - q + 12) % 12]);
                }
                sits[at + p] = Math.Clamp(0.6f + share * 1.6f - rub, -0.5f, 1);
            }
        }
        return new HarmonyTimeline(count, sits);
    }
}

/// <summary>How well a MIDI part fits a harmony, and the transposition that makes it fit best.</summary>
/// <param name="Score">0 to 1.</param>
/// <param name="Semitones">How far to move the part: −6 to +5, 0 when it already fits as it is.</param>
/// <param name="ChordScore">How closely its chords follow the target's (0 to 1), or null when one of them has no chords.</param>
/// <param name="Clash">The share of the part's notes (by how long they sound) on notes the target hardly plays.</param>
public sealed record HarmonyFit(double Score, int Semitones, double Tonal, double? ChordScore, double Clash)
{
    public int Percent => (int)Math.Round(Score * 100);

    /// <summary>
    /// How well its notes sit against what sounds at the same moment, played together (0 to 1), or null when that
    /// couldn't be checked (no notes to line up).
    /// </summary>
    public double? Together { get; init; }

    /// <summary>Where it lines up with a song, in ticks from the song's start (0 for a part, which loops with it).</summary>
    public long At { get; init; }

    /// <summary>Octaves to move it as well, so it plays where that kind of part plays in the song (or next to the part).</summary>
    public int Octaves { get; init; }

    /// <summary>"as it is", "up 3 semitones", "down 1 semitone".</summary>
    public string MoveText => Semitones switch
    {
        0 => "as it is",
        1 => "up 1 semitone",
        -1 => "down 1 semitone",
        > 0 => $"up {Semitones} semitones",
        _ => $"down {-Semitones} semitones",
    };
}

/// <summary>
/// Matches MIDI against a harmony. A part is tried in all 12 keys; in each, two things are scored: how much of it lands
/// on notes the target plays (and how little on notes it doesn't), and, when both have chords, how closely its chord
/// sequence follows a run of the target's chords. The best key wins, so a C minor progression is suggested for an F
/// minor song moved up 5 semitones, and a progression is found whatever key it was saved in. The best few are then
/// looked at closer (<see cref="Refine"/>): played against the harmony in time, as if on top of each other.
/// </summary>
public static class HarmonyMatcher
{
    /// <summary>Below this share of the strongest pitch class, the target "hardly plays" a note.</summary>
    const float Rare = 0.1f;

    /// <summary>The best way for a part to fit, from its pitch-class shares (adding to 1) and its chords.</summary>
    public static HarmonyFit Fit(ReadOnlySpan<float> chroma, IReadOnlyList<PhraseChord> chords, HarmonyTarget target)
    {
        HarmonyFit? best = null;
        foreach (var fit in Moves(chroma, chords, target))
            if (best is null || fit.Score > best.Score) best = fit;
        return best!;
    }

    /// <summary>The part moved each of the 12 ways, scored on its notes and chords alone.</summary>
    static HarmonyFit[] Moves(ReadOnlySpan<float> chroma, IReadOnlyList<PhraseChord> chords, HarmonyTarget target)
    {
        Span<double> tonal = stackalloc double[12];
        Span<double> clash = stackalloc double[12];
        Tonal(chroma, target.Chroma, tonal, clash);

        var useChords = chords.Count >= 2 && target.Chords.Count >= 2;
        var moves = new HarmonyFit[12];
        for (var s = 0; s < 12; s++)
        {
            var notes = Math.Clamp(tonal[s] - 0.6 * clash[s], 0, 1);
            double? chordScore = useChords ? ChordFollow(chords, target.Chords, s) : null;
            var score = chordScore is { } c ? 0.45 * notes + 0.55 * c : notes;
            var semitones = s <= 5 ? s : s - 12;
            // On a near tie the smaller move wins.
            score -= Math.Abs(semitones) * 0.0005;
            moves[s] = new HarmonyFit(Math.Clamp(score, 0, 1), semitones, tonal[s], chordScore, clash[s]);
        }
        return moves;
    }

    /// <summary>
    /// The closer look, with the part's notes: the few best moves are played against the harmony at the same time, half
    /// bar by half bar, both looping (a part) or lined up where it fits best (a song), so a melody whose notes are all in
    /// the song's key but land on the wrong chords ranks below one that follows them (Solomon, 2026-10-16: "make sure you
    /// are thinking about them as if they were playing at the same time on top of each other"). Also picks the octave.
    /// </summary>
    public static HarmonyFit Refine(ReadOnlySpan<float> chroma, IReadOnlyList<PhraseChord> chords, IReadOnlyList<NoteEvent> notes,
                                    string role, HarmonyTarget target)
    {
        var moves = Moves(chroma, chords, target);
        HarmonyFit? best = null;
        if (target.Timeline is { } timeline && notes.Count > 0)
        {
            var count = Math.Max(1, PhraseAnalyser.BarsOf(notes) * 2);
            var weights = HarmonyTimeline.Weights(notes, count);
            foreach (var move in moves.OrderByDescending(m => m.Score).Take(3))
            {
                var s = (move.Semitones + 12) % 12;
                if (Together(weights, count, timeline, s, target.Loops, out var at) is not { } together) continue;
                var fit = move with { Score = Math.Clamp(0.4 * move.Score + 0.6 * together, 0, 1) };
                fit = fit with { Together = together, At = at };
                if (best is null || fit.Score > best.Score) best = fit;
            }
        }
        best ??= moves.MaxBy(m => m.Score)!;
        return best with { Octaves = target.OctavesFor(role, Centre(notes) + best.Semitones) };
    }

    /// <summary>The octave alone, for a part whose notes aren't to hand: from its lowest and highest notes.</summary>
    public static HarmonyFit WithOctaves(HarmonyFit fit, string role, int lowest, int highest, HarmonyTarget target) =>
        fit with { Octaves = target.OctavesFor(role, (lowest + highest) / 2.0 + fit.Semitones) };

    /// <summary>The middle of a part's notes (MIDI note number), each counted by how long it sounds.</summary>
    public static double Centre(IReadOnlyList<NoteEvent> notes)
    {
        double sum = 0, total = 0;
        foreach (var n in notes)
        {
            var w = Math.Clamp(n.Length, 1, 8 * PhraseAnalyser.Ppq);
            sum += n.Key * (double)w;
            total += w;
        }
        return total > 0 ? sum / total : 0;
    }

    /// <summary>
    /// How well the part (half-bar <paramref name="weights"/>, moved by <paramref name="shift"/>) sits against what the
    /// harmony plays at the same time, 0 to 1. A looping target: both go round from the same bar until they meet again
    /// (32 bars at most). A song: the part is tried at every 4th bar and the best place wins. Null when they hardly
    /// overlap (the song is silent where the part plays).
    /// </summary>
    static double? Together(float[] weights, int count, HarmonyTimeline target, int shift, bool loops, out long at)
    {
        at = 0;
        int length, places;
        if (loops)
        {
            var both = (long)count / Gcd(count, target.Count) * target.Count;
            length = both <= 64 ? (int)both : Math.Max(count, target.Count);
            places = 1;
        }
        else
        {
            length = count;
            places = Math.Max(1, (target.Count - count) / 8 + 1);
            places = Math.Min(places, 128);
        }
        double? best = null;
        var sits = target.Sits;
        for (var place = 0; place < places; place++)
        {
            var from = place * 8;
            double sum = 0, heard = 0, all = 0;
            for (var i = 0; i < length; i++)
            {
                var mine = (i % count) * 12;
                var theirs = loops ? (from + i) % target.Count : from + i;
                double w = 0;
                for (var p = 0; p < 12; p++) w += weights[mine + p];
                if (w <= 0) continue;
                all += w;
                if (theirs >= target.Count || float.IsNaN(sits[theirs * 12])) continue;
                heard += w;
                for (var p = 0; p < 12; p++)
                    if (weights[mine + p] > 0) sum += weights[mine + p] * sits[theirs * 12 + (p + shift) % 12];
            }
            if (all <= 0 || heard < 0.25 * all) continue;
            var score = Math.Clamp(sum / heard, 0, 1);
            if (best is null || score > best + 1e-9)
            {
                best = score;
                at = (long)from * HarmonyTimeline.Slot;
            }
        }
        return best;
    }

    static int Gcd(int a, int b) => b == 0 ? Math.Max(1, a) : Gcd(b, a % b);

    /// <summary>
    /// How well one part's notes sit with another's sounding at the same time, both looping from the same bar (0 to 1),
    /// as they are: for showing the effect of an edit straight away. Null when they hardly overlap.
    /// </summary>
    public static double? SitsTogether(IReadOnlyList<NoteEvent> mine, IReadOnlyList<NoteEvent> theirs)
    {
        if (mine.Count == 0 || theirs.Count == 0) return null;
        var timeline = HarmonyTimeline.Of(theirs);
        var count = Math.Max(1, PhraseAnalyser.BarsOf(mine) * 2);
        return Together(HarmonyTimeline.Weights(mine, count), count, timeline, 0, loops: true, out _);
    }

    /// <summary>The note score alone for each of the 12 moves (cheap: used to pick which parts are worth a closer look).</summary>
    public static double QuickScore(ReadOnlySpan<float> chroma, HarmonyTarget target)
    {
        Span<double> tonal = stackalloc double[12];
        Span<double> clash = stackalloc double[12];
        Tonal(chroma, target.Chroma, tonal, clash);
        var best = 0.0;
        for (var s = 0; s < 12; s++) best = Math.Max(best, tonal[s] - 0.6 * clash[s]);
        return best;
    }

    /// <summary>For each move s (pitch class p goes to p + s): how alike its notes are to the target's, and how much lands on notes it hardly plays.</summary>
    static void Tonal(ReadOnlySpan<float> chroma, float[] target, Span<double> tonal, Span<double> clash)
    {
        double total = 0, mine = 0, theirs = 0;
        foreach (var c in chroma) { total += c; mine += c * c; }
        foreach (var t in target) theirs += t * t;
        if (total <= 0 || theirs <= 0) return;
        var norm = Math.Sqrt(mine * theirs);
        for (var s = 0; s < 12; s++)
        {
            double on = 0, off = 0;
            for (var p = 0; p < 12; p++)
            {
                if (chroma[p] <= 0) continue;
                var t = target[(p + s) % 12];
                on += chroma[p] * t;
                if (t < Rare) off += chroma[p];
            }
            // How alike the two sets of notes are, by how long each sounds (1 = the same balance of notes).
            tonal[s] = on / norm;
            clash[s] = off / total;
        }
    }

    /// <summary>
    /// How closely the part's chords, moved by <paramref name="shift"/>, follow the target's: the best run of the target's
    /// chords (wrapping round, as songs loop), chord by chord. Timing doesn't count, only the order.
    /// </summary>
    public static double ChordFollow(IReadOnlyList<PhraseChord> chords, IReadOnlyList<PhraseChord> target, int shift)
    {
        var moved = new PhraseChord[Math.Min(chords.Count, 16)];
        for (var i = 0; i < moved.Length; i++) moved[i] = chords[i].Transposed(shift);
        var best = 0.0;
        var starts = Math.Min(target.Count, 256);
        for (var j = 0; j < starts; j++)
        {
            double sum = 0;
            for (var k = 0; k < moved.Length; k++) sum += moved[k].Similarity(target[(j + k) % target.Count]);
            best = Math.Max(best, sum / moved.Length);
        }
        return best;
    }
}
