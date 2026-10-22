using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Preview;

/// <summary>What a lane in the arrangement holds, which decides how its clips are drawn.</summary>
public enum LaneKind { Instrument, Audio, Automation, Group }

/// <summary>A note in a clip, in beats from the clip's start.</summary>
public readonly record struct PreviewNote(double Start, double Length, int Key, int Velocity, bool Muted = false);

/// <summary>A point on an automation curve, in beats from the clip's start, with the value from 0 to 1.</summary>
public readonly record struct PreviewPoint(double Beat, double Value);

/// <summary>
/// A stretch of an audio file playing in a clip, from <see cref="Start"/> for <see cref="Length"/> beats (timed from the
/// clip's start). It starts <see cref="From"/> seconds into the file and moves through it at <see cref="SecondsPerBeat"/>;
/// when that's only known once the file's length is, <see cref="WholeFileBeats"/> says how many beats the whole file
/// takes instead.
/// </summary>
public sealed record AudioPart(string Path, double Start, double Length, double From, double? SecondsPerBeat, double? WholeFileBeats = null)
{
    /// <summary>Whether the file is on this computer, so its waveform can be drawn.</summary>
    public bool Found { get; init; }
    /// <summary>Plays backwards (FL Studio's reverse switch).</summary>
    public bool Reversed { get; init; }

    /// <summary>Seconds of the file a beat covers, once the file's length is known.</summary>
    public double Rate(double fileSeconds) =>
        SecondsPerBeat ?? (WholeFileBeats is > 0 && fileSeconds > 0 ? fileSeconds / WholeFileBeats.Value : 0);
}

/// <summary>A clip on a lane: where it starts and how long it is, in beats from the song's start.</summary>
public sealed record PreviewClip(string Name, double Start, double Length, bool Muted = false)
{
    public IReadOnlyList<PreviewNote> Notes { get; init; } = [];
    public IReadOnlyList<AudioPart> Audio { get; init; } = [];
    public IReadOnlyList<PreviewPoint> Points { get; init; } = [];
    /// <summary>The clip's own colour (0xRRGGBB), when it has one; otherwise it takes its lane's.</summary>
    public int? Colour { get; init; }
    public double End => Start + Length;
}

/// <summary>
/// A track in the arrangement. <see cref="Detail"/> says what plays it (a plugin, a sample) or what it moves (an automation
/// lane's target); <see cref="Depth"/> is how deep in groups it sits.
/// </summary>
public sealed record PreviewLane(string Name, LaneKind Kind)
{
    public string Detail { get; init; } = "";
    public int? Colour { get; init; }
    public int Depth { get; init; }
    public bool Muted { get; init; }
    public IReadOnlyList<PreviewClip> Clips { get; init; } = [];
}

/// <summary>A marker on the timeline, in beats.</summary>
public readonly record struct PreviewMarker(double Beat, string Name);

/// <summary>
/// A project laid out the way a DAW's arrangement shows it, for drawing without opening the DAW. <see cref="View"/> names
/// the layout ("FL Studio playlist", "As it opens in Bitwig") when a project can be shown more than one way.
/// </summary>
public sealed record Arrangement(string View, double Tempo, int BeatsPerBar, IReadOnlyList<PreviewLane> Lanes)
{
    public IReadOnlyList<PreviewMarker> Markers { get; init; } = [];
    /// <summary>Things the preview couldn't show, in plain words.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>Where the last clip ends, in beats.</summary>
    public double Length => Lanes.SelectMany(l => l.Clips).Select(c => c.End).DefaultIfEmpty(0).Max();
    public double Bars => BeatsPerBar > 0 ? Length / BeatsPerBar : 0;
    public int ClipCount => Lanes.Sum(l => l.Clips.Count);
    public int NoteCount => Lanes.Sum(l => l.Clips.Where(c => !c.Muted).Sum(c => c.Notes.Count(n => !n.Muted)));
    public double Seconds => Tempo > 0 ? Length * 60 / Tempo : 0;
}

/// <summary>Reads a project's arrangement for the preview, in every layout its DAW has.</summary>
public static class ArrangementReader
{
    [ThreadStatic] static Dictionary<string, string?>? _located;
    [ThreadStatic] static bool _noLooking;

    /// <summary>
    /// The project's arrangement views (FL Studio has two: its playlist, and the tracks it becomes in Bitwig). Empty when
    /// the format has none. <paramref name="findAudio"/> false: audio clips aren't looked for on disk (the MIDI page only
    /// wants the notes), so they all count as missing.
    /// </summary>
    public static IReadOnlyList<Arrangement> Read(string path, bool findAudio = true)
    {
        // Each audio file is looked for once per read, however many clips play it: Solomon's "TEKA TEK dubstep new drums 21"
        // has 2,594 audio clips of 31 files, and looking on disk up to seven times for each clip took seconds on his PC.
        var (located, noLooking) = (_located, _noLooking);
        _located = new(StringComparer.OrdinalIgnoreCase);
        _noLooking = !findAudio;
        try
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".flp" => FlArrangement.Read(path),
                ".bwproject" => [new BitwigReader().ReadArrangement(path)],
                ".song" => [new StudioOneReader().ReadArrangement(path)],
                _ => [],
            };
        }
        finally { (_located, _noLooking) = (located, noLooking); }
    }

    /// <summary>
    /// The arrangement Overview shows: the project's own, except for an FL Studio save FL stopped writing before its
    /// playlist (<see cref="FlDamage"/>), which shows the newest older save FL finished, with a note saying so.
    /// </summary>
    public static IReadOnlyList<Arrangement> ReadNewestWhole(string path)
    {
        if (!System.IO.Path.GetExtension(path).Equals(".flp", StringComparison.OrdinalIgnoreCase) ||
            FlStudioReader.Damage(path) is not { EndsEarly: true } || FlStudioReader.WholeOlderSave(path) is not { } older)
            return Read(path);
        var (name, from) = (System.IO.Path.GetFileNameWithoutExtension(path), System.IO.Path.GetFileNameWithoutExtension(older));
        var note = $"{name} stopped part-way through saving, before its playlist, so this is {from}'s arrangement: the newest save FL finished. "
                   + $"Fix can finish {name} from it.";
        return Read(older).Select(a => a with { Notes = [note, .. a.Notes] }).ToList();
    }

    /// <summary>
    /// Where an audio file is on this computer: the path the project saved, else a file of that name in the project's
    /// folder or the folders beside it (Bitwig keeps them in samples/ and recordings/, Studio One in Media/, FL in the project's folder).
    /// </summary>
    public static string? Locate(string saved, string projectPath)
    {
        if (saved.Length == 0 || _noLooking) return null;
        if (_located is not { } cache) return Look(saved, projectPath);
        if (!cache.TryGetValue(saved, out var found)) cache[saved] = found = Look(saved, projectPath);
        return found;
    }

    static string? Look(string saved, string projectPath)
    {
        try
        {
            if (File.Exists(saved)) return saved;
            var name = saved.Split('\\', '/')[^1];
            var dir = System.IO.Path.GetDirectoryName(projectPath);
            if (string.IsNullOrEmpty(dir) || name.Length == 0) return null;
            foreach (var folder in new[] { dir, System.IO.Path.Combine(dir, "samples"), System.IO.Path.Combine(dir, "recordings"),
                                           System.IO.Path.Combine(dir, "Samples"), System.IO.Path.Combine(dir, "Audio"),
                                           System.IO.Path.Combine(dir, "Media") })
            {
                var candidate = System.IO.Path.Combine(folder, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }
}
