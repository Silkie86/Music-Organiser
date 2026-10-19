namespace MusicOrganiser.Core.Parsers;

/// <summary>How much of a file format the reader actually understands.</summary>
public enum Confidence
{
    /// <summary>Format is well understood; data is read directly.</summary>
    Full,
    /// <summary>Most data is read directly; some details may be missing.</summary>
    Partial,
    /// <summary>Proprietary binary format; data found by scanning readable text.</summary>
    Heuristic,
    /// <summary>File is identified and catalogued, contents aren't decoded.</summary>
    Catalogue,
}

public enum PluginKind { Unknown, Instrument, Effect, NoteEffect }

public sealed record PluginRef(string Name, string Vendor = "", PluginKind Kind = PluginKind.Unknown,
                               string Format = "", string Path = "");

/// <param name="Muted">Muted in the piano roll, so it doesn't play (FL Studio).</param>
/// <param name="Step">Entered in FL's step sequencer: it has no length of its own and FL plays it until its sound ends.</param>
public readonly record struct NoteEvent(long Start, long Length, int Key, int Velocity, bool Muted = false, bool Step = false);

public sealed class MidiClip
{
    public required string Name { get; init; }
    public required int Ppq { get; init; }
    /// <summary>Track name → notes, in insertion order.</summary>
    public List<KeyValuePair<string, List<NoteEvent>>> Tracks { get; } = new();

    public List<NoteEvent> Track(string name)
    {
        foreach (var t in Tracks)
            if (t.Key == name) return t.Value;
        var list = new List<NoteEvent>();
        Tracks.Add(new(name, list));
        return list;
    }
}

public sealed record PresetBlob(string Name, string Plugin, string Extension, byte[] Data)
{
    /// <summary>Instrument or effect, where the project says (FL Studio).</summary>
    public PluginKind Kind { get; init; }
    /// <summary>One of the DAW's own plugins (an FL Studio plugin) rather than a third-party VST.</summary>
    public bool Native { get; init; }
    /// <summary>Where it sits when that tells copies apart, e.g. "Insert 3 · Vocals" or "Master".</summary>
    public string Where { get; init; } = "";
    /// <summary>FL Studio: the channel an instrument is on (its place in the channel rack, from 0).</summary>
    public int? Channel { get; init; }
    /// <summary>A VST2's program that was selected when it was saved (FL Studio), which the host selects again after loading it.</summary>
    public int Program { get; init; }
}

/// <summary>Whether a project plays a sample it holds (see <see cref="ProjectInfo.Unplayed"/>).</summary>
public enum SampleUse
{
    Plays = 0,
    /// <summary>Its channel or track has no notes or clips.</summary>
    NoNotes = 1,
    /// <summary>It has notes or clips, but all of them are muted.</summary>
    Muted = 2,
}

public sealed class ProjectInfo
{
    public required string Daw { get; init; }
    public Confidence Confidence { get; set; } = Confidence.Partial;
    public double? Tempo { get; set; }
    public string Version { get; set; } = "";
    public string Title { get; set; } = "";
    public List<PluginRef> Plugins { get; } = new();
    public List<string> Samples { get; } = new();
    public List<string> Tracks { get; } = new();
    /// <summary>External instruments the project talks to (e.g. Bitwig's HW Instrument devices).</summary>
    public List<string> Hardware { get; } = new();
    public Dictionary<string, string> Details { get; } = new();
    /// <summary>When the project was first made, as the project file itself records it (FL Studio keeps this through every re-save).</summary>
    public DateTime? Created { get; set; }
    public List<string> Warnings { get; } = new();
    public bool CanExtractMidi { get; set; }
    public bool CanExtractPresets { get; set; }
    /// <summary>
    /// Samples in <see cref="Samples"/> that this save doesn't play, and why. Only readers that can tell fill it in
    /// (FL Studio so far); a sample not listed plays, or its reader can't say.
    /// </summary>
    public Dictionary<string, SampleUse> Unplayed { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ProjectInfo Dedupe()
    {
        var seen = new HashSet<(string, PluginKind)>();
        var plugins = Plugins.Where(p => !string.IsNullOrWhiteSpace(p.Name) &&
                                         seen.Add((p.Name.ToLowerInvariant(), p.Kind))).ToList();
        Plugins.Clear();
        Plugins.AddRange(plugins);
        var samples = Samples.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Samples.Clear();
        Samples.AddRange(samples);
        return this;
    }
}

/// <summary>A reader for one project file format.</summary>
public interface IProjectReader
{
    string Daw { get; }
    IReadOnlyList<string> Extensions { get; }
    /// <summary>
    /// Goes up by one whenever the reader learns to read its projects better, so a scan reads that DAW's projects again
    /// even though their files haven't changed.
    /// </summary>
    int Version => 1;
    ProjectInfo Read(string path);
    IReadOnlyList<MidiClip> ExtractMidi(string path) => Array.Empty<MidiClip>();
    /// <summary>Each channel or track's notes across the song, with what plays them.</summary>
    IReadOnlyList<MidiChannel> ExtractChannels(string path) => MidiChannel.FromClips(ExtractMidi(path));
    IReadOnlyList<PresetBlob> ExtractPresets(string path) => Array.Empty<PresetBlob>();
}
