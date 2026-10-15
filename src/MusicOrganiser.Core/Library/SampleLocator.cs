using System.Collections.Concurrent;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Library;

/// <summary>
/// Finds a project's sample on this PC. Solomon's rule (2026-10-15): "when deciding if something is missing check
/// the project folders and native folders for that daw". So a sample counts as found, in this order:
/// <list type="number">
/// <item>where the project says it is (a relative path from the project's folder);</item>
/// <item>at the same path on the project's drive, for a drive that was given a new letter (F: is now K:);</item>
/// <item>where it would be if the folders it was in moved with the project: a path into the project's own folder,
/// or into a folder above it ("F:\Bitwig Studio\Projects\Other Song\samples\x.wav" next to "K:\Bitwig Studio\Projects\This Song");</item>
/// <item>anywhere in the project's folder tree with the same file name (Bitwig's bounce, multi-samples and
/// recordings folders, FL Studio's Audio and Samples folders);</item>
/// <item>in the DAW's own content folders (its factory packs, user library and installed sound packages), where the
/// folder it's in must have the same name too, since those folders hold thousands of sounds.</item>
/// </list>
/// Folder trees are read once and kept for a few minutes, so a scan reads each folder once.
/// </summary>
public sealed class SampleLocator(TimeSpan keep, Func<string, IReadOnlyList<string>>? contentFolders = null)
{
    /// <summary>The one the scanner uses.</summary>
    public static SampleLocator Shared { get; } = new(TimeSpan.FromMinutes(5));

    /// <summary>The most files read from one folder tree; a project kept at the top of a big drive stops there.</summary>
    public const int MostFiles = 200_000;

    readonly Func<string, IReadOnlyList<string>> _contentFolders = contentFolders ?? DawContentFolders;
    readonly ConcurrentDictionary<string, (DateTime Read, Lazy<FolderIndex> Index)> _trees = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this sample is on this PC, by the rules above. FL Studio's own content (%FLStudioFactoryData%...) always is.</summary>
    public bool Found(string sample, string projectPath) => sample.StartsWith('%') || Locate(sample, projectPath) is not null;

    /// <summary>Where this sample is on this PC, by the rules above, or null when it's missing.</summary>
    public string? Locate(string sample, string projectPath)
    {
        if (sample.Length == 0) return null;
        try
        {
            foreach (var candidate in Candidates(sample, projectPath))
                if (File.Exists(candidate)) return candidate;

            var parts = Split(sample).Parts;
            var projectDir = Path.GetDirectoryName(projectPath);
            if (parts.Length == 0 || string.IsNullOrEmpty(projectDir)) return null;
            var name = parts[^1];
            // A project kept at the top of a drive: only the files beside it, not the whole drive.
            var depth = Path.GetPathRoot(projectDir) is { Length: > 0 } root && Same(root, projectDir) ? 0 : int.MaxValue;
            if (Tree(projectDir, depth).Find(name, folder: null) is { } inProject)
                return inProject;

            var folder = parts.Length > 1 ? parts[^2] : null;
            foreach (var content in _contentFolders(projectPath))
                if (Tree(content, int.MaxValue).Find(name, folder) is { } inContent)
                    return inContent;
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// The paths to try for a sample before searching folders, best first: as saved, then moved with a drive or a
    /// folder above the project. Works on Windows paths on any OS, so it can be tested anywhere.
    /// </summary>
    public static IEnumerable<string> Candidates(string sample, string projectPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (root, parts) = Split(sample);
        var project = Split(projectPath);
        var dir = project.Parts[..^1];
        var sep = projectPath.Contains('\\') && !projectPath.Contains('/') ? '\\' : Path.DirectorySeparatorChar;
        string Join(string r, IEnumerable<string> p) => r + string.Join(sep, p);

        if (root.Length > 0)
        {
            if (seen.Add(sample)) yield return sample;
        }
        else if (Join(project.Root, [.. dir, .. parts]) is var relative && seen.Add(relative))
            yield return relative;

        // A drive that was given a new letter: the same path on the project's drive.
        if (IsDrive(root) && IsDrive(project.Root) && !Same(root, project.Root) &&
            Join(project.Root, parts) is var sameDrive && seen.Add(sameDrive))
            yield return sameDrive;

        // A folder that moved with the project: the project's own folder first, then each folder above it.
        if (root.Length == 0) yield break;
        for (var k = dir.Length - 1; k >= 0; k--)
        {
            var at = -1;
            for (var i = parts.Length - 2; i >= 0 && at < 0; i--)
                if (Same(parts[i], dir[k])) at = i;
            if (at < 0) continue;
            var moved = Join(project.Root, [.. dir[..(k + 1)], .. parts[(at + 1)..]]);
            if (seen.Add(moved)) yield return moved;
        }
    }

    /// <summary>
    /// The DAW's own content folders on this PC for a project, by its file type: FL Studio's packs and user data folder,
    /// Bitwig's library and installed sound packages, Studio One's user folder. Other projects' folders inside them are
    /// left out, so a recording of another song with the same name doesn't count.
    /// </summary>
    public static IReadOnlyList<string> DawContentFolders(string projectPath)
    {
        var extension = Path.GetExtension(projectPath).ToLowerInvariant();
        var folders = new List<string>(DawFolders.GetOrAdd(extension, FixedContentFolders));
        // Projects kept in a Bitwig Studio folder of their own (K:\Bitwig Studio\Projects) can have a library beside them.
        if (extension == ".bwproject")
            for (var d = Path.GetDirectoryName(projectPath); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                if (Path.GetFileName(d).Equals("Bitwig Studio", StringComparison.OrdinalIgnoreCase) &&
                    Path.Combine(d, "Library") is var library && Directory.Exists(library) && !folders.Contains(library, StringComparer.OrdinalIgnoreCase))
                    folders.Add(library);
        return folders;
    }

    static readonly ConcurrentDictionary<string, IReadOnlyList<string>> DawFolders = new();

    static IReadOnlyList<string> FixedContentFolders(string extension)
    {
        string Special(Environment.SpecialFolder f) => Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);
        var home = Special(Environment.SpecialFolder.UserProfile);
        var documents = Special(OperatingSystem.IsLinux() ? Environment.SpecialFolder.UserProfile : Environment.SpecialFolder.MyDocuments);
        var folders = new List<string>();
        try
        {
            switch (extension)
            {
                case ".flp":
                    foreach (var programs in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                        if (Special(programs) is { Length: > 0 } pf && Directory.Exists(Path.Combine(pf, "Image-Line")))
                            foreach (var install in Directory.EnumerateDirectories(Path.Combine(pf, "Image-Line"), "FL Studio*"))
                                folders.Add(Path.Combine(install, "Data", "Patches", "Packs"));
                    folders.Add(Path.Combine(documents, "Image-Line", "FL Studio"));
                    folders.Add(Path.Combine(documents, "Image-Line", "Data", "FL Studio"));
                    break;
                case ".bwproject":
                    folders.Add(Path.Combine(documents, "Bitwig Studio"));
                    folders.Add(OperatingSystem.IsWindows()
                        ? Path.Combine(Special(Environment.SpecialFolder.LocalApplicationData), "Bitwig Studio", "installed-packages")
                        : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support", "Bitwig", "Bitwig Studio", "installed-packages")
                        : Path.Combine(home, ".BitwigStudio", "installed-packages"));
                    if (OperatingSystem.IsWindows())
                        folders.Add(Path.Combine(Special(Environment.SpecialFolder.CommonApplicationData), "Bitwig Studio", "installed-packages"));
                    break;
                case ".song":
                    folders.Add(Path.Combine(documents, "Studio One"));
                    folders.Add(Path.Combine(documents, "Fender Studio Pro"));
                    break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return folders.Where(f => f.Length > 0 && Path.IsPathRooted(f) && Directory.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Folders inside a DAW's folders that hold projects, autosaves or history rather than sounds.</summary>
    static readonly HashSet<string> NotContent = new(StringComparer.OrdinalIgnoreCase)
        { "Projects", "Songs", "Shows", "auto-backups", "Backup", "History", "$RECYCLE.BIN", "System Volume Information" };

    FolderIndex Tree(string folder, int depth)
    {
        var now = DateTime.UtcNow;
        var entry = _trees.AddOrUpdate(folder,
            f => (now, new Lazy<FolderIndex>(() => FolderIndex.Read(f, depth))),
            (f, old) => now - old.Read < keep ? old : (now, new Lazy<FolderIndex>(() => FolderIndex.Read(f, depth))));
        if (_trees.Count > 4096)
            foreach (var key in _trees.Keys)
                if (!Same(key, folder)) _trees.TryRemove(key, out _);
        return entry.Index.Value;
    }

    /// <summary>The sample files in a folder tree, by file name.</summary>
    sealed class FolderIndex
    {
        readonly Dictionary<string, List<string>> _byName = new(StringComparer.OrdinalIgnoreCase);

        public static FolderIndex Read(string root, int depth)
        {
            var index = new FolderIndex();
            var files = 0;
            var todo = new Stack<(string Dir, int Depth)>();
            todo.Push((root, 0));
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
            while (todo.Count > 0 && files < MostFiles)
            {
                var (dir, level) = todo.Pop();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", options))
                    {
                        if (!IsSample(f)) continue;
                        var name = Path.GetFileName(f);
                        if (!index._byName.TryGetValue(name, out var list)) index._byName[name] = list = [];
                        list.Add(f);
                        if (++files >= MostFiles) break;
                    }
                    if (level >= depth) continue;
                    foreach (var d in Directory.EnumerateDirectories(dir, "*", options).OrderDescending(StringComparer.OrdinalIgnoreCase))
                    {
                        var name = Path.GetFileName(d);
                        if (!name.StartsWith('.') && !NotContent.Contains(name)) todo.Push((d, level + 1));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return index;
        }

        /// <summary>A file with this name, in a folder with this name when one is given; one that still exists.</summary>
        public string? Find(string name, string? folder) =>
            _byName.TryGetValue(name, out var list)
                ? list.FirstOrDefault(f => (folder is null || Same(Path.GetFileName(Path.GetDirectoryName(f)) ?? "", folder)) && File.Exists(f))
                : null;
    }

    static bool IsSample(string path) =>
        TextScan.IsAudioPath(path) || path.EndsWith(".multisample", StringComparison.OrdinalIgnoreCase);

    static bool Same(string a, string b) => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    static bool IsDrive(string root) => root.Length >= 2 && char.IsAsciiLetter(root[0]) && root[1] == ':';

    /// <summary>A path's root ("K:\", "/", "\\server\share\", or "" when relative) and its folder and file names.</summary>
    static (string Root, string[] Parts) Split(string path)
    {
        var root = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/' ? path[..3]
                 : path.StartsWith(@"\\") ? @"\\"
                 : path.StartsWith('/') || path.StartsWith('\\') ? path[..1]
                 : "";
        return (root, path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries));
    }
}
