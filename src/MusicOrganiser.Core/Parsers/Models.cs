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

public readonly record struct NoteEvent(long Start, long Length, int Key, int Velocity);

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

public sealed record PresetBlob(string Name, string Plugin, string Extension, byte[] Data);

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
    public List<string> Warnings { get; } = new();
    public bool CanExtractMidi { get; set; }
    public bool CanExtractPresets { get; set; }

    public ProjectInfo Dedupe()
    {
        // The same plugin in two formats stays twice: an AU and a VST3 of one synth load differently.
        // A copy with no format is dropped when the same plugin also turned up with one.
        var named = Plugins.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();
        var withFormat = named.Where(p => p.Format.Length > 0).Select(p => (p.Name.ToLowerInvariant(), p.Kind)).ToHashSet();
        var seen = new HashSet<(string, PluginKind, string)>();
        var plugins = named.Where(p => (p.Format.Length > 0 || !withFormat.Contains((p.Name.ToLowerInvariant(), p.Kind))) &&
                                       seen.Add((p.Name.ToLowerInvariant(), p.Kind, p.Format.ToUpperInvariant()))).ToList();
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
    ProjectInfo Read(string path);
    IReadOnlyList<MidiClip> ExtractMidi(string path) => Array.Empty<MidiClip>();
    /// <summary>Each channel or track's notes across the song, with what plays them.</summary>
    IReadOnlyList<MidiChannel> ExtractChannels(string path) => MidiChannel.FromClips(ExtractMidi(path));
    IReadOnlyList<PresetBlob> ExtractPresets(string path) => Array.Empty<PresetBlob>();
}
