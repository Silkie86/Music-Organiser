using System.Numerics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Midi;

/// <summary>What a MIDI part is for, as the MIDI browser sorts them.</summary>
public static class MidiRoles
{
    public const string Chords = "Chords", Melody = "Melody", Bass = "Bass", Arp = "Arp", Drums = "Drums", Other = "Other";

    public static readonly IReadOnlyList<string> All = [Chords, Melody, Bass, Arp, Drums, Other];

    /// <summary>How the browser's chips name a kind of part: "Basslines", "Melodies".</summary>
    public static string Plural(string role) => role switch
    {
        Chords => "Chords", Melody => "Melodies", Bass => "Basslines", Arp => "Arps", Drums => "Drums", _ => "Other",
    };

    public static string FromPlural(string plural) => All.FirstOrDefault(r => Plural(r) == plural) ?? plural;
}

/// <summary>
/// One chord: its root and every pitch class sounding, as a 12-bit set (bit 0 = C). Keeping the notes rather than just a
/// name lets two chords be compared by how many notes they share, so Am7 and C are close and Am and F# are not.
/// </summary>
public readonly record struct PhraseChord(int Root, int Mask)
{
    public PhraseChord Transposed(int semitones) => new(Mod12(Root + semitones), Rotate(Mask, semitones));

    public static int Rotate(int mask, int semitones)
    {
        var s = Mod12(semitones);
        return s == 0 ? mask : ((mask << s) | (mask >> (12 - s))) & 0xFFF;
    }

    static int Mod12(int n) => ((n % 12) + 12) % 12;

    /// <summary>
    /// 0 to 1: the share of notes the two have in common, a little more when they're built on the same root. Rhythm
    /// plays no part; only which notes sound.
    /// </summary>
    public double Similarity(PhraseChord other)
    {
        var union = BitOperations.PopCount((uint)(Mask | other.Mask));
        if (union == 0) return 0;
        var shared = (double)BitOperations.PopCount((uint)(Mask & other.Mask)) / union;
        return Math.Min(1, shared + (Root == other.Root ? 0.25 : 0));
    }

    /// <summary>The chord's name, such as "Am7", or its root with a "?" when it isn't a chord with a name.</summary>
    public string Name
    {
        get
        {
            var chroma = new double[12];
            for (var i = 0; i < 12; i++) if ((Mask >> i & 1) != 0) chroma[i] = 1;
            chroma[Root] += 0.5;
            return MusicTheory.NameChord(chroma, 0.6)?.Name ?? MusicTheory.NoteNames[Root] + "?";
        }
    }

    public override string ToString() => $"{Root}:{Mask:x3}";

    public static string Encode(IEnumerable<PhraseChord> chords) => string.Join(" ", chords);

    public static IReadOnlyList<PhraseChord> Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var list = new List<PhraseChord>();
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.IndexOf(':');
            if (colon > 0 && int.TryParse(part[..colon], out var root) &&
                int.TryParse(part[(colon + 1)..], System.Globalization.NumberStyles.HexNumber, null, out var mask))
                list.Add(new PhraseChord(root, mask));
        }
        return list;
    }
}

/// <summary>What the browser knows about one MIDI part, worked out from its notes and names.</summary>
/// <param name="Role">Chords, Melody, Bass, Arp, Drums or Other (<see cref="MidiRoles"/>).</param>
/// <param name="RoleWhy">Why, in a few words, for the tooltip.</param>
/// <param name="Chroma">How long each of the 12 pitch classes sounds, as shares adding up to 1.</param>
/// <param name="Chords">The chords in order, a repeated chord once, with no timing: what harmony matching compares.</param>
/// <param name="Features">The part's shape, the same whatever key it's in, for "more like this" (<see cref="PhraseAnalyser.FeatureCount"/> numbers).</param>
/// <param name="Hash">The same notes give the same hash, so a pattern saved in 20 versions of a song is shown once.</param>
public sealed record PhraseAnalysis(string Role, string RoleWhy, int Bars, int Lowest, int Highest, KeyEstimate? Key,
                                    float[] Chroma, IReadOnlyList<PhraseChord> Chords, float[] Features, string Hash);

/// <summary>
/// Works out what a MIDI part is: chords, a melody, a bassline, an arp or drums. Its notes decide (how many sound at once,
/// how low, how fast and even), with names as a hint: a part called "Bass" or a file in a "Chords" folder. Everything
/// here is plain music theory, so it's quick enough for hundreds of thousands of parts and says why it decided.
/// </summary>
public static partial class PhraseAnalyser
{
    /// <summary>Every phrase is kept at this resolution: 96 ticks to a beat.</summary>
    public const int Ppq = 96;
    const int Bar = 4 * Ppq;
    /// <summary>Parts longer than this are cut into phrases of 4 to 16 bars, where the music breathes (<see cref="CutPoints"/>).</summary>
    public const int MaxBars = 16;
    /// <summary>The most notes kept in one phrase.</summary>
    public const int MaxNotes = 4096;
    public const int FeatureCount = 48;

    /// <summary>
    /// The notes at <see cref="Ppq"/>, muted ones left out, moved so the phrase starts in its first bar (a pattern
    /// that starts on bar 9 of a song starts on bar 1 here). With <paramref name="keepPlace"/>, they stay where they are.
    /// </summary>
    public static List<NoteEvent> Normalise(IEnumerable<NoteEvent> notes, int ppq, bool keepPlace = false)
    {
        var scale = (double)Ppq / Math.Max(1, ppq);
        var list = notes.Where(n => !n.Muted)
            .Select(n => n with
            {
                Start = (long)Math.Round(n.Start * scale),
                Length = Math.Max(1, (long)Math.Round((n.Step ? ppq / 4.0 : n.Length) * scale)),
                Step = false,
            })
            .OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
        if (list.Count == 0 || keepPlace) return list;
        var shift = list[0].Start / Bar * Bar;
        if (shift != 0) for (var i = 0; i < list.Count; i++) list[i] = list[i] with { Start = list[i].Start - shift };
        return list;
    }

    public static int BarsOf(IReadOnlyList<NoteEvent> notes) =>
        notes.Count == 0 ? 0 : (int)Math.Max(1, Math.Ceiling(notes.Max(n => n.Start + n.Length) / (double)Bar - 0.01));

    /// <summary>This many empty bars or more inside a part split it into sections, each kept on its own.</summary>
    public const int GapBars = 4;

    /// <summary>
    /// A part as phrases, with no empty bars before or after any of them (Solomon, 2026-10-15: "midi with a lot of space
    /// either side truncate them"). <see cref="GapBars"/> or more empty bars split it into sections; a section longer than
    /// <see cref="MaxBars"/> bars is cut where the music breathes (<see cref="CutPoints"/>). Each comes with where it
    /// starts in the part ("bars 9–16"), or "" when it's the whole part, and how far it was moved to start on bar 1 (in
    /// ticks), so what sits in the part can be found in the phrase. Pieces with the same notes are kept once.
    /// </summary>
    /// <param name="clipStarts">Bars (from the part's start) where a clip starts, when the part was joined from clips: likely places to cut.</param>
    public static IEnumerable<(string Where, List<NoteEvent> Notes, long From)> Split(List<NoteEvent> notes, IReadOnlySet<long>? clipStarts = null)
    {
        if (notes.Count == 0) yield break;
        var sections = Sections(notes);
        var whole = sections.Count == 1 && BarsOf(notes) <= MaxBars;
        var seen = new HashSet<string>();
        foreach (var section in sections)
        {
            var first = section[0].Start / Bar;
            var bars = BarsOf(section) - (int)first;
            var pieces = new List<List<NoteEvent>>();
            if (bars <= MaxBars) pieces.Add(section);
            else
            {
                var bounds = CutPoints(section, first, bars, clipStarts).Prepend(first).Append(first + bars).ToList();
                for (var i = 0; i + 1 < bounds.Count; i++)
                {
                    long from = bounds[i] * Bar, to = bounds[i + 1] * Bar;   // notes held past the piece are cut at its end
                    var piece = section.Where(n => n.Start >= from && n.Start < to).Select(n => n with { Length = Math.Min(n.Length, to - n.Start) }).ToList();
                    if (piece.Count > 0) pieces.Add(piece);
                }
            }
            foreach (var piece in pieces)
            {
                if (!whole && piece.Count < 3) continue;
                var firstBar = piece[0].Start / Bar;
                var trimmed = piece.Select(n => n with { Start = n.Start - firstBar * Bar }).Take(MaxNotes).ToList();
                if (!seen.Add(HashOf(trimmed))) continue;
                var lastBar = firstBar + BarsOf(trimmed);
                yield return (whole ? "" : firstBar + 1 == lastBar ? $"bar {lastBar}" : $"bars {firstBar + 1}–{lastBar}", trimmed, firstBar * Bar);
            }
        }
    }

    /// <summary>
    /// Where to cut a long section into phrases of 4, 8, 12 or 16 bars (the last can be shorter), as bar numbers: where
    /// the music breathes rather than every 8 bars (Solomon, 2026-10-16: "this could be part of a larger phrase"). A
    /// piece is a good phrase when the music after it plays it again, and a bar line is a good place to cut when nothing
    /// is held across it, the last beat before it rests or lands back on the note the section started on, or a clip
    /// starts there. Of every way to cut the section, the best placed wins. Each cut costs a little, so a 32-bar loop of
    /// one 8-bar progression becomes four 8-bar pieces (kept as one), not eight 4-bar halves.
    /// </summary>
    static List<long> CutPoints(List<NoteEvent> section, long first, int bars, IReadOnlySet<long>? clipStarts)
    {
        // Each bar's notes and where they start in it, as one number: equal bars play the same thing.
        var content = new HashCode[bars];
        var home = section.Where(n => n.Start - section[0].Start < Ppq / 16).Min(n => n.Key) % 12;
        foreach (var n in section)
        {
            var bar = (int)(n.Start / Bar - first);
            if (bar >= 0 && bar < bars) content[bar].Add((n.Key, n.Start % Bar));
        }
        var barIds = content.Select(c => c.ToHashCode()).ToArray();

        // How much the bars after a piece repeat it (0 to 1).
        double Repeated(int from, int to)
        {
            var length = to - from;
            var compared = Math.Min(length, bars - to);
            if (compared <= 0) return 0;
            var same = 0;
            for (var i = 0; i < compared; i++) if (barIds[from + i] == barIds[to + i]) same++;
            return (double)same / length;
        }

        // How good each bar line is to cut at.
        var good = new double[bars];
        for (var b = 1; b < bars; b++)
        {
            var at = (first + b) * Bar;
            var score = -0.6;   // each cut costs this much, so only good places beat a longer piece
            var held = section.Count(n => n.Start < at && n.Start + n.Length > at + Ppq / 4);
            var sounding = section.Where(n => n.Start < at && n.Start + n.Length > at - Ppq).ToList();
            if (held > 0) score -= 0.6 * Math.Min(1, held / 2.0);
            if (sounding.Count == 0) score += 0.3;   // a rest before the bar line
            else if (sounding.Min(n => n.Key) % 12 == home) score += 0.4;   // back home
            if (clipStarts?.Contains(first + b) == true) score += 0.5;
            good[b] = score;
        }

        // The best way through: best[p] is the best score for cutting the first p bars into pieces.
        var best = Enumerable.Repeat(double.NegativeInfinity, bars + 1).ToArray();
        var from = new int[bars + 1];
        best[0] = 0;
        for (var p = 1; p <= bars; p++)
            for (var length = 1; length <= Math.Min(MaxBars, p); length++)
            {
                var size = length switch { 8 or 16 => 0.3, 4 => 0.0, 12 => -0.1, _ => p == bars ? -0.3 : double.NaN };
                var start = p - length;
                if (double.IsNaN(size) || double.IsNegativeInfinity(best[start])) continue;
                var score = best[start] + (start > 0 ? good[start] : 0) + size + 1.2 * Repeated(start, p);
                if (score > best[p]) (best[p], from[p]) = (score, start);
            }
        var cuts = new List<long>();
        for (var p = from[bars]; p > 0; p = from[p]) cuts.Add(first + p);
        cuts.Reverse();
        return cuts;
    }

    /// <summary>The part cut wherever <see cref="GapBars"/> or more bars go by with no note playing.</summary>
    static List<List<NoteEvent>> Sections(List<NoteEvent> notes)
    {
        var sections = new List<List<NoteEvent>> { new() };
        long playingUntil = 0;
        foreach (var n in notes)
        {
            if (sections[^1].Count > 0 && n.Start / Bar - (playingUntil + Bar - 1) / Bar >= GapBars) sections.Add([]);
            sections[^1].Add(n);
            playingUntil = Math.Max(playingUntil, n.Start + n.Length);
        }
        return sections;
    }

    // ---- what kind of part ----

    static readonly HashSet<string> DrumWords =
    [
        "kick", "kicks", "snare", "snares", "hat", "hats", "hihat", "hihats", "hh", "clap", "claps", "drum", "drums", "perc",
        "percs", "percussion", "cymbal", "cymbals", "shaker", "fpc", "909", "rim", "rimshot", "tom", "toms", "ride", "crash",
        "kit", "beat", "beats", "groove", "grooves", "break", "breaks", "tops", "snr", "kck", "battery",
    ];
    static readonly HashSet<string> BassWords = ["bass", "basses", "bassline", "basslines", "sub", "subs", "subbass", "reese", "808", "808s"];
    static readonly HashSet<string> ChordWords = ["chord", "chords", "prog", "progs", "progression", "progressions", "pad", "pads", "stab", "stabs", "harmony"];
    static readonly HashSet<string> MelodyWords = ["melody", "melodies", "melodic", "lead", "leads", "hook", "hooks", "topline", "toplines", "riff", "riffs", "line", "counter"];
    static readonly HashSet<string> ArpWords = ["arp", "arps", "arpeggio", "arpeggios", "arpeggiated", "arpeggiator"];

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWord();

    /// <summary>What the names say the part is: each kind the words point to.</summary>
    public static HashSet<string> RolesNamed(string hints)
    {
        var roles = new HashSet<string>();
        foreach (var word in NonWord().Split(hints.ToLowerInvariant()))
        {
            if (word.Length == 0) continue;
            if (BassWords.Contains(word) || word.EndsWith("bass", StringComparison.Ordinal)) roles.Add(MidiRoles.Bass);
            if (word is not ("808" or "808s") && DrumWords.Contains(word)) roles.Add(MidiRoles.Drums);
            if (ChordWords.Contains(word)) roles.Add(MidiRoles.Chords);
            if (MelodyWords.Contains(word)) roles.Add(MidiRoles.Melody);
            if (ArpWords.Contains(word)) roles.Add(MidiRoles.Arp);
        }
        return roles;
    }

    /// <summary>
    /// The General MIDI drum notes, played like drums: a part using kick, snare and hats from these is a drum pattern.
    /// Those notes are also C2 to Bb2, where plenty of basslines and low chords sit (C minor: C, Eb, G, Bb), so the notes
    /// alone aren't enough: drums keep the hats going, hit with the kick and snare or in between them, where a bassline
    /// only passes through Bb now and then and chords stack three notes or more.
    /// </summary>
    static bool LooksLikeGmDrums(IReadOnlyCollection<int> keys, IReadOnlyList<NoteEvent> notes, Shape s)
    {
        if (!keys.All(k => k is >= 35 and <= 81) ||
            !keys.Any(k => k is 35 or 36) || !keys.Any(k => k is 38 or 39 or 40) || !keys.Any(k => k is 42 or 44 or 46))
            return false;
        if (s.ChordShare >= 0.35) return false;
        var onsets = notes.GroupBy(n => n.Start).ToList();
        var together = onsets.Count(g => g.Select(n => n.Key).Distinct().Count() >= 2) / (double)onsets.Count;
        var hats = notes.Count(n => n.Key is 42 or 44 or 46) / (double)notes.Count;
        return hats >= 0.25 && together >= 0.15 || hats >= 0.4;
    }

    /// <param name="notes">At <see cref="Ppq"/>, as <see cref="Normalise"/> gives them.</param>
    /// <param name="hints">Names that may say what the part is: its track, pattern, file and folders.</param>
    /// <param name="drums">Known to be drums: on MIDI channel 10, or played by a drum plugin.</param>
    public static PhraseAnalysis Analyse(IReadOnlyList<NoteEvent> notes, string hints = "", bool drums = false)
    {
        var shape = Shape.Of(notes);
        var (role, why) = Role(shape, notes, hints, drums);

        var chroma = new float[12];
        foreach (var n in notes) chroma[n.Key % 12] += (float)Math.Clamp(n.Length / (double)Bar, 1 / 16.0, 2);
        var total = chroma.Sum();
        if (total > 0) for (var i = 0; i < 12; i++) chroma[i] /= total;

        var pitched = role is not MidiRoles.Drums;
        var key = pitched && notes.Count >= 4 && chroma.Count(c => c > 0) >= 3
            ? MusicTheory.EstimateKey(chroma.Select(c => (double)c).ToArray())
            : null;
        var chords = pitched ? Progression(notes, role == MidiRoles.Chords) : [];
        // A key and its relative major or minor share every note; a progression that starts on the other one's root
        // (Cm – Ab – Eb – Bb) is in that one.
        if (key is not null && chords.Count > 0)
        {
            var relative = key.Minor ? (key.Tonic + 3) % 12 : (key.Tonic + 9) % 12;
            if (chords[0].Root == relative) key = key with { Tonic = relative, Minor = !key.Minor };
        }
        return new PhraseAnalysis(role, why, BarsOf(notes), notes.Count == 0 ? 0 : notes.Min(n => n.Key),
                                  notes.Count == 0 ? 0 : notes.Max(n => n.Key), key, chroma, chords,
                                  Features(shape, notes, chroma, key), HashOf(notes));
    }

    /// <summary>Measurements of a part's notes that say what kind of part it is.</summary>
    sealed record Shape(int Notes, int Onsets, double ChordShare, double Voices, double PerBeat, double MedianBeats,
                        int MedianKey, int Distinct, double Evenness, double RepeatShare, double[] Intervals, double NotesPerBar)
    {
        public static Shape Of(IReadOnlyList<NoteEvent> notes)
        {
            if (notes.Count == 0) return new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new double[13], 0);
            // Notes starting within a 64th of each other are struck together.
            var onsets = new List<List<NoteEvent>>();
            foreach (var n in notes.OrderBy(n => n.Start))
                if (onsets.Count > 0 && n.Start - onsets[^1][0].Start <= Ppq / 16) onsets[^1].Add(n);
                else onsets.Add([n]);
            // Three or more notes at once, not all the same note in octaves (a doubled lead is still a melody).
            var chordShare = onsets.Count(o => o.Select(n => n.Key).Distinct().Count() >= 3 &&
                                               o.Select(n => n.Key % 12).Distinct().Count() >= 2) / (double)onsets.Count;

            // How many different notes sound at once, on average, while anything sounds (octave doublings count once).
            var single = notes.DistinctBy(n => (n.Start, n.Key % 12)).OrderBy(n => n.Start).ToList();
            long covered = 0, coverEnd = long.MinValue;
            foreach (var n in single)
            {
                var end = n.Start + n.Length;
                if (end <= coverEnd) continue;
                covered += end - Math.Max(n.Start, coverEnd);
                coverEnd = end;
            }
            var voices = covered <= 0 ? 1 : single.Sum(n => (double)n.Length) / covered;

            var beats = Math.Max(1, BarsOf(notes) * 4);
            var lengths = notes.Select(n => n.Length / (double)Ppq).Order().ToList();
            var keys = notes.Select(n => n.Key).Order().ToList();

            // How even the gaps between notes are (1 = a steady grid, like an arpeggiator).
            var gaps = onsets.Zip(onsets.Skip(1), (a, b) => (double)(b[0].Start - a[0].Start)).ToList();
            var evenness = 0.0;
            if (gaps.Count >= 4)
            {
                var mean = gaps.Average();
                var sd = Math.Sqrt(gaps.Average(g => (g - mean) * (g - mean)));
                evenness = mean <= 0 ? 0 : Math.Clamp(1 - sd / mean, 0, 1);
            }

            // The top line's steps, by size: 0 (the same note again) to an octave or more.
            var tops = onsets.Select(o => o.Max(n => n.Key)).ToList();
            var intervals = new double[13];
            for (var i = 1; i < tops.Count; i++) intervals[Math.Min(12, Math.Abs(tops[i] - tops[i - 1]))]++;
            var steps = intervals.Sum();
            var repeatShare = steps == 0 ? 1 : intervals[0] / steps;
            if (steps > 0) for (var i = 0; i < 13; i++) intervals[i] /= steps;

            // How many different notes each bar uses: an arp keeps to its chord, a tune wanders through the scale.
            var perBar = notes.GroupBy(n => n.Start / Bar).Average(g => g.Select(n => n.Key % 12).Distinct().Count());

            return new(notes.Count, onsets.Count, chordShare, voices, onsets.Count / (double)beats, lengths[lengths.Count / 2],
                       keys[keys.Count / 2], keys.Distinct().Count(), evenness, repeatShare, intervals, perBar);
        }
    }

    static (string Role, string Why) Role(Shape s, IReadOnlyList<NoteEvent> notes, string hints, bool drums)
    {
        if (drums) return (MidiRoles.Drums, "Drums by where it's played (MIDI channel 10 or a drum plugin)");
        if (s.Notes == 0) return (MidiRoles.Other, "");
        var named = RolesNamed(hints);
        var keys = notes.Select(n => n.Key).Distinct().ToList();

        // "808" names a bass when it plays a tune and a drum when it only hits.
        var words = NonWord().Split(hints.ToLowerInvariant()).ToHashSet();
        if (s.Distinct == 1 && words.Overlaps(["808", "808s"]) &&
            !words.Any(w => w is not ("808" or "808s") && (BassWords.Contains(w) || w.EndsWith("bass", StringComparison.Ordinal))))
        {
            named.Remove(MidiRoles.Bass);
            named.Add(MidiRoles.Drums);
        }
        // A name that says a tuned part (and not drums) wins over the drum notes test.
        var namedTuned = !named.Contains(MidiRoles.Drums) &&
                         named.Overlaps([MidiRoles.Bass, MidiRoles.Chords, MidiRoles.Melody, MidiRoles.Arp]);
        if (!namedTuned && LooksLikeGmDrums(keys, notes, s)) return (MidiRoles.Drums, "Uses the General MIDI kick, snare and hat notes, played like drums");

        var (fromNotes, why) = FromNotes(s);
        if (named.Count == 0) return (fromNotes, why);
        if (named.Contains(fromNotes)) return (fromNotes, why + ", and its name agrees");
        if (named.Contains(MidiRoles.Drums) && s.Distinct <= 3 && s.ChordShare < 0.2) return (MidiRoles.Drums, "Named like a drum part");
        // A name wins unless the notes plainly say otherwise.
        foreach (var role in new[] { MidiRoles.Chords, MidiRoles.Arp, MidiRoles.Bass, MidiRoles.Melody })
        {
            if (!named.Contains(role)) continue;
            var contradicts = role == MidiRoles.Chords ? s.ChordShare < 0.1 && s.Voices < 1.4
                            : role == MidiRoles.Bass ? s.ChordShare > 0.5 || s.MedianKey > 64
                            : s.ChordShare > 0.6;
            if (!contradicts) return (role, $"Named as {(role == MidiRoles.Arp ? "an arp" : role == MidiRoles.Chords ? "chords" : "a " + role.ToLowerInvariant())}");
        }
        return (fromNotes, why);
    }

    static (string, string) FromNotes(Shape s)
    {
        if (s.ChordShare >= 0.35 || s.Voices >= 2.5) return (MidiRoles.Chords, "Mostly three or more notes at once");
        if (s.Distinct == 1)
            return s.MedianKey <= 52 ? (MidiRoles.Bass, "One low note, repeated") : (MidiRoles.Other, "One note, repeated");
        if (s.MedianKey <= 50 && s.ChordShare < 0.2) return (MidiRoles.Bass, "Single notes, low down");
        if (s.Onsets >= 8 && s.ChordShare < 0.15 && s.PerBeat >= 1.5 && s.MedianBeats <= 0.5 && s.Evenness >= 0.6 &&
            s.RepeatShare < 0.5 && s.Distinct >= 3 && s.NotesPerBar <= 4.5)
            return (MidiRoles.Arp, "Fast, evenly spaced notes of one chord at a time");
        return (MidiRoles.Melody, "Single notes, one after another");
    }

    // ---- harmony ----

    /// <summary>
    /// The chords, a half bar at a time, in order: the notes that sound for a good share of each half bar (short stabs count). A chord
    /// held for four bars is one chord, the same as one struck once, so only the harmony counts, not the rhythm.
    /// </summary>
    /// <param name="twoNoteChords">Count two notes as a chord too (power chords and thirds in a chord part).</param>
    public static IReadOnlyList<PhraseChord> Progression(IReadOnlyList<NoteEvent> notes, bool twoNoteChords = false, int window = 2 * Ppq)
    {
        if (notes.Count == 0) return [];
        var sorted = notes.OrderBy(n => n.Start).ToList();
        var end = sorted.Max(n => n.Start + n.Length);
        var chords = new List<PhraseChord>();
        var longest = sorted.Max(n => n.Length);
        var first = 0;
        var weights = new double[12];
        for (long w = 0; w < end; w += window)
        {
            Array.Clear(weights);
            var lowest = int.MaxValue;
            // Notes sorted by start: none before here can still be sounding.
            while (first < sorted.Count && sorted[first].Start + longest <= w) first++;
            for (var i = first; i < sorted.Count && sorted[i].Start < w + window; i++)
            {
                var n = sorted[i];
                var overlap = Math.Min(n.Start + n.Length, w + window) - Math.Max(n.Start, w);
                if (overlap < window / 16) continue;   // a grace note or a flam, not part of the harmony
                weights[n.Key % 12] += overlap;
                lowest = Math.Min(lowest, n.Key);
            }
            var max = weights.Max();
            if (max <= 0) continue;
            var mask = 0;
            for (var p = 0; p < 12; p++) if (weights[p] >= 0.25 * max) mask |= 1 << p;
            var count = BitOperations.PopCount((uint)mask);
            if (count < (twoNoteChords ? 2 : 3)) continue;
            var chord = new PhraseChord(RootOf(weights, lowest % 12), mask);
            if (chords.Count == 0 || chords[^1].Mask != chord.Mask) chords.Add(chord);
        }
        return chords;
    }

    /// <summary>The root a chord's notes name (the bass note leans it), else the bass note itself.</summary>
    static int RootOf(double[] weights, int bass)
    {
        var chroma = (double[])weights.Clone();
        chroma[bass] += chroma.Max() * 0.5;
        if (MusicTheory.NameChord(chroma, 0.6) is { } named)
        {
            var name = named.Name;
            var root = Array.IndexOf(MusicTheory.NoteNames, name.Length > 1 && name[1] == '#' ? name[..2] : name[..1]);
            if (root >= 0) return root;
        }
        return bass;
    }

    // ---- likeness ----

    /// <summary>
    /// The numbers "more like this" compares, all the same whatever key the part is in: how chordal, busy, long-noted and
    /// low it is, the size of its steps, its scale (from its own key's root) and where in the bar its notes fall.
    /// </summary>
    static float[] Features(Shape s, IReadOnlyList<NoteEvent> notes, float[] chroma, KeyEstimate? key)
    {
        var f = new float[FeatureCount];
        if (s.Notes == 0) return f;
        f[0] = (float)Math.Min(1, s.Voices / 4);
        f[1] = (float)s.ChordShare;
        f[2] = (float)Math.Min(1, s.PerBeat / 4);
        f[3] = (float)Math.Min(1, s.MedianBeats / 2);
        f[4] = (float)Math.Clamp((s.MedianKey - 24) / 72.0, 0, 1);
        f[5] = (float)Math.Min(1, (notes.Max(n => n.Key) - notes.Min(n => n.Key)) / 36.0);
        for (var i = 0; i < 13; i++) f[6 + i] = (float)s.Intervals[i];
        var tonic = key?.Tonic ?? Array.IndexOf(chroma, chroma.Max());
        var top = Math.Max(1e-6f, chroma.Max());
        for (var i = 0; i < 12; i++) f[19 + i] = chroma[(tonic + i) % 12] / top;
        var grid = new float[16];
        foreach (var n in notes) grid[(int)(n.Start % Bar * 16 / Bar)]++;
        var gmax = Math.Max(1, grid.Max());
        for (var i = 0; i < 16; i++) f[31 + i] = grid[i] / gmax;
        f[47] = key is { Minor: true } ? 1 : 0;
        return f;
    }

    public static double Likeness(float[] a, float[] b)
    {
        double ab = 0, aa = 0, bb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            // The kind of part (the first six) counts for more than the finer detail.
            var w = i < 6 ? 3.0 : 1.0;
            ab += w * a[i] * b[i];
            aa += w * a[i] * a[i];
            bb += w * b[i] * b[i];
        }
        return aa <= 0 || bb <= 0 ? 0 : ab / Math.Sqrt(aa * bb);
    }

    public static string HashOf(IReadOnlyList<NoteEvent> notes)
    {
        var bytes = new byte[notes.Count * 9];
        var i = 0;
        foreach (var n in notes.OrderBy(n => n.Start).ThenBy(n => n.Key))
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i), (int)n.Start);
            BitConverter.TryWriteBytes(bytes.AsSpan(i + 4), (int)n.Length);
            bytes[i + 8] = (byte)n.Key;
            i += 9;
        }
        return Convert.ToHexString(SHA256.HashData(bytes), 0, 8);
    }
}
