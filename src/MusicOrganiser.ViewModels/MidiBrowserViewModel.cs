using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// The MIDI page: every MIDI part in your projects and MIDI libraries, searchable by words and sorted into chords,
/// melodies, basslines, arps and drums. Pick a project (or a part) to fit, and every part is ranked by how well its
/// harmony goes with it once transposed, and shown, played and dragged out already moved into the right key.
/// </summary>
public sealed partial class MidiBrowserViewModel : ObservableObject, IFileDragSource, IDisposable
{
    /// <summary>Set once the MIDI page has been opened: from then on, each scan also brings the MIDI index up to date.</summary>
    public const string UsedKey = "midi_browser_used";

    public const string Everything = "Everything", YourProjects = "Your projects", YourLibraries = "MIDI libraries";
    public static IReadOnlyList<string> FromChoices { get; } = [Everything, YourProjects, YourLibraries];

    readonly LibraryDb _db;
    readonly MainViewModel _main;
    readonly MidiIndex _index;
    readonly MidiIndexer _indexer;
    MidiCatalogue? _catalogue;
    IAudioPlayer? _player;
    IUiTimer? _clock;
    MidiRow? _playing;
    CancellationTokenSource? _indexCts;
    Task? _indexing;
    int _searchRequest, _listenRequest;
    bool _loading;

    public MidiBrowserViewModel(LibraryDb db, MainViewModel main, MidiIndex? index = null)
    {
        _db = db;
        _main = main;
        _index = index ?? new MidiIndex(MidiIndex.PathBeside(db.FilePath));
        _indexer = new MidiIndexer(_index, db);
        Folders = new ObservableCollection<string>(db.GetList(MidiIndexer.FoldersKey));
        RoleChips = MidiRoles.All.Prepend("All").Select(r => new RoleChip(r == "All" ? "All" : MidiRoles.Plural(r), r)).ToList();
        _selectedRole = RoleChips[0];
    }

    public MidiIndex Index => _index;

    // ---- what's shown ----

    public IReadOnlyList<RoleChip> RoleChips { get; }
    public ObservableCollection<string> Folders { get; }

    [ObservableProperty] string _search = "";
    [ObservableProperty] RoleChip _selectedRole;
    [ObservableProperty] string _from = Everything;
    [ObservableProperty] IReadOnlyList<MidiRow> _results = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection))] MidiRow? _selected;
    [ObservableProperty] string _summary = "Reading your MIDI…";
    [ObservableProperty] string _resultText = "";
    [ObservableProperty] string _message = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty))] bool _isIndexing;
    [ObservableProperty] string _indexText = "";
    [ObservableProperty] double _indexPercent;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty))] bool _hasParts;

    public bool HasSelection => Selected is not null;
    /// <summary>Nothing to show yet, and nothing being read: say how to get MIDI in.</summary>
    public bool IsEmpty => !HasParts && !IsIndexing;

    // ---- fitting a harmony ----

    [ObservableProperty] string _fitSearch = "";
    [ObservableProperty] IReadOnlyList<ProjectItem> _fitChoices = [];
    [ObservableProperty] bool _fitChoicesOpen;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsFitting))] HarmonyTarget? _fit;
    [ObservableProperty] string _fitText = "";
    public bool IsFitting => Fit is not null;
    /// <summary>The project being fitted to, whose own parts are left out of the list.</summary>
    string? _fitPath;

    // ---- more like this ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsLiking))] MidiEntry? _like;
    public bool IsLiking => Like is not null;
    public string LikeText => Like is null ? "" : $"Parts shaped like {Like.Title}";

    partial void OnSearchChanged(string value) => _ = RefreshAsync(delayMs: 150);
    partial void OnSelectedRoleChanged(RoleChip value) => _ = RefreshAsync();
    partial void OnFromChanged(string value) => _ = RefreshAsync();
    partial void OnLikeChanged(MidiEntry? value)
    {
        OnPropertyChanged(nameof(LikeText));
        _ = RefreshAsync();
    }

    partial void OnFitSearchChanged(string value)
    {
        var words = value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        FitChoices = words.Length == 0 ? [] : _main.Projects.Items
            .Where(p => words.All(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                       p.FileName.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(12).ToList();
        FitChoicesOpen = FitChoices.Count > 0;
    }

    /// <summary>The page has come into view: the first time, read the index (and bring it up to date).</summary>
    public void Opened()
    {
        if (_db.GetSetting(UsedKey) != "1") _db.SetSetting(UsedKey, "1");
        if (_catalogue is null && !_loading) _ = LoadAsync(thenIndex: true);
    }

    async Task LoadAsync(bool thenIndex = false)
    {
        _loading = true;
        try
        {
            var catalogue = await UiWork.Run(() => new MidiCatalogue(_index.All()));
            _catalogue = catalogue;
            HasParts = catalogue.Entries.Count > 0;
            var counts = catalogue.RoleCounts();
            foreach (var chip in RoleChips)
                chip.Count = chip.Role == "All" ? catalogue.Entries.Count : counts.GetValueOrDefault(chip.Role);
            var files = catalogue.Entries.Select(e => e.Phrase.Source.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Summary = catalogue.Entries.Count == 0
                ? "No MIDI found yet."
                : $"{catalogue.Entries.Count:N0} MIDI parts from {files:N0} files" +
                  (catalogue.PhraseCount > catalogue.Entries.Count ? $" · {catalogue.PhraseCount - catalogue.Entries.Count:N0} copies kept as one" : "");
            await RefreshAsync();
        }
        finally { _loading = false; }
        if (thenIndex) _ = IndexAsync();
    }

    /// <summary>Runs the search (after a short pause while typing) in the background, newest request winning.</summary>
    public async Task RefreshAsync(int delayMs = 0)
    {
        var request = ++_searchRequest;
        if (delayMs > 0 && UiWork.InBackground) await Task.Delay(delayMs);
        if (request != _searchRequest || _catalogue is not { } catalogue) return;
        var query = new MidiQuery(Search.Trim(), SelectedRole.Role == "All" ? null : SelectedRole.Role,
                                  From == YourProjects ? "projects" : From == YourLibraries ? "files" : null, Fit, Like, Leave: _fitPath);
        var (hits, matching) = await UiWork.Run(() => (catalogue.Search(query, out var n), n));
        if (request != _searchRequest) return;
        var keep = Selected?.Entry;
        Results = hits.Select(h => new MidiRow(h, this)).ToList();
        Selected = Results.FirstOrDefault(r => r.Entry == keep) ?? Results.FirstOrDefault();
        ResultText = matching == 0 ? (HasParts ? "Nothing matches. Try fewer words, or another kind." : "")
            : Fit is not null ? $"Best fits first · {Math.Min(matching, hits.Count):N0} shown of {matching:N0}, each moved into the right key"
            : Like is not null ? $"Most alike first · {hits.Count:N0} of {matching:N0}"
            : hits.Count < matching ? $"{hits.Count:N0} of {matching:N0}, newest first. Search to narrow it down." : $"{matching:N0}, newest first";
    }

    // ---- indexing ----

    /// <summary>After a scan: bring the MIDI index up to date, once the page has been used.</summary>
    public Task AfterScanAsync() => _db.GetSetting(UsedKey) == "1" ? IndexAsync() : Task.CompletedTask;

    [RelayCommand]
    Task Reindex() => IndexAsync();

    [RelayCommand]
    void StopIndexing() => _indexCts?.Cancel();

    public Task IndexAsync()
    {
        if (_indexing is { IsCompleted: false }) return _indexing;
        return _indexing = RunIndexAsync();
    }

    async Task RunIndexAsync()
    {
        IsIndexing = true;
        _indexCts = new CancellationTokenSource();
        var card = _main.Activities.Start("Reading MIDI", "Looking for new and changed projects and MIDI files", _indexCts.Cancel);
        var progress = new ThrottledProgress<ScanProgress>(p =>
        {
            IndexPercent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
            IndexText = p.Total > 0 ? $"Reading MIDI: {p.Done:N0} of {p.Total:N0}" : p.Stage;
            card.Report(p.Total > 0 ? IndexPercent : -1, p.Total > 0 ? $"{p.Done:N0} of {p.Total:N0} files · {p.Current}" : p.Stage);
        }, 250);
        try
        {
            var token = _indexCts.Token;
            var result = await Task.Run(() => _indexer.Run(progress, token), token);
            if (result.Read > 0 || result.Forgotten > 0 || _catalogue is null) await LoadAsync();
            card.Finish(result.Read == 0 ? "MIDI up to date" : "MIDI read",
                        result.Read == 0 ? $"{_catalogue?.Entries.Count ?? 0:N0} parts" :
                        $"{result.Read:N0} file{(result.Read == 1 ? "" : "s")}, {result.Phrases:N0} parts" +
                        (result.Failed > 0 ? $"; {result.Failed} couldn't be read" : ""));
        }
        catch (OperationCanceledException)
        {
            card.Stop("Stopped reading MIDI", "It carries on from there next time");
            await LoadAsync();
        }
        catch (Exception e)
        {
            card.Fail("Reading MIDI failed", e.Message);
            AppLog.Info("MIDI", $"Indexing failed: {e}");
        }
        finally
        {
            progress.Close();
            IsIndexing = false;
            IndexText = "";
        }
    }

    [RelayCommand]
    async Task AddFolder()
    {
        if (await Ui.Current.PickFolder("Choose a folder of MIDI files (they stay where they are)") is not { } f || Folders.Contains(f)) return;
        Folders.Add(f);
        _db.SetList(MidiIndexer.FoldersKey, Folders);
        Message = $"Added {Path.GetFileName(f.TrimEnd('\\', '/'))}. Its MIDI files are read where they are: nothing is moved, copied or changed.";
        await IndexAsync();
    }

    [RelayCommand]
    async Task RemoveFolder(string folder)
    {
        if (!Folders.Remove(folder)) return;
        _db.SetList(MidiIndexer.FoldersKey, Folders);
        Message = $"Removed {Path.GetFileName(folder.TrimEnd('\\', '/'))} from the MIDI browser. The files themselves are untouched.";
        await IndexAsync();
    }

    // ---- fitting ----

    [RelayCommand]
    async Task FitTo(ProjectItem? project)
    {
        if (project is null) return;
        FitChoicesOpen = false;
        await FitToProjectAsync(project.Path, project.Name);
    }

    /// <summary>Ranks every part by how well it fits this project's harmony (from the Projects page too).</summary>
    public async Task FitToProjectAsync(string path, string name)
    {
        if (Readers.For(path) is not { } reader) return;
        Message = $"Listening to the harmony of {name}…";
        try
        {
            var target = await UiWork.Run(() => HarmonyTarget.FromChannels(name, reader.ExtractChannels(path)));
            if (target is null)
            {
                Message = $"{name} has too few pitched notes to fit MIDI to.";
                return;
            }
            FitToHarmony(target, leave: path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Message = $"Couldn't read {name}: {e.Message}";
        }
    }

    /// <summary>"What goes with this?": parts that fit the selected part's harmony.</summary>
    [RelayCommand]
    void FitToSelected()
    {
        if (Selected is not { } row) return;
        var e = row.Entry;
        Search = "";   // the words found this part; its partners won't share them
        var chroma = row.Fit is { Semitones: var s and not 0 } ? Rotate(e.Chroma, s) : e.Chroma;
        var chords = row.Fit is { Semitones: var s2 and not 0 } ? e.Chords.Select(c => c.Transposed(s2)).ToList() : e.Chords;
        FitToHarmony(HarmonyTarget.FromPhrase(row.Title, chroma, chords, e.Phrase.Key));
        // A chord part's partner is usually a melody or bassline, and the other way round.
        if (e.Role == MidiRoles.Chords && SelectedRole.Role is "All" or MidiRoles.Chords) SelectedRole = RoleChips.First(c => c.Role == MidiRoles.Melody);
        else if (e.Role is MidiRoles.Melody or MidiRoles.Bass or MidiRoles.Arp && SelectedRole.Role != MidiRoles.Chords)
            SelectedRole = RoleChips.First(c => c.Role == MidiRoles.Chords);
    }

    static float[] Rotate(float[] chroma, int semitones)
    {
        var r = new float[12];
        for (var p = 0; p < 12; p++) r[((p + semitones) % 12 + 12) % 12] = chroma[p];
        return r;
    }

    /// <summary>Ranks every part by how well it fits this harmony, each moved into the key that fits best.</summary>
    /// <param name="leave">The project the harmony is from, whose own parts are left out.</param>
    public void FitToHarmony(HarmonyTarget target, string? leave = null)
    {
        _fitPath = leave;
        Like = null;
        Fit = target;
        FitSearch = "";
        FitChoicesOpen = false;
        FitText = string.Join(" · ", new[]
        {
            target.Key is { } k ? $"Key {k.Name}" : "",
            target.Chords.Count > 0 ? target.ChordText() : "",
        }.Where(s => s.Length > 0));
        Message = "";
        _ = RefreshAsync();
    }

    [RelayCommand]
    void ClearFit()
    {
        Fit = null;
        _fitPath = null;
        FitText = "";
        _ = RefreshAsync();
    }

    [RelayCommand]
    void MoreLikeSelected()
    {
        if (Selected is not { } row) return;
        Fit = null;
        _fitPath = null;
        FitText = "";
        Search = "";
        Like = row.Entry;
    }

    [RelayCommand]
    void ClearLike() => Like = null;

    /// <summary>Says what kind of part the selected one is; the app remembers it for every copy.</summary>
    [RelayCommand]
    async Task SetRole(string role)
    {
        if (Selected is not { } row || _catalogue is null) return;
        var yours = role == row.Entry.Phrase.Role ? null : role;
        await UiWork.Run(() => { _index.SetYourRole(row.Entry.Phrase.Hash, yours); return 0; });
        MidiCatalogue.Relabel(row.Entry, yours);
        row.RoleChanged();
        var counts = _catalogue.RoleCounts();
        foreach (var chip in RoleChips.Where(c => c.Role != "All")) chip.Count = counts.GetValueOrDefault(chip.Role);
        Message = $"Marked as {MidiRoles.Plural(role).ToLowerInvariant()}.";
    }

    /// <summary>Shows the project on the Projects page, or the file in its folder.</summary>
    [RelayCommand]
    void ShowSource()
    {
        if (Selected is not { } row) return;
        var path = row.Entry.Phrase.Source.Path;
        if (!row.Entry.IsProject)
        {
            Shell.Reveal(path);
            return;
        }
        var item = _main.Projects.Items.FirstOrDefault(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                                                            i.OlderSaves.Any(o => o.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));
        var id = item is null ? (long?)null
            : item.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ? item.Id
            : item.OlderSaves.First(o => o.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Id;
        if (id is { } found) _main.Projects.Reveal(found);
        else Shell.Reveal(path);
    }

    // ---- listening ----

    internal async Task Toggle(MidiRow row, double fromTicks = 0)
    {
        if (row.IsPlaying && fromTicks <= 0) { StopPlaying(); return; }
        StopPlaying();
        var request = _listenRequest;
        var channel = row.Channel;
        if (channel.Notes.Count == 0) return;
        var tempo = row.Entry.Phrase.Source.Tempo is > 10 and < 1000 ? row.Entry.Phrase.Source.Tempo : 120;
        MidiRender.Result render;
        try { render = await UiWork.Run(() => MidiRender.Render([channel], tempo, "")); }
        catch (Exception e) when (e is IOException or OutOfMemoryException)
        {
            Message = $"Couldn't play it: {e.Message}";
            return;
        }
        if (request != _listenRequest) return;
        _player ??= Ui.Current.CreateAudioPlayer();
        _clock ??= Ui.Current.CreateTimer(TimeSpan.FromMilliseconds(30), Tick);
        try { _player.Play(render.Audio, MidiRender.Rate, $"midi-browser:{row.Entry.Id}", MidiRender.TicksToSeconds((long)fromTicks, PhraseAnalyser.Ppq, tempo.Value)); }
        catch (Exception e)
        {
            Message = $"Couldn't play it: {e.Message}";
            return;
        }
        _playing = row;
        row.IsPlaying = true;
        Tick();
        _clock.Start();
    }

    void Tick()
    {
        if (_playing is not { } row || _player is null) return;
        if (_player.Playing != $"midi-browser:{row.Entry.Id}" || _player.Position is not { } seconds)
        {
            StopPlaying();
            return;
        }
        var tempo = row.Entry.Phrase.Source.Tempo is > 10 and < 1000 ? row.Entry.Phrase.Source.Tempo.Value : 120;
        row.Position = MidiRender.SecondsToTicks(seconds, PhraseAnalyser.Ppq, tempo);
    }

    void StopPlaying()
    {
        _listenRequest++;
        _clock?.Stop();
        if (_playing is { } row)
        {
            if (_player?.Playing == $"midi-browser:{row.Entry.Id}") _player.Stop();
            row.IsPlaying = false;
            row.Position = -1;
        }
        _playing = null;
    }

    [RelayCommand]
    Task PlaySelected() => Selected is { } row ? Toggle(row) : Task.CompletedTask;

    // ---- dragging out ----

    string ExportFolder()
    {
        var root = _db.GetSetting(ProjectDetailViewModel.ExportFolderKey) is { Length: > 0 } f
            ? f
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.Create), "Music Organiser Exports");
        var dir = Path.Combine(root, "MIDI browser");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// The parts as new MIDI files in the exports folder (moved into the fitted key, when fitting), named after them. The
    /// files they came from are never touched.
    /// </summary>
    public DragPayload? MakeDrag(IReadOnlyList<object> items)
    {
        var rows = items.OfType<MidiRow>().ToList();
        if (rows.Count == 0 && Selected is { } s) rows = [s];
        if (rows.Count == 0) return null;
        var dir = ExportFolder();
        var files = rows.Select(r => WriteMidi(r, dir)).ToList();
        return new DragPayload(files, rows.Select(r => new DragCard(r.Title, r.Details, Glyphs.Midi)).ToList(),
                               rows.Count == 1 ? "1 MIDI clip" : $"{rows.Count} MIDI clips");
    }

    static string WriteMidi(MidiRow row, string dir)
    {
        var name = TextScan.SafeFileName(row.Title + (row.Fit is { Semitones: not 0 } f ? $" ({(f.Semitones > 0 ? "+" : "")}{f.Semitones})" : ""));
        var file = Path.Combine(dir, name + ".mid");
        File.WriteAllBytes(file, MidiWriter.Write(row.Channel.ToClip(), row.Entry.Phrase.Source.Tempo));
        return file;
    }

    [RelayCommand]
    async Task SaveSelected()
    {
        if (Selected is not { } row) return;
        if (await Ui.Current.PickSaveFile(TextScan.SafeFileName(row.Title) + ".mid", "MIDI file", "mid", ExportFolder()) is not { } file) return;
        File.WriteAllBytes(file, MidiWriter.Write(row.Channel.ToClip(), row.Entry.Phrase.Source.Tempo));
        Message = $"Saved {Path.GetFileName(file)}";
    }

    internal IReadOnlyList<NoteEvent> NotesOf(MidiEntry e)
    {
        try { return _index.Notes(e.Id); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { return []; }
    }

    public void Dispose()
    {
        _indexCts?.Cancel();
        StopPlaying();
        _player?.Dispose();
    }
}

/// <summary>A kind-of-part chip: "Basslines (1,204)".</summary>
public sealed partial class RoleChip(string label, string role) : ObservableObject
{
    public string Label => label;
    public string Role => role;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Text))] int _count;
    public string Text => Count > 0 ? $"{label}  {Count:N0}" : label;
}

/// <summary>One MIDI part in the list.</summary>
public sealed partial class MidiRow : ObservableObject
{
    readonly MidiBrowserViewModel _owner;
    MidiChannel? _channel;

    public MidiRow(MidiHit hit, MidiBrowserViewModel owner)
    {
        _owner = owner;
        Entry = hit.Entry;
        Fit = hit.Fit;
        Likeness = hit.Likeness;
    }

    public MidiEntry Entry { get; }
    public HarmonyFit? Fit { get; }
    public double? Likeness { get; }

    public string Title => Entry.Title.Length > 0 ? Entry.Title : Entry.SourceName;
    /// <summary>Where it's from: the project or file, and how many other saves have the same notes.</summary>
    public string From => (Entry.IsProject ? $"{Entry.SourceName} · {Entry.Phrase.Source.Daw}"
                              : $"MIDI file in {Path.GetFileName(Path.GetDirectoryName(Entry.Phrase.Source.Path))}") +
                          (Entry.Copies > 1 ? $" · in {Entry.Copies} files" : "");
    public string Role => Entry.Role;
    public string RoleLabel => MidiRoles.Plural(Entry.Role) switch { "Melodies" => "Melody", "Basslines" => "Bassline", "Arps" => "Arp", var p => p };
    public string RoleWhy => Entry.YourRole is not null ? "You said so" : Entry.Phrase.RoleWhy;

    /// <summary>Its key as it'll be used: "Am", or "Cm → Am" when it's moved to fit.</summary>
    public string KeyText
    {
        get
        {
            if (Entry.Phrase.Key is not { Confidence: >= 0.05 } k) return "";
            if (Fit is not { Semitones: not 0 } f) return k.Short;
            var moved = new KeyEstimate(((k.Tonic + f.Semitones) % 12 + 12) % 12, k.Minor, k.Confidence);
            return $"{k.Short} → {moved.Short}";
        }
    }

    public string BarsText => $"{Entry.Phrase.Bars} bar{(Entry.Phrase.Bars == 1 ? "" : "s")}";
    public string Details => string.Join(" · ", new[] { RoleLabel, KeyText, BarsText, $"{Entry.Phrase.NoteCount:N0} notes" }.Where(s => s.Length > 0));

    public bool HasFit => Fit is not null;
    public string FitText => Fit is { } f ? $"{f.Percent}%" : Likeness is { } l ? $"{Math.Round(l * 100)}%" : "";
    public string MoveText => Fit is { } f ? f.MoveText : "";
    public string FitWhy => Fit is not { } f ? "" : string.Join(" ", new[]
    {
        $"{f.Percent}% fit, {f.MoveText}.",
        f.ChordScore is { } c ? $"Its chords follow the song's {Math.Round(c * 100)}%." : "",
        f.Clash > 0.02 ? $"{Math.Round(f.Clash * 100)}% of its notes land outside the song's notes." : "Its notes all sit in the song's harmony.",
    }.Where(s => s.Length > 0));
    public string ChordsText => Entry.Chords.Count == 0 ? ""
        : string.Join(" – ", Entry.Chords.Take(8).Select(c => (Fit is { Semitones: var s and not 0 } ? c.Transposed(s) : c).Name)) +
          (Entry.Chords.Count > 8 ? " …" : "");

    /// <summary>Its notes, moved into the fitted key when fitting; read when first shown.</summary>
    public MidiChannel Channel => _channel ??= MakeChannel();

    MidiChannel MakeChannel()
    {
        var notes = _owner.NotesOf(Entry);
        if (Fit is { Semitones: not 0 } f)
            notes = notes.Select(n => n with { Key = Math.Clamp(n.Key + f.Semitones, 0, 127) }).ToList();
        // Named "Drums" for drum parts so the preview plays them as hits rather than tones.
        return new MidiChannel(Entry.Role == MidiRoles.Drums ? $"Drums · {Title}" : Title, "", PhraseAnalyser.Ppq, notes);
    }

    [ObservableProperty] double _position = -1;
    [ObservableProperty] bool _isPlaying;

    [RelayCommand]
    Task Play() => _owner.Toggle(this);

    [RelayCommand]
    Task Seek(double ticks) => _owner.Toggle(this, Math.Max(1, ticks));

    internal void RoleChanged()
    {
        OnPropertyChanged(nameof(Role));
        OnPropertyChanged(nameof(RoleLabel));
        OnPropertyChanged(nameof(RoleWhy));
        OnPropertyChanged(nameof(Details));
    }
}
