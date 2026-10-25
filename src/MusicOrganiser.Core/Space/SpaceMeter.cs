using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Space;

/// <summary>
/// The background job that measures plugins and song folders and keeps the sizes in the library. Sizes older than a
/// week (or older than the song's newest save) are measured again; the rest are left alone, so a scan that changed
/// nothing measures nothing.
/// </summary>
public sealed class SpaceMeter(LibraryDb db)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    /// <summary>The folders that are yours (projects and samples): never a plugin's, and never cleaned up.</summary>
    public IReadOnlyList<string> ProtectedRoots() =>
        db.Roots().Concat(db.GetList(Scanner.SampleFoldersKey)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<string> PluginRoots() =>
        db.GetList(Scanner.PluginFoldersKey) is { Count: > 0 } custom ? custom : PluginCatalog.DefaultFolders().ToList();

    /// <summary>Whether the plugins need measuring: never measured, or not this week.</summary>
    public bool PluginsStale() => db.PluginSpaceMeasured() is not { } t || DateTime.Now - t > StaleAfter;

    /// <summary>
    /// Measures the installed plugins, or only <paramref name="onlyKeys"/> (after you add or rule out a folder, or clean
    /// one up), and stores the sizes.
    /// </summary>
    public IReadOnlyList<PluginSpaceInfo> MeasurePlugins(IReadOnlyCollection<string>? onlyKeys = null, CancellationToken ct = default)
    {
        var roots = PluginRoots();
        var installed = PluginSpace.Group(PluginCatalog.ScanFiles(roots), roots, db.ScannedVendors());
        // Everything is guessed together, so a folder two plugins claim is shared either way.
        var spaces = PluginSpace.Measure(installed, PluginSpace.DataRoots(), roots, ProtectedRoots(),
            key => db.GetList(LibraryDb.AddedFoldersKey(key)), key => db.GetList(LibraryDb.RuledOutKey(key)), onlyKeys, ct);
        db.SavePluginSpaces(spaces, all: onlyKeys is null);
        return spaces;
    }

    /// <summary>
    /// Measures song folders (each one a song has to itself) whose size is missing, from before the song's newest save,
    /// or over a week old. Reports how many are done as it goes.
    /// </summary>
    public int MeasureFolders(IReadOnlyList<(string Folder, DateTime NewestSave)> folders, IProgress<(int Done, int Total)>? progress = null,
                              CancellationToken ct = default)
    {
        var known = db.FolderSpaces();
        var now = DateTime.Now;
        var todo = folders.Where(f => !known.TryGetValue(f.Folder, out var k) || k.Measured < f.NewestSave || now - k.Measured > StaleAfter)
                          .Select(f => f.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var done = new List<(string, long)>();
        foreach (var folder in todo)
        {
            ct.ThrowIfCancellationRequested();
            done.Add((folder, DiskSpace.Measure(folder, ct).Bytes));
            progress?.Report((done.Count, todo.Count));
            // Saved in batches, so stopping part way keeps what was measured.
            if (done.Count % 200 == 0) db.SaveFolderSpaces(done.TakeLast(200));
        }
        db.SaveFolderSpaces(done.TakeLast(done.Count % 200));
        return todo.Count;
    }
}
