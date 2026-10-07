namespace MusicOrganiser.Core.Parsers;

/// <summary>
/// Plugins installed on this PC, found in the standard Windows plugin folders.
/// Used to recognise plugin names inside formats that store names only (Bitwig).
/// </summary>
public static class PluginCatalog
{
    /// <summary>Elektron Overbridge plugins: how the boxes show up inside DAW projects.</summary>
    public static readonly IReadOnlyDictionary<string, string> ElektronOverbridge =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Analog Rytm MKII"] = "Analog Rytm MKII",
            ["Analog Rytm"] = "Analog Rytm MKII",
            ["Digitakt II"] = "Digitakt II",
            ["Digitakt"] = "Digitakt",
            ["Syntakt"] = "Syntakt",
            ["Analog Four MKII"] = "Analog Four MKII",
            ["Analog Heat MKII"] = "Analog Heat MKII",
            ["Digitone"] = "Digitone",
            ["Digitone II"] = "Digitone II",
        };

    public static readonly IReadOnlySet<string> AlwaysKnown =
        new HashSet<string>(ElektronOverbridge.Keys, StringComparer.OrdinalIgnoreCase);

    /// <summary>The Elektron box a plugin name refers to, if any.</summary>
    public static string? ElektronDevice(string pluginName)
    {
        var n = pluginName.Replace("Overbridge", "", StringComparison.OrdinalIgnoreCase).Trim(' ', '-', '_');
        if (ElektronOverbridge.TryGetValue(n, out var device)) return device;
        foreach (var (key, dev) in ElektronOverbridge.OrderByDescending(k => k.Key.Length))
            if (pluginName.Contains(key, StringComparison.OrdinalIgnoreCase)) return dev;
        return null;
    }

    public static PluginKind KindOf(string name) =>
        ElektronDevice(name) is not null ? PluginKind.Instrument : PluginKind.Unknown;

    public static IEnumerable<string> DefaultFolders()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (common.Length > 0)
        {
            yield return Path.Combine(common, "VST3");
            yield return Path.Combine(common, "CLAP");
            yield return Path.Combine(common, "VST2");
        }
        if (pf.Length > 0)
        {
            yield return Path.Combine(pf, "VSTPlugins");
            yield return Path.Combine(pf, "Steinberg", "VSTPlugins");
            yield return Path.Combine(pf, "Common Files", "VST3");
        }
    }

    /// <summary>Plugin names from .vst3 / .clap / .dll files under the given folders.</summary>
    public static HashSet<string> Scan(IEnumerable<string> folders)
    {
        var names = new HashSet<string>(AlwaysKnown, StringComparer.OrdinalIgnoreCase);
        names.UnionWith(ScanFiles(folders).Select(p => p.Name));
        return names;
    }

    /// <summary>Every plugin file under the given folders, with its format.</summary>
    public static List<InstalledPlugin> ScanFiles(IEnumerable<string> folders)
    {
        var found = new Dictionary<string, InstalledPlugin>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders.Where(Directory.Exists))
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            try
            {
                foreach (var e in entries)
                {
                    // .vst3 bundles are folders; skip what's inside them.
                    if (e.Contains(".vst3" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    if (TextScan.IsPluginPath(e))
                    {
                        var name = TextScan.PluginNameFromPath(e);
                        if (name.Length >= 3) found.TryAdd(Path.GetFullPath(e), new(name, TextScan.PluginFormatFromPath(e), e));
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return found.Values.ToList();
    }
}

/// <summary>A plugin file on this PC (or in a friend's list): its name, format and where it lives.</summary>
public sealed record InstalledPlugin(string Name, string Format, string Path = "");
