using System.IO;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// The Bitwig project FL Studio songs are converted with (see <see cref="BitwigProject"/>). The app keeps its own copy in
/// its data folder, so it always finds it, moving or deleting the original doesn't lose it, and Settings can open it in
/// Bitwig to change it.
/// </summary>
public static class BitwigTemplateStore
{
    public const string FileName = "Music Organiser template.bwproject";

    /// <summary>Where the app keeps its copy: Bitwig template\Music Organiser template.bwproject in its data folder.</summary>
    public static string PathIn(string dataFolder) => Path.Combine(dataFolder, "Bitwig template", FileName);

    /// <summary>
    /// The app's copy, or null when there's none yet. The project it was taken from (the
    /// <see cref="ProjectDetailViewModel.BitwigTemplateKey"/> setting names it) is copied in again when it was saved
    /// after the copy was, so whichever was changed last is used: the original, opened from Bitwig's recent projects
    /// as before, or the app's copy, changed with Edit in Bitwig. The first time there's no copy yet; when it can't be
    /// copied, the original is used where it is.
    /// </summary>
    public static string? Find(LibraryDb db, string dataFolder)
    {
        var mine = PathIn(dataFolder);
        var picked = db.GetSetting(ProjectDetailViewModel.BitwigTemplateKey) is { Length: > 0 } p && File.Exists(p) && !Same(p, mine) ? p : null;
        if (File.Exists(mine))
        {
            if (picked is null || File.GetLastWriteTimeUtc(picked) <= File.GetLastWriteTimeUtc(mine)) return mine;
            try { return Keep(db, picked, dataFolder); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return mine; }
        }
        if (picked is null) return null;
        try { return Keep(db, picked, dataFolder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return picked; }
    }

    /// <summary>
    /// Copies <paramref name="file"/> in as the template, replacing the app's copy, and remembers where it came from.
    /// The copy is dated as the original, so a later save of either is the newer one, and it's never read-only,
    /// so Bitwig can save it and it can be replaced. Returns the app's copy.
    /// </summary>
    public static string Keep(LibraryDb db, string file, string dataFolder)
    {
        var mine = PathIn(dataFolder);
        if (!Same(file, mine))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
            // Copied next to it first, so a copy that fails part way never leaves a broken template behind.
            var part = mine + ".part";
            Writable(part);
            File.Copy(file, part, overwrite: true);
            Writable(part);
            File.SetLastWriteTimeUtc(part, File.GetLastWriteTimeUtc(file));
            Writable(mine);
            File.Move(part, mine, overwrite: true);
            db.SetSetting(ProjectDetailViewModel.BitwigTemplateKey, file);
        }
        return mine;
    }

    static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Clears a file's read-only flag (a copy takes the original's), if it's there.</summary>
    static void Writable(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path) is var a && a.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
    }
}
