using Microsoft.Diagnostics.Runtime;

namespace MusicOrganiser.Avalonia.Diagnostics;

/// <summary>
/// What a thread of this app is doing right now, read from a snapshot of the app (.NET can't read another thread's stack
/// any other way): for a freeze in the log, the code the window's thread was stuck in. Windows only: there the snapshot
/// is a copy-on-write clone that costs next to nothing, while on Linux it's a full dump of the app's memory (and the test
/// process crashed afterwards).
/// </summary>
static class StackSnapshot
{
    /// <summary>How many of the stack's top methods are written, plus the app's own nearest ones below them.</summary>
    const int Top = 4, Own = 3;

    /// <summary>"Monitor.Enter ← MidiIndex.Notes ← MidiRow.MakeChannel …", or null when it can't be read.</summary>
    public static string? Of(int managedThreadId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            using var runtime = target.ClrVersions[0].CreateRuntime();
            if (runtime.Threads.FirstOrDefault(t => t.ManagedThreadId == managedThreadId) is not { } thread) return null;
            var frames = thread.EnumerateStackTrace()
                .Where(f => f.Method is not null)
                .Select(f => (Type: f.Method!.Type?.Name ?? "?", f.Method.Name))
                .Take(200).ToList();
            if (frames.Count == 0) return null;
            // The top says what it's waiting in (a lock, a file); the app's own frames say which of its code asked.
            var shown = frames.Take(Top)
                .Concat(frames.Skip(Top).Where(f => f.Type.StartsWith("MusicOrganiser", StringComparison.Ordinal)).Take(Own));
            return string.Join(" ← ", shown.Select(f => $"{f.Type[(f.Type.LastIndexOf('.') + 1)..]}.{f.Name}"));
        }
        catch (Exception e)
        {
            return $"(couldn't read it: {e.GetType().Name})";
        }
    }
}
