using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// The Duplicates tab on the Sounds page: sounds that are copies of each other, even renamed, converted or trimmed,
/// grouped with the best one to keep first. It lists and plays them; it never moves or deletes a file.
/// </summary>
public sealed partial class DuplicatesViewModel : ObservableObject
{
    readonly SoundsViewModel parent;
    public SoundsViewModel Parent => parent;

    public DuplicatesViewModel(SoundsViewModel parent)
    {
        this.parent = parent;
        parent.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SoundsViewModel.BusyPercent) or nameof(SoundsViewModel.IsBusy)) OnPropertyChanged(nameof(StepUnknown));
        };
    }

    /// <summary>While a step of the job doesn't know how long it'll take, its bar just shows activity.</summary>
    public bool StepUnknown => parent.IsBusy && parent.BusyPercent <= 0;

    public const string AllKinds = "All", ExactOnly = "Exact copies", ConvertedOnly = "Converted", TrimmedOnly = "Trimmed";
    public static IReadOnlyList<string> KindChoices { get; } = [AllKinds, ExactOnly, ConvertedOnly, TrimmedOnly];

    [ObservableProperty] string _kind = AllKinds;
    [ObservableProperty] string _searchText = "";
    [ObservableProperty] IReadOnlyList<DuplicateGroupItem> _groups = [];
    [ObservableProperty] string _summary = "";
    [ObservableProperty] string _shownText = "";
    /// <summary>True once every sound has been looked at, so an empty list means there are no copies.</summary>
    [ObservableProperty] bool _hasLooked;
    [ObservableProperty] bool _isLoading;

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
        : HasLooked ? "None of your sounds is a copy of another. New sounds are checked each time you press Find duplicates."
        : "Finds copies of the same sound across your sample folders, even renamed, converted to another format or trimmed. "
          + "You can play each copy to compare. Nothing is moved or deleted. The first time it listens to every sound, which takes a while; after that only new ones.";

    IReadOnlyList<DuplicateGroup> _all = [];
    bool _loaded;

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
            Summary = $"Look through your {counts.Total:N0} sounds for copies of the same sound, even renamed, converted to another format, or trimmed. Nothing is moved or deleted.";
            return;
        }
        var left = counts.Total - counts.Printed;
        var copies = _all.Sum(g => g.Members.Count - 1);
        var spare = _all.Sum(g => g.SpareBytes);
        Summary = _all.Count == 0
            ? $"No copies found among {counts.Printed:N0} sounds."
            : $"{_all.Count:N0} sounds have copies: {copies:N0} extra files, {Size(spare)} in exact and converted copies.";
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
                _ => true,
            })
            .Where(g => words.Length == 0 || g.Members.Any(m => words.All(w => m.Sound.Path.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Select(g => new DuplicateGroupItem(g))
            .ToList();
        Groups = shown;
        ShownText = shown.Count == _all.Count ? "" : $"Showing {shown.Count:N0} of {_all.Count:N0}";
    }

    [RelayCommand]
    async Task Find()
    {
        var printed = 0;
        await parent.RunAsync("Finding duplicate sounds", (p, ct) => printed = DuplicateFinder.Update(parent.Org, p, ct), () =>
        {
            var groups = DuplicateFinder.Groups(parent.Org);
            return ("Duplicate sounds checked", groups.Count == 0
                ? "No copies found"
                : $"{groups.Count:N0} sounds have copies ({groups.Sum(g => g.Members.Count - 1):N0} extra files)");
        });
        _loaded = true;
        await Load();
    }

    [RelayCommand]
    void Play(DuplicateMemberItem? m)
    {
        if (m is not null) parent.PlayCommand.Execute(m.Sound);
    }

    [RelayCommand]
    void Reveal(DuplicateMemberItem? m)
    {
        if (m is not null) Core.Shell.Reveal(m.Sound.Path);
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };
}

/// <summary>A group of copies as shown: the one to keep, then its copies.</summary>
public sealed class DuplicateGroupItem(DuplicateGroup group)
{
    public DuplicateGroup Group => group;
    public string Title => group.Members[0].Sound.Name;
    public string Type => group.Members[0].Sound.Type;
    public IReadOnlyList<DuplicateMemberItem> Members { get; } = group.Members.Select(m => new DuplicateMemberItem(m)).ToList();
    public string Detail
    {
        get
        {
            var n = group.Members.Count - 1;
            var text = n == 1 ? "1 copy" : $"{n} copies";
            return group.SpareBytes > 0 ? $"{text} · {DuplicatesViewModel.Size(group.SpareBytes)} in copies" : text;
        }
    }
}

/// <summary>One file in a group of copies.</summary>
public sealed class DuplicateMemberItem(DuplicateMember member)
{
    public Sound Sound => member.Sound;
    public bool Keep => member.Keep;
    public bool IsExact => member.Kind == CopyKind.Exact;
    public bool IsConverted => member.Kind == CopyKind.Converted;
    public bool IsTrimmed => member.Kind == CopyKind.Trimmed;
    public string Badge => member.Kind switch
    {
        null => "Keep",
        CopyKind.Exact => "Exact copy",
        CopyKind.Converted => "Converted",
        _ => "Trimmed",
    };
    public string Name => System.IO.Path.GetFileName(member.Sound.Path);
    public string Folder => System.IO.Path.GetDirectoryName(member.Sound.Path) ?? "";
    public string Why => member.Why;
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
