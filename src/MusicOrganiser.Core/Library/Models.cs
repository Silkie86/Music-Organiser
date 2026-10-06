using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Library;

public static class Statuses
{
    public static readonly IReadOnlyList<string> All =
        ["Idea", "In progress", "Arranging", "Mixing", "Finished", "Released", "Archived"];
}

/// <summary>One project as shown in the library list.</summary>
public sealed class ProjectSummary
{
    public long Id { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Daw { get; init; }
    public string Folder { get; init; } = "";
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public double? Tempo { get; init; }
    public string Version { get; init; } = "";
    public Confidence Confidence { get; init; }
    public bool FileMissing { get; init; }
    public string? Error { get; init; }
    public DateTime? LastOpened { get; init; }

    public int PluginCount { get; init; }
    public int SampleCount { get; init; }
    public int MissingPlugins { get; init; }
    public int MissingSamples { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> PluginNames { get; init; } = [];
    public IReadOnlyList<string> Hardware { get; init; } = [];

    // Your own organisation, kept across rescans.
    public string Status { get; set; } = "Idea";
    public int Rating { get; set; }
    public bool Favourite { get; set; }
    public string MusicalKey { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed record ProjectPlugin(string Name, string Vendor, PluginKind Kind, string Format, string Path, bool Installed);

public sealed record ProjectSample(string Path, bool Found);

public sealed class ProjectDetail
{
    public required ProjectSummary Summary { get; init; }
    public string Title { get; init; } = "";
    public IReadOnlyList<ProjectPlugin> Plugins { get; init; } = [];
    public IReadOnlyList<ProjectSample> Samples { get; init; } = [];
    public IReadOnlyList<string> Tracks { get; init; } = [];
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool CanExtractMidi { get; init; }
    public bool CanExtractPresets { get; init; }
}

public sealed record PluginUsage(string Name, string Vendor, string Format, int Projects, string Daws, bool Installed);

public sealed record SampleUsage(string Path, int Projects, bool Found);
