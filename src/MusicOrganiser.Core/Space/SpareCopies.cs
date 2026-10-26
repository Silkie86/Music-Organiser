using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Space;

/// <summary>A plugin file in a format none of your projects use, while the format they do use is installed too.</summary>
public sealed record SpareCopy(string Name, string Format, string Path, long Bytes, string Why);

/// <summary>
/// Spare copies (Solomon's F12 note, 25 Oct 2026: "if i have lots of aax plugins but i never use them or have a vst 2
/// version i never use show that"). Plugin installers often put the same plugin in every format: VST2, VST3, CLAP and
/// AAX. A copy is spare when no project uses that format and a format they do use stays installed; AAX copies are
/// spare when Pro Tools, the only DAW that loads them, isn't on the computer. When a project's format isn't known
/// ("VST" from an old FL Studio save), every copy of that plugin counts as used.
/// </summary>
public static class SpareCopies
{
    static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase) { "VST2", "VST3", "CLAP", "AU" };

    /// <param name="files">Every plugin file found, AAX ones included (<see cref="AaxFolders"/>).</param>
    /// <param name="used">Each plugin name your projects use, with how many projects use each format.</param>
    /// <param name="proTools">Whether Pro Tools is installed (then AAX copies are in use).</param>
    /// <param name="size">How big a file or bundle is.</param>
    public static IReadOnlyList<SpareCopy> Find(IReadOnlyList<InstalledPlugin> files, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> used,
                                                bool proTools, Func<string, long> size)
    {
        // Each used name by its family ("FabFilter Pro-Q 3" and "Pro-Q 3" are both families of the files' names).
        var usedFamilies = used.Select(u => (Family: PluginCatalog.Family(u.Key), Formats: u.Value)).Where(u => u.Family.Length >= 2).ToList();
        var spare = new List<SpareCopy>();
        foreach (var copies in files.Where(f => !PluginCatalog.IsWaves(f.Name, f.Path)).GroupBy(f => PluginCatalog.Family(f.Name)))
        {
            if (copies.Key.Length < 2) continue;
            var formats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in usedFamilies.Where(u => Close(u.Family, copies.Key)))
                foreach (var (format, n) in u.Formats)
                    formats[format] = formats.GetValueOrDefault(format) + n;
            var unknown = formats.Keys.Any(f => !Known.Contains(f));
            foreach (var copy in copies)
            {
                if (copy.Format.Equals("AAX", StringComparison.OrdinalIgnoreCase))
                {
                    if (!proTools) spare.Add(new(copy.Name, "AAX", copy.Path, size(copy.Path), "Only Pro Tools loads AAX, and it isn't on this computer."));
                    continue;
                }
                // Never used at all: the Never used chip has it, whole. A format no one knows: keep everything.
                if (formats.Count == 0 || unknown || formats.ContainsKey(copy.Format)) continue;
                var kept = copies.Where(c => formats.ContainsKey(c.Format)).Select(c => c.Format).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (kept.Count == 0) continue;   // what the projects use isn't installed: this copy may be the next best thing
                var n = kept.Sum(f => formats[f]);
                spare.Add(new(copy.Name, copy.Format, copy.Path, size(copy.Path),
                              $"Your projects use its {string.Join(" and ", kept)} ({(n == 1 ? "1 project" : $"{n} projects")}); none use this {copy.Format}."));
            }
        }
        return spare.OrderByDescending(s => s.Bytes).ToList();
    }

    static bool Close(string a, string b) =>
        a == b || a.Length >= 4 && b.Length >= 4 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));

    /// <summary>Where AAX plugins live (they aren't among the folders the app scans for your DAWs).</summary>
    public static IEnumerable<string> AaxFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            if (common.Length > 0) yield return System.IO.Path.Combine(common, "Avid", "Audio", "Plug-Ins");
        }
        else if (OperatingSystem.IsMacOS()) yield return "/Library/Application Support/Avid/Audio/Plug-Ins";
    }

    /// <summary>Whether Pro Tools is installed, so AAX plugins are in use.</summary>
    public static bool ProToolsInstalled()
    {
        if (OperatingSystem.IsMacOS()) return Directory.Exists("/Applications/Pro Tools.app");
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return pf.Length > 0 && Directory.Exists(System.IO.Path.Combine(pf, "Avid", "Pro Tools"));
    }
}
