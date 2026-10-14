using System.Globalization;
using System.Text;
using System.Text.Json;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Formats;

/// <summary>
/// MO Probe, the test plugin in native/mo-probe: what it is, its reports, and its saved state in project
/// files. Its knobs have distinctive IDs and its state starts with "MOPROBE1", so both are easy to find in
/// a project; its reports say what the DAW sent it and when it saved, so a file can be checked against them.
/// </summary>
public static class MoProbe
{
    public const string Marker = "MOPROBE1";

    /// <summary>The probe's knobs, as native/mo-probe/src/probe.cpp defines them.</summary>
    public static IReadOnlyList<(int Id, string Name, double Min, double Max, double Default)> Knobs { get; } =
    [
        (1001, "MO Knob A", 0, 1, 0.25),
        (1002, "MO Gain", -60, 12, 0),
        (1003, "MO Frequency", 20, 20000, 440),
        (1004, "MO Steps", 0, 7, 3),
        (1005, "MO Switch", 0, 1, 0),
        (1006, "MO Choice", 0, 3, 1),
        (1007, "MO Ramp", 0, 100, 50),
        (1008, "MO Bypass", 0, 1, 0),
    ];

    public static string KnobName(int id) => Knobs.FirstOrDefault(k => k.Id == id).Name ?? $"knob {id}";

    /// <summary>
    /// The probe's formats in <paramref name="pluginFolders"/>: "CLAP", "VST3" and "AU", for each that's there.
    /// (On a Mac each is a bundle, so a folder; on Windows the CLAP is a file.)
    /// </summary>
    public static IReadOnlyList<string> Installed(IEnumerable<string> pluginFolders)
    {
        var found = new List<string>();
        var folders = pluginFolders.ToList();
        foreach (var (file, format) in new[] { ("MO Probe.clap", "CLAP"), ("MO Probe.vst3", "VST3"), ("MO Probe.component", "AU") })
            if (folders.Any(f => Path.Exists(Path.Combine(f, file)))) found.Add(format);
        return found;
    }

    /// <summary>Where native/README.md says to put the probe: the usual plugin folders, and on Windows the per-user ones too.</summary>
    public static IEnumerable<string> InstallFolders()
    {
        foreach (var f in PluginCatalog.DefaultFolders()) yield return f;
        if (!OperatingSystem.IsWindows()) yield break;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        yield return Path.Combine(local, "Programs", "Common", "CLAP");
        yield return Path.Combine(local, "Programs", "Common", "VST3");
    }

    /// <summary>Where the probe writes its reports: the app's data folder (as the probe works it out on each system).</summary>
    public static string ReportFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
                     "MusicOrganiser", "probe");
}

/// <summary>One line of a probe report: when, what happened, and the rest of what it says.</summary>
public sealed record ProbeLine(DateTimeOffset Time, string Event, long Number, JsonElement Data)
{
    public string Str(string name) => Data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    public double? Num(string name) => Data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

/// <summary>A save the probe made, as its report tells it.</summary>
public sealed record ProbeSave(DateTimeOffset Time, int Number, string Identity, string Context, int Bytes);

/// <summary>
/// One probe's report: one plugin instance, from when the DAW made it to when it removed it. The file is
/// JSON lines (see native/mo-probe/src/log.h); a line still being written is left out.
/// </summary>
public sealed class ProbeReport
{
    public required string File { get; init; }
    public required IReadOnlyList<ProbeLine> Lines { get; init; }

    ProbeLine? First(string ev) => Lines.FirstOrDefault(l => l.Event == ev);

    /// <summary>"instrument" or "effect".</summary>
    public string Kind => First("create")?.Str("plugin") is { } p && p.EndsWith(".effect", StringComparison.Ordinal) ? "effect"
        : Path.GetFileName(File).Contains("-effect-", StringComparison.Ordinal) ? "effect" : "instrument";
    public string Instance => Lines.Select(l => l.Str("instance")).FirstOrDefault(s => s.Length > 0) ?? "";
    public string Host => First("create")?.Str("host") ?? "";
    public string HostVersion => First("create")?.Str("hostVersion") ?? "";
    /// <summary>CLAP, VST3 or AU: how the DAW loaded it.</summary>
    public string Format => First("create")?.Str("format") ?? "";
    /// <summary>The track it's on, as the DAW last said (only DAWs that tell CLAP plugins do).</summary>
    public string Track => Lines.LastOrDefault(l => l.Event == "track")?.Str("name") ?? "";
    public string TrackColour => Lines.LastOrDefault(l => l.Event == "track")?.Str("color") ?? "";
    public DateTimeOffset Started => Lines.Count > 0 ? Lines[0].Time : default;
    public DateTimeOffset Ended => Lines.Count > 0 ? Lines[^1].Time : default;
    /// <summary>The DAW removed the plugin (closed the project or deleted it), so the report is complete.</summary>
    public bool Finished => Lines.Any(l => l.Event == "destroy");
    public int Notes => Lines.Count(l => l.Event == "note-on");
    public int KnobMoves => Lines.Count(l => l.Event is "param" or "param-last");
    public IReadOnlyList<ProbeSave> Saves => Lines.Where(l => l.Event == "save")
        .Select(l => new ProbeSave(l.Time, (int)(l.Num("save") ?? 0), l.Str("identity"), l.Str("context"), (int)(l.Num("bytes") ?? 0))).ToList();
    public int Loads => Lines.Count(l => l.Event == "load");

    /// <summary>E.g. "Bitwig Studio 6.1.3 · CLAP · on “Bass” · 2 saves · 31 notes · 4 knob moves".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{(Host.Length > 0 ? Host : "Unknown DAW")} {HostVersion}".Trim() };
            if (Format.Length > 0) parts.Add(Format);
            if (Track.Length > 0) parts.Add($"on “{Track}”");
            void Count(int n, string what)
            {
                if (n > 0) parts.Add(n == 1 ? $"1 {what}" : $"{n:N0} {what}s");
            }
            Count(Saves.Count, "save");
            Count(Loads, "load");
            Count(Notes, "note");
            Count(KnobMoves, "knob move");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Reads a report; null when the file can't be read or holds no lines.</summary>
    public static ProbeReport? Read(string file)
    {
        string text;
        try
        {
            // The probe may still be writing it.
            using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(s, Encoding.UTF8);
            text = r.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var lines = new List<ProbeLine>();
        foreach (var raw in text.Split('\n'))
        {
            if (raw.Length < 2 || raw[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var e = doc.RootElement;
                if (!e.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.String) continue;
                var time = e.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String &&
                           DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : default;
                var n = e.TryGetProperty("n", out var nn) && nn.TryGetInt64(out var num) ? num : 0;
                lines.Add(new ProbeLine(time, ev.GetString()!, n, e.Clone()));
            }
            catch (JsonException) { }   // the last line, half written
        }
        return lines.Count == 0 ? null : new ProbeReport { File = file, Lines = lines };
    }

    /// <summary>The reports in <paramref name="folder"/> (the probe's own folder unless given), newest first.</summary>
    public static IReadOnlyList<ProbeReport> ReadAll(string? folder = null, int max = 500)
    {
        folder ??= MoProbe.ReportFolder;
        if (!Directory.Exists(folder)) return [];
        return new DirectoryInfo(folder).EnumerateFiles("*.jsonl")
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(max)
            .Select(f => Read(f.FullName)).OfType<ProbeReport>()
            .OrderByDescending(r => r.Ended).ToList();
    }
}

/// <summary>
/// A probe's saved state found in a file: what it says (the probe saved its knobs, who it is, and how many
/// times it had saved), where it was, and how it was stored.
/// </summary>
public sealed record ProbeState(string Where, string How, string Kind, string Identity, string Instance, string Label,
                                int Save, string Context, IReadOnlyDictionary<int, double> Knobs)
{
    /// <summary>The report of the probe that saved this, if it's among <paramref name="reports"/>.</summary>
    public ProbeReport? SavedBy(IEnumerable<ProbeReport> reports) =>
        reports.FirstOrDefault(r => r.Instance == Instance && r.Saves.Any(s => s.Number == Save));

    /// <summary>Every probe state in a file Format Lab has laid out, stored as plain text.</summary>
    public static IReadOnlyList<ProbeState> In(LabFile file)
    {
        var found = new List<ProbeState>();
        var marker = Encoding.ASCII.GetBytes(MoProbe.Marker);
        var holders = file.Items.Where(i => i.Data is byte[] d && d.AsSpan().IndexOf(marker) >= 0).ToList();
        foreach (var item in holders)
        {
            // Only the innermost place that holds it: an event holds what its records do.
            if (holders.Any(o => o.Key.StartsWith(item.Key + "/", StringComparison.Ordinal))) continue;
            var b = (byte[])item.Data!;
            for (var at = b.AsSpan().IndexOf(marker); at >= 0;)
            {
                if (Parse(b.AsSpan(at)) is { } s)
                    found.Add(s with { Where = item.Path, How = at == 0 ? "plain text" : $"plain text, {at:N0} bytes in" });
                var next = b.AsSpan(at + 1).IndexOf(marker);
                at = next < 0 ? -1 : at + 1 + next;
            }
        }
        return found;
    }

    /// <summary>Reads a state from the start of its marker: "MOPROBE1", a newline, then one JSON object.</summary>
    public static ProbeState? Parse(ReadOnlySpan<byte> data)
    {
        var open = data.IndexOf((byte)'{');
        if (open < 0 || open > 16) return null;
        var json = data[open..];
        var reader = new Utf8JsonReader(json);
        try
        {
            if (!JsonDocument.TryParseValue(ref reader, out var doc)) return null;
            using (doc)
            {
                var e = doc.RootElement;
                string Str(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var knobs = new SortedDictionary<int, double>();
                if (e.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                    foreach (var k in p.EnumerateObject())
                        if (int.TryParse(k.Name, out var id) && k.Value.TryGetDouble(out var v)) knobs[id] = v;
                var save = e.TryGetProperty("save", out var sv) && sv.TryGetInt32(out var n) ? n : 0;
                return new ProbeState("", "", Str("probe"), Str("identity"), Str("instance"), Str("label"), save, Str("context"), knobs);
            }
        }
        catch (JsonException) { return null; }
    }

    /// <summary>What a file holds of the probe, checked against the reports: for Format Lab and the app.</summary>
    public static string Describe(LabFile file, IReadOnlyList<ProbeState> states, IReadOnlyList<ProbeReport> reports)
    {
        var sb = new StringBuilder();
        if (states.Count == 0)
        {
            sb.AppendLine($"No MO Probe state in {file.Name} as plain text.");
            return sb.ToString();
        }
        sb.AppendLine(states.Count == 1 ? $"MO Probe in {file.Name}:" : $"{states.Count} MO Probes in {file.Name}:");
        foreach (var s in states)
        {
            var label = s.Label.Length > 0 ? $" “{s.Label}”" : "";
            sb.AppendLine($"  MO Probe {Capital(s.Kind)}{label}, save {s.Save}{(s.Context.Length > 0 ? $" ({s.Context})" : "")}");
            sb.AppendLine($"    Knobs: {string.Join(", ", s.Knobs.Select(k => $"{MoProbe.KnobName(k.Key)} {LabText.Number(k.Value)}"))}");
            sb.AppendLine($"    Stored in {s.Where}, as {s.How}");
            if (s.SavedBy(reports) is { } r)
            {
                var save = r.Saves.First(x => x.Number == s.Save);
                sb.AppendLine($"    Saved by {r.Host} {r.HostVersion} as {r.Format}{(r.Track.Length > 0 ? $", on “{r.Track}”" : "")}, " +
                              $"{save.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({Path.GetFileName(r.File)})");
            }
            else sb.AppendLine("    No report has this save (it may have been saved on another computer, or the reports were cleared).");
        }
        return sb.ToString();
    }

    static string Capital(string s) => s.Length == 0 ? "" : char.ToUpperInvariant(s[0]) + s[1..];
}
