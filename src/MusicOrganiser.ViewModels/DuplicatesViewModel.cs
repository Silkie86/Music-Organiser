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
        };
        RefreshUndo();
    }

    /// <summary>While a step of the job doesn't know how long it'll take, its bar just shows activity.</summary>
    public bool StepUnknown => parent.IsBusy && parent.BusyPercent <= 0;

    public const string AllKinds = "All", ExactOnly = "Exact copies", ConvertedOnly = "Converted", TrimmedOnly = "Trimmed", HiddenOnly = "Hidden";
    public static IReadOnlyList<string> KindChoices { get; } = [AllKinds, ExactOnly, ConvertedOnly, TrimmedOnly, HiddenOnly];

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
        : _all.Count > 0 ? "Try another filter or search."
        : HasLooked ? "None of your sounds is a copy of another. New sounds are checked after each scan, or when you press Find duplicates."
        : "Finds copies of the same sound across your sample folders, even renamed, converted to another format or trimmed. "
          + "Play each copy to compare, then tick groups to hide their extra copies from the app. Your files are never moved or deleted. "
          + "The first time it listens to every sound, which takes a while; after that only new ones.";

    IReadOnlyList<DuplicateGroup> _all = [];
    bool _loaded;
    // Ticks and keep choices, by group (the id of the sound first suggested to keep), so they survive filtering.
    readonly HashSet<long> _ticked = [];
    readonly Dictionary<long, long> _keep = [];

    partial void OnKindChanged(string value) => Filter();
    partial void OnSearchTextChanged(string value) => Filter();

    /// <summary>Shows what was found last time, the first time the tab opens.</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _ = Load();
    }

    async Task Load()
    {
        IsLoading = true;
        try
        {
            var org = parent.Org;
            var (all, counts) = await Task.Run(() => (DuplicateFinder.Groups(org), org.Library.PrintCounts()));
            _all = all;
            var keys = all.Select(g => g.Members[0].Sound.Id).ToHashSet();
            _ticked.IntersectWith(keys);
            foreach (var k in _keep.Keys.Where(k => !keys.Contains(k)).ToList()) _keep.Remove(k);
            HasLooked = counts.Total > 0 && counts.Printed >= counts.Total;
            Summarise(counts);
            Filter();
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
        var copies = _all.Sum(g => g.Members.Count - 1);
        var hidden = _all.Sum(g => g.Members.Count(m => m.Sound.Hidden));
        Summary = _all.Count == 0
            ? $"No copies found among {counts.Printed:N0} sounds."
            : $"{_all.Count:N0} sounds have copies: {copies:N0} extra files" + (hidden > 0 ? $", {hidden:N0} of them hidden." : ".");
        if (left > 0) Summary += $" {left:N0} sounds haven't been looked at yet.";
    }

    void Filter()
    {
        var words = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = _all.Where(g => Kind switch
            {
                ExactOnly => g.Members.Skip(1).Any(m => m.Kind == CopyKind.Exact),
                ConvertedOnly => g.Members.Skip(1).Any(m => m.Kind == CopyKind.Converted),
                TrimmedOnly => g.Members.Skip(1).Any(m => m.Kind == CopyKind.Trimmed),
                HiddenOnly => g.Members.Any(m => m.Sound.Hidden),
                _ => true,
            })
            .Where(g => words.Length == 0 || g.Members.Any(m => words.All(w => m.Sound.Path.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Select(g => new DuplicateGroupItem(g, this))
            .ToList();
        Groups = shown;
        ShownText = shown.Count == _all.Count ? "" : $"Showing {shown.Count:N0} of {_all.Count:N0}";
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

    /// <summary>The ticked groups' extra copies still showing: every file but the one to keep.</summary>
    List<Sound> TickedExtras() => _all.Where(g => _ticked.Contains(g.Members[0].Sound.Id))
        .SelectMany(g => { var keep = KeepOf(g); return g.Members.Where(m => m.Sound.Id != keep && !m.Sound.Hidden).Select(m => m.Sound); })
        .ToList();

    void UpdateSelection()
    {
        var extras = TickedExtras();
        HasSelection = extras.Count > 0;
        SelectionText = _ticked.Count == 0
            ? "Tick groups, or Select all, to hide their extra copies from the app together. Your files stay where they are."
            : $"{_ticked.Count:N0} groups ticked: {extras.Count:N0} extra copies to hide.";
    }

    [RelayCommand]
    void SelectAll()
    {
        foreach (var g in Groups) { _ticked.Add(g.Key); g.Refresh(); }
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
        await parent.RunAsync("Finding duplicate sounds", (p, ct) => DuplicateFinder.Update(parent.Org, p, ct), () =>
        {
            var groups = DuplicateFinder.Groups(parent.Org);
            return ("Duplicate sounds checked", groups.Count == 0
                ? "No copies found"
                : $"{groups.Count:N0} sounds have copies ({groups.Sum(g => g.Members.Count - 1):N0} extra files)");
        });
        _loaded = true;
        await Load();
    }

    /// <summary>Hides the ticked groups' extra copies from every list in the app. The files themselves aren't touched.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    async Task HideCopies()
    {
        var extras = TickedExtras();
        if (extras.Count == 0) return;
        await Task.Run(() => parent.Org.Library.Hide(extras.Select(s => s.Id), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        BatchMessage = $"Hid {extras.Count:N0} extra copies from the app. The files are still in your folders.";
        parent.Activities.Done($"Hid {extras.Count:N0} extra copies", "Only in the app: your files are untouched", "Show again", () => UndoCommand.Execute(null));
        _ticked.Clear();
        await AfterChange();
    }

    /// <summary>Shows the last batch of hidden copies again.</summary>
    [RelayCommand]
    async Task Undo()
    {
        if (parent.Org.Library.LastHidden() is not { } last) return;
        await Task.Run(() => parent.Org.Library.Unhide(last.Ids));
        BatchMessage = $"Showing {last.Ids.Count:N0} copies again.";
        await AfterChange();
    }

    [RelayCommand]
    async Task Unhide(DuplicateMemberItem? m)
    {
        if (m is null) return;
        await Task.Run(() => parent.Org.Library.Unhide([m.Sound.Id]));
        await AfterChange();
    }

    async Task AfterChange()
    {
        await Task.Run(parent.Org.Reload);
        parent.Reload();
        RefreshUndo();
        await Load();
    }

    void RefreshUndo()
    {
        var last = parent.Org.Library.LastHidden();
        CanUndo = last is not null;
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

    internal void Refresh() => OnPropertyChanged(nameof(IsTicked));

    internal void Keep(DuplicateMemberItem keep)
    {
        _owner.SetKeep(Group, keep.Sound.Id);
        foreach (var m in Members) m.IsKeep = m == keep;
    }

    public string Detail
    {
        get
        {
            var n = Group.Members.Count - 1;
            var hidden = Group.Members.Count(m => m.Sound.Hidden);
            var text = n == 1 ? "1 copy" : $"{n} copies";
            if (hidden > 0) return $"{text} · {hidden} hidden";
            return Group.SpareBytes > 0 ? $"{text} · {DuplicatesViewModel.Size(Group.SpareBytes)} in copies" : text;
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
    [NotifyPropertyChangedFor(nameof(Badge), nameof(IsExact), nameof(IsConverted), nameof(IsTrimmed), nameof(CanKeep), nameof(Why))]
    bool _isKeep;

    /// <summary>"Keep this one" is offered on copies still showing.</summary>
    public bool CanKeep => !IsKeep && !IsHidden;
    public bool IsExact => !IsKeep && !IsHidden && member.Kind == CopyKind.Exact;
    public bool IsConverted => !IsKeep && !IsHidden && member.Kind == CopyKind.Converted;
    public bool IsTrimmed => !IsKeep && !IsHidden && member.Kind == CopyKind.Trimmed;
    public string Badge => IsKeep ? "Keep" : IsHidden ? "Hidden" : member.Kind switch
    {
        null => "Suggested",
        CopyKind.Exact => "Exact copy",
        CopyKind.Converted => "Converted",
        _ => "Trimmed",
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
