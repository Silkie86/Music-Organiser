using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MusicOrganiser.Core.Sounds;

/// <summary>One sample in your sound library, as listed on screen.</summary>
public sealed class Sound
{
    public long Id { get; init; }
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Folder => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path) ?? "") ?? "";
    public string? Error { get; init; }
    public SoundAnalysis? Analysis { get; init; }

    /// <summary>The type shown: your own label, else the file name's, else what the classifier learned.</summary>
    public string Type { get; set; } = SoundTypes.Unknown;
    /// <summary>"you", "name", "learned" or "model".</summary>
    public string TypeSource { get; set; } = "";
    public double TypeConfidence { get; set; }
    /// <summary>Your own label, when you've set one. These are the strongest training examples.</summary>
    public string? UserType { get; set; }
    public string? NameType { get; init; }

    public List<string> Tags { get; set; } = [];
    public bool Favourite { get; set; }
    /// <summary>A copy you've hidden on the Duplicates tab: the file stays, the app just doesn't list it.</summary>
    public bool Hidden { get; set; }
    public int Rating { get; set; }
    public int Plays { get; set; }
    /// <summary>When the file was last changed: what "Newest" sorts by.</summary>
    public DateTime Added { get; init; }
    /// <summary>What you've done with it in the app, recent (the last year) and older, for Most liked.</summary>
    public SoundActivity Activity { get; set; } = SoundActivity.None;

    /// <summary>Number of your projects that use this sample (from the project library).</summary>
    public int UsedIn { get; set; }
    /// <summary>How many of <see cref="UsedIn"/> were saved in the last year.</summary>
    public int UsedInRecently { get; set; }
    /// <summary>The years the projects using it were last saved in, earliest first.</summary>
    public int[] UsedInYears { get; set; } = [];
    /// <summary>Its Most liked score (see <see cref="LikedWeights"/>), and that as a share of the most liked sound's, for its bar.</summary>
    public double Liked { get; set; }
    public double LikedShare { get; set; }
    /// <summary>What the liked bar is made of, e.g. "In 4 projects, dragged out 6 times, played 12 times".</summary>
    public string LikedTip => LikedWeights.Explain(this);

    public double Seconds => Analysis?.Seconds ?? 0;
    public bool IsLoop => Analysis?.IsLoop ?? false;
    public double? Bpm => Analysis?.Bpm;
    public string Key => Analysis?.KeyName ?? "";
    public string RootNote => Analysis?.RootNoteName ?? "";
    public string Chord => Analysis?.Chord ?? "";
    public string Character => Analysis is null ? "" : string.Join(", ", Analysis.Character);
}

/// <summary>What can be done with a sound that says you like it.</summary>
public static class SoundEvent
{
    public const string Play = "play", Drag = "drag", Send = "send";
}

/// <summary>Deliberate plays, drags out of the app and sends into a DAW: in the last year, and before it.</summary>
public sealed record SoundActivity(int Plays, int OlderPlays, int Drags, int OlderDrags, int Sends, int OlderSends)
{
    public static readonly SoundActivity None = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// The sound library database (sounds.db, next to the project library): analysis
/// results, neural embeddings, your labels and tags, and the trained models.
/// Kept apart from the project library so each can evolve on its own.
/// </summary>
public sealed partial class SoundLibrary : IDisposable
{
    const string Schema = """
        PRAGMA journal_mode = WAL;
        PRAGMA foreign_keys = ON;
        CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE IF NOT EXISTS sounds (
            id INTEGER PRIMARY KEY,
            path TEXT UNIQUE NOT NULL COLLATE NOCASE,
            size INTEGER, mtime INTEGER, error TEXT,
            analysis TEXT, features BLOB, clap BLOB,
            name_type TEXT, user_type TEXT, type TEXT, type_source TEXT, type_conf REAL,
            favourite INTEGER DEFAULT 0, rating INTEGER DEFAULT 0, plays INTEGER DEFAULT 0, last_played INTEGER
        );
        CREATE TABLE IF NOT EXISTS sound_tags (
            sound_id INTEGER REFERENCES sounds(id) ON DELETE CASCADE,
            tag TEXT NOT NULL, source TEXT NOT NULL DEFAULT 'you',
            UNIQUE(sound_id, tag)
        );
        CREATE INDEX IF NOT EXISTS ix_sound_tags_tag ON sound_tags(tag);
        CREATE TABLE IF NOT EXISTS sound_events (
            sound_id INTEGER REFERENCES sounds(id) ON DELETE CASCADE,
            kind TEXT NOT NULL, at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_sound_events ON sound_events(kind, at);
        CREATE TABLE IF NOT EXISTS sound_maps (
            space TEXT NOT NULL,
            sound_id INTEGER NOT NULL REFERENCES sounds(id) ON DELETE CASCADE,
            x REAL NOT NULL, y REAL NOT NULL,
            PRIMARY KEY (space, sound_id)
        );
        -- Forgetting a sound clears its rows here too: without these, each one read every row of both tables.
        CREATE INDEX IF NOT EXISTS ix_sound_maps_sound ON sound_maps(sound_id);
        CREATE INDEX IF NOT EXISTS ix_sound_events_sound ON sound_events(sound_id);
        """;

    // The galaxy kept one map before each kind of placing had its own: it moves across under the kind it was made from.
    const string MoveOldMap = """
        INSERT OR IGNORE INTO sound_maps
            SELECT COALESCE((SELECT value FROM settings WHERE key = 'galaxy_space'), 'features'), sound_id, x, y FROM sound_map;
        DROP TABLE sound_map;
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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "MusicOrganiser", "sounds.db");

    public SoundLibrary(string path)
    {
        FilePath = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        _readString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        Exec(Schema);
        if (Query("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'sound_map'", r => (string)r["name"]).Any())
            Exec(MoveOldMap);
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

    // ---- plumbing ----------------------------------------------------------------

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

    static string? StrOrNull(SqliteDataReader r, string col) => r[col] as string;
    static long Long(SqliteDataReader r, string col) => r[col] is long l ? l : 0;

    public static byte[]? ToBlob(float[]? v) => v is null ? null : MemoryMarshal.AsBytes(v.AsSpan()).ToArray();

    public static float[]? FromBlob(object? o) => o is byte[] b && b.Length % 4 == 0 ? MemoryMarshal.Cast<byte, float>(b).ToArray() : null;

    // ---- settings ----------------------------------------------------------------

    public string? GetSetting(string key) =>
        Query("SELECT value FROM settings WHERE key = $k", r => StrOrNull(r, "value"), ("$k", key)).FirstOrDefault();

    public void SetSetting(string key, string? value)
    {
        if (value is null) Exec("DELETE FROM settings WHERE key = $k", ("$k", key));
        else Exec("INSERT INTO settings VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));
    }

    // ---- scanning ----------------------------------------------------------------

    public Dictionary<string, (long Mtime, long Size, bool HasClap)> KnownFiles() =>
        Query("SELECT path, mtime, size, clap IS NOT NULL AS has_clap FROM sounds",
              r => ((string)r["path"], Long(r, "mtime"), Long(r, "size"), Long(r, "has_clap") == 1))
            .ToDictionary(t => t.Item1, t => (t.Item2, t.Item3, t.Item4), StringComparer.OrdinalIgnoreCase);

    public sealed record Analysed(string Path, long Size, long Mtime, SoundAnalysis? Analysis, string? Error, float[]? Clap, string? NameType);

    public void Save(IEnumerable<Analysed> batch)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var a in batch)
            {
                using var cmd = Cmd("""
                    INSERT INTO sounds (path, size, mtime, error, analysis, features, clap, name_type, type, type_source, type_conf)
                    VALUES ($p, $s, $m, $e, $a, $f, $c, $n, COALESCE($n, 'Unknown'), CASE WHEN $n IS NULL THEN '' ELSE 'name' END, 1)
                    ON CONFLICT(path) DO UPDATE SET
                        size = excluded.size, mtime = excluded.mtime, error = excluded.error, analysis = excluded.analysis,
                        features = excluded.features, clap = COALESCE(excluded.clap, sounds.clap), name_type = excluded.name_type
                    """,
                    ("$p", a.Path), ("$s", a.Size), ("$m", a.Mtime), ("$e", a.Error),
                    ("$a", a.Analysis is null ? null : JsonSerializer.Serialize(a.Analysis)),
                    ("$f", ToBlob(a.Analysis?.Features)), ("$c", ToBlob(a.Clap)), ("$n", a.NameType));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void SetClap(long id, float[] v) => Exec("UPDATE sounds SET clap = $c WHERE id = $id", ("$c", ToBlob(v)), ("$id", id));

    /// <summary>The size and modified time stored for one file, or null when it isn't in the library.</summary>
    public (long Mtime, long Size)? KnownFile(string path) =>
        Query("SELECT mtime, size FROM sounds WHERE path = $p", r => (Long(r, "mtime"), Long(r, "size")), ("$p", path))
            .Select(t => ((long, long)?)t).FirstOrDefault();

    /// <summary>Forgets these files (deleted or moved away); returns how many were in the library.</summary>
    public int Remove(IEnumerable<string> paths) =>
        InBatches(paths, "DELETE FROM sounds WHERE path = $p", ["$p"], (path, p) => p[0].Value = path);

    /// <summary>
    /// Sounds whose files were renamed or moved (see <see cref="SoundOrganiser.Moves"/>): each keeps its row, so its tags,
    /// rating, favourite, your type, plays and galaxy place stay with it. Returns how many moved.
    /// </summary>
    public int Move(IEnumerable<(string From, string To)> moves) =>
        InBatches(moves, "UPDATE sounds SET path = $to, name_type = $n WHERE path = $from", ["$from", "$to", "$n"], (m, p) =>
        {
            p[0].Value = m.From;
            p[1].Value = m.To;
            p[2].Value = (object?)SoundTypes.FromPath(m.To) ?? DBNull.Value;   // the type its new name or folder says
        });

    /// <summary>Forgets samples that are no longer in your sample folders.</summary>
    public int RemoveMissing(IReadOnlySet<string> present, Func<string, bool>? keep = null)
    {
        var gone = Query("SELECT id, path FROM sounds", r => ((long)r["id"], (string)r["path"]))
            .Where(t => !present.Contains(t.Item2) && keep?.Invoke(t.Item2) != true).Select(t => t.Item1).ToList();
        InBatches(gone, "DELETE FROM sounds WHERE id = $id", ["$id"], (id, p) => p[0].Value = id);
        return gone.Count;
    }

    /// <summary>
    /// Runs one statement for each value, a few hundred to a transaction, letting go of the database between them, so
    /// a play or a favourite clicked meanwhile isn't kept waiting for a whole pack to go. Returns the rows changed.
    /// </summary>
    int InBatches<T>(IEnumerable<T> values, string sql, string[] names, Action<T, SqliteParameter[]> fill, int size = 500)
    {
        var changed = 0;
        using var e = values.GetEnumerator();
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
                    changed += cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
        return changed;
    }

    // ---- reading -----------------------------------------------------------------

    public int Count() => Query("SELECT COUNT(*) AS n FROM sounds", r => (int)(long)r["n"]).First();

    public int CountWithClap() => Query("SELECT COUNT(*) AS n FROM sounds WHERE clap IS NOT NULL", r => (int)(long)r["n"]).First();

    public List<Sound> All()
    {
        var activity = Activity();
        var tags = Query("SELECT sound_id, tag FROM sound_tags ORDER BY tag", r => ((long)r["sound_id"], (string)r["tag"]))
            .GroupBy(t => t.Item1).ToDictionary(g => g.Key, g => g.Select(t => t.Item2).ToList());
        var sounds = Query("""
            SELECT id, path, mtime, error, analysis, name_type, user_type, type, type_source, type_conf, favourite, rating, plays
            FROM sounds ORDER BY path
            """, r =>
        {
            var id = (long)r["id"];
            SoundAnalysis? analysis = null;
            if (StrOrNull(r, "analysis") is { } json)
                try { analysis = JsonSerializer.Deserialize<SoundAnalysis>(json); } catch (JsonException) { }
            return new Sound
            {
                Id = id, Path = (string)r["path"], Error = StrOrNull(r, "error"), Analysis = analysis,
                NameType = StrOrNull(r, "name_type"), UserType = StrOrNull(r, "user_type"),
                Type = StrOrNull(r, "type") ?? SoundTypes.Unknown, TypeSource = StrOrNull(r, "type_source") ?? "",
                TypeConfidence = r["type_conf"] is double d ? d : 0,
                Favourite = Long(r, "favourite") == 1, Rating = (int)Long(r, "rating"), Plays = (int)Long(r, "plays"),
                Tags = tags.GetValueOrDefault(id) ?? [],
                Added = DateTimeOffset.FromUnixTimeSeconds(Long(r, "mtime")).LocalDateTime,
            };
        });
        foreach (var s in sounds) s.Activity = activity.GetValueOrDefault(s.Id) ?? SoundActivity.None;
        return sounds;
    }

    /// <summary>
    /// What's been done with each sound since <paramref name="since"/> and before it: deliberate plays, drags out of the
    /// app and sends into a DAW. Plays counted before the app kept dates count as recent.
    /// </summary>
    public Dictionary<long, SoundActivity> Activity(DateTime? since = null)
    {
        var cutoff = new DateTimeOffset(since ?? DateTime.Now.AddYears(-1)).ToUnixTimeSeconds();
        var counts = Query("""
            SELECT sound_id, kind, SUM(at >= $c) AS recent, SUM(at < $c) AS older FROM sound_events GROUP BY sound_id, kind
            """, r => ((long)r["sound_id"], (string)r["kind"], (int)Long(r, "recent"), (int)Long(r, "older")), ("$c", cutoff));
        var plays = Query("SELECT id, plays FROM sounds WHERE plays > 0", r => ((long)r["id"], (int)Long(r, "plays"))).ToDictionary(t => t.Item1, t => t.Item2);
        var result = new Dictionary<long, SoundActivity>();
        foreach (var g in counts.GroupBy(c => c.Item1))
        {
            (int, int) Of(string kind) => g.Where(c => c.Item2 == kind).Select(c => (c.Item3, c.Item4)).FirstOrDefault();
            var (pr, po) = Of(SoundEvent.Play);
            var (dr, d_o) = Of(SoundEvent.Drag);
            var (sr, so) = Of(SoundEvent.Send);
            var legacy = Math.Max(0, plays.GetValueOrDefault(g.Key) - pr - po);
            result[g.Key] = new SoundActivity(pr + legacy, po, dr, d_o, sr, so);
        }
        foreach (var (id, n) in plays)
            result.TryAdd(id, new SoundActivity(n, 0, 0, 0, 0, 0));
        return result;
    }

    /// <summary>The sounds you've played on purpose, most recent first, each once with when you last played it.</summary>
    public IReadOnlyList<(long Id, DateTime At)> History(int limit = 500) => Query("""
        SELECT sound_id, MAX(at) AS at FROM sound_events WHERE kind = 'play' GROUP BY sound_id ORDER BY at DESC LIMIT $n
        """, r => ((long)r["sound_id"], DateTimeOffset.FromUnixTimeSeconds(Long(r, "at")).LocalDateTime), ("$n", limit));

    /// <summary>Notes that a sound was dragged out of the app or sent into a DAW (see <see cref="SoundEvent"/>).</summary>
    public void Record(IEnumerable<long> ids, string kind)
    {
        var at = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var id in ids.Distinct())
            {
                // A sound forgotten since (its file gone in a rescan) is skipped.
                using var cmd = Cmd("INSERT INTO sound_events SELECT $id, $k, $t WHERE EXISTS (SELECT 1 FROM sounds WHERE id = $id)",
                                    ("$id", id), ("$k", kind), ("$t", at));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Where each sound sits on the galaxy map (see <see cref="GalaxyMap"/>) when placed by <paramref name="space"/>
    /// (see <see cref="SoundOrganiser.GalaxySpace"/>). Each kind of placing keeps its own map, so switching back to
    /// one is instant.
    /// </summary>
    public Dictionary<long, StarPoint> Map(string space) =>
        Query("SELECT sound_id, x, y FROM sound_maps WHERE space = $s",
              r => ((long)r["sound_id"], new StarPoint((float)(double)r["x"], (float)(double)r["y"])), ("$s", space))
            .ToDictionary(t => t.Item1, t => t.Item2);

    /// <summary>
    /// Saves galaxy positions for one kind of placing; <paramref name="replace"/> throws away its old places that the new
    /// map hasn't got (a fresh layout). Written a few hundred at a time, letting go of the database between them: a fresh
    /// layout of 357,000 sounds held it for seconds, and a sound played meanwhile waited.
    /// </summary>
    public void SaveMap(string space, IReadOnlyDictionary<long, StarPoint> points, bool replace)
    {
        // Every new place written over the old one, then the old ones left over gone, so the map is never empty meanwhile.
        InBatches(points, "INSERT OR REPLACE INTO sound_maps SELECT $s, $id, $x, $y WHERE EXISTS (SELECT 1 FROM sounds WHERE id = $id)",
                  ["$s", "$id", "$x", "$y"], (point, p) =>
                  {
                      p[0].Value = space;
                      p[1].Value = point.Key;
                      p[2].Value = (double)point.Value.X;
                      p[3].Value = (double)point.Value.Y;
                  });
        if (!replace) return;
        var stale = Query("SELECT sound_id FROM sound_maps WHERE space = $s", r => (long)r["sound_id"], ("$s", space))
            .Where(id => !points.ContainsKey(id));
        InBatches(stale, "DELETE FROM sound_maps WHERE space = $s AND sound_id = $id", ["$s", "$id"], (id, p) =>
        {
            p[0].Value = space;
            p[1].Value = id;
        });
    }

    /// <summary>Vectors for every sound in a space: "features" (always) or "clap" (when the model has run).</summary>
    public Dictionary<long, float[]> Vectors(string space)
    {
        var col = space == "clap" ? "clap" : "features";
        return Query($"SELECT id, {col} AS v FROM sounds WHERE {col} IS NOT NULL", r => ((long)r["id"], FromBlob(r["v"])))
            .Where(t => t.Item2 is { Length: > 0 }).ToDictionary(t => t.Item1, t => t.Item2!);
    }

    // ---- your organisation ---------------------------------------------------------

    public void SetUserType(long id, string? type) =>
        Exec("""
            UPDATE sounds SET user_type = $t,
                type = COALESCE($t, name_type, 'Unknown'),
                type_source = CASE WHEN $t IS NOT NULL THEN 'you' WHEN name_type IS NOT NULL THEN 'name' ELSE '' END,
                type_conf = 1
            WHERE id = $id
            """, ("$t", type), ("$id", id));

    /// <summary>Stores the shown type for many sounds at once (after the classifier runs), a few hundred at a time.</summary>
    public void SetTypes(IEnumerable<(long Id, string Type, string Source, double Confidence)> types) =>
        InBatches(types, "UPDATE sounds SET type = $t, type_source = $s, type_conf = $c WHERE id = $id", ["$t", "$s", "$c", "$id"], (u, p) =>
        {
            p[0].Value = u.Type;
            p[1].Value = u.Source;
            p[2].Value = u.Confidence;
            p[3].Value = u.Id;
        });

    public void SetFavourite(long id, bool fav) => Exec("UPDATE sounds SET favourite = $f WHERE id = $id", ("$f", fav ? 1 : 0), ("$id", id));
    public void SetRating(long id, int rating) => Exec("UPDATE sounds SET rating = $r WHERE id = $id", ("$r", rating), ("$id", id));

    /// <summary>A deliberate play: counted, and kept for History and Most liked.</summary>
    public void MarkPlayed(long id)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Exec("UPDATE sounds SET plays = plays + 1, last_played = $t WHERE id = $id", ("$t", now), ("$id", id));
        Exec("INSERT INTO sound_events SELECT $id, 'play', $t WHERE EXISTS (SELECT 1 FROM sounds WHERE id = $id)", ("$id", id), ("$t", now));
    }

    public void AddTag(long id, string tag, string source = "you") =>
        Exec("""
            INSERT INTO sound_tags VALUES ($id, $t, $s)
            ON CONFLICT(sound_id, tag) DO UPDATE SET source = CASE WHEN excluded.source = 'you' THEN 'you' ELSE sound_tags.source END
            """, ("$id", id), ("$t", tag.Trim()), ("$s", source));

    public void RemoveTag(long id, string tag) => Exec("DELETE FROM sound_tags WHERE sound_id = $id AND tag = $t", ("$id", id), ("$t", tag));

    /// <summary>Removes tags the model suggested, so a retrain starts clean. Your own tags stay.</summary>
    public void ClearLearnedTags(string tag) => Exec("DELETE FROM sound_tags WHERE tag = $t AND source = 'learned'", ("$t", tag));

    public IReadOnlyList<(string Tag, int Yours, int Learned)> TagCounts() => Query("""
        SELECT tag, SUM(source = 'you') AS yours, SUM(source = 'learned') AS learned
        FROM sound_tags GROUP BY tag ORDER BY yours DESC, tag
        """, r => ((string)r["tag"], (int)Long(r, "yours"), (int)Long(r, "learned")));

    public IReadOnlyList<long> SoundsTagged(string tag, string source = "you") =>
        Query("SELECT sound_id FROM sound_tags WHERE tag = $t AND source = $s", r => (long)r["sound_id"], ("$t", tag), ("$s", source));
}
