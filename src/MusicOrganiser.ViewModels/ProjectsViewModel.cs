using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.ViewModels;

/// <summary>The main library screen: filters, grouping, the list, and the selected project.</summary>
public sealed partial class ProjectsViewModel : ObservableObject, IFileDragSource
{
    public const string Any = "Any";
    readonly LibraryDb _db;
    readonly MainViewModel _main;

    public BulkCollection<ProjectItem> Items { get; } = new();
    public IItemsView View { get; }

    public static IReadOnlyList<string> StatusChoices => Statuses.All;
    public static IReadOnlyList<int> RatingChoices { get; } = [0, 1, 2, 3, 4, 5];
    public static IReadOnlyList<string> GroupChoices { get; } =
        ["When made", "No grouping", "DAW", "Status", "Folder", "Month modified", "Year modified", "Elektron box", "Rating"];

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
    [ObservableProperty] bool _groupSaves = true;
    [ObservableProperty] string _groupBy = "When made";
    [ObservableProperty] string _countText = "";

    /// <summary>Set from the Plugins or Samples screens: only projects with these ids.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasIdFilter))] string _idFilterLabel = "";
    HashSet<long>? _idFilter;
    public bool HasIdFilter => _idFilter is not null;

    [ObservableProperty] ProjectItem? _selected;
    [ObservableProperty] ProjectDetailViewModel? _detail;

    /// <summary>Setting: how much of the page's width the selected song's panel takes, 0 to 1.</summary>
    public const string PanelShareKey = "projects_panel_share";

    /// <summary>
    /// How much of the width the song's panel takes (the rest is the list): two thirds at first, then where you leave
    /// the divider. Kept between a fifth and nine tenths so neither side disappears.
    /// </summary>
    public double PanelShare
    {
        get => double.TryParse(_db.GetSetting(PanelShareKey), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var share) ? Math.Clamp(share, 0.2, 0.9) : 2 / 3.0;
        set => _db.SetSetting(PanelShareKey, Math.Clamp(value, 0.2, 0.9).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    public ProjectsViewModel(LibraryDb db, MainViewModel main)
    {
        _db = db;
        _main = main;
        View = Ui.Current.CreateView(Items);
        View.Filter = o => o is ProjectItem p && Filter(p);
        View.SortBy(nameof(ProjectItem.Made), ListSortDirection.Descending);
        OnGroupByChanged(GroupBy);
    }

    public void Reload() => Show(Load(GroupSaves));

    /// <summary>Reload with the database work done in the background, so the window keeps answering.</summary>
    public Task ReloadAsync() => ReloadAsync(null);

    /// <param name="select">Pick the row holding this file afterwards (when the one picked now is going away).</param>
    public async Task ReloadAsync(string? select)
    {
        var group = GroupSaves;
        Show(await UiWork.Run(() => Load(group)), select);
    }

    /// <summary>The rows, read from the database and grouped into songs (on any thread).</summary>
    (int Saves, List<ProjectItem> Items, List<string> Tags) Load(bool group)
    {
        var projects = _db.Projects();
        var items = group
            ? SongVersions.Group(projects).Select(g => new ProjectItem(_db, g.Latest, g.Song, g.Older)).ToList()
            : projects.Select(p => new ProjectItem(_db, p)).ToList();
        return (projects.Count, items, _db.AllTags().Select(t => t.Tag).ToList());
    }

    void Show((int Saves, List<ProjectItem> Items, List<string> Tags) loaded, string? select = null)
    {
        var selectedId = Selected?.Id;
        _saveCount = loaded.Saves;
        Items.ReplaceAll(loaded.Items);

        Refill(DawFilters, Items.Select(i => i.Daw), DawFilter, v => DawFilter = v);
        Refill(StatusFilters, Statuses.All, StatusFilter, v => StatusFilter = v);
        Refill(TagFilters, loaded.Tags, TagFilter, v => TagFilter = v);
        Refill(HardwareFilters, Items.SelectMany(i => i.Hardware), HardwareFilter, v => HardwareFilter = v);

        Selected = select is not null
            ? Items.FirstOrDefault(i => i.Path == select || i.OlderSaves.Any(o => o.Path == select))
            : Items.FirstOrDefault(i => i.Id == selectedId || i.OlderSaves.Any(o => o.Id == selectedId));
        ApplyFilter();
        _main.Live.MarkProjects();
    }

    static void Refill(ObservableCollection<string> target, IEnumerable<string> values, string current, Action<string> reset)
    {
        target.Clear();   // the ComboBox clears its selection and writes that back, so always restore it
        target.Add(Any);
        foreach (var v in values.Distinct(StringComparer.OrdinalIgnoreCase).Order()) target.Add(v);
        reset(current is not null && target.Contains(current) ? current : Any);
    }

    bool Filter(ProjectItem p)
    {
        if (_idFilter is not null && !_idFilter.Contains(p.Id) && !p.OlderSaves.Any(o => _idFilter.Contains(o.Id))) return false;
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
        var shown = View.Count;
        var noun = GroupSaves ? "song" : "project";
        CountText = (shown == Items.Count ? Count(Items.Count, noun) : $"{shown:N0} of {Count(Items.Count, noun)}") +
                    (GroupSaves && _saveCount > Items.Count ? $" ({_saveCount:N0} saves)" : "");
    }

    int _saveCount;

    static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";

    partial void OnGroupSavesChanged(bool value) => Reload();

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
        var prop = value switch
        {
            "When made" => nameof(ProjectItem.WhenGroup),
            "DAW" => nameof(ProjectItem.Daw),
            "Status" => nameof(ProjectItem.Status),
            "Folder" => nameof(ProjectItem.FolderName),
            "Month modified" => nameof(ProjectItem.MonthGroup),
            "Year modified" => nameof(ProjectItem.Year),
            "Elektron box" => nameof(ProjectItem.HardwareText),
            "Rating" => nameof(ProjectItem.Rating),
            _ => null,
        };
        View.GroupBy(prop);
    }

    partial void OnSelectedChanged(ProjectItem? value)
    {
        Detail = value is null ? null : new ProjectDetailViewModel(_db, value, _main);
        _main.Live.ShowOn(Detail);
    }

    /// <summary>Shows a project (or the song it's a save of) in the list and opens its page, clearing filters that hide it.</summary>
    public void Reveal(long id)
    {
        _main.ShowProjects();
        var item = Items.FirstOrDefault(i => i.Id == id || i.OlderSaves.Any(o => o.Id == id));
        if (item is null) return;
        if (!Filter(item)) ClearFilters();
        Selected = item;
    }

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
    void OpenSelected() => Detail?.OpenMainCommand.Execute(null);

    /// <summary>The project files themselves, e.g. into Explorer or a DAW that imports them.</summary>
    public DragPayload? MakeDrag(IReadOnlyList<object> items)
    {
        var projects = items.OfType<ProjectItem>().Where(p => System.IO.File.Exists(p.Path)).ToList();
        if (projects.Count == 0) return null;
        return new DragPayload(projects.Select(p => p.Path).ToList(),
            projects.Select(p => new DragCard(p.Name, string.Join(" · ", new[] { p.Daw, p.TempoText.Length > 0 ? $"{p.TempoText} BPM" : "" }.Where(x => x.Length > 0)), Glyphs.Project)).ToList(),
            projects.Count == 1 ? "1 project" : $"{projects.Count} projects");
    }
}
