using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Plugins;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.ViewModels;

/// <summary>The window: which screen is showing, and background scans.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    readonly LibraryDb _db;
    readonly Scanner _scanner;
    CancellationTokenSource? _scanCts;

    /// <summary>The native plugin scanner (mo-scan), if it was built.</summary>
    public PluginScanner PluginScanner { get; } = new();

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

    partial void OnSelectedNavChanged(NavItem value)
    {
        if (value.Page == Settings) _ = Settings.RefreshLinksAsync();
    }

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
    }
}

public sealed record NavItem(string Title, string Glyph, ObservableObject Page);
