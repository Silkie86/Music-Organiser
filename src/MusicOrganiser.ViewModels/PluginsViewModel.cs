using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Library;

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
    }

    partial void OnSearchTextChanged(string value) => View?.Refresh();
    partial void OnOnlyMissingChanged(bool value) => View?.Refresh();

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
