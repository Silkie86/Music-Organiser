namespace MusicOrganiser.Core.Library;

/// <summary>
/// Watches folders (and everything under them) for files appearing, changing, moving or going, and reports them
/// together once the folders have been quiet for a moment: a DAW writes a project in several steps, and a sample pack
/// unzips thousands of files. This is how the app keeps up with your projects and samples while it's open, instead of
/// walking every folder again (the same trick as Plex's "update my library automatically" or VS Code's file watching).
/// </summary>
/// <remarks>
/// Windows keeps a limited list of changes for each watcher. When a lot changes at once the list overflows and the
/// files are no longer named; the folder is then reported as <c>Overflowed</c>, so the caller walks it instead. A folder
/// moved or renamed inside a watched one is reported the same way, since its files aren't named one by one.
/// Reports arrive on the thread that created the watch when it has a synchronisation context (the window's thread).
/// </remarks>
public sealed class FolderWatch : IDisposable
{
    public sealed record Changes(IReadOnlyCollection<string> Files, IReadOnlyCollection<string> Overflowed);

    readonly List<FileSystemWatcher> _watchers = [];
    readonly Func<string, bool> _wanted;
    readonly Action<Changes> _report;
    readonly TimeSpan _quiet;
    readonly SynchronizationContext? _context = SynchronizationContext.Current;
    readonly Timer _timer;
    readonly object _lock = new();
    HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> _overflowed = new(StringComparer.OrdinalIgnoreCase);
    bool _disposed;

    /// <param name="folders">The folders to watch; ones that don't exist (an unplugged drive) are left out.</param>
    /// <param name="wanted">Which files matter, e.g. project files or sounds.</param>
    /// <param name="report">Hears what changed, once things have been quiet for <paramref name="quiet"/>.</param>
    public FolderWatch(IEnumerable<string> folders, Func<string, bool> wanted, Action<Changes> report, TimeSpan quiet)
    {
        _wanted = wanted;
        _report = report;
        _quiet = quiet;
        _timer = new Timer(_ => Flush());
        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var w = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,   // the most Windows allows for a folder on another computer
                };
                w.Created += (_, e) => Touched(e.FullPath);
                w.Changed += (_, e) => Touched(e.FullPath);
                w.Deleted += (_, e) => Gone(e.FullPath, folder);
                w.Renamed += (_, e) => Renamed(e.OldFullPath, e.FullPath, folder);
                w.Error += (_, _) => Overflow(folder);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Can't be watched (some network drives): changes there are found by the next scan, as before.
            }
        }
    }

    /// <summary>How many folders are being watched.</summary>
    public int Count => _watchers.Count;

    void Touched(string path)
    {
        if (Directory.Exists(path)) return;   // a folder's own date changed; its files report themselves
        if (_wanted(path)) Add(path, null);
    }

    void Gone(string path, string root)
    {
        if (_wanted(path)) Add(path, null);
        // A folder deleted: its files aren't named one by one (and it can't be told from a file any more, except by
        // having no extension, as folders usually do).
        else if (!Path.HasExtension(path)) Add(null, root);
    }

    void Renamed(string from, string to, string root)
    {
        if (Directory.Exists(to)) { Add(null, root); return; }
        if (_wanted(from)) Add(from, null);
        if (_wanted(to)) Add(to, null);
    }

    void Overflow(string root) => Add(null, root);

    void Add(string? file, string? overflowed)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (file is not null) _files.Add(file);
            if (overflowed is not null) _overflowed.Add(overflowed);
            _timer.Change(_quiet, Timeout.InfiniteTimeSpan);   // wait until it has been quiet this long
        }
    }

    void Flush()
    {
        Changes changes;
        lock (_lock)
        {
            if (_disposed || _files.Count + _overflowed.Count == 0) return;
            changes = new([.. _files.Order(StringComparer.OrdinalIgnoreCase)], [.. _overflowed]);
            _files = new(StringComparer.OrdinalIgnoreCase);
            _overflowed = new(StringComparer.OrdinalIgnoreCase);
        }
        if (_context is { } ui) ui.Post(_ => _report(changes), null);
        else _report(changes);
    }

    public void Dispose()
    {
        lock (_lock) _disposed = true;
        _timer.Dispose();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}
