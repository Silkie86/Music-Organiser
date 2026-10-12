using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sharing;

namespace MusicOrganiser.Core.Library;

/// <summary>
/// A song (all its numbered saves, see <see cref="SongVersions"/>) with saves that use a plugin
/// you're thinking of uninstalling. Uninstalling breaks the older saves too, so any save counts.
/// </summary>
public sealed record ImpactSong(string Song, ProjectSummary Latest, int Saves, IReadOnlyList<long> SaveIds,
                                IReadOnlyList<string> Plugins, IReadOnlyList<string> Formats)
{
    public bool InLatest => SaveIds.Contains(Latest.Id);
    public string FormatsText => string.Join(", ", Formats);
    public string PluginsText => string.Join(", ", Plugins);

    /// <summary>"" for a song saved once, else e.g. "all 3 saves", "12 of 77 saves", "only 2 older saves".</summary>
    public string SavesText =>
        Saves <= 1 ? ""
        : SaveIds.Count >= Saves ? $"all {Saves} saves"
        : InLatest ? $"{SaveIds.Count} of {Saves} saves"
        : SaveIds.Count == 1 ? "only an older save" : $"only {SaveIds.Count} older saves";

    public string DetailText => string.Join(" · ", new[]
    {
        Latest.Status, Latest.Daw, FormatsText, SavesText, Latest.Modified.ToString("d MMM yyyy"),
    }.Where(t => t.Length > 0));
}

/// <summary>One installed file of the plugin, and how many songs saved with its format.</summary>
public sealed record InstalledCopy(InstalledPlugin File, int Songs, int UnknownFormat)
{
    public bool Unused => Songs == 0 && UnknownFormat == 0;

    public string UsageText => (Songs, UnknownFormat) switch
    {
        (0, 0) => "No song uses this one",
        (_, 0) => $"Used by {Songs} song{(Songs == 1 ? "" : "s")}",
        (0, _) => $"Maybe used by {UnknownFormat} song{(UnknownFormat == 1 ? "" : "s")} (format not saved)",
        _ => $"Used by {Songs}, maybe {UnknownFormat} more",
    };
}

/// <summary>What would stop opening properly if a plugin went away.</summary>
public sealed class UninstallImpact
{
    public IReadOnlyList<string> PluginNames { get; init; } = [];
    public IReadOnlyList<ImpactSong> Songs { get; init; } = [];
    public IReadOnlyList<InstalledCopy> Copies { get; init; } = [];
    /// <summary>Songs that use only the Mac (AU) version: they don't load these plugins on this PC anyway.</summary>
    public int MacOnlySongs { get; init; }
    public string Headline { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>An installed plugin no project in the library uses (or uses only in another format).</summary>
public sealed record UnusedPlugin(string Name, IReadOnlyList<InstalledPlugin> Copies, IReadOnlyList<string> UsedFormats)
{
    public string FormatsText => string.Join(", ", Copies.Select(c => c.Format).Where(f => f.Length > 0).Distinct().Order());
    public string UsageText => UsedFormats.Count == 0
        ? "Not used by any project"
        : $"Projects only use the {string.Join(" and ", UsedFormats.Select(f => f == "AU" ? "AU (Mac)" : f))}";
    public string Location => Copies.Count > 0 ? Copies[0].Path : "";
}

/// <summary>"What breaks if I uninstall X?", and which installed plugins nothing uses.</summary>
public static class PluginImpact
{
    // Most painful to lose first.
    static readonly string[] StatusOrder = ["Released", "Finished", "Mixing", "Arranging", "In progress", "Idea", "Archived"];

    static int Rank(string status) => Array.IndexOf(StatusOrder, status) is var i and >= 0 ? i : StatusOrder.Length;

    static string FormatOf(ProjectPlugin p) => PluginKey.ProjectFormat(p.Format);

    /// <summary>
    /// Every name a project's plugin answers to: its own name (with and without vendor) and the
    /// file it was loaded from, which for Waves is the WaveShell that hosts it.
    /// </summary>
    static IEnumerable<string> Keys(ProjectPlugin p)
    {
        foreach (var k in PluginKey.Candidates(p.Name, p.Vendor)) yield return k;
        if (TextScan.IsPluginPath(p.Path) && PluginKey.Of(TextScan.PluginNameFromPath(p.Path)) is { Length: > 0 } fileKey)
            yield return fileKey;
    }

    /// <summary>Songs using any of the plugins, and the installed copies of them.</summary>
    public static UninstallImpact Of(LibraryDb db, IReadOnlyList<(string Name, string Vendor)> plugins)
    {
        if (plugins.Count == 0) return new UninstallImpact();
        var wanted = plugins.SelectMany(p => PluginKey.Candidates(p.Name, p.Vendor)).ToHashSet(StringComparer.Ordinal);

        // Every save belongs to a song; a song is known by its latest save.
        var songs = SongVersions.Group(db.Projects());
        var songOf = new Dictionary<long, SongGroup>();
        foreach (var g in songs)
            foreach (var save in g.Older.Prepend(g.Latest)) songOf[save.Id] = g;
        long SongId(long projectId) => songOf[projectId].Latest.Id;

        var all = db.PluginRows().Where(r => songOf.ContainsKey(r.ProjectId) && FormatOf(r.Plugin) != "Native" &&
                                             Keys(r.Plugin).Any(wanted.Contains)).ToList();
        // Audio Units never load on Windows, so uninstalling the Windows plugin changes nothing for them.
        var rows = all.Where(r => FormatOf(r.Plugin) != "AU").ToList();
        var macOnly = all.Select(r => SongId(r.ProjectId)).Distinct().Count() - rows.Select(r => SongId(r.ProjectId)).Distinct().Count();
        var affected = rows.GroupBy(r => SongId(r.ProjectId))
            .Select(g =>
            {
                var song = songOf[g.Key];
                return new ImpactSong(song.Song, song.Latest, song.Older.Count + 1,
                    g.Select(r => r.ProjectId).Distinct().ToList(),
                    g.Select(r => r.Plugin.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList(),
                    g.Select(r => FormatOf(r.Plugin) is { Length: > 0 } f ? f : "format not saved").Distinct().Order().ToList());
            })
            .OrderBy(s => Rank(s.Latest.Status))
            .ThenByDescending(s => s.Latest.Favourite)
            .ThenByDescending(s => s.Latest.Modified)
            .ToList();

        var copies = db.InstalledPluginFiles()
            .Where(f => wanted.Contains(PluginKey.Of(f.Name)))
            .Select(f =>
            {
                // Only this copy's own plugin counts, even when several plugins are selected.
                var key = PluginKey.Of(f.Name);
                var format = PluginKey.NormaliseFormat(f.Format);
                var mine = rows.Where(r => Keys(r.Plugin).Contains(key)).ToList();
                var same = mine.Where(r => FormatOf(r.Plugin) == format).Select(r => SongId(r.ProjectId)).ToHashSet();
                var unknown = mine.Where(r => FormatOf(r.Plugin).Length == 0).Select(r => SongId(r.ProjectId))
                                  .Where(id => !same.Contains(id)).Distinct().Count();
                return new InstalledCopy(f, same.Count, unknown);
            })
            .OrderBy(c => c.File.Format).ThenBy(c => c.File.Path)
            .ToList();

        var names = plugins.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var label = names.Count == 1 ? names[0] : $"these {names.Count} plugins";
        return new UninstallImpact
        {
            PluginNames = names, Songs = affected, Copies = copies, MacOnlySongs = macOnly,
            Headline = Headline(label, affected),
            Detail = Detail(affected, copies, macOnly),
        };
    }

    static string Headline(string label, IReadOnlyList<ImpactSong> songs)
    {
        if (songs.Count == 0)
            return $"No project in your library uses {label}. Uninstalling won't break anything here.";
        var parts = new List<string>();
        foreach (var status in new[] { "Released", "Finished" })
            if (songs.Count(s => s.Latest.Status == status) is var n and > 0) parts.Add($"{n} {status.ToLowerInvariant()}");
        if (songs.Count(s => s.Latest.Favourite) is var fav and > 0) parts.Add($"{fav} favourite{(fav == 1 ? "" : "s")}");
        var including = parts.Count > 0 ? $", including {string.Join(" and ", parts)}" : "";
        var saves = songs.Sum(s => s.SaveIds.Count);
        var savesText = saves > songs.Count ? $" ({saves:N0} saves)" : "";
        return $"Uninstalling {label} would affect {songs.Count:N0} song{(songs.Count == 1 ? "" : "s")}{savesText}{including}.";
    }

    static string Detail(IReadOnlyList<ImpactSong> songs, IReadOnlyList<InstalledCopy> copies, int macOnly)
    {
        var lines = new List<string>();
        if (macOnly > 0)
            lines.Add($"{macOnly}{(songs.Count > 0 ? " more" : "")} song{(macOnly == 1 ? " uses" : "s use")} " +
                      "the Mac (AU) version, which doesn't load on this PC anyway.");
        var byFormat = songs.SelectMany(s => s.Formats.Select(f => (f, s))).GroupBy(x => x.f)
                            .Select(g => $"the {g.Key} in {g.Count()}").ToList();
        if (songs.Count > 0 && byFormat.Count > 1) lines.Add($"Your songs use {string.Join(", ", byFormat)}.");
        var spare = copies.Where(c => c.Unused).ToList();
        if (spare.Count > 0 && spare.Count < copies.Count)
            lines.Add($"No song uses the {string.Join(" or ", spare.Select(c => c.File.Format).Distinct())} copy, " +
                      "so removing just that one breaks nothing here.");
        if (songs.Count > 0)
            lines.Add("Older saves of a song count too. To keep a song's sound without the plugin, " +
                      "render those tracks to audio before uninstalling.");
        lines.Add("Only projects in your library folders are counted.");
        return string.Join(" ", lines);
    }

    /// <summary>
    /// Installed plugins that no project in the library uses, plus plugins installed in
    /// several formats where projects only use some of them (e.g. the VST2 and VST3 of one synth).
    /// </summary>
    public static IReadOnlyList<UnusedPlugin> Unused(LibraryDb db)
    {
        // For every way a project's plugin can be named, the formats it was saved in.
        var used = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (_, p) in db.PluginRows())
        {
            var format = FormatOf(p);
            if (format == "Native") continue;
            foreach (var key in Keys(p))
            {
                if (!used.TryGetValue(key, out var set)) used[key] = set = new();
                set.Add(format);
            }
        }

        var result = new List<UnusedPlugin>();
        foreach (var group in db.InstalledPluginFiles().GroupBy(f => PluginKey.Of(f.Name)).Where(g => g.Key.Length > 0))
        {
            var copies = group.ToList();
            if (!used.TryGetValue(group.Key, out var formats))
            {
                result.Add(new UnusedPlugin(copies[0].Name, copies, []));
                continue;
            }
            if (formats.Contains("")) continue;   // some project didn't say which format: assume all are needed
            var spare = copies.Where(c => !formats.Contains(PluginKey.NormaliseFormat(c.Format))).ToList();
            if (spare.Count > 0)
                result.Add(new UnusedPlugin(copies[0].Name, spare, formats.Order().ToList()));
        }
        return result.OrderBy(u => u.UsedFormats.Count).ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
