namespace MusicOrganiser.Core.Library;

public sealed record Match(string Candidate, double Score, string Reason);

/// <summary>Suggests replacements for missing samples and plugins, locally and online.</summary>
public static class MatchFinder
{
    /// <summary>Audio files in your indexed sample folders that could be the missing sample.</summary>
    public static IReadOnlyList<Match> ForSample(LibraryDb db, string missingPath, int max = 15)
    {
        var fileName = missingPath.Split('\\', '/')[^1];
        var results = db.SamplesNamed(fileName).Select(p => new Match(p, 1.0, "Same file name")).ToList();
        var stem = LibraryDb.NameKey(Path.GetFileNameWithoutExtension(fileName));
        if (stem.Length == 0) return results;

        var exact = results.Select(r => r.Candidate).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fuzzy = db.SampleIndex()
            .Where(s => !exact.Contains(s.Path))
            .Select(s => (s.Path, Score: Similarity(stem, s.StemKey)))
            .Where(x => x.Score >= 0.6)
            .OrderByDescending(x => x.Score)
            .Take(max)
            .Select(x => new Match(x.Path, x.Score, "Similar name"));
        return results.Concat(fuzzy).Take(max).ToList();
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
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        var edit = 1.0 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);
        var (shortS, longS) = a.Length <= b.Length ? (a, b) : (b, a);
        var contain = shortS.Length >= 4 && longS.Contains(shortS) ? 0.6 + 0.4 * shortS.Length / longS.Length : 0;
        // Names often differ only by a vendor prefix ("FabFilter Pro-Q 3" vs "Pro-Q 4"):
        // compare the shorter name with the end of the longer one.
        var tail = shortS.Length >= 4
            ? 0.9 * (1.0 - (double)Levenshtein(shortS, longS[^shortS.Length..]) / shortS.Length)
            : 0;
        return Math.Max(edit, Math.Max(contain, tail));
    }

    static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
