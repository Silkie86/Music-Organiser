using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MusicOrganiser.Core.LiveTests;

/// <summary>
/// The live tests' reports: each run writes &lt;UTC stamp&gt;-&lt;daw&gt;.json (everything, for reading by program) and
/// &lt;UTC stamp&gt;-&lt;daw&gt;.md (a readable summary: the counts, then each failure and note, then the rest) into
/// reports\live in the app's data folder, to be read on the PC. The newest 50 runs of each DAW are kept.
/// </summary>
public static class LiveReports
{
    /// <summary>How many runs of each DAW are kept.</summary>
    public const int Kept = 50;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The app's data folder, where the library and MO Probe's reports also live: %LOCALAPPDATA%\MusicOrganiser on
    /// Windows, the same under Application Support on a Mac, and ~/.local/share on Linux.
    /// </summary>
    public static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
                     "MusicOrganiser");

    /// <summary>Where the reports go unless told otherwise: reports\live in the app's data folder.</summary>
    public static string DefaultFolder => Path.Combine(DataFolder, "reports", "live");

    /// <summary>The app's version, with the commit it was built from when the build knew it.</summary>
    public static string AppVersion
    {
        get
        {
            var core = typeof(LiveReports).Assembly;
            return core.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? core.GetName().Version?.ToString() ?? "";
        }
    }

    /// <summary>A run's stamp: when it started, in UTC, as its report files are named ("20261014T143012Z").</summary>
    public static string Stamp(DateTimeOffset time) => time.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes a run's two reports into <paramref name="folder"/> and tidies away all but the newest <see cref="Kept"/>
    /// runs of its DAW. Returns the run with where its reports are, or why they couldn't be written.
    /// </summary>
    public static LiveRun Write(LiveRun run, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var name = $"{run.Stamp}-{run.Key}";
            // Two runs in the same second (a quick run stopped straight away, then another) keep both.
            for (var n = 2; File.Exists(Path.Combine(folder, name + ".json")); n++) name = $"{run.Stamp}-{n}-{run.Key}";
            var json = Path.Combine(folder, name + ".json");
            var markdown = Path.Combine(folder, name + ".md");
            var written = run with { ReportJson = json, ReportMarkdown = markdown };
            File.WriteAllText(json, ToJson(written).ToJsonString(Json) + "\n");
            File.WriteAllText(markdown, ToMarkdown(written));
            Prune(folder, run.Key);
            return written;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return run with { ReportProblem = $"The report couldn't be written to {folder}: {e.Message}" };
        }
    }

    /// <summary>Deletes the reports of all but the newest <paramref name="keep"/> runs of a DAW, leaving other files alone.</summary>
    public static void Prune(string folder, string key, int keep = Kept)
    {
        foreach (var run in Runs(folder, key).Skip(keep))
            foreach (var file in run.Files)
            {
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // open in an editor: next time
            }
    }

    /// <summary>The newest run's readable report for a DAW in <paramref name="folder"/>, or null if there's none.</summary>
    public static string? Newest(string folder, string key) =>
        Runs(folder, key).SelectMany(r => r.Files).FirstOrDefault(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase));

    /// <summary>A DAW's runs in a folder, newest first, each with its report files.</summary>
    static IEnumerable<(string Stamp, int Count, List<string> Files)> Runs(string folder, string key)
    {
        if (!Directory.Exists(folder)) return [];
        var suffix = $"-{key}";
        return Directory.EnumerateFiles(folder, $"*{suffix}.*")
            .Where(f => Path.GetExtension(f) is ".json" or ".md")
            .Select(f => (File: f, Name: Path.GetFileNameWithoutExtension(f)))
            .Where(f => f.Name.EndsWith(suffix, StringComparison.Ordinal) && RunOf(f.Name[..^suffix.Length]) is not null)
            .GroupBy(f => RunOf(f.Name[..^suffix.Length])!.Value)
            .Select(g => (g.Key.Stamp, g.Key.Count, Files: g.Select(f => f.File).ToList()))
            .OrderByDescending(r => r.Stamp, StringComparer.Ordinal).ThenByDescending(r => r.Count);
    }

    /// <summary>
    /// Which run a report is, from its name without the DAW: its stamp ("20261014T143012Z"), and a count after it when
    /// two runs started in the same second ("20261014T143012Z-2"). Null for a name that isn't a run's.
    /// </summary>
    static (string Stamp, int Count)? RunOf(string name)
    {
        if (name.Length < 16 || !DateTime.TryParseExact(name[..16], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return null;
        if (name.Length == 16) return (name, 1);
        return name[16] == '-' && int.TryParse(name[17..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? (name[..16], n) : null;
    }

    /// <summary>Everything about a run, for reading by program.</summary>
    public static JsonObject ToJson(LiveRun run)
    {
        var counts = new JsonObject { ["passed"] = run.Passed, ["failed"] = run.Failed, ["skipped"] = run.Skipped, ["noted"] = run.Noted };
        var tests = new JsonArray();
        foreach (var r in run.Results)
        {
            var test = new JsonObject
            {
                ["area"] = r.Area, ["name"] = r.Name, ["outcome"] = r.Outcome.ToString().ToLowerInvariant(), ["detail"] = r.Detail,
                ["started"] = Time(r.Started), ["ms"] = Math.Round(r.Took.TotalMilliseconds, 1),
            };
            if (r.Lines.Count > 0) test["lines"] = new JsonArray(r.Lines.Select(l => (JsonNode?)l).ToArray());
            if (r.Data is not null) test["data"] = r.Data.DeepClone();
            tests.Add(test);
        }
        return new JsonObject
        {
            ["daw"] = run.Daw, ["stamp"] = run.Stamp, ["started"] = Time(run.Started), ["ended"] = Time(run.Ended),
            ["seconds"] = Math.Round(run.Took.TotalSeconds, 1), ["cancelled"] = run.Cancelled, ["summary"] = run.Summary,
            ["setup"] = run.Setup,
            ["app"] = new JsonObject
            {
                ["version"] = run.AppVersion, ["os"] = Environment.OSVersion.VersionString, ["dotnet"] = Environment.Version.ToString(),
            },
            ["hello"] = run.Hello?.DeepClone(), ["counts"] = counts, ["tests"] = tests,
        };
    }

    /// <summary>A readable summary: the counts, then each failure and note with all it says, then every test by area.</summary>
    public static string ToMarkdown(LiveRun run)
    {
        var md = new StringBuilder();
        md.AppendLine($"# {run.Daw}{(run.Key == SelfCheck.Key ? "" : " live tests")}, {run.Started.UtcDateTime.ToString("d MMMM yyyy HH:mm:ss", CultureInfo.InvariantCulture)} UTC");
        md.AppendLine();
        if (run.Setup.Length > 0) md.AppendLine(run.Setup + "  ");
        md.AppendLine($"App {(run.AppVersion.Length > 0 ? run.AppVersion : "(version unknown)")} · run {run.Stamp}  ");
        md.AppendLine($"**{run.Summary}**");

        void Section(string title, LiveOutcome outcome)
        {
            var these = run.Results.Where(r => r.Outcome == outcome).ToList();
            if (these.Count == 0) return;
            md.AppendLine().AppendLine($"## {title}").AppendLine();
            foreach (var r in these)
            {
                md.AppendLine($"### {r.Mark} {r.Title}").AppendLine();
                md.AppendLine(r.Detail);
                if (r.Lines.Count > 0)
                {
                    md.AppendLine();
                    foreach (var line in r.Lines) md.AppendLine(line);
                }
                md.AppendLine();
            }
        }
        Section("Failed", LiveOutcome.Failed);
        Section("Noted", LiveOutcome.Noted);

        md.AppendLine().AppendLine("## Every test").AppendLine();
        foreach (var area in run.Results.GroupBy(r => r.Area))
        {
            md.AppendLine($"### {area.Key}").AppendLine();
            string? last = null;
            foreach (var r in area)
            {
                // The same reason again and again (every change skipped, say) is said once.
                var detail = r.Outcome == LiveOutcome.Skipped && r.Detail == last ? "Skipped, as above." : r.Detail;
                var time = r.Took >= TimeSpan.FromMilliseconds(0.5) ? $" ({r.Time})" : "";   // not "(0 ms)" on each test skipped straight away
                md.AppendLine($"- {r.Mark} **{r.Name}**{time}: {detail}");
                if (r.Outcome is LiveOutcome.Passed && r.Lines.Count > 0)
                    foreach (var line in r.Lines) md.AppendLine($"  {line}");
                last = r.Outcome == LiveOutcome.Skipped ? r.Detail : null;
            }
            md.AppendLine();
        }
        md.AppendLine($"Everything this run saw, with timings and what {run.Daw} answered: `{Path.GetFileName(run.ReportJson ?? $"{run.Stamp}-{run.Key}.json")}`.");
        return md.ToString();
    }

    static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
