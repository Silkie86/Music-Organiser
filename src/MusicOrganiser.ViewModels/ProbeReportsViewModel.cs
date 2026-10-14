using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Formats;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// MO Probe in Settings: whether the test plugin is installed, and its newest reports. It writes one each time a
/// DAW uses it (see native/README.md), saying what the DAW told it and when it saved.
/// </summary>
public sealed partial class ProbeReportsViewModel : ObservableObject
{
    const int Newest = 12;
    readonly Func<IEnumerable<string>> _pluginFolders;

    /// <param name="folder">Where the reports are; the probe's own folder unless given.</param>
    /// <param name="pluginFolders">Where to look for the probe itself; the usual plugin folders unless given.</param>
    public ProbeReportsViewModel(string? folder = null, Func<IEnumerable<string>>? pluginFolders = null)
    {
        Folder = folder ?? MoProbe.ReportFolder;
        _pluginFolders = pluginFolders ?? MoProbe.InstallFolders;
    }

    public string Folder { get; }
    public ObservableCollection<ProbeReportRow> Reports { get; } = new();
    /// <summary>Whether the probe is installed, and what the list shows.</summary>
    [ObservableProperty] string _status = "";

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
