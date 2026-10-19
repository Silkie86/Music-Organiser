using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.LiveTests;
using MusicOrganiser.Core.Plugins;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.ViewModels;

/// <summary>The window: which screen is showing, background scans, and the DAWs open now.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    readonly LibraryDb _db;
    readonly Scanner _scanner;
    CancellationTokenSource? _scanCts;

    /// <summary>The native plugin scanner (mo-scan), if it was built.</summary>
    public PluginScanner PluginScanner { get; }

    /// <summary>The app's data folder (tests use one of their own), where it also keeps its Bitwig template.</summary>
    public string DataFolder { get; }

    public ProjectsViewModel Projects { get; }
    public PluginsViewModel Plugins { get; }
    public SamplesViewModel Samples { get; }
    public SoundsViewModel Sounds { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>Bitwig and FL Studio as they are now, through their links (the sidebar's "In your DAWs").</summary>
    public LiveDawsViewModel Live { get; }

    public IReadOnlyList<NavItem> Nav { get; }

    [ObservableProperty] NavItem _selectedNav;
    [ObservableProperty] bool _isScanning;
    [ObservableProperty] string _scanStatus = "";
    [ObservableProperty] double _scanPercent;

    /// <param name="watch">How the DAWs are reached: their usual links unless given (tests use stand-ins).</param>
    /// <param name="pluginScanner">The plugin scanner: mo-scan where the build put it unless given (tests use a stand-in).</param>
    /// <param name="dataFolder">
    /// Where Settings finds MO Probe's reports and puts the live tests' reports: the app's data folder unless given (tests
    /// use a folder of their own).
    /// </param>
    public MainViewModel(LibraryDb db, SoundOrganiser sounds, DawWatch? watch = null, PluginScanner? pluginScanner = null,
                         string? dataFolder = null)
    {
        _db = db;
        DataFolder = dataFolder ?? LiveReports.DataFolder;
        PluginScanner = pluginScanner ?? new();
        _scanner = new Scanner(db);
        Live = new LiveDawsViewModel(db, this, watch ?? new DawWatch());
        Projects = new ProjectsViewModel(db, this);
        Plugins = new PluginsViewModel(db, this);
        Samples = new SamplesViewModel(db, this);
        Settings = new SettingsViewModel(db, this, dataFolder);
        Sounds = new SoundsViewModel(db, sounds, this);
        Nav =
        [
            new("Projects", "", Projects),
            new("Sound Lab", "\uE8D6", Sounds),
            new("Plugins", "", Plugins),
            new("Samples", "", Samples),
            new("Settings", "", Settings),
        ];
        _selectedNav = Nav[0];
        Projects.Reload();

        // First run: send people to Settings to add their project folders.
        if (_db.Roots().Count == 0 && _db.GetList(Scanner.SampleFoldersKey).Count == 0) SelectedNav = Nav[^1];
        else _ = ScanAsync(false);
        Live.Start();
    }

    public void ShowProjects() => SelectedNav = Nav.First(n => n.Page == Projects);

    public void ShowSettings() => SelectedNav = Nav.First(n => n.Page == Settings);

    /// <summary>
    /// The window is in front again, perhaps after the template was changed in Bitwig: Settings, if it's showing,
    /// looks at the template again.
    /// </summary>
    public void WindowActivated()
    {
        if (SelectedNav?.Page == Settings) Settings.RefreshTemplate();
    }

    partial void OnSelectedNavChanged(NavItem value)
    {
        // The MO Probes running now are followed only while Settings shows them.
        if (value.Page != Settings)
        {
            Settings.Probe.StopLive();
            return;
        }
        Settings.RefreshTemplate();
        _ = Settings.RefreshLinksAsync();
        _ = Settings.SoundOutput.RefreshAsync();
        _ = Settings.Probe.RefreshAsync();
        Settings.Probe.StartLive();
    }

    public void RefreshAll()
    {
        Projects.Reload();
        Plugins.Reload();
        Samples.Reload();
        Sounds.Reload();
        Live.Match();   // the library may have the projects the DAWs have open now
    }

    [RelayCommand]
    Task Rescan() => ScanAsync(false);

    [RelayCommand]
    Task FullRescan() => ScanAsync(true);

    [RelayCommand]
    void CancelScan()
    {
        _parametersQueued = false;
        _scanCts?.Cancel();
        DropQueuedScan();
    }

    /// <summary>
    /// Stops scanning as the app closes, and ends the plugin scanner's processes there and then: a plugin still
    /// loading, or waiting on its licence window, would otherwise outlive the app.
    /// </summary>
    public void Shutdown()
    {
        CancelScan();
        PluginScanner.StopAll();
    }

    bool _readingParameters, _parametersQueued;

    /// <summary>Reading every plugin's parameters is what's running now.</summary>
    internal bool IsReadingParameters => _readingParameters;

    /// <summary>Reading every plugin's parameters is running, or will once the scan running now ends.</summary>
    internal bool ParametersReadPending => _readingParameters || _parametersQueued;

    /// <summary>
    /// Reads the parameters of every installed plugin that doesn't have them yet (the Plugins screen), with
    /// the scan's progress and Stop button. Asked for during a scan, it starts when the scan ends; a scan asked
    /// for while it runs goes first, and it carries on after.
    /// </summary>
    public Task ReadAllParametersAsync()
    {
        if (ParametersReadPending || !PluginScanner.Available) return Task.CompletedTask;
        if (!IsScanning) return RunParametersAsync();
        _parametersQueued = true;
        return Task.CompletedTask;
    }

    async Task RunParametersAsync()
    {
        _readingParameters = IsScanning = true;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        var progress = ProgressInStatus();
        try
        {
            var (read, failed) = await Task.Run(() => _scanner.ReadAllParametersAsync(PluginScanner, progress, ct), ct);
            ScanStatus = $"Parameters read for {read} plugin{(read == 1 ? "" : "s")}" +
                         (failed > 0 ? $"; {failed} couldn't be loaded" : "") + $" · {DateTime.Now:HH:mm}";
        }
        catch (OperationCanceledException)
        {
            ScanStatus = _parametersQueued ? "Reading parameters after this scan"   // making way for one
                : "Stopped reading parameters; it carries on from there next time";
        }
        catch (Exception e)
        {
            ScanStatus = $"Reading parameters failed: {e.Message}";
        }
        finally
        {
            _readingParameters = IsScanning = false;
            ScanPercent = 0;
            Plugins.Reload();
        }
        await RunQueuedScanAsync();
    }

    Progress<ScanProgress> ProgressInStatus() => new(p =>
    {
        ScanStatus = p.Total > 0 ? $"{p.Stage}: {p.Done} of {p.Total}" : p.Stage;
        ScanPercent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
    });

    // A scan asked for while another is running (say, adding a folder during the startup scan)
    // runs straight after it instead of being dropped.
    (bool Full, bool Plugins, bool Samples)? _queuedScan;
    TaskCompletionSource? _queuedScanDone;

    public Task ScanAsync(bool full, bool plugins = true, bool samples = false)
    {
        if (!IsScanning) return RunScanAsync(full, plugins, samples);
        var q = _queuedScan ?? default;
        _queuedScan = (q.Full || full, q.Plugins || plugins, q.Samples || samples);
        var done = (_queuedScanDone ??= new TaskCompletionSource()).Task;
        if (_readingParameters && !_parametersQueued && _scanCts is { IsCancellationRequested: false })
        {
            // Reading every plugin's parameters can take an hour: it makes way for the scan (the plugins it was
            // loading are read again) and carries on after it. (Not when Stop has just stopped it.)
            _parametersQueued = true;
            _scanCts.Cancel();
        }
        return done;
    }

    void DropQueuedScan()
    {
        var done = _queuedScanDone;
        _queuedScan = null;
        _queuedScanDone = null;
        done?.TrySetResult();
    }

    async Task RunQueuedScanAsync()
    {
        if (_queuedScan is not { } next)
        {
            // Reading every plugin's parameters, asked for during the scan that just ended or paused for it. It runs
            // on its own, so whoever waited for the scan isn't kept waiting for it too.
            if (_parametersQueued)
            {
                _parametersQueued = false;
                if (_db.ParameterCounts().Left > 0) _ = RunParametersAsync();
            }
            return;
        }
        var done = _queuedScanDone;
        _queuedScan = null;
        _queuedScanDone = null;
        try { await RunScanAsync(next.Full, next.Plugins, next.Samples); }
        finally { done?.TrySetResult(); }
    }

    async Task RunScanAsync(bool full, bool plugins, bool samples)
    {
        IsScanning = true;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        var progress = ProgressInStatus();
        try
        {
            IReadOnlyList<string> pluginFiles = [];
            await Task.Run(() =>
            {
                // Sample folders are indexed alongside the projects; the projects need the plugin list first.
                var sampling = samples ? Task.Run(() => _scanner.ScanSampleFolders(null, ct)) : Task.CompletedTask;
                if (plugins)
                {
                    ((IProgress<ScanProgress>)progress).Report(new("Checking installed plugins", 0, 0, ""));
                    _scanner.ScanInstalledPlugins(out pluginFiles);
                }
                _scanner.ScanProjects(progress, full, ct);
                sampling.GetAwaiter().GetResult();
            }, ct);

            // New or updated plugin files: read what's inside them with the native scanner. That can take
            // a minute the first time, so show the projects first.
            if (pluginFiles.Count > 0 && PluginScanner.Available)
            {
                RefreshAll();
                await Task.Run(async () =>
                {
                    await _scanner.ReadPluginFilesAsync(PluginScanner, pluginFiles, progress, ct);
                    _scanner.ScanInstalledPlugins();   // now knowing every plugin inside them
                }, ct);
            }
            ScanStatus = $"Library up to date · {DateTime.Now:HH:mm}";
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "Scan stopped";
        }
        catch (Exception e)
        {
            ScanStatus = $"Scan failed: {e.Message}";
        }
        finally
        {
            IsScanning = false;
            ScanPercent = 0;
            RefreshAll();
        }
        await RunQueuedScanAsync();
    }
}

public sealed record NavItem(string Title, string Glyph, ObservableObject Page);
