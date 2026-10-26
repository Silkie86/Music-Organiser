using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.ViewModels;

/// <summary>Every sample referenced by your projects, with missing ones first.</summary>
public sealed partial class SamplesViewModel : ObservableObject, IFileDragSource
{
    readonly LibraryDb _db;
    readonly MainViewModel _main;

    [ObservableProperty] IReadOnlyList<SampleUsage> _items = [];
    [ObservableProperty] IItemsView? _view;
    [ObservableProperty] string _searchText = "";
    [ObservableProperty] bool _onlyMissing;
    [ObservableProperty] SampleUsage? _selected;
    [ObservableProperty] string _summary = "";
    [ObservableProperty] IReadOnlyList<Match> _matches = [];
    [ObservableProperty] string _matchMessage = "";

    public SamplesViewModel(LibraryDb db, MainViewModel main)
    {
        _db = db;
        _main = main;
    }

    /// <summary>Samples that are where the project expects them, or a match found on your drives.</summary>
    public DragPayload? MakeDrag(IReadOnlyList<object> items)
    {
        var files = items.Select(i => i switch { SampleUsage s => s.Path, Match m => m.Candidate, _ => "" })
                         .Where(System.IO.File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) return null;
        return new DragPayload(files,
            files.Select(f => new DragCard(System.IO.Path.GetFileName(f), System.IO.Path.GetDirectoryName(f) ?? "", Glyphs.Audio)).ToList(),
            files.Count == 1 ? "1 sample" : $"{files.Count} samples");
    }

    // Counts the reloads, so one that started earlier and ends later doesn't show its older list.
    int _reloads;

    /// <summary>
    /// Reads the list with the database work done in the background, so the window keeps answering (it adds up every
    /// project's samples). The window starts it as it opens.
    /// </summary>
    public async Task ReloadAsync()
    {
        var reload = ++_reloads;
        var items = await UiWork.Run(_db.SampleUsage);
        if (reload == _reloads) Show(items);
    }

    void Show(IReadOnlyList<SampleUsage> items)
    {
        Items = items;
        View = Ui.Current.CreateView(Items as System.Collections.IList ?? Items.ToList());
        View.Filter = o => o is SampleUsage s &&
                           (!OnlyMissing || !s.Found) &&
                           (SearchText.Length == 0 || s.Path.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        var missing = Items.Count(s => !s.Found);
        Summary = $"{Items.Count} samples referenced" + (missing > 0 ? $", {missing} can't be found" : "");
    }

    partial void OnSearchTextChanged(string value) => View?.Refresh();
    partial void OnOnlyMissingChanged(bool value) => View?.Refresh();

    // Counts the searches for matches, so one still running for a sample picked earlier doesn't show its result.
    int _findRequest;

    partial void OnSelectedChanged(SampleUsage? value)
    {
        _findRequest++;
        Matches = [];
        MatchMessage = "";
    }

    /// <summary>
    /// Files in your sample folders that could be the missing one. Looked for in the background: it compares the name
    /// with every sound you have (357,000), which held the window up for seconds.
    /// </summary>
    [RelayCommand]
    async Task FindMatches()
    {
        if (Selected is not { } sample) return;
        var request = ++_findRequest;
        Matches = [];
        MatchMessage = "Looking on your drives…";
        var matches = await UiWork.Run(() => _db.SampleIndexCount() == 0 ? null : MatchFinder.ForSample(_db, sample.Path));
        if (request != _findRequest) return;
        if (matches is null)
        {
            MatchMessage = "Add your sample folders in Settings and index them to search your drives.";
            return;
        }
        Matches = matches;
        MatchMessage = Matches.Count == 0 ? "No similar files in your sample folders." : $"{Matches.Count} possible matches";
    }

    [RelayCommand]
    void SearchOnline()
    {
        if (Selected is not null) Shell.OpenUrl(MatchFinder.SampleSearchUrl(Selected.Path));
    }

    [RelayCommand]
    void RevealMatch(Match? m)
    {
        if (m is not null) Shell.Reveal(m.Candidate);
    }

    [RelayCommand]
    void ShowProjects()
    {
        if (Selected is not null)
            _main.Projects.ShowOnly(_db.ProjectsUsingSample(Selected.Path), $"Projects using {Selected.Path.Split('\\', '/')[^1]}");
    }
}
