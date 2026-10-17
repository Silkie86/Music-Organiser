using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi.Jazz;

/// <summary>One chord and when it plays, in ticks at <see cref="PhraseAnalyser.Ppq"/>.</summary>
public sealed record TimedChord(long Start, long Length, ChordSymbol Chord)
{
    public long End => Start + Length;
}

/// <summary>
/// The chord track: which chord sounds when. Worked out from notes (half a bar at a time, the lowest note as the bass,
/// so a bassline under rootless chords names them right), or read from a chart typed like a lead sheet:
/// "Dm9 G13 | Cmaj9 | %" (two chords share a bar between bar lines; % repeats the bar before).
/// </summary>
public static class ChordLane
{
    public const int Ppq = PhraseAnalyser.Ppq, Bar = 4 * Ppq;

    /// <summary>The chords in these notes, a half bar at a time, the same chord running on as one.</summary>
    public static List<TimedChord> Detect(IReadOnlyList<NoteEvent> notes, long length, int slot = 2 * Ppq)
    {
        var lane = new List<TimedChord>();
        if (notes.Count == 0 || length <= 0) return lane;
        var sorted = notes.Where(n => !n.Muted).OrderBy(n => n.Start).ToList();
        var weights = new float[12];
        for (long at = 0; at < length; at += slot)
        {
            Array.Clear(weights);
            var lowest = int.MaxValue;
            long end = Math.Min(length, at + slot), lowestHeld = 0;
            foreach (var n in sorted)
            {
                if (n.Start >= end) break;
                var overlap = Math.Min(n.Start + n.Length, end) - Math.Max(n.Start, at);
                if (overlap < slot / 8) continue;   // a grace note or the tail of the chord before
                weights[n.Key % 12] += overlap;
                // The bass: the lowest note that sounds for a good part of the slot.
                if (overlap >= slot / 4 && (n.Key < lowest || (n.Key == lowest && overlap > lowestHeld)))
                {
                    lowest = n.Key;
                    lowestHeld = overlap;
                }
            }
            var max = weights.Max();
            ChordSymbol? chord = null;
            if (max > 0)
            {
                var mask = 0;
                for (var p = 0; p < 12; p++)
                {
                    weights[p] /= max;
                    if (weights[p] >= 0.2f) mask |= 1 << p;
                }
                chord = ChordSpeller.Spell(mask, lowest == int.MaxValue ? -1 : lowest % 12, weights);
                // Too few notes to say (a melody alone, a bass note): the chord before carries on if these notes fit it.
                if (chord is null && lane.Count > 0 && Fits(lane[^1].Chord, mask)) chord = lane[^1].Chord;
            }
            if (chord is not { } c) continue;
            if (lane.Count > 0 && lane[^1].End == at && lane[^1].Chord.Name == c.Name)
                lane[^1] = lane[^1] with { Length = lane[^1].Length + (end - at) };
            else lane.Add(new TimedChord(at, end - at, c));
        }
        return lane;
    }

    static bool Fits(ChordSymbol chord, int mask)
    {
        for (var p = 0; p < 12; p++)
            if ((mask >> p & 1) != 0 && ChordScales.Sit(chord, p) < 0.3) return false;
        return true;
    }

    /// <summary>The chord sounding at a tick, the lane going round when it's shorter; null where none is.</summary>
    public static ChordSymbol? At(IReadOnlyList<TimedChord> lane, long tick, long loop = 0)
    {
        if (lane.Count == 0) return null;
        if (loop > 0) tick %= loop;
        // Lanes are short (a section): a walk is quicker than it looks.
        foreach (var c in lane)
            if (tick >= c.Start && tick < c.End) return c.Chord;
        return null;
    }

    /// <summary>
    /// A typed chart as chords in time, or null with why when it can't be read. Bar lines split bars; chords between
    /// two bar lines share the bar evenly; without bar lines each chord gets a bar. "%" repeats the bar before, and
    /// "/" or "-" inside a bar holds the chord before for another share of it.
    /// </summary>
    public static List<TimedChord>? ParseChart(string text, out string why)
    {
        why = "";
        var lane = new List<TimedChord>();
        text = text.Replace("\n", " | ").Replace("\r", " ");
        var bars = text.Contains('|')
            ? text.Split('|').Select(b => b.Trim()).Where(b => b.Length > 0).ToList()
            : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        List<ChordSymbol?> previousBar = [];
        long at = 0;
        foreach (var bar in bars)
        {
            var tokens = bar.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            List<ChordSymbol?> chords = [];
            if (tokens is ["%"]) chords = [.. previousBar];
            else
                foreach (var t in tokens)
                {
                    if (t is "/" or "-" or "%")
                    {
                        chords.Add(chords.Count > 0 ? chords[^1] : lane.Count > 0 ? lane[^1].Chord : null);
                        continue;
                    }
                    if (t is "N.C." or "NC")
                    {
                        chords.Add(null);
                        continue;
                    }
                    if (ChordSymbol.Parse(t) is not { } c)
                    {
                        why = $"“{t}” isn't a chord the app can read.";
                        return null;
                    }
                    chords.Add(c);
                }
            if (chords.Count == 0) continue;
            var share = Bar / chords.Count;
            foreach (var c in chords)
            {
                if (c is { } chord)
                {
                    if (lane.Count > 0 && lane[^1].End == at && lane[^1].Chord.Name == chord.Name)
                        lane[^1] = lane[^1] with { Length = lane[^1].Length + share };
                    else lane.Add(new TimedChord(at, share, chord));
                }
                at += share;
            }
            previousBar = chords;
        }
        if (lane.Count == 0) why = "Type chords such as Dm9 G13 | Cmaj9 |";
        return lane.Count == 0 ? null : lane;
    }

    /// <summary>The lane as a chart, bar by bar: "Dm9 G13 | Cmaj9 | % | A7alt".</summary>
    public static string ToChart(IReadOnlyList<TimedChord> lane, long length)
    {
        if (lane.Count == 0) return "";
        var bars = (int)Math.Ceiling(Math.Max(length, lane[^1].End) / (double)Bar);
        var text = new List<string>();
        string? last = null;
        for (var b = 0; b < bars; b++)
        {
            long from = (long)b * Bar, to = from + Bar;
            // Chords that start in the bar, or the one still sounding at its start.
            var inBar = lane.Where(c => c.Start < to && c.End > from).ToList();
            if (inBar.Count == 0) { text.Add("N.C."); last = null; continue; }
            var names = inBar.Select(c => c.Chord.Name).ToList();
            var bar = string.Join(" ", names);
            text.Add(names.Count == 1 && bar == last ? "%" : bar);
            last = bar;
        }
        return string.Join(" | ", text);
    }

    /// <summary>The lane moved up or down.</summary>
    public static List<TimedChord> Transposed(IReadOnlyList<TimedChord> lane, int semitones) =>
        lane.Select(c => c with { Chord = c.Chord.Transposed(semitones) }).ToList();
}
