using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Space;

/// <summary>What a plugin's room on disk is: the plugin itself, its presets and settings, or its sounds.</summary>
public enum SpaceKind { Program, Presets, Sounds }

/// <summary>
/// One of a plugin's files or folders and how much it takes. <paramref name="SharedWith"/> names the other plugins
/// (by name key) whose folder it also is (a maker's folder, or Kontakt 7 and Kontakt 8's), so it's never counted twice.
/// <paramref name="Added"/> is a folder you added yourself.
/// </summary>
public sealed record SpaceFolder(string Path, SpaceKind Kind, long Bytes, IReadOnlyList<string> SharedWith, bool Added = false)
{
    public bool Shared => SharedWith.Count > 0;
}

/// <summary>A plugin's room on disk: its files and folders, found by <see cref="PluginSpace.Measure"/>.</summary>
public sealed record PluginSpaceInfo(string Key, string Name, string Vendor, IReadOnlyList<SpaceFolder> Folders, DateTime Measured)
{
    long Sum(SpaceKind kind) => Folders.Where(f => !f.Shared && f.Kind == kind).Sum(f => f.Bytes);
    public long Program => Sum(SpaceKind.Program);
    public long Presets => Sum(SpaceKind.Presets);
    public long Sounds => Sum(SpaceKind.Sounds);
    /// <summary>Its own room: the folders it shares with other plugins left out.</summary>
    public long Bytes => Program + Presets + Sounds;
    public long SharedBytes => Folders.Where(f => f.Shared).Sum(f => f.Bytes);
}

/// <summary>
/// Works out how much room each installed plugin takes. Windows doesn't record which folders belong to which plugin,
/// so besides the plugin files themselves this looks for folders named after the plugin or its maker in the usual
/// places (Documents, AppData, ProgramData, Program Files): "Serum Presets", "UVI\UVI Workstation", "Toontrack".
/// Folders you add or rule out for a plugin win over the guess.
/// </summary>
public static class PluginSpace
{
    /// <summary>An installed plugin: its name key (as the Plugins page groups it), name, maker and plugin files.</summary>
    public sealed record Installed(string Key, string Name, string Vendor, IReadOnlyList<string> Files);

    /// <summary>Where plugins keep presets, settings and sound libraries on this operating system.</summary>
    public static IReadOnlyList<string> DataRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IEnumerable<string> roots;
        if (OperatingSystem.IsWindows())
        {
            roots = new[]
            {
                Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.CommonDocuments, Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            }.Select(Environment.GetFolderPath);
        }
        else if (OperatingSystem.IsMacOS())
            roots = [Path.Combine(home, "Library", "Application Support"), "/Library/Application Support", Path.Combine(home, "Documents"),
                     Path.Combine(home, "Music"), Path.Combine(home, "Library", "Audio", "Presets"), "/Library/Audio/Presets"];
        else
            roots = [Path.Combine(home, ".local", "share"), Path.Combine(home, ".config"), Path.Combine(home, "Documents")];
        return roots.Where(r => r.Length > 0 && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Whether a folder is where programs are installed: those go with Uninstall, never Clean up.</summary>
    public static bool IsInstallFolder(string path) =>
        OperatingSystem.IsWindows() &&
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Any(pf => pf.Length > 0 && DiskSpace.IsInside(path, pf));

    /// <summary>
    /// Folders that hold a DAW, the system or this app rather than a plugin's things. Never matched, even when a plugin
    /// shares the name (PreSonus makes plugins and Studio One).
    /// </summary>
    static readonly string[] NeverNames =
    [
        "imageline", "flstudio", "bitwigstudio", "bitwig", "studioone", "presonus", "fenderstudio", "ableton", "cakewalk", "reaper",
        "steinberg", "cubase", "microsoft", "windows", "windowsapps", "packages", "temp", "programs", "commonfiles", "musicorganiser",
        "google", "mozilla", "apple", "nvidia", "intel", "amd", "realtek", "adobe", "claude", "anthropic", "vst", "vst2", "vst3",
        "vstplugins", "clap", "plugins", "audio", "music", "samples", "projects", "presets", "data", "content", "library",
    ];

    /// <summary>Endings a folder named after a plugin may have: "Serum Presets", "Omnisphere Library", "Kontakt 8".</summary>
    static readonly string[] Endings =
    [
        "", "presets", "preset", "userpresets", "data", "library", "libraries", "content", "factory", "factorycontent", "soundbanks",
        "soundbank", "sounds", "samples", "expansions", "banks", "settings", "x64", "vst", "vst3",
    ];

    internal static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// Whether a folder's name is a plugin's: the plugin's name (no version), perhaps with a version number and one of
    /// <see cref="Endings"/>. Names under four letters must match exactly ("M1" never takes "M1 Backups").
    /// </summary>
    public static bool NameMatches(string folderName, string family)
    {
        var name = Norm(folderName);
        if (family.Length < 2 || name.Length == 0) return false;
        if (family.Length < 4) return name == family;
        if (!name.StartsWith(family, StringComparison.Ordinal)) return false;
        var rest = name[family.Length..].TrimStart("0123456789".ToCharArray());
        return Endings.Contains(rest);
    }

    /// <summary>Whether a folder is the maker's: its name is the maker's name ("UVI", "Toontrack", "Xfer Records" or "Xfer").</summary>
    static bool MakerMatches(string folderName, string maker)
    {
        var name = Norm(folderName);
        if (maker.Length < 3 || name.Length < 3) return false;
        if (name == maker) return true;
        // "Xfer" for Xfer Records, "Arturia" for "Arturia SA": the folder is the start of the maker's name.
        return name.Length >= 4 && maker.StartsWith(name, StringComparison.Ordinal) && !char.IsDigit(maker[name.Length]);
    }

    /// <summary>
    /// Measures every installed plugin. <paramref name="pluginRoots"/> are the plugin folders (never data, so the plugin
    /// files aren't counted twice); <paramref name="protectedRoots"/> are your project and sample folders, never counted
    /// as a plugin's. <paramref name="added"/> and <paramref name="ruledOut"/> give each plugin's folders you added or said
    /// aren't its. Every plugin's folders are guessed, so shared ones are known, but only <paramref name="only"/>'s are
    /// measured when it's given.
    /// </summary>
    public static IReadOnlyList<PluginSpaceInfo> Measure(IReadOnlyList<Installed> plugins, IReadOnlyList<string> dataRoots,
        IReadOnlyList<string> pluginRoots, IReadOnlyList<string> protectedRoots,
        Func<string, IReadOnlyList<string>> added, Func<string, IReadOnlyList<string>> ruledOut,
        IReadOnlyCollection<string>? only = null, CancellationToken ct = default)
    {
        var guesses = Guess(plugins, dataRoots, pluginRoots, protectedRoots);
        var sizes = new Dictionary<string, DiskSpace.Measured>(StringComparer.OrdinalIgnoreCase);
        DiskSpace.Measured Size(string path)
        {
            if (sizes.TryGetValue(path, out var m)) return m;
            ct.ThrowIfCancellationRequested();
            return sizes[path] = DiskSpace.Measure(path, ct);
        }

        // Folders you added are the plugin's alone; the ones you ruled out are left out of every guess for it.
        var claimed = plugins.ToDictionary(p => p.Key, p =>
        {
            var no = ruledOut(p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (Guessed: guesses.TryGetValue(p.Key, out var g) ? g.Where(f => !no.Contains(f.Path)).ToList() : [],
                    Added: added(p.Key).Where(Directory.Exists).ToList());
        });
        // Who else claims each guessed folder, for the shared ones.
        var owners = claimed.SelectMany(c => c.Value.Guessed.Select(g => (g.Path, c.Key)))
                            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.Select(x => x.Key).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);
        var now = DateTime.Now;
        var result = new List<PluginSpaceInfo>();
        foreach (var p in plugins.Where(p => only is null || only.Contains(p.Key)))
        {
            var folders = new List<SpaceFolder>();
            foreach (var f in p.Files.Distinct(StringComparer.OrdinalIgnoreCase))
                folders.Add(new(f, SpaceKind.Program, Size(f).Bytes, []));
            var (guessed, mine) = claimed[p.Key];
            foreach (var g in guessed)
            {
                var size = g.Within is { } within ? Remainder(g.Path, within, Size) : Size(g.Path);
                var others = owners[g.Path].Where(k => k != p.Key).ToList();
                folders.Add(new(g.Path, KindOf(size), size.Bytes, others));
            }
            foreach (var a in mine.Where(a => !folders.Any(f => f.Path.Equals(a, StringComparison.OrdinalIgnoreCase))))
            {
                var size = Size(a);
                folders.Add(new(a, KindOf(size), size.Bytes, [], Added: true));
            }
            result.Add(new(p.Key, p.Name, p.Vendor, folders.Where(f => f.Bytes > 0 || f.Added).ToList(), now));
        }
        return result;
    }

    // A maker's folder, less the plugin folders inside it that were counted on their own.
    static DiskSpace.Measured Remainder(string folder, IReadOnlyList<string> within, Func<string, DiskSpace.Measured> size)
    {
        var all = size(folder);
        foreach (var w in within)
        {
            var part = size(w);
            all = new(all.Bytes - part.Bytes, all.SoundBytes - part.SoundBytes, all.Files - part.Files);
        }
        return all;
    }

    static SpaceKind KindOf(DiskSpace.Measured m) => m.SoundBytes * 2 >= m.Bytes && m.Bytes > 0 ? SpaceKind.Sounds : SpaceKind.Presets;

    /// <summary>A guessed folder; <paramref name="Within"/>: for a maker's folder, the plugin folders inside it counted on their own.</summary>
    internal sealed record Guessed(string Path, IReadOnlyList<string>? Within = null);

    /// <summary>
    /// Each plugin's guessed data folders. A folder in a data root named after a plugin is that plugin's. A folder named
    /// after a maker is looked into: its folders named after one of the maker's plugins are that plugin's, and the rest
    /// of it belongs to all the maker's plugins together.
    /// </summary>
    internal static Dictionary<string, List<Guessed>> Guess(IReadOnlyList<Installed> plugins, IReadOnlyList<string> dataRoots,
        IReadOnlyList<string> pluginRoots, IReadOnlyList<string> protectedRoots)
    {
        var result = new Dictionary<string, List<Guessed>>();
        void Add(string key, Guessed g) => (result.TryGetValue(key, out var l) ? l : result[key] = []).Add(g);
        var families = plugins.Select(p => (p.Key, Family: PluginCatalog.Family(p.Name))).Where(x => x.Family.Length >= 2).ToList();
        var makers = plugins.Where(p => p.Vendor.Length > 0).GroupBy(p => Norm(p.Vendor)).Where(g => g.Key.Length >= 3)
                            .ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToList());

        bool Off(string dir) =>
            NeverNames.Contains(Norm(Path.GetFileName(dir))) ||
            pluginRoots.Any(r => DiskSpace.IsInside(r, dir) || DiskSpace.IsInside(dir, r)) ||
            protectedRoots.Any(r => DiskSpace.IsInside(r, dir) || DiskSpace.IsInside(dir, r));

        // The longest name wins: "Serum 2 Presets" is Serum 2's, not Serum's.
        List<string> PluginsNamed(string dir, IEnumerable<(string Key, string Family)> among)
        {
            var name = Path.GetFileName(dir);
            var hits = among.Where(f => NameMatches(name, f.Family)).ToList();
            if (hits.Count == 0) return [];
            var longest = hits.Max(h => h.Family.Length);
            return hits.Where(h => h.Family.Length == longest).Select(h => h.Key).ToList();
        }

        foreach (var root in dataRoots)
        {
            foreach (var dir in SubFolders(root))
            {
                if (Off(dir)) continue;
                var named = PluginsNamed(dir, families);
                if (named.Count > 0)
                {
                    foreach (var k in named) Add(k, new(dir));
                    continue;
                }
                var maker = makers.FirstOrDefault(m => MakerMatches(Path.GetFileName(dir), m.Key));
                if (maker.Value is null) continue;
                var theirs = families.Where(f => maker.Value.Contains(f.Key)).ToList();
                var inside = new List<string>();
                foreach (var sub in SubFolders(dir))
                {
                    if (Off(sub)) continue;
                    var hits = PluginsNamed(sub, theirs);
                    foreach (var k in hits) Add(k, new(sub));
                    if (hits.Count > 0) inside.Add(sub);
                }
                foreach (var k in maker.Value) Add(k, new(dir, inside));
            }
        }
        return result;
    }

    // The folders in a folder, links to other places left out (a folder moved to another drive isn't guessed again).
    // OneDrive's folders carry the same flag as links without being one, so they stay in.
    static IEnumerable<string> SubFolders(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
                                         .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint) || !DiskSpace.IsLink(d))
                                         .Select(d => d.FullName).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// The installed plugins, from the plugin files in your plugin folders, grouped as the Plugins page groups them (a
    /// plugin's VST2, VST3 and CLAP files together). The maker comes from what the plugin scanner read, or else the folder
    /// the plugin sits in ("VST3\UVI\UVIWorkstation.vst3" is UVI's).
    /// </summary>
    public static IReadOnlyList<Installed> Group(IReadOnlyList<InstalledPlugin> files, IReadOnlyList<string> pluginRoots,
                                                 IReadOnlyDictionary<string, string> vendors)
    {
        return files.GroupBy(f => LibraryDb.NameKey(f.Name))
                    .Select(g =>
                    {
                        var first = g.First();
                        var vendor = vendors.TryGetValue(g.Key, out var v) && v.Length > 0 ? v : g.Select(f => FolderMaker(f.Path, pluginRoots)).FirstOrDefault(m => m.Length > 0) ?? "";
                        return new Installed(g.Key, first.Name, vendor, g.Select(f => f.Path).ToList());
                    }).ToList();
    }

    static string FolderMaker(string path, IReadOnlyList<string> pluginRoots)
    {
        var root = pluginRoots.FirstOrDefault(r => DiskSpace.IsInside(path, r));
        if (root is null) return "";
        var rel = Path.GetRelativePath(root, path).Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return rel.Length > 1 && !rel[0].Contains('.') ? rel[0] : "";
    }
}
