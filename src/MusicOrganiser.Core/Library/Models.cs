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
                                 int Unlocks = 0, DateTime? LastUsed = null, PluginKind Kind = PluginKind.Unknown)
{
    /// <summary>For a DAW's own device, that DAW (each DAW's is its own row); "" for a plugin.</summary>
    public string Own => Format == "Native" ? Daws : "";

    /// <summary>Its room on disk (plugin files, presets and sounds), once measured; 0 when not installed or not measured yet.</summary>
    public long Bytes { get; init; }
    public string SizeText => Bytes > 0 ? Space.DiskSpace.Text(Bytes) : "";
    /// <summary>The room of folders it shares with other plugins from its maker (a Toontrack or Arturia library), left out of <see cref="Bytes"/>.</summary>
    public long SharedBytes { get; init; }
    public string SharedText => SharedBytes > 0 ? Space.DiskSpace.Text(SharedBytes) : "";
    /// <summary>Installed, but no project has used it in the years the Plugins page asks about (or ever).</summary>
    public bool Unused { get; init; }
    /// <summary>The list's "Installed" pill: installed and not flagged as good to remove.</summary>
    public bool InstalledInUse => Installed && !Unused;
    /// <summary>Installed, and not one project uses it.</summary>
    public bool NeverUsed => Installed && Projects == 0;
    /// <summary>Unused, though older projects use it: removing it leaves those projects missing it.</summary>
    public bool UnusedLately => Unused && Projects > 0;
    /// <summary>"Mar 2022", or "Never" for an installed plugin no project uses.</summary>
    public string LastUsedText => LastUsed is { } t ? t.ToString("MMM yyyy") : Projects == 0 ? "Never" : "";
}

/// <summary>A project that uses a plugin, for the Plugins page's side panel.</summary>
public sealed record PluginProject(long Id, string Name, string Daw, DateTime Saved, string Folder = "", string Path = "");

/// <summary>A song in a list of saves (see <see cref="SongVersions.Songs"/>): its newest listed save and how many are listed.</summary>
public sealed record SongUse(string Song, PluginProject Latest, int Saves)
{
    /// <summary>"21 saves", or nothing for one.</summary>
    public string SavesText => Saves > 1 ? $"{Saves} saves" : "";
}

public sealed record SampleUsage(string Path, int Projects, bool Found);
