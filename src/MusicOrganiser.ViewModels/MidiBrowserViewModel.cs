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
    /// <summary>Parts play round and round until stopped ("1"), or once.</summary>
    public const string LoopKey = "midi_browser_loop";

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
    PlayPlan? _plan;
    int? _shuffle;
    CancellationTokenSource? _indexCts;
    Task? _indexing;
    int _searchRequest, _listenRequest;
    bool _loading, _indexAgain, _retryFailed;

    public MidiBrowserViewModel(LibraryDb db, MainViewModel main, MidiIndex? index = null)
    {
        _db = db;
        _main = main;
        _index = index ?? new MidiIndex(MidiIndex.PathBeside(db.FilePath));
        _indexer = new MidiIndexer(_index, db);
        Folders = new ObservableCollection<string>(db.GetList(MidiIndexer.FoldersKey));
        RoleChips = MidiRoles.All.Prepend("All").Select(r => new RoleChip(r == "All" ? "All" : MidiRoles.Plural(r), r)).ToList();
        _selectedRole = RoleChips[0];
        _includeOlderSaves = db.GetSetting(MidiIndexer.OlderSavesKey) == "1";
        _loop = db.GetSetting(LoopKey) == "1";
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

    /// <summary>Read every save of a song, not just its newest (off: one save per song, as Projects' "One row per song" shows).</summary>
    [ObservableProperty] bool _includeOlderSaves;

    /// <summary>Projects and MIDI files that couldn't be read, and why.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFailures), nameof(FailedText))] IReadOnlyList<MidiFailure> _failures = [];
    public bool HasFailures => Failures.Count > 0;
    public string FailedText => $"{Failures.Count:N0} couldn't be read";

    partial void OnIncludeOlderSavesChanged(bool value)
    {
        _db.SetSetting(MidiIndexer.OlderSavesKey, value ? "1" : "0");
        Message = value
            ? "Reading every save of each song too. Songs saved many times add a lot of near-identical parts, kept as one where the notes are the same."
            : "Reading only the newest save of each song. Parts from older saves are taken out of the list; the files themselves are untouched.";
        _ = IndexAsync();
    }

    public bool HasSelection => Selected is not null;
    /// <summary>Nothing to show yet, and nothing being read: say how to get MIDI in.</summary>
    public bool IsEmpty => !HasParts && !IsIndexing;

    // ---- fitting a harmony ----

    [ObservableProperty] string _fitSearch = "";
    [ObservableProperty] IReadOnlyList<ProjectItem> _fitChoices = [];
    [ObservableProperty] bool _fitChoicesOpen;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsFitting), nameof(IsPairing), nameof(PairButtonText))] HarmonyTarget? _fit;
    [ObservableProperty] string _fitText = "";
    public bool IsFitting => Fit is not null;

    /// <summary>
    /// Fitting to something with notes (a part, or a song): each suggestion can be played on top of it, and the card
    /// grows to show both (Solomon, 2026-10-16: "there should be a way to play midi files on top of each other").
    /// </summary>
    public bool IsPairing => Fit is { Notes.Count: > 0 };
    public string PairButtonText => Fit is { Loops: false } ? "Play with the song" : "Play together";

    // ---- loop and shuffle ----

    /// <summary>Play round and round until stopped (remembered).</summary>
    [ObservableProperty] bool _loop;

    partial void OnLoopChanged(bool value)
    {
        _db.SetSetting(LoopKey, value ? "1" : "0");
        // Carry on with what's playing, now looping (or not).
        if (_playing is { } row && _plan is { } plan) _ = Toggle(row, Math.Max(1, row.Position), plan.Together);
    }

    /// <summary>Showing a random pick in a random order (Shuffle), until the search changes.</summary>
    public bool IsShuffled => _shuffle is not null;

    [RelayCommand]
    async Task Shuffle()
    {
        var wasPlaying = _playing is not null;
        _shuffle = Random.Shared.Next();
        OnPropertyChanged(nameof(IsShuffled));
        await RefreshAsync();
        Selected = Results.FirstOrDefault();
        if (wasPlaying && Selected is { } first) await Toggle(first);
    }

    [RelayCommand]
    void Unshuffle()
    {
        if (_shuffle is null) return;
        _shuffle = null;
        OnPropertyChanged(nameof(IsShuffled));
        _ = RefreshAsync();
    }
    /// <summary>The project being fitted to, whose own parts are left out of the list.</summary>
    string? _fitPath;

    // ---- more like this ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsLiking))] MidiEntry? _like;
    public bool IsLiking => Like is not null;
    public string LikeText => Like is null ? "" : $"Parts shaped like {Like.Title}";

    // ---- longer phrases ----

    /// <summary>Showing the longer phrases this part is in (Show the whole phrase).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsShowingPhrases), nameof(PhrasesText))] MidiEntry? _phrasesOf;
    public bool IsShowingPhrases => PhrasesOf is not null;
    public string PhrasesText => PhrasesOf is null ? "" : $"Longer phrases with {PhrasesOf.Title} in them";

    partial void OnPhrasesOfChanged(MidiEntry? value) => _ = RefreshAsync();

    /// <summary>The longer phrases the selected part is in, with it lit up inside each.</summary>
    [RelayCommand]
    void ShowWholePhrase()
    {
        if (Selected?.Entry is not { Within.Count: > 0 } entry) return;
        Like = null;
        PhrasesOf = entry;
    }

    [RelayCommand]
    void ClearPhrases() => PhrasesOf = null;

    partial void OnSearchChanged(string value)
    {
        if (_shuffle is not null) { _shuffle = null; OnPropertyChanged(nameof(IsShuffled)); }
        _ = RefreshAsync(delayMs: 150);
    }
    partial void OnSelectedRoleChanged(RoleChip value) => _ = RefreshAsync();
    partial void OnFromChanged(string value) => _ = RefreshAsync();
    partial void OnLikeChanged(MidiEntry? value)
    {
        OnPropertyChanged(nameof(LikeText));
        if (value is not null) PhrasesOf = null;
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
            // Counting a few hundred thousand parts happens off the window's thread too.
            var (catalogue, counts, files) = await UiWork.Run(() =>
            {
                var c = new MidiCatalogue(_index.All());
                return (c, c.RoleCounts(), c.Entries.Select(e => e.Phrase.Source.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            });
            _catalogue = catalogue;
            HasParts = catalogue.Entries.Count > 0;
            foreach (var chip in RoleChips)
                chip.Count = chip.Role == "All" ? catalogue.Entries.Count : counts.GetValueOrDefault(chip.Role);
            Failures = await UiWork.Run(_index.Failures);
            Summary = catalogue.Entries.Count == 0
                ? "No MIDI found yet."
                : $"{catalogue.Entries.Count:N0} MIDI parts from {files:N0} files" +
                  (IncludeOlderSaves ? "" : " · newest save of each song") +
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
                                  From == YourProjects ? "projects" : From == YourLibraries ? "files" : null, Fit, Like, Leave: _fitPath,
                                  Within: PhrasesOf, Notes: NotesOf, Shuffle: _shuffle);
        var (hits, matching) = await UiWork.Run(() => (catalogue.Search(query, out var n), n));
        if (request != _searchRequest) return;
        var keep = Selected?.Entry;
        Results = hits.Select(h => new MidiRow(h, this)).ToList();
        Selected = Results.FirstOrDefault(r => r.Entry == keep) ?? Results.FirstOrDefault();
        if (PhrasesOf is not null) Message = "";
        ResultText = matching == 0 ? (HasParts ? "Nothing matches. Try fewer words, or another kind." : "")
            : PhrasesOf is not null ? (matching == 1 ? "1 longer phrase" : $"{matching:N0} longer phrases") + ", longest first. The part you picked is lit up in each."
            : _shuffle is not null ? $"Shuffled · {hits.Count:N0} picked at random from {matching:N0}" + (Fit is not null ? " of the best fits" : "")
            : Fit is not null ? $"Best fits first · {Math.Min(matching, hits.Count):N0} shown of {matching:N0}, each moved into the right key and octave"
            : Like is not null ? $"Most alike first · {hits.Count:N0} of {matching:N0}"
            : hits.Count < matching ? $"{hits.Count:N0} of {matching:N0}, newest first. Search to narrow it down." : $"{matching:N0}, newest first";
    }

    // ---- indexing ----

    /// <summary>After a scan: bring the MIDI index up to date, once the page has been used.</summary>
    public Task AfterScanAsync() => _db.GetSetting(UsedKey) == "1" ? IndexAsync() : Task.CompletedTask;

    [RelayCommand]
    Task Reindex() => IndexAsync();

    /// <summary>Reads again just the files that failed last time (and anything new or changed).</summary>
    [RelayCommand]
    Task RetryFailed() => IndexAsync(retryFailed: true);

    [RelayCommand]
    void StopIndexing() => _indexCts?.Cancel();

    public Task IndexAsync(bool retryFailed = false)
    {
        _retryFailed |= retryFailed;
        if (_indexing is { IsCompleted: false })
        {
            // A setting changed (or a retry was asked for) part way: go round once more when this run ends.
            _indexAgain = true;
            return _indexing;
        }
        return _indexing = RunIndexAsync();
    }

    async Task RunIndexAsync()
    {
        do
        {
            _indexAgain = false;
            var retry = _retryFailed;
            _retryFailed = false;
            await IndexOnceAsync(retry);
        } while (_indexAgain && _indexCts is { IsCancellationRequested: false });
    }

    async Task IndexOnceAsync(bool retryFailed)
    {
        IsIndexing = true;
        _indexCts = new CancellationTokenSource();
        var card = _main.Activities.Start(retryFailed ? "Trying the failed MIDI again" : "Reading MIDI",
                                          retryFailed ? $"Reading the {Failures.Count:N0} that couldn't be read" : "Looking for new and changed projects and MIDI files",
                                          _indexCts.Cancel);
        var progress = new ThrottledProgress<ScanProgress>(p =>
        {
            IndexPercent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
            IndexText = p.Total > 0 ? $"Reading MIDI: {p.Done:N0} of {p.Total:N0}" : p.Stage;
            card.Report(p.Total > 0 ? IndexPercent : -1, p.Total > 0 ? $"{p.Done:N0} of {p.Total:N0} files · {p.Current}" : p.Stage);
        }, 250);
        try
        {
            var token = _indexCts.Token;
            var result = await Task.Run(() => _indexer.Run(progress, token, retryFailed), token);
            if (result.Read > 0 || result.Forgotten > 0 || _catalogue is null) await LoadAsync();
            else Failures = await UiWork.Run(_index.Failures);
            card.Finish(result.Read == 0 ? "MIDI up to date" : "MIDI read",
                        result.Read == 0 ? $"{_catalogue?.Entries.Count ?? 0:N0} parts" :
                        $"{result.Read:N0} file{(result.Read == 1 ? "" : "s")}, {result.Phrases:N0} parts" +
                        (result.Failed > 0 ? $"; {result.Failed} couldn't be read" : ""));
            if (retryFailed)
                Message = result.Failed == 0 ? "Every file that failed before has now been read."
                    : $"{result.Failed:N0} still couldn't be read. Click \"{FailedText}\" to see why.";
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
        Message = $"Listening to the harmony of {name}…";
        var (target, why) = await HarmonyOfAsync(path, name);
        if (target is null) Message = why;
        else FitToHarmony(target, leave: path);
    }

    /// <summary>A project's harmony (its pitched channels' notes and chords), or why there isn't one.</summary>
    public static async Task<(HarmonyTarget? Target, string Why)> HarmonyOfAsync(string path, string name)
    {
        if (Readers.For(path) is not { } reader) return (null, $"The app can't read the notes of {name} yet.");
        try
        {
            var target = await UiWork.Run(() => HarmonyTarget.FromChannels(name, reader.ExtractChannels(path)));
            return (target, target is null ? $"{name} has too few pitched notes to fit MIDI to." : "");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (null, $"Couldn't read {name}: {e.Message}");
        }
    }

    /// <summary>Every part, read from the index the first time it's wanted (by this page or a song's MIDI tab).</summary>
    public async Task<MidiCatalogue?> CatalogueAsync()
    {
        if (_catalogue is null && !_loading) Opened();
        while (_loading && _catalogue is null) await Task.Delay(50);
        return _catalogue;
    }

    /// <summary>"What goes with this?": parts that fit the selected part's harmony.</summary>
    [RelayCommand]
    void FitToSelected()
    {
        if (Selected is not { } row) return;
        var e = row.Entry;
        Search = "";   // the words found this part; its partners won't share them
        var chroma = row.Semitones != 0 ? Rotate(e.Chroma, row.Semitones) : e.Chroma;
        var chords = row.Semitones != 0 ? e.Chords.Select(c => c.Transposed(row.Semitones)).ToList() : e.Chords;
        var key = e.Phrase.Key is { } k && row.Semitones != 0 ? k with { Tonic = ((k.Tonic + row.Semitones) % 12 + 12) % 12 } : e.Phrase.Key;
        // Its notes as they play here (moved, octave and all), from its first bar, so partners are checked against them
        // in time and played on top of them.
        FitToHarmony(HarmonyTarget.FromPart(row.Title, e.Role, row.BarNotes, chroma, chords, key));
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
        StopPlaying();
        _fitPath = leave;
        Like = null;
        PhrasesOf = null;
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
        StopPlaying();
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
        foreach (var r in Results.Where(r => r.Entry == row.Entry)) r.RoleChanged();
        if (!Results.Contains(row)) row.RoleChanged();
        var counts = _catalogue.RoleCounts();
        foreach (var chip in RoleChips.Where(c => c.Role != "All")) chip.Count = counts.GetValueOrDefault(chip.Role);
        Message = $"Marked as {MidiRoles.Plural(role).ToLowerInvariant()}.";
    }

    /// <summary>Moves a part up or down octaves (from how it was saved); remembered for every copy of its notes.</summary>
    internal async Task SetOctave(MidiRow row, int? octaves)
    {
        if (octaves is { } o) octaves = Math.Clamp(o, -3, 3);
        await UiWork.Run(() => { _index.SetYourOctave(row.Entry.Phrase.Hash, octaves); return 0; });
        row.Entry.YourOctave = octaves;
        foreach (var r in Results.Where(r => r.Entry == row.Entry)) r.MoveChanged();
        if (!Results.Contains(row)) row.MoveChanged();
        Message = octaves is null ? "Back to the octave the app picks." : "";
        // Hear it straight away where it now sits.
        if (_playing?.Entry == row.Entry && _plan is { } plan) await Toggle(_playing, Math.Max(1, _playing.Position), plan.Together);
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

    /// <summary>Played on its own, or on top of what's being fitted to (<see cref="IsPairing"/>).</summary>
    internal async Task Toggle(MidiRow row, double fromTicks = 0, bool? together = null)
    {
        var both = (together ?? row.CanPlayTogether) && row.CanPlayTogether;
        if (row.IsPlaying && fromTicks <= 0 && (together is null || _plan?.Together == both)) { StopPlaying(); return; }
        StopPlaying();
        var request = _listenRequest;
        var tempo = row.Entry.Phrase.Source.Tempo is > 10 and < 1000 ? row.Entry.Phrase.Source.Tempo.Value : 120;
        if (PlanFor(row, both, tempo) is not { } plan) return;
        MidiRender.Result render;
        try
        {
            render = await UiWork.Run(() =>
            {
                var r = MidiRender.Render(plan.Channels, tempo, "");
                // A loop is cut exactly where it goes round, so it starts again without a gap.
                if (plan.Loop)
                {
                    var samples = (int)Math.Min(r.Audio.Length, Math.Round(MidiRender.TicksToSeconds(plan.Total, PhraseAnalyser.Ppq, tempo) * MidiRender.Rate));
                    if (samples > 0 && samples < r.Audio.Length) r = r with { Audio = r.Audio[..samples] };
                }
                return r;
            });
        }
        catch (Exception e) when (e is IOException or OutOfMemoryException)
        {
            Message = $"Couldn't play it: {e.Message}";
            return;
        }
        if (request != _listenRequest) return;
        if (_player is null)
        {
            _player = Ui.Current.CreateAudioPlayer();
            _player.Stopped += () => { if (_plan is { Loop: true }) GoRound(); };
        }
        _clock ??= Ui.Current.CreateTimer(TimeSpan.FromMilliseconds(30), Tick);
        plan = plan with { Audio = render.Audio, Tempo = tempo };
        // Where to start: ticks on the part's own roll (from its first note), which a loop plays from its first bar.
        var from = fromTicks <= 0 ? 0 : fromTicks + (plan.Loop || plan.Together ? plan.Lead : 0);
        var start = MidiRender.TicksToSeconds((long)(Math.Max(0, from) % Math.Max(1, plan.Length)), PhraseAnalyser.Ppq, tempo);
        try { _player.Play(render.Audio, MidiRender.Rate, plan.Key, start); }
        catch (Exception e)
        {
            Message = $"Couldn't play it: {e.Message}";
            return;
        }
        _plan = plan;
        _playing = row;
        row.IsPlaying = true;
        row.IsPlayingTogether = plan.Together;
        Tick();
        _clock.Start();
    }

    /// <summary>
    /// What to play: the part alone (from its first note, once), or from its first bar going round, or with the harmony
    /// it's fitted to on top, both going round from the same bar until they meet again.
    /// </summary>
    PlayPlan? PlanFor(MidiRow row, bool together, double tempo)
    {
        if (row.Channel.Notes.Count == 0) return null;
        var key = $"midi-browser:{row.Entry.Id}{(together ? ":together" : "")}";
        if (!together && !Loop)
            return new PlayPlan([row.Channel], row.Channel.End, row.Channel.End, 0, false, false, key);

        var mine = row.BarNotes;
        var period = row.LoopLength;
        var length = period;
        var channels = new List<MidiChannel>();
        List<NoteEvent>? partner = null;
        if (together && row.PartnerNotes is { Count: > 0 } theirs)
        {
            partner = [.. theirs];
            length = row.PairLength;
        }
        // Going round: enough times over to play for a minute or so without a break.
        var once = MidiRender.TicksToSeconds(length, PhraseAnalyser.Ppq, tempo);
        var times = Loop ? (int)Math.Clamp(Math.Ceiling(60 / Math.Max(0.1, once)), 1, Math.Max(1, (MidiRender.MaxSeconds - 4) / Math.Max(0.1, once))) : 1;
        var total = length * times;
        if (partner is not null)
            channels.Add(new MidiChannel(row.With?.Name ?? "With", "", PhraseAnalyser.Ppq, Repeat(partner, row.PartnerLength, total)));
        channels.Add(row.Channel with { Notes = Repeat(mine, period, total) });
        return new PlayPlan(channels, length, total, row.Lead, partner is not null, Loop, key) { RowLength = period };
    }

    /// <summary>The notes over and over, every <paramref name="period"/> ticks, up to <paramref name="total"/>.</summary>
    public static List<NoteEvent> Repeat(IReadOnlyList<NoteEvent> notes, long period, long total)
    {
        var list = new List<NoteEvent>();
        if (period <= 0) return list;
        for (long at = 0; at < total; at += period)
            foreach (var n in notes)
                if (n.Start < period && at + n.Start < total) list.Add(n with { Start = at + n.Start });
        return list;
    }

    /// <summary>A loop played to its end: round again from the top.</summary>
    void GoRound()
    {
        if (_plan is not { Loop: true, Audio: { } audio } plan || _playing is null || _player is null) return;
        if (_player.Playing == plan.Key) return;
        try { _player.Play(audio, MidiRender.Rate, plan.Key); }
        catch (Exception e) { Message = $"Couldn't play it: {e.Message}"; StopPlaying(); }
    }

    void Tick()
    {
        if (_playing is not { } row || _player is null || _plan is not { } plan) return;
        if (_player.Playing != plan.Key || _player.Position is not { } seconds)
        {
            // Played to its end: a loop goes round; anything else is finished.
            if (plan.Loop && _player.Playing is null) GoRound();
            if (_player.Playing != plan.Key) StopPlaying();
            return;
        }
        var ticks = MidiRender.SecondsToTicks(seconds, PhraseAnalyser.Ppq, plan.Tempo);
        if (plan.Loop || plan.Together)
        {
            row.Position = ticks % Math.Max(1, plan.RowLength) - plan.Lead;
            row.PairPosition = plan.Together ? ticks % Math.Max(1, plan.Length) : -1;
        }
        else row.Position = ticks;
    }

    void StopPlaying()
    {
        _listenRequest++;
        _clock?.Stop();
        if (_playing is { } row)
        {
            if (_plan is { } plan && _player?.Playing == plan.Key) _player.Stop();
            row.IsPlaying = false;
            row.IsPlayingTogether = false;
            row.Position = -1;
            row.PairPosition = -1;
        }
        _playing = null;
        _plan = null;
    }

    [RelayCommand]
    Task PlaySelected() => Selected is { } row ? Toggle(row) : Task.CompletedTask;

    /// <summary>The part picked, on its own even when fitting.</summary>
    [RelayCommand]
    Task PlaySelectedAlone() => Selected is { } row ? Toggle(row, together: false) : Task.CompletedTask;

    /// <summary>The part picked on top of what it's fitted to.</summary>
    [RelayCommand]
    Task PlaySelectedTogether() => Selected is { } row ? Toggle(row, together: true) : Task.CompletedTask;

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
        var name = TextScan.SafeFileName(row.Title + (row.Move != 0 ? $" ({(row.Move > 0 ? "+" : "")}{row.Move})" : ""));
        var file = Path.Combine(dir, name + ".mid");
        File.WriteAllBytes(file, MidiWriter.Write(row.FileClip(), row.Entry.Phrase.Source.Tempo));
        return file;
    }

    [RelayCommand]
    async Task SaveSelected()
    {
        if (Selected is not { } row) return;
        if (await Ui.Current.PickSaveFile(TextScan.SafeFileName(row.Title) + ".mid", "MIDI file", "mid", ExportFolder()) is not { } file) return;
        File.WriteAllBytes(file, MidiWriter.Write(row.FileClip(), row.Entry.Phrase.Source.Tempo));
        Message = $"Saved {Path.GetFileName(file)}";
    }

    internal IReadOnlyList<NoteEvent> NotesOf(MidiEntry e)
    {
        try { return _index.Notes(e.Id, e.Phrase.Hash); }
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

    /// <param name="with">What it's fitted to, to play it with (the MIDI page's fit unless given: a song's MIDI tab gives its song).</param>
    public MidiRow(MidiHit hit, MidiBrowserViewModel owner, HarmonyTarget? with = null)
    {
        _owner = owner;
        With = hit.Fit is null ? null : with ?? owner.Fit;
        Entry = hit.Entry;
        Fit = hit.Fit;
        Likeness = hit.Likeness;
    }

    public MidiEntry Entry { get; }
    public HarmonyFit? Fit { get; }
    public double? Likeness { get; }
    /// <summary>What it's fitted to, which it can be played with.</summary>
    public HarmonyTarget? With { get; }
    public bool CanPlayTogether => With is { Notes.Count: > 0 };

    const long Bar = 4 * PhraseAnalyser.Ppq;

    // ---- where it plays: key and octave ----

    /// <summary>Semitones it's moved to fit the key.</summary>
    public int Semitones => Fit?.Semitones ?? 0;

    /// <summary>
    /// Octaves it's moved: yours when you've moved it (remembered), otherwise where that kind of part plays in what it's
    /// fitted to (Solomon, 2026-10-16: "some things are not in the correct octave"). Drums stay put: their notes are sounds.
    /// </summary>
    public int Octaves => Entry.Role == MidiRoles.Drums ? 0 : Entry.YourOctave ?? Fit?.Octaves ?? 0;

    /// <summary>Semitones it's moved in all.</summary>
    public int Move => Semitones + 12 * Octaves;

    public bool CanChangeOctave => Entry.Role != MidiRoles.Drums;
    public string OctaveText => Octaves switch { 0 => "Octave as saved", > 0 => $"Octave +{Octaves}", _ => $"Octave {Octaves}" };
    public bool IsYourOctave => Entry.YourOctave is not null;
    public string OctaveWhy =>
        Entry.YourOctave is not null ? "You moved it. The app remembers this for every copy of these notes. Reset lets the app pick again."
        : Fit is { Octaves: not 0 } ? $"Moved so it plays where {Article(RoleLabel.ToLowerInvariant())} {RoleLabel.ToLowerInvariant()} plays in {With?.Name ?? "the song"}."
        : "Where it was saved. − and + move it an octave, and the app remembers.";

    static string Article(string word) => word.Length > 0 && "aeiou".Contains(word[0]) ? "an" : "a";

    [RelayCommand]
    Task OctaveUp() => _owner.SetOctave(this, Octaves + 1);

    [RelayCommand]
    Task OctaveDown() => _owner.SetOctave(this, Octaves - 1);

    [RelayCommand]
    Task ResetOctave() => _owner.SetOctave(this, null);

    /// <summary>The kinds of part, for changing what the app thinks it is in one click.</summary>
    public IReadOnlyList<RoleOption> RoleOptions => _roleOptions ??= MidiRoles.All.Select(r => new RoleOption(this, r)).ToList();
    IReadOnlyList<RoleOption>? _roleOptions;

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

    /// <summary>"Joined from 4 clips" for a longer phrase; "Part of a 16-bar phrase" for a clip that's in one.</summary>
    public string PhraseText =>
        Entry.IsJoined ? $"Joined from {Entry.ClipCount} clips"
        : Entry.Within is { Count: > 0 } w ? (w.Count == 1 ? $"Part of {Article(w[0].Phrase.Bars)} {w[0].Phrase.Bars}-bar phrase" : $"Part of {w.Count} longer phrases")
        : "";

    /// <summary>"an" before a number said with a vowel sound first: an 8-bar, an 11-bar, an 18-bar.</summary>
    static string Article(int n) => n is 8 or 11 or 18 or (>= 80 and <= 89) ? "an" : "a";
    public bool HasPhrase => PhraseText.Length > 0;
    public bool CanShowWholePhrase => Entry.Within is { Count: > 0 };
    public string PhraseWhy => Entry.IsJoined
        ? "Clips that play one after another on this track in the arrangement, joined into one phrase. The lines on the notes show where each clip starts."
        : "This clip plays next to others on its track in the arrangement, as part of a longer phrase. Show the whole phrase to see it.";

    /// <summary>For a joined phrase: where each clip sits on the roll, the part being looked for lit up.</summary>
    public IReadOnlyList<RollSpan>? Spans
    {
        get
        {
            if (!Entry.IsJoined) return null;
            var lead = Channel.Notes.Count == 0 ? 0 : _lead;
            var lit = _owner.PhrasesOf?.Phrase.Hash;
            return Entry.Members.Select(m => new RollSpan(m.Start - lead, m.Length, m.Hash == lit)).ToList();
        }
    }
    public string Details => string.Join(" · ", new[] { RoleLabel, KeyText, BarsText, $"{Entry.Phrase.NoteCount:N0} notes" }.Where(s => s.Length > 0));

    public bool HasFit => Fit is not null;
    public string FitText => Fit is { } f ? $"{f.Percent}%" : Likeness is { } l ? $"{Math.Round(l * 100)}%" : "";
    /// <summary>"up 3 semitones", "up 3 semitones, an octave down", "an octave up".</summary>
    public string MoveText
    {
        get
        {
            if (Fit is null && Octaves == 0) return "";
            var octaves = Octaves switch { 0 => "", 1 => "an octave up", -1 => "an octave down", > 0 => $"{Octaves} octaves up", _ => $"{-Octaves} octaves down" };
            if (Semitones == 0) return octaves.Length > 0 ? octaves : "as it is";
            var semis = Fit!.MoveText;
            return octaves.Length > 0 ? $"{semis}, {octaves}" : semis;
        }
    }
    public string FitWhy => Fit is not { } f ? "" : string.Join(" ", new[]
    {
        $"{f.Percent}% fit, {MoveText}.",
        f.Together is { } t ? $"Played together, {Math.Round(t * 100)}% of its notes sit with what sounds at the same moment." : "",
        With is { Loops: false } && f.Together is not null ? $"Fits best from bar {f.At / Bar + 1} of the song." : "",
        f.ChordScore is { } c ? $"Its chords follow {(With is { Loops: true } ? "the part's" : "the song's")} {Math.Round(c * 100)}%." : "",
        f.Clash > 0.02 ? $"{Math.Round(f.Clash * 100)}% of its notes are on notes the other hardly plays." : "Its notes are all ones the other plays too.",
    }.Where(s => s.Length > 0));
    public string ChordsText => Entry.Chords.Count == 0 ? ""
        : string.Join(" – ", Entry.Chords.Take(8).Select(c => (Fit is { Semitones: var s and not 0 } ? c.Transposed(s) : c).Name)) +
          (Entry.Chords.Count > 8 ? " …" : "");

    /// <summary>Its notes, moved into the fitted key when fitting; read when first shown.</summary>
    /// <summary>
    /// The part as shown and played: moved into the fitted key, and starting right at its first note, so a part that
    /// only comes in at the end of its bar doesn't sit squashed at the end of the roll or play silence first
    /// (Solomon, 2026-10-15: "midi with alot of space either side truncate them").
    /// </summary>
    public MidiChannel Channel => _channel ??= MakeChannel(fromFirstNote: true);

    /// <summary>
    /// The part as written to a .mid file: the same, but from the start of its first note's bar, so a pickup still lands
    /// before the beat when it's dropped on a bar line in the DAW.
    /// </summary>
    public MidiClip FileClip() => MakeChannel(fromFirstNote: false).ToClip();

    long _lead;

    /// <summary>How far the part's first note is into its first bar (ticks): the roll starts there.</summary>
    internal long Lead
    {
        get
        {
            _ = Channel;
            return _lead;
        }
    }

    MidiChannel MakeChannel(bool fromFirstNote)
    {
        var notes = _owner.NotesOf(Entry);
        var lead = fromFirstNote && notes.Count > 0 ? notes.Min(n => n.Start) : 0;
        if (fromFirstNote) _lead = lead;
        var move = Move;
        if (move != 0 || lead > 0)
            notes = notes.Select(n => n with { Start = n.Start - lead, Key = Math.Clamp(n.Key + move, 0, 127) }).ToList();
        // Named "Drums" for drum parts so the preview plays them as hits rather than tones.
        return new MidiChannel(Entry.Role == MidiRoles.Drums ? $"Drums · {Title}" : Title, "", PhraseAnalyser.Ppq, notes);
    }

    /// <summary>Its notes as they play (moved), from the start of its first bar: how it loops, and lines up with others.</summary>
    public IReadOnlyList<NoteEvent> BarNotes => _barNotes ??= MakeChannel(fromFirstNote: false).Notes;
    IReadOnlyList<NoteEvent>? _barNotes;

    /// <summary>How long it plays before going round: its bars.</summary>
    public long LoopLength => Math.Max(Math.Max(1, Entry.Phrase.Bars) * Bar,
                                       BarNotes.Count == 0 ? Bar : (BarNotes.Max(n => n.Start) / Bar + 1) * Bar);

    // ---- played together with what it's fitted to ----

    /// <summary>What it's fitted to, as it plays with this part: the whole part (it loops too), or the stretch of the song where this fits best.</summary>
    public IReadOnlyList<NoteEvent>? PartnerNotes
    {
        get
        {
            if (With is not { Notes.Count: > 0 } target) return null;
            if (target.Loops) return target.Notes;
            long from = Fit?.At ?? 0, to = from + LoopLength;
            return target.Notes.Where(n => n.Start >= from && n.Start < to)
                               .Select(n => n with { Start = n.Start - from, Length = Math.Min(n.Length, to - n.Start) }).ToList();
        }
    }

    /// <summary>How long what it's fitted to plays before going round.</summary>
    public long PartnerLength => With is { Loops: true } target ? Math.Max(Bar, target.Length) : LoopLength;

    /// <summary>How long the two play before both are back at the start together (32 bars at most, else the longer).</summary>
    public long PairLength
    {
        get
        {
            long a = LoopLength, b = PartnerLength;
            if (a == b) return a;
            var both = a / Gcd(a, b) * b;
            return both <= 32 * Bar ? both : Math.Max(a, b);
        }
    }

    static long Gcd(long a, long b) => b == 0 ? Math.Max(1, a) : Gcd(b, a % b);

    /// <summary>For the card: what it's fitted to and this part, both over the same stretch, one above the other.</summary>
    public MidiChannel? PairPartner => PartnerNotes is { Count: > 0 } theirs
        ? new MidiChannel(With?.Name ?? "", "", PhraseAnalyser.Ppq, MidiBrowserViewModel.Repeat(theirs, PartnerLength, PairLength))
        : null;
    public MidiChannel? PairMine => PartnerNotes is { Count: > 0 }
        ? Channel with { Notes = MidiBrowserViewModel.Repeat(BarNotes, LoopLength, PairLength) }
        : null;

    public string PairText => With is not { Notes.Count: > 0 } target ? ""
        : target.Loops ? $"With {target.Name}"
        : $"With {target.Name}, bars {(Fit?.At ?? 0) / Bar + 1}–{((Fit?.At ?? 0) + LoopLength) / Bar}";

    [ObservableProperty] double _position = -1;
    /// <summary>Where playback is on the card's together view (ticks from the first bar), or −1.</summary>
    [ObservableProperty] double _pairPosition = -1;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsPlayingAlone))] bool _isPlaying;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsPlayingAlone))] bool _isPlayingTogether;
    public bool IsPlayingAlone => IsPlaying && !IsPlayingTogether;

    [RelayCommand]
    Task Play() => _owner.Toggle(this);

    [RelayCommand]
    Task Seek(double ticks) => _owner.Toggle(this, Math.Max(1, ticks));

    /// <summary>A click on the together view: both from there.</summary>
    [RelayCommand]
    Task PairSeek(double ticks) => _owner.Toggle(this, Math.Max(1, (ticks % Math.Max(1, LoopLength)) - Lead), together: true);

    internal void RoleChanged()
    {
        OnPropertyChanged(nameof(Role));
        OnPropertyChanged(nameof(RoleLabel));
        OnPropertyChanged(nameof(RoleWhy));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(CanChangeOctave));
        if (_roleOptions is not null) foreach (var o in _roleOptions) o.Changed();
        MoveChanged();
    }

    /// <summary>Moved up or down an octave: everything shown and played follows.</summary>
    internal void MoveChanged()
    {
        _channel = null;
        _barNotes = null;
        foreach (var name in new[] { nameof(Channel), nameof(Spans), nameof(BarNotes), nameof(Octaves), nameof(Move), nameof(OctaveText),
                                     nameof(IsYourOctave), nameof(OctaveWhy), nameof(MoveText), nameof(FitWhy), nameof(PairPartner),
                                     nameof(PairMine), nameof(PairText) })
            OnPropertyChanged(name);
    }
}

/// <summary>One kind of part on a part's card: click to say that's what it is.</summary>
public sealed partial class RoleOption(MidiRow row, string role) : ObservableObject
{
    public string Role => role;
    public string Label => role switch { MidiRoles.Bass => "Bassline", MidiRoles.Other => "Something else", _ => role };
    public bool IsCurrent => row.Role == role;
    internal void Changed() => OnPropertyChanged(nameof(IsCurrent));
}

/// <summary>What's playing: the channels rendered, how long one go round is, and how it maps back onto the rolls.</summary>
/// <param name="Length">One go round (ticks): the part, or both parts until they meet again.</param>
/// <param name="Total">Ticks rendered (several goes round when looping).</param>
/// <param name="Lead">How far the part's roll is from its first bar (it starts at its first note).</param>
sealed record PlayPlan(IReadOnlyList<MidiChannel> Channels, long Length, long Total, long Lead, bool Together, bool Loop, string Key)
{
    /// <summary>One go round of the part itself.</summary>
    public long RowLength { get; init; } = Length;
    public float[]? Audio { get; init; }
    public double Tempo { get; init; } = 120;
}

/// <summary>A stretch of a note roll (ticks): where a clip sits inside a joined phrase, lit when it's the one looked for.</summary>
public readonly record struct RollSpan(long Start, long Length, bool Lit);
