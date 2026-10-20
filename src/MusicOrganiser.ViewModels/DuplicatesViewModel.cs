using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// The Duplicates tab on the Sounds page: sounds that are copies of each other, even renamed, converted or trimmed,
/// grouped with the best one to keep first. Tick groups (or Select all) to hide their extra copies from the app in
/// one go; Show again brings them back. It only changes what the app lists: no file is ever moved or deleted.
/// </summary>
public sealed partial class DuplicatesViewModel : ObservableObject
{
    readonly SoundsViewModel parent;
    public SoundsViewModel Parent => parent;

    /// <summary>Setting: look for copies of new sounds straight after each scan of the sample folders.</summary>
    public const string AfterScanKey = "duplicates_after_scan";

    public DuplicatesViewModel(SoundsViewModel parent)
    {
        this.parent = parent;
        _afterScan = parent.Db.GetSetting(AfterScanKey) != "0";
        parent.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SoundsViewModel.BusyPercent) or nameof(SoundsViewModel.IsBusy)) OnPropertyChanged(nameof(StepUnknown));
            if (e.PropertyName == nameof(SoundsViewModel.IsBusy))
            {
                // Hiding and showing wait while a scan or other job runs: it works on the sounds as they were.
                HideCopiesCommand.NotifyCanExecuteChanged();
                UndoCommand.NotifyCanExecuteChanged();
                UnhideCommand.NotifyCanExecuteChanged();
                ShowAllHiddenCommand.NotifyCanExecuteChanged();
            }
        };
        RefreshUndo();
    }

    /// <summary>While a step of the job doesn't know how long it'll take, its bar just shows activity.</summary>
    public bool StepUnknown => parent.IsBusy && parent.BusyPercent <= 0;

    public const string AllKinds = "All", SameOnly = "Same sound", SimilarOnly = "Very similar", HiddenOnly = "Hidden";
    public static IReadOnlyList<string> KindChoices { get; } = [AllKinds, SameOnly, SimilarOnly, HiddenOnly];

    [ObservableProperty] string _kind = AllKinds;
    [ObservableProperty] string _searchText = "";
    [ObservableProperty] IReadOnlyList<DuplicateGroupItem> _groups = [];
    [ObservableProperty] string _summary = "";
    [ObservableProperty] string _shownText = "";
    /// <summary>True once every sound has been looked at, so an empty list means there are no copies.</summary>
    [ObservableProperty] bool _hasLooked;
    [ObservableProperty] bool _isLoading;
    [ObservableProperty] bool _afterScan;
    partial void OnAfterScanChanged(bool value) => parent.Db.SetSetting(AfterScanKey, value ? "1" : "0");

    // Batch
    [ObservableProperty] string _selectionText = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(HideCopiesCommand))]
    bool _hasSelection;
    [ObservableProperty] string _undoText = "";
    [ObservableProperty] bool _canUndo;
    [ObservableProperty] string _batchMessage = "";
    public bool HasBatchMessage => BatchMessage.Length > 0;
    partial void OnBatchMessageChanged(string value) => OnPropertyChanged(nameof(HasBatchMessage));

    public bool IsEmpty => Groups.Count == 0;
    /// <summary>The list is empty only because what it would show is hidden: then undo is offered right there.</summary>
    [ObservableProperty] bool _onlyHiddenLeft;
    /// <summary>Hidden copies no longer in any group (their copy went, or now counts as only very similar).</summary>
    [ObservableProperty] int _strayHidden;
    public string StrayHiddenText => StrayHidden == 1 ? "1 hidden copy is no longer in any group. Show it again"
        : $"{StrayHidden:N0} hidden copies are no longer in any group. Show them again";
    public bool HasStrayHidden => StrayHidden > 0;
    partial void OnStrayHiddenChanged(int value) { OnPropertyChanged(nameof(StrayHiddenText)); OnPropertyChanged(nameof(HasStrayHidden)); }
    partial void OnOnlyHiddenLeftChanged(bool value) => OnPropertyChanged(nameof(EmptyText));
    partial void OnGroupsChanged(IReadOnlyList<DuplicateGroupItem> value) => Changed();
    partial void OnHasLookedChanged(bool value) => Changed();
    partial void OnIsLoadingChanged(bool value) => Changed();
    void Changed()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>What the empty tab says: nothing looked for yet, or nothing found, or nothing matching the filters.</summary>
    public string EmptyTitle =>
        IsLoading ? "Reading what was found…"
        : _all.Count > 0 ? "No copies match"
        : HasLooked ? "No duplicates found"
        : "Find duplicate sounds";
    public string EmptyText =>
        IsLoading ? ""
        : _all.Count > 0 ? (OnlyHiddenLeft ? "Everything that matches is hidden. Show the last batch again below, or pick Hidden to bring back single copies." : "Try another filter or search.")
        : HasLooked ? "None of your sounds is a copy of another. New sounds are checked after each scan, or when you press Find duplicates."
        : "Finds copies of the same sound across your sample folders, even renamed, converted to another format or trimmed. "
          + "Play each copy to compare, then tick groups to hide the copies that are the same sound from the app (very similar ones are only listed). "
          + "Your files are never moved or deleted. "
          + "The first time it listens to every sound, which takes a while; after that only new ones.";

    IReadOnlyList<DuplicateGroup> _all = [];
    List<long> _strays = [];
    bool _loaded;
    // Ticks and keep choices, by group (the id of the sound first suggested to keep), so they survive filtering.
    readonly HashSet<long> _ticked = [];
    readonly Dictionary<long, long> _keep = [];

    partial void OnKindChanged(string value) => _ = Filter();
    partial void OnSearchTextChanged(string value) => _ = Filter();

    /// <summary>Shows what was found last time, the first time the tab opens.</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _ = Load();
    }

    async Task Load(IReadOnlyList<DuplicateGroup>? found = null)
    {
        IsLoading = true;
        try
        {
            var org = parent.Org;
            var (all, counts, toCheck, hiddenIds) = await Task.Run(() =>
                (found ?? DuplicateFinder.Groups(org), org.Library.PrintCounts(), org.Library.SamenessToCheck().Count, org.Library.HiddenIds()));
            _all = all;
            var inGroups = all.SelectMany(g => g.Members).Where(m => m.Sound.Hidden).Select(m => m.Sound.Id).ToHashSet();
            _strays = hiddenIds.Where(id => !inGroups.Contains(id)).ToList();
            StrayHidden = _strays.Count;
            var keys = all.Select(g => g.Members[0].Sound.Id).ToHashSet();
            _ticked.IntersectWith(keys);
            foreach (var k in _keep.Keys.Where(k => !keys.Contains(k)).ToList()) _keep.Remove(k);
            HasLooked = counts.Total > 0 && counts.Printed >= counts.Total;
            Summarise(counts);
            if (toCheck > 0) Summary += $" Press Find duplicates to listen to {toCheck:N0} matches again: whether each is the same sound, only very similar, or not a copy at all.";
            await Filter();
        }
        catch (Exception e) { Summary = $"Couldn't read the duplicates: {e.Message}"; }
        finally { IsLoading = false; }
    }

    void Summarise((int Printed, int Total) counts)
    {
        if (counts.Total == 0) { Summary = "Scan your sample folders first, then look for copies here."; return; }
        if (counts.Printed == 0)
        {
            Summary = $"Look through your {counts.Total:N0} sounds for copies of the same sound, even renamed, converted to another format, or trimmed.";
            return;
        }
        var left = counts.Total - counts.Printed;
        var same = _all.Sum(g => g.SameCopies);
        var similar = _all.Sum(g => g.Members.Count(m => !m.Keep && !m.Same));
        var hidden = _all.Sum(g => g.Members.Count(m => m.Sound.Hidden));
        Summary = _all.Count == 0
            ? $"No copies found among {counts.Printed:N0} sounds."
            : $"{_all.Count:N0} sounds have copies: {same:N0} the same sound, {similar:N0} only very similar"
              + (hidden > 0 ? $", {hidden:N0} hidden." : ".");
        if (left > 0) Summary += $" {left:N0} sounds haven't been looked at yet.";
    }

    int _filtering;

    /// <summary>Rebuilds the list for the chips and search, off the UI thread (a big library can have thousands of groups).</summary>
    async Task Filter()
    {
        var run = ++_filtering;
        var (all, kind, words) = (_all, Kind, SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        bool Matches(DuplicateGroup g) =>
            words.Length == 0 || g.Members.Any(m => words.All(w => m.Sound.Path.Contains(w, StringComparison.OrdinalIgnoreCase)));
        var (shown, onlyHidden) = await Task.Run(() =>
        {
            var list = all.Where(g => kind switch
                {
                    SameOnly => g.SameCopies > 0,
                    SimilarOnly => g.HasSimilar,
                    HiddenOnly => g.Members.Any(m => m.Sound.Hidden),
                    _ => true,
                })
                .Where(Matches)
                .Select(g => new DuplicateGroupItem(g, this))
                .ToList();
            // Nothing to show, yet groups the search matches have hidden copies: the hiding emptied the list.
            return (list, list.Count == 0 && all.Any(g => Matches(g) && g.Members.Any(m => m.Sound.Hidden)));
        });
        if (run != _filtering) return;   // a newer filter is on its way
        OnlyHiddenLeft = onlyHidden;
        Groups = shown;
        ShownText = shown.Count == all.Count ? "" : $"Showing {shown.Count:N0} of {all.Count:N0}";
        UpdateSelection();
    }

    // ---- ticks and keep choices --------------------------------------------------------------

    internal bool IsTicked(long group) => _ticked.Contains(group);

    internal void SetTicked(long group, bool on)
    {
        if (on) _ticked.Add(group); else _ticked.Remove(group);
        UpdateSelection();
    }

    internal long KeepOf(DuplicateGroup g) => _keep.TryGetValue(g.Members[0].Sound.Id, out var id) ? id : g.Members[0].Sound.Id;

    internal void SetKeep(DuplicateGroup g, long soundId)
    {
        if (soundId == g.Members[0].Sound.Id) _keep.Remove(g.Members[0].Sound.Id);
        else _keep[g.Members[0].Sound.Id] = soundId;
        UpdateSelection();
    }

    /// <summary>The ticked groups' copies to hide: the ones still showing that are the same sound as one that stays.</summary>
    List<Sound> TickedExtras() => _all.Where(g => _ticked.Contains(g.Members[0].Sound.Id)).SelectMany(SameExtras).ToList();

    internal IEnumerable<Sound> SameExtras(DuplicateGroup g)
    {
        // Within each set of copies that are the same sound, one stays: the one you keep if it's in the set, else
        // the set's lead (the one that's only very like the one to keep). Very similar ones are never hidden.
        var keep = KeepOf(g);
        foreach (var set in g.Members.GroupBy(m => m.SameSet).Where(s => s.Count() > 1))
        {
            var stays = set.Any(m => m.Sound.Id == keep) ? keep : (set.FirstOrDefault(m => !m.Same) ?? set.First()).Sound.Id;
            foreach (var m in set)
                if (m.Sound.Id != stays && !m.Sound.Hidden) yield return m.Sound;
        }
    }

    void UpdateSelection()
    {
        var extras = TickedExtras();
        HasSelection = extras.Count > 0;
        SelectionText = _ticked.Count == 0
            ? "Tick groups, or Select all, to hide the copies that are the same sound from the app together. Very similar ones stay. Your files stay where they are."
            : $"{_ticked.Count:N0} groups ticked: {extras.Count:N0} copies that are the same sound to hide.";
    }

    [RelayCommand]
    void SelectAll()
    {
        foreach (var g in Groups.Where(g => g.CanTick)) { _ticked.Add(g.Key); g.Refresh(); }
        UpdateSelection();
    }

    [RelayCommand]
    void SelectNone()
    {
        _ticked.Clear();
        foreach (var g in Groups) g.Refresh();
        UpdateSelection();
    }

    // ---- jobs ---------------------------------------------------------------------------------

    [RelayCommand]
    Task Find() => FindAsync();

    /// <summary>Looks for copies of new and changed sounds; the Sounds page calls this after a scan when <see cref="AfterScan"/> is on.</summary>
    public async Task FindAsync()
    {
        IReadOnlyList<DuplicateGroup>? found = null;
        await parent.RunAsync("Finding duplicate sounds", (p, ct) =>
        {
            DuplicateFinder.Update(parent.Org, p, ct);
            found = DuplicateFinder.Groups(parent.Org);   // here, off the UI thread: on a big library this takes a while
        }, () => ("Duplicate sounds checked", found is not { Count: > 0 } f
            ? "No copies found"
            : $"{f.Count:N0} sounds have copies ({f.Sum(g => g.Members.Count - 1):N0} extra files)"),
            always: false);   // nothing in the library itself changes, so the Sounds lists aren't rebuilt
        _loaded = true;
        await Load(found);
    }

    /// <summary>Hides the ticked groups' extra copies from every list in the app. The files themselves aren't touched.</summary>
    bool CanHide() => HasSelection && !parent.IsBusy;
    bool NotBusy => !parent.IsBusy;

    [RelayCommand(CanExecute = nameof(CanHide))]
    async Task HideCopies()
    {
        var extras = TickedExtras();
        if (extras.Count == 0) return;
        var ids = extras.Select(s => s.Id).ToList();
        await Task.Run(() => parent.Org.Library.Hide(ids, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffffff")));
        BatchMessage = $"Hid {extras.Count:N0} extra copies from the app. The files are still in your folders.";
        // This card's Show again brings back this batch, even if another has been hidden since.
        parent.Activities.Done($"Hid {extras.Count:N0} extra copies", "Only in the app: your files are untouched", "Show again", () => _ = Show(ids));
        _ticked.Clear();
        await AfterChange(ids, hidden: true);
    }

    /// <summary>Shows the last batch of hidden copies again.</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    async Task Undo()
    {
        if (parent.Org.Library.LastHidden() is not { } last) return;
        await Show(last.Ids);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    Task Unhide(DuplicateMemberItem? m) => m is null ? Task.CompletedTask : Show([m.Sound.Id]);

    /// <summary>Shows the hidden copies that aren't in any group any more (nothing else would bring them back).</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    Task ShowAllHidden() => Show(_strays.ToList());

    async Task Show(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0 || parent.IsBusy) return;
        await Task.Run(() => parent.Org.Library.Unhide(ids));
        BatchMessage = ids.Count == 1 ? "Showing it again." : $"Showing {ids.Count:N0} copies again.";
        await AfterChange(ids, hidden: false);
    }

    async Task AfterChange(IReadOnlyList<long> ids, bool hidden)
    {
        // Only the hidden flags change: the library isn't read again, and the Sounds lists are rebuilt off the UI thread.
        parent.Org.SetHidden(ids, hidden);
        RefreshUndo();
        await Task.WhenAll(parent.ReloadAsync(), Load());
    }

    void RefreshUndo()
    {
        var last = parent.Org.Library.LastHidden();
        CanUndo = last is not null;
        OnPropertyChanged(nameof(EmptyText));
        UndoText = last is { } l ? $"Show the {l.Ids.Count:N0} copies hidden at {l.At:HH:mm, d MMM} again" : "";
    }

    [RelayCommand]
    void Play(DuplicateMemberItem? m)
    {
        if (m is not null) parent.PlayCommand.Execute(m.Sound);
    }

    [RelayCommand]
    void Reveal(DuplicateMemberItem? m)
    {
        if (m is not null) Shell.Reveal(m.Sound.Path);
    }

    [RelayCommand]
    void KeepThis(DuplicateMemberItem? m) => m?.Owner.Keep(m);

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };
}

/// <summary>A group of copies as shown: the one to keep, then its copies, with a tick for batch hiding.</summary>
public sealed partial class DuplicateGroupItem : ObservableObject
{
    readonly DuplicatesViewModel _owner;

    public DuplicateGroupItem(DuplicateGroup group, DuplicatesViewModel owner)
    {
        Group = group;
        _owner = owner;
        Members = group.Members.Select(m => new DuplicateMemberItem(m, this)).ToList();
        var keep = owner.KeepOf(group);
        foreach (var m in Members) m.IsKeep = m.Sound.Id == keep;
    }

    public DuplicateGroup Group { get; }
    internal long Key => Group.Members[0].Sound.Id;
    public string Title => Group.Members[0].Sound.Name;
    public string Type => Group.Members[0].Sound.Type;
    public IReadOnlyList<DuplicateMemberItem> Members { get; }

    public bool IsTicked
    {
        get => _owner.IsTicked(Key);
        set { _owner.SetTicked(Key, value); OnPropertyChanged(); }
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsTicked));
        OnPropertyChanged(nameof(CanTick));
    }

    /// <summary>Only groups with copies that are the same sound (and still showing) can be ticked for hiding.</summary>
    public bool CanTick => _owner.SameExtras(Group).Any();

    internal void Keep(DuplicateMemberItem keep)
    {
        _owner.SetKeep(Group, keep.Sound.Id);
        foreach (var m in Members) m.IsKeep = m == keep;
        if (!CanTick && IsTicked) IsTicked = false;
        Refresh();
    }

    public string Detail
    {
        get
        {
            var same = Group.SameCopies;
            var similar = Group.Members.Count - 1 - same;
            var hidden = Group.Members.Count(m => m.Sound.Hidden);
            var bits = new List<string>();
            if (same > 0) bits.Add(same == 1 ? "1 the same sound" : $"{same} the same sound");
            if (similar > 0) bits.Add($"{similar} very similar");
            if (hidden > 0) bits.Add($"{hidden} hidden");
            else if (Group.SpareBytes > 0) bits.Add($"{DuplicatesViewModel.Size(Group.SpareBytes)} in copies");
            return string.Join(" · ", bits);
        }
    }
}

/// <summary>One file in a group of copies.</summary>
public sealed partial class DuplicateMemberItem(DuplicateMember member, DuplicateGroupItem owner) : ObservableObject
{
    public DuplicateGroupItem Owner => owner;
    public Sound Sound => member.Sound;
    public bool IsHidden => member.Sound.Hidden;

    /// <summary>The one this group keeps: the suggested one, unless you picked another.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Badge), nameof(IsExact), nameof(IsConverted), nameof(IsSimilar), nameof(CanKeep), nameof(Why))]
    bool _isKeep;

    /// <summary>"Keep this one" is offered on copies still showing.</summary>
    public bool CanKeep => !IsKeep && !IsHidden;
    /// <summary>The same sound as the suggested one to keep (so it can be hidden), not just very like it.</summary>
    public bool IsSame => member.Same;
    public bool IsExact => !IsKeep && !IsHidden && member.Same && member.Kind == CopyKind.Exact;
    public bool IsConverted => !IsKeep && !IsHidden && member.Same && member.Kind != CopyKind.Exact;
    public bool IsSimilar => !IsKeep && !IsHidden && !member.Same;
    public string Badge => IsKeep ? "Keep" : IsHidden ? "Hidden" : member.Kind switch
    {
        null => "Suggested",
        CopyKind.Exact when member.Same => "Exact copy",
        _ when member.Same => "Same sound",
        CopyKind.Trimmed => "Trimmed",
        _ => "Very similar",
    };
    public string Name => System.IO.Path.GetFileName(member.Sound.Path);
    public string Folder => System.IO.Path.GetDirectoryName(member.Sound.Path) ?? "";
    public string Why => IsKeep && !member.Keep ? "Your pick to keep" : member.Why;
    public string Facts
    {
        get
        {
            var bits = new List<string> { member.Format };
            if (member.Sound.Analysis is { } a)
            {
                bits.Add($"{a.SampleRate / 1000.0:0.#} kHz");
                bits.Add(a.ChannelCount == 1 ? "mono" : "stereo");
                bits.Add(a.Seconds < 10 ? $"{a.Seconds:0.00} s" : $"{a.Seconds:0.0} s");
            }
            bits.Add(DuplicatesViewModel.Size(member.Size));
            return string.Join(" · ", bits);
        }
    }
    public string UsedText => member.Sound.UsedIn switch
    {
        0 => "",
        1 => "In 1 project",
        var n => $"In {n} projects",
    };
}
