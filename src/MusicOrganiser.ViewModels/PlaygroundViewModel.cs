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
        RoleChips = new[] { MidiRoles.Chords, MidiRoles.Bass, MidiRoles.Melody, MidiRoles.Arp, MidiRoles.Drums }
            .Select(r => new RoleChip(MidiRoles.Plural(r), r)).ToList();
        _suggestRole = RoleChips[0];
        Show();
    }

    // ---- the idea: one timeline of tracks and clips ----

    /// <summary>The tracks, top to bottom.</summary>
    public ObservableCollection<LayerRow> Layers { get; } = [];
    public ObservableCollection<ChordCell> Chords { get; } = [];
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
    public bool IsEmpty => Layers.Count == 0;
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
        RefreshGrooveSources();
        OnPropertyChanged(nameof(IsEmpty));
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
            Chords.Add(new ChordCell(h.Lane[i], funcs?[i], h.Length));
        KeySummary = key is null ? "Key: not sure yet" : $"Key: {key.Name}{(h.KeyTyped ? "" : " (heard)")}";
        ChordSummary = h.Lane.Count == 0 ? "No chords yet: add a chord or bass part"
            : "Chords heard live from the chord and bass parts (everything playing, where there are none)";
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
                        NoteLabelsKey = "playground_note_labels",
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
        NewStyle = _db.GetSetting(NewStyleKey) == Classical ? Classical : JazzGospel;
        SmoothIncoming = _db.GetSetting(SmoothKey) != "0";
        LoopOn = _db.GetSetting(LoopKey) != "0";
        SnapGrid = _db.GetSetting(SnapGridKey) != "0";
        SnapRelative = _db.GetSetting(SnapRelativeKey) != "0";
        SnapObjects = _db.GetSetting(SnapObjectsKey) != "0";
        NoteLabelPick = NoteLabelChoices.Contains(_db.GetSetting(NoteLabelsKey) ?? "") ? _db.GetSetting(NoteLabelsKey)! : "Off";
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
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FoldText))] bool _pianoFolded;
    public string FoldText => PianoFolded ? "▸ Show piano roll" : "▾ Fold piano roll";

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
    void Remember()
    {
        _undo.Add(_session.ToJson());
        while (_undo.Count > UndoSteps) _undo.RemoveAt(0);
        _redo.Clear();
        CanUndo = true;
        CanRedo = false;
    }

    /// <summary>After every change: saved straight away, so nothing is lost if the app closes.</summary>
    void Changed(bool harmony = true)
    {
        // Parts written from the chords follow when an edit changed the chords heard, or moved or stretched a written clip.
        if (harmony && Section.Layers.Any(l => l.Written.Length > 0) && _writtenFor != WrittenKey(LaneForWriting()))
            RewriteWritten();
        Revision++;
        // A cut-down playground is a sketch: nothing to keep until Open in Playground takes it over.
        try { if (!IsQuick) _store.SaveCurrent(_session); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = $"Couldn't save the idea: {e.Message}"; }
        if (harmony) HarmonyChanged();
        else RefreshEnergy();
        OnPropertyChanged(nameof(IsEmpty));
        if (_playing) _ = PlayAsync(keepPlace: true);
    }

    [RelayCommand]
    void Undo()
    {
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
        _undo.Add(_session.ToJson());
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        CanRedo = _redo.Count > 0;
        CanUndo = true;
    }

    void Restore(string json)
    {
        if (PlaygroundSession.FromJson(json) is not { } s) return;
        _session = s;
        Show();
        if (Loop is { } loop) SetLoop(loop);   // kept inside the timeline as it now is
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
        try
        {
            _store.SaveIdea(_session);
            Message = $"Saved “{_session.Name}”. Open it again from Ideas.";
            OnPropertyChanged(nameof(SavedIdeas));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = $"Couldn't save it: {e.Message}"; }
    }

    public IReadOnlyList<string> SavedIdeas => _store.Ideas();

    [RelayCommand]
    void OpenIdea(string? name)
    {
        if (name is null || _store.LoadIdea(name) is not { } s) return;
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
        _session.Name = value.Trim().Length == 0 ? "Untitled idea" : value.Trim();
        OnPropertyChanged(nameof(Title));
        Changed(harmony: false);
    }

    partial void OnTempoChanged(double value)
    {
        if (_showing) return;
        var tempo = Math.Clamp(Math.Round(value), 40, 240);
        if (tempo == _session.Tempo) return;
        _session.Tempo = tempo;
        _ = SaveTempoSoon();
    }

    int _tempoMoves;

    /// <summary>Saved (and the music started again at the new speed) once the tempo slider stops, not on every step of a drag.</summary>
    async Task SaveTempoSoon()
    {
        var move = ++_tempoMoves;
        await Task.Delay(300);
        if (move == _tempoMoves) Changed(harmony: false);
    }

    partial void OnKeyTextChanged(string value)
    {
        if (_showing || value == _session.Key) return;
        if (value.Trim().Length > 0 && MusicTheory.ParseKey(value) is null) return;
        _session.Key = value.Trim();
        Changed();
    }

    partial void OnIsClassicalChanged(bool value)
    {
        if (_showing) return;
        _session.Style = value ? HarmonyStyle.Classical : HarmonyStyle.JazzGospel;
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
        if (Loop is { } loop) SetLoop(loop);   // kept inside the timeline, or dropped if it's now past the end
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
        if (label is not null) SetLoop(new RollRange(label.Start, label.End));
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
        layer.Render();
        Section.Layers.Add(layer);
        var row = new LayerRow(this, layer);
        Layers.Add(row);
        SelectedLayer = row;
        SelectedClip = new ClipRow(this, row, layer.Clips[0]);
        RefreshGrooveSources();
        return layer;
    }

    string UniqueName(string name)
    {
        name = name.Trim().Length == 0 ? "Part" : name.Trim();
        if (Section.Layers.All(l => l.Name != name)) return name;
        for (var i = 2; ; i++)
            if (Section.Layers.All(l => l.Name != $"{name} {i}")) return $"{name} {i}";
    }

    /// <summary>
    /// A part from the MIDI page or the suggestions, as it plays there (moved, octave and all), on a new track over the
    /// highlight, or from bar 1 for as long as the timeline (and at least once through, up to Settings' longest).
    /// </summary>
    public void AddFromLibrary(MidiEntry entry, IReadOnlyList<NoteEvent> notes, string? title = null) => AddFromLibrary(entry, notes, title, SmoothIncoming);

    void AddFromLibrary(MidiEntry entry, IReadOnlyList<NoteEvent> notes, string? title, bool smooth)
    {
        if (notes.Count == 0) return;
        Remember();
        var name = title ?? (entry.Title.Length > 0 ? entry.Title : entry.SourceName);
        var bars = Math.Max(1, PhraseAnalyser.BarsOf(notes));
        var loop = bars * (long)Bar;
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
        RefreshGrooveSources();
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
        RefreshGrooveSources();
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
    void Refit(PlaygroundLayer layer, PlaygroundClip clip)
    {
        var role = clip.Role == MidiRoles.Other ? layer.Role : clip.Role;
        if (clip.Pinned || layer.Written.Length > 0 || role is MidiRoles.Chords or MidiRoles.Drums) { if (!clip.Pinned) clip.Fits = []; return; }
        var leaveOut = role == MidiRoles.Bass ? Section.Layers.Where(l => l.Role != MidiRoles.Chords).ToList() : [layer];
        var lane = SectionHarmony.Of(Section, _session.Key, _session.Style, leaveOut).Lane;
        clip.Fits = ClipFit.Repeats(clip, lane, _session.Style);
    }

    // ---- parts swapped in, or dropped onto the timeline ----

    /// <summary>
    /// The clip the highlight is in (Solomon, 2026-10-19: "highlighting inside a clip gives alternatives of the same kind
    /// for just those bars"): the picked clip, when the highlight covers some of it. Suggest then lists parts of its kind
    /// for those bars, and picking one swaps it in there.
    /// </summary>
    public ClipRow? SwapTarget => Loop is { } r && SelectedClip is { } c && !c.Track.IsWritten && c.Clip.Start < r.End && c.Clip.End > r.Start ? c : null;

    /// <summary>The bars of <see cref="SwapTarget"/> under the highlight.</summary>
    (long Start, long End)? SwapRange => SwapTarget is { } t && Loop is { } r ? (Math.Max(r.Start, t.Clip.Start), Math.Min(r.End, t.Clip.End)) : null;

    static string RoleOf(ClipRow row) => row.Clip.Role == MidiRoles.Other ? row.Track.Role : row.Clip.Role;

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
        if (target is not null && RoleChips.FirstOrDefault(c => c.Role == RoleOf(target)) is { } chip && chip != SuggestRole)
        {
            SuggestRole = chip;   // finds again
            return;
        }
        if (Matches.Count > 0 || target is not null) _ = FindMatchesAsync();
    }

    partial void OnSelectedClipChanged(ClipRow? value) => SuggestFollow(false);

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
        RefreshGrooveSources();
        return to;
    }

    /// <summary>A library part as a clip: one go round of its bars (chord parts turned into smooth inversions when that's on).</summary>
    PlaygroundClip LibraryClip(MidiEntry entry, IReadOnlyList<NoteEvent> notes, long start, long? length = null)
    {
        var loop = Math.Max(1, PhraseAnalyser.BarsOf(notes)) * (long)Bar;
        var one = notes.Where(n => n.Start < loop).ToList();
        if (SmoothIncoming && entry.Role == MidiRoles.Chords) one = Inversions.Smooth(one, ChordCentre(null));
        return PlaygroundClip.Of(entry.Title.Length > 0 ? entry.Title : entry.SourceName, entry.Role, one, loop, start, length ?? loop,
                                 entry.Phrase.Source.Path, entry.Phrase.Hash);
    }

    /// <summary>A suggestion picked for bars of a clip: it takes their place, and the rest of the clip stays either side.</summary>
    internal void SwapIn(ClipRow target, (long Start, long End) bars, MidiEntry entry, IReadOnlyList<NoteEvent> notes)
    {
        if (notes.Count == 0) return;
        Remember();
        var was = target.Clip.Name;
        var clip = LibraryClip(entry, notes, bars.Start, bars.End - bars.Start);
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
                var clip = LibraryClip(m.Match.Entry, m.Match.Notes, drop.Start);
                PlaceClip(clip, drop.Row, clip.Name);
                Changed();
                Message = $"Added {clip.Name} from {m.Match.Entry.SourceName} at bar {drop.Start / Bar + 1}. Parts from {m.Match.Entry.SourceName} won't be suggested now.";
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

    /// <summary>Its chords turned over by hand, up or down: it stays like that until Fit again.</summary>
    internal void TurnClip(ClipRow row, int steps) => EditClip(row, c => { c.Inversion += steps; c.Pinned = true; });

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

    [ObservableProperty] bool _snapGrid = true;
    [ObservableProperty] bool _snapRelative = true;
    [ObservableProperty] bool _snapObjects = true;
    partial void OnSnapGridChanged(bool value) => SaveOption(SnapGridKey, value ? "1" : "0");
    partial void OnSnapRelativeChanged(bool value) => SaveOption(SnapRelativeKey, value ? "1" : "0");
    partial void OnSnapObjectsChanged(bool value) => SaveOption(SnapObjectsKey, value ? "1" : "0");

    [RelayCommand] void SelectClip(ClipPick? pick) => PickClip(pick is null ? null : Layers.FirstOrDefault(r => r.Layer == pick.Track), pick?.Clip);

    internal void EditClip(ClipRow row, Action<PlaygroundClip> change, bool harmony = true)
    {
        Remember();
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
            c.Notes = notes.Select(n => n with { Key = Math.Clamp(n.Key - c.Shift(0), 0, 127) }).ToList();
            c.Inversion = 0;   // the notes as shown are already turned
            c.Pinned = true;   // changed by hand: it stays as it is until Fit again
            row.Track.Layer.Written = "";
            row.Track.Layer.GrooveData = "";
        });

    /// <summary>Moved up or down by hand: it stays there instead of moving to fit, until Fit again.</summary>
    internal void MoveClip(ClipRow row, int semitones) => EditClip(row, c => { c.Transpose += semitones; c.Pinned = true; });

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
        var rows = Layers.Where(r => r.Layer.SongPart.Length > 0 && song.Contains(r.Layer.SongPart)
                                     && (was is null || was.Contains(r.Layer.SongPart) != off.Contains(r.Layer.SongPart))
                                     && r.Layer.Muted != off.Contains(r.Layer.SongPart)).ToList();
        if (rows.Count == 0) return;
        foreach (var r in rows)
        {
            r.Layer.Muted = off.Contains(r.Layer.SongPart);
            r.Changed();
        }
        Changed(harmony: true);
    }

    HashSet<string>? _songOff;

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
        foreach (var row in Layers) row.Changed();
        SelectedClip?.Changed();
    }


    /// <summary>A written part with the groove copied onto it put on again (after the chords changed and it was rewritten).</summary>
    static IReadOnlyList<NoteEvent> WithGroove(PlaygroundLayer layer, IReadOnlyList<NoteEvent> notes, IReadOnlyList<TimedChord> lane, long length) =>
        layer.GrooveData.Length == 0 ? notes : Groove.Copy(notes, layer.GrooveNotes, layer.GrooveDepth, length, lane, layer.GrooveAmount);

    // ---- copying a groove ----

    /// <summary>
    /// How much of the other part's groove to copy, one slider (Solomon, 2026-10-17: "make how much to copy a slider"):
    /// 1–4 its feel at 25%–100% (swing, push and pull, accents), 5 its rhythm too (your chords struck on its hits),
    /// 6 its whole playing (block chords played the way it plays its chords).
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(GrooveAmountText))] double _grooveAmount = 4;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(GrooveSourceText), nameof(CanCopyGroove))] LayerRow? _grooveSource;
    /// <summary>A part from the suggestions to take the groove from, instead of one of the section's own.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(GrooveSourceText), nameof(CanCopyGroove))] PlaygroundMatchRow? _grooveMatch;
    [ObservableProperty] bool _isGroovePlaying;
    [ObservableProperty] bool _smoothIncoming = true;

    public ObservableCollection<LayerRow> GrooveSources { get; } = [];

    (GrooveDepth Depth, double Amount) GrooveChoice => GrooveAmount switch
    {
        >= 5.5 => (GrooveDepth.Playing, 1),
        >= 4.5 => (GrooveDepth.Rhythm, 1),
        _ => (GrooveDepth.Feel, Math.Clamp(Math.Round(GrooveAmount) / 4, 0.25, 1)),
    };

    public string GrooveAmountText => GrooveChoice switch
    {
        (GrooveDepth.Playing, _) => "Its whole playing: your chords played the way it plays its chords, with its rhythm, voicing moves and touch.",
        (GrooveDepth.Rhythm, _) => "Its rhythm too: your chords, as they're voiced, struck when it strikes, as long and as hard.",
        (_, var a) => $"Its feel, {a * 100:0}%: your notes stay put but swing, push, pull and accent like it{(a < 1 ? ", part of the way" : "")}.",
    };

    IReadOnlyList<NoteEvent>? GrooveNotesSource => GrooveMatch?.Match.Notes ?? GrooveSource?.Layer.Notes;
    string GrooveSourceName => GrooveMatch?.Title ?? GrooveSource?.Name ?? "";

    public string GrooveSourceText => GrooveNotesSource is { } src
        ? $"{GrooveSourceName}: {(Groove.Of(src) is { } g ? g.Describe() : "too few notes to hear a groove")}"
        : "Pick the part to copy from.";

    public bool CanCopyGroove => GrooveNotesSource is { Count: > 2 } && SelectedLayer is not null;

    partial void OnSelectedLayerChanged(LayerRow? value)
    {
        // Picking a track picks its first clip, unless one of its clips is picked already.
        if (SelectedClip?.Track != value)
            SelectedClip = value is { Layer.Clips.Count: > 0 } t ? new ClipRow(this, t, t.Layer.Clips[0]) : null;
        RefreshGrooveSources();
        OnPropertyChanged(nameof(CanCopyGroove));
        OnPropertyChanged(nameof(GrooveTargetText));
        OnPropertyChanged(nameof(IsChordLayer));
    }

    public string GrooveTargetText => SelectedLayer is { } s ? $"Onto {s.Name}" : "Pick a part on the left to put the groove on.";
    public bool IsChordLayer => SelectedLayer?.Role == MidiRoles.Chords;

    void RefreshGrooveSources()
    {
        var keep = GrooveSource?.Layer;
        GrooveSources.Clear();
        foreach (var row in Layers.Where(r => r != SelectedLayer)) GrooveSources.Add(row);
        // A suggestion picked to copy from stays picked when you pick another part to put it on.
        if (GrooveMatch is not null) { GrooveSource = null; return; }
        GrooveSource = GrooveSources.FirstOrDefault(r => r.Layer == keep) ?? GrooveSources.FirstOrDefault(r => r.Role == MidiRoles.Drums)
                       ?? GrooveSources.FirstOrDefault();
    }

    partial void OnGrooveSourceChanged(LayerRow? value)
    {
        if (value is not null) GrooveMatch = null;
    }

    /// <summary>
    /// The picked part (its picked clip, when one is) with the groove on it, as it would be: a copy of the track, so
    /// nothing changes until it's kept.
    /// </summary>
    PlaygroundLayer? Grooved(LayerRow row)
    {
        if (GrooveNotesSource is not { Count: > 2 } src) return null;
        var (depth, amount) = GrooveChoice;
        var l = row.Layer;
        var copy = l.Copy();
        MapClips(copy, (played, length, heard) =>
        {
            // A written part is grooved from its plain writing, so copying again replaces the groove instead of piling on.
            var lane = row.IsWritten ? heard : row.Role == MidiRoles.Chords ? ChordLane.Detect(played.ToList(), length) : heard;
            var plain = row.IsWritten && lane.Count > 0 ? Written(l.Written, lane, length) : played;
            if (depth == GrooveDepth.Playing && lane.Count == 0) lane = heard;
            return Groove.Copy(plain, src, depth, length, lane, amount);
        }, like: l);
        return copy;
    }

    [RelayCommand]
    void CopyGroove()
    {
        if (SelectedLayer is not { } row || Grooved(row) is not { } grooved) return;
        if (grooved.Notes.Count == 0)
        {
            Message = "That gave no notes: the part to copy from needs chords for its whole playing to come across. Try less on the slider.";
            return;
        }
        var (depth, amount) = GrooveChoice;
        var from = GrooveSourceName;
        var src = GrooveNotesSource!;
        EditLayer(row, l =>
        {
            l.Clips = grooved.Clips;
            if (l.Written.Length > 0)
            {
                // Remembered, so it's put on again when the chords change.
                l.GrooveNotes = src;
                l.GrooveDepth = depth;
                l.GrooveAmount = amount;
            }
            l.GrooveFrom = from;
        });
        StopPlaying();
        Message = $"{row.Name} now has {(depth == GrooveDepth.Playing ? "the whole playing" : depth == GrooveDepth.Rhythm ? "the rhythm and feel" : "the feel")} of {from}. Undo takes it back.";
    }

    /// <summary>The section with the picked part grooved, to hear before keeping it.</summary>
    [RelayCommand]
    async Task PreviewGroove()
    {
        if (IsGroovePlaying) { StopPlaying(); return; }
        if (SelectedLayer is not { } row || Grooved(row) is not { } grooved) return;
        StopPlaying();
        var channels = PlaygroundSession.Audible(Section).Where(l => l != row.Layer).Select(l => Channel(l.Name, l.Program, l.Notes)).ToList();
        channels.Add(Channel(row.Name, row.Layer.Program, grooved.Notes));
        await PlayAsync(instead: channels);
        IsGroovePlaying = _playing;
    }

    /// <summary>A suggestion's groove, to copy onto the picked part (from the Groove button on a suggestion).</summary>
    internal void TakeGrooveFrom(PlaygroundMatchRow row)
    {
        GrooveSource = null;
        GrooveMatch = row;
        RightTab = GrooveTab;
        Message = $"Copying from {row.Title}. Pick how much on the Groove tab, then Copy the groove.";
    }

    /// <summary>The tab showing on the right: Suggest, Write, Groove, Reharm, Song.</summary>
    [ObservableProperty] int _rightTab;
    const int GrooveTab = 2;

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
        Ideas.Clear();
        if (IsQuick) return;   // the small playground shows no ideas, so it doesn't work them out (or read every chord part for next chords)
        var h = Harmony;
        if (h.Key is { } k && h.Lane.Count > 0)
            foreach (var r in Reharmoniser.Suggest(h.Lane, k.Tonic, k.Minor, 14)) Ideas.Add(new ReharmRow(this, r));
        _ = RefreshNextChordsAsync();
    }

    ProgressionModel? _model;
    string _modelFor = "";

    int _nextRequest;

    /// <summary>
    /// The page was opened: the MIDI is being read now, so the next-chord ideas can learn from it, and Suggest lists parts
    /// straight away (Solomon, 2026-10-19: "the list of midi didnt load initially", until he clicked another kind and back).
    /// </summary>
    public void Opened()
    {
        _ = RefreshNextChordsAsync(load: true);
        if (Matches.Count == 0 && !IsMatching) _ = FindMatchesAsync();
    }

    /// <summary>
    /// What usually comes next, learned from your chord parts. Only reads the MIDI when the page is open (<paramref name="load"/>),
    /// so building this page when the app starts doesn't read and index every MIDI part for people who never come here.
    /// </summary>
    async Task RefreshNextChordsAsync(bool load = false)
    {
        var request = ++_nextRequest;
        NextChords.Clear();
        var lane = Harmony.Lane;
        if (lane.Count == 0) { NextChordsText = ""; return; }
        var catalogue = load ? await _main.Midi.CatalogueAsync() : _main.Midi.LoadedCatalogue;
        if (catalogue is null || request != _nextRequest) return;
        var songs = Harmony.Songs;
        var key = string.Join("|", songs.OrderBy(s => s)) + ":" + catalogue.Entries.Count;
        if (_model is null || _modelFor != key)
        {
            var model = await UiWork.Run(() => ProgressionModel.Learn(catalogue.Entries, songs));
            if (request != _nextRequest) return;   // the chords changed while it learned; the newer call shows its own
            _model = model;
            _modelFor = key;
        }
        var last = lane[^1].Chord;
        ChordSymbol? before = lane.Count > 1 ? lane[^2].Chord : null;
        NextChords.Clear();
        foreach (var (chord, share) in _model.Next(last, before)) NextChords.Add(new NextChordRow(this, chord, share));
        NextChordsText = _model.Learned == 0 ? "" : $"After {last.Name}, in your {_model.Learned:N0} chord parts (other songs):";
    }

    internal void ApplyIdea(Reharm idea)
    {
        if (Changes(Harmony.Lane, idea.Lane) is not { } range) return;
        Remember();
        if (Rechord(Section, idea.Lane, range.From, range.To)) ShowSection();   // a new chord track to show
        Changed();
        Message = $"{idea.Title}: {idea.Before} → {idea.After}, played by the chord parts there. Undo takes it back.";
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

    /// <summary>The chord goes on the end as a new bar, played by the chord parts that reach the end.</summary>
    internal void AddNextChord(ChordSymbol chord)
    {
        if (Section.Bars >= MaxBars) { Message = $"The timeline is full ({MaxBars} bars)."; return; }
        Remember();
        var end = Section.Length;
        var lane = Harmony.Lane.Append(new TimedChord(end, Bar, chord)).ToList();
        SetBars(Section.Bars + 1);
        // Chord clips reaching the end go round into the new bar, so it's played with their rhythm.
        foreach (var l in Section.Layers.Where(l => l.Written.Length == 0))
            foreach (var c in l.Clips.Where(c => c.End == end && (c.Role == MidiRoles.Chords || c.Role == MidiRoles.Other && l.Role == MidiRoles.Chords)))
                c.Length += Bar;
        if (Rechord(Section, lane, end, end + Bar)) ShowSection();
        Changed();
        Message = $"{chord.Name} added as bar {Section.Bars}. Undo takes it back.";
    }

    /// <summary>
    /// New chords from <paramref name="from"/> to <paramref name="to"/>, played by the chord clips there (chords are heard,
    /// never typed: Solomon, 2026-10-19). Each one's notes in those bars are voiced again on the new chords, struck with
    /// the rhythm they had; a clip changed this way plays its notes as they are from
    /// then on. With no chord clip there, a plain chord clip is put over those bars.
    /// </summary>
    /// <returns>Whether it made a new chord track.</returns>
    static bool Rechord(PlaygroundSection timeline, IReadOnlyList<TimedChord> lane, long from, long to)
    {
        var clips = PlaygroundSession.Audible(timeline).Where(l => l.Written.Length == 0)
            .SelectMany(l => l.Clips.Where(c => (c.Role == MidiRoles.Chords || c.Role == MidiRoles.Other && l.Role == MidiRoles.Chords) && c.Start < to && c.End > from)
                                    .Select(c => (Layer: l, Clip: c))).ToList();
        if (clips.Count == 0)
        {
            var notes = Voicer.Plain(LaneAt(lane, from, to));
            var track = timeline.Layers.FirstOrDefault(l => l.Name == "Chords" && l.Written.Length == 0);
            var added = track is null;
            if (track is null) timeline.Layers.Add(track = new PlaygroundLayer { Name = "Chords", Role = MidiRoles.Chords, Program = GmSounds.DefaultFor(MidiRoles.Chords) });
            track.Clips.Add(PlaygroundClip.Of("Chords", MidiRoles.Chords, notes, to - from, from, to - from));
            track.Render();
            return added;
        }
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
            c.Pinned = true;
            layer.Render();
        }
        return false;
    }

    // ---- suggestions from the library ----

    public IReadOnlyList<RoleChip> RoleChips { get; }
    [ObservableProperty] RoleChip _suggestRole;
    [ObservableProperty] string _suggestSearch = "";
    public ObservableCollection<PlaygroundMatchRow> Matches { get; } = [];
    [ObservableProperty] bool _isMatching;
    [ObservableProperty] string _matchStatus = "Pick a kind of part and press Find to get parts from your library that fit this section.";
    CancellationTokenSource? _matchCts;

    partial void OnSuggestRoleChanged(RoleChip value) => _ = FindMatchesAsync();

    [RelayCommand]
    Task FindMatches() => FindMatchesAsync();

    public async Task FindMatchesAsync()
    {
        _matchCts?.Cancel();
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
            var found = await UiWork.Run(() => SectionMatcher.Rank(catalogue, harmony, role, _main.Midi.NotesOf, text, SuggestCount, cts.Token, feel));
            if (cts.IsCancellationRequested) return;
            Matches.Clear();
            foreach (var m in found) Matches.Add(new PlaygroundMatchRow(this, m));
            var left = harmony.Songs.Count == 0 ? "" : $" Left out: {string.Join(", ", harmony.Songs.Select(s => Path.GetFileName(s)))}, already in the idea.";
            var target = SwapTarget;
            MatchStatus = found.Count == 0 ? "Nothing found. Try another kind of part, or fewer words."
                : target is not null && SwapRange is { } bars
                    ? $"{found.Count} {MidiRoles.Plural(role).ToLowerInvariant()} for bars {bars.Start / Bar + 1}–{(bars.End + Bar - 1) / Bar} of {target.Clip.Name}, best first: Swap in puts one there instead." + left
                : (harmony.IsEmpty ? $"{found.Count} {MidiRoles.Plural(role).ToLowerInvariant()}, newest first: add a part or chords to rank them by fit."
                                   : $"{found.Count} {MidiRoles.Plural(role).ToLowerInvariant()} that fit {(Loop is null ? "the whole idea" : LoopText.ToLowerInvariant())}, best first, moved into its key.") + left;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_matchCts == cts) IsMatching = false;
        }
    }

    internal void AddMatch(PlaygroundMatchRow row)
    {
        if (SwapTarget is { } target && SwapRange is { } bars) SwapIn(target, bars, row.Match.Entry, row.Match.Notes);
        else AddFromLibrary(row.Match.Entry, row.Match.Notes);
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
    Task BringSong(ProjectItem? project) => project is null ? Task.CompletedTask : BringSongAsync(project.Path, project.Name);

    async Task<bool> BringSongAsync(string path, string songName, IReadOnlyCollection<string>? leaveOut = null)
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
        Remember();
        var at = IsEmpty ? 0 : Section.Length;
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

    // ---- loop points ----

    /// <summary>
    /// The highlight: bars dragged out on the timeline (or along the small playground's piano roll). Playing goes round
    /// them, suggestions fit just them, and new parts go there.
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasLoop), nameof(LoopText))] RollRange? _loop;
    public bool HasLoop => Loop is not null;
    partial void OnPositionChanged(double value) => SelectedClip?.PlayheadAt(value);
    partial void OnLoopChanged(RollRange? value) => Revision++;
    public string LoopText => Loop is { } r ? $"Bars {r.Start / Bar + 1}–{(r.End + Bar - 1) / Bar}" : "";

    [RelayCommand]
    void SetLoop(RollRange? range)
    {
        Loop = range is { } r && Math.Clamp(r.End, 1, Length) > Math.Clamp(r.Start, 0, Length)
            ? new RollRange(Math.Clamp(r.Start, 0, Length), Math.Clamp(r.End, 1, Length)) : null;
        if (_playing) _ = PlayAsync();
        SuggestFollow(true);
    }

    [RelayCommand]
    void ClearLoop() => SetLoop(null);

    /// <summary>The harmony suggestions are fitted to: the loop's bars when there are loop points, else the section.</summary>
    /// <summary>
    /// Swapping bars of a clip, the bars it covers, heard without its track (it's what's being replaced).
    /// </summary>
    SectionHarmony MatchHarmony => SwapTarget is { } t && SwapRange is { } bars
        ? SectionHarmony.Of(Section, _session.Key, _session.Style, [t.Track.Layer]).Slice(bars.Start, bars.End)
        : Loop is { } r ? Harmony.Slice(r.Start, r.End) : Harmony;

    // ---- listening ----

    [ObservableProperty] bool _isPlaying;
    [ObservableProperty] double _position = -1;
    /// <summary>
    /// The Loop button (Solomon, 2026-10-19): on, Play goes round the highlight (or the whole idea); off, it plays from
    /// where it starts to the end of the highlight (or the idea) and stops.
    /// </summary>
    [ObservableProperty] bool _loopOn = true;
    /// <summary>Where Play starts, set by clicking the bar numbers (inside the highlight, it starts there and goes round the highlight).</summary>
    [ObservableProperty] long _cursor;
    partial void OnCursorChanged(long value) { if (_playing) _ = PlayAsync(); }

    [RelayCommand]
    void SetCursor(long tick) => Cursor = Math.Clamp(tick, 0, Math.Max(0, Length - 1));

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

    /// <summary>The ? cheat sheet: open, and what's on it.</summary>
    [ObservableProperty] bool _cheatSheetOpen;
    public IReadOnlyList<ShortcutRow> CheatSheet => Shortcuts.For(ShortcutsViewModel.Playground);
    partial void OnCheatSheetOpenChanged(bool value) { if (value) OnPropertyChanged(nameof(CheatSheet)); }
    [RelayCommand] void CloseCheatSheet() => CheatSheetOpen = false;

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
            case "playground.help": CheatSheetOpen = !CheatSheetOpen; return true;
            case "playground.zoomIn": Zoom(0.5); return true;
            case "playground.zoomOut": Zoom(2); return true;
            case "playground.zoomFit": ZoomToFit(); return true;
        }
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
        if (_playing) { StopPlaying(); return; }
        if (IsQuick) _main.Midi.StopForQuick();   // a small playground and the MIDI page never play over each other
        await PlayAsync();
    }

    /// <summary>The section (or the arrangement) going round, every part that plays, with its sound.</summary>
    async Task PlayAsync(bool keepPlace = false, IReadOnlyList<MidiChannel>? instead = null)
    {
        var request = ++_request;
        List<MidiChannel> channels;
        if (instead is null) channels = ChannelsToPlay(out _playLength);
        else
        {
            _playLength = Section.Length;
            channels = Looped(instead.ToList(), ref _playLength);
        }
        if (channels.Count == 0 || channels.All(c => c.Notes.Count == 0))
        {
            StopPlaying();
            Message = "Nothing to play yet.";
            return;
        }
        var tempo = _session.Tempo;
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
            return;
        }
        if (request != _request) return;
        if (_player is null)
        {
            _player = Ui.Current.CreateAudioPlayer();
            _player.Stopped += () => { if (_playing && _player?.Playing is null) GoRound(); };
        }
        _clock ??= Ui.Current.CreateTimer(TimeSpan.FromMilliseconds(30), Tick);
        var start = 0.0;
        if (keepPlace && _player.Playing == _playKey && _player.Position is { } at)
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
            return;
        }
        _playing = true;
        IsPlaying = true;
        _clock.Start();
    }

    List<MidiChannel> ChannelsToPlay(out long length)
    {
        length = Section.Length;
        var channels = PlaygroundSession.Audible(Section).Select(l => Channel(l.Name, l.Program, l.Notes)).ToList();
        return Looped(channels, ref length);
    }

    /// <summary>Just the loop's bars, from its start, when there are loop points.</summary>
    List<MidiChannel> Looped(List<MidiChannel> channels, ref long length)
    {
        if (Loop is not { } r) return channels;
        length = r.End - r.Start;
        // Chords pushed ahead of the loop's first bar, or tied into it, still sound from its start.
        return channels.Select(c => c with { Notes = MidiBrowserViewModel.Cut(c.Notes, r.Start, r.End) }).ToList();
    }

    static MidiChannel Channel(string name, int? program, IReadOnlyList<NoteEvent> notes) =>
        new(program == GmSounds.DrumKit ? $"Drums · {name}" : name, "", Ppq, notes) { GmProgram = program };

    void GoRound()
    {
        if (!_playing || _audio is null || _player is null) return;
        if (!LoopOn) { StopPlaying(); return; }
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
        foreach (var m in Matches) m.IsPlaying = false;
        foreach (var r in Ideas) r.IsPlaying = false;
        IsGroovePlaying = false;
    }

    /// <summary>The idea with a suggestion playing on top where it would go (the highlight, or from bar 1), to hear it before adding it.</summary>
    internal async Task PreviewMatch(PlaygroundMatchRow row)
    {
        if (row.IsPlaying) { StopPlaying(); return; }
        StopPlaying();
        if (SwapTarget is { } target && SwapRange is { } bars)
        {
            // Heard as Swap in would leave it: those bars of the clip played by the suggestion instead.
            var tried = PlaygroundSession.FromJson(_session.ToJson())!.Timeline;
            var layer = tried.Layers[Section.Layers.IndexOf(target.Track.Layer)];
            CutOut(layer, bars.Start, bars.End);
            layer.Clips.Add(LibraryClip(row.Match.Entry, row.Match.Notes, bars.Start, bars.End - bars.Start));
            layer.Render();
            await PlayAsync(instead: ChannelsOf(tried).ToList());
            row.IsPlaying = _playing;
            return;
        }
        var channels = ChannelsOf(Section).ToList();
        var notes = row.Match.Notes;
        var period = Math.Max(1, PhraseAnalyser.BarsOf(notes)) * (long)Bar;
        var (start, length) = Place;
        channels.Add(Channel(row.Title, GmSounds.DefaultFor(row.Match.Entry.Role),
                             SectionMatcher.Loop(notes, period, length).Select(n => n with { Start = n.Start + start }).ToList()));
        await PlayAsync(instead: channels);
        row.IsPlaying = _playing;
    }

    /// <summary>The idea with a reharmonisation in place, as Apply would leave it: the chord parts play the new chords, and parts written from the chords follow.</summary>
    internal async Task PreviewIdea(ReharmRow row)
    {
        if (row.IsPlaying) { StopPlaying(); return; }
        StopPlaying();
        var tried = PlaygroundSession.FromJson(_session.ToJson())!.Timeline;
        if (Changes(Harmony.Lane, row.Idea.Lane) is { } range) Rechord(tried, row.Idea.Lane, range.From, range.To);
        // What plays then (soloed parts only, when any are), with the written parts playing the idea's chords.
        var channels = new List<MidiChannel>();
        foreach (var l in PlaygroundSession.Audible(tried))
        {
            if (l.Written.Length == 0) { channels.Add(Channel(l.Name, l.Program, l.Notes)); continue; }
            var notes = l.Clips.SelectMany(c =>
            {
                var under = LaneAt(row.Idea.Lane, c.Start, c.End);
                return WithGroove(l, Written(l.Written, under, c.Length), under, c.Length).Select(n => n with { Start = n.Start + c.Start });
            }).ToList();
            channels.Add(Channel(l.Name, l.Program, notes));
        }
        await PlayAsync(instead: channels);
        row.IsPlaying = _playing;
    }

    IEnumerable<MidiChannel> ChannelsOf(PlaygroundSection section) =>
        PlaygroundSession.Audible(section).Select(l => Channel(l.Name, l.Program, l.Notes));

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

    public void Left() => StopPlaying();

    public void Dispose()
    {
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
            _owner.EditLayer(this, l => l.Name = value.Trim(), harmony: false);
        }
    }

    public string Role => Layer.Role;
    public string RoleLabel => MidiRoles.Plural(Layer.Role) switch { "Melodies" => "Melody", "Basslines" => "Bassline", "Arps" => "Arp", var p => p };
    public bool IsWritten => Layer.Written.Length > 0;
    public string From => Layer.SourcePath.Length > 0 ? Path.GetFileNameWithoutExtension(Layer.SourcePath)
        : IsWritten ? "Written from the chords" : "Your own";
    public string Details => $"{RoleLabel} · {From}{(Layer.GrooveFrom.Length > 0 ? $" · groove of {Layer.GrooveFrom}" : "")}";
    public bool Muted => Layer.Muted;
    public bool Solo => Layer.Solo;

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

/// <summary>
/// A clip dragged on the timeline: onto track <paramref name="Row"/> (one past the last makes a new track), starting at
/// <paramref name="Start"/>, playing for <paramref name="Length"/>, from <paramref name="Offset"/> into its go round.
/// </summary>
/// <summary>
/// A part let go on the timeline: a suggestion (<see cref="PlaygroundMatchRow"/>) or .mid file paths, on track
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

    /// <summary>Where its first go round would start on the timeline.</summary>
    long At => clip.RepeatAt(0);

    /// <summary>One go round, as it first plays (moved by hand and to fit).</summary>
    public IReadOnlyList<NoteEvent> Notes => Inversions.Turn(clip.Notes, clip.Inversion).Select(n => n with { Key = Math.Clamp(n.Key + clip.Shift(0), 0, 127) }).ToList();
    public long Loop => clip.Loop;
    public bool IsChords => clip.Role == MidiRoles.Chords;

    /// <summary>What shift to fit is doing with it, to show beside its tools.</summary>
    public string FitText => track.IsWritten ? "Written from the chords: follows them"
        : clip.Role is MidiRoles.Chords or MidiRoles.Drums || track.Role is MidiRoles.Chords or MidiRoles.Drums ? ""
        : clip.Pinned ? "Set by hand: stays as it is"
        : clip.Fits.All(f => f == 0) ? "Fits where it is"
        : "Moved to fit: " + string.Join(" ", clip.Fits.Select(f => f == 0 ? "0" : f > 0 ? $"+{f}" : $"{f}"));
    public bool CanFitAgain => clip.Pinned && clip.Role != MidiRoles.Drums && track.Role != MidiRoles.Drums;

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

    /// <summary>Which notes fight the chords under them, judged as <see cref="LayerRow.Clashes"/> does.</summary>
    public Func<NoteEvent, bool> Clashes
    {
        get
        {
            var h = owner.Harmony;
            var style = h.Style;
            var at = At;
            if (clip.Role == MidiRoles.Drums || track.Role == MidiRoles.Drums || h.Lane.Count == 0) return _ => false;
            return n => ChordLane.Fights(h.Lane, at + n.Start, n.Length, n.Key % 12, style, h.Length);
        }
    }

    /// <summary>What each note is over the chord heard under it (3, ♭7, 9), for the piano roll's note labels.</summary>
    public Func<NoteEvent, string?> DegreeOf
    {
        get
        {
            var h = owner.Harmony;
            var at = At;
            if (clip.Role == MidiRoles.Drums || track.Role == MidiRoles.Drums || h.Lane.Count == 0) return _ => null;
            return n => ChordLane.At(h.Lane, at + n.Start + 1, h.Length) is { } chord ? ChordScales.Degree(chord, n.Key % 12) : null;
        }
    }

    /// <summary>The playhead on the piano roll, while it's in the go round shown.</summary>
    [ObservableProperty] double _position = -1;
    internal void PlayheadAt(double tick) => Position = tick < 0 ? -1 : tick >= At && tick < At + clip.Loop ? tick - At : -1;

    internal void Changed()
    {
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(Loop));
        OnPropertyChanged(nameof(FitText));
        OnPropertyChanged(nameof(CanFitAgain));
        GuideChanged();
    }

    internal void GuideChanged()
    {
        OnPropertyChanged(nameof(Guide));
        OnPropertyChanged(nameof(Clashes));
        OnPropertyChanged(nameof(DegreeOf));
    }

    [RelayCommand] void Remove() => owner.RemoveClip(this);
    [RelayCommand] void Duplicate() => owner.DuplicateClip(this);
    [RelayCommand] void OctaveUp() => owner.MoveClip(this, 12);
    [RelayCommand] void OctaveDown() => owner.MoveClip(this, -12);
    [RelayCommand] void Up() => owner.MoveClip(this, 1);
    [RelayCommand] void Down() => owner.MoveClip(this, -1);
    [RelayCommand] void TurnUp() => owner.TurnClip(this, 1);
    [RelayCommand] void TurnDown() => owner.TurnClip(this, -1);
    [RelayCommand] void FitAgain() => owner.FitAgain(this);
    [RelayCommand] void Edited(IReadOnlyList<NoteEvent>? notes) { if (notes is not null) owner.SetClipNotes(this, notes); }
}

/// <summary>One chord on the chord lane: its name, Roman numeral and what it's doing, as wide as it lasts.</summary>
public sealed class ChordCell(TimedChord chord, ChordFunction? function, long length)
{
    public string Name => chord.Chord.Name;
    public string Roman => function?.Roman ?? "";
    public string Label => function?.Label ?? "";
    /// <summary>T, S, D or blank: colours the cell.</summary>
    public string Kind => function is { Kind: not ' ' } f ? f.Kind.ToString() : "";
    public double Start => chord.Start / (double)Math.Max(1, length);
    public double Share => chord.Length / (double)Math.Max(1, length);
    public string Tip => $"{Name}{(Roman.Length > 0 ? $"  ({Roman})" : "")}{(Label.Length > 0 ? $": {Label}" : "")}";
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
    public string Chords => string.Join(" ", match.Entry.Chords.Take(6).Select(c => c.Symbol.Transposed(match.Fit?.Semitones ?? 0).Name));
    public MidiChannel Channel => new(Title, "", PhraseAnalyser.Ppq, match.Notes);
    [ObservableProperty] bool _isPlaying;
    [RelayCommand] void Add() => owner.AddMatch(this);
    /// <summary>What Add does: a new track, or (with a highlight in a clip) swapping those bars of it.</summary>
    public string AddText { get; } = owner.SwapTarget is null ? "Add" : "Swap in";
    [RelayCommand] Task Preview() => owner.PreviewMatch(this);
    /// <summary>Copy its groove (up to its whole playing) onto the part picked in the section.</summary>
    [RelayCommand] void TakeGroove() => owner.TakeGrooveFrom(this);
}

/// <summary>A reharmonisation idea, to hear and keep.</summary>
public sealed partial class ReharmRow(PlaygroundViewModel owner, Reharm idea) : ObservableObject
{
    public Reharm Idea => idea;
    public string Title => idea.Title;
    public string Change => $"Bar {idea.Bar}: {idea.Before} → {idea.After}";
    public string Why => idea.Why;
    [ObservableProperty] bool _isPlaying;
    [RelayCommand] void Apply() => owner.ApplyIdea(idea);
    [RelayCommand] Task Preview() => owner.PreviewIdea(this);
}

/// <summary>A chord your library often plays next.</summary>
public sealed partial class NextChordRow(PlaygroundViewModel owner, ChordSymbol chord, double share)
{
    public string Name => chord.Name;
    public string ShareText => $"{share * 100:0}%";
    [RelayCommand] void Add() => owner.AddNextChord(chord);
}
