using Microsoft.Data.Sqlite;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Midi;

/// <summary>A file the MIDI browser has read: a project, or a .mid file in a MIDI library folder.</summary>
public sealed record MidiSource(string Path, string Kind, string Daw, long Mtime, long Size, double? Tempo)
{
    public const string Project = "project", File = "file";
}

/// <summary>One MIDI part ready to save: where it's from, its notes (at <see cref="PhraseAnalyser.Ppq"/>) and what it is.</summary>
public sealed record IndexedPhrase(string Part, string Clip, IReadOnlyList<NoteEvent> Notes, PhraseAnalysis Analysis, bool Drums);

/// <summary>A saved phrase as the browser lists it: everything but its notes, which are read when it's shown.</summary>
public sealed record StoredPhrase(long Id, MidiSource Source, string Part, string Clip, string Role, string RoleWhy, string? YourRole,
                                  int Bars, int NoteCount, int Lowest, int Highest, KeyEstimate? Key, byte[] Chroma, string Chords,
                                  byte[] Features, string Hash);

/// <summary>
/// The MIDI browser's own database (midi.db, next to the library): every MIDI part found in your projects and MIDI
/// libraries, what kind of part it is, its key, chords and shape. It only stores what was read: projects and .mid
/// files are never changed. Indexing writes on one connection while the page reads on another, so neither waits.
/// </summary>
public sealed class MidiIndex : IDisposable
{
    const string Schema = """
        PRAGMA journal_mode = WAL;
        PRAGMA foreign_keys = ON;
        CREATE TABLE IF NOT EXISTS sources (
            path TEXT PRIMARY KEY COLLATE NOCASE, kind TEXT NOT NULL, daw TEXT, mtime INTEGER, size INTEGER,
            tempo REAL, version INTEGER, error TEXT
        );
        CREATE TABLE IF NOT EXISTS phrases (
            id INTEGER PRIMARY KEY,
            source TEXT NOT NULL COLLATE NOCASE REFERENCES sources(path) ON DELETE CASCADE,
            part TEXT, clip TEXT, role TEXT, role_why TEXT, drums INTEGER,
            bars INTEGER, notes INTEGER, lowest INTEGER, highest INTEGER,
            key_tonic INTEGER, key_minor INTEGER, key_conf REAL,
            chroma BLOB, chords TEXT, features BLOB, hash TEXT, data BLOB
        );
        CREATE INDEX IF NOT EXISTS ix_phrases_source ON phrases(source);
        CREATE INDEX IF NOT EXISTS ix_phrases_hash ON phrases(hash);
        -- What you said a part is, by its notes, so it holds for every copy and survives reading the file again.
        CREATE TABLE IF NOT EXISTS your_roles (hash TEXT PRIMARY KEY, role TEXT NOT NULL);
        """;

    readonly SqliteConnection _write, _read;
    readonly object _writeLock = new(), _readLock = new();

    public string FilePath { get; }

    /// <summary>midi.db in the same folder as the library.</summary>
    public static string PathBeside(string libraryPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(libraryPath))!, "midi.db");

    public MidiIndex(string path)
    {
        FilePath = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _write = new SqliteConnection($"Data Source={path}");
        _write.Open();
        using (var cmd = _write.CreateCommand())
        {
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
        }
        _read = new SqliteConnection($"Data Source={path}");
        _read.Open();
    }

    public void Dispose()
    {
        lock (_writeLock) _write.Dispose();
        lock (_readLock) _read.Dispose();
    }

    /// <summary>Every file read so far: when it was last changed, its size, and which version of the analysis read it.</summary>
    public Dictionary<string, (long Mtime, long Size, int Version)> Sources()
    {
        var map = new Dictionary<string, (long, long, int)>(StringComparer.OrdinalIgnoreCase);
        lock (_readLock)
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT path, mtime, size, version FROM sources";
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? 0 : r.GetInt32(3));
        }
        return map;
    }

    /// <summary>Saves what was read from a file, replacing what was there before.</summary>
    public void Replace(MidiSource source, int version, IReadOnlyList<IndexedPhrase> phrases, string? error = null) =>
        ReplaceMany([(source, version, phrases, error)]);

    public void ReplaceMany(IReadOnlyList<(MidiSource Source, int Version, IReadOnlyList<IndexedPhrase> Phrases, string? Error)> batch)
    {
        if (batch.Count == 0) return;
        lock (_writeLock)
        {
            using var tx = _write.BeginTransaction();
            foreach (var (s, version, phrases, error) in batch)
            {
                Exec(tx, "DELETE FROM phrases WHERE source = $p", ("$p", s.Path));
                Exec(tx, """
                    INSERT INTO sources (path, kind, daw, mtime, size, tempo, version, error) VALUES ($p, $k, $d, $m, $s, $t, $v, $e)
                    ON CONFLICT(path) DO UPDATE SET kind = $k, daw = $d, mtime = $m, size = $s, tempo = $t, version = $v, error = $e
                    """, ("$p", s.Path), ("$k", s.Kind), ("$d", s.Daw), ("$m", s.Mtime), ("$s", s.Size), ("$t", s.Tempo),
                     ("$v", version), ("$e", error));
                using var cmd = _write.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO phrases (source, part, clip, role, role_why, drums, bars, notes, lowest, highest, key_tonic, key_minor,
                                         key_conf, chroma, chords, features, hash, data)
                    VALUES ($s, $part, $clip, $role, $why, $drums, $bars, $notes, $lo, $hi, $kt, $km, $kc, $chroma, $chords, $feat, $hash, $data)
                    """;
                var ps = new[] { "$s", "$part", "$clip", "$role", "$why", "$drums", "$bars", "$notes", "$lo", "$hi", "$kt", "$km", "$kc",
                                 "$chroma", "$chords", "$feat", "$hash", "$data" }
                    .ToDictionary(n => n, n => cmd.Parameters.Add(new SqliteParameter(n, DBNull.Value)));
                foreach (var p in phrases)
                {
                    var a = p.Analysis;
                    ps["$s"].Value = s.Path;
                    ps["$part"].Value = p.Part;
                    ps["$clip"].Value = p.Clip;
                    ps["$role"].Value = a.Role;
                    ps["$why"].Value = a.RoleWhy;
                    ps["$drums"].Value = p.Drums ? 1 : 0;
                    ps["$bars"].Value = a.Bars;
                    ps["$notes"].Value = p.Notes.Count;
                    ps["$lo"].Value = a.Lowest;
                    ps["$hi"].Value = a.Highest;
                    ps["$kt"].Value = a.Key is { } k ? k.Tonic : DBNull.Value;
                    ps["$km"].Value = a.Key is { } k2 ? (k2.Minor ? 1 : 0) : DBNull.Value;
                    ps["$kc"].Value = a.Key is { } k3 ? k3.Confidence : DBNull.Value;
                    ps["$chroma"].Value = Bytes(a.Chroma);
                    ps["$chords"].Value = PhraseChord.Encode(a.Chords);
                    ps["$feat"].Value = Bytes(a.Features, scaleToMax: false);
                    ps["$hash"].Value = a.Hash;
                    ps["$data"].Value = EncodeNotes(p.Notes);
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    /// <summary>Forgets files that are gone, or no longer in a library folder or the project library.</summary>
    public void Forget(IEnumerable<string> paths)
    {
        lock (_writeLock)
        {
            using var tx = _write.BeginTransaction();
            foreach (var p in paths) Exec(tx, "DELETE FROM sources WHERE path = $p", ("$p", p));
            tx.Commit();
        }
    }

    public int PhraseCount()
    {
        lock (_readLock)
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM phrases";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Every phrase, newest file first, without its notes.</summary>
    public IReadOnlyList<StoredPhrase> All(CancellationToken ct = default)
    {
        var list = new List<StoredPhrase>();
        var sources = new Dictionary<string, MidiSource>(StringComparer.OrdinalIgnoreCase);
        lock (_readLock)
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = """
                SELECT ph.id, ph.source, ph.part, ph.clip, ph.role, ph.role_why, y.role AS your_role, ph.bars, ph.notes, ph.lowest,
                       ph.highest, ph.key_tonic, ph.key_minor, ph.key_conf, ph.chroma, ph.chords, ph.features, ph.hash,
                       s.kind, s.daw, s.mtime, s.size, s.tempo
                FROM phrases ph JOIN sources s ON s.path = ph.source LEFT JOIN your_roles y ON y.hash = ph.hash
                ORDER BY s.mtime DESC, ph.id
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (list.Count % 4096 == 0) ct.ThrowIfCancellationRequested();
                var path = r.GetString(1);
                if (!sources.TryGetValue(path, out var source))
                    sources[path] = source = new MidiSource(path, r.GetString(18), r.IsDBNull(19) ? "" : r.GetString(19),
                                                            r.GetInt64(20), r.GetInt64(21), r.IsDBNull(22) ? null : r.GetDouble(22));
                KeyEstimate? key = r.IsDBNull(11) ? null : new KeyEstimate(r.GetInt32(11), r.GetInt32(12) == 1, r.GetDouble(13));
                list.Add(new StoredPhrase(r.GetInt64(0), source, Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5),
                                          r.IsDBNull(6) ? null : r.GetString(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10),
                                          key, (byte[])r[14], Str(r, 15), (byte[])r[16], Str(r, 17)));
            }
        }
        return list;
    }

    /// <summary>A phrase's notes, at <see cref="PhraseAnalyser.Ppq"/>.</summary>
    public IReadOnlyList<NoteEvent> Notes(long id)
    {
        lock (_readLock)
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT data FROM phrases WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() is byte[] data ? DecodeNotes(data) : [];
        }
    }

    /// <summary>Says what kind of part this is (every copy with the same notes), or null to go back to the app's guess.</summary>
    public void SetYourRole(string hash, string? role)
    {
        lock (_writeLock)
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = role is null ? "DELETE FROM your_roles WHERE hash = $h"
                : "INSERT INTO your_roles (hash, role) VALUES ($h, $r) ON CONFLICT(hash) DO UPDATE SET role = $r";
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$r", (object?)role ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    void Exec(SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

    /// <summary>
    /// Numbers from 0 to 1 as bytes (0 to 255): plenty for comparing, at a quarter of the size. Pitch-class shares are
    /// scaled so the strongest is 255; they're only ever compared as proportions.
    /// </summary>
    static byte[] Bytes(float[] values, bool scaleToMax = true)
    {
        var max = scaleToMax ? Math.Max(1e-9f, values.Max()) : 1f;
        return values.Select(v => (byte)Math.Clamp(Math.Round(v / max * 255), 0, 255)).ToArray();
    }

    public static float[] Floats(byte[] bytes) => bytes.Select(b => b / 255f).ToArray();

    /// <summary>10 bytes a note: start and length in ticks, key, velocity.</summary>
    public static byte[] EncodeNotes(IReadOnlyList<NoteEvent> notes)
    {
        var data = new byte[notes.Count * 10];
        for (var i = 0; i < notes.Count; i++)
        {
            var n = notes[i];
            BitConverter.TryWriteBytes(data.AsSpan(i * 10), (int)Math.Min(int.MaxValue, n.Start));
            BitConverter.TryWriteBytes(data.AsSpan(i * 10 + 4), (int)Math.Min(int.MaxValue, n.Length));
            data[i * 10 + 8] = (byte)n.Key;
            data[i * 10 + 9] = (byte)n.Velocity;
        }
        return data;
    }

    public static IReadOnlyList<NoteEvent> DecodeNotes(byte[] data)
    {
        var notes = new NoteEvent[data.Length / 10];
        for (var i = 0; i < notes.Length; i++)
            notes[i] = new NoteEvent(BitConverter.ToInt32(data, i * 10), BitConverter.ToInt32(data, i * 10 + 4), data[i * 10 + 8], data[i * 10 + 9]);
        return notes;
    }
}
