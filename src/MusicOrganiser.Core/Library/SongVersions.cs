using System.Text.RegularExpressions;

namespace MusicOrganiser.Core.Library;

/// <summary>One song and its numbered saves: Song.flp, Song_2.flp … Song_77.flp in one folder.</summary>
public sealed record SongGroup(string Song, ProjectSummary Latest, IReadOnlyList<ProjectSummary> Older);

/// <summary>
/// One working period on a song: saves less than <see cref="SongVersions.RunGapMonths"/> months apart. <see cref="Last"/> is the save
/// it ended on, which is what gets opened. <see cref="Reworked"/>: it's quite different from how the song first ended up,
/// not just saved again (see <see cref="SongVersions.IsReworked"/>).
/// </summary>
public sealed record SaveRun(ProjectSummary Last, DateTime Start, DateTime End, int Saves, bool First, bool Reworked)
{
    /// <summary>"2019 original", "2024 rework", or "2023 re-save" for a run that only saved it again.</summary>
    public string Label => $"{End.Year} {(First ? "original" : Reworked ? "rework" : "re-save")}";
    /// <summary>"Didums_77 · 3 Aug 2024 · 12 saves".</summary>
    public string Details => string.Join(" · ", new[]
    {
        Last.Name, End.ToString("d MMM yyyy"), Saves == 1 ? null : $"{Saves} saves",
    }.OfType<string>());
}

/// <summary>
/// Groups numbered saves of the same song. FL Studio's "Save new version" names them
/// Song_2, Song_3 …, and plenty of people number saves that way by hand in any DAW.
/// </summary>
public static partial class SongVersions
{
    [GeneratedRegex(@"^(.+?)_(\d{1,4})$")]
    private static partial Regex Numbered();

    /// <summary>Fix's copies: "Didums_70 (fixed)", "Didums_70 (fixed 2)".</summary>
    [GeneratedRegex(@"(\s*\(fixed(\s+\d+)?\))+$", RegexOptions.IgnoreCase)]
    private static partial Regex FixedTail();

    /// <summary>
    /// "Didums_70" → ("Didums", 70); "Didums" → ("Didums", 1). A fixed copy counts as the save it was made from:
    /// "Didums_70 (fixed)" → ("Didums", 70), so it sits with the song's saves.
    /// </summary>
    public static (string Song, int Number) Split(string name)
    {
        name = FixedTail().Replace(name, "");
        var m = Numbered().Match(name);
        return m.Success && m.Groups[1].Value.Trim().Length > 0
            ? (m.Groups[1].Value.TrimEnd(), int.Parse(m.Groups[2].Value))
            : (name, 1);
    }

    /// <summary>A gap this long between two saves (Solomon's pick: 3 months) starts a new run.</summary>
    public const int DefaultRunGapMonths = 3;
    /// <summary>The gap the app uses: Settings › Projects can change it.</summary>
    public static int RunGapMonths { get; set; } = DefaultRunGapMonths;

    /// <summary>
    /// When the song was first made: its oldest save, or the date its file records (FL Studio keeps it through
    /// re-saves), whichever is earlier. Re-saving an old project in a newer DAW doesn't make it new.
    /// </summary>
    public static DateTime FirstMade(IEnumerable<ProjectSummary> saves)
    {
        var dates = saves.SelectMany(s => s.Created is { } c && c.Year >= 1995 ? new[] { s.Modified, c } : [s.Modified]).ToList();
        return dates.Count == 0 ? DateTime.MinValue : dates.Min();
    }

    /// <summary>
    /// The saves split into working periods, oldest first: a gap of <see cref="RunGapMonths"/> months or more
    /// between two saves starts a new one. Moved files are left out, since they can't be opened.
    /// </summary>
    public static IReadOnlyList<SaveRun> Runs(IEnumerable<ProjectSummary> saves, int? gapMonths = null)
    {
        var gap = gapMonths ?? RunGapMonths;
        var ordered = saves.Where(s => !s.FileMissing).OrderBy(s => s.Modified).ThenBy(s => Split(s.Name).Number).ToList();
        var runs = new List<List<ProjectSummary>>();
        foreach (var save in ordered)
        {
            if (runs.Count == 0 || save.Modified >= runs[^1][^1].Modified.AddMonths(gap)) runs.Add([]);
            runs[^1].Add(save);
        }
        var original = runs.Count > 0 ? runs[0][^1] : null;
        return runs.Select((r, i) => new SaveRun(r[^1], r[0].Modified, r[^1].Modified, r.Count, i == 0,
                                                 i > 0 && IsReworked(original!, r[^1]))).ToList();
    }

    /// <summary>
    /// Whether <paramref name="later"/> is quite different from <paramref name="earlier"/>, not just saved again
    /// (Solomon's rules): plugins added or removed (a newer version of the same one, such as Serum 2 for Serum, doesn't
    /// count), channels added or removed, the number of notes changed by more than a fifth, or more than 8 bars longer or
    /// shorter. What isn't known about either save is left out.
    /// </summary>
    public static bool IsReworked(ProjectSummary earlier, ProjectSummary later)
    {
        var before = earlier.PluginNames.Select(PluginFamily).ToHashSet();
        var after = later.PluginNames.Select(PluginFamily).ToHashSet();
        if (!before.SetEquals(after)) return true;
        if (earlier.TrackCount >= 0 && later.TrackCount >= 0 && earlier.TrackCount != later.TrackCount) return true;
        if (earlier.NoteCount >= 0 && later.NoteCount >= 0 && Math.Abs(later.NoteCount - earlier.NoteCount) * 5 > Math.Max(earlier.NoteCount, 1))
            return true;
        return earlier.LengthBars >= 0 && later.LengthBars >= 0 && Math.Abs(later.LengthBars - earlier.LengthBars) > 8;
    }

    [GeneratedRegex(@"(\s*\((x64|x86|64-?bit|32-?bit|vst3?|clap|au)\)|[\s_-]+(x64|x86|64-?bit|32-?bit|vst3?)|[\s_-]*v?\d+(\.\d+)*)$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionTail();

    /// <summary>A plugin's name without its version or format: "Serum 2" and "Serum (x64)" are both "serum".</summary>
    public static string PluginFamily(string name)
    {
        var n = name.Trim();
        while (VersionTail().Replace(n, "").Trim() is { Length: > 0 } shorter && shorter != n) n = shorter;
        return n.ToLowerInvariant();
    }

    /// <summary>Saves of the same song share a key: same folder, same DAW, same name before the number.</summary>
    public static string Key(string folder, string daw, string name) =>
        $"{folder}\u001f{daw}\u001f{Split(name).Song}".ToLowerInvariant();

    /// <summary>
    /// One group per song. The latest save is the highest-numbered file that is still on disk
    /// (ties go to the most recently modified); folder copies reset dates, numbers survive.
    /// </summary>
    public static IReadOnlyList<SongGroup> Group(IEnumerable<ProjectSummary> projects) =>
        projects.GroupBy(p => Key(p.Folder, p.Daw, p.Name))
            .Select(g =>
            {
                var ordered = g.OrderBy(p => p.FileMissing)
                               .ThenByDescending(p => Split(p.Name).Number)
                               .ThenByDescending(p => p.Modified)
                               .ThenByDescending(p => FixedTail().IsMatch(p.Name))   // saved the same second: the fix came after
                               .ToList();
                return new SongGroup(Split(ordered[0].Name).Song, ordered[0], ordered.Skip(1).ToList());
            })
            .ToList();

    /// <summary>
    /// Saves grouped into songs the way <see cref="Group"/> does, newest song first. Each keeps its highest-numbered save
    /// in the list (ties go to the most recent), so a plugin's song shows the last save that still used it.
    /// </summary>
    public static IReadOnlyList<SongUse> Songs(IEnumerable<PluginProject> saves) =>
        saves.GroupBy(p => Key(p.Folder, p.Daw, p.Name))
            .Select(g =>
            {
                var latest = g.OrderByDescending(p => Split(p.Name).Number).ThenByDescending(p => p.Saved).First();
                return new SongUse(Split(latest.Name).Song, latest, g.Count());
            })
            .OrderByDescending(s => s.Latest.Saved).ThenBy(s => s.Song, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
