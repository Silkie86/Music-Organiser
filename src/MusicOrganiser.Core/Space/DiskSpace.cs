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
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            var entries = new FileSystemEnumerable<(string Path, long Size)>(path,
                (ref FileSystemEntry e) => (e.ToFullPath(), e.Length), options)
            { ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory };
            foreach (var (file, size) in entries)
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
