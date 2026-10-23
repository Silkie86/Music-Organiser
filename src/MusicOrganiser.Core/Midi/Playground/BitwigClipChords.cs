using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi.Playground;

/// <summary>
/// Chords from a clip selected in Bitwig (chords first, step 3; Solomon, 2026-10-22: "How will it get the chords from
/// bitwig"): the clip's notes, as the extension reads them, made into chords the way the chord lane reads a part.
/// </summary>
public static class BitwigClipChords
{
    const int Ppq = PhraseAnalyser.Ppq, Bar = 4 * Ppq;

    /// <summary>
    /// The clip to take: one on the track selected in Bitwig (the one just clicked), else the arranger's, else the
    /// launcher's. Clips with no notes come last. Null when none is selected.
    /// </summary>
    public static BitwigClipNotes? Pick(IReadOnlyList<BitwigClipNotes> clips) =>
        clips.Select((c, i) => (Clip: c, Order: (c.Notes.Count == 0 ? 4 : 0) + (c.OnSelectedTrack ? 0 : 2) + (c.From == "arranger" ? 0 : 1), i))
             .OrderBy(x => x.Order).ThenBy(x => x.i).Select(x => x.Clip).FirstOrDefault();

    /// <summary>The clip's notes inside its loop, from its loop start, in ticks; any that go past its end are cut there.</summary>
    public static List<NoteEvent> Notes(BitwigClipNotes clip)
    {
        var length = Ticks(clip.Length);
        var start = Ticks(clip.Start);
        var notes = new List<NoteEvent>();
        foreach (var n in clip.Notes)
        {
            var at = Ticks(n.Start) - start;
            if (at < 0 || (length > 0 && at >= length) || n.Key is < 0 or > 127) continue;
            var lasts = Math.Max(1, Ticks(n.Length));
            if (length > 0) lasts = Math.Min(lasts, length - at);
            notes.Add(new NoteEvent(at, lasts, n.Key, Math.Clamp(n.Velocity, 1, 127)));
        }
        return notes.OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
    }

    /// <summary>The clip's chords over whole bars (its loop rounded up), read as the chord lane reads parts; empty with no notes.</summary>
    public static List<TimedChord> Lane(BitwigClipNotes clip)
    {
        var notes = Notes(clip);
        if (notes.Count == 0) return [];
        var length = Math.Max(Ticks(clip.Length), notes.Max(n => n.Start + n.Length));
        length = Math.Max(Bar, (length + Bar - 1) / Bar * Bar);
        var lane = ChordReader.Read(notes, length);
        if (lane.Count == 0) return lane;
        // The last chord lasts to the end of the clip's last bar, so it goes round cleanly.
        lane[^1] = lane[^1] with { Length = length - lane[^1].Start };
        return lane;
    }

    static long Ticks(double beats) => (long)Math.Round(beats * Ppq);
}
