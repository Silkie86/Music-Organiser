using System.Collections.Concurrent;

namespace MusicOrganiser.Core;

/// <summary>
/// Spreads scanning and analysis across the CPU, without taking it all: the window must keep answering while a scan,
/// Deep listen or indexing runs (Solomon, 2026-10-15: "don't allow any scan to take up so many resources that the
/// program becomes laggy").
/// </summary>
public static class Parallelism
{
    public static int Cores => Environment.ProcessorCount;

    /// <summary>How hard background work may push the computer (Settings › Performance).</summary>
    public enum Pace { Gentle, Balanced, Fast }

    public const string Gentle = "Gentle", Balanced = "Balanced", Fast = "Fast";

    /// <summary>Settings › Performance's choices, as saved.</summary>
    public static IReadOnlyList<string> PaceChoices { get; } = [Gentle, Balanced, Fast];

    static Pace _pace = Pace.Gentle;
    static volatile bool _inBackground;
    static volatile int _workers = LimitFor(Pace.Gentle, false);

    /// <summary>
    /// Gentle (the default): half the processor's threads, so the window, the galaxy and playback stay smooth even on
    /// a processor whose threads share cores (Solomon, 2026-10-15: "its still laggy while scanning sample folder the cpu
    /// is at max all threads"). Balanced: all but two. Fast: every thread while the app is in the background, Balanced
    /// while you're using it.
    /// </summary>
    public static Pace Mode
    {
        get => _pace;
        set { _pace = value; Update(); }
    }

    /// <summary>Set by the window: true while it's minimised or another app is in front, which is when Fast uses everything.</summary>
    public static bool AppInBackground
    {
        get => _inBackground;
        set { _inBackground = value; Update(); }
    }

    public static Pace ParsePace(string? saved) => saved switch { Balanced => Pace.Balanced, Fast => Pace.Fast, _ => Pace.Gentle };

    static int LimitFor(Pace pace, bool background) => pace switch
    {
        Pace.Fast when background => Cores,
        Pace.Gentle => Math.Max(1, Cores / 2),
        _ => Math.Max(1, Cores - 2),
    };

    static void Update()
    {
        _workers = LimitFor(_pace, _inBackground);
        lock (Gate) Monitor.PulseAll(Gate);   // more room: the work waiting for a slot can go
    }

    /// <summary>How many pieces of background work may run at once, across every job together (see <see cref="Mode"/>).</summary>
    public static int Workers => _workers;

    // Shared by every job, so a scan and Deep listen running together still stay within the limit.
    static readonly object Gate = new();
    static int _running;
    [ThreadStatic] static int _depth;

    static Parallelism()
    {
        // Jobs waiting for a slot hold thread-pool threads; enough threads up front means the window's own
        // background reads (Task.Run) never queue behind them while the pool slowly grows.
        ThreadPool.GetMinThreads(out var worker, out var io);
        ThreadPool.SetMinThreads(Math.Max(worker, 3 * Cores), io);
    }

    /// <summary>
    /// Called once on the window's thread: puts it above normal priority. Some libraries (SoundFingerprinting) spread
    /// their own work over every core at normal priority, beyond the slots above; this way the window still comes first.
    /// </summary>
    public static void FavourThisThread()
    {
        try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; }
        catch (Exception) { }   // not allowed on some systems; the slots still apply
    }

    public static ParallelOptions Options(CancellationToken ct = default) =>
        new() { CancellationToken = ct, MaxDegreeOfParallelism = Workers };

    /// <summary>Set while a sound is playing. Background work always steps aside now; kept for the players that set it.</summary>
    public static volatile bool AudioPlaying;

    /// <summary>
    /// Runs one piece of background work below normal priority, in one of the shared <see cref="Workers"/> slots, so
    /// the window, the galaxy and playback always get the CPU first.
    /// </summary>
    public static T Background<T>(Func<T> work)
    {
        // Already in a slot: work started from background work never waits on itself. And the window's own thread (the
        // only one with a synchronisation context) takes part in a search it started without waiting or being slowed.
        if (_depth > 0 || SynchronizationContext.Current is not null) return work();
        lock (Gate)
        {
            while (_running >= _workers) Monitor.Wait(Gate);
            _running++;
        }
        _depth++;
        var thread = Thread.CurrentThread;
        var before = thread.Priority;
        try
        {
            thread.Priority = ThreadPriority.BelowNormal;
            return work();
        }
        finally
        {
            thread.Priority = before;
            _depth--;
            lock (Gate)
            {
                _running--;
                Monitor.Pulse(Gate);
            }
        }
    }

    public static void Background(Action work) => Background(() => { work(); return 0; });

    /// <summary>
    /// The "._" file a Mac leaves beside each file it copies to a shared drive: Finder details only, never the sound, song
    /// or MIDI its name says.
    /// </summary>
    public static bool IsMacShadow(string path) => Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal);

    /// <summary>A hidden folder (".git"), or the "__MACOSX" folder a zip made on a Mac holds its "._" files in.</summary>
    public static bool IsHiddenFolder(string name) =>
        name.StartsWith('.') || name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase);

    /// <summary>A file found by <see cref="FindFileStamps"/>: its size and last-modified time (Unix seconds) came with the folder listing.</summary>
    public readonly record struct FoundFile(string Path, long Size, long Mtime);

    /// <summary>
    /// Walks folder trees with every core at once, a level at a time, and returns the files
    /// <paramref name="keepFile"/> accepts, sorted by path. <paramref name="skipDir"/> gets a
    /// folder's name and its parent's name. <paramref name="progress"/> hears how many files
    /// have been found and the folder being read, every couple of hundred folders.
    /// </summary>
    public static List<string> FindFiles(IEnumerable<string> roots, Func<string, bool> keepFile, Func<string, string, bool> skipDir,
                                         Action<int, string>? progress = null, CancellationToken ct = default,
                                         ConcurrentBag<string>? unreadable = null) =>
        FindFileStamps(roots, keepFile, skipDir, progress, ct, unreadable).Select(f => f.Path).ToList();

    /// <summary>
    /// <see cref="FindFiles"/> with each file's size and modified time. On Windows these come with the folder listing
    /// itself, so checking 357,000 sounds for changes doesn't ask the disk about each file again.
    /// </summary>
    public static List<FoundFile> FindFileStamps(IEnumerable<string> roots, Func<string, bool> keepFile, Func<string, string, bool> skipDir,
                                                 Action<int, string>? progress = null, CancellationToken ct = default,
                                                 ConcurrentBag<string>? unreadable = null)
    {
        var found = new ConcurrentBag<FoundFile>();
        var folders = 0;
        var level = roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // As Directory.GetFiles lists them: hidden and system files too, and a folder that can't be read is an error.
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        while (level.Count > 0)
        {
            var next = new ConcurrentBag<string>();
            Parallel.ForEach(level, Options(ct), dir => Background(() =>
            {
                List<(string Path, bool IsDir, long Size, long Mtime)> entries;
                try
                {
                    entries = new System.IO.Enumeration.FileSystemEnumerable<(string, bool, long, long)>(dir,
                        (ref System.IO.Enumeration.FileSystemEntry e) =>
                            (e.ToFullPath(), e.IsDirectory, e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.ToUnixTimeSeconds()),
                        options).ToList();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { unreadable?.Add(dir); return; }
                var parent = Path.GetFileName(dir.TrimEnd('\\', '/'));
                foreach (var e in entries)
                {
                    if (!e.IsDir) { if (keepFile(e.Path)) found.Add(new(e.Path, e.Size, e.Mtime)); }
                    else if (!skipDir(Path.GetFileName(e.Path), parent)) next.Add(e.Path);
                }
                if (Interlocked.Increment(ref folders) % 200 == 0) progress?.Invoke(found.Count, dir);
            }));
            level = [.. next];
        }
        return found.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
