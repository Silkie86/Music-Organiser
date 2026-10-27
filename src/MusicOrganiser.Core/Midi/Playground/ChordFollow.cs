using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi.Playground;

/// <summary>
/// How a part follows the chord track (Solomon, 2026-10-22: "there should be a few different options, some might fit
/// better for different types of music"). Each clip has its own; <see cref="Auto"/> picks by what the part plays.
/// </summary>
public enum FollowWay
{
    /// <summary>Chord parts, arps and basslines keep their place in the chord; drums don't follow; everything else nudges clashes.</summary>
    Auto,
    /// <summary>The line stays as written (moved whole to fit, as before), and only notes that clash move to the nearest note that fits: melodies, soul, pop.</summary>
    Nudge,
    /// <summary>A root stays a root and a 3rd a 3rd, so the part reshapes for each chord: chord parts re-voiced, gospel and funk bass, arps.</summary>
    Degree,
    /// <summary>The whole part moves up or down with the chord's root, note for note, in the new chord's quality: riffs and ostinatos.</summary>
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

    /// <remarks>
    /// A bassline keeps its place (Solomon, 2026-10-27: "seems to be making silly decisions musically"): only nudging its
    /// clashes left a bass on G under a D♭9, the old root still sounding under the new chord.
    /// </remarks>
    public static FollowWay Resolve(FollowWay way, string role) => way != FollowWay.Auto ? way : role switch
    {
        MidiRoles.Chords or MidiRoles.Arp or MidiRoles.Bass => FollowWay.Degree,
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
            // a chord held over a change of the track's changing with it. A hit the ear can't name on its own chord (an
            // open fifth, two notes) is heard as the chord its own notes make.
            var own = ChordReader.Read(sorted, sorted.Max(n => n.Start + n.Length));
            var hits = Groove.Hits(Split(sorted, track)).ToList();
            var onTrack = hits.Select(h => Over(track, h.Start, h.Length)).ToList();
            var heard = hits.Select(h => Over(own, h.Start, h.Length) is { } s ? own[s].Chord : Alone(h.Notes)).ToList();
            // Moving with the root moves a riff or an arp note for note (Solomon, 2026-10-23: ARP House on "Move with root"),
            // and an arp's single notes keep their place together: everything played over one chord of the track is judged
            // as one, not a note at a time, which would leave the notes that happen to fit behind (one note alone fits most
            // chords: C over F stays, so F is never played).
            bool Together(int i) => way == FollowWay.Root || hits[i].Notes.Count == 1;
            var groups = Enumerable.Range(0, hits.Count).Where(i => onTrack[i] is not null && Together(i))
                .GroupBy(i => (T: onTrack[i]!.Value, S: heard[i]))
                .ToDictionary(g => g.Key, g => g.SelectMany(i => hits[i].Notes).Select(n => n.Key).Distinct().Order().ToArray());
            // How each group's notes move: none when they already voice the track's chord.
            var moves = groups.ToDictionary(g => g.Key, g => Moves(g.Value, g.Key.S, track[g.Key.T].Chord, way, style));
            // A note struck alone a fifth or more under everything else the part plays over that chord is a left hand's
            // bass, as Revoice hears one struck with the chord: it plays a slash chord's bass either way.
            var keysOver = Enumerable.Range(0, hits.Count).GroupBy(i => heard[i])
                .ToDictionary(g => g.Key, g => g.SelectMany(i => hits[i].Notes).Select(n => n.Key).Distinct().Order().ToList());
            bool Apart(int i) => hits[i].Notes.Count == 1 && keysOver[heard[i]] is var keys &&
                                 keys[0] == hits[i].Notes[0].Key && keys.Count > 1 && keys[1] - keys[0] >= 7;
            var voiced = new Dictionary<(string Keys, ChordSymbol From, ChordSymbol To), int[]>();
            for (var i = 0; i < hits.Count; i++)
            {
                var hit = hits[i];
                if (onTrack[i] is not { } t) { list.AddRange(hit.Notes); continue; }
                var target = track[t].Chord;
                var from = heard[i];
                if (Together(i))
                {
                    if (moves[(t, from)] is not { } move) { list.AddRange(hit.Notes); continue; }
                    // A left hand's bass on its own plays the new chord's bass.
                    if (Apart(i) && way != FollowWay.Root) { list.AddRange(Play(hit.Notes, from, target, way, apart: true)); continue; }
                    list.AddRange(hit.Notes.Select(n => n with { Key = move.TryGetValue(n.Key, out var k) ? k : n.Key }));
                    continue;
                }
                // A hit that already voices the track's chord stays as it is: a rootless voicing over the bass's root (E G B D
                // over C) is heard alone as another chord (Em7), but it is already the track's Cmaj9.
                var struck = hit.Notes.Select(n => n.Key).Distinct().Order().ToArray();
                if (SitsOn(struck, target, from, style)) { list.AddRange(hit.Notes); continue; }
                var key = (string.Join(",", struck), from, target);
                if (!voiced.TryGetValue(key, out var placed)) voiced[key] = placed = Revoicer.Keys(struck, from, target);
                var map = struck.Zip(placed).ToDictionary(p => p.First, p => p.Second);
                var used = new HashSet<int>();
                foreach (var n in hit.Notes.OrderBy(n => n.Key))
                    if (used.Add(map[n.Key])) list.Add(n with { Key = map[n.Key] });
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
            if (Implied(part, target, bass, way != FollowWay.Root) is not { } from) { list.AddRange(part.Select(n => Nudge(n, target, style))); continue; }
            foreach (var n in part) list.AddRange(Play([n], from, target, way, bass));
        }
        return Sorted(list);
    }

    /// <summary>
    /// Whether <paramref name="keys"/>, heard as <paramref name="heard"/>, already play the track's chord
    /// <paramref name="target"/> (<see cref="Revoicer.Voices"/>): then there is nothing to change. A suspension over its own
    /// chord (Csus4 where the track has C) resolves by itself, so it stays too.
    /// </summary>
    static bool SitsOn(IReadOnlyList<int> keys, ChordSymbol target, ChordSymbol heard, HarmonyStyle style)
    {
        if (target.Root == heard.Root && target.Mask == heard.Mask) return true;
        if (heard.Root == target.Root && heard.Family == ChordFamily.Sus && target.Family is ChordFamily.Major or ChordFamily.Dominant) return true;
        var fromRoot = keys.Count > 0 && (keys[0] % 12 == heard.Root || heard.Bass != heard.Root && keys[0] % 12 == heard.Bass);
        return Revoicer.Voices(keys, target, fromRoot, style);
    }

    /// <summary>
    /// Where each of <paramref name="keys"/> (all a part plays over one chord of the track, heard as <paramref name="from"/>)
    /// goes on <paramref name="to"/>, judged together; null when they already play it. Moving with the root moves them all
    /// by the change of root; otherwise each keeps its place in the chord, so an arp keeps its shape, and an arp that
    /// leaves out what the new chord needs is voiced as one chord from there (<see cref="Revoicer"/>).
    /// </summary>
    static Dictionary<int, int>? Moves(int[] keys, ChordSymbol from, ChordSymbol to, FollowWay way, HarmonyStyle style)
    {
        if (way == FollowWay.Root)
        {
            if (from.Root == to.Root && from.Mask == to.Mask) return null;
            return keys.ToDictionary(k => k, k => Play([new NoteEvent(0, 1, k, 1)], from, to, way)[0].Key);
        }
        if (SitsOn(keys, to, from, style)) return null;
        // The root stays the root and the 3rd the 3rd (on a slash chord too: its bass is the bass part's).
        var kept = keys.ToDictionary(k => k, k => Play([new NoteEvent(0, 1, k, 1)], from, to, way)[0].Key);
        if (keys.Length < 2 || keys[^1] < 52) return kept;   // a bass's low notes
        // Notes that outline the new chord but leave out what it needs, its 7th or the 9 of a richer chord (Solomon,
        // 2026-10-27: "doesnt seem to change the chord when i chose richer chords"), move to it, as few and as little as
        // they can, the root staying at the bottom. A line's passing notes keep their places.
        var placed = kept.Values.Distinct().Order().ToArray();
        var own = Revoicer.Telling(to).Append(to.Bass).ToHashSet();
        if (!placed.All(k => own.Contains(k % 12)) || Revoicer.Voices(placed, to, fromRoot: false, style)) return kept;
        var map = placed.Zip(Revoicer.Keys(placed, to, to, slashBass: false)).ToDictionary(p => p.First, p => p.Second);
        return kept.ToDictionary(p => p.Key, p => map[p.Value]);
    }

    /// <summary>
    /// The chord a hit's notes make on their own, for a hit the ear can't name on the part's chords: its lowest note the
    /// root, major or minor by its third, with its seventh, or a power chord when it has no third.
    /// </summary>
    static ChordSymbol Alone(IReadOnlyList<NoteEvent> notes)
    {
        var root = notes.Min(n => n.Key) % 12;
        var iv = notes.Aggregate(0, (m, n) => m | 1 << ChordSymbol.Mod(n.Key - root));
        bool Has(int i) => (iv >> i & 1) != 0;
        var kind = Has(4) ? Has(10) ? "7" : Has(11) ? "maj7" : "" : Has(3) ? Has(10) ? "m7" : "m" : "5";
        return ChordSymbol.Parse(ChordSymbol.Names[root] + kind) ?? ChordSymbol.Parse(ChordSymbol.Names[root])!.Value;
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

    /// <summary>
    /// Notes struck over chord <paramref name="from"/> played on <paramref name="to"/>; <paramref name="bass"/> for a bassline's,
    /// <paramref name="apart"/> for a chord part's single note played as its left hand's bass.
    /// </summary>
    static List<NoteEvent> Play(IReadOnlyList<NoteEvent> hit, ChordSymbol from, ChordSymbol to, FollowWay way, bool bass = false, bool apart = false)
    {
        if (way == FollowWay.Root)
        {
            var shift = ChordSymbol.Mod(to.Root - from.Root);
            if (shift > 5) shift -= 12;
            return hit.Select(n => n with { Key = Math.Clamp(Moved(n.Key, shift, from, to), 0, 127) }).ToList();
        }
        if (hit.Count > 1) return Groove.Revoice(hit, from, to);
        // A single note: the bass of the old chord is the bass of the new one (a bassline's root, an old slash chord's bass,
        // or a left hand's root played apart); everything else keeps its place, so an arp's root stays the root on a slash chord.
        var note = hit[0];
        var moveBy = ChordSymbol.Mod(to.Root - from.Root);
        if (moveBy > 6) moveBy -= 12;
        if (moveBy == 6 && bass && note.Key - 6 >= 28) moveBy = -6;   // a tritone either way: a bass goes down, while it stays a bass
        var isBass = note.Key % 12 == from.Bass && (bass || from.Bass != from.Root) || apart && note.Key % 12 == from.Root;
        var pc = isBass ? to.Bass : ChordSymbol.Mod(to.Root + Groove.Map(from, to, ChordSymbol.Mod(note.Key - from.Root)));
        return [note with { Key = Groove.Nearest(pc, note.Key + moveBy) }];
    }

    /// <summary>
    /// <paramref name="key"/> moved with the root by <paramref name="shift"/>, note for note, but as the new chord has it:
    /// its own 3rd, 5th and 7th where the old chord's were (a C riff's E is F, not F#, on Dm), and a note it fights a
    /// semitone to one it takes. A riff in C major moved onto D minor stays in the key.
    /// </summary>
    static int Moved(int key, int shift, ChordSymbol from, ChordSymbol to)
    {
        var moved = key + shift;
        var iv = ChordSymbol.Mod(key - from.Root);
        var change = ChordSymbol.Mod(Groove.Map(from, to, iv) - iv);
        if (change is 1 or 11) return moved + (change == 1 ? 1 : -1);
        if (ChordScales.Sit(to, ChordSymbol.Mod(moved)) >= 0.05) return moved;
        return ChordScales.Sit(to, ChordSymbol.Mod(moved - 1)) >= ChordScales.Sit(to, ChordSymbol.Mod(moved + 1)) ? moved - 1 : moved + 1;
    }

    /// <summary>
    /// A note that doesn't sit on the chord moved to the nearest note that does, a chord note before a colour: one held a
    /// beat or more or struck on beat 1 or 3 needs to sit well (a 9 or a 13, not an avoid note or a rub: Solomon,
    /// 2026-10-27, "its not favouring good choices just seems like if it has a number associated it will accept it"); a
    /// shorter one off the beat only needs not to fight it, as the red marks say. Quick passing notes stay where they are.
    /// </summary>
    static NoteEvent Nudge(NoteEvent n, ChordSymbol chord, HarmonyStyle style)
    {
        if (n.Length <= Ppq / 2) return n;
        var prominent = n.Length >= Ppq || n.Start % (2 * Ppq) == 0;
        if (ChordScales.Sit(chord, n.Key % 12, style) >= (prominent ? 0.4 : 0.05)) return n;
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
    /// most. The track's own chord wins a near tie (<paramref name="keepFit"/>), so a line that already fits stays as it
    /// is; never for one moving with the root, which moves as it was written (an A minor riff onto D7 moves, though A C E
    /// sit on D9). A bassline's chord has its first or lowest note as the root (a riff on C moved onto F moves, though C
    /// is F's fifth).
    /// </summary>
    static ChordSymbol? Implied(IReadOnlyList<NoteEvent> notes, ChordSymbol target, bool bass, bool keepFit = true)
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
        if (!keepFit || bass && !roots.Contains(target.Root)) return best;
        var own = notes.Sum(n => Math.Min(n.Length, Bar) * ChordScales.Sit(target, n.Key % 12)) / weight + 0.1;
        return own >= bestScore - 1e-9 ? target : best;
    }
}
