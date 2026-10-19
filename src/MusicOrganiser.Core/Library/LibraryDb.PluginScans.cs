using MusicOrganiser.Core.Plugins;

namespace MusicOrganiser.Core.Library;

// What the native plugin scanner found inside each plugin file. A file is only read again when its
// size or modified time changes.
public sealed partial class LibraryDb
{
    const string PluginScanSchema = """
        CREATE TABLE IF NOT EXISTS plugin_scans (
            path TEXT PRIMARY KEY COLLATE NOCASE, size INTEGER, mtime INTEGER, scanned INTEGER, error TEXT
        );
        CREATE TABLE IF NOT EXISTS scanned_plugins (
            path TEXT COLLATE NOCASE, format TEXT, id TEXT, name TEXT, name_key TEXT, vendor TEXT, version TEXT,
            kind TEXT, json TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_scanned_plugins_path ON scanned_plugins(path);
        CREATE INDEX IF NOT EXISTS ix_scanned_plugins_key ON scanned_plugins(name_key);
        """;

    /// <summary>The size and modified time each plugin file had when it was last read.</summary>
    public Dictionary<string, PluginStamp> PluginScanStamps() =>
        Query("SELECT path, size, mtime FROM plugin_scans", r => ((string)r["path"], new PluginStamp(Long(r, "size"), Long(r, "mtime"))))
            .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);

    /// <summary>Stores what the scanner found in a file, replacing what it found before.</summary>
    public void SavePluginScan(PluginFileScan scan, PluginStamp stamp)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using (var del = Cmd("DELETE FROM scanned_plugins WHERE path = $p", ("$p", scan.Path))) { del.Transaction = tx; del.ExecuteNonQuery(); }
            using (var cmd = Cmd("""
                INSERT INTO plugin_scans VALUES ($p, $s, $m, $t, $e)
                ON CONFLICT(path) DO UPDATE SET size = $s, mtime = $m, scanned = $t, error = $e
                """, ("$p", scan.Path), ("$s", stamp.Size), ("$m", stamp.Modified),
                     ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$e", scan.Error)))
            {
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            foreach (var p in scan.Plugins)
            {
                using var cmd = Cmd("INSERT INTO scanned_plugins VALUES ($p, $f, $i, $n, $k, $v, $ver, $kind, $j)",
                    ("$p", scan.Path), ("$f", p.Format), ("$i", p.Id), ("$n", p.Name), ("$k", NameKey(p.Name)),
                    ("$v", p.Vendor), ("$ver", p.Version), ("$kind", p.Kind), ("$j", p.Json));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Replaces one plugin's stored line, e.g. once its parameters have been read. A line without parameters (why
    /// the plugin couldn't be loaded) never replaces one that has them, read meanwhile by another try.
    /// </summary>
    public void SaveScannedPlugin(ScannedPlugin p) =>
        Exec("UPDATE scanned_plugins SET json = $j WHERE path = $p AND id = $i" +
             (p.Params is null ? " AND CASE WHEN json_valid(json) THEN coalesce(json_type(json, '$.params'), '') != 'array' ELSE 1 END" : ""),
             ("$j", p.Json), ("$p", p.Path), ("$i", p.Id));

    // Still to read: no parameters yet, and no note that the scanner couldn't load it.
    const string ParametersToRead = """
        CASE WHEN json_valid(json) THEN json_type(json, '$.params') IS NULL AND coalesce(json_extract(json, '$.error'), '') = '' ELSE 0 END
        """;

    /// <summary>
    /// Installed plugins whose parameters haven't been read, leaving out the ones the scanner couldn't load
    /// last time (Read parameters on one of those tries again).
    /// </summary>
    public IReadOnlyList<ScannedPlugin> PluginsWithoutParameters() =>
        Query($"SELECT path, json FROM scanned_plugins WHERE {ParametersToRead} ORDER BY name, format, path",
              r => ScannedPlugin.Parse(Str(r, "json")) is { } p ? p with { Path = Str(r, "path") } : null)
            .OfType<ScannedPlugin>().ToList();

    /// <summary>How many installed plugins have their parameters read, and how many are still to read.</summary>
    public (int Read, int Left) ParameterCounts() => Query($"""
        SELECT coalesce(SUM(CASE WHEN json_valid(json) THEN json_type(json, '$.params') = 'array' ELSE 0 END), 0) AS done,
               coalesce(SUM({ParametersToRead}), 0) AS todo
        FROM scanned_plugins
        """, r => ((int)Long(r, "done"), (int)Long(r, "todo"))).First();

    /// <summary>Forgets files that are no longer in the plugin folders.</summary>
    public void ForgetPluginScans(IEnumerable<string> keep)
    {
        var present = keep.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = Query("SELECT path FROM plugin_scans", r => (string)r["path"]).Where(p => !present.Contains(p)).ToList();
        if (gone.Count == 0) return;
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var path in gone)
                foreach (var sql in new[] { "DELETE FROM plugin_scans WHERE path = $p", "DELETE FROM scanned_plugins WHERE path = $p" })
                {
                    using var cmd = Cmd(sql, ("$p", path));
                    cmd.Transaction = tx;
                    cmd.ExecuteNonQuery();
                }
            tx.Commit();
        }
    }

    /// <summary>The names of every plugin found inside the plugin files (each one in a Waves shell, say).</summary>
    public IReadOnlyList<string> ScannedPluginNames() =>
        Query("SELECT DISTINCT name FROM scanned_plugins WHERE name != ''", r => (string)r["name"]);

    /// <summary>
    /// Installed plugins that a project's plugin name refers to: the same name, or the same name
    /// with or without the vendor in front ("FabFilter Pro-Q 3" and "Pro-Q 3").
    /// </summary>
    public IReadOnlyList<ScannedPlugin> ScannedPluginsNamed(string name)
    {
        var key = NameKey(name);
        if (key.Length == 0) return [];
        return Query("""
            SELECT path, json FROM scanned_plugins
            WHERE name_key = $k
               OR (length(name_key) >= 4 AND length($k) > length(name_key) AND substr($k, -length(name_key)) = name_key)
               OR (length($k) >= 4 AND length(name_key) > length($k) AND substr(name_key, -length($k)) = $k)
            ORDER BY name_key = $k DESC, format, name, path
            """, r => ScannedPlugin.Parse(Str(r, "json")) is { } p ? p with { Path = Str(r, "path") } : null, ("$k", key))
            .OfType<ScannedPlugin>().ToList();
    }

    /// <summary>
    /// How many plugin files have been read, how many plugins they hold, and how many plugin files
    /// couldn't be read (not counting the other DLLs that live in VST2 folders).
    /// </summary>
    public (int Files, int Plugins, int Failed) PluginScanCounts() => Query("""
        SELECT (SELECT COUNT(*) FROM plugin_scans) AS files, (SELECT COUNT(*) FROM scanned_plugins) AS plugins,
               (SELECT COUNT(*) FROM plugin_scans WHERE error != '' AND error NOT LIKE 'not a VST2 plugin%'
                  AND path NOT IN (SELECT path FROM scanned_plugins)) AS failed
        """, r => ((int)Long(r, "files"), (int)Long(r, "plugins"), (int)Long(r, "failed"))).First();
}
