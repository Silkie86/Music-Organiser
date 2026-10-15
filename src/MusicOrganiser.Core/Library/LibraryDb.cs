using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Library;

/// <summary>
/// The local library: every scanned project, its plugins and samples, your tags
/// and notes, plus indexes of installed plugins and of audio files in your
/// sample folders (used to suggest replacements for missing ones).
/// </summary>
public sealed partial class LibraryDb : IDisposable
{
    const string Schema = """
        PRAGMA journal_mode = WAL;
        PRAGMA foreign_keys = ON;
        CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE IF NOT EXISTS roots (path TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS projects (
            id INTEGER PRIMARY KEY,
            path TEXT UNIQUE NOT NULL COLLATE NOCASE,
            name TEXT NOT NULL, daw TEXT NOT NULL, folder TEXT, size INTEGER, mtime INTEGER,
            tempo REAL, version TEXT, title TEXT, confidence INTEGER,
            can_midi INTEGER DEFAULT 0, can_presets INTEGER DEFAULT 0,
            info TEXT, error TEXT, file_missing INTEGER DEFAULT 0, hardware TEXT DEFAULT '',
            status TEXT DEFAULT 'Idea', rating INTEGER DEFAULT 0, favourite INTEGER DEFAULT 0,
            musical_key TEXT DEFAULT '', notes TEXT DEFAULT '', last_opened INTEGER
        );
        CREATE TABLE IF NOT EXISTS plugins (
            project_id INTEGER REFERENCES projects(id) ON DELETE CASCADE,
            name TEXT, name_key TEXT, vendor TEXT, kind INTEGER, format TEXT, path TEXT, installed INTEGER
        );
        CREATE TABLE IF NOT EXISTS samples (
            project_id INTEGER REFERENCES projects(id) ON DELETE CASCADE,
            path TEXT, found INTEGER
        );
        CREATE TABLE IF NOT EXISTS tags (
            project_id INTEGER REFERENCES projects(id) ON DELETE CASCADE,
            tag TEXT, UNIQUE(project_id, tag)
        );
        CREATE TABLE IF NOT EXISTS installed_plugins (name TEXT, name_key TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS sample_index (path TEXT PRIMARY KEY COLLATE NOCASE, file_name TEXT COLLATE NOCASE, stem_key TEXT);
        CREATE INDEX IF NOT EXISTS ix_plugins_project ON plugins(project_id);
        CREATE INDEX IF NOT EXISTS ix_plugins_key ON plugins(name_key);
        CREATE INDEX IF NOT EXISTS ix_samples_project ON samples(project_id);
        CREATE INDEX IF NOT EXISTS ix_tags_tag ON tags(tag);
        CREATE INDEX IF NOT EXISTS ix_sample_index_name ON sample_index(file_name);
        CREATE INDEX IF NOT EXISTS ix_projects_folder ON projects(folder);
        """;

    readonly SqliteConnection _conn;
    readonly object _lock = new();
    // Reads get connections of their own (the database is in WAL mode, so they never wait for a write): a page reading
    // the library on the window's thread no longer stalls behind a scan's long write or a 357,000-row read.
    readonly ConcurrentBag<SqliteConnection> _readers = [];
    string? _readString;
    volatile bool _disposed;

    public string FilePath { get; }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "MusicOrganiser", "library.db");

    public LibraryDb(string path)
    {
        FilePath = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        _readString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        Exec(Schema);
        Exec(PluginScanSchema);
        AddColumn("projects", "detected_key", "TEXT");
        AddColumn("projects", "created", "INTEGER");
        AddColumn("projects", "note_count", "INTEGER");
        AddColumn("projects", "length_bars", "INTEGER");
        AddColumn("projects", "track_count", "INTEGER");
    }

    /// <summary>A column added after the first release: libraries made before it get it here.</summary>
    void AddColumn(string table, string column, string type)
    {
        var has = Query($"SELECT name FROM pragma_table_info('{table}')", r => (string)r["name"]).Contains(column);
        if (!has) Exec($"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    // Under the lock, so a query still running on a background task (say, Settings looking for the DAW scripts) finishes
    // first rather than having the connection closed under it.
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _conn.Dispose();
            while (_readers.TryTake(out var r)) r.Dispose();
        }
    }

    // ---- plumbing --------------------------------------------------------------

    int Exec(string sql, params (string, object?)[] args)
    {
        lock (_lock)
        {
            using var cmd = Cmd(sql, args);
            return cmd.ExecuteNonQuery();
        }
    }

    SqliteCommand Cmd(string sql, params (string Name, object? Value)[] args) => Cmd(_conn, sql, args);

    static SqliteCommand Cmd(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        // Inside a write on this thread: its own connection, which sees the rows it hasn't committed yet.
        if (Monitor.IsEntered(_lock) || _readString is null)
            lock (_lock) return Read(_conn, sql, map, args);
        var reader = Reader();
        try { return Read(reader, sql, map, args); }
        finally
        {
            if (_disposed) reader.Dispose();
            else _readers.Add(reader);
        }
    }

    static List<T> Read<T>(SqliteConnection conn, string sql, Func<SqliteDataReader, T> map, (string, object?)[] args)
    {
        using var cmd = Cmd(conn, sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    SqliteConnection Reader()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_readers.TryTake(out var reader)) return reader;
        reader = new SqliteConnection(_readString);
        reader.Open();
        return reader;
    }

    static string Str(SqliteDataReader r, string col) => r[col] is string s ? s : "";
    static long Long(SqliteDataReader r, string col) => r[col] is long l ? l : 0;

    /// <summary>Lower-case letters and digits only, minus common suffixes, so "Serum (x64)" matches "Serum".</summary>
    public static string NameKey(string name)
    {
        var n = name.ToLowerInvariant();
        n = NameNoise().Replace(n, "");
        return NonAlnum().Replace(n, "");
    }

    [GeneratedRegex(@"\((x64|x86|64[- ]?bit|32[- ]?bit|vst3?|clap|au|mono|stereo|automap)\)|\b(x64|x86|vst3?|clap)\b|\bvst_(x64|x86)\b|_(x64|x86|64)\b|(?<=[a-z])x64\b")]
    private static partial Regex NameNoise();

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlnum();

    // ---- settings & roots --------------------------------------------------------

    public string? GetSetting(string key) =>
        Query("SELECT value FROM settings WHERE key = $k", r => r["value"] as string, ("$k", key)).FirstOrDefault();

    public void SetSetting(string key, string value) =>
        Exec("INSERT INTO settings VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));

    public IReadOnlyList<string> GetList(string key) =>
        GetSetting(key) is { } json ? JsonSerializer.Deserialize<List<string>>(json) ?? [] : [];

    public void SetList(string key, IEnumerable<string> values) => SetSetting(key, JsonSerializer.Serialize(values.ToList()));

    public IReadOnlyList<string> Roots() => Query("SELECT path FROM roots ORDER BY path", r => (string)r["path"]);
    public void AddRoot(string path) => Exec("INSERT OR IGNORE INTO roots VALUES ($p)", ("$p", path));
    public void RemoveRoot(string path) => Exec("DELETE FROM roots WHERE path = $p", ("$p", path));

    // ---- indexing ----------------------------------------------------------------

    /// <remarks>
    /// A project read before the app knew its key, notes and length counts as changed, so the next scan reads it again.
    /// </remarks>
    public Dictionary<string, (long Mtime, long Size)> KnownFiles() =>
        Query("SELECT path, mtime, size, (detected_key IS NULL OR note_count IS NULL) AS unkeyed FROM projects",
              r => (Path: (string)r["path"], Mtime: Long(r, "unkeyed") == 1 ? -1 : Long(r, "mtime"), Size: Long(r, "size")))
            .ToDictionary(x => x.Path, x => (x.Mtime, x.Size), StringComparer.OrdinalIgnoreCase);

    public sealed record IndexedFile(string Path, long Size, long Mtime, ProjectInfo? Info, string? Error,
                                     IReadOnlyList<(string Path, bool Found)> Samples, IReadOnlyList<string> Hardware)
    {
        /// <summary>The key its notes point to, "" for none, or null when the notes couldn't be read.</summary>
        public string? DetectedKey { get; init; }
        /// <summary>How many notes play in it, or -1 when unknown.</summary>
        public int NoteCount { get; init; } = -1;
        /// <summary>How many bars its notes run, or -1 when unknown.</summary>
        public int LengthBars { get; init; } = -1;
    }

    public void Upsert(IndexedFile f) => UpsertMany([f]);

    /// <summary>Saves several scanned projects in one transaction.</summary>
    public void UpsertMany(IReadOnlyCollection<IndexedFile> files)
    {
        if (files.Count == 0) return;
        var installed = InstalledKeys();
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var f in files) Upsert(tx, f, installed);
            tx.Commit();
        }
    }

    void Upsert(SqliteTransaction tx, IndexedFile f, HashSet<string> installed)
    {
        var info = f.Info;
        bool isNew;
        using (var cmd = Cmd("SELECT COUNT(*) FROM projects WHERE path = $p", ("$p", f.Path)))
        {
            cmd.Transaction = tx;
            isNew = (long)cmd.ExecuteScalar()! == 0;
        }
        var infoJson = info is null ? "{}" : JsonSerializer.Serialize(new StoredInfo(info.Tracks, info.Details, info.Warnings));
        using (var cmd = Cmd("""
            INSERT INTO projects (path, name, daw, folder, size, mtime, tempo, version, title, confidence,
                                  can_midi, can_presets, info, error, file_missing, hardware, detected_key,
                                  created, note_count, length_bars, track_count)
            VALUES ($path, $name, $daw, $folder, $size, $mtime, $tempo, $version, $title, $conf,
                    $midi, $presets, $info, $error, 0, $hw, $key, $created, $notes, $bars, $tracks)
            ON CONFLICT(path) DO UPDATE SET name=excluded.name, daw=excluded.daw, folder=excluded.folder,
                size=excluded.size, mtime=excluded.mtime, tempo=excluded.tempo, version=excluded.version,
                title=excluded.title, confidence=excluded.confidence, can_midi=excluded.can_midi,
                can_presets=excluded.can_presets, info=excluded.info, error=excluded.error, file_missing=0,
                hardware=excluded.hardware, detected_key=excluded.detected_key, created=excluded.created,
                note_count=excluded.note_count, length_bars=excluded.length_bars, track_count=excluded.track_count
            """,
            ("$path", f.Path), ("$name", Path.GetFileNameWithoutExtension(f.Path)),
            ("$daw", info?.Daw ?? Readers.For(f.Path)?.Daw ?? "Unknown"), ("$folder", Path.GetDirectoryName(f.Path)),
            ("$size", f.Size), ("$mtime", f.Mtime), ("$tempo", info?.Tempo), ("$version", info?.Version),
            ("$title", info?.Title), ("$conf", (int)(info?.Confidence ?? Confidence.Catalogue)),
            ("$midi", info?.CanExtractMidi == true ? 1 : 0), ("$presets", info?.CanExtractPresets == true ? 1 : 0),
            ("$info", infoJson), ("$error", f.Error), ("$hw", string.Join(", ", f.Hardware)), ("$key", f.DetectedKey ?? ""),
            ("$created", info?.Created is { } made ? new DateTimeOffset(made).ToUnixTimeSeconds() : null),
            ("$notes", f.NoteCount), ("$bars", f.LengthBars), ("$tracks", info?.Tracks.Count ?? -1)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }

        long id;
        using (var cmd = Cmd("SELECT id FROM projects WHERE path = $p", ("$p", f.Path)))
        {
            cmd.Transaction = tx;
            id = (long)cmd.ExecuteScalar()!;
        }
        foreach (var sql in new[] { "DELETE FROM plugins WHERE project_id = $id", "DELETE FROM samples WHERE project_id = $id" })
        {
            using var cmd = Cmd(sql, ("$id", id));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        foreach (var p in info?.Plugins ?? [])
        {
            var key = NameKey(p.Name);
            using var cmd = Cmd("INSERT INTO plugins VALUES ($id, $n, $k, $v, $kind, $f, $path, $inst)",
                ("$id", id), ("$n", p.Name), ("$k", key), ("$v", p.Vendor), ("$kind", (int)p.Kind),
                ("$f", p.Format), ("$path", p.Path), ("$inst", IsInstalled(p, key, installed) ? 1 : 0));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        foreach (var (path, found) in f.Samples)
        {
            using var cmd = Cmd("INSERT INTO samples VALUES ($id, $p, $f)", ("$id", id), ("$p", path), ("$f", found ? 1 : 0));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        foreach (var hw in f.Hardware)
        {
            using var cmd = Cmd("INSERT OR IGNORE INTO tags VALUES ($id, $t)", ("$id", id), ("$t", hw.ToLowerInvariant()));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        if (isNew) InheritFromEarlierSave(tx, id, f.Path);
    }

    /// <summary>
    /// A new numbered save (Song_12 next to Song_11) takes over the status, rating, notes and
    /// tags you gave the song, so they stay with it as it moves on.
    /// </summary>
    void InheritFromEarlierSave(SqliteTransaction tx, long id, string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var (song, number) = SongVersions.Split(name);
        var folder = Path.GetDirectoryName(path);
        long? from = null;
        string hardware = "";
        var best = int.MinValue;
        using (var cmd = Cmd("SELECT id, name, daw, hardware FROM projects WHERE folder = $f AND id != $id",
                             ("$f", folder), ("$id", id)))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            var daw = Readers.For(path)?.Daw;
            while (r.Read())
            {
                var (s, n) = SongVersions.Split((string)r["name"]);
                if (!s.Equals(song, StringComparison.OrdinalIgnoreCase) || n >= number || n <= best) continue;
                if (daw is not null && !daw.Equals(Str(r, "daw"), StringComparison.Ordinal)) continue;
                best = n;
                from = (long)r["id"];
                hardware = Str(r, "hardware");
            }
        }
        if (from is null) return;

        var autoTags = hardware.Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(h => h.ToLowerInvariant()).ToHashSet();
        using (var cmd = Cmd("""
            UPDATE projects SET (status, rating, favourite, musical_key, notes) =
                (SELECT status, rating, favourite, musical_key, notes FROM projects WHERE id = $from)
            WHERE id = $id
            """, ("$id", id), ("$from", from)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        var tags = new List<string>();
        using (var cmd = Cmd("SELECT tag FROM tags WHERE project_id = $from", ("$from", from)))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            while (r.Read()) tags.Add((string)r["tag"]);
        }
        foreach (var t in tags.Where(t => !autoTags.Contains(t)))
        {
            using var cmd = Cmd("INSERT OR IGNORE INTO tags VALUES ($id, $t)", ("$id", id), ("$t", t));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
    }

    sealed record StoredInfo(List<string> Tracks, Dictionary<string, string> Details, List<string> Warnings);

    static bool IsInstalled(PluginRef p, string key, HashSet<string> installed)
    {
        if (p.Format.Equals("Native", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Path.Length > 0 && (File.Exists(p.Path) || Directory.Exists(p.Path))) return true;
        if (PluginCatalog.IsFlStudioBundled(p.Name, p.Vendor, p.Path)) return true;
        if (installed.Count == 0) return true;   // no plugin scan yet: don't cry wolf
        if (installed.Contains(key)) return true;
        if (PluginCatalog.IsWaves(p.Name, p.Path) && installed.Any(k => k.StartsWith("waveshell"))) return true;
        // Projects often store "Vendor Name" while the file is just "Name", or the reverse.
        return installed.Any(k => k.Length >= 4 && (key.EndsWith(k) || k.EndsWith(key) && key.Length >= 4));
    }

    /// <summary>
    /// Flags projects under your folders that the scan didn't find. Ones the scan now leaves out on
    /// purpose (<paramref name="ignored"/>, such as autosave copies) are dropped from the library instead.
    /// </summary>
    public void MarkMissingFiles(IReadOnlySet<string> present, IReadOnlyList<string> roots, Func<string, bool>? ignored = null)
    {
        var rows = Query("SELECT id, path FROM projects WHERE file_missing = 0", r => (Id: (long)r["id"], Path: (string)r["path"]));
        foreach (var (id, path) in rows)
        {
            if (present.Contains(path) || !roots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase))) continue;
            if (ignored?.Invoke(path) == true) RemoveProject(id);
            else Exec("UPDATE projects SET file_missing = 1 WHERE id = $id", ("$id", id));
        }
    }

    public void RemoveProject(long id) => Exec("DELETE FROM projects WHERE id = $id", ("$id", id));
    public void RemoveProject(string path) => Exec("DELETE FROM projects WHERE path = $p", ("$p", path));

    // ---- installed plugins & sample index ----------------------------------------------

    public HashSet<string> InstalledKeys() =>
        Query("SELECT name_key FROM installed_plugins", r => (string)r["name_key"]).ToHashSet();

    public HashSet<string> InstalledNames() =>
        Query("SELECT name FROM installed_plugins", r => (string)r["name"]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void ReplaceInstalledPlugins(IEnumerable<string> names)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using (var del = Cmd("DELETE FROM installed_plugins")) { del.Transaction = tx; del.ExecuteNonQuery(); }
            foreach (var n in names)
            {
                using var cmd = Cmd("INSERT OR IGNORE INTO installed_plugins VALUES ($n, $k)", ("$n", n), ("$k", NameKey(n)));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        RefreshInstalledFlags();
    }

    /// <summary>
    /// Re-check every project plugin against the installed list. Only the rows whose answer changed are written, and in
    /// small batches: a library of 16,000 projects has hundreds of thousands of plugin rows, and holding the database for
    /// all of them at once kept the window waiting ("Not responding").
    /// </summary>
    public void RefreshInstalledFlags()
    {
        var installed = InstalledKeys();
        var rows = Query("SELECT rowid, name, name_key, vendor, kind, format, path, installed FROM plugins",
            r => (RowId: (long)r["rowid"], Key: NameKey(Str(r, "name")), Was: Long(r, "installed") != 0,
                  Ref: new PluginRef(Str(r, "name"), Str(r, "vendor"), (PluginKind)Long(r, "kind"), Str(r, "format"), Str(r, "path"))));
        var changed = rows.Select(row => (row.RowId, Now: IsInstalled(row.Ref, row.Key, installed), row.Was))
                          .Where(row => row.Now != row.Was)
                          .Select(row => (row.RowId, row.Now))
                          .ToList();
        InBatches(changed, "UPDATE plugins SET installed = $i WHERE rowid = $r", ["$i", "$r"],
                  (row, p) => { p[0].Value = row.Now ? 1 : 0; p[1].Value = row.RowId; });
    }

    /// <summary>
    /// The sample folders' files, in place of the last list. Written in batches with the database let go in between,
    /// so the window can keep reading it while a big library (350,000 files) goes in.
    /// </summary>
    public void ReplaceSampleIndex(IEnumerable<string> files)
    {
        Exec("DELETE FROM sample_index");
        InBatches(files, "INSERT OR IGNORE INTO sample_index VALUES ($p, $n, $k)", ["$p", "$n", "$k"], (f, p) =>
        {
            p[0].Value = f;
            p[1].Value = Path.GetFileName(f);
            p[2].Value = NameKey(Path.GetFileNameWithoutExtension(f));
        });
    }

    /// <summary>Runs one statement for each item, a few thousand to a transaction, letting go of the database between them.</summary>
    void InBatches<T>(IEnumerable<T> items, string sql, string[] names, Action<T, SqliteParameter[]> fill, int size = 2000)
    {
        using var e = items.GetEnumerator();
        var more = e.MoveNext();
        while (more)
        {
            lock (_lock)
            {
                using var tx = _conn.BeginTransaction();
                using var cmd = Cmd(sql);
                cmd.Transaction = tx;
                var p = names.Select(n => cmd.Parameters.Add(new SqliteParameter(n, null))).ToArray();
                for (var n = 0; more && n < size; n++, more = e.MoveNext())
                {
                    fill(e.Current, p);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
    }

    public int SampleIndexCount() => Query("SELECT COUNT(*) AS n FROM sample_index", r => (int)(long)r["n"]).First();

    public IReadOnlyList<(string Path, string StemKey)> SampleIndex() =>
        Query("SELECT path, stem_key FROM sample_index", r => ((string)r["path"], Str(r, "stem_key")));

    public IReadOnlyList<string> SamplesNamed(string fileName) =>
        Query("SELECT path FROM sample_index WHERE file_name = $n", r => (string)r["path"], ("$n", fileName));

    // ---- queries -----------------------------------------------------------------

    public IReadOnlyList<ProjectSummary> Projects() => Summaries("");

    /// <summary>A DAW's projects with this file name (without the extension, ignoring case), newest first, leaving out moved files.</summary>
    public IReadOnlyList<ProjectSummary> ProjectsNamed(string daw, string name) =>
        Summaries("WHERE p.daw = $daw AND p.name = $name COLLATE NOCASE AND p.file_missing = 0", ("$daw", daw), ("$name", name.Trim()));

    IReadOnlyList<ProjectSummary> Summaries(string where, params (string, object?)[] args) => Query($"""
        SELECT p.*,
          (SELECT COUNT(*) FROM plugins WHERE project_id = p.id) AS plugin_count,
          (SELECT COUNT(*) FROM plugins WHERE project_id = p.id AND installed = 0) AS missing_plugins,
          (SELECT COUNT(*) FROM samples WHERE project_id = p.id) AS sample_count,
          (SELECT COUNT(*) FROM samples WHERE project_id = p.id AND found = 0) AS missing_samples,
          (SELECT group_concat(tag, char(31)) FROM tags WHERE project_id = p.id) AS tag_list,
          (SELECT group_concat(name, char(31)) FROM plugins WHERE project_id = p.id) AS plugin_list
        FROM projects p {where} ORDER BY p.mtime DESC
        """, MapSummary, args);

    ProjectSummary MapSummary(SqliteDataReader r) => new()
    {
        Id = (long)r["id"], Path = (string)r["path"], Name = (string)r["name"], Daw = (string)r["daw"],
        Folder = Str(r, "folder"), Size = Long(r, "size"),
        Modified = DateTimeOffset.FromUnixTimeSeconds(Long(r, "mtime")).LocalDateTime, Mtime = Long(r, "mtime"),
        Tempo = r["tempo"] is double t ? t : null, Version = Str(r, "version"),
        Confidence = (Confidence)Long(r, "confidence"), FileMissing = Long(r, "file_missing") == 1,
        Error = r["error"] as string,
        LastOpened = r["last_opened"] is long lo ? DateTimeOffset.FromUnixTimeSeconds(lo).LocalDateTime : null,
        PluginCount = HasCol(r, "plugin_count") ? (int)Long(r, "plugin_count") : 0,
        MissingPlugins = HasCol(r, "missing_plugins") ? (int)Long(r, "missing_plugins") : 0,
        SampleCount = HasCol(r, "sample_count") ? (int)Long(r, "sample_count") : 0,
        MissingSamples = HasCol(r, "missing_samples") ? (int)Long(r, "missing_samples") : 0,
        Tags = HasCol(r, "tag_list") ? Split(Str(r, "tag_list")).Order().ToList() : [],
        PluginNames = HasCol(r, "plugin_list") ? Split(Str(r, "plugin_list")) : [],
        Hardware = Str(r, "hardware").Split(", ", StringSplitOptions.RemoveEmptyEntries),
        Status = Str(r, "status") is { Length: > 0 } s ? s : "Idea",
        Rating = (int)Long(r, "rating"), Favourite = Long(r, "favourite") == 1,
        MusicalKey = Str(r, "musical_key"), Notes = Str(r, "notes"),
        DetectedKey = HasCol(r, "detected_key") ? Str(r, "detected_key") : "",
        Created = HasCol(r, "created") && r["created"] is long made ? DateTimeOffset.FromUnixTimeSeconds(made).LocalDateTime : null,
        NoteCount = HasCol(r, "note_count") && r["note_count"] is long notes ? (int)notes : -1,
        LengthBars = HasCol(r, "length_bars") && r["length_bars"] is long bars ? (int)bars : -1,
        TrackCount = HasCol(r, "track_count") && r["track_count"] is long tracks ? (int)tracks : -1,
    };

    static bool HasCol(SqliteDataReader r, string name)
    {
        for (var i = 0; i < r.FieldCount; i++) if (r.GetName(i) == name) return true;
        return false;
    }

    static List<string> Split(string s) => s.Length == 0 ? [] : s.Split('\u001f').ToList();

    public ProjectDetail? Project(long id)
    {
        var summary = Summaries("WHERE p.id = $id", ("$id", id)).FirstOrDefault();
        if (summary is null) return null;
        var row = Query("SELECT title, info, can_midi, can_presets FROM projects WHERE id = $id",
            r => (Title: Str(r, "title"), Info: Str(r, "info"), Midi: Long(r, "can_midi") == 1, Presets: Long(r, "can_presets") == 1),
            ("$id", id)).First();
        StoredInfo? info = null;
        try { info = JsonSerializer.Deserialize<StoredInfo>(row.Info); } catch (JsonException) { }
        return new ProjectDetail
        {
            Summary = summary, Title = row.Title, CanExtractMidi = row.Midi, CanExtractPresets = row.Presets,
            Tracks = info?.Tracks ?? [], Details = info?.Details ?? new(), Warnings = info?.Warnings ?? [],
            Plugins = Query("SELECT * FROM plugins WHERE project_id = $id ORDER BY kind, name",
                r => new ProjectPlugin(Str(r, "name"), Str(r, "vendor"), (PluginKind)Long(r, "kind"), Str(r, "format"),
                                       Str(r, "path"), Long(r, "installed") == 1), ("$id", id)),
            Samples = Query("SELECT path, found FROM samples WHERE project_id = $id ORDER BY found, path",
                r => new ProjectSample(Str(r, "path"), Long(r, "found") == 1), ("$id", id)),
        };
    }

    public void SaveOrganisation(ProjectSummary p) => Exec("""
        UPDATE projects SET status = $s, rating = $r, favourite = $f, musical_key = $k, notes = $n WHERE id = $id
        """, ("$s", p.Status), ("$r", p.Rating), ("$f", p.Favourite ? 1 : 0), ("$k", p.MusicalKey), ("$n", p.Notes), ("$id", p.Id));

    public void SetTags(long id, IEnumerable<string> tags)
    {
        var clean = tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using (var del = Cmd("DELETE FROM tags WHERE project_id = $id", ("$id", id))) { del.Transaction = tx; del.ExecuteNonQuery(); }
            foreach (var t in clean)
            {
                using var cmd = Cmd("INSERT INTO tags VALUES ($id, $t)", ("$id", id), ("$t", t));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void MarkOpened(long id) =>
        Exec("UPDATE projects SET last_opened = $t WHERE id = $id", ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$id", id));

    public IReadOnlyList<(string Tag, int Count)> AllTags() =>
        Query("SELECT tag, COUNT(*) AS n FROM tags GROUP BY tag ORDER BY n DESC, tag", r => ((string)r["tag"], (int)(long)r["n"]));

    public IReadOnlyList<PluginUsage> PluginUsage() => Query("""
        WITH live AS (
            SELECT pl.*, p.mtime AS saved, p.daw AS daw FROM plugins pl JOIN projects p ON p.id = pl.project_id WHERE p.file_missing = 0),
        missing AS (SELECT project_id, COUNT(DISTINCT name_key) AS n FROM live WHERE installed = 0 GROUP BY project_id)
        SELECT MIN(l.name) AS name, MAX(l.vendor) AS vendor, MAX(l.format) AS format,
               COUNT(DISTINCT l.project_id) AS n, group_concat(DISTINCT l.daw) AS daws, MIN(l.installed) AS installed,
               COUNT(DISTINCT CASE WHEN l.installed = 0 AND m.n = 1 THEN l.project_id END) AS unlocks,
               MAX(l.saved) AS last, MAX(l.kind) AS kind
        FROM live l LEFT JOIN missing m ON m.project_id = l.project_id
        GROUP BY l.name_key ORDER BY n DESC, name
        """, r => new PluginUsage(Str(r, "name"), Str(r, "vendor"), Str(r, "format"), (int)Long(r, "n"), Str(r, "daws").Replace(",", ", "), Long(r, "installed") == 1,
                                  (int)Long(r, "unlocks"), Long(r, "last") is > 0 and var t ? DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime : null,
                                  (PluginKind)Long(r, "kind")));

    /// <summary>Each project that's missing a plugin, with the plugins it's missing (by name key).</summary>
    public IReadOnlyDictionary<long, HashSet<string>> MissingPluginsByProject() =>
        Query("""
            SELECT DISTINCT pl.project_id, pl.name_key FROM plugins pl JOIN projects p ON p.id = pl.project_id
            WHERE p.file_missing = 0 AND pl.installed = 0
            """, r => ((long)r["project_id"], Str(r, "name_key")))
            .GroupBy(t => t.Item1).ToDictionary(g => g.Key, g => g.Select(t => t.Item2).ToHashSet());

    /// <summary>The projects using a plugin, most recently saved first.</summary>
    public IReadOnlyList<PluginProject> ProjectsWithPlugin(string name) => Query("""
        SELECT DISTINCT p.id, p.name, p.daw, p.mtime FROM plugins pl JOIN projects p ON p.id = pl.project_id
        WHERE pl.name_key = $k AND p.file_missing = 0 ORDER BY p.mtime DESC
        """, r => new PluginProject((long)r["id"], Str(r, "name"), Str(r, "daw"), DateTimeOffset.FromUnixTimeSeconds(Long(r, "mtime")).LocalDateTime),
        ("$k", NameKey(name)));

    public IReadOnlyList<SampleUsage> SampleUsage() => Query("""
        SELECT s.path, COUNT(DISTINCT s.project_id) AS n, MIN(s.found) AS found
        FROM samples s JOIN projects p ON p.id = s.project_id
        WHERE p.file_missing = 0
        GROUP BY s.path COLLATE NOCASE ORDER BY found, n DESC, s.path LIMIT 20000
        """, r => new SampleUsage(Str(r, "path"), (int)Long(r, "n"), Long(r, "found") == 1));

    /// <summary>Each sample's projects, and how many of them were saved since <paramref name="recentSince"/>.</summary>
    public IReadOnlyList<(string Path, int Projects, int Recent)> SampleUsageByAge(DateTime recentSince) => Query("""
        SELECT s.path, COUNT(DISTINCT s.project_id) AS n, COUNT(DISTINCT CASE WHEN p.mtime >= $c THEN s.project_id END) AS recent
        FROM samples s JOIN projects p ON p.id = s.project_id
        WHERE p.file_missing = 0
        GROUP BY s.path COLLATE NOCASE
        """, r => (Str(r, "path"), (int)Long(r, "n"), (int)Long(r, "recent")), ("$c", new DateTimeOffset(recentSince).ToUnixTimeSeconds()));

    /// <summary>Projects using a plugin (by name) or a sample (by path).</summary>
    public IReadOnlyList<long> ProjectsUsingPlugin(string name) =>
        Query("SELECT DISTINCT project_id FROM plugins WHERE name_key = $k", r => (long)r["project_id"], ("$k", NameKey(name)));

    public IReadOnlyList<long> ProjectsUsingSample(string path) =>
        Query("SELECT DISTINCT project_id FROM samples WHERE path = $p COLLATE NOCASE", r => (long)r["project_id"], ("$p", path));
}
