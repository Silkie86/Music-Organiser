using System.Globalization;
using System.Text;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.Core.Formats;

/// <summary>The formatlab command (tools/MusicOrganiser.FormatLab): Format Lab, the census and the probe from a terminal.</summary>
public static class FormatLabCommand
{
    public const string Usage = """
        Format Lab: looks inside Bitwig and FL Studio files, to learn how each DAW saves things.

          formatlab tree <file> [--depth N]        everything in the file, named where the app knows the name
          formatlab diff <before> <after>          every value that differs between two saves
          formatlab find <file> <value> [--text]   everywhere the file holds a value: text or a number
          formatlab census <folder>... [--top N]   what the app doesn't know yet, across all projects in folders
          formatlab probe [<file>] [--reports <folder>]
                                                   MO Probe's reports, or the probe's state in a project checked against them
          formatlab fields bitwig|fl [<id>]        what the app knows (the field registry)

        Files: .bwproject, .bwpreset, .bwclip and .flp.
        """;

    /// <summary>Runs one command; returns the exit code (0 done, 1 failed, 2 not understood).</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        var rest = args.Skip(1).ToList();
        string? Option(string name)
        {
            var i = rest.IndexOf(name);
            if (i < 0) return null;
            var v = i + 1 < rest.Count ? rest[i + 1] : null;
            rest.RemoveRange(i, v is null ? 1 : 2);
            return v ?? "";
        }
        bool Flag(string name) => rest.Remove(name);
        int Number(string? text, int fallback) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : fallback;

        try
        {
            switch (args[0])
            {
                case "tree":
                {
                    var depth = Number(Option("--depth"), int.MaxValue);
                    if (rest.Count != 1) return Wrong(error, "tree needs one file");
                    output.Write(FormatLab.Tree(LabFile.Open(rest[0]), depth));
                    return 0;
                }
                case "diff":
                {
                    if (rest.Count != 2) return Wrong(error, "diff needs two files: the save before and the save after");
                    var (a, b) = (LabFile.Open(rest[0]), LabFile.Open(rest[1]));
                    if (a.Format != b.Format) return Fail(error, "Those are files from two different DAWs.");
                    output.Write(FormatLab.DiffText(a, b, FormatLab.Diff(a, b)));
                    return 0;
                }
                case "find":
                {
                    var text = Flag("--text");
                    if (rest.Count != 2) return Wrong(error, "find needs a file and a value");
                    var f = LabFile.Open(rest[0]);
                    output.Write(FormatLab.FindText(f, rest[1], FormatLab.Find(f, rest[1], text)));
                    return 0;
                }
                case "census":
                {
                    var top = Number(Option("--top"), 40);
                    if (rest.Count == 0) return Wrong(error, "census needs at least one folder");
                    var files = rest.Where(File.Exists).Concat(Scanner.FindProjects(rest.Where(Directory.Exists)))
                                    .Where(FormatCensus.IsProject).Distinct().ToList();
                    error.WriteLine($"Counting {files.Count:N0} projects…");
                    var census = FormatCensus.Run(files, new Progress<int>(n => { if (n % 1000 == 0) error.WriteLine($"  {n:N0}"); }));
                    output.Write(census.Report(top));
                    return 0;
                }
                case "probe":
                {
                    var folder = Option("--reports");
                    var reports = ProbeReport.ReadAll(folder);
                    if (rest.Count == 0)
                    {
                        output.WriteLine($"MO Probe reports in {folder ?? MoProbe.ReportFolder}: {reports.Count}");
                        foreach (var r in reports.Take(50))
                            output.WriteLine($"  {r.Ended.ToLocalTime():yyyy-MM-dd HH:mm} {Path.GetFileName(r.File)}: MO Probe {r.Kind}, {r.Summary}");
                        return 0;
                    }
                    if (rest.Count != 1) return Wrong(error, "probe takes one project file, or none to list the reports");
                    var f = LabFile.Open(rest[0]);
                    output.Write(ProbeState.Describe(f, ProbeState.In(f), reports));
                    return 0;
                }
                case "fields":
                {
                    if (rest.Count is 0 or > 2 || rest[0] is not ("bitwig" or "fl")) return Wrong(error, "fields needs bitwig or fl");
                    output.Write(Fields(rest[0] == "bitwig" ? FormatRegistry.Bitwig : FormatRegistry.FlStudio, rest.ElementAtOrDefault(1)));
                    return 0;
                }
                default:
                    return Wrong(error, $"there's no command \"{args[0]}\"");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            return Fail(error, e.Message);
        }
    }

    static string Fields(FormatRegistry r, string? id)
    {
        var sb = new StringBuilder();
        var groups = new[] { ("classes", r.Classes), ("fields", r.Fields), ("metadata", r.Meta), ("events", r.Events),
                             ("Fruity Wrapper records", r.Wrapper), ("VST3 records", r.Vst3) };
        if (id is null) sb.AppendLine(r.Format).AppendLine(r.About);
        foreach (var (name, entries) in groups)
        {
            var shown = entries.Where(e => id is null || e.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                                           e.Name.Contains(id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (shown.Count == 0) continue;
            sb.AppendLine().AppendLine($"{name}:");
            foreach (var e in shown)
            {
                sb.AppendLine($"  {e.Id,-8} {e.Name}{(e.Type is null ? "" : $" ({e.Type})")}");
                if (id is null) continue;
                if (e.About.Length > 0) sb.AppendLine($"           {e.About}");
                if (e.Layout is not null) sb.AppendLine($"           layout: {e.Layout}");
                if (e.RecordLayout is not null) sb.AppendLine($"           each record: {e.RecordLayout}");
                if (e.Seen is not null) sb.AppendLine($"           seen in: {e.Seen}");
                if (e.Proof is not null) sb.AppendLine($"           how we know: {e.Proof}");
                if (e.Code is not null) sb.AppendLine($"           in the code: {e.Code}");
            }
        }
        return sb.ToString();
    }

    static int Wrong(TextWriter error, string why)
    {
        error.WriteLine($"Format Lab: {why}.");
        error.WriteLine();
        error.WriteLine(Usage);
        return 2;
    }

    static int Fail(TextWriter error, string why)
    {
        error.WriteLine($"Format Lab: {why}");
        return 1;
    }
}
