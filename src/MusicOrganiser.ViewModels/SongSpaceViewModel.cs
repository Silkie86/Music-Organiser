using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Space;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// A song's Space tab: how much room it takes, where that goes, and what Compact can free without losing music
/// (exact copies of a save, old autosaves, caches the DAW rebuilds). Measured the first time the tab opens (<see cref="LoadAsync"/>),
/// in the background. Compact asks Keep or Delete first and puts everything in the Recycle Bin; samples are never on its list.
/// </summary>
public sealed partial class SongSpaceViewModel : ObservableObject
{
    readonly LibraryDb _db;
    readonly MainViewModel _main;
    readonly ProjectItem _item;

    public SongSpaceViewModel(LibraryDb db, MainViewModel main, ProjectItem item)
    {
        _db = db;
        _main = main;
        _item = item;
    }

    bool _started;

    /// <summary>The tab showed: measure the song, once (Measure again does it again).</summary>
    public Task LoadAsync()
    {
        if (_started) return Task.CompletedTask;
        _started = true;
        return MeasureAsync();
    }

    [ObservableProperty] bool _measuring = true;
    [ObservableProperty] string _totalText = "";
    /// <summary>"could save 2.6 GB", or that nothing needs to go.</summary>
    [ObservableProperty] string _freeableText = "";
    /// <summary>Where the room goes: saves, autosaves, caches, audio, other.</summary>
    [ObservableProperty] IReadOnlyList<SpacePart> _parts = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNothing))] IReadOnlyList<CompactRow> _rows = [];
    public bool HasNothing => Rows.Count == 0;
    [ObservableProperty] string _note = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CompactCommand))] bool _canCompact;
    /// <summary>The Keep or Delete step: showing once Compact is pressed.</summary>
    [ObservableProperty] bool _confirming;
    [ObservableProperty] string _confirmText = "";
    [ObservableProperty] bool _busy;

    public static IReadOnlyList<int> KeepBackupsChoices { get; } = [1, 3, 5, 10];
    [ObservableProperty] int _keepBackups;

    int _measures;

    async Task MeasureAsync()
    {
        Measuring = true;
        var measure = ++_measures;
        var saves = _item.OlderSaves.Prepend(_item.Summary).Where(s => !s.FileMissing)
                         .Select(s => new SongSpace.Save(s.Path, s.Size, s.Modified)).ToList();
        if (_keepBackups == 0)
            _keepBackups = int.TryParse(_db.GetSetting(LibraryDb.KeepBackupsKey), out var k) && KeepBackupsChoices.Contains(k) ? k : SongSpace.DefaultKeepBackups;
        var keep = KeepBackups;
        var ids = _item.OlderSaves.Prepend(_item.Summary).Select(s => s.Id).ToList();
        SongSpaceReport report;
        try
        {
            report = await Task.Run(() => SongSpace.Measure(saves, _db.Roots(), _db.SamplesOf(ids), keep));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Measuring = false;
            Note = $"Couldn't measure the song's folder: {e.Message}";
            return;
        }
        if (measure != _measures) return;
        Show(report);
        Measuring = false;
        OnPropertyChanged(nameof(KeepBackups));
    }

    SongSpaceReport? _report;

    void Show(SongSpaceReport r)
    {
        _report = r;
        TotalText = DiskSpace.Text(r.Bytes);
        Parts = new[]
        {
            new SpacePart("Saves", r.Saves, "#5B9CF2"),
            new SpacePart("Autosaves", r.Backups, "#B48CE8"),
            new SpacePart("Caches", r.Caches, "#4FB6A8"),
            new SpacePart("Audio (recordings, bounces, samples)", r.Audio, "#E0A640"),
            new SpacePart("Other files", r.Other, "#8D939C"),
        }.Where(p => p.Bytes > 0).Select(p => p with { Share = (double)p.Bytes / Math.Max(1, r.Bytes) }).ToList();
        Rows = Group(r.Items).ToList();
        foreach (var row in Rows) row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CompactRow.IsTicked)) Refresh(); };
        FreeableText = r.Freeable > 0 ? $"could save {DiskSpace.Text(r.Freeable)}" : "nothing to compact";
        Note = r.OwnFolder
            ? $"Everything in {Path.GetFileName(r.Folder)}, the song's own folder."
            : "Its save files and autosaves: the folder holds other songs too, so the rest isn't counted.";
        Refresh();
        // The list shows the newest size too.
        if (r.OwnFolder) _db.SaveFolderSpaces([(r.Folder, r.Bytes)]);
        _item.SpaceBytes = r.OwnFolder ? r.Bytes : _item.SavesBytes;
    }

    /// <summary>One row per kind: "7 identical saves", "41 old autosaves", with the files behind each.</summary>
    static IEnumerable<CompactRow> Group(IReadOnlyList<CompactItem> items) =>
        items.GroupBy(i => i.Kind).Select(g =>
        {
            var n = g.Count();
            var (title, detail) = g.Key switch
            {
                CompactKind.SameSave => (n == 1 ? "1 identical save" : $"{n} identical saves",
                                         "Byte-for-byte copies of another save in the same folder. The newest of each stays."),
                CompactKind.Backup => (n == 1 ? "1 old autosave" : $"{n} old autosaves", "Your DAW's own backup copies, older than the newest few."),
                CompactKind.Cache => ("Caches the DAW rebuilds", g.First().Note),
                _ => (g.First().Note, "Shown only: samples and recordings are never removed. Check them yourself with Show."),
            };
            return new CompactRow(g.Key, title, detail, g.SelectMany(i => i.Paths).ToList(), g.Sum(i => i.Bytes));
        });

    void Refresh()
    {
        var bytes = Rows.Where(r => r.IsTicked).Sum(r => r.Bytes);
        CanCompact = bytes > 0 && !Busy;
        ConfirmText = bytes > 0 ? $"Delete {DiskSpace.Text(bytes)}" : "Delete";
    }

    partial void OnKeepBackupsChanged(int value)
    {
        if (Measuring || value == 0) return;
        _db.SetSetting(LibraryDb.KeepBackupsKey, value.ToString());
        _ = MeasureAsync();
    }

    /// <summary>Measures the song again (after a save, or when its folder changed).</summary>
    [RelayCommand]
    Task Remeasure() => MeasureAsync();

    /// <summary>Compact: shows the Keep or Delete step.</summary>
    [RelayCommand(CanExecute = nameof(CanCompact))]
    void Compact() => Confirming = true;

    [RelayCommand]
    void Keep() => Confirming = false;

    /// <summary>Delete: the ticked files go to the Recycle Bin, and saves that went leave the library.</summary>
    [RelayCommand]
    async Task Delete()
    {
        if (_report is null) return;
        Busy = true;
        CanCompact = false;
        var newest = _item.Path;
        // Your newest save never goes, whatever the list says.
        var paths = Rows.Where(r => r.IsTicked && r.Kind != CompactKind.UnusedAudio).SelectMany(r => r.Paths)
                        .Where(p => !p.Equals(newest, StringComparison.OrdinalIgnoreCase)).ToList();
        var bytes = Rows.Where(r => r.IsTicked).Sum(r => r.Bytes);
        var saves = Rows.Where(r => r.IsTicked && r.Kind == CompactKind.SameSave).SelectMany(r => r.Paths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var problems = await Task.Run(() => paths.Select(p => (Path: p, Problem: RecycleBin.Send(p))).Where(x => x.Problem.Length > 0).ToList());
        foreach (var gone in saves.Where(s => problems.All(x => !x.Path.Equals(s, StringComparison.OrdinalIgnoreCase))))
            _db.RemoveProject(gone);
        Busy = false;
        Confirming = false;
        if (problems.Count == 0)
            _main.Activities.Start("Song compacted").Finish($"Freed {DiskSpace.Text(bytes)} from {_item.Name}", "The files are in the Recycle Bin if you want them back.");
        else
            _main.Activities.Failed($"Some of {_item.Name}'s files couldn't go", $"{problems.Count} left: {problems[0].Problem}");
        if (saves.Count > 0) await _main.Projects.ReloadAsync(_item.Path);
        else await MeasureAsync();
    }
}

/// <summary>A part of a song's room (saves, autosaves …), for the bar and its legend.</summary>
public sealed record SpacePart(string Name, long Bytes, string Colour, double Share = 0)
{
    public string SizeText => DiskSpace.Text(Bytes);
}

/// <summary>A line on the Space tab: something that could go, ticked to go with Compact.</summary>
public sealed partial class CompactRow : ObservableObject
{
    public CompactRow(CompactKind kind, string title, string detail, IReadOnlyList<string> paths, long bytes)
    {
        Kind = kind;
        Title = title;
        Detail = detail;
        Paths = paths;
        Bytes = bytes;
        _isTicked = CanTick;
    }

    public CompactKind Kind { get; }
    public string Title { get; }
    public string Detail { get; }
    public IReadOnlyList<string> Paths { get; }
    public long Bytes { get; }
    public string SizeText => DiskSpace.Text(Bytes);
    public bool CanTick => Kind != CompactKind.UnusedAudio;
    /// <summary>The first few files, for the tooltip.</summary>
    public string FilesTip => string.Join("\n", Paths.Take(12).Select(Path.GetFileName)) + (Paths.Count > 12 ? $"\n… and {Paths.Count - 12} more" : "");
    [ObservableProperty] bool _isTicked;

    [RelayCommand] void Show() => Shell.Reveal(Paths[0]);
}
