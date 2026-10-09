using System.Text;
using System.Text.RegularExpressions;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Sharing;

/// <summary>
/// Names that can be compared across computers: "Serum_x64.dll" on Windows, "Serum.vst" on a Mac
/// and "Serum" inside a project are the same plugin.
/// </summary>
public static class PluginKey
{
    static readonly string[] Suffixes = ["x64", "x86", "64bit", "32bit", "vst3", "vst"];

    public static string Of(string name)
    {
        var k = LibraryDb.NameKey(name);
        foreach (var s in Suffixes)
            if (k.Length > s.Length + 2 && k.EndsWith(s, StringComparison.Ordinal)) k = k[..^s.Length];
        return k;
    }

    /// <summary>
    /// The keys a plugin may be listed under: its name, its name with the vendor in front
    /// ("FabFilter Pro-Q 3" for "Pro-Q 3"), and its name without the vendor ("Diva" for "u-he Diva").
    /// </summary>
    public static IEnumerable<string> Candidates(string name, string vendor = "")
    {
        var key = Of(name);
        if (key.Length > 0) yield return key;
        var v = LibraryDb.NameKey(vendor);
        if (v.Length == 0 || key.Length == 0) yield break;
        yield return v + key;
        if (key.Length > v.Length + 2 && key.StartsWith(v, StringComparison.Ordinal)) yield return key[v.Length..];
    }

    /// <summary>"VST", "Audio Unit", "component" and friends → VST2, AU, VST3, CLAP, AAX or Native.</summary>
    public static string NormaliseFormat(string format)
    {
        var f = format.Trim().ToUpperInvariant().Replace(" ", "");
        return f switch
        {
            "VST" or "VST2" or "VST2.4" or "DLL" => "VST2",
            "VST3" => "VST3",
            "AU" or "AUDIOUNIT" or "AUDIOUNITS" or "COMPONENT" or "COMPONENTS" => "AU",
            "CLAP" => "CLAP",
            "AAX" => "AAX",
            "NATIVE" => "Native",
            _ => "",
        };
    }

    /// <summary>
    /// A format as a project stored it. Readers write a bare "VST" when they couldn't tell
    /// VST2 from VST3, so unlike in a person's list it means "unknown" here.
    /// </summary>
    public static string ProjectFormat(string format) =>
        format.Trim().Equals("VST", StringComparison.OrdinalIgnoreCase) ? "" : NormaliseFormat(format);
}

/// <summary>How a list of plugins answers "do you have this one?".</summary>
public enum Availability
{
    /// <summary>The same plugin in the same format.</summary>
    Same,
    /// <summary>The plugin, but only in other formats (e.g. VST3 when the project used the AU).</summary>
    OtherFormat,
    /// <summary>The plugin's name is on the list but its format isn't.</summary>
    NameOnly,
    None,
}

/// <summary>Plugins installed on one computer: this PC from the plugin scan, or a friend's list.</summary>
public sealed partial class PluginInventory
{
    readonly Dictionary<string, List<InstalledPlugin>> _byKey = new(StringComparer.Ordinal);

    public string Owner { get; }
    public IReadOnlyList<InstalledPlugin> Plugins { get; }
    public bool IsEmpty => Plugins.Count == 0;

    public PluginInventory(string owner, IEnumerable<InstalledPlugin> plugins)
    {
        Owner = owner;
        Plugins = plugins.Where(p => p.Name.Length > 0)
                         .DistinctBy(p => (PluginKey.Of(p.Name), p.Format, p.Path.ToLowerInvariant()))
                         .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var p in Plugins)
        {
            var key = PluginKey.Of(p.Name);
            if (key.Length == 0) continue;
            if (!_byKey.TryGetValue(key, out var list)) _byKey[key] = list = new();
            list.Add(p);
        }
    }

    /// <summary>This PC's plugins, from the last plugin scan.</summary>
    public static PluginInventory ThisPc(LibraryDb db) => new("This PC", db.InstalledPluginFiles());

    /// <summary>Every listed copy of a plugin, in any format.</summary>
    public IReadOnlyList<InstalledPlugin> Find(string name, string vendor = "")
    {
        var found = new List<InstalledPlugin>();
        foreach (var key in PluginKey.Candidates(name, vendor).Distinct())
            if (_byKey.TryGetValue(key, out var list))
                found.AddRange(list.Where(p => !found.Contains(p)));
        return found;
    }

    public Availability Check(string name, string vendor, string format, out IReadOnlyList<string> otherFormats)
    {
        var copies = Find(name, vendor);
        var wanted = PluginKey.NormaliseFormat(format);
        otherFormats = copies.Select(c => PluginKey.NormaliseFormat(c.Format)).Where(f => f.Length > 0 && f != wanted)
                             .Distinct().Order().ToList();
        if (copies.Count == 0) return Availability.None;
        if (wanted.Length == 0 || copies.Any(c => PluginKey.NormaliseFormat(c.Format) == wanted)) return Availability.Same;
        if (copies.Any(c => PluginKey.NormaliseFormat(c.Format).Length == 0)) return Availability.NameOnly;
        return Availability.OtherFormat;
    }

    // ---- reading and writing lists -----------------------------------------------------

    /// <summary>
    /// The Terminal command a friend on a Mac runs to list their plugins. It writes
    /// "my-plugins.txt" to their Desktop with one plugin bundle per line, e.g.
    /// /Library/Audio/Plug-Ins/Components/Diva.component.
    /// </summary>
    public const string MacListCommand =
        "find /Library/Audio/Plug-Ins ~/Library/Audio/Plug-Ins -maxdepth 3 \\( -name \"*.component\" -o -name \"*.vst3\" " +
        "-o -name \"*.vst\" -o -name \"*.clap\" \\) -prune > ~/Desktop/my-plugins.txt";

    /// <summary>The same for a friend on Windows, in PowerShell.</summary>
    public const string WindowsListCommand =
        "Get-ChildItem \"$env:CommonProgramFiles\\VST3\",\"$env:CommonProgramFiles\\CLAP\",\"$env:CommonProgramFiles\\VST2\"," +
        "\"$env:ProgramFiles\\VstPlugins\",\"$env:ProgramFiles\\Steinberg\\VstPlugins\" -Recurse -Include *.vst3,*.clap,*.dll " +
        "-ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\.vst3\\\\' } | " +
        "Select-Object -ExpandProperty FullName | Out-File (Join-Path ([Environment]::GetFolderPath('Desktop')) 'my-plugins.txt')";

    /// <summary>
    /// Reads a plugin list in any of the shapes people send: plugin file paths (from the
    /// commands above, or a folder listing), "Name<tab>Format" lines (what <see cref="ToText"/>
    /// writes), "Name (AU)" or "Name: VST3, AU", or just names.
    /// </summary>
    public static PluginInventory Parse(string owner, string text)
    {
        var plugins = new List<InstalledPlugin>();
        var folderFormat = "";
        var inBundle = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().Trim('\uFEFF', '\0').Trim();
            if (line.Length == 0 || line.StartsWith('#') || Boilerplate().IsMatch(line)) continue;

            // Folder headers set the format for bare names below them:
            // "ls" prints "/Library/Audio/Plug-Ins/Components:", "dir" prints "Directory of C:\...\VST3".
            // Recursive listings also show folders inside plugin bundles, which hold no other plugins.
            string? folder = line.StartsWith("Directory of ", StringComparison.OrdinalIgnoreCase) ? line["Directory of ".Length..]
                : line.EndsWith(':') && (line.Contains('/') || line.Contains('\\')) ? line.TrimEnd(':')
                : null;
            if (folder is not null)
            {
                folderFormat = FolderFormat(folder);
                inBundle = InsideBundle().IsMatch(folder.TrimEnd('/', '\\') + "/");
                continue;
            }
            if (inBundle) continue;

            // "ls -l" and "dir" rows: just the name at the end. Those rows also list sub-folders
            // and other files, so only plugin files count.
            var listed = false;
            if (LongListing().Match(line) is { Success: true } row)
            {
                line = row.Groups["name"].Value.Trim();
                listed = true;
            }

            // Plain "ls" prints several names per line, in columns ("ls -F" adds a slash to bundles).
            var cells = Columns().Split(line).Select(c => c.Trim().TrimEnd('/', '\\')).Where(c => c.Length > 0).ToList();
            if (cells.Count > 1 && cells.All(TextScan.IsPluginPath))
            {
                foreach (var cell in cells.Where(c => !InsideBundle().IsMatch(c)))
                    plugins.Add(new(TextScan.PluginNameFromPath(cell), TextScan.PluginFormatFromPath(cell), cell));
                continue;
            }
            line = line.TrimEnd('/', '\\');
            if (line.Length == 0 || line is "." or "..") continue;
            // Anything inside a plugin bundle (e.g. Contents/x86_64-win/Serum.vst3) isn't another plugin.
            if (InsideBundle().IsMatch(line)) continue;

            if (TextScan.IsPluginPath(line))
            {
                var name = TextScan.PluginNameFromPath(line);
                if (name.Length > 0) plugins.Add(new(name, TextScan.PluginFormatFromPath(line), line));
                continue;
            }
            if (listed) continue;
            if (line.Contains('\t'))
            {
                var parts = line.Split('\t');
                plugins.Add(new(parts[0].Trim(), PluginKey.NormaliseFormat(parts[1]), parts.Length > 2 ? parts[2].Trim() : ""));
                continue;
            }
            if (NameWithFormats().Match(line) is { Success: true } m)
            {
                foreach (var f in m.Groups["formats"].Value.Split(',', '/'))
                    plugins.Add(new(m.Groups["name"].Value.Trim(), PluginKey.NormaliseFormat(f), ""));
                continue;
            }
            plugins.Add(new(line, folderFormat, ""));
        }
        return new PluginInventory(owner, plugins);
    }

    static string FolderFormat(string folder) =>
        PluginKey.NormaliseFormat(folder.Trim().TrimEnd('/', '\\').Split('/', '\\')[^1]);

    /// <summary>One plugin per line as "Name<tab>Format", which <see cref="Parse"/> reads back.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Plugins on {Owner}, listed by Music Organiser. One per line: name, then format.");
        foreach (var p in Plugins.DistinctBy(p => (p.Name.ToLowerInvariant(), p.Format)))
            sb.Append(p.Name).Append('\t').AppendLine(p.Format);
        return sb.ToString();
    }

    [GeneratedRegex(@"\.(vst3|vst|component|clap)[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex InsideBundle();

    [GeneratedRegex(@"^(total \d+$|total files listed|volume in drive|volume serial number|\d+ file\(s\)|\d+ dir\(s\))", RegexOptions.IgnoreCase)]
    private static partial Regex Boilerplate();

    // "drwxr-xr-x@ 3 root wheel 96 12 Jan 2024 Diva.component" (ls -l), or
    // "07/10/2026  14:02    <DIR>          Diva.vst3" (dir).
    [GeneratedRegex(@"^(?:[dl-][rwxsStT@+.-]{9,11}\s+\d+\s+\S+\s+\S+\s+\d+\s+(?:\w{3}\s+\d{1,2}|\d{1,2}\s+\w{3})\s+(?:\d{1,2}:\d{2}|\d{4})" +
                    @"|\d{1,4}[/.-]\d{1,2}[/.-]\d{1,4}\s+\d{1,2}:\d{2}(?:\s*[AP]M)?\s+(?:<DIR>|<JUNCTION>|[\d,.]+))\s+(?<name>.+)$",
                    RegexOptions.IgnoreCase)]
    private static partial Regex LongListing();

    [GeneratedRegex(@"\t+| {2,}")]
    private static partial Regex Columns();

    [GeneratedRegex(@"^(?<name>.+?)\s*(?:\(|:|\s-\s)\s*(?<formats>(?:VST3|VST2|VST|AU|CLAP|AAX|Audio ?Unit)(?:\s*[,/]\s*(?:VST3|VST2|VST|AU|CLAP|AAX|Audio ?Unit))*)\s*\)?$",
                    RegexOptions.IgnoreCase)]
    private static partial Regex NameWithFormats();
}
