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
public sealed record SoundMatch(long A, long B, CopyKind Kind, double Score, double Offset);

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
            kind TEXT NOT NULL, score REAL, offset REAL,
            PRIMARY KEY (a, b)
        );
        CREATE INDEX IF NOT EXISTS ix_sound_matches_b ON sound_matches(b);
        """;

    bool _printsReady;

    void EnsurePrints()
    {
        if (_printsReady) return;
        Exec(PrintSchema);
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
                INSERT OR REPLACE INTO sound_matches SELECT $a, $b, $k, $s, $o
                WHERE EXISTS (SELECT 1 FROM sounds WHERE id = $a) AND EXISTS (SELECT 1 FROM sounds WHERE id = $b)
                """, ("$a", a), ("$b", b), ("$k", m.Kind.ToString()), ("$s", m.Score), ("$o", m.Offset));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Every stored SoundFingerprinting print, for looking new sounds up against.</summary>
    public void ForEachSfp(Action<long, byte[]> each)
    {
        EnsurePrints();
        lock (_lock)
        {
            using var cmd = Cmd("SELECT sound_id, sfp FROM sound_prints WHERE sfp IS NOT NULL");
            using var r = cmd.ExecuteReader();
            while (r.Read()) each((long)r["sound_id"], (byte[])r["sfp"]);
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
        return Query("SELECT a, b, kind, score, offset FROM sound_matches",
            r => new SoundMatch((long)r["a"], (long)r["b"], Enum.TryParse<CopyKind>((string)r["kind"], out var k) ? k : CopyKind.Converted,
                                r["score"] is double s ? s : 0, r["offset"] is double o ? o : 0));
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
