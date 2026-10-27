using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Plugins.Serum;

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
/// One line of Fix's preview. <paramref name="Use"/> is the file or plugin it would use instead. Samples are written
/// into the fixed copy, and Serum swapped for Serum 2 (<see cref="IsSwap"/>), whose sound the app can translate; other
/// plugin swaps are listed for you to make in the DAW, since a newer version may not take the old one's settings.
/// </summary>
public sealed record FixLine(bool IsSample, string Missing, FixAction Action, string? Use, string Note)
{
    /// <summary>A plugin the fixed copy swaps for <see cref="Use"/>, with its sound translated (Serum for Serum 2).</summary>
    public bool IsSwap { get; init; }
    /// <summary>The save itself is damaged (FL stopped writing it part-way): the fixed copy is repaired, finished from
    /// <see cref="Use"/>, an older save of the song, when there is one.</summary>
    public bool IsRepair { get; init; }
    /// <summary>The other files it could use instead of <see cref="Use"/> (Use first), best first.</summary>
    public IReadOnlyList<string> Choices { get; init; } = Use is null ? [] : [Use];
    /// <summary>The file name or plugin name, for the line's title.</summary>
    public string Name => IsSample ? Missing.Split('\\', '/')[^1] : Missing;
    /// <summary>Whether the fixed copy can use <see cref="Use"/>: a sample, when there's one, or a plugin swap.</summary>
    public bool CanWrite => IsSwap || IsRepair || IsSample && Use is not null && Action != FixAction.StillMissing;
}

/// <summary>What <see cref="ProjectFixer.Write"/> did: where the fixed copy went, how many samples it relinked and
/// plugins it swapped, and anything to tell you about the swaps.</summary>
public sealed record FixResult(string Path, int Relinked, int Swapped, IReadOnlyList<string> Notes)
{
    /// <summary>Whether the save itself was repaired.</summary>
    public bool Repaired { get; init; }
}

/// <summary>
/// Fix: repairs what it can of a project's missing samples into a new file next to the original
/// ("Didums_77 (fixed).flp"), and can swap Serum for Serum 2 in it. The original is never changed.
/// </summary>
public static partial class ProjectFixer
{
    [System.Text.RegularExpressions.GeneratedRegex(@"(\s*\(fixed(\s+\d+)?\))+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FixedSuffix();

    /// <summary>Whether Fix can write this kind of project (FL Studio and Bitwig so far).</summary>
    public static bool CanWrite(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".flp" or ".bwproject";

    /// <summary>Whether the save itself is damaged in a way Fix repairs: an FL Studio save FL didn't finish writing.</summary>
    public static bool NeedsRepair(ProjectDetail project) =>
        project.Summary.Path.EndsWith(".flp", StringComparison.OrdinalIgnoreCase) &&
        project.Warnings.Any(w => w is FlStudioReader.StoppedPartWay or FlStudioReader.GarbledHeader);

    /// <summary>One line per missing sample and plugin, with the best guess for each, after a line for repairing the save itself if it's damaged.</summary>
    public static IReadOnlyList<FixLine> Plan(LibraryDb db, ProjectDetail project)
    {
        var lines = new List<FixLine>();
        if (Repair(project.Summary.Path) is { } repair) lines.Add(repair);
        var indexed = db.SampleIndexCount() > 0;
        var why = db.GetList(Scanner.SampleFoldersKey).Count > 0
            ? "No sound files were found in your sample folders, so there was nowhere to look for it."
            : "Add your sample folders in Settings so Fix can look for it on your drives.";
        foreach (var s in project.Samples.Where(s => !s.Found))
            lines.Add(indexed ? ForSample(db, s.Path) : new FixLine(true, s.Path, FixAction.StillMissing, null, why));

        var installed = db.InstalledNames();
        var swap = SerumSwap(project, installed);
        if (swap is not null) lines.Add(swap);
        foreach (var p in project.Plugins.Where(p => !p.Installed && !(swap is not null && IsSerum1(p.Name))))
            lines.Add(ForPlugin(db, p.Name, installed));
        return lines;
    }

    /// <summary>The line for repairing a save FL didn't finish writing, or null when it isn't damaged.</summary>
    static FixLine? Repair(string path)
    {
        if (!path.EndsWith(".flp", StringComparison.OrdinalIgnoreCase) || FlStudioReader.Damage(path) is not { } damage) return null;
        if (!damage.EndsEarly)
            return new FixLine(false, "This save", FixAction.Relink, null,
                "FL Studio didn't finish writing the start of its data. The fixed copy writes it in; nothing else changes.") { IsRepair = true };
        var older = FlStudioReader.WholeOlderSave(path);
        return new FixLine(false, "This save", FixAction.Relink, older, older is null
            ? $"FL Studio stopped part-way through writing it, after {damage.Channels} channels. No earlier save of this song was found "
              + "to finish it from, so the fixed copy has what it got as far as, without a mixer or playlist."
            : $"FL Studio stopped part-way through writing it, after {damage.Channels} channels. The fixed copy keeps everything it got as far as "
              + $"(your newest changes) and takes the rest, its last channels, mixer and playlist, from {Path.GetFileNameWithoutExtension(older)}.")
            { IsRepair = true };
    }

    /// <summary>Whether Fix can swap this project's Serum for Serum 2: an FL Studio or Bitwig project with Serum in it, and Serum 2 installed.</summary>
    public static bool CanSwapSerum(ProjectDetail project, IReadOnlySet<string> installed) => SerumSwap(project, installed) is not null;

    static FixLine? SerumSwap(ProjectDetail project, IReadOnlySet<string> installed)
    {
        if (!CanWrite(project.Summary.Path) || !installed.Any(IsSerum2) || project.Plugins.FirstOrDefault(p => IsSerum1(p.Name)) is not { } serum)
            return null;
        // Serum 1 missing: the swap is the fix, so it's ticked. Installed: it's yours to choose.
        return new FixLine(false, serum.Name, serum.Installed ? FixAction.Suggestion : FixAction.Relink, "Serum 2",
            "Tick it to swap in Serum 2 with Serum's sound translated. Some settings only come across roughly; "
            + "automation of Serum's knobs is left out.") { IsSwap = true };
    }

    /// <summary>The original Serum (not Serum 2 or Serum FX), by any of the names DAWs give it.</summary>
    static bool IsSerum1(string name) => IsSerumSynth(name) && !name.Contains('2');

    /// <summary>Serum 2, as "Serum 2" or "Serum2".</summary>
    static bool IsSerum2(string name) => IsSerumSynth(name) && name.Contains('2');

    static bool IsSerumSynth(string name) =>
        PluginCatalog.Family(name) == "serum" && !name.Contains("FX", StringComparison.OrdinalIgnoreCase);

    static FixLine ForSample(LibraryDb db, string path)
    {
        var matches = MatchFinder.ForSample(db, path, 8).Where(m => File.Exists(m.Candidate)).ToList();
        var choices = matches.Select(m => m.Candidate).ToList();
        var same = matches.Where(m => m.Score >= 1).ToList();
        if (same.Count > 0)
            return new FixLine(true, path, FixAction.Relink, same[0].Candidate, same.Count == 1
                ? "Found with the same name"
                : same[0].Folders > 0
                    ? $"Found with the same name: {same.Count} copies, using the one in the most similar folders"
                    : $"Found with the same name: {same.Count} copies, using the first") { Choices = choices };
        if (matches.FirstOrDefault() is { } best)
            return new FixLine(true, path, FixAction.Suggestion, best.Candidate, best.Folders > 0
                ? $"Not found, but this one has a similar name ({best.Score:P0}) in matching folders. Tick it to use it."
                : $"Not found, but this one has a similar name ({best.Score:P0}). Tick it to use it.") { Choices = choices };
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

    /// <summary>
    /// Where the fixed copy goes: next to the original, "Name (fixed).ext", or "(fixed 2)" and on if that's taken. Fixing
    /// a fixed copy again makes the next "(fixed N)" of the same name, not "Name (fixed) (fixed)".
    /// </summary>
    public static string FixedPath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = FixedSuffix().Replace(Path.GetFileNameWithoutExtension(path), "");
        var ext = Path.GetExtension(path);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(dir, $"{name} (fixed{(n == 1 ? "" : $" {n}")}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Writes the fixed copy with the given samples relinked (missing path → file to use) and Serum swapped for
    /// Serum 2 if that's ticked, and checks it reads back. The original file is only read.
    /// </summary>
    /// <param name="serumAudio">Finds Serum's wavetables for the swap; Serum's own folders unless given.</param>
    /// <param name="serum2User">Serum 2's user presets folder, where each swapped Serum's own sound is saved; found unless given.</param>
    public static FixResult Write(string projectPath, IEnumerable<FixLine> use, Func<string, Serum1To2.AudioInfo?>? serumAudio = null,
                                  string? serum2User = null)
    {
        var lines = use.Where(l => l.CanWrite).ToList();
        var relink = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Where(l => l.IsSample)) relink[line.Missing] = line.Use!;
        var swap = lines.Any(l => l.IsSwap);
        var repair = lines.FirstOrDefault(l => l.IsRepair);
        if (relink.Count == 0 && !swap && repair is null) throw new InvalidOperationException("There's nothing to relink.");

        var data = File.ReadAllBytes(projectPath);
        var changed = 0;
        IReadOnlyList<string> cantWrite = [];
        var kind = Path.GetExtension(projectPath).ToLowerInvariant();
        string? repaired = null;
        if (repair is not null)
        {
            if (kind != ".flp") throw new NotSupportedException("Fix can only repair FL Studio saves.");
            var (fixedData, fromOlder) = FlStudioReader.Repair(data, repair.Use is { } older ? File.ReadAllBytes(older) : null);
            data = fixedData;
            var from = repair.Use is { } o ? Path.GetFileNameWithoutExtension(o) : null;
            repaired = (from, fromOlder) switch
            {
                (null, _) => "Repaired the save.",
                (_, -1) => $"Repaired the save, but its channels didn't line up with {from}'s, so nothing was taken from it: it has no mixer or playlist.",
                (_, 0) => $"Repaired the save, with its mixer and playlist from {from}.",
                (_, 1) => $"Repaired the save, with its last channel, mixer and playlist from {from}.",
                _ => $"Repaired the save, with its last {fromOlder} channels, mixer and playlist from {from}.",
            };
        }
        if (relink.Count > 0)
        {
            (data, changed, cantWrite) = kind switch
            {
                ".flp" => FlStudioReader.RelinkSamples(data, projectPath, relink),
                ".bwproject" => BitwigReader.RelinkSamples(data, projectPath, relink) is { } b ? (b.Data, b.Changed, (IReadOnlyList<string>)[])
                                : throw new InvalidDataException("This Bitwig project couldn't be read fully enough to write a fixed copy."),
                _ => throw new NotSupportedException("Fix can only write FL Studio and Bitwig projects so far."),
            };
            if (changed == 0 && cantWrite.Count > 0 && !swap) throw new InvalidDataException(CantWriteNote(cantWrite));
            if (changed == 0 && cantWrite.Count == 0) throw new InvalidDataException("None of the missing samples could be found inside the project file.");
        }
        var swapped = 0;
        IReadOnlyList<string> notes = [];
        IReadOnlyList<(string Name, byte[] Fxp)> originals = [];
        if (swap)
        {
            serumAudio ??= Serum1To2.XferAudio();
            var result = kind switch
            {
                ".flp" => FlStudioReader.SwapSerum(data, Serum2Path(), serumAudio),
                ".bwproject" => BitwigReader.SwapSerum(data, Serum2Path(), serumAudio)
                                ?? throw new InvalidDataException("This Bitwig project couldn't be read fully enough to write a fixed copy."),
                _ => throw new NotSupportedException("Fix can only swap Serum for Serum 2 in FL Studio and Bitwig projects."),
            };
            if (result.Swapped == 0) throw new InvalidDataException(result.Notes.FirstOrDefault() ?? "No Serum was found inside the project file.");
            (data, swapped, notes) = (result.Data, result.Swapped, kind == ".flp" ? [.. result.Notes, .. FlAutomationNote(projectPath)] : result.Notes);
            originals = result.Originals ?? [];
        }
        if (cantWrite.Count > 0) notes = [CantWriteNote(cantWrite), .. notes];
        if (repaired is not null) notes = [repaired, .. notes];

        var target = FixedPath(projectPath);
        var file = new FileStream(target, FileMode.CreateNew);   // only once it's made is there a copy of ours to remove
        try
        {
            // Written inside the try, so a full disk or an unplugged drive doesn't leave half a copy next to the original.
            using (file) file.Write(data);
            ReadsBack(target, kind);
        }
        catch
        {
            try { File.Delete(target); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // the drive went: the first error is the one to show
            throw;
        }
        // Serum 1's own sounds, saved only once the copy is known to be good: a failed Fix leaves nothing behind.
        if (Serum1To2.KeepOriginals(Path.GetFileNameWithoutExtension(projectPath), originals, serum2User ?? Serum1To2.Serum2UserFolder()) is { } kept)
            notes = [.. notes, kept];
        return new FixResult(target, changed, swapped, notes) { Repaired = repair is not null };
    }

    /// <summary>
    /// Reads the copy back from the disk as the app would: a copy the app can't read is no use to the DAW either. The
    /// readers list what they can of a damaged project instead of failing, so the copy is also decoded strictly: a
    /// Bitwig body that doesn't decode cleanly, or FL events that don't, would be a copy the DAW won't open.
    /// </summary>
    static void ReadsBack(string target, string kind)
    {
        const string bad = "Fix's copy couldn't be read back, so it wasn't saved.";
        if (kind == ".bwproject")
        {
            var raw = File.ReadAllBytes(target);
            if (!BitwigFormat.IsBitwig(raw) || BitwigFormat.Decode(raw) is not { Root: not null, BodyError: null }) throw new InvalidDataException(bad);
        }
        else if (kind == ".flp") FlStudioReader.ReadEvents(File.ReadAllBytes(target));
        Readers.For(target)?.Read(target);
    }

    static string CantWriteNote(IReadOnlyList<string> files) =>
        $"This project is from an FL Studio older than 11.5, which can't store the path of {string.Join(", ", files.Select(f => $"\"{Path.GetFileName(f)}\""))}: " +
        "its folder or name has letters that FL version doesn't know, so it was left missing. Rename it, or copy it to a folder with a plain name, then fix again.";

    /// <summary>
    /// FL's automation of a plugin names its setting by number, and Serum 2's numbers are other knobs, so automation
    /// of Serum's knobs would move the wrong ones. It's left for you to redo, with a note.
    /// </summary>
    static IEnumerable<string> FlAutomationNote(string projectPath)
    {
        FlSong song;
        try { song = Readers.FlStudio.ReadSong(projectPath); }
        catch (InvalidDataException) { yield break; }
        var serum = song.Channels.Where(c => c.Instrument is { } i && IsSerum1(i.Name)).Select(c => c.Index).ToHashSet();
        var clips = song.Automation.Count(a => a.Control == FlControl.PluginParameter && serum.Contains(a.Channel));
        if (clips > 0)
            yield return $"{clips} automation clip{(clips == 1 ? "" : "s")} of Serum's knobs now move other knobs of Serum 2: redo or delete them.";
    }

    /// <summary>Where Serum 2's VST3 is: the usual place, else wherever it is in the VST3 folder.</summary>
    static string Serum2Path()
    {
        const string usual = @"C:\Program Files\Common Files\VST3\Serum2.vst3\Contents\x86_64-win\Serum2.vst3";
        if (!OperatingSystem.IsWindows() || File.Exists(usual)) return usual;
        var vst3 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3");
        try
        {
            return Directory.Exists(vst3) ? Directory.EnumerateFiles(vst3, "Serum2.vst3", SearchOption.AllDirectories).FirstOrDefault() ?? usual : usual;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return usual; }
    }
}
