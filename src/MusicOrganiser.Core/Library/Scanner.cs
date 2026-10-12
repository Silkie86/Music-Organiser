using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Library;

public sealed record ScanProgress(string Stage, int Done, int Total, string Current);

/// <summary>Finds projects in your folders and keeps the library up to date.</summary>
public sealed class Scanner(LibraryDb db)
{
    public const string PluginFoldersKey = "plugin_folders";
    public const string SampleFoldersKey = "sample_folders";

    /// <summary>
    /// Bump when a reader starts storing different data, so existing libraries re-read every
    /// project once instead of keeping what the older reader found. 2: FL Studio Audio Units.
    /// </summary>
    public const string IndexVersion = "2";
    const string IndexVersionKey = "index_version";

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

    public static IEnumerable<string> FindProjects(string root)
    {
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (Readers.For(f) is not null && !name.StartsWith("._") && !IsAutosaveFile(name)) yield return f;
            }
            foreach (var d in dirs)
                if (!SkipDir(Path.GetFileName(d), Path.GetFileName(dir.TrimEnd('\\', '/')))) stack.Push(d);
        }
    }

    public static bool SampleFound(string sample, string projectPath)
    {
        if (sample.StartsWith('%')) return true;   // FL Studio factory content, e.g. %FLStudioFactoryData%
        var projectDir = Path.GetDirectoryName(projectPath) ?? "";
        try
        {
            if (Path.IsPathRooted(sample) && File.Exists(sample)) return true;
            if (!Path.IsPathRooted(sample) && File.Exists(Path.Combine(projectDir, sample))) return true;
            // Collected / consolidated copies next to the project.
            var name = sample.Split('\\', '/')[^1];
            foreach (var sub in new[] { "", "samples", "recordings", "Media", "Audio", "audio" })
                if (File.Exists(Path.Combine(projectDir, sub, name))) return true;
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { }
        return false;
    }

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
            return new(path, fi.Length, mtime, info, null, samples, hardware);
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

        full |= db.GetSetting(IndexVersionKey) != IndexVersion;
        var roots = db.Roots().Where(Directory.Exists).ToList();
        progress?.Report(new("Finding projects", 0, 0, ""));
        var files = roots.SelectMany(FindProjects).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var known = db.KnownFiles();
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = files[i];
            progress?.Report(new("Reading projects", i, files.Count, f));
            try
            {
                var fi = new FileInfo(f);
                var mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                if (full || !known.TryGetValue(f, out var k) || k.Mtime != mtime || k.Size != fi.Length)
                    db.Upsert(Analyse(f));
            }
            catch (IOException) { }
        }
        db.MarkMissingFiles(files.ToHashSet(StringComparer.OrdinalIgnoreCase), roots, f => IsIgnored(f, roots));
        db.SetSetting(IndexVersionKey, IndexVersion);
        progress?.Report(new("Done", files.Count, files.Count, ""));
    }

    /// <summary>Refresh the list of plugins installed on this PC.</summary>
    public int ScanInstalledPlugins()
    {
        var folders = db.GetList(PluginFoldersKey) is { Count: > 0 } custom ? custom : PluginCatalog.DefaultFolders().ToList();
        var files = PluginCatalog.ScanFiles(folders);
        var names = new HashSet<string>(PluginCatalog.AlwaysKnown, StringComparer.OrdinalIgnoreCase);
        names.UnionWith(files.Select(f => f.Name));
        db.ReplaceInstalledPluginFiles(files);
        db.ReplaceInstalledPlugins(names);
        return names.Count;
    }

    /// <summary>Index audio files in your sample folders, so missing samples can be matched.</summary>
    public int ScanSampleFolders(IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var files = new List<string>();
        foreach (var root in db.GetList(SampleFoldersKey).Where(Directory.Exists))
        {
            var stack = new Stack<string>([root]);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                try
                {
                    files.AddRange(Directory.GetFiles(dir).Where(TextScan.IsAudioPath));
                    foreach (var d in Directory.GetDirectories(dir))
                        if (!Path.GetFileName(d).StartsWith('.')) stack.Push(d);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                if (files.Count % 2000 == 0) progress?.Report(new("Indexing samples", files.Count, 0, dir));
            }
        }
        db.ReplaceSampleIndex(files);
        return files.Count;
    }
}
