using System.Buffers;

namespace MusicOrganiser.Core.Parsers;

/// <summary>
/// While a <see cref="Scope"/> is open on a thread, each project file is read from disk and decoded once, however many
/// readers look at it: the MIDI page reads a song's clips, its playlist and its channels from the same file. A song can
/// be 50 MB, and every big read is memory to clean up, which pauses the whole app, the window included.
/// </summary>
public static class ReadOnce
{
    [ThreadStatic] static Dictionary<(string Path, Type Kind), object>? _files;
    [ThreadStatic] static int _depth;
    [ThreadStatic] static List<byte[]>? _borrowed;

    public static IDisposable Scope()
    {
        _depth++;
        _files ??= [];
        return new Closer();
    }

    /// <summary>The file decoded by <paramref name="load"/>, once per open scope; outside a scope, every time.</summary>
    internal static T Get<T>(string path, Func<string, T> load) where T : class
    {
        if (_depth == 0 || _files is null) return load(path);
        if (_files.TryGetValue((path, typeof(T)), out var done)) return (T)done;
        var loaded = load(path);
        _files[(path, typeof(T))] = loaded;
        return loaded;
    }

    /// <summary>
    /// Inside a scope: the file read into a buffer borrowed until the scope closes, so reading song after song reuses the
    /// same memory instead of leaving a big block behind each time. Null outside a scope.
    /// </summary>
    internal static byte[]? Bytes(string path, out int length)
    {
        length = 0;
        if (_depth == 0) return null;
        using var file = File.OpenRead(path);
        if (file.Length > int.MaxValue) return null;
        length = (int)file.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        (_borrowed ??= []).Add(buffer);
        file.ReadExactly(buffer, 0, length);
        return buffer;
    }

    sealed class Closer : IDisposable
    {
        bool _closed;

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            if (--_depth > 0) return;
            _files = null;
            // Nothing read from them outlives the scope: what the readers keep is copied out.
            foreach (var b in _borrowed ?? []) ArrayPool<byte>.Shared.Return(b);
            _borrowed = null;
        }
    }
}
