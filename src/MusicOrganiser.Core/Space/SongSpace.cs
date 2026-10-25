using System.Security.Cryptography;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Space;

/// <summary>What a song's space can be freed from, if anything.</summary>
public enum CompactKind
{
    /// <summary>A save that's byte for byte the same as another save in the same folder.</summary>
    SameSave,
    /// <summary>A DAW's own autosave copy, older than the newest few.</summary>
    Backup,
    /// <summary>Files the DAW makes again by itself when the song opens (Studio One's Cache folder).</summary>
    Cache,
    /// <summary>Audio in the song's folder that no save seems to use. Shown only: samples are never removed.</summary>
    UnusedAudio,
}

/// <summary>Something in a song's folder that could go. <paramref name="Paths"/>: one file, or several shown as one line.</summary>
public sealed record CompactItem(CompactKind Kind, IReadOnlyList<string> Paths, long Bytes, string Note)
{
    /// <summary>Removable without losing anything; unused audio is only shown.</summary>
    public bool CanRemove => Kind != CompactKind.UnusedAudio;
}

/// <summary>Where a song's space goes, and what could be freed (see <see cref="SongSpace.Measure"/>).</summary>
public sealed record SongSpaceReport(long Bytes, bool OwnFolder, string Folder, long Saves, long Backups, long Caches, long Audio, long Other,
                                     IReadOnlyList<CompactItem> Items)
{
    public long Freeable => Items.Where(i => i.CanRemove).Sum(i => i.Bytes);
}

/// <summary>
/// Measures a song: its saves, the DAW's autosaves of it, and (when the song has a folder to itself) everything in that
/// folder. Then lists what can go without losing any music: saves that are exact copies of another, autosaves beyond the
/// newest few, and caches the DAW rebuilds. Your newest save is never on the list, and neither is any sample.
/// </summary>
public static class SongSpace
{
    /// <summary>One save of the song.</summary>
    public sealed record Save(string Path, long Size, DateTime Modified);

    public const int DefaultKeepBackups = 5;

    /// <param name="saves">The song's saves.</param>
    /// <param name="roots">Your project folders: a song never has one of these to itself.</param>
    /// <param name="usedAudio">
    /// Every sample path the song's saves use, to spot audio nothing uses. Projects store paths as they were on the PC that
    /// saved them, so a file counts as used when any save names a file of that name.
    /// </param>
    /// <param name="keepBackups">How many of the newest autosaves stay.</param>
    public static SongSpaceReport Measure(IReadOnlyList<Save> saves, IReadOnlyList<string> roots, IReadOnlySet<string> usedAudio,
                                          int keepBackups = DefaultKeepBackups, CancellationToken ct = default) => Parallelism.Background(() =>
    {
        if (saves.Count == 0) return new SongSpaceReport(0, false, "", 0, 0, 0, 0, 0, []);
        var newest = saves.MaxBy(s => s.Modified)!;
        var folder = Path.GetDirectoryName(newest.Path) ?? "";
        var savePaths = saves.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var own = OwnsFolder(folder, savePaths, roots);

        long savesBytes = 0, backupBytes = 0, cacheBytes = 0, audioBytes = 0, other = 0;
        var backups = new List<(string Path, long Size, DateTime Modified)>();
        var unusedAudio = new List<(string Path, long Size)>();
        string? cache = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedNames = usedAudio.Select(u => Path.GetFileName(u.Replace('\\', '/'))).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (own)
        {
            var isStudioOne = Directory.EnumerateFiles(folder, "*.song").Any();
            foreach (var (path, size, modified) in Walk(folder, ct))
            {
                seen.Add(path);
                var rel = Path.GetRelativePath(folder, path).Split(['\\', '/']);
                if (savePaths.Contains(path)) savesBytes += size;
                else if (rel.Length > 1 && rel[..^1].Any(Scanner.IsAutosaveDir) || Scanner.IsAutosaveFile(rel[^1]))
                {
                    backupBytes += size;
                    backups.Add((path, size, modified));
                }
                else if (isStudioOne && rel.Length > 1 && rel[0].Equals("Cache", StringComparison.OrdinalIgnoreCase))
                {
                    cacheBytes += size;
                    cache = Path.Combine(folder, rel[0]);
                }
                else if (TextScan.IsAudioPath(path))
                {
                    audioBytes += size;
                    if (!usedAudio.Contains(path) && !usedNames.Contains(Path.GetFileName(path))) unusedAudio.Add((path, size));
                }
                else other += size;
            }
        }
        else
        {
            foreach (var s in saves)
                if (seen.Add(s.Path)) savesBytes += File.Exists(s.Path) ? new FileInfo(s.Path).Length : s.Size;
        }

        // FL Studio keeps its autosaves in one Backup folder for all songs (Projects\Backup), named after the song.
        foreach (var b in FlBackups(saves, roots))
            if (seen.Add(b.Path))
            {
                backupBytes += b.Size;
                backups.Add(b);
            }

        var items = new List<CompactItem>();
        items.AddRange(SameSaves(saves, newest, ct));
        // The newest few autosaves stay: they're what you'd reach for after a crash.
        foreach (var b in backups.OrderByDescending(b => b.Modified).Skip(Math.Max(0, keepBackups)))
            items.Add(new(CompactKind.Backup, [b.Path], b.Size, $"Autosave from {b.Modified:d MMM yyyy}"));
        if (cache is not null && cacheBytes > 0)
            items.Add(new(CompactKind.Cache, [cache], cacheBytes, "Studio One makes these again when the song opens"));
        if (unusedAudio.Count > 0)
            items.Add(new(CompactKind.UnusedAudio, unusedAudio.Select(a => a.Path).ToList(), unusedAudio.Sum(a => a.Size),
                          unusedAudio.Count == 1 ? "1 audio file no save seems to use" : $"{unusedAudio.Count} audio files no save seems to use"));
        var total = savesBytes + backupBytes + cacheBytes + audioBytes + other;
        return new SongSpaceReport(total, own, folder, savesBytes, backupBytes, cacheBytes, audioBytes, other, items);
    });

    /// <summary>
    /// Whether the folder is the song's own: none of your project folders, and every project file directly in it is one
    /// of the song's saves. Bitwig and Studio One give each song a folder; FL Studio songs often share one.
    /// </summary>
    public static bool OwnsFolder(string folder, IReadOnlySet<string> savePaths, IReadOnlyList<string> roots)
    {
        if (folder.Length == 0 || !Directory.Exists(folder)) return false;
        if (roots.Any(r => DiskSpace.IsInside(r, folder))) return false;   // a project folder, or one holding it
        try
        {
            return Directory.EnumerateFiles(folder).Where(Scanner.IsProjectFile).All(savePaths.Contains);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static IEnumerable<(string Path, long Size, DateTime Modified)> Walk(string folder, CancellationToken ct)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var entries = new System.IO.Enumeration.FileSystemEnumerable<(string, long, DateTime)>(folder,
            (ref System.IO.Enumeration.FileSystemEntry e) => (e.ToFullPath(), e.Length, e.LastWriteTimeUtc.LocalDateTime), options)
        { ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry e) => !e.IsDirectory };
        var n = 0;
        foreach (var e in entries)
        {
            if ((++n & 1023) == 0) ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }

    /// <summary>
    /// FL Studio's autosaves of the song: "Song (autosave).flp" and "Song (overwritten at 2213h05).flp" in a Backup folder
    /// beside the song or in a folder above it, up to your project folder.
    /// </summary>
    static IEnumerable<(string Path, long Size, DateTime Modified)> FlBackups(IReadOnlyList<Save> saves, IReadOnlyList<string> roots)
    {
        var stems = saves.Where(s => s.Path.EndsWith(".flp", StringComparison.OrdinalIgnoreCase))
                         .Select(s => Path.GetFileNameWithoutExtension(s.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (stems.Count == 0) yield break;
        var dirs = saves.Select(s => Path.GetDirectoryName(s.Path) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var backupDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in dirs)
        {
            for (var dir = start; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                var b = Path.Combine(dir, "Backup");
                if (Directory.Exists(b)) backupDirs.Add(b);
                if (roots.Any(r => DiskSpace.IsInside(r, dir)) || backupDirs.Count > 4) break;   // up to the project folder
            }
        }
        foreach (var b in backupDirs)
        {
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(b).EnumerateFiles("*.flp").ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f.Name);
                var cut = name.IndexOf(" (autosave)", StringComparison.OrdinalIgnoreCase) is >= 0 and var a ? a
                        : name.IndexOf(" (overwritten at ", StringComparison.OrdinalIgnoreCase);
                if (cut > 0 && stems.Contains(name[..cut])) yield return (f.FullName, f.Length, f.LastWriteTime);
            }
        }
    }

    /// <summary>
    /// Saves that are exact copies of another save in the same folder (a DAW saving with nothing changed). Of each set the
    /// newest stays, so the save you'd open is never on the list. Only files of the same size are read, to compare them.
    /// </summary>
    static IEnumerable<CompactItem> SameSaves(IReadOnlyList<Save> saves, Save newest, CancellationToken ct)
    {
        var sets = saves.Where(s => File.Exists(s.Path))
                        .GroupBy(s => (Folder: (Path.GetDirectoryName(s.Path) ?? "").ToLowerInvariant(), Size: new FileInfo(s.Path).Length))
                        .Where(g => g.Count() > 1);
        foreach (var bySize in sets)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var same in bySize.GroupBy(s => Hash(s.Path)).Where(g => g.Key.Length > 0 && g.Count() > 1))
            {
                var keep = same.Any(s => s == newest) ? newest : same.MaxBy(s => s.Modified)!;
                foreach (var s in same.Where(s => s != keep))
                    yield return new(CompactKind.SameSave, [s.Path], bySize.Key.Size, $"Same as {Path.GetFileName(keep.Path)}");
            }
        }
    }

    static string Hash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }
}
