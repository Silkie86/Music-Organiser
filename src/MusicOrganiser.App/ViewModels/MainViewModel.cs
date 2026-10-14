using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.App.ViewModels;

/// <summary>The window: which screen is showing, and background scans.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    readonly LibraryDb _db;
    readonly Scanner _scanner;
    CancellationTokenSource? _scanCts;

    public ProjectsViewModel Projects { get; }
    public PluginsViewModel Plugins { get; }
    public SamplesViewModel Samples { get; }
    public SoundsViewModel Sounds { get; }
    public SettingsViewModel Settings { get; }

    public IReadOnlyList<NavItem> Nav { get; }

    [ObservableProperty] NavItem _selectedNav;
    [ObservableProperty] bool _isScanning;
    [ObservableProperty] string _scanStatus = "";
    [ObservableProperty] double _scanPercent;

    public MainViewModel(LibraryDb db, SoundOrganiser sounds)
    {
        _db = db;
        _scanner = new Scanner(db);
        Projects = new ProjectsViewModel(db, this);
        Plugins = new PluginsViewModel(db, this);
        Samples = new SamplesViewModel(db, this);
        Settings = new SettingsViewModel(db, this);
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
    }

    public void ShowProjects() => SelectedNav = Nav.First(n => n.Page == Projects);

    public void RefreshAll()
    {
        Projects.Reload();
        Plugins.Reload();
        Samples.Reload();
        Sounds.Reload();
    }

    [RelayCommand]
    Task Rescan() => ScanAsync(false);

    [RelayCommand]
    Task FullRescan() => ScanAsync(true);

    [RelayCommand]
    void CancelScan()
    {
        _scanCts?.Cancel();
        DropQueuedScan();
    }

    // A scan asked for while another is running (say, adding a folder during the startup scan)
    // runs straight after it instead of being dropped.
    (bool Full, bool Plugins, bool Samples)? _queuedScan;
    TaskCompletionSource? _queuedScanDone;

    public Task ScanAsync(bool full, bool plugins = true, bool samples = false)
    {
        if (!IsScanning) return RunScanAsync(full, plugins, samples);
        var q = _queuedScan ?? default;
        _queuedScan = (q.Full || full, q.Plugins || plugins, q.Samples || samples);
        return (_queuedScanDone ??= new TaskCompletionSource()).Task;
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
        if (_queuedScan is not { } next) return;
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
        var progress = new Progress<ScanProgress>(p =>
        {
            ScanStatus = p.Total > 0 ? $"{p.Stage}: {p.Done} of {p.Total}" : p.Stage;
            ScanPercent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
        });
        try
        {
            await Task.Run(() =>
            {
                // Sample folders are indexed alongside the projects; the projects need the plugin list first.
                var sampling = samples ? Task.Run(() => _scanner.ScanSampleFolders(null, ct)) : Task.CompletedTask;
                if (plugins)
                {
                    ((IProgress<ScanProgress>)progress).Report(new("Checking installed plugins", 0, 0, ""));
                    _scanner.ScanInstalledPlugins();
                }
                _scanner.ScanProjects(progress, full, ct);
                sampling.GetAwaiter().GetResult();
            }, ct);
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
