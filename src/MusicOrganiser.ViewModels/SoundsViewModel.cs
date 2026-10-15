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

    public static IReadOnlyList<string> AllTypes { get; } = [.. SoundTypes.All, SoundTypes.Unknown];
    public static IReadOnlyList<string> KeyChoices { get; } = ["Any key", .. MusicTheory.NoteNames.Select(n => n), .. MusicTheory.NoteNames.Select(n => n + "m")];

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

    public SoundsViewModel(LibraryDb db, SoundOrganiser org, MainViewModel main)
    {
        _db = db;
        _main = main;
        Org = org;
        Rediscover = new RediscoverViewModel(this);
        Kit = new KitViewModel(this);
        _autoPlay = db.GetSetting(AutoPlayKey) == "1";
        _viewMode = db.GetSetting(ViewKey) is List or Galaxy ? db.GetSetting(ViewKey)! : Tiles;
        _placedBy = db.GetSetting(GalaxyBasisKey) is { } basis && PlacedByChoices.Contains(basis) ? basis : SoundOrganiser.Both;
        _colourBy = db.GetSetting(GalaxyColourKey) is { } c && ColourChoices.Contains(c) ? c : "Type";
        _weights = LikedWeights.FromJson(db.GetSetting(LikedWeightsKey));
        _clock = Ui.Current.CreateTimer(TimeSpan.FromMilliseconds(30), FollowPlayback);
        Reload();
        if (SampleFolders.Count > 0) _ = ScanAsync();
        else if (IsGalaxy) _ = MapGalaxyIfNeeded();
    }

    IReadOnlyList<string> SampleFolders => _db.GetList(Scanner.SampleFoldersKey);

    /// <summary>Settings › Most liked: how much each sign of liking a sound counts.</summary>
    public const string LikedWeightsKey = "liked_weights";

    public void Reload()
    {
        Org.ApplyUsage(_db.SampleUsageByAge(DateTime.Now.AddYears(-1)));
        Items = Org.Sounds;
        foreach (var s in Items) _shuffle.TryAdd(s.Id, Random.Shared.Next());
        Score();
        View = Ui.Current.CreateView(Items as System.Collections.IList ?? Items.ToList());
        View.Filter = o => o is Sound s && Matches(s);
        ApplySort();
        LoadHistory();
        BuildStars();
        TypeCounts = [new TypeCount("All sounds", Items.Count), .. Items.GroupBy(s => s.Type).OrderByDescending(g => g.Count()).Select(g => new TypeCount(g.Key, g.Count()))];
        SelectedType ??= TypeCounts[0];
        UpdateSummary();
        UpdateModelInfo();
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
            Reload();   // nothing showing yet, or the sounds themselves changed
            return;
        }
        var since = DateTime.Now.AddYears(-1);
        var weights = _weights;
        var (changed, liked) = await Task.Run(() =>
        {
            var before = items.Select(s => (s.UsedIn, s.UsedInRecently)).ToArray();
            Org.ApplyUsage(_db.SampleUsageByAge(since));
            var changed = items.Where((s, i) => (s.UsedIn, s.UsedInRecently) != before[i]).Any();
            return (changed, changed ? items.Select(weights.Score).ToArray() : []);
        });
        if (!changed || !ReferenceEquals(items, Items)) return;
        var top = liked.Length == 0 ? 0 : liked.Max();
        for (var i = 0; i < items.Count; i++)
        {
            items[i].Liked = liked[i];
            items[i].LikedShare = top > 0 ? liked[i] / top : 0;
        }
        UpdateSummary();
        if (OnlyUnused) Refresh();
        else if (SortBy == MostLiked) ApplySort();
    }

    void UpdateSummary()
    {
        var loops = Items.Count(s => s.IsLoop);
        var unused = Items.Count(s => s.UsedIn == 0);
        Summary = Items.Count == 0
            ? "Add your sample folders in Settings, then scan them here."
            : $"{Items.Count:N0} sounds · {loops:N0} loops · {unused:N0} never used in a project";
    }

    void UpdateModelInfo()
    {
        ClapInstalled = ClapModel.IsInstalled();
        var withClap = Org.Library.CountWithClap();
        ClapStatus = !ClapInstalled
            ? "Not installed. Download it once (about 205 MB) to search by describing a sound, and for sharper sound-alikes and type guesses. It runs on this PC; nothing is uploaded."
            : Items.Count == 0
                ? $"Installed in {ClapModel.DefaultFolder}. Scan your sample folders, then Deep listen."
            : withClap >= Items.Count && Items.Count > 0
                ? $"Installed. Every sound has been deep-listened ({withClap:N0})."
                : $"Installed. {withClap:N0} of {Items.Count:N0} sounds deep-listened so far; run Deep listen to do the rest.";
        var m = Org.TypeModel;
        ModelSummary = m is null
            ? "Not trained yet: it needs at least 20 sounds whose folder or file names say what they are (or that you've labelled)."
            : $"Trained {m.Trained:g} on {m.Examples:N0} labelled sounds using {(m.Space == "clap" ? "deep-listening (CLAP) vectors" : "sound features")}, {m.Classes.Length} types" +
              (m.HeldOutAccuracy is { } acc ? $". Right {acc:P0} of the time on sounds it hadn't seen." : ".");
        Disagreements = Org.Disagreements().Select(d => new Disagreement(d.Sound, d.Thinks, d.P)).ToList();
        TagSummaries = Org.Library.TagCounts().Select(t => new TagSummary(t.Tag, t.Yours, t.Learned)).ToList();
    }

    // ---- filtering -----------------------------------------------------------------

    bool Matches(Sound s)
    {
        if (SelectedType is { Type: var t } && t != "All sounds" && s.Type != t) return false;
        if (LoopFilter == 1 && s.IsLoop) return false;
        if (LoopFilter == 2 && !s.IsLoop) return false;
        if (OnlyUnused && s.UsedIn > 0) return false;
        if (OnlyFavourites && !s.Favourite) return false;
        if (TagFilter.Length > 0 && !s.Tags.Any(x => x.Equals(TagFilter, StringComparison.OrdinalIgnoreCase))) return false;
        if (KeyFilter != "Any key" && MusicTheory.ParseKey(KeyFilter) is { } k)
        {
            if (s.Analysis?.KeyTonic is not { } tonic) return false;
            var ok = CompatibleKeys
                ? MusicTheory.CompatibleKeys(k.Tonic, k.Minor).Contains((tonic, s.Analysis.KeyMinor))
                : tonic == k.Tonic && s.Analysis.KeyMinor == k.Minor;
            if (!ok) return false;
        }
        if (double.TryParse(BpmFrom, out var lo) && (s.Bpm ?? -1) < lo) return false;
        if (double.TryParse(BpmTo, out var hi) && (s.Bpm is null || s.Bpm > hi)) return false;
        if (_describeScores is not null) return _describeScores.ContainsKey(s.Id);
        if (SearchText.Length > 0)
            return SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w =>
                s.Path.Contains(w, StringComparison.OrdinalIgnoreCase) || s.Type.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                s.Tags.Any(x => x.Contains(w, StringComparison.OrdinalIgnoreCase)) || s.Character.Contains(w, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    void ApplySort()
    {
        if (View is null) return;
        View.CustomSort = _describeScores is { } scores
            ? Comparer<object>.Create((a, b) => scores.GetValueOrDefault(((Sound)b).Id).CompareTo(scores.GetValueOrDefault(((Sound)a).Id)))
            : Comparer<object>.Create((a, b) => Compare((Sound)a, (Sound)b));
        BuildTiles();
    }

    int Compare(Sound a, Sound b)
    {
        var c = SortBy switch
        {
            MostLiked => b.Liked.CompareTo(a.Liked),
            "Name" => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            "Type" => string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase),
            "Key" => KeyOrder(a).CompareTo(KeyOrder(b)),
            "Newest" => b.Added.CompareTo(a.Added),
            _ => _shuffle.GetValueOrDefault(a.Id).CompareTo(_shuffle.GetValueOrDefault(b.Id)),
        };
        return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    // Keys in note order, major before minor; sounds with no key last.
    static int KeyOrder(Sound s) => s.Analysis?.KeyTonic is { } t ? t * 2 + (s.Analysis.KeyMinor ? 1 : 0) : int.MaxValue;

    partial void OnSortByChanged(string value) => ApplySort();

    /// <summary>A new random order (and switches to it).</summary>
    [RelayCommand]
    void Shuffle()
    {
        _shuffle = Items.ToDictionary(s => s.Id, _ => Random.Shared.Next());
        if (SortBy == Shuffled) ApplySort();
        else SortBy = Shuffled;
    }

    partial void OnViewModeChanged(string value)
    {
        if (value != History) _db.SetSetting(ViewKey, value);
        if (value == Tiles) BuildTiles();
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
        View?.Refresh();
        BuildTiles();
        BuildStars();
    }

    Dictionary<long, SoundTile> _tiles = [];

    void BuildTiles()
    {
        if (View is null || !IsTiles) return;
        Shown = View.Cast<Sound>().ToList();
        _tiles = Shown.ToDictionary(s => s.Id, s => new SoundTile(s) { IsSelected = s == Selected });
        TileRows = Shown.Chunk(TileColumns).Select(c => new TileRow(c.Select(s => _tiles[s.Id]).ToList())).ToList();
    }

    /// <summary>The row of tiles holding a sound, to scroll it into view.</summary>
    public TileRow? RowOf(Sound s) => TileRows.FirstOrDefault(r => r.Tiles.Any(t => t.Sound == s));

    /// <summary>Works out every sound's Most liked score, and its bar as a share of the most liked one's.</summary>
    void Score()
    {
        foreach (var s in Items) s.Liked = _weights.Score(s);
        var top = Items.Count == 0 ? 0 : Items.Max(s => s.Liked);
        foreach (var s in Items) s.LikedShare = top > 0 ? s.Liked / top : 0;
    }

    /// <summary>Settings changed how Most liked weighs things.</summary>
    public void WeightsChanged(LikedWeights weights)
    {
        _weights = weights;
        Score();
        if (SortBy == MostLiked) ApplySort();
        else BuildTiles();
    }

    void LoadHistory()
    {
        var byId = Items.ToDictionary(s => s.Id);
        HistoryItems = Org.Library.History().Where(h => byId.ContainsKey(h.Id)).Select(h => new HistoryItem(byId[h.Id], h.At)).ToList();
        OnPropertyChanged(nameof(HasHistory));
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
        Score();
    }

    partial void OnSearchTextChanged(string value)
    {
        if (DescribeMode) return;   // describe searches run on Enter
        Refresh();
    }

    partial void OnDescribeModeChanged(bool value)
    {
        _describeScores = null;
        ApplySort();
        Refresh();
    }

    partial void OnSelectedTypeChanged(TypeCount? value) => Refresh();
    partial void OnLoopFilterChanged(int value) => Refresh();
    partial void OnKeyFilterChanged(string value) => Refresh();
    partial void OnCompatibleKeysChanged(bool value) => Refresh();
    partial void OnBpmFromChanged(string value) => Refresh();
    partial void OnBpmToChanged(string value) => Refresh();
    partial void OnOnlyUnusedChanged(bool value) => Refresh();
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
            _describeScores = hits.ToDictionary(h => h.Sound.Id, h => h.Score);
            Message = $"Best matches for “{text}”, closest first";
        }
        ApplySort();
        Refresh();
    }

    [RelayCommand]
    void ClearFilters()
    {
        SearchText = "";
        _describeScores = null;
        SelectedType = TypeCounts.FirstOrDefault();
        LoopFilter = 0;
        KeyFilter = "Any key";
        CompatibleKeys = false;
        BpmFrom = BpmTo = "";
        OnlyUnused = OnlyFavourites = false;
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
        Similar = value is null ? [] : Org.Similar(value, 12);
        SelectedTypeLabel = value?.Type ?? "";
        KeyDetail = value?.Analysis is { KeyTonic: { } t } a
            ? $"{MusicTheory.NoteNames[t]} {(a.KeyMinor ? "minor" : "major")} · Camelot {MusicTheory.Camelot(t, a.KeyMinor)} · {a.KeySource}" +
              (a.KeySource == "detected" ? $" ({a.KeyConfidence:P0} sure)" : "")
            : "";
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
        Score();
        if (IsHistory) return;   // the History list doesn't jump about under the pointer while you play from it
        LoadHistory();
    }

    /// <summary>
    /// The views call this when you click a row or arrow onto one, not when the list moves the
    /// selection by itself (a sort, a filter, coming back to the tab). Autoplay previews don't
    /// count as plays.
    /// </summary>
    public void PlayPicked()
    {
        if (!AutoPlay || Selected is not { } s || _player.Playing == s.Path) return;
        try { Start(s, 0, count: false); }
        catch (Exception e) { Message = $"Can't play {s.Name}: {e.Message}"; }
    }

    /// <summary>
    /// A double-click: plays or stops the sound. With Autoplay on, the first click already started it,
    /// so it keeps playing and now counts as a play.
    /// </summary>
    [RelayCommand]
    void Audition()
    {
        if (!AutoPlay || Selected is not { } s) Play(null);
        else if (_player.Playing != s.Path) Play(s);
        else if (_preview == s.Path) { Count(s); _preview = null; }
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

    [RelayCommand]
    void Reveal(Sound? s)
    {
        if ((s ?? Selected) is { } x) Shell.Reveal(x.Path);
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
        Score();
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
            Notes = notes;
            NoteChords = chords;
            NotesMessage = notes.Count == 0 ? "No clear notes found (drums and noise usually have none)."
                : $"{notes.Count} notes" + (chords.Count > 0 ? $" · chords: {string.Join(" → ", chords.Select(c => c.Name).Take(16))}" : "");
        }
        catch (Exception e) { NotesMessage = $"Couldn't detect notes: {e.Message}"; }
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
    async Task RunAsync(string label, Action<IProgress<SoundProgress>, CancellationToken> job, Func<(string Title, string Detail)>? done = null)
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
            Reload();
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
        await RunAsync("Scanning sample folders", (p, ct) => count = Org.Scan(folders, p, ct),
                       () => ("Sample folders up to date", count == 0 ? "Nothing new" : $"Analysed {count:N0} new or changed sounds"));
        if (count > 0) Message = $"Analysed {count:N0} new or changed sounds.";
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

    [RelayCommand]
    void ShowProjects()
    {
        if (Selected is { UsedIn: > 0 } s)
        {
            var ids = _db.ProjectsUsingSample(s.Path).ToList();
            if (ids.Count == 0)
                ids = _db.SampleUsage().Where(u => u.Path.Split('\\', '/')[^1].Equals(Path.GetFileName(s.Path), StringComparison.OrdinalIgnoreCase))
                         .SelectMany(u => _db.ProjectsUsingSample(u.Path)).Distinct().ToList();
            _main.Projects.ShowOnly(ids, $"Projects using {s.Name}");
            _main.ShowProjects();
        }
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
    [ObservableProperty] double _adventure = 0.3;
    [ObservableProperty] bool _includeRarelyUsed;
    [ObservableProperty] IReadOnlyList<Suggestion> _suggestions = [];
    [ObservableProperty] IReadOnlyList<FolderUse> _folders = [];
    [ObservableProperty] string _intro = "";

    [RelayCommand]
    async Task Shuffle()
    {
        var o = new Core.Sounds.Rediscover.Options(Type == "Any type" ? null : Type, 30, Adventure, 0, IncludeRarelyUsed ? 1 : 0);
        var suggesting = Task.Run(() => Core.Sounds.Rediscover.Suggest(parent.Org, o));
        var folders = Task.Run(() => Core.Sounds.Rediscover.Folders(parent.Org).Take(40).ToList());
        Suggestions = await suggesting;
        Folders = await folders;
        var used = parent.Org.Sounds.Count(s => s.UsedIn > 0);
        Intro = used == 0
            ? "None of your sounds show up in scanned projects yet, so these are a varied pick. Star favourites or scan your projects to teach it your taste."
            : $"Learned from the {used:N0} sounds your projects use, plus your favourites and plays.";
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

    string? ExportKit()
    {
        var slots = Slots.Select(s => s.Slot).Where(s => s.Sound is not null).ToList();
        if (slots.Count == 0) { Status = "Roll a kit first."; return null; }
        return KitBuilder.Export(slots, Path.Combine(parent.ExportFolder(), "Kits"),
            $"Kit {DateTime.Now:yyyy-MM-dd HHmm} #{Seed}", new KitBuilder.ExportOptions(ElektronReady, Mono, Normalise));
    }

    /// <summary>
    /// The "Drag kit" handle: the kit's pads, exported with the chosen options, in pad order.
    /// Single pads dragged from the list carry their original sound instead.
    /// </summary>
    public DragPayload? MakeDrag(IReadOnlyList<object> items)
    {
        if (items.Count > 0 && items.All(i => i is KitSlotItem) && items.Count < Slots.Count)
            return parent.MakeDrag(items);
        if (ExportKit() is not { } folder) return null;
        var files = Directory.GetFiles(folder).Where(f => !f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        var pads = Slots.Where(s => s.Slot.Sound is not null).ToList();
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
