using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// The MIDI playground: a small DAW for MIDI ideas (Solomon, 2026-10-17: "a midi daw that works with my midi files and
/// can layer edit suggest and match midi with sections ... knows not to match midi from the same project ... understand
/// jazz and gospel harmony ... drag the midi into my daw when I'm happy"; 2026-10-19: "it should work like a mini DAW").
/// An idea is one timeline: tracks of clips that sit anywhere, with labels (Verse, Build, Drop…) over stretches of bars
/// and a chord lane heard live from the chord and bass clips. Parts come from your MIDI library (suggested to fit the
/// bars, never from a song already in it), from a stretch of one of your songs, or are written from the chords
/// (voicings, basslines, guide-tone lines). A clip is edited on the piano roll with the rest greyed behind it, and a
/// part or the whole timeline can be dragged into the DAW.
/// </summary>
public sealed partial class PlaygroundViewModel : ObservableObject, IFileDragSource, IDisposable
{
    const int Ppq = PhraseAnalyser.Ppq, Bar = 4 * Ppq;

    readonly LibraryDb _db;
    readonly MainViewModel _main;
    readonly PlaygroundStore _store;
    PlaygroundSession _session;
    readonly List<string> _undo = [], _redo = [];

    public PlaygroundViewModel(LibraryDb db, MainViewModel main, PlaygroundStore? store = null, bool quick = false)
    {
        _db = db;
        _main = main;
        IsQuick = quick;
        _store = store ?? new PlaygroundStore(Path.Combine(main.DataFolder, "playground"));
        LoadOptions();
        _session = !quick && _store.HasCurrent ? _store.LoadCurrent() : Fresh();
        if (_store.OpenFailed) _session = Fresh();
        RoleChips = new[] { MidiRoles.Chords, MidiRoles.Bass, MidiRoles.Melody, MidiRoles.Arp, MidiRoles.Drums }
            .Select(r => new RoleChip(MidiRoles.Plural(r), r)).ToList();
        _suggestRole = RoleChips[0];
        BitwigClips = AskBitwigAsync;
        SetUpBitwig();
        // More MIDI read (a folder or project added, or the list read again): the progressions are found again.
        if (!quick) main.Midi.Read += (_, _) => _ = RefreshProgressionsAsync();
        Show();
        if (_store.OpenFailed)
            Message = $"Your last idea couldn't be opened, so a new one was started. The old one is kept in Ideas as “{_store.KeptAs}”.";
        _wasEmpty = IsEmpty;   // nothing looked up while being made: Opened does that
    }

    // ---- the idea: one timeline of tracks and clips ----

    /// <summary>The tracks, top to bottom.</summary>
    public ObservableCollection<LayerRow> Layers { get; } = [];
    public ObservableCollection<ChordCell> Chords { get; } = [];
    /// <summary>The key heard, shown faintly in the empty key box (a typed key replaces it).</summary>
    [ObservableProperty] string _keyHint = "Key";
    /// <summary>The names over stretches of bars (Verse, Build…).</summary>
    public ObservableCollection<PlaygroundLabel> Labels { get; } = [];

    /// <summary>The timeline: its tracks are <see cref="PlaygroundSection.Layers"/>, its length <see cref="PlaygroundSection.Bars"/>.</summary>
    PlaygroundSection Section => _session.Timeline;
    internal PlaygroundSection CurrentSection => Section;

    /// <summary>Goes up after every change, so the timeline draws itself again.</summary>
    [ObservableProperty] int _revision;
    /// <summary>The longest the timeline can be.</summary>
    public const int MaxBars = 128;

    [ObservableProperty] string _ideaName = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMessage))] string _message = "";
    public bool HasMessage => Message.Length > 0;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelectedLayer))] LayerRow? _selectedLayer;
    public bool HasSelectedLayer => SelectedLayer is not null;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelectedClip))] ClipRow? _selectedClip;
    public bool HasSelectedClip => SelectedClip is not null;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelectedLabel))] PlaygroundLabel? _selectedLabel;
    public bool HasSelectedLabel => SelectedLabel is not null;
    [ObservableProperty] string _keyText = "";
    [ObservableProperty] string _keySummary = "";
    [ObservableProperty] string _chordSummary = "";
    [ObservableProperty] int _bars = 4;
    [ObservableProperty] double _tempo = 90;
    [ObservableProperty] bool _isClassical;
    [ObservableProperty] bool _canUndo;
    [ObservableProperty] bool _canRedo;

    /// <summary>The timeline's length, in ticks.</summary>
    public long Length => Section.Length;
    /// <summary>Nothing on the timeline yet: no tracks, and no chords on the chord track (chords first counts as something to fit to).</summary>
    public bool IsEmpty => Layers.Count == 0 && !HasChordTrack;
    public string Title => _session.Name;

    SectionHarmony? _harmony;
    /// <summary>The current section's harmony, worked out again after each change.</summary>
    public SectionHarmony Harmony => _harmony ??= SectionHarmony.Of(Section, _session.Key, _session.Style);

    bool _showing;

    /// <summary>Everything on the page from the idea, after loading it.</summary>
    void Show()
    {
        _showing = true;
        IdeaName = _session.Name;
        Tempo = _session.Tempo;
        KeyText = _session.Key;
        IsClassical = _session.Style == HarmonyStyle.Classical;
        _partsEnd = PartsEnd();
        ShowSection();
        _showing = false;
    }

    void ShowSection()
    {
        var was = _showing;
        _showing = true;
        // Kept by place: after Undo the idea is a fresh copy, so the old objects aren't in it and the rows as shown say where it was.
        int IndexOf(LayerRow row) => Section.Layers.IndexOf(row.Layer) is >= 0 and var i ? i : Layers.IndexOf(row);
        var keep = SelectedLayer is { } sel ? IndexOf(sel) : -1;
        var keepClip = SelectedClip is { } sc ? (IndexOf(sc.Track), sc.Track.Layer.Clips.IndexOf(sc.Clip)) : (-1, -1);
        foreach (var l in Section.Layers) l.Render();
        _followedFor = ChordTrackKey();
        Layers.Clear();
        foreach (var l in Section.Layers) Layers.Add(new LayerRow(this, l));
        SelectedLayer = keep >= 0 && keep < Layers.Count ? Layers[keep] : Layers.FirstOrDefault();
        SelectedClip = keepClip.Item1 >= 0 && keepClip.Item1 < Layers.Count && keepClip.Item2 >= 0 && keepClip.Item2 < Layers[keepClip.Item1].Layer.Clips.Count
            ? new ClipRow(this, Layers[keepClip.Item1], Layers[keepClip.Item1].Layer.Clips[keepClip.Item2])
            : SelectedLayer is { Layer.Clips.Count: > 0 } t ? new ClipRow(this, t, t.Layer.Clips[0]) : null;
        Labels.Clear();
        foreach (var l in Section.Labels.OrderBy(l => l.Start)) Labels.Add(l);
        SelectedLabel = null;
        Bars = Section.Bars;
        FeelPick = FeelHere.ToString();
        _showing = was;
        HarmonyChanged();
        EmptyChanged();
        OnPropertyChanged(nameof(Length));
        OnPropertyChanged(nameof(ViewFrom));
        OnPropertyChanged(nameof(ViewTo));
    }

    /// <summary>The parts or chords changed: the chord lane, key and every roll's guide are worked out again.</summary>
    void HarmonyChanged()
    {
        _harmony = null;
        var h = Harmony;
        Chords.Clear();
        var key = h.Key;
        var funcs = key is { } k ? ChordFunctions.Analyse(h.Lane.Select(c => c.Chord).ToList(), k.Tonic, k.Minor) : null;
        for (var i = 0; i < h.Lane.Count; i++)
        {
            var c = h.Lane[i];
            Chords.Add(new ChordCell(c, funcs?[i], h.Length, Section.ChordTrack.Any(t => t.Start <= c.Start && t.End >= c.End)));
        }
        KeySummary = key is null ? "Key: not sure yet" : $"Key: {key.Name}{(h.KeyTyped ? "" : " (heard)")}";
        KeyHint = key is null ? "Key" : key.Name;
        ChordSummary = h.Lane.Count == 0 ? "No chords yet: use a progression on the Chords tab, or add a chord or bass part"
            : HasChordTrack ? "Chords from the chord track, and heard from the parts where it has none"
            : "Chords heard live from the chord and bass parts (everything playing, where there are none)";
        OnPropertyChanged(nameof(HasChordTrack));
        if (_progressionsKey != key?.Name) ShowProgressions();
        foreach (var row in Layers) row.GuideChanged();
        SelectedClip?.GuideChanged();
        RefreshIdeas();
        RefreshEnergy();
    }

    // ---- options (Settings › Playground) ----

    public const string SuggestCountKey = "playground_suggestions", GrowBarsKey = "playground_grow_bars", SmoothKey = "playground_smooth",
                        NewTempoKey = "playground_new_tempo", NewBarsKey = "playground_new_bars", NewStyleKey = "playground_new_style",
                        UndoStepsKey = "playground_undo_steps", LoopKey = "playground_loop",
                        SnapGridKey = "playground_snap_grid", SnapRelativeKey = "playground_snap_relative", SnapObjectsKey = "playground_snap_objects",
                        SnapChordKey = "playground_snap_chord",
                        NoteLabelsKey = "playground_note_labels", NoteColoursKey = "playground_note_colours",
                        TrackHeightKey = "playground_track_height", ClipPanelHeightKey = "playground_clip_panel_height",
                        SidePanelWidthKey = "playground_side_width", PianoFoldedKey = "playground_piano_folded";
    public const string JazzGospel = "Jazz and gospel", Classical = "Classical";
    public static IReadOnlyList<int> SuggestCountChoices { get; } = [30, 60, 100, 200];
    public static IReadOnlyList<int> GrowBarsChoices { get; } = [4, 8, 16, 32, 64];
    public static IReadOnlyList<int> NewTempoChoices { get; } = [60, 70, 80, 85, 90, 95, 100, 110, 120, 130, 140, 160, 174];
    public static IReadOnlyList<int> NewBarsChoices { get; } = [2, 4, 8, 16];
    public static IReadOnlyList<string> StyleChoices { get; } = [JazzGospel, Classical];
    public static IReadOnlyList<int> UndoStepsChoices { get; } = [30, 60, 120, 250];

    /// <summary>How many parts Suggest lists, best first.</summary>
    [ObservableProperty] int _suggestCount = 60;
    /// <summary>The longest a part brought in can make the section (a longer part is cut there).</summary>
    [ObservableProperty] int _growBars = 16;
    /// <summary>What a new idea starts with: tempo, bars in its first section, and which ears judge its harmony.</summary>
    [ObservableProperty] int _newTempo = 90;
    [ObservableProperty] int _newBars = 4;
    [ObservableProperty] string _newStyle = JazzGospel;
    /// <summary>How many changes Undo can go back.</summary>
    [ObservableProperty] int _undoSteps = 60;

    bool _loadingOptions;

    void LoadOptions()
    {
        _loadingOptions = true;
        int Number(string key, IReadOnlyList<int> choices, int fallback) =>
            int.TryParse(_db.GetSetting(key), out var n) && choices.Contains(n) ? n : fallback;
        SuggestCount = Number(SuggestCountKey, SuggestCountChoices, 60);
        GrowBars = Number(GrowBarsKey, GrowBarsChoices, 16);
        NewTempo = Number(NewTempoKey, NewTempoChoices, 90);
        NewBars = Number(NewBarsKey, NewBarsChoices, 4);
        UndoSteps = Number(UndoStepsKey, UndoStepsChoices, 60);
        FollowNudge = Number(FollowNudgeKey, FollowNudgeChoices, 0);
        NewStyle = _db.GetSetting(NewStyleKey) == Classical ? Classical : JazzGospel;
        SmoothIncoming = _db.GetSetting(SmoothKey) != "0";
        LoopOn = _db.GetSetting(LoopKey) != "0";
        SnapGrid = _db.GetSetting(SnapGridKey) != "0";
        SnapRelative = _db.GetSetting(SnapRelativeKey) != "0";
        SnapObjects = _db.GetSetting(SnapObjectsKey) != "0";
        SnapChord = _db.GetSetting(SnapChordKey) == "1";
        HearChordTrack = _db.GetSetting(HearChordTrackKey) != "0";
        NoteLabelPick = NoteLabelChoices.Contains(_db.GetSetting(NoteLabelsKey) ?? "") ? _db.GetSetting(NoteLabelsKey)! : "Off";
        NoteColourPick = NoteColourChoices.Contains(_db.GetSetting(NoteColoursKey) ?? "") ? _db.GetSetting(NoteColoursKey)! : ByFit;
        double Size(string key, double fallback, double min, double max) =>
            double.TryParse(_db.GetSetting(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= min && v <= max ? v : fallback;
        TrackHeight = Size(TrackHeightKey, DefaultTrackHeight, MinTrackHeight, MaxTrackHeight);
        ClipPanelHeight = Size(ClipPanelHeightKey, DefaultClipPanelHeight, MinClipPanelHeight, 2000);
        SidePanelWidth = Size(SidePanelWidthKey, DefaultSidePanelWidth, MinSidePanelWidth, MaxSidePanelWidth);
        PianoFolded = _db.GetSetting(PianoFoldedKey) == "1";
        _loadingOptions = false;
    }

    void SaveOption(string key, string value)
    {
        if (!_loadingOptions) _db.SetSetting(key, value);
    }

    partial void OnSuggestCountChanged(int value) => SaveOption(SuggestCountKey, value.ToString());
    partial void OnGrowBarsChanged(int value) => SaveOption(GrowBarsKey, value.ToString());
    partial void OnNewTempoChanged(int value) => SaveOption(NewTempoKey, value.ToString());
    partial void OnNewBarsChanged(int value) => SaveOption(NewBarsKey, value.ToString());
    partial void OnNewStyleChanged(string value) => SaveOption(NewStyleKey, value);
    partial void OnUndoStepsChanged(int value) => SaveOption(UndoStepsKey, value.ToString());
    partial void OnSmoothIncomingChanged(bool value) => SaveOption(SmoothKey, value ? "1" : "0");
    partial void OnLoopOnChanged(bool value)
    {
        SaveOption(LoopKey, value ? "1" : "0");
        if (_playing) _ = PlayAsync(keepPlace: true);
    }

    // ---- pane sizes (Solomon's design, point 18): dragged, remembered, a double-click on a divider puts it back ----

    public const double DefaultTrackHeight = 46, MinTrackHeight = 30, MaxTrackHeight = 140,
                        DefaultClipPanelHeight = 330, MinClipPanelHeight = 150,
                        DefaultSidePanelWidth = 400, MinSidePanelWidth = 300, MaxSidePanelWidth = 720;

    /// <summary>How tall each track is on the timeline (drag the line under a track's name).</summary>
    [ObservableProperty] double _trackHeight = DefaultTrackHeight;
    /// <summary>How tall the picked clip's panel and piano roll are, under the timeline (drag the divider between them).</summary>
    [ObservableProperty] double _clipPanelHeight = DefaultClipPanelHeight;
    /// <summary>How wide the side panel of suggestions is (drag its left edge).</summary>
    [ObservableProperty] double _sidePanelWidth = DefaultSidePanelWidth;
    /// <summary>The piano roll folded away, leaving the timeline the room.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FoldText), nameof(FoldGlyph))] bool _pianoFolded;
    public string FoldText => PianoFolded ? "▸ Show piano roll" : "▾ Fold piano roll";
    public string FoldGlyph => PianoFolded ? "▸" : "▾";

    static string Number(double v) => Math.Round(v).ToString(System.Globalization.CultureInfo.InvariantCulture);
    // Changes live while the line is dragged; KeepTrackHeight saves it once, when it's let go.
    partial void OnTrackHeightChanged(double value)
    {
        foreach (var row in Layers) row.HeightChanged();
    }
    public void KeepTrackHeight() => SaveOption(TrackHeightKey, Number(TrackHeight));
    partial void OnClipPanelHeightChanged(double value) => SaveOption(ClipPanelHeightKey, Number(value));
    partial void OnSidePanelWidthChanged(double value) => SaveOption(SidePanelWidthKey, Number(value));
    partial void OnPianoFoldedChanged(bool value) => SaveOption(PianoFoldedKey, value ? "1" : "0");
    [RelayCommand] void FoldPiano() => PianoFolded = !PianoFolded;

    /// <summary>A new, empty idea, started the way Settings › Playground says.</summary>
    PlaygroundSession Fresh() => new()
    {
        Tempo = NewTempo,
        Style = NewStyle == Classical ? HarmonyStyle.Classical : HarmonyStyle.JazzGospel,
        Sections = [new PlaygroundSection { Name = "Timeline", Bars = NewBars }],
    };

    // ---- saving and undo ----

    /// <summary>Called before every change: remembers the idea as it was, for Undo.</summary>
    void Remember() => Remember(_session.ToJson());

    /// <param name="before">The idea as it was, saved when a gesture began (it's only remembered once something changes).</param>
    void Remember(string before)
    {
        // Something else changed while an idea was being tried: it's kept first, as what's shown is what was changed.
        if (_tryBefore is not null) KeepTried();
        _gesture = 0;
        _undo.Add(before);
        while (_undo.Count > UndoSteps) _undo.RemoveAt(0);
        _redo.Clear();
        CanUndo = true;
        CanRedo = false;
    }

    /// <summary>After every change: saved straight away, so nothing is lost if the app closes.</summary>
    /// <param name="settled">
    /// False for a step of a drag still going on: parts follow and everything is redrawn, but it's saved, and the sound
    /// made again, only once it's let go.
    /// </param>
    void Changed(bool harmony = true, bool settled = true)
    {
        if (harmony && settled) ChordTrackGoesRound();
        if (harmony && ChordTrackKey() != _followedFor) FollowChordTrack();
        // Parts written from the chords follow when an edit changed the chords heard, or moved or stretched a written clip.
        if (harmony && Section.Layers.Any(l => l.Written.Length > 0) && _writtenFor != WrittenKey(LaneForWriting()))
            RewriteWritten();
        PickedClipChanged();
        Revision++;
        if (settled) SaveCurrent();
        if (harmony) HarmonyChanged();
        else RefreshEnergy();
        EmptyChanged();
        if (!settled) return;
        if (_playing) _ = PlayAsync(keepPlace: true);
        PlayInBitwigSoon();
    }

    void SaveCurrent()
    {
        // A cut-down playground is a sketch: nothing to keep until Open in Playground takes it over. While an idea is
        // being tried, the idea is kept as it was before it, until it's kept.
        try { if (!IsQuick) _store.SaveCurrent(_tryBefore is { } before ? PlaygroundSession.FromJson(before) ?? _session : _session); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = $"Couldn't save the idea: {e.Message}"; }
    }

    /// <summary>
    /// The picked clip as it plays now, whatever changed it (Solomon, 2026-10-27: "even after you press keep it still doesnt
    /// change on screen"): the piano roll and the panel beside it show its notes again, and when the change replaced the
    /// clip (cut it in two, or put new chords over it) the one now in its place is picked.
    /// </summary>
    void PickedClipChanged()
    {
        if (SelectedClip is not { } picked) return;
        if (Layers.Contains(picked.Track) && picked.Track.Layer.Clips.Contains(picked.Clip)) { picked.Changed(); return; }
        var track = Layers.FirstOrDefault(r => r.Layer == picked.Track.Layer);
        var clip = track?.Layer.Clips.FirstOrDefault(c => c.Start <= picked.Clip.Start && c.End > picked.Clip.Start) ?? track?.Layer.Clips.FirstOrDefault();
        SelectedClip = track is null || clip is null ? null : new ClipRow(this, track, clip);
    }

    /// <summary>The chord track as parts last followed it.</summary>
    string _followedFor = "";

    string ChordTrackKey() => string.Join(" ", Section.ChordTrack.Select(c => $"{c.Start}+{c.Length}:{c.Root}:{c.Mask}/{c.Bass}"));

    /// <summary>
    /// The chord track changed (a chord stretched, moved, added or taken off): every part not changed by hand follows it
    /// straight away (Solomon, 2026-10-22: parts change live as a chord is stretched).
    /// </summary>
    /// <param name="all">Every part, not only those whose chords changed (the idea's style changed).</param>
    void FollowChordTrack(bool all = false)
    {
        _followedFor = ChordTrackKey();
        foreach (var row in Layers)
        {
            if (row.Layer.Written.Length > 0) continue;
            // Only clips the change reaches: a part far from it (brought in from a song as it was) stays as it is.
            // (And one saved before it knew the idea's style, once.)
            var touched = row.Layer.Clips.Where(c => !c.Pinned && (all || c.Style != _session.Style || !SameChords(c.Under, UnderOf(Section, row.Layer, c)))).ToList();
            if (touched.Count == 0) continue;
            foreach (var c in touched) Refit(row.Layer, c);
            row.Layer.Render();
            row.Changed();
        }
        SelectedClip?.Changed();
    }

    [RelayCommand]
    void Undo()
    {
        if (_tryBefore is not null) { BackIdea(); return; }   // the idea being tried is what Undo takes back first
        if (_undo.Count == 0) return;
        _redo.Add(_session.ToJson());
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        CanUndo = _undo.Count > 0;
        CanRedo = true;
    }

    [RelayCommand]
    void Redo()
    {
        if (_redo.Count == 0) return;
        if (_tryBefore is not null) BackIdea();
        _undo.Add(_session.ToJson());
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        CanRedo = _redo.Count > 0;
        CanUndo = true;
    }

    void Restore(string json)
    {
        if (PlaygroundSession.FromJson(json) is not { } s) return;
        // Where the idea is saved isn't undone with its changes: saved, then undone and saved again, it still replaces
        // itself. A step from another idea (before New idea or Open) keeps its own.
        if (s.Id == _session.Id) s.SavedAs = _session.SavedAs;
        _session = s;
        Show();
        if (Loop is { } loop) LoopOver(loop);   // kept inside the timeline as it now is
        Changed(harmony: false);
    }

    [RelayCommand]
    async Task NewIdea()
    {
        if (!IsEmpty && !await Ui.Current.Confirm("Start a new idea? This one is kept: save it by name first if you want to come back to it.", "New idea"))
            return;
        Remember();
        StopPlaying();
        _session = Fresh();
        Loop = null;   // the highlight was over the old idea's bars
        Show();
        Changed(harmony: false);
    }

    [RelayCommand]
    void SaveIdea()
    {
        if (_tryBefore is not null) KeepTried();   // saved as it's shown and heard
        var named = _session.Name;
        try
        {
            // Another idea saved by the same name is kept: this one is saved with a number after its name.
            var name = _store.SaveIdea(_session);
            _showing = true;
            IdeaName = _session.Name;
            _showing = false;
            OnPropertyChanged(nameof(Title));
            SaveCurrent();   // where it's saved is kept with it, so saving it again replaces just that one
            Message = _session.Name == named ? $"Saved “{named}”. Open it again from Ideas."
                : $"Saved as “{name}”, so the other idea called “{named}” is kept. Open either from Ideas.";
            OnPropertyChanged(nameof(SavedIdeas));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = $"Couldn't save it: {e.Message}"; }
    }

    public IReadOnlyList<string> SavedIdeas => _store.Ideas();

    [RelayCommand]
    void OpenIdea(string? name)
    {
        if (name is null) return;
        PlaygroundSession? s;
        try { s = _store.LoadIdea(name); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { s = null; }
        if (s is null)
        {
            // Saved by a newer build, damaged, or open in another program for now: it stays in Ideas to try again later.
            Message = $"“{name}” couldn't be opened. It's still in Ideas, so you can try again later.";
            return;
        }
        Remember();
        StopPlaying();
        _session = s;
        Loop = null;
        Show();
        Changed(harmony: false);
        Message = $"Opened “{name}”.";
    }

    partial void OnIdeaNameChanged(string value)
    {
        if (_showing || value == _session.Name) return;
        Remember();
        _session.Name = value.Trim().Length == 0 ? "Untitled idea" : value.Trim();
        OnPropertyChanged(nameof(Title));
        Changed(harmony: false);
    }

    partial void OnTempoChanged(double value)
    {
        if (_showing) return;
        var tempo = Math.Clamp(Math.Round(value), 40, 240);
        if (tempo == _session.Tempo) return;
        // Steps close together (scrolling over it) are one undo step, unless something else was changed in between.
        if (_tempoGesture == 0 || _gesture != _tempoGesture)
        {
            Remember();
            _gesture = _tempoGesture = GestureIds.Next();
        }
        _session.Tempo = tempo;
        _ = SaveTempoSoon();
    }

    int _tempoMoves;
    /// <summary>The undo step the tempo steps going on now are part of; 0 once they've stopped.</summary>
    int _tempoGesture;

    /// <summary>Saved (and the music started again at the new speed) once the tempo slider stops, not on every step of a drag.</summary>
    async Task SaveTempoSoon()
    {
        var move = ++_tempoMoves;
        await Task.Delay(300);
        if (move != _tempoMoves) return;
        _tempoGesture = 0;
        Changed(harmony: false);
    }

    partial void OnKeyTextChanged(string value)
    {
        if (_showing || value == _session.Key) return;
        if (value.Trim().Length > 0 && MusicTheory.ParseKey(value) is null) return;
        Remember();
        _session.Key = value.Trim();
        Changed();
    }

    partial void OnIsClassicalChanged(bool value)
    {
        if (_showing) return;
        Remember();
        _session.Style = value ? HarmonyStyle.Classical : HarmonyStyle.JazzGospel;
        FollowChordTrack(all: true);
        Changed();
        if (Matches.Count > 0) _ = FindMatchesAsync();
    }

    partial void OnBarsChanged(int value)
    {
        if (_showing || value == Section.Bars) return;
        Remember();
        SetBars(value, follow: true);
        Changed();
    }

    /// <summary>
    /// The timeline made longer or shorter. With <paramref name="follow"/> (the Bars box), clips that reached the old end
    /// follow it, playing round again to fill the new bars (or cut shorter); the rest stay where they are.
    /// </summary>
    void SetBars(int bars, bool follow = false)
    {
        var old = Section.Length;
        Section.Bars = Math.Clamp(bars, 1, MaxBars);
        var length = Section.Length;
        if (follow)
            foreach (var l in Section.Layers)
            {
                foreach (var c in l.Clips.Where(c => c.End == old && c.Start < length))
                {
                    c.Length = length - c.Start;
                    Refit(l, c);   // the new go rounds fit the chords under them too
                }
                l.Render();
            }
        if (Loop is { } loop) LoopOver(loop);   // kept inside the timeline, or dropped if it's now past the end
        _showing = true;
        Bars = Section.Bars;
        _showing = false;
        OnPropertyChanged(nameof(Length));
        OnPropertyChanged(nameof(ViewFrom));
        OnPropertyChanged(nameof(ViewTo));
        foreach (var row in Layers) row.Changed();
    }

    /// <summary>Makes room for a clip ending at <paramref name="end"/>: the timeline grows (never shrinks) to the bar after it.</summary>
    void Reach(long end)
    {
        var bars = (int)Math.Min(MaxBars, (end + Bar - 1) / Bar);
        if (bars > Section.Bars) SetBars(bars);
    }

    // ---- labels ----

    /// <summary>The label over the highlight, or the one picked, or the one at the start: what the bars being worked on are for.</summary>
    PlaygroundLabel? LabelHere => SelectedLabel ?? Section.LabelAt(Loop?.Start ?? 0);
    SectionFeel FeelHere => LabelHere?.Feel ?? SectionFeel.Any;

    /// <summary>A label over the highlight, or over the bars after the last label (the whole timeline when there are none).</summary>
    [RelayCommand]
    void AddLabel()
    {
        var (start, end) = Loop is { } r ? (r.Start, r.End) : (Section.Labels.Count == 0 ? 0 : Section.Labels.Max(l => l.End), Section.Length);
        if (end <= start) { start = Section.Length; end = start + 4L * Bar; }
        if (start >= MaxBars * (long)Bar) { Message = $"The timeline is full ({MaxBars} bars): highlight some bars to label them."; return; }
        end = Math.Min(end, MaxBars * (long)Bar);
        Remember();
        Reach(end);
        var names = new[] { "Intro", "Verse", "Build", "Drop", "Breakdown", "Outro" };
        var label = new PlaygroundLabel
        {
            Name = names.FirstOrDefault(n => Section.Labels.All(l => l.Name != n)) ?? $"Part {Section.Labels.Count + 1}",
            Start = start, Length = end - start,
        };
        label.Feel = Enum.TryParse<SectionFeel>(label.Name, out var f) ? f : SectionFeel.Any;
        Section.Labels.Add(label);
        ShowSection();
        SelectedLabel = label;
        Changed(harmony: false);
    }

    internal void PickLabel(PlaygroundLabel? label)
    {
        SelectedLabel = label;
        if (label is not null) LoopOver(new RollRange(label.Start, label.End));
        _showing = true;
        FeelPick = FeelHere.ToString();
        _showing = false;
        OnPropertyChanged(nameof(LabelName));
    }

    [RelayCommand] void SelectLabel(PlaygroundLabel? label) => PickLabel(label);

    /// <summary>The picked label's name, to change.</summary>
    public string LabelName
    {
        get => SelectedLabel?.Name ?? "";
        set
        {
            if (SelectedLabel is not { } l || value.Trim().Length == 0 || value.Trim() == l.Name) return;
            Remember();
            l.Name = value.Trim();
            Changed(harmony: false);
        }
    }

    [RelayCommand]
    void RemoveLabel()
    {
        if (SelectedLabel is not { } l) return;
        Remember();
        Section.Labels.Remove(l);
        ShowSection();
        Changed(harmony: false);
    }

    // ---- parts ----

    /// <summary>Where new parts go, and for how long: the highlight, or the whole timeline.</summary>
    (long Start, long Length) Place => Loop is { } r ? (r.Start, r.End - r.Start) : (0, Section.Length);

    /// <summary>
    /// Adds a track with one clip, picked, and makes room for it: <paramref name="notes"/> as one go round of
    /// <paramref name="loop"/> ticks (0: as long as the clip), playing from <paramref name="start"/> for <paramref name="length"/>.
    /// </summary>
    PlaygroundLayer AddLayer(string name, string role, IReadOnlyList<NoteEvent> notes, string sourcePath = "", string hash = "", string written = "",
                             long? start = null, long? length = null, long loop = 0)
    {
        var at = start ?? Place.Start;
        var len = length ?? Place.Length;
        name = UniqueName(name);
        var layer = new PlaygroundLayer
        {
            Name = name, Role = role, Program = GmSounds.DefaultFor(role), SourcePath = sourcePath, SourceHash = hash, Written = written,
            Clips = [PlaygroundClip.Of(name, role, notes, loop > 0 ? loop : len, at, len, sourcePath, hash)],
        };
        Reach(at + len);
        if (written.Length == 0 && HasChordTrack) Refit(layer, layer.Clips[0]);   // it follows the chord track
        layer.Render();
        Section.Layers.Add(layer);
        var row = new LayerRow(this, layer);
        Layers.Add(row);
        SelectedLayer = row;
        SelectedClip = new ClipRow(this, row, layer.Clips[0]);
        return layer;
    }

    /// <param name="except">The track being renamed, which can keep its own name.</param>
    string UniqueName(string name, PlaygroundLayer? except = null) => UniqueName(Section, name, except);

    /// <summary><paramref name="name"/>, or with a number after it when another track on <paramref name="timeline"/> has it.</summary>
    static string UniqueName(PlaygroundSection timeline, string name, PlaygroundLayer? except = null)
    {
        name = name.Trim().Length == 0 ? "Part" : name.Trim();
        if (!Taken(name)) return name;
        for (var i = 2; ; i++)
        {
            // A long name is cut short to make room for its number, so the number is still in its file's name (which is
            // cut at 120 characters) and each number tried is a different file.
            var number = $" {i}";
            var numbered = name[..Math.Min(name.Length, TextScan.LongestFileName - number.Length)].TrimEnd() + number;
            if (!Taken(numbered)) return numbered;
        }

        // Names are told apart as their dragged files are ("Chords" and "chords", "Lead?" and "Lead*" are one file on
        // Windows), so each track still drags out as a file of its own.
        bool Taken(string n) => timeline.Layers.Any(l => l != except
            && TextScan.SafeFileName(l.Name).Equals(TextScan.SafeFileName(n), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A track renamed: a name another track has gets a number after it, as new tracks do, so each still goes out to the
    /// DAW as a track of its own (MIDI tracks are told apart by name).
    /// </summary>
    internal void RenameLayer(LayerRow row, string name)
    {
        if (name.Trim().Length == 0) return;
        name = UniqueName(name, except: row.Layer);
        if (name == row.Layer.Name) { row.Changed(); return; }   // the box shows its name again
        EditLayer(row, l => l.Name = name, harmony: false);
    }

    /// <summary>
    /// A part from the MIDI page or the suggestions, as it plays there (moved, octave and all), on a new track over the
    /// highlight, or from bar 1 for as long as the timeline (and at least once through, up to Settings' longest).
    /// </summary>
    /// <param name="period">How long it goes round, for a stretch cut from a part (ticks); 0 for its own bars.</param>
    public void AddFromLibrary(MidiEntry entry, IReadOnlyList<NoteEvent> notes, string? title = null, long period = 0) =>
        AddFromLibrary(entry, notes, title, SmoothIncoming && period == 0, period);

    void AddFromLibrary(MidiEntry entry, IReadOnlyList<NoteEvent> notes, string? title, bool smooth, long period = 0)
    {
        if (notes.Count == 0) return;
        Remember();
        var name = title ?? (entry.Title.Length > 0 ? entry.Title : entry.SourceName);
        var loop = period > 0 ? period : Math.Max(1, PhraseAnalyser.BarsOf(notes)) * (long)Bar;
        var bars = (int)((loop + Bar - 1) / Bar);
        var (start, length) = Place;
        // A part longer than the timeline grows it (up to Settings' longest), so nothing is cut off; an empty timeline takes its length.
        if (Loop is null)
        {
            if (IsEmpty) SetBars(Math.Min(bars, GrowBars));
            else if (bars > Section.Bars) SetBars(Math.Max(Section.Bars, Math.Min(GrowBars, bars)));
            length = Section.Length;
        }
        var one = notes.Where(n => n.Start < loop).ToList();
        // Chord parts come in turned into their smoothest inversions, sitting with the chords already here.
        var smoothed = smooth && entry.Role == MidiRoles.Chords ? Inversions.Smooth(one, ChordCentre(null)) : one;
        var turned = !smoothed.SequenceEqual(one);
        AddLayer(name, entry.Role, smoothed, entry.Phrase.Source.Path, entry.Phrase.Hash, start: start, length: length, loop: loop);
        Changed();
        Message = $"Added {name} from {entry.SourceName}{(turned ? ", its chords turned into smooth inversions" : "")}. Parts from {entry.SourceName} won't be suggested now.";
    }

    internal void RemoveLayer(LayerRow row)
    {
        Remember();
        Section.Layers.Remove(row.Layer);
        var i = Layers.IndexOf(row);
        Layers.Remove(row);
        if (SelectedLayer == row) SelectedLayer = Layers.Count == 0 ? null : Layers[Math.Clamp(i, 0, Layers.Count - 1)];
        if (SelectedClip?.Track == row) SelectedClip = null;
        Changed();
    }

    internal void DuplicateLayer(LayerRow row)
    {
        Remember();
        var copy = row.Layer.Copy();
        copy.Name = UniqueName(row.Layer.Name);
        copy.Written = "";
        Section.Layers.Insert(Section.Layers.IndexOf(row.Layer) + 1, copy);
        ShowSection();
        SelectedLayer = Layers.FirstOrDefault(l => l.Layer == copy);
        Changed();
    }

    internal void EditLayer(LayerRow row, Action<PlaygroundLayer> change, bool harmony = true)
    {
        Remember();
        var picked = SelectedClip is { } sc && sc.Track == row ? row.Layer.Clips.IndexOf(sc.Clip) : -1;
        change(row.Layer);
        if (row.Layer.Clips.Count > 0) row.Layer.Render();
        row.Changed();
        // The clips were made again: the same one stays picked.
        if (picked >= 0 && !row.Layer.Clips.Contains(SelectedClip!.Clip))
            SelectedClip = row.Layer.Clips.Count == 0 ? null : new ClipRow(this, row, row.Layer.Clips[Math.Min(picked, row.Layer.Clips.Count - 1)]);
        SelectedClip?.Changed();
        Changed(harmony);
    }

    /// <summary>
    /// A whole track's notes changed by hand (the small playground's piano roll): it becomes one clip of those notes the
    /// timeline's length, no longer rewritten from the chords.
    /// </summary>
    internal void SetNotes(LayerRow row, IReadOnlyList<NoteEvent> notes) =>
        EditLayer(row, l =>
        {
            var first = l.Clips.FirstOrDefault();
            l.Clips = [new PlaygroundClip
            {
                Name = first?.Name ?? l.Name, Role = l.Role, Notes = notes, Loop = Section.Length, Start = 0, Length = Section.Length, Pinned = true,
                SourcePath = first?.SourcePath ?? l.SourcePath, SourceHash = first?.SourceHash ?? l.SourceHash,
            }];
            l.Written = "";
            l.GrooveData = "";
        });

    /// <summary>Every clip on the track moved up or down by hand: they stop moving to fit until Fit again.</summary>
    internal void MoveLayer(LayerRow row, int semitones) =>
        EditLayer(row, l =>
        {
            foreach (var c in l.Clips) { c.Transpose += semitones; c.Pinned = true; }
        });

    /// <summary>
    /// Changes the notes of the picked clip when it's on <paramref name="layer"/>, else of every clip on it, one go round
    /// at a time as it plays: <paramref name="change"/> gets the notes, the go round's length and the chords under it,
    /// and gives back the new notes. The clip then plays them as they are.
    /// </summary>
    /// <param name="like">The track <paramref name="layer"/> is a copy of (to try a change out): its picked clip is the one changed.</param>
    void MapClips(PlaygroundLayer layer, Func<IReadOnlyList<NoteEvent>, long, IReadOnlyList<TimedChord>, IReadOnlyList<NoteEvent>> change,
                  PlaygroundLayer? like = null)
    {
        var picked = SelectedClip is { } sc && sc.Track.Layer == (like ?? layer) ? sc.Track.Layer.Clips.IndexOf(sc.Clip) : -1;
        var clips = picked >= 0 && picked < layer.Clips.Count ? [layer.Clips[picked]] : layer.Clips.ToList();
        foreach (var c in clips)
        {
            if (c.Under.Count > 0) c.Bake();   // following the chord track: changed as it plays, all of it
            var at = c.RepeatAt(0);
            var played = Inversions.Turn(c.Notes, c.Inversion).Select(n => n with { Key = Math.Clamp(n.Key + c.Shift(0), 0, 127) }).ToList();
            var lane = LaneAt(Harmony.Lane, at, at + c.Loop);
            c.Notes = change(played, c.Loop, lane).Select(n => n with { Key = Math.Clamp(n.Key - c.Shift(0), 0, 127) }).ToList();
            c.Inversion = 0;
        }
        layer.Render();
    }

    /// <summary>The chords from <paramref name="from"/> to <paramref name="to"/>, from 0.</summary>
    static List<TimedChord> LaneAt(IReadOnlyList<TimedChord> lane, long from, long to) =>
        lane.Where(c => c.End > from && c.Start < to)
            .Select(c => new TimedChord(Math.Max(c.Start, from) - from, Math.Min(c.End, to) - Math.Max(c.Start, from), c.Chord)).ToList();

    // ---- clips ----

    internal void PickClip(LayerRow? track, PlaygroundClip? clip)
    {
        _chordLaneLast = false;   // Delete is for the clip now
        if (track is null || clip is null) { SelectedClip = null; return; }
        if (SelectedClip?.Clip == clip) return;
        SelectedLayer = track;
        SelectedClip = new ClipRow(this, track, clip);
    }

    /// <summary>
    /// A clip slid along or onto another track, played round more times (its right edge pulled out) or trimmed (its left
    /// edge pulled in) on the timeline (Solomon, 2026-10-19). Below the last track makes a new track named after its kind.
    /// </summary>
    [RelayCommand]
    void ChangeClip(ClipEdit? edit)
    {
        if (edit is null || Layers.FirstOrDefault(r => r.Layer == edit.Track) is not { } from || !edit.Track.Clips.Contains(edit.Clip)) return;
        Remember();
        var clip = edit.Clip;
        var (wasStart, wasEnd, loop) = (clip.Start, clip.End, Math.Max(1, clip.Loop));
        clip.Start = Math.Max(0, edit.Start);
        clip.Length = Math.Max(1, edit.Length);
        // Trimmed or pulled out at its start (its end stays put): a different go round now plays first, so a pinned
        // clip's moves follow their go rounds, as Piece does when a clip is cut.
        if (clip.Pinned && clip.Start != wasStart && clip.End == wasEnd && clip.Fits.Count > 0)
        {
            var into = (long)Math.Floor((clip.Offset % loop + clip.Start - wasStart) / (double)loop);
            clip.Fits = into >= 0 ? [.. clip.Fits.Skip((int)Math.Min(into, clip.Fits.Count))]
                                  : [.. Enumerable.Repeat(clip.Fits[0], (int)-into), .. clip.Fits];
        }
        clip.Offset = Math.Max(0, edit.Offset);
        if (clip.Start != wasStart && clip.End == wasEnd) clip.StartMoved(clip.Start - wasStart);
        var to = from;
        if (edit.Row != Layers.IndexOf(from))
        {
            from.Layer.Clips.Remove(clip);
            from.Layer.Render();
            // A track written from the chords would write over it, so it gets a new track instead, as a dropped part does.
            if (edit.Row >= 0 && edit.Row < Layers.Count && !Layers[edit.Row].IsWritten) to = Layers[edit.Row];
            else
            {
                var role = clip.Role == MidiRoles.Other ? from.Role : clip.Role;
                var layer = new PlaygroundLayer
                {
                    Name = UniqueName(KindName(role)), Role = role, Program = from.Layer.Program ?? GmSounds.DefaultFor(role),
                    SourcePath = clip.SourcePath, SourceHash = clip.SourceHash,
                };
                Section.Layers.Add(layer);
                Layers.Add(to = new LayerRow(this, layer));
            }
            to.Layer.Clips.Add(clip);
            from.Changed();
        }
        // Over other clips on its track, it cuts out what it covers, as in a DAW.
        to.Layer.Clips.Remove(clip);
        CutOut(to.Layer, clip.Start, clip.End);
        to.Layer.Clips.Add(clip);
        Reach(clip.End);
        Refit(to.Layer, clip);
        to.Layer.Render();
        to.Changed();
        SelectedLayer = to;
        SelectedClip = new ClipRow(this, to, clip);
        Changed();
        if (clip.Fits.Any(f => f != 0))
        {
            static string By(int f) => $"{(f > 0 ? "up" : "down")} {Math.Abs(f)}";
            var moves = clip.Fits.Count == 1 ? By(clip.Fits[0])
                : string.Join(", ", clip.Fits.Select((f, i) => (f, i)).Where(x => x.f != 0).Select(x => $"go round {x.i + 1} {By(x.f)}"));
            Message = $"{clip.Name} moved to fit the chords there: {moves}. Move it by hand to keep it as it is.";
        }
    }

    /// <summary>
    /// Shift to fit (Solomon, 2026-10-19: "a clip moved to new chords transposes itself to the best fit"): each go round
    /// of the clip moved (−6 to +5 semitones) to sit best on the chords the other parts play under it. Left alone when
    /// it's pinned (changed by hand), written from the chords, drums, or a chord part (chord parts are what the chords
    /// are heard from). A bassline fits the chord parts only.
    /// </summary>
    /// <remarks>
    /// Chords first, step 2 (Solomon, 2026-10-22): over the chord track it follows the chords there, the way picked for
    /// it (<see cref="PlaygroundClip.Way"/>). Nudging clashes moves the whole clip into the key first.
    /// </remarks>
    void Refit(PlaygroundLayer layer, PlaygroundClip clip) => Refit(Section, layer, clip);

    /// <param name="section">The idea's timeline, or a copy of it (a reharm tried out before it's kept).</param>
    void Refit(PlaygroundSection section, PlaygroundLayer layer, PlaygroundClip clip)
    {
        var role = clip.Role == MidiRoles.Other ? layer.Role : clip.Role;
        if (clip.Pinned) return;
        clip.PlaysAs = role;
        clip.Style = _session.Style;
        var way = clip.Way;
        clip.Under = UnderOf(section, layer, clip);
        // Not following the chords: it plays as it is, moved to fit as it was (Harmonise's chords were made for that).
        if (way == FollowWay.None) return;
        if (layer.Written.Length > 0 || role is MidiRoles.Chords or MidiRoles.Drums ||
            clip.Under.Count > 0 && way is FollowWay.Degree or FollowWay.Root)
        {
            clip.Fits = [];
            return;
        }
        var leaveOut = role == MidiRoles.Bass ? section.Layers.Where(l => l.Role != MidiRoles.Chords).ToList() : [layer];
        var lane = SectionHarmony.Of(section, _session.Key, _session.Style, leaveOut).Lane;
        // Over the chord track a line nudging its clashes moves as a whole, into the key, and the nudges do the rest.
        clip.Fits = clip.Under.Count > 0 ? ClipFit.Whole(clip, lane, _session.Style) : ClipFit.Repeats(clip, lane, _session.Style);
    }

    /// <summary>The chord track's chords under <paramref name="clip"/>, from its start, when it follows them.</summary>
    static List<PlaygroundChord> UnderOf(PlaygroundSection section, PlaygroundLayer layer, PlaygroundClip clip) =>
        layer.Written.Length == 0 && clip.Way != FollowWay.None
            ? section.ChordTrack.Where(c => c.Start < clip.End && c.End > clip.Start).Select(c => new PlaygroundChord { Start = c.Start - clip.Start, Length = c.Length, Chord = c.Chord }).ToList()
            : [];

    static bool SameChords(IReadOnlyList<PlaygroundChord> a, IReadOnlyList<PlaygroundChord> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Start == p.Second.Start && p.First.Length == p.Second.Length &&
                                                p.First.Chord == p.Second.Chord);

    // ---- parts swapped in, or dropped onto the timeline ----

    /// <summary>
    /// The clip the highlight is in (Solomon, 2026-10-19: "highlighting inside a clip gives alternatives of the same kind
    /// for just those bars"): the picked clip, when the highlight covers some of it. Suggest then lists parts of its kind
    /// for those bars, and picking one swaps it in there.
    /// </summary>
    public ClipRow? SwapTarget => Loop is { } r && SelectedClip is { } c && !c.Track.IsWritten && c.Clip.Start < r.End && c.Clip.End > r.Start ? c : null;

    /// <summary>The bars of <see cref="SwapTarget"/> under the highlight.</summary>
    (long Start, long End)? SwapRange => SwapTarget is { } t && Loop is { } r ? (Math.Max(r.Start, t.Clip.Start), Math.Min(r.End, t.Clip.End)) : null;

    /// <summary>What the picked clip's track plays in the bar either side of the bars being swapped, for stretches of other parts to join on to.</summary>
    static SwapAround AroundOf(ClipRow target, (long Start, long End) bars)
    {
        var notes = target.Track.Layer.Notes.Where(n => !n.Muted).ToList();
        var own = target.Notes;
        return new SwapAround(
            notes.Where(n => n.Start < bars.Start && n.Start >= bars.Start - Bar).Select(n => n with { Start = n.Start - bars.Start }).ToList(),
            notes.Where(n => n.Start >= bars.End && n.Start < bars.End + Bar).Select(n => n with { Start = n.Start - bars.End }).ToList(),
            bars.Start % Bar,
            own.Count == 0 ? null : own.Average(n => n.Key),
            own.Count / Math.Max(0.25, target.Clip.Loop / (double)Bar));
    }

    /// <summary>
    /// Bars of the picked clip dragged out along the piano roll's bar numbers (Solomon, 2026-10-20: "select a part of a
    /// clip and have suggestions to replace that"): the same bars are highlighted on the timeline, so Suggest finds the
    /// bit of each part that fits there and Swap in puts one in. Null lets go.
    /// </summary>
    internal void HighlightInClip(ClipRow row, RollRange? range)
    {
        if (range is not { } r)
        {
            LoopOver(null);
            return;
        }
        long a = Math.Max(row.At + r.Start, row.Clip.Start), b = Math.Min(row.At + r.End, row.Clip.End);
        if (b <= a) return;
        LoopOver(new RollRange(a, b));
        if (SwapTarget is null) return;
        RightTab = SuggestTabIndex;
        Message = $"{LoopText} of {row.Clip.Name} picked: Suggest finds the bit of each part that fits there, and Swap in puts one in. Right-click the bar numbers to let go.";
    }

    PlaygroundClip? _swapFor;

    /// <summary>
    /// The highlight moved or another clip was picked: the suggestions follow, for the clip the highlight is in (its kind
    /// of part) or for the bars highlighted.
    /// </summary>
    void SuggestFollow(bool highlightMoved)
    {
        var target = SwapTarget;
        if (target?.Clip == _swapFor && !highlightMoved) return;
        _swapFor = target?.Clip;
        OnPropertyChanged(nameof(SwapTarget));
        if (target is not null && RoleChips.FirstOrDefault(c => c.Role == target.Role) is { } chip && chip != SuggestRole)
        {
            SuggestRole = chip;   // finds again
            return;
        }
        if (Matches.Count > 0 || target is not null) _ = FindMatchesAsync();
    }

    partial void OnSelectedClipChanged(ClipRow? oldValue, ClipRow? newValue)
    {
        if (oldValue?.Clip != newValue?.Clip) PickedNotes = null;   // another clip: none of its notes are picked yet
    }

    partial void OnSelectedClipChanged(ClipRow? value)
    {
        if (!_showing) _chordLaneLast = false;   // a clip picked (from the track list, or one just put in): Delete is for it now
        SuggestFollow(false);
    }

    /// <summary>
    /// Cuts <paramref name="from"/>–<paramref name="to"/> out of the clips on <paramref name="layer"/>, as a DAW does when
    /// a clip is put on top of others: what's left either side stays where it was.
    /// </summary>
    static void CutOut(PlaygroundLayer layer, long from, long to)
    {
        for (var i = layer.Clips.Count - 1; i >= 0; i--)
        {
            var c = layer.Clips[i];
            if (c.End <= from || c.Start >= to) continue;
            layer.Clips.RemoveAt(i);
            if (c.End > to) layer.Clips.Insert(i, Piece(c, to, c.End));
            if (c.Start < from) layer.Clips.Insert(i, Piece(c, c.Start, from));
        }
    }

    /// <summary>The bit of <paramref name="clip"/> from <paramref name="from"/> to <paramref name="to"/>: its notes where they were, each go round still moved as far.</summary>
    internal static PlaygroundClip Piece(PlaygroundClip clip, long from, long to)
    {
        var piece = clip.Copy();
        var loop = Math.Max(1, clip.Loop);
        var into = clip.Offset % loop + from - clip.Start;
        piece.Start = from;
        piece.Length = to - from;
        piece.Offset = into % loop;
        piece.Fits = [.. clip.Fits.Skip((int)(into / loop))];
        piece.StartMoved(from - clip.Start);
        return piece;
    }

    /// <summary>
    /// Puts <paramref name="clip"/> on track <paramref name="row"/>, cutting out what was there (a track written from the
    /// chords, or one past the last, gives it a new track named <paramref name="trackName"/>), then fits and picks it.
    /// </summary>
    LayerRow PlaceClip(PlaygroundClip clip, int row, string trackName)
    {
        LayerRow to;
        if (row >= 0 && row < Layers.Count && !Layers[row].IsWritten)
        {
            to = Layers[row];
            CutOut(to.Layer, clip.Start, clip.End);
        }
        else
        {
            var layer = new PlaygroundLayer
            {
                Name = UniqueName(trackName), Role = clip.Role, Program = GmSounds.DefaultFor(clip.Role), SourcePath = clip.SourcePath, SourceHash = clip.SourceHash,
            };
            Section.Layers.Add(layer);
            Layers.Add(to = new LayerRow(this, layer));
        }
        to.Layer.Clips.Add(clip);
        Reach(clip.End);
        Refit(to.Layer, clip);
        to.Layer.Render();
        to.Changed();
        SelectedLayer = to;
        SelectedClip = new ClipRow(this, to, clip);
        return to;
    }

    /// <summary>
    /// A library part as a clip: one go round of its bars, or of <paramref name="period"/> for a stretch cut from it (chord
    /// parts turned into smooth inversions when that's on; a stretch keeps its own voicing, which it was picked for
    /// joining on).
    /// </summary>
    PlaygroundClip LibraryClip(MidiEntry entry, IReadOnlyList<NoteEvent> notes, long start, long? length = null, long period = 0)
    {
        var loop = period > 0 ? period : Math.Max(1, PhraseAnalyser.BarsOf(notes)) * (long)Bar;
        var one = notes.Where(n => n.Start < loop).ToList();
        if (SmoothIncoming && entry.Role == MidiRoles.Chords && period == 0) one = Inversions.Smooth(one, ChordCentre(null));
        return PlaygroundClip.Of(entry.Title.Length > 0 ? entry.Title : entry.SourceName, entry.Role, one, loop, start, length ?? loop,
                                 entry.Phrase.Source.Path, entry.Phrase.Hash);
    }

    /// <summary>A suggestion picked for bars of a clip: it takes their place, and the rest of the clip stays either side.</summary>
    internal void SwapIn(ClipRow target, (long Start, long End) bars, MidiEntry entry, IReadOnlyList<NoteEvent> notes, long period = 0)
    {
        if (notes.Count == 0) return;
        Remember();
        var was = target.Clip.Name;
        var clip = LibraryClip(entry, notes, bars.Start, bars.End - bars.Start, period);
        PlaceClip(clip, Layers.IndexOf(target.Track), clip.Name);
        Changed();
        Message = $"Bars {bars.Start / Bar + 1}–{(bars.End + Bar - 1) / Bar} of {was} swapped for {clip.Name} from {entry.SourceName}. Undo puts them back.";
    }

    /// <summary>
    /// A suggestion dragged from the side panel, or .mid files dragged in from Explorer, let go on the timeline at
    /// <see cref="PartDrop.Start"/> on track <see cref="PartDrop.Row"/> (one past the last: a new track).
    /// </summary>
    [RelayCommand]
    void DropPart(PartDrop? drop)
    {
        switch (drop?.Part)
        {
            case PlaygroundMatchRow m when m.Match.Notes.Count > 0:
                Remember();
                var clip = LibraryClip(m.Match.Entry, m.Match.Notes, drop.Start, period: m.Match.Period);
                PlaceClip(clip, drop.Row, clip.Name);
                Changed();
                Message = $"Added {clip.Name} from {m.Match.Entry.SourceName} at bar {drop.Start / Bar + 1}. Parts from {m.Match.Entry.SourceName} won't be suggested now.";
                break;
            case MidiRow r when r.Channel.Notes.Count > 0:
                StopBrowsing();
                Remember();
                var part = LibraryClip(r.Entry, r.Channel.Notes, drop.Start);
                part.Name = r.Title;
                PlaceClip(part, drop.Row, r.Title);
                Changed();
                Message = $"Added {r.Title} at bar {drop.Start / Bar + 1}. Parts from {r.Entry.SourceName} won't be suggested now.";
                break;
            case IReadOnlyList<string> paths:
                DropFiles(paths, drop.Row, drop.Start);
                break;
        }
    }

    /// <summary>The parts of .mid files, kept in time with each other, as clips from <paramref name="start"/>: one part goes on the track it was let go on, more go on new tracks.</summary>
    void DropFiles(IReadOnlyList<string> paths, int row, long start)
    {
        var parts = new List<(string Name, string Role, List<NoteEvent> Notes, string Path, string Hash)>();
        var problems = new List<string>();
        foreach (var path in paths.Where(MidiFileReader.IsMidiPath))
        {
            try
            {
                var file = Path.GetFileNameWithoutExtension(path);
                var read = MidiFileReader.Read(path).Select(p => (Part: p, Notes: PhraseAnalyser.Normalise(p.Notes, p.Ppq, keepPlace: true))).Where(p => p.Notes.Count > 0).ToList();
                if (read.Count == 0) continue;
                // The whole file moves to start in its first bar with notes, its parts staying in time together.
                var shift = read.Min(p => p.Notes[0].Start) / Bar * Bar;
                foreach (var (part, notes) in read)
                {
                    var moved = notes.Select(n => n with { Start = n.Start - shift }).ToList();
                    var analysis = PhraseAnalyser.Analyse(moved, $"{part.Name} {file}", part.Drums);
                    parts.Add((part.Name.Trim().Length > 0 ? part.Name.Trim() : file, analysis.Role, moved, path, analysis.Hash));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                problems.Add($"{Path.GetFileName(path)} ({ex.Message})");
            }
        }
        if (parts.Count == 0)
        {
            Message = problems.Count > 0 ? $"Couldn't read {string.Join(", ", problems)}." : "No notes in that: only .mid files with notes can be dropped in.";
            return;
        }
        Remember();
        foreach (var (name, role, notes, path, hash) in parts)
        {
            var loop = Math.Max(1, PhraseAnalyser.BarsOf(notes)) * (long)Bar;
            PlaceClip(PlaygroundClip.Of(name, role, notes, loop, start, loop, path, hash), parts.Count == 1 ? row : Layers.Count, name);
        }
        Changed();
        Message = $"Added {(parts.Count == 1 ? parts[0].Name : $"{parts.Count} parts")} from {string.Join(", ", paths.Where(MidiFileReader.IsMidiPath).Select(Path.GetFileName))} at bar {start / Bar + 1}."
                  + (problems.Count > 0 ? $" Couldn't read {string.Join(", ", problems)}." : "");
    }

    /// <summary>Hands a clip changed by hand back to shift to fit: its octave kept, the rest worked out again.</summary>
    internal void FitAgain(ClipRow row) =>
        EditClip(row, c =>
        {
            c.Pinned = false;
            c.Transpose = (int)Math.Round(c.Transpose / 12.0) * 12;
            c.Inversion = 0;
            Refit(row.Track.Layer, c);
        });

    /// <summary>
    /// How the clip follows the chord track, picked (Solomon, 2026-10-22: "some might fit better for different types of
    /// music"): it follows again that way, as Follow the chords again does.
    /// </summary>
    internal void SetFollow(ClipRow row, FollowWay way)
    {
        EditClip(row, c =>
        {
            if (way == FollowWay.None)
            {
                // It stays exactly as it plays now: a part following the chords keeps the notes it plays on them.
                if (c.Under.Count > 0) c.Bake();
                c.Follow = way;
                return;
            }
            c.Follow = way;
            if (c.Pinned)
            {
                c.Pinned = false;
                c.Transpose = (int)Math.Round(c.Transpose / 12.0) * 12;
                c.Inversion = 0;
            }
            Refit(row.Track.Layer, c);
        });
        Message = way == FollowWay.None ? $"{row.Clip.Name} stays as it is, whatever the chords."
            : HasChordTrack ? $"{row.Clip.Name} follows the chord track: {ChordFollow.Label(way).ToLowerInvariant()}."
            : $"{row.Clip.Name} will {ChordFollow.Label(way).ToLowerInvariant()} once the chord track has chords under it: Put the chords heard there (under Follows the chords) to start.";
    }

    /// <summary>Its chords turned over by hand, up or down: it stays like that until Fit again.</summary>
    internal void TurnClip(ClipRow row, int steps, int gesture = 0) => EditClip(row, c => { c.Inversion += steps; c.Pinned = true; }, gesture: gesture);

    /// <summary>What a kind of part is called, one of them: Chords, Bassline, Melody, Arp, Drums.</summary>
    static string KindName(string role) => MidiRoles.Plural(role) switch { "Melodies" => "Melody", "Basslines" => "Bassline", "Arps" => "Arp", var p => p };

    [RelayCommand]
    void DeleteClip(ClipPick? pick)
    {
        if (pick is null || Layers.FirstOrDefault(r => r.Layer == pick.Track) is not { } track) return;
        RemoveClip(new ClipRow(this, track, pick.Clip));
    }

    // ---- the grid clips snap to ----

    public static IReadOnlyList<string> GridChoices { get; } = ["Bar", "Half bar", "Beat"];
    /// <summary>What clips snap to when dragged (hold Alt to drag freely).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Grid))] string _gridPick = "Bar";
    public long Grid => GridPick switch { "Beat" => Ppq, "Half bar" => Bar / 2, _ => Bar };

    /// <summary>
    /// Snapping like Bitwig (Solomon, 2026-10-19): Grid lands a dragged clip on grid lines; Relative (Bitwig's "grid
    /// offset") moves it in whole grid steps keeping how far off the grid it was; Objects (Bitwig's "events") catches the
    /// edges of other clips, the highlight, labels and the play marker. With more than one on, the nearest wins.
    /// </summary>
    /// <summary>The piano roll's "Note names" chip (Solomon, 2026-10-19): off, names, or what each note is over the chord.</summary>
    public static IReadOnlyList<string> NoteLabelChoices { get; } = ["Off", "Names", "Over the chord"];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NoteLabels))] string _noteLabelPick = "Off";
    public NoteLabelKind NoteLabels => NoteLabelPick switch { "Names" => NoteLabelKind.Names, "Over the chord" => NoteLabelKind.Degrees, _ => NoteLabelKind.Off };
    partial void OnNoteLabelPickChanged(string value) => SaveOption(NoteLabelsKey, value);

    /// <summary>
    /// How the piano roll colours the notes: all alike, or by how strongly each fits the chord under it (Solomon,
    /// 2026-10-20: "maybe the colour should show the strength"), brightest for the strongest and red where it fights.
    /// </summary>
    public const string ByFit = "By fit";
    public static IReadOnlyList<string> NoteColourChoices { get; } = ["Plain", ByFit];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FitColours))] string _noteColourPick = ByFit;
    public bool FitColours => NoteColourPick == ByFit;
    partial void OnNoteColourPickChanged(string value)
    {
        SaveOption(NoteColoursKey, value);
        SelectedClip?.GuideChanged();
    }

    [ObservableProperty] bool _snapGrid = true;
    [ObservableProperty] bool _snapRelative = true;
    [ObservableProperty] bool _snapObjects = true;
    partial void OnSnapGridChanged(bool value) => SaveOption(SnapGridKey, value ? "1" : "0");
    partial void OnSnapRelativeChanged(bool value) => SaveOption(SnapRelativeKey, value ? "1" : "0");
    partial void OnSnapObjectsChanged(bool value) => SaveOption(SnapObjectsKey, value ? "1" : "0");

    /// <summary>
    /// Notes dragged, put in or moved with the arrow keys on the piano roll go only to notes that go with the chord under
    /// them (Solomon, 2026-10-27: "select a few notes and have it snapped to chord so it only allows notes that go with the
    /// chords to be moved to").
    /// </summary>
    [ObservableProperty] bool _snapChord;
    partial void OnSnapChordChanged(bool value) => SaveOption(SnapChordKey, value ? "1" : "0");

    /// <summary>The notes picked on the piano roll (Ctrl+click, a Ctrl+drag box, Ctrl+A), as it shows them: snapped, moved and deleted together.</summary>
    [ObservableProperty] IReadOnlyList<NoteEvent>? _pickedNotes;

    /// <summary>
    /// The notes picked on the piano roll (all of them when none are) each moved to the nearest note that goes with the chord
    /// under it, a root, 3rd, 5th, 7th or a note the chord's name has (Solomon, 2026-10-27). One undo step; they stay picked.
    /// </summary>
    [RelayCommand]
    void SnapToChords()
    {
        if (SelectedClip is not { } row) { Message = "Pick a clip first, then the notes to snap on its piano roll."; return; }
        if (row.FitsChords is not { } fits) { Message = "No chords under it to snap to."; return; }
        var notes = row.Notes;
        // (Read before the notes change: the piano roll lets go of picked notes that are no longer there.)
        var anyPicked = PickedNotes is { Count: > 0 };
        var picked = anyPicked ? PickedNotes!.Where(notes.Contains).ToHashSet() : notes.ToHashSet();
        var moved = new List<NoteEvent>();
        var snapped = notes.Select(n =>
        {
            if (!picked.Contains(n)) return n;
            var to = n with { Key = ClipRow.NearestFit(n, fits, -1) };
            moved.Add(to);
            return to;
        }).ToList();
        var changed = snapped.Zip(notes).Count(x => x.First != x.Second);
        if (changed == 0) { Message = $"{(anyPicked ? "The notes picked" : "Its notes")} all go with the chords already."; return; }
        SetClipNotes(row, snapped);
        PickedNotes = anyPicked ? moved : null;
        Message = $"{changed} note{(changed == 1 ? "" : "s")} moved onto the chords. Undo puts {(changed == 1 ? "it" : "them")} back.";
    }

    [RelayCommand] void SelectClip(ClipPick? pick) => PickClip(pick is null ? null : Layers.FirstOrDefault(r => r.Layer == pick.Track), pick?.Clip);

    /// <summary>The drag or typed value the last undo step was taken for: its other steps go in the same one.</summary>
    int _gesture;

    /// <summary>
    /// A track's volume moved (Solomon, 2026-10-22: "should be able to change the volume of the tracks individually"): the
    /// fader follows the pointer, and letting go makes it one undo step and plays it so.
    /// </summary>
    internal void SetVolume(LayerRow row, FaderMove move)
    {
        if (move.Gesture == 0 || move.Gesture != _gesture) Remember();
        _gesture = move.Gesture;
        row.Layer.Volume = Math.Clamp(move.Value, 0, PlaygroundLayer.MaxVolume);
        row.VolumeChanged();
        if (move.Done) Changed(harmony: false);
    }

    internal void EditClip(ClipRow row, Action<PlaygroundClip> change, bool harmony = true, int gesture = 0)
    {
        if (gesture == 0 || gesture != _gesture) Remember();
        _gesture = gesture;
        change(row.Clip);
        row.Track.Layer.Render();
        Reach(row.Clip.End);
        row.Track.Changed();
        row.Changed();
        Changed(harmony);
    }

    /// <summary>The clip's notes changed on the piano roll (one go round, as it plays): the track is no longer rewritten from the chords.</summary>
    internal void SetClipNotes(ClipRow row, IReadOnlyList<NoteEvent> notes) =>
        EditClip(row, c =>
        {
            if (c.Under.Count > 0)
            {
                // Following the chord track: the clip keeps playing just as it does, with the go round on the piano roll as
                // changed (and from then on it's one go round as long as the clip, so all of it shows).
                long from = c.RepeatAt(0) - c.Start, to = from + c.Loop;
                c.Bake();
                c.Notes = c.Notes.Where(n => n.Start < from || n.Start >= to)
                           .Concat(notes.Select(n => n with { Start = n.Start + from }).Where(n => n.Start >= 0 && n.Start < c.Length)).ToList();
            }
            else c.Notes = notes.Select(n => n with { Key = Math.Clamp(n.Key - c.Shift(0), 0, 127) }).ToList();
            c.Inversion = 0;   // the notes as shown are already turned
            c.Pinned = true;   // changed by hand: it stays as it is until Fit again
            row.Track.Layer.Written = "";
            row.Track.Layer.GrooveData = "";
        });

    /// <summary>
    /// What <paramref name="judge"/> makes of the idea's chords with <paramref name="note"/> (one go round, as the piano roll
    /// shows it) put in the clip, as <see cref="SetClipNotes"/> would; the clip is put back as it was straight after.
    /// </summary>
    internal T WithNoteAdded<T>(ClipRow row, NoteEvent note, Func<SectionHarmony, T> judge)
    {
        var clip = row.Clip;
        var layer = row.Track.Layer;
        var (notes, inversion, written) = (clip.Data, clip.Inversion, layer.Written);
        // The parts written from the chords, as they are: they follow when the note changes the chords.
        var others = Section.Layers.Where(l => l != layer && l.Written.Length > 0).ToList();
        var kept = others.SelectMany(l => l.Clips).Select(c => (Clip: c, c.Data, c.Loop, c.Offset, c.Fits)).ToList();
        var shown = row.Notes.ToList();
        shown.Add(note with { Start = (note.Start % clip.Loop + clip.Loop) % clip.Loop });
        try
        {
            clip.Notes = shown.Select(n => n with { Key = Math.Clamp(n.Key - clip.Shift(0), 0, 127) }).ToList();
            clip.Inversion = 0;
            layer.Written = "";
            layer.Render();
            if (others.Count > 0 && LaneForWriting() is var lane && WrittenKey(lane) != _writtenFor) WriteAgain(lane);
            return judge(SectionHarmony.Of(Section, _session.Key, _session.Style));
        }
        finally
        {
            (clip.Data, clip.Inversion, layer.Written) = (notes, inversion, written);
            layer.Render();
            if (kept.Count > 0)
            {
                foreach (var k in kept) (k.Clip.Data, k.Clip.Loop, k.Clip.Offset, k.Clip.Fits) = (k.Data, k.Loop, k.Offset, k.Fits);
                foreach (var l in others) l.Render();
            }
        }
    }

    /// <summary>Moved up or down by hand: it stays there instead of moving to fit, until Fit again.</summary>
    internal void MoveClip(ClipRow row, int semitones, int gesture = 0) => EditClip(row, c => { c.Transpose += semitones; c.Pinned = true; }, gesture: gesture);

    internal void RemoveClip(ClipRow row)
    {
        if (row.Track.Layer.Clips.Count <= 1) { RemoveLayer(row.Track); return; }
        Remember();
        row.Track.Layer.Clips.Remove(row.Clip);
        row.Track.Layer.Render();
        row.Track.Changed();
        SelectedClip = null;
        Changed();
    }

    /// <summary>A copy of the clip straight after it on the same track.</summary>
    internal void DuplicateClip(ClipRow row)
    {
        Remember();
        var copy = row.Clip.Copy();
        copy.Start = row.Clip.End;
        CutOut(row.Track.Layer, copy.Start, copy.End);   // over what was there, as in a DAW
        row.Track.Layer.Clips.Add(copy);
        Reach(copy.End);
        Refit(row.Track.Layer, copy);   // fitted to the chords where it lands
        row.Track.Layer.Render();
        row.Track.Changed();
        SelectedClip = new ClipRow(this, row.Track, copy);
        Changed();
    }

    /// <summary>
    /// The song's parts switched off in "Fit to" muted, the rest playing, in one Undo step (Solomon, 2026-10-19: "see the
    /// midi from the channel when i pick fit to … make it so i can hide them then i can test alongside them").
    /// </summary>
    public void MuteSongParts(IReadOnlyCollection<string> song, IReadOnlyCollection<string> off)
    {
        // Only the parts whose chip changed (a part muted by hand with M stays muted), found by the song part each track
        // is, not by its name (the part being tried may share one). The chips aren't undone, so neither is this.
        var was = _songOff;
        _songOff = off.ToHashSet();
        bool Flipped(PlaygroundLayer l) => l.SongPart.Length > 0 && song.Contains(l.SongPart) && (was is null || was.Contains(l.SongPart) != off.Contains(l.SongPart));
        // The undo steps too, so going back to before a chip was clicked leaves the part as the chip shows it.
        ChangeHistory(s =>
        {
            foreach (var l in s.Timeline.Layers.Where(Flipped)) l.Muted = off.Contains(l.SongPart);
        });
        var rows = Layers.Where(r => Flipped(r.Layer) && r.Layer.Muted != off.Contains(r.Layer.SongPart)).ToList();
        if (rows.Count == 0) return;
        foreach (var r in rows)
        {
            r.Layer.Muted = off.Contains(r.Layer.SongPart);
            r.Changed();
        }
        Changed(harmony: true);
    }

    HashSet<string>? _songOff;

    /// <summary>Every undo and redo step changed by <paramref name="change"/>, for a change that isn't undone itself.</summary>
    void ChangeHistory(Action<PlaygroundSession> change)
    {
        foreach (var steps in new[] { _undo, _redo })
            for (var i = 0; i < steps.Count; i++)
            {
                if (PlaygroundSession.FromJson(steps[i]) is not { } step) continue;
                change(step);
                if (step.ToJson() is var json && json != steps[i]) steps[i] = json;
            }
    }

    internal void ToggleMute(LayerRow row) => EditLayer(row, l => l.Muted = !l.Muted);

    internal void ToggleSolo(LayerRow row)
    {
        EditLayer(row, l => l.Solo = !l.Solo);
        foreach (var r in Layers) r.Changed();
    }

    // ---- writing parts from the chords ----

    public static IReadOnlyList<string> VoicingChoices { get; } =
        ["Close", "Rootless (jazz piano)", "Drop 2", "Gospel", "Classical (4 voices)", "Shell (left hand)"];
    public static IReadOnlyList<string> RhythmChoices { get; } = ["Held", "Every beat", "Charleston", "Stabs on 2 and 4", "Pushed (an eighth early)"];
    public static IReadOnlyList<string> BassChoices { get; } = ["Roots", "Root and fifth", "Walking", "Gospel runs"];

    [ObservableProperty] string _voicing = "Gospel";
    [ObservableProperty] string _rhythm = "Held";
    [ObservableProperty] string _bassPick = "Walking";

    static VoicingStyle VoicingOf(string choice) => VoicingChoices.ToList().IndexOf(choice) switch
    {
        1 => VoicingStyle.Rootless, 2 => VoicingStyle.Drop2, 3 => VoicingStyle.Gospel, 4 => VoicingStyle.Classical, 5 => VoicingStyle.Shell,
        _ => VoicingStyle.Close,
    };

    static CompRhythm RhythmOf(string choice) => RhythmChoices.ToList().IndexOf(choice) switch
    {
        1 => CompRhythm.Beats, 2 => CompRhythm.Charleston, 3 => CompRhythm.TwoAndFour, 4 => CompRhythm.Anticipate, _ => CompRhythm.Held,
    };

    static BassStyle BassOf(string choice) => BassChoices.ToList().IndexOf(choice) switch
    {
        1 => BassStyle.RootFifth, 2 => BassStyle.Walking, 3 => BassStyle.Gospel, _ => BassStyle.Roots,
    };

    /// <summary>
    /// The chords for writing parts to: the chords heard from the parts that weren't written from them (so rewriting a
    /// written part doesn't feed on itself).
    /// </summary>
    IReadOnlyList<TimedChord> LaneForWriting()
    {
        var written = Section.Layers.Where(l => l.Written.Length > 0).ToList();
        return SectionHarmony.Of(Section, _session.Key, _session.Style, written).Lane;
    }

    [RelayCommand]
    void WriteChords() => Write("chords:" + Voicing + ":" + Rhythm, $"Chords · {Voicing.Split(' ')[0]}", MidiRoles.Chords);

    [RelayCommand]
    void WriteBass() => Write("bass:" + BassPick, $"Bass · {BassPick}", MidiRoles.Bass);

    [RelayCommand]
    void WriteGuideTones() => Write("guide", "Guide tones", MidiRoles.Melody);

    void Write(string how, string name, string role)
    {
        var lane = LaneForWriting();
        if (lane.Count == 0)
        {
            Message = "There are no chords to write from yet: bring in a chord or bass part first.";
            return;
        }
        Remember();
        // Over the highlight, or the whole timeline.
        var (start, length) = Place;
        var layer = AddLayer(name, role, Written(how, LaneAt(lane, start, start + length), length), written: how, start: start, length: length);
        layer.Program = role == MidiRoles.Melody ? 73 : layer.Program;
        _writtenFor = WrittenKey(lane);
        Changed();
        Message = $"Wrote {name} from the chords{(Loop is null ? "" : $", {LoopText.ToLowerInvariant()}")}. Change the chords and it follows; edit its notes and it's yours.";
    }

    static IReadOnlyList<NoteEvent> Written(string how, IReadOnlyList<TimedChord> lane, long length)
    {
        var parts = how.Split(':');
        return parts[0] switch
        {
            "chords" => Voicer.Voice(lane, VoicingOf(parts.ElementAtOrDefault(1) ?? ""), RhythmOf(parts.ElementAtOrDefault(2) ?? ""), length),
            "bass" => Voicer.Bass(lane, BassOf(parts.ElementAtOrDefault(1) ?? ""), length),
            "guide" => Voicer.Voice(lane, VoicingStyle.GuideTones, CompRhythm.Held, length, 92),
            _ => [],
        };
    }

    /// <summary>The chords (as a chart) and the written clips' places its written parts were last written for.</summary>
    string? _writtenFor;

    string WrittenKey(IReadOnlyList<TimedChord> lane) =>
        ChordLane.ToChart(lane, Section.Length) + "@" +
        string.Join(",", Section.Layers.Where(l => l.Written.Length > 0).SelectMany(l => l.Clips).Select(c => $"{c.Start}+{c.Length}"));

    /// <summary>Parts written from the chords are written again, clip by clip, when the chords under them change or they're moved.</summary>
    void RewriteWritten()
    {
        if (Section.Layers.All(l => l.Written.Length == 0)) return;
        _harmony = null;
        var lane = LaneForWriting();
        _writtenFor = WrittenKey(lane);
        WriteAgain(lane);
        foreach (var row in Layers) row.Changed();
        SelectedClip?.Changed();
    }


    /// <summary>Every part written from the chords written again over <paramref name="lane"/>.</summary>
    void WriteAgain(IReadOnlyList<TimedChord> lane)
    {
        foreach (var l in Section.Layers.Where(l => l.Written.Length > 0))
        {
            foreach (var c in l.Clips)
            {
                var under = LaneAt(lane, c.Start, c.End);
                c.Notes = under.Count == 0 ? [] : WithGroove(l, Written(l.Written, under, c.Length), under, c.Length);
                c.Loop = c.Length;
                c.Offset = 0;
                c.Fits = [];
            }
            l.Render();
        }
    }

    /// <summary>A written part with the groove copied onto it put on again (after the chords changed and it was rewritten).</summary>
    static IReadOnlyList<NoteEvent> WithGroove(PlaygroundLayer layer, IReadOnlyList<NoteEvent> notes, IReadOnlyList<TimedChord> lane, long length) =>
        layer.GrooveData.Length == 0 ? notes : Groove.Copy(notes, layer.GrooveNotes, layer.GrooveDepth, length, lane, layer.GrooveAmount);

    partial void OnSelectedLayerChanged(LayerRow? value)
    {
        if (!_showing) _chordLaneLast = false;   // a track picked: Delete is for its clip now
        // Picking a track picks its first clip, unless one of its clips is picked already.
        if (SelectedClip?.Track != value)
            SelectedClip = value is { Layer.Clips.Count: > 0 } t ? new ClipRow(this, t, t.Layer.Clips[0]) : null;
        OnPropertyChanged(nameof(IsChordLayer));
    }

    public bool IsChordLayer => SelectedLayer?.Role == MidiRoles.Chords;

    /// <summary>Chord parts put in are turned into the inversions that flow best (Settings › Playground).</summary>
    [ObservableProperty] bool _smoothIncoming = true;

    /// <summary>The tab showing on the right: Suggest, Chords, Write, Reharm, Song.</summary>
    [ObservableProperty] int _rightTab;

    // ---- smooth inversions ----

    /// <summary>Where chord parts already in the section sit, so a new one is turned to sit with them.</summary>
    double? ChordCentre(PlaygroundLayer? except)
    {
        var others = Section.Layers.Where(l => l != except && l.Role == MidiRoles.Chords && !l.Muted).SelectMany(l => l.Notes).ToList();
        return others.Count == 0 ? null : others.Average(n => n.Key);
    }

    [RelayCommand]
    void SmoothInversions()
    {
        if (SelectedLayer is not { } row) return;
        var centre = ChordCentre(row.Layer);
        var copy = row.Layer.Copy();
        MapClips(copy, (played, _, _) => Inversions.Smooth(played, centre), like: row.Layer);
        if (copy.Notes.SequenceEqual(row.Layer.Notes))
        {
            Message = $"{row.Name}'s chords already move as smoothly as they can.";
            return;
        }
        EditLayer(row, l => { l.Clips = copy.Clips; l.Written = ""; });
        Message = $"{row.Name}'s chords are turned into the inversions that move least. Undo takes it back.";
    }

    // ---- energy ----

    public static IReadOnlyList<string> FeelChoices { get; } = ["Any", "Verse", "Build", "Drop", "Breakdown"];
    [ObservableProperty] string _feelPick = "Any";
    [ObservableProperty] double[] _energyCurve = [];
    [ObservableProperty] double[]? _energyTarget;
    [ObservableProperty] string _energyText = "";

    /// <summary>
    /// What the bars being worked on are for: the feel of the label over them. Picking one with no label there puts a
    /// label over them (the highlight, or the whole timeline) named after it.
    /// </summary>
    partial void OnFeelPickChanged(string value)
    {
        if (_showing) return;
        var feel = Enum.TryParse<SectionFeel>(value, out var f) ? f : SectionFeel.Any;
        if (feel == FeelHere) return;
        Remember();
        if (LabelHere is { } label) label.Feel = feel;
        else if (feel != SectionFeel.Any)
        {
            var (start, length) = Place;
            Section.Labels.Add(new PlaygroundLabel { Name = feel.ToString(), Start = start, Length = length, Feel = feel });
            var keep = Loop;
            ShowSection();
            Loop = keep;
        }
        Changed(harmony: false);
        if (Matches.Count > 0) _ = FindMatchesAsync();
    }

    /// <summary>The timeline's tension curve, bar by bar, and under each label the one its feel asks for.</summary>
    void RefreshEnergy()
    {
        var h = Harmony;
        var bars = Section.Bars;
        EnergyCurve = h.Notes.Count == 0 && Section.Layers.All(l => l.Role != MidiRoles.Drums)
            ? []
            : Energy.Curve(PlaygroundSession.Audible(Section).SelectMany(l => l.Notes).ToList(), Section.Length, "", h.Key);
        var asked = Section.Labels.Where(l => l.Feel != SectionFeel.Any).ToList();
        if (asked.Count == 0) EnergyTarget = null;
        else
        {
            // Bars with no label (or a label that asks for nothing) have no dashed line.
            var target = Enumerable.Repeat(double.NaN, bars).ToArray();
            foreach (var l in asked)
            {
                int from = (int)(l.Start / Bar), count = (int)Math.Max(1, (l.Length + Bar - 1) / Bar);
                var curve = Energy.Target(l.Feel, count);
                for (var i = 0; i < count && from + i < bars; i++) target[from + i] = curve[i];
            }
            EnergyTarget = target;
        }
        EnergyText = EnergyCurve.Length == 0 ? "" : asked.Count == 0 ? "Energy, bar by bar" : "Energy, bar by bar · dashed: what each label's feel asks for";
    }

    // ---- reharmonisation and next-chord ideas ----

    public ObservableCollection<ReharmRow> Ideas { get; } = [];
    public ObservableCollection<NextChordRow> NextChords { get; } = [];
    [ObservableProperty] string _nextChordsText = "";

    void RefreshIdeas()
    {
        if (_tryBefore is not null) return;   // an idea being tried: the ideas stay those for the chords as they were
        Ideas.Clear();
        if (IsQuick) return;   // the small playground shows no ideas, so it doesn't work them out (or read every chord part for next chords)
        var h = Harmony;
        if (h.Key is { } k && h.Lane.Count > 0)
            foreach (var r in Reharmoniser.All(h.Lane, k.Tonic, k.Minor)) Ideas.Add(new ReharmRow(this, r));
        _ = RefreshHarmoniseAsync();
        ChordsChangedUnderReharm();
        _ = RefreshNextChordsAsync();
    }

    ProgressionModel? _model;
    string _modelFor = "";
    /// <summary>The learning going on now and what it's for, so callers asking meanwhile wait for it instead of starting their own.</summary>
    Task<ProgressionModel>? _learning;
    string _learningFor = "";
    CancellationTokenSource? _learningCts;

    int _nextRequest;

    /// <summary>
    /// What your chord parts usually do next, learned once from the MIDI read so far (leaving out the songs the idea's
    /// parts come from). Only reads the MIDI when the page is open (<paramref name="load"/>).
    /// </summary>
    async Task<ProgressionModel?> ModelAsync(bool load = false)
    {
        var catalogue = load ? await _main.Midi.CatalogueAsync() : _main.Midi.LoadedCatalogue;
        if (catalogue is null) return _model ??= ProgressionModel.Learn([]).With(ProgressionModel.FromCharts);
        var songs = Harmony.Songs;
        var key = string.Join("|", songs.OrderBy(s => s)) + ":" + catalogue.Entries.Count;
        if (_model is not null && _modelFor == key) return _model;
        // One run through every chord part for each set of songs left out: one for songs no longer in the idea is stopped.
        if (_learning is null || _learningFor != key)
        {
            _learningCts?.Cancel();
            var cts = _learningCts = new CancellationTokenSource();
            _learningFor = key;
            _learning = UiWork.Run(() => ProgressionModel.Learn(catalogue.Entries, songs, cts.Token).With(ProgressionModel.FromCharts));
        }
        var learning = _learning;
        try
        {
            var model = await learning;
            if (_learning == learning) (_model, _modelFor, _learning) = (model, key, null);
            return model;
        }
        catch (OperationCanceledException) { return null; }   // the songs changed meanwhile: the newer call learns for them
        catch when (_learning == learning)
        {
            (_learning, _learningFor) = (null, "");   // it went wrong: the next call tries again
            throw;
        }
    }

    /// <summary>The page opened: the MIDI is read and learned from once, then the next chords and Harmonise use it.</summary>
    async Task LearnThenRefreshAsync()
    {
        await ModelAsync(load: true);
        _ = RefreshNextChordsAsync();
        _ = RefreshHarmoniseAsync();
    }

    /// <summary>
    /// The page was opened: the MIDI is being read now, so the next-chord ideas can learn from it, and Suggest lists parts
    /// straight away (Solomon, 2026-10-19: "the list of midi didnt load initially", until he clicked another kind and back).
    /// </summary>
    public void Opened()
    {
        _ = LearnThenRefreshAsync();
        _ = RefreshProgressionsAsync(load: true);
        if (IsEmpty) Midi.Opened();   // its list shows here
        else if (Matches.Count == 0 && !IsMatching) _ = FindMatchesAsync();
    }

    /// <summary>
    /// What usually comes next, learned from your chord parts. Only reads the MIDI when the page is open (<paramref name="load"/>),
    /// so building this page when the app starts doesn't read and index every MIDI part for people who never come here.
    /// </summary>
    async Task RefreshNextChordsAsync()
    {
        var request = ++_nextRequest;
        NextChords.Clear();
        // After the chord track's last chord when it has some: that's where a chord added goes.
        var lane = HasChordTrack ? Section.ChordTrack.OrderBy(c => c.Start).Select(c => c.Timed).ToList() : Harmony.Lane;
        if (lane.Count == 0) { NextChordsText = ""; return; }
        await ModelAsync();
        if (_model is null || request != _nextRequest) return;   // the chords changed while it learned; the newer call shows its own
        var last = lane[^1].Chord;
        ChordSymbol? before = lane.Count > 1 ? lane[^2].Chord : null;
        NextChords.Clear();
        foreach (var (chord, share) in _model.Next(last, before)) NextChords.Add(new NextChordRow(this, chord, share, Harmony.Key));
        var charts = _model.Prior?.Learned ?? 0;
        NextChordsText = _model.Learned == 0
            ? charts == 0 ? "" : $"After {last.NameIn(Harmony.Key)}, in {charts:N0} jazz and gospel charts:"
            : $"After {last.NameIn(Harmony.Key)}, in your {_model.Learned:N0} chord parts (other songs) and {charts:N0} jazz and gospel charts:";
    }

    /// <summary>
    /// An idea's chords kept. Chords made for a melody or bassline (Harmonise) go on the chord track (chords first, step 4;
    /// Solomon, 2026-10-22: Harmonise puts its result on the chord track, with an option to write a chord part), or with
    /// <paramref name="asPart"/> become a plain Chords part playing them. A reharm's go where the chords were.
    /// </summary>
    internal void ApplyIdea(Reharm idea, bool asPart = false)
    {
        if (RangeOf(idea) is not { } range) return;
        Remember();
        if (PutIdea(idea, range, asPart)) ShowSection();   // a new chord part's track to show
        Changed();
        Message = KeptText(idea, range, asPart);
    }

    /// <summary>
    /// An idea's chords put in from <paramref name="range"/>'s start to its end: on the chord track, so every part follows
    /// them as it's set to, or with <paramref name="asPart"/> played plainly by a Chords part.
    /// </summary>
    /// <returns>Whether it made a new track.</returns>
    bool PutIdea(Reharm idea, (long From, long To) range, bool asPart)
    {
        if (asPart) return Rechord(Section, idea.Lane, range.From, range.To);
        if (idea.Whole)
            // The chords were made for these parts as they are, so they don't follow them (each can be told to).
            foreach (var clip in PlaygroundSession.Audible(Section).Where(l => l.Role != MidiRoles.Drums)
                                                  .SelectMany(l => l.Clips).Where(c => c.Start < range.To && c.End > range.From && c.Follow == FollowWay.Auto))
                clip.Follow = FollowWay.None;
        SetChords(idea.Lane, range.From, range.To);
        return false;
    }

    /// <summary>What the message line says once an idea is kept.</summary>
    string KeptText(Reharm idea, (long From, long To)? range, bool asPart)
    {
        if (asPart) return $"{idea.Title}: {idea.After}, played by a Chords part under {idea.Before}. Undo takes it back.";
        if (idea.Whole)
            return $"{idea.Title}: {idea.After}, on the chord track under {idea.Before}, which stays as it is. Parts dropped in follow the chords; drag a chord's edges to stretch it. Undo takes it back.";
        // Parts changed by hand don't follow (chord parts are re-voiced where the chords changed): they're named, so it's no surprise.
        var staying = range is { } r
            ? Section.Layers.Where(l => l.Written.Length == 0 && l.Role != MidiRoles.Drums)
                     .Where(l => l.Clips.Any(c => c.Pinned && c.Start < r.To && c.End > r.From && c.Role != MidiRoles.Drums &&
                                                  (c.Role == MidiRoles.Other ? l.Role : c.Role) != MidiRoles.Chords))
                     .Select(l => l.Name).Distinct().ToList()
            : [];
        var stays = staying.Count == 0 ? ""
            : $" {string.Join(" and ", staying)} {(staying.Count == 1 ? "was" : "were")} changed by hand, so {(staying.Count == 1 ? "it stays as it is" : "they stay as they are")}: pick {(staying.Count == 1 ? "it" : "each")} and press Follow again to make {(staying.Count == 1 ? "it" : "them")} follow.";
        return $"Kept {idea.Title}: {idea.Before} → {idea.After}, on the chord track: the parts follow it.{stays} Undo takes it back.";
    }

    /// <summary>A plain chord part playing <paramref name="chart"/> ("Dm7 | G7"), from the highlight or bar 1: chords to start from.</summary>
    internal void PutChords(string chart)
    {
        if (ChordLane.ParseChart(chart, out var why) is not { Count: > 0 } lane) { Message = why; return; }
        Remember();
        var length = (lane[^1].End + Bar - 1) / Bar * Bar;
        var start = Loop?.Start ?? 0;
        if (IsEmpty && Loop is null) SetBars((int)(length / Bar));
        AddLayer("Chords", MidiRoles.Chords, Voicer.Plain(lane), start: start, length: length);
        Changed();
    }

    /// <summary>Where <paramref name="after"/> differs from <paramref name="before"/>, in ticks.</summary>
    static (long From, long To)? Changes(IReadOnlyList<TimedChord> before, IReadOnlyList<TimedChord> after)
    {
        long from = long.MaxValue, to = 0;
        foreach (var c in after)
            if (!before.Any(b => b.Start == c.Start && b.Length == c.Length && b.Chord.Name == c.Chord.Name))
            {
                from = Math.Min(from, c.Start);
                to = Math.Max(to, c.End);
            }
        return to > from ? (from, to) : null;
    }

    /// <summary>
    /// The chord goes on the end as a new bar, on the chord track: the chord parts that reach the end go round into it and
    /// follow it, with as many notes and the rhythm they had.
    /// </summary>
    internal void AddNextChord(ChordSymbol chord)
    {
        if (HasChordTrack) { AddToChordTrack(chord); return; }
        if (Section.Bars >= MaxBars) { Message = $"The timeline is full ({MaxBars} bars)."; return; }
        Remember();
        var end = Section.Length;
        SetBars(Section.Bars + 1);
        // Chord clips reaching the end go round into the new bar, so it's played with their rhythm.
        foreach (var l in Section.Layers.Where(l => l.Written.Length == 0))
            foreach (var c in l.Clips.Where(c => c.End == end && (c.Role == MidiRoles.Chords || c.Role == MidiRoles.Other && l.Role == MidiRoles.Chords)))
            {
                c.Length += Bar;
                l.Render();
            }
        SetChords([new TimedChord(end, Bar, chord)], end, end + Bar);
        Changed();
        Message = $"{chord.Name} added as bar {Section.Bars}, on the chord track: the chord parts go round into it and follow it. Undo takes it back.";
    }

    /// <summary>
    /// New chords from <paramref name="from"/> to <paramref name="to"/>, played by the chord clips there (chords are heard,
    /// never typed: Solomon, 2026-10-19). Each one's notes in those bars are voiced again on the new chords, struck with
    /// the rhythm they had; a clip changed this way plays its notes as they are from
    /// then on. Muted chord clips (and those not soloed) change too, so they never play the old chords over the new ones
    /// once they're heard again. With no chord clip there that isn't muted, a plain chord clip is put over those bars.
    /// </summary>
    /// <returns>Whether it made a new chord track.</returns>
    static bool Rechord(PlaygroundSection timeline, IReadOnlyList<TimedChord> lane, long from, long to)
    {
        var clips = timeline.Layers.Where(l => l.Written.Length == 0)
            .SelectMany(l => l.Clips.Where(c => (c.Role == MidiRoles.Chords || c.Role == MidiRoles.Other && l.Role == MidiRoles.Chords) && c.Start < to && c.End > from)
                                    .Select(c => (Layer: l, Clip: c))).ToList();
        foreach (var (layer, c) in clips)
        {
            var local = c.Render().Select(n => n with { Start = n.Start - c.Start }).ToList();
            long a = Math.Max(from, c.Start) - c.Start, b = Math.Min(to, c.End) - c.Start;
            var old = local.Where(n => n.Start >= a && n.Start < b).Select(n => n with { Start = n.Start - a, Length = Math.Min(n.Length, b - n.Start) }).ToList();
            var under = LaneAt(lane, c.Start + a, c.Start + b);
            // Root at the bottom, so the chords are heard as the chords meant (and not as slash chords).
            var voiced = Voicer.Plain(under);
            var struck = old.Count > 2 ? Groove.Copy(voiced, old, GrooveDepth.Rhythm, b - a, under, 1) : voiced;
            if (struck.Count == 0) struck = voiced;
            c.Notes = local.Where(n => n.Start < a || n.Start >= b).Concat(struck.Select(n => n with { Start = n.Start + a })).ToList();
            c.Loop = c.Length;
            c.Offset = 0;
            c.Transpose = 0;
            c.Inversion = 0;   // Render already turned them
            c.Fits = [];
            c.Under = [];
            c.Pinned = true;
            layer.Render();
        }
        if (clips.Any(p => !p.Layer.Muted)) return false;
        // A Chords track that isn't muted takes the plain chords (over anything it had there, as in a DAW), else a new one.
        // Its name is matched as UniqueName names it ("chords", or "Chords 2" beside a muted one), so one is made only once.
        var notes = Voicer.Plain(LaneAt(lane, from, to));
        var track = timeline.Layers.FirstOrDefault(l => PlainChordsName(l.Name) && l.Written.Length == 0 && !l.Muted);
        var added = track is null;
        if (track is null)
        {
            timeline.Layers.Add(track = new PlaygroundLayer { Name = UniqueName(timeline, "Chords"), Role = MidiRoles.Chords, Program = GmSounds.DefaultFor(MidiRoles.Chords) });
        }
        CutOut(track, from, to);
        track.Clips.Add(PlaygroundClip.Of("Chords", MidiRoles.Chords, notes, to - from, from, to - from));
        track.Render();
        return added;
    }

    /// <summary>"Chords", or "Chords" with a number after it, told apart as their dragged files are (as in UniqueName).</summary>
    static bool PlainChordsName(string name)
    {
        var file = TextScan.SafeFileName(name);
        return file.Equals("Chords", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("Chords ", StringComparison.OrdinalIgnoreCase) && file.Length > 7 && file[7..].All(char.IsAsciiDigit);
    }

    // ---- suggestions from the library ----

    public IReadOnlyList<RoleChip> RoleChips { get; }

    bool _wasEmpty = true;

    /// <summary>The first side tab's name: Browse while the idea is empty and it's the MIDI list (Solomon, 2026-10-21:
    /// "shouldn't say suggest"), Suggest once there's something to fit to.</summary>
    public string SuggestTab => IsEmpty ? "Browse" : "Suggest";

    /// <summary>The first part on the timeline: Suggest takes over from the MIDI page's list, so it lists parts that fit.</summary>
    void EmptyChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SuggestTab));
        if (IsEmpty == _wasEmpty || _main.Playground != this) return;   // not while being made
        _wasEmpty = IsEmpty;
        if (!IsEmpty) _ = FindMatchesAsync();
        else Midi.Opened();   // the list shows again: it gets the parts if it hasn't yet (the page opened with an idea on it)
    }

    /// <summary>A copy of the MIDI page's list (its own search and kind), which an empty idea shows instead of Suggest (Solomon, 2026-10-21: "should just be a
    /// smaller version of the main midi list on the midi page so any improvements there can be carried over").</summary>
    public MidiBrowserViewModel Midi
    {
        get
        {
            if (_midiList is not null) return _midiList;
            _midiList = new MidiBrowserViewModel(_main.Midi);
            _midiList.StartedPlaying += () => { if (_playing) StopPlaying(); };   // one sound at a time
            // Where the parts come from (your projects, your MIDI folders, or both) goes for Suggest too.
            _midiList.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MidiBrowserViewModel.From) && !IsEmpty) _ = FindMatchesAsync(); };
            return _midiList;
        }
    }
    MidiBrowserViewModel? _midiList;

    /// <summary>
    /// The list's part stops when the idea plays, a part is added, or the page is left (Solomon, 2026-10-21: "the clip
    /// that you were playing in the browser continues playing").
    /// </summary>
    void StopBrowsing() => _midiList?.StopForQuick();

    /// <summary>A part from the MIDI page's list onto the timeline, as it plays there (moved, octave and all).</summary>
    [RelayCommand]
    void AddMidiRow(MidiRow? row)
    {
        if (row is null) return;
        if (row.Channel.Notes.Count == 0)
        {
            Message = $"{row.Title} has no notes to add.";
            return;
        }
        StopBrowsing();
        AddFromLibrary(row.Entry, row.Channel.Notes, row.Title);
    }
    [ObservableProperty] RoleChip _suggestRole;
    [ObservableProperty] string _suggestSearch = "";
    public ObservableCollection<PlaygroundMatchRow> Matches { get; } = [];
    [ObservableProperty] bool _isMatching;
    [ObservableProperty] string _matchStatus = "Pick a kind of part and press Find to get parts from your library that fit this section.";
    CancellationTokenSource? _matchCts;
    /// <summary>How many suggestions the list holds now: the Settings number, grown by Show more.</summary>
    int _shown;
    /// <summary>The most Show more goes to, so ranking stays quick.</summary>
    public const int MaxShown = 1000;
    /// <summary>Whether more parts could fit than the list shows, so Show more has something to add.</summary>
    [ObservableProperty] bool _canShowMore;

    partial void OnSuggestRoleChanged(RoleChip value) => _ = FindMatchesAsync();

    [RelayCommand]
    Task FindMatches() => FindMatchesAsync();

    /// <summary>The next lot of suggestions under the ones showing, best first as before.</summary>
    [RelayCommand]
    Task ShowMore() => FindMatchesAsync(more: true);

    /// <summary>The parts scored for the list shown, so Show more only scores the ones it adds.</summary>
    ConcurrentDictionary<MidiEntry, PlaygroundMatch?> _scored = new(ReferenceEqualityComparer.Instance);

    public async Task FindMatchesAsync(bool more = false)
    {
        if (!more) _scored = new(ReferenceEqualityComparer.Instance);
        var scored = _scored;
        _shown = more ? Math.Min(MaxShown, Math.Max(_shown, SuggestCount) + SuggestCount) : SuggestCount;
        var limit = _shown;
        _matchCts?.Cancel();
        if (IsEmpty)
        {
            // nothing to fit to: the page shows the MIDI page's list instead
            Matches.Clear();
            CanShowMore = false;
            IsMatching = false;
            return;
        }
        var cts = _matchCts = new CancellationTokenSource();
        IsMatching = true;
        MatchStatus = "Looking through your MIDI…";
        try
        {
            if (await _main.Midi.CatalogueAsync() is not { } catalogue)
            {
                MatchStatus = "Your MIDI hasn't been read yet: open the MIDI page once to read it.";
                return;
            }
            var harmony = MatchHarmony;
            var role = SuggestRole.Role;
            var text = SuggestSearch.Trim();
            // nothing highlighted or picked: the whole timeline, whatever its labels' feels
            var feel = Loop is null && SelectedLabel is null ? SectionFeel.Any : FeelHere;
            var matching = 0;
            var from = Midi.FromQuery;
            var around = SwapTarget is { } swapping && SwapRange is { } swapped ? AroundOf(swapping, swapped) : null;
            // Swapping: the highlight may still be moving, and each move starts again, so wait for it to settle.
            if (around is not null) await Task.Delay(150, cts.Token);
            var found = await UiWork.Run(() => SectionMatcher.Rank(catalogue, harmony, role, _main.Midi.NotesOf, out matching, text, limit, cts.Token, feel, around, from, scored));
            if (cts.IsCancellationRequested) return;
            Matches.Clear();
            foreach (var m in found) Matches.Add(new PlaygroundMatchRow(this, m));
            CanShowMore = found.Count >= limit && matching > found.Count && limit < MaxShown;
            // The list is the best few of everything of that kind, so say how many there were to pick from.
            var count = matching > found.Count ? $"{found.Count} of {matching:N0}" : $"{found.Count}";
            var left = harmony.Songs.Count == 0 ? "" : $" Left out: {string.Join(", ", harmony.Songs.Select(s => Path.GetFileName(s)))}, already in the idea.";
            var target = SwapTarget;
            MatchStatus = found.Count == 0 ? "Nothing found. Try another kind of part, or fewer words."
                : target is not null && SwapRange is { } bars
                    ? $"{count} {MidiRoles.Plural(role).ToLowerInvariant()} for bars {bars.Start / Bar + 1}–{(bars.End + Bar - 1) / Bar} of {target.Clip.Name}, best first: the bit of each that fits there and joins on best. Swap in puts one there instead." + left
                : (harmony.IsEmpty ? $"{count} {MidiRoles.Plural(role).ToLowerInvariant()}, newest first: add a part or chords to rank them by fit."
                                   : $"{count} {MidiRoles.Plural(role).ToLowerInvariant()} that fit {(Loop is null ? "the whole idea" : LoopText.ToLowerInvariant())}, best first, moved into its key.") + left;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_matchCts == cts) IsMatching = false;
        }
    }

    internal void AddMatch(PlaygroundMatchRow row)
    {
        if (SwapTarget is { } target && SwapRange is { } bars) SwapIn(target, bars, row.Match.Entry, row.Match.Notes, row.Match.Period);
        else AddFromLibrary(row.Match.Entry, row.Match.Notes, period: row.Match.Period);
    }

    // ---- a stretch of one of your songs ----

    [ObservableProperty] string _songSearch = "";
    [ObservableProperty] IReadOnlyList<ProjectItem> _songChoices = [];
    [ObservableProperty] bool _songChoicesOpen;
    [ObservableProperty] int _fromBar = 1;

    partial void OnSongSearchChanged(string value)
    {
        var words = value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        SongChoices = words.Length == 0 ? [] : _main.Projects.Items
            .Where(p => words.All(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase)) && Readers.For(p.Path) is not null)
            .Take(30).ToList();
        SongChoicesOpen = SongChoices.Count > 0;
    }

    /// <summary>
    /// Brings in every pitched and drum part of a song from a bar, over the highlight (or the whole timeline), each as a
    /// track of its own: a stretch of a song to build on. Its other parts are then never suggested.
    /// </summary>
    [RelayCommand]
    Task BringSong(ProjectItem? project) => project is null ? Task.CompletedTask : BringSongAsync(project.Path, project.Name, chordsOnly: SongChordsOnly);

    /// <param name="chordsOnly">Only the song's chords, onto the chord track (the Song tab's "Chords only"), not its parts.</param>
    async Task<bool> BringSongAsync(string path, string songName, IReadOnlyCollection<string>? leaveOut = null, bool chordsOnly = false)
    {
        if (Readers.For(path) is not { } reader) return false;
        SongChoicesOpen = false;
        Message = $"Reading {songName}…";
        try
        {
            var from = (long)Math.Max(0, FromBar - 1) * Bar;
            var (start, length) = Place;
            // Read and worked out off the screen's thread, so a big song doesn't freeze the window.
            // Where the parts play in the arranger (as MIDI that fits reads them), so they line up with each other.
            var parts = await UiWork.Run(() => CutParts(PhraseJoiner.ArrangedChannels(path) ?? reader.ExtractChannels(path), from, length, leaveOut));
            if (parts.Count == 0)
            {
                Message = $"{songName} has no notes from bar {FromBar} for {length / Bar} bars.";
                return false;
            }
            if (chordsOnly)
            {
                PutSongChords(parts, start, length, songName);
                return true;
            }
            Remember();
            foreach (var (name, role, notes) in parts) AddLayer(name, role, notes, path, start: start, length: length);
            Changed();
            Message = $"Brought in {parts.Count} parts of {songName}, its bars {FromBar}–{FromBar + length / Bar - 1}. Its parts won't be suggested now.";
            SongSearch = "";
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Message = $"Couldn't read {songName}: {e.Message}";
            return false;
        }
    }

    /// <summary>
    /// Each part's notes from <paramref name="from"/> for <paramref name="length"/> ticks, with what it plays as. A part
    /// that loops (<paramref name="period"/> above 0) plays round again to fill the length.
    /// </summary>
    static List<(string Name, string Role, List<NoteEvent> Notes)> CutParts(IEnumerable<MidiChannel> channels, long from, long length,
                                                                          IReadOnlyCollection<string>? leaveOut = null, long period = 0)
    {
        var found = new List<(string Name, string Role, List<NoteEvent> Notes)>();
        foreach (var c in channels)
        {
            if (leaveOut is not null && leaveOut.Contains(c.Name)) continue;   // a part switched off in "Fit to"
            var all = PhraseAnalyser.Normalise(c.Notes, c.Ppq, keepPlace: true);
            if (period > 0) all = SectionMatcher.Loop(all, period, from + length);
            // Notes held into the first bar (a pushed chord, a tie) come in too, cut to start there.
            var notes = MidiBrowserViewModel.Cut(all, from, from + length);
            if (notes.Count == 0) continue;
            var role = c.Kind == "Drums" ? MidiRoles.Drums : PhraseAnalyser.Analyse(notes, c.Name).Role;
            found.Add((c.Name, role, notes));
        }
        return found;
    }

    // ---- the cut-down playground on a song's "MIDI that fits" tab ----

    /// <summary>
    /// A small playground of its own beside a song's "MIDI that fits" list (Solomon, 2026-10-19: "work with these files
    /// in a cut down playground"): the song's parts you pick and the part you're trying, layered, without touching the
    /// idea on the Playground page until you press Open in Playground.
    /// </summary>
    public bool IsQuick { get; }

    /// <summary>A cut-down playground of its own, kept apart from this one's idea.</summary>
    public PlaygroundViewModel Quick() =>
        new(_db, _main, new PlaygroundStore(Path.Combine(_main.DataFolder, "playground-quick")), quick: true);

    /// <summary>Starts again with the song's bars where the part fits (less the song's parts named in <paramref name="leaveOut"/>)
    /// and the part on top.</summary>
    public async Task LoadSongAsync(string path, string songName, MidiEntry? entry, IReadOnlyList<NoteEvent>? notes, string? title,
                                    long at = 0, IReadOnlyCollection<string>? leaveOut = null)
    {
        StartAgain(songName, notes is { Count: > 0 } ? PhraseAnalyser.BarsOf(notes) : NewBars, at);
        await BringSongAsync(path, songName, leaveOut);
        if (entry is not null && notes is { Count: > 0 }) AddFromLibrary(entry, notes, title);
        SelectedLayer = Layers.LastOrDefault();
        Message = "";
    }

    /// <summary>
    /// Starts again with the parts a part is fitted to on the MIDI page and the part on top (Solomon, 2026-10-19: "i want
    /// this and the playground to be linked so when features are added to the playground the midi version gets those too").
    /// The section is as long as the longer of the two, so a part longer than what it's fitted to plays all of it; what
    /// it's fitted to plays round again to fill it when it loops.
    /// </summary>
    /// <param name="partners">What it's fitted to: a song's parts where they play, or one part from its first bar.</param>
    /// <param name="period">How long what it's fitted to is when it loops (0 when it's a song, which doesn't).</param>
    /// <param name="at">Where in what it's fitted to the part lines up.</param>
    /// <param name="tempo">The song's tempo, or the MIDI page's tempo slider: what the pair is heard at.</param>
    /// <param name="off">The song parts switched off in "Fit to", muted from the start.</param>
    public async Task LoadPairAsync(string name, IReadOnlyList<MidiChannel> partners, long period, string? path, long at,
                                    MidiEntry entry, IReadOnlyList<NoteEvent> notes, string? title, double? tempo = null,
                                    IReadOnlyCollection<string>? off = null)
    {
        var bars = Math.Max(1, PhraseAnalyser.BarsOf(notes));
        // A looping partner and the part go round together until they line up again, as the matcher heard them (up to 64 bars).
        if (period > 0) bars = LineUp(bars, Math.Max(1, (int)Math.Ceiling(period / (double)Bar)));
        var load = ++_loads;
        StartAgain(name, bars, period > 0 ? 0 : at, MaxQuickBars);
        if (tempo is { } t) { _session.Tempo = Math.Clamp(Math.Round(t), 40, 240); _showing = true; Tempo = _session.Tempo; _showing = false; }
        var from = (long)(FromBar - 1) * Bar;
        var length = Section.Length;
        var parts = await UiWork.Run(() => CutParts(partners, from, length, period: period));
        if (load != _loads) return;   // another part picked meanwhile
        foreach (var (part, role, partNotes) in parts)
        {
            var layer = AddLayer(part, role, partNotes, path ?? "", start: 0, length: length);
            layer.SongPart = part;
            layer.Muted = off?.Contains(part) == true;
        }
        _songOff = off?.ToHashSet();
        // The part as it is in the list, not turned into smooth inversions: what you hear is what you'd drag.
        AddFromLibrary(entry, notes, title, smooth: false);
        SelectedLayer = Layers.LastOrDefault();
        foreach (var r in Layers) r.Changed();
        Changed(harmony: true);
        _undo.Clear();
        CanUndo = false;
        Message = "";
    }

    const int MaxQuickBars = 64;

    static int LineUp(int a, int b)
    {
        static int Gcd(int x, int y) => y == 0 ? x : Gcd(y, x % y);
        var lcm = (long)a / Gcd(a, b) * b;
        return lcm <= MaxQuickBars ? (int)lcm : Math.Max(a, b);
    }

    int _loads;

    // A new, empty idea named after the song or part, its section as long as the part (within Settings' longest), from the
    // bar the part lines up at. Undo starts afresh.
    void StartAgain(string name, int bars, long at, int? most = null)
    {
        StopPlaying();
        _undo.Clear();
        _redo.Clear();
        CanUndo = CanRedo = false;
        _tryBefore = null;
        TryingIdea = null;
        ProposedChords = null;
        _session = Fresh();
        _session.Name = $"{name} sketch";
        Section.Bars = Math.Clamp(bars, 1, most ?? GrowBars);
        Loop = null;
        SelectedClip = null;
        Show();
        FromBar = (int)(Math.Max(0, at) / Bar) + 1;
    }

    /// <summary>The cut-down playground's parts, taken to the Playground page to work up further.</summary>
    [RelayCommand]
    void OpenInMain()
    {
        if (Section.Layers.Count == 0) return;
        StopPlaying();
        var copy = PlaygroundSession.FromJson(_session.ToJson())!;
        _main.Playground.TakeSketch(copy.Timeline, _session.Name.EndsWith(" sketch") ? _session.Name[..^7] : _session.Name);
    }

    /// <summary>
    /// A cut-down playground's parts: from bar 1 when the timeline is empty, else after its last bar, with a label named
    /// <paramref name="name"/> over them and highlighted. Tracks of the same name are joined. Then the page is shown.
    /// </summary>
    internal void TakeSketch(PlaygroundSection sketch, string name)
    {
        StopPlaying();
        var at = IsEmpty ? 0 : Section.Length;
        if (at + sketch.Length > (long)MaxBars * Bar)
        {
            TimelineFull(name, sketch.Bars);
            return;
        }
        Remember();
        if (IsEmpty) Section.Bars = sketch.Bars;
        Reach(at + sketch.Length);
        foreach (var t in sketch.Layers)
        {
            var clips = t.Clips.Select(x => { var c = x.Copy(); c.Start += at; return c; }).ToList();
            var track = Section.Layers.FirstOrDefault(l => l.Name == t.Name && l.Written == t.Written);
            if (track is null)
            {
                track = t.Copy();
                track.Clips = clips;
                Section.Layers.Add(track);
            }
            else track.Clips.AddRange(clips);
            track.Render();
        }
        if (at > 0 || Section.Labels.Count > 0)
            Section.Labels.Add(new PlaygroundLabel { Name = name, Start = at, Length = sketch.Length });
        Show();
        Loop = new RollRange(at, at + sketch.Length);
        Changed(harmony: true);
        Message = $"{name} came from MIDI that fits{(at > 0 ? $", after bar {at / Bar}" : "")}. Undo takes it back out.";
        if (_main.Nav.FirstOrDefault(n => n.Page == this) is { } page) _main.SelectedNav = page;
    }

    /// <summary>
    /// From a song's "MIDI that fits" tab (Solomon, 2026-10-19: "Open in Playground" to work an idea up): the song's bars
    /// where the part fits brought in to build on and the part added on top, from bar 1 when the timeline is empty or
    /// else after its last bar under a label named after the song, highlighted; then the Playground page is shown.
    /// </summary>
    public async Task OpenFromSongAsync(string path, string songName, MidiEntry? entry = null, IReadOnlyList<NoteEvent>? notes = null,
                                        string? title = null, long at = 0, IReadOnlyCollection<string>? leaveOut = null)
    {
        StopPlaying();
        var bars = notes is { Count: > 0 } ? Math.Clamp(PhraseAnalyser.BarsOf(notes), 1, GrowBars) : NewBars;
        if (!IsEmpty && Section.Bars + bars > MaxBars)
        {
            TimelineFull(songName, bars);
            return;
        }
        if (!IsEmpty)
        {
            Remember();
            var start = Section.Length;
            SetBars(Section.Bars + bars);
            Section.Labels.Add(new PlaygroundLabel { Name = songName, Start = start, Length = (long)bars * Bar });
            ShowSection();
            Loop = new RollRange(start, start + (long)bars * Bar);
            Changed(harmony: false);
        }
        else
        {
            Loop = null;
            SetBars(bars);
        }
        FromBar = (int)(Math.Max(0, at) / Bar) + 1;
        if (_main.Nav.FirstOrDefault(n => n.Page == this) is { } page) _main.SelectedNav = page;
        var brought = await BringSongAsync(path, songName, leaveOut);
        if (entry is not null && notes is { Count: > 0 }) AddFromLibrary(entry, notes, title);
        Message = entry is null
            ? Message
            : $"{title ?? entry.Title} on {(brought ? $"{songName}, its bars {FromBar}–{FromBar + bars - 1}" : "its own")}{(Loop is { } r ? $", at bar {r.Start / Bar + 1}" : "")}. Mute the song's parts to hear it alone; Undo takes it out.";
    }

    /// <summary>
    /// Bars brought in from another page don't fit after the last bar (the timeline is at most <see cref="MaxBars"/>): the
    /// page says so, and nothing is added.
    /// </summary>
    void TimelineFull(string name, int bars)
    {
        Message = $"There's no room for {name} ({bars} bar{(bars == 1 ? "" : "s")}): the timeline can't go past {MaxBars} bars. " +
                  "Make the timeline shorter, or start a new idea, then try again.";
        if (_main.Nav.FirstOrDefault(n => n.Page == this) is { } page) _main.SelectedNav = page;
    }

    // ---- loop points ----

    /// <summary>
    /// The highlight: bars dragged out on the timeline (or along the small playground's piano roll). Playing goes round
    /// them, suggestions fit just them, and new parts go there.
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasLoop), nameof(LoopText))] RollRange? _loop;
    public bool HasLoop => Loop is not null;
    partial void OnPositionChanged(double value) => SelectedClip?.PlayheadAt(value);
    partial void OnLoopChanged(RollRange? value)
    {
        Revision++;
        SelectedClip?.HighlightMoved();
    }
    public string LoopText => Loop is { } r ? $"Bars {r.Start / Bar + 1}–{(r.End + Bar - 1) / Bar}" : "";

    /// <summary>Bars highlighted on the timeline (or along the small playground's piano roll), or let go of: Delete is for the clip now.</summary>
    [RelayCommand]
    void SetLoop(RollRange? range)
    {
        _chordLaneLast = false;
        LoopOver(range);
    }

    /// <summary>The highlight put over <paramref name="range"/> (kept inside the timeline), or taken off with null.</summary>
    void LoopOver(RollRange? range)
    {
        Loop = range is { } r && Math.Clamp(r.End, 1, Length) > Math.Clamp(r.Start, 0, Length)
            ? new RollRange(Math.Clamp(r.Start, 0, Length), Math.Clamp(r.End, 1, Length)) : null;
        if (_playing) _ = PlayAsync();
        PlayInBitwigSoon();
        SuggestFollow(true);
    }

    [RelayCommand]
    void ClearLoop() => LoopOver(null);

    /// <summary>The harmony suggestions are fitted to: the loop's bars when there are loop points, else the section.</summary>
    /// <summary>
    /// Swapping bars of a clip, the bars it covers, heard without its track (it's what's being replaced).
    /// </summary>
    SectionHarmony MatchHarmony => SwapTarget is { } t && SwapRange is { } bars
        ? SectionHarmony.Of(Section, _session.Key, _session.Style, [t.Track.Layer]).Slice(bars.Start, bars.End)
        : Loop is { } r ? Harmony.Slice(r.Start, r.End) : Harmony;

    // ---- listening ----

    [ObservableProperty] bool _isPlaying;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PositionText))] double _position = -1;
    /// <summary>
    /// The Loop button (Solomon, 2026-10-19): on, Play goes round the highlight (or the whole idea); off, it plays from
    /// where it starts to the end of the highlight (or the idea) and stops.
    /// </summary>
    [ObservableProperty] bool _loopOn = true;
    /// <summary>Where Play starts, set by clicking the bar numbers (inside the highlight, it starts there and goes round the highlight).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PositionText))] long _cursor;
    partial void OnCursorChanged(long value) { if (_playing) _ = PlayAsync(); }

    [RelayCommand]
    void SetCursor(long tick)
    {
        _chordLaneLast = false;   // the bar numbers clicked: Delete is for the clip again
        Cursor = Math.Clamp(tick, 0, Math.Max(0, Length - 1));
    }

    [RelayCommand]
    void ToStart() => Cursor = 0;

    /// <summary>Where it's playing, or where Play starts, as a DAW shows it: bar.beat.sixteenth ("3.2.1").</summary>
    public string PositionText
    {
        get
        {
            var t = Position >= 0 ? (long)Position : Cursor;
            int ppq = PhraseAnalyser.Ppq, bar = 4 * ppq;
            return $"{t / bar + 1}.{t % bar / ppq + 1}.{t % ppq / (ppq / 4) + 1}";
        }
    }

    // ---- zoom ----

    /// <summary>The timeline's view when zoomed in (Ctrl + scroll): its first tick and how many it shows (0: the whole idea).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ViewFrom), nameof(ViewTo))] long _viewStart;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ViewFrom), nameof(ViewTo))] long _viewLength;
    long ViewSpan => ViewLength <= 0 || ViewLength >= Length ? Math.Max(1, Length) : ViewLength;
    /// <summary>The view as shares of the whole idea, for the chord lane and energy line above the timeline.</summary>
    public double ViewFrom => Length <= 0 || ViewLength <= 0 || ViewLength >= Length ? 0 : Math.Clamp(ViewStart, 0, Length - ViewSpan) / (double)Length;
    public double ViewTo => Length <= 0 || ViewLength <= 0 || ViewLength >= Length ? 1 : ViewFrom + ViewSpan / (double)Length;

    void Zoom(double by)
    {
        var span = (long)Math.Round(ViewSpan * by);
        if (span >= Length) { ViewLength = 0; ViewStart = 0; return; }
        span = Math.Max(Bar, span);   // the timeline never shows less than a bar, so the chord lane stays lined up with it
        // round the play marker, as Ctrl + scroll zooms round the pointer
        var around = Math.Clamp(Cursor, 0, Length);
        var share = ViewLength <= 0 ? around / (double)Math.Max(1, Length) : (around - ViewStart) / (double)ViewSpan;
        share = Math.Clamp(share, 0, 1);
        ViewStart = Math.Clamp(around - (long)(share * span), 0, Math.Max(0, Length - span));
        ViewLength = span;
    }

    /// <summary>Bitwig's "zoom to fit": the highlight fills the timeline, or when it already does (or there's none), the whole idea.</summary>
    void ZoomToFit()
    {
        if (Loop is { } r && (ViewStart != r.Start || ViewLength != r.Length) && r.Length < Length)
        {
            ViewStart = r.Start;
            ViewLength = r.Length;
        }
        else { ViewStart = 0; ViewLength = 0; }
    }

    // ---- keyboard shortcuts ----

    /// <summary>The app's shortcut list, whose Playground keys this page answers (Settings › Shortcuts changes them).</summary>
    public ShortcutsViewModel Shortcuts => _main.Shortcuts;

    /// <summary>Whether the window's keys sheet is open on this page (? or Help › Keyboard shortcuts opens it).</summary>
    public bool CheatSheetOpen => Shortcuts.SheetOpen && Shortcuts.SheetPage == ShortcutsViewModel.Playground;

    /// <summary>Does what a Playground shortcut is for; false when there was nothing for it to do (so the key goes on to the box or list it was pressed in).</summary>
    public bool RunShortcut(string id)
    {
        var clip = SelectedClip;
        switch (id)
        {
            case "playground.play": PlayCommand.Execute(null); return true;
            case "playground.loop": LoopOn = !LoopOn; Message = LoopOn ? "Loop on" : "Loop off"; return true;
            case "playground.undo": Undo(); return true;
            case "playground.redo": Redo(); return true;
            case "playground.save": SaveIdea(); return true;
            case "playground.help": Shortcuts.ToggleSheet(ShortcutsViewModel.Playground); return true;
            case "playground.zoomIn": Zoom(0.5); return true;
            case "playground.zoomOut": Zoom(2); return true;
            case "playground.zoomFit": ZoomToFit(); return true;
        }
        // After clicking the chord lane, Delete is for the chord picked there, never the clip picked before.
        if (id == "playground.delete" && _chordLaneLast) { RemoveChord(); return true; }
        if (RunMenuAction(id, clip)) return true;
        if (clip is null) return false;
        switch (id)
        {
            case "playground.duplicate": DuplicateClip(clip); break;
            case "playground.split": SplitClip(clip); break;
            case "playground.delete": RemoveClip(clip); break;
            case "playground.nudgeLeft": Nudge(clip, -Grid); break;
            case "playground.nudgeRight": Nudge(clip, Grid); break;
            case "playground.nudgeBarLeft": Nudge(clip, -Bar); break;
            case "playground.nudgeBarRight": Nudge(clip, Bar); break;
            case "playground.up": MoveClip(clip, 1); break;
            case "playground.down": MoveClip(clip, -1); break;
            case "playground.octaveUp": MoveClip(clip, 12); break;
            case "playground.octaveDown": MoveClip(clip, -12); break;
            case "playground.fitAgain": if (clip.CanFitAgain) FitAgain(clip); break;
            default: return false;
        }
        return true;
    }

    /// <summary>The picked clip slid along its track, as dragging it does (so it fits the chords where it lands).</summary>
    void Nudge(ClipRow row, long by)
    {
        if (row.Clip.Start + by < 0) by = -row.Clip.Start;
        if (by == 0) return;
        ChangeClip(new ClipEdit(row.Track.Layer, row.Clip, Layers.IndexOf(row.Track), row.Clip.Start + by, row.Clip.Length, row.Clip.Offset));
    }

    /// <summary>
    /// Bitwig's Split: the clip cut in two at the play marker, or at the highlight's edges when the highlight is over it,
    /// each bit playing just as it did.
    /// </summary>
    internal void SplitClip(ClipRow row)
    {
        var clip = row.Clip;
        var cuts = (Loop is { } r && r.Start < clip.End && r.End > clip.Start ? new[] { r.Start, r.End } : [Cursor])
            .Where(t => t > clip.Start && t < clip.End).Distinct().Order().ToList();
        if (cuts.Count == 0)
        {
            Message = "Put the play marker (click the bar numbers) or the highlight inside the clip to split it there.";
            return;
        }
        Remember();
        var layer = row.Track.Layer;
        var at = layer.Clips.IndexOf(clip);
        layer.Clips.RemoveAt(at);
        var edges = new List<long> { clip.Start };
        edges.AddRange(cuts);
        edges.Add(clip.End);
        var pieces = edges.Zip(edges.Skip(1), (a, b) => Piece(clip, a, b)).ToList();
        layer.Clips.InsertRange(at, pieces);
        layer.Render();
        row.Track.Changed();
        SelectedClip = new ClipRow(this, row.Track, pieces[1]);
        Changed(harmony: false);
        Message = $"{clip.Name} split in {pieces.Count}.";
    }

    bool _playing;
    IAudioPlayer? _player;
    IUiTimer? _clock;
    int _request;
    long _playLength;
    string _playKey = "";
    float[]? _audio;
    double _playTempo;

    [RelayCommand]
    async Task Play()
    {
        // While it plays because Bitwig does, Stop stops following too: otherwise the next look at Bitwig starts it again.
        if (_playing && _follow.Following) { FollowBitwig = false; return; }
        if (_playing) { StopPlaying(); return; }
        if (IsQuick) _main.Midi.StopForQuick();   // a small playground and the MIDI page never play over each other
        await PlayAsync();
    }

    /// <summary>The section (or the arrangement) going round, every part that plays, with its sound.</summary>
    /// <summary>Plays the idea (or <paramref name="instead"/>); true when this is what's playing now, false when a newer
    /// play or a stop came while the sound was being made, so a row only shows Stop for what you hear.</summary>
    async Task<bool> PlayAsync(bool keepPlace = false, IReadOnlyList<MidiChannel>? instead = null, long? startAt = null)
    {
        StopBrowsing();
        var request = ++_request;
        List<MidiChannel> channels;
        if (instead is null) Trying = null;   // the idea alone now
        if (instead is null) channels = ChannelsToPlay(out _playLength);
        else
        {
            _playLength = PlayedTo(instead.SelectMany(c => c.Notes).Select(n => n.Start + n.Length).DefaultIfEmpty(0).Max());
            channels = Looped(instead.ToList(), ref _playLength);
        }
        if (channels.Count == 0 || channels.All(c => c.Notes.Count == 0))
        {
            StopPlaying();
            Message = "Nothing to play yet.";
            return false;
        }
        var tempo = _followTempo ?? _session.Tempo;
        var total = _playLength;
        float[] audio;
        try
        {
            audio = await UiWork.Run(() =>
            {
                var a = MidiRender.Render(channels, tempo, "").Audio;
                var samples = (int)Math.Min(a.Length, Math.Round(MidiRender.TicksToSeconds(total, Ppq, tempo) * MidiRender.Rate));
                return samples > 0 && samples < a.Length ? a[..samples] : a;
            });
        }
        catch (Exception e) when (e is IOException or OutOfMemoryException)
        {
            Message = $"Couldn't play it: {e.Message}";
            return false;
        }
        if (request != _request) return false;
        if (_player is null)
        {
            _player = Ui.Current.CreateAudioPlayer();
            _player.Stopped += () => { if (_playing && _player?.Playing is null) GoRound(); };
        }
        _clock ??= Ui.Current.CreateTimer(TimeSpan.FromMilliseconds(30), Tick);
        var start = 0.0;
        if (startAt is { } tick)
            start = MidiRender.TicksToSeconds(Math.Clamp(tick - (Loop?.Start ?? 0), 0, Math.Max(0, total - 1)), Ppq, tempo);
        else if (keepPlace && _player.Playing == _playKey && _player.Position is { } at)
            start = MidiRender.TicksToSeconds((long)(MidiRender.SecondsToTicks(at, Ppq, _playTempo) % Math.Max(1, total)), Ppq, tempo);
        else if (instead is null)
        {
            // From where Play starts, when that's within what plays.
            var from = Loop?.Start ?? 0;
            if (Cursor > from && Cursor < from + total) start = MidiRender.TicksToSeconds(Cursor - from, Ppq, tempo);
        }
        _playKey = $"playground:{request}";
        _audio = audio;
        _playTempo = tempo;
        try { _player.Play(audio, MidiRender.Rate, _playKey, Math.Min(start, Math.Max(0, audio.Length / (double)MidiRender.Rate - 0.05))); }
        catch (Exception e)
        {
            Message = $"Couldn't play it: {e.Message}";
            return false;
        }
        _playing = true;
        IsPlaying = true;
        _clock.Start();
        return true;
    }

    List<MidiChannel> ChannelsToPlay(out long length)
    {
        length = PlayedTo(Section.Layers.SelectMany(l => l.Clips).Select(c => c.End).Concat(Section.ChordTrack.Select(c => c.End)).DefaultIfEmpty(0).Max());
        var channels = PlaygroundSession.Audible(Section).Select(l => Channel(l.Name, l.Program, l.Notes, l.Volume)).ToList();
        if (ChordTrackChannel(Section) is { } chords) channels.Add(chords);
        return Looped(channels, ref length);
    }

    /// <summary>
    /// With no highlight, Play goes round at the end of the bar the last clip ends in, not on through the empty bars after it
    /// (Solomon, 2026-10-21: "it should automatically loop at the end of all the clips").
    /// </summary>
    long PlayedTo(long end) => Math.Clamp((end + Bar - 1) / Bar * Bar, Bar, Math.Max(Bar, Section.Length));

    /// <summary>Just the loop's bars, from its start, when there are loop points.</summary>
    List<MidiChannel> Looped(List<MidiChannel> channels, ref long length)
    {
        if (Loop is not { } r) return channels;
        length = r.End - r.Start;
        // Chords pushed ahead of the loop's first bar, or tied into it, still sound from its start.
        return channels.Select(c => c with { Notes = MidiBrowserViewModel.Cut(c.Notes, r.Start, r.End) }).ToList();
    }

    static MidiChannel Channel(string name, int? program, IReadOnlyList<NoteEvent> notes, double volume = 1) =>
        new(program == GmSounds.DrumKit ? $"Drums · {name}" : name, "", Ppq, notes) { GmProgram = program, Volume = (float)volume };

    void GoRound()
    {
        if (!_playing || _audio is null || _player is null) return;
        if (!LoopOn && !_follow.Following) { StopPlaying(); return; }
        try { _player.Play(_audio, MidiRender.Rate, _playKey); }
        catch (Exception e) { Message = $"Couldn't play it: {e.Message}"; StopPlaying(); }
    }

    void Tick()
    {
        if (!_playing || _player is null) return;
        if (_player.Playing != _playKey || _player.Position is not { } seconds)
        {
            if (_player.Playing is null) GoRound();
            return;
        }
        var ticks = MidiRender.SecondsToTicks(seconds, Ppq, _playTempo);
        Position = ticks % Math.Max(1, _playLength) + (Loop?.Start ?? 0);
    }

    public void StopPlaying()
    {
        _request++;
        _clock?.Stop();
        if (_player is not null && _player.Playing == _playKey) _player.Stop();
        _playing = false;
        IsPlaying = false;
        Position = -1;
        foreach (var m in Matches) m.IsPlaying = m.IsPreparing = false;
        foreach (var r in Ideas) r.IsPlaying = false;
        foreach (var r in HarmoniseIdeas) r.IsPlaying = false;
        foreach (var r in Progressions) r.IsPlaying = false;
        Trying = null;
    }

    /// <summary>The suggestion being heard with the idea, shown faintly on the timeline (Solomon, 2026-10-21: "play with
    /// idea has no visual feedback"); null when none is.</summary>
    [ObservableProperty] TryingPart? _trying;

    partial void OnTryingChanged(TryingPart? value)
    {
        // With no clip picked, the first pitched one where it's tried is, so the piano roll shows them together. Not
        // with bars highlighted: the picked clip would become the one to swap bars of, and Suggest would find again
        // under the part being heard.
        if (value is not null && SelectedClip is null && Loop is null &&
            Layers.Where(l => l.Role != MidiRoles.Drums).SelectMany(l => l.Layer.Clips.Select(c => (l, c)))
                  .FirstOrDefault(p => p.c.Start < value.Start + value.Length && p.c.End > value.Start) is ({ } track, { } clip))
            PickClip(track, clip);
        SelectedClip?.TryingChanged();
    }

    /// <summary>The idea with a suggestion playing on top where it would go (the highlight, or from bar 1), to hear it before adding it.</summary>
    internal async Task PreviewMatch(PlaygroundMatchRow row, bool carryOn = false)
    {
        if (row.IsPlaying || row.IsPreparing)
        {
            StopPlaying();
            row.IsPlaying = row.IsPreparing = false;   // also a row no longer in the list
            return;
        }
        // Moving on to another row while one plays carries on from where it got to, so parts can be compared bar by bar.
        long? from = carryOn && _playing && Position >= 0 ? (long)Position : null;
        _pickingToHear = true;
        SelectedMatch = row;   // the arrow keys then go on from this row
        _pickingToHear = false;
        StopPlaying();
        row.IsPreparing = true;   // making the sound takes a moment: say so straight away
        try { await PreviewMatchAsync(row, from); }
        finally { row.IsPreparing = false; }
    }

    /// <summary>The suggestion picked in the list (by a click or the arrow keys).</summary>
    [ObservableProperty] PlaygroundMatchRow? _selectedMatch;
    bool _pickingToHear;

    /// <summary>
    /// Another suggestion picked while one is being heard (Solomon, 2026-10-22: "press the arrow key or select another
    /// clip and it starts playing straight away from where the playhead is"): that one plays instead, from there.
    /// </summary>
    partial void OnSelectedMatchChanged(PlaygroundMatchRow? value)
    {
        if (_pickingToHear || value is null || value.IsPlaying || value.IsPreparing) return;
        if (Matches.Any(m => m.IsPlaying || m.IsPreparing)) _ = PreviewMatch(value, carryOn: true);
    }

    async Task PreviewMatchAsync(PlaygroundMatchRow row, long? from = null)
    {
        if (SwapTarget is { } target && SwapRange is { } bars)
        {
            // Heard as Swap in would leave it: those bars of the clip played by the suggestion instead.
            var tried = PlaygroundSession.FromJson(_session.ToJson())!.Timeline;
            var layer = tried.Layers[Section.Layers.IndexOf(target.Track.Layer)];
            CutOut(layer, bars.Start, bars.End);
            var swapped = LibraryClip(row.Match.Entry, row.Match.Notes, bars.Start, bars.End - bars.Start, row.Match.Period);
            layer.Clips.Add(swapped);
            layer.Render();
            if (!await PlayAsync(instead: ChannelsOf(tried).ToList(), startAt: from)) return;
            row.IsPlaying = true;
            Trying = new(row.Title, Layers.IndexOf(target.Track), bars.Start, bars.End - bars.Start,
                         layer.Notes.Where(n => n.Start >= bars.Start && n.Start < bars.End).ToList());
            return;
        }
        var channels = ChannelsOf(Section).ToList();
        var notes = row.Match.Notes;
        var period = row.Match.Period > 0 ? row.Match.Period : Math.Max(1, PhraseAnalyser.BarsOf(notes)) * (long)Bar;
        var (start, length) = Place;
        var placed = SectionMatcher.Loop(notes, period, length).Select(n => n with { Start = n.Start + start }).ToList();
        channels.Add(Channel(row.Title, GmSounds.DefaultFor(row.Match.Entry.Role), placed));
        if (!await PlayAsync(instead: channels, startAt: from)) return;
        row.IsPlaying = true;
        Trying = new(row.Title, Layers.Count, start, length, placed);
    }

    /// <summary>
    /// The play button on the card of the idea being tried: it's in place (the chord parts play the new chords, and the parts follow
    /// them), so this plays the idea as it is, from the bar before the change, or stops it. Another idea is tried first.
    /// </summary>
    internal async Task PreviewIdea(ReharmRow row)
    {
        if (TryingIdea != row) { await TryIdea(row); return; }
        if (_playing) { StopPlaying(); return; }
        row.IsPlaying = await PlayAsync(startAt: Math.Max(0, _tryRange.From / Bar * Bar - Bar));
    }

    IEnumerable<MidiChannel> ChannelsOf(PlaygroundSection section) =>
        PlaygroundSession.Audible(section).Select(l => Channel(l.Name, l.Program, l.Notes, l.Volume))
                         .Concat(ChordTrackChannel(section) is { } chords ? [chords] : []);

    // ---- out to the DAW ----

    string ExportFolder()
    {
        var root = _db.GetSetting(ProjectDetailViewModel.ExportFolderKey) is { Length: > 0 } f
            ? f
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.Create), "Music Organiser Exports");
        var dir = Path.Combine(root, "MIDI playground");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A track's notes within the timeline, cut at its end: clips past the end (after it was made shorter) don't go out to the DAW.</summary>
    List<NoteEvent> InSection(IReadOnlyList<NoteEvent> notes) => MidiBrowserViewModel.Cut(notes, 0, Section.Length);

    public DragPayload? MakeDrag(IReadOnlyList<object> items)
    {
        var dir = ExportFolder();
        // The whole timeline ("arrangement", or "section" from the small playground): a track for each part that plays.
        if (items.FirstOrDefault() is "arrangement" or "section")
        {
            var tracks = PlaygroundSession.Audible(Section).Select(l => (l.Name, l.Program, Notes: InSection(l.Notes))).Where(t => t.Notes.Count > 0).ToList();
            if (tracks.Count == 0) return null;
            var clip = new MidiClip { Name = _session.Name, Ppq = Ppq };
            foreach (var t in tracks) clip.Track(t.Name).AddRange(t.Notes);
            var name = _session.Name;
            var file = Path.Combine(dir, TextScan.SafeFileName(name) + ".mid");
            File.WriteAllBytes(file, MidiWriter.Write(clip, _session.Tempo));
            return new DragPayload([file], tracks.Select(t => new DragCard(t.Name, $"{t.Notes.Count} notes", Glyphs.Midi)).ToList(),
                                   $"{name}: {tracks.Count} tracks", Numbered: true);
        }
        // A label pulled out of the timeline: its bars, a track for each part playing in them.
        if (items.FirstOrDefault() is PlaygroundLabel label)
        {
            var tracks = PlaygroundSession.Audible(Section)
                .Select(l => (l.Name, Notes: MidiBrowserViewModel.Cut(l.Notes, label.Start, Math.Min(label.End, Section.Length)).Select(n => n with { Start = n.Start - label.Start }).ToList()))
                .Where(t => t.Notes.Count > 0).ToList();
            if (tracks.Count == 0) return null;
            var name = $"{label.Name} ({_session.Name})";
            var clip = new MidiClip { Name = name, Ppq = Ppq };
            foreach (var t in tracks) clip.Track(t.Name).AddRange(t.Notes);
            var file = Path.Combine(dir, TextScan.SafeFileName(name) + ".mid");
            File.WriteAllBytes(file, MidiWriter.Write(clip, _session.Tempo));
            return new DragPayload([file], tracks.Select(t => new DragCard(t.Name, $"{t.Notes.Count} notes", Glyphs.Midi)).ToList(),
                                   $"{label.Name}: {tracks.Count} tracks", Numbered: true);
        }
        // A clip pulled out of the timeline: just its notes, from its start.
        if (items.FirstOrDefault() is ClipPick pick)
        {
            var notes = pick.Clip.Render().Select(n => n with { Start = n.Start - pick.Clip.Start }).ToList();
            if (notes.Count == 0) return null;
            var clip = new MidiClip { Name = pick.Clip.Name, Ppq = Ppq };
            clip.Track(pick.Track.Name).AddRange(notes);
            var file = Path.Combine(dir, TextScan.SafeFileName($"{pick.Clip.Name} ({_session.Name})") + ".mid");
            File.WriteAllBytes(file, MidiWriter.Write(clip, _session.Tempo));
            return new DragPayload([file], [new DragCard(pick.Clip.Name, $"{notes.Count} notes", Glyphs.Midi)], "1 MIDI clip");
        }
        var rows = items.OfType<LayerRow>().ToList();
        if (rows.Count == 0 && SelectedLayer is { } s) rows = [s];
        if (rows.Count == 0) return null;
        var files = rows.Select(r =>
        {
            var clip = new MidiClip { Name = r.Name, Ppq = Ppq };
            clip.Track(r.Name).AddRange(InSection(r.Layer.Notes));
            var file = Path.Combine(dir, TextScan.SafeFileName($"{r.Name} ({_session.Name})") + ".mid");
            File.WriteAllBytes(file, MidiWriter.Write(clip, _session.Tempo));
            return file;
        }).ToList();
        return new DragPayload(files, rows.Select(r => new DragCard(r.Name, r.Details, Glyphs.Midi)).ToList(),
                               rows.Count == 1 ? "1 MIDI clip" : $"{rows.Count} MIDI clips");
    }

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendToBitwigCommand))] bool _isSending;

    /// <summary>Each part of the section on its own new track in Bitwig, as a clip.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    async Task SendToBitwig()
    {
        var layers = PlaygroundSession.Audible(Section).ToList();
        if (layers.Count == 0) return;
        IsSending = true;
        try
        {
            var dir = ExportFolder();
            Message = "Connecting to Bitwig…";
            using var link = await _main.Live.Watch.ConnectBitwigAsync();
            foreach (var l in layers)
            {
                Message = $"Sending {l.Name} to Bitwig…";
                var clip = new MidiClip { Name = l.Name, Ppq = Ppq };
                clip.Track(l.Name).AddRange(InSection(l.Notes));
                var file = Path.Combine(dir, TextScan.SafeFileName($"{l.Name} ({_session.Name})") + ".mid");
                File.WriteAllBytes(file, MidiWriter.Write(clip, _session.Tempo));
                var track = await link.NewTrackAsync(l.Name);
                await link.AddClipAsync(track, file);
            }
            Message = $"{layers.Count} parts are on new tracks in Bitwig, as clips at {_session.Tempo:0} BPM. Put an instrument on each to hear them.";
        }
        catch (Core.DawLink.DawLinkException e) { Message = e.Message; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Message = $"Sending to Bitwig failed: {e.Message}";
        }
        finally { IsSending = false; }
    }

    bool CanSend() => !IsSending;

    public void Left()
    {
        FollowBitwig = false;
        StopPlaying();
        StopBrowsing();
    }

    public void Dispose()
    {
        StopWithBitwig();
        StopPlaying();
        _player?.Dispose();
        _matchCts?.Cancel();
    }
}

/// <summary>A track on the timeline, as its header (and the small playground's lane) shows it.</summary>
public sealed partial class LayerRow : ObservableObject
{
    readonly PlaygroundViewModel _owner;

    public LayerRow(PlaygroundViewModel owner, PlaygroundLayer layer)
    {
        _owner = owner;
        Layer = layer;
    }

    public PlaygroundLayer Layer { get; }

    /// <summary>How tall its row is: every track the same, as dragged.</summary>
    public double Height => _owner.TrackHeight;
    internal void HeightChanged() => OnPropertyChanged(nameof(Height));

    public string Name
    {
        get => Layer.Name;
        set
        {
            if (value.Trim().Length == 0 || value == Layer.Name) return;
            _owner.RenameLayer(this, value);
        }
    }

    public string Role => Layer.Role;
    /// <summary>Its colour on the timeline and beside its name: one per kind of part, like a DAW's track colours.</summary>
    public string Colour => TrackColours.Of(Layer.Role);
    public string RoleLabel => MidiRoles.Plural(Layer.Role) switch { "Melodies" => "Melody", "Basslines" => "Bassline", "Arps" => "Arp", var p => p };
    public bool IsWritten => Layer.Written.Length > 0;
    public string From => Layer.SourcePath.Length > 0 ? Path.GetFileNameWithoutExtension(Layer.SourcePath)
        : IsWritten ? "Written from the chords" : "Your own";
    public string Details => $"{RoleLabel} · {From}{(Layer.GrooveFrom.Length > 0 ? $" · groove of {Layer.GrooveFrom}" : "")}";
    public bool Muted => Layer.Muted;
    public bool Solo => Layer.Solo;

    /// <summary>Its volume, 1 as it is, and in decibels as a mixer writes it ("-6.0 dB").</summary>
    public double Volume => Layer.Volume;
    public string VolumeText => Decibels(Layer.Volume);
    internal void VolumeChanged()
    {
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(VolumeText));
    }
    // A General MIDI channel volume sounds as its square: 100 × v is 40·log10(v) dB from 100.
    internal static string Decibels(double v) => v <= 0.001 ? "−∞ dB" : $"{40 * Math.Log10(v):+0.0;−0.0;0.0} dB";
    [RelayCommand] void SetVolume(FaderMove move) => _owner.SetVolume(this, move);

    /// <summary>The notes as a channel for the small roll, the timeline's length wide.</summary>
    public MidiChannel Channel => new(Layer.Name, "", PhraseAnalyser.Ppq, Layer.Notes);
    /// <summary>The track's clips, for the timeline to draw.</summary>
    public IReadOnlyList<PlaygroundClip> Clips => Layer.Clips;
    public IReadOnlyList<NoteEvent> Notes => Layer.Notes;
    public long Length => _owner.Length;

    /// <summary>Every other part that plays, grey behind this one on the piano roll.</summary>
    public IReadOnlyList<NoteEvent> Guide =>
        PlaygroundSession.Audible(_owner.CurrentSection).Where(l => l != Layer && l.Role != MidiRoles.Drums).SelectMany(l => l.Notes).ToList();

    /// <summary>
    /// Which notes fight the chords, for the piano roll to mark red: judged by chord-scale theory against every chord
    /// each note sounds over, with the section's ears, so jazz tensions and blue notes aren't marked and a note held
    /// into a chord it fights is; notes an eighth or shorter are let off as passing notes.
    /// </summary>
    public Func<NoteEvent, bool> Clashes
    {
        get
        {
            var h = _owner.Harmony;
            var style = h.Style;
            if (Layer.Role == MidiRoles.Drums || h.Lane.Count == 0) return _ => false;
            return n => ChordLane.Fights(h.Lane, n.Start, n.Length, n.Key % 12, style, h.Length);
        }
    }

    public GmSounds.Choice Sound
    {
        get => GmSounds.ChoiceFor(Layer.Program ?? GmSounds.DefaultFor(Layer.Role));
        set
        {
            if (value is null || value.Program == Layer.Program) return;
            _owner.EditLayer(this, l => l.Program = value.Program, harmony: false);
        }
    }

    public static IReadOnlyList<GmSounds.Choice> SoundChoices => GmSounds.Choices;

    internal void Changed()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Channel));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(Muted));
        OnPropertyChanged(nameof(Solo));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(IsWritten));
        OnPropertyChanged(nameof(Sound));
        OnPropertyChanged(nameof(Clips));
    }

    internal void GuideChanged()
    {
        OnPropertyChanged(nameof(Guide));
        OnPropertyChanged(nameof(Clashes));
    }
    internal void LengthChanged() => OnPropertyChanged(nameof(Length));

    [RelayCommand] void Remove() => _owner.RemoveLayer(this);
    [RelayCommand] void Duplicate() => _owner.DuplicateLayer(this);
    [RelayCommand] void Mute() => _owner.ToggleMute(this);
    [RelayCommand] void ToggleSolo() => _owner.ToggleSolo(this);
    [RelayCommand] void OctaveUp() => _owner.MoveLayer(this, 12);
    [RelayCommand] void OctaveDown() => _owner.MoveLayer(this, -12);
    [RelayCommand] void Up() => _owner.MoveLayer(this, 1);
    [RelayCommand] void Down() => _owner.MoveLayer(this, -1);
    [RelayCommand] void Edited(IReadOnlyList<NoteEvent>? notes) { if (notes is not null) _owner.SetNotes(this, notes); }
}

/// <summary>A clip on a track, as the timeline hands it over when it's clicked.</summary>
public sealed record ClipPick(PlaygroundLayer Track, PlaygroundClip Clip);

/// <summary>A suggestion being heard with the idea, drawn faintly on the timeline where it would go: on track
/// <paramref name="Row"/> (one past the last is a new track), its notes already placed.</summary>
public sealed record TryingPart(string Name, int Row, long Start, long Length, IReadOnlyList<NoteEvent> Notes);

/// <summary>
/// A clip dragged on the timeline: onto track <paramref name="Row"/> (one past the last makes a new track), starting at
/// <paramref name="Start"/>, playing for <paramref name="Length"/>, from <paramref name="Offset"/> into its go round.
/// </summary>
/// <summary>
/// A part let go on the timeline: a suggestion (<see cref="PlaygroundMatchRow"/>), a part from the MIDI list (<see cref="MidiRow"/>) or .mid file paths, on track
/// <paramref name="Row"/> (one past the last makes a new track) from <paramref name="Start"/>.
/// </summary>
public sealed record PartDrop(object Part, int Row, long Start);

public sealed record ClipEdit(PlaygroundLayer Track, PlaygroundClip Clip, int Row, long Start, long Length, long Offset);

/// <summary>
/// The clip picked on the timeline, as the piano roll under it shows it: one go round of its notes as they first play,
/// with the other parts playing then grey behind and the notes fighting the chords there marked red.
/// </summary>
public sealed partial class ClipRow(PlaygroundViewModel owner, LayerRow track, PlaygroundClip clip) : ObservableObject
{
    const int Bar = 4 * PhraseAnalyser.Ppq;

    public LayerRow Track => track;
    public PlaygroundClip Clip => clip;
    public string Name => clip.Name;

    public string Details
    {
        get
        {
            var moved = clip.Pinned ? clip.Shift(0) : clip.Transpose;
            var shift = moved == 0 ? "" : $" · {(moved > 0 ? "+" : "")}{moved}{(clip.Pinned ? ", set by hand" : " by hand")}";
            var times = clip.Length > clip.Loop ? $", played {clip.Length / (double)clip.Loop:0.#} times" : "";
            return $"{track.Name} · bars {clip.Start / Bar + 1}–{(clip.End + Bar - 1) / Bar} · {Bars(clip.Loop)} round{times}{shift}";
        }
    }

    static string Bars(long ticks) => ticks % Bar == 0 ? $"{ticks / Bar} bar{(ticks == Bar ? "" : "s")}" : $"{ticks / (double)Bar:0.##} bars";

    // The inspector's lines, short (the whole sentence is Details).
    public string BarsText => $"{clip.Start / Bar + 1} – {(clip.End + Bar - 1) / Bar}";
    public string RoundText => $"{Bars(clip.Loop)}{(clip.Length > clip.Loop ? $" × {clip.Length / (double)clip.Loop:0.#}" : "")}";
    public string MovedText
    {
        get
        {
            var moved = clip.Pinned ? clip.Shift(0) : clip.Transpose;
            return moved == 0 ? "0 st" : $"{(moved > 0 ? "+" : "")}{moved} st";
        }
    }

    // The inspector's values you drag or type (Solomon, 2026-10-22): how far it's moved, as whole octaves and the
    // semitones left over (+14 is an octave and 2), and how far its chords are turned.
    int Moved => clip.Pinned ? clip.Shift(0) : clip.Transpose;
    // −6 to +5 semitones on top of the nearest octave, so +5 st an octave down reads "−1, +5 st", not "0, −7 st".
    public int OctaveValue => (int)Math.Floor((Moved + 6) / 12.0);
    public int SemitoneValue => Moved - 12 * OctaveValue;
    public int InversionValue => clip.Inversion;
    public string OctaveText => Signed(OctaveValue);
    public string SemitoneText => $"{Signed(SemitoneValue)} st";
    public string InversionText => clip.Inversion switch { 0 => "Root", 1 => "1st", 2 => "2nd", 3 => "3rd", var i => Signed(i) };
    static string Signed(int v) => v > 0 ? $"+{v}" : $"{v}";
    public string Colour => track.Colour;

    /// <summary>Where its first go round would start on the timeline.</summary>
    internal long At => clip.RepeatAt(0);

    /// <summary>What it plays as: its own kind, or its track's when it has none.</summary>
    internal string Role => clip.Role == MidiRoles.Other ? track.Role : clip.Role;
    internal bool IsDrums => clip.Role == MidiRoles.Drums || track.Role == MidiRoles.Drums;

    /// <summary>One go round, as it first plays (moved by hand and to fit).</summary>
    public IReadOnlyList<NoteEvent> Notes => clip.Under.Count > 0 ? clip.FirstRound()
        : Inversions.Turn(clip.Notes, clip.Inversion).Select(n => n with { Key = Math.Clamp(n.Key + clip.Shift(0), 0, 127) }).ToList();
    public long Loop => clip.Loop;
    public bool IsChords => clip.Role == MidiRoles.Chords;

    /// <summary>What shift to fit is doing with it, to show beside its tools.</summary>
    public string FitText => track.IsWritten ? "Written from the chords: follows them"
        : IsDrums ? ""
        : clip.Pinned ? "Set by hand: stays as it is"
        : clip.Under.Count > 0 ? "Follows the chord track"
        : clip.Way == FollowWay.None ? "Doesn't follow the chords"
        : clip.Role is MidiRoles.Chords || track.Role is MidiRoles.Chords ? ""
        : clip.Fits.All(f => f == 0) ? "Fits where it is"
        : "Moved to fit: " + string.Join(" ", clip.Fits.Select(f => f == 0 ? "0" : f > 0 ? $"+{f}" : $"{f}"));
    public bool CanFitAgain => clip.Pinned && clip.Role != MidiRoles.Drums && track.Role != MidiRoles.Drums;

    /// <summary>The ways it can follow the chord track, for the inspector's list.</summary>
    public static IReadOnlyList<string> FollowWays { get; } =
        [.. new[] { FollowWay.Nudge, FollowWay.Degree, FollowWay.Root, FollowWay.None }.Select(ChordFollow.ShortLabel)];

    /// <summary>How it follows the chord track; picking another follows that way (Solomon, 2026-10-22).</summary>
    public string FollowPick
    {
        get => ChordFollow.ShortLabel(clip.Way);
        set
        {
            var way = Enum.GetValues<FollowWay>().FirstOrDefault(w => w != FollowWay.Auto && ChordFollow.ShortLabel(w) == value);
            if (way != FollowWay.Auto && way != clip.Way) owner.SetFollow(this, way);
        }
    }
    public bool CanFollow => !IsDrums && !track.IsWritten;

    /// <summary>
    /// A way to follow picked by hand with no chords on the chord track under the clip, so there's nothing to follow yet
    /// (Solomon, 2026-10-23: "changing follow the chords doesnt seem to do anything"): the inspector says so, with the
    /// chords heard to put there.
    /// </summary>
    public bool NothingToFollow => CanFollow && !clip.Pinned && clip.Follow is not (FollowWay.Auto or FollowWay.None) && clip.Under.Count == 0;
    public string NothingToFollowText => owner.HasChordTrack
        ? "No chords on the chord track under it, so there's nothing to follow yet."
        : "The chord track is empty, so there's nothing to follow yet.";
    [RelayCommand] void PutChordsUnder() => owner.KeepHeardChordsUnder(this);

    /// <summary>Every other part playing during that go round, grey behind it on the piano roll.</summary>
    public IReadOnlyList<NoteEvent> Guide
    {
        get
        {
            long from = At, to = At + clip.Loop;
            return PlaygroundSession.Audible(owner.CurrentSection).Where(l => l != track.Layer && l.Role != MidiRoles.Drums)
                .SelectMany(l => l.Notes).Where(n => n.Start < to && n.Start + n.Length > from)
                // a note held over from before sounds from the start of the go round
                .Select(n => n.Start >= from ? n with { Start = n.Start - from } : n with { Start = 0, Length = n.Start + n.Length - from }).ToList();
        }
    }

    /// <summary>The part being tried with the idea, over this go round of the clip: outlined on the piano roll.</summary>
    public IReadOnlyList<NoteEvent>? Overlay
    {
        get
        {
            if (owner.Trying is not { } t) return null;
            long from = At, to = At + clip.Loop;
            var notes = t.Notes.Where(n => n.Start < to && n.Start + n.Length > from)
                .Select(n => n.Start >= from ? n with { Start = n.Start - from } : n with { Start = 0, Length = n.Start + n.Length - from }).ToList();
            return notes.Count == 0 ? null : notes;
        }
    }

    internal void TryingChanged() => OnPropertyChanged(nameof(Overlay));

    /// <summary>Which notes fight the chords under them, judged as <see cref="LayerRow.Clashes"/> does.</summary>
    public Func<NoteEvent, bool> Clashes => ClashesIn(owner.Harmony);

    Func<NoteEvent, bool> ClashesIn(SectionHarmony h)
    {
        var style = h.Style;
        var at = At;
        if (clip.Role == MidiRoles.Drums || track.Role == MidiRoles.Drums || h.Lane.Count == 0) return _ => false;
        return n => ChordLane.Fights(h.Lane, at + n.Start, n.Length, n.Key % 12, style, h.Length);
    }

    /// <summary>What each note is over the chord heard under it (3, ♭7, 9), for the piano roll's note labels.</summary>
    public Func<NoteEvent, string?> DegreeOf
    {
        get
        {
            var h = owner.Harmony;
            var at = At;
            if (clip.Role == MidiRoles.Drums || track.Role == MidiRoles.Drums || h.Lane.Count == 0) return _ => null;
            return n => ChordLane.Heard(h.Lane, at + n.Start, n.Length, h.Length) is { } chord ? ChordScales.Degree(chord, n.Key % 12) : null;
        }
    }

    /// <summary>
    /// How strongly each note fits the chord under it (<see cref="NoteStrength"/>), for the piano roll to colour the notes
    /// by; null when they're shown plain. A note that fights the chords there (<see cref="Clashes"/>) stays red, and one
    /// that only looks wrong on paper (a quick passing note) is weak.
    /// </summary>
    public Func<NoteEvent, FitStrength?>? StrengthOf => owner.FitColours ? StrengthIn(owner.Harmony) : null;

    Func<NoteEvent, FitStrength?>? StrengthIn(SectionHarmony h)
    {
        if (IsDrums || h.Lane.Count == 0) return null;
        var at = At;
        var role = Role;
        var fights = ClashesIn(h);
        return n =>
        {
            if (ChordLane.Heard(h.Lane, at + n.Start, n.Length, h.Length) is not { } chord) return null;
            if (fights(n)) return FitStrength.Fights;
            var strength = NoteStrength.Shown(chord, n.Key % 12, role, h.Style);
            return strength == FitStrength.Fights ? FitStrength.Weak : strength;
        };
    }

    /// <summary>
    /// The colour a note clicked in would take, for the piano roll's preview of it (Solomon, 2026-10-22: "the preview colour
    /// of the piano roll doesnt make the colour when you click the note in"). The chords are heard from the chord and bass
    /// parts, so a note put in one can change the chord it's judged against (a low B♭ under Cmaj9 makes it B♭maj13): it's
    /// judged against the chords as they'll be with it in. Each spot is worked out once, until the idea changes.
    /// </summary>
    public Func<NoteEvent, FitStrength?>? WouldBe
    {
        get
        {
            if (StrengthOf is not { } now) return null;
            if (owner.Harmony.ChordsTyped) return now;   // typed chords don't change with the notes
            var known = new Dictionary<NoteEvent, FitStrength?>();
            return n => known.TryGetValue(n, out var s) ? s : known[n] = owner.WithNoteAdded(this, n, h => StrengthIn(h)?.Invoke(n));
        }
    }

    /// <summary>The chords under this go round, each with how strongly each note would fit it: the piano roll lights the strongest rows behind.</summary>
    public IReadOnlyList<FitSpan>? FitGuide
    {
        get
        {
            var h = owner.Harmony;
            if (!owner.FitColours || IsDrums || h.Lane.Count == 0) return null;
            long from = At, to = At + clip.Loop, length = Math.Max(1, h.Length);
            var role = Role;
            // Going round past the section's end, it carries on from the section's start, as the note colours do.
            var spans = new List<FitSpan>();
            for (var a = from; a < to;)
            {
                var lap = a / length * length;
                var b = Math.Min(to, lap + length);
                foreach (var c in h.Lane.Where(c => c.Start + lap < b && c.End + lap > a))
                {
                    long start = Math.Max(c.Start + lap, a), end = Math.Min(c.End + lap, b);
                    spans.Add(new FitSpan(start - from, end - start, NoteStrength.AllShown(c.Chord, role, h.Style)));
                }
                a = b;
            }
            return spans;
        }
    }

    /// <summary>
    /// Whether a note (placed at its time on the roll) goes with the chord under it, for snapping to the chords: one of its
    /// own notes, the strongest fits (<see cref="NoteStrength"/>), whatever the colours show. Where no chord is heard
    /// anything goes; null when there are no chords at all.
    /// </summary>
    public Func<NoteEvent, bool>? FitsChords => StrengthIn(owner.Harmony) is { } strength ? n => strength(n) is null or >= FitStrength.Strong : null;

    /// <summary>Whether its notes can be snapped to the chords: anything but drums.</summary>
    public bool CanSnap => !IsDrums;

    /// <summary>
    /// The key nearest <paramref name="note"/>'s that <paramref name="fits"/> says goes with the chords there, a semitone
    /// at a time either way up to an octave, the way of <paramref name="prefer"/> first; its own key when none does.
    /// </summary>
    public static int NearestFit(NoteEvent note, Func<NoteEvent, bool> fits, int prefer)
    {
        var way = prefer < 0 ? -1 : 1;
        for (var d = 0; d <= 12; d++)
            foreach (var key in d == 0 ? [note.Key] : new[] { note.Key + way * d, note.Key - way * d })
                if (key is >= 0 and <= 127 && fits(note with { Key = key })) return key;
        return note.Key;
    }

    /// <summary>Whether it's a bassline, whose strongest notes are the root and 5th (the colours' key says so).</summary>
    public bool IsBass => Role == MidiRoles.Bass;

    /// <summary>The highlight, where it's over the go round shown: bars picked along the piano roll's bar numbers.</summary>
    public RollRange? Highlight => owner.Loop is { } r && r.Start < At + clip.Loop && r.End > At
        ? new RollRange(Math.Max(r.Start, At) - At, Math.Min(r.End, At + clip.Loop) - At) : null;

    internal void HighlightMoved() => OnPropertyChanged(nameof(Highlight));

    /// <summary>Bars dragged out along the piano roll's bar numbers (null: let go): Suggest offers bits of parts to swap in there.</summary>
    [RelayCommand] void PickBars(RollRange? range) => owner.HighlightInClip(this, range);

    /// <summary>A click on the piano roll's bar numbers: the play marker goes there.</summary>
    [RelayCommand] void Seek(double tick) => owner.SetCursorCommand.Execute(At + (long)tick);

    /// <summary>The playhead on the piano roll, while it's in the go round shown.</summary>
    [ObservableProperty] double _position = -1;
    // Round and round with the clip where it repeats (Solomon, 2026-10-22: "playhead goes one time through a looping clip").
    internal void PlayheadAt(double tick) =>
        Position = tick < 0 || tick < clip.Start || tick >= clip.End || clip.Loop <= 0 ? -1 : ((tick - At) % clip.Loop + clip.Loop) % clip.Loop;

    internal void Changed()
    {
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(MovedText));
        OnPropertyChanged(nameof(OctaveValue));
        OnPropertyChanged(nameof(OctaveText));
        OnPropertyChanged(nameof(SemitoneValue));
        OnPropertyChanged(nameof(SemitoneText));
        OnPropertyChanged(nameof(InversionValue));
        OnPropertyChanged(nameof(InversionText));
        OnPropertyChanged(nameof(Loop));
        OnPropertyChanged(nameof(FitText));
        OnPropertyChanged(nameof(CanFitAgain));
        OnPropertyChanged(nameof(FollowPick));
        OnPropertyChanged(nameof(NothingToFollow));
        OnPropertyChanged(nameof(NothingToFollowText));
        OnPropertyChanged(nameof(Highlight));
        GuideChanged();
    }

    internal void GuideChanged()
    {
        OnPropertyChanged(nameof(Guide));
        OnPropertyChanged(nameof(Clashes));
        OnPropertyChanged(nameof(DegreeOf));
        OnPropertyChanged(nameof(StrengthOf));
        OnPropertyChanged(nameof(WouldBe));
        OnPropertyChanged(nameof(FitGuide));
        OnPropertyChanged(nameof(FitsChords));
    }

    [RelayCommand] void Remove() => owner.RemoveClip(this);
    [RelayCommand] void Duplicate() => owner.DuplicateClip(this);
    [RelayCommand] void OctaveUp() => owner.MoveClip(this, 12);
    [RelayCommand] void OctaveDown() => owner.MoveClip(this, -12);
    [RelayCommand] void Up() => owner.MoveClip(this, 1);
    [RelayCommand] void Down() => owner.MoveClip(this, -1);
    [RelayCommand] void TurnUp() => owner.TurnClip(this, 1);
    // Several steps at once from a dragged or typed value, in one undo step.
    [RelayCommand] void OctaveBy(ValueStep step) => owner.MoveClip(this, 12 * step.By, step.Gesture);
    [RelayCommand] void SemitoneBy(ValueStep step) => owner.MoveClip(this, step.By, step.Gesture);
    [RelayCommand] void TurnBy(ValueStep step) => owner.TurnClip(this, step.By, step.Gesture);
    [RelayCommand] void TurnDown() => owner.TurnClip(this, -1);
    [RelayCommand] void FitAgain() => owner.FitAgain(this);
    [RelayCommand] void Edited(IReadOnlyList<NoteEvent>? notes) { if (notes is not null) owner.SetClipNotes(this, notes); }
}

/// <summary>One chord on the chord lane: its name, Roman numeral and what it's doing, as wide as it lasts.</summary>
public sealed class ChordCell(TimedChord chord, ChordFunction? function, long length, bool set = false)
{
    /// <summary>On the chord track (no MIDI of its own): it can be stretched and moved. Otherwise heard from the parts.</summary>
    public bool IsSet => set;
    public string Name => chord.Chord.Name;
    /// <summary>The name in parts, so a narrow chord can be written shorter (<see cref="ChordSymbol.ShortQuality"/>).</summary>
    public string RootName => chord.Chord.RootName;
    public string Quality => chord.Chord.Quality;
    public string ShortQuality => ChordSymbol.ShortQuality(chord.Chord.Quality);
    public string LeastQuality => ChordSymbol.LeastQuality(chord.Chord.Quality);
    public string BassName => chord.Chord.BassName;
    public string Roman => function?.Roman ?? "";
    public string Label => function?.Label ?? "";
    /// <summary>T, S, D or blank: colours the cell.</summary>
    public string Kind => function is { Kind: not ' ' } f ? f.Kind.ToString() : "";
    public double Start => chord.Start / (double)Math.Max(1, length);
    public double Share => chord.Length / (double)Math.Max(1, length);
    public string Tip => $"{Name}{(Roman.Length > 0 ? $"  ({Roman})" : "")}{(Label.Length > 0 ? $": {Label}" : "")}" +
                         (set ? "\nOn the chord track: drag an edge to stretch it, the middle to move it" : "\nHeard from the parts");
    public TimedChord Timed => chord;
    public int Bar => (int)(chord.Start / (4 * PhraseAnalyser.Ppq)) + 1;
}

/// <summary>What a piano roll writes on its notes and keys: nothing, note names, or what each note is over the chord.</summary>
public enum NoteLabelKind { Off, Names, Degrees }

/// <summary>A library part suggested for the section.</summary>
public sealed partial class PlaygroundMatchRow(PlaygroundViewModel owner, PlaygroundMatch match) : ObservableObject
{
    public PlaygroundMatch Match => match;
    public string Title => match.Entry.Title.Length > 0 ? match.Entry.Title : match.Entry.SourceName;
    public string From => match.Entry.IsProject ? $"{match.Entry.SourceName} · {match.Entry.Phrase.Source.Daw}" : match.Entry.SourceName;
    public string FitText => match.Score > 0 ? $"{match.Percent}%" : "";
    public string Why => match.Why;
    /// <summary>Its chords as they'd go in, spelled in the section's key: a stretch cut from a chord part reads its own (moved as it is).</summary>
    public string Chords => match.Period > 0
        ? match.Entry.Role == MidiRoles.Chords ? string.Join(" ", ChordLane.Detect(match.Notes, match.Period).Take(6).Select(c => c.Chord.NameIn(owner.Harmony.Key))) : ""
        : string.Join(" ", match.Entry.Chords.Take(6).Select(c => c.Symbol.Transposed(match.Fit?.Semitones ?? 0).NameIn(owner.Harmony.Key)));
    public MidiChannel Channel => new(Title, "", PhraseAnalyser.Ppq, match.Notes);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewText))] bool _isPlaying;
    /// <summary>Its sound is being made, before it plays.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewText))] bool _isPreparing;
    public string PreviewText => IsPreparing ? "Getting it ready…" : IsPlaying ? "Stop" : "With the idea";
    [RelayCommand] void Add() => owner.AddMatch(this);
    /// <summary>What Add does: a new track, or (with a highlight in a clip) swapping those bars of it.</summary>
    public string AddText { get; } = owner.SwapTarget is null ? "Add" : "Swap in";
    [RelayCommand] Task Preview() => owner.PreviewMatch(this);
}

/// <summary>A reharmonisation idea, to hear and keep.</summary>
public sealed partial class ReharmRow(PlaygroundViewModel owner, Reharm idea) : ObservableObject
{
    public Reharm Idea => idea;
    public string Title => idea.Title;
    public string Change => $"Bar {idea.Bar}: {idea.Before} → {idea.After}";
    /// <summary>"G7 → Db9": what the chord becomes.</summary>
    public string Swap => $"{idea.Before} → {idea.After}";
    public string Why => idea.Why;
    [ObservableProperty] bool _isPlaying;
    [RelayCommand] void Apply() => owner.ApplyIdea(idea);
    [RelayCommand] Task Preview() => owner.PreviewIdea(this);
    /// <summary>Step 2 of Reharm: shown striped on the chord lane and heard, to keep or go back from.</summary>
    [RelayCommand] Task Try() => owner.TryIdea(this);
}

/// <summary>A chord your library often plays next.</summary>
public sealed partial class NextChordRow(PlaygroundViewModel owner, ChordSymbol chord, double share, KeyEstimate? key = null)
{
    public string Name => chord.NameIn(key);
    public string ShareText => $"{share * 100:0}%";
    [RelayCommand] void Add() => owner.AddNextChord(chord);
}

/// <summary>
/// Another way to hear the picked chord (another name for its notes, or another of them as its root), to put on the chord
/// track; named as the chord lane names it.
/// </summary>
public sealed partial class ChordReadingRow(PlaygroundViewModel owner, ChordSymbol chord, string text)
{
    public ChordSymbol Chord => chord;
    public string Text => text;
    public string Tip => $"Hear it as {chord.Name}: it goes on the chord track here, and the parts follow it";
    [RelayCommand] void Pick() => owner.HearAs(chord);
}

/// <summary>A colour for each kind of part, the same in every theme, as a DAW gives its tracks colours.</summary>
public static class TrackColours
{
    public static string Of(string role) => role switch
    {
        MidiRoles.Chords => "#2FBF83",
        MidiRoles.Bass => "#3A86D0",
        MidiRoles.Melody => "#8A63D2",
        MidiRoles.Arp => "#C9577A",
        MidiRoles.Drums => "#D79B3A",
        _ => "#7D8590",
    };
}

/// <summary>
/// Numbers for drags and typed values (<see cref="ValueStep"/>, <see cref="FaderMove"/>), one counter for every control that
/// sends them, so two different gestures never share a number and run into one undo step.
/// </summary>
public static class GestureIds
{
    static int _last;
    public static int Next() => Interlocked.Increment(ref _last);
}

/// <summary>A value dragged or typed: how many steps it moves, and which drag or typing it's part of (one undo step for all of them).</summary>
public readonly record struct ValueStep(int By, int Gesture);

/// <summary>A fader moved to <see cref="Value"/>: which drag it's part of (one undo step), and whether it was let go.</summary>
public readonly record struct FaderMove(double Value, int Gesture, bool Done);
