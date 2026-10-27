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
        CREATE TABLE IF NOT EXISTS song_choices (kind TEXT, a TEXT, b TEXT, PRIMARY KEY (kind, a));
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

    long _projectsRevision, _pluginsRevision, _sampleIndexRevision;
    /// <summary>
    /// Goes up whenever a scan changes the projects (added, re-read, gone, a plugin's installed answer). The pages compare
    /// it after a scan and skip reading everything again when nothing changed, which is most scans.
    /// </summary>
    public long ProjectsRevision => Interlocked.Read(ref _projectsRevision);
    /// <summary>Goes up when the installed plugins or what the plugin scanner found in them change.</summary>
    public long PluginsRevision => Interlocked.Read(ref _pluginsRevision);
    /// <summary>Goes up when the files in your sample folders change.</summary>
    public long SampleIndexRevision => Interlocked.Read(ref _sampleIndexRevision);
    void ProjectsChanged() => Interlocked.Increment(ref _projectsRevision);
    void PluginsChanged() => Interlocked.Increment(ref _pluginsRevision);

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
        Exec(SpaceSchema);
        AddColumn("plugins", "plugin_id", "TEXT NOT NULL DEFAULT ''");
        AddColumn("projects", "detected_key", "TEXT");
        AddColumn("projects", "created", "INTEGER");
        AddColumn("projects", "note_count", "INTEGER");
        AddColumn("projects", "length_bars", "INTEGER");
        AddColumn("projects", "track_count", "INTEGER");
        AddColumn("samples", "unplayed", "INTEGER NOT NULL DEFAULT 0");
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

    /// <summary>Your changes to which saves are one song (see <see cref="Library.SongChoices"/>).</summary>
    public SongChoices SongChoices() => _songChoices ??= ReadSongChoices(null);

    // Kept until you change them, since a scan asks for every new save.
    volatile SongChoices? _songChoices;

    SongChoices ReadSongChoices(SqliteTransaction? tx)
    {
        var rows = new List<(string Kind, string A, string B)>();
        using (var cmd = Cmd("SELECT kind, a, b FROM song_choices"))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((Str(r, "kind"), Str(r, "a"), Str(r, "b")));
        }
        return new SongChoices(rows.Where(r => r.Kind == "join").ToDictionary(r => r.A, r => r.B),
                               rows.Where(r => r.Kind == "split").Select(r => r.A).ToList(),
                               rows.Where(r => r.Kind == "apart").Select(r => r.A).ToList());
    }

    /// <summary>
    /// Puts the song with key <paramref name="from"/> under the song <paramref name="to"/> (see <see cref="SongVersions.Key"/>);
    /// false when <paramref name="to"/> is already under <paramref name="from"/>, which would make a loop.
    /// </summary>
    public bool JoinSong(string from, string to)
    {
        if (from == to || SongChoices().Follow(to) == from) return false;
        Exec("INSERT INTO song_choices VALUES ('join', $a, $b) ON CONFLICT(kind, a) DO UPDATE SET b = excluded.b", ("$a", from), ("$b", to));
        Exec("DELETE FROM song_choices WHERE kind = 'apart' AND a = $p", ("$p", Library.SongChoices.Pair(from, to)));
        _songChoices = null;
        return true;
    }

    /// <summary>Takes one save out of its song into a song of its own (<see cref="Library.SongChoices.SplitKey"/>).</summary>
    public void SplitSave(string path)
    {
        Exec("INSERT OR IGNORE INTO song_choices VALUES ('split', $a, '')", ("$a", path.ToLowerInvariant()));
        _songChoices = null;
    }

    /// <summary>Remembers that two songs are different, so the app stops asking.</summary>
    public void KeepApart(string a, string b)
    {
        Exec("INSERT OR IGNORE INTO song_choices VALUES ('apart', $a, '')", ("$a", Library.SongChoices.Pair(a, b)));
        _songChoices = null;
    }

    /// <summary>Undoes your changes to a song: its joins (either way) and the saves split off from it go back to the app's guess.</summary>
    public void ForgetSongChoices(string key, IEnumerable<string> paths)
    {
        Exec("DELETE FROM song_choices WHERE kind = 'join' AND (a = $k OR b = $k)", ("$k", key));
        foreach (var p in paths)
        {
            Exec("DELETE FROM song_choices WHERE kind = 'split' AND a = $p", ("$p", p.ToLowerInvariant()));
            Exec("DELETE FROM song_choices WHERE kind = 'join' AND a = $k", ("$k", Library.SongChoices.SplitKey(p)));
        }
        _songChoices = null;
    }

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
        ProjectsChanged();
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
            using var cmd = Cmd("""
                INSERT INTO plugins (project_id, name, name_key, vendor, kind, format, path, installed, plugin_id)
                VALUES ($id, $n, $k, $v, $kind, $f, $path, $inst, $pid)
                """,
                ("$id", id), ("$n", p.Name), ("$k", key), ("$v", p.Vendor), ("$kind", (int)p.Kind),
                ("$f", p.Format), ("$path", p.Path), ("$inst", IsInstalled(p, key, installed) ? 1 : 0), ("$pid", p.Id));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        foreach (var (path, found) in f.Samples)
        {
            using var cmd = Cmd("INSERT INTO samples (project_id, path, found, unplayed) VALUES ($id, $p, $f, $u)",
                                ("$id", id), ("$p", path), ("$f", found ? 1 : 0), ("$u", (int)(f.Info?.Unplayed.GetValueOrDefault(path) ?? SampleUse.Plays)));
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
    /// A new save of a song (Song_12 next to Song_11, or a Bitwig bored_58 in a folder of its own next to bored_57) takes
    /// over the status, rating, notes and tags you gave the song, so they stay with it as it moves on. It finds the song the
    /// way the Projects list groups it (<see cref="SongVersions.Key"/> and your <see cref="Library.SongChoices"/>), and copies
    /// from the save just before it: the highest number below its own, else the last one saved.
    /// </summary>
    void InheritFromEarlierSave(SqliteTransaction tx, long id, string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var number = SongVersions.Split(name).Number;
        var folder = Path.GetDirectoryName(path) ?? "";
        var daw = Readers.For(path)?.Daw;
        if (daw is null) return;
        var choices = _songChoices ??= ReadSongChoices(tx);
        var song = choices.KeyOf(path, folder, daw, name);
        // The song's saves sit in its folder, or in folders of their own inside it (Bitwig's): the folder's index finds them.
        var root = SongVersions.SongFolder(folder, name);
        var candidates = new List<(long Id, int Number, long Mtime, string Hardware)>();
        using (var cmd = Cmd("""
            SELECT id, name, path, folder, mtime, hardware FROM projects
            WHERE daw = $daw AND id != $id AND (folder = $root OR (folder >= $sub AND folder < $end))
            """, ("$daw", daw), ("$id", id), ("$root", root), ("$sub", root + Path.DirectorySeparatorChar),
                 ("$end", root + (char)(Path.DirectorySeparatorChar + 1))))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (choices.KeyOf(Str(r, "path"), Str(r, "folder"), daw, Str(r, "name")) == song)
                    candidates.Add(((long)r["id"], SongVersions.Split(Str(r, "name")).Number, Long(r, "mtime"), Str(r, "hardware")));
        }
        var before = candidates.Where(c => c.Number < number).OrderByDescending(c => c.Number).ThenByDescending(c => c.Mtime).ToList();
        var pick = before.Count > 0 ? before[0] : candidates.OrderByDescending(c => c.Mtime).FirstOrDefault();
        long? from = pick.Id == 0 ? null : pick.Id;
        var hardware = pick.Hardware ?? "";
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

    bool IsInstalled(PluginRef p, string key, HashSet<string> installed)
    {
        if (p.Format.Equals("Native", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Path.Length > 0 && (File.Exists(p.Path) || Directory.Exists(p.Path))) return true;
        if (PluginCatalog.IsFlStudioBundled(p.Name, p.Vendor, p.Path)) return true;
        if (installed.Count == 0) return true;   // no plugin scan yet: don't cry wolf
        // The ID the project saved is the plugin itself, whatever it's called: the plugin scanner found it in a plugin file.
        if (p.Id.Length > 0 && ScannedIds().Has(p.Id)) return true;
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
            else if (Exec("UPDATE projects SET file_missing = 1 WHERE id = $id", ("$id", id)) > 0) ProjectsChanged();
        }
    }

    /// <summary>A project file that has gone (deleted, or moved out of your folders): it stays listed, marked missing.</summary>
    public bool MarkFileMissing(string path)
    {
        if (Exec("UPDATE projects SET file_missing = 1 WHERE path = $p AND file_missing = 0", ("$p", path)) == 0) return false;
        ProjectsChanged();
        return true;
    }

    public void RemoveProject(long id)
    {
        if (Exec("DELETE FROM projects WHERE id = $id", ("$id", id)) > 0) ProjectsChanged();
    }

    public void RemoveProject(string path)
    {
        if (Exec("DELETE FROM projects WHERE path = $p", ("$p", path)) > 0) ProjectsChanged();
    }

    // ---- installed plugins & sample index ----------------------------------------------

    public HashSet<string> InstalledKeys() =>
        Query("SELECT name_key FROM installed_plugins", r => (string)r["name_key"]).ToHashSet();

    public HashSet<string> InstalledNames() =>
        Query("SELECT name FROM installed_plugins", r => (string)r["name"]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void ReplaceInstalledPlugins(IEnumerable<string> names)
    {
        var list = names.ToList();
        var now = Query("SELECT name, name_key FROM installed_plugins", r => (Name: (string)r["name"], Key: (string)r["name_key"])).ToList();
        // The same list as last time, and the same plugins found in the files (most scans): nothing to write, and every
        // project's plugins keep their answers.
        var keys = new Dictionary<string, string>();
        foreach (var n in list) keys.TryAdd(NameKey(n), n);
        var same = now.Count == keys.Count && now.All(r => keys.TryGetValue(r.Key, out var n) && n == r.Name);
        var ids = ScannedIds().Signature;
        if (same && GetSetting(FlaggedIdsKey) == ids) return;
        if (!same)
        {
            lock (_lock)
            {
                using var tx = _conn.BeginTransaction();
                using (var del = Cmd("DELETE FROM installed_plugins")) { del.Transaction = tx; del.ExecuteNonQuery(); }
                foreach (var n in list)
                {
                    using var cmd = Cmd("INSERT OR IGNORE INTO installed_plugins VALUES ($n, $k)", ("$n", n), ("$k", NameKey(n)));
                    cmd.Transaction = tx;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            PluginsChanged();
        }
        RefreshInstalledFlags();
        SetSetting(FlaggedIdsKey, ids);
    }

    // Which plugin IDs the projects' installed answers were last worked out with: the answers go by them too.
    const string FlaggedIdsKey = "installed_flags_ids";

    /// <summary>
    /// Re-check every project plugin against the installed list. Only the rows whose answer changed are written, and in
    /// small batches: a library of 16,000 projects has hundreds of thousands of plugin rows, and holding the database for
    /// all of them at once kept the window waiting ("Not responding").
    /// </summary>
    public void RefreshInstalledFlags()
    {
        var installed = InstalledKeys();
        var rows = Query("SELECT rowid, name, name_key, vendor, kind, format, path, installed, plugin_id FROM plugins",
            r => (RowId: (long)r["rowid"], Key: NameKey(Str(r, "name")), Was: Long(r, "installed") != 0,
                  Ref: new PluginRef(Str(r, "name"), Str(r, "vendor"), (PluginKind)Long(r, "kind"), Str(r, "format"), Str(r, "path"),
                                     Str(r, "plugin_id"))));
        var changed = rows.Select(row => (row.RowId, Now: IsInstalled(row.Ref, row.Key, installed), row.Was))
                          .Where(row => row.Now != row.Was)
                          .Select(row => (row.RowId, row.Now))
                          .ToList();
        InBatches(changed, "UPDATE plugins SET installed = $i WHERE rowid = $r", ["$i", "$r"],
                  (row, p) => { p[0].Value = row.Now ? 1 : 0; p[1].Value = row.RowId; });
        if (changed.Count > 0) ProjectsChanged();
    }

    /// <summary>
    /// The sample folders' files, in place of the last list. Written in batches with the database let go in between,
    /// so the window can keep reading it while a big library (350,000 files) goes in.
    /// </summary>
    public void ReplaceSampleIndex(IEnumerable<string> files)
    {
        // Only the difference is written: most scans find the same files, and rewriting 350,000 rows each time kept the
        // disk and the database busy for nothing.
        var now = Query("SELECT path FROM sample_index", r => (string)r["path"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = now.Where(f => !found.Contains(f)).ToList();
        var added = found.Where(f => !now.Contains(f)).ToList();
        InBatches(gone, "DELETE FROM sample_index WHERE path = $p", ["$p"], (f, p) => p[0].Value = f);
        InBatches(added, "INSERT OR IGNORE INTO sample_index VALUES ($p, $n, $k)", ["$p", "$n", "$k"], (f, p) =>
        {
            p[0].Value = f;
            p[1].Value = Path.GetFileName(f);
            p[2].Value = NameKey(Path.GetFileNameWithoutExtension(f));
        });
        if (gone.Count + added.Count > 0) Interlocked.Increment(ref _sampleIndexRevision);
    }

    /// <summary>The sample index brought up to date for files the sample folders' watcher saw change: there now, or gone.</summary>
    public void UpdateSampleIndex(IEnumerable<string> paths)
    {
        var list = paths.Where(TextScan.IsAudioPath).ToList();
        var gone = list.Where(f => !File.Exists(f)).ToList();
        InBatches(gone, "DELETE FROM sample_index WHERE path = $p", ["$p"], (f, p) => p[0].Value = f);
        InBatches(list.Except(gone), "INSERT OR IGNORE INTO sample_index VALUES ($p, $n, $k)", ["$p", "$n", "$k"], (f, p) =>
        {
            p[0].Value = f;
            p[1].Value = Path.GetFileName(f);
            p[2].Value = NameKey(Path.GetFileNameWithoutExtension(f));
        });
        if (list.Count > 0) Interlocked.Increment(ref _sampleIndexRevision);
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

    /// <summary>The project at this path, or null if it isn't in the library.</summary>
    public ProjectSummary? ProjectAt(string path) => Summaries("WHERE p.path = $p", ("$p", path)).FirstOrDefault();

    /// <summary>
    /// Fix's copies still waiting for Keep or Delete (the values of the fix_check: settings). Until Keep, the song's
    /// newest save stays the original, so the Projects list leaves these out.
    /// </summary>
    public IReadOnlySet<string> PendingFixes() =>
        Query("SELECT value FROM settings WHERE key LIKE 'fix_check:%' AND value <> ''", r => (string)r["value"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every project with just its file details (path, name, DAW, folder, size, time, tempo, whether it's missing or
    /// failed), for work that only needs to know which files there are. <see cref="Projects"/> also counts each one's
    /// plugins and samples, which on a big library reads most of the database.
    /// </summary>
    public IReadOnlyList<ProjectSummary> ProjectFiles() => Query(
        "SELECT id, path, name, daw, folder, size, mtime, tempo, file_missing, error FROM projects ORDER BY mtime DESC",
        r => new ProjectSummary
        {
            Id = (long)r["id"], Path = (string)r["path"], Name = (string)r["name"], Daw = (string)r["daw"],
            Folder = Str(r, "folder"), Size = Long(r, "size"),
            Modified = DateTimeOffset.FromUnixTimeSeconds(Long(r, "mtime")).LocalDateTime, Mtime = Long(r, "mtime"),
            Tempo = r["tempo"] is double t ? t : null, FileMissing = Long(r, "file_missing") == 1, Error = r["error"] as string,
        });

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

    public void SaveOrganisation(ProjectSummary p) => SaveOrganisation(p, []);

    /// <summary>Your fields for a song, as <see cref="SaveOrganisation(ProjectSummary, IEnumerable{long}, OrganisationField)"/> with all of them.</summary>
    public void SaveOrganisation(ProjectSummary p, IEnumerable<long> otherSaves) => SaveOrganisation(p, otherSaves, OrganisationField.All);

    /// <summary>
    /// Your fields for a song: written to its newest save, and those in <paramref name="fields"/> to each of its other
    /// saves too, so they stay the song's. Only the field you changed goes to the other saves, so changing a rating
    /// leaves an older save's own notes and key alone.
    /// </summary>
    public void SaveOrganisation(ProjectSummary p, IEnumerable<long> otherSaves, OrganisationField fields)
    {
        var sets = new List<string>();
        if (fields.HasFlag(OrganisationField.Status)) sets.Add("status = $s");
        if (fields.HasFlag(OrganisationField.Rating)) sets.Add("rating = $r");
        if (fields.HasFlag(OrganisationField.Favourite)) sets.Add("favourite = $f");
        if (fields.HasFlag(OrganisationField.Key)) sets.Add("musical_key = $k");
        if (fields.HasFlag(OrganisationField.Notes)) sets.Add("notes = $n");
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var id in otherSaves.Prepend(p.Id).Distinct())
            {
                var set = id == p.Id ? "status = $s, rating = $r, favourite = $f, musical_key = $k, notes = $n" : string.Join(", ", sets);
                if (set.Length == 0) continue;
                using var cmd = Cmd($"UPDATE projects SET {set} WHERE id = $id",
                    ("$s", p.Status), ("$r", p.Rating), ("$f", p.Favourite ? 1 : 0), ("$k", p.MusicalKey), ("$n", p.Notes), ("$id", id));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

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

    /// <summary>
    /// Each plugin across the projects. A DAW's own device is one row per DAW (Bitwig's Arpeggiator isn't Studio
    /// One's); a plugin is shown as the kind most of its projects have it as.
    /// </summary>
    public IReadOnlyList<PluginUsage> PluginUsage() => Query("""
        WITH live AS (
            SELECT pl.*, p.mtime AS saved, p.daw AS daw, CASE WHEN pl.format = 'Native' THEN p.daw ELSE '' END AS own
            FROM plugins pl JOIN projects p ON p.id = pl.project_id WHERE p.file_missing = 0),
        missing AS (SELECT project_id, COUNT(DISTINCT name_key) AS n FROM live WHERE installed = 0 GROUP BY project_id),
        kinds AS (
            SELECT name_key, own, kind, ROW_NUMBER() OVER (PARTITION BY name_key, own ORDER BY COUNT(*) DESC, kind) AS r
            FROM live GROUP BY name_key, own, kind)
        SELECT MIN(l.name) AS name, MAX(l.vendor) AS vendor, MAX(l.format) AS format,
               COUNT(DISTINCT l.project_id) AS n, group_concat(DISTINCT l.daw) AS daws, MIN(l.installed) AS installed,
               COUNT(DISTINCT CASE WHEN l.installed = 0 AND m.n = 1 THEN l.project_id END) AS unlocks,
               MAX(l.saved) AS last, MIN(k.kind) AS kind
        FROM live l LEFT JOIN missing m ON m.project_id = l.project_id
        JOIN kinds k ON k.name_key = l.name_key AND k.own = l.own AND k.r = 1
        GROUP BY l.name_key, l.own ORDER BY n DESC, name
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

    /// <summary>
    /// The projects using a plugin, most recently saved first. With <paramref name="own"/> (see <see cref="MusicOrganiser.Core.Library.PluginUsage.Own"/>)
    /// only that row's ones: a DAW's own device in that DAW's projects, or "" for the plugin rather than a device.
    /// </summary>
    public IReadOnlyList<PluginProject> ProjectsWithPlugin(string name, string? own = null) => Query($"""
        SELECT DISTINCT p.id, p.name, p.daw, p.mtime, p.folder, p.path FROM plugins pl JOIN projects p ON p.id = pl.project_id
        WHERE pl.name_key = $k AND p.file_missing = 0 {OwnFilter(own)} ORDER BY p.mtime DESC
        """, r => new PluginProject((long)r["id"], Str(r, "name"), Str(r, "daw"), DateTimeOffset.FromUnixTimeSeconds(Long(r, "mtime")).LocalDateTime,
                                     Str(r, "folder"), Str(r, "path")),
        ("$k", NameKey(name)), ("$own", own ?? ""));

    /// <summary>The part of a query on <c>plugins pl JOIN projects p</c> that keeps one Plugins page row's uses.</summary>
    static string OwnFilter(string? own) => own switch
    {
        null => "",
        "" => "AND pl.format <> 'Native'",
        _ => "AND pl.format = 'Native' AND p.daw = $own",
    };

    public IReadOnlyList<SampleUsage> SampleUsage() => Query("""
        SELECT s.path, COUNT(DISTINCT s.project_id) AS n, MIN(s.found) AS found
        FROM samples s JOIN projects p ON p.id = s.project_id
        WHERE p.file_missing = 0
        GROUP BY s.path COLLATE NOCASE ORDER BY found, n DESC, s.path LIMIT 20000
        """, r => new SampleUsage(Str(r, "path"), (int)Long(r, "n"), Long(r, "found") == 1));

    /// <summary>
    /// Each sample's projects, how many of them were saved since <paramref name="recentSince"/>, and the years those
    /// projects were last saved in. <paramref name="rules"/> leave out saves that hold a sample without playing it.
    /// </summary>
    public IReadOnlyList<(string Path, int Projects, int Recent, int[] Years)> SampleUsageByAge(DateTime recentSince, UsageRules rules = default)
    {
        var since = new DateTimeOffset(recentSince).ToUnixTimeSeconds();
        if (!rules.NotMutedInNewest)
            return Query("""
                SELECT s.path, COUNT(DISTINCT s.project_id) AS n, COUNT(DISTINCT CASE WHEN p.mtime >= $c THEN s.project_id END) AS recent,
                       GROUP_CONCAT(DISTINCT strftime('%Y', p.mtime, 'unixepoch', 'localtime')) AS years
                FROM samples s JOIN projects p ON p.id = s.project_id
                WHERE p.file_missing = 0 AND ($notes = 0 OR s.unplayed <> 1)
                GROUP BY s.path COLLATE NOCASE
                """, r => (Str(r, "path"), (int)Long(r, "n"), (int)Long(r, "recent"),
                           Str(r, "years").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Order().ToArray()),
                ("$c", since), ("$notes", rules.NeedsNotes ? 1 : 0));

        // A sound muted in a song's newest save doesn't count for that song at all: its older saves are left out too.
        var newest = new Dictionary<long, long>();
        foreach (var g in SongVersions.Group(Projects(), SongChoices()))
            foreach (var p in g.Older.Append(g.Latest))
                newest[p.Id] = g.Latest.Id;
        var muted = Query("SELECT project_id, path FROM samples WHERE unplayed = 2", r => ((long)r["project_id"], Str(r, "path")))
            .Where(m => newest.GetValueOrDefault(m.Item1) == m.Item1)
            .Select(m => (m.Item1, m.Item2.ToLowerInvariant()))
            .ToHashSet();
        var rows = Query("""
            SELECT s.path, s.project_id, p.mtime FROM samples s JOIN projects p ON p.id = s.project_id
            WHERE p.file_missing = 0 AND ($notes = 0 OR s.unplayed <> 1)
            """, r => (Path: Str(r, "path"), Project: (long)r["project_id"], Mtime: Long(r, "mtime")), ("$notes", rules.NeedsNotes ? 1 : 0));
        return rows
            .Where(r => !muted.Contains((newest.GetValueOrDefault(r.Project, r.Project), r.Path.ToLowerInvariant())))
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var saves = g.DistinctBy(r => r.Project).ToList();
                return (g.First().Path, saves.Count, saves.Count(r => r.Mtime >= since),
                        saves.Select(r => DateTimeOffset.FromUnixTimeSeconds(r.Mtime).LocalDateTime.Year).Distinct().Order().ToArray());
            })
            .ToList();
    }

    /// <summary>Projects using a plugin (by name) or a sample (by path).</summary>
    public IReadOnlyList<long> ProjectsUsingPlugin(string name, string? own = null) =>
        Query($"SELECT DISTINCT pl.project_id FROM plugins pl JOIN projects p ON p.id = pl.project_id WHERE pl.name_key = $k {OwnFilter(own)}",
              r => (long)r["project_id"], ("$k", NameKey(name)), ("$own", own ?? ""));

    public IReadOnlyList<long> ProjectsUsingSample(string path) =>
        Query("SELECT DISTINCT project_id FROM samples WHERE path = $p COLLATE NOCASE", r => (long)r["project_id"], ("$p", path));
}

/// <summary>Which projects count as using a sound (Settings › Sounds), for projects whose reader can tell (FL Studio so far).</summary>
/// <param name="NeedsNotes">Only where the sound's channel has notes or clips.</param>
/// <param name="NotMutedInNewest">Not in a song whose newest save has it muted.</param>
public readonly record struct UsageRules(bool NeedsNotes = false, bool NotMutedInNewest = false);

/// <summary>Your fields on a song (see <see cref="LibraryDb.SaveOrganisation(ProjectSummary, IEnumerable{long}, OrganisationField)"/>).</summary>
[Flags]
public enum OrganisationField
{
    Status = 1, Rating = 2, Favourite = 4, Key = 8, Notes = 16,
    All = Status | Rating | Favourite | Key | Notes,
}
