using System.Text.RegularExpressions;
using MusicOrganiser.Core.Export;

namespace MusicOrganiser.Core.DawLink;

public enum ScriptState { NotInstalled, Installed, Outdated }

/// <summary>A script that links the app with a DAW, and the folders the DAW loads it from.</summary>
/// <param name="Source">The copy that ships with the app, in Extensions next to it.</param>
/// <param name="Folders">Where it goes: one folder for each place the DAW may look.</param>
/// <param name="Kept">The start of a setting line people may change in their copy, which updates keep.</param>
/// <param name="Replaces">Older forms of the link to remove when this is installed, so the DAW doesn't run both.</param>
public sealed record DawScript(string Daw, string Source, IReadOnlyList<string> Folders, string? Kept = null,
                               IReadOnlyList<string>? Replaces = null)
{
    public string FileName => Path.GetFileName(Source);

    /// <summary>A built extension (Bitwig's .bwextension, a Java archive) rather than a script's text.</summary>
    public bool Binary => Path.GetExtension(Source).Equals(".bwextension", StringComparison.OrdinalIgnoreCase);

    /// <summary>The folders that have the script in them.</summary>
    public IReadOnlyList<string> InstalledIn => Folders.Where(f => File.Exists(Path.Combine(f, FileName))).ToList();

    /// <summary>Installed when every copy matches the app's own (apart from the kept setting).</summary>
    public ScriptState State
    {
        get
        {
            var installed = InstalledIn;
            if (installed.Count == 0) return ScriptState.NotInstalled;
            if (Binary)
            {
                var built = File.ReadAllBytes(Source);
                return installed.All(f => File.ReadAllBytes(Path.Combine(f, FileName)).AsSpan().SequenceEqual(built))
                    ? ScriptState.Installed : ScriptState.Outdated;
            }
            var shipped = Lines(File.ReadAllText(Source));
            return installed.All(f => Lines(File.ReadAllText(Path.Combine(f, FileName))).SequenceEqual(shipped))
                ? ScriptState.Installed : ScriptState.Outdated;
        }
    }

    /// <summary>Copies the app's script into every folder, keeping the <see cref="Kept"/> setting each copy has.</summary>
    public void Install()
    {
        foreach (var old in Replaces ?? [])
        {
            if (!File.Exists(old)) continue;
            File.Delete(old);
            var folder = Path.GetDirectoryName(old)!;
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        if (Binary)
        {
            foreach (var folder in Folders)
            {
                Directory.CreateDirectory(folder);
                try { File.Copy(Source, Path.Combine(folder, FileName), overwrite: true); }
                catch (IOException e) when (File.Exists(Path.Combine(folder, FileName)))
                {
                    // Windows won't replace a file a program has open, and the DAW may keep its extensions open.
                    throw new IOException($"{e.Message} If {Daw} is open, close it, then press Install again.", e);
                }
            }
            return;
        }
        var shipped = File.ReadAllText(Source);
        foreach (var folder in Folders)
        {
            var target = Path.Combine(folder, FileName);
            var text = shipped;
            if (Kept is { } kept && File.Exists(target) && Setting(File.ReadAllText(target), kept) is { } theirs)
                text = new Regex("^" + Regex.Escape(kept) + "[^\r\n]*", RegexOptions.Multiline).Replace(text, theirs.Replace("$", "$$"), 1);
            Directory.CreateDirectory(folder);
            File.WriteAllText(target, text);
        }
    }

    IEnumerable<string> Lines(string text) =>
        text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Where(l => Kept is null || !l.StartsWith(Kept, StringComparison.Ordinal));

    static string? Setting(string text, string kept) =>
        text.ReplaceLineEndings("\n").Split('\n').FirstOrDefault(l => l.StartsWith(kept, StringComparison.Ordinal));
}

/// <summary>The scripts that link the app with Bitwig and FL Studio, and where each DAW looks for them.</summary>
public static class DawScripts
{
    /// <summary>The FL Studio script's setting for letting the app make changes, which updates keep.</summary>
    public const string FlChanges = "LET_THE_APP_MAKE_CHANGES = ";

    /// <summary>One of the scripts the app ships, in Extensions next to it.</summary>
    public static string Shipped(string daw, string file) => Path.Combine(AppContext.BaseDirectory, "Extensions", daw, file);

    /// <summary>The Bitwig controller script, for Controller Scripts in Bitwig's user folder.</summary>
    public static DawScript Bitwig(string userFolder) => Bitwig([userFolder]);

    /// <summary>The Bitwig controller script, for Controller Scripts in each of these user folders (<see cref="BitwigUserFolders"/>).</summary>
    public static DawScript Bitwig(IReadOnlyList<string>? userFolders = null) =>
        new("Bitwig Studio", Shipped("Bitwig", "MusicOrganiser.control.js"),
            (userFolders ?? BitwigUserFolders()).Select(f => Path.Combine(f, "Controller Scripts", "Music Organiser")).ToList());

    /// <summary>
    /// The Bitwig extension (Java), for Extensions in each of these user folders. It takes over from the script, as the
    /// same controller, so installing it removes the script.
    /// </summary>
    public static DawScript BitwigExtension(IReadOnlyList<string>? userFolders = null, string? source = null)
    {
        var folders = userFolders ?? BitwigUserFolders();
        return new("Bitwig Studio", source ?? Shipped("Bitwig", "MusicOrganiser.bwextension"),
                   folders.Select(f => Path.Combine(f, "Extensions")).ToList(),
                   Replaces: Bitwig(folders).Folders.Select(f => Path.Combine(f, "MusicOrganiser.control.js")).ToList());
    }

    /// <summary>Bitwig's link: the extension when the build made it (it needs a JDK), else the script.</summary>
    /// <param name="extension">The built extension (the one next to the app unless given).</param>
    public static DawScript BitwigLink(IReadOnlyList<string>? userFolders = null, string? extension = null) =>
        BitwigExtension(userFolders, extension) is var built && File.Exists(built.Source) ? built : Bitwig(userFolders);

    /// <summary>Bitwig's usual user folder: Documents\Bitwig Studio (on Linux, ~/Bitwig Studio).</summary>
    public static string BitwigUserFolder => BitwigPreset.DefaultFolders()[0];

    /// <summary>
    /// Bitwig's user folders: the one picked in the app, or else every Bitwig Studio folder in a Documents folder. A PC
    /// can have more than one (Windows may keep Documents in OneDrive, and an older one at home), and only Bitwig knows
    /// which it uses (Settings > Locations), so without a choice the script goes in each.
    /// </summary>
    public static IReadOnlyList<string> BitwigUserFolders(string? chosen = null)
    {
        if (chosen is { Length: > 0 }) return [chosen];
        IEnumerable<string> documents = [Path.GetDirectoryName(BitwigUserFolder)!];
        if (!OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            documents = documents.Append(Path.Combine(home, "Documents"))
                .Concat(new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
                    .Select(Environment.GetEnvironmentVariable).OfType<string>().Where(d => d.Length > 0)
                    .Select(d => Path.Combine(d, "Documents")));
        }
        return documents.Where(d => d.Length > 0).Select(d => Path.Combine(d, "Bitwig Studio"))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }

    /// <summary>Bitwig's user folder has Library, Projects or Extensions folders in it, or is called Bitwig Studio.</summary>
    public static bool IsBitwigUserFolder(string folder) =>
        Path.GetFileName(folder.TrimEnd('\\', '/')).Equals("Bitwig Studio", StringComparison.OrdinalIgnoreCase) ||
        new[] { "Library", "Projects", "Extensions", "Controller Scripts" }.Any(d => Directory.Exists(Path.Combine(folder, d)));

    /// <summary>The FL Studio script, for Settings\Hardware in each of FL Studio's user data folders.</summary>
    public static DawScript FlStudio(IReadOnlyList<string> userDataFolders) =>
        new("FL Studio", Shipped("FL Studio", FlLink.Script),
            userDataFolders.Select(f => Path.Combine(f, "Settings", "Hardware", "Music Organiser")).ToList(), FlChanges);

    /// <summary>
    /// FL Studio's user data folders: one picked in the app, the usual one in Documents, and any that holds
    /// the given FL projects (FL Studio keeps a Projects folder next to its Settings). FL Studio can be told
    /// to use another, so there may be more than one.
    /// </summary>
    public static IReadOnlyList<string> FlUserDataFolders(IEnumerable<string> projects, string? chosen = null)
    {
        var found = new List<string>();
        void Add(string folder)
        {
            if (!found.Contains(folder, StringComparer.OrdinalIgnoreCase)) found.Add(folder);
        }

        if (chosen is { Length: > 0 }) Add(chosen);
        var documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Image-Line", "FL Studio");
        if (IsFlUserData(documents)) Add(documents);
        var looked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
            for (var dir = Path.GetDirectoryName(project); !string.IsNullOrEmpty(dir) && looked.Add(dir); dir = Path.GetDirectoryName(dir))
                if (IsFlUserData(dir)) Add(dir);
        return found;
    }

    /// <summary>FL Studio's user data folder has Settings, Presets and Projects folders in it.</summary>
    public static bool IsFlUserData(string folder) =>
        Directory.Exists(Path.Combine(folder, "Settings")) && Directory.Exists(Path.Combine(folder, "Presets")) &&
        Directory.Exists(Path.Combine(folder, "Projects"));
}
