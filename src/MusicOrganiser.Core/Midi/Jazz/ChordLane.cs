using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi.Jazz;

/// <summary>One chord and when it plays, in ticks at <see cref="PhraseAnalyser.Ppq"/>.</summary>
public sealed record TimedChord(long Start, long Length, ChordSymbol Chord)
{
    public long End => Start + Length;
}

/// <summary>
/// The chord track: which chord sounds when. Worked out from notes by the app's chord reader (<see cref="ChordReader"/>:
/// beat by beat, pushes and walking basses heard as a player hears them, a bassline under rootless chords naming
/// them), or read from a chart typed like a lead sheet: "Dm9 G13 | Cmaj9 | %" (two chords share a bar between bar
/// lines; % repeats the bar before).
/// </summary>
public static class ChordLane
{
    public const int Ppq = PhraseAnalyser.Ppq, Bar = 4 * Ppq;

    /// <summary>The chords in these notes, beat by beat, the same chord running on as one (<see cref="ChordReader"/>).</summary>
    public static List<TimedChord> Detect(IReadOnlyList<NoteEvent> notes, long length) => ChordReader.Read(notes, length);

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
    /// Every chord a note sounds over, one piece per chord, in order: a note held from C7 into F7 is heard against both.
    /// Struck is true for the first piece, where the note starts (or, struck in a gap before a chord, where that chord
    /// comes in: nothing came before it to prepare it); the others are held over. Gaps with no chord are skipped.
    /// </summary>
    public static IEnumerable<(ChordSymbol Chord, long Start, long Length, bool Struck)> Under(
        IReadOnlyList<TimedChord> lane, long start, long length, long loop = 0)
    {
        if (lane.Count == 0) yield break;
        long at = start, end = start + Math.Max(1, length);
        var first = true;
        while (at < end)
        {
            var tick = loop > 0 ? at % loop : at;
            long next;
            TimedChord? hit = null;
            foreach (var c in lane)
                if (tick >= c.Start && tick < c.End) { hit = c; break; }
            if (hit is not null) next = at + (hit.End - tick);
            else
            {
                // A gap: on to the next chord to start, or the loop's end.
                var later = long.MaxValue;
                foreach (var c in lane)
                    if (c.Start > tick) later = Math.Min(later, c.Start);
                next = later != long.MaxValue ? at + (later - tick) : loop > 0 ? at + (loop - tick) : end;
            }
            if (loop > 0 && next - at > loop - tick) next = at + (loop - tick);   // a chord running past the loop's end
            next = Math.Min(Math.Max(next, at + 1), end);
            if (hit is not null)
            {
                yield return (hit.Chord, at, next - at, first);
                first = false;
            }
            at = next;
        }
    }

    /// <summary>
    /// The chord a note is heard against: the one it sounds over longest (the first of equals). A note pushed a 16th
    /// ahead of the bar belongs to the chord it rings into, not to a sliver of the chord before (Solomon, 2026-10-21:
    /// a D marked red over Gsus4 → Fmaj13 "didn't rub against the chord tho").
    /// </summary>
    public static ChordSymbol? Heard(IReadOnlyList<TimedChord> lane, long start, long length, long loop = 0)
    {
        ChordSymbol? best = null;
        long most = 0;
        foreach (var (chord, _, len, _) in Under(lane, start, length, loop))
            if (len > most) { best = chord; most = len; }
        return best;
    }

    /// <summary>
    /// Whether a note fights the chords under it, for marking it red: true when it sounds for longer than an eighth
    /// over a chord it doesn't sit on (<see cref="ChordScales.Sit"/> under 0.05). Shorter notes are passing notes.
    /// </summary>
    public static bool Fights(IReadOnlyList<TimedChord> lane, long start, long length, int pc,
                              HarmonyStyle style = HarmonyStyle.JazzGospel, long loop = 0)
    {
        if (length <= Ppq / 2) return false;
        foreach (var (chord, _, len, _) in Under(lane, start, length, loop))
            if (len > Ppq / 2 && ChordScales.Sit(chord, pc, style) < 0.05) return true;
        return false;
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
            // Each chord's start worked out from the bar's, so a bar of 5 or 7 chords still ends on the bar line.
            var barStart = at;
            for (var k = 0; k < chords.Count; k++)
            {
                at = barStart + (long)k * Bar / chords.Count;
                var share = barStart + (long)(k + 1) * Bar / chords.Count - at;
                if (chords[k] is { } chord)
                {
                    if (lane.Count > 0 && lane[^1].End == at && lane[^1].Chord.Name == chord.Name)
                        lane[^1] = lane[^1] with { Length = lane[^1].Length + share };
                    else lane.Add(new TimedChord(at, share, chord));
                }
            }
            at = barStart + Bar;
            previousBar = chords;
        }
        if (lane.Count == 0) why = "Type chords such as Dm9 G13 | Cmaj9 |";
        return lane.Count == 0 ? null : lane;
    }

    /// <summary>
    /// The lane as a chart, bar by bar: "Dm9 G13 | Cmaj9 | % | A7alt". A bar that isn't split evenly is written in
    /// eighths with "/" holds ("C / / D" is three beats of C, one of D), so reading the chart back gives the same timing.
    /// </summary>
    public static string ToChart(IReadOnlyList<TimedChord> lane, long length)
    {
        if (lane.Count == 0) return "";
        const int slots = 8;
        var bars = (int)Math.Ceiling(Math.Max(length, lane[^1].End) / (double)Bar);
        var text = new List<string>();
        string? last = null;
        for (var b = 0; b < bars; b++)
        {
            long from = (long)b * Bar;
            // The chord sounding at the start of each eighth of the bar (null between chords).
            var at = new string?[slots];
            for (var i = 0; i < slots; i++)
            {
                var t = from + (long)i * Bar / slots;
                at[i] = lane.FirstOrDefault(c => c.Start <= t && c.End > t)?.Chord.Name;
            }
            // The fewest even pieces the bar splits into: 1, 2, 4 or 8.
            var n = new[] { 1, 2, 4, 8 }.First(k => Enumerable.Range(0, slots).All(i => at[i] == at[i / (slots / k) * (slots / k)]));
            var tokens = new List<string>();
            for (var i = 0; i < n; i++)
            {
                var name = at[i * (slots / n)];
                tokens.Add(name is null ? "N.C." : i > 0 && name == at[(i - 1) * (slots / n)] ? "/" : name);
            }
            var bar = string.Join(" ", tokens);
            text.Add(n == 1 && bar == last && bar != "N.C." ? "%" : bar);
            last = bar;
        }
        // One bar on its own gets a bar line, or "C F" would read back as two bars.
        return bars == 1 ? text[0] + " |" : string.Join(" | ", text);
    }

    /// <summary>The lane moved up or down.</summary>
    public static List<TimedChord> Transposed(IReadOnlyList<TimedChord> lane, int semitones) =>
        lane.Select(c => c with { Chord = c.Chord.Transposed(semitones) }).ToList();
}
