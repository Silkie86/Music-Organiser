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
            lane = ChordLane.Detect(HarmonySource(layers, notes).Where(n => n.Start < length).ToList(), length);
            typed = false;
        }

        KeyEstimate? key = null;
        var keyTyped = false;
        if (typedKey.Trim().Length > 0 && MusicTheory.ParseKey(typedKey) is { } k)
        {
            key = new KeyEstimate(k.Tonic, k.Minor, 1);
            keyTyped = true;
        }
        else
        {
            var chroma = new double[12];
            foreach (var n in notes) chroma[n.Key % 12] += Math.Clamp(n.Length / (double)ChordLane.Bar, 1 / 16.0, 2);
            // The chords count too, so a typed chart with no parts yet still has a key.
            foreach (var c in lane)
                for (var p = 0; p < 12; p++)
                    if ((c.Chord.Mask >> p & 1) != 0) chroma[p] += 0.5 * c.Length / ChordLane.Bar + (p == c.Chord.Root ? 0.25 : 0);
            key = MusicTheory.EstimateKey(chroma);
            // A ii–V–I lane says its key louder than the notes do: home is where the last V resolves.
            if (HomeOf(lane) is { } home && key is { } est && est.Tonic != home.Tonic) key = new KeyEstimate(home.Tonic, home.Minor, est.Confidence);
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
    /// The notes the chords are heard from (Solomon, 2026-10-19: "only chord and bass clips decide the harmony; where no
    /// chord or bass clip plays, everything playing there counts"). Under a chord clip, the chord and bass clips there;
    /// everywhere else, every part that plays. Bass alone doesn't say enough, so it only counts alongside chords.
    /// </summary>
    static List<NoteEvent> HarmonySource(IReadOnlyList<PlaygroundLayer> layers, IReadOnlyList<NoteEvent> all)
    {
        // Each part's clips with what they play as; a part with no clips counts as one clip the whole length.
        var pieces = layers.SelectMany(l => l.Clips.Count == 0
            ? [(Role: l.Role, Start: 0L, End: long.MaxValue, Notes: (IReadOnlyList<NoteEvent>)l.Notes)]
            : l.Clips.Select(c => (Role: c.Role == MidiRoles.Other ? l.Role : c.Role, c.Start, c.End, Notes: (IReadOnlyList<NoteEvent>)c.Render()))).ToList();
        var chordSpans = pieces.Where(p => p.Role == MidiRoles.Chords && p.Notes.Count > 0).Select(p => (p.Start, p.End)).ToList();
        if (chordSpans.Count == 0) return all.ToList();
        bool UnderChords(NoteEvent n) => chordSpans.Any(s => n.Start >= s.Start && n.Start < s.End);
        // Under chords, a bassline says the harmony with the notes it strikes with them: the ones between are a walking
        // bass's passing notes, which would colour the chords heard (Dm7 heard as D7#9).
        const int Near = PhraseAnalyser.Ppq / 8;
        var chordNotes = pieces.Where(p => p.Role == MidiRoles.Chords).SelectMany(p => p.Notes).ToList();
        var struck = chordNotes.Select(n => (n.Start + Near / 2) / Near).ToHashSet();
        bool WithTheChords(NoteEvent n) => struck.Contains((n.Start + Near / 2) / Near);
        return chordNotes.Concat(pieces.Where(p => p.Role == MidiRoles.Bass).SelectMany(p => p.Notes).Where(WithTheChords))
                     .Where(n => !n.Muted && UnderChords(n))
                     .Concat(all.Where(n => !UnderChords(n))).OrderBy(n => n.Start).ToList();
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
        return HarmonyTarget.FromPart(name, MidiRoles.Chords, padded, chroma, PhraseAnalyser.Progression(notes), Key);
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

    /// <summary>0 to 1, or null when no note falls on a chord.</summary>
    public static double? Score(IReadOnlyList<NoteEvent> notes, IReadOnlyList<TimedChord> lane, long length, string role,
                                HarmonyStyle style = HarmonyStyle.JazzGospel)
    {
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
                var v = ChordScales.Sit(chord, pc, style);
                // A tension on beat 1 or 3 of a tune counts less than a chord note there: melodies land on chord
                // notes on the strong beats and colour the weak ones.
                if (melody && jazz && metric >= 1.25 && v > 0 && v < 1) v *= 0.6;
                if (bass && metric >= 1.25)
                    v = pc == chord.Bass ? 1 : pc == chord.Root ? 0.9 : v * 0.75;
                if (struck && v < 0.55 && n.Length <= Beat / 2 && metric < 1.25)
                {
                    // A short note between beats: a passing or approach note if the next note steps to one that sits.
                    var next = sorted.Skip(i + 1).FirstOrDefault(m => m.Start > n.Start);
                    if (next.Length > 0 && Math.Abs(next.Key - n.Key) <= 2 && ChordLane.At(lane, next.Start + 1) is { } then &&
                        ChordScales.Sit(then, next.Key % 12, style) >= 0.75)
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
    /// How closely a chord part's own chords follow the lane, half a bar at a time, with substitutes counting as alike
    /// (<see cref="ChordKinship"/>): a part playing Db7 where the lane has G7 still follows it. Null when it plays too
    /// few chords to say.
    /// </summary>
    public static double? Follow(IReadOnlyList<NoteEvent> notes, IReadOnlyList<TimedChord> lane, long length)
    {
        var mine = ChordLane.Detect(notes, length);
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
    public int Percent => (int)Math.Round(Score * 100);
    public int Move => (Fit?.Semitones ?? 0) + 12 * (Fit?.Octaves ?? 0);
}

/// <summary>
/// Finds library parts that go with a section: the MIDI page's harmony matcher picks the likeliest few hundred
/// (every key tried, played against the section in time), then each is judged again against the section's chord
/// lane with the jazz or classical ear and, for chord parts, on how closely its chords follow the lane. Parts from any
/// song already in the section are left out: a song's own parts always fit each other, which says nothing.
/// </summary>
public static class SectionMatcher
{
    public static IReadOnlyList<PlaygroundMatch> Rank(MidiCatalogue catalogue, SectionHarmony section, string role,
                                                      Func<MidiEntry, IReadOnlyList<NoteEvent>> notesOf, string text = "",
                                                      int limit = 60, CancellationToken ct = default, SectionFeel feel = SectionFeel.Any)
    {
        var energy = feel == SectionFeel.Any ? null : Energy.Target(feel, (int)(section.Length / ChordLane.Bar));
        if (role == MidiRoles.Drums) return Grooves(catalogue, section, notesOf, text, limit, ct, energy, feel);
        var target = section.Target();
        var hits = catalogue.Search(new MidiQuery(text, role, Fit: target, Limit: target is null && energy is null ? limit : Math.Max(limit * 4, 200), Notes: notesOf)
                                    { LeaveSongs = section.Songs }, out _, ct);
        var list = new List<PlaygroundMatch>();
        foreach (var hit in hits)
        {
            ct.ThrowIfCancellationRequested();
            if (section.Hashes.Contains(hit.Entry.Phrase.Hash)) continue;
            var raw = notesOf(hit.Entry);
            if (raw.Count == 0) continue;
            var move = hit.Fit is { } f ? f.Semitones + 12 * (hit.Entry.YourOctave ?? f.Octaves) : 12 * (hit.Entry.YourOctave ?? 0);
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
                continue;
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
            var why = string.Join(" · ", new[]
            {
                sits is { } sv ? $"Sits on the chords {sv * 100:0}%" : "",
                follow is { } fv ? $"follows them {fv * 100:0}%" : "",
                feelText,
                MoveText(move),
            }.Where(s => s.Length > 0));
            list.Add(new PlaygroundMatch(hit.Entry, hit.Fit, Math.Clamp(score, 0, 1), moved, why));
        }
        return list.OrderByDescending(m => m.Score).ThenByDescending(m => m.Entry.Copies).Take(limit).ToList();
    }

    static string MoveText(int move) => move switch
    {
        0 => "as it is",
        > 0 when move % 12 == 0 => $"up {move / 12} octave{(move == 12 ? "" : "s")}",
        < 0 when move % 12 == 0 => $"down {-move / 12} octave{(move == -12 ? "" : "s")}",
        > 0 => $"up {move}",
        _ => $"down {-move}",
    };

    /// <summary>Drums have no harmony: they're matched on where their hits fall against the section's rhythm.</summary>
    static IReadOnlyList<PlaygroundMatch> Grooves(MidiCatalogue catalogue, SectionHarmony section, Func<MidiEntry, IReadOnlyList<NoteEvent>> notesOf,
                                                  string text, int limit, CancellationToken ct, double[]? energy, SectionFeel feel)
    {
        var hits = catalogue.Search(new MidiQuery(text, MidiRoles.Drums, Limit: section.Notes.Count == 0 && energy is null ? limit : 600) { LeaveSongs = section.Songs }, out _, ct);
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
    readonly Dictionary<string, Dictionary<string, int>> _one = new(), _two = new();

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
        var model = new ProgressionModel();
        var n = 0;
        foreach (var e in entries)
        {
            if (++n % 4096 == 0) ct.ThrowIfCancellationRequested();
            if (e.Role != MidiRoles.Chords || e.Chords.Count < 3) continue;
            if (leaveSongs.Count > 0 && leaveSongs.Any(e.IsInSong)) continue;
            var chords = e.Chords.Select(c => ChordSpeller.Spell(c.Mask, c.Root)).OfType<ChordSymbol>().ToList();
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

    /// <summary>The likeliest chords to follow <paramref name="last"/> (after <paramref name="before"/>), with how often (0 to 1).</summary>
    public IReadOnlyList<(ChordSymbol Chord, double Share)> Next(ChordSymbol last, ChordSymbol? before, int most = 6)
    {
        Dictionary<string, int>? row = null;
        if (before is { } b) _two.TryGetValue($"{Kind(b)}>{ChordSymbol.Mod(last.Root - b.Root)}:{Kind(last)}", out row);
        if (row is null || row.Values.Sum() < 8) _one.TryGetValue(Kind(last), out row);
        if (row is null) return [];
        var total = (double)row.Values.Sum();
        return row.OrderByDescending(kv => kv.Value).Take(most).Select(kv =>
        {
            var colon = kv.Key.IndexOf(':');
            var root = ChordSymbol.Mod(last.Root + int.Parse(kv.Key[..colon]));
            var chord = ChordSymbol.Parse(ChordSymbol.Names[root] + kv.Key[(colon + 1)..]);
            return (chord, kv.Value / total);
        }).Where(x => x.chord is not null).Select(x => (x.chord!.Value, x.Item2)).ToList();
    }
}
