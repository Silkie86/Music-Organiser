using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Plugins;

namespace MusicOrganiser.ViewModels;

/// <summary>Every plugin across your projects, how often it's used, and whether it's installed.</summary>
public sealed partial class PluginsViewModel : ObservableObject
{
    readonly LibraryDb _db;
    readonly MainViewModel _main;

    [ObservableProperty] IReadOnlyList<PluginUsage> _items = [];
    [ObservableProperty] IItemsView? _view;
    [ObservableProperty] string _searchText = "";
    [ObservableProperty] bool _onlyMissing;
    [ObservableProperty] PluginUsage? _selected;
    [ObservableProperty] string _summary = "";

    /// <summary>The installed plugins the selected one matches, as the plugin scanner read them.</summary>
    [ObservableProperty] IReadOnlyList<InstalledPluginItem> _installed = [];
    [ObservableProperty] string _installedNote = "";
    /// <summary>The installed copy whose parameters are showing.</summary>
    [ObservableProperty] InstalledPluginItem? _shownCopy;
    /// <summary>What the plugin scanner has read so far, or why it can't.</summary>
    [ObservableProperty] string _scannerNote = "";
    /// <summary>Some installed plugins' parameters are still to read (Read all parameters).</summary>
    [ObservableProperty] bool _canReadAll;

    public PluginsViewModel(LibraryDb db, MainViewModel main)
    {
        _db = db;
        _main = main;
        Reload();
    }

    public void Reload()
    {
        Items = _db.PluginUsage();
        View = Ui.Current.CreateView(Items as System.Collections.IList ?? Items.ToList());
        View.Filter = o => o is PluginUsage p &&
                           (!OnlyMissing || !p.Installed) &&
                           (SearchText.Length == 0 || p.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                            p.Vendor.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        var missing = Items.Count(p => !p.Installed);
        Summary = $"{Items.Count} plugins used" + (missing > 0 ? $", {missing} not installed on this PC" : "");

        if (!_main.PluginScanner.Available)
            ScannerNote = "IDs, versions and parameters come from the plugin scanner, which isn't built on this computer yet. " +
                          (PluginScanner.BuildProblem() is { } problem
                              ? $"The last try to build it said: {problem} (the rest is in native/out/app/build.log)."
                              : OperatingSystem.IsWindows()
                                  ? "Building the app builds it once Visual Studio's \"Desktop development with C++\" workload is installed (see native/README.md)."
                                  : "Building the app builds it once CMake and a C++ compiler are installed (see native/README.md).");
        else
        {
            var (files, plugins, failed) = _db.PluginScanCounts();
            var (read, left) = _db.ParameterCounts();
            ScannerNote = files == 0 ? "The plugin scanner reads your plugin files after the next scan."
                : $"The plugin scanner has read {files} plugin files holding {plugins} plugins" +
                  (failed > 0 ? $"; {failed} couldn't be read." : ".") +
                  (read == 0 ? "" : left > 0 ? $" It has the parameters of {read}, with {left} to go." : $" It has the parameters of {read}.");
            CanReadAll = left > 0 && !_main.ParametersReadPending;
        }
        LoadInstalled();
    }

    /// <summary>Loads every installed plugin whose parameters haven't been read, to read them.</summary>
    [RelayCommand]
    Task ReadAllParameters()
    {
        CanReadAll = false;   // back when it ends, if any are left
        return _main.ReadAllParametersAsync();
    }

    partial void OnSearchTextChanged(string value) => View?.Refresh();
    partial void OnOnlyMissingChanged(bool value) => View?.Refresh();
    partial void OnSelectedChanged(PluginUsage? value) => LoadInstalled();

    void LoadInstalled()
    {
        if (Selected is not { } p)
        {
            Installed = [];
            InstalledNote = "";
            ShownCopy = null;
            return;
        }
        Installed = _db.ScannedPluginsNamed(p.Name).Select(s => new InstalledPluginItem(s, _db, _main.PluginScanner)).ToList();
        ShownCopy = Installed.FirstOrDefault(i => i.Plugin.Params is not null) ?? Installed.FirstOrDefault();
        InstalledNote = Installed.Count > 0 ? ""
            : !_main.PluginScanner.Available || _db.PluginScanCounts().Files == 0 ? "The plugin scanner hasn't read your plugins yet."
            : p.Installed ? "Installed, but the plugin scanner couldn't read it, or it's in a format it doesn't read yet (AU, AAX)."
            : "Not found in your plugin folders.";
    }

    [RelayCommand]
    void ShowProjects(PluginUsage? p)
    {
        if ((p ?? Selected) is { } plugin)
            _main.Projects.ShowOnly(_db.ProjectsUsingPlugin(plugin.Name), $"Projects using {plugin.Name}");
    }

    [RelayCommand]
    void SearchOnline(PluginUsage? p)
    {
        if ((p ?? Selected) is { } plugin) Shell.OpenUrl(MatchFinder.PluginSearchUrl(plugin.Name, plugin.Vendor));
    }
}

/// <summary>One installed plugin as the plugin scanner read it, with its parameters once they're read.</summary>
public sealed partial class InstalledPluginItem : ObservableObject
{
    readonly LibraryDb _db;
    readonly PluginScanner _scanner;

    public ScannedPlugin Plugin { get; private set; }

    [ObservableProperty] IReadOnlyList<ScannedParam> _parameters = [];
    [ObservableProperty] string _parametersNote = "";
    [ObservableProperty] bool _isReading;

    public InstalledPluginItem(ScannedPlugin plugin, LibraryDb db, PluginScanner scanner)
    {
        Plugin = plugin;
        _db = db;
        _scanner = scanner;
        ShowParameters();
    }

    public string Title => Plugin.Name;
    public string Format => Plugin.Format;

    /// <summary>"Instrument · FabFilter · 3.24".</summary>
    public string Details => string.Join(" · ", new[]
    {
        Plugin.Kind.Length > 0 ? char.ToUpperInvariant(Plugin.Kind[0]) + Plugin.Kind[1..] : "",
        Plugin.Vendor,
        Plugin.Version.Length > 0 ? $"version {Plugin.Version}" : "",
        Plugin.Shell.Length > 0 ? $"in {Plugin.Shell}" : "",
    }.Where(s => s.Length > 0));

    /// <summary>The ID DAW projects save it under.</summary>
    public string IdText => $"ID {Plugin.Id}";
    public string Location => Plugin.Path;
    public bool CanRead => Plugin.Params is null && !IsReading;
    public bool HasParameters => Parameters.Count > 0;

    void ShowParameters()
    {
        if (Plugin.Params is not { } all)
        {
            Parameters = [];
            ParametersNote = Plugin.Error.Length > 0 ? $"The scanner couldn't load it: {Plugin.Error}" : "";
            return;
        }
        Parameters = all.Where(p => !p.Hidden).ToList();
        var hidden = all.Count - Parameters.Count;
        ParametersNote = all.Count == 0 ? "It has no parameters."
            : $"{Parameters.Count} parameters, with the values a new copy starts with" + (hidden > 0 ? $" ({hidden} hidden ones left out)." : ".");
    }

    partial void OnIsReadingChanged(bool value) => OnPropertyChanged(nameof(CanRead));
    partial void OnParametersChanged(IReadOnlyList<ScannedParam> value) => OnPropertyChanged(nameof(HasParameters));

    /// <summary>Loads the plugin in the scanner to read its parameters (some plugins show a licence window).</summary>
    [RelayCommand]
    async Task ReadParameters()
    {
        if (IsReading) return;
        IsReading = true;
        ParametersNote = "Loading the plugin…";
        try
        {
            // Kept either way: its parameters, or why it couldn't be loaded.
            Plugin = await _scanner.ReadParametersAsync(Plugin);
            await Task.Run(() => _db.SaveScannedPlugin(Plugin));
            OnPropertyChanged(nameof(Details));
            ShowParameters();
        }
        finally
        {
            IsReading = false;
        }
    }
}
