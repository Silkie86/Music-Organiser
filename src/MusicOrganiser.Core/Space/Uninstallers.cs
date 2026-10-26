using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Space;

/// <summary>A program's own uninstaller, as Windows lists it under Settings › Apps › Installed apps.</summary>
public sealed record Uninstaller(string Name, string Publisher, string Command, long Bytes = 0);

/// <summary>
/// Finds a plugin's uninstaller in the list Windows keeps of installed programs (the same list Settings › Apps shows),
/// so removing a plugin tidies its licence, registry entries and shortcuts the way its maker intended.
/// </summary>
public static class Uninstallers
{
    /// <summary>Every uninstaller Windows knows of, for this user and for everyone (64- and 32-bit). Empty elsewhere.</summary>
    public static IReadOnlyList<Uninstaller> All()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var found = new List<Uninstaller>();
        Read(found, Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        Read(found, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        Read(found, Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        return found.DistinctBy(u => (u.Name, u.Command)).ToList();
    }

    [SupportedOSPlatform("windows")]
    static void Read(List<Uninstaller> into, RegistryKey hive, string path)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(sub);
                if (app?.GetValue("DisplayName") is not string name || name.Length == 0) continue;
                if (app.GetValue("SystemComponent") is int hidden && hidden == 1) continue;
                if (app.GetValue("UninstallString") is not string command || command.Length == 0) continue;
                // Left behind by a program that's gone (I:\Current\unins000.exe on Solomon's PC): there's nothing to run.
                if (!CanRun(command)) continue;
                var kb = app.GetValue("EstimatedSize") is int size ? size : 0;
                into.Add(new(name, app.GetValue("Publisher") as string ?? "", command, kb * 1024L));
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }

    /// <summary>
    /// The uninstallers that look like this plugin's, best first: the program's name holds the plugin's (without its
    /// version or format), and the maker's name counts in its favour. "Serum" finds "Xfer Serum 2"; a two-letter name
    /// like "M1" only matches a program named just that, so it can't pick a stranger. The same version ("Serum 2" for
    /// Serum 2) comes before another, and another version's ("iZotope Ozone 11" for Ozone 9) comes last
    /// (see <see cref="IsOtherVersion"/>).
    /// </summary>
    /// <param name="installed">The installed plugins' names, which tell another version from this one's.</param>
    public static IReadOnlyList<Uninstaller> For(string plugin, string vendor, IReadOnlyList<Uninstaller> all, IReadOnlyCollection<string>? installed = null)
    {
        var family = PluginCatalog.Family(plugin);
        if (family.Length < 2) return [];
        var (maker, full) = (Norm(vendor), Norm(plugin));
        return all.Select(u => (u, Score: Score(u, family, maker, full)))
                  .Where(x => x.Score > 0)
                  .OrderBy(x => IsOtherVersion(plugin, x.u, installed)).ThenByDescending(x => x.Score).ThenBy(x => x.u.Name.Length)
                  .Select(x => x.u).ToList();
    }

    /// <summary>
    /// Whether the program's name gives another version than the plugin's: "iZotope Ozone 11" for Ozone 9. Running it
    /// would remove a plugin you may still use, so it's never picked for you. A program whose name gives no version isn't
    /// another one. Nor is one for a plugin whose own name gives none, as it could be any version (Kontakt 6 installs as
    /// "Kontakt", under "Native Instruments Kontakt 6"), unless that version is installed too: "Xfer Serum 2" is Serum 2's
    /// when Serum 2 is beside Serum.
    /// </summary>
    /// <param name="installed">The installed plugins' names.</param>
    public static bool IsOtherVersion(string plugin, Uninstaller u, IReadOnlyCollection<string>? installed = null)
    {
        var family = PluginCatalog.Family(plugin);
        if (family.Length < 2) return false;
        var theirs = MajorAfter(u.Name, family);
        if (theirs < 0) return false;
        var mine = MajorAfter(plugin, family);
        if (mine >= 0) return theirs != mine;
        return installed?.Any(n => PluginCatalog.Family(n) == family && MajorAfter(n, family) == theirs) == true;
    }

    // The whole number written straight after the plugin's name in a name ("iZotope Ozone 11 Advanced": 11, "Serum2": 2,
    // "Diva v1.4.5": 1), or -1 when none follows it.
    static int MajorAfter(string name, string family)
    {
        // Each letter or digit of the name, with where it is, so the family can be found the way names are compared.
        var kept = name.Select((c, at) => (C: char.ToLowerInvariant(c), At: at)).Where(x => char.IsLetterOrDigit(x.C)).ToList();
        var start = new string(kept.Select(x => x.C).ToArray()).IndexOf(family, StringComparison.Ordinal);
        if (start < 0) return -1;
        var at = kept[start + family.Length - 1].At + 1;
        while (at < name.Length && name[at] is ' ' or '-' or '_' or '.') at++;
        if (at + 1 < name.Length && name[at] is 'v' or 'V' && char.IsDigit(name[at + 1])) at++;
        var digits = new string(name[at..].TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var n) ? n : -1;
    }

    static int Score(Uninstaller u, string family, string maker, string full)
    {
        var name = Norm(u.Name);
        // Programs that hold plugins but aren't one: a DAW's own uninstaller must never come up for its devices.
        if (IsDaw(name)) return 0;
        var score = 0;
        if (family.Length < 4) score = name == family ? 3 : 0;
        else if (name == family) score = 3;
        else if (name.Contains(family)) score = 2;
        if (score == 0) return 0;
        if (maker.Length >= 3 && (Norm(u.Publisher).Contains(maker) || name.Contains(maker))) score += 2;
        if (name.Contains(full)) score += 2;   // the same version
        return score;
    }

    static readonly string[] Daws = ["flstudio", "bitwigstudio", "studioone", "fenderstudio", "ableton", "cubase", "reaper", "cakewalk", "protools", "logicpro"];

    static bool IsDaw(string name) => Daws.Any(name.Contains);

    static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// Starts an uninstaller the way Settings › Apps does: its own window, which asks its own questions. Returns the
    /// running program to wait for, or null with why it couldn't start.
    /// </summary>
    public static (Process? Running, string Problem) Start(Uninstaller u)
    {
        var (file, args) = Split(u.Command);
        try
        {
            var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
            return (p, "");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return (null, e.Message);
        }
    }

    /// <summary>Whether the command's program is there: a full path must exist; a bare name (MsiExec.exe) is found by Windows.</summary>
    public static bool CanRun(string command)
    {
        var (file, _) = Split(command);
        return !Path.IsPathRooted(file) || File.Exists(Environment.ExpandEnvironmentVariables(file));
    }

    /// <summary>
    /// An uninstall command split into the program and its arguments: <c>"C:\Program Files\UVI\unins000.exe" /x</c>,
    /// <c>MsiExec.exe /X{GUID}</c>, or a path with spaces and no quotes (some installers write them like that).
    /// </summary>
    public static (string File, string Args) Split(string command)
    {
        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end > 0 ? (c[1..end], c[(end + 1)..].Trim()) : (c.Trim('"'), "");
        }
        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (c[..(exe + 4)], c[(exe + 4)..].Trim());
        var space = c.IndexOf(' ');
        return space > 0 ? (c[..space], c[(space + 1)..].Trim()) : (c, "");
    }
}
