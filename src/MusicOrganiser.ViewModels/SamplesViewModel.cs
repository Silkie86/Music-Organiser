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
        Reload();
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

    public void Reload() => Show(_db.SampleUsage());

    /// <summary>Reload with the database work done in the background, so the window keeps answering.</summary>
    public async Task ReloadAsync() => Show(await UiWork.Run(_db.SampleUsage));

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

    partial void OnSelectedChanged(SampleUsage? value)
    {
        Matches = [];
        MatchMessage = "";
    }

    [RelayCommand]
    async Task FindMatches()
    {
        if (Selected is not { } sample) return;
        if (_db.SampleIndexCount() == 0)
        {
            MatchMessage = "Add your sample folders in Settings and index them to search your drives.";
            return;
        }
        MatchMessage = "Looking through your sample folders…";
        // Off the window's thread: every file in the sample folders is compared with the name.
        var matches = await UiWork.Run(() => MatchFinder.ForSample(_db, sample.Path));
        if (Selected != sample) return;   // another sample picked meanwhile
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
    async Task ShowProjects()
    {
        if (Selected is not { } sample) return;
        var ids = await UiWork.Run(() => _db.ProjectsUsingSample(sample.Path));   // off the window's thread
        _main.Projects.ShowOnly(ids, $"Projects using {sample.Path.Split('\\', '/')[^1]}");
    }
}
