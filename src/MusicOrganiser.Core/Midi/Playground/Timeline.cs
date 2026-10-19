using System.Text.Json.Serialization;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi.Playground;

/// <summary>
/// A clip on the playground's timeline (Solomon, 2026-10-19: "the playground feels very static, it should work like a
/// mini DAW"): a stretch of notes that can sit anywhere on a track. Its notes are one go round of the part, from 0;
/// the clip plays them round and round for as long as it's stretched, from <see cref="Offset"/> into the first go round
/// when its start has been trimmed. Each go round can be moved to fit the chords under it.
/// </summary>
public sealed class PlaygroundClip
{
    public string Name { get; set; } = "Part";
    public string Role { get; set; } = MidiRoles.Other;
    /// <summary>One go round of the notes, from 0, packed as <see cref="MidiIndex.EncodeNotes"/> does.</summary>
    public string Data { get; set; } = "";
    /// <summary>How long one go round is, in ticks.</summary>
    public long Loop { get; set; } = ChordLane.Bar;
    /// <summary>Where on the timeline it starts.</summary>
    public long Start { get; set; }
    /// <summary>How long it plays: longer than <see cref="Loop"/> plays it round again.</summary>
    public long Length { get; set; } = ChordLane.Bar;
    /// <summary>How far into its first go round it starts (its start dragged in).</summary>
    public long Offset { get; set; }
    /// <summary>Semitones it's moved by hand (the octave and semitone buttons).</summary>
    public int Transpose { get; set; }
    /// <summary>
    /// Changed by hand, so it stays as it is instead of moving to fit the chords, until Fit again (Solomon, 2026-10-19:
    /// "any manual change is an override").
    /// </summary>
    public bool Pinned { get; set; }
    /// <summary>How far each go round is moved to fit the chords under it, worked out by <see cref="ClipFit"/>.</summary>
    public List<int> Fits { get; set; } = [];
    /// <summary>The project or .mid file it came from, or "" for notes the playground wrote.</summary>
    public string SourcePath { get; set; } = "";
    /// <summary>The library part's note hash, so the same part isn't suggested again.</summary>
    public string SourceHash { get; set; } = "";

    [JsonIgnore] public long End => Start + Length;

    [JsonIgnore]
    public IReadOnlyList<NoteEvent> Notes
    {
        get => Data.Length == 0 ? [] : MidiIndex.DecodeNotes(Convert.FromBase64String(Data));
        set => Data = value.Count == 0 ? "" : Convert.ToBase64String(MidiIndex.EncodeNotes(value.OrderBy(n => n.Start).ThenBy(n => n.Key).ToList()));
    }

    /// <summary>How many go rounds it plays, counting a cut-short first or last one.</summary>
    [JsonIgnore]
    public int Repeats => Loop <= 0 ? 0 : (int)((Offset + Length + Loop - 1) / Loop - Offset / Loop);

    /// <summary>How far go round <paramref name="repeat"/> is moved, by hand and to fit.</summary>
    public int Shift(int repeat) => Transpose + (repeat >= 0 && repeat < Fits.Count ? Fits[repeat] : 0);

    /// <summary>Where go round <paramref name="repeat"/>'s first tick would be on the timeline (before the start's trim).</summary>
    public long RepeatAt(int repeat) => Start - Offset % Math.Max(1, Loop) + repeat * Loop;

    /// <summary>The notes as they play on the timeline: each go round placed, moved, and cut at the clip's edges.</summary>
    public List<NoteEvent> Render() => Render(Notes);

    /// <summary>As <see cref="Render()"/>, with <paramref name="source"/> as one go round (to try notes out before keeping them).</summary>
    public List<NoteEvent> Render(IReadOnlyList<NoteEvent> source)
    {
        var list = new List<NoteEvent>();
        if (Loop <= 0 || Length <= 0 || source.Count == 0) return list;
        long from = Offset, to = Offset + Length;
        var first = from / Loop;
        for (var r = first; r * Loop < to; r++)
        {
            var shift = Shift((int)(r - first));
            foreach (var n in source)
            {
                if (n.Start >= Loop) continue;
                long s = r * Loop + n.Start, e = Math.Min(s + n.Length, to);
                if (e <= from || s >= to) continue;
                s = Math.Max(s, from);   // a note held into the trimmed start sounds from it
                list.Add(n with { Start = Start + s - from, Length = e - s, Key = Math.Clamp(n.Key + shift, 0, 127) });
            }
        }
        list.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Key.CompareTo(b.Key));
        return list;
    }

    public PlaygroundClip Copy()
    {
        var c = (PlaygroundClip)MemberwiseClone();
        c.Fits = [.. Fits];
        return c;
    }

    /// <summary>A clip of <paramref name="notes"/> (one go round of <paramref name="loop"/> ticks) at <paramref name="start"/>, playing for <paramref name="length"/>.</summary>
    public static PlaygroundClip Of(string name, string role, IReadOnlyList<NoteEvent> notes, long loop, long start, long length,
                                    string sourcePath = "", string hash = "") => new()
    {
        Name = name, Role = role, Notes = notes, Loop = Math.Max(1, loop), Start = Math.Max(0, start), Length = Math.Max(1, length),
        SourcePath = sourcePath, SourceHash = hash,
    };
}

/// <summary>A name stretched over some bars of the timeline (Verse, Build, Drop…): what those bars are for.</summary>
public sealed class PlaygroundLabel
{
    public string Name { get; set; } = "Verse";
    public long Start { get; set; }
    public long Length { get; set; } = 4 * ChordLane.Bar;
    /// <summary>Suggestions for bars under the label are ranked on how well their energy suits this.</summary>
    public SectionFeel Feel { get; set; }

    [JsonIgnore] public long End => Start + Length;

    public PlaygroundLabel Copy() => (PlaygroundLabel)MemberwiseClone();
}

/// <summary>
/// Shift to fit (Solomon, 2026-10-19: "a clip moved to new chords transposes itself to the best fit"): how far to move
/// a clip so it sits best on the chords under it, judged go round by go round with the same chord-scale ear as the
/// red clash marks.
/// </summary>
public static class ClipFit
{
    /// <summary>
    /// The move (−6 to +5 semitones) that sits <paramref name="notes"/> best on <paramref name="lane"/>: each note weighed
    /// by how long it sounds, valued by <see cref="ChordScales.Sit"/>. The smaller move wins a tie, and no chords means no move.
    /// </summary>
    public static int Best(IReadOnlyList<NoteEvent> notes, IReadOnlyList<TimedChord> lane, long length, HarmonyStyle style)
    {
        if (notes.Count == 0 || lane.Count == 0) return 0;
        int best = 0;
        var bestScore = double.MinValue;
        foreach (var s in new[] { 0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, -6 })
        {
            double score = 0;
            foreach (var n in notes)
                if (ChordLane.At(lane, n.Start + 1, length) is { } chord)
                    score += Math.Min(n.Length, ChordLane.Bar) * ChordScales.Sit(chord, ChordSymbol.Mod(n.Key + s), style);
            if (score > bestScore + 1e-9) { best = s; bestScore = score; }
        }
        return best;
    }
}
