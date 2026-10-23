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
    /// <summary>How many times its chords are turned over by hand: up (bottom note up an octave) or down.</summary>
    public int Inversion { get; set; }
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
    /// <summary>How it follows the chord track (Solomon, 2026-10-22): its own choice, or what suits its kind.</summary>
    public FollowWay Follow { get; set; }
    /// <summary>What it plays as: its own kind, or its track's when it has none (set when it's put on a track).</summary>
    public string PlaysAs { get; set; } = "";
    /// <summary>
    /// The chord track's chords under it that it follows, from its start, set when it's put down and when the chord
    /// track changes. A clip changed by hand keeps the ones it had, so it stays as it is, wherever it's moved.
    /// </summary>
    public List<PlaygroundChord> Under { get; set; } = [];
    /// <summary>Which idea of a clash it follows the chords by (the idea's Classical box), set with <see cref="Under"/>.</summary>
    public HarmonyStyle Style { get; set; }

    /// <summary>Its start moved by <paramref name="by"/> with its notes staying where they are (trimmed, or cut in two).</summary>
    public void StartMoved(long by)
    {
        foreach (var c in Under) c.Start -= by;
    }

    [JsonIgnore] public long End => Start + Length;

    /// <summary>The way it follows the chords, its kind deciding when it hasn't been picked.</summary>
    [JsonIgnore] public FollowWay Way => ChordFollow.Resolve(Follow, PlaysAs.Length > 0 ? PlaysAs : Role);

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

    /// <summary>
    /// As <see cref="Render()"/>, with <paramref name="source"/> as one go round (to try notes out before keeping them).
    /// Following the chord track comes after the moves to fit and before the moves by hand, so an octave or a semitone
    /// up by hand moves what it plays on the chords.
    /// </summary>
    public List<NoteEvent> Render(IReadOnlyList<NoteEvent> source)
    {
        if (Under.Count == 0 || Way == FollowWay.None) return Place(source, Transpose);
        var followed = ChordFollow.Apply(Place(source, 0), Under.Select(c => new TimedChord(Start + c.Start, c.Length, c.Chord)).ToList(), Way,
                                         PlaysAs.Length > 0 ? PlaysAs : Role, Style);
        return Transpose == 0 ? followed : followed.Select(n => n with { Key = Math.Clamp(n.Key + Transpose, 0, 127) }).ToList();
    }

    /// <summary>Each go round placed, moved by <paramref name="transpose"/> and to fit, and cut at the clip's edges.</summary>
    List<NoteEvent> Place(IReadOnlyList<NoteEvent> source, int transpose)
    {
        var list = new List<NoteEvent>();
        if (Loop <= 0 || Length <= 0 || source.Count == 0) return list;
        if (Inversion != 0) source = Inversions.Turn(source, Inversion);
        long from = Offset, to = Offset + Length;
        var first = from / Loop;
        for (var r = first; r * Loop < to; r++)
        {
            var repeat = (int)(r - first);
            var shift = transpose + (repeat < Fits.Count ? Fits[repeat] : 0);
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

    /// <summary>One go round as it first plays, from 0: moved, turned and following the chords as it is on the timeline.</summary>
    public List<NoteEvent> FirstRound()
    {
        var c = Copy();
        c.Start = RepeatAt(0);
        c.StartMoved(c.Start - Start);   // the chords it follows stay where they are on the timeline
        c.Offset = Offset - Offset % Math.Max(1, Loop);
        c.Length = Loop;
        return c.Render().Select(n => n with { Start = n.Start - c.Start }).ToList();
    }

    /// <summary>
    /// Its notes made to play exactly as they do now, as one go round as long as the clip: nothing moved, turned or
    /// following any more (before it's changed by hand, so it stays as it is).
    /// </summary>
    public void Bake()
    {
        Notes = Render().Select(n => n with { Start = n.Start - Start }).ToList();
        Loop = Length;
        Offset = 0;
        Transpose = 0;
        Inversion = 0;
        Fits = [];
        Under = [];
    }

    public PlaygroundClip Copy()
    {
        var c = (PlaygroundClip)MemberwiseClone();
        c.Fits = [.. Fits];
        c.Under = Under.Select(u => u.Copy()).ToList();
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
/// One chord on the chord track (Solomon, 2026-10-22: "extract chord progressions from your chord library so it will go
/// on the chord track but not place any MIDI"): a chord and when it plays, with no notes of its own. Where the track has
/// chords they're the harmony; everywhere else the chords are heard from the parts.
/// </summary>
public sealed class PlaygroundChord
{
    public long Start { get; set; }
    public long Length { get; set; } = ChordLane.Bar;
    public int Root { get; set; }
    /// <summary>Every note in the chord, a 12-bit set (bit 0 = C), as <see cref="ChordSymbol.Mask"/>.</summary>
    public int Mask { get; set; }
    public int Bass { get; set; }
    public ChordFamily Family { get; set; }

    [JsonIgnore] public long End => Start + Length;

    [JsonIgnore]
    public ChordSymbol Chord
    {
        get => new(Root, Mask, Bass, Family);
        set { Root = value.Root; Mask = value.Mask; Bass = value.Bass; Family = value.Family; }
    }

    [JsonIgnore] public TimedChord Timed => new(Start, Length, Chord);

    public static PlaygroundChord Of(TimedChord c) => new() { Start = c.Start, Length = c.Length, Chord = c.Chord };

    public PlaygroundChord Copy() => (PlaygroundChord)MemberwiseClone();
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
    /// <remarks>Each semitone moved costs a little (<paramref name="cost"/> of what a perfect fit scores), so a part only
    /// moves when it sits clearly better, and stays put when it already fits.</remarks>
    public static int Best(IReadOnlyList<NoteEvent> notes, IReadOnlyList<TimedChord> lane, long length, HarmonyStyle style, double cost = 0.03)
    {
        if (notes.Count == 0 || lane.Count == 0) return 0;
        var weight = notes.Sum(n => (double)Math.Min(n.Length, ChordLane.Bar));
        int best = 0;
        var bestScore = double.MinValue;
        foreach (var s in new[] { 0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, -6 })
        {
            var score = -cost * Math.Abs(s) * weight;
            foreach (var n in notes)
                if (ChordLane.At(lane, n.Start + 1, length) is { } chord)
                    score += Math.Min(n.Length, ChordLane.Bar) * ChordScales.Sit(chord, ChordSymbol.Mod(n.Key + s), style);
            if (score > bestScore + 1e-9) { best = s; bestScore = score; }
        }
        return best;
    }

    /// <summary>
    /// How far each go round of <paramref name="clip"/> moves to fit <paramref name="lane"/> (the chords from the other
    /// parts, across the whole timeline), judged on the notes of that go round that play.
    /// </summary>
    public static List<int> Repeats(PlaygroundClip clip, IReadOnlyList<TimedChord> lane, HarmonyStyle style)
    {
        var source = clip.Notes;
        var fits = new List<int>();
        for (var r = 0; r < clip.Repeats; r++)
        {
            var at = clip.RepeatAt(r);
            var playing = source.Where(n => at + n.Start >= clip.Start && at + n.Start < clip.End)
                                .Select(n => n with { Key = n.Key + clip.Transpose }).ToList();
            var under = lane.Where(c => c.End > at && c.Start < at + clip.Loop)
                            .Select(c => new TimedChord(Math.Max(c.Start, at) - at, Math.Min(c.End, at + clip.Loop) - Math.Max(c.Start, at), c.Chord)).ToList();
            fits.Add(Best(playing, under, clip.Loop, style));
        }
        return fits;
    }
}
