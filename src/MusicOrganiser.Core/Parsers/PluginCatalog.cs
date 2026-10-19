namespace MusicOrganiser.Core.Parsers;

/// <summary>
/// Plugins installed on this PC, found in the standard Windows plugin folders.
/// Used to recognise plugin names inside formats that store names only (Bitwig).
/// </summary>
public static partial class PluginCatalog
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

    /// <summary>
    /// Plugins that come with FL Studio. Old projects often load FL's own VST builds of them
    /// (C:\Program Files\FL Studio 10\Plugins\VST\Fruity Balance.dll), which aren't in the
    /// usual plugin folders but are always there in any FL install.
    /// </summary>
    static readonly HashSet<string> ImageLine = new(StringComparer.OrdinalIgnoreCase)
    {
        "3x Osc", "Autogun", "BassDrum", "BeepMap", "BooBass", "DirectWave", "Distructor", "Drumaxx", "Drumpad",
        "Edison", "Effector", "Emphasis", "Equo", "FL Keys", "FLEX", "FPC", "Frequency Shifter", "Frequency Splitter",
        "Gross Beat", "Groove Machine", "Hardcore", "Harmless", "Harmor", "Kepler", "Kepler Exo", "Love Philter",
        "Luxeverb", "Maximus", "MiniSynth", "Morphine", "Multiband Delay", "Newtime", "Newtone", "Ogun",
        "Parametric EQ", "Parametric EQ 2", "Patcher", "Pitch Resonator", "Pitch Shifter", "Pitcher", "Plucked!",
        "PoiZone", "Sakura", "Sawer", "SimSynth", "Slicex", "Soundgoodizer", "Sytrus", "Toxic Biohazard",
        "Transient Processor", "Transistor Bass", "Vintage Bass", "Vintage Chorus", "Vintage Phaser", "Vocodex",
        "Wasp", "Wasp XT", "Wave Candy", "Wave Traveller",
    };

    /// <summary>Whether a plugin ships with FL Studio, so it is never "missing" on a PC with FL installed.</summary>
    public static bool IsFlStudioBundled(string name, string vendor, string path) =>
        name.StartsWith("Fruity ", StringComparison.OrdinalIgnoreCase) ||
        ImageLine.Contains(name.Trim()) ||
        name.StartsWith("IL ", StringComparison.OrdinalIgnoreCase) && ImageLine.Contains(name[3..].Trim()) ||
        vendor.StartsWith("Image-Line", StringComparison.OrdinalIgnoreCase) ||
        path.Replace('/', '\\').Contains(@"\Image-Line\", StringComparison.OrdinalIgnoreCase) ||
        FlStudioPluginFolder().IsMatch(path.Replace('/', '\\'));

    [System.Text.RegularExpressions.GeneratedRegex(@"\\FL Studio[^\\]*\\Plugins\\", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FlStudioPluginFolder();

    /// <summary>
    /// Waves plugins all live inside one WaveShell file whose version changes with every update,
    /// so a project's "Waves L2" or "WaveShell-VST 9.3" counts as installed when any WaveShell is.
    /// </summary>
    public static bool IsWaves(string name, string path) =>
        name.StartsWith("Waves ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("WaveShell", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("WaveShell", StringComparison.OrdinalIgnoreCase);

    [System.Text.RegularExpressions.GeneratedRegex(@"[\s_-]*(v?\d+(\.\d+)*|mk\s*[ivx\d]+|x64|x86|vst3?|\(.*\))$|\s+v$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex VersionTail();

    /// <summary>A plugin's name without its version: "Ozone 8" and "Ozone 12" are both "ozone".</summary>
    public static string Family(string name)
    {
        var n = name.Trim();
        for (var prev = ""; n != prev && n.Length > 0;)
        {
            prev = n;
            n = VersionTail().Replace(n, "").Trim();
        }
        return new string(n.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>
    /// For a plugin that isn't installed, another version of it that is ("Ozone 12" for "Ozone 8").
    /// It won't load in place of the old one by itself, but it's what to swap in.
    /// </summary>
    public static string? OtherVersionInstalled(string name, IEnumerable<string> installed)
    {
        var family = Family(name);
        if (family.Length < 3) return null;
        return installed.Where(i => !i.Equals(name, StringComparison.OrdinalIgnoreCase) && Family(i) == family)
                        .OrderByDescending(i => i, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
    }

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

    /// <summary>The standard plugin folders for this operating system.</summary>
    public static IEnumerable<string> DefaultFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            foreach (var root in new[] { "/Library/Audio/Plug-Ins", Path.Combine(home, "Library", "Audio", "Plug-Ins") })
                foreach (var kind in new[] { "VST3", "CLAP", "VST", "Components" })
                    yield return Path.Combine(root, kind);
            yield break;
        }
        if (OperatingSystem.IsLinux())
        {
            yield return Path.Combine(home, ".vst3");
            yield return Path.Combine(home, ".clap");
            yield return Path.Combine(home, ".vst");
            foreach (var root in new[] { "/usr/lib", "/usr/local/lib" })
            {
                yield return Path.Combine(root, "vst3");
                yield return Path.Combine(root, "clap");
                yield return Path.Combine(root, "vst");
            }
            yield break;
        }

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
            // One locked subfolder (e.g. a vendor's licence folder) mustn't hide every other plugin in this folder.
            try { entries = Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            try
            {
                foreach (var e in entries)
                {
                    // .vst3 bundles (and on a Mac .component, .clap and .vst too) are folders; skip what's inside them.
                    if (InsideBundle(e)) continue;
                    if (TextScan.IsPluginPath(e))
                    {
                        // Two letters is a real name: KORG's M1 is M1.vst3 and M1.dll.
                        var name = TextScan.PluginNameFromPath(e);
                        if (name.Length >= 2) found.TryAdd(Path.GetFullPath(e), new(name, TextScan.PluginFormatFromPath(e), e));
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return found.Values.ToList();
    }

    static readonly string[] BundleExtensions = [".vst3", ".component", ".clap", ".vst"];

    static bool InsideBundle(string path) =>
        BundleExtensions.Any(b => path.Contains(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A plugin file on this PC (or in a friend's list): its name, format and where it lives.</summary>
public sealed record InstalledPlugin(string Name, string Format, string Path = "");
