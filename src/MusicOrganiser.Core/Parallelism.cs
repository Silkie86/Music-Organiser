using System.Collections.Concurrent;

namespace MusicOrganiser.Core;

/// <summary>Spreads scanning and analysis across every CPU core.</summary>
public static class Parallelism
{
    public static int Cores => Environment.ProcessorCount;

    public static ParallelOptions Options(CancellationToken ct = default) =>
        new() { CancellationToken = ct, MaxDegreeOfParallelism = Cores };

    /// <summary>
    /// Runs one piece of background work below normal priority, so sound playback and the
    /// window always get the CPU first while every core is busy scanning.
    /// </summary>
    public static T Background<T>(Func<T> work)
    {
        var thread = Thread.CurrentThread;
        var before = thread.Priority;
        thread.Priority = ThreadPriority.BelowNormal;
        try { return work(); }
        finally { thread.Priority = before; }
    }

    public static void Background(Action work) => Background(() => { work(); return 0; });

    /// <summary>
    /// Walks folder trees with every core at once, a level at a time, and returns the files
    /// <paramref name="keepFile"/> accepts, sorted by path. <paramref name="skipDir"/> gets a
    /// folder's name and its parent's name. <paramref name="progress"/> hears how many files
    /// have been found and the folder being read, every couple of hundred folders.
    /// </summary>
    public static List<string> FindFiles(IEnumerable<string> roots, Func<string, bool> keepFile, Func<string, string, bool> skipDir,
                                         Action<int, string>? progress = null, CancellationToken ct = default)
    {
        var found = new ConcurrentBag<string>();
        var folders = 0;
        var level = roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        while (level.Count > 0)
        {
            var next = new ConcurrentBag<string>();
            Parallel.ForEach(level, Options(ct), dir => Background(() =>
            {
                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
                foreach (var f in files)
                    if (keepFile(f)) found.Add(f);
                var parent = Path.GetFileName(dir.TrimEnd('\\', '/'));
                foreach (var d in dirs)
                    if (!skipDir(Path.GetFileName(d), parent)) next.Add(d);
                if (Interlocked.Increment(ref folders) % 200 == 0) progress?.Invoke(found.Count, dir);
            }));
            level = [.. next];
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
