using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.Core.Formats;

/// <summary>The formatlab command (tools/MusicOrganiser.FormatLab): Format Lab, the census and MO Probe from a terminal.</summary>
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
          formatlab probe live                     the MO Probes running in a DAW now, with their knobs
          formatlab probe send <probe> ping|beep [<seconds>]|label <text>|set <knob> <value>
                                                   sends a running probe a command, and shows its answer
          formatlab probe check-bitwig [<probe>]   turns the probe's knobs through Bitwig and back, to check Bitwig's values
          formatlab fields bitwig|fl [<id>]        what the app knows (the field registry)

        Files: .bwproject, .bwpreset, .bwclip and .flp. A <probe> is its instance, its track or its label.
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
                    switch (rest.FirstOrDefault())
                    {
                        case "live":
                            if (rest.Count != 1) return Wrong(error, "probe live takes nothing more");
                            return ProbeLive(folder ?? MoProbe.ReportFolder, output);
                        case "send":
                            return ProbeSend(rest.Skip(1).ToList(), folder ?? MoProbe.ReportFolder, output, error);
                        case "check-bitwig":
                            if (rest.Count > 2) return Wrong(error, "probe check-bitwig takes one probe, or none when only one is running");
                            return ProbeCheckBitwig(rest.ElementAtOrDefault(1), folder ?? MoProbe.ReportFolder, output, error);
                    }
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or DawLinkException)
        {
            return Fail(error, e.Message);
        }
    }

    static int ProbeLive(string folder, TextWriter output)
    {
        var running = MoProbeLive.Running(folder);
        if (running.Count == 0)
        {
            output.WriteLine($"No MO Probe is running now (none has written its status in {Path.Combine(folder, "live")} in the last " +
                             $"{MoProbeLive.Stale.TotalSeconds:0} seconds). Put one on a track in your DAW to see it here.");
            return 0;
        }
        output.WriteLine($"MO Probes running now: {running.Count}");
        foreach (var p in running)
        {
            output.WriteLine($"  {p.Instance} {p.Title} · {p.Summary} · {Checks.Count(p.Commands, "command")}");
            output.WriteLine($"    {p.KnobsText}");
            output.WriteLine($"    report: {p.Report}");
        }
        return 0;
    }

    static int ProbeSend(List<string> args, string folder, TextWriter output, TextWriter error)
    {
        const string how = "probe send needs a probe (its instance, track or label) and a command: ping, beep [<seconds>], label <text> or set <knob> <value>";
        if (args.Count < 2) return Wrong(error, how);
        JsonObject? command;
        switch (args[1])
        {
            case "ping" when args.Count == 2:
                command = ProbeCommand.Ping();
                break;
            case "beep" when args.Count <= 3:
                double? seconds = null;
                if (args.Count == 3)
                {
                    if (!double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return Wrong(error, $"“{args[2]}” isn't a number of seconds");
                    seconds = s;
                }
                command = ProbeCommand.Beep(seconds);
                break;
            case "label" when args.Count >= 3:
                command = ProbeCommand.Label(string.Join(' ', args.Skip(2)));
                break;
            case "set" when args.Count == 4:
                if (ProbeKnobs.Find(args[2]) is not { } knob)
                    return Wrong(error, $"MO Probe has no knob “{args[2]}”: its knobs are {string.Join(", ", MoProbe.Knobs.Select(k => $"{k.Id} {k.Name}"))}");
                if (ProbeKnobs.Parse(knob, args[3]) is not { } value) return Wrong(error, $"“{args[3]}” isn't a value for {MoProbe.KnobName(knob)}");
                command = ProbeCommand.Set(knob, value);
                break;
            default:
                return Wrong(error, how);
        }
        if (Pick(folder, args[0], error) is not { } probe) return 1;
        error.WriteLine($"Sending {args[1]} to {probe.Title} ({probe.Instance}), and waiting for its answer…");
        var answer = MoProbeLive.SendAsync(probe.Instance, command, folder).GetAwaiter().GetResult();
        output.WriteLine(answer.Data.GetRawText());
        return answer.Event == "command-error" ? 1 : 0;
    }

    static int ProbeCheckBitwig(string? which, string folder, TextWriter output, TextWriter error)
    {
        if (Pick(folder, which, error, bitwig: true) is not { } probe) return 1;
        error.WriteLine($"Checking {probe.Title}'s knobs through Bitwig: each one turned in Bitwig, then by the probe. " +
                        "Ctrl+C stops it (the knobs are put back).");
        using var stop = new CancellationTokenSource();
        void Stop(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            stop.Cancel();
        }
        Console.CancelKeyPress += Stop;
        try
        {
            using var link = BitwigLink.ConnectAsync(ct: stop.Token).GetAwaiter().GetResult();
            var check = ProbeKnobCheck.BitwigAsync(link, probe, new Lines(l => output.WriteLine($"  {l.Mark} {l.Text}")), stop.Token)
                                      .GetAwaiter().GetResult();
            output.WriteLine();
            output.WriteLine(check.Summary);
            foreach (var note in check.Notes) output.WriteLine($"  • {note}");
            if (check.ChangesOff is { } off) output.WriteLine(off);
            return check.AllMatched ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            return Fail(error, "Stopped. It put back the knobs it had turned (formatlab probe live shows them).");
        }
        finally
        {
            Console.CancelKeyPress -= Stop;
        }
    }

    /// <summary>
    /// The running probe <paramref name="which"/> names (<see cref="MoProbeLive.Find"/>); with none named, the one running
    /// (in Bitwig, for <paramref name="bitwig"/>). Null, having said why, when there isn't exactly one.
    /// </summary>
    static MoProbeLive? Pick(string folder, string? which, TextWriter error, bool bitwig = false)
    {
        var running = MoProbeLive.Running(folder);
        if (running.Count == 0)
        {
            Fail(error, "No MO Probe is running now. Put one on a track in your DAW, then try again (formatlab probe live lists them).");
            return null;
        }
        var found = running;
        if (which is not null) found = MoProbeLive.Find(running, which);
        else if (bitwig && running.Where(p => p.Host.Contains("Bitwig", StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } inBitwig)
            found = inBitwig;
        if (found.Count == 1) return found[0];
        var listed = string.Join(Environment.NewLine, running.Select(p => $"  {p.Instance} {p.Title} · {p.Summary}"));
        Fail(error, found.Count == 0 ? $"No running MO Probe is “{which}”. The ones running now:{Environment.NewLine}{listed}"
                                     : $"{found.Count} MO Probes {(which is null ? "are running" : $"match “{which}”")}: say which, by its " +
                                       $"instance, track or label.{Environment.NewLine}{listed}");
        return null;
    }

    /// <summary>Reports each line straight away, in order (Progress would post them to the thread pool).</summary>
    sealed class Lines(Action<KnobCheckLine> write) : IProgress<KnobCheckLine>
    {
        public void Report(KnobCheckLine value) => write(value);
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
