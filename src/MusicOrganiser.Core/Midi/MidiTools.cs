using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi;

/// <summary>
/// One-click changes to a part's notes, for the MIDI page's piano roll (Solomon, 2026-10-18: "a mini version of
/// playground here so for simple stuff i can do it here"). Each gives new notes; the notes given are never changed.
/// Notes are in ticks at <see cref="PhraseAnalyser.Ppq"/>, from the part's first bar.
/// </summary>
public static class MidiTools
{
    const int Bar = 4 * PhraseAnalyser.Ppq;

    /// <summary>Every note moved up (or down, negative) this many semitones, kept on the keyboard.</summary>
    public static List<NoteEvent> Transpose(IReadOnlyList<NoteEvent> notes, int semitones) =>
        notes.Select(n => n with { Key = Math.Clamp(n.Key + semitones, 0, 127) }).ToList();

    /// <summary>
    /// Each note's start moved to the nearest step of the grid (a 16th by default), and its length to a whole number of
    /// steps (one at least). Two notes of the same key landing together become one.
    /// </summary>
    public static List<NoteEvent> Quantise(IReadOnlyList<NoteEvent> notes, int grid = PhraseAnalyser.Ppq / 4)
    {
        if (grid <= 0) return [.. notes];
        static long Snap(long t, int g) => (long)Math.Round(t / (double)g, MidpointRounding.AwayFromZero) * g;
        return notes.Select(n => n with { Start = Snap(n.Start, grid), Length = Math.Max(grid, Snap(n.Length, grid)) })
                    .GroupBy(n => (n.Start, n.Key)).Select(g => g.MaxBy(n => n.Length))
                    .OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
    }

    /// <summary>The part played twice: the notes again straight after its <paramref name="length"/> (its bars).</summary>
    public static List<NoteEvent> Repeat(IReadOnlyList<NoteEvent> notes, long length) =>
        [.. notes, .. notes.Select(n => n with { Start = n.Start + length })];

    /// <summary>
    /// Played back to front over its <paramref name="length"/>: the last note first. Each note keeps its length, so
    /// chords stay chords.
    /// </summary>
    public static List<NoteEvent> Reverse(IReadOnlyList<NoteEvent> notes, long length) =>
        notes.Select(n => n with { Start = Math.Max(0, length - (n.Start + n.Length)) })
             .OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();

    /// <summary>Only its first half (in whole bars, one at least): for a part that's the same idea twice.</summary>
    public static List<NoteEvent> FirstHalf(IReadOnlyList<NoteEvent> notes, long length)
    {
        var half = Math.Max(Bar, length / 2 / Bar * Bar);
        return notes.Where(n => n.Start < half).Select(n => n with { Length = Math.Min(n.Length, half - n.Start) }).ToList();
    }
}
