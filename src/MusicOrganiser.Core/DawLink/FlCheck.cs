namespace MusicOrganiser.Core.DawLink;

/// <summary>
/// Compares what the app read from an FL Studio project file with what FL Studio itself shows for it, to
/// catch anything the app's reader gets wrong.
/// </summary>
public static class FlCheck
{
    /// <param name="path">The project file.</param>
    /// <param name="tempo">The tempo the app read, if any.</param>
    /// <param name="channels">The channel names the app read, in channel rack order.</param>
    /// <param name="plugins">The names of every plugin the app found, on channels and in the mixer.</param>
    /// <param name="live">What FL Studio shows for the project it has open.</param>
    public static IReadOnlyList<CheckLine> Compare(string path, double? tempo, IReadOnlyList<string> channels,
                                                   IReadOnlyList<string> plugins, FlProject live)
    {
        if (!ShowsProject(live.Title, Checks.NameOf(path)))
            return [new(CheckResult.Different, string.IsNullOrWhiteSpace(live.Title)
                ? "FL Studio didn't say which project it has open. Open this one in FL Studio, then check again."
                : $"FL Studio's window says “{live.Title.Trim()}”, so it has another project open. Open this one in FL Studio, then check again.")];

        var lines = new List<CheckLine>();
        if (Checks.Tempo(tempo, live.Tempo, "FL Studio") is { } tempoLine) lines.Add(tempoLine);
        lines.AddRange(Checks.Names("Channels", "channel", "FL Studio", channels, live.Channels.Select(c => c.Name).ToList()));
        if (live.Grouped)
            lines.Add(new(CheckResult.Note, "This FL Studio only lists the channels in the group the channel rack shows. " +
                                            "Show All there to compare every channel."));
        lines.AddRange(ComparePlugins(plugins, live));
        if (Checks.Unsaved(lines, live.Modified, "FL Studio") is { } unsaved) lines.Add(unsaved);
        return lines;
    }

    /// <summary>
    /// Whether FL Studio's window title names this project. FL Studio puts the project's name (with or without
    /// .flp, and a * while it has unsaved changes) before or after its own name, joined by " - ".
    /// </summary>
    public static bool ShowsProject(string title, string name)
    {
        var t = title.Replace("*", "").Trim();
        if (name.Length == 0 || t.Length == 0) return false;
        foreach (var shown in new[] { name + ".flp", name })
        {
            if (t.Equals(shown, StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith(shown + " - ", StringComparison.OrdinalIgnoreCase) ||
                t.EndsWith(" - " + shown, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static IEnumerable<CheckLine> ComparePlugins(IReadOnlyList<string> read, FlProject live)
    {
        // Where FL Studio shows each plugin: on a channel, or in a mixer track's slot.
        var shown = live.Channels.Where(c => c.Plugin is { Length: > 0 }).Select(c => (Name: c.Plugin!, Where: $"the “{c.Name}” channel"))
            .Concat(live.Mixer.SelectMany(t => t.Plugins.Select(p => (p.Name, Where: $"mixer track “{t.Name}”"))))
            .ToList();
        var known = new HashSet<string>(read.Select(Checks.Key));
        var missing = shown.Where(p => !known.Contains(Checks.Key(p.Name))).Select(p => $"“{p.Name}” on {p.Where}").ToList();
        var seen = new HashSet<string>(shown.Select(p => Checks.Key(p.Name)));
        var extra = read.Where(p => !seen.Contains(Checks.Key(p))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (missing.Count == 0 && extra.Count == 0)
        {
            if (shown.Count > 0)
                yield return new(CheckResult.Same, shown.Count == 1
                    ? "Plugins: the one plugin FL Studio shows is in the app's list, and no others."
                    : $"Plugins: all {shown.Count} plugins FL Studio shows are in the app's list, and no others.");
            yield break;
        }
        if (missing.Count > 0)
            yield return new(CheckResult.Different, $"Plugins FL Studio shows that the app didn't find: {Checks.List(missing, quote: false)}.");
        if (extra.Count > 0)
            yield return new(CheckResult.Different, $"Plugins the app found that FL Studio doesn't show: {Checks.List(extra)}.");
    }
}
