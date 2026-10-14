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
    /// The app's copy, or null when there's none yet. The first time, it takes a copy of the project picked before the app
    /// kept its own (the <see cref="ProjectDetailViewModel.BitwigTemplateKey"/> setting still names it), and if that can't
    /// be copied it uses it where it is.
    /// </summary>
    public static string? Find(LibraryDb db, string dataFolder)
    {
        var mine = PathIn(dataFolder);
        if (File.Exists(mine)) return mine;
        if (db.GetSetting(ProjectDetailViewModel.BitwigTemplateKey) is not { Length: > 0 } picked || !File.Exists(picked)) return null;
        try { return Keep(db, picked, dataFolder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return picked; }
    }

    /// <summary>
    /// Copies <paramref name="file"/> in as the template, replacing the app's copy, and remembers where it came from.
    /// Returns the app's copy.
    /// </summary>
    public static string Keep(LibraryDb db, string file, string dataFolder)
    {
        var mine = PathIn(dataFolder);
        if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(mine), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
            // Copied next to it first, so a copy that fails part way never leaves a broken template behind.
            var part = mine + ".part";
            File.Copy(file, part, overwrite: true);
            File.Move(part, mine, overwrite: true);
            db.SetSetting(ProjectDetailViewModel.BitwigTemplateKey, file);
        }
        return mine;
    }
}
