using System.Collections.Concurrent;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Midi.Playground;

/// <summary>
/// What a section sounds like as a whole: every part that plays (drums aside) on top of each other, the chords they
/// make (or the chords typed for it), its key, and the songs its parts came from.
/// </summary>
public sealed class SectionHarmony
{
    /// <summary>Below this a heard key is a guess (see <see cref="MusicTheory.EstimateMidiKey"/>): the MIDI page hides it too.</summary>
    public const double MinKeyConfidence = MusicTheory.SureMidiKey;

    public required IReadOnlyList<NoteEvent> Notes { get; init; }
    public required IReadOnlyList<TimedChord> Lane { get; init; }
    public required long Length { get; init; }
    public KeyEstimate? Key { get; init; }
    public bool KeyTyped { get; init; }
    public bool ChordsTyped { get; init; }
    public HarmonyStyle Style { get; init; }
    /// <summary>The songs the section's parts came from (<see cref="MidiEntry.SongOf"/>): never suggested.</summary>
    public required IReadOnlyCollection<string> Songs { get; init; }
    /// <summary>The library parts already in it, by note hash.</summary>
    public required IReadOnlySet<string> Hashes { get; init; }

    public bool IsEmpty => Notes.Count == 0 && Lane.Count == 0;

    /// <summary>The section's harmony, worked out from its parts, its typed chart and its typed key.</summary>
    public static SectionHarmony Of(PlaygroundSection section, string typedKey = "", HarmonyStyle style = HarmonyStyle.JazzGospel,
                                    IReadOnlyCollection<PlaygroundLayer>? leaveOut = null)
    {
        var length = section.Length;
        var layers = PlaygroundSession.Audible(section).Where(l => l.Role != MidiRoles.Drums && (leaveOut is null || !leaveOut.Contains(l))).ToList();
        var notes = layers.SelectMany(l => l.Notes.Where(n => n.Start < length && !n.Muted)).OrderBy(n => n.Start).ToList();

        List<TimedChord>? lane = null;
        var typed = section.Chart.Trim().Length > 0;
        if (typed) lane = ChordLane.ParseChart(section.Chart, out _);
        if (lane is not null)
        {
            // A chart shorter than the section goes round.
            var chartEnd = lane[^1].End;
            var looped = new List<TimedChord>();
            for (long at = 0; at < length && chartEnd > 0; at += chartEnd)
                looped.AddRange(lane.Where(c => at + c.Start < length)
                                    .Select(c => c with { Start = at + c.Start, Length = Math.Min(c.Length, length - at - c.Start) }));
            lane = looped;
        }
        else
        {
            var (source, cuts) = HarmonySource(layers, notes);
            lane = ChordReader.Read(source.Where(n => n.Start < length).ToList(), length, cuts);
            typed = false;
        }
        if (section.ChordTrack.Count > 0) lane = Overlay(lane, section.ChordTrack.Select(c => c.Timed), length);

        KeyEstimate? key = null;
        var keyTyped = false;
        if (typedKey.Trim().Length > 0 && MusicTheory.ParseKey(typedKey) is { } k)
        {
            key = new KeyEstimate(k.Tonic, k.Minor, 1);
            keyTyped = true;
        }
        else if (notes.Count == 0 && lane.Count > 0)
        {
            // Only chords (the chord track, a typed chart): which key real tunes with these chords, held this long and
            // moving this way, are in. Checked on charts experts labelled, this names the key far more often than the
            // chords' notes do (ChordKey).
            key = ChordKey.Of(lane.OrderBy(c => c.Start).ToList()) is { } guess
                ? guess with { Confidence = Math.Max(guess.Confidence, MinKeyConfidence) }
                : null;
        }
        else
        {
            var chroma = new double[12];
            foreach (var n in notes) chroma[n.Key % 12] += Math.Clamp(n.Length / (double)ChordLane.Bar, 1 / 16.0, 2);
            // The chords count too, so a typed chart with no parts yet still has a key.
            foreach (var c in lane)
                for (var p = 0; p < 12; p++)
                    if ((c.Chord.Mask >> p & 1) != 0) chroma[p] += 0.5 * c.Length / ChordLane.Bar + (p == c.Chord.Root ? 0.25 : 0);
            // A key the notes don't clearly point at (a gospel lift, half in C and half in Db) is no key at all.
            var heard = MusicTheory.EstimateMidiKey(chroma, lane.Count > 0 ? (lane[0].Chord.Root, MusicTheory.IsMinorChord(lane[0].Chord.Root, lane[0].Chord.Mask)) : null);
            key = heard is { Confidence: >= MinKeyConfidence } ? heard : null;
            // A ii–V–I lane says its key louder than the notes do: home is where the last V resolves, even where the
            // notes alone are too busy to be sure.
            if (HomeOf(lane) is { } home && key?.Tonic != home.Tonic)
                key = new KeyEstimate(home.Tonic, home.Minor, Math.Max(heard?.Confidence ?? 0, MinKeyConfidence));
        }
        return new SectionHarmony
        {
            Notes = notes, Lane = lane, Length = length, Key = key, KeyTyped = keyTyped, ChordsTyped = typed, Style = style,
            Songs = section.Layers.Select(l => l.SourcePath).Concat(section.Layers.SelectMany(l => l.Clips).Select(c => c.SourcePath))
                               .Where(p => p.Length > 0).Select(MidiEntry.SongOf).Distinct().ToList(),
            Hashes = section.Layers.Select(l => l.SourceHash).Concat(section.Layers.SelectMany(l => l.Clips).Select(c => c.SourceHash))
                                .Where(h => h.Length > 0).ToHashSet(),
        };
    }

    /// <summary>
    /// The chord track laid over the chords heard (or typed): where a set chord plays it's the chord, and a heard chord it
    /// overlaps is cut back to the stretches the track leaves free. In time order; nothing past the section's end.
    /// </summary>
    public static List<TimedChord> Overlay(IReadOnlyList<TimedChord> heard, IEnumerable<TimedChord> set, long length)
    {
        var track = set.Where(c => c.Length > 0 && c.Start < length)
                       .Select(c => c with { Length = Math.Min(c.Length, length - c.Start) })
                       .OrderBy(c => c.Start).ToList();
        var lane = new List<TimedChord>(track);
        foreach (var c in heard)
        {
            // What's left of the heard chord once every set chord over it is taken out.
            var pieces = new List<(long Start, long End)> { (c.Start, c.End) };
            foreach (var t in track)
                pieces = pieces.SelectMany(p => t.End <= p.Start || t.Start >= p.End
                    ? [p]
                    : new[] { (p.Start, Math.Min(p.End, t.Start)), (Math.Max(p.Start, t.End), p.End) }.Where(q => q.Item2 > q.Item1)).ToList();
            lane.AddRange(pieces.Select(p => c with { Start = p.Start, Length = p.End - p.Start }));
        }
        return lane.OrderBy(c => c.Start).ToList();
    }

    /// <summary>
    /// The notes the chords are heard from (Solomon, 2026-10-19: "only chord and bass clips decide the harmony; where no
    /// chord or bass clip plays, everything playing there counts"). Under a chord clip, the chord and bass clips there;
    /// everywhere else, every part that plays. Bass alone doesn't say enough, so it only counts while chords sound. The
    /// cuts are where chord clips start and end: a chord heard under one doesn't run on outside it.
    /// </summary>
    static (List<NoteEvent> Notes, List<long> Cuts) HarmonySource(IReadOnlyList<PlaygroundLayer> layers, IReadOnlyList<NoteEvent> all)
    {
        // Each part's clips with what they play as; a part with no clips counts as one clip the whole length.
        var pieces = layers.SelectMany(l => l.Clips.Count == 0
            ? [(Role: l.Role, Start: 0L, End: long.MaxValue, Notes: (IReadOnlyList<NoteEvent>)l.Notes)]
            : l.Clips.Select(c => (Role: c.Role == MidiRoles.Other ? l.Role : c.Role, c.Start, c.End, Notes: (IReadOnlyList<NoteEvent>)c.Render()))).ToList();
        var chordSpans = pieces.Where(p => p.Role == MidiRoles.Chords && p.Notes.Count > 0).Select(p => (p.Start, p.End)).ToList();
        if (chordSpans.Count == 0) return (all.ToList(), []);
        bool UnderChords(NoteEvent n) => chordSpans.Any(s => n.Start >= s.Start && n.Start < s.End);
        var chordNotes = pieces.Where(p => p.Role == MidiRoles.Chords).SelectMany(p => p.Notes).Where(n => !n.Muted).ToList();
        // The bassline counts while the chords sound, passing notes and all: the chord reader hears a walking bass's
        // passing and approach notes as passing (Dm7 stays Dm7, not D7#9), and a bass on the one under chords pushed onto
        // the and of four still names them. Where the chord part rests a bar or more, an 808 line on its own isn't read
        // as chords; a shorter gap is still the chords playing, so short stabs let go before the bass on the one (or an
        // offbeat skank) are still read over their bass: Dm9, not Fmaj7. A bass struck up to half a beat before the
        // chords come in counts with them.
        var sounding = Sounding(chordNotes, joinUnder: ChordLane.Bar);
        var notes = chordNotes.Concat(pieces.Where(p => p.Role == MidiRoles.Bass).SelectMany(p => p.Notes)
                                            .Where(n => !n.Muted && Sounds(sounding, n.Start, n.Start + Math.Max(PhraseAnalyser.Ppq / 2 + 1, n.Length))))
                              .Where(UnderChords)
                              .Concat(all.Where(n => !UnderChords(n))).OrderBy(n => n.Start).ToList();
        return (notes, chordSpans.SelectMany(s => new[] { s.Start, s.End }).Distinct().ToList());
    }

    /// <summary>Where at least one of the notes sounds: stretches in order, none touching the next.</summary>
    /// <param name="joinUnder">A gap shorter than this (ticks) between two stretches counts as sounding too.</param>
    static List<(long Start, long End)> Sounding(IEnumerable<NoteEvent> notes, long joinUnder = 0)
    {
        var spans = new List<(long Start, long End)>();
        foreach (var n in notes.OrderBy(n => n.Start))
        {
            var end = n.Start + Math.Max(1, n.Length);
            if (spans.Count > 0 && n.Start <= spans[^1].End || spans.Count > 0 && n.Start - spans[^1].End < joinUnder) spans[^1] = (spans[^1].Start, Math.Max(spans[^1].End, end));
            else spans.Add((n.Start, end));
        }
        return spans;
    }

    /// <summary>Whether anything in <paramref name="spans"/> (from <see cref="Sounding"/>) sounds between start and end.</summary>
    static bool Sounds(List<(long Start, long End)> spans, long start, long end)
    {
        // The last stretch to start before the end is the only one that can reach past the start.
        int lo = 0, hi = spans.Count - 1, last = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (spans[mid].Start < end) { last = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return last >= 0 && spans[last].End > start;
    }

    /// <summary>Just the bars from <paramref name="from"/> to <paramref name="to"/> (ticks), as if they were the whole section.</summary>
    public SectionHarmony Slice(long from, long to)
    {
        // A loop set before the section got shorter can start past its end: fall back to the last bar's worth.
        from = Math.Clamp(from, 0, Math.Max(0, Length - 1));
        to = Math.Clamp(to, from + 1, Math.Max(from + 1, Length));
        return new SectionHarmony
        {
            // Notes that start before the window but still sound in it (a pushed chord, a tie) are kept, cut at its edges.
            Notes = Notes.Where(n => n.Start < to && n.Start + n.Length > from)
                         .Select(n => n with { Start = Math.Max(n.Start, from) - from, Length = Math.Min(n.Start + n.Length, to) - Math.Max(n.Start, from) }).ToList(),
            Lane = Lane.Where(c => c.End > from && c.Start < to)
                       .Select(c => new TimedChord(Math.Max(c.Start, from) - from, Math.Min(c.End, to) - Math.Max(c.Start, from), c.Chord)).ToList(),
            Length = to - from, Key = Key, KeyTyped = KeyTyped, ChordsTyped = ChordsTyped, Style = Style, Songs = Songs, Hashes = Hashes,
        };
    }

    /// <summary>Where a lane's dominants resolve most (V7 → I), when they say clearly: a jazz lane's home.</summary>
    static (int Tonic, bool Minor)? HomeOf(IReadOnlyList<TimedChord> lane)
    {
        var votes = new Dictionary<(int, bool), int>();
        for (var i = 0; i + 1 < lane.Count; i++)
        {
            ChordSymbol a = lane[i].Chord, b = lane[i + 1].Chord;
            if (a.Family != ChordFamily.Dominant || ChordSymbol.Mod(b.Root - a.Root) != 5) continue;
            if (b.Family is not (ChordFamily.Major or ChordFamily.Minor)) continue;
            var k = (b.Root, b.Family == ChordFamily.Minor);
            votes[k] = votes.GetValueOrDefault(k) + 1 + (i + 2 == lane.Count ? 1 : 0);
        }
        if (votes.Count == 0) return null;
        var best = votes.MaxBy(v => v.Value);
        return votes.Count(v => v.Value == best.Value) == 1 ? best.Key : null;
    }

    /// <summary>
    /// The harmony as the MIDI page's matcher wants it, looping like a part. With no parts yet, the typed chords are
    /// voiced plainly so there's something to match against.
    /// </summary>
    public HarmonyTarget? Target(string name = "This section")
    {
        var notes = Notes.Count > 0 ? Notes : Lane.Count > 0 ? Voicer.Voice(Lane, VoicingStyle.Close, CompRhythm.Held, Length) : [];
        if (notes.Count < 3) return null;
        var chroma = new float[12];
        foreach (var n in notes) chroma[n.Key % 12] += (float)Math.Clamp(n.Length / (4.0 * PhraseAnalyser.Ppq), 1 / 16.0, 2);
        var total = chroma.Sum();
        if (total > 0) for (var i = 0; i < 12; i++) chroma[i] /= total;
        // The harmony loops over the whole section, even where its last bars are empty.
        var padded = notes.ToList();
        var bars = PhraseAnalyser.BarsOf(padded);
        if (bars * ChordLane.Bar < Length) padded.Add(new NoteEvent(Length - 1, 1, notes[^1].Key, 1));
        // The chords are the lane's (typed, or heard from the chord and bass parts), so the tune doesn't colour them.
        return HarmonyTarget.FromPart(name, MidiRoles.Chords, padded, chroma, PhraseAnalyser.Progression(Lane), Key);
    }
}

/// <summary>
/// How well a part's notes sit on a chord lane, with a jazz (or classical) ear: each note is weighed by how long it
/// sounds and where it falls in the bar, valued by chord-scale theory (<see cref="ChordScales"/>), and let off when it's
/// a short note on a weak beat stepping into a good one (a passing or approach note, the chromatic language of
/// bebop and gospel runs). A bassline is judged more on landing on the chord's bass note on the strong beats.
/// </summary>
public static class SectionFit
{
    const int Ppq = PhraseAnalyser.Ppq, Beat = Ppq, Bar = 4 * Ppq;

    static double Metric(long start)
    {
        var inBar = start % Bar;
        if (inBar == 0) return 1.5;
        if (inBar == 2 * Beat) return 1.25;
        if (inBar % Beat == 0) return 1.0;
        if (inBar % (Beat / 2) == 0) return 0.75;
        return 0.6;
    }

    /// <summary>
    /// 0 to 1, or null when no note falls on a chord. <paramref name="sit"/> and <paramref name="strongTension"/> swap in
    /// other note values and strong-beat weights (the theory bench tries learned ones).
    /// </summary>
    public static double? Score(IReadOnlyList<NoteEvent> notes, IReadOnlyList<TimedChord> lane, long length, string role,
                                HarmonyStyle style = HarmonyStyle.JazzGospel, Func<ChordSymbol, int, double>? sit = null,
                                double strongTension = 0.6)
    {
        double Sit(ChordSymbol chord, int pc) => sit?.Invoke(chord, pc) ?? ChordScales.Sit(chord, pc, style);
        if (notes.Count == 0 || lane.Count == 0) return null;
        var sorted = notes.Where(n => n.Start < length).OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
        var bass = role == MidiRoles.Bass;
        var melody = role == MidiRoles.Melody;
        var jazz = style == HarmonyStyle.JazzGospel;
        double sum = 0, total = 0;
        for (var i = 0; i < sorted.Count; i++)
        {
            var n = sorted[i];
            var pc = n.Key % 12;
            // A held note is heard against every chord it sounds over, not just the one it starts on: an E held from
            // C7 into F7 rubs on the F7. The pieces held over count a little less than where the note is struck, and by
            // how long they last: a legato note running a few ticks past the change counts next to nothing. The
            // strong-beat rules below judge held pieces too: on 387 pop songs that tells a song's own melody from
            // another's better (AUC 0.808 against 0.793 without), as a tension held onto a new downbeat is heard there.
            foreach (var (chord, start, len, struck) in ChordLane.Under(lane, n.Start, Math.Min(n.Length, length - n.Start)))
            {
                var metric = Metric(start);
                var w = (struck ? Math.Clamp(len / (double)Beat, 0.25, 2) : Math.Min(len / (double)Beat, 2) * 0.8) * metric;
                var v = Sit(chord, pc);
                // A tension on beat 1 or 3 of a tune counts less than a chord note there: melodies land on chord
                // notes on the strong beats and colour the weak ones.
                if (melody && jazz && metric >= 1.25 && v > 0 && v < 1) v *= strongTension;
                if (bass && metric >= 1.25)
                    v = pc == chord.Bass ? 1 : pc == chord.Root ? 0.9 : v * 0.75;
                if (struck && v < 0.55 && n.Length <= Beat / 2 && metric < 1.25)
                {
                    // A short note between beats: a passing or approach note if the next note steps to one that sits.
                    var next = sorted.Skip(i + 1).FirstOrDefault(m => m.Start > n.Start);
                    if (next.Length > 0 && Math.Abs(next.Key - n.Key) <= 2 && ChordLane.At(lane, next.Start + 1) is { } then &&
                        Sit(then, next.Key % 12) >= 0.75)
                        v = Math.Max(v, jazz ? 0.65 : 0.55);
                    else v = Math.Max(v, 0.15);
                }
                // Classical ears want the chord's own notes on the strong beats; a dissonance there must be prepared
                // (a note held over from the chord before is prepared, a suspension).
                if (!jazz && struck && metric >= 1.25 && v < 1) v -= 0.2;
                sum += w * v;
                total += w;
            }
        }
        return total <= 0 ? null : Math.Clamp(sum / total, 0, 1);
    }

    /// <summary>
    /// How much of the time the part plays while there are chords: the share of beats with a chord in which one of its
    /// notes sounds. A short fragment that sits well on one chord shouldn't beat a part that plays through them all
    /// (Solomon, 2026-10-21: "favour clips that go with the chords for the most amount of time").
    /// </summary>
    public static double Cover(IReadOnlyList<NoteEvent> notes, IReadOnlyList<TimedChord> lane, long length)
    {
        int beats = 0, played = 0;
        var sorted = notes.Where(n => n.Start < length).OrderBy(n => n.Start).ToList();
        var from = 0;
        for (long at = 0; at < length; at += Beat)
        {
            if (ChordLane.At(lane, at + 1) is null) continue;
            beats++;
            while (from < sorted.Count && sorted[from].Start + sorted[from].Length <= at) from++;
            for (var i = from; i < sorted.Count && sorted[i].Start < at + Beat; i++)
                if (sorted[i].Start + sorted[i].Length > at) { played++; break; }
        }
        return beats == 0 ? 1 : played / (double)beats;
    }

    /// <summary>
    /// How closely a chord part's own chords follow the lane, half a bar at a time, with substitutes counting as alike
    /// (<see cref="ChordKinship"/>): a part playing Db7 where the lane has G7 still follows it. Null when it plays too
    /// few chords to say.
    /// </summary>
    public static double? Follow(IReadOnlyList<NoteEvent> notes, IReadOnlyList<TimedChord> lane, long length) =>
        Follow(ChordLane.Detect(notes, length), lane, length);

    /// <summary>The same, from the part's chords already read.</summary>
    public static double? Follow(IReadOnlyList<TimedChord> mine, IReadOnlyList<TimedChord> lane, long length)
    {
        if (mine.Count == 0 || lane.Count == 0) return null;
        double sum = 0;
        var count = 0;
        for (long at = 0; at < length; at += 2 * Beat)
        {
            if (ChordLane.At(lane, at + 1) is not { } theirs || ChordLane.At(mine, at + 1) is not { } own) continue;
            sum += ChordKinship.Similarity(own, theirs);
            count++;
        }
        return count == 0 ? null : sum / count;
    }

    /// <summary>
    /// Where the notes land in the bar, 16 steps, each the share of onsets there: for matching a groove (drums to a
    /// section's rhythm).
    /// </summary>
    public static double[] Onsets(IEnumerable<NoteEvent> notes)
    {
        var grid = new double[16];
        foreach (var n in notes) grid[(int)(n.Start % Bar * 16 / Bar)] += n.Velocity / 127.0;
        var max = grid.Max();
        if (max > 0) for (var i = 0; i < 16; i++) grid[i] /= max;
        return grid;
    }

    public static double Cosine(double[] a, double[] b)
    {
        double ab = 0, aa = 0, bb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) { ab += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        return aa <= 0 || bb <= 0 ? 0 : ab / Math.Sqrt(aa * bb);
    }
}

/// <summary>A library part suggested for a section, already moved into its key and octave.</summary>
/// <param name="Score">0 to 1: how well it goes with the section.</param>
/// <param name="Notes">Its notes moved, from its first bar, as it would go in.</param>
/// <param name="Why">In a few words: "Sits on the chords 88% · follows them 81% · up 3".</param>
public sealed record PlaygroundMatch(MidiEntry Entry, HarmonyFit? Fit, double Score, IReadOnlyList<NoteEvent> Notes, string Why)
{
    /// <summary>How long one go round of <see cref="Notes"/> is when they're a stretch cut from the part to swap in (the bars it fills); 0 for the part's own bars.</summary>
    public long Period { get; init; }
    public int Percent => (int)Math.Round(Score * 100);
    public int Move => (Fit?.Semitones ?? 0) + 12 * (Fit?.Octaves ?? 0);
}

/// <summary>
/// What a part plays around bars of it being swapped for a stretch of another part: its notes in the bar before (starting
/// before 0) and the bar after (from 0), where in the bar the swapped bars start, and its usual height (the average key,
/// or null with no notes) and notes per bar, for judging how a stretch joins on.
/// </summary>
public sealed record SwapAround(IReadOnlyList<NoteEvent> Before, IReadOnlyList<NoteEvent> After, long Phase, double? Height, double Busy);

/// <summary>
/// Finds library parts that go with a section: the MIDI page's harmony matcher picks the likeliest few hundred
/// (every key tried, played against the section in time), then each is judged again against the section's chord
/// lane with the jazz or classical ear and, for chord parts, on how closely its chords follow the lane. Parts from any
/// song already in the section are left out: a song's own parts always fit each other, which says nothing.
/// </summary>
public static class SectionMatcher
{
    /// <param name="around">Set when bars of a part are being swapped: then each part offers its stretch that fits there best.</param>
    /// <param name="from">"projects" or "files" for only the parts in your projects or in your MIDI folders; null for both.</param>
    public static IReadOnlyList<PlaygroundMatch> Rank(MidiCatalogue catalogue, SectionHarmony section, string role,
                                                      Func<MidiEntry, IReadOnlyList<NoteEvent>> notesOf, string text = "",
                                                      int limit = 60, CancellationToken ct = default, SectionFeel feel = SectionFeel.Any,
                                                      SwapAround? around = null, string? from = null) =>
        Rank(catalogue, section, role, notesOf, out _, text, limit, ct, feel, around, from);

    /// <param name="matching">How many parts of this kind could have been suggested (the words found them and no song
    /// in the section has them), of which the best <paramref name="limit"/> come back.</param>
    /// <param name="scored">Parts already scored for the same question (Show more asks again for more): they aren't
    /// scored again, and the ones scored now are added. Null scores every part.</param>
    public static IReadOnlyList<PlaygroundMatch> Rank(MidiCatalogue catalogue, SectionHarmony section, string role,
                                                      Func<MidiEntry, IReadOnlyList<NoteEvent>> notesOf, out int matching, string text = "",
                                                      int limit = 60, CancellationToken ct = default, SectionFeel feel = SectionFeel.Any,
                                                      SwapAround? around = null, string? from = null,
                                                      ConcurrentDictionary<MidiEntry, PlaygroundMatch?>? scored = null)
    {
        var energy = feel == SectionFeel.Any ? null : Energy.Target(feel, (int)(section.Length / ChordLane.Bar));
        if (role == MidiRoles.Drums) return Grooves(catalogue, section, notesOf, text, limit, ct, energy, feel, around, from, out matching);
        var target = section.Target();
        var hits = catalogue.Search(new MidiQuery(text, role, from, Fit: target, Limit: target is null && energy is null ? limit : Math.Max(limit * 6, 300), Notes: notesOf)
                                    { LeaveSongs = section.Songs }, out matching, ct);
        var list = new List<PlaygroundMatch>();
        foreach (var hit in hits)
        {
            ct.ThrowIfCancellationRequested();
            if (section.Hashes.Contains(hit.Entry.Phrase.Hash)) continue;
            if (scored is not null && scored.TryGetValue(hit.Entry, out var known))
            {
                if (known is not null) list.Add(known);
                continue;
            }
            var before = list.Count;
            Score(hit);
            scored?.TryAdd(hit.Entry, list.Count > before ? list[^1] : null);
        }
        return list.OrderByDescending(m => m.Score).ThenByDescending(m => m.Entry.Copies).Take(limit).ToList();

        void Score(MidiHit hit)
        {
            var raw = notesOf(hit.Entry);
            if (raw.Count == 0) return;
            var move = hit.Fit is { } f ? f.Semitones + 12 * (hit.Entry.YourOctave ?? f.Octaves) : 12 * (hit.Entry.YourOctave ?? 0);
            if (around is not null)
            {
                if (Stretch(hit.Entry, hit.Fit, raw, move, section, role, around, energy, feel) is { } stretch) list.Add(stretch);
                return;
            }
            var moved = raw.Select(n => n with { Key = Math.Clamp(n.Key + move, 0, 127) }).ToList();
            var period = Math.Max(1, PhraseAnalyser.BarsOf(moved)) * (long)ChordLane.Bar;
            var looped = Loop(moved, period, section.Length);
            // How well its energy suits the section's feel (a verse, a build, a drop), when one is picked.
            double? lift = energy is null ? null : Energy.Match(Energy.Curve(looped, section.Length, role, section.Key), energy);
            var feelText = lift is { } lv ? $"{Energy.Name(feel)} energy {lv * 100:0}%" : "";
            if (target is null || section.Lane.Count == 0)
            {
                var plain = hit.Fit?.Score ?? 0;
                if (lift is { } l0) plain = hit.Fit is null ? l0 : 0.7 * plain + 0.3 * l0;
                list.Add(new PlaygroundMatch(hit.Entry, hit.Fit, plain, moved,
                    string.Join(" · ", new[] { hit.Fit is null ? "" : $"Fits {hit.Fit.Percent}%", feelText }.Where(s => s.Length > 0))));
                return;
            }
            var sits = SectionFit.Score(looped, section.Lane, section.Length, role, section.Style);
            var follow = role == MidiRoles.Chords ? SectionFit.Follow(looped, section.Lane, section.Length) : null;
            var refine = hit.Fit?.Score ?? 0;
            var score = (sits, follow) switch
            {
                ({ } s, { } fo) => 0.3 * refine + 0.35 * s + 0.35 * fo,
                ({ } s, null) => 0.4 * refine + 0.6 * s,
                _ => refine,
            };
            if (lift is { } l) score = 0.72 * score + 0.28 * l;
            // Sitting well only counts for the time it plays: a chunk that plays a quarter of the time keeps about half its score.
            var cover = SectionFit.Cover(looped, section.Lane, section.Length);
            score *= 0.35 + 0.65 * cover;
            var why = string.Join(" · ", new[]
            {
                sits is { } sv ? $"Sits on the chords {sv * 100:0}%" : "",
                follow is { } fv ? $"follows them {fv * 100:0}%" : "",
                cover < 0.95 ? $"plays {cover * 100:0}% of the time" : "",
                feelText,
                MoveText(move),
            }.Where(s => s.Length > 0));
            list.Add(new PlaygroundMatch(hit.Entry, hit.Fit, Math.Clamp(score, 0, 1), moved, why));
        }
    }

    static string MoveText(int move) => move switch
    {
        0 => "as it is",
        > 0 when move % 12 == 0 => $"up {move / 12} octave{(move == 12 ? "" : "s")}",
        < 0 when move % 12 == 0 => $"down {-move / 12} octave{(move == -12 ? "" : "s")}",
        > 0 => $"up {move}",
        _ => $"down {-move}",
    };

    /// <summary>The most stretches of one part tried (a stretch starts on each of its bars).</summary>
    const int MostStretches = 32;

    /// <summary>How many of a part's stretches (each in a key) are judged in full: the rest are passed over on a quick look.</summary>
    const int JudgedStretches = 4;

    /// <summary>
    /// A part's stretches as long as the bars being swapped, one starting on each of its bars where they start in the bar
    /// (going round from its end into its start), with where each starts, and how long the part goes round.
    /// </summary>
    static (List<(long From, List<NoteEvent> Piece)> Pieces, long Period) Stretches(IReadOnlyList<NoteEvent> raw, SwapAround around, long length)
    {
        var period = Math.Max(1, PhraseAnalyser.BarsOf(raw)) * (long)ChordLane.Bar;
        var round = Loop(raw, period, period + length);
        var pieces = new List<(long, List<NoteEvent>)>();
        var tried = 0;
        for (var from = around.Phase % ChordLane.Bar; from < period && tried < MostStretches; from += ChordLane.Bar, tried++)
        {
            var piece = round.Where(n => n.Start >= from && n.Start < from + length)
                             .Select(n => n with { Start = n.Start - from, Length = Math.Min(n.Length, from + length - n.Start) }).ToList();
            if (piece.Count > 0) pieces.Add((from, piece));
        }
        return (pieces, period);
    }

    /// <summary>
    /// The stretch of a part that best fills bars being swapped (Solomon, 2026-10-20: "select a part of a clip and have
    /// suggestions to replace that with midi suggested so it finds a fragment that goes"). Each stretch as long as the
    /// bars, starting where they start in the bar (and going round from the part's end into its start), is tried in the
    /// keys that suit the chords there, at the height the part around it plays. The best sits on the chords and joins on
    /// smoothly to the notes either side.
    /// </summary>
    static PlaygroundMatch? Stretch(MidiEntry entry, HarmonyFit? fit, IReadOnlyList<NoteEvent> raw, int move, SectionHarmony section, string role,
                                    SwapAround around, double[]? energy, SectionFeel feel)
    {
        var length = section.Length;
        var (pieces, period) = Stretches(raw, around, length);
        var lane = section.Lane;
        var chords = role == MidiRoles.Chords && lane.Count > 0;
        // How well each of the 12 notes sits on the chords there, by how long each chord lasts: picks the keys worth trying.
        var sits = new double[12];
        foreach (var c in lane)
            for (var pc = 0; pc < 12; pc++) sits[pc] += c.Length * ChordScales.Sit(c.Chord, pc, section.Style);
        var laneLength = Math.Max(1, lane.Sum(c => c.Length));
        // A quick look first: how its notes sit on the chords there by pitch alone, and how it joins on. Only the best few
        // are judged in full (the notes against each chord in time, a chord part's own chords read), so a Suggest stays quick.
        var quick = new List<(double Quick, long From, List<NoteEvent> Piece, int Shift, List<NoteEvent> Moved, double Joins)>();
        foreach (var (from, piece) in pieces)
        {
            var held = new double[12];
            foreach (var n in piece) held[n.Key % 12] += n.Length;
            var heard = Math.Max(1, held.Sum());
            foreach (var shift in Keys(piece, move, sits, lane.Count > 0, around.Height))
            {
                var moved = piece.Select(n => n with { Key = Math.Clamp(n.Key + shift, 0, 127) }).ToList();
                var joins = Joins(moved, around, role, length);
                var keyFit = lane.Count == 0 ? 0 : Enumerable.Range(0, 12).Sum(pc => held[pc] * sits[ChordSymbol.Mod(pc + shift)]) / (heard * laneLength);
                quick.Add((0.6 * keyFit + 0.4 * joins, from, piece, shift, moved, joins));
            }
        }
        (double Score, List<NoteEvent> Notes, long From, int Move, double? Sits, double? Follow, double Joins)? best = null;
        foreach (var (_, from, piece, shift, moved, joins) in quick.OrderByDescending(q => q.Quick).Take(JudgedStretches))
        {
            // Chords and basslines fill the bars they're in: a stretch that's mostly rests counts for less.
            var fills = role is MidiRoles.Chords or MidiRoles.Bass or MidiRoles.Arp ? 0.75 + 0.25 * Filled(piece, length) : 1;
            double? sit = lane.Count > 0 ? SectionFit.Score(moved, lane, length, role, section.Style) : null;
            // A chord part whose chords can't be read there (a lone line) doesn't follow the chords.
            double? follow = chords ? SectionFit.Follow(ChordLane.Detect(moved, length), lane, length) ?? 0.4 : null;
            var score = fills * (sit, follow) switch
            {
                ({ } s, { } fo) => 0.35 * s + 0.3 * fo + 0.35 * joins,
                ({ } s, null) => 0.6 * s + 0.4 * joins,
                _ => joins,
            };
            if (best is null || score > best.Value.Score) best = (score, moved, from, shift, sit, follow, joins);
        }
        if (best is not { } b) return null;
        var total = b.Score;
        double? lift = energy is null ? null : Energy.Match(Energy.Curve(b.Notes, length, role, section.Key), energy);
        if (lift is { } l) total = 0.72 * total + 0.28 * l;
        var why = string.Join(" · ", new[]
        {
            Where(b.From, length, period),
            b.Sits is { } sv ? $"sits on the chords {sv * 100:0}%" : "",
            b.Follow is { } fv ? $"follows them {fv * 100:0}%" : "",
            $"joins on {b.Joins * 100:0}%",
            lift is { } lv ? $"{Energy.Name(feel)} energy {lv * 100:0}%" : "",
            MoveText(b.Move),
        }.Where(t => t.Length > 0));
        return new PlaygroundMatch(entry, fit, Math.Clamp(total, 0, 1), b.Notes, char.ToUpperInvariant(why[0]) + why[1..]) { Period = length };
    }

    /// <summary>
    /// The moves worth trying for a stretch: the three keys whose notes sit best on the chords (just the part's own move
    /// when there are no chords), each in the octave nearest the height the part around it plays at.
    /// </summary>
    static IEnumerable<int> Keys(IReadOnlyList<NoteEvent> piece, int move, double[] sits, bool chords, double? height)
    {
        var shifts = new List<int> { move };
        if (chords)
        {
            var held = new double[12];
            foreach (var n in piece) held[n.Key % 12] += n.Length;
            shifts = Enumerable.Range(0, 12).Select(d => move + d)
                .OrderByDescending(s => Enumerable.Range(0, 12).Sum(pc => held[pc] * sits[ChordSymbol.Mod(pc + s)]))
                .Take(3).ToList();
        }
        var mean = piece.Average(n => n.Key);
        return shifts.Select(s => height is { } h ? s + 12 * (int)Math.Round((h - mean - s) / 12) : s).Distinct();
    }

    /// <summary>The share of its beats where a note sounds.</summary>
    static double Filled(IReadOnlyList<NoteEvent> notes, long length)
    {
        var beats = (int)Math.Max(1, (length + ChordLane.Ppq - 1) / ChordLane.Ppq);
        var sounds = new bool[beats];
        foreach (var n in notes)
            for (var b = (int)(n.Start / ChordLane.Ppq); b < beats && b * (long)ChordLane.Ppq < n.Start + n.Length; b++) sounds[b] = true;
        return sounds.Count(x => x) / (double)beats;
    }

    /// <summary>Which bars of the part a stretch is: "its bars 5–6", or "its bar 8 round to bar 1" when it goes round.</summary>
    static string Where(long from, long length, long period)
    {
        int first = (int)(from / ChordLane.Bar) + 1, last = (int)((from + length - 1) % period / ChordLane.Bar) + 1;
        return first == last ? $"its bar {first}" : last > first ? $"its bars {first}–{last}" : $"its bar {first} round to bar {last}";
    }

    /// <summary>
    /// How smoothly a stretch joins on to the part around it, 0 to 1: at about the same height, a small step in from the
    /// notes before it and out to the notes after (each note of a chord moving as little as it can), and about as busy.
    /// </summary>
    public static double Joins(IReadOnlyList<NoteEvent> piece, SwapAround around, string role, long length)
    {
        if (piece.Count == 0) return 0;
        double sum = 0, weight = 0;
        void Add(double w, double v) { sum += w * v; weight += w; }
        if (around.Height is { } height)
        {
            var off = piece.Average(n => n.Key) - height;
            Add(0.35, Math.Exp(-off * off / 50));   // 5 semitones off: 0.6, an octave: 0.06
        }
        if (role == MidiRoles.Drums)
        {
            // A drum's note number is which drum it is, not how high: a stretch joins on when it plays the same kit.
            if (around.Before.Count + around.After.Count > 0) Add(0.5, Kit(around.Before.Concat(around.After), piece));
        }
        else
        {
            if (Struck(around.Before, last: true) is { Count: > 0 } before) Add(0.25, Step(before, Struck(piece, last: false), role));
            if (Struck(around.After, last: false) is { Count: > 0 } after) Add(0.25, Step(Struck(piece, last: true), after, role));
        }
        if (around.Busy > 0)
        {
            var busy = piece.Count / Math.Max(0.25, length / (double)ChordLane.Bar);
            Add(0.15, Math.Sqrt(Math.Min(busy, around.Busy) / Math.Max(busy, around.Busy)));
        }
        return weight == 0 ? 0.5 : sum / weight;
    }

    /// <summary>The share of the drums either plays that both play, 0 to 1.</summary>
    static double Kit(IEnumerable<NoteEvent> a, IEnumerable<NoteEvent> b)
    {
        var mine = a.Select(n => n.Key).ToHashSet();
        var theirs = b.Select(n => n.Key).ToHashSet();
        var all = mine.Count + theirs.Count - mine.Intersect(theirs).Count();
        return all == 0 ? 0.5 : mine.Intersect(theirs).Count() / (double)all;
    }

    /// <summary>The keys of the notes struck first (or last), together within a 32nd.</summary>
    static List<int> Struck(IReadOnlyList<NoteEvent> notes, bool last)
    {
        if (notes.Count == 0) return [];
        var at = last ? notes.Max(n => n.Start) : notes.Min(n => n.Start);
        return notes.Where(n => Math.Abs(n.Start - at) <= ChordLane.Ppq / 8).Select(n => n.Key).ToList();
    }

    /// <summary>How small a move from one group of notes to the next is, 0 to 1: a line's step (a bassline from its lowest note, a tune from its top one), or each note of a chord to its nearest.</summary>
    static double Step(List<int> from, List<int> to, string role)
    {
        if (from.Count == 0 || to.Count == 0) return 0.5;
        if (role == MidiRoles.Chords) return Math.Exp(-to.Average(t => from.Min(f => Math.Abs(f - t))) / 3);
        var leap = role == MidiRoles.Bass ? Math.Abs(from.Min() - to.Min()) : Math.Abs(from.Max() - to.Max());
        return leap <= 2 ? 1 : leap <= 4 ? 0.85 : leap <= 7 ? 0.65 : leap <= 12 ? 0.4 : 0.15;
    }

    /// <summary>Drums have no harmony: they're matched on where their hits fall against the section's rhythm.</summary>
    static IReadOnlyList<PlaygroundMatch> Grooves(MidiCatalogue catalogue, SectionHarmony section, Func<MidiEntry, IReadOnlyList<NoteEvent>> notesOf,
                                                  string text, int limit, CancellationToken ct, double[]? energy, SectionFeel feel, SwapAround? around, string? from, out int matching)
    {
        var hits = catalogue.Search(new MidiQuery(text, MidiRoles.Drums, from, Limit: section.Notes.Count == 0 && energy is null && around is null ? limit : Math.Max(limit * 2, 600)) { LeaveSongs = section.Songs }, out matching, ct);
        if (around is not null) return DrumStretches(hits, section, notesOf, limit, ct, around, energy, feel);
        if (energy is not null)
        {
            var mineOrNone = section.Notes.Count == 0 ? null : SectionFit.Onsets(section.Notes);
            var ranked = new List<PlaygroundMatch>();
            foreach (var h in hits)
            {
                ct.ThrowIfCancellationRequested();
                if (section.Hashes.Contains(h.Entry.Phrase.Hash)) continue;
                var notes = notesOf(h.Entry);
                if (notes.Count == 0) continue;
                var looped = Loop(notes, Math.Max(1, PhraseAnalyser.BarsOf(notes)) * (long)ChordLane.Bar, section.Length);
                var lift = Energy.Match(Energy.Curve(looped, section.Length, MidiRoles.Drums, null), energy);
                double? groove = mineOrNone is null ? null : SectionFit.Cosine(mineOrNone, SectionFit.Onsets(notes));
                var score = groove is { } g ? 0.6 * g + 0.4 * lift : lift;
                var why = string.Join(" · ", new[] { groove is { } gv ? $"Grooves with the section {gv * 100:0}%" : "", $"{Energy.Name(feel)} energy {lift * 100:0}%" }
                                             .Where(s => s.Length > 0));
                ranked.Add(new PlaygroundMatch(h.Entry, null, score, notes, why));
            }
            return ranked.OrderByDescending(m => m.Score).Take(limit).ToList();
        }
        if (section.Notes.Count == 0)
            return hits.Where(h => !section.Hashes.Contains(h.Entry.Phrase.Hash))
                       .Select(h => new PlaygroundMatch(h.Entry, null, 0, notesOf(h.Entry), "")).Where(m => m.Notes.Count > 0).ToList();
        var mine = SectionFit.Onsets(section.Notes);
        var list = new List<PlaygroundMatch>();
        foreach (var h in hits)
        {
            ct.ThrowIfCancellationRequested();
            if (section.Hashes.Contains(h.Entry.Phrase.Hash)) continue;
            var notes = notesOf(h.Entry);
            if (notes.Count == 0) continue;
            var groove = SectionFit.Cosine(mine, SectionFit.Onsets(notes));
            list.Add(new PlaygroundMatch(h.Entry, null, groove, notes, $"Grooves with the section {groove * 100:0}%"));
        }
        return list.OrderByDescending(m => m.Score).Take(limit).ToList();
    }

    /// <summary>
    /// Bars of a drum part being swapped: each drum part's stretch that grooves most like the bars around them (the
    /// section's rhythm when the part has none there) and is about as busy.
    /// </summary>
    static IReadOnlyList<PlaygroundMatch> DrumStretches(IReadOnlyList<MidiHit> hits, SectionHarmony section, Func<MidiEntry, IReadOnlyList<NoteEvent>> notesOf,
                                                        int limit, CancellationToken ct, SwapAround around, double[]? energy, SectionFeel feel)
    {
        var length = section.Length;
        var near = around.Before.Concat(around.After).ToList();
        var rhythm = near.Count > 0 ? SectionFit.Onsets(near) : section.Notes.Count > 0 ? SectionFit.Onsets(section.Notes) : null;
        var list = new List<PlaygroundMatch>();
        foreach (var h in hits)
        {
            ct.ThrowIfCancellationRequested();
            if (section.Hashes.Contains(h.Entry.Phrase.Hash)) continue;
            var raw = notesOf(h.Entry);
            if (raw.Count == 0) continue;
            var (pieces, period) = Stretches(raw, around, length);
            (double Score, List<NoteEvent> Notes, long From, double? Groove)? best = null;
            foreach (var (from, piece) in pieces)
            {
                double? groove = rhythm is null ? null : SectionFit.Cosine(rhythm, SectionFit.Onsets(piece));
                var joins = Joins(piece, around with { Height = null }, MidiRoles.Drums, length);
                var score = groove is { } g ? 0.6 * g + 0.4 * joins : joins;
                if (best is null || score > best.Value.Score) best = (score, piece, from, groove);
            }
            if (best is not { } b) continue;
            // How well its energy suits the section's feel (a verse, a build, a drop), as for every other kind of part.
            double? lift = energy is null ? null : Energy.Match(Energy.Curve(b.Notes, length, MidiRoles.Drums, null), energy);
            var total = lift is { } l ? 0.72 * b.Score + 0.28 * l : b.Score;
            var why = string.Join(" · ", new[]
            {
                Where(b.From, length, period),
                b.Groove is { } gv ? $"grooves with it {gv * 100:0}%" : "",
                lift is { } lv ? $"{Energy.Name(feel)} energy {lv * 100:0}%" : "",
            }.Where(t => t.Length > 0));
            list.Add(new PlaygroundMatch(h.Entry, null, Math.Clamp(total, 0, 1), b.Notes, char.ToUpperInvariant(why[0]) + why[1..]) { Period = length });
        }
        return list.OrderByDescending(m => m.Score).Take(limit).ToList();
    }

    /// <summary>The notes over and over every <paramref name="period"/> ticks, up to <paramref name="length"/>.</summary>
    public static List<NoteEvent> Loop(IReadOnlyList<NoteEvent> notes, long period, long length)
    {
        var list = new List<NoteEvent>();
        if (period <= 0) return list;
        for (long at = 0; at < length; at += period)
            foreach (var n in notes)
                if (n.Start < period && at + n.Start < length)
                    list.Add(n with { Start = at + n.Start, Length = Math.Min(n.Length, length - at - n.Start) });
        return list;
    }
}

/// <summary>
/// "What chord next?", learned from the chord parts in your own library (other songs only): how often each kind of
/// chord follows another, by the step between their roots, so a ii–V learned in Eb is offered in C. Two chords of
/// context when there's enough, one when there isn't.
/// </summary>
public sealed class ProgressionModel
{
    Dictionary<string, Dictionary<string, int>> _one = new(), _two = new();

    /// <summary>The chord-kind spelling the model counts: maj7, m7, 7, m7b5, dim7, sus and plain triads.</summary>
    public static string Kind(ChordSymbol c) => c.Family switch
    {
        ChordFamily.Major => c.Has(11) ? "maj7" : c.Has(9) ? "6" : "",
        ChordFamily.Minor => c.Has(10) ? "m7" : c.Has(9) ? "m6" : "m",
        ChordFamily.Dominant => "7",
        ChordFamily.HalfDiminished => "m7b5",
        ChordFamily.Diminished => c.Has(9) ? "dim7" : "dim",
        ChordFamily.Sus => c.Has(10) ? "7sus4" : "sus4",
        ChordFamily.Augmented => "+",
        _ => "5",
    };

    public int Learned { get; private set; }

    public static ProgressionModel Learn(IEnumerable<MidiEntry> entries, IReadOnlyCollection<string> leaveSongs, CancellationToken ct = default)
    {
        var n = 0;
        return Learn(entries.Where(e =>
            {
                if (++n % 4096 == 0) ct.ThrowIfCancellationRequested();
                return e.Role == MidiRoles.Chords && e.Chords.Count >= 3 && (leaveSongs.Count == 0 || !leaveSongs.Any(e.IsInSong));
            })
            .Select(e => e.Chords.Select(c => c.Symbol).ToList()));
    }

    /// <summary>The same from chord parts' chords in order (the theory bench learns from packs this way).</summary>
    public static ProgressionModel Learn(IEnumerable<IReadOnlyList<ChordSymbol>> parts)
    {
        var model = new ProgressionModel();
        foreach (var chords in parts)
        {
            for (var i = 1; i < chords.Count; i++)
            {
                if (chords[i].Root == chords[i - 1].Root && Kind(chords[i]) == Kind(chords[i - 1])) continue;
                var next = $"{ChordSymbol.Mod(chords[i].Root - chords[i - 1].Root)}:{Kind(chords[i])}";
                Count(model._one, Kind(chords[i - 1]), next);
                if (i >= 2)
                    Count(model._two, $"{Kind(chords[i - 2])}>{ChordSymbol.Mod(chords[i - 1].Root - chords[i - 2].Root)}:{Kind(chords[i - 1])}", next);
            }
            model.Learned++;
        }
        return model;
    }

    static void Count(Dictionary<string, Dictionary<string, int>> table, string context, string next)
    {
        if (!table.TryGetValue(context, out var row)) table[context] = row = new Dictionary<string, int>();
        row[next] = row.GetValueOrDefault(next) + 1;
    }

    /// <summary>How often <paramref name="next"/> (its step from <paramref name="last"/> and its kind) follows <paramref name="last"/>, 0 to 1 (blended with <see cref="Prior"/> as <see cref="Next"/> does); 0 when never seen.</summary>
    public double Chance(ChordSymbol last, ChordSymbol next)
    {
        var step = $"{ChordSymbol.Mod(next.Root - last.Root)}:{Kind(next)}";
        double seen = 0, mine = 0;
        if (_one.TryGetValue(Kind(last), out var row) && (seen = row.Values.Sum()) > 0) mine = row.GetValueOrDefault(step) / seen;
        if (Prior is not { } prior || !prior._one.TryGetValue(Kind(last), out var from)) return mine;
        double total = from.Values.Sum();
        if (total == 0) return mine;
        var weight = seen / (seen + PriorStrength);
        return weight * mine + (1 - weight) * from.GetValueOrDefault(step) / (double)total;
    }

    /// <summary>
    /// What to start from before your own chord parts have taught it much: the chord moves of 6,479 charts gathered by ChoCo
    /// (Chord Corpus, de Berardinis et al., CC BY 4.0, github.com/smashub/choco: the Real Book, iReal Pro jazz playlists
    /// and gospel, worship and soul charts, the Weimar Jazz Database and the Jazz Corpus). Only the counts are kept
    /// (ChordMoves.tsv, made by the theory bench's --save-chord-moves). Your own parts count for more the more of them
    /// there are after a chord (<see cref="PriorStrength"/>).
    /// </summary>
    public ProgressionModel? Prior { get; init; }

    /// <summary>
    /// How many of your own chord changes after a chord weigh as much as everything <see cref="Prior"/> says. Tried on
    /// gospel charts guessed from 40 or from about 900 other gospel charts, with ChoCo's jazz as the prior (--corpus):
    /// 1,000 did best for both, a small library getting most of the help and a big one still a little.
    /// </summary>
    public double PriorStrength { get; init; } = DefaultPriorStrength;

    public const double DefaultPriorStrength = 1000;

    static readonly Lazy<ProgressionModel> Charts = new(() =>
    {
        using var s = typeof(ProgressionModel).Assembly.GetManifestResourceStream("MusicOrganiser.Harmony.ChordMoves.tsv")
                      ?? throw new InvalidOperationException("ChordMoves.tsv isn't in the build");
        using var r = new StreamReader(s);
        return Load(r);
    });

    /// <summary>The ChoCo chord moves the app ships with.</summary>
    public static ProgressionModel FromCharts => Charts.Value;

    /// <summary>The same model starting from <paramref name="prior"/>.</summary>
    public ProgressionModel With(ProgressionModel? prior, double strength = DefaultPriorStrength)
    {
        var model = new ProgressionModel { Prior = prior, PriorStrength = strength, Learned = Learned };
        (model._one, model._two) = (_one, _two);
        return model;
    }

    /// <summary>The counts as a table (tab-separated): what ChordMoves.tsv holds. Counts under <paramref name="least"/> are left out.</summary>
    public string Save(int least = 1)
    {
        var w = new StringWriter { NewLine = "\n" };
        w.WriteLine("# What chord next: counts learned from ChoCo (Chord Corpus, de Berardinis et al., CC BY 4.0, github.com/smashub/choco).");
        w.WriteLine("# Made by tools/MusicOrganiser.TheoryBench --save-chord-moves. Columns: one|two, the chord(s) before, the next step:kind, count.");
        w.WriteLine($"learned\t{Learned}");
        foreach (var (name, table) in new[] { ("one", _one), ("two", _two) })
            foreach (var (context, row) in table.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                foreach (var (next, n) in row.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    if (n >= least) w.WriteLine($"{name}\t{(context.Length == 0 ? "maj" : context)}\t{next}\t{n}");
        return w.ToString();
    }

    public static ProgressionModel Load(TextReader reader)
    {
        var model = new ProgressionModel();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split('\t');
            if (f[0] == "learned") { model.Learned = int.Parse(f[1]); continue; }
            var table = f[0] == "two" ? model._two : model._one;
            var context = f[1] == "maj" ? "" : f[1];
            if (!table.TryGetValue(context, out var row)) table[context] = row = new Dictionary<string, int>();
            row[f[2]] = int.Parse(f[3]);
        }
        return model;
    }

    /// <summary>What follows, by this model's own counts alone, with how many changes after the last chord it has seen.</summary>
    Dictionary<string, double> Shares(ChordSymbol last, ChordSymbol? before, out double seen)
    {
        Dictionary<string, int>? two = null;
        if (before is { } b) _two.TryGetValue($"{Kind(b)}>{ChordSymbol.Mod(last.Root - b.Root)}:{Kind(last)}", out two);
        _one.TryGetValue(Kind(last), out var one);
        double seenTwo = two?.Values.Sum() ?? 0, seenOne = one?.Values.Sum() ?? 0, w = seenTwo / (seenTwo + 5);
        var mix = new Dictionary<string, double>();
        if (two is not null) foreach (var kv in two) mix[kv.Key] = mix.GetValueOrDefault(kv.Key) + w * kv.Value / seenTwo;
        if (one is not null) foreach (var kv in one) mix[kv.Key] = mix.GetValueOrDefault(kv.Key) + (1 - w) * kv.Value / seenOne;
        seen = seenOne;
        return mix;
    }

    /// <summary>
    /// The likeliest chords to follow <paramref name="last"/> (after <paramref name="before"/>), with how often (0 to 1).
    /// What followed both chords and what followed the last one alone are blended, the two-chord count weighing more
    /// the more it has seen: on 77 gospel, jazz and soul packs, each guessed from the others with their chords read as the
    /// app reads them, the chord played next was the first guess 11 times in 100 and among the first six 32 (10 and 30
    /// using one count or the other; tools/MusicOrganiser.TheoryBench --learn). With a <see cref="Prior"/>, your own counts
    /// are blended with it, yours weighing more the more you have: on gospel charts guessed from 40 other gospel charts
    /// the first guess was right 18 times in 100 alone and 24 starting from ChoCo's jazz, and guessed from about 900
    /// it was 26 alone and 27 with it (--corpus).
    /// </summary>
    public IReadOnlyList<(ChordSymbol Chord, double Share)> Next(ChordSymbol last, ChordSymbol? before, int most = 6)
    {
        var mix = Shares(last, before, out var seen);
        if (Prior is { } prior)
        {
            var from = prior.Shares(last, before, out var priorSeen);
            if (priorSeen > 0)
            {
                var mine = seen / (seen + PriorStrength);
                foreach (var key in mix.Keys.ToList()) mix[key] *= mine;
                foreach (var kv in from) mix[kv.Key] = mix.GetValueOrDefault(kv.Key) + (1 - mine) * kv.Value;
            }
        }
        return mix.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(most).Select(kv =>
        {
            var colon = kv.Key.IndexOf(':');
            var root = ChordSymbol.Mod(last.Root + int.Parse(kv.Key[..colon]));
            var chord = ChordSymbol.Parse(ChordSymbol.Names[root] + kv.Key[(colon + 1)..]);
            return (chord, kv.Value);
        }).Where(x => x.chord is not null).Select(x => (x.chord!.Value, x.Item2)).ToList();
    }
}
