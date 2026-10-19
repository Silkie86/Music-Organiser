using System.Globalization;
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
using Microsoft.ML.OnnxRuntime;

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
    /// <summary>A SoundFont you chose to play MIDI with, instead of the downloaded General MIDI one.</summary>
    public const string SoundFontKey = "midi_soundfont";
    /// <summary>The General MIDI sound each kind of part plays with ("midi_sound_Bass"), and what it's fitted to ("midi_sound_with_Chords").</summary>
    public const string SoundKey = "midi_sound_";

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
    CancellationTokenSource? _searchCts;
    PlayPlan? _plan;
    int? _shuffle;
    CancellationTokenSource? _indexCts;
    Task? _indexing;
    int _searchRequest, _listenRequest;
    (MidiQuery, float[]?)? _lastAsked;

    /// <summary>Results for a different search than last time are in: the list goes back to the top.</summary>
    public event EventHandler? NewSearchShown;
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
        if (double.TryParse(db.GetSetting(NameWeightKey), NumberStyles.Float, CultureInfo.InvariantCulture, out var weight))
            _nameWeight = Math.Clamp(weight, 0, 3);
        _loop = db.GetSetting(LoopKey) == "1";
        GmSounds.ChosenFile = db.GetSetting(SoundFontKey) is { Length: > 0 } chosen ? chosen : null;
        _hasGoodSounds = GmSounds.IsInstalled;
        LoadOptions(db);
    }

    // ---- sounds ----

    public static IReadOnlyList<GmSounds.Choice> SoundChoices => GmSounds.Choices;

    /// <summary>A General MIDI SoundFont is there to play parts with real instrument sounds.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SoundsText))] bool _hasGoodSounds;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SoundsText))] bool _isDownloadingSounds;
    [ObservableProperty] string _soundsProgress = "";

    public string SoundsText => IsDownloadingSounds ? SoundsProgress
        : HasGoodSounds ? $"Parts play with General MIDI sounds from {Path.GetFileNameWithoutExtension(GmSounds.FileInUse)}. Pick a sound for each part on its card."
        : $"Parts play with one stand-in tone. Get {GmSounds.DownloadName}, a free General MIDI sound set (about 32 MB, downloaded once), " +
          "so basslines sound like a bass and chords like a piano. Or choose a SoundFont (.sf2) you already have.";

    /// <summary>The sound parts of this kind play with (remembered).</summary>
    internal GmSounds.Choice SoundFor(string role, bool with = false) =>
        GmSounds.ChoiceFor(int.TryParse(_db.GetSetting(SoundKey + (with ? "with_" : "") + role), out var p) ? p : GmSounds.DefaultFor(role));

    internal void SetSound(string role, GmSounds.Choice? choice, bool with = false)
    {
        if (choice is null || choice == SoundFor(role, with)) return;
        _db.SetSetting(SoundKey + (with ? "with_" : "") + role, choice.Program.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (with) OnPropertyChanged(nameof(WithSound));
        else foreach (var r in Results.Where(r => r.Role == role)) r.SoundChanged();
        _ = RefreshPlayingAsync();
    }

    /// <summary>What kind of part the thing being fitted to is: the part's, or chords for a song.</summary>
    string WithRole => _fitSource?.Role ?? MidiRoles.Chords;

    /// <summary>The sound what it's fitted to plays with.</summary>
    public GmSounds.Choice WithSound
    {
        get => SoundFor(WithRole, with: true);
        set => SetSound(WithRole, value, with: true);
    }

    [RelayCommand]
    async Task GetSounds()
    {
        if (IsDownloadingSounds) return;
        IsDownloadingSounds = true;
        using var cts = new CancellationTokenSource();
        var card = _main.Activities.Start($"Downloading {GmSounds.DownloadName}", "Connecting…", cts.Cancel);
        try
        {
            var progress = new Progress<(long Done, long? Total)>(p =>
            {
                var size = $"{p.Done / 1048576.0:N1} MB" + (p.Total is { } t ? $" of {t / 1048576.0:N1} MB" : "");
                SoundsProgress = $"Downloading {GmSounds.DownloadName}: {size}";
                OnPropertyChanged(nameof(SoundsText));
                card.Report(p.Total is > 0 ? 100.0 * p.Done / p.Total.Value : -1, size);
            });
            await GmSounds.DownloadAsync(progress, ct: cts.Token);
            Message = "Better sounds are in. Each part plays with a sound for its kind; pick another on its card.";
            card.Finish("Better sounds installed", GmSounds.DownloadName);
        }
        catch (OperationCanceledException) { card.Stop("Download stopped", "Press Get better sounds to try again"); }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Message = $"The sounds didn't download: {e.Message} Check the internet connection and try again.";
            card.Fail("Download failed", e.Message);
        }
        finally
        {
            IsDownloadingSounds = false;
            HasGoodSounds = GmSounds.IsInstalled;
        }
        await RefreshPlayingAsync();
    }

    [RelayCommand]
    async Task ChooseSoundFont()
    {
        if (await Ui.Current.PickFile("Choose a SoundFont to play MIDI with", "SoundFont", "sf2") is not { } file) return;
        if (!GmSounds.LooksLikeSoundFont(file))
        {
            Message = $"{Path.GetFileName(file)} isn't a SoundFont (.sf2), so the sounds stay as they are.";
            return;
        }
        _db.SetSetting(SoundFontKey, file);
        GmSounds.ChosenFile = file;
        HasGoodSounds = GmSounds.IsInstalled;
        Message = $"Playing MIDI with {Path.GetFileName(file)}. The file is only read, never changed.";
        await RefreshPlayingAsync();
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
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsFitting), nameof(IsPairing), nameof(PairButtonText), nameof(FitName), nameof(CanFindForSection), nameof(SectionHint))] HarmonyTarget? _fit;
    public string FitName => _sectionTarget?.Name ?? Fit?.Name ?? "";
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
        _ = RefreshPlayingAsync();
    }

    // ---- loop points, and parts for just some bars ----

    /// <summary>
    /// The bars to loop on the piano roll (ticks from its first bar), dragged out along the bar numbers, or null for all
    /// of it (Solomon, 2026-10-17: "there should be a way to set the loop points"). Kept while trying other parts.
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSection), nameof(SectionText), nameof(FindForSectionText), nameof(CanFindForSection))] RollRange? _section;
    public bool HasSection => Section is not null;
    public string SectionText => Section is { } s ? $"Looping {s.Describe()}" : "";
    public string FindForSectionText => Section is { } s ? $"Find parts for {s.Describe()}" : "";
    /// <summary>Parts for just the looped bars: when fitting, which gives something to fit them to.</summary>
    public bool CanFindForSection => Section is not null && Fit is not null;
    public string SectionHint => Fit is not null
        ? "Drag along the bar numbers to loop some bars and find parts for just those bars."
        : "Drag along the bar numbers to loop some bars.";

    partial void OnSectionChanged(RollRange? value)
    {
        foreach (var r in Results) r.SectionChanged();
        if (_frame is not null)
        {
            // Finding parts for some bars: they follow the bars you pick, and all of them again when cleared.
            if (value is { } s) FitSection(s);
            else WholeFit();
        }
        // Loop points loop.
        if (value is not null && !Loop) Loop = true;
        else _ = RefreshPlayingAsync();
    }

    [RelayCommand]
    void SetSection(RollRange? range) => Section = range is { } r && r.Length >= PhraseAnalyser.Ppq / 4 ? r : null;

    [RelayCommand]
    void ClearSection() => Section = null;

    /// <summary>
    /// Finding parts for some bars: what they're played with stays as it was on the roll when you asked (the part, or
    /// the stretch of the song), and each part tried plays in those bars.
    /// </summary>
    internal sealed record SectionFrame(IReadOnlyList<NoteEvent> Guide, long Length, int Octaves, string PairText)
    {
        public IReadOnlyList<NoteEvent> GuideAt(int octaves) => octaves == Octaves ? Guide
            : Guide.Select(n => n with { Key = Math.Clamp(n.Key + 12 * (octaves - Octaves), 0, 127) }).ToList();
    }

    SectionFrame? _frame;
    HarmonyTarget? _sectionTarget;
    string _wholeFitText = "";

    /// <summary>The list is ranked for the bars you picked, not the whole part or song.</summary>
    public bool IsFittingSection => _sectionTarget is not null;

    internal SectionFrame? FrameFor(MidiRow row) => _frame is not null && row.With is { } with && ReferenceEquals(with, Fit) ? _frame : null;

    /// <summary>
    /// Parts that fit just the bars you picked (Solomon, 2026-10-17: "highlight a section of the midi and find a match
    /// for that section"): the list is ranked again for what plays there, and each part you try plays in those bars.
    /// </summary>
    [RelayCommand]
    void FindForSection()
    {
        if (Section is not { } s || Fit is null) return;
        if (_frame is null)
        {
            if (Selected is not { CanPlayTogether: true, EditorGuide: { Count: > 0 } guide } row) return;
            _frame = new SectionFrame(guide, row.PairLength, WithOctaves, row.PairText);
            _wholeFitText = FitText;
        }
        FitSection(s);
    }

    void FitSection(RollRange range)
    {
        if (_frame is not { } frame || Fit is not { } fit) return;
        var s = range.Within(frame.Length);
        var notes = Cut(frame.GuideAt(WithOctaves), s.Start, s.End);
        if (HarmonyTarget.FromStretch($"{s.Describe()} of {fit.Name}", notes, s.Length, fit.Key, fit.Registers) is not { } target)
        {
            Message = $"Nothing of {fit.Name} plays in {s.Describe()}, so there's nothing there to fit to.";
            return;
        }
        StopPlaying();
        _sectionTarget = target;
        FitText = string.Join(" · ", new[] { target.Chords.Count > 0 ? target.ChordText() : "", "each part plays in those bars" }.Where(t => t.Length > 0));
        SectionFitChanged();
        _ = RefreshAsync();
    }

    /// <summary>Back to parts that fit all of it.</summary>
    [RelayCommand]
    void WholeFit()
    {
        if (_frame is null && _sectionTarget is null) return;
        StopPlaying();
        _frame = null;
        _sectionTarget = null;
        FitText = _wholeFitText;
        SectionFitChanged();
        _ = RefreshAsync();
    }

    void ForgetSection()
    {
        _frame = null;
        _sectionTarget = null;
        Section = null;
        SectionFitChanged();
    }

    void SectionFitChanged()
    {
        OnPropertyChanged(nameof(IsFittingSection));
        OnPropertyChanged(nameof(FitName));
    }

    /// <summary>The notes sounding from <paramref name="from"/> to <paramref name="to"/>, timed from there: ones already held are cut to start there, ones running on end there.</summary>
    public static List<NoteEvent> Cut(IReadOnlyList<NoteEvent> notes, long from, long to)
    {
        var list = new List<NoteEvent>();
        foreach (var n in notes)
        {
            long a = Math.Max(n.Start, from), b = Math.Min(n.Start + Math.Max(1, n.Length), to);
            if (b > a) list.Add(n with { Start = a - from, Length = b - a });
        }
        return list;
    }

    // ---- trying an idea: edits, tempo, mute, into Bitwig ----

    /// <summary>Your edits to parts on the piano roll, by part's notes (its hash, which outlives a reload of the list), as
    /// played (moved by the semitones then), and how long the part is now: kept while the app is open.</summary>
    readonly Dictionary<string, (IReadOnlyList<NoteEvent> Notes, int Move, long Length)> _edits = [];
    readonly Dictionary<string, Stack<(IReadOnlyList<NoteEvent> Notes, int Move, long Length)?>> _undo = [];

    internal (IReadOnlyList<NoteEvent> Notes, int Move, long Length)? EditsOf(MidiEntry e) => _edits.TryGetValue(e.Phrase.Hash, out var x) ? x : null;
    internal bool CanUndo(MidiEntry e) => _undo.TryGetValue(e.Phrase.Hash, out var s) && s.Count > 0;

    /// <summary>The same part: the list is rebuilt from the index on every reload (a DAW save, a new scan), so parts are
    /// told apart by their notes, not by which copy of the list they came from.</summary>
    internal static bool Same(MidiEntry? a, MidiEntry? b) => a is not null && b is not null && a.Phrase.Hash == b.Phrase.Hash;

    /// <summary>The part's notes changed on the piano roll (from its first bar, as played).</summary>
    /// <param name="length">How long it is now (ticks), when a change makes it longer or shorter; else as long as it was.</param>
    internal async Task EditAsync(MidiRow row, IReadOnlyList<NoteEvent>? notes, long? length = null)
    {
        var e = row.Entry;
        var hash = e.Phrase.Hash;
        if (!_undo.TryGetValue(hash, out var undo)) _undo[hash] = undo = new();
        undo.Push(EditsOf(e));
        if (notes is null) _edits.Remove(hash);
        else _edits[hash] = (notes, row.Move, length ?? row.LoopLength);
        foreach (var r in Results.Where(r => Same(r.Entry, e))) r.EditsChanged();
        if (!Results.Contains(row)) row.EditsChanged();
        await RefreshPlayingAsync();
    }

    internal async Task UndoAsync(MidiRow row)
    {
        var hash = row.Entry.Phrase.Hash;
        if (!_undo.TryGetValue(hash, out var undo) || undo.Count == 0) return;
        if (undo.Pop() is { } before) _edits[hash] = before;
        else _edits.Remove(hash);
        foreach (var r in Results.Where(r => Same(r.Entry, row.Entry))) r.EditsChanged();
        if (!Results.Contains(row)) row.EditsChanged();
        await RefreshPlayingAsync();
    }

    /// <summary>The tempo to try parts at, or null for each part's own (the tempo its song or file has).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TempoText), nameof(HasOwnTempo))] double? _tempo;
    public string TempoText => Tempo is { } t ? $"{t:0} BPM" : "Its own tempo";
    public bool HasOwnTempo => Tempo is null;
    /// <summary>For the slider: the tempo set, or the picked part's own.</summary>
    public double TempoValue
    {
        get => Tempo ?? TempoOf(Selected);
        set
        {
            var rounded = Math.Round(Math.Clamp(value, 40, 240));
            if (Tempo == rounded) return;
            Tempo = rounded;
        }
    }

    partial void OnTempoChanged(double? value)
    {
        OnPropertyChanged(nameof(TempoValue));
        _ = RefreshPlayingAsync();
    }

    [RelayCommand]
    void OwnTempo() => Tempo = null;

    /// <summary>The page's tempo, for its own list; a song tab's parts play at their own.</summary>
    double TempoOf(MidiRow? row) => (row is null || row.OnPage ? Tempo : null) ?? (row?.Entry.Phrase.Source.Tempo is > 10 and < 1000 and var t ? t : 120);

    /// <summary>Mute what it's fitted to, or the part itself, while playing them together.</summary>
    [ObservableProperty] bool _muteWith;
    [ObservableProperty] bool _muteMine;
    partial void OnMuteWithChanged(bool value) => _ = RefreshPlayingAsync();
    partial void OnMuteMineChanged(bool value) => _ = RefreshPlayingAsync();

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendBothToBitwigCommand))] bool _isSending;

    /// <summary>
    /// The part picked (as edited, moved and in octave) and what it's fitted to, each on a new track in Bitwig as a clip,
    /// to carry on with the idea there (Solomon, 2026-10-17: "more fully fledged with daw link controls").
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    async Task SendBothToBitwig()
    {
        if (Selected is not { } row) return;
        IsSending = true;
        try
        {
            var dir = ExportFolder();
            var tempo = TempoOf(row);
            var parts = new List<(string Name, string File)> { (row.Title, WriteMidi(row, dir, tempo)) };
            if (row.CanPlayTogether && row.EditorGuide is { Count: > 0 } theirs)
            {
                var name = row.With!.Name;
                var clip = new MidiClip { Name = name, Ppq = PhraseAnalyser.Ppq };
                clip.Track(name).AddRange(theirs);
                var file = Path.Combine(dir, TextScan.SafeFileName($"{name} (with {row.Title})") + ".mid");
                File.WriteAllBytes(file, MidiWriter.Write(clip, tempo));
                parts.Insert(0, (name, file));
            }
            Message = "Connecting to Bitwig…";
            using var link = await _main.Live.Watch.ConnectBitwigAsync();
            foreach (var (name, file) in parts)
            {
                Message = $"Sending {name} to Bitwig…";
                var track = await link.NewTrackAsync(name);
                await link.AddClipAsync(track, file);
            }
            Message = (parts.Count == 1 ? "It's on a new track in Bitwig" : "Both parts are on new tracks in Bitwig") +
                      $", as clips in the first empty launcher slot. Put an instrument on each track to hear them; the clips are at {tempo:0} BPM.";
        }
        catch (Core.DawLink.DawLinkException e) { Message = e.Message; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Message = $"Sending to Bitwig failed: {e.Message}";
        }
        finally { IsSending = false; }
    }

    bool CanSend() => !IsSending;

    /// <summary>
    /// The part picked, as it plays here (moved, in its octave, with your edits), into the section being built in the
    /// MIDI playground (Solomon, 2026-10-17: building parts together "like lego").
    /// </summary>
    [RelayCommand]
    void SendToPlayground()
    {
        if (Selected is not { } row) return;
        _main.Playground.AddFromLibrary(row.Entry, row.BarNotes, row.Title);
        Message = $"{row.Title} is in the playground, in section {_main.Playground.SectionName}.";
    }

    // ---- the octave of what it's fitted to ----

    /// <summary>
    /// Octaves the part being fitted to is moved by, on its own (Solomon, 2026-10-17: "should be able to change the
    /// octave of each part independently"). A part's is remembered like any part's; a song's lasts while fitting.
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(WithOctaveText))] int _withOctaves;
    public string WithOctaveText => WithOctaves switch { 0 => "Octave as it is", > 0 => $"Octave +{WithOctaves}", _ => $"Octave {WithOctaves}" };

    /// <summary>The part "What goes with this?" was asked of, and the octave it was in then.</summary>
    MidiEntry? _fitSource;
    int _fitSourceOctaves;

    /// <summary>How far to move what this harmony's notes are played and shown at.</summary>
    internal int OctavesOf(HarmonyTarget? target) => target is not null && ReferenceEquals(target, Fit) ? WithOctaves : 0;

    [RelayCommand]
    Task WithOctaveUp() => MoveWithAsync(WithOctaves + 1);

    [RelayCommand]
    Task WithOctaveDown() => MoveWithAsync(WithOctaves - 1);

    async Task MoveWithAsync(int octaves)
    {
        WithOctaves = Math.Clamp(octaves, -3, 3);
        if (_fitSource is { } source)
        {
            var mine = Math.Clamp(_fitSourceOctaves + WithOctaves, -3, 3);
            await UiWork.Run(() => { _index.SetYourOctave(source.Phrase.Hash, mine); return 0; });
            source.YourOctave = mine;
        }
        foreach (var r in Results) r.PartnerChanged();
        await RefreshPlayingAsync();
    }

    partial void OnFitChanged(HarmonyTarget? value)
    {
        OnPropertyChanged(nameof(WithSound));
        OnPropertyChanged(nameof(OrderText));
    }

    /// <summary>Showing a random pick in a random order (Shuffle), until the search changes.</summary>
    public bool IsShuffled => _shuffle is not null;

    /// <summary>
    /// The order the list is in when it isn't shuffled, as a choice next to Shuffle (Solomon, 2026-10-17: "should be an
    /// option to list the matches by highest match").
    /// </summary>
    public string OrderText => Fit is not null ? "Best match first" : Like is not null ? "Most alike first" : PhrasesOf is not null ? "Longest first"
        : _describing ? "Closest to your words first" : "Newest first";

    // The list shown is a search by description (closest first), not newest first.
    bool _describing;

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
        var was = _shuffle;
        _shuffle = null;
        // Said again even when it was already in order, so the order chips show the right one after a click.
        OnPropertyChanged(nameof(IsShuffled));
        if (was is not null) _ = RefreshAsync();
    }
    /// <summary>The project being fitted to, whose own parts are left out of the list.</summary>
    string? _fitPath;

    // ---- more like this ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsLiking))] MidiEntry? _like;
    public bool IsLiking => Like is not null;
    public string LikeText => Like is null ? "" : $"Parts shaped like {Like.Title}";

    // ---- search by description (CLaMP 3) ----

    Clamp3Model? _clamp;
    readonly object _clampLock = new();
    bool _clampFailed;
    Task? _hearing;
    // The listening itself, on a background thread: waited for on quit before the model is freed.
    Task? _hearWork;
    // Asked to listen again while listening (new parts were read meanwhile): it goes round once more when done.
    bool _hearAgain;
    CancellationTokenSource? _hearCts;
    (string Text, float[] Vector)? _lastWords;

    /// <summary>Whether the MIDI model is on this computer: then the search box takes descriptions too.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SearchHint))] [NotifyPropertyChangedFor(nameof(CanDownloadMeaning))]
    bool _meaningInstalled = Clamp3Model.IsInstalled();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanDownloadMeaning))] bool _isDownloadingMeaning;
    [ObservableProperty] double _meaningPercent;
    [ObservableProperty] string _meaningText = "";
    /// <summary>The model is listening to parts it hasn't heard yet.</summary>
    [ObservableProperty] bool _isHearing;

    public const string NameWeightKey = "midi_name_weight";

    /// <summary>
    /// How much names, folders and kinds count against what the model hears, when searching by description: 0 is the
    /// model only, 1 as it comes, 3 lets names lead. Saved, so it stays as you set it.
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NameWeightText))] double _nameWeight = 1;

    public string NameWeightText => NameWeight <= 0 ? "Notes only: names are ignored"
        : NameWeight < 1 ? "Mostly the notes, names help a little"
        : NameWeight == 1 ? "Matching names first, then by how the notes play"
        : NameWeight < 3 ? "Names count more than the notes" : "Names lead, the notes sort them";

    partial void OnNameWeightChanged(double value)
    {
        _db.SetSetting(NameWeightKey, value.ToString(CultureInfo.InvariantCulture));
        if (Search.Trim().Length > 0) _ = RefreshAsync(delayMs: 150);
    }

    public bool CanDownloadMeaning => !MeaningInstalled && !IsDownloadingMeaning;
    public string SearchHint => MeaningInstalled
        ? "Describe it: dark pad chords, bouncy funk bassline… or a name, key or chord"
        : "Search MIDI: dark, Cm, trap, piano, Am7…";

    // ---- longer phrases ----

    /// <summary>Showing the longer phrases this part is in (Show the whole phrase).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsShowingPhrases), nameof(PhrasesText))] MidiEntry? _phrasesOf;
    public bool IsShowingPhrases => PhrasesOf is not null;
    public string PhrasesText => PhrasesOf is null ? "" : $"Longer phrases with {PhrasesOf.Title} in them";

    partial void OnPhrasesOfChanged(MidiEntry? value)
    {
        OnPropertyChanged(nameof(OrderText));
        _ = RefreshAsync();
    }

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

    /// <summary>Another part picked in the list: what was playing stops (Solomon, 2026-10-17).</summary>
    partial void OnSelectedChanged(MidiRow? value)
    {
        if (_playing is { } playing && !Same(playing.Entry, value?.Entry)) StopPlaying();
        if (Tempo is null) OnPropertyChanged(nameof(TempoValue));
    }

    /// <summary>Another page is showing: nothing keeps playing behind it.</summary>
    public void Left() => StopPlaying();

    partial void OnSearchChanged(string value)
    {
        if (_shuffle is not null) { _shuffle = null; OnPropertyChanged(nameof(IsShuffled)); }
        _ = RefreshAsync(delayMs: MeaningInstalled ? 300 : 150);
    }
    partial void OnSelectedRoleChanged(RoleChip value) => _ = RefreshAsync();
    partial void OnFromChanged(string value) => _ = RefreshAsync();
    partial void OnLikeChanged(MidiEntry? value)
    {
        OnPropertyChanged(nameof(LikeText));
        OnPropertyChanged(nameof(OrderText));
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
                var c = new MidiCatalogue(_index.All(), _index.Embeddings(Clamp3Model.Version));
                return (c, c.RoleCounts(), c.Entries.Select(e => e.Phrase.Source.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            });
            _catalogue = catalogue;
            // The parts picked before the reload, found again in the new list (gone parts are let go).
            if (_fitSource is { } source) _fitSource = catalogue.Find(source.Phrase.Hash);
#pragma warning disable MVVMTK0034   // straight to the fields: the search below runs once with both
            if (_like is { } like) _like = catalogue.Find(like.Phrase.Hash);
            if (_phrasesOf is { } within) _phrasesOf = catalogue.Find(within.Phrase.Hash);
#pragma warning restore MVVMTK0034
            OnPropertyChanged(nameof(Like));
            OnPropertyChanged(nameof(PhrasesOf));
            OnPropertyChanged(nameof(IsLiking));
            OnPropertyChanged(nameof(IsShowingPhrases));
            OnPropertyChanged(nameof(LikeText));
            OnPropertyChanged(nameof(PhrasesText));
            OnPropertyChanged(nameof(OrderText));
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
        var text = Search.Trim();
        var words = text.Length > 0 && (_sectionTarget ?? Fit) is null && Like is null && PhrasesOf is null ? await WordsAsync(text) : null;
        if (request != _searchRequest) return;
        var query = new MidiQuery(text, SelectedRole.Role == "All" ? null : SelectedRole.Role,
                                  From == YourProjects ? "projects" : From == YourLibraries ? "files" : null, _sectionTarget ?? Fit, Like, Leave: _fitPath,
                                  Words: words, Within: PhrasesOf, NameWeight: (float)NameWeight, Notes: NotesOf, Shuffle: _shuffle);
        query = query with { Limit = ListLimit };
        // A new search stops the one before: scoring every part against a harmony is the heavy part, and only the
        // newest is shown.
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var token = (_searchCts = new CancellationTokenSource()).Token;
        IReadOnlyList<MidiHit> hits;
        int matching;
        try { (hits, matching) = await UiWork.Run(() => (catalogue.Search(query, out var n, token), n)); }
        catch (OperationCanceledException) { return; }
        if (request != _searchRequest) return;
        var keep = Selected?.Entry;
        var asked = (query with { Words = null }, words);
        var newSearch = !Equals(asked, _lastAsked);
        _lastAsked = asked;
        Results = hits.Select(h => new MidiRow(h, this)).ToList();
        // What's playing carries on, its playhead moved to its row in the new list.
        if (_playing is { } playing && Results.FirstOrDefault(r => Same(r.Entry, playing.Entry)) is { } again && again != playing)
        {
            again.IsPlaying = playing.IsPlaying;
            again.IsPlayingTogether = playing.IsPlayingTogether;
            playing.IsPlaying = playing.IsPlayingTogether = false;
            playing.Position = playing.PairPosition = -1;
            _playing = again;
        }
        Selected = Results.FirstOrDefault(r => Same(r.Entry, keep)) ?? Results.FirstOrDefault();
        if (_describing != words is not null)
        {
            _describing = words is not null;
            OnPropertyChanged(nameof(OrderText));
        }
        if (PhrasesOf is not null) Message = "";
        // The part picked stays picked, but a new search shows its best match first rather than scrolling to it.
        if (newSearch) NewSearchShown?.Invoke(this, EventArgs.Empty);
        ResultText = matching == 0 ? (HasParts ? "Nothing matches. Try fewer words, or another kind." : "")
            : PhrasesOf is not null ? (matching == 1 ? "1 longer phrase" : $"{matching:N0} longer phrases") + ", longest first. The part you picked is lit up in each."
            : _shuffle is not null ? $"Shuffled · {hits.Count:N0} picked at random from {matching:N0}" + (Fit is not null ? " of the best fits" : "")
            : _sectionTarget is not null ? $"Best match first for {Section?.Describe()} · {Math.Min(matching, hits.Count):N0} shown of {matching:N0}, each played in those bars"
            : Fit is not null ? $"Best match first · {Math.Min(matching, hits.Count):N0} shown of {matching:N0}, each moved into the right key and octave"
            : Like is not null ? $"Most alike first · {hits.Count:N0} of {matching:N0}"
            : words is not null ? $"Closest to “{text}” first · {hits.Count:N0} of {matching:N0}" +
                                  (catalogue.HeardCount < catalogue.Entries.Count ? $" · {catalogue.Entries.Count - catalogue.HeardCount:N0} not heard yet" : "")
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
            _ = HearAsync();
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

    // ---- the MIDI model ----

    /// <summary>The model, loaded the first time it's needed; null when it isn't installed or won't load.</summary>
    Clamp3Model? Clamp()
    {
        if (_clamp is not null || _clampFailed || !Clamp3Model.IsInstalled()) return _clamp;
        lock (_clampLock)
        {
            if (_clamp is not null || _clampFailed) return _clamp;
            try { _clamp = new Clamp3Model(); }
            catch (Exception e)
            {
                _clampFailed = true;
                AppLog.Info("MIDI", $"The MIDI model wouldn't load: {e}");
            }
            return _clamp;
        }
    }

    /// <summary>What the model makes of the search words (the last one is kept, so changing the kind doesn't ask again).</summary>
    async Task<float[]?> WordsAsync(string text)
    {
        if (!MeaningInstalled) return null;
        if (_lastWords is { } last && last.Text == text) return last.Vector;
        try
        {
            var vector = await UiWork.Run(() => Clamp()?.EmbedText(text));
            if (vector is not null) _lastWords = (text, vector);
            return vector;
        }
        catch (Exception e) when (e is OnnxRuntimeException or InvalidOperationException)
        {
            AppLog.Info("MIDI", $"Describing \"{text}\" failed: {e.Message}");
            return null;
        }
    }

    [RelayCommand]
    async Task DownloadMeaning()
    {
        if (IsDownloadingMeaning) return;
        IsDownloadingMeaning = true;
        MeaningPercent = 0;
        MeaningText = "Connecting…";
        var cts = new CancellationTokenSource();
        var card = _main.Activities.Start("Downloading the MIDI model", "Connecting…", cts.Cancel);
        try
        {
            var progress = new Progress<(string File, long Done, long? Total)>(p =>
            {
                var index = Array.FindIndex(Clamp3Model.Files, f => f.Local == p.File) + 1;
                var size = $"{p.Done / 1048576.0:N1} MB" + (p.Total is { } t ? $" of {t / 1048576.0:N1} MB" : "");
                MeaningPercent = p.Total is > 0 ? 100.0 * p.Done / p.Total.Value : 0;
                MeaningText = $"File {index} of {Clamp3Model.Files.Length}: {size}";
                card.Report(p.Total is > 0 ? MeaningPercent : -1, MeaningText);
            });
            await Clamp3Model.DownloadAsync(progress, ct: cts.Token);
            _clampFailed = false;
            MeaningInstalled = true;
            card.Finish("MIDI model installed", "It's listening to your MIDI now; search by description as it goes");
            Message = "The MIDI model is installed. It's listening to your MIDI in the background; describe what you're after in the search box.";
            _ = HearAsync();
        }
        catch (OperationCanceledException)
        {
            card.Stop("Download stopped", "It carries on from the file it was on");
        }
        catch (Exception e)
        {
            card.Fail("Downloading the MIDI model failed", e.Message);
            Message = $"Downloading the MIDI model failed: {e.Message}. Check the internet connection and try again.";
        }
        finally
        {
            IsDownloadingMeaning = false;
            MeaningText = "";
        }
    }

    /// <summary>Lets the model hear the parts it hasn't heard yet, then searches again so they show up.</summary>
    public Task HearAsync()
    {
        if (!MeaningInstalled || _catalogue is null) return Task.CompletedTask;
        if (_hearing is { IsCompleted: false })
        {
            _hearAgain = true;
            return _hearing;
        }
        return _hearing = RunHearAsync();
    }

    [RelayCommand]
    void StopHearing() => _hearCts?.Cancel();

    async Task RunHearAsync()
    {
        if (await UiWork.Run(Clamp) is not { } model)
        {
            Message = "The MIDI model is installed but wouldn't load, so search matches names only. Details are in the log.";
            return;
        }
        var cts = _hearCts = new CancellationTokenSource();
        Activity? card = null;
        var progress = new ThrottledProgress<ScanProgress>(p =>
        {
            card ??= _main.Activities.Start("Listening to your MIDI", "So you can search it by description", cts.Cancel);
            if (p.Total > 0) card.Report(100.0 * p.Done / p.Total, $"{p.Done:N0} of {p.Total:N0} parts");
        }, 500);
        IsHearing = true;
        // Each batch heard joins the catalogue as it's saved, so description search uses it during a long first listen;
        // the list is searched again now and then while a description is showing.
        var catalogue = _catalogue;
        var ui = SynchronizationContext.Current;
        long lastShown = Environment.TickCount64;
        void Heard(IReadOnlyDictionary<string, sbyte[]> batch)
        {
            catalogue?.AddMeanings(batch);
            if (ui is null || Environment.TickCount64 - lastShown < 20_000) return;
            lastShown = Environment.TickCount64;
            ui.Post(_ => { if (!_disposed && (Search.Trim().Length > 0 || Like is not null)) _ = RefreshAsync(); }, null);
        }
        try
        {
            int heard = 0, failed = 0;
            do
            {
                _hearAgain = false;
                var work = Task.Run(() => MidiEmbedder.Run(_index, model, progress, cts.Token, Heard), cts.Token);
                _hearWork = work;
                var result = await work;
                heard += result.Heard;
                failed += result.Failed;
            }
            while (_hearAgain && !cts.IsCancellationRequested);   // parts read while it listened are heard now, not next time
            if (heard > 0) await AddMeaningsAsync();
            card?.Finish("Heard your MIDI", $"{heard:N0} parts can be found by description" +
                                            (failed > 0 ? $"; {failed:N0} couldn't be heard" : ""));
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) await AddMeaningsAsync();
            card?.Stop("Stopped listening to MIDI", "It carries on from there next time");
        }
        catch (Exception e)
        {
            card?.Fail("Listening to MIDI failed", e.Message);
            AppLog.Info("MIDI", $"Listening failed: {e}");
        }
        finally
        {
            progress.Close();
            IsHearing = false;
        }
    }

    async Task AddMeaningsAsync()
    {
        if (_catalogue is not { } catalogue) return;
        await UiWork.Run(() => { catalogue.AddMeanings(_index.Embeddings(Clamp3Model.Version)); return 0; });
        if (Search.Trim().Length > 0 || Like is not null) await RefreshAsync();
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

    /// <summary>Every part, if they've been read already; null before that, and asking never starts the reading.</summary>
    public MidiCatalogue? LoadedCatalogue => _catalogue;

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
        _fitSource = e;
        _fitSourceOctaves = row.Octaves;
        OnPropertyChanged(nameof(WithSound));
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
        ForgetSection();
        _fitPath = leave;
        _fitSource = null;
        WithOctaves = 0;
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
        ForgetSection();
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
        foreach (var r in Results.Where(r => Same(r.Entry, row.Entry))) r.RoleChanged();
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
        foreach (var r in Results.Where(r => Same(r.Entry, row.Entry))) r.MoveChanged();
        if (!Results.Contains(row)) row.MoveChanged();
        Message = octaves is null ? "Back to the octave the app picks." : "";
        // Hear it straight away where it now sits.
        // Heard straight away where it now sits, without stopping.
        if (Same(_playing?.Entry, row.Entry)) await RefreshPlayingAsync();
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
    /// <param name="fromTicks">Where on the part's own roll to start (from its first note).</param>
    /// <param name="pairTicks">Where on the piano roll to start (from its first bar).</param>
    internal async Task Toggle(MidiRow row, double fromTicks = 0, bool? together = null, double? pairTicks = null)
    {
        var both = (together ?? row.CanPlayTogether) && row.CanPlayTogether;
        if (row.IsPlaying && fromTicks <= 0 && pairTicks is null && (together is null || _plan?.Together == both)) { StopPlaying(); return; }
        StopPlaying();
        var request = _listenRequest;
        var tempo = TempoOf(row);
        if (PlanFor(row, both, tempo) is not { } plan) return;
        if (await RenderAsync(plan, tempo) is not { } audio || request != _listenRequest) return;
        if (_player is null)
        {
            _player = Ui.Current.CreateAudioPlayer();
            _player.Stopped += () => { if (_plan is { Loop: true }) GoRound(); };
        }
        _clock ??= Ui.Current.CreateTimer(TimeSpan.FromMilliseconds(30), Tick);
        plan = plan with { Audio = audio, Tempo = tempo };
        // Where to start: a click on the piano roll (inside the loop points, or from their start), or on the part's own
        // roll (from its first note), which a loop plays from its first bar.
        var from = pairTicks is { } p ? (plan.FromBar ? plan.Inside(p) : Math.Max(0, p - row.Lead))
            : fromTicks <= 0 ? 0 : fromTicks + (plan.FromBar ? plan.Lead : 0);
        var start = MidiRender.TicksToSeconds((long)(Math.Max(0, from) % Math.Max(1, plan.Length)), PhraseAnalyser.Ppq, tempo);
        try { _player.Play(audio, MidiRender.Rate, plan.Key, start); }
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

    /// <summary>The plan's notes as sound; a loop is cut exactly where it goes round, so it starts again without a gap.</summary>
    async Task<float[]?> RenderAsync(PlayPlan plan, double tempo)
    {
        try
        {
            return await UiWork.Run(() =>
            {
                var audio = MidiRender.Render(plan.Channels, tempo, "").Audio;
                if (plan.Loop)
                {
                    var samples = (int)Math.Min(audio.Length, Math.Round(MidiRender.TicksToSeconds(plan.Total, PhraseAnalyser.Ppq, tempo) * MidiRender.Rate));
                    if (samples > 0 && samples < audio.Length) audio = audio[..samples];
                }
                return audio;
            });
        }
        catch (Exception e) when (e is IOException or OutOfMemoryException)
        {
            Message = $"Couldn't play it: {e.Message}";
            return null;
        }
    }

    /// <summary>
    /// What's playing changed (an octave, looping, a sound): played again from where it is, without stopping
    /// (Solomon, 2026-10-17: "changing the octave shouldn't stop the midi playing").
    /// </summary>
    async Task RefreshPlayingAsync()
    {
        if (_playing is not { } row || _plan is not { } old || _player is null) return;
        var request = ++_listenRequest;
        var tempo = TempoOf(row);
        if (PlanFor(row, old.Together, tempo) is not { } plan) return;
        if (await RenderAsync(plan, tempo) is not { } audio || request != _listenRequest || _playing != row) return;
        var at = _player.Playing == old.Key && _player.Position is { } seconds ? MidiRender.SecondsToTicks(seconds, PhraseAnalyser.Ppq, old.Tempo) : 0;
        // Carry on from the same bar: inside new loop points, or from their start. Out of a loop back to playing once:
        // carry on in the first go round.
        var from = old.FromBar && plan.FromBar ? plan.Inside(old.Offset + at % Math.Max(1, old.Length))
            : at % Math.Max(1, plan.Loop ? plan.Length : old.Length);
        var start = MidiRender.TicksToSeconds((long)from, PhraseAnalyser.Ppq, tempo);
        if (start >= audio.Length / (double)MidiRender.Rate) start = 0;
        plan = plan with { Audio = audio, Tempo = tempo };
        try { _player.Play(audio, MidiRender.Rate, plan.Key, start); }
        catch (Exception e)
        {
            Message = $"Couldn't play it: {e.Message}";
            StopPlaying();
            return;
        }
        _plan = plan;
    }

    /// <summary>
    /// What to play: the part alone (from its first note, once), or from its first bar going round, or with the harmony
    /// it's fitted to on top. Together, both play exactly as the piano roll shows them, and go round together where it
    /// does (Solomon, 2026-10-17: "the moment they just go out of sync and it's not following what is on screen"): over
    /// both parts until they meet again, or the loop points you set.
    /// </summary>
    PlayPlan? PlanFor(MidiRow row, bool together, double tempo)
    {
        if (row.Channel.Notes.Count == 0) return null;
        var key = $"midi-browser:{row.Entry.Id}{(together ? ":together" : "")}";
        var sound = row.Sound.Program;
        // On the piano roll's terms: together, or inside the loop points (with what it's fitted to or on its own).
        var onRoll = together && row.CanPlayTogether && row.EditorGuide is { Count: > 0 } || row.LoopPoints is not null;
        if (!together && !Loop && !onRoll)
            return new PlayPlan([row.Channel with { GmProgram = sound }], row.Channel.End, row.Channel.End, 0, false, false, key);

        IReadOnlyList<NoteEvent> mine, theirs = [];
        long length, offset = 0;
        if (onRoll)
        {
            // The stretch of the piano roll that plays, both parts cut to it: one go round.
            var range = row.Region;
            offset = range.Start;
            length = range.Length;
            mine = Cut(row.MineOnRoll, range.Start, range.End);
            if (together && row.EditorGuide is { } guide) theirs = Cut(guide, range.Start, range.End);
        }
        else
        {
            // On its own: its own bars, from the first.
            mine = row.BarNotes;
            length = row.LoopLength;
        }
        // Going round: enough times over to play for a minute or so without a break.
        var once = MidiRender.TicksToSeconds(length, PhraseAnalyser.Ppq, tempo);
        var times = Loop ? (int)Math.Clamp(Math.Ceiling(60 / Math.Max(0.1, once)), 1, Math.Max(1, (MidiRender.MaxSeconds - 4) / Math.Max(0.1, once))) : 1;
        var total = length * times;
        var channels = new List<MidiChannel>();
        // The mutes are the MIDI page's, for its own list: a song tab's parts always play with the song.
        if (theirs.Count > 0 && !(MuteWith && row.OnPage))
            channels.Add(new MidiChannel(row.With?.Name ?? "With", "", PhraseAnalyser.Ppq, Repeat(theirs, length, total)) { GmProgram = WithSound.Program });
        if (!(together && MuteMine && row.OnPage)) channels.Add(row.Channel with { Notes = Repeat(mine, length, total), GmProgram = sound });
        return new PlayPlan(channels, length, total, row.Lead, together && theirs.Count > 0, Loop, key)
        {
            RowLength = onRoll ? length : row.LoopLength,
            Offset = offset,
            FromBar = true,
            OnRoll = onRoll,
        };
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
        if (plan.FromBar)
        {
            var pair = plan.Offset + ticks % Math.Max(1, plan.Length);
            row.PairPosition = pair;
            // Played as the piano roll shows it, the part goes round every its own length from where it starts there,
            // which isn't the length of the loop points.
            row.Position = plan.OnRoll ? row.PartPositionAt(pair) : ticks % Math.Max(1, plan.RowLength) - plan.Lead;
        }
        else
        {
            row.Position = ticks;
            row.PairPosition = ticks + row.Lead;
        }
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
        var files = rows.Select(r => WriteMidi(r, dir, r.OnPage ? Tempo : null)).ToList();
        return new DragPayload(files, rows.Select(r => new DragCard(r.Title, r.Details, Glyphs.Midi)).ToList(),
                               rows.Count == 1 ? "1 MIDI clip" : $"{rows.Count} MIDI clips");
    }

    static string WriteMidi(MidiRow row, string dir, double? tempo = null)
    {
        var name = TextScan.SafeFileName(row.Title + (row.Move != 0 ? $" ({(row.Move > 0 ? "+" : "")}{row.Move})" : ""));
        var file = Path.Combine(dir, name + ".mid");
        File.WriteAllBytes(file, MidiWriter.Write(row.FileClip(), tempo ?? row.Entry.Phrase.Source.Tempo));
        return file;
    }

    [RelayCommand]
    async Task SaveSelected()
    {
        if (Selected is not { } row) return;
        if (await Ui.Current.PickSaveFile(TextScan.SafeFileName(row.Title) + ".mid", "MIDI file", "mid", ExportFolder()) is not { } file) return;
        File.WriteAllBytes(file, MidiWriter.Write(row.FileClip(), Tempo ?? row.Entry.Phrase.Source.Tempo));
        Message = $"Saved {Path.GetFileName(file)}";
    }

    internal IReadOnlyList<NoteEvent> NotesOf(MidiEntry e)
    {
        try { return _index.Notes(e.Id, e.Phrase.Hash); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { return []; }
    }

    bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        _indexCts?.Cancel();
        _searchCts?.Cancel();
        _hearCts?.Cancel();
        // The model is only freed once nothing is using it: a part being heard finishes within a second or so. If it's
        // still going after that, it's left to the end of the app rather than freed under it.
        var stopped = true;
        try { stopped = _hearWork?.Wait(TimeSpan.FromSeconds(3)) ?? true; }
        catch (AggregateException) { }
        if (stopped) _clamp?.Dispose();
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

    /// <summary>The General MIDI sound it plays with: the one for its kind (remembered), drums on a kit.</summary>
    public GmSounds.Choice Sound
    {
        get => _owner.SoundFor(Role);
        set => _owner.SetSound(Role, value);
    }

    internal void SoundChanged() => OnPropertyChanged(nameof(Sound));

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
    public string Details => string.Join(" · ", new[] { InstrumentText, RoleLabel, KeyText, BarsText, $"{Entry.Phrase.NoteCount:N0} notes" }.Where(s => s.Length > 0));

    /// <summary>The instrument, when something attached to the part says ("Piano"), else "".</summary>
    /// <summary>Left out where it would only repeat the kind ("Drums · Drums").</summary>
    public string InstrumentText => Entry.Instrument is { } i && !i.Name.Equals(RoleLabel, StringComparison.OrdinalIgnoreCase) ? i.Name : "";
    public bool ShowsInstrument => InstrumentText.Length > 0;
    public bool HasInstrument => Entry.Instrument is not null;
    /// <summary>How the app knows the instrument: "“Keyscape” is a piano plugin".</summary>
    public string InstrumentWhy => Entry.Instrument?.Why ?? "";

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
        With is { Loops: false } && f.Together is not null && Frame is null ? $"Fits best from bar {f.At / Bar + 1} of the song." : "",
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
        // Your edits, as played when you made them, follow any octave moved since.
        IReadOnlyList<NoteEvent> notes;
        int move;
        if (_owner.EditsOf(Entry) is { } edits)
        {
            notes = edits.Notes;
            move = Move - edits.Move;
        }
        else
        {
            notes = _owner.NotesOf(Entry);
            move = Move;
        }
        var lead = fromFirstNote && notes.Count > 0 ? notes.Min(n => n.Start) : 0;
        if (fromFirstNote) _lead = lead;
        if (move != 0 || lead > 0)
            notes = notes.Select(n => n with { Start = n.Start - lead, Key = Math.Clamp(n.Key + move, 0, 127) }).ToList();
        // Named "Drums" for drum parts so the preview plays them as hits rather than tones.
        return new MidiChannel(Entry.Role == MidiRoles.Drums ? $"Drums · {Title}" : Title, "", PhraseAnalyser.Ppq, notes);
    }

    /// <summary>Its notes as they play (moved), from the start of its first bar: how it loops, and lines up with others.</summary>
    public IReadOnlyList<NoteEvent> BarNotes => _barNotes ??= MakeChannel(fromFirstNote: false).Notes;
    IReadOnlyList<NoteEvent>? _barNotes;

    /// <summary>How long it plays before going round: its bars (as long as your edits made it).</summary>
    public long LoopLength => Math.Max(_owner.EditsOf(Entry) is { } edits ? Math.Max(Bar, edits.Length) : Math.Max(1, Entry.Phrase.Bars) * Bar,
                                       BarNotes.Count == 0 ? Bar : (BarNotes.Max(n => n.Start) / Bar + 1) * Bar);

    // ---- played together with what it's fitted to ----

    /// <summary>What it's fitted to, as it plays with this part: the whole part (it loops too), or the stretch of the song where this fits best.</summary>
    public IReadOnlyList<NoteEvent>? PartnerNotes
    {
        get
        {
            if (With is not { Notes.Count: > 0 } target) return null;
            var move = 12 * _owner.OctavesOf(target);
            if (target.Loops)
                return move == 0 ? target.Notes : target.Notes.Select(n => n with { Key = Math.Clamp(n.Key + move, 0, 127) }).ToList();
            long from = Fit?.At ?? 0, to = from + LoopLength;
            return target.Notes.Where(n => n.Start >= from && n.Start < to)
                               .Select(n => n with { Start = n.Start - from, Length = Math.Min(n.Length, to - n.Start), Key = Math.Clamp(n.Key + move, 0, 127) })
                               .ToList();
        }
    }

    /// <summary>What it's fitted to moved: the together view follows.</summary>
    internal void PartnerChanged()
    {
        foreach (var name in new[] { nameof(PairPartner), nameof(PartnerNotes), nameof(EditorGuide), nameof(TogetherText), nameof(PairLength), nameof(PartEnd) })
            OnPropertyChanged(name);
    }

    // ---- the piano roll ----

    /// <summary>What it's fitted to, over the stretch both play before meeting again: grey behind the part on the piano roll.</summary>
    public IReadOnlyList<NoteEvent>? EditorGuide => Frame is { } frame ? frame.GuideAt(_owner.WithOctaves)
        : PartnerNotes is { Count: > 0 } theirs ? MidiBrowserViewModel.Repeat(theirs, PartnerLength, PairLength) : null;

    /// <summary>Finding parts for some bars: what's fitted to stays as it was then, and this part plays in those bars.</summary>
    MidiBrowserViewModel.SectionFrame? Frame => _owner.FrameFor(this);

    /// <summary>
    /// In the MIDI page's own list, fitted to what the page is fitting to; not a song tab's "MIDI that fits", which plays
    /// its parts with that song, without the page's loop points, mutes or tempo.
    /// </summary>
    internal bool OnPage => With is null || ReferenceEquals(With, _owner.Fit);

    /// <summary>The page's loop points, for its own list.</summary>
    internal RollRange? LoopPoints => OnPage ? _owner.Section : null;

    /// <summary>Where the part plays on the piano roll: from the first bar, or in the bars it's being tried in.</summary>
    public long PartStart => Frame is not null && LoopPoints is { } s ? s.Within(PairLength).Start : 0;
    public long PartEnd => Frame is not null && LoopPoints is { } s ? s.Within(PairLength).End : PairLength;

    /// <summary>The part as it plays on the piano roll: from <see cref="PartStart"/>, going round, up to <see cref="PartEnd"/>.</summary>
    public IReadOnlyList<NoteEvent> MineOnRoll
    {
        get
        {
            long from = PartStart, to = PartEnd;
            return MidiBrowserViewModel.Repeat(BarNotes, LoopLength, to - from)
                .Select(n => n with { Start = n.Start + from, Length = Math.Min(n.Length, to - from - n.Start) }).ToList();
        }
    }

    /// <summary>Where in the part (its own roll, from its first note) a place on the piano roll is; -1 where it doesn't play.</summary>
    internal double PartPositionAt(double pairTicks) =>
        pairTicks < PartStart || pairTicks >= PartEnd ? -1 : (pairTicks - PartStart) % Math.Max(1, LoopLength) - Lead;

    /// <summary>The stretch of the piano roll that plays and is scored: the loop points, or all of it.</summary>
    public RollRange Region => LoopPoints is { } s ? s.Within(PairLength) : new RollRange(0, PairLength);

    internal void SectionChanged()
    {
        foreach (var name in new[] { nameof(PartStart), nameof(PartEnd), nameof(TogetherText) })
            OnPropertyChanged(name);
    }

    public bool IsEdited => _owner.EditsOf(Entry) is not null;
    public bool CanUndoEdit => _owner.CanUndo(Entry);

    /// <summary>
    /// How much of it sits with what it's fitted to as they play now, over the loop points or all of it: edits, octaves
    /// and loop points too.
    /// </summary>
    public string TogetherText
    {
        get
        {
            if (EditorGuide is not { Count: > 0 } guide) return "";
            var range = Region;
            var mine = MidiBrowserViewModel.Cut(MineOnRoll, range.Start, range.End);
            var theirs = MidiBrowserViewModel.Cut(guide, range.Start, range.End);
            var where = LoopPoints is null ? "" : $"Looping {range.Describe()}: ";
            if (HarmonyMatcher.SitsWithin(mine, theirs, range.Length) is not { } sits) return where + "they hardly play at the same time.";
            var rubs = mine.Count(n => theirs.Any(g => g.Start < n.Start + n.Length && g.Start + g.Length > n.Start &&
                                                       (((n.Key - g.Key) % 12 + 12) % 12) is 1 or 11));
            // Two different checks: the score is per half bar (the harmony around a note), the red is note against note.
            return where + $"{Math.Round(sits * 100)}% of its notes fit the other part's harmony" +
                   (rubs == 0 ? "." : rubs == 1 ? ". 1 note rubs a semitone against a note playing at the same moment (red)."
                                                : $". {rubs} notes rub a semitone against a note playing at the same moment (red).");
        }
    }

    [RelayCommand]
    Task Edit(IReadOnlyList<NoteEvent>? notes) => notes is null ? Task.CompletedTask : _owner.EditAsync(this, notes);

    [RelayCommand]
    Task UndoEdit() => _owner.UndoAsync(this);

    /// <summary>
    /// A one-click change on the piano roll (the MIDI page's mini playground): "up" or "down" a semitone, "quantise" to
    /// 16ths, "twice" (played twice), "half" (just its first half) or "reverse". Undo takes it back like any edit.
    /// </summary>
    [RelayCommand]
    Task Tool(string which)
    {
        var notes = BarNotes;
        var length = LoopLength;
        (List<NoteEvent> Notes, long Length)? change = which switch
        {
            "up" => (MidiTools.Transpose(notes, 1), length),
            "down" => (MidiTools.Transpose(notes, -1), length),
            "quantise" => (MidiTools.Quantise(notes, _owner.QuantiseGrid), length),
            "twice" => (MidiTools.Repeat(notes, length), 2 * length),
            "half" => (MidiTools.FirstHalf(notes, length), Math.Max(Bar, length / 2 / Bar * Bar)),
            "reverse" => (MidiTools.Reverse(notes, length), length),
            _ => null,
        };
        return change is { } c ? _owner.EditAsync(this, c.Notes, c.Length) : Task.CompletedTask;
    }

    [RelayCommand]
    Task ResetEdits() => _owner.EditAsync(this, null);

    internal void EditsChanged()
    {
        MoveChanged();
        foreach (var name in new[] { nameof(IsEdited), nameof(CanUndoEdit), nameof(TogetherText), nameof(LoopLength), nameof(PairLength), nameof(EditorGuide),
                                     nameof(PartStart), nameof(PartEnd) })
            OnPropertyChanged(name);
    }

    /// <summary>How long what it's fitted to plays before going round.</summary>
    public long PartnerLength => With is { Loops: true } target ? Math.Max(Bar, target.Length) : LoopLength;

    /// <summary>How long the two play before both are back at the start together (32 bars at most, else the longer).</summary>
    public long PairLength
    {
        get
        {
            if (Frame is { } frame) return frame.Length;
            long a = LoopLength, b = PartnerLength;
            if (a == b) return a;
            var both = a / Gcd(a, b) * b;
            return both <= _owner.LongestLoopBars * Bar ? both : Math.Max(a, b);
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

    public string PairText => Frame is { } frame ? frame.PairText
        : With is not { Notes.Count: > 0 } target ? ""
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

    /// <summary>With what it's fitted to (on a song's MIDI tab, the song), or stopped.</summary>
    [RelayCommand]
    Task PlayTogether() => _owner.Toggle(this, together: true);

    /// <summary>On its own, or stopped.</summary>
    [RelayCommand]
    Task PlayAlone() => _owner.Toggle(this, together: false);

    [RelayCommand]
    Task Seek(double ticks) => _owner.Toggle(this, Math.Max(1, ticks));

    /// <summary>A click on the together view: both from there.</summary>
    [RelayCommand]
    Task PairSeek(double ticks) => _owner.Toggle(this, together: true, pairTicks: Math.Max(0, ticks));

    internal void RoleChanged()
    {
        OnPropertyChanged(nameof(Role));
        OnPropertyChanged(nameof(RoleLabel));
        OnPropertyChanged(nameof(RoleWhy));
        OnPropertyChanged(nameof(Details));
        // The instrument pill hides when it would only repeat the kind, so it changes with it.
        OnPropertyChanged(nameof(InstrumentText));
        OnPropertyChanged(nameof(ShowsInstrument));
        OnPropertyChanged(nameof(CanChangeOctave));
        OnPropertyChanged(nameof(Sound));
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
                                     nameof(PairMine), nameof(PairText), nameof(TogetherText) })
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
    /// <summary>It starts at a bar (looping, together, or loop points), not at the part's first note.</summary>
    public bool FromBar { get; init; }
    /// <summary>Where on the piano roll it starts: the first loop point, or 0.</summary>
    public long Offset { get; init; }
    /// <summary>Played as the piano roll shows it (together, or the loop points), not the part on its own.</summary>
    public bool OnRoll { get; init; }

    /// <summary>A place on the piano roll as ticks into this: inside the loop points, or their start.</summary>
    public long Inside(double pairTicks) => pairTicks >= Offset && pairTicks < Offset + Length ? (long)pairTicks - Offset : 0;
}

/// <summary>A stretch of the piano roll (ticks from its first bar): loop points.</summary>
public readonly record struct RollRange(long Start, long End)
{
    const long Bar = 4 * PhraseAnalyser.Ppq;
    public long Length => End - Start;

    /// <summary>Kept inside something this long; all of it when nothing's left.</summary>
    public RollRange Within(long length)
    {
        var a = Math.Clamp(Start, 0, Math.Max(0, length));
        var b = Math.Clamp(End, a, Math.Max(0, length));
        return b > a ? new RollRange(a, b) : new RollRange(0, length);
    }

    /// <summary>"bars 5–8", "bar 3", or "bars 3.2–5.1" (bar.beat) when it doesn't start and end on bar lines.</summary>
    public string Describe()
    {
        if (Start % Bar == 0 && End % Bar == 0)
        {
            long a = Start / Bar + 1, b = End / Bar;
            return a == b ? $"bar {a}" : $"bars {a}–{b}";
        }
        return $"bars {Pos(Start)}–{Pos(End)}";
    }

    static string Pos(long t) => $"{t / Bar + 1}.{t % Bar / PhraseAnalyser.Ppq + 1}";
}

/// <summary>A stretch of a note roll (ticks): where a clip sits inside a joined phrase, lit when it's the one looked for.</summary>
public readonly record struct RollSpan(long Start, long Length, bool Lit);
