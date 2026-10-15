using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Library;

/// <summary>What Fix will do about one missing thing.</summary>
public enum FixAction
{
    /// <summary>The same file was found elsewhere on your drives: the fixed copy points at it.</summary>
    Relink,
    /// <summary>A likely stand-in (a similarly named sample, another version of a plugin) that you choose whether to use.</summary>
    Suggestion,
    /// <summary>Nothing to go on: it stays missing in the fixed copy too.</summary>
    StillMissing,
}

/// <summary>
/// One line of Fix's preview. <paramref name="Use"/> is the file or plugin it would use instead. Only samples are ever
/// written into the fixed copy; plugin swaps are listed for you to make in the DAW, since a newer version may not take
/// the old one's settings.
/// </summary>
public sealed record FixLine(bool IsSample, string Missing, FixAction Action, string? Use, string Note)
{
    /// <summary>The file name or plugin name, for the line's title.</summary>
    public string Name => IsSample ? Missing.Split('\\', '/')[^1] : Missing;
    /// <summary>Whether the fixed copy can use <see cref="Use"/>: only a sample, and only when there's one.</summary>
    public bool CanWrite => IsSample && Use is not null && Action != FixAction.StillMissing;
}

/// <summary>
/// Fix: repairs what it can of a project's missing samples into a new file next to the original
/// ("Didums_77 (fixed).flp"). The original is never changed.
/// </summary>
public static class ProjectFixer
{
    /// <summary>Whether Fix can write this kind of project (FL Studio and Bitwig so far).</summary>
    public static bool CanWrite(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".flp" or ".bwproject";

    /// <summary>One line per missing sample and plugin, with the best guess for each.</summary>
    public static IReadOnlyList<FixLine> Plan(LibraryDb db, ProjectDetail project)
    {
        var lines = new List<FixLine>();
        var indexed = db.SampleIndexCount() > 0;
        var why = db.GetList(Scanner.SampleFoldersKey).Count > 0
            ? "No sound files were found in your sample folders, so there was nowhere to look for it."
            : "Add your sample folders in Settings so Fix can look for it on your drives.";
        foreach (var s in project.Samples.Where(s => !s.Found))
            lines.Add(indexed ? ForSample(db, s.Path) : new FixLine(true, s.Path, FixAction.StillMissing, null, why));

        var installed = db.InstalledNames();
        foreach (var p in project.Plugins.Where(p => !p.Installed))
            lines.Add(ForPlugin(db, p.Name, installed));
        return lines;
    }

    static FixLine ForSample(LibraryDb db, string path)
    {
        var matches = MatchFinder.ForSample(db, path, 6).Where(m => File.Exists(m.Candidate)).ToList();
        var same = matches.Where(m => m.Score >= 1).ToList();
        if (same.Count > 0)
            return new FixLine(true, path, FixAction.Relink, same[0].Candidate,
                same.Count == 1 ? "Found with the same name" : $"Found with the same name ({same.Count} copies; using the first)");
        if (matches.FirstOrDefault() is { } best)
            return new FixLine(true, path, FixAction.Suggestion, best.Candidate,
                $"Not found, but this one has a similar name ({best.Score:P0}). Tick it to use it.");
        return new FixLine(true, path, FixAction.StillMissing, null, "Not found in your sample folders.");
    }

    static FixLine ForPlugin(LibraryDb db, string name, IReadOnlySet<string> installed)
    {
        if (PluginCatalog.OtherVersionInstalled(name, installed) is { } other)
            return new FixLine(false, name, FixAction.Suggestion, other,
                $"You have {other}. Swap it in yourself in the DAW: its settings may not carry over from {name}.");
        if (MatchFinder.ForPlugin(db, name, 1).FirstOrDefault(m => m.Score >= 0.7) is { } similar)
            return new FixLine(false, name, FixAction.Suggestion, similar.Candidate,
                $"{similar.Candidate} is installed and has a similar name. Swap it in yourself in the DAW if it's the same plugin.");
        return new FixLine(false, name, FixAction.StillMissing, null, "Not installed on this computer.");
    }

    /// <summary>Where the fixed copy goes: next to the original, "Name (fixed).ext", or "(fixed 2)" and on if that's taken.</summary>
    public static string FixedPath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(dir, $"{name} (fixed{(n == 1 ? "" : $" {n}")}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Writes the fixed copy with the given samples relinked (missing path → file to use) and checks it reads back.
    /// Returns where it went and how many references changed. The original file is only read.
    /// </summary>
    public static (string Path, int Changed) Write(string projectPath, IEnumerable<FixLine> use)
    {
        var relink = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in use.Where(l => l.CanWrite)) relink[line.Missing] = line.Use!;
        if (relink.Count == 0) throw new InvalidOperationException("There's nothing to relink.");

        var raw = File.ReadAllBytes(projectPath);
        var (data, changed) = Path.GetExtension(projectPath).ToLowerInvariant() switch
        {
            ".flp" => FlStudioReader.RelinkSamples(raw, projectPath, relink),
            ".bwproject" => BitwigReader.RelinkSamples(raw, projectPath, relink)
                            ?? throw new InvalidDataException("This Bitwig project couldn't be read fully enough to write a fixed copy."),
            _ => throw new NotSupportedException("Fix can only write FL Studio and Bitwig projects so far."),
        };
        if (changed == 0) throw new InvalidDataException("None of the missing samples could be found inside the project file.");

        var target = FixedPath(projectPath);
        using (var f = new FileStream(target, FileMode.CreateNew)) f.Write(data);
        try
        {
            // Read it back as the app would: a copy the app can't read is no use to the DAW either.
            Readers.For(target)?.Read(target);
        }
        catch
        {
            File.Delete(target);
            throw;
        }
        return (target, changed);
    }
}
