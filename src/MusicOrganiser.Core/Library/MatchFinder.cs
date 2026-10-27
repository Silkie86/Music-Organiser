namespace MusicOrganiser.Core.Library;

/// <summary>
/// A possible replacement. <paramref name="Score"/> is how alike the names are; <paramref name="Folders"/> (0..1) is how
/// many of the missing file's folder names the candidate's path shares, nearest folder counting most.
/// </summary>
public sealed record Match(string Candidate, double Score, string Reason, double Folders = 0)
{
    /// <summary>What the list is ordered by: the name first, then a nudge for sitting in the same pack's folders.</summary>
    public double Rank => Score + 0.3 * Folders;
}

/// <summary>Suggests replacements for missing samples and plugins, locally and online.</summary>
public static class MatchFinder
{
    /// <summary>Audio files in your indexed sample folders that could be the missing sample.</summary>
    /// <remarks>
    /// The project only stores where the file was, so there's no sound to compare: names and folders are all there is.
    /// Copies with the same name come first, the one in the most similar folders ("Soundscan 46 - 70's Breakbeats",
    /// "drumhits2") leading; then similar names, nudged up when their folders match too.
    /// </remarks>
    public static IReadOnlyList<Match> ForSample(LibraryDb db, string missingPath, int max = 15)
    {
        var fileName = missingPath.Split('\\', '/')[^1];
        var folders = FolderKeys(missingPath);
        var results = db.SamplesNamed(fileName)
            .Select(p => FolderMatch(folders, p) is var f && f > 0
                ? new Match(p, 1.0, "Same file name, in matching folders", f)
                : new Match(p, 1.0, "Same file name"))
            .OrderByDescending(m => m.Folders).ToList();
        var stem = LibraryDb.NameKey(Path.GetFileNameWithoutExtension(fileName));
        if (stem.Length == 0) return results.Take(max).ToList();

        var exact = results.Select(r => r.Candidate).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fuzzy = db.SampleIndex()
            .Where(s => !exact.Contains(s.Path))
            .Select(s => (s.Path, Score: Similarity(stem, s.StemKey, atLeast: 0.5)))
            .Where(x => x.Score >= 0.5)
            .Select(x => (x.Path, x.Score, Folders: FolderMatch(folders, x.Path)))
            // A weaker name is only worth showing when it sits in the same pack's folders.
            .Where(x => x.Score >= 0.6 || x.Folders >= 0.5)
            .Select(x => new Match(x.Path, x.Score, x.Folders > 0 ? "Similar name, in matching folders" : "Similar name", x.Folders))
            .OrderByDescending(m => m.Rank)
            .Take(max);
        return results.Concat(fuzzy).Take(max).ToList();
    }

    /// <summary>Folder names too common to say anything about which pack a sample came from.</summary>
    static readonly HashSet<string> CommonFolders =
    [
        "users", "user", "documents", "mydocuments", "desktop", "downloads", "music", "programfiles", "programfilesx86",
        "programdata", "appdata", "roaming", "local", "imageline", "flstudio", "data", "patches", "packs", "pack",
        "samples", "sample", "sounds", "sound", "audio", "library", "libraries", "content", "splice", "projects",
        "recorded", "rendered", "sliced", "bitwigstudio", "studioone", "presonus", "wav", "wavs",
    ];

    /// <summary>The folder names a path sits in, nearest first, normalised, without drives and generic names.</summary>
    static List<string> FolderKeys(string path)
    {
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var keys = new List<string>();
        for (var i = parts.Length - 2; i >= 0; i--)
        {
            if (parts[i].EndsWith(':')) continue;
            if (i > 0 && LibraryDb.NameKey(parts[i - 1]) is "users" or "home") continue;   // the account name is in every path
            var key = LibraryDb.NameKey(parts[i]);
            if (key.Length >= 3 && !CommonFolders.Contains(key)) keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// 0..1: how many of the missing file's folders turn up in the candidate's path (alike enough counts, so
    /// "Soundscan 46 - 70's Breakbeats" finds "Soundscan Vol 46 70s Breakbeats"). The nearest folder weighs most.
    /// </summary>
    static double FolderMatch(List<string> missing, string candidate)
    {
        if (missing.Count == 0) return 0;
        var theirs = FolderKeys(candidate);
        if (theirs.Count == 0) return 0;
        double got = 0, total = 0;
        for (var i = 0; i < missing.Count && i < 4; i++)
        {
            var weight = 1.0 / (i + 1);
            total += weight;
            if (theirs.Any(t => t == missing[i] || Similarity(t, missing[i]) >= 0.8)) got += weight;
        }
        return got / total;
    }

    /// <summary>Installed plugins whose names are close to a missing plugin's (e.g. a newer version).</summary>
    public static IReadOnlyList<Match> ForPlugin(LibraryDb db, string missingName, int max = 10)
    {
        var key = LibraryDb.NameKey(missingName);
        return db.InstalledNames()
            .Select(n => (Name: n, Score: Similarity(key, LibraryDb.NameKey(n))))
            .Where(x => x.Score >= 0.55)
            .OrderByDescending(x => x.Score)
            .Take(max)
            .Select(x => new Match(x.Name, x.Score, x.Score >= 0.99 ? "Installed" : "Similar installed plugin"))
            .ToList();
    }

    public static string PluginSearchUrl(string name, string vendor) =>
        "https://www.google.com/search?q=" + Uri.EscapeDataString($"{vendor} {name} plugin download".Trim());

    public static string SampleSearchUrl(string missingPath) =>
        "https://www.google.com/search?q=" + Uri.EscapeDataString($"\"{missingPath.Split('\\', '/')[^1]}\" sample pack");

    /// <summary>
    /// 0..1 similarity of two normalised names: the better of edit-distance
    /// similarity and containment (one name inside the other, e.g. "kick01" in "bigkick01").
    /// </summary>
    public static double Similarity(string a, string b) => Similarity(a, b, atLeast: 0);

    /// <summary>
    /// <see cref="Similarity(string, string)"/>, skipping the whole-name comparison when the lengths alone keep it below
    /// <paramref name="atLeast"/> (it can only lose a point for each letter one name has more): run over every file in
    /// the sample index, that was most of the time. The answer is the same whenever it's <paramref name="atLeast"/> or more.
    /// </summary>
    static double Similarity(string a, string b, double atLeast)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        var longest = Math.Max(a.Length, b.Length);
        var edit = 1.0 - (double)Math.Abs(a.Length - b.Length) / longest < atLeast ? 0
            : 1.0 - (double)Levenshtein(a, b) / longest;
        var (shortS, longS) = a.Length <= b.Length ? (a, b) : (b, a);
        var contain = shortS.Length >= 4 && longS.Contains(shortS) ? 0.6 + 0.4 * shortS.Length / longS.Length : 0;
        // Names often differ only by a vendor prefix ("FabFilter Pro-Q 3" vs "Pro-Q 4"):
        // compare the shorter name with the end of the longer one.
        var tail = shortS.Length >= 4
            ? 0.9 * (1.0 - (double)Levenshtein(shortS, longS.AsSpan(longS.Length - shortS.Length)) / shortS.Length)
            : 0;
        return Math.Max(edit, Math.Max(contain, tail));
    }

    static int Levenshtein(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        // On the stack for names (nearly all), so comparing with every indexed file makes no garbage.
        Span<int> prev = b.Length < 256 ? stackalloc int[b.Length + 1] : new int[b.Length + 1];
        Span<int> cur = b.Length < 256 ? stackalloc int[b.Length + 1] : new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            var swap = prev;
            prev = cur;
            cur = swap;
        }
        return prev[b.Length];
    }
}
