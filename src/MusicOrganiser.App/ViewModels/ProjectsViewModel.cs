using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.App.ViewModels;

/// <summary>The main library screen: filters, grouping, the list, and the selected project.</summary>
public sealed partial class ProjectsViewModel : ObservableObject
{
    public const string Any = "Any";
    readonly LibraryDb _db;
    readonly MainViewModel _main;

    public ObservableCollection<ProjectItem> Items { get; } = new();
    public ICollectionView View { get; }

    public static IReadOnlyList<string> StatusChoices => Statuses.All;
    public static IReadOnlyList<int> RatingChoices { get; } = [0, 1, 2, 3, 4, 5];
    public static IReadOnlyList<string> GroupChoices { get; } =
        ["No grouping", "DAW", "Status", "Folder", "Month modified", "Year modified", "Elektron box", "Rating"];

    public ObservableCollection<string> DawFilters { get; } = new();
    public ObservableCollection<string> StatusFilters { get; } = new();
    public ObservableCollection<string> TagFilters { get; } = new();
    public ObservableCollection<string> HardwareFilters { get; } = new();

    [ObservableProperty] string _searchText = "";
    [ObservableProperty] string _dawFilter = Any;
    [ObservableProperty] string _statusFilter = Any;
    [ObservableProperty] string _tagFilter = Any;
    [ObservableProperty] string _hardwareFilter = Any;
    [ObservableProperty] bool _onlyFavourites;
    [ObservableProperty] bool _onlyMissing;
    [ObservableProperty] bool _hideArchived = true;
    [ObservableProperty] string _groupBy = "No grouping";
    [ObservableProperty] string _countText = "";

    /// <summary>Set from the Plugins or Samples screens: only projects with these ids.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasIdFilter))] string _idFilterLabel = "";
    HashSet<long>? _idFilter;
    public bool HasIdFilter => _idFilter is not null;

    [ObservableProperty] ProjectItem? _selected;
    [ObservableProperty] ProjectDetailViewModel? _detail;

    public ProjectsViewModel(LibraryDb db, MainViewModel main)
    {
        _db = db;
        _main = main;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is ProjectItem p && Filter(p);
        View.SortDescriptions.Add(new SortDescription(nameof(ProjectItem.Modified), ListSortDirection.Descending));
    }

    public void Reload()
    {
        var selectedId = Selected?.Id;
        using (View.DeferRefresh())
        {
            Items.Clear();
            foreach (var p in _db.Projects()) Items.Add(new ProjectItem(_db, p));
        }

        Refill(DawFilters, Items.Select(i => i.Daw), DawFilter, v => DawFilter = v);
        Refill(StatusFilters, Statuses.All, StatusFilter, v => StatusFilter = v);
        Refill(TagFilters, _db.AllTags().Select(t => t.Tag), TagFilter, v => TagFilter = v);
        Refill(HardwareFilters, Items.SelectMany(i => i.Hardware), HardwareFilter, v => HardwareFilter = v);

        Selected = Items.FirstOrDefault(i => i.Id == selectedId);
        ApplyFilter();
    }

    static void Refill(ObservableCollection<string> target, IEnumerable<string> values, string current, Action<string> reset)
    {
        target.Clear();
        target.Add(Any);
        foreach (var v in values.Distinct(StringComparer.OrdinalIgnoreCase).Order()) target.Add(v);
        if (!target.Contains(current)) reset(Any);
    }

    bool Filter(ProjectItem p)
    {
        if (_idFilter is not null && !_idFilter.Contains(p.Id)) return false;
        if (DawFilter != Any && p.Daw != DawFilter) return false;
        if (StatusFilter != Any && p.Status != StatusFilter) return false;
        if (StatusFilter == Any && HideArchived && p.Status == "Archived") return false;
        if (TagFilter != Any && !p.Tags.Contains(TagFilter)) return false;
        if (HardwareFilter != Any && !p.Hardware.Contains(HardwareFilter)) return false;
        if (OnlyFavourites && !p.Favourite) return false;
        if (OnlyMissing && !p.HasMissing && !p.FileMissing) return false;
        return SearchText.Length == 0 || p.Matches(SearchText.Trim());
    }

    void ApplyFilter()
    {
        View.Refresh();
        var shown = View.Cast<object>().Count();
        CountText = shown == Items.Count ? $"{Items.Count} projects" : $"{shown} of {Items.Count} projects";
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnDawFilterChanged(string value) => ApplyFilter();
    partial void OnStatusFilterChanged(string value) => ApplyFilter();
    partial void OnTagFilterChanged(string value) => ApplyFilter();
    partial void OnHardwareFilterChanged(string value) => ApplyFilter();
    partial void OnOnlyFavouritesChanged(bool value) => ApplyFilter();
    partial void OnOnlyMissingChanged(bool value) => ApplyFilter();
    partial void OnHideArchivedChanged(bool value) => ApplyFilter();

    partial void OnGroupByChanged(string value)
    {
        View.GroupDescriptions.Clear();
        var prop = value switch
        {
            "DAW" => nameof(ProjectItem.Daw),
            "Status" => nameof(ProjectItem.Status),
            "Folder" => nameof(ProjectItem.FolderName),
            "Month modified" => nameof(ProjectItem.MonthGroup),
            "Year modified" => nameof(ProjectItem.Year),
            "Elektron box" => nameof(ProjectItem.HardwareText),
            "Rating" => nameof(ProjectItem.Rating),
            _ => null,
        };
        if (prop is not null) View.GroupDescriptions.Add(new PropertyGroupDescription(prop));
    }

    partial void OnSelectedChanged(ProjectItem? value) =>
        Detail = value is null ? null : new ProjectDetailViewModel(_db, value, _main);

    public void ShowOnly(IEnumerable<long> ids, string label)
    {
        _idFilter = ids.ToHashSet();
        IdFilterLabel = label;
        OnPropertyChanged(nameof(HasIdFilter));
        ApplyFilter();
        _main.ShowProjects();
    }

    [RelayCommand]
    void ClearFilters()
    {
        _idFilter = null;
        IdFilterLabel = "";
        OnPropertyChanged(nameof(HasIdFilter));
        SearchText = "";
        DawFilter = StatusFilter = TagFilter = HardwareFilter = Any;
        OnlyFavourites = OnlyMissing = false;
        ApplyFilter();
    }

    [RelayCommand]
    void OpenSelected() => Detail?.OpenInDawCommand.Execute(null);
}
