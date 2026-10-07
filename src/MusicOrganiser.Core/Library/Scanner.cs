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
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith('.') || SkipDirs.Contains(name) || AutosaveDirs.Contains(name)) continue;
                stack.Push(d);
            }
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
        db.MarkMissingFiles(files.ToHashSet(StringComparer.OrdinalIgnoreCase), roots, f => IsAutosaveFile(Path.GetFileName(f)));
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
