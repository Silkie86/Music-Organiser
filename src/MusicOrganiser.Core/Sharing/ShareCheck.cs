using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Sharing;

/// <summary>How a plugin or sample fares on one computer.</summary>
public enum ShareStatus
{
    Ok,
    /// <summary>Can't tell (no plugin list yet, or the list doesn't say which format).</summary>
    Unknown,
    /// <summary>Loads, but worth a look (e.g. Waves shells must match versions).</summary>
    Note,
    /// <summary>The plugin is there, but not in the format the project saved.</summary>
    WrongFormat,
    Missing,
    /// <summary>An Audio Unit on Windows: it can never load there.</summary>
    WontLoad,
}

/// <param name="HereHas">Other formats of this plugin installed on this PC.</param>
/// <param name="FriendHas">Other formats of it on the friend's list.</param>
public sealed record PluginCheck(
    string Name, string Vendor, PluginKind Kind, string Format,
    ShareStatus Here, string HereText, IReadOnlyList<string> HereHas,
    ShareStatus? Friend, string FriendText, IReadOnlyList<string> FriendHas)
{
    public bool IsAu => Format == "AU";
}

public sealed record SampleCheck(string Path, string FileName, bool Found, string Where);

public enum Readiness { Ready, Check, Blocked }

/// <summary>The result of checking a project (or a zip of one) before sharing it.</summary>
public sealed class ShareReport
{
    public required string SourcePath { get; init; }
    public required string ProjectName { get; init; }
    /// <summary>The project file inside the zip, when the source is a zip.</summary>
    public string? FileInZip { get; init; }
    public IReadOnlyList<string> OtherProjectsInZip { get; init; } = [];
    public required string Daw { get; init; }
    public string DawVersion { get; init; } = "";
    public double? Tempo { get; init; }
    /// <summary>"Mac", "Windows", "Mac and Windows", or "" when the file doesn't say.</summary>
    public string SavedOn { get; init; } = "";
    public string? FriendName { get; init; }
    public IReadOnlyList<PluginCheck> Plugins { get; init; } = [];
    public IReadOnlyList<string> NativePlugins { get; init; } = [];
    public IReadOnlyList<SampleCheck> Samples { get; init; } = [];
    public IReadOnlyList<string> Actions { get; init; } = [];
    public Readiness Readiness { get; init; }
    public string Headline { get; init; } = "";
    /// <summary>This PC's plugins haven't been scanned, so "on this PC" is unknown.</summary>
    public bool NotScanned { get; init; }
    /// <summary>Your own project, checked before sending it (rather than one you've been sent).</summary>
    public bool Outgoing { get; init; }

    public IEnumerable<SampleCheck> MissingSamples => Samples.Where(s => !s.Found);
}

/// <summary>
/// Checks whether a project will open on this PC and on a friend's computer: which plugins
/// are Audio Units (Mac only), which aren't installed on either side or only in another
/// format, and which samples are missing. Works on a project file or a zip someone sent.
/// </summary>
public static partial class ShareCheck
{
    const string Friend = "your friend";

    /// <param name="outgoing">True for your own project before you send it, false for one you've been sent.</param>
    public static ShareReport Run(string path, PluginInventory here, PluginInventory? friend = null, bool outgoing = false)
    {
        if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return RunZip(path, here, friend, outgoing);
        var reader = Readers.For(path) ?? throw new InvalidDataException("This isn't a project file Music Organiser can read.");
        var info = reader.Read(path);
        var samples = info.Samples.Select(s => CheckSample(s, path, null)).ToList();
        return Build(path, Path.GetFileNameWithoutExtension(path), null, [], info, samples, here, friend, outgoing);
    }

    static ShareReport RunZip(string zipPath, PluginInventory here, PluginInventory? friend, bool outgoing)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var projects = zip.Entries
            .Where(e => e.Length > 0 && Readers.For(e.Name) is not null && !e.Name.StartsWith("._") &&
                        !e.FullName.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (projects.Count == 0) throw new InvalidDataException("There's no project file in this zip.");

        // A zipped loop package holds one project; if there are several, prefer the one named
        // like the zip, then the newest.
        var stem = PluginKey.Of(Path.GetFileNameWithoutExtension(zipPath));
        var entry = projects.OrderByDescending(e => PluginKey.Of(Path.GetFileNameWithoutExtension(e.Name)) == stem)
                            .ThenByDescending(e => e.LastWriteTime).First();

        var temp = Path.Combine(Path.GetTempPath(), "MusicOrganiser", "share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // A fixed name: Mac file names can hold characters Windows doesn't allow.
            var file = Path.Combine(temp, "project" + Path.GetExtension(entry.Name));
            entry.ExtractToFile(file);
            var info = Readers.For(file)!.Read(file);
            var inZip = zip.Entries.Where(e => e.Name.Length > 0)
                                   .Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var samples = info.Samples.Select(s => CheckSample(s, zipPath, inZip)).ToList();
            return Build(zipPath, Path.GetFileNameWithoutExtension(entry.Name), entry.FullName,
                         projects.Where(p => p != entry).Select(p => p.FullName).ToList(), info, samples, here, friend, outgoing);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static SampleCheck CheckSample(string sample, string projectPath, HashSet<string>? inZip)
    {
        var name = sample.Split('\\', '/')[^1];
        if (inZip is not null && inZip.Contains(name)) return new(sample, name, true, "In the zip");
        // FL's factory content is on every computer with FL. Anything else under a %...% folder
        // (e.g. %FLStudioUserData%) is the sender's own sample, which has to travel with the project.
        if (sample.StartsWith("%FLStudioFactoryData%", StringComparison.OrdinalIgnoreCase))
            return new(sample, name, true, "Comes with FL Studio");
        if (sample.StartsWith('%'))
            return NextToProject(name, projectPath) ? new(sample, name, true, "On this PC") : new(sample, name, false, "Missing");
        if (Scanner.SampleFound(sample, projectPath)) return new(sample, name, true, "On this PC");
        return new(sample, name, false, "Missing");
    }

    static bool NextToProject(string fileName, string projectPath)
    {
        var dir = Path.GetDirectoryName(projectPath) ?? "";
        try
        {
            return new[] { "", "samples", "recordings", "Media", "Audio" }.Any(sub => File.Exists(Path.Combine(dir, sub, fileName)));
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    static ShareReport Build(string source, string projectName, string? fileInZip, IReadOnlyList<string> others,
                             ProjectInfo info, List<SampleCheck> samples, PluginInventory here, PluginInventory? friend,
                             bool outgoing)
    {
        var who = friend?.Owner is { Length: > 0 } o ? o : Friend;
        var native = info.Plugins.Where(IsNative).Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        var checks = info.Plugins.Where(p => !IsNative(p))
            .Select(p => CheckPlugin(p, here, friend, who))
            .OrderByDescending(c => Severity(c.Here, c.Friend))
            .ThenBy(c => c.Kind == PluginKind.Instrument ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var actions = Actions(checks, samples, who, friend is not null, here.IsEmpty, info.Daw);
        var readiness = checks.Any(c => IsBlocking(c.Here) || c.Friend is { } f && IsBlocking(f)) || samples.Any(s => !s.Found)
            ? Readiness.Blocked
            : checks.Any(c => c.Here is ShareStatus.Unknown or ShareStatus.Note || c.Friend is null or ShareStatus.Unknown or ShareStatus.Note)
                ? Readiness.Check
                : Readiness.Ready;

        return new ShareReport
        {
            SourcePath = source, ProjectName = projectName, FileInZip = fileInZip, OtherProjectsInZip = others,
            Daw = info.Daw, DawVersion = VersionWithoutDaw(info), Tempo = info.Tempo, SavedOn = SavedOn(info),
            FriendName = friend?.Owner, Plugins = checks, NativePlugins = native, Samples = samples,
            Actions = actions, Readiness = readiness,
            Headline = Headline(checks, samples, readiness, who, friend is not null, here.IsEmpty),
            NotScanned = here.IsEmpty, Outgoing = outgoing,
        };
    }

    /// <summary>"Studio One/6.5.2" → "6.5.2", so it reads well after the DAW's name.</summary>
    static string VersionWithoutDaw(ProjectInfo info) =>
        info.Version.StartsWith(info.Daw, StringComparison.OrdinalIgnoreCase)
            ? info.Version[info.Daw.Length..].TrimStart('/', ' ', '-')
            : info.Version;

    static bool IsNative(PluginRef p) => p.Format.Equals("Native", StringComparison.OrdinalIgnoreCase);

    static bool IsBlocking(ShareStatus s) => s is ShareStatus.Missing or ShareStatus.WrongFormat or ShareStatus.WontLoad;

    /// <summary>
    /// Loads here but not for the friend: the case where rendering to audio before sending helps.
    /// (A plugin that's broken on both sides is already covered by what this PC needs.)
    /// </summary>
    static bool BlocksFriendOnly(PluginCheck c) => c.Friend is { } f && IsBlocking(f) && !IsBlocking(c.Here);

    static int Severity(ShareStatus here, ShareStatus? friend) => Math.Max((int)here, (int)(friend ?? ShareStatus.Ok));

    static PluginCheck CheckPlugin(PluginRef p, PluginInventory here, PluginInventory? friend, string who)
    {
        var format = PluginKey.ProjectFormat(p.Format);
        var (hs, ht, hh) = OnThisPc(p, format, here);
        var (fs, ft, fh) = friend is null ? (null, "", []) : OnFriends(p, format, friend, who);
        return new PluginCheck(p.Name, p.Vendor, p.Kind, format.Length > 0 ? format : p.Format, hs, ht, hh, fs, ft, fh);
    }

    static (ShareStatus, string, IReadOnlyList<string>) OnThisPc(PluginRef p, string format, PluginInventory here)
    {
        var availability = here.Check(p.Name, p.Vendor, format, out var others);
        if (format == "AU")
        {
            var text = others.Count > 0 ? $"Mac only (Audio Unit). You have the {string.Join(" and ", others)}."
                     : here.IsEmpty ? "Mac only (Audio Unit). Scan your plugins to see if you have another version."
                     : "Mac only (Audio Unit), and you don't have another version.";
            return (ShareStatus.WontLoad, text, others);
        }
        if (IsWavesShell(p))
            return (ShareStatus.Note, "Waves plugins load through their WaveShell: you both need the same Waves version.", others);
        if (p.Path.Length > 0 && !p.Path.StartsWith('/') && (File.Exists(p.Path) || Directory.Exists(p.Path)))
            return (ShareStatus.Ok, "Installed", others);
        if (here.IsEmpty) return (ShareStatus.Unknown, "Not checked: scan your plugins first", others);
        return availability switch
        {
            Availability.Same => (ShareStatus.Ok, "Installed", others),
            Availability.NameOnly => (ShareStatus.Unknown, "Installed, format unknown", others),
            Availability.OtherFormat => (ShareStatus.WrongFormat, $"You have the {string.Join(" and ", others)}, but the project used the {format}", others),
            _ => (ShareStatus.Missing, "Not installed on this PC", others),
        };
    }

    static (ShareStatus?, string, IReadOnlyList<string>) OnFriends(PluginRef p, string format, PluginInventory friend, string who)
    {
        if (friend.IsEmpty) return (ShareStatus.Unknown, "Their list is empty", []);
        var availability = friend.Check(p.Name, p.Vendor, format, out var others);
        if (IsWavesShell(p)) return (ShareStatus.Note, "Check you both have the same Waves version", others);
        return availability switch
        {
            Availability.Same => (ShareStatus.Ok, "They have it", others),
            Availability.NameOnly => (ShareStatus.Unknown, "On their list, format unknown", others),
            Availability.OtherFormat => (ShareStatus.WrongFormat, $"They have the {string.Join(" and ", others)}, not the {format}", others),
            _ => (ShareStatus.Missing, $"Not on {who}'s list", others),
        };
    }

    /// <summary>Waves plugins live inside a version-numbered WaveShell; FL stores either the shell or the plugin with the shell's path.</summary>
    static bool IsWavesShell(PluginRef p) =>
        p.Name.StartsWith("WaveShell", StringComparison.OrdinalIgnoreCase) ||
        TextScan.PluginNameFromPath(p.Path).StartsWith("WaveShell", StringComparison.OrdinalIgnoreCase);

    static string Names(IEnumerable<PluginCheck> plugins) =>
        JoinWithAnd(plugins.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

    static string JoinWithAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    static string Plural(int n, string one, string many) => n == 1 ? one : many;

    static List<string> Actions(List<PluginCheck> checks, List<SampleCheck> samples, string who, bool haveFriend,
                                bool noScan, string daw)
    {
        var actions = new List<string>();
        var au = checks.Where(c => c.Here == ShareStatus.WontLoad).ToList();
        if (au.Count > 0)
        {
            var swappable = au.Where(c => c.HereHas.Count > 0).ToList();
            var line = $"Ask {who} to use the VST3 (or VST2) versions instead of these Audio Units, which can't load on Windows: {Names(au)}.";
            if (swappable.Count > 0) line += $" You already have {Names(swappable)} in another format.";
            var stuck = noScan ? [] : au.Except(swappable).ToList();
            if (stuck.Count > 0) line += $" For {Names(stuck)}, ask them to render {Plural(stuck.Count, "that channel", "those channels")} to audio.";
            actions.Add(line);
        }
        var wrong = checks.Where(c => c.Here == ShareStatus.WrongFormat).ToList();
        if (wrong.Count > 0)
            actions.Add($"You have {Names(wrong)} in a different format from the one the project saved. Install the matching " +
                        $"version too, or ask {who} to switch to the one you have.");
        var missing = checks.Where(c => c.Here == ShareStatus.Missing).ToList();
        if (missing.Count > 0)
            actions.Add($"Not installed on this PC: {Names(missing)}. Install {Plural(missing.Count, "it", "them")}, " +
                        $"or ask {who} to render {Plural(missing.Count, "that channel", "those channels")} to audio.");

        if (haveFriend)
        {
            var theirs = checks.Where(BlocksFriendOnly).ToList();
            if (theirs.Count > 0)
                actions.Add($"{Cap(who)} can't load {Names(theirs)}. Before sending, render {Plural(theirs.Count, "that channel", "those channels")} " +
                            "to audio and keep the original muted, so the sound travels and the plugin can come back later.");
            var unknown = checks.Where(c => c.Friend == ShareStatus.Unknown).ToList();
            if (unknown.Count > 0)
                actions.Add($"{Cap(who)}'s list doesn't say which format they have for {Names(unknown)}. Worth asking.");
        }
        else if (checks.Count > 0)
        {
            actions.Add($"Add {who}'s plugin list to check their side too.");
        }

        if (checks.Any(c => c.Here == ShareStatus.Note || c.Friend == ShareStatus.Note))
            actions.Add("This project uses Waves plugins. They only load when you both have the same Waves version installed.");

        var lost = samples.Where(s => !s.Found).ToList();
        if (lost.Count > 0)
            actions.Add($"{lost.Count} {Plural(lost.Count, "sample can't", "samples can't")} be found: " +
                        $"{JoinWithAnd(lost.Take(5).Select(s => s.FileName).ToList())}{(lost.Count > 5 ? " and more" : "")}. " +
                        (daw == "FL Studio" ? "FL's zipped loop package only includes samples it can find." : "They won't travel with the project."));

        if (noScan) actions.Insert(0, "Scan your plugins (Scan for changes) so this PC can be checked.");
        return actions;
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    static string Headline(List<PluginCheck> checks, List<SampleCheck> samples, Readiness readiness, string who, bool haveFriend,
                           bool noScan)
    {
        if (readiness == Readiness.Blocked)
        {
            var parts = new List<string>();
            var here = checks.Count(c => IsBlocking(c.Here));
            if (here > 0) parts.Add($"{here} {Plural(here, "plugin won't", "plugins won't")} load on this PC");
            var theirs = checks.Count(BlocksFriendOnly);
            if (theirs > 0) parts.Add($"{theirs} {Plural(theirs, "plugin won't", "plugins won't")} load for {who}");
            var lost = samples.Count(s => !s.Found);
            if (lost > 0) parts.Add($"{lost} {Plural(lost, "sample is", "samples are")} missing");
            return Cap(JoinWithAnd(parts)) + ".";
        }
        if (readiness == Readiness.Ready) return "Ready to share: everything loads on both computers.";
        if (noScan && checks.Count > 0) return "This PC hasn't been checked yet: scan your plugins (Scan for changes) first.";
        return !haveFriend && checks.All(c => c.Here is ShareStatus.Ok or ShareStatus.Note)
            ? $"Everything loads on this PC. Add {who}'s plugin list to check their side."
            : "Probably fine, but a few things are worth checking.";
    }

    /// <summary>Where the project was last saved, judging by the plugin and sample paths it stores.</summary>
    static string SavedOn(ProjectInfo info)
    {
        var paths = info.Plugins.Select(p => p.Path).Concat(info.Samples).Where(p => p.Length > 0).ToList();
        var mac = info.Plugins.Any(p => p.Format == "AU") || paths.Any(p => p.StartsWith("/Library/") || p.StartsWith("/Users/") ||
                                                                          p.StartsWith("/Applications/") || p.StartsWith("/Volumes/"));
        var win = paths.Any(p => WindowsPath().IsMatch(p));
        return (mac, win) switch
        {
            (true, true) => "Mac and Windows",
            (true, false) => "Mac",
            (false, true) => "Windows",
            _ => "",
        };
    }

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex WindowsPath();

    // ---- what goes back to the friend -------------------------------------------------------

    /// <summary>A short message to send your friend about what needs changing, or "" if nothing does.</summary>
    public static string MessageForFriend(ShareReport r)
    {
        var name = r.FriendName is { Length: > 0 } n ? n : "";
        var sb = new StringBuilder();
        var blockedHere = r.Plugins.Where(c => IsBlocking(c.Here)).ToList();
        var blockedThere = r.Plugins.Where(BlocksFriendOnly).ToList();

        // Your own project: what doesn't load here is yours to sort out, not something to ask them for.
        if (r.Outgoing) blockedHere.Clear();
        if (blockedHere.Count > 0)
        {
            sb.AppendLine($"Hey{(name.Length > 0 ? " " + name : "")}, I checked {r.ProjectName} and {Plural(blockedHere.Count, "one plugin doesn't", "a few plugins don't")} load on my side:");
            foreach (var c in blockedHere)
            {
                var mine = JoinWithAnd(c.HereHas);
                var ask = c.Here switch
                {
                    ShareStatus.WontLoad when c.HereHas.Count > 0 =>
                        $"it's the Audio Unit and Windows can't run those. I have the {mine}, could you switch to that?",
                    ShareStatus.WontLoad when r.NotScanned => "it's the Audio Unit and Windows can't run those. Could you switch to the VST3 version?",
                    ShareStatus.WontLoad => "it's the Audio Unit and Windows can't run those, and I don't have another version. Could you render it to audio?",
                    ShareStatus.WrongFormat => $"I only have the {mine}. Could you switch to that one?",
                    _ => "I don't have it. Could you render it to audio?",
                };
                sb.AppendLine($"- {c.Name} ({c.Format}): {ask}");
            }
        }
        if (blockedThere.Count > 0)
        {
            sb.AppendLine(sb.Length == 0
                ? $"Hey{(name.Length > 0 ? " " + name : "")}, heads up about {r.ProjectName}: from your plugin list you might not have:"
                : "And from your plugin list you might not have:");
            foreach (var c in blockedThere) sb.AppendLine($"- {c.Name} ({c.Format}): I'll render it to audio before I send it.");
        }
        var lost = r.Outgoing ? [] : r.MissingSamples.ToList();
        if (lost.Count > 0)
        {
            if (sb.Length == 0) sb.AppendLine($"Hey{(name.Length > 0 ? " " + name : "")}, about {r.ProjectName}:");
            sb.AppendLine($"These samples didn't come through: {JoinWithAnd(lost.Select(s => s.FileName).ToList())}. Could you send them?");
        }
        if (sb.Length > 0) sb.AppendLine("Thanks!");
        return sb.ToString().TrimEnd();
    }

    /// <summary>A plain-text readme to put in the zip, so the other person sees what the project needs.</summary>
    public static string Readme(ShareReport r, DateTime? when = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{r.ProjectName}: what this project needs");
        sb.AppendLine(new string('=', Math.Min(60, r.ProjectName.Length + 26)));
        sb.AppendLine($"{r.Daw}{(r.DawVersion.Length > 0 ? " " + r.DawVersion : "")}" +
                      (r.Tempo is { } t ? $", {t:0.##} BPM" : "") +
                      (r.SavedOn.Length > 0 ? $", last saved on {r.SavedOn}" : ""));
        sb.AppendLine($"Checked by Music Organiser on {(when ?? DateTime.Now):d MMM yyyy}.");
        sb.AppendLine();

        void Section(string title, IEnumerable<PluginCheck> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            sb.AppendLine(title);
            foreach (var c in list)
            {
                var vendor = c.Vendor.Length > 0 ? $" ({c.Vendor})" : "";
                var flag = c.IsAu ? "   << Mac only (Audio Unit)" : "";
                sb.AppendLine($"  {c.Name}{vendor}, {c.Format}{flag}");
            }
            sb.AppendLine();
        }
        Section("INSTRUMENTS", r.Plugins.Where(p => p.Kind == PluginKind.Instrument).OrderBy(p => p.Name));
        Section("EFFECTS", r.Plugins.Where(p => p.Kind != PluginKind.Instrument).OrderBy(p => p.Name));
        if (r.NativePlugins.Count > 0)
        {
            sb.AppendLine($"BUILT INTO {r.Daw.ToUpperInvariant()} (open on Mac and Windows)");
            sb.AppendLine("  " + string.Join(", ", r.NativePlugins));
            sb.AppendLine();
        }
        var lost = r.MissingSamples.ToList();
        sb.AppendLine($"SAMPLES: {r.Samples.Count}{(lost.Count > 0 ? $", {lost.Count} missing" : "")}");
        foreach (var s in lost) sb.AppendLine($"  Missing: {s.FileName}");
        sb.AppendLine();
        if (r.Plugins.Any(p => p.IsAu))
        {
            sb.AppendLine("Audio Units only work on a Mac: on Windows these channels open empty.");
            sb.AppendLine("Use the VST3 version of a plugin when you share a project between Mac and Windows.");
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    public const string ReadmeName = "SHARE-README.txt";

    /// <summary>Puts the readme into a zip (replacing an older one), next to the project.</summary>
    public static void AddReadmeToZip(string zipPath, string readme)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        foreach (var old in zip.Entries.Where(e => e.FullName.Equals(ReadmeName, StringComparison.OrdinalIgnoreCase)).ToList())
            old.Delete();
        var entry = zip.CreateEntry(ReadmeName);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(readme.Replace("\r\n", "\n").Replace("\n", "\r\n"));
    }
}
