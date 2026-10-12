using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MusicOrganiser.App.Audio;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.App.ViewModels;

/// <summary>
/// The Sound Lab: your sample library analysed and learned. Browse and search,
/// rediscover sounds you don't use, build random kits, and teach it your labels.
/// </summary>
public sealed partial class SoundsViewModel : ObservableObject
{
    readonly LibraryDb _db;
    readonly MainViewModel _main;
    readonly AudioPlayer _player = new();
    CancellationTokenSource? _cts;

    public SoundOrganiser Org { get; }
    public RediscoverViewModel Rediscover { get; }
    public KitViewModel Kit { get; }

    public static IReadOnlyList<string> AllTypes { get; } = [.. SoundTypes.All, SoundTypes.Unknown];
    public static IReadOnlyList<string> KeyChoices { get; } = ["Any key", .. MusicTheory.NoteNames.Select(n => n), .. MusicTheory.NoteNames.Select(n => n + "m")];

    [ObservableProperty] IReadOnlyList<Sound> _items = [];
    [ObservableProperty] ICollectionView? _view;
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

    // Teaching & models
    [ObservableProperty] string _modelSummary = "";
    [ObservableProperty] string _clapStatus = "";
    [ObservableProperty] bool _clapInstalled;
    [ObservableProperty] IReadOnlyList<Disagreement> _disagreements = [];
    [ObservableProperty] IReadOnlyList<TagSummary> _tagSummaries = [];

    Dictionary<long, double>? _describeScores;

    public SoundsViewModel(LibraryDb db, SoundOrganiser org, MainViewModel main)
    {
        _db = db;
        _main = main;
        Org = org;
        Rediscover = new RediscoverViewModel(this);
        Kit = new KitViewModel(this);
        Reload();
        if (SampleFolders.Count > 0) _ = ScanAsync();
    }

    IReadOnlyList<string> SampleFolders => _db.GetList(Scanner.SampleFoldersKey);

    public void Reload()
    {
        var usage = _db.SampleUsage().Select(u => (u.Path, u.Projects)).ToList();
        Org.ApplyUsage(usage);
        Items = Org.Sounds;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is Sound s && Matches(s);
        ApplySort();
        TypeCounts = [new TypeCount("All sounds", Items.Count), .. Items.GroupBy(s => s.Type).OrderByDescending(g => g.Count()).Select(g => new TypeCount(g.Key, g.Count()))];
        SelectedType ??= TypeCounts[0];
        var loops = Items.Count(s => s.IsLoop);
        var unused = Items.Count(s => s.UsedIn == 0);
        Summary = Items.Count == 0
            ? "Add your sample folders in Settings, then scan them here."
            : $"{Items.Count:N0} sounds · {loops:N0} loops · {unused:N0} never used in a project";
        UpdateModelInfo();
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
        if (View is not ListCollectionView lcv) return;
        lcv.CustomSort = _describeScores is { } scores
            ? Comparer<object>.Create((a, b) => scores.GetValueOrDefault(((Sound)b).Id).CompareTo(scores.GetValueOrDefault(((Sound)a).Id)))
            : null;
    }

    partial void OnSearchTextChanged(string value)
    {
        if (DescribeMode) return;   // describe searches run on Enter
        View?.Refresh();
    }

    partial void OnDescribeModeChanged(bool value)
    {
        _describeScores = null;
        ApplySort();
        View?.Refresh();
    }

    partial void OnSelectedTypeChanged(TypeCount? value) => View?.Refresh();
    partial void OnLoopFilterChanged(int value) => View?.Refresh();
    partial void OnKeyFilterChanged(string value) => View?.Refresh();
    partial void OnCompatibleKeysChanged(bool value) => View?.Refresh();
    partial void OnBpmFromChanged(string value) => View?.Refresh();
    partial void OnBpmToChanged(string value) => View?.Refresh();
    partial void OnOnlyUnusedChanged(bool value) => View?.Refresh();
    partial void OnOnlyFavouritesChanged(bool value) => View?.Refresh();
    partial void OnTagFilterChanged(string value) => View?.Refresh();

    [RelayCommand]
    async Task Search()
    {
        if (!DescribeMode)
        {
            View?.Refresh();
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
        View?.Refresh();
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
        View?.Refresh();
    }

    // ---- selected sound -------------------------------------------------------------

    partial void OnSelectedChanged(Sound? value)
    {
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
        View?.Refresh();
    }

    [RelayCommand]
    void Play(Sound? s)
    {
        s ??= Selected;
        if (s is null) return;
        try
        {
            if (_player.Playing == s.Path) { _player.Stop(); return; }
            _player.Play(s.Path);
            Org.Library.MarkPlayed(s.Id);
            s.Plays++;
        }
        catch (Exception e) { Message = $"Can't play {s.Name}: {e.Message}"; }
    }

    [RelayCommand]
    void StopPlaying() => _player.Stop();

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
        OnPropertyChanged(nameof(Selected));
        View?.Refresh();
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
    void ExportMidi()
    {
        if (Selected is not { } s || Notes.Count == 0) return;
        var dialog = new SaveFileDialog { FileName = s.Name + ".mid", Filter = "MIDI file (*.mid)|*.mid", InitialDirectory = ExportFolder() };
        if (dialog.ShowDialog() != true) return;
        var bpm = s.Bpm ?? 120;
        File.WriteAllBytes(dialog.FileName, MidiWriter.Write(NoteTranscriber.ToMidi(s.Name, Notes, bpm), bpm));
        Message = $"Saved {Path.GetFileName(dialog.FileName)}";
    }

    internal string ExportFolder()
    {
        var f = _db.GetSetting(ProjectDetailViewModel.ExportFolderKey);
        if (!string.IsNullOrEmpty(f) && Directory.Exists(f)) return f;
        var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Music Organiser Exports");
        Directory.CreateDirectory(d);
        return d;
    }

    // ---- long jobs ------------------------------------------------------------------

    async Task RunAsync(string label, Action<IProgress<SoundProgress>, CancellationToken> job)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = label;
        _cts = new CancellationTokenSource();
        var progress = new Progress<SoundProgress>(p =>
        {
            BusyText = p.Total > 0 ? $"{p.Stage}: {p.Done:N0} of {p.Total:N0}" : p.Stage;
            BusyPercent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
        });
        try
        {
            await Task.Run(() => job(progress, _cts.Token), _cts.Token);
        }
        catch (OperationCanceledException) { Message = "Stopped"; }
        catch (Exception e) { Message = $"{label} failed: {e.Message}"; }
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
        await RunAsync("Scanning sample folders", (p, ct) => count = Org.Scan(folders, p, ct));
        if (count > 0) Message = $"Analysed {count:N0} new or changed sounds.";
    }

    [RelayCommand]
    async Task DeepListen()
    {
        if (!ClapModel.IsInstalled())
        {
            Message = "Download the sound-understanding model first (Teach tab).";
            return;
        }
        await RunAsync("Deep listening", (p, ct) => Org.DeepListen(p, ct));
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
            });
            await ClapModel.DownloadAsync(progress, ct: _cts.Token);
            Org.ResetClap();
            ModelMessage = "Downloaded and installed. Press Deep listen to let it hear your library.";
        }
        catch (OperationCanceledException) { ModelMessage = "Download stopped. Press Download model to carry on from the file it was on."; }
        catch (Exception e) { ModelMessage = $"Download failed: {e.Message}. Check the internet connection and try again."; }
        finally
        {
            IsBusy = IsDownloading = false;
            BusyPercent = DownloadPercent = 0;
            UpdateModelInfo();
        }
    }

    [RelayCommand]
    async Task Retrain() => await RunAsync("Learning your sounds", (p, _) => Org.UpdateTypes(p));

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
        View?.Refresh();
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
        Suggestions = await Task.Run(() => Core.Sounds.Rediscover.Suggest(parent.Org, o));
        Folders = await Task.Run(() => Core.Sounds.Rediscover.Folders(parent.Org).Take(40).ToList());
        var used = parent.Org.Sounds.Count(s => s.UsedIn > 0);
        Intro = used == 0
            ? "None of your sounds show up in scanned projects yet, so these are a varied pick. Star favourites or scan your projects to teach it your taste."
            : $"Learned from the {used:N0} sounds your projects use, plus your favourites and plays.";
    }
}

/// <summary>Random kits from your library.</summary>
public sealed partial class KitViewModel(SoundsViewModel parent) : ObservableObject
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
        var slots = Slots.Select(s => s.Slot).Where(s => s.Sound is not null).ToList();
        if (slots.Count == 0) { Status = "Roll a kit first."; return; }
        try
        {
            var folder = KitBuilder.Export(slots, Path.Combine(parent.ExportFolder(), "Kits"),
                $"Kit {DateTime.Now:yyyy-MM-dd HHmm} #{Seed}", new KitBuilder.ExportOptions(ElektronReady, Mono, Normalise));
            Status = $"Exported to {folder}";
            Shell.Open(folder);
        }
        catch (Exception e) { Status = $"Export failed: {e.Message}"; }
    }
}

public sealed partial class KitSlotItem(KitSlot slot) : ObservableObject
{
    public KitSlot Slot => slot;
    public string Name => slot.Name;
    public string SoundName => slot.Sound?.Name ?? "—";
    public string Detail => slot.Sound is { } s ? $"{s.Type} · {s.Folder}" + (s.UsedIn > 0 ? $" · used in {s.UsedIn}" : " · unused") : "";

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
