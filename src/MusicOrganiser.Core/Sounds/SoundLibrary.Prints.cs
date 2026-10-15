namespace MusicOrganiser.Core.Sounds;

/// <summary>How one sound is a copy of another.</summary>
public enum CopyKind
{
    /// <summary>The very same audio, whatever the file is called.</summary>
    Exact,
    /// <summary>The same sound in another file: a different format, sample rate, bit depth or level.</summary>
    Converted,
    /// <summary>One is cut from the other: shorter at the start or the end, or a part of a longer sound.</summary>
    Trimmed,
}

/// <summary>Two sounds found to be copies (A's id is the smaller). <paramref name="Offset"/>: where the shorter starts in the longer, in seconds.</summary>
/// <paramref name="Apart"/> and <paramref name="Typical"/>: how far apart the two sound where they overlap, in dB, at worst and
/// typically (null until checked; see <see cref="SoundPrint.SpectralDistances"/>).
public sealed record SoundMatch(long A, long B, CopyKind Kind, double Score, double Offset, double? Apart = null, double? Typical = null);

/// <summary>The duplicate finder's tables: each sound's print, and the copies found.</summary>
public sealed partial class SoundLibrary
{
    const string PrintSchema = """
        CREATE TABLE IF NOT EXISTS sound_prints (
            sound_id INTEGER PRIMARY KEY REFERENCES sounds(id) ON DELETE CASCADE,
            mtime INTEGER, size INTEGER, version INTEGER,
            exact BLOB, frames INTEGER, bands BLOB, keys BLOB, sketch BLOB, seconds REAL, sfp BLOB,
            compared INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_sound_prints_exact ON sound_prints(exact);
        CREATE TABLE IF NOT EXISTS sound_matches (
            a INTEGER NOT NULL REFERENCES sounds(id) ON DELETE CASCADE,
            b INTEGER NOT NULL REFERENCES sounds(id) ON DELETE CASCADE,
            kind TEXT NOT NULL, score REAL, offset REAL, apart REAL, typical REAL,
            PRIMARY KEY (a, b)
        );
        CREATE INDEX IF NOT EXISTS ix_sound_matches_b ON sound_matches(b);
        CREATE TABLE IF NOT EXISTS sound_hidden (
            sound_id INTEGER PRIMARY KEY REFERENCES sounds(id) ON DELETE CASCADE,
            batch TEXT NOT NULL, at INTEGER NOT NULL
        );
        """;

    bool _printsReady;

    void EnsurePrints()
    {
        if (_printsReady) return;
        Exec(PrintSchema);
        foreach (var column in new[] { "apart", "typical" })   // added after the first test builds
            if (Query("SELECT COUNT(*) AS n FROM pragma_table_info('sound_matches') WHERE name = $c", r => Long(r, "n"), ("$c", column)).First() == 0)
                Exec($"ALTER TABLE sound_matches ADD COLUMN {column} REAL");
        _printsReady = true;
    }

    /// <summary>Sounds with no print yet, or whose file changed since (or printed an older way).</summary>
    public IReadOnlyList<(long Id, string Path, long Mtime, long Size)> SoundsToPrint()
    {
        EnsurePrints();
        return Query("""
            SELECT s.id, s.path, s.mtime, s.size FROM sounds s LEFT JOIN sound_prints p ON p.sound_id = s.id
            WHERE s.error IS NULL AND (p.sound_id IS NULL OR p.mtime IS NOT s.mtime OR p.size IS NOT s.size OR p.version IS NOT $v)
            ORDER BY s.id
            """, r => ((long)r["id"], (string)r["path"], Long(r, "mtime"), Long(r, "size")), ("$v", SoundPrint.Version));
    }

    /// <summary>Throws away what's known about these sounds' copies, before they're printed again.</summary>
    public void ForgetPrints(IEnumerable<long> ids)
    {
        EnsurePrints();
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var id in ids)
                foreach (var sql in new[] { "DELETE FROM sound_matches WHERE a = $id OR b = $id", "DELETE FROM sound_prints WHERE sound_id = $id" })
                {
                    using var cmd = Cmd(sql, ("$id", id));
                    cmd.Transaction = tx;
                    cmd.ExecuteNonQuery();
                }
            tx.Commit();
        }
    }

    /// <summary>Saves new prints and the copies found while making them, together, so a stopped run loses nothing.</summary>
    public void SavePrints(IEnumerable<(long Id, long Mtime, long Size, SoundPrint? Print)> prints, IEnumerable<SoundMatch> matches)
    {
        EnsurePrints();
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var (id, mtime, size, p) in prints)
            {
                // A sound that can't be read gets an empty print, so it isn't tried again until its file changes.
                using var cmd = Cmd("""
                    INSERT OR REPLACE INTO sound_prints (sound_id, mtime, size, version, exact, frames, bands, keys, sketch, seconds, sfp, compared)
                    SELECT $id, $m, $s, $v, $e, $f, $b, $k, $sk, $sec, $sfp, 0 WHERE EXISTS (SELECT 1 FROM sounds WHERE id = $id)
                    """,
                    ("$id", id), ("$m", mtime), ("$s", size), ("$v", SoundPrint.Version), ("$e", p?.Exact), ("$f", p?.Frames ?? 0),
                    ("$b", p?.Bands), ("$sk", p?.Sketch), ("$k", p is null ? null : SoundPrint.PackKeys(p.Keys)), ("$sec", p?.Seconds ?? 0), ("$sfp", p?.Sfp));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            SaveMatches(matches, tx);
            tx.Commit();
        }
    }

    public void SaveMatches(IEnumerable<SoundMatch> matches)
    {
        EnsurePrints();
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            SaveMatches(matches, tx);
            tx.Commit();
        }
    }

    void SaveMatches(IEnumerable<SoundMatch> matches, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        foreach (var m in matches)
        {
            var (a, b) = m.A < m.B ? (m.A, m.B) : (m.B, m.A);
            using var cmd = Cmd("""
                INSERT OR REPLACE INTO sound_matches (a, b, kind, score, offset, apart, typical) SELECT $a, $b, $k, $s, $o, $apart, $typ
                WHERE EXISTS (SELECT 1 FROM sounds WHERE id = $a) AND EXISTS (SELECT 1 FROM sounds WHERE id = $b)
                """, ("$a", a), ("$b", b), ("$k", m.Kind.ToString()), ("$s", m.Score), ("$o", m.Offset), ("$apart", m.Apart), ("$typ", m.Typical));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Every stored SoundFingerprinting print, for looking new sounds up against. Read a page at a time, and
    /// <paramref name="each"/> is called outside the database lock, so the rest of the app isn't kept waiting.
    /// </summary>
    public void ForEachSfp(Action<long, byte[]> each)
    {
        EnsurePrints();
        var after = long.MinValue;
        while (true)
        {
            var page = Query("SELECT sound_id, sfp FROM sound_prints WHERE sfp IS NOT NULL AND sound_id > $after ORDER BY sound_id LIMIT 1000",
                             r => ((long)r["sound_id"], (byte[])r["sfp"]), ("$after", after));
            foreach (var (id, sfp) in page) each(id, sfp);
            if (page.Count < 1000) return;
            after = page[^1].Item1;
        }
    }

    /// <summary>What finding candidates needs for every printed sound: bucket keys, exact hash, length, whether it's been compared.</summary>
    public IReadOnlyList<(long Id, ushort[] Keys, byte[]? Exact, double Seconds, bool Compared)> PrintKeys()
    {
        EnsurePrints();
        return Query("SELECT sound_id, keys, exact, seconds, compared FROM sound_prints",
            r => ((long)r["sound_id"], SoundPrint.UnpackKeys(r["keys"] as byte[]), r["exact"] as byte[],
                  r["seconds"] is double d ? d : 0, Long(r, "compared") == 1));
    }

    /// <summary>The band prints and waveform sketches of some sounds.</summary>
    public Dictionary<long, (int Frames, byte[] Bands, byte[] Sketch)> PrintBands(IEnumerable<long> ids)
    {
        EnsurePrints();
        var result = new Dictionary<long, (int, byte[], byte[])>();
        foreach (var chunk in ids.Distinct().Chunk(500))
            foreach (var (id, f, b, sk) in Query($"SELECT sound_id, frames, bands, sketch FROM sound_prints WHERE bands IS NOT NULL AND sound_id IN ({string.Join(",", chunk)})",
                                             r => ((long)r["sound_id"], (int)Long(r, "frames"), (byte[])r["bands"], r["sketch"] as byte[] ?? [])))
                result[id] = (f, b, sk);
        return result;
    }

    public void MarkCompared()
    {
        EnsurePrints();
        Exec("UPDATE sound_prints SET compared = 1 WHERE compared = 0");
    }

    public IReadOnlyList<SoundMatch> Matches()
    {
        EnsurePrints();
        return Query("SELECT a, b, kind, score, offset, apart, typical FROM sound_matches",
            r => new SoundMatch((long)r["a"], (long)r["b"], Enum.TryParse<CopyKind>((string)r["kind"], out var k) ? k : CopyKind.Converted,
                                r["score"] is double s ? s : 0, r["offset"] is double o ? o : 0, r["apart"] as double?, r["typical"] as double?));
    }

    /// <summary>Converted and trimmed copies whose sounds haven't been compared yet, with where the shorter starts in the longer.</summary>
    public IReadOnlyList<(long A, long B, double Offset)> SamenessToCheck()
    {
        EnsurePrints();
        return Query("SELECT a, b, offset FROM sound_matches WHERE kind IN ('Converted', 'Trimmed') AND apart IS NULL",
                     r => ((long)r["a"], (long)r["b"], r["offset"] is double o ? o : 0));
    }

    public void SaveSameness(IEnumerable<(long A, long B, double Typical, double Apart)> checks)
    {
        EnsurePrints();
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var (a, b, typical, apart) in checks)
            {
                using var cmd = Cmd("UPDATE sound_matches SET apart = $s, typical = $t WHERE a = $a AND b = $b", ("$a", a), ("$b", b), ("$s", apart), ("$t", typical));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Sounds whose audio is exactly the same as another's, by hash.</summary>
    public IReadOnlyList<(long Id, string Hash)> ExactCopies()
    {
        EnsurePrints();
        return Query("""
            SELECT sound_id, hex(exact) AS h FROM sound_prints
            WHERE exact IN (SELECT exact FROM sound_prints WHERE exact IS NOT NULL GROUP BY exact HAVING COUNT(*) > 1)
            """, r => ((long)r["sound_id"], (string)r["h"]));
    }

    // ---- copies hidden from the app (the files stay where they are) ------------------------------

    public HashSet<long> HiddenIds()
    {
        EnsurePrints();
        return Query("SELECT sound_id FROM sound_hidden", r => (long)r["sound_id"]).ToHashSet();
    }

    /// <summary>Hides sounds from the app's lists, together as one batch (so they can be shown again together).</summary>
    public void Hide(IEnumerable<long> ids, string batch)
    {
        EnsurePrints();
        var at = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var id in ids)
            {
                using var cmd = Cmd("INSERT OR REPLACE INTO sound_hidden SELECT $id, $b, $t WHERE EXISTS (SELECT 1 FROM sounds WHERE id = $id)",
                                    ("$id", id), ("$b", batch), ("$t", at));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void Unhide(IEnumerable<long> ids)
    {
        EnsurePrints();
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var id in ids)
            {
                using var cmd = Cmd("DELETE FROM sound_hidden WHERE sound_id = $id", ("$id", id));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>The most recent batch of hidden copies: its name, when, and which sounds.</summary>
    public (string Batch, DateTime At, IReadOnlyList<long> Ids)? LastHidden()
    {
        EnsurePrints();
        var last = Query("SELECT batch, MAX(at) AS at FROM sound_hidden GROUP BY batch ORDER BY at DESC LIMIT 1",
                         r => ((string)r["batch"], Long(r, "at"))).FirstOrDefault();
        if (last.Item1 is null) return null;
        var ids = Query("SELECT sound_id FROM sound_hidden WHERE batch = $b", r => (long)r["sound_id"], ("$b", last.Item1));
        return (last.Item1, DateTimeOffset.FromUnixTimeSeconds(last.Item2).LocalDateTime, ids);
    }

    /// <summary>File sizes in bytes, by sound.</summary>
    public Dictionary<long, long> Sizes() =>
        Query("SELECT id, size FROM sounds", r => ((long)r["id"], Long(r, "size"))).ToDictionary(t => t.Item1, t => t.Item2);

    /// <summary>How many sounds have a print, and how many are still to do.</summary>
    public (int Printed, int Total) PrintCounts()
    {
        EnsurePrints();
        return Query("SELECT (SELECT COUNT(*) FROM sound_prints) AS p, (SELECT COUNT(*) FROM sounds WHERE error IS NULL) AS t",
                     r => ((int)Long(r, "p"), (int)Long(r, "t"))).First();
    }
}
