using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Library;

public sealed record ScanProgress(string Stage, int Done, int Total, string Current);

/// <summary>Finds projects in your folders and keeps the library up to date.</summary>
public sealed class Scanner(LibraryDb db)
{
    public const string PluginFoldersKey = "plugin_folders";
    public const string SampleFoldersKey = "sample_folders";

    // DAW autosave folders: Bitwig "auto-backups", FL Studio "Backup", Studio One "History".
    static readonly HashSet<string> AutosaveDirs = new(StringComparer.OrdinalIgnoreCase)
        { "auto-backups", "backup", "backups", "history", "autosave", "autosaves" };

    // FL Studio's own backup copies, should they end up outside its Backup folder:
    // "Song (autosave).flp" and "Song (overwritten at 2213h05).flp".
    static bool IsAutosaveFile(string name) =>
        name.Contains(" (autosave)", StringComparison.OrdinalIgnoreCase) ||
        name.Contains(" (overwritten at ", StringComparison.OrdinalIgnoreCase);

    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "$RECYCLE.BIN", "System Volume Information", "__MACOSX", "node_modules" };

    // Demo songs, tutorials and templates that came with the DAW rather than your own projects.
    // Old FL Studio installs keep demos in "Cool stuff" and loop packs in Data\Patches.
    static readonly HashSet<string> FactoryDirs = new(StringComparer.OrdinalIgnoreCase)
        { "Cool stuff", "Demo songs", "Product demos", "Templates", "Template", "Tutorial", "Tutorials" };

    static bool SkipDir(string name, string parent) =>
        name.StartsWith('.') || SkipDirs.Contains(name) || AutosaveDirs.Contains(name) || FactoryDirs.Contains(name) ||
        name.Equals("Patches", StringComparison.OrdinalIgnoreCase) && parent.Equals("Data", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the scan leaves this file out: an autosave copy, or inside a skipped folder under its root.</summary>
    public static bool IsIgnored(string path, IEnumerable<string> roots)
    {
        if (IsAutosaveFile(Path.GetFileName(path))) return true;
        var root = roots.FirstOrDefault(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase));
        if (root is null) return false;
        var dir = Path.GetDirectoryName(path) ?? "";
        var dirs = (dir.Length > root.Length ? dir[root.Length..] : "")
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < dirs.Length; i++)
            if (SkipDir(dirs[i], i > 0 ? dirs[i - 1] : Path.GetFileName(root.TrimEnd('\\', '/')))) return true;
        return false;
    }

    /// <summary>Every project file under these folders, found with all cores at once and sorted by path.</summary>
    public static List<string> FindProjects(IEnumerable<string> roots, CancellationToken ct = default) =>
        Parallelism.FindFiles(roots, f =>
        {
            var name = Path.GetFileName(f);
            return Readers.For(f) is not null && !name.StartsWith("._") && !IsAutosaveFile(name);
        }, SkipDir, ct: ct);

    /// <summary>
    /// Reads one project. Whether each of its samples is on this PC is decided by <paramref name="samples"/> (see
    /// <see cref="SampleLocator"/> for where it looks); a scan passes one locator for all its projects, so each folder is
    /// read once, and a project read on its own gets a fresh one, so a sample put back just now counts.
    /// </summary>
    public static LibraryDb.IndexedFile Analyse(string path, SampleLocator? samples = null)
    {
        samples ??= new SampleLocator();
        var fi = new FileInfo(path);
        var mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
        try
        {
            var info = Readers.For(path)!.Read(path);
            var found = info.Samples.Select(s => (s, samples.Found(s, path))).ToList();
            var hardware = info.Plugins.Select(p => p.Name).Concat(info.Hardware)
                               .Select(PluginCatalog.ElektronDevice)
                               .OfType<string>().Distinct().Order().ToList();
            return new(path, fi.Length, mtime, info, null, found, hardware);
        }
        catch (Exception e)   // one unreadable file must never stop a scan
        {
            return new(path, fi.Length, mtime, null, $"{e.GetType().Name}: {e.Message}", [], []);
        }
    }

    /// <summary>Index new and changed projects under every library folder.</summary>
    public void ScanProjects(IProgress<ScanProgress>? progress = null, bool full = false, CancellationToken ct = default)
    {
        Readers.Bitwig.KnownPlugins = db.InstalledNames() is { Count: > 0 } names
            ? names.Union(PluginCatalog.AlwaysKnown, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : PluginCatalog.AlwaysKnown;

        var roots = db.Roots().Where(Directory.Exists).ToList();
        progress?.Report(new("Finding projects", 0, 0, ""));
        var files = FindProjects(roots, ct);
        var known = db.KnownFiles();
        var todo = files.AsParallel().AsOrdered().WithDegreeOfParallelism(Parallelism.Cores).WithCancellation(ct)
            .Where(f =>
            {
                try
                {
                    var fi = new FileInfo(f);
                    var mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                    return full || !known.TryGetValue(f, out var k) || k.Mtime != mtime || k.Size != fi.Length;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
            }).ToList();

        // Read the projects on every core; save them here, in path order, a batch at a time,
        // so a new numbered save always finds the earlier ones it takes its status from.
        var samples = new SampleLocator();
        var analysed = todo.AsParallel().AsOrdered().WithDegreeOfParallelism(Parallelism.Cores).WithCancellation(ct)
            .Select(f =>
            {
                try { return Parallelism.Background(() => Analyse(f, samples)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }   // deleted or locked since it was found
            });
        var batch = new List<LibraryDb.IndexedFile>();
        var done = 0;
        var lastReport = Environment.TickCount64;
        foreach (var a in analysed)
        {
            done++;
            if (a is not null) batch.Add(a);
            if (batch.Count >= 50 || Environment.TickCount64 - lastReport > 250)
            {
                db.UpsertMany(batch);
                batch.Clear();
                progress?.Report(new("Reading projects", done, todo.Count, a?.Path ?? ""));
                lastReport = Environment.TickCount64;
            }
        }
        db.UpsertMany(batch);
        db.MarkMissingFiles(files.ToHashSet(StringComparer.OrdinalIgnoreCase), roots, f => IsIgnored(f, roots));
        progress?.Report(new("Done", files.Count, files.Count, ""));
    }

    /// <summary>Refresh the list of plugins installed on this PC.</summary>
    public int ScanInstalledPlugins()
    {
        var folders = db.GetList(PluginFoldersKey) is { Count: > 0 } custom ? custom : PluginCatalog.DefaultFolders().ToList();
        var names = PluginCatalog.Scan(folders);
        db.ReplaceInstalledPlugins(names);
        return names.Count;
    }

    /// <summary>Index audio files in your sample folders, so missing samples can be matched.</summary>
    public int ScanSampleFolders(IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var files = Parallelism.FindFiles(db.GetList(SampleFoldersKey), TextScan.IsAudioPath, (name, _) => name.StartsWith('.'),
            (n, dir) => progress?.Report(new("Indexing samples", n, 0, dir)), ct);
        db.ReplaceSampleIndex(files);
        return files.Count;
    }
}
