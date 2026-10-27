using System.Text.Json;
using MusicOrganiser.Core.Space;

namespace MusicOrganiser.Core.Library;

// How much room plugins and songs take on disk (Plugins page's Size, Projects page's Space). Measured in the background
// and kept here, so the pages show sizes straight away and nothing is measured twice.
public sealed partial class LibraryDb
{
    const string SpaceSchema = """
        CREATE TABLE IF NOT EXISTS plugin_space (
            name_key TEXT PRIMARY KEY, name TEXT, vendor TEXT, folders TEXT, measured INTEGER
        );
        CREATE TABLE IF NOT EXISTS folder_space (
            folder TEXT PRIMARY KEY COLLATE NOCASE, bytes INTEGER, measured INTEGER
        );
        """;

    /// <summary>Setting: how many years without a project save makes a plugin "unused" (Plugins page).</summary>
    public const string UnusedYearsKey = "space_unused_years";
    /// <summary>Setting: how many of a song's newest autosaves Compact keeps.</summary>
    public const string KeepBackupsKey = "space_keep_backups";
    /// <summary>Setting: how long without a save ends a sitting, for thinning older saves (0: don't thin).</summary>
    public const string SittingHoursKey = "space_sitting_hours";
    /// <summary>Settings per plugin: folders you added to it, and guessed folders you said aren't its.</summary>
    public static string AddedFoldersKey(string key) => $"plugin_space_added:{key}";
    public static string RuledOutKey(string key) => $"plugin_space_not:{key}";

    /// <summary>Setting: the spare plugin copies found when plugins were last measured (JSON).</summary>
    public const string SpareCopiesKey = "space_spare_copies";

    /// <summary>Each plugin your projects use, by name key (devices of a DAW's own aside), with how many projects use each format.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> PluginFormatsUsed() =>
        Query("""
            SELECT pl.name_key, COALESCE(pl.format, '') AS format, COUNT(DISTINCT pl.project_id) AS n
            FROM plugins pl JOIN projects p ON p.id = pl.project_id
            WHERE p.file_missing = 0 AND COALESCE(pl.format, '') <> 'Native'
            GROUP BY pl.name_key, COALESCE(pl.format, '')
            """, r => (Key: Str(r, "name_key"), Format: Str(r, "format"), N: (int)Long(r, "n")))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, int>)g.ToDictionary(x => x.Format, x => x.N, StringComparer.OrdinalIgnoreCase));

    /// <summary>The spare copies found at the last measure (see <see cref="SpareCopies"/>).</summary>
    public IReadOnlyList<SpareCopy> SpareCopyList() =>
        GetSetting(SpareCopiesKey) is { Length: > 0 } json ? JsonSerializer.Deserialize<List<SpareCopy>>(json) ?? [] : [];

    public void SaveSpareCopies(IEnumerable<SpareCopy> copies) => SetSetting(SpareCopiesKey, JsonSerializer.Serialize(copies.ToList()));

    /// <summary>
    /// Whether spare copies have been looked for at all (an empty list once looked counts). Plugins measured before the
    /// look existed, or a measure stopped part way, leave it unknown until the next full measure (F12 note, 27 Oct).
    /// </summary>
    public bool SpareCopiesLookedFor() => GetSetting(SpareCopiesKey) is { Length: > 0 };

    /// <summary>Setting: plugin folders moved to another drive (see <see cref="FolderMove"/>).</summary>
    public const string MovedFoldersKey = "space_moved_folders";

    public IReadOnlyList<MovedFolder> MovedFolders() =>
        GetSetting(MovedFoldersKey) is { Length: > 0 } json ? JsonSerializer.Deserialize<List<MovedFolder>>(json) ?? [] : [];

    public void SaveMovedFolders(IEnumerable<MovedFolder> moved) => SetSetting(MovedFoldersKey, JsonSerializer.Serialize(moved.ToList()));

    /// <summary>Setting: where the moved folders' home was before it moved to another drive (deleted when you say).</summary>
    public const string OldHomesKey = "space_old_homes";

    public IReadOnlyList<OldHome> OldHomes() =>
        GetSetting(OldHomesKey) is { Length: > 0 } json ? JsonSerializer.Deserialize<List<OldHome>>(json) ?? [] : [];

    public void SaveOldHomes(IEnumerable<OldHome> homes) => SetSetting(OldHomesKey, JsonSerializer.Serialize(homes.ToList()));

    long _spaceRevision;
    /// <summary>Goes up when sizes are measured again, so the pages show the new ones.</summary>
    public long SpaceRevision => Interlocked.Read(ref _spaceRevision);

    sealed record FolderRow(string Path, SpaceKind Kind, long Bytes, List<string> SharedWith, bool Added);

    /// <summary>Every plugin's measured room on disk, by name key.</summary>
    public IReadOnlyDictionary<string, PluginSpaceInfo> PluginSpaces() =>
        Query("SELECT * FROM plugin_space", r => new PluginSpaceInfo(Str(r, "name_key"), Str(r, "name"), Str(r, "vendor"),
                (JsonSerializer.Deserialize<List<FolderRow>>(Str(r, "folders")) ?? [])
                    .Select(f => new SpaceFolder(f.Path, f.Kind, f.Bytes, f.SharedWith, f.Added)).ToList(),
                DateTimeOffset.FromUnixTimeSeconds(Long(r, "measured")).LocalDateTime))
            .ToDictionary(p => p.Key);

    /// <summary>Replaces what's stored with a new measurement; <paramref name="all"/> drops plugins no longer installed.</summary>
    public void SavePluginSpaces(IReadOnlyList<PluginSpaceInfo> spaces, bool all)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            if (all) using (var del = Cmd("DELETE FROM plugin_space")) { del.Transaction = tx; del.ExecuteNonQuery(); }
            foreach (var s in spaces)
            {
                var folders = JsonSerializer.Serialize(s.Folders.Select(f => new FolderRow(f.Path, f.Kind, f.Bytes, f.SharedWith.ToList(), f.Added)));
                using var cmd = Cmd("""
                    INSERT INTO plugin_space VALUES ($k, $n, $v, $f, $m)
                    ON CONFLICT(name_key) DO UPDATE SET name = $n, vendor = $v, folders = $f, measured = $m
                    """, ("$k", s.Key), ("$n", s.Name), ("$v", s.Vendor), ("$f", folders), ("$m", new DateTimeOffset(s.Measured).ToUnixTimeSeconds()));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        Interlocked.Increment(ref _spaceRevision);
    }

    /// <summary>When the plugins were last measured, or null if never.</summary>
    public DateTime? PluginSpaceMeasured() =>
        Query("SELECT MIN(measured) AS m FROM plugin_space", r => Long(r, "m")).FirstOrDefault() is > 0 and var t
            ? DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime : null;

    /// <summary>Each song folder's measured size and when it was measured.</summary>
    public IReadOnlyDictionary<string, (long Bytes, DateTime Measured)> FolderSpaces() =>
        Query("SELECT folder, bytes, measured FROM folder_space", r => (Str(r, "folder"), Long(r, "bytes"), Long(r, "measured")))
            .ToDictionary(x => x.Item1, x => (x.Item2, DateTimeOffset.FromUnixTimeSeconds(x.Item3).LocalDateTime), StringComparer.OrdinalIgnoreCase);

    public void SaveFolderSpaces(IEnumerable<(string Folder, long Bytes)> folders)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var (folder, bytes) in folders)
            {
                using var cmd = Cmd("""
                    INSERT INTO folder_space VALUES ($f, $b, $m) ON CONFLICT(folder) DO UPDATE SET bytes = $b, measured = $m
                    """, ("$f", folder), ("$b", bytes), ("$m", now));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        Interlocked.Increment(ref _spaceRevision);
    }

    /// <summary>The sample paths these projects use, as the projects say.</summary>
    public HashSet<string> SamplesOf(IEnumerable<long> projectIds)
    {
        var ids = string.Join(",", projectIds.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (ids.Length == 0) return [];
        return Query($"SELECT DISTINCT path FROM samples WHERE project_id IN ({ids})", r => Str(r, "path"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Which of these file names ("take 2.wav") any project's samples have. Projects store paths as they were on the PC
    /// that saved them, so only the name is compared. Reads the whole samples table once (call it in the background).
    /// </summary>
    public HashSet<string> SampleNamesUsed(IReadOnlyCollection<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return used;
        Query("SELECT path FROM samples", r =>
        {
            var path = Str(r, "path");
            if (path[(path.LastIndexOfAny(['\\', '/']) + 1)..] is var name && wanted.Contains(name)) used.Add(name);
            return 0;
        });
        return used;
    }

    /// <summary>The FL Studio projects with one of these names (the file's name without .flp), anywhere in the library.</summary>
    public IReadOnlyList<string> FlProjectsNamed(IEnumerable<string> names) =>
        names.Distinct(StringComparer.OrdinalIgnoreCase)
             .SelectMany(n => Query("SELECT path FROM projects WHERE name = $n COLLATE NOCASE AND path LIKE '%.flp'", r => Str(r, "path"), ("$n", n)))
             .ToList();

    /// <summary>
    /// A sample some project uses that's inside this folder, if any: Clean up never removes a folder holding one (your
    /// rule that samples are never moved or deleted; a plugin's library is only fair game when no project uses it).
    /// </summary>
    /// <remarks>
    /// The same folder under another drive letter counts too: a project saved when the drive was F: still finds its
    /// samples now it's K: (see <see cref="SampleLocator"/>), so they hold the folder just the same.
    /// </remarks>
    public string? SampleInside(string folder)
    {
        var f = folder.Replace('/', '\\').TrimEnd('\\');
        var g = folder.Replace('\\', '/').TrimEnd('/');
        var drive = f.Length >= 2 && f[1] == ':' && char.IsAsciiLetter(f[0]);
        string Pattern(string path) => drive ? "_:" + Like(path[2..]) : Like(path);   // "_" stands for any one letter
        return Query("""
            SELECT path FROM samples WHERE path LIKE $a ESCAPE '|' OR path LIKE $b ESCAPE '|' LIMIT 1
            """, r => Str(r, "path"), ("$a", Pattern(f) + "\\%"), ("$b", Pattern(g) + "/%")).FirstOrDefault();

        static string Like(string s) => s.Replace("|", "||").Replace("%", "|%").Replace("_", "|_");
    }

    /// <summary>
    /// For each folder that holds one, a sample some project uses whose file is in it under another path: a file of the
    /// same name, in a folder of the same name. Projects store paths as they were on the PC that saved them
    /// ("C:\Users\OldPC\…", a drive with another letter), so a sample found here since doesn't start with the folder's
    /// path (see <see cref="SampleInside"/>). Walks the folders and reads the samples table once: call it in the background.
    /// A OneDrive file that's only online here counts too: it's still the sample, and removing the folder would remove it.
    /// </summary>
    public IReadOnlyDictionary<string, string> SamplesNamedIn(IReadOnlyList<string> folders, CancellationToken ct = default)
    {
        // "Kicks/kick 1.wav" → the folders holding it.
        var tails = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders.Where(Directory.Exists))
        {
            var n = 0;
            foreach (var file in DiskSpace.AllFiles(folder, (ref System.IO.Enumeration.FileSystemEntry e) => e.ToFullPath()))
            {
                if ((++n & 1023) == 0) ct.ThrowIfCancellationRequested();
                if (DiskSpace.IsSound(file, 0) && Tail(file) is { } tail) (tails.TryGetValue(tail, out var l) ? l : tails[tail] = []).Add(folder);
            }
        }
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tails.Count == 0) return found;
        var rows = 0;
        Query("SELECT path FROM samples", r =>
        {
            if ((++rows & 4095) == 0) ct.ThrowIfCancellationRequested();
            var path = Str(r, "path");
            if (Tail(path) is { } tail && tails.TryGetValue(tail, out var holding))
                foreach (var f in holding) found.TryAdd(f, path);
            return 0;
        });
        return found;

        static string? Tail(string path) =>
            path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } parts ? $"{parts[^2]}/{parts[^1]}" : null;
    }

    /// <summary>Each scanned plugin's maker, by name key, as the plugin scanner read it.</summary>
    public IReadOnlyDictionary<string, string> ScannedVendors() =>
        Query("SELECT name_key, MAX(vendor) AS vendor FROM scanned_plugins WHERE vendor <> '' GROUP BY name_key",
              r => (Str(r, "name_key"), Str(r, "vendor")))
            .ToDictionary(x => x.Item1, x => x.Item2);
}
