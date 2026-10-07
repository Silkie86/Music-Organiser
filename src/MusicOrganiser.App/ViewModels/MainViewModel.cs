using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.Library;

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
    public ShareCheckViewModel ShareCheck { get; }
    public SettingsViewModel Settings { get; }

    public IReadOnlyList<NavItem> Nav { get; }

    [ObservableProperty] NavItem _selectedNav;
    [ObservableProperty] bool _isScanning;
    [ObservableProperty] string _scanStatus = "";
    [ObservableProperty] double _scanPercent;

    public MainViewModel(LibraryDb db)
    {
        _db = db;
        _scanner = new Scanner(db);
        Projects = new ProjectsViewModel(db, this);
        Plugins = new PluginsViewModel(db, this);
        Samples = new SamplesViewModel(db, this);
        ShareCheck = new ShareCheckViewModel(db);
        Settings = new SettingsViewModel(db, this);
        Nav =
        [
            new("Projects", "", Projects),
            new("Plugins", "", Plugins),
            new("Samples", "", Samples),
            new("Share Check", "", ShareCheck),
            new("Settings", "", Settings),
        ];
        _selectedNav = Nav[0];
        Projects.Reload();

        // First run: send people to Settings to add their project folders.
        if (_db.Roots().Count == 0) SelectedNav = Nav.First(n => n.Page == Settings);
        else _ = ScanAsync(false);
    }

    public void ShowProjects() => SelectedNav = Nav[0];

    /// <summary>Opens Share Check on a project, e.g. from the project's page.</summary>
    public async Task CheckSharing(string path)
    {
        SelectedNav = Nav.First(n => n.Page == ShareCheck);
        await ShareCheck.Open(path);
    }

    public void RefreshAll()
    {
        Projects.Reload();
        Plugins.Reload();
        Samples.Reload();
    }

    [RelayCommand]
    Task Rescan() => ScanAsync(false);

    [RelayCommand]
    Task FullRescan() => ScanAsync(true);

    [RelayCommand]
    void CancelScan() => _scanCts?.Cancel();

    public async Task ScanAsync(bool full, bool plugins = true, bool samples = false)
    {
        if (IsScanning) return;
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
                if (plugins)
                {
                    ((IProgress<ScanProgress>)progress).Report(new("Checking installed plugins", 0, 0, ""));
                    _scanner.ScanInstalledPlugins();
                }
                if (samples) _scanner.ScanSampleFolders(progress, ct);
                _scanner.ScanProjects(progress, full, ct);
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
    }
}

public sealed record NavItem(string Title, string Glyph, ObservableObject Page);
