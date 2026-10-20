namespace MusicOrganiser.Core.Parsers;

/// <summary>
/// One channel's (FL Studio) or track's (Bitwig, Studio One) notes across the whole song,
/// with what plays them. Positions are song ticks at <see cref="Ppq"/>. <see cref="Source"/> is what to
/// show (the sample, for a channel that has one); <see cref="Plugin"/> the plugin on the channel, if any;
/// <see cref="SamplePath"/> the full path of the channel's sample, when it plays one, which sounds at its own
/// pitch on <see cref="RootKey"/>, shifted by <see cref="PitchCents"/>.
/// </summary>
public sealed record MidiChannel(string Name, string Source, int Ppq, IReadOnlyList<NoteEvent> Notes, string Plugin = "", string SamplePath = "",
                                 int RootKey = 60, int PitchCents = 0)
{
    /// <summary>FL Studio: the channel's place in the channel rack, from 0, which its plugin's settings also carry.</summary>
    public int? Channel { get; init; }

    /// <summary>For previewing: the General MIDI sound to play it with (<see cref="Sounds.GmSounds"/>), or null for the usual stand-in.</summary>
    public int? GmProgram { get; init; }

    /// <summary>For previewing: how loud, from 0 to 1.</summary>
    public float Volume { get; init; } = 1;

    public int Lowest => Notes.Count == 0 ? 0 : Notes.Min(n => n.Key);
    public int Highest => Notes.Count == 0 ? 0 : Notes.Max(n => n.Key);
    public long End => Notes.Count == 0 ? 0 : Notes.Max(n => n.Start + n.Length);
    public int Bars => (int)Math.Ceiling(End / (4.0 * Ppq));
    public int DistinctKeys => Notes.Select(n => n.Key).Distinct().Count();

    /// <summary>The label shown in lists: "Lead (Serum)", or just "Serum" when they're the same.</summary>
    public string Label =>
        Source.Length == 0 || Source.Equals(Name, StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileNameWithoutExtension(Source).Equals(Name, StringComparison.OrdinalIgnoreCase)
            ? Name : $"{Name} ({Source})";

    // Found inside a word too ("kick2", "hihat", "fpc_kit"), so none of these may hide inside an everyday word: "hat"
    // is in "phat" and "perc" in "percussive", so those two are matched with a pattern instead.
    static readonly string[] DrumParts = ["kick", "snare", "clap", "drum", "cymbal", "shaker", "fpc", "909"];
    static readonly HashSet<string> DrumWords = ["rim", "tom", "toms", "ride", "crash", "kit", "percussion"];
    static readonly System.Text.RegularExpressions.Regex HatOrPerc = new(@"^((hi|open|closed|oh|ch)?hats?|percs?)\d*$");
    static readonly HashSet<string> BassWords = ["bass", "sub", "subs", "reese"];

    /// <summary>A rough guess at what the part is, from its notes and what plays it.</summary>
    public string Kind
    {
        get
        {
            if (Notes.Count == 0) return "";
            var words = System.Text.RegularExpressions.Regex.Split($"{Name} {Source}".ToLowerInvariant(), "[^a-z0-9]+");
            if (words.Any(w => DrumParts.Any(w.Contains) || DrumWords.Contains(w) || HatOrPerc.IsMatch(w))) return "Drums";
            // An 808 is a kick on one note, but a bassline once it moves or is called a bass ("808 Sub").
            if (DistinctKeys == 1 && words.Any(w => w.Contains("808")) && !words.Any(w => BassWords.Contains(w) || w.EndsWith("bass"))) return "Drums";
            if (DistinctKeys == 1) return "Hits";
            var overlapping = Notes.GroupBy(n => n.Start).Count(g => g.Count() >= 3);
            if (overlapping * 4 >= Notes.GroupBy(n => n.Start).Count()) return "Chords";
            if (Highest <= 52) return "Bass";
            return "Melody";
        }
    }

    public static string KeyName(int key)
    {
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return $"{names[key % 12]}{key / 12 - 1}";
    }

    public string RangeText => Notes.Count == 0 ? "" : Lowest == Highest ? KeyName(Lowest) : $"{KeyName(Lowest)}–{KeyName(Highest)}";

    /// <summary>
    /// For formats read clip by clip: each track's clips laid one after another, each
    /// padded to whole bars, so nothing overlaps.
    /// </summary>
    public static IReadOnlyList<MidiChannel> FromClips(IReadOnlyList<MidiClip> clips)
    {
        if (clips.Count == 0) return [];
        var ppq = clips[0].Ppq;
        var bar = 4L * ppq;
        var byTrack = new Dictionary<string, (List<NoteEvent> Notes, long At)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var clip in clips)
        {
            foreach (var (track, notes) in clip.Tracks)
            {
                if (notes.Count == 0) continue;
                if (!byTrack.TryGetValue(track, out var t)) { byTrack[track] = t = (new List<NoteEvent>(), 0); order.Add(track); }
                var scale = (double)ppq / clip.Ppq;
                var shifted = notes.Select(n => n with
                {
                    Start = t.At + (long)Math.Round(n.Start * scale),
                    Length = Math.Max(1, (long)Math.Round(n.Length * scale)),
                }).ToList();
                t.Notes.AddRange(shifted);
                var end = shifted.Max(n => n.Start + n.Length);
                byTrack[track] = (t.Notes, Math.Max(t.At + bar, (end + bar - 1) / bar * bar));
            }
        }
        return order.Select(t => new MidiChannel(t, "", ppq, byTrack[t].Notes.OrderBy(n => n.Start).ThenBy(n => n.Key).ToList())).ToList();
    }

    /// <summary>What plays notes in the song: the plugin (or sample) behind each channel with notes.</summary>
    public static IReadOnlySet<string> Players(IEnumerable<MidiChannel> channels) =>
        channels.Where(c => c.Notes.Count > 0)
                .SelectMany(c => new[] { c.Plugin, c.Source }).Where(s => s.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>As a MIDI clip with a single track, ready for <c>MidiWriter</c>.</summary>
    public MidiClip ToClip()
    {
        var clip = new MidiClip { Name = Label, Ppq = Ppq };
        clip.Track(Name).AddRange(Notes);
        return clip;
    }
}
