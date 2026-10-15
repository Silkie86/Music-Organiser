using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Plugins;

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
        FindProjectStamps(roots, ct).Select(f => f.Path).ToList();

    /// <summary>The project files under these folders, with the size and modified time the folder listing gave.</summary>
    public static List<Parallelism.FoundFile> FindProjectStamps(IEnumerable<string> roots, CancellationToken ct = default) =>
        Parallelism.FindFileStamps(roots, IsProjectFile, SkipDir, ct: ct);

    /// <summary>A file the library lists: one the readers know, leaving out macOS "._" shadows and DAW autosaves.</summary>
    public static bool IsProjectFile(string path)
    {
        var name = Path.GetFileName(path);
        return Readers.For(path) is not null && !name.StartsWith("._") && !IsAutosaveFile(name);
    }

    /// <summary>Whether a project's sample is on this PC: see <see cref="SampleLocator"/> for where it looks.</summary>
    public static bool SampleFound(string sample, string projectPath) => SampleLocator.Shared.Found(sample, projectPath);

    public static LibraryDb.IndexedFile Analyse(string path)
    {
        var fi = new FileInfo(path);
        var mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
        try
        {
            var info = Readers.For(path)!.Read(path);
            var samples = info.Samples.Select(s => (s, SampleFound(s, path))).ToList();
            var hardware = info.Plugins.Select(p => p.Name).Concat(info.Hardware)
                               .Select(PluginCatalog.ElektronDevice)
                               .OfType<string>().Distinct().Order().ToList();
            var notes = info.CanExtractMidi ? ProjectKey.Read(path) : new SongNotes("", -1, -1);
            return new(path, fi.Length, mtime, info, null, samples, hardware)
            {
                DetectedKey = notes?.Key, NoteCount = notes?.Notes ?? -1, LengthBars = notes?.Bars ?? -1,
            };
        }
        catch (Exception e)   // one unreadable file must never stop a scan
        {
            return new(path, fi.Length, mtime, null, $"{e.GetType().Name}: {e.Message}", [], []);
        }
    }

    /// <summary>Index new and changed projects under every library folder. Returns how many project files were new.</summary>
    public int ScanProjects(IProgress<ScanProgress>? progress = null, bool full = false, CancellationToken ct = default)
    {
        Readers.Bitwig.KnownPlugins = db.InstalledNames() is { Count: > 0 } names
            ? names.Union(PluginCatalog.AlwaysKnown, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : PluginCatalog.AlwaysKnown;

        var roots = db.Roots().Where(Directory.Exists).ToList();
        progress?.Report(new("Finding projects", 0, 0, ""));
        var found = FindProjectStamps(roots, ct);
        var files = found.Select(f => f.Path).ToList();
        var known = db.KnownFiles();
        // The folder listing already says each file's size and modified time: only new or changed files are read.
        var todo = found.Where(f => full || !known.TryGetValue(f.Path, out var k) || k.Mtime != f.Mtime || k.Size != f.Size)
                        .Select(f => f.Path).ToList();

        // Read the projects on every core; save them here, in path order, a batch at a time,
        // so a new numbered save always finds the earlier ones it takes its status from.
        var analysed = todo.AsParallel().AsOrdered().WithDegreeOfParallelism(Parallelism.Workers).WithCancellation(ct)
            .Select(f =>
            {
                try { return Parallelism.Background(() => Analyse(f)); }
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
        return todo.Count(f => !known.ContainsKey(f));
    }

    /// <summary>
    /// Brings the library up to date with project files the folder watcher saw change (<see cref="FolderWatch"/>): new
    /// or changed ones are read, gone ones marked missing, and nothing else is looked at. Returns how many of each.
    /// </summary>
    public (int Read, int Gone) Update(IEnumerable<string> paths)
    {
        var roots = db.Roots();
        var known = db.KnownFiles();
        var read = new List<LibraryDb.IndexedFile>();
        var gone = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsProjectFile(path) || IsIgnored(path, roots)) continue;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists)
                {
                    if (known.ContainsKey(path) && db.MarkFileMissing(path)) gone++;
                    continue;
                }
                var mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                if (known.TryGetValue(path, out var k) && k.Mtime == mtime && k.Size == fi.Length) continue;   // already read
                read.Add(Parallelism.Background(() => Analyse(path)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // still being written, or locked: the next change or scan gets it
        }
        db.UpsertMany(read);
        return (read.Count, gone);
    }

    /// <summary>Refresh the list of plugins installed on this PC.</summary>
    public int ScanInstalledPlugins() => ScanInstalledPlugins(out _);

    /// <summary>
    /// Refresh the list of plugins installed on this PC: the plugin files in the plugin folders, and
    /// the plugins the native scanner found inside them (a Waves shell holds hundreds).
    /// <paramref name="toRead"/> gets the files the scanner hasn't read since they last changed.
    /// </summary>
    public int ScanInstalledPlugins(out IReadOnlyList<string> toRead)
    {
        var folders = db.GetList(PluginFoldersKey) is { Count: > 0 } custom ? custom : PluginCatalog.DefaultFolders().ToList();
        var files = PluginCatalog.ScanFiles(folders);
        db.ForgetPluginScans(files.Select(f => f.Path));
        var read = db.PluginScanStamps();
        toRead = files.Select(f => f.Path).Where(PluginScanner.CanRead)
                      .Where(f => !read.TryGetValue(f, out var stamp) || stamp != PluginScanner.Stamp(f))
                      .ToList();
        var names = new HashSet<string>(PluginCatalog.AlwaysKnown, StringComparer.OrdinalIgnoreCase);
        names.UnionWith(files.Select(f => f.Name));
        names.UnionWith(db.ScannedPluginNames());
        db.ReplaceInstalledPlugins(names);
        return names.Count;
    }

    /// <summary>
    /// Reads plugin files with the native scanner, four at a time, and stores the plugins inside each
    /// one: exact ID, name, vendor, version and kind. Parameters are read later (<see cref="ReadAllParametersAsync"/>).
    /// </summary>
    public async Task ReadPluginFilesAsync(PluginScanner scanner, IReadOnlyList<string> paths,
                                           IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var done = 0;
        await Parallel.ForEachAsync(paths, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Parallelism.Workers), CancellationToken = ct }, async (path, token) =>
        {
            var stamp = PluginScanner.Stamp(path);
            var scan = await scanner.ScanAsync(path, ct: token);
            db.SavePluginScan(scan, stamp);
            progress?.Report(new("Reading plugins", Interlocked.Increment(ref done), paths.Count, Path.GetFileName(path.TrimEnd('\\', '/'))));
        });
    }

    /// <summary>
    /// Reads the parameters of every installed plugin that doesn't have them yet: each plugin is loaded in
    /// the native scanner, four at a time. Stopped part way, it carries on from there next time. A plugin
    /// that can't be loaded keeps why, and is left out after that.
    /// </summary>
    public async Task<(int Read, int Failed)> ReadAllParametersAsync(PluginScanner scanner, IProgress<ScanProgress>? progress = null,
                                                                     CancellationToken ct = default)
    {
        var todo = db.PluginsWithoutParameters();
        int done = 0, read = 0, failed = 0;
        progress?.Report(new("Reading parameters", 0, todo.Count, ""));
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Parallelism.Workers), CancellationToken = ct }, async (plugin, token) =>
        {
            var result = await scanner.ReadParametersAsync(plugin, token);
            db.SaveScannedPlugin(result);
            if (result.Params is null) Interlocked.Increment(ref failed);
            else Interlocked.Increment(ref read);
            progress?.Report(new("Reading parameters", Interlocked.Increment(ref done), todo.Count, plugin.Name));
        });
        return (read, failed);
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
