using System.Text.RegularExpressions;

namespace MusicOrganiser.Core.Library;

/// <summary>One song and its numbered saves: Song.flp, Song_2.flp … Song_77.flp in one folder.</summary>
public sealed record SongGroup(string Song, ProjectSummary Latest, IReadOnlyList<ProjectSummary> Older)
{
    /// <summary>The song's key (see <see cref="SongVersions.Key"/> and <see cref="SongChoices"/>), for joining it to another.</summary>
    public string Key { get; init; } = "";
    public int SaveCount => Older.Count + 1;
}

/// <summary>Two songs that may be saves of one: their names are close but not close enough to be sure (see <see cref="SongVersions.Doubts"/>).</summary>
public sealed record SongDoubt(SongGroup A, SongGroup B, string Why)
{
    /// <summary>B is one save inside A, put there by a save mark ("final", "copy", "v2", a date): is it a save of A?</summary>
    public bool OneSave { get; init; }
}

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
    [GeneratedRegex(@"^(.+?)_([0-9]{1,4})$")]
    private static partial Regex Numbered();

    /// <summary>"Song 12", "Song-12", "Song - 12" (up to three digits, so "Swu oct 2026" stays a name).</summary>
    [GeneratedRegex(@"^(.+?)(?:\s*-\s*|\s+)([0-9]{1,3})$")]
    private static partial Regex SpaceNumbered();

    /// <summary>"track2", "FLP19": a number glued to the last word.</summary>
    [GeneratedRegex(@"^(.*\p{L})([0-9]{1,3})$")]
    private static partial Regex GluedOn();

    /// <summary>
    /// The names DAWs give a new project: "Untitled 3" and "Untitled 4" are different songs that were never named, not
    /// saves of one, so a number after one of these is part of the name (FL Studio's "Untitled_2" is still a save).
    /// </summary>
    static readonly HashSet<string> DawDefaults = new(StringComparer.OrdinalIgnoreCase)
        { "untitled", "untitled project", "untitled song", "new project", "new song" };

    /// <summary>Fix's copies: "Didums_70 (fixed)", "Didums_70 (fixed 2)".</summary>
    [GeneratedRegex(@"(\s*\(fixed(\s+[0-9]+)?\))+$", RegexOptions.IgnoreCase)]
    private static partial Regex FixedTail();

    /// <summary>
    /// A word that marks a save, then its number: "Song v2", "Song v2.1", "Song take 4", "Song mix_3", "Song rev 7". There has
    /// to be a space, "_", "-" or "." before the word, so "Remix2" isn't read as a "mix". Not "FLP": in "fleck athens FLP19"
    /// it's part of the song's name (Solomon, 19 Oct), and only the number is the save.
    /// </summary>
    [GeneratedRegex(@"^(.+?)[\s_.-]+(?:v|ver|version|take|tk|mix|rev|revision|draft|alt)\s*[.#_]?\s*([0-9]{1,4})(?:\.([0-9]{1,3}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex MarkedNumber();

    /// <summary>The same words glued on with a capital: "SongV2", "SongTake4".</summary>
    [GeneratedRegex(@"^(.*\p{Ll})(?:V|Ver|VER|Take|TAKE|Mix|MIX|Rev|REV)([0-9]{1,4})$")]
    private static partial Regex GluedNumber();

    /// <summary>"Song final", "Song FINAL FINAL", "Song final2", "Song final v3": the last save, ahead of any number.</summary>
    [GeneratedRegex(@"^(.+?)[\s_.-]+final(?:[\s_.-]*final)*(?:[\s_.-]*v?([0-9]{1,3}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Final();

    /// <summary>
    /// Copies and side saves that say nothing about order: Windows' "Song - Copy", "Song - Copy (2)" and "Song (2)",
    /// "Song copy", "Song wip", "Song backup", and a date: "Song 2024-03-01", "Song_20240301".
    /// </summary>
    [GeneratedRegex(@"^(.+?)(?:\s*-\s*copy(?:\s*\([0-9]{1,3}\))?|\s+copy(?:\s*[0-9]{1,3})?|\s*\(copy\)|\s*\([0-9]{1,2}\)|[\s_.-]+(?:wip|backup|bak|bkp)|[\s_.-]+(?:19|20)[0-9][0-9][-_.]?(?:0[1-9]|1[0-2])[-_.]?(?:0[1-9]|[12][0-9]|3[01]))$", RegexOptions.IgnoreCase)]
    private static partial Regex SideSave();

    /// <summary>A "final" save's number: above every numbered save, so it's the one the song opens.</summary>
    const int FinalNumber = 1_000_000;

    /// <summary>
    /// "Didums_70" → ("Didums", 70); "Didums" → ("Didums", 1). A fixed copy counts as the save it was made from:
    /// "Didums_70 (fixed)" → ("Didums", 70), so it sits with the song's saves. Solomon's rule for every DAW (2026-10-19):
    /// the same name with a number on the end is a save of one song, whether the number is glued to the last word
    /// ("track2"; "fleck athens FLP19" is a save of "fleck athens FLP"), after a space ("analog full set nb 36"), a dash
    /// ("song-3") or "_". So "track1" and "track2" are one song, "track nd 1" and "track 2" aren't (<see cref="Key"/> also
    /// needs the same folder). The
    /// usual marks count too: "Song v2.1", "Song take 4", "Song final" (the last save), and copies, side saves and dates
    /// such as "Song - Copy", "Song wip" and "Song 2024-03-01", which keep the number of what's left. A DAW's own name
    /// for a new project is never cut down to: "Untitled 3" and "Untitled 4" are songs of their own.
    /// </summary>
    public static (string Song, int Number) Split(string name)
    {
        // Every grouping, key and sort asks this many times per save, so each name is worked out once.
        if (SplitCache.TryGetValue(name, out var known)) return known;
        if (SplitCache.Count > 200_000) SplitCache.Clear();
        return SplitCache[name] = SplitUncached(name);
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string, int)> SplitCache = new();

    /// <summary>
    /// The song a name is a numbered save of by the plain rule alone (a number on the end, see <see cref="Split"/>), leaving
    /// save marks such as "final", "copy", "v2" or a date in the name.
    /// </summary>
    static string PlainSong(string name)
    {
        name = FixedTail().Replace(name, "");
        var m = Numbered().Match(name);
        if (m.Success && m.Groups[1].Value.Trim().Length > 0) return m.Groups[1].Value.TrimEnd();
        var tail = SpaceNumbered().Match(name) is { Success: true } spaced ? spaced : GluedOn().Match(name);
        return tail.Success && IsSong(tail.Groups[1].Value) ? tail.Groups[1].Value.Trim() : name;
    }

    /// <summary>Whether a save mark ("final", "copy", "wip", "v2", a date) put this save with its song: a guess, so the app asks.</summary>
    public static bool Marked(string name) => !PlainSong(name).Equals(Split(name).Song, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a song's saves aren't all one name numbered (a marked save, or songs you joined). Their numbers then don't
    /// count against each other, so the newest is the one saved last: an old "final" doesn't win over newer saves.
    /// </summary>
    static bool Mixed(IEnumerable<string> names) =>
        names.Select(PlainSong).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();

    static (string Song, int Number) SplitUncached(string name)
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
        var tail = SpaceNumbered().Match(name) is { Success: true } spaced ? spaced : GluedOn().Match(name);
        if (tail.Success && IsSong(tail.Groups[1].Value)) return (tail.Groups[1].Value.Trim(), number ?? int.Parse(tail.Groups[2].Value));
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

    /// <summary>Whether what's left is a song's own name, not nothing or a DAW's "Untitled".</summary>
    static bool IsSong(string rest) => rest.Trim() is { Length: > 0 } song && !DawDefaults.Contains(song);

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

    /// <summary>Saves of the same song share a key: same folder (see <see cref="SongFolder"/>), same DAW, same name before the number.</summary>
    public static string Key(string folder, string daw, string name) =>
        $"{SongFolder(folder, name)}\u001f{daw}\u001f{Split(name).Song}".ToLowerInvariant();

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
    /// <param name="choices">Your changes to the grouping (see <see cref="SongChoices"/>), which win over the guess.</param>
    public static IReadOnlyList<SongGroup> Group(IEnumerable<ProjectSummary> projects, SongChoices? choices = null)
    {
        choices ??= SongChoices.None;
        return projects.GroupBy(p => choices.KeyOf(p.Path, p.Folder, p.Daw, p.Name))
            .Select(g =>
            {
                var mixed = Mixed(g.Select(p => p.Name));
                var ordered = g.OrderBy(p => p.FileMissing)
                               .ThenByDescending(p => mixed ? 0 : Split(p.Name).Number)
                               .ThenByDescending(p => p.Modified)
                               .ThenBy(p => Marked(p.Name))   // "Didums_70 - Copy" saved with Didums_70: the original
                               .ThenByDescending(p => Split(p.Name).Number)
                               .ThenByDescending(p => FixedTail().IsMatch(p.Name))   // saved the same second: the fix came after
                               .ToList();
                return new SongGroup(SongName(g.Key, ordered, p => (p.Folder, p.Daw, p.Name)), ordered[0], ordered.Skip(1).ToList()) { Key = g.Key };
            })
            .ToList();
    }

    /// <summary>
    /// Songs that may be saves of one song, for you to say (Solomon, 19 Oct: "if there is a doubt the app should let you
    /// choose"): in the same folder and DAW, names that differ by a short word or a letter or two once numbers, spaces and
    /// marks are left out ("track nd 1" and "track 2", "Sunrise" and "Sunrize"); with <paramref name="acrossFolders"/>
    /// also the same name in different folders. Pairs you said are different (<see cref="SongChoices.Apart"/>) aren't asked
    /// again. Newest first.
    /// </summary>
    public static IReadOnlyList<SongDoubt> Doubts(IReadOnlyList<SongGroup> songs, SongChoices? choices = null, bool acrossFolders = false, int most = 200)
    {
        choices ??= SongChoices.None;
        var named = songs.Where(g => !DawDefaults.Contains(g.Song.Trim()) && Skeleton(g.Song).Length >= 3).ToList();
        var doubts = new List<SongDoubt>();
        void Add(SongGroup a, SongGroup b, string why)
        {
            if (a.Key != b.Key && !choices.Apart(a.Key, b.Key)) doubts.Add(First(a, b) ? new(a, b, why) : new(b, a, why));
        }
        // The newer song first (its name is the first offered), or the one with more saves.
        static bool First(SongGroup a, SongGroup b) =>
            a.Latest.Modified != b.Latest.Modified ? a.Latest.Modified > b.Latest.Modified : a.SaveCount >= b.SaveCount;
        foreach (var bucket in named.GroupBy(g => (Folder: SongFolder(g.Latest.Folder, g.Latest.Name).ToLowerInvariant(), g.Latest.Daw)))
        {
            var list = bucket.Select(g => (Song: g, Skeleton: Skeleton(g.Song))).ToList();
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                    if (Close(list[i].Skeleton, list[j].Skeleton) is { } why) Add(list[i].Song, list[j].Song, why);
        }
        if (acrossFolders)
            foreach (var same in named.GroupBy(g => (g.Latest.Daw, Skeleton(g.Song))))
            {
                var list = same.ToList();
                for (var i = 0; i < list.Count; i++)
                    for (var j = i + 1; j < list.Count; j++)
                        if (!string.Equals(SongFolder(list[i].Latest.Folder, list[i].Latest.Name), SongFolder(list[j].Latest.Folder, list[j].Latest.Name),
                                           StringComparison.OrdinalIgnoreCase))
                            Add(list[i], list[j], "Same name in different folders");
            }
        // Saves a mark put with their song ("Night Drive final" with Night Drive_2 … _14): asked once each. A song whose saves
        // all carry the same mark ("Night Drive v2", "v3") has nothing to ask.
        foreach (var g in songs.Where(g => g.SaveCount > 1))
        {
            var saves = g.Older.Prepend(g.Latest).ToList();
            var plain = saves.Select(p => PlainSong(p.Name)).ToList();
            for (var i = 0; i < saves.Count; i++)
            {
                var save = saves[i];
                var own = SongChoices.SplitKey(save.Path);
                if (!Marked(save.Name) || choices.IsSplit(save.Path) || choices.Apart(g.Key, own) ||
                    plain.All(p => p.Equals(plain[i], StringComparison.OrdinalIgnoreCase))) continue;
                var name = FixedTail().Replace(save.Name, "");
                var mark = name.StartsWith(g.Song, StringComparison.OrdinalIgnoreCase) ? name[g.Song.Length..].Trim(' ', '_', '-', '.') : "";
                doubts.Add(new(g, new SongGroup(save.Name, save, []) { Key = own },
                               mark.Length > 0 ? $"It was put with {g.Song} because its name adds \"{mark}\"" : $"It was put with {g.Song} by a save mark in its name")
                           { OneSave = true });
            }
        }
        return doubts.OrderByDescending(d => d.A.Latest.Modified).Take(most).ToList();
    }

    /// <summary>A song's name with only its letters, lower case: "Track nd" is "tracknd".</summary>
    static string Skeleton(string song) => new(song.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Why two names (as <see cref="Skeleton"/>s) may be one song, or null when they're plainly different or the same.</summary>
    static string? Close(string a, string b)
    {
        if (a == b) return null;   // the same name, numbered differently: the rules already decided
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        if (longer.StartsWith(shorter, StringComparison.Ordinal) && longer.Length - shorter.Length <= 3)
            return $"The names differ only by \"{longer[shorter.Length..]}\"";
        if (longer.EndsWith(shorter, StringComparison.Ordinal) && longer.Length - shorter.Length <= 3)
            return $"The names differ only by \"{longer[..^shorter.Length]}\"";
        var most = longer.Length >= 8 ? 2 : 1;
        return shorter.Length >= 4 && longer.Length - shorter.Length <= most && Distance(a, b, most) <= most
            ? (Distance(a, b, most) == 1 ? "The names are one letter apart" : "The names are two letters apart")
            : null;
    }

    /// <summary>How many letters to change, add or remove to turn one into the other, up to <paramref name="most"/> + 1.</summary>
    static int Distance(string a, string b, int most)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var best = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                best = Math.Min(best, current[j]);
            }
            if (best > most) return most + 1;
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>
    /// A group's name: the name of the song the others were joined to (one of its own saves names it), else the newest's.
    /// Mixed numbering ("track 2" newer than "track nd 1") picks a later save's number first, so the newest isn't always it.
    /// </summary>
    static string SongName<T>(string key, IReadOnlyList<T> ordered, Func<T, (string Folder, string Daw, string Name)> of)
    {
        foreach (var p in ordered)
            if (of(p) is var (folder, daw, name) && Key(folder, daw, name) == key) return Split(name).Song;
        return Split(of(ordered[0]).Name).Song;
    }

    /// <summary>
    /// Saves grouped into songs the way <see cref="Group"/> does, newest song first. Each keeps its highest-numbered save
    /// in the list (ties go to the most recent), so a plugin's song shows the last save that still used it.
    /// </summary>
    public static IReadOnlyList<SongUse> Songs(IEnumerable<PluginProject> saves, SongChoices? choices = null)
    {
        choices ??= SongChoices.None;
        return saves.GroupBy(p => choices.KeyOf(p.Path, p.Folder, p.Daw, p.Name))
            .Select(g =>
            {
                var mixed = Mixed(g.Select(p => p.Name));
                var ordered = g.OrderByDescending(p => mixed ? 0 : Split(p.Name).Number).ThenByDescending(p => p.Saved)
                               .ThenByDescending(p => Split(p.Name).Number).ToList();
                return new SongUse(SongName(g.Key, ordered, p => (p.Folder, p.Daw, p.Name)), ordered[0], g.Count());
            })
            .OrderByDescending(s => s.Latest.Saved).ThenBy(s => s.Song, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
