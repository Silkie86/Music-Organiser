using System.Globalization;

namespace MusicOrganiser.Core.DawLink;

public enum CheckResult { Same, Different, Note }

/// <summary>One thing compared between what the app read from a project file and what the DAW shows.</summary>
public sealed record CheckLine(CheckResult Result, string Text)
{
    public string Mark => Result switch { CheckResult.Same => "✓", CheckResult.Different => "✗", _ => "•" };
}

/// <summary>The comparisons both DAW checks make, worded the same way.</summary>
static class Checks
{
    const int Listed = 8;

    /// <summary>A project's name from its path, splitting on both kinds of slash so a Windows path reads the same everywhere.</summary>
    public static string NameOf(string path) => Path.GetFileNameWithoutExtension(path[(path.LastIndexOfAny(['\\', '/']) + 1)..]).Trim();

    public static CheckLine? Tempo(double? read, double? shown, string daw) =>
        read is not { } bpm || shown is not { } live ? null
        : Math.Abs(bpm - live) < 0.005 ? new(CheckResult.Same, $"Tempo: {Bpm(bpm)} BPM in both.")
        : new(CheckResult.Different, $"Tempo: the app read {Bpm(bpm)} BPM, {daw} says {Bpm(live)} BPM.");

    /// <summary>Compares names in order, e.g. "Tracks", "track", "Bitwig".</summary>
    public static IEnumerable<CheckLine> Names(string label, string what, string daw, IReadOnlyList<string> read, IReadOnlyList<string> shown)
    {
        if (read.SequenceEqual(shown, StringComparer.Ordinal))
        {
            yield return new(CheckResult.Same, $"{label}: {Count(shown.Count, what)}, named the same in both.");
            yield break;
        }
        var onlyDaw = Without(shown, read);
        var onlyApp = Without(read, shown);
        if (onlyDaw.Count == 0 && onlyApp.Count == 0)
        {
            yield return new(CheckResult.Different, $"{label}: the same {Count(shown.Count, what)}, but in a different order. {daw}: {List(shown)}.");
            yield break;
        }
        if (onlyDaw.Count > 0)
            yield return new(CheckResult.Different, $"{label} {daw} shows that the app didn't read: {List(onlyDaw)}.");
        if (onlyApp.Count > 0)
            yield return new(CheckResult.Different, $"{label} the app read that {daw} doesn't show: {List(onlyApp)}.");
    }

    /// <summary>A note that unsaved changes may explain the differences, when there are any.</summary>
    public static CheckLine? Unsaved(IEnumerable<CheckLine> lines, bool modified, string daw) =>
        modified && lines.Any(l => l.Result == CheckResult.Different)
            ? new(CheckResult.Note, $"{daw} has unsaved changes to this project, so some differences may just be those. " +
                                    $"Save it in {daw} and rescan, then check again.")
            : null;

    /// <summary>The items of <paramref name="a"/> left after taking out one match for each item of <paramref name="b"/>.</summary>
    public static List<string> Without(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var left = b.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var x in a)
        {
            if (left.TryGetValue(x, out var n) && n > 0) left[x] = n - 1;
            else result.Add(x);
        }
        return result;
    }

    public static string Key(string name) => name.Trim().ToLowerInvariant();

    public static string Bpm(double bpm) => bpm.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    public static string List(IReadOnlyList<string> names, bool quote = true)
    {
        var shown = names.Take(Listed).Select(n => quote ? $"“{n}”" : n);
        return string.Join(", ", shown) + (names.Count > Listed ? $" and {names.Count - Listed} more" : "");
    }
}
