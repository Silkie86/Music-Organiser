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

    /// <summary>Formats where the project's saved settings can't tell us which one was used.</summary>
    public static bool IsKnownFormat(string format) => NormaliseFormat(format) is { Length: > 0 } f && f != "Native";
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
        "Get-ChildItem \"$env:CommonProgramFiles\\VST3\",\"$env:CommonProgramFiles\\CLAP\",\"$env:ProgramFiles\\VstPlugins\"," +
        "\"$env:ProgramFiles\\Steinberg\\VstPlugins\" -Recurse -Include *.vst3,*.clap,*.dll -ErrorAction SilentlyContinue | " +
        "Where-Object { $_.FullName -notmatch '\\.vst3\\\\' } | Select-Object -ExpandProperty FullName | " +
        "Out-File \"$env:USERPROFILE\\Desktop\\my-plugins.txt\"";

    /// <summary>
    /// Reads a plugin list in any of the shapes people send: plugin file paths (from the
    /// commands above, or a folder listing), "Name<tab>Format" lines (what <see cref="ToText"/>
    /// writes), "Name (AU)" or "Name: VST3, AU", or just names.
    /// </summary>
    public static PluginInventory Parse(string owner, string text)
    {
        var plugins = new List<InstalledPlugin>();
        var folderFormat = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().Trim('\uFEFF', '\0').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            // "ls" prints a header per folder: "/Library/Audio/Plug-Ins/Components:".
            if (line.EndsWith(':') && (line.Contains('/') || line.Contains('\\')))
            {
                folderFormat = PluginKey.NormaliseFormat(line.TrimEnd(':').TrimEnd('/', '\\').Split('/', '\\')[^1]);
                continue;
            }
            // Anything inside a plugin bundle (e.g. Contents/x86_64-win/Serum.vst3) isn't another plugin.
            if (InsideBundle().IsMatch(line)) continue;

            if (TextScan.IsPluginPath(line))
            {
                var name = TextScan.PluginNameFromPath(line);
                if (name.Length > 0) plugins.Add(new(name, TextScan.PluginFormatFromPath(line), line));
                continue;
            }
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

    [GeneratedRegex(@"^(?<name>.+?)\s*(?:\(|:|\s-\s)\s*(?<formats>(?:VST3|VST2|VST|AU|CLAP|AAX|Audio ?Unit)(?:\s*[,/]\s*(?:VST3|VST2|VST|AU|CLAP|AAX|Audio ?Unit))*)\s*\)?$",
                    RegexOptions.IgnoreCase)]
    private static partial Regex NameWithFormats();
}
