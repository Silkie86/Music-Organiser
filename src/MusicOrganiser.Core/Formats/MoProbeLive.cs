using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MusicOrganiser.Core.DawLink;

namespace MusicOrganiser.Core.Formats;

/// <summary>
/// An MO Probe running in a DAW now, as its live status says. Each probe in use writes its status to
/// &lt;report folder&gt;/live/&lt;instance&gt;.json about twice a second, and takes commands as files dropped in
/// &lt;instance&gt;.in next to it, answering in its report (native/README.md, "MO Probe"). They're plain files, so the
/// app, formatlab and anything else that can read and write files can talk to it.
/// </summary>
public sealed record MoProbeLive
{
    /// <summary>A status older than this belongs to a probe whose DAW went away without saying so.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromSeconds(3);
    /// <summary>A status this old, and its command folder, are tidied away.</summary>
    public static readonly TimeSpan Forgotten = TimeSpan.FromMinutes(1);
    /// <summary>How long to wait for an answer when not told: the probe looks for commands ten times a second.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);
    /// <summary>How long a status can be missing before the probe counts as gone (it's replaced, never removed, while it runs).</summary>
    static readonly TimeSpan Missing = TimeSpan.FromMilliseconds(500);
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static int _sent;

    /// <summary>The status file.</summary>
    public required string File { get; init; }
    /// <summary>"instrument" or "effect".</summary>
    public required string Kind { get; init; }
    /// <summary>This run's ID, as in its report.</summary>
    public required string Instance { get; init; }
    /// <summary>The identity saved in its state, which follows it from save to save.</summary>
    public string Identity { get; init; } = "";
    /// <summary>The label saved in its state, if it has one.</summary>
    public string Label { get; init; } = "";
    /// <summary>The DAW, e.g. "Bitwig Studio" (see <see cref="MoProbe.Daw"/>).</summary>
    public string Host { get; init; } = "";
    /// <summary>The DAW's version, or empty when it came through the CLAP wrapper, which only gives its own.</summary>
    public string HostVersion { get; init; } = "";
    /// <summary>CLAP, VST3 or AU: how the DAW loaded it.</summary>
    public string Format { get; init; } = "";
    /// <summary>The track it's on, as the DAW last said (empty when the DAW doesn't say).</summary>
    public string Track { get; init; } = "";
    public string TrackColour { get; init; } = "";
    /// <summary>Its report's file name, in <see cref="ReportFolder"/>.</summary>
    public string Report { get; init; } = "";
    /// <summary>When it wrote this status.</summary>
    public DateTimeOffset Time { get; init; }
    /// <summary>The DAW's process ID.</summary>
    public int Pid { get; init; }
    /// <summary>Whether the DAW has it switched on (activated).</summary>
    public bool Active { get; init; }
    /// <summary>The transport at the last block it played: null when the DAW gave none.</summary>
    public bool? Playing { get; init; }
    /// <summary>The song position in beats.</summary>
    public double? Beats { get; init; }
    /// <summary>The bar, from 1.</summary>
    public int? Bar { get; init; }
    public double? Tempo { get; init; }
    /// <summary>How many commands it has handled.</summary>
    public int Commands { get; init; }
    /// <summary>Each knob's value in its own units, by ID (see <see cref="MoProbe.Knobs"/>).</summary>
    public IReadOnlyDictionary<int, double> Knobs { get; init; } = new Dictionary<int, double>();

    /// <summary>The plugin's name, as DAWs show it: "MO Probe Instrument" or "MO Probe Effect".</summary>
    public string Name => Kind == "effect" ? "MO Probe Effect" : "MO Probe Instrument";
    /// <summary>E.g. "MO Probe Instrument “Kick” on “Bass”".</summary>
    public string Title => Name + (Label.Length > 0 ? $" “{Label}”" : "") + (Track.Length > 0 ? $" on “{Track}”" : "");
    public string ReportFolder => Path.GetDirectoryName(Path.GetDirectoryName(File)) ?? "";
    public string ReportFile => Path.Combine(ReportFolder, Report);
    /// <summary>Where it takes commands: &lt;instance&gt;.in, next to its status.</summary>
    public string CommandFolder => Path.Combine(Path.GetDirectoryName(File) ?? "", Instance + ".in");

    /// <summary>E.g. "Bitwig Studio 6.1.3 · CLAP · playing at bar 4 · 110 BPM".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{(Host.Length > 0 ? Host : "Unknown DAW")} {HostVersion}".Trim() };
            if (Format.Length > 0) parts.Add(Format);
            if (!Active) parts.Add("not active");
            else if (Playing is true) parts.Add(Bar is { } bar ? $"playing at bar {bar}" : "playing");
            else if (Playing is false) parts.Add("stopped");
            if (Tempo is { } bpm) parts.Add($"{bpm.ToString("0.##", CultureInfo.InvariantCulture)} BPM");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>E.g. "MO Knob A 0.250, MO Gain -12.0 dB, …", in the knobs' own order.</summary>
    public string KnobsText => string.Join(", ", Knobs.OrderBy(k => k.Key).Select(k => $"{MoProbe.KnobName(k.Key)} {ProbeKnobs.Text(k.Key, k.Value)}"));

    /// <summary>Whether its DAW has gone away without saying so: it hasn't written its status for a while.</summary>
    public bool Gone(DateTimeOffset now) => now - Time > Stale;

    /// <summary>
    /// The probes running now, in the order they started: those whose status is under <see cref="Stale"/> old. A status
    /// over <see cref="Forgotten"/> old is deleted, with its command folder, as is a command folder left with no status.
    /// </summary>
    /// <param name="reportFolder">The probe's report folder (<see cref="MoProbe.ReportFolder"/> unless given).</param>
    /// <param name="now">The time to compare with; now unless given.</param>
    public static IReadOnlyList<MoProbeLive> Running(string? reportFolder = null, DateTimeOffset? now = null)
    {
        var live = new DirectoryInfo(LiveFolder(reportFolder));
        var at = now ?? DateTimeOffset.UtcNow;
        var running = new List<MoProbeLive>();
        try
        {
            if (!live.Exists) return [];
            foreach (var f in live.EnumerateFiles())
            {
                if (f.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var status = Read(f.FullName) ?? (at - f.LastWriteTimeUtc <= Stale ? ReadAgain(f.FullName) : null);
                    var written = status?.Time ?? new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero);
                    if (status is not null && at - written <= Stale) running.Add(status);
                    else if (at - written > Forgotten) Forget(f.FullName);
                }
                else if (f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && at - f.LastWriteTimeUtc > Forgotten)
                {
                    TryDelete(f.FullName);   // a status the probe never finished writing
                }
            }
            foreach (var d in live.EnumerateDirectories("*.in"))
                if (!System.IO.File.Exists(Path.ChangeExtension(d.FullName, ".json")) && at - d.LastWriteTimeUtc > Forgotten) Forget(d.FullName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return running.OrderBy(p => p.Report, StringComparer.Ordinal).ThenBy(p => p.Instance, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The running probes that <paramref name="which"/> names: by instance (all of it, or at least its first four
    /// characters, as report names have them), by track or by label, ignoring case.
    /// </summary>
    public static IReadOnlyList<MoProbeLive> Find(IEnumerable<MoProbeLive> running, string which)
    {
        var all = running.ToList();
        var exact = all.Where(p => p.Instance.Equals(which, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;
        return all.Where(p => (which.Length >= 4 && p.Instance.StartsWith(which, StringComparison.OrdinalIgnoreCase)) ||
                              (p.Track.Length > 0 && p.Track.Equals(which.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                              (p.Label.Length > 0 && p.Label.Equals(which.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Reads a probe's status; null when the file isn't there, can't be read, or isn't a probe's status.</summary>
    public static MoProbeLive? Read(string file)
    {
        byte[] bytes;
        DateTime written;
        try
        {
            // Opened so the probe can still replace it while it's read.
            using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            s.CopyTo(copy);
            bytes = copy.ToArray();
            written = System.IO.File.GetLastWriteTimeUtc(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return null;
            string Str(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            double? Num(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            bool? Bool(string n) => e.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

            var instance = Str("instance") is { Length: > 0 } i ? i : Path.GetFileNameWithoutExtension(file);
            var knobs = new SortedDictionary<int, double>();
            if (e.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                foreach (var k in p.EnumerateObject())
                    if (int.TryParse(k.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && k.Value.TryGetDouble(out var v)) knobs[id] = v;
            var (host, version) = MoProbe.Daw(Str("host"), Str("hostVendor"), Str("hostVersion"));
            var time = DateTimeOffset.TryParse(Str("t"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
                ? t : new DateTimeOffset(DateTime.SpecifyKind(written, DateTimeKind.Utc));
            return new MoProbeLive
            {
                File = file, Kind = Str("probe"), Instance = instance, Identity = Str("identity"), Label = Str("label"),
                Host = host, HostVersion = version, Format = Str("format"), Track = Str("track"), TrackColour = Str("trackColor"),
                Report = Path.GetFileName(Str("report")), Time = time, Pid = (int)(Num("pid") ?? 0), Active = Bool("active") ?? false,
                Playing = Bool("playing"), Beats = Num("beats"), Bar = Num("bar") is { } bar ? (int)bar : null, Tempo = Num("tempo"),
                Commands = (int)(Num("commands") ?? 0), Knobs = knobs,
            };
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Sends a running probe a command (see <see cref="ProbeCommand"/>). It's written aside and moved into the probe's
    /// command folder, so the probe never reads half of one. Returns the command's ID, which the probe's answer in its
    /// report carries as "for": the command's own "id", or a new one.
    /// </summary>
    public static string Send(string instance, JsonObject command, string? reportFolder = null)
    {
        var folder = Path.Combine(LiveFolder(reportFolder), CheckInstance(instance) + ".in");
        if (!Directory.Exists(folder)) throw new DawLinkException(NotRunning(instance));
        var line = command.DeepClone().AsObject();
        var id = line["id"] is JsonValue given && given.TryGetValue<string>(out var text) && text.Length > 0 ? text : NewId();
        line["id"] = id;
        // Named by when it was sent, as the probe takes them in name order.
        var name = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'.'fffffff", CultureInfo.InvariantCulture) +
                   $"-{(uint)Interlocked.Increment(ref _sent) % 10000:D4}";
        var temp = Path.Combine(folder, name + ".tmp");
        try
        {
            System.IO.File.WriteAllText(temp, line.ToJsonString(Json) + "\n");
            System.IO.File.Move(temp, Path.Combine(folder, name + ".json"));
        }
        catch (DirectoryNotFoundException e)
        {
            throw new DawLinkException(NotRunning(instance), e);   // the DAW removed it just now
        }
        return id;
    }

    /// <summary>Sends a running probe a command and waits for its answer: the line in its report for the command.</summary>
    /// <exception cref="DawLinkException">The probe isn't running, or didn't answer in time.</exception>
    public static async Task<ProbeLine> SendAsync(string instance, JsonObject command, string? reportFolder = null, TimeSpan? timeout = null,
                                                  CancellationToken ct = default)
    {
        // Only what it writes from now on can be the answer, so a long report isn't read from the start.
        var status = await ReadRunningAsync(StatusFile(instance, reportFolder), instance, ct);
        var tail = status.Report.Length > 0 ? new ReportTail(status.ReportFile, Length(status.ReportFile)) : null;
        var id = Send(instance, command, reportFolder);
        return await AnswerAsync(instance, id, reportFolder, timeout, tail, ct);
    }

    /// <summary>The line in a probe's report that answers command <paramref name="id"/>, once the probe has written it.</summary>
    /// <exception cref="DawLinkException">The probe stopped running, or didn't answer in time.</exception>
    public static Task<ProbeLine> AnswerAsync(string instance, string id, string? reportFolder = null, TimeSpan? timeout = null,
                                              CancellationToken ct = default) =>
        AnswerAsync(instance, id, reportFolder, timeout, null, ct);

    static async Task<ProbeLine> AnswerAsync(string instance, string id, string? reportFolder, TimeSpan? timeout, ReportTail? tail,
                                             CancellationToken ct)
    {
        ProbeLine? answer = null;
        var (answered, status) = await UntilAsync(instance, s =>
        {
            if (s.Report.Length == 0) return false;
            tail ??= new ReportTail(s.ReportFile, 0);
            answer = tail.ReadNew().FirstOrDefault(l => l.Str("for") == id);
            return answer is not null;
        }, reportFolder, timeout, ct);
        return answered ? answer! : throw new DawLinkException($"{status.Title} didn't answer in time. If it's an older build of MO Probe, " +
                                                               "it doesn't take commands: install the new one.");
    }

    /// <summary>
    /// Waits for a probe's status to show a knob at <paramref name="value"/> (in its own units, within
    /// <see cref="ProbeKnobs.Tolerance"/>). Returns whether it did, and the newest status.
    /// </summary>
    public static Task<(bool Shown, MoProbeLive Status)> KnobAsync(string instance, int knob, double value, string? reportFolder = null,
                                                                   TimeSpan? timeout = null, CancellationToken ct = default) =>
        UntilAsync(instance, s => s.Knobs.TryGetValue(knob, out var now) && ProbeKnobs.Same(knob, now, value), reportFolder, timeout, ct);

    /// <summary>Reads a probe's status until <paramref name="shown"/> is true of it or time runs out. Returns whether it was, and the newest status.</summary>
    /// <exception cref="DawLinkException">The probe isn't running, or stopped while this waited.</exception>
    public static async Task<(bool Shown, MoProbeLive Status)> UntilAsync(string instance, Func<MoProbeLive, bool> shown, string? reportFolder = null,
                                                                          TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var file = StatusFile(instance, reportFolder);
        var limit = timeout ?? DefaultWait;
        var waited = Stopwatch.StartNew();
        var seen = TimeSpan.Zero;
        MoProbeLive? last = null;
        while (true)
        {
            if (Read(file) is { } status)
            {
                if (status.Gone(DateTimeOffset.UtcNow)) throw new DawLinkException(NotRunning(instance));
                last = status;
                seen = waited.Elapsed;
                if (shown(status)) return (true, status);
            }
            else if (waited.Elapsed - seen > Missing) throw new DawLinkException(NotRunning(instance));
            if (waited.Elapsed >= limit && last is not null) return (false, last);
            await Task.Delay(Poll, ct);
        }
    }

    /// <summary>A fresh status that couldn't be read may be being swapped for a new one just then (on Windows): a few more tries.</summary>
    static MoProbeLive? ReadAgain(string file)
    {
        for (var i = 0; i < 5; i++)
        {
            Thread.Sleep(Poll / 2);
            if (Read(file) is { } status) return status;
        }
        return null;
    }

    /// <summary>
    /// Reads a running probe's status, giving it a moment if it can't be read just then: on Windows the file can't be
    /// opened for the instant the probe swaps in a new one, which isn't the probe stopping.
    /// </summary>
    /// <exception cref="DawLinkException">The probe isn't running.</exception>
    public static async Task<MoProbeLive> ReadRunningAsync(string file, string instance, CancellationToken ct = default)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            if (Read(file) is { } status) return status;
            if (waited.Elapsed > Missing) throw new DawLinkException(NotRunning(instance));
            await Task.Delay(Poll, ct);
        }
    }

    public static string StatusFile(string instance, string? reportFolder = null) =>
        Path.Combine(LiveFolder(reportFolder), CheckInstance(instance) + ".json");

    public static string NotRunning(string instance) =>
        $"MO Probe {instance} isn't running now: its DAW may have closed, or removed it from the track.";

    static string LiveFolder(string? reportFolder) => Path.Combine(reportFolder ?? MoProbe.ReportFolder, "live");

    /// <summary>An instance is letters and digits (16 hex digits from the probe), so it can't lead out of the folder.</summary>
    static string CheckInstance(string instance) =>
        instance.Length > 0 && instance.All(char.IsAsciiLetterOrDigit) ? instance
            : throw new ArgumentException($"Not an MO Probe instance: “{instance}”.", nameof(instance));

    static string NewId() => "app-" + Guid.NewGuid().ToString("N")[..12];

    static long Length(string file)
    {
        try { return new FileInfo(file).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Deletes a status (or a command folder) and whatever goes with it.</summary>
    static void Forget(string path)
    {
        TryDelete(Path.ChangeExtension(path, ".json"));
        try { Directory.Delete(Path.ChangeExtension(path, ".in"), recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static void TryDelete(string file)
    {
        try { System.IO.File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Reads a report as the probe adds to it: each call gives the lines written since the last.</summary>
    sealed class ReportTail(string file, long from)
    {
        long _read = from;

        public List<ProbeLine> ReadNew()
        {
            var lines = new List<ProbeLine>();
            byte[] fresh;
            try
            {
                using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (s.Length <= _read) return lines;
                s.Position = _read;
                fresh = new byte[s.Length - _read];
                fresh = fresh[..s.ReadAtLeast(fresh, fresh.Length, throwOnEndOfStream: false)];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return lines; }
            // A line still being written is read next time, whole.
            var end = Array.LastIndexOf(fresh, (byte)'\n');
            if (end < 0) return lines;
            _read += end + 1;
            foreach (var raw in Encoding.UTF8.GetString(fresh, 0, end).Split('\n'))
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
                catch (JsonException) { }
            }
            return lines;
        }
    }
}

/// <summary>The commands MO Probe takes (native/README.md, "MO Probe"), to send with <see cref="MoProbeLive.Send"/>.</summary>
public static class ProbeCommand
{
    /// <summary>Answers with "pong", and writes its status straight away.</summary>
    public static JsonObject Ping() => new() { ["cmd"] = "ping" };

    /// <summary>
    /// Turns a knob to a value in its own units, as if by hand, so the DAW's knob (and its automation recording and undo)
    /// follows. Answers with "app-set".
    /// </summary>
    public static JsonObject Set(int knob, double value) => new() { ["cmd"] = "set", ["param"] = knob, ["value"] = value };

    /// <summary>Gives it a label, which it keeps in its saved state. Answers with "label".</summary>
    public static JsonObject Label(string label) => new() { ["cmd"] = "label", ["label"] = CleanLabel(label) };

    /// <summary>Plays a quiet 880 Hz tone (0.3 seconds unless given, at most 2), to hear which track it's on. Answers with "beep".</summary>
    public static JsonObject Beep(double? seconds = null) =>
        seconds is { } s ? new() { ["cmd"] = "beep", ["seconds"] = s } : new() { ["cmd"] = "beep" };

    /// <summary>A label as the probe keeps it: up to 64 characters, without quotes, backslashes or control characters.</summary>
    public static string CleanLabel(string label)
    {
        var kept = string.Concat(label.Where(c => c is not ('"' or '\\') && !char.IsControl(c)));
        return kept.Length > 64 ? kept[..64] : kept;
    }
}

/// <summary>
/// MO Probe's knobs (<see cref="MoProbe.Knobs"/>): how a DAW's 0 to 1 values should land on them, and their values as the
/// probe shows them.
/// </summary>
public static class ProbeKnobs
{
    /// <summary>How close two values of a knob have to be, as a share of its range, to count as the same.</summary>
    public const double Tolerance = 0.005;

    /// <summary>The knobs that only take whole numbers (stepped in native/mo-probe/src/probe.cpp).</summary>
    public static IReadOnlySet<int> Stepped { get; } = new HashSet<int> { 1004, 1005, 1006, 1008 };

    /// <summary>MO Choice's choices, from 0.</summary>
    public static IReadOnlyList<string> Choices { get; } = ["Alpha", "Bravo", "Charlie", "Delta"];

    static (int Id, string Name, double Min, double Max, double Default) Knob(int id) =>
        MoProbe.Knobs.FirstOrDefault(k => k.Id == id) is { Name: not null } k ? k
            : throw new ArgumentOutOfRangeException(nameof(id), $"MO Probe has no knob {id}.");

    /// <summary>The knob's value for a 0 to 1 one, spread evenly over its range, and on a stepped knob the nearest step.</summary>
    public static double Plain(int knob, double normalized)
    {
        var k = Knob(knob);
        var v = k.Min + Math.Clamp(normalized, 0, 1) * (k.Max - k.Min);
        return Stepped.Contains(knob) ? Math.Round(v, MidpointRounding.AwayFromZero) : v;
    }

    /// <summary>The 0 to 1 value for a value of the knob.</summary>
    public static double Normalized(int knob, double plain)
    {
        var k = Knob(knob);
        return Math.Clamp((plain - k.Min) / (k.Max - k.Min), 0, 1);
    }

    /// <summary>Whether two values of a knob are the same, within <see cref="Tolerance"/>.</summary>
    public static bool Same(int knob, double a, double b) => Math.Abs(Normalized(knob, a) - Normalized(knob, b)) <= Tolerance;

    /// <summary>A value of the knob as the probe shows it: "0.250", "-12.0 dB", "440.0 Hz", "3", "On", "Charlie", "50.0 %".</summary>
    public static string Text(int knob, double value)
    {
        var c = CultureInfo.InvariantCulture;
        return knob switch
        {
            1004 => Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", c),
            1005 or 1008 => value >= 0.5 ? "On" : "Off",
            1006 => Choices[(int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, Choices.Count - 1)],
            1002 => value.ToString("0.0", c) + " dB",
            1003 => value.ToString(value < 1000 ? "0.0" : "0", c) + " Hz",
            1007 => value.ToString("0.0", c) + " %",
            _ => value.ToString("0.000", c),
        };
    }

    /// <summary>A knob by its ID ("1002") or its name ("MO Gain", or just "gain"), ignoring case; null if the probe has none.</summary>
    public static int? Find(string idOrName)
    {
        var text = idOrName.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return MoProbe.Knobs.Any(k => k.Id == id) ? id : null;
        var key = Key(text);
        foreach (var k in MoProbe.Knobs)
            if (Key(k.Name) == key || Key(k.Name) == "mo" + key) return k.Id;
        return null;
    }

    /// <summary>
    /// A value for a knob, typed as a number in its own units ("-12", "-12 dB", "440Hz") or as the probe shows it ("On",
    /// "Charlie"); null if it isn't either.
    /// </summary>
    public static double? Parse(int knob, string text)
    {
        var t = text.Trim();
        if (knob is 1005 or 1008)
        {
            if (t.Equals("on", StringComparison.OrdinalIgnoreCase)) return 1;
            if (t.Equals("off", StringComparison.OrdinalIgnoreCase)) return 0;
        }
        if (knob == 1006)
            for (var i = 0; i < Choices.Count; i++)
                if (t.Equals(Choices[i], StringComparison.OrdinalIgnoreCase)) return i;
        foreach (var unit in new[] { "dB", "Hz", "%" })
            if (t.EndsWith(unit, StringComparison.OrdinalIgnoreCase)) t = t[..^unit.Length].TrimEnd();
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    static string Key(string name) => string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
