using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Plugins;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.LiveTests;

/// <summary>What a self-check looks at: Settings fills it from the library and the app.</summary>
public sealed record SelfCheckOptions
{
    /// <summary>Where the reports go: reports\checks in the app's data folder.</summary>
    public string ReportFolder { get; init; } = SelfCheck.DefaultFolder;
    public string AppVersion { get; init; } = LiveReports.AppVersion;
    /// <summary>The app's data folder, which must be writable.</summary>
    public string DataFolder { get; init; } = LiveReports.DataFolder;
    /// <summary>The folders the library scans for projects.</summary>
    public IReadOnlyList<string> ProjectFolders { get; init; } = [];
    /// <summary>The folders the sound library scans.</summary>
    public IReadOnlyList<string> SampleFolders { get; init; } = [];
    /// <summary>The folders the Plugins page scans.</summary>
    public IReadOnlyList<string> PluginFolders { get; init; } = [];
    /// <summary>The plugin folders are the usual places, not ones chosen in Settings: those not on this computer aren't missing.</summary>
    public bool PluginFoldersAreDefaults { get; init; }
    /// <summary>Every project in the library, by path: each is opened and read.</summary>
    public IReadOnlyList<string> Projects { get; init; } = [];
    /// <summary>The sound library, to time loading it; null skips the sound checks.</summary>
    public SoundLibrary? Sounds { get; init; }
    /// <summary>The plugin scanner to try; null finds mo-scan as the app does.</summary>
    public string? ScannerExecutable { get; init; }
    /// <summary>
    /// Checks only the app itself can make (sound output, graphics), each run like the others: area, name and the test.
    /// </summary>
    public IReadOnlyList<(string Area, string Name, Func<CancellationToken, Task<LiveVerdict>> Test)> AppChecks { get; init; } = [];
}

/// <summary>
/// The app checking itself on Solomon's computer, with his real library: that the folders are there, that every project
/// opens, how fast the sound library loads at its real size, that the plugin scanner works, and what the app adds
/// (sound output, graphics). It only reads. The report goes in reports\checks, where Claude reads it from the computer,
/// so a test at home needs no copying out. Nothing of the music is in it: file names, timings and error messages only.
/// </summary>
public static class SelfCheck
{
    public const string Key = "selfcheck";
    public const string Name = "Music Organiser self-check";

    /// <summary>How many problems of one kind the report lists line by line; the count covers them all.</summary>
    const int Listed = 40;

    public static string DefaultFolder => Path.Combine(LiveReports.DataFolder, "reports", "checks");

    public static async Task<LiveRun> RunAsync(SelfCheckOptions options, IProgress<LiveProgress>? progress = null, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var run = new LiveRunner(progress, ct);

        await run.RunAsync("Setup", "App data folder can be written", _ => Task.FromResult(DataFolderWritable(options.DataFolder)));
        await run.RunAsync("Setup", "Library folders are there", _ => Task.FromResult(FoldersThere(options)));
        await run.RunAsync("Projects", "Every project opens", t => Task.FromResult(ReadProjects(options.Projects, t)),
                           TimeSpan.FromMinutes(30));
        await run.RunAsync("Sounds", "Sound library loads", t => Task.FromResult(LoadSounds(options.Sounds, t)), TimeSpan.FromMinutes(5));
        await run.RunAsync("Plugins", "Plugin scanner reads a plugin", t => ScanOnePluginAsync(options, t), TimeSpan.FromMinutes(1));
        foreach (var (area, name, test) in options.AppChecks) await run.RunAsync(area, name, test);

        var result = new LiveRun
        {
            Daw = Name, Key = Key, Stamp = LiveReports.Stamp(started), Started = started, Ended = DateTimeOffset.UtcNow,
            Results = run.Results.ToList(), AppVersion = options.AppVersion, Cancelled = run.Stopped,
            Setup = $"{options.Projects.Count} projects, {options.ProjectFolders.Count} project folders, " +
                    $"{options.SampleFolders.Count} sample folders, {Environment.OSVersion.VersionString}, {Environment.ProcessorCount} cores",
        };
        return LiveReports.Write(result, options.ReportFolder);
    }

    static LiveVerdict DataFolderWritable(string folder)
    {
        var probe = Path.Combine(folder, $".selfcheck-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return LiveVerdict.Pass(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return LiveVerdict.Fail($"{folder}: {e.Message}");
        }
    }

    static LiveVerdict FoldersThere(SelfCheckOptions o)
    {
        var all = o.ProjectFolders.Select(f => ("Projects", f)).Concat(o.SampleFolders.Select(f => ("Samples", f)))
                   .Concat(o.PluginFoldersAreDefaults ? [] : o.PluginFolders.Select(f => ("Plugins", f))).ToList();
        if (all.Count == 0) return LiveVerdict.Skip("No folders are set up in Settings yet.");
        var missing = all.Where(f => !Directory.Exists(f.Item2)).Select(f => $"{f.Item1}: {f.Item2}").ToList();
        var data = new JsonObject { ["checked"] = all.Count, ["missing"] = new JsonArray(missing.Select(m => (JsonNode?)m).ToArray()) };
        return missing.Count == 0
            ? LiveVerdict.Pass($"All {all.Count} folders are there.", data)
            : LiveVerdict.Fail($"{missing.Count} of {all.Count} folders can't be found (a drive unplugged, or moved).", data) with { Lines = missing };
    }

    /// <summary>Opens every project with its reader, as a scan does, timing each and noting any that fail.</summary>
    internal static LiveVerdict ReadProjects(IReadOnlyList<string> projects, CancellationToken ct)
    {
        if (projects.Count == 0) return LiveVerdict.Skip("The library has no projects yet.");
        var failures = new List<(string Daw, string Path, string Error)>();
        var damaged = new List<string>();   // files that are empty or broken on disk: worth knowing, not the app's fault
        var times = new Dictionary<string, List<double>>();
        var slowest = new List<(double Ms, string Path)>();
        var gone = 0;
        foreach (var path in projects)
        {
            ct.ThrowIfCancellationRequested();
            if (Readers.For(path) is not { } reader) continue;
            if (!File.Exists(path)) { gone++; continue; }
            var watch = Stopwatch.StartNew();
            try
            {
                var info = reader.Read(path);
                if (info.Confidence == Confidence.Catalogue && info.Warnings.Any(w => w.Contains("empty or damaged", StringComparison.Ordinal)))
                    damaged.Add(path);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures.Add((reader.Daw, path, $"{e.GetType().Name}: {e.Message}"));
            }
            var ms = watch.Elapsed.TotalMilliseconds;
            (times.TryGetValue(reader.Daw, out var list) ? list : times[reader.Daw] = []).Add(ms);
            slowest.Add((ms, path));
        }

        var read = times.Values.Sum(t => t.Count);
        var perDaw = new JsonObject();
        var lines = new List<string>();
        foreach (var (daw, t) in times.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            var sorted = t.Order().ToList();
            perDaw[daw] = new JsonObject
            {
                ["projects"] = t.Count, ["failed"] = failures.Count(f => f.Daw == daw),
                ["msMedian"] = Math.Round(sorted[sorted.Count / 2], 1), ["msMax"] = Math.Round(sorted[^1], 1), ["msTotal"] = Math.Round(t.Sum()),
            };
            lines.Add($"{daw}: {t.Count} read, {failures.Count(f => f.Daw == daw)} failed, median {Ms(sorted[sorted.Count / 2])}, slowest {Ms(sorted[^1])}");
        }
        foreach (var f in failures.Take(Listed)) lines.Add($"Failed: {f.Path} ({f.Error})");
        if (failures.Count > Listed) lines.Add($"…and {failures.Count - Listed} more failed.");
        foreach (var d in damaged.Take(Listed)) lines.Add($"Damaged: {d} (the file itself is empty or broken)");
        if (damaged.Count > Listed) lines.Add($"…and {damaged.Count - Listed} more damaged.");
        var top = slowest.OrderByDescending(s => s.Ms).Take(5).ToList();
        foreach (var s in top) lines.Add($"Slow: {Ms(s.Ms)} {s.Path}");

        var data = new JsonObject
        {
            ["read"] = read, ["missing"] = gone, ["failed"] = failures.Count, ["damaged"] = damaged.Count, ["perDaw"] = perDaw,
            ["failures"] = new JsonArray(failures.Select(f => (JsonNode?)new JsonObject { ["daw"] = f.Daw, ["path"] = f.Path, ["error"] = f.Error }).ToArray()),
            ["slowest"] = new JsonArray(top.Select(s => (JsonNode?)new JsonObject { ["ms"] = Math.Round(s.Ms, 1), ["path"] = s.Path }).ToArray()),
        };
        var notes = new List<string>();
        if (gone > 0) notes.Add($"{gone} in the library aren't on disk any more");
        if (damaged.Count > 0) notes.Add(damaged.Count == 1 ? "1 is a damaged file" : $"{damaged.Count} are damaged files");
        var missingNote = notes.Count > 0 ? $" ({string.Join("; ", notes)})" : "";
        return failures.Count == 0
            ? LiveVerdict.Pass($"All {read} projects opened{missingNote}.", data) with { Lines = lines }
            : LiveVerdict.Fail($"{failures.Count} of {read} projects couldn't be read{missingNote}.", data) with { Lines = lines };
    }

    /// <summary>Loads the whole sound library and its galaxy map, as the Sounds page does, and says how long that took.</summary>
    static LiveVerdict LoadSounds(SoundLibrary? sounds, CancellationToken ct)
    {
        if (sounds is null) return LiveVerdict.Skip("No sound library to look at.");
        var watch = Stopwatch.StartNew();
        var count = sounds.Count();
        var all = sounds.All();
        var loadMs = watch.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested();
        watch.Restart();
        var map = sounds.Map();
        var mapMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        // A search as the search box makes one: every sound's name against a word.
        var hits = all.Count(s => s.Name.Contains("kick", StringComparison.OrdinalIgnoreCase));
        var searchMs = watch.Elapsed.TotalMilliseconds;
        var withClap = sounds.CountWithClap();
        var data = new JsonObject
        {
            ["sounds"] = count, ["withClap"] = withClap, ["onMap"] = map.Count,
            ["msLoadAll"] = Math.Round(loadMs), ["msLoadMap"] = Math.Round(mapMs), ["msSearchKick"] = Math.Round(searchMs, 1), ["kickHits"] = hits,
        };
        var detail = $"{count:N0} sounds loaded in {Ms(loadMs)}, map of {map.Count:N0} in {Ms(mapMs)}, a name search in {Ms(searchMs)}.";
        return count == 0 ? LiveVerdict.Skip("The sound library is empty: scan sample folders first.", data) : LiveVerdict.Note(detail, data);
    }

    static async Task<LiveVerdict> ScanOnePluginAsync(SelfCheckOptions o, CancellationToken ct)
    {
        var scanner = new PluginScanner(o.ScannerExecutable);
        if (scanner.Executable is not { } exe || !File.Exists(exe))
            return LiveVerdict.Fail("The plugin scanner (mo-scan) isn't with the app. " +
                                    (PluginScanner.BuildProblem() ?? "It needs Visual Studio's C++ workload to build."));
        var file = o.PluginFolders.Where(Directory.Exists).SelectMany(PluginFiles).FirstOrDefault();
        if (file is null) return LiveVerdict.Skip($"Found the scanner ({scanner.Executable}), but no CLAP or VST3 plugin to try it on.");
        var watch = Stopwatch.StartNew();
        var scan = await scanner.ScanAsync(file, ct: ct);
        var data = new JsonObject { ["scanner"] = scanner.Executable, ["file"] = file, ["plugins"] = scan.Plugins.Count, ["ms"] = Math.Round(watch.Elapsed.TotalMilliseconds) };
        return scan.Error.Length > 0 || scan.Plugins.Count == 0
            ? LiveVerdict.Fail($"{Path.GetFileName(file)}: {(scan.Error.Length > 0 ? scan.Error : "no plugin found inside")}", data)
            : LiveVerdict.Pass($"Read {Path.GetFileName(file)} ({scan.Plugins[0].Name}) in {Ms(watch.Elapsed.TotalMilliseconds)}.", data);
    }

    static IEnumerable<string> PluginFiles(string folder)
    {
        IEnumerable<string> found;
        try
        {
            found = Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                             .Where(p => p.EndsWith(".clap", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
                             .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        return found;
    }

    static string Ms(double ms) => ms >= 1000 ? (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s" : $"{Math.Round(ms):0} ms";
}
