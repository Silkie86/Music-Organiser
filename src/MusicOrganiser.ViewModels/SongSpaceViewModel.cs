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

    public IRelayCommand OpenRecycleBinCommand => _main.OpenRecycleBinCommand;

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

    /// <summary>0 lets every autosave go.</summary>
    public static IReadOnlyList<int> KeepBackupsChoices { get; } = [0, 1, 3, 5, 10];
    [ObservableProperty] int _keepBackups;
    bool _keepRead;

    /// <summary>How long without a save ends a sitting, for thinning older saves (Off doesn't offer it).</summary>
    public static IReadOnlyList<SittingChoice> SittingChoices { get; } =
        [new(0, "Off"), new(1, "1 hour"), new(SongSpace.DefaultSittingHours, "3 hours"), new(24, "a day")];
    [ObservableProperty] SittingChoice? _sitting;

    int _measures;

    async Task MeasureAsync()
    {
        Measuring = true;
        var measure = ++_measures;
        var saves = _item.OlderSaves.Prepend(_item.Summary).Where(s => !s.FileMissing)
                         .Select(s => new SongSpace.Save(s.Path, s.Size, s.Modified)).ToList();
        if (!_keepRead)
        {
            _keepRead = true;
            KeepBackups = int.TryParse(_db.GetSetting(LibraryDb.KeepBackupsKey), out var k) && KeepBackupsChoices.Contains(k) ? k : SongSpace.DefaultKeepBackups;
            var hours = int.TryParse(_db.GetSetting(LibraryDb.SittingHoursKey), out var h) ? h : SongSpace.DefaultSittingHours;
            Sitting = SittingChoices.FirstOrDefault(c => c.Hours == hours) ?? SittingChoices[2];
        }
        var keep = KeepBackups;
        var sitting = Sitting?.Hours ?? 0;
        var ids = _item.OlderSaves.Prepend(_item.Summary).Select(s => s.Id).ToList();
        var paths = _item.OlderSaves.Prepend(_item.Summary).Select(s => s.Path).ToList();
        SongSpaceReport report;
        try
        {
            report = await Task.Run(() => SongSpace.Measure(saves, _db.Roots(), _db.SamplesOf(ids), keep, sitting, elsewhere: Elsewhere(paths)));
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
        OnPropertyChanged(nameof(Sitting));
    }

    /// <summary>
    /// What other songs need, so it's never offered: your sample folders, audio any project uses, and FL Studio autosaves
    /// named like another song's (read in the background).
    /// </summary>
    SongSpace.Elsewhere Elsewhere(IReadOnlyList<string> saves)
    {
        var mine = saves.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = saves.Where(s => s.EndsWith(".flp", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
        var shared = _db.FlProjectsNamed(names).Where(p => !mine.Contains(Path.GetFullPath(p)))
                        .Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(_db.GetList(Scanner.SampleFoldersKey), _db.SampleNamesUsed, shared);
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
        Rows = Group(r.Items, this).ToList();
        foreach (var row in Rows)
        {
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CompactRow.IsTicked)) Refresh(); };
            foreach (var file in row.Files) file.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AudioFileRow.IsTicked)) Refresh(); };
        }
        var thin = DiskSpace.Text(r.Thinnable);
        FreeableText = (r.Freeable, r.Thinnable) switch
        {
            ( > 0, > 0) => $"could save {DiskSpace.Text(r.Freeable)}, and {thin} more by thinning older saves",
            ( > 0, _) => $"could save {DiskSpace.Text(r.Freeable)}",
            (_, > 0) => $"thinning older saves could save {thin}",
            _ => "nothing to compact",
        };
        Note = r.OwnFolder
            ? $"Everything in {Path.GetFileName(r.Folder)}, the song's own folder."
            : "Its save files and autosaves: the folder holds other songs too, so the rest isn't counted.";
        Refresh();
        // The list shows the newest size too.
        if (r.OwnFolder) _db.SaveFolderSpaces([(r.Folder, r.Bytes)]);
        _item.SpaceBytes = r.OwnFolder ? r.Bytes : _item.SavesBytes;
    }

    /// <summary>
    /// One row per kind: "7 identical saves", "41 old autosaves", with the files behind each. Audio no save seems to use
    /// opens into its files, each with Play, none ticked: you hear each one and pick (Solomon's pick, 25 Oct).
    /// </summary>
    static IEnumerable<CompactRow> Group(IReadOnlyList<CompactItem> items, SongSpaceViewModel owner) =>
        items.GroupBy(i => i.Kind).Select(g =>
        {
            var n = g.Count();
            var (title, detail) = g.Key switch
            {
                CompactKind.SameSave => (n == 1 ? "1 identical save" : $"{n} identical saves",
                                         "Byte-for-byte copies of another save in the same folder. The newest of each stays."),
                CompactKind.Backup => (n == 1 ? "1 old autosave" : $"{n} old autosaves", "Your DAW's own backup copies, older than the newest you keep."),
                CompactKind.Cache => ("Caches the DAW rebuilds", g.First().Note),
                CompactKind.ThinnedSave => (n == 1 ? "Thin older saves: 1 save from the middle of a sitting" : $"Thin older saves: {n} saves from the middle of a sitting",
                                            "Saves made close together while you worked. Your first save, your newest and the last save of each sitting stay. Not ticked for you."),
                _ => (n == 1 ? "1 audio file no save seems to use" : $"{n} audio files no save seems to use",
                      "Maybe old bounces or takes, maybe a mix you'd want. Play each one and tick only what can go."),
            };
            var files = g.Key == CompactKind.UnusedAudio ? g.Select(i => new AudioFileRow(i.Paths[0], i.Bytes, owner)).ToList() : [];
            return new CompactRow(g.Key, title, detail, g.SelectMany(i => i.Paths).ToList(), g.Sum(i => i.Bytes), files);
        });

    /// <summary>What's ticked: whole rows, and the audio files picked one by one.</summary>
    IEnumerable<(string Path, long Bytes)> Ticked() =>
        Rows.Where(r => r.IsTicked).SelectMany(r => r.Paths.Select(p => (p, 0L))).Concat(Rows.SelectMany(r => r.Files).Where(f => f.IsTicked).Select(f => (f.Path, f.Bytes)));

    long TickedBytes => Rows.Where(r => r.IsTicked).Sum(r => r.Bytes) + Rows.SelectMany(r => r.Files).Where(f => f.IsTicked).Sum(f => f.Bytes);
    int TickedAudio => Rows.SelectMany(r => r.Files).Count(f => f.IsTicked);

    /// <summary>The Keep or Delete step's line: says so when audio is among what goes.</summary>
    [ObservableProperty] string _confirmNote = "";

    void Refresh()
    {
        var bytes = TickedBytes;
        CanCompact = bytes > 0 && !Busy;
        ConfirmText = bytes > 0 ? $"Delete {DiskSpace.Text(bytes)}" : "Delete";
        var audio = TickedAudio;
        ConfirmNote = "They go to the Recycle Bin, so you can put them back. Your newest save stays where it is" +
                      (audio == 0 ? ", and so does every sample."
                                  : $". {(audio == 1 ? "1 audio file you picked goes too" : $"{audio} audio files you picked go too")}: make sure you've heard them.");
    }

    IAudioPlayer? _player;

    /// <summary>Plays an audio file on the list, or stops it if it's the one playing.</summary>
    internal void Play(AudioFileRow file)
    {
        try
        {
            _player ??= Ui.Current.CreateAudioPlayer();
            if (_player.Playing == file.Path) _player.Stop();
            else _player.Play(file.Path);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            _main.Activities.Failed($"Couldn't play {Path.GetFileName(file.Path)}", e.Message);
        }
    }

    /// <summary>The tab closed or the song changed: stop anything still playing.</summary>
    public void StopPlaying()
    {
        _player?.Dispose();
        _player = null;
    }

    partial void OnSittingChanged(SittingChoice? value)
    {
        if (Measuring || value is null) return;
        _db.SetSetting(LibraryDb.SittingHoursKey, value.Hours.ToString());
        _ = MeasureAsync();
    }

    partial void OnKeepBackupsChanged(int value)
    {
        if (Measuring) return;
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
        _player?.Stop();
        // Your newest save never goes, whatever the list says.
        var paths = Ticked().Select(t => t.Path).Where(p => !p.Equals(newest, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        var bytes = TickedBytes;
        var saves = Rows.Where(r => r.IsTicked && r.Kind is CompactKind.SameSave or CompactKind.ThinnedSave).SelectMany(r => r.Paths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var problems = await Task.Run(() => RecycleBin.SendMany(paths));
        foreach (var gone in saves.Where(s => !problems.ContainsKey(s)))
            _db.RemoveProject(gone);
        Busy = false;
        Confirming = false;
        if (problems.Count == 0)
            _main.Activities.Start("Song compacted").Finish($"Freed {DiskSpace.Text(bytes)} from {_item.Name}", "The files are in the Recycle Bin if you want them back.",
                                                            "Open Recycle Bin", _main.OpenRecycleBin);
        else
            _main.Activities.Failed($"Some of {_item.Name}'s files couldn't go", $"{problems.Count} left: {problems.First().Value}");
        if (saves.Count > 0) await _main.Projects.ReloadAsync(_item.Path);
        else await MeasureAsync();
    }
}

/// <summary>A part of a song's room (saves, autosaves …), for the bar and its legend.</summary>
/// <param name="What">What the part holds, for the app's own files on Settings ("Projects, plugins, samples, tags and ratings").</param>
public sealed record SpacePart(string Name, long Bytes, string Colour, double Share = 0, string What = "")
{
    public string SizeText => DiskSpace.Text(Bytes);
}

/// <summary>A choice of how long without a save ends a sitting.</summary>
public sealed record SittingChoice(int Hours, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A line on the Space tab: something that could go, ticked to go with Compact.</summary>
public sealed partial class CompactRow : ObservableObject
{
    public CompactRow(CompactKind kind, string title, string detail, IReadOnlyList<string> paths, long bytes, IReadOnlyList<AudioFileRow>? files = null)
    {
        Kind = kind;
        Title = title;
        Detail = detail;
        Paths = paths;
        Bytes = bytes;
        Files = files ?? [];
        // Thinning removes versions you might want to go back to: you tick it yourself.
        _isTicked = CanTick && kind != CompactKind.ThinnedSave;
    }

    /// <summary>The audio files, each picked on its own (only on the audio line).</summary>
    public IReadOnlyList<AudioFileRow> Files { get; }
    public bool HasFiles => Files.Count > 0;

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

/// <summary>An audio file no save seems to use: played, then ticked by hand if it can go.</summary>
public sealed partial class AudioFileRow(string path, long bytes, SongSpaceViewModel owner) : ObservableObject
{
    public string Path => path;
    public long Bytes => bytes;
    public string Name => System.IO.Path.GetFileName(path);
    public string SizeText => DiskSpace.Text(bytes);
    /// <summary>The folder it's in, inside the song's ("Bounces", "Media").</summary>
    public string Folder => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path) ?? "");
    [ObservableProperty] bool _isTicked;

    [RelayCommand] void Play() => owner.Play(this);
    [RelayCommand] void Show() => Shell.Reveal(path);
}
