using System.IO.Compression;
using System.Text.RegularExpressions;
using MusicOrganiser.Core.Export;

namespace MusicOrganiser.Core.DawLink;

public enum ScriptState { NotInstalled, Installed, Outdated }

/// <summary>A script that links the app with a DAW, and the folders the DAW loads it from.</summary>
/// <param name="Source">The copy that ships with the app, in Extensions next to it.</param>
/// <param name="Folders">Where it goes: one folder for each place the DAW may look.</param>
/// <param name="Kept">The start of a setting line people may change in their copy, which updates keep.</param>
/// <param name="Replaces">Older forms of the link, and copies in the DAW's other user folders, to remove when this is
/// installed, so the DAW can't run two.</param>
/// <param name="Code">For a built extension, the folder of its code when the app runs from a copy of the repo, to tell
/// when the build is older than the code.</param>
public sealed record DawScript(string Daw, string Source, IReadOnlyList<string> Folders, string? Kept = null,
                               IReadOnlyList<string>? Replaces = null, string? Code = null)
{
    public string FileName => Path.GetFileName(Source);

    /// <summary>A built extension (Bitwig's .bwextension, a Java archive) rather than a script's text.</summary>
    public bool Binary => Path.GetExtension(Source).Equals(".bwextension", StringComparison.OrdinalIgnoreCase);

    /// <summary>The version of the extension Install copies, or null for a script.</summary>
    public string? Version => Binary ? ExtensionVersion(Source) : null;

    /// <summary>The versions of the extension installed, folder by folder ("" where one doesn't say).</summary>
    public IReadOnlyList<string> InstalledVersions =>
        Binary ? InstalledIn.Select(f => ExtensionVersion(Path.Combine(f, FileName)) ?? "").ToList() : [];

    /// <summary>
    /// The newest file of the extension's <see cref="Code"/> when it changed after the extension was built (so Install
    /// would copy an old build: running the app with hot reload doesn't build it again), else null.
    /// </summary>
    public string? ChangedSinceBuilt
    {
        get
        {
            if (Code is null || !File.Exists(Source)) return null;
            var built = File.GetLastWriteTimeUtc(Source);
            var main = Path.Combine(Code, "src", "main");
            var files = Directory.Exists(main) ? Directory.EnumerateFiles(main, "*", SearchOption.AllDirectories) : [];
            return files.Append(Path.Combine(Code, "pom.xml")).Where(File.Exists)
                        .Select(f => (File: f, Changed: File.GetLastWriteTimeUtc(f))).Where(f => f.Changed > built)
                        .OrderByDescending(f => f.Changed).Select(f => f.File).FirstOrDefault();
        }
    }

    /// <summary>The version a built extension gives in its manifest (Implementation-Version), or null if it doesn't say.</summary>
    public static string? ExtensionVersion(string file)
    {
        try
        {
            using var zip = ZipFile.OpenRead(file);
            if (zip.GetEntry("META-INF/MANIFEST.MF") is not { } manifest) return null;
            using var reader = new StreamReader(manifest.Open());
            for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
                if (line.StartsWith("Implementation-Version:", StringComparison.OrdinalIgnoreCase))
                    return line["Implementation-Version:".Length..].Trim();
            return null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The older forms and other copies of the link that are there now, which Install removes.</summary>
    public IReadOnlyList<string> Leftovers => (Replaces ?? []).Where(File.Exists).ToList();

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

    /// <summary>
    /// Removes the <see cref="Leftovers"/>, then copies the app's script into every folder, keeping the <see cref="Kept"/>
    /// setting each copy has. Returns the leftovers it removed.
    /// </summary>
    public IReadOnlyList<string> Install()
    {
        var removed = new List<string>();
        foreach (var old in Leftovers)
        {
            try { File.Delete(old); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Couldn't remove the old copy {old}: {e.Message} If {Daw} is open, close it, then press Install again.", e);
            }
            removed.Add(old);
            // The script's own folder goes with it once empty, but never one of the DAW's (its Extensions folder).
            var folder = Path.GetDirectoryName(old)!;
            if (Path.GetFileName(folder) == "Music Organiser" && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
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
            return removed;
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
        return removed;
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
    /// <param name="others">Bitwig user folders found besides those, which Install takes any copy of the link out of.</param>
    public static DawScript Bitwig(IReadOnlyList<string>? userFolders = null, IReadOnlyList<string>? others = null)
    {
        var folders = userFolders ?? BitwigUserFolders();
        var replaces = CopiesIn(others, folders).ToList();
        return new("Bitwig Studio", Shipped("Bitwig", "MusicOrganiser.control.js"),
                   folders.Select(f => Path.Combine(f, "Controller Scripts", "Music Organiser")).ToList(),
                   Replaces: replaces.Count > 0 ? replaces : null);
    }

    /// <summary>
    /// The Bitwig extension (Java), for Extensions in each of these user folders. It takes over from the script, as the
    /// same controller, so installing it removes the script.
    /// </summary>
    /// <param name="source">The built extension: unless given, the one next to the app (and then, when the app runs from
    /// a copy of the repo, its code there too).</param>
    /// <param name="others">Bitwig user folders found besides those, which Install takes any copy of the link out of.</param>
    public static DawScript BitwigExtension(IReadOnlyList<string>? userFolders = null, string? source = null,
                                            IReadOnlyList<string>? others = null)
    {
        var folders = userFolders ?? BitwigUserFolders();
        return new("Bitwig Studio", source ?? Shipped("Bitwig", "MusicOrganiser.bwextension"),
                   folders.Select(f => Path.Combine(f, "Extensions")).ToList(),
                   Replaces: Bitwig(folders).Folders.Select(f => Path.Combine(f, "MusicOrganiser.control.js"))
                                            .Concat(CopiesIn(others, folders)).ToList(),
                   Code: source is null ? ExtensionCode() : null);
    }

    /// <summary>
    /// Where either form of the link would be in each of the <paramref name="others"/> that isn't one of the
    /// <paramref name="folders"/> it goes into. A copy left in another user folder (say Documents\Bitwig Studio, from
    /// before Bitwig's folder was chosen) could be loaded as well as the new one, as the same controller.
    /// </summary>
    static IEnumerable<string> CopiesIn(IReadOnlyList<string>? others, IReadOnlyList<string> folders) =>
        (others ?? []).Where(o => !folders.Any(f => SameFolder(f, o))).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(o => new[]
            {
                Path.Combine(o, "Extensions", "MusicOrganiser.bwextension"),
                Path.Combine(o, "Controller Scripts", "Music Organiser", "MusicOrganiser.control.js"),
            });

    static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                      StringComparison.OrdinalIgnoreCase);

    /// <summary>The Bitwig extension's code (extensions/bitwig-java) in the repo the app runs from, or null.</summary>
    static string? ExtensionCode()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "extensions", "bitwig-java", "pom.xml")))
                return Path.Combine(dir.FullName, "extensions", "bitwig-java");
        return null;
    }

    /// <summary>Bitwig's link: the extension when the build made it (it needs a JDK), else the script.</summary>
    /// <param name="extension">The built extension (the one next to the app unless given).</param>
    /// <param name="others">Bitwig user folders found besides those, which Install takes any copy of the link out of.</param>
    public static DawScript BitwigLink(IReadOnlyList<string>? userFolders = null, string? extension = null,
                                       IReadOnlyList<string>? others = null) =>
        BitwigExtension(userFolders, extension, others) is var built && File.Exists(built.Source) ? built : Bitwig(userFolders, others);

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

    /// <summary>The folders Bitwig keeps in its user folder.</summary>
    static readonly string[] BitwigFolders = ["Library", "Projects", "Extensions", "Controller Scripts"];

    /// <summary>Bitwig's user folder has Library, Projects or Extensions folders in it, or is called Bitwig Studio.</summary>
    public static bool IsBitwigUserFolder(string folder) =>
        Path.GetFileName(folder.TrimEnd('\\', '/')).Equals("Bitwig Studio", StringComparison.OrdinalIgnoreCase) ||
        BitwigFolders.Any(d => Directory.Exists(Path.Combine(folder, d)));

    /// <summary>
    /// Bitwig's user folder, from a folder picked in Settings: the folder itself, or the user folder it's in. People
    /// pick Extensions (where the link goes, so it's the one they know) or a folder in Library or Projects. Null if
    /// it's neither.
    /// </summary>
    public static string? BitwigUserFolderFrom(string picked)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(picked));
        var parent = Path.GetDirectoryName(folder);
        var name = Path.GetFileName(folder);
        // Extensions or Controller Scripts first: one with a Library folder of its own in it would pass as a user folder.
        // The folder it's in counts when it's called Bitwig Studio or has another of Bitwig's folders, so an Extensions,
        // Library or Projects folder somewhere else (D:\Music\Projects, say) doesn't make its parent the user folder.
        if (parent is not null && BitwigFolders.Contains(name, StringComparer.OrdinalIgnoreCase) &&
            (Path.GetFileName(parent).Equals("Bitwig Studio", StringComparison.OrdinalIgnoreCase) ||
             BitwigFolders.Any(d => !d.Equals(name, StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(parent, d)))))
            return parent;
        if (IsBitwigUserFolder(folder)) return folder;
        // Deeper in: a Bitwig Studio folder with Bitwig's folders in it (not, say, the one Bitwig is installed in).
        for (var up = parent; up is not null; up = Path.GetDirectoryName(up))
            if (Path.GetFileName(up).Equals("Bitwig Studio", StringComparison.OrdinalIgnoreCase) &&
                BitwigFolders.Any(d => Directory.Exists(Path.Combine(up, d))))
                return up;
        return null;
    }

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

    /// <summary>
    /// FL Studio's user data folder, from a folder picked in Settings: the folder itself, or the one it's in (people
    /// pick Settings\Hardware, where the script goes). Null if it's neither.
    /// </summary>
    public static string? FlUserDataFrom(string picked)
    {
        for (string? up = Path.TrimEndingDirectorySeparator(Path.GetFullPath(picked)); up is not null; up = Path.GetDirectoryName(up))
            if (IsFlUserData(up)) return up;
        return null;
    }
}
