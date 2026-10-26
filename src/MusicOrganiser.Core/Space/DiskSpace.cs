using System.IO.Enumeration;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Space;

/// <summary>How much room files and folders take, and how to say it ("38.2 GB").</summary>
public static class DiskSpace
{
    /// <summary>What a file or folder holds: every byte, the bytes in sound files, and how many files.</summary>
    public readonly record struct Measured(long Bytes, long SoundBytes, int Files)
    {
        public static Measured operator +(Measured a, Measured b) => new(a.Bytes + b.Bytes, a.SoundBytes + b.SoundBytes, a.Files + b.Files);
    }

    /// <summary>
    /// Sample-library formats besides plain audio: Kontakt's (.ncw, .nkx, .nkc, .nkr), UVI's (.ufs), SoundFonts, REX loops,
    /// Native Instruments' library files. Big files (<see cref="BigFile"/>) count as sounds whatever they're called
    /// (Spectrasonics' STEAM, Toontrack's packs): presets and settings are small.
    /// </summary>
    static readonly HashSet<string> SoundExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ncw", ".nkx", ".nkc", ".nkr", ".ufs", ".sf2", ".sf3", ".rx2", ".rex", ".exs", ".sfz", ".wv", ".nicnt" };

    const long BigFile = 16L << 20;

    /// <summary>Whether a file is sound content (a sample or a sample library) rather than presets or settings.</summary>
    public static bool IsSound(string path, long size) =>
        size >= BigFile || TextScan.IsAudioPath(path) || SoundExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Whether a file or folder is a link to another place (a junction or symbolic link), even one whose place has gone.
    /// OneDrive marks its files and folders with the same "reparse point" flag without them being links, so on Windows
    /// the flag alone doesn't say: the link's target does.
    /// </summary>
    public static bool IsLink(FileSystemInfo info)
    {
        try { return info.LinkTarget is not null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // A folder met on a walk that is a link to another place: not looked into, so nothing is counted twice.
    static bool IsLinkedFolder(ref FileSystemEntry e) =>
        (e.Attributes & FileAttributes.ReparsePoint) != 0 && (!OperatingSystem.IsWindows() || IsLink(new DirectoryInfo(e.ToFullPath())));

    // Windows' "offline" and "recall on data access": a OneDrive file that's only online, so it takes no room here.
    const FileAttributes OnlineOnly = FileAttributes.Offline | (FileAttributes)0x00400000;

    // A file met on a walk that takes room here. On Windows a link to a file has no size of its own, and OneDrive's files
    // carry the link flag too, so only the ones that are only online are left out; elsewhere the flag means a link.
    static bool TakesRoom(ref FileSystemEntry e) =>
        OperatingSystem.IsWindows() ? !IsOnlineOnly(e.Attributes) : (e.Attributes & FileAttributes.ReparsePoint) == 0;

    /// <summary>Whether a file is a OneDrive file that's only online: it takes no room here, and reading it downloads it.</summary>
    public static bool IsOnlineOnly(FileAttributes attributes) => (attributes & OnlineOnly) != 0;

    /// <summary>
    /// Whether any file in a folder or its folders is a OneDrive file that's only online, which the size walks leave out
    /// and copying would download first. Links to other places aren't followed. Only Windows has them.
    /// </summary>
    public static bool HasOnlineOnly(string folder, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(folder)) return false;
        var seen = 0;
        try
        {
            var online = new FileSystemEnumerable<bool>(folder, (ref FileSystemEntry _) => true,
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) =>
                {
                    if ((++seen & 1023) == 0) ct.ThrowIfCancellationRequested();
                    return !e.IsDirectory && IsOnlineOnly(e.Attributes);
                },
                ShouldRecursePredicate = (ref FileSystemEntry e) => !IsLinkedFolder(ref e),
            };
            return online.Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Every file in a folder and its folders, as the size walks see them: links to other places (junctions) aren't
    /// followed, OneDrive files that are only online are left out, and folders it can't read are skipped.
    /// </summary>
    public static FileSystemEnumerable<T> Files<T>(string folder, FileSystemEnumerable<T>.FindTransform transform) =>
        new(folder, transform, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && TakesRoom(ref e),
            ShouldRecursePredicate = (ref FileSystemEntry e) => !IsLinkedFolder(ref e),
        };

    /// <summary>
    /// Adds up a file, or everything in a folder and its folders. Links to other folders (junctions) aren't followed,
    /// so nothing is counted twice, and folders it can't read are left out. Runs in one of the background slots.
    /// </summary>
    public static Measured Measure(string path, CancellationToken ct = default) => Parallelism.Background(() =>
    {
        if (File.Exists(path))
        {
            try
            {
                var len = new FileInfo(path).Length;
                return new Measured(len, IsSound(path, len) ? len : 0, 1);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; }
        }
        if (!Directory.Exists(path)) return default;
        long bytes = 0, sound = 0;
        var files = 0;
        try
        {
            foreach (var (file, size) in Files(path, (ref FileSystemEntry e) => (e.ToFullPath(), e.Length)))
            {
                if ((++files & 1023) == 0) ct.ThrowIfCancellationRequested();
                bytes += size;
                if (IsSound(file, size)) sound += size;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new Measured(bytes, sound, files);
    });

    /// <summary>"38.2 GB", "610 MB", "12 KB": three figures at most, as Explorer shows sizes.</summary>
    public static string Text(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        if (unit == 0) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        return value >= 100 ? $"{value:0} {units[unit]}" : value >= 10 ? $"{value:0.#} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or somewhere inside it.</summary>
    public static bool IsInside(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(path.Replace('\\', '/'));
        var f = Path.TrimEndingDirectorySeparator(folder.Replace('\\', '/'));
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f.EndsWith('/') ? f : f + "/", StringComparison.OrdinalIgnoreCase);
    }
}
