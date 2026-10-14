using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Formats;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// MO Probe in Settings: whether the test plugin is installed, the probes running in a DAW now, and its newest reports.
/// It writes one each time a DAW uses it (see native/README.md), saying what the DAW told it and when it saved. While it
/// runs it also writes how it is now, and takes commands, so the card can make it beep, ping it and check its knobs.
/// </summary>
public sealed partial class ProbeReportsViewModel : ObservableObject
{
    const int Newest = 12;
    /// <summary>The timer ticks four times a second, so the first look comes straight after Settings opens; then it looks every fourth tick.</summary>
    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
    const int LookEvery = 4;
    public const string NoneRunning = "None. Put MO Probe on a track in your DAW and press play, and it shows here while it runs.";

    readonly Func<IEnumerable<string>> _pluginFolders;
    IUiTimer? _timer;
    int _ticks;
    bool _looking, _looked;

    /// <param name="folder">Where the reports are; the probe's own folder unless given.</param>
    /// <param name="pluginFolders">Where to look for the probe itself; the usual plugin folders unless given.</param>
    /// <param name="connectBitwig">How the knob check connects to Bitwig: on Bitwig's usual port unless given (tests use a stand-in).</param>
    public ProbeReportsViewModel(string? folder = null, Func<IEnumerable<string>>? pluginFolders = null,
                                 Func<CancellationToken, Task<BitwigLink>>? connectBitwig = null)
    {
        Folder = folder ?? MoProbe.ReportFolder;
        _pluginFolders = pluginFolders ?? MoProbe.InstallFolders;
        ConnectBitwig = connectBitwig ?? (ct => BitwigLink.ConnectAsync(ct: ct));
    }

    public string Folder { get; }
    public ObservableCollection<ProbeReportRow> Reports { get; } = new();
    /// <summary>Whether the probe is installed, and what the list shows.</summary>
    [ObservableProperty] string _status = "";

    /// <summary>The probes running in a DAW now, in the order they started.</summary>
    public ObservableCollection<RunningProbeRow> Running { get; } = new();
    /// <summary>What to do to see one here, when none are running; empty while some are, or before the first look.</summary>
    [ObservableProperty] string _runningNote = "";

    /// <summary>How long a probe has to answer Beep and Ping. It looks for commands ten times a second.</summary>
    public TimeSpan AnswerWait { get; set; } = TimeSpan.FromSeconds(3);

    internal Func<CancellationToken, Task<BitwigLink>> ConnectBitwig { get; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var folders = _pluginFolders().ToList();
        var folder = Folder;
        IReadOnlyList<string> installed;
        IReadOnlyList<ProbeReport> reports;
        try { (installed, reports) = await Task.Run(() => (MoProbe.Installed(folders), ProbeReport.ReadAll(folder, Newest))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't read MO Probe's reports: {e.Message}";
            return;
        }
        Reports.Clear();
        foreach (var r in reports) Reports.Add(new ProbeReportRow(r));
        var where = installed.Count > 0 ? $"MO Probe is installed ({string.Join(", ", installed)}). " : "";
        Status = (installed.Count, reports.Count) switch
        {
            (0, 0) => "MO Probe isn't in your plugin folders. To try it, copy it in from a build (native/README.md says how), " +
                      "then put it on a track in your DAW.",
            (_, 0) => where + "Put it on a track in your DAW, and its reports show here.",
            _ => where + (reports.Count == 1 ? "Its report:" : "Its newest reports:"),
        };
    }

    /// <summary>Starts following the probes running now, about once a second. Settings calls it when it opens.</summary>
    public void StartLive()
    {
        _ticks = 0;
        _timer ??= Ui.Current.CreateTimer(Tick, () =>
        {
            if (_ticks++ % LookEvery == 0) _ = RefreshRunningAsync();
        });
        _timer.Start();
    }

    /// <summary>Stops following them, when another screen is chosen.</summary>
    public void StopLive() => _timer?.Stop();

    /// <summary>
    /// Looks at the probes running now, and shows them. A probe's row stays as it is from one look to the next, with what
    /// its buttons said, and only its lines change. Skipped while the last look is still going.
    /// </summary>
    public async Task RefreshRunningAsync()
    {
        if (_looking) return;
        _looking = true;
        try
        {
            var folder = Folder;
            var running = await Task.Run(() => MoProbeLive.Running(folder));
            var changed = false;
            for (var i = 0; i < running.Count; i++)
            {
                var probe = running[i];
                var at = IndexOf(probe.Instance);
                if (at < 0)
                {
                    Running.Insert(i, new RunningProbeRow(probe, this));
                    changed = true;
                    continue;
                }
                if (at != i) Running.Move(at, i);
                Running[i].Show(probe);
            }
            // The rest have stopped running, though one still checking its knobs stays until the check ends.
            for (var i = Running.Count - 1; i >= running.Count; i--)
            {
                if (Running[i].Checking) continue;
                Running.RemoveAt(i);
                changed = true;
            }
            RunningNote = Running.Count == 0 ? NoneRunning : "";
            // A probe starting or stopping starts or finishes a report.
            if (changed && _looked) _ = RefreshAsync();
            _looked = true;
        }
        finally { _looking = false; }
    }

    int IndexOf(string instance)
    {
        for (var i = 0; i < Running.Count; i++)
            if (Running[i].Instance == instance) return i;
        return -1;
    }

    /// <summary>Opens the folder the probe writes its reports to (making it, if the probe hasn't yet).</summary>
    [RelayCommand]
    void OpenFolder()
    {
        try { Directory.CreateDirectory(Folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        Shell.Open(Folder);
    }
}

/// <summary>One MO Probe report, as Settings lists it.</summary>
public sealed partial class ProbeReportRow(ProbeReport report)
{
    public string Title { get; } = $"MO Probe {(report.Kind == "effect" ? "Effect" : "Instrument")}" + (report.Finished ? "" : ", still open");
    public string When { get; } = report.Ended.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    /// <summary>E.g. "Bitwig Studio 6.1.3 · CLAP · on “Bass” · 2 saves · 31 notes".</summary>
    public string Summary { get; } = report.Summary;
    public string File { get; } = report.File;

    [RelayCommand]
    void Reveal() => Shell.Reveal(File);
}

/// <summary>
/// One MO Probe running in a DAW now, as Settings shows it: where it is, its knobs, and buttons to make it beep, ping it,
/// and (in Bitwig) check how its knobs come across.
/// </summary>
public sealed partial class RunningProbeRow : ObservableObject
{
    readonly ProbeReportsViewModel _owner;
    CancellationTokenSource? _check;

    public RunningProbeRow(MoProbeLive probe, ProbeReportsViewModel owner)
    {
        _owner = owner;
        Instance = probe.Instance;
        Probe = probe;
        Show(probe);
    }

    public string Instance { get; }
    /// <summary>Its status at the last look.</summary>
    public MoProbeLive Probe { get; private set; }

    /// <summary>E.g. "MO Probe Instrument on “Inst 3”".</summary>
    [ObservableProperty] string _title = "";
    /// <summary>E.g. "Bitwig Studio 6.1.3 · CLAP · playing, bar 5 · 110 BPM".</summary>
    [ObservableProperty] string _details = "";
    /// <summary>E.g. "Knob A 0.250 · Gain 0.0 dB · Frequency 440.0 Hz · …".</summary>
    [ObservableProperty] string _knobs = "";
    [ObservableProperty] string _tip = "";
    /// <summary>Whether its DAW is Bitwig, so the knob check can go through Bitwig's script.</summary>
    [ObservableProperty] bool _canCheckKnobs;
    /// <summary>What Beep or Ping got back.</summary>
    [ObservableProperty] string _reply = "";
    /// <summary>How the knob check is going, or what it found.</summary>
    [ObservableProperty] string _checkResult = "";
    [ObservableProperty] bool _checking;

    internal void Show(MoProbeLive probe)
    {
        Probe = probe;
        Title = probe.Title;
        Details = DetailsOf(probe);
        Knobs = string.Join(" · ", probe.Knobs.OrderBy(k => k.Key).Select(k => $"{KnobName(k.Key)} {ProbeKnobs.Text(k.Key, k.Value)}"));
        Tip = $"Instance {probe.Instance}" + (probe.Report.Length > 0 ? $", reporting to {probe.Report}" : "");
        CanCheckKnobs = probe.Host.Contains("Bitwig", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The DAW, the format, where the song is and its tempo.</summary>
    static string DetailsOf(MoProbeLive p)
    {
        var parts = new List<string> { p.Host.Length > 0 ? $"{p.Host} {p.HostVersion}".Trim() : "Unknown DAW" };
        if (p.Format.Length > 0) parts.Add(p.Format);
        if (!p.Active) parts.Add("switched off");
        else if (p.Playing is true) parts.Add(p.Bar is { } bar ? $"playing, bar {bar}" : "playing");
        else if (p.Playing is false) parts.Add(p.Bar is { } bar ? $"stopped at bar {bar}" : "stopped");
        if (p.Tempo is { } bpm) parts.Add($"{bpm.ToString("0.##", CultureInfo.InvariantCulture)} BPM");
        return string.Join(" · ", parts);
    }

    /// <summary>A knob's name without the "MO " every one starts with: "Gain".</summary>
    static string KnobName(int id)
    {
        var name = MoProbe.KnobName(id);
        return name.StartsWith("MO ", StringComparison.Ordinal) ? name[3..] : name;
    }

    /// <summary>Has it play a short tone on its track, to hear which one it's on.</summary>
    [RelayCommand]
    async Task Beep()
    {
        Reply = "Asking it to beep…";
        if (await SendAsync(ProbeCommand.Beep()) is not null) Reply = "Beeped: listen for a short tone on its track.";
    }

    /// <summary>Checks that it answers, and how quickly.</summary>
    [RelayCommand]
    async Task Ping()
    {
        Reply = "Pinging…";
        var took = Stopwatch.StartNew();
        if (await SendAsync(ProbeCommand.Ping()) is not null)
            Reply = $"Answered in {took.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms";
    }

    /// <summary>Sends it a command and waits for its answer; null, having said why in <see cref="Reply"/>, when there's none.</summary>
    async Task<ProbeLine?> SendAsync(JsonObject command)
    {
        var (probe, wait) = (Probe, _owner.AnswerWait);
        try
        {
            return await Task.Run(() => MoProbeLive.SendAsync(probe.Instance, command, probe.ReportFolder, wait));
        }
        catch (DawLinkException)
        {
            Reply = MoProbeLive.Read(probe.File) is { } now && !now.Gone(DateTimeOffset.UtcNow)
                ? $"No answer in {wait.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s, though it's still running."
                : "No answer: it isn't running now. Its DAW may have closed, or removed it from the track.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Reply = $"Couldn't send it: {e.Message}";
        }
        return null;
    }

    /// <summary>
    /// Turns each of its knobs through Bitwig and reads what the probe got, then has the probe turn them and reads Bitwig's
    /// values back (<see cref="ProbeKnobCheck"/>). The knobs are put back as they were afterwards.
    /// </summary>
    [RelayCommand]
    async Task CheckKnobs()
    {
        Checking = true;
        using var cancel = _check = new CancellationTokenSource();
        var ct = cancel.Token;
        var (done, off, finished) = (0, 0, false);
        var progress = new Progress<KnobCheckLine>(line =>
        {
            if (finished) return;   // a line that arrives after the result
            done++;
            if (!line.Matched) off++;
            CheckResult = $"Checking the knobs through Bitwig: {done} done so far" + (off > 0 ? $", {off} not matching" : "") +
                          ". Leave them alone until it's finished.";
        });
        CheckResult = "Finding it in Bitwig…";
        try
        {
            using var link = await _owner.ConnectBitwig(ct);
            var probe = Probe;
            CheckResult = Describe(await Task.Run(() => ProbeKnobCheck.BitwigAsync(link, probe, progress, ct), ct));
        }
        catch (OperationCanceledException) { CheckResult = "Stopped. Any knobs it had turned were set back as they were."; }
        catch (DawLinkException e) { CheckResult = e.Message; }
        catch (Exception e) { CheckResult = $"Couldn't check its knobs: {e.Message}"; }
        finally
        {
            finished = true;
            _check = null;
            Checking = false;
        }
    }

    /// <summary>Stops the knob check, which puts the knobs back first.</summary>
    [RelayCommand]
    void StopCheck() => _check?.Cancel();

    /// <summary>The check's summary, then each turn that didn't match, then anything else to know.</summary>
    static string Describe(KnobCheck check)
    {
        var lines = new List<string> { check.Summary };
        lines.AddRange(check.Lines.Where(l => !l.Matched).Select(l => $"{l.Mark} {l.Text}"));
        if (check.ChangesOff is { } off) lines.Add(off);
        lines.AddRange(check.Notes);
        return string.Join(Environment.NewLine, lines);
    }
}
