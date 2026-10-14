using CommunityToolkit.Mvvm.ComponentModel;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.ViewModels;

/// <summary>A row in the project list. Edits to your own fields save straight to the library.</summary>
public sealed partial class ProjectItem : ObservableObject
{
    readonly LibraryDb _db;
    readonly ProjectSummary _p;
    readonly string? _song;
    bool _loading = true;

    public ProjectItem(LibraryDb db, ProjectSummary p, string? song = null, IReadOnlyList<ProjectSummary>? olderSaves = null)
    {
        _db = db;
        _p = p;
        _song = song;
        OlderSaves = olderSaves ?? [];
        var saves = OlderSaves.Prepend(p).ToList();
        Made = SongVersions.FirstMade(saves);
        Runs = SongVersions.Runs(saves);
        _status = p.Status;
        _rating = p.Rating;
        _favourite = p.Favourite;
        _musicalKey = p.MusicalKey;
        _notes = p.Notes;
        _tags = p.Tags;
        _loading = false;
    }

    public ProjectSummary Summary => _p;
    public long Id => _p.Id;
    /// <summary>The song name; numbered saves (Song_2, Song_3 …) share one row named after the song.</summary>
    public string Name => _song ?? _p.Name;
    /// <summary>The newest save's file name, e.g. "Didums_77".</summary>
    public string FileName => _p.Name;
    public IReadOnlyList<ProjectSummary> OlderSaves { get; }
    public int SaveCount => OlderSaves.Count + 1;
    public string Path => _p.Path;
    public string Daw => _p.Daw;
    public string Folder => _p.Folder;
    public string FolderName => System.IO.Path.GetFileName(_p.Folder);
    public DateTime Modified => _p.Modified;
    /// <summary>
    /// When the song was first made: its oldest save, or the date its file records, whichever is earlier. The list is
    /// grouped and sorted by this, so re-saving an old song in a newer DAW leaves it where it was.
    /// </summary>
    public DateTime Made { get; }
    /// <summary>The song's working periods, oldest first; each one's last save can be opened (see <see cref="SongVersions.Runs"/>).</summary>
    public IReadOnlyList<SaveRun> Runs { get; }
    public bool HasRuns => Runs.Count > 1;
    /// <summary>The newest run, when it's a rework: quite different from how the song first ended up.</summary>
    public SaveRun? Rework => Runs.Count > 1 && Runs[^1].Reworked ? Runs[^1] : null;
    public int Year => _p.Modified.Year;
    public string MonthGroup => _p.Modified.ToString("yyyy · MMMM");
    /// <summary>The list's time heading, by when the song was first made: "This week", "This month", "Earlier in 2026", then one per year.</summary>
    public string WhenGroup => WhenGroupFor(Made, DateTime.Now);
    /// <summary>When it was first made: "3 Aug" this year, "3 Aug 2019" before.</summary>
    public string ShortDate => Made.Year == DateTime.Now.Year ? Made.ToString("d MMM") : Made.ToString("d MMM yyyy");
    /// <summary>The date's tooltip, saying which date it is and when it was last saved.</summary>
    public string DateTip => Made.Date == Modified.Date
        ? $"Made {Made:d MMM yyyy}"
        : $"First made {Made:d MMM yyyy}. Last saved {Modified:d MMM yyyy}.";
    /// <summary>The row's second line: "FL Studio · 140 BPM · G minor · 77 saves · reworked 2024", leaving out what isn't known.</summary>
    public string DetailsLine => string.Join(" · ", new[]
    {
        Daw,
        Tempo is { } t ? $"{t:0.##} BPM" : null,
        Key.Length == 0 ? null : Key,
        SaveCount > 1 ? $"{SaveCount} saves" : null,
        Rework is { } r ? $"reworked {r.End.Year}" : null,
    }.OfType<string>());
    public int MissingCount => _p.MissingPlugins + _p.MissingSamples;

    /// <summary>The key the project's notes point to (drums left out), or "" when they don't point to one.</summary>
    public string DetectedKey => _p.DetectedKey;
    /// <summary>The key: the one typed in, or else the one its notes point to.</summary>
    public string Key => string.IsNullOrWhiteSpace(MusicalKey) ? DetectedKey : MusicalKey.Trim();
    /// <summary>The Key box's hint: the key the notes point to, until one is typed in.</summary>
    public string KeyHint => DetectedKey.Length > 0 ? $"{DetectedKey} (from the notes)" : "e.g. F minor";

    public static string WhenGroupFor(DateTime modified, DateTime now)
    {
        if (modified > now.Date.AddDays(-7)) return "This week";
        if (modified.Year == now.Year && modified.Month == now.Month) return "This month";
        if (modified.Year == now.Year) return $"Earlier in {now.Year}";
        return modified.Year.ToString();
    }
    public double? Tempo => _p.Tempo;
    public string TempoText => _p.Tempo is { } t ? t.ToString("0.##") : "";
    public long Size => _p.Size;
    public int PluginCount => _p.PluginCount;
    public int SampleCount => _p.SampleCount;
    public int MissingPlugins => _p.MissingPlugins;
    public int MissingSamples => _p.MissingSamples;
    public string MissingText => string.Join(", ", new[]
    {
        MissingPlugins > 0 ? $"{MissingPlugins} plugin{(MissingPlugins == 1 ? "" : "s")}" : null,
        MissingSamples > 0 ? $"{MissingSamples} sample{(MissingSamples == 1 ? "" : "s")}" : null,
        FileMissing ? "file moved" : null,
    }.OfType<string>());
    public bool HasMissing => _p.MissingPlugins + _p.MissingSamples > 0;
    public bool FileMissing => _p.FileMissing;
    public string? Error => _p.Error;
    public IReadOnlyList<string> PluginNames => _p.PluginNames;
    public IReadOnlyList<string> Hardware => _p.Hardware;
    public string HardwareText => _p.Hardware.Count > 0 ? string.Join(", ", _p.Hardware) : "No Elektron boxes";
    public DateTime? LastOpened => _p.LastOpened;
    public bool IsHeuristic => _p.Confidence == Confidence.Heuristic;

    /// <summary>"Open in Bitwig" while a DAW has this song open, else empty.</summary>
    [ObservableProperty] string _openIn = "";

    [ObservableProperty] string _status;
    [ObservableProperty] int _rating;
    [ObservableProperty] bool _favourite;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DetailsLine), nameof(Key))] string _musicalKey;
    [ObservableProperty] string _notes;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TagsText))] IReadOnlyList<string> _tags;

    public string TagsText
    {
        get => string.Join(", ", Tags);
        set
        {
            var tags = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(t => t.ToLowerInvariant()).Distinct().Order().ToList();
            if (tags.SequenceEqual(Tags)) return;
            _db.SetTags(Id, tags);
            Tags = tags;
        }
    }

    partial void OnStatusChanged(string value) => Save();
    partial void OnRatingChanged(int value) => Save();
    partial void OnFavouriteChanged(bool value) => Save();
    partial void OnMusicalKeyChanged(string value) => Save();
    partial void OnNotesChanged(string value) => Save();

    void Save()
    {
        if (_loading) return;
        _p.Status = Status;
        _p.Rating = Rating;
        _p.Favourite = Favourite;
        _p.MusicalKey = MusicalKey ?? "";
        _p.Notes = Notes ?? "";
        _db.SaveOrganisation(_p);
    }

    /// <summary>
    /// Everything the search box matches against. Several words must each match something, in any order, so
    /// "serum dark" finds a project using Serum with "dark" in its name or notes; a year such as 2019 also matches
    /// projects last saved that year.
    /// </summary>
    public bool Matches(string text) =>
        MatchesWord(text) ||
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } words && words.All(MatchesWord);

    bool MatchesWord(string text) =>
        Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        FileName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        Folder.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        Notes.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        Daw.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        Key.Equals(text, StringComparison.OrdinalIgnoreCase) ||
        (text.Length == 4 && int.TryParse(text, out var year) && (year == Year || year == Made.Year)) ||
        Tags.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)) ||
        PluginNames.Any(p => p.Contains(text, StringComparison.OrdinalIgnoreCase));
}
