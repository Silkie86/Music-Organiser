using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi.Playground;

/// <summary>
/// How a part follows the chord track (Solomon, 2026-10-22: "there should be a few different options, some might fit
/// better for different types of music"). Each clip has its own; <see cref="Auto"/> picks by what the part plays.
/// </summary>
public enum FollowWay
{
    /// <summary>Chord parts and arps keep their place in the chord; drums don't follow; everything else nudges clashes.</summary>
    Auto,
    /// <summary>The line stays as written (moved whole to fit, as before), and only notes that clash move to the nearest note that fits: melodies, soul, pop.</summary>
    Nudge,
    /// <summary>A root stays a root and a 3rd a 3rd, so the part reshapes for each chord: chord parts re-voiced, gospel and funk bass, arps.</summary>
    Degree,
    /// <summary>The whole part moves up or down with the chord's root, note for note: riffs and ostinatos.</summary>
    Root,
    /// <summary>Stays exactly as it is: a pedal note, a drone.</summary>
    None,
}

/// <summary>
/// Chords first, step 2 (Solomon, 2026-10-22): parts follow the chords on the chord track. A part's notes are heard
/// against the chords they were written over (read from the part itself) and played on the chord track's chords instead,
/// in the way its <see cref="FollowWay"/> says. Where the track has no chord, the notes play as they are.
/// </summary>
public static class ChordFollow
{
    const int Ppq = PhraseAnalyser.Ppq, Bar = 4 * Ppq;

    public static FollowWay Resolve(FollowWay way, string role) => way != FollowWay.Auto ? way : role switch
    {
        MidiRoles.Chords or MidiRoles.Arp => FollowWay.Degree,
        MidiRoles.Drums => FollowWay.None,
        _ => FollowWay.Nudge,
    };

    /// <summary>What each way is called in menus and the inspector.</summary>
    public static string Label(FollowWay way) => way switch
    {
        FollowWay.Nudge => "Nudge clashes",
        FollowWay.Degree => "Keep its place in the chord",
        FollowWay.Root => "Move with the root",
        FollowWay.None => "Don't follow",
        _ => "As its kind suits",
    };

    /// <summary>The same, short enough for the inspector's list.</summary>
    public static string ShortLabel(FollowWay way) => way switch
    {
        FollowWay.Degree => "Keep its place",
        FollowWay.Root => "Move with root",
        _ => Label(way),
    };

    /// <summary>
    /// <paramref name="notes"/> (placed on the timeline) played on <paramref name="track"/>'s chords, the way
    /// <paramref name="way"/> says; <paramref name="role"/> is what the part plays (a chord part's own chords are read
    /// from its notes, a line's from the notes it plays over each chord of the track).
    /// </summary>
    public static List<NoteEvent> Apply(IReadOnlyList<NoteEvent> notes, IReadOnlyList<TimedChord> track, FollowWay way, string role,
                                        HarmonyStyle style = HarmonyStyle.JazzGospel)
    {
        way = Resolve(way, role);
        if (notes.Count == 0 || track.Count == 0 || way == FollowWay.None) return notes.ToList();
        var sorted = notes.OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
        // Which chord of the track each note plays over (null: none there).
        var over = sorted.Select(n => Over(track, n.Start, n.Length)).ToList();
        var list = new List<NoteEvent>(sorted.Count);
        if (way == FollowWay.Nudge)
        {
            for (var i = 0; i < sorted.Count; i++) list.Add(over[i] is { } t ? Nudge(sorted[i], track[t].Chord, style) : sorted[i]);
            return Sorted(list);
        }
        var chordal = role is MidiRoles.Chords or MidiRoles.Arp || role == MidiRoles.Other && Polyphonic(sorted);
        if (chordal)
        {
            // A chord part's own chords, as the app's ear hears them; each hit is played on the track's chord instead,
            // a chord held over a change of the track's changing with it.
            var own = ChordReader.Read(sorted, sorted.Max(n => n.Start + n.Length));
            foreach (var hit in Groove.Hits(Split(sorted, track)))
            {
                var t = Over(track, hit.Start, hit.Length);
                var s = Over(own, hit.Start, hit.Length);
                if (t is null || s is null) { list.AddRange(hit.Notes.Select(n => t is { } c ? Nudge(n, track[c].Chord, style) : n)); continue; }
                // A hit that already sits on the track's chord stays as it is, as a line does: a rootless voicing over the
                // bass's root (E G B D over C) is heard alone as another chord (Em7), but it is already the track's Cmaj9.
                if (SitsOn(hit.Notes, track[t.Value].Chord, own[s.Value].Chord, style)) { list.AddRange(hit.Notes); continue; }
                list.AddRange(Play(hit.Notes, own[s.Value].Chord, track[t.Value].Chord, way));
            }
            return Sorted(list);
        }
        // A line: the chord it was written over is read from the notes it plays over each chord of the track.
        var bass = role == MidiRoles.Bass;
        foreach (var group in Enumerable.Range(0, sorted.Count).GroupBy(i => over[i]))
        {
            var part = group.Select(i => sorted[i]).ToList();
            if (group.Key is not { } t) { list.AddRange(part); continue; }
            var target = track[t].Chord;
            if (Implied(part, target, bass) is not { } from) { list.AddRange(part.Select(n => Nudge(n, target, style))); continue; }
            foreach (var n in part) list.AddRange(Play([n], from, target, way));
        }
        return Sorted(list);
    }

    /// <summary>
    /// Whether <paramref name="notes"/> sit on the track's chord <paramref name="target"/> about as well as on the chord
    /// they were heard as (<paramref name="heard"/>): then there is nothing to change.
    /// </summary>
    static bool SitsOn(IReadOnlyList<NoteEvent> notes, ChordSymbol target, ChordSymbol heard, HarmonyStyle style)
    {
        if (target.Root == heard.Root && target.Mask == heard.Mask) return true;
        double Fit(ChordSymbol c) => notes.Average(n => ChordScales.Sit(c, n.Key % 12, style));
        return Fit(target) + 0.1 >= Fit(heard) - 1e-9;
    }

    /// <summary>Notes held over a change of chord on the track cut in two there (more than an eighth either side).</summary>
    static List<NoteEvent> Split(IReadOnlyList<NoteEvent> notes, IReadOnlyList<TimedChord> track)
    {
        var list = new List<NoteEvent>(notes.Count);
        foreach (var n in notes)
        {
            var piece = n;
            foreach (var c in track)
                if (c.Start > piece.Start + Ppq / 2 && c.Start < piece.Start + piece.Length - Ppq / 2)
                {
                    list.Add(piece with { Length = c.Start - piece.Start });
                    piece = piece with { Start = c.Start, Length = piece.Start + piece.Length - c.Start };
                }
            list.Add(piece);
        }
        return Sorted(list);
    }

    static List<NoteEvent> Sorted(List<NoteEvent> list)
    {
        list.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Key.CompareTo(b.Key));
        return list;
    }

    static bool Polyphonic(IReadOnlyList<NoteEvent> notes)
    {
        var hits = Groove.Hits(notes).ToList();
        return hits.Count(h => h.Notes.Count >= 3) * 3 >= hits.Count;
    }

    /// <summary>Notes struck over chord <paramref name="from"/> played on <paramref name="to"/>.</summary>
    static List<NoteEvent> Play(IReadOnlyList<NoteEvent> hit, ChordSymbol from, ChordSymbol to, FollowWay way)
    {
        if (way == FollowWay.Root)
        {
            var shift = ChordSymbol.Mod(to.Root - from.Root);
            if (shift > 5) shift -= 12;
            return hit.Select(n => n with { Key = Math.Clamp(n.Key + shift, 0, 127) }).ToList();
        }
        if (hit.Count > 1) return Groove.Revoice(hit, from, to);
        // A single note: the bass of the old chord is the bass of the new one; everything else keeps its place.
        var note = hit[0];
        var moveBy = ChordSymbol.Mod(to.Root - from.Root);
        if (moveBy > 6) moveBy -= 12;
        var pc = note.Key % 12 == from.Bass ? to.Bass : ChordSymbol.Mod(to.Root + Groove.Map(from, to, ChordSymbol.Mod(note.Key - from.Root)));
        return [note with { Key = Groove.Nearest(pc, note.Key + moveBy) }];
    }

    /// <summary>
    /// A note that fights the chord (held longer than an eighth on a note the chord doesn't take, as the red marks say)
    /// moved to the nearest note that sits on it; passing notes and notes that fit stay where they are.
    /// </summary>
    static NoteEvent Nudge(NoteEvent n, ChordSymbol chord, HarmonyStyle style)
    {
        if (n.Length <= Ppq / 2 || ChordScales.Sit(chord, n.Key % 12, style) >= 0.05) return n;
        var best = 0;
        var bestSit = ChordScales.Sit(chord, n.Key % 12, style);
        foreach (var s in new[] { -1, 1, -2, 2 })
        {
            var sit = ChordScales.Sit(chord, ChordSymbol.Mod(n.Key + s), style);
            if (sit > bestSit + 1e-9) { best = s; bestSit = sit; }
        }
        return n with { Key = Math.Clamp(n.Key + best, 0, 127) };
    }

    /// <summary>
    /// The chord of <paramref name="track"/> a note or hit plays over: the one sounding where it starts, or the next when
    /// it pushes into it (struck up to an eighth before the change and held over it), as players push into a change.
    /// </summary>
    static int? Over(IReadOnlyList<TimedChord> track, long start, long length)
    {
        for (var i = 0; i < track.Count; i++)
        {
            var c = track[i];
            if (c.Start > start && c.Start - start <= Ppq / 2 && start + length > c.Start + Ppq / 8) return i;
        }
        for (var i = 0; i < track.Count; i++)
            if (start >= track[i].Start && start < track[i].End) return i;
        return null;
    }

    /// <summary>The chords a line can imply, on each of the 12 roots: made once, as they're tried for every chord a line plays over.</summary>
    static readonly (ChordSymbol Chord, int Plainness)[][] Implies = Enumerable.Range(0, 12).Select(root =>
        new[] { "", "m", "7", "m7", "maj7", "m7b5" }
            .Select(kind => (Chord: ChordSymbol.Parse(ChordSymbol.Names[root] + kind), kind.Length))
            .Where(k => k.Chord is not null).Select(k => (k.Chord!.Value, k.Length)).ToArray()).ToArray();

    /// <summary>
    /// The chord a line's notes imply (what they were written over): the one they sit on best, held longest weighing
    /// most. The track's own chord wins a near tie, so a line that already fits stays as it is; a bassline's chord has
    /// its first or lowest note as the root (a riff on C moved onto F moves, though C is F's fifth).
    /// </summary>
    static ChordSymbol? Implied(IReadOnlyList<NoteEvent> notes, ChordSymbol target, bool bass)
    {
        var weight = notes.Sum(n => (double)Math.Min(n.Length, Bar));
        if (weight <= 0) return null;
        var roots = bass ? new[] { notes[0].Key % 12, notes.MinBy(n => n.Key).Key % 12 }.Distinct().ToArray() : Enumerable.Range(0, 12).ToArray();
        ChordSymbol? best = null;
        var bestScore = double.MinValue;
        foreach (var root in roots)
            foreach (var (c, plainness) in Implies[root])
            {
                var score = notes.Sum(n => Math.Min(n.Length, Bar) * ChordScales.Sit(c, n.Key % 12)) / weight;
                score -= 0.02 * plainness;   // the plainer chord, when a seventh adds nothing
                if (score > bestScore) { best = c; bestScore = score; }
            }
        // The track's chord itself, when the notes sit on it as well as on anything (and, for a bassline, start on or
        // go down to its root): nothing to change.
        if (bass && !roots.Contains(target.Root)) return best;
        var own = notes.Sum(n => Math.Min(n.Length, Bar) * ChordScales.Sit(target, n.Key % 12)) / weight + 0.1;
        return own >= bestScore - 1e-9 ? target : best;
    }
}
