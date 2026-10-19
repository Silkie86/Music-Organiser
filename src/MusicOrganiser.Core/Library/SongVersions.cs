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

    /// <summary>"Song 12", as Solomon numbers Bitwig saves (up to three digits, so "Swu oct 2026" stays a name).</summary>
    [GeneratedRegex(@"^(.+?)\s+(\d{1,3})$")]
    private static partial Regex SpaceNumbered();

    /// <summary>
    /// Names that are a DAW's or a person's stand-in rather than a song's: "Untitled 3" and "Untitled 4" are different
    /// songs, not saves of one.
    /// </summary>
    static readonly HashSet<string> StandIns = new(StringComparer.OrdinalIgnoreCase)
        { "untitled", "untitled project", "new project", "project", "song", "new song", "track", "idea", "demo", "beat", "test", "sketch" };

    /// <summary>Fix's copies: "Didums_70 (fixed)", "Didums_70 (fixed 2)".</summary>
    [GeneratedRegex(@"(\s*\(fixed(\s+\d+)?\))+$", RegexOptions.IgnoreCase)]
    private static partial Regex FixedTail();

    /// <summary>
    /// A word that marks a save, then its number: "fleck athens FLP19", "Song v2", "Song v2.1", "Song take 4", "Song mix_3",
    /// "Song rev 7". There has to be a space, "_", "-" or "." before the word, so "Remix2" and "Dev3" stay names.
    /// </summary>
    [GeneratedRegex(@"^(.+?)[\s_.-]+(?:v|ver|version|take|tk|mix|rev|revision|flp|draft|alt)\s*[.#_]?\s*(\d{1,4})(?:\.(\d{1,3}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex MarkedNumber();

    /// <summary>The same words glued on with a capital: "fleckathensFLP19", "SongV2", "SongTake4".</summary>
    [GeneratedRegex(@"^(.*\p{Ll})(?:V|Ver|VER|FLP|Flp|Take|TAKE|Mix|MIX|Rev|REV)(\d{1,4})$")]
    private static partial Regex GluedNumber();

    /// <summary>"Song final", "Song FINAL FINAL", "Song final2", "Song final v3": the last save, ahead of any number.</summary>
    [GeneratedRegex(@"^(.+?)[\s_.-]+final(?:[\s_.-]*final)*(?:[\s_.-]*v?(\d{1,3}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Final();

    /// <summary>
    /// Copies and side saves that say nothing about order: Windows' "Song - Copy", "Song - Copy (2)" and "Song (2)",
    /// "Song copy", "Song wip", "Song backup", and a date: "Song 2024-03-01", "Song_20240301".
    /// </summary>
    [GeneratedRegex(@"^(.+?)(?:\s*-\s*copy(?:\s*\(\d{1,3}\))?|\s+copy(?:\s*\d{1,3})?|\s*\(copy\)|\s*\(\d{1,2}\)|[\s_.-]+(?:wip|backup|bak|bkp)|[\s_.-]+(?:19|20)\d\d[-_.]?(?:0[1-9]|1[0-2])[-_.]?(?:0[1-9]|[12]\d|3[01]))$", RegexOptions.IgnoreCase)]
    private static partial Regex SideSave();

    /// <summary>A "final" save's number: above every numbered save, so it's the one the song opens.</summary>
    const int FinalNumber = 1_000_000;

    /// <summary>
    /// "Didums_70" → ("Didums", 70); "Didums" → ("Didums", 1). A fixed copy counts as the save it was made from:
    /// "Didums_70 (fixed)" → ("Didums", 70), so it sits with the song's saves. The ways people mark saves by hand count
    /// in every DAW (Solomon, 2026-10-19: "understand the different ways people version projects"): "fleck athens FLP19"
    /// → ("fleck athens", 19), "Song v2.1", "Song take 4", "Song final" (the last save), and copies, side saves and
    /// dates such as "Song - Copy", "Song wip" and "Song 2024-03-01", which keep the number of what's left. For Bitwig, a
    /// bare number after a space counts too: "analog full set nb 36" → ("analog full set nb", 36). A stand-in like
    /// "Untitled" is never cut down to: "Untitled 3" and "Untitled v4" are songs of their own. Other DAWs' "Kick 808" or
    /// "Track 2" are left as names (Solomon numbers those with FL Studio's "_2").
    /// </summary>
    public static (string Song, int Number) Split(string name, string? daw = null)
    {
        name = FixedTail().Replace(name, "");
        int? number = null;
        // Marks stack ("Song v2 final", "Song_3 - Copy"): take them off from the end, keeping the first number found.
        for (var i = 0; i < 4 && Unmark(name) is { } cut; i++)
        {
            name = cut.Song;
            number ??= cut.Number;
        }
        var m = Numbered().Match(name);
        if (m.Success && m.Groups[1].Value.Trim().Length > 0) return (m.Groups[1].Value.TrimEnd(), number ?? int.Parse(m.Groups[2].Value));
        if (daw == "Bitwig Studio" && SpaceNumbered().Match(name) is { Success: true } spaced && IsSong(spaced.Groups[1].Value))
            return (spaced.Groups[1].Value.Trim(), number ?? int.Parse(spaced.Groups[2].Value));
        return (name, number ?? 1);
    }

    /// <summary>The name with one save mark taken off its end, and the number it gave (null when it gave none).</summary>
    static (string Song, int? Number)? Unmark(string name)
    {
        if (Final().Match(name) is { Success: true } final && IsSong(final.Groups[1].Value))
            return (final.Groups[1].Value.Trim(), FinalNumber + (final.Groups[2].Success ? int.Parse(final.Groups[2].Value) : 0));
        if (MarkedNumber().Match(name) is { Success: true } marked && IsSong(marked.Groups[1].Value))
            return (marked.Groups[1].Value.Trim(), int.Parse(marked.Groups[2].Value));   // "v2.1" is save 2: the date tells it from "v2"
        if (GluedNumber().Match(name) is { Success: true } glued && IsSong(glued.Groups[1].Value))
            return (glued.Groups[1].Value.Trim(), int.Parse(glued.Groups[2].Value));
        if (SideSave().Match(name) is { Success: true } side && IsSong(side.Groups[1].Value))
            return (side.Groups[1].Value.Trim(), null);
        return null;
    }

    /// <summary>Whether what's left is a song's own name, not nothing or a stand-in like "Untitled".</summary>
    static bool IsSong(string rest) => rest.Trim() is { Length: > 0 } song && !StandIns.Contains(song);

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
        var ordered = saves.Where(s => !s.FileMissing).OrderBy(s => s.Modified).ThenBy(s => Split(s.Name, s.Daw).Number).ToList();
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

    /// <summary>Saves of the same song share a key: same folder (see <see cref="SongFolder"/>), same DAW, same name before the number.</summary>
    public static string Key(string folder, string daw, string name) =>
        $"{SongFolder(folder, name)}\u001f{daw}\u001f{Split(name, daw).Song}".ToLowerInvariant();

    /// <summary>
    /// The folder a song's saves sit in. Bitwig keeps each project in a folder named after it, so saving bored_58 as a new
    /// project makes Projects\bored_58\bored_58.bwproject next to Projects\bored_57\bored_57.bwproject (Solomon,
    /// 2026-10-18: "midi from multiple save versions of bitwig are listed as different"): a folder named like the song
    /// counts as the folder it's in, so those saves are one song, and so are saves made into the same folder.
    /// </summary>
    public static string SongFolder(string folder, string name)
    {
        var trimmed = folder.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        if (cut < 0) return folder;
        var own = trimmed[(cut + 1)..];
        return string.Equals(Split(own).Song, Split(name).Song, StringComparison.OrdinalIgnoreCase) ? trimmed[..cut] : folder;
    }

    /// <summary>
    /// One group per song. The latest save is the highest-numbered file that is still on disk
    /// (ties go to the most recently modified); folder copies reset dates, numbers survive.
    /// </summary>
    public static IReadOnlyList<SongGroup> Group(IEnumerable<ProjectSummary> projects) =>
        projects.GroupBy(p => Key(p.Folder, p.Daw, p.Name))
            .Select(g =>
            {
                var ordered = g.OrderBy(p => p.FileMissing)
                               .ThenByDescending(p => Split(p.Name, p.Daw).Number)
                               .ThenByDescending(p => p.Modified)
                               .ThenByDescending(p => FixedTail().IsMatch(p.Name))   // saved the same second: the fix came after
                               .ToList();
                return new SongGroup(Split(ordered[0].Name, ordered[0].Daw).Song, ordered[0], ordered.Skip(1).ToList());
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
                var latest = g.OrderByDescending(p => Split(p.Name, p.Daw).Number).ThenByDescending(p => p.Saved).First();
                return new SongUse(Split(latest.Name, latest.Daw).Song, latest, g.Count());
            })
            .OrderByDescending(s => s.Latest.Saved).ThenBy(s => s.Song, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
