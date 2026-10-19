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
/// jazz and gospel harmony ... drag the midi into my daw when I'm happy"). An idea has sections (A, B…), each a stack of
/// parts on top of each other, with a chord lane heard from the parts or typed as a chart. Parts come from your MIDI
/// library (suggested to fit the section, never from a song already in it), from a stretch of one of your songs, or
/// are written from the chords (voicings, basslines, guide-tone lines). Every part can be edited on the piano roll
/// with the rest greyed behind it, and the part, the section or the whole arrangement can be dragged into the DAW.
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

    // ---- the idea and its sections ----

    public ObservableCollection<SectionTab> Sections { get; } = [];
    public ObservableCollection<LayerRow> Layers { get; } = [];
    public ObservableCollection<ChordCell> Chords { get; } = [];

    PlaygroundSection Section => _session.Sections[Math.Clamp(_session.Current, 0, _session.Sections.Count - 1)];
    internal PlaygroundSection CurrentSection => Section;
    public string SectionName => Section.Name;

    [ObservableProperty] string _ideaName = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMessage))] string _message = "";
    public bool HasMessage => Message.Length > 0;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelectedLayer))] LayerRow? _selectedLayer;
    public bool HasSelectedLayer => SelectedLayer is not null;
    [ObservableProperty] SectionTab? _selectedSection;
    [ObservableProperty] string _chartText = "";
    [ObservableProperty] string _keyText = "";
    [ObservableProperty] string _keySummary = "";
    [ObservableProperty] string _chordSummary = "";
    [ObservableProperty] int _bars = 4;
    [ObservableProperty] double _tempo = 90;
    [ObservableProperty] string _arrangement = "";
    [ObservableProperty] bool _isClassical;
    [ObservableProperty] bool _canUndo;
    [ObservableProperty] bool _canRedo;

    /// <summary>The section as one length, in ticks, for the rolls.</summary>
    public long Length => Section.Length;
    public bool IsEmpty => Layers.Count == 0;
    public string Title => _session.Name;
    public bool ChordsTyped => Section.Chart.Trim().Length > 0;
    public string ChartHint => "Type chords like Dm9 G13 | Cmaj9 | %   (bars split by |)";

    SectionHarmony? _harmony;
    /// <summary>The current section's harmony, worked out again after each change.</summary>
    public SectionHarmony Harmony => _harmony ??= SectionHarmony.Of(Section, _session.Key, _session.Style);

    bool _showing;

    /// <summary>Everything on the page from the idea, after loading it or switching section.</summary>
    void Show()
    {
        _showing = true;
        IdeaName = _session.Name;
        Tempo = _session.Tempo;
        KeyText = _session.Key;
        Arrangement = _session.Arrangement;
        IsClassical = _session.Style == HarmonyStyle.Classical;
        Sections.Clear();
        for (var i = 0; i < _session.Sections.Count; i++) Sections.Add(new SectionTab(this, _session.Sections[i], i));
        SelectedSection = Sections[Math.Clamp(_session.Current, 0, Sections.Count - 1)];
        ShowSection();
        _showing = false;
    }

    void ShowSection()
    {
        var was = _showing;
        _showing = true;
        var keep = SelectedLayer is { } sel ? Section.Layers.IndexOf(sel.Layer) : -1;
        Layers.Clear();
        foreach (var l in Section.Layers) Layers.Add(new LayerRow(this, l));
        SelectedLayer = keep >= 0 && keep < Layers.Count ? Layers[keep] : Layers.FirstOrDefault();
        foreach (var s in Sections) s.Refresh();
        Bars = Section.Bars;
        ChartText = Section.Chart;
        FeelPick = Section.Feel.ToString();
        _showing = was;
        HarmonyChanged();
        RefreshGrooveSources();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Length));
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
        ChordSummary = h.Lane.Count == 0 ? "No chords yet: add a part, or type them"
            : h.ChordsTyped ? "Chords as typed" : "Chords heard from the parts";
        OnPropertyChanged(nameof(ChordsTyped));
        foreach (var row in Layers) row.GuideChanged();
        RefreshIdeas();
        RefreshEnergy();
    }

    // ---- options (Settings › Playground) ----

    public const string SuggestCountKey = "playground_suggestions", GrowBarsKey = "playground_grow_bars", SmoothKey = "playground_smooth",
                        NewTempoKey = "playground_new_tempo", NewBarsKey = "playground_new_bars", NewStyleKey = "playground_new_style",
                        UndoStepsKey = "playground_undo_steps";
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

    /// <summary>A new, empty idea, started the way Settings › Playground says.</summary>
    PlaygroundSession Fresh() => new()
    {
        Tempo = NewTempo,
        Style = NewStyle == Classical ? HarmonyStyle.Classical : HarmonyStyle.JazzGospel,
        Sections = [new PlaygroundSection { Bars = NewBars }],
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
        // Parts written from the chords follow when an edit changed the chords heard (or the length grew), not only the chart.
        if (harmony && Section.Layers.Any(l => l.Written.Length > 0) && _writtenFor != (Section, ChordLane.ToChart(LaneForWriting(), Section.Length)))
            RewriteWritten();
        try { _store.SaveCurrent(_session); }
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

    partial void OnArrangementChanged(string value)
    {
        if (_showing) return;
        _session.Arrangement = value;
        Changed(harmony: false);
    }

    partial void OnBarsChanged(int value)
    {
        if (_showing || value == Section.Bars) return;
        Remember();
        Section.Bars = Math.Clamp(value, 1, 64);
        if (Loop is { } loop) SetLoop(loop);   // kept inside the section, or dropped if it's now past the end
        RewriteWritten();
        OnPropertyChanged(nameof(Length));
        foreach (var row in Layers) row.LengthChanged();
        Changed();
    }

    /// <summary>A typed chart: the chords as written, replacing what's heard. Parts written from the chords follow.</summary>
    partial void OnChartTextChanged(string value)
    {
        if (_showing || value == Section.Chart) return;
        if (value.Trim().Length > 0 && ChordLane.ParseChart(value, out var why) is not { } lane)
        {
            Message = why;
            return;
        }
        Message = "";
        SetChart(value.Trim());
    }

    void SetChart(string chart)
    {
        Remember();
        Section.Chart = chart;
        if (chart.Length > 0 && ChordLane.ParseChart(chart, out _) is { } lane)
        {
            // Room for the whole chart.
            var bars = (int)Math.Ceiling(lane[^1].End / (double)Bar);
            if (bars > Section.Bars) Section.Bars = Math.Min(64, bars);
        }
        _showing = true;
        Bars = Section.Bars;
        ChartText = chart;
        _showing = false;
        _harmony = null;
        RewriteWritten();
        Changed();
        foreach (var row in Layers) row.LengthChanged();
    }

    /// <summary>Back to hearing the chords from the parts.</summary>
    [RelayCommand]
    void HearChords() => SetChart("");

    /// <summary>The chords heard from the parts, written into the chart box to edit.</summary>
    [RelayCommand]
    void TypeHeardChords()
    {
        if (Harmony.Lane.Count == 0) return;
        SetChart(ChordLane.ToChart(Harmony.Lane, Harmony.Length));
    }

    // ---- sections ----

    internal void PickSection(int index)
    {
        if (index == _session.Current || index < 0 || index >= _session.Sections.Count) return;
        StopPlaying();
        _session.Current = index;
        Loop = null;   // loop points belong to the section they were set on
        SelectedLayer = null;
        ShowSection();
        Changed(harmony: false);
    }

    partial void OnSelectedSectionChanged(SectionTab? value)
    {
        if (_showing || value is null) return;
        PickSection(value.Index);
    }

    [RelayCommand]
    void AddSection()
    {
        Remember();
        var name = NextSectionName();
        _session.Sections.Add(new PlaygroundSection { Name = name, Bars = Section.Bars, Chart = "" });
        _session.Current = _session.Sections.Count - 1;
        Show();
        Changed(harmony: false);
        Message = $"Section {name} added. Bring parts in, or type its chords.";
    }

    [RelayCommand]
    void DuplicateSection()
    {
        Remember();
        var copy = new PlaygroundSection
        {
            Name = NextSectionName(), Bars = Section.Bars, Chart = Section.Chart, Feel = Section.Feel,
            Layers = Section.Layers.Select(l => l.Copy()).ToList(),
        };
        _session.Sections.Insert(_session.Current + 1, copy);
        _session.Current++;
        Show();
        Changed(harmony: false);
    }

    [RelayCommand]
    async Task RemoveSection()
    {
        if (_session.Sections.Count <= 1) return;
        if (Section.Layers.Count > 0 && !await Ui.Current.Confirm($"Take section {Section.Name} and its {Section.Layers.Count} parts out of the idea? Undo brings it back.", "Remove section"))
            return;
        Remember();
        StopPlaying();
        _session.Sections.RemoveAt(_session.Current);
        _session.Current = Math.Clamp(_session.Current - 1, 0, _session.Sections.Count - 1);
        Show();
        Changed(harmony: false);
    }

    string NextSectionName()
    {
        for (var c = 'A'; c <= 'Z'; c++)
            if (_session.Sections.All(s => s.Name != c.ToString())) return c.ToString();
        return "Section " + (_session.Sections.Count + 1);
    }

    internal void RenameSection(PlaygroundSection section, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == section.Name) return;
        Remember();
        // The arrangement keeps pointing at it.
        var order = _session.Arrangement.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(n => n == section.Name ? name : n);
        section.Name = name;
        _session.Arrangement = string.Join(' ', order);
        _showing = true;
        Arrangement = _session.Arrangement;
        _showing = false;
        Changed(harmony: false);
    }

    // ---- parts ----

    /// <summary>Adds a part to the current section, picked, and makes room for it.</summary>
    PlaygroundLayer AddLayer(string name, string role, IReadOnlyList<NoteEvent> notes, string sourcePath = "", string hash = "", string written = "")
    {
        var layer = new PlaygroundLayer
        {
            Name = UniqueName(name), Role = role, Notes = notes, Program = GmSounds.DefaultFor(role),
            SourcePath = sourcePath, SourceHash = hash, Written = written,
        };
        Section.Layers.Add(layer);
        var row = new LayerRow(this, layer);
        Layers.Add(row);
        SelectedLayer = row;
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

    /// <summary>A part from the MIDI page or the suggestions, as it plays there (moved, octave and all).</summary>
    public void AddFromLibrary(MidiEntry entry, IReadOnlyList<NoteEvent> notes, string? title = null)
    {
        if (notes.Count == 0) return;
        Remember();
        var name = title ?? (entry.Title.Length > 0 ? entry.Title : entry.SourceName);
        // A part longer than the section grows it (up to 16 bars), so nothing is cut off.
        var bars = PhraseAnalyser.BarsOf(notes);
        if (Section.Layers.Count == 0 && Section.Chart.Length == 0) Section.Bars = Math.Clamp(bars, 1, GrowBars);
        else if (bars > Section.Bars) Section.Bars = Math.Max(Section.Bars, Math.Min(GrowBars, bars));
        var looped = SectionMatcher.Loop(notes, Math.Max(1, bars) * (long)Bar, Section.Length);
        // Chord parts come in turned into their smoothest inversions, sitting with the chords already here.
        var smoothed = SmoothIncoming && entry.Role == MidiRoles.Chords ? Inversions.Smooth(looped, ChordCentre(null)) : looped;
        var turned = !smoothed.SequenceEqual(looped);
        AddLayer(name, entry.Role, smoothed, entry.Phrase.Source.Path, entry.Phrase.Hash);
        _showing = true;
        Bars = Section.Bars;
        _showing = false;
        foreach (var row in Layers) row.LengthChanged();
        Changed();
        Message = $"Added {name} from {entry.SourceName}{(turned ? ", its chords turned into smooth inversions" : "")}. Parts from {entry.SourceName} won't be suggested for this section now.";
    }

    internal void RemoveLayer(LayerRow row)
    {
        Remember();
        Section.Layers.Remove(row.Layer);
        var i = Layers.IndexOf(row);
        Layers.Remove(row);
        if (SelectedLayer == row) SelectedLayer = Layers.Count == 0 ? null : Layers[Math.Clamp(i, 0, Layers.Count - 1)];
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
        change(row.Layer);
        row.Changed();
        Changed(harmony);
    }

    /// <summary>Notes changed by hand on the piano roll: the part is no longer rewritten from the chords.</summary>
    internal void SetNotes(LayerRow row, IReadOnlyList<NoteEvent> notes) =>
        EditLayer(row, l => { l.Notes = notes; l.Written = ""; l.GrooveData = ""; });

    internal void MoveLayer(LayerRow row, int semitones) =>
        EditLayer(row, l => l.Notes = l.Notes.Select(n => n with { Key = Math.Clamp(n.Key + semitones, 0, 127) }).ToList());

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
    /// The chords for writing parts to: the typed chart, or the chords heard from the parts that weren't written from
    /// them (so rewriting a written part doesn't feed on itself).
    /// </summary>
    IReadOnlyList<TimedChord> LaneForWriting()
    {
        if (ChordsTyped) return Harmony.Lane;
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
            Message = "There are no chords to write from yet: type some in the chord box, or bring in a chord part.";
            return;
        }
        Remember();
        _writtenFor = (Section, ChordLane.ToChart(lane, Section.Length));
        var layer = AddLayer(name, role, Written(how, lane, Section.Length), written: how);
        layer.Program = role == MidiRoles.Melody ? 73 : layer.Program;
        Changed();
        Message = $"Wrote {name} from the chords. Change the chords and it follows; edit its notes and it's yours.";
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

    /// <summary>The section and chords (as a chart) its written parts were last written from.</summary>
    (PlaygroundSection, string)? _writtenFor;

    /// <summary>Parts written from the chords are written again when the chords or the length change.</summary>
    void RewriteWritten()
    {
        if (Section.Layers.All(l => l.Written.Length == 0)) return;
        _harmony = null;
        var lane = LaneForWriting();
        _writtenFor = (Section, ChordLane.ToChart(lane, Section.Length));
        foreach (var l in Section.Layers.Where(l => l.Written.Length > 0))
            l.Notes = lane.Count == 0 ? [] : WithGroove(l, Written(l.Written, lane, Section.Length), lane, Section.Length);
        foreach (var row in Layers) row.Changed();
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

    /// <summary>The picked part with the groove on it, as it would be.</summary>
    IReadOnlyList<NoteEvent>? Grooved(LayerRow row)
    {
        if (GrooveNotesSource is not { Count: > 2 } src) return null;
        var (depth, amount) = GrooveChoice;
        var l = row.Layer;
        // A written part is grooved from its plain writing, so copying again replaces the groove instead of piling on.
        var lane = row.IsWritten ? LaneForWriting() : row.Role == MidiRoles.Chords ? ChordLane.Detect(l.Notes, Section.Length) : Harmony.Lane;
        var plain = row.IsWritten && lane.Count > 0 ? Written(l.Written, lane, Section.Length) : l.Notes;
        if (depth == GrooveDepth.Playing && lane.Count == 0) lane = Harmony.Lane;
        return Groove.Copy(plain, src, depth, Section.Length, lane, amount);
    }

    [RelayCommand]
    void CopyGroove()
    {
        if (SelectedLayer is not { } row || Grooved(row) is not { } notes) return;
        if (notes.Count == 0)
        {
            Message = "That gave no notes: the part to copy from needs chords for its whole playing to come across. Try less on the slider.";
            return;
        }
        var (depth, amount) = GrooveChoice;
        var from = GrooveSourceName;
        var src = GrooveNotesSource!;
        EditLayer(row, l =>
        {
            l.Notes = notes;
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
        if (SelectedLayer is not { } row || Grooved(row) is not { } notes) return;
        StopPlaying();
        var channels = PlaygroundSession.Audible(Section).Where(l => l != row.Layer).Select(l => Channel(l.Name, l.Program, l.Notes)).ToList();
        channels.Add(Channel(row.Name, row.Layer.Program, notes));
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
        var smooth = Inversions.Smooth(row.Layer.Notes, ChordCentre(row.Layer));
        if (smooth.SequenceEqual(row.Layer.Notes))
        {
            Message = $"{row.Name}'s chords already move as smoothly as they can.";
            return;
        }
        EditLayer(row, l => { l.Notes = smooth; l.Written = ""; });
        Message = $"{row.Name}'s chords are turned into the inversions that move least. Undo takes it back.";
    }

    // ---- energy ----

    public static IReadOnlyList<string> FeelChoices { get; } = ["Any", "Verse", "Build", "Drop", "Breakdown"];
    [ObservableProperty] string _feelPick = "Any";
    [ObservableProperty] double[] _energyCurve = [];
    [ObservableProperty] double[]? _energyTarget;
    [ObservableProperty] string _energyText = "";

    partial void OnFeelPickChanged(string value)
    {
        if (_showing) return;
        var feel = Enum.TryParse<SectionFeel>(value, out var f) ? f : SectionFeel.Any;
        if (feel == Section.Feel) return;
        Remember();
        Section.Feel = feel;
        Changed(harmony: false);
        if (Matches.Count > 0) _ = FindMatchesAsync();
    }

    /// <summary>The section's tension curve, bar by bar, and the one its feel asks for.</summary>
    void RefreshEnergy()
    {
        var h = Harmony;
        var bars = Section.Bars;
        EnergyCurve = h.Notes.Count == 0 && Section.Layers.All(l => l.Role != MidiRoles.Drums)
            ? []
            : Energy.Curve(PlaygroundSession.Audible(Section).SelectMany(l => l.Notes).ToList(), Section.Length, "", h.Key);
        EnergyTarget = Section.Feel == SectionFeel.Any ? null : Energy.Target(Section.Feel, bars);
        EnergyText = EnergyCurve.Length == 0 ? "" :
            Section.Feel == SectionFeel.Any ? "Energy, bar by bar" : $"Energy, bar by bar · dashed: a {Energy.Name(Section.Feel)}";
    }

    // ---- reharmonisation and next-chord ideas ----

    public ObservableCollection<ReharmRow> Ideas { get; } = [];
    public ObservableCollection<NextChordRow> NextChords { get; } = [];
    [ObservableProperty] string _nextChordsText = "";

    void RefreshIdeas()
    {
        Ideas.Clear();
        var h = Harmony;
        if (h.Key is { } k && h.Lane.Count > 0)
            foreach (var r in Reharmoniser.Suggest(h.Lane, k.Tonic, k.Minor, 14)) Ideas.Add(new ReharmRow(this, r));
        _ = RefreshNextChordsAsync();
    }

    ProgressionModel? _model;
    string _modelFor = "";

    int _nextRequest;

    /// <summary>The page was opened: the MIDI is being read now, so the next-chord ideas can learn from it.</summary>
    public void Opened() => _ = RefreshNextChordsAsync(load: true);

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
        SetChart(ChordLane.ToChart(idea.Lane, Section.Length));
        Message = $"{idea.Title}: {idea.Before} → {idea.After}. Undo takes it back.";
    }

    internal void AddNextChord(ChordSymbol chord)
    {
        // The chord goes on the end of the chart as a new bar.
        // Written out bar by bar first, so a chart typed without bar lines ("Dm7 G7") keeps its chords in their own bars.
        var lane = Harmony.Lane;
        var chart = lane.Count == 0 ? "" : ChordLane.ToChart(lane, ChordsTyped ? lane[^1].End : Harmony.Length);
        SetChart((chart.Length == 0 ? "" : chart.TrimEnd(' ', '|') + " | ") + chord.Name + " |");
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
            var feel = Section.Feel;
            var found = await UiWork.Run(() => SectionMatcher.Rank(catalogue, harmony, role, _main.Midi.NotesOf, text, SuggestCount, cts.Token, feel));
            if (cts.IsCancellationRequested) return;
            Matches.Clear();
            foreach (var m in found) Matches.Add(new PlaygroundMatchRow(this, m));
            var left = harmony.Songs.Count == 0 ? "" : $" Left out: {string.Join(", ", harmony.Songs.Select(s => Path.GetFileName(s)))}, already in this section.";
            MatchStatus = found.Count == 0 ? "Nothing found. Try another kind of part, or fewer words."
                : (harmony.IsEmpty ? $"{found.Count} {MidiRoles.Plural(role).ToLowerInvariant()}, newest first: add a part or chords to rank them by fit."
                                   : $"{found.Count} {MidiRoles.Plural(role).ToLowerInvariant()} that fit {(Loop is null ? "the section" : LoopText.ToLowerInvariant())}, best first, moved into its key.") + left;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_matchCts == cts) IsMatching = false;
        }
    }

    internal void AddMatch(PlaygroundMatchRow row) => AddFromLibrary(row.Match.Entry, row.Match.Notes);

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
    /// Brings in every pitched and drum part of a song from a bar, as long as the section, each as a part of its own:
    /// a section of a song to build on. Its other parts are then never suggested for this section.
    /// </summary>
    [RelayCommand]
    Task BringSong(ProjectItem? project) => project is null ? Task.CompletedTask : BringSongAsync(project.Path, project.Name);

    async Task<bool> BringSongAsync(string path, string songName, IReadOnlyCollection<string>? only = null)
    {
        if (Readers.For(path) is not { } reader) return false;
        SongChoicesOpen = false;
        Message = $"Reading {songName}…";
        try
        {
            var from = (long)Math.Max(0, FromBar - 1) * Bar;
            var length = Section.Length;
            // Read and worked out off the screen's thread, so a big song doesn't freeze the window.
            var parts = await UiWork.Run(() =>
            {
                var found = new List<(string Name, string Role, List<NoteEvent> Notes)>();
                foreach (var c in reader.ExtractChannels(path))
                {
                    if (only is not null && !only.Contains(c.Name)) continue;   // just the song's parts picked
                    // Notes held into the first bar (a pushed chord, a tie) come in too, cut to start there.
                    var notes = MidiBrowserViewModel.Cut(PhraseAnalyser.Normalise(c.Notes, c.Ppq, keepPlace: true), from, from + length);
                    if (notes.Count == 0) continue;
                    var role = c.Kind == "Drums" ? MidiRoles.Drums : PhraseAnalyser.Analyse(notes, c.Name).Role;
                    found.Add((c.Name, role, notes));
                }
                return found;
            });
            if (parts.Count == 0)
            {
                Message = $"{songName} has no notes from bar {FromBar} for {Section.Bars} bars.";
                return false;
            }
            Remember();
            foreach (var (name, role, notes) in parts) AddLayer(name, role, notes, path);
            Changed();
            Message = $"Brought in {parts.Count} parts of {songName}, bars {FromBar}–{FromBar + Section.Bars - 1}. Its parts won't be suggested for this section.";
            SongSearch = "";
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Message = $"Couldn't read {songName}: {e.Message}";
            return false;
        }
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

    /// <summary>Starts again with the song's bars where the part fits (only the song's parts named in <paramref name="parts"/>
    /// when given) and the part on top.</summary>
    public async Task LoadSongAsync(string path, string songName, MidiEntry? entry, IReadOnlyList<NoteEvent>? notes, string? title,
                                    long at = 0, IReadOnlyCollection<string>? parts = null)
    {
        StopPlaying();
        _undo.Clear();
        _redo.Clear();
        CanUndo = CanRedo = false;
        var bars = notes is { Count: > 0 } ? Math.Clamp(PhraseAnalyser.BarsOf(notes), 1, GrowBars) : NewBars;
        _session = Fresh();
        _session.Name = $"{songName} sketch";
        Section.Bars = bars;
        Show();
        FromBar = (int)(Math.Max(0, at) / Bar) + 1;
        if (parts is not { Count: 0 }) await BringSongAsync(path, songName, parts);
        if (entry is not null && notes is { Count: > 0 }) AddFromLibrary(entry, notes, title);
        SelectedLayer = Layers.LastOrDefault();
        Message = "";
    }

    /// <summary>The cut-down playground's section, taken to the Playground page to work up further.</summary>
    [RelayCommand]
    void OpenInMain()
    {
        if (Section.Layers.Count == 0) return;
        StopPlaying();
        var copy = PlaygroundSession.FromJson(_session.ToJson())!;
        _main.Playground.TakeSection(copy.Sections[Math.Clamp(copy.Current, 0, copy.Sections.Count - 1)]);
    }

    /// <summary>A section from a cut-down playground: into the empty section, or as a new one, then the page is shown.</summary>
    internal void TakeSection(PlaygroundSection section)
    {
        StopPlaying();
        Remember();
        if (Section.Layers.Count == 0 && !ChordsTyped)
        {
            section.Name = Section.Name;
            _session.Sections[_session.Current] = section;
        }
        else
        {
            section.Name = NextSectionName();
            _session.Sections.Add(section);
            _session.Current = _session.Sections.Count - 1;
        }
        Show();
        Changed(harmony: false);
        Message = $"Section {section.Name} came from a song's MIDI that fits. Undo takes it back out.";
        if (_main.Nav.FirstOrDefault(n => n.Page == this) is { } page) _main.SelectedNav = page;
    }

    /// <summary>
    /// From a song's "MIDI that fits" tab (Solomon, 2026-10-19: "Open in Playground" to work an idea up): a new section
    /// named after the song, with the song's bars where the part fits brought in to build on, the part added on top,
    /// and the Playground page shown.
    /// </summary>
    public async Task OpenFromSongAsync(string path, string songName, MidiEntry? entry = null, IReadOnlyList<NoteEvent>? notes = null,
                                        string? title = null, long at = 0)
    {
        StopPlaying();
        var bars = notes is { Count: > 0 } ? Math.Clamp(PhraseAnalyser.BarsOf(notes), 1, GrowBars) : NewBars;
        if (Section.Layers.Count > 0 || ChordsTyped)
        {
            Remember();
            _session.Sections.Add(new PlaygroundSection { Name = NextSectionName(), Bars = bars });
            _session.Current = _session.Sections.Count - 1;
            Show();
            Changed(harmony: false);
        }
        else
        {
            Section.Bars = bars;
            _showing = true;
            Bars = bars;
            _showing = false;
        }
        FromBar = (int)(Math.Max(0, at) / Bar) + 1;
        if (_main.Nav.FirstOrDefault(n => n.Page == this) is { } page) _main.SelectedNav = page;
        var brought = await BringSongAsync(path, songName);
        if (entry is not null && notes is { Count: > 0 }) AddFromLibrary(entry, notes, title);
        Message = entry is null
            ? Message
            : $"Section {SectionName}: {title ?? entry.Title} on {(brought ? $"{songName}, bars {FromBar}–{FromBar + Section.Bars - 1}" : "its own")}. Mute the song's parts to hear it alone; Undo takes it out.";
    }

    // ---- loop points ----

    /// <summary>Loop points dragged along the piano roll's bar numbers: playing goes round them, and suggestions fit just them.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasLoop), nameof(LoopText))] RollRange? _loop;
    public bool HasLoop => Loop is not null;
    public string LoopText => Loop is { } r ? $"Bars {r.Start / Bar + 1}–{(r.End + Bar - 1) / Bar}" : "";

    [RelayCommand]
    void SetLoop(RollRange? range)
    {
        Loop = range is { } r && Math.Clamp(r.End, 1, Length) > Math.Clamp(r.Start, 0, Length)
            ? new RollRange(Math.Clamp(r.Start, 0, Length), Math.Clamp(r.End, 1, Length)) : null;
        if (_playing) _ = PlayAsync();
    }

    [RelayCommand]
    void ClearLoop() => SetLoop(null);

    /// <summary>The harmony suggestions are fitted to: the loop's bars when there are loop points, else the section.</summary>
    SectionHarmony MatchHarmony => Loop is { } r ? Harmony.Slice(r.Start, r.End) : Harmony;

    // ---- listening ----

    [ObservableProperty] bool _isPlaying;
    [ObservableProperty] double _position = -1;
    [ObservableProperty] bool _playArrangement;
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
        if (PlayArrangement)
        {
            length = _session.Order().Sum(s => s.Length);
            var tracks = _session.ArrangedTracks();
            return tracks.Select(t => Channel(t.Name, t.Program, t.Notes)).ToList();
        }
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
        Position = PlayArrangement ? -1 : ticks % Math.Max(1, _playLength) + (Loop?.Start ?? 0);
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

    partial void OnPlayArrangementChanged(bool value)
    {
        if (_playing) _ = PlayAsync();
    }

    /// <summary>The section with a suggestion playing on top, to hear it before adding it.</summary>
    internal async Task PreviewMatch(PlaygroundMatchRow row)
    {
        if (row.IsPlaying) { StopPlaying(); return; }
        StopPlaying();
        var channels = ChannelsOf(Section).ToList();
        var notes = row.Match.Notes;
        var period = Math.Max(1, PhraseAnalyser.BarsOf(notes)) * (long)Bar;
        channels.Add(Channel(row.Title, GmSounds.DefaultFor(row.Match.Entry.Role), SectionMatcher.Loop(notes, period, Section.Length)));
        await PlayAsync(instead: channels);
        row.IsPlaying = _playing;
    }

    /// <summary>The section with a reharmonisation in place: parts written from the chords follow it; with none, the new chords play plainly.</summary>
    internal async Task PreviewIdea(ReharmRow row)
    {
        if (row.IsPlaying) { StopPlaying(); return; }
        StopPlaying();
        // What plays now (soloed parts only, when any are), with the written parts playing the idea's chords.
        var audible = PlaygroundSession.Audible(Section).ToList();
        var written = audible.Where(l => l.Written.Length > 0).ToList();
        var channels = audible.Where(l => l.Written.Length == 0).Select(l => Channel(l.Name, l.Program, l.Notes)).ToList();
        if (Section.Layers.All(l => l.Written.Length == 0))
            channels.Add(Channel("Chords", 4, Voicer.Voice(row.Idea.Lane, VoicingStyle.Close, CompRhythm.Held, Section.Length)));
        else
            foreach (var l in written)
                channels.Add(Channel(l.Name, l.Program, WithGroove(l, Written(l.Written, row.Idea.Lane, Section.Length), row.Idea.Lane, Section.Length)));
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

    /// <summary>What a drag carries: one part, the section ("section"), or the whole arrangement ("arrangement").</summary>
    /// <summary>A part's notes within the section, cut at its end: a part keeps its notes past the end when the section is
    /// made shorter (so making it longer again brings them back), but they don't go out to the DAW.</summary>
    List<NoteEvent> InSection(IReadOnlyList<NoteEvent> notes) => MidiBrowserViewModel.Cut(notes, 0, Section.Length);

    public DragPayload? MakeDrag(IReadOnlyList<object> items)
    {
        var dir = ExportFolder();
        if (items.FirstOrDefault() is "arrangement" or "section")
        {
            var whole = items[0] is "arrangement";
            var tracks = whole ? _session.ArrangedTracks()
                : PlaygroundSession.Audible(Section).Select(l => (l.Name, l.Program, InSection(l.Notes))).ToList();
            if (tracks.Count == 0) return null;
            var clip = new MidiClip { Name = _session.Name, Ppq = Ppq };
            foreach (var t in tracks) clip.Track(t.Name).AddRange(t.Notes);
            var name = whole ? _session.Name : $"{_session.Name} - {Section.Name}";
            var file = Path.Combine(dir, TextScan.SafeFileName(name) + ".mid");
            File.WriteAllBytes(file, MidiWriter.Write(clip, _session.Tempo));
            return new DragPayload([file], tracks.Select(t => new DragCard(t.Name, $"{t.Notes.Count} notes", Glyphs.Midi)).ToList(),
                                   $"{name}: {tracks.Count} tracks", Numbered: true);
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

/// <summary>A section's tab: its name and how many parts and bars it has.</summary>
public sealed partial class SectionTab(PlaygroundViewModel owner, PlaygroundSection section, int index) : ObservableObject
{
    public PlaygroundSection Section => section;
    public int Index => index;
    public string Name
    {
        get => section.Name;
        set { owner.RenameSection(section, value); OnPropertyChanged(); OnPropertyChanged(nameof(Details)); }
    }
    public string Details => $"{section.Bars} bars · {section.Layers.Count} part{(section.Layers.Count == 1 ? "" : "s")}";
    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Details));
    }
}

/// <summary>One part in the section, as its lane shows it.</summary>
public sealed partial class LayerRow : ObservableObject
{
    readonly PlaygroundViewModel _owner;

    public LayerRow(PlaygroundViewModel owner, PlaygroundLayer layer)
    {
        _owner = owner;
        Layer = layer;
    }

    public PlaygroundLayer Layer { get; }

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

    /// <summary>The notes as a channel for the small roll, the section's length wide.</summary>
    public MidiChannel Channel => new(Layer.Name, "", PhraseAnalyser.Ppq, Layer.Notes);
    public IReadOnlyList<NoteEvent> Notes => Layer.Notes;
    public long Length => _owner.Length;

    /// <summary>Every other part that plays, grey behind this one on the piano roll.</summary>
    public IReadOnlyList<NoteEvent> Guide =>
        PlaygroundSession.Audible(_owner.CurrentSection).Where(l => l != Layer && l.Role != MidiRoles.Drums).SelectMany(l => l.Notes).ToList();

    /// <summary>
    /// Which notes fight the chords, for the piano roll to mark red: judged by chord-scale theory against the chord
    /// lane with the section's ears, so jazz tensions and blue notes aren't marked; a short note stepping into a good
    /// one is let off as a passing note.
    /// </summary>
    public Func<NoteEvent, bool> Clashes
    {
        get
        {
            var h = _owner.Harmony;
            var style = h.Style;
            if (Layer.Role == MidiRoles.Drums || h.Lane.Count == 0) return _ => false;
            return n => ChordLane.At(h.Lane, n.Start + 1, h.Length) is { } chord && n.Length > PhraseAnalyser.Ppq / 2 &&
                        ChordScales.Sit(chord, n.Key % 12, style) < 0.05;
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

/// <summary>A library part suggested for the section.</summary>
public sealed partial class PlaygroundMatchRow(PlaygroundViewModel owner, PlaygroundMatch match) : ObservableObject
{
    public PlaygroundMatch Match => match;
    public string Title => match.Entry.Title.Length > 0 ? match.Entry.Title : match.Entry.SourceName;
    public string From => match.Entry.IsProject ? $"{match.Entry.SourceName} · {match.Entry.Phrase.Source.Daw}" : match.Entry.SourceName;
    public string FitText => match.Score > 0 ? $"{match.Percent}%" : "";
    public string Why => match.Why;
    public string Chords => string.Join(" ", match.Entry.Chords.Take(6).Select(c => ChordSpeller.Spell(c.Mask, c.Root)?.Transposed(match.Fit?.Semitones ?? 0).Name).OfType<string>());
    public MidiChannel Channel => new(Title, "", PhraseAnalyser.Ppq, match.Notes);
    [ObservableProperty] bool _isPlaying;
    [RelayCommand] void Add() => owner.AddMatch(this);
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
