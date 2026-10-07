using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.App.ViewModels;

/// <summary>
/// Every plugin across your projects, how often it's used, whether it's installed, and
/// what would break if you uninstalled it. Also lists installed plugins nothing uses.
/// </summary>
public sealed partial class PluginsViewModel : ObservableObject
{
    public const string ShowUsed = "Used in projects";
    public const string ShowMissing = "Not installed on this PC";
    public const string ShowUnused = "Installed, but no project uses it";
    public static IReadOnlyList<string> ShowChoices { get; } = [ShowUsed, ShowMissing, ShowUnused];

    readonly LibraryDb _db;
    readonly MainViewModel _main;
    int _impactVersion;

    [ObservableProperty] IReadOnlyList<PluginUsage> _items = [];
    [ObservableProperty] ICollectionView? _view;
    [ObservableProperty] IReadOnlyList<UnusedPlugin> _unusedItems = [];
    [ObservableProperty] ICollectionView? _unusedView;
    [ObservableProperty] string _searchText = "";
    [ObservableProperty] string _show = ShowUsed;
    [ObservableProperty] PluginUsage? _selected;
    [ObservableProperty] string _summary = "";
    [ObservableProperty] UninstallImpact? _impact;
    [ObservableProperty] bool _isWorking;

    public bool ShowingUnused => Show == ShowUnused;
    public bool HasImpact => Impact is not null;
    public string Hint => ShowingUnused
        ? "Plugins in your plugin folders that no project in your library uses, or uses only in another format. Select one to see where it is."
        : "Select plugins (Ctrl or Shift for several) to see which projects would break if you uninstalled them. Double-click to list those projects.";

    public PluginsViewModel(LibraryDb db, MainViewModel main)
    {
        _db = db;
        _main = main;
        Reload();
    }

    public void Reload()
    {
        Items = _db.PluginUsage();
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is PluginUsage p && (Show != ShowMissing || !p.Installed) && Matches(p.Name, p.Vendor);
        UnusedItems = PluginImpact.Unused(_db);
        UnusedView = CollectionViewSource.GetDefaultView(UnusedItems);
        UnusedView.Filter = o => o is UnusedPlugin u && Matches(u.Name, u.Location);

        var missing = Items.Count(p => !p.Installed);
        var parts = new List<string> { $"{Items.Count} plugins used" };
        if (missing > 0) parts.Add($"{missing} not installed on this PC");
        var never = UnusedItems.Count(u => u.UsedFormats.Count == 0);
        if (never > 0) parts.Add($"{never} installed but never used");
        Summary = string.Join(", ", parts);
        Impact = null;
    }

    bool Matches(string name, string other) =>
        SearchText.Length == 0 || name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
        other.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

    partial void OnSearchTextChanged(string value)
    {
        View?.Refresh();
        UnusedView?.Refresh();
    }

    partial void OnShowChanged(string value)
    {
        View?.Refresh();
        Impact = null;
        OnPropertyChanged(nameof(ShowingUnused));
        OnPropertyChanged(nameof(Hint));
    }

    partial void OnImpactChanged(UninstallImpact? value) => OnPropertyChanged(nameof(HasImpact));

    /// <summary>The rows selected in either list (called by the view when the selection changes).</summary>
    public void SetSelection(IEnumerable<object> rows)
    {
        var plugins = rows.Select(r => r switch
        {
            PluginUsage p => (Name: p.Name, Vendor: p.Vendor),
            UnusedPlugin u => (Name: u.Name, Vendor: ""),
            _ => (Name: "", Vendor: ""),
        }).Where(p => p.Name.Length > 0).ToList();
        _ = UpdateImpact(plugins);
    }

    async Task UpdateImpact(IReadOnlyList<(string Name, string Vendor)> plugins)
    {
        var version = ++_impactVersion;
        if (plugins.Count == 0)
        {
            Impact = null;
            return;
        }
        IsWorking = true;
        try
        {
            var result = await Task.Run(() => PluginImpact.Of(_db, plugins));
            if (version == _impactVersion) Impact = result;
        }
        catch (Exception)   // the library changed under us (e.g. a scan); the next selection retries
        {
            if (version == _impactVersion) Impact = null;
        }
        finally
        {
            if (version == _impactVersion) IsWorking = false;
        }
    }

    [RelayCommand]
    void ShowProjects(PluginUsage? p)
    {
        if ((p ?? Selected) is { } plugin)
            _main.Projects.ShowOnly(_db.ProjectsUsingPlugin(plugin.Name), $"Projects using {plugin.Name}");
    }

    [RelayCommand]
    void ShowImpactProjects()
    {
        if (Impact is not { Projects.Count: > 0 } i) return;
        var label = i.PluginNames.Count == 1 ? i.PluginNames[0] : $"{i.PluginNames.Count} plugins";
        _main.Projects.ShowOnly(i.Projects.Select(p => p.Project.Id), $"Projects using {label}");
    }

    [RelayCommand]
    void OpenImpactProject(ImpactProject? p)
    {
        if (p is not null) _main.Projects.ShowOnly([p.Project.Id], p.Project.Name);
    }

    [RelayCommand]
    void RevealCopy(InstalledCopy? c)
    {
        if (c is not null) Shell.Reveal(c.File.Path);
    }

    [RelayCommand]
    void SearchOnline(PluginUsage? p)
    {
        if ((p ?? Selected) is { } plugin) Shell.OpenUrl(MatchFinder.PluginSearchUrl(plugin.Name, plugin.Vendor));
    }
}
