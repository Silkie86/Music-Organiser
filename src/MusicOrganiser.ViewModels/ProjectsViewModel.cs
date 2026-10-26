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
    /// <summary>Biggest songs first (the Space chip), instead of by when they were made.</summary>
    [ObservableProperty] bool _biggestFirst;
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
        _askDoubts = db.GetSetting(AskDoubtsKey) != "0";
        _doubtsAcrossFolders = db.GetSetting(DoubtsAcrossFoldersKey) == "1";
        OnGroupByChanged(GroupBy);
    }

    public void Reload()
    {
        _loads++;
        Show(Load(GroupSaves, Asking));
    }

    // Counts reloads, so one that finishes after a newer one (a scan's, say) doesn't put older rows back.
    int _loads;

    /// <summary>Reload with the database work done in the background, so the window keeps answering.</summary>
    public Task ReloadAsync() => ReloadAsync(null);

    /// <param name="select">Pick the row holding this file afterwards (when the one picked now is going away).</param>
    public async Task ReloadAsync(string? select)
    {
        var group = GroupSaves;
        var asking = Asking;
        var load = ++_loads;
        var loaded = await UiWork.Run(() => Load(group, asking));
        if (load != _loads) return;   // a newer reload is on its way
        Show(loaded, select);
    }

    sealed record Loaded(int Saves, List<ProjectItem> Items, List<string> Tags, SongChoices Choices, IReadOnlyList<SongDoubt> Doubts,
                         Dictionary<long, string> OwnFolders, IReadOnlyDictionary<string, (long Bytes, DateTime Measured)> Spaces);

    /// <summary>The rows, read from the database and grouped into songs (on any thread).</summary>
    Loaded Load(bool group, (bool Ask, bool AcrossFolders) asking)
    {
        // A fixed copy waiting for Keep isn't a save yet: the song still opens its original, and Older saves leave it out.
        // The saves are read first: Fix marks its copy pending before reading it into the library, so a copy in this list
        // is always in the pending list read after it, even when a reload runs while Fix is saving.
        var projects = _db.Projects();
        var pending = _db.PendingFixes();
        if (pending.Count > 0) projects = projects.Where(p => !pending.Contains(p.Path)).ToList();
        var choices = _db.SongChoices();
        var songs = SongVersions.Group(projects, choices);
        var items = group
            ? songs.Select(g => new ProjectItem(_db, g.Latest, g.Song, g.Older) { SongKey = g.Key }).ToList()
            : projects.Select(p => new ProjectItem(_db, p) { SongKey = choices.KeyOf(p.Path, p.Folder, p.Daw, p.Name) }).ToList();
        var doubts = asking.Ask ? SongVersions.Doubts(songs, choices, asking.AcrossFolders) : [];
        var own = OwnFolders(items, _db.Roots());
        var spaces = _db.FolderSpaces();
        foreach (var i in items) i.SpaceBytes = SpaceOf(i, own, spaces);
        return new(projects.Count, items, _db.AllTags().Select(t => t.Tag).ToList(), choices, doubts, own, spaces);
    }

    void Show(Loaded loaded, string? select = null)
    {
        var selectedId = Selected?.Id;
        _saveCount = loaded.Saves;
        _ownFolders = loaded.OwnFolders;
        Choices = loaded.Choices;   // before the rows, so the song's panel sees what you changed
        // The song you have open keeps its row (and so its panel, with Fix's choices and Check's results) when none of
        // its saves changed: a DAW saving some other project reloads the list.
        if (Selected is { } open && loaded.Items.FindIndex(i => i.Id == open.Id) is >= 0 and var at && Same(open, loaded.Items[at]))
            loaded.Items[at] = open;
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
        Doubts = loaded.Doubts.Where(d => !_skipped.Contains(SongChoices.Pair(d.A.Key, d.B.Key))).ToList();
    }

    // ---- Which saves are one song: your say (Solomon, 19 Oct) --------------------------------------------------------

    public const string AskDoubtsKey = "ask_song_doubts", DoubtsAcrossFoldersKey = "song_doubts_across_folders";

    /// <summary>Setting: ask when two songs' names are close enough that they may be saves of one (on unless turned off).</summary>
    [ObservableProperty] bool _askDoubts;
    /// <summary>Setting: also ask about songs with the same name in different folders (off: different folders are different songs).</summary>
    [ObservableProperty] bool _doubtsAcrossFolders;

    (bool, bool) Asking => (AskDoubts, DoubtsAcrossFolders);

    partial void OnAskDoubtsChanged(bool value)
    {
        _db.SetSetting(AskDoubtsKey, value ? "1" : "0");
        _ = ReloadAsync();
    }

    partial void OnDoubtsAcrossFoldersChanged(bool value)
    {
        _db.SetSetting(DoubtsAcrossFoldersKey, value ? "1" : "0");
        _ = ReloadAsync();
    }

    /// <summary>Your changes to the grouping, as last read (see <see cref="SongChoices"/>).</summary>
    public SongChoices Choices { get; private set; } = SongChoices.None;

    /// <summary>Songs that may be saves of one, waiting for you to say; the first is the one asked about.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Doubt), nameof(HasDoubts), nameof(ShowDoubtsBanner), nameof(DoubtsText), nameof(DoubtA), nameof(DoubtB), nameof(KeepAText), nameof(KeepBText),
                              nameof(QuestionText), nameof(DifferentText))]
    IReadOnlyList<SongDoubt> _doubts = [];
    /// <summary>Whether the question card is open (the banner's Check button).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDoubtsBanner))]
    bool _checkingDoubts;
    public bool ShowDoubtsBanner => HasDoubts && !CheckingDoubts;
    readonly HashSet<string> _skipped = [];

    public SongDoubt? Doubt => Doubts.Count > 0 ? Doubts[0] : null;
    public bool HasDoubts => Doubts.Count > 0;
    public string DoubtsText => Doubts.Count == 1 ? "Two songs may be saves of one song." : $"{Doubts.Count} pairs of songs may be saves of one song.";
    public string DoubtA => Doubt is { } d ? Describe(d.A) : "";
    public string DoubtB => Doubt is { } d ? Describe(d.B) : "";
    public string QuestionText => Doubt is { OneSave: true } d ? $"Is {d.B.Song} a save of {d.A.Song}?" : "Are these saves of one song?";
    public string KeepAText => Doubt is { } d ? d.OneSave ? $"Yes, a save of {d.A.Song}" : $"One song, called {d.A.Song}" : "";
    public string KeepBText => Doubt is { OneSave: false } d ? $"One song, called {d.B.Song}" : "";
    public string DifferentText => Doubt is { OneSave: true } ? "No, its own song" : "Different songs";

    static string Describe(SongGroup g) =>
        $"{g.Song}: {(g.SaveCount == 1 ? "1 save" : $"{g.SaveCount} saves")}, newest {g.Latest.Name} on {g.Latest.Modified:d MMM yyyy}, " +
        $"in {System.IO.Path.GetFileName(g.Latest.Folder.TrimEnd('\\', '/'))}";

    /// <summary>The two are one song, under the name of <c>"A"</c> or <c>"B"</c>.</summary>
    [RelayCommand]
    async Task SameSong(string keep)
    {
        if (Doubt is not { } d) return;
        if (d.OneSave)
        {
            _db.KeepApart(d.A.Key, d.B.Key);   // answered: it stays where it is, and isn't asked about again
            Next(d);
            return;
        }
        var (into, from) = keep == "B" ? (d.B, d.A) : (d.A, d.B);
        _db.JoinSong(from.Key, into.Key);
        await ReloadAsync(select: Selected is { } open && (open.SongKey == from.Key || open.SongKey == into.Key) ? into.Latest.Path : null);
        CheckingDoubts = HasDoubts && CheckingDoubts;
    }

    /// <summary>They're different songs: don't ask again. A marked save is taken out into a song of its own.</summary>
    [RelayCommand]
    async Task DifferentSongs()
    {
        if (Doubt is not { } d) return;
        if (d.OneSave)
        {
            _db.SplitSave(d.B.Latest.Path);
            await ReloadAsync(select: Selected is { } open && open.SongKey == d.A.Key ? open.Path : null);
            CheckingDoubts = HasDoubts && CheckingDoubts;
            return;
        }
        _db.KeepApart(d.A.Key, d.B.Key);
        Next(d);
    }

    /// <summary>Ask about this one another time (after the app restarts).</summary>
    [RelayCommand]
    void SkipDoubt()
    {
        if (Doubt is { } d) Next(d);
    }

    void Next(SongDoubt answered)
    {
        _skipped.Add(SongChoices.Pair(answered.A.Key, answered.B.Key));
        Doubts = Doubts.Skip(1).ToList();
        if (!HasDoubts) CheckingDoubts = false;
    }

    [RelayCommand]
    void CheckDoubts() => CheckingDoubts = HasDoubts;

    [RelayCommand]
    void CloseDoubts() => CheckingDoubts = false;

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

    // ---- Space: how much room each song takes ------------------------------------------------------------------------

    // Each song's folder, by its row's id, when the song has the folder to itself (see OwnFolders).
    Dictionary<long, string> _ownFolders = [];

    /// <summary>
    /// The folders songs have to themselves: none of your project folders (or one holding them), and every save the
    /// library knows in it, or in a folder inside it, is the same song's. Bitwig and Studio One give each song its own;
    /// FL Studio songs often share.
    /// </summary>
    internal static Dictionary<long, string> OwnFolders(IReadOnlyList<ProjectItem> items, IReadOnlyList<string> roots)
    {
        var songsIn = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in items)
            foreach (var s in i.OlderSaves.Prepend(i.Summary))
                (songsIn.TryGetValue(s.Folder, out var set) ? set : songsIn[s.Folder] = []).Add(i.Id);
        // The songs with saves in folders inside each folder that holds saves itself.
        var songsBelow = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, songs) in songsIn)
            for (var up = Path.GetDirectoryName(folder); !string.IsNullOrEmpty(up); up = Path.GetDirectoryName(up))
                if (songsIn.ContainsKey(up))
                    (songsBelow.TryGetValue(up, out var set) ? set : songsBelow[up] = []).UnionWith(songs);
        var own = new Dictionary<long, string>();
        foreach (var i in items)
        {
            var folder = i.Folder;
            if (folder.Length > 0 && songsIn[folder].Count == 1 && !roots.Any(r => Core.Space.DiskSpace.IsInside(r, folder)) &&
                (!songsBelow.TryGetValue(folder, out var below) || below.All(id => id == i.Id)))
                own[i.Id] = folder;
        }
        return own;
    }

    static long SpaceOf(ProjectItem i, Dictionary<long, string> own, IReadOnlyDictionary<string, (long Bytes, DateTime Measured)> spaces) =>
        own.TryGetValue(i.Id, out var folder) && spaces.TryGetValue(folder, out var s) && s.Bytes > 0 ? s.Bytes : i.SavesBytes;

    /// <summary>The songs' own folders and their newest saves, for measuring in the background.</summary>
    public IReadOnlyList<(string Folder, DateTime NewestSave)> OwnFolderList() =>
        Items.Where(i => _ownFolders.ContainsKey(i.Id)).Select(i => (_ownFolders[i.Id], i.Modified)).ToList();

    /// <summary>Shows newly measured sizes without reading the whole list again.</summary>
    public void ApplySpace(IReadOnlyDictionary<string, (long Bytes, DateTime Measured)> spaces)
    {
        foreach (var i in Items) i.SpaceBytes = SpaceOf(i, _ownFolders, spaces);
        if (BiggestFirst) View.Refresh();
    }

    partial void OnBiggestFirstChanged(bool value)
    {
        if (value)
        {
            View.GroupBy(null);
            View.SortBy(nameof(ProjectItem.SpaceBytes), ListSortDirection.Descending);
        }
        else
        {
            View.SortBy(nameof(ProjectItem.Made), ListSortDirection.Descending);
            OnGroupByChanged(GroupBy);
        }
    }

    partial void OnGroupByChanged(string value)
    {
        if (BiggestFirst) return;   // sizes aren't grouped
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

    /// <summary>The same song with the same saves, each as it was read.</summary>
    static bool Same(ProjectItem a, ProjectItem b)
    {
        static IEnumerable<(long, string, long, long, bool, int, int)> Saves(ProjectItem i) =>
            i.OlderSaves.Prepend(i.Summary).Select(p => (p.Id, p.Path, p.Mtime, p.Size, p.FileMissing, p.MissingSamples, p.MissingPlugins));
        return a.Name == b.Name && a.SongKey == b.SongKey && Saves(a).SequenceEqual(Saves(b));
    }

    partial void OnSelectedChanged(ProjectItem? value)
    {
        Detail?.LeftSongMidi(closed: true);   // its small playground mustn't keep playing with nothing to stop it
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
