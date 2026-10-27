using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// The Sound Lab: your sample library analysed and learned. Browse and search,
/// rediscover sounds you don't use, build random kits, and teach it your labels.
/// </summary>
public sealed partial class SoundsViewModel : ObservableObject, IFileDragSource
{
    readonly LibraryDb _db;
    readonly MainViewModel _main;
    readonly IAudioPlayer _player = Ui.Current.CreateAudioPlayer();
    readonly IUiTimer _clock;
    CancellationTokenSource? _cts;

    public SoundOrganiser Org { get; }
    public RediscoverViewModel Rediscover { get; }
    public KitViewModel Kit { get; }
    /// <summary>The Duplicates tab: copies of the same sound across your sample folders.</summary>
    public DuplicatesViewModel Duplicates { get; }
    /// <summary>The app's shortcut list, whose keys the sounds' right-click menus show.</summary>
    public ShortcutsViewModel Shortcuts => _main.Shortcuts;

    public static IReadOnlyList<string> AllTypes { get; } = [.. SoundTypes.All, SoundTypes.Unknown];
    public static IReadOnlyList<string> KeyChoices { get; } = ["Any key", .. Enumerable.Range(0, 12).Select(t => MusicTheory.KeyName(t, false, true)), .. Enumerable.Range(0, 12).Select(t => MusicTheory.KeyName(t, true, true))];

    [ObservableProperty] IReadOnlyList<Sound> _items = [];
    [ObservableProperty] IItemsView? _view;
    [ObservableProperty] string _summary = "";
    [ObservableProperty] bool _isBusy;
    [ObservableProperty] string _busyText = "";
    /// <summary>The model download, shown next to its button on the Teach tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartDownload))]
    bool _isDownloading;
    public bool CanStartDownload => !IsDownloading;
    [ObservableProperty] string _downloadText = "";
    [ObservableProperty] double _downloadPercent;
    /// <summary>Before the first file's size arrives the bar just shows activity.</summary>
    [ObservableProperty] bool _downloadSizeUnknown;
    [ObservableProperty] string _modelMessage = "";
    [ObservableProperty] double _busyPercent;
    [ObservableProperty] string _message = "";

    // Filters
    [ObservableProperty] string _searchText = "";
    [ObservableProperty] bool _describeMode;
    [ObservableProperty] IReadOnlyList<TypeCount> _typeCounts = [];
    [ObservableProperty] TypeCount? _selectedType;
    [ObservableProperty] int _loopFilter;   // 0 all, 1 one-shots, 2 loops
    [ObservableProperty] string _keyFilter = "Any key";
    public const string AnyYear = "Any year";
    /// <summary>Only sounds used in a project last saved in this year ("2019"), to see what you used when.</summary>
    [ObservableProperty] string _yearFilter = AnyYear;
    /// <summary>"Any year", then each year a project using one of your sounds was saved in, newest first.</summary>
    [ObservableProperty] IReadOnlyList<string> _yearChoices = [AnyYear];
    [ObservableProperty] bool _compatibleKeys;
    [ObservableProperty] string _bpmFrom = "";
    [ObservableProperty] string _bpmTo = "";
    [ObservableProperty] bool _onlyUnused;
    [ObservableProperty] bool _onlyFavourites;
    [ObservableProperty] string _tagFilter = "";

    // Selected sound
    [ObservableProperty] Sound? _selected;
    [ObservableProperty] IReadOnlyList<SimilarSound> _similar = [];
    [ObservableProperty] IReadOnlyList<TranscribedNote> _notes = [];
    [ObservableProperty] IReadOnlyList<ChordSpan> _noteChords = [];
    [ObservableProperty] string _notesMessage = "";
    [ObservableProperty] string _selectedTypeLabel = "";
    [ObservableProperty] string _newTag = "";
    [ObservableProperty] string _keyDetail = "";
    /// <summary>Where the selected sound is playing, in seconds, or -1 when it isn't.</summary>
    [ObservableProperty] double _playPosition = -1;
    /// <summary>Seconds the selected sound's waveform covers (the analysis reads the first minute).</summary>
    public double WaveSeconds => Selected is { } s ? Math.Min(s.Seconds, AudioFile.MaxSeconds) : 0;

    // Teaching & models
    [ObservableProperty] string _modelSummary = "";
    [ObservableProperty] string _clapStatus = "";
    [ObservableProperty] bool _clapInstalled;
    [ObservableProperty] IReadOnlyList<Disagreement> _disagreements = [];
    [ObservableProperty] IReadOnlyList<TagSummary> _tagSummaries = [];

    Dictionary<long, double>? _describeScores;
    // Counts describe searches (and what drops their result), so one that ends late doesn't put its matches back.
    int _describing;

    public const string AutoPlayKey = "sound_lab_autoplay";
    /// <summary>Tiles or List: how the sounds were shown last time.</summary>
    public const string ViewKey = "sounds_view";
    public const string Tiles = "Tiles", List = "List", History = "History";
    public static IReadOnlyList<string> ViewChoices { get; } = [Tiles, List, Galaxy, History];

    public const string Shuffled = "Shuffled", MostLiked = "Most liked";
    /// <summary>The orders on offer. The page opens shuffled, a new order every time, to stumble on forgotten sounds.</summary>
    public static IReadOnlyList<string> SortChoices { get; } = [Shuffled, MostLiked, "Name", "Type", "Key", "Newest"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTiles), nameof(IsList), nameof(IsHistory), nameof(IsGalaxy), nameof(ShowsOrder))]
    string _viewMode;
    public bool IsTiles => ViewMode == Tiles;
    public bool IsList => ViewMode == List;
    public bool IsHistory => ViewMode == History;

    [ObservableProperty] string _sortBy = Shuffled;

    /// <summary>
    /// The list column whose header you clicked ("Bpm", "Name"…), or null to follow <see cref="SortBy"/>. It orders the list
    /// only, here off the UI thread like every order: the grid sorting 357,000 sounds itself froze the window.
    /// </summary>
    public string? ColumnSort { get; private set; }
    public bool ColumnSortDescending { get; private set; }

    /// <summary>The tiles, a row at a time (so only the rows on screen are drawn), <see cref="TileColumns"/> to a row.</summary>
    [ObservableProperty] IReadOnlyList<TileRow> _tileRows = [];
    /// <summary>The sounds in tile order, as shown: for moving round the tiles with the arrow keys.</summary>
    public IReadOnlyList<Sound> Shown { get; private set; } = [];
    int _tileColumns = 4;
    public int TileColumns
    {
        get => _tileColumns;
        set
        {
            value = Math.Max(1, value);
            if (value == _tileColumns) return;
            _tileColumns = value;
            OnPropertyChanged();
            BuildTiles();
        }
    }

    /// <summary>Sounds you've played on purpose, most recent first.</summary>
    [ObservableProperty] IReadOnlyList<HistoryItem> _historyItems = [];
    [ObservableProperty] HistoryItem? _historySelected;
    public bool HasHistory => HistoryItems.Count > 0;

    Dictionary<long, int> _shuffle = [];
    LikedWeights _weights = LikedWeights.Default;

    /// <summary>Plays each sound as soon as you click it or arrow onto it in the list.</summary>
    [ObservableProperty] bool _autoPlay;

    partial void OnAutoPlayChanged(bool value) => _db.SetSetting(AutoPlayKey, value ? "1" : "0");

    public const string SimilarCountKey = "similar_count", SweepKey = "galaxy_sweep_ms";
    public static IReadOnlyList<int> SimilarCountChoices { get; } = [6, 12, 24, 48];
    public static IReadOnlyList<int> SweepChoices { get; } = [15, 30, 45, 60, 90, 150, 250];
    /// <summary>How many sounds "Sounds like this" shows (Settings › Sounds).</summary>
    [ObservableProperty] int _similarCount;
    /// <summary>How long each star plays when the mouse sweeps across the galaxy, in milliseconds (Settings › Sounds).</summary>
    [ObservableProperty] int _sweepMs;

    partial void OnSimilarCountChanged(int value)
    {
        _db.SetSetting(SimilarCountKey, value.ToString());
        FindSimilar(Selected);
    }

    partial void OnSweepMsChanged(int value) => _db.SetSetting(SweepKey, value.ToString());

    public const string ListenToNewKey = "deep_listen_new";
    /// <summary>
    /// With the sound-understanding model downloaded, CLAP listens to the sounds a scan or the folder watcher finds as
    /// they arrive (Solomon's 18 Oct note). Sounds it has already heard aren't listened to again. On unless turned off.
    /// </summary>
    [ObservableProperty] bool _listenToNew;

    partial void OnListenToNewChanged(bool value) => _db.SetSetting(ListenToNewKey, value ? "1" : "0");

    public const string UsedNeedsNotesKey = "used_in_needs_notes", UsedNotMutedKey = "used_in_not_muted_newest";
    /// <summary>
    /// Used in (and the year filter, Most liked and Never used, which go by it) counts a project only where the sound's
    /// channel has notes (Solomon's 18 Oct note). FL Studio projects so far; other DAWs' projects count as before.
    /// </summary>
    [ObservableProperty] bool _usedNeedsNotes;
    /// <summary>...and not in a song whose newest save has the sound's channel muted.</summary>
    [ObservableProperty] bool _usedNotMutedInNewest;

    partial void OnUsedNeedsNotesChanged(bool value)
    {
        _db.SetSetting(UsedNeedsNotesKey, value ? "1" : "0");
        _ = RefreshUsageAsync();
    }

    partial void OnUsedNotMutedInNewestChanged(bool value)
    {
        _db.SetSetting(UsedNotMutedKey, value ? "1" : "0");
        _ = RefreshUsageAsync();
    }

    UsageRules Rules => new(UsedNeedsNotes, UsedNotMutedInNewest);

    public SoundsViewModel(LibraryDb db, SoundOrganiser org, MainViewModel main)
    {
        _db = db;
        _main = main;
        Org = org;
        Rediscover = new RediscoverViewModel(this);
        Kit = new KitViewModel(this);
        Duplicates = new DuplicatesViewModel(this);
        _autoPlay = db.GetSetting(AutoPlayKey) == "1";
        _listenToNew = db.GetSetting(ListenToNewKey) != "0";
        _usedNeedsNotes = db.GetSetting(UsedNeedsNotesKey) == "1";
        _usedNotMutedInNewest = db.GetSetting(UsedNotMutedKey) == "1";
        _similarCount = int.TryParse(db.GetSetting(SimilarCountKey), out var similar) && similar > 0 ? similar : 12;
        _sweepMs = int.TryParse(db.GetSetting(SweepKey), out var sweep) && sweep > 0 ? sweep : 90;
        _stepMs = int.TryParse(db.GetSetting(StepKey), out var step) && step > 0 ? step : 250;
        _viewMode = db.GetSetting(ViewKey) is List or Galaxy ? db.GetSetting(ViewKey)! : Tiles;
        _placedBy = db.GetSetting(GalaxyBasisKey) is { } basis && PlacedByChoices.Contains(basis) ? basis : SoundOrganiser.Both;
        _colourBy = db.GetSetting(GalaxyColourKey) is { } c && ColourChoices.Contains(c) ? c : "Type";
        _weights = LikedWeights.FromJson(db.GetSetting(LikedWeightsKey));
        _clock = Ui.Current.CreateTimer(TimeSpan.FromMilliseconds(30), FollowPlayback);
        _ = StartAsync();
    }

    /// <summary>The first load, in the background (it held the window up while the app opened), then a scan for new sounds.</summary>
    async Task StartAsync()
    {
        await ReloadAsync();
        WatchSampleFolders();
        if (SampleFolders.Count > 0) await ScanAsync();
        else if (IsGalaxy) await MapGalaxyIfNeeded();
    }

    /// <summary>Rediscover fills itself the first time it's opened, so it never greets you empty.</summary>
    partial void OnTabIndexChanged(int value)
    {
        if (value == RediscoverTab && Rediscover.Suggestions.Count == 0 && !Rediscover.IsBusy && Org.Sounds.Count > 0)
            Rediscover.ShuffleCommand.Execute(null);
    }

    public const int RediscoverTab = 1;

    FolderWatch? _sampleWatch;
    readonly HashSet<string> _changedSounds = new(StringComparer.OrdinalIgnoreCase);
    bool _sampleFoldersOverflowed;   // the folders are to be walked: too much changed to name, or a folder was added

    /// <summary>
    /// How long the sample folders must be quiet before new sounds are listened to: a pack being unzipped or copied in
    /// arrives as thousands of changes, and the sounds are taken in together once it's done.
    /// </summary>
    public static TimeSpan SampleWatchQuiet { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// While the app is open, the sample folders are watched: sounds added, changed or deleted there are taken in a
    /// moment later, on their own, without walking 357,000 files again. Called again when Settings changes the folders.
    /// </summary>
    public void WatchSampleFolders()
    {
        _sampleWatch?.Dispose();
        _sampleWatch = new FolderWatch(SampleFolders, AudioFile.CanRead, SampleFilesChanged, SampleWatchQuiet);
    }

    public void StopWatching() => _sampleWatch?.Dispose();

    /// <summary>
    /// Settings added a sample folder: it's watched from now on, and the folders are walked for the sounds already in
    /// it (the watcher only sees what changes). While a job runs (a scan, Deep listen) the walk waits for it rather than
    /// being dropped.
    /// </summary>
    public void SampleFolderAdded()
    {
        WatchSampleFolders();
        _sampleFoldersOverflowed = true;   // walked as when too much changed to name
        if (!IsBusy) _ = TakeInChangedSoundsAsync();
    }

    internal void SampleFilesChanged(FolderWatch.Changes changes)
    {
        _changedSounds.UnionWith(changes.Files);
        _sampleFoldersOverflowed |= changes.Overflowed.Count > 0;
        if (!IsBusy) _ = TakeInChangedSoundsAsync();   // otherwise once the job running now is done (see RunAsync)
    }

    /// <summary>Takes in what the sample folders' watcher saw change, or walks the folders when too much changed to name.</summary>
    internal async Task TakeInChangedSoundsAsync()
    {
        // A job started meanwhile (RunAsync awaits a reload before calling this): leave the changes queued for it to
        // take in when it's done, as RunAsync and ScanAsync wouldn't run now.
        if (IsBusy) return;
        if (_sampleFoldersOverflowed)
        {
            _sampleFoldersOverflowed = false;
            _changedSounds.Clear();
            await ScanAsync();
            return;
        }
        if (_changedSounds.Count == 0) return;
        var paths = _changedSounds.ToList();
        _changedSounds.Clear();
        var folders = SampleFolders;
        var count = 0;
        List<long> arrived = [];
        await RunAsync("Taking in new sounds", (p, ct) =>
        {
            count = Org.Update(paths, folders, p, ct);
            if (_db.SampleIndexCount() > 0) _db.UpdateSampleIndex(paths);   // what Fix searches
            var seen = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (count > 0) arrived = Org.Sounds.Where(s => seen.Contains(s.Path)).Select(s => s.Id).ToList();
        },
                       () => ("Sample folders up to date", count == 0 ? "Nothing new" : $"{count:N0} new, changed or removed sounds"), always: false);
        AppLog.Info("Watch", $"Sample folders: {count} sounds changed ({paths.Count} changes seen)");
        if (count > 0) await ListenToArrivals(arrived);
        if (count > 0) await MapGalaxyIfNeeded();
    }

    /// <summary>
    /// <see cref="ListenToNew"/>: CLAP listens to the sounds that just arrived, skipping any it has heard, before the
    /// galaxy places them.
    /// </summary>
    async Task ListenToArrivals(IReadOnlyCollection<long> arrived)
    {
        if (!ListenToNew || arrived.Count == 0 || !ClapModel.IsInstalled()) return;
        var heard = 0;
        await RunAsync("Deep listening to new sounds", (p, ct) => heard = Org.DeepListen(p, ct, arrived),
                       () => ("Deep listened to new sounds", heard == 0 ? "It had heard them all already" : $"{heard:N0} new sounds"),
                       always: false);
    }

    IReadOnlyList<string> SampleFolders => _db.GetList(Scanner.SampleFoldersKey);

    /// <summary>Settings › Most liked: how much each sign of liking a sound counts.</summary>
    public const string LikedWeightsKey = "liked_weights";

    public void Reload() => Show(LoadAll(Org.Sounds));

    /// <summary>
    /// The same, with the database reads, scores and counts worked out off the UI thread: with 357,000 sounds they held
    /// the window up for seconds.
    /// </summary>
    public async Task ReloadAsync()
    {
        var version = ++_reloadVersion;
        var items = Org.Sounds;
        try
        {
            var loaded = await UiWork.Run(() => LoadAll(items));
            if (version == _reloadVersion) Show(loaded);   // else a newer reload is on its way
        }
        catch (Exception e) { Message = $"Couldn't reload the sounds: {e.Message}"; }
    }

    int _reloadVersion;

    bool _showing;

    sealed record Loaded(IReadOnlyList<Sound> Items, double[] Liked, IReadOnlyList<TypeCount> TypeCounts,
                         IReadOnlyList<HistoryItem> History, ModelInfo Model);

    /// <summary>Everything the page shows about the sounds, worked out on any thread. Changes nothing the window shows.</summary>
    Loaded LoadAll(IReadOnlyList<Sound> items)
    {
        Org.ApplyUsage(_db.SampleUsageByAge(DateTime.Now.AddYears(-1), Rules));
        var weights = _weights;
        var liked = items.Select(weights.Score).ToArray();
        IReadOnlyList<TypeCount> types =
            [new TypeCount("All sounds", items.Count), .. items.GroupBy(s => s.Type).OrderByDescending(g => g.Count()).Select(g => new TypeCount(g.Key, g.Count()))];
        return new(items, liked, types, LoadHistory(items), LoadModelInfo(items.Count));
    }

    void Show(Loaded loaded)
    {
        Items = loaded.Items;
        foreach (var s in Items) _shuffle.TryAdd(s.Id, Random.Shared.Next());
        var top = _topLiked = loaded.Liked.Length == 0 ? 0 : loaded.Liked.Max();
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].Liked = loaded.Liked[i];
            Items[i].LikedShare = top > 0 ? loaded.Liked[i] / top : 0;
        }
        HistoryItems = loaded.History;
        OnPropertyChanged(nameof(HasHistory));
        var type = SelectedType?.Type;
        TypeCounts = loaded.TypeCounts;
        _showing = true;   // the list is worked out once, just below
        try { SelectedType = TypeCounts.FirstOrDefault(t => t.Type == type) ?? TypeCounts[0]; }
        finally { _showing = false; }
        UpdateYears();
        UpdateList();
        BuildStars();
        UpdateSummary();
        ShowModelInfo(loaded.Model);
    }

    /// <summary>
    /// After a scan of projects: only how many projects use each sound can have changed, so only that (and the Most
    /// liked scores built on it) is worked out again, in the background. Reloading the whole page for 357,000 sounds
    /// held the window up for seconds.
    /// </summary>
    public async Task RefreshUsageAsync()
    {
        var items = Items;
        if (items.Count == 0 || !ReferenceEquals(items, Org.Sounds))
        {
            await ReloadAsync();   // nothing showing yet, or the sounds themselves changed
            return;
        }
        var since = DateTime.Now.AddYears(-1);
        var rules = Rules;
        var weights = _weights;
        var (changed, years, liked) = await Task.Run(() =>
        {
            var before = items.Select(s => (s.UsedIn, s.UsedInRecently, s.UsedInYears)).ToArray();
            Org.ApplyUsage(_db.SampleUsageByAge(since, rules));
            var changed = items.Where((s, i) => (s.UsedIn, s.UsedInRecently) != (before[i].UsedIn, before[i].UsedInRecently)).Any();
            // A project re-saved in a new year changes only the years its sounds are used in, which the year filter goes by.
            var years = changed || items.Where((s, i) => !s.UsedInYears.AsSpan().SequenceEqual(before[i].UsedInYears)).Any();
            return (changed, years, changed ? items.Select(weights.Score).ToArray() : []);
        });
        if (!years || !ReferenceEquals(items, Items)) return;
        var top = liked.Length == 0 ? _topLiked : _topLiked = liked.Max();
        for (var i = 0; i < liked.Length; i++)
        {
            items[i].Liked = liked[i];
            items[i].LikedShare = top > 0 ? liked[i] / top : 0;
        }
        UpdateSummary();
        UpdateYears();
        if (OnlyUnused || YearFilter != AnyYear) Refresh();
        else if (SortBy == MostLiked || IsList && ColumnSort is "UsedIn" or "Liked") ApplySort();
    }

    void UpdateSummary()
    {
        var loops = Items.Count(s => s.IsLoop);
        var unused = Items.Count(s => s.UsedIn == 0);
        Summary = Items.Count == 0
            ? "Add your sample folders in Settings, then scan them here."
            : $"{Items.Count:N0} sounds · {loops:N0} loops · {unused:N0} never used in a project";
    }

    void UpdateModelInfo() => ShowModelInfo(LoadModelInfo(Items.Count));

    sealed record ModelInfo(bool ClapInstalled, string ClapStatus, string ModelSummary,
                            IReadOnlyList<Disagreement> Disagreements, IReadOnlyList<TagSummary> TagSummaries);

    ModelInfo LoadModelInfo(int count)
    {
        var installed = ClapModel.IsInstalled();
        var withClap = Org.Library.CountWithClap();
        var everything = Org.Everything.Count;   // deep listening covers hidden copies too, so count them as well
        var clap = !installed
            ? "Not installed. Download it once (about 205 MB) to search by describing a sound, and for sharper sound-alikes and type guesses. It runs on this PC; nothing is uploaded."
            : count == 0
                ? $"Installed in {ClapModel.DefaultFolder}. Scan your sample folders, then Deep listen."
            : withClap >= everything && everything > 0
                ? $"Installed. Every sound has been deep-listened ({withClap:N0})."
                : $"Installed. {withClap:N0} of {everything:N0} sounds deep-listened so far; run Deep listen to do the rest.";
        var m = Org.TypeModel;
        var summary = m is null
            ? "Not trained yet: it needs at least 20 sounds whose folder or file names say what they are (or that you've labelled)."
            : $"Trained {m.Trained:g} on {m.Examples:N0} labelled sounds using {(m.Space == "clap" ? "deep-listening (CLAP) vectors" : "sound features")}, {m.Classes.Length} types" +
              (m.HeldOutAccuracy is { } acc ? $". Right {acc:P0} of the time on sounds it hadn't seen." : ".");
        return new(installed, clap, summary,
                   Org.Disagreements().Select(d => new Disagreement(d.Sound, d.Thinks, d.P)).ToList(),
                   Org.Library.TagCounts().Select(t => new TagSummary(t.Tag, t.Yours, t.Learned)).ToList());
    }

    void ShowModelInfo(ModelInfo info)
    {
        ClapInstalled = info.ClapInstalled;
        ClapStatus = info.ClapStatus;
        ModelSummary = info.ModelSummary;
        Disagreements = info.Disagreements;
        TagSummaries = info.TagSummaries;
    }

    // ---- filtering -----------------------------------------------------------------

    /// <summary>
    /// The filters as they are now, as a test for one sound. It keeps its own copy of every setting, so the list can be
    /// worked out on another thread while you go on changing them (a newer list then replaces it).
    /// </summary>
    Func<Sound, bool> MakeFilter()
    {
        var type = SelectedType is { Type: var t } && t != "All sounds" ? t : null;
        var loops = LoopFilter;
        var unused = OnlyUnused;
        int? year = int.TryParse(YearFilter, out var y) ? y : null;
        var favourites = OnlyFavourites;
        var tag = TagFilter;
        var key = KeyFilter != "Any key" ? MusicTheory.ParseKey(KeyFilter) : null;
        var compatible = key is { } k0 && CompatibleKeys ? MusicTheory.CompatibleKeys(k0.Tonic, k0.Minor).ToHashSet() : null;
        double? lo = double.TryParse(BpmFrom, out var l) ? l : null;
        double? hi = double.TryParse(BpmTo, out var h) ? h : null;
        var described = _describeScores;
        var words = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return s =>
        {
            if (type is not null && s.Type != type) return false;
            if (loops == 1 && s.IsLoop) return false;
            if (loops == 2 && !s.IsLoop) return false;
            if (unused && s.UsedIn > 0) return false;
            if (year is { } yr && Array.BinarySearch(s.UsedInYears, yr) < 0) return false;
            if (favourites && !s.Favourite) return false;
            if (tag.Length > 0 && !s.Tags.Any(x => x.Equals(tag, StringComparison.OrdinalIgnoreCase))) return false;
            if (key is { } k)
            {
                if (s.Analysis?.KeyTonic is not { } tonic) return false;
                var ok = compatible is not null
                    ? compatible.Contains((tonic, s.Analysis.KeyMinor))
                    : tonic == k.Tonic && s.Analysis.KeyMinor == k.Minor;
                if (!ok) return false;
            }
            if (lo is { } low && (s.Bpm ?? -1) < low) return false;
            if (hi is { } high && (s.Bpm is null || s.Bpm > high)) return false;
            if (described is not null) return described.ContainsKey(s.Id);
            return words.All(w =>
                s.Path.Contains(w, StringComparison.OrdinalIgnoreCase) || s.Type.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                s.Tags.Any(x => x.Contains(w, StringComparison.OrdinalIgnoreCase)) || s.Character.Contains(w, StringComparison.OrdinalIgnoreCase));
        };
    }

    /// <summary>The order chosen now, kept the same way as <see cref="MakeFilter"/>.</summary>
    Comparison<Sound> MakeOrder()
    {
        if (IsList && ColumnSort is { } column) return ColumnOrder(column, ColumnSortDescending);
        if (_describeScores is { } scores)
            return (a, b) => scores.GetValueOrDefault(b.Id).CompareTo(scores.GetValueOrDefault(a.Id));
        var sort = SortBy;
        var shuffle = _shuffle;
        return (a, b) =>
        {
            var c = sort switch
            {
                MostLiked => b.Liked.CompareTo(a.Liked),
                "Name" => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                "Type" => string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase),
                "Key" => KeyOrder(a).CompareTo(KeyOrder(b)),
                "Newest" => b.Added.CompareTo(a.Added),
                _ => shuffle.GetValueOrDefault(a.Id).CompareTo(shuffle.GetValueOrDefault(b.Id)),
            };
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        };
    }

    void ApplySort() => UpdateList();

    /// <summary>A click on a list column's header: sorts by that column, then the other way, then back to the Order box's order.</summary>
    public void SortByColumn(string column)
    {
        if (ColumnSort != column) (ColumnSort, ColumnSortDescending) = (column, false);
        else if (!ColumnSortDescending) ColumnSortDescending = true;
        else ColumnSort = null;
        OnPropertyChanged(nameof(ColumnSort));
        ApplySort();
    }

    /// <summary>Back to the Order box's order (or the best matches'), when one is picked.</summary>
    void ClearColumnSort()
    {
        if (ColumnSort is null) return;
        ColumnSort = null;
        OnPropertyChanged(nameof(ColumnSort));
    }

    /// <summary>A list column's order, smallest first unless <paramref name="descending"/>; sounds with nothing in it go last either way.</summary>
    static Comparison<Sound> ColumnOrder(string column, bool descending)
    {
        int Way(int c) => descending ? -c : c;
        int Text(string a, string b) =>
            a.Length == 0 || b.Length == 0 ? b.Length.CompareTo(a.Length) : Way(string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
        int Number<T>(T? a, T? b) where T : struct, IComparable<T> =>
            a is { } x ? b is { } y ? Way(x.CompareTo(y)) : -1 : b is null ? 0 : 1;
        Comparison<Sound> by = column switch
        {
            "Favourite" => (a, b) => Way(a.Favourite.CompareTo(b.Favourite)),
            "Liked" => (a, b) => Way(a.Liked.CompareTo(b.Liked)),
            "Type" => (a, b) => Text(a.Type, b.Type),
            "Key" => (a, b) => Number(KeyNumber(a), KeyNumber(b)),
            "Bpm" => (a, b) => Number(a.Bpm, b.Bpm),
            "RootNote" => (a, b) => Number(a.Analysis?.Pitch, b.Analysis?.Pitch),
            "Seconds" => (a, b) => Way(a.Seconds.CompareTo(b.Seconds)),
            "Character" => (a, b) => Text(a.Character, b.Character),
            "UsedIn" => (a, b) => Way(a.UsedIn.CompareTo(b.UsedIn)),
            "Folder" => (a, b) => Text(a.Folder, b.Folder),
            _ => (a, b) => Text(a.Name, b.Name),
        };
        return (a, b) =>
        {
            var c = by(a, b);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        };
    }

    int _listVersion;

    /// <summary>
    /// Works out the list (filtered and in order) and its tiles off the UI thread, then swaps the finished list in at
    /// once. Filtering and sorting 357,000 sounds on the UI thread froze the window, and the list went blank meanwhile.
    /// </summary>
    async void UpdateList()
    {
        var version = ++_listVersion;
        var items = Items;
        var keep = MakeFilter();
        var order = MakeOrder();
        var columns = TileColumns;
        var tiles = IsTiles;
        try
        {
            var (shown, map, rows) = await UiWork.Run(() =>
            {
                var list = items.Where(keep).ToList();
                list.Sort(order);
                if (!tiles) return (list, new Dictionary<long, SoundTile>(), (IReadOnlyList<TileRow>)[]);
                var map = list.ToDictionary(s => s.Id, s => new SoundTile(s));
                return (list, map, list.Chunk(columns).Select(c => new TileRow(c.Select(s => map[s.Id]).ToList())).ToList());
            });
            if (version != _listVersion) return;   // something changed meanwhile: a newer list is on its way
            Shown = shown;
            View = Ui.Current.CreateView(shown);
            _tiles = map;
            TileRows = rows;
            if (Selected is { } picked && _tiles.TryGetValue(picked.Id, out var tile)) tile.IsSelected = true;
        }
        catch (Exception e)
        {
            Message = $"Couldn't show the sounds: {e.Message}";
        }
    }

    // Keys in note order, major before minor; sounds with no key last.
    static int KeyOrder(Sound s) => KeyNumber(s) ?? int.MaxValue;
    static int? KeyNumber(Sound s) => s.Analysis?.KeyTonic is { } t ? t * 2 + (s.Analysis.KeyMinor ? 1 : 0) : null;

    partial void OnSortByChanged(string value)
    {
        ClearColumnSort();
        ApplySort();
    }

    /// <summary>A new random order (and switches to it).</summary>
    [RelayCommand]
    void Shuffle()
    {
        _shuffle = Items.ToDictionary(s => s.Id, _ => Random.Shared.Next());
        if (SortBy == Shuffled)
        {
            ClearColumnSort();
            ApplySort();
        }
        else SortBy = Shuffled;
    }

    partial void OnViewModeChanged(string value)
    {
        if (value != History) _db.SetSetting(ViewKey, value);
        if (value == Tiles) BuildTiles();
        if (value == List && ColumnSort is not null) UpdateList();   // the tiles left the header's order to the list
        if (value == History) LoadHistory();
        if (value == Galaxy)
        {
            BuildStars();
            _ = MapGalaxyIfNeeded();
        }
    }

    /// <summary>The filter or something it depends on changed: the list and the tiles follow.</summary>
    void Refresh()
    {
        UpdateList();
        BuildStars();
    }

    Dictionary<long, SoundTile> _tiles = [];

    void BuildTiles()
    {
        if (IsTiles) UpdateList();
    }

    /// <summary>The row of tiles holding a sound, to scroll it into view.</summary>
    public TileRow? RowOf(Sound s) => TileRows.FirstOrDefault(r => r.Tiles.Any(t => t.Sound == s));

    /// <summary>Works out every sound's Most liked score, and its bar as a share of the most liked one's.</summary>
    void Score()
    {
        foreach (var s in Items) s.Liked = _weights.Score(s);
        var top = _topLiked = Items.Count == 0 ? 0 : Items.Max(s => s.Liked);
        foreach (var s in Items) s.LikedShare = top > 0 ? s.Liked / top : 0;
    }

    double _topLiked;

    /// <summary>
    /// Scores again only the sounds just played, dragged or starred. Every sound's share only changes when one of
    /// them becomes, or stops being, the most liked; scoring all 450,000 on every double-click held the window up.
    /// </summary>
    void Rescore(IReadOnlyCollection<Sound> changed)
    {
        var wasTop = _topLiked > 0 && changed.Any(s => s.Liked >= _topLiked);
        foreach (var s in changed) s.Liked = _weights.Score(s);
        if (wasTop || changed.Any(s => s.Liked > _topLiked)) { Score(); return; }
        foreach (var s in changed) s.LikedShare = _topLiked > 0 ? s.Liked / _topLiked : 0;
    }

    /// <summary>Settings changed how Most liked weighs things.</summary>
    public void WeightsChanged(LikedWeights weights)
    {
        _weights = weights;
        Score();
        if (SortBy == MostLiked || IsList && ColumnSort == "Liked") ApplySort();
        else BuildTiles();
    }

    void LoadHistory()
    {
        HistoryItems = LoadHistory(Items);
        OnPropertyChanged(nameof(HasHistory));
    }

    IReadOnlyList<HistoryItem> LoadHistory(IReadOnlyList<Sound> items)
    {
        var history = Org.Library.History();
        if (history.Count == 0) return [];
        // Looked up one by one: a lookup of all 450,000 sounds made for a list of a few hundred held up every play.
        var byId = ReferenceEquals(items, Org.Sounds) ? null : items.ToDictionary(s => s.Id);
        return history.Select(h => (h, Sound: byId is null ? Org.Get(h.Id) : byId.GetValueOrDefault(h.Id)))
                      .Where(t => t.Sound is not null).Select(t => new HistoryItem(t.Sound!, t.h.At)).ToList();
    }

    partial void OnHistorySelectedChanged(HistoryItem? value)
    {
        if (value is not null) Selected = value.Sound;
    }

    /// <summary>Notes something done with sounds that says you like them (a drag out, a send into a DAW).</summary>
    internal void Record(IEnumerable<Sound> sounds, string kind)
    {
        var list = sounds.DistinctBy(s => s.Id).ToList();
        if (list.Count == 0) return;
        Org.Library.Record(list.Select(s => s.Id), kind);
        foreach (var s in list)
            s.Activity = kind == SoundEvent.Drag ? s.Activity with { Drags = s.Activity.Drags + 1 } : s.Activity with { Sends = s.Activity.Sends + 1 };
        Rescore(list);
    }

    partial void OnSearchTextChanged(string value)
    {
        if (DescribeMode) return;   // describe searches run on Enter
        Refresh();
    }

    partial void OnDescribeModeChanged(bool value)
    {
        _describing++;
        _describeScores = null;
        ApplySort();
        Refresh();
    }

    partial void OnSelectedTypeChanged(TypeCount? value)
    {
        if (!_showing) Refresh();
    }
    partial void OnLoopFilterChanged(int value) => Refresh();
    partial void OnKeyFilterChanged(string value) => Refresh();
    partial void OnCompatibleKeysChanged(bool value) => Refresh();
    partial void OnBpmFromChanged(string value) => Refresh();
    partial void OnBpmToChanged(string value) => Refresh();
    partial void OnOnlyUnusedChanged(bool value) => Refresh();
    partial void OnYearFilterChanged(string value)
    {
        if (value is null) YearFilter = AnyYear;   // the list of years was replaced under the box
        else Refresh();
    }

    /// <summary>The years to choose from, from what your projects used; the chosen one stays if it's still there.</summary>
    void UpdateYears()
    {
        var years = Items.SelectMany(s => s.UsedInYears).Distinct().OrderDescending().Select(y => y.ToString()).ToList();
        if (years.SequenceEqual(YearChoices.Skip(1))) return;
        var keep = YearFilter;
        YearChoices = [AnyYear, .. years];
        YearFilter = YearChoices.Contains(keep) ? keep : AnyYear;
    }
    partial void OnOnlyFavouritesChanged(bool value) => Refresh();
    partial void OnTagFilterChanged(string value) => Refresh();

    [RelayCommand]
    async Task Search()
    {
        if (!DescribeMode)
        {
            Refresh();
            return;
        }
        var request = ++_describing;   // a describe search still running is no longer wanted
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            _describeScores = null;
        }
        else if (!Org.HasDeepListening)
        {
            Message = "Searching by description needs the sound-understanding model and a Deep listen pass (see the Teach tab).";
            return;
        }
        else
        {
            var text = SearchText;
            var hits = await Task.Run(() => Org.Describe(text, 300));
            // Describe mode turned off or the filters cleared meanwhile: not wanted any more. (Words typed meanwhile wait
            // for Enter, as always; the message says which words these matches are for.)
            if (request != _describing || !DescribeMode) return;
            _describeScores = hits.ToDictionary(h => h.Sound.Id, h => h.Score);
            ClearColumnSort();   // closest first, as the message says
            Message = $"Best matches for “{text}”, closest first";
        }
        ApplySort();
        Refresh();
    }

    [RelayCommand]
    void ClearFilters()
    {
        SearchText = "";
        _describing++;
        _describeScores = null;
        SelectedType = TypeCounts.FirstOrDefault();
        LoopFilter = 0;
        KeyFilter = "Any key";
        CompatibleKeys = false;
        BpmFrom = BpmTo = "";
        OnlyUnused = OnlyFavourites = false;
        YearFilter = AnyYear;
        TagFilter = "";
        Message = "";
        ApplySort();
        Refresh();
    }

    // ---- dragging into a DAW -------------------------------------------------------

    /// <summary>Whatever a row stands for: a sound, a sound-alike, a suggestion, a kit pad.</summary>
    static Sound? SoundOf(object item) => item switch
    {
        Sound s => s,
        SimilarSound s => s.Sound,
        Suggestion s => s.Sound,
        KitSlotItem k => k.Slot.Sound,
        Disagreement d => d.Sound,
        SoundTile t => t.Sound,
        HistoryItem h => h.Sound,
        GalaxyStar g => g.Sound,
        _ => null,
    };

    internal static DragCard Card(Sound s) => new(s.Name,
        string.Join(" · ", new[] { s.Type, s.Key, s.Bpm is { } b ? $"{b:0.#} BPM" : "", $"{s.Seconds:0.0}s" }.Where(x => x.Length > 0)),
        Glyphs.Audio);

    /// <summary>The original files, so the DAW gets the sound exactly as it is on disk.</summary>
    public DragPayload? MakeDrag(IReadOnlyList<object> items)
    {
        var sounds = items.SelectMany(i => i is TrailDrag t ? t.Sounds.Cast<object>() : [i]).Select(SoundOf).OfType<Sound>().Where(s => File.Exists(s.Path)).DistinctBy(s => s.Path).ToList();
        if (sounds.Count == 0) return null;
        Record(sounds, SoundEvent.Drag);
        return new DragPayload(sounds.Select(s => s.Path).ToList(), sounds.Select(Card).ToList(),
            sounds.Count == 1 ? "1 sound" : $"{sounds.Count} sounds");
    }

    // ---- selected sound -------------------------------------------------------------

    partial void OnSelectedChanged(Sound? value)
    {
        foreach (var old in _tiles.Values.Where(x => x.IsSelected)) old.IsSelected = false;
        if (value is not null && _tiles.TryGetValue(value.Id, out var tile)) tile.IsSelected = true;
        OnPropertyChanged(nameof(WaveSeconds));
        FollowPlayback();
        Notes = [];
        NoteChords = [];
        NotesMessage = "";
        FindSimilar(value);
        SelectedTypeLabel = value?.Type ?? "";
        KeyDetail = value?.Analysis is { KeyTonic: { } t } a
            ? $"{MusicTheory.KeyName(t, a.KeyMinor)} · Camelot {MusicTheory.Camelot(t, a.KeyMinor)} · {a.KeySource}" +
              (a.KeySource == "detected" ? $" ({a.KeyConfidence:P0} sure)" : "")
            : "";
    }

    int _similarFor;

    /// <summary>
    /// The picked sound's sound-alikes, found off the window's thread: comparing it with 450,000 others took about half
    /// a second, so arrowing through the list or clicking stars kept stopping. Only the newest pick's are shown.
    /// </summary>
    async void FindSimilar(Sound? s)
    {
        var version = ++_similarFor;
        Similar = [];
        if (s is null) return;
        try
        {
            var count = SimilarCount;
            var found = await UiWork.Run(() => Org.Similar(s, count));
            if (version == _similarFor) Similar = found;
        }
        catch (Exception e) { AppLog.Info("Sounds", $"Couldn't find sounds like {s.Name}: {e.Message}"); }
    }

    partial void OnSelectedTypeLabelChanged(string value)
    {
        if (Selected is not { } s || string.IsNullOrEmpty(value) || value == s.Type) return;
        Org.SetUserType(s, value == SoundTypes.Unknown ? null : value);
        Message = $"Labelled {s.Name} as {value}. Retrain on the Teach tab to let it learn from this.";
        Refresh();
    }

    [RelayCommand]
    void Play(Sound? s)
    {
        s ??= Selected;
        if (s is null) return;
        try
        {
            if (_player.Playing == s.Path) { _player.Stop(); FollowPlayback(); return; }
            Start(s, 0);
        }
        catch (Exception e) { Message = $"Can't play {s.Name}: {e.Message}"; }
    }

    void Start(Sound s, double from, bool count = true)
    {
        _previews?.Cancel();   // a preview still opening mustn't start over this one
        _player.Play(s.Path, from);
        _preview = count ? null : s.Path;
        if (count) Count(s);
        FollowPlayback();
        _clock.Start();
    }

    string? _preview;

    // Plays feed Rediscover's sense of what you like, so only deliberate plays count.
    void Count(Sound s)
    {
        Org.Library.MarkPlayed(s.Id);
        s.Plays++;
        s.Activity = s.Activity with { Plays = s.Activity.Plays + 1 };
        Rescore([s]);
        if (IsHistory) return;   // the History list doesn't jump about under the pointer while you play from it
        LoadHistory();
    }

    /// <summary>
    /// A single click on a sound (a row, a tile, a History line): it plays, Autoplay or not, and clicking it again while
    /// it plays stops it (Solomon's 19 Oct note). Like Autoplay previews, it doesn't count as a play until you
    /// double-click it; the views leave a double-click's second click to <see cref="Audition"/>.
    /// </summary>
    public void PlayClicked()
    {
        if (Selected is not { } s) return;
        if (IsPreviewing(s))
        {
            _previews?.Cancel();   // one still opening mustn't start after the click that stopped it
            StopPlaying();
            return;
        }
        StartPreview(s);
    }

    /// <summary>
    /// The views call this when an arrow key lands on a sound, not when the list moves the selection by itself
    /// (a sort, a filter, coming back to the tab). It plays only with Autoplay on; previews don't count as plays.
    /// </summary>
    public void PlayPicked()
    {
        if (!AutoPlay || Selected is not { } s || IsPreviewing(s)) return;
        StartPreview(s);
    }

    /// <summary>
    /// An arrow key moved onto another sound: with Autoplay on, it plays at once. Held down, the key moves on no faster
    /// than <see cref="StepMs"/> (see <see cref="MayStep"/>), so every sound it lands on plays while it's highlighted and
    /// none is skipped (Solomon's 19 Oct note); the one the key is let go on plays in full (<see cref="PlayPicked"/>).
    /// </summary>
    public void PlayPassing()
    {
        if (!AutoPlay || Selected is not { } s || IsPreviewing(s)) return;
        StartPreview(s);
    }

    public const string StepKey = "sound_step_ms";
    public static IReadOnlyList<int> StepChoices { get; } = [150, 250, 400, 600, 1000];
    /// <summary>How long each sound plays while a held arrow key steps through them, in milliseconds (Settings › Sounds).</summary>
    [ObservableProperty] int _stepMs;

    partial void OnStepMsChanged(int value) => _db.SetSetting(StepKey, value.ToString());

    long _steppedAt;

    /// <summary>
    /// Whether an arrow key may move the highlight now. A press always may; a key held down (its repeats) waits until
    /// the sound it's on has played for <see cref="StepMs"/>, so what you see and what you hear stay together. With
    /// Autoplay off nothing plays, so a held key runs through the list at the keyboard's own speed.
    /// </summary>
    public bool MayStep(bool held)
    {
        if (!AutoPlay) return true;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (held && System.Diagnostics.Stopwatch.GetElapsedTime(_steppedAt, now).TotalMilliseconds < StepMs) return false;
        _steppedAt = now;
        return true;
    }

    /// <summary>The sound is playing, or still being opened to play.</summary>
    bool IsPreviewing(Sound s) => _player.Playing == s.Path || _previews is { Busy: true } p && p.Latest == s.Path;

    /// <summary>A preview (not a deliberate play): opened off the UI thread, so the arrow keys never wait for the disk.</summary>
    void StartPreview(Sound s)
    {
        _preview = s.Path;
        Previews.Play(s.Path);
        _clock.Start();
    }

    /// <summary>
    /// A double-click: the first click already started the sound as a preview (<see cref="PlayClicked"/>, Autoplay or
    /// not), so it keeps playing and now counts as a play. A sound that isn't playing starts (and counts).
    /// </summary>
    [RelayCommand]
    void Audition()
    {
        if (Selected is not { } s) return;
        if (_preview == s.Path && IsPreviewing(s)) { Count(s); _preview = null; }
        else if (!IsPreviewing(s)) Play(s);
    }

    /// <summary>A click on the waveform: jump there if it's playing, otherwise play from there.</summary>
    [RelayCommand]
    void Seek(double seconds)
    {
        if (Selected is not { } s) return;
        try
        {
            if (_player.Playing == s.Path) _player.Seek(seconds);
            else Start(s, seconds);
            FollowPlayback();
        }
        catch (Exception e) { Message = $"Can't play {s.Name}: {e.Message}"; }
    }

    /// <summary>Moves the waveform's playhead along with the sound; stops the clock once nothing plays.</summary>
    void FollowPlayback()
    {
        var playing = _player.Playing is not null;
        PlayPosition = playing && Selected is { } s && _player.Playing == s.Path && _player.Position is { } p ? p : -1;
        if (!playing) _clock.Stop();
    }

    [RelayCommand]
    void StopPlaying()
    {
        _player.Stop();
        FollowPlayback();
    }

    /// <summary>Whether a sound is playing now (Esc stops it).</summary>
    public bool IsSounding => _player.Playing is not null;

    /// <summary>Puts where the sound (or the selected one) is on the clipboard.</summary>
    [RelayCommand]
    void CopyPath(Sound? s)
    {
        if ((s ?? Selected) is { } x) Ui.Current.CopyText(x.Path);
    }

    [RelayCommand]
    void Reveal(Sound? s)
    {
        if ((s ?? Selected) is { } x) Shell.Reveal(x.Path);
    }

    /// <summary>
    /// Puts the sound (or the selected one) into the project open in Bitwig, in a Sampler on a new track named after
    /// it. Bitwig needs the Music Organiser extension with "Let the app make changes" on.
    /// </summary>
    [RelayCommand]
    async Task ToBitwig(Sound? s)
    {
        if ((s ?? Selected) is not { } sound) return;
        var card = Activities.Start($"Putting {sound.Name} into Bitwig", "On a Sampler, on a new track");
        try
        {
            using var link = await BitwigLink.ConnectAsync();
            var sent = await BitwigSend.SoundAsync(link, sound.Path, sound.Name);
            if (sent.Problem is { } problem)
            {
                card.Fail($"{sound.Name} didn't go in cleanly", problem + ".");
                Message = $"{problem}.";
                return;
            }
            card.Finish($"{sound.Name} is in Bitwig", "On a Sampler, on a new track");
            Record([sound], SoundEvent.Send);
        }
        catch (DawLinkException e) { card.Fail("Couldn't put it into Bitwig", e.Message); Message = e.Message; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            card.Fail("Couldn't put it into Bitwig", e.Message);
            Message = $"Sending to Bitwig failed: {e.Message}";
        }
    }

    [RelayCommand]
    void Select(Sound? s)
    {
        if (s is null) return;
        Selected = s;
        Play(s);
    }

    [RelayCommand]
    void ToggleFavourite()
    {
        if (Selected is not { } s) return;
        s.Favourite = !s.Favourite;
        Org.Library.SetFavourite(s.Id, s.Favourite);
        Rescore([s]);
        OnPropertyChanged(nameof(Selected));
        Refresh();
    }

    [RelayCommand]
    void AddTag()
    {
        if (Selected is not { } s || string.IsNullOrWhiteSpace(NewTag)) return;
        var tag = NewTag.Trim();
        Org.Library.AddTag(s.Id, tag);
        if (!s.Tags.Contains(tag)) s.Tags = [.. s.Tags, tag];
        NewTag = "";
        RefreshSelected();
    }

    [RelayCommand]
    void RemoveTag(string? tag)
    {
        if (Selected is not { } s || tag is null) return;
        Org.Library.RemoveTag(s.Id, tag);
        s.Tags = s.Tags.Where(t => t != tag).ToList();
        RefreshSelected();
    }

    void RefreshSelected()
    {
        var s = Selected;
        Selected = null;
        Selected = s;
        TagSummaries = Org.Library.TagCounts().Select(t => new TagSummary(t.Tag, t.Yours, t.Learned)).ToList();
    }

    [RelayCommand]
    async Task DetectNotes()
    {
        if (Selected is not { } s) return;
        NotesMessage = "Listening for notes…";
        try
        {
            var (notes, chords) = await Task.Run(() => Org.Transcribe(s));
            // Another sound picked meanwhile: these notes aren't its (and Export MIDI would save them under its name).
            if (Selected != s) return;
            Notes = notes;
            NoteChords = chords;
            NotesMessage = notes.Count == 0 ? "No clear notes found (drums and noise usually have none)."
                : $"{notes.Count} notes" + (chords.Count > 0 ? $" · chords: {string.Join(" → ", chords.Select(c => c.Name).Take(16))}" : "");
        }
        catch (Exception e)
        {
            if (Selected == s) NotesMessage = $"Couldn't detect notes: {e.Message}";
        }
    }

    [RelayCommand]
    async Task ExportMidi()
    {
        if (Selected is not { } s || Notes.Count == 0) return;
        if (await Ui.Current.PickSaveFile(s.Name + ".mid", "MIDI file", "mid", ExportFolder()) is not { } file) return;
        var bpm = s.Bpm ?? 120;
        File.WriteAllBytes(file, MidiWriter.Write(NoteTranscriber.ToMidi(s.Name, Notes, bpm), bpm));
        Message = $"Saved {Path.GetFileName(file)}";
        Activities.Done($"Saved {Path.GetFileName(file)}", $"{Notes.Count} notes", "Show file", () => Shell.Reveal(file));
    }

    internal LibraryDb Db => _db;
    /// <summary>Samples your projects use, missing ones first: the "In your projects" tab.</summary>
    public SamplesViewModel Samples => _main.Samples;
    internal string DataFolder => _main.DataFolder;
    internal ActivityFeed Activities => _main.Activities;

    internal string ExportFolder()
    {
        var f = _db.GetSetting(ProjectDetailViewModel.ExportFolderKey);
        if (!string.IsNullOrEmpty(f) && Directory.Exists(f)) return f;
        var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.Create), "Music Organiser Exports");
        Directory.CreateDirectory(d);
        return d;
    }

    // ---- long jobs ------------------------------------------------------------------

    /// <param name="done">The card's words once the job has finished, worked out then.</param>
    /// <param name="always">False: the page reloads only if the job read the sounds in again (a scan that found nothing new doesn't).</param>
    internal async Task RunAsync(string label, Action<IProgress<SoundProgress>, CancellationToken> job, Func<(string Title, string Detail)>? done = null,
                                 bool always = true)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = label;
        _cts = new CancellationTokenSource();
        var cts = _cts;
        var card = Activities.Start(label, cancel: cts.Cancel);
        var progress = new ThrottledProgress<SoundProgress>(p =>
        {
            BusyText = p.Total > 0 ? $"{p.Stage}: {p.Done:N0} of {p.Total:N0}" : p.Stage;
            BusyPercent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
            card.Report(p.Total > 0 ? BusyPercent : -1, BusyText);
        });
        try
        {
            try { await Task.Run(() => job(progress, cts.Token), cts.Token); }
            finally { progress.Close(); }
            var (title, detail) = done?.Invoke() ?? ($"{label}: done", "");
            card.Finish(title, detail);
        }
        catch (OperationCanceledException) { Message = "Stopped"; card.Stop($"{label}: stopped"); }
        catch (Exception e) { Message = $"{label} failed: {e.Message}"; card.Fail($"{label} failed", e.Message); }
        finally
        {
            IsBusy = false;
            BusyPercent = 0;
            if (always || !ReferenceEquals(Items, Org.Sounds)) await ReloadAsync();
            if (_changedSounds.Count > 0 || _sampleFoldersOverflowed) _ = TakeInChangedSoundsAsync();   // seen while this ran
        }
    }

    [RelayCommand]
    async Task ScanAsync()
    {
        var folders = SampleFolders;
        if (folders.Count == 0)
        {
            Message = "Add your sample folders in Settings first.";
            return;
        }
        var count = 0;
        List<long> arrived = [];
        await RunAsync("Scanning sample folders", (p, ct) =>
        {
            var before = Org.Sounds.Select(s => s.Id).ToHashSet();
            count = Org.Scan(folders, p, ct);
            if (count > 0) arrived = Org.Sounds.Where(s => !before.Contains(s.Id)).Select(s => s.Id).ToList();
        },
                       () => ("Sample folders up to date", count == 0 ? "Nothing new" : $"Analysed {count:N0} new or changed sounds"), always: false);
        if (count > 0) Message = $"Analysed {count:N0} new or changed sounds.";
        if (count > 0 && Duplicates.AfterScan) await Duplicates.FindAsync();
        if (count > 0) await ListenToArrivals(arrived);
        if (count > 0 || IsGalaxy) await MapGalaxyIfNeeded();
    }

    [RelayCommand]
    async Task DeepListen()
    {
        if (!ClapModel.IsInstalled())
        {
            Message = "Download the sound-understanding model first (Teach tab).";
            return;
        }
        await RunAsync("Deep listening", (p, ct) => Org.DeepListen(p, ct), () => ("Deep listening finished", "Searching by description now covers these sounds"));
        await MapGalaxyIfNeeded();   // the galaxy redraws itself from the sharper fingerprints
    }

    [RelayCommand]
    async Task DownloadModel()
    {
        if (IsBusy)
        {
            ModelMessage = $"Wait for {BusyText.ToLowerInvariant()} to finish (or press Stop), then download the model.";
            return;
        }
        IsBusy = IsDownloading = true;
        ModelMessage = "";
        DownloadText = BusyText = "Connecting to Hugging Face…";
        DownloadSizeUnknown = true;
        DownloadPercent = BusyPercent = 0;
        _cts = new CancellationTokenSource();
        var card = Activities.Start("Downloading the sound model", "Connecting to Hugging Face…", _cts.Cancel);
        try
        {
            var progress = new Progress<(string File, long Done, long? Total)>(p =>
            {
                var index = Array.FindIndex(ClapModel.Files, f => f.Local == p.File) + 1;
                var size = $"{p.Done / 1048576.0:N1} MB" + (p.Total is { } t ? $" of {t / 1048576.0:N1} MB" : "");
                DownloadText = $"Downloading file {index} of {ClapModel.Files.Length} ({p.File}): {size}";
                BusyText = $"Downloading model: {size}";
                DownloadSizeUnknown = p.Total is not > 0;
                DownloadPercent = BusyPercent = p.Total is > 0 ? 100.0 * p.Done / p.Total.Value : 0;
                card.Report(p.Total is > 0 ? DownloadPercent : -1, $"File {index} of {ClapModel.Files.Length}: {size}");
            });
            await ClapModel.DownloadAsync(progress, ct: _cts.Token);
            Org.ResetClap();
            ModelMessage = "Downloaded and installed. Press Deep listen to let it hear your library.";
            card.Finish("Sound model installed", "Press Deep listen to let it hear your library");
        }
        catch (OperationCanceledException)
        {
            ModelMessage = "Download stopped. Press Download model to carry on from the file it was on.";
            card.Stop("Download stopped", "It carries on from the file it was on");
        }
        catch (Exception e)
        {
            ModelMessage = $"Download failed: {e.Message}. Check the internet connection and try again.";
            card.Fail("Download failed", e.Message);
        }
        finally
        {
            IsBusy = IsDownloading = false;
            BusyPercent = DownloadPercent = 0;
            UpdateModelInfo();
        }
    }

    [RelayCommand]
    async Task Retrain() => await RunAsync("Learning your sounds", (p, _) => Org.UpdateTypes(p), () => ("Learned from your labels", "Sound types are updated"));

    [RelayCommand]
    void Cancel() => _cts?.Cancel();

    [RelayCommand]
    void Confirm(Disagreement? d)
    {
        if (d is null) return;
        Org.SetUserType(d.Sound, d.Thinks);
        Disagreements = Disagreements.Where(x => x != d).ToList();
    }

    [RelayCommand]
    void KeepName(Disagreement? d)
    {
        if (d is null || d.Sound.NameType is not { } n) return;
        Org.SetUserType(d.Sound, n);
        Disagreements = Disagreements.Where(x => x != d).ToList();
    }

    [RelayCommand]
    void FilterByTag(string? tag)
    {
        TagFilter = tag ?? "";
        Refresh();
    }

    /// <summary>
    /// The projects the sound's "Used in" counts: those with it at its own path, else (as the count does) those with a
    /// copy of the same name anywhere. Looked up in the background: the name means reading every project's samples.
    /// </summary>
    [RelayCommand]
    async Task ShowProjects()
    {
        if (Selected is not { UsedIn: > 0 } s) return;
        var path = s.Path;
        var ids = await UiWork.Run(() => _db.ProjectsUsingSample(path) is { Count: > 0 } atPath
            ? atPath
            : _db.ProjectsUsingSampleNamed(Path.GetFileName(path)));
        _main.Projects.ShowOnly(ids, $"Projects using {s.Name}");
        _main.ShowProjects();
    }
}

public sealed record TypeCount(string Type, int Count)
{
    public override string ToString() => $"{Type} ({Count:N0})";
}

public sealed record Disagreement(Sound Sound, string Thinks, double P)
{
    public string Text => $"Named as {Sound.NameType}, sounds like {Thinks} ({P:P0} sure)";
}

public sealed record TagSummary(string Tag, int Yours, int Learned)
{
    public string Text => Learned > 0 ? $"{Yours} tagged by you, {Learned} suggested" : Yours >= 5 ? $"{Yours} tagged by you" : $"{Yours} tagged by you (tag 5 to teach it)";
}

/// <summary>Sounds you haven't used much, picked to match your taste.</summary>
public sealed partial class RediscoverViewModel(SoundsViewModel parent) : ObservableObject
{
    public SoundsViewModel Parent => parent;

    public IReadOnlyList<string> Types => ["Any type", .. SoundTypes.All];

    [ObservableProperty] string _type = "Any type";
    /// <summary>Only sounds whose file is dated this year (Solomon, 2026-10-23: "should be able to filter by year here").</summary>
    [ObservableProperty] string _year = AnyYear;
    public const string AnyYear = "Any year";
    [ObservableProperty] IReadOnlyList<string> _years = [AnyYear];
    [ObservableProperty] double _adventure = 0.3;
    [ObservableProperty] bool _includeRarelyUsed;
    /// <summary>How many sounds a shuffle picks: the last number picked here or in Settings › Sounds.</summary>
    [ObservableProperty] int _howMany = int.TryParse(parent.Db.GetSetting(HowManyKey), out var n) && HowManyChoices.Contains(n) ? n : 60;
    public const string HowManyKey = "rediscover_count";
    public static IReadOnlyList<int> HowManyChoices { get; } = [30, 60, 120, 250];
    [ObservableProperty] IReadOnlyList<Suggestion> _suggestions = [];
    [ObservableProperty] IReadOnlyList<FolderUse> _folders = [];
    [ObservableProperty] string _intro = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShuffleText))] bool _isBusy;
    [ObservableProperty] string _emptyTitle = "Find sounds you've forgotten";
    [ObservableProperty] string _emptyText = "Pick a type and how adventurous to be, then press Shuffle.";

    public string ShuffleText => IsBusy ? "Shuffling…" : "Shuffle";

    [RelayCommand]
    async Task Shuffle()
    {
        _changes++;   // whatever was waiting to shuffle is covered by this one
        var o = new Core.Sounds.Rediscover.Options(Type == "Any type" ? null : Type, HowMany, Adventure, 0, IncludeRarelyUsed ? 1 : 0,
                                                   int.TryParse(Year, out var year) ? year : null);
        var org = parent.Org;
        IsBusy = true;
        Intro = $"Looking through {org.Sounds.Count:N0} sounds…";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // The folders are quick: they show while the sounds are still being picked.
            var suggesting = Task.Run(() => Core.Sounds.Rediscover.Suggest(org, o));
            Folders = await Task.Run(() => Core.Sounds.Rediscover.Folders(org).Take(40).ToList());
            var years = await Task.Run(() => Core.Sounds.Rediscover.Years(org).Select(y => y.ToString()).ToList());
            if (!years.SequenceEqual(Years.Skip(1)))
            {
                var keep = Year;
                Years = [AnyYear, .. years];
                Year = Years.Contains(keep) ? keep : AnyYear;
            }
            Suggestions = await suggesting;
            if (Suggestions.Count == 0)
            {
                EmptyTitle = "Nothing to suggest";
                EmptyText = await Task.Run(() => Core.Sounds.Rediscover.WhyNothing(org, o));
                Intro = "";
            }
            else
            {
                var used = org.Sounds.Count(s => s.UsedIn > 0);
                Intro = used == 0
                    ? "None of your sounds show up in scanned projects yet, so these are a varied pick. Star favourites or scan your projects to teach it your taste."
                    : $"Learned from the {used:N0} sounds your projects use, plus your favourites and plays.";
            }
            AppLog.Info("Rediscover", $"{Suggestions.Count} suggestions from {org.Sounds.Count:N0} sounds in {clock.Elapsed.TotalSeconds:0.00} s");
        }
        catch (Exception e)
        {
            Suggestions = [];
            EmptyTitle = "Couldn't pick sounds";
            EmptyText = e.Message;
            Intro = "";
            AppLog.Info("Rediscover", $"Failed: {e}");
        }
        finally { IsBusy = false; }
    }

    /// <summary>How long after the last change (a slider still moving) the list shuffles again.</summary>
    public static TimeSpan ShuffleDelay { get; set; } = TimeSpan.FromMilliseconds(400);
    int _changes;

    // Once there's a list, changing what it's picked by picks it again, so the slider visibly does something.
    partial void OnTypeChanged(string value) => ShuffleSoon();
    partial void OnYearChanged(string value)
    {
        if (value is null) Year = AnyYear;   // the list of years was replaced under the box
        else ShuffleSoon();
    }
    partial void OnAdventureChanged(double value) => ShuffleSoon();
    partial void OnIncludeRarelyUsedChanged(bool value) => ShuffleSoon();
    partial void OnHowManyChanged(int value)
    {
        parent.Db.SetSetting(HowManyKey, value.ToString());
        ShuffleSoon();
    }

    async void ShuffleSoon()
    {
        if (Suggestions.Count == 0 && !IsBusy) return;   // nothing picked yet: Shuffle starts it
        var mine = ++_changes;
        if (ShuffleDelay > TimeSpan.Zero) await Task.Delay(ShuffleDelay);
        while (IsBusy && mine == _changes) await Task.Delay(50);
        if (mine == _changes) await Shuffle();
    }
}

/// <summary>Random kits from your library.</summary>
public sealed partial class KitViewModel(SoundsViewModel parent) : ObservableObject, IFileDragSource
{
    public SoundsViewModel Parent => parent;

    public ObservableCollection<KitSlotItem> Slots { get; } = new(KitBuilder.DefaultDrumKit().Select(s => new KitSlotItem(s)));

    [ObservableProperty] bool _preferUnused = true;
    [ObservableProperty] bool _samePack;
    [ObservableProperty] string _vibe = "";
    [ObservableProperty] string _key = "Any key";
    [ObservableProperty] double _randomness = 0.5;
    [ObservableProperty] bool _elektronReady = true;
    [ObservableProperty] bool _mono;
    [ObservableProperty] bool _normalise;
    [ObservableProperty] int _seed;
    [ObservableProperty] string _status = "";

    KitBuilder.Options Options(int seed) => new(PreferUnused, SamePack, string.IsNullOrWhiteSpace(Vibe) ? null : Vibe,
        MusicTheory.ParseKey(Key) is { } k ? k.Tonic : null, seed, Randomness);

    [RelayCommand]
    async Task Roll()
    {
        var slots = Slots.Select(s => s.Slot).ToList();
        if (!string.IsNullOrWhiteSpace(Vibe) && !parent.Org.HasDeepListening)
            Status = "The vibe needs deep listening (Teach tab); rolling without it.";
        Seed = await Task.Run(() => KitBuilder.Roll(parent.Org, slots, Options(0)));
        foreach (var s in Slots) s.Refresh();
        var empty = Slots.Count(s => s.Slot.Sound is null);
        Status = empty > 0 ? $"{empty} pad(s) found no sound of the right type yet." : $"Kit #{Seed}";
    }

    [RelayCommand]
    async Task RollOne(KitSlotItem? item)
    {
        if (item is null) return;
        var locks = Slots.ToDictionary(s => s, s => s.Slot.Locked);
        foreach (var s in Slots) s.Slot.Locked = s != item;
        item.Slot.Locked = false;
        await Task.Run(() => KitBuilder.Roll(parent.Org, Slots.Select(s => s.Slot).ToList(), Options(0)));
        foreach (var (s, locked) in locks) s.Slot.Locked = locked;
        foreach (var s in Slots) s.Refresh();
    }

    [RelayCommand]
    void PlaySlot(KitSlotItem? item)
    {
        if (item?.Slot.Sound is { } s) parent.PlayCommand.Execute(s);
    }

    [RelayCommand]
    void RemoveSlot(KitSlotItem? item)
    {
        if (item is not null) Slots.Remove(item);
    }

    [RelayCommand]
    void AddSlot(string? type)
    {
        if (string.IsNullOrEmpty(type)) return;
        Slots.Add(new KitSlotItem(new KitSlot { Name = type, Types = [type] }));
    }

    [RelayCommand]
    void Export()
    {
        try
        {
            if (ExportKit() is not { } folder) return;
            Status = $"Exported to {folder}";
            Shell.Open(folder);
            parent.Activities.Done("Kit exported", Path.GetFileName(folder), "Show", () => Shell.Open(folder));
        }
        catch (Exception e)
        {
            Status = $"Export failed: {e.Message}";
            parent.Activities.Failed("Kit export failed", e.Message);
        }
    }

    string? ExportKit() => ExportPads()?.Folder;

    KitBuilder.Exported? ExportPads()
    {
        var slots = Slots.Select(s => s.Slot).Where(s => s.Sound is not null).ToList();
        if (slots.Count == 0) { Status = "Roll a kit first."; return null; }
        return KitBuilder.ExportPads(slots, Path.Combine(parent.ExportFolder(), "Kits"),
            $"Kit {DateTime.Now:yyyy-MM-dd HHmm} #{Seed}", new KitBuilder.ExportOptions(ElektronReady, Mono, Normalise));
    }

    // The kit as last dragged (its pads, sounds and options), and what that export wrote.
    (string Kit, KitBuilder.Exported Export)? _dragged;

    /// <summary>
    /// The "Drag kit" handle: the kit's pads, exported with the chosen options, in pad order (only this kit's: a pad
    /// re-rolled or taken off since an earlier drag isn't in it). The same kit dragged again, unchanged, takes the files
    /// already exported rather than converting every pad again as the drag starts.
    /// Single pads dragged from the list carry their original sound instead.
    /// </summary>
    public DragPayload? MakeDrag(IReadOnlyList<object> items)
    {
        if (items.Count > 0 && items.All(i => i is KitSlotItem) && items.Count < Slots.Count)
            return parent.MakeDrag(items);
        var pads = Slots.Where(s => s.Slot.Sound is not null).ToList();
        var kit = string.Join("\n", pads.Select(p => $"{p.Name}\t{p.Slot.Sound!.Path}").Prepend($"{Seed} {ElektronReady} {Mono} {Normalise}"));
        if (_dragged is not { } last || last.Kit != kit || !last.Export.Pads.All(File.Exists))
        {
            if (ExportPads() is not { } export) return null;
            _dragged = (kit, export);
        }
        var (folder, files) = _dragged.Value.Export;
        var cards = pads.Select(p => new DragCard(p.Name, p.SoundName, Glyphs.Kit)).ToList();
        parent.Record(pads.Select(p => p.Slot.Sound).OfType<Sound>(), SoundEvent.Drag);
        Status = $"Exported to {folder}";
        return new DragPayload(files, cards, $"Kit of {files.Count} pads", Numbered: true);
    }

    // ---- Sending to Bitwig ----

    /// <summary>Bitwig's ID for its Drum Machine, once the app has found it in Bitwig's own files.</summary>
    public const string DrumMachineKey = "bitwig_drum_machine";
    /// <summary>
    /// Set when no Bitwig file had a Drum Machine in it, or the ID found didn't give one, so kits go on the
    /// selected Drum Machine until the app restarts.
    /// </summary>
    bool _useSelected;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendToBitwigCommand))] bool _isSending;

    /// <summary>
    /// Puts the kit on a Drum Machine in the project open in Bitwig: a new one on a new track, each sound on the
    /// pad General MIDI drum parts use (kick on C1, snare on D1, closed hat on F#1). Until the app has learned the
    /// Drum Machine's ID from Bitwig's files, the kit goes on the Drum Machine selected in Bitwig instead.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    async Task SendToBitwig()
    {
        var pads = BitwigSend.KitPads(Slots.Where(s => s.Slot.Sound is not null)
            .Select(s => new BitwigSend.KitSound(s.Name, s.Slot.Types.FirstOrDefault() ?? "", s.Slot.Sound!.Path)));
        if (pads.Count == 0)
        {
            Status = "Roll a kit first.";
            return;
        }
        IsSending = true;
        Status = "Sending the kit to Bitwig…";
        var card = parent.Activities.Start("Sending the kit to Bitwig", $"{pads.Count} pads");
        try
        {
            using var link = await BitwigLink.ConnectAsync();
            var db = parent.Db;
            var id = await Task.Run(() => DrumMachineId(db));
            var name = Seed > 0 ? $"Kit #{Seed}" : "Kit";
            var kit = await BitwigSend.KitAsync(link, name, pads, id);
            if (!kit.DrumMachine)
            {
                db.SetSetting(DrumMachineKey, "");
                _useSelected = true;
                Status = "Bitwig didn't add a Drum Machine to the new track. Add one to a track yourself, select it, and send the kit again.";
                card.Fail("Bitwig didn't add a Drum Machine", "Add one to a track yourself, select it, and send the kit again.");
                return;
            }
            Status = Describe(kit, name);
            card.Finish($"{name} is in Bitwig", $"{pads.Count} pads on a Drum Machine");
            parent.Record(Slots.Select(s => s.Slot.Sound).OfType<Sound>(), SoundEvent.Send);
        }
        catch (DawLinkException e) { Status = e.Message; card.Fail("Couldn't send the kit", e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = $"Sending to Bitwig failed: {e.Message}";
            card.Fail("Sending to Bitwig failed", e.Message);
        }
        finally { IsSending = false; }
    }

    bool CanSend() => !IsSending;

    /// <summary>
    /// The Drum Machine's ID: as saved, or found in a Bitwig file that has one (a project in the library, the
    /// template, then Bitwig presets), or null to use the Drum Machine selected in Bitwig.
    /// </summary>
    Guid? DrumMachineId(LibraryDb db)
    {
        if (Guid.TryParse(db.GetSetting(DrumMachineKey), out var known)) return known;
        if (_useSelected) return null;
        var used = db.ProjectsUsingPlugin(BitwigSend.DrumMachine).ToHashSet();
        var projects = db.Projects().Where(p => used.Contains(p.Id) && p.Daw == "Bitwig Studio" && !p.FileMissing)
                         .OrderByDescending(p => p.Modified).Select(p => p.Path);
        var template = BitwigTemplateStore.Find(db, parent.DataFolder) is { } t ? [t] : Array.Empty<string>();
        if (BitwigSend.FindDeviceId(BitwigSend.DrumMachine, projects.Concat(template).Concat(BitwigSend.BitwigPresets())) is not { } id)
        {
            _useSelected = true;
            return null;
        }
        db.SetSetting(DrumMachineKey, id.ToString());
        return id;
    }

    /// <summary>Where the kit went and which sound is on which pad, with Bitwig's note names.</summary>
    static string Describe(BitwigSend.SentKit kit, string name)
    {
        static string On(IEnumerable<BitwigPad> pads) => string.Join(", ", pads.Select(p => $"{p.Name} on {DawProject.NoteName(p.Note)}"));
        static string Named(IEnumerable<BitwigPad> pads) => string.Join(", ", pads.Select(p => $"{p.Name} ({DawProject.NoteName(p.Note)})"));
        var where = kit.Track is null ? "the Drum Machine selected in Bitwig" : "a Drum Machine on a new track in Bitwig";
        var parts = new List<string> { kit.Filled.Count > 0 ? $"{name} is on {where}: {On(kit.Filled)}." : $"Nothing went on {where}." };
        if (kit.Skipped.Count > 0) parts.Add($"The pads for {Named(kit.Skipped)} already had sounds, so they were left alone.");
        if (kit.Failed.Count > 0) parts.Add($"Bitwig didn't load {Named(kit.Failed)}.");
        return string.Join(" ", parts);
    }
}

public sealed partial class KitSlotItem(KitSlot slot) : ObservableObject
{
    public KitSlot Slot => slot;
    public string Name => slot.Name;
    public string SoundName => slot.Sound?.Name ?? "Empty pad";
    public string Detail => slot.Sound is { } s ? $"{s.Type} · {s.Folder}" + (s.UsedIn > 0 ? $" · used in {s.UsedIn}" : " · unused") : "Roll a kit to fill it";

    public bool Locked
    {
        get => slot.Locked;
        set
        {
            slot.Locked = value;
            OnPropertyChanged();
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(SoundName));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Locked));
    }
}

/// <summary>A row of tiles on the Sounds page.</summary>
public sealed record TileRow(IReadOnlyList<SoundTile> Tiles);

/// <summary>One sound as a tile.</summary>
public sealed partial class SoundTile(Sound sound) : ObservableObject
{
    public Sound Sound => sound;
    [ObservableProperty] bool _isSelected;
    /// <summary>"Kick · C · 0.4s" or "Loop · 124 BPM · F minor · 8.0s".</summary>
    public string Details => string.Join(" · ", new[]
    {
        sound.Type, sound.Bpm is { } b ? $"{b:0.#} BPM" : "", sound.Key, sound.Seconds > 0 ? $"{sound.Seconds:0.0}s" : "",
    }.Where(x => x.Length > 0));
}

/// <summary>A sound in History, with when you last played it.</summary>
public sealed record HistoryItem(Sound Sound, DateTime At)
{
    public string When => Ago(DateTime.Now - At, At);

    static string Ago(TimeSpan t, DateTime at) =>
        t.TotalMinutes < 1 ? "just now" :
        t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min ago" :
        t.TotalDays < 1 ? $"{(int)t.TotalHours} h ago" :
        t.TotalDays < 7 ? at.ToString("dddd HH:mm") : at.ToString("d MMM yyyy");
}
