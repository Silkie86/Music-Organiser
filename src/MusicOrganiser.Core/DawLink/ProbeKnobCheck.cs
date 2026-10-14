using System.Diagnostics;
using System.Globalization;
using MusicOrganiser.Core.Formats;

namespace MusicOrganiser.Core.DawLink;

/// <summary>MO Probe's device in a DAW, as the knob check needs it: the probe's knobs as the DAW lists them, and turning one.</summary>
public interface IProbeDevice
{
    /// <summary>The DAW's name, for what the check says: "Bitwig".</summary>
    string Daw { get; }

    /// <summary>Why the app can't turn knobs in the DAW, in words to show, or null when it can.</summary>
    string? CantSet { get; }

    /// <summary>The probe's knobs as the DAW lists them: its IDs and names for them, their values from 0 to 1, and what it shows.</summary>
    Task<IReadOnlyList<BitwigParam>> KnobsAsync(CancellationToken ct = default);

    /// <summary>Turns one of the probe's knobs in the DAW, by the DAW's ID for it, to a value from 0 to 1.</summary>
    Task SetAsync(string id, double value, CancellationToken ct = default);
}

/// <summary>
/// MO Probe on a track in Bitwig, through the Music Organiser script: found by the name of the track Bitwig told the
/// probe it's on, and selected to get at its knobs. It's selected again whenever something else has been.
/// </summary>
public sealed class BitwigProbeDevice : IProbeDevice
{
    static readonly TimeSpan SelectionWait = TimeSpan.FromSeconds(4);
    readonly BitwigLink _link;
    readonly BitwigTrack _track;
    readonly BitwigDevice _device;

    BitwigProbeDevice(BitwigLink link, BitwigTrack track, BitwigDevice device) => (_link, _track, _device) = (link, track, device);

    public string Daw => "Bitwig";
    public string? CantSet => _link.Hello.Changes ? null : ChangesOff;

    /// <summary>Bitwig's <see cref="BitwigSend.ChangesOff"/>, worded for the check.</summary>
    public static string ChangesOff => BitwigSend.ChangesOff.Replace("send it again", "check again", StringComparison.Ordinal);

    /// <summary>Finds the probe in the project open in Bitwig (<see cref="Locate"/>) and selects it.</summary>
    /// <exception cref="DawLinkException">Bitwig isn't answering, or the probe can't be found or selected there.</exception>
    public static async Task<BitwigProbeDevice> FindAsync(BitwigLink link, MoProbeLive probe, CancellationToken ct = default)
    {
        await link.HelloAsync(ct);   // whether Bitwig lets the app make changes now
        var (track, device) = Locate(await link.ProjectAsync(ct), probe);
        var found = new BitwigProbeDevice(link, track, device);
        await found.SelectedAsync(ct);
        return found;
    }

    /// <summary>
    /// Where the probe is in Bitwig's project: the track with the name Bitwig told the probe, and the probe at the top of
    /// its chain.
    /// </summary>
    /// <exception cref="DawLinkException">Why it can't be told, in words to show.</exception>
    public static (BitwigTrack Track, BitwigDevice Device) Locate(BitwigProject project, MoProbeLive probe)
    {
        if (probe.Host.Length > 0 && !probe.Host.Contains("Bitwig", StringComparison.OrdinalIgnoreCase))
            throw new DawLinkException($"{probe.Title} is running in {probe.Host}, not Bitwig.");
        if (probe.Track.Length == 0)
            throw new DawLinkException($"{probe.Name} doesn't know which track it's on (Bitwig hasn't told it), so the app can't find it in Bitwig.");
        var named = project.Tracks.Where(t => t.Name == probe.Track).ToList();
        if (named.Count == 0)
            named = project.Tracks.Where(t => t.Name.Trim().Equals(probe.Track.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 0)
            throw new DawLinkException(
                $"Bitwig has no track called “{probe.Track}”, which is the name it gave {probe.Name}. The probe may be inside a " +
                "container, layer or drum pad, which Bitwig names on its own, or in another project: put it straight on a track " +
                "in the project open in Bitwig, then check again." +
                (project.TrackCount > project.Tracks.Count ? $" (Bitwig lists the app its first {project.Tracks.Count} tracks of {project.TrackCount}.)" : ""));
        if (named.Count > 1)
            throw new DawLinkException($"Bitwig has {named.Count} tracks called “{probe.Track}”, so the app can't tell which one {probe.Name} " +
                                       "is on. Give them different names in Bitwig, then check again.");
        var track = named[0];
        var devices = track.Devices.Where(d => d.Name.Equals(probe.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (devices.Count == 0)
            throw new DawLinkException($"{probe.Name} isn't at the top of “{track.Name}”'s chain in Bitwig (it may be inside a container, " +
                                       "layer or drum pad), so the app can't select it. Put it straight on the track, then check again.");
        if (devices.Count > 1)
            throw new DawLinkException($"“{track.Name}” has {devices.Count} of {probe.Name}, so the app can't tell which is this one. " +
                                       "Leave one on the track, then check again.");
        return (track, devices[0]);
    }

    public async Task<IReadOnlyList<BitwigParam>> KnobsAsync(CancellationToken ct = default) => (await SelectedAsync(ct)).Params;

    public async Task SetAsync(string id, double value, CancellationToken ct = default)
    {
        // Bitwig sets the selected device's knobs, so make sure that's still the probe.
        await SelectedAsync(ct);
        await _link.SetParameterAsync(id, value, ct);
    }

    bool IsProbe(BitwigSelection s) => s.Track == _track.Name && s.Device?.Name == _device.Name;

    /// <summary>The probe as Bitwig shows it selected, selecting it first when something else is.</summary>
    async Task<BitwigSelection> SelectedAsync(CancellationToken ct)
    {
        var now = await _link.SelectedDeviceAsync(ct);
        if (IsProbe(now) && now.Params.Count > 0) return now;
        if (!IsProbe(now)) await _link.SelectAsync(_track.Index, _device.Index, ct);
        // Bitwig moves its selection, and lists the device's knobs, a moment after being asked.
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < SelectionWait)
        {
            await Task.Delay(100, ct);
            now = await _link.SelectedDeviceAsync(ct);
            if (IsProbe(now) && now.Params.Count > 0) return now;
        }
        throw new DawLinkException(IsProbe(now) ? $"Bitwig didn't list {_device.Name}'s knobs."
                                                : $"Bitwig didn't select {_device.Name} on “{_track.Name}”.");
    }
}

/// <summary>Which way a knob was turned in the knob check.</summary>
public enum KnobWay
{
    /// <summary>Turned in the DAW, to a value from 0 to 1, and read from what the probe got.</summary>
    ToProbe,
    /// <summary>Turned by the probe, to a value in the knob's own units, and read from what the DAW shows.</summary>
    ToDaw,
}

/// <summary>One turn of one knob in the knob check, and what arrived.</summary>
/// <param name="Sent">A value from 0 to 1 sent through the DAW, or a value in the knob's own units sent through the probe.</param>
/// <param name="Expected">What should arrive: a value in the knob's own units at the probe, or a value from 0 to 1 in the DAW.</param>
/// <param name="Got">What did, or null when nothing could be read.</param>
/// <param name="Display">The value as the DAW showed it afterwards, if it said.</param>
/// <param name="Took">From sending it to it arriving, or to giving up.</param>
public sealed record KnobCheckLine(int Knob, KnobWay Way, double Sent, double Expected, double? Got, string? Display, TimeSpan Took,
                                   bool Matched, string Daw = "Bitwig")
{
    /// <summary>Why it didn't arrive, when that's known.</summary>
    public string? Note { get; init; }

    public string Name => MoProbe.KnobName(Knob);
    public string Mark => Matched ? "✓" : "✗";
    /// <summary>"Bitwig → probe" or "probe → Bitwig".</summary>
    public string Direction => Way == KnobWay.ToProbe ? $"{Daw} → probe" : $"probe → {Daw}";

    /// <summary>E.g. "MO Gain, Bitwig → probe: sent 0.250, expected -42.0 dB, got -42.0 dB (Bitwig shows “-42.0 dB”), 0.4 s".</summary>
    public string Text
    {
        get
        {
            var (sent, expected, got) = Way == KnobWay.ToProbe
                ? (Share(Sent), AtProbe(Expected), Got is { } g ? AtProbe(g) : "nothing")
                : (AtProbe(Sent), Share(Expected), Got is { } h ? Share(h) : "nothing");
            var shows = Display is { Length: > 0 } d ? $" ({Daw} shows “{d}”)" : "";
            return $"{Name}, {Direction}: sent {sent}, expected {expected}, got {got}{shows}, " +
                   $"{Took.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s" + (Note is null ? "" : $"; {Note}");
        }
    }

    static string Share(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>As the probe shows it; a stepped knob between steps as the number, so it can be seen.</summary>
    string AtProbe(double v) => ProbeKnobs.Stepped.Contains(Knob) && Math.Abs(v - Math.Round(v)) > 1e-6
        ? v.ToString("0.###", CultureInfo.InvariantCulture) : ProbeKnobs.Text(Knob, v);
}

/// <summary>What the knob check found.</summary>
/// <param name="Probe">Which probe: "MO Probe Instrument on “Bass”".</param>
/// <param name="Notes">Anything else to know: knobs the DAW doesn't list, playback, knobs that didn't go back.</param>
/// <param name="ChangesOff">Why the DAW didn't let the app turn its knobs (so only the probe's way was checked), or null.</param>
/// <param name="Stopped">Why the check stopped part way, or null if it finished.</param>
public sealed record KnobCheck(string Probe, IReadOnlyList<KnobCheckLine> Lines, IReadOnlyList<string> Notes,
                               string? ChangesOff = null, string? Stopped = null)
{
    public int Matched => Lines.Count(l => l.Matched);
    public bool AllMatched => Stopped is null && Lines.Count > 0 && Lines.All(l => l.Matched);

    /// <summary>E.g. "MO Probe Instrument on “Bass”: 61 of 64 checks matched. Not matching: MO Steps, Bitwig → probe (3)."</summary>
    public string Summary
    {
        get
        {
            if (Lines.Count == 0) return $"{Probe}: nothing was checked" + (Stopped is null ? "." : $": {Stopped}");
            var text = Matched == Lines.Count ? $"{Probe}: all {Checks.Count(Lines.Count, "check")} matched."
                : $"{Probe}: {Matched} of {Checks.Count(Lines.Count, "check")} matched. Not matching: " +
                  string.Join(", ", Lines.Where(l => !l.Matched).GroupBy(l => (l.Name, l.Direction))
                                         .Select(g => $"{g.Key.Name}, {g.Key.Direction}" + (g.Count() > 1 ? $" ({g.Count()})" : ""))) + ".";
            return Stopped is null ? text : $"{text} It stopped part way: {Stopped}";
        }
    }
}

/// <summary>
/// The knob check: with MO Probe on a track in a DAW, turns each of its knobs through the DAW (with values from 0 to 1, as
/// the app sends FL Studio's knobs into Bitwig) and reads what the probe got from its live status, then has the probe turn
/// them and reads the DAW's values back. That shows how the DAW maps the app's 0 to 1 values onto a plugin's real ones.
/// The knobs are put back as they were at the end, also when it stops part way.
/// </summary>
public static class ProbeKnobCheck
{
    /// <summary>How long each turn has to arrive: the probe writes its status twice a second.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);
    /// <summary>
    /// How long a turn has to land before what arrives counts. The DAW passes a knob it turned to the plugin with its next
    /// block of audio, and a knob the plugin turned back with its next block, so straight after a turn it still shows the
    /// value from before, which may be what this turn should give.
    /// </summary>
    public static readonly TimeSpan DefaultSettle = TimeSpan.FromMilliseconds(150);
    /// <summary>The check's <see cref="KnobCheck.Stopped"/> when it was cancelled part way.</summary>
    public const string YouStopped = "You stopped it.";
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    readonly record struct Timing(TimeSpan Wait, TimeSpan Settle);

    /// <summary>The values from 0 to 1 each knob is turned to: 0, ¼, ½ and 1, or each step of a stepped knob.</summary>
    public static IReadOnlyList<double> Values(int knob)
    {
        if (!ProbeKnobs.Stepped.Contains(knob)) return [0, 0.25, 0.5, 1];
        var k = MoProbe.Knobs.First(x => x.Id == knob);
        var steps = (int)Math.Round(k.Max - k.Min);
        return Enumerable.Range(0, steps + 1).Select(i => (double)i / steps).ToList();
    }

    /// <summary>
    /// The DAW's knobs for the probe's: by name (ignoring case, spaces and punctuation), else by the probe's ID in the DAW's
    /// ID for it (Bitwig's "CONTENTS/PID3e9" is knob 1001).
    /// </summary>
    public static IReadOnlyList<(int Knob, BitwigParam Param)> Pair(IReadOnlyList<BitwigParam> shown)
    {
        static string Key(string name) => string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        var pairs = new List<(int, BitwigParam)>();
        foreach (var k in MoProbe.Knobs)
        {
            var pid = $"PID{k.Id:x}";
            if ((shown.FirstOrDefault(p => Key(p.Name) == Key(k.Name)) ??
                 shown.FirstOrDefault(p => p.Id.Split('/')[^1].Equals(pid, StringComparison.OrdinalIgnoreCase))) is { } param)
                pairs.Add((k.Id, param));
        }
        return pairs;
    }

    /// <summary>Finds the probe in Bitwig (<see cref="BitwigProbeDevice.FindAsync"/>) and checks its knobs there.</summary>
    public static async Task<KnobCheck> BitwigAsync(BitwigLink link, MoProbeLive probe, IProgress<KnobCheckLine>? progress = null,
                                                    CancellationToken ct = default) =>
        await RunAsync(await BitwigProbeDevice.FindAsync(link, probe, ct), probe, progress, ct: ct);

    /// <summary>Checks the probe's knobs through <paramref name="daw"/>, giving each line to <paramref name="progress"/> as it's done.</summary>
    /// <param name="wait">How long each turn has to arrive (<see cref="DefaultWait"/> unless given).</param>
    /// <param name="settle">How long each turn has to land before what arrives counts (<see cref="DefaultSettle"/> unless given).</param>
    /// <exception cref="DawLinkException">The probe isn't running, or the DAW couldn't list its knobs.</exception>
    public static async Task<KnobCheck> RunAsync(IProbeDevice daw, MoProbeLive probe, IProgress<KnobCheckLine>? progress = null,
                                                 TimeSpan? wait = null, TimeSpan? settle = null, CancellationToken ct = default)
    {
        var timing = new Timing(wait ?? DefaultWait, settle ?? DefaultSettle);
        var title = probe.Name + (probe.Track.Length > 0 ? $" on “{probe.Track}”" : "");
        var start = await MoProbeLive.ReadRunningAsync(probe.File, probe.Instance, ct);
        var pairs = Pair(await daw.KnobsAsync(ct));
        var lines = new List<KnobCheckLine>();
        var notes = new List<string>();
        var unlisted = MoProbe.Knobs.Where(k => pairs.All(p => p.Knob != k.Id)).Select(k => k.Name).ToList();
        if (unlisted.Count > 0)
            notes.Add($"{daw.Daw} doesn't list {Checks.List(unlisted, quote: false)} among the probe's knobs, so " +
                      (unlisted.Count == 1 ? "it wasn't checked." : "they weren't checked."));
        if (start.Playing is true)
            notes.Add($"{daw.Daw} was playing, so a knob with automation may not have held what the check set. Stop playback for a clean check.");

        var cantSet = daw.CantSet;
        string? stopped = null;
        var touched = new List<(int Knob, BitwigParam Param, double Plain)>();
        void Add(KnobCheckLine line)
        {
            lines.Add(line);
            progress?.Report(line);
        }
        try
        {
            foreach (var (knob, param) in pairs)
            {
                ct.ThrowIfCancellationRequested();
                var was = start.Knobs.TryGetValue(knob, out var v) ? v : MoProbe.Knobs.First(k => k.Id == knob).Default;
                touched.Add((knob, param, was));
                try
                {
                    if (cantSet is null)
                        foreach (var n in Values(knob)) Add(await ToProbeAsync(daw, probe, knob, param, n, timing, ct));
                    foreach (var n in Values(knob)) Add(await ToDawAsync(daw, probe, knob, param, n, timing, ct));
                }
                finally
                {
                    // Back as it was, through the probe, which sets its own value exactly; checked at the end.
                    try { MoProbeLive.Send(probe.Instance, ProbeCommand.Set(knob, was), probe.ReportFolder); }
                    catch (Exception e) when (e is DawLinkException or IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception e) when (e is DawLinkException or IOException or UnauthorizedAccessException)
        {
            stopped = e.Message;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Returned rather than thrown, so any knob that didn't go back is still in the notes.
            stopped = YouStopped;
        }
        finally
        {
            // Even when cancelled: the knobs are put back first.
            notes.AddRange(await PutBackAsync(daw, probe, touched, cantSet is null, timing.Wait));
        }
        return new KnobCheck(title, lines, notes, cantSet, stopped);
    }

    static async Task<KnobCheckLine> ToProbeAsync(IProbeDevice daw, MoProbeLive probe, int knob, BitwigParam param, double value,
                                                  Timing timing, CancellationToken ct)
    {
        var expected = ProbeKnobs.Plain(knob, value);
        var took = Stopwatch.StartNew();
        await daw.SetAsync(param.Id, value, ct);
        // Only a status the probe wrote once the turn had landed counts.
        var landed = DateTimeOffset.UtcNow + timing.Settle;
        var (arrived, status) = await MoProbeLive.UntilAsync(probe.Instance,
            s => s.Time >= landed && s.Knobs.TryGetValue(knob, out var now) && ProbeKnobs.Same(knob, now, expected),
            probe.ReportFolder, timing.Settle + timing.Wait, ct);
        took.Stop();
        var display = (await daw.KnobsAsync(ct)).FirstOrDefault(p => p.Id == param.Id)?.Display;
        return new KnobCheckLine(knob, KnobWay.ToProbe, value, expected, status.Knobs.TryGetValue(knob, out var got) ? got : null,
                                 display, took.Elapsed, arrived, daw.Daw);
    }

    static async Task<KnobCheckLine> ToDawAsync(IProbeDevice daw, MoProbeLive probe, int knob, BitwigParam param, double value,
                                                Timing timing, CancellationToken ct)
    {
        var plain = ProbeKnobs.Plain(knob, value);
        var expected = ProbeKnobs.Normalized(knob, plain);
        var took = Stopwatch.StartNew();
        var answer = await MoProbeLive.SendAsync(probe.Instance, ProbeCommand.Set(knob, plain), probe.ReportFolder, timing.Wait, ct);
        if (answer.Event != "app-set")
            return new KnobCheckLine(knob, KnobWay.ToDaw, plain, expected, null, null, took.Elapsed, false, daw.Daw)
                   { Note = $"the probe said “{answer.Str("error")}”" };
        // The probe has turned it: only what the DAW shows once that has landed counts.
        await Task.Delay(timing.Settle, ct);
        var until = took.Elapsed + timing.Wait;
        BitwigParam? now;
        bool Arrived() => now?.Value is { } v && Math.Abs(v - expected) <= ProbeKnobs.Tolerance;
        while (true)
        {
            now = (await daw.KnobsAsync(ct)).FirstOrDefault(p => p.Id == param.Id);
            if (Arrived() || took.Elapsed >= until) break;
            await Task.Delay(Poll, ct);
        }
        took.Stop();
        return new KnobCheckLine(knob, KnobWay.ToDaw, plain, expected, now?.Value, now?.Display, took.Elapsed, Arrived(), daw.Daw);
    }

    /// <summary>
    /// Waits for the knobs to be back as they were (the probe was asked to set each), puts any that aren't back through
    /// the DAW where it can, and says which still aren't.
    /// </summary>
    static async Task<IReadOnlyList<string>> PutBackAsync(IProbeDevice daw, MoProbeLive probe, IReadOnlyList<(int Knob, BitwigParam Param, double Plain)> touched,
                                                          bool dawCanSet, TimeSpan wait)
    {
        if (touched.Count == 0) return [];
        List<(int Knob, BitwigParam Param, double Plain)> Away(MoProbeLive s) =>
            touched.Where(t => !(s.Knobs.TryGetValue(t.Knob, out var v) && ProbeKnobs.Same(t.Knob, v, t.Plain))).ToList();
        try
        {
            var (back, status) = await MoProbeLive.UntilAsync(probe.Instance, s => Away(s).Count == 0, probe.ReportFolder, wait);
            if (back) return [];
            if (dawCanSet)
            {
                foreach (var t in Away(status))
                    if (t.Param.Value is { } was)
                    {
                        try { await daw.SetAsync(t.Param.Id, was); }
                        catch (DawLinkException) { }
                    }
                (back, status) = await MoProbeLive.UntilAsync(probe.Instance, s => Away(s).Count == 0, probe.ReportFolder, wait);
                if (back) return [];
            }
            return [.. Away(status).Select(t => $"{MoProbe.KnobName(t.Knob)} didn't go back to {ProbeKnobs.Text(t.Knob, t.Plain)}: set it back by hand.")];
        }
        catch (DawLinkException e)
        {
            return [$"The knobs couldn't be checked afterwards: {e.Message}"];
        }
    }
}
