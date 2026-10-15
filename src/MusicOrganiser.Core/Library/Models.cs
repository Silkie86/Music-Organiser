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
    /// <summary>When the file was last written, as the scan saw it (Unix seconds): <see cref="Modified"/> as stored.</summary>
    public long Mtime { get; init; }
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
    /// <summary>The key the project's notes point to (drums left out), or "" when they don't point to one.</summary>
    public string DetectedKey { get; init; } = "";
    /// <summary>When the project was first made, as its file records it (FL Studio does), or null.</summary>
    public DateTime? Created { get; init; }
    /// <summary>How many notes play in it, how many bars they run, and how many channels or tracks it has; -1 when unknown.</summary>
    public int NoteCount { get; init; } = -1;
    public int LengthBars { get; init; } = -1;
    public int TrackCount { get; init; } = -1;
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

/// <summary>
/// A plugin across your projects. <see cref="Unlocks"/> counts the projects it's the only missing plugin of, so
/// installing it would open them with nothing missing; <see cref="LastUsed"/> is when a project using it was last saved.
/// </summary>
public sealed record PluginUsage(string Name, string Vendor, string Format, int Projects, string Daws, bool Installed,
                                 int Unlocks = 0, DateTime? LastUsed = null, PluginKind Kind = PluginKind.Unknown);

/// <summary>A project that uses a plugin, for the Plugins page's side panel.</summary>
public sealed record PluginProject(long Id, string Name, string Daw, DateTime Saved);

public sealed record SampleUsage(string Path, int Projects, bool Found);
