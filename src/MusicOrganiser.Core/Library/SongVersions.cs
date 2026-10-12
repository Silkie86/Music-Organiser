using System.Text.RegularExpressions;

namespace MusicOrganiser.Core.Library;

/// <summary>One song and its numbered saves: Song.flp, Song_2.flp … Song_77.flp in one folder.</summary>
public sealed record SongGroup(string Song, ProjectSummary Latest, IReadOnlyList<ProjectSummary> Older);

/// <summary>
/// Groups numbered saves of the same song. FL Studio's "Save new version" names them
/// Song_2, Song_3 …, and plenty of people number saves that way by hand in any DAW.
/// </summary>
public static partial class SongVersions
{
    [GeneratedRegex(@"^(.+?)_(\d{1,4})$")]
    private static partial Regex Numbered();

    /// <summary>"Didums_70" → ("Didums", 70); "Didums" → ("Didums", 1).</summary>
    public static (string Song, int Number) Split(string name)
    {
        var m = Numbered().Match(name);
        return m.Success && m.Groups[1].Value.Trim().Length > 0
            ? (m.Groups[1].Value.TrimEnd(), int.Parse(m.Groups[2].Value))
            : (name, 1);
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
                               .ToList();
                return new SongGroup(Split(ordered[0].Name).Song, ordered[0], ordered.Skip(1).ToList());
            })
            .ToList();
}
