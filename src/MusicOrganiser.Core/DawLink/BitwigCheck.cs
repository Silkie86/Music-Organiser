namespace MusicOrganiser.Core.DawLink;

/// <summary>
/// Compares what the app read from a Bitwig project file with what Bitwig itself shows for it, to catch
/// anything the app's reader gets wrong (Bitwig's format isn't documented).
/// </summary>
public static class BitwigCheck
{
    /// <param name="path">The project file.</param>
    /// <param name="tempo">The tempo the app read, if any.</param>
    /// <param name="tracks">The track names the app read (without the master track).</param>
    /// <param name="devices">The names of every plugin and Bitwig device the app found.</param>
    /// <param name="live">What Bitwig shows for the project it has open.</param>
    public static IReadOnlyList<CheckLine> Compare(string path, double? tempo, IReadOnlyList<string> tracks,
                                                   IReadOnlyList<string> devices, BitwigProject live)
    {
        if (!string.Equals(live.Name.Trim(), Checks.NameOf(path), StringComparison.OrdinalIgnoreCase))
            return [new(CheckResult.Different, $"Bitwig has “{live.Name}” open, not this project. Open this one in Bitwig, then check again.")];

        var lines = new List<CheckLine>();
        if (Checks.Tempo(tempo, live.Tempo, "Bitwig") is { } tempoLine) lines.Add(tempoLine);

        var shown = live.Tracks.Where(t => !t.Type.Equals("Master", StringComparison.OrdinalIgnoreCase)).Select(t => t.Name).ToList();
        lines.AddRange(Checks.Names("Tracks", "track", "Bitwig", tracks, shown));
        if (live.TrackCount > live.Tracks.Count)
            lines.Add(new(CheckResult.Note, $"Bitwig has {live.TrackCount} tracks; the first {live.Tracks.Count} were compared."));

        lines.AddRange(CompareDevices(devices, live.Tracks));
        if (Checks.Unsaved(lines, live.Modified, "Bitwig") is { } unsaved) lines.Add(unsaved);
        return lines;
    }

    static IEnumerable<CheckLine> CompareDevices(IReadOnlyList<string> read, IReadOnlyList<BitwigTrack> tracks)
    {
        var known = new HashSet<string>(read.Select(Checks.Key));
        var seen = new HashSet<string>();
        var missing = new List<string>();
        var count = 0;
        foreach (var track in tracks)
            foreach (var device in track.Devices)
            {
                count++;
                seen.Add(Checks.Key(device.Name));
                if (!known.Contains(Checks.Key(device.Name))) missing.Add($"“{device.Name}” on {track.Name}");
            }

        if (count > 0 && missing.Count == 0)
            yield return new(CheckResult.Same, count == 1
                ? "Devices: the one device at the top of Bitwig's tracks is in the app's list."
                : $"Devices: all {count} devices at the top of Bitwig's tracks are in the app's list.");
        else if (missing.Count > 0)
            yield return new(CheckResult.Different, $"Devices Bitwig shows that the app didn't find: {Checks.List(missing, quote: false)}.");

        var deeper = read.Where(d => !seen.Contains(Checks.Key(d))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (deeper.Count > 0)
            yield return new(CheckResult.Note, $"The app also found {Checks.List(deeper)}. Bitwig's link only sees the top of each " +
                                               "track's chain, so these are probably inside containers, layers or drum pads.");
        if (tracks.Any(t => t.DeviceCount > t.Devices.Count))
            yield return new(CheckResult.Note, "Some tracks have more than 16 devices; the first 16 were compared.");
    }
}
