using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Midi;

/// <summary>
/// The harmony MIDI is matched against: how long each pitch class sounds (shares, the strongest = 1) and the chords in
/// order, with no timing. Made from a project's pitched parts, or from one MIDI part ("what goes with this?").
/// </summary>
public sealed record HarmonyTarget(string Name, float[] Chroma, IReadOnlyList<PhraseChord> Chords, KeyEstimate? Key)
{
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
        return new HarmonyTarget(name, Strongest(chroma), PhraseAnalyser.Progression(notes), key);
    }

    public static HarmonyTarget FromPhrase(string name, float[] chroma, IReadOnlyList<PhraseChord> chords, KeyEstimate? key) =>
        new(name, Strongest(chroma), chords, key);

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
}

/// <summary>How well a MIDI part fits a harmony, and the transposition that makes it fit best.</summary>
/// <param name="Score">0 to 1.</param>
/// <param name="Semitones">How far to move the part: −6 to +5, 0 when it already fits as it is.</param>
/// <param name="ChordScore">How closely its chords follow the target's (0 to 1), or null when one of them has no chords.</param>
/// <param name="Clash">The share of the part's notes (by how long they sound) on notes the target hardly plays.</param>
public sealed record HarmonyFit(double Score, int Semitones, double Tonal, double? ChordScore, double Clash)
{
    public int Percent => (int)Math.Round(Score * 100);

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
/// Matches MIDI against a harmony, ignoring rhythm. A part is tried in all 12 keys; in each, two things are scored:
/// how much of it lands on notes the target plays (and how little on notes it doesn't), and, when both have chords, how
/// closely its chord sequence follows a run of the target's chords. The best key wins, so a C minor progression is
/// suggested for an F minor song moved up 5 semitones, and a progression is found whatever key it was saved in.
/// </summary>
public static class HarmonyMatcher
{
    /// <summary>Below this share of the strongest pitch class, the target "hardly plays" a note.</summary>
    const float Rare = 0.1f;

    /// <summary>The best way for a part to fit, from its pitch-class shares (adding to 1) and its chords.</summary>
    public static HarmonyFit Fit(ReadOnlySpan<float> chroma, IReadOnlyList<PhraseChord> chords, HarmonyTarget target)
    {
        Span<double> tonal = stackalloc double[12];
        Span<double> clash = stackalloc double[12];
        Tonal(chroma, target.Chroma, tonal, clash);

        var useChords = chords.Count >= 2 && target.Chords.Count >= 2;
        HarmonyFit? best = null;
        for (var s = 0; s < 12; s++)
        {
            var notes = Math.Clamp(tonal[s] - 0.6 * clash[s], 0, 1);
            double? chordScore = useChords ? ChordFollow(chords, target.Chords, s) : null;
            var score = chordScore is { } c ? 0.45 * notes + 0.55 * c : notes;
            var semitones = s <= 5 ? s : s - 12;
            // On a near tie the smaller move wins.
            score -= Math.Abs(semitones) * 0.0005;
            if (best is null || score > best.Score)
                best = new HarmonyFit(Math.Clamp(score, 0, 1), semitones, tonal[s], chordScore, clash[s]);
        }
        return best!;
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
