using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Midi.Playground;

/// <summary>
/// A chord progression found in your chord parts (Solomon, 2026-10-22: "extract chord progressions from your chord library
/// so it will go on the chord track"): its chords moved to C (C minor for a minor one), how many songs play it, and one
/// part that does, to hear it and to take its timing from.
/// </summary>
/// <param name="Chords">The chords in order, a repeated chord once, in C major or C minor.</param>
/// <param name="Bars">About how many bars one go round lasts.</param>
/// <param name="Songs">How many different songs (or MIDI files) play it.</param>
/// <param name="Example">One part that plays it: the one with the most copies.</param>
/// <param name="Shift">How far the example's notes move to be in C: its key's tonic, taken away.</param>
public sealed record Progression(IReadOnlyList<PhraseChord> Chords, bool Minor, int Bars, int Songs, MidiEntry Example, int Shift)
{
    /// <summary>Roman numerals in the progression's own key: "ii7 V7 Imaj7".</summary>
    public IReadOnlyList<string> Romans { get; init; } = [];

    /// <summary>How far to move it so it's in <paramref name="key"/>: the same tonic, or the relative one across major and minor.</summary>
    public int ShiftInto(KeyEstimate? key) =>
        key is null ? (Minor ? 9 : 0)   // no key yet: C major, A minor (the keys with no sharps or flats)
                    : ChordSymbol.Mod(key.Tonic + (key.Minor == Minor ? 0 : Minor ? 9 : 3));

    /// <summary>Its chords moved into <paramref name="key"/>, named the way that key writes them.</summary>
    public IReadOnlyList<string> NamesIn(KeyEstimate? key)
    {
        var shift = ShiftInto(key);
        var spelled = key ?? new KeyEstimate(shift, Minor, 1);
        return Chords.Select(c => c.Transposed(shift).NameIn(spelled)).ToList();
    }
}

/// <summary>
/// The progressions in every chord part read from your projects and MIDI folders, grouped by what they play (the roots
/// and kinds of chord, in the key) and ranked by how many songs play them. Worked out again as more MIDI is read.
/// </summary>
public static class ChordProgressions
{
    /// <summary>Longer than this (after going round once) it's a whole song's chords, not a progression to start from.</summary>
    public const int MostChords = 12;

    public static IReadOnlyList<Progression> From(IEnumerable<MidiEntry> entries, CancellationToken ct = default)
    {
        var groups = new Dictionary<string, Group>();
        var n = 0;
        foreach (var e in entries)
        {
            if (++n % 4096 == 0) ct.ThrowIfCancellationRequested();
            if (e.Role != MidiRoles.Chords || e.Chords.Count < 2) continue;
            var (tonic, minor) = KeyOf(e);
            var chords = OneGoRound(e.Chords.Select(c => c.Transposed(-tonic)).ToList());
            if (chords.Count is < 2 or > MostChords) continue;
            var name = (minor ? "m|" : "M|") + string.Join(" ", chords.Select(c => $"{c.Root}:{ProgressionModel.Kind(c.Symbol)}"));
            if (!groups.TryGetValue(name, out var g)) groups[name] = g = new Group(chords, minor);
            g.Add(e, tonic, e.Chords.Count);
        }
        return groups.Values
            .OrderByDescending(g => g.Songs.Count).ThenByDescending(g => g.Parts).ThenBy(g => g.Chords.Count)
            .Select(g => g.ToProgression()).ToList();
    }

    /// <summary>The part's key (the one heard, else its first chord's root, minor if that chord is).</summary>
    public static (int Tonic, bool Minor) KeyOf(MidiEntry e)
    {
        if (e.Phrase.Key is { Confidence: >= MusicTheory.SureMidiKey } k) return (k.Tonic, k.Minor);
        var first = e.Chords[0];
        return (first.Root, MusicTheory.IsMinorChord(first.Root, first.Sounding));
    }

    /// <summary>
    /// A progression played round more than once, once: "Dm7 G7 Dm7 G7" is "Dm7 G7", and "C Am F G C" (back home at the
    /// end) is "C Am F G".
    /// </summary>
    public static List<PhraseChord> OneGoRound(IReadOnlyList<PhraseChord> chords)
    {
        for (var p = 2; p < chords.Count; p++)
        {
            var repeats = true;
            for (var i = p; i < chords.Count && repeats; i++) repeats = Same(chords[i], chords[i - p]);
            if (repeats) return chords.Take(p).ToList();
        }
        return chords.ToList();
    }

    static bool Same(PhraseChord a, PhraseChord b) => a.Root == b.Root && ProgressionModel.Kind(a.Symbol) == ProgressionModel.Kind(b.Symbol);

    sealed class Group(List<PhraseChord> chords, bool minor)
    {
        public List<PhraseChord> Chords { get; } = chords;
        public HashSet<string> Songs { get; } = [];
        public int Parts { get; private set; }
        MidiEntry? _example;
        int _shift, _played;

        public void Add(MidiEntry e, int tonic, int played)
        {
            Parts++;
            Songs.Add(MidiEntry.SongOf(e.Phrase.Source.Path));
            if (e.OtherSources is { } others) foreach (var o in others) Songs.Add(MidiEntry.SongOf(o));
            if (_example is not null && e.Copies <= _example.Copies) return;
            (_example, _shift, _played) = (e, -tonic, played);
        }

        public Progression ToProgression()
        {
            var e = _example!;
            // One go round's share of the part's bars.
            var bars = Math.Max(1, (int)Math.Round(e.Phrase.Bars * (double)Chords.Count / Math.Max(Chords.Count, _played)));
            var romans = ChordFunctions.Analyse(Chords.Select(c => c.Symbol).ToList(), 0, minor).Select(f => f.Roman).ToList();
            return new Progression(Chords, minor, bars, Songs.Count, e, _shift) { Romans = romans };
        }
    }
}
