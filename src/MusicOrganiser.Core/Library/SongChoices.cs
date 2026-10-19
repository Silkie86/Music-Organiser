namespace MusicOrganiser.Core.Library;

/// <summary>
/// Your own say on which saves are one song, over <see cref="SongVersions"/>' guess from names and folders (Solomon, 19 Oct:
/// "you should be able to change what list they go under", since other people name their work differently): a song joined
/// to another, a save split off into a song of its own, and songs you said are different, so the app doesn't ask again.
/// </summary>
public sealed class SongChoices(IReadOnlyDictionary<string, string> joined, IReadOnlyCollection<string> split, IReadOnlyCollection<string> apart)
{
    public static readonly SongChoices None = new(new Dictionary<string, string>(), [], []);

    readonly HashSet<string> _split = split.Select(p => p.ToLowerInvariant()).ToHashSet();
    readonly HashSet<string> _apart = apart.ToHashSet();

    public bool IsEmpty => joined.Count == 0 && _split.Count == 0;

    /// <summary>The song a save goes under: its own when split off, else its name's song, followed to whatever that was joined to.</summary>
    public string KeyOf(string path, string folder, string daw, string name) =>
        Follow(_split.Contains(path.ToLowerInvariant()) ? SplitKey(path) : SongVersions.Key(folder, daw, name));

    /// <summary>Where a song's key ends up after the joins (a few steps at most; a loop stops where it started).</summary>
    public string Follow(string key)
    {
        for (var i = 0; i < 32 && joined.TryGetValue(key, out var to); i++) key = to;
        return key;
    }

    /// <summary>The song of one save split off from its song.</summary>
    public static string SplitKey(string path) => "save:" + path.ToLowerInvariant();

    /// <summary>Whether you said these two songs are different.</summary>
    public bool Apart(string a, string b) => _apart.Contains(Pair(a, b));

    /// <summary>Two songs' keys as one, in a set order.</summary>
    public static string Pair(string a, string b) => string.CompareOrdinal(a, b) < 0 ? $"{a}\u001e{b}" : $"{b}\u001e{a}";

    /// <summary>Whether the song with this key was joined to another, or a save in it split off.</summary>
    public bool Changed(string key, IEnumerable<string> paths) =>
        joined.ContainsKey(key) || joined.Values.Contains(key) || paths.Any(p => _split.Contains(p.ToLowerInvariant()));
}
