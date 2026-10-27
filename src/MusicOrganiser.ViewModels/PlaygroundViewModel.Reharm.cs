using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// Reharm in three steps on the chord lane (Solomon, 2026-10-21: "reharm is not intuitive to use, make it workflow easy to
/// follow with visual feedback"): 1, click a chord; 2, pick one of the ideas for it, which shows striped in place on the
/// chord lane and plays; 3, Keep it or go Back to the chord as it was.
/// </summary>
public sealed partial class PlaygroundViewModel
{
    /// <summary>The chord clicked on the chord lane, whose ideas the Reharm tab lists; null before one is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReharmStep), nameof(IsReharmPicking), nameof(IsReharmChoosing), nameof(PickedChordText))]
    ChordCell? _pickedChord;

    /// <summary>The idea being tried: shown striped on the chord lane, to Keep or go Back from.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReharmStep), nameof(IsReharmPicking), nameof(IsReharmChoosing), nameof(IsReharmTrying), nameof(BackText))]
    ReharmRow? _tryingIdea;

    /// <summary>The tried idea's chords where they differ, drawn striped over the chord lane; null when none is tried.</summary>
    [ObservableProperty] IReadOnlyList<ChordCell>? _proposedChords;

    /// <summary>The ideas that change the picked chord.</summary>
    public ObservableCollection<ReharmRow> ChordIdeas { get; } = [];

    /// <summary>1: pick a chord, 2: pick an idea, 3: keep it or go back.</summary>
    public int ReharmStep => TryingIdea is not null ? 3 : PickedChord is not null ? 2 : 1;
    public bool IsReharmPicking => ReharmStep == 1;
    public bool IsReharmChoosing => ReharmStep == 2;
    public bool IsReharmTrying => ReharmStep == 3;
    public string PickedChordText => PickedChord is { } c ? $"{c.Name}{(c.Roman.Length > 0 ? $" ({c.Roman})" : "")} · bar {c.Bar}" : "";
    public string BackText => TryingIdea is { } t ? $"Back to {t.Idea.Before}" : "Back";

    /// <summary>A chord clicked on the chord lane: picked, and Delete then takes it off the chord track.</summary>
    [RelayCommand]
    void PickChord(ChordCell? cell)
    {
        if (cell is null) return;
        Pick(cell);
        _chordLaneLast = true;
    }

    void Pick(ChordCell cell)
    {
        if (TryingIdea is not null)
        {
            // Another chord clicked while an idea is tried: the chords go back as they were, and the one there is picked.
            BackIdea();
            cell = Chords.FirstOrDefault(c => c.Timed.Start <= cell.Timed.Start && c.Timed.End > cell.Timed.Start) ?? cell;
        }
        PickedChord = cell;
        RefreshChordIdeas();
        RightTab = ReharmTab;
    }

    /// <summary>
    /// The chord lane was clicked last, not a clip (Solomon, 2026-10-23: "should be able to delete chords", "at the moment
    /// you have to rightclick to delete"): Delete and Backspace take the picked chord off the chord track until a clip or the
    /// timeline is clicked.
    /// </summary>
    bool _chordLaneLast;

    /// <summary>
    /// Other names for the picked chord's notes (Solomon, 2026-10-23: "if the harmony engine gets it wrong should be able
    /// to tell it what the root is and that will change what chord the chord track interprets it as"): picking one puts
    /// it on the chord track for as long as the chord lasts, and the parts follow it.
    /// </summary>
    public ObservableCollection<ChordReadingRow> ChordReadings { get; } = [];

    /// <summary>The picked chord's other notes, each to say is its root.</summary>
    public ObservableCollection<ChordReadingRow> ChordRoots { get; } = [];

    /// <summary>The ideas whose changes reach the picked chord, and the other ways to hear it.</summary>
    void RefreshChordIdeas()
    {
        ChordIdeas.Clear();
        ChordReadings.Clear();
        ChordRoots.Clear();
        if (PickedChord?.Timed is not { } picked) return;
        foreach (var row in Ideas)
            if (Changes(Harmony.Lane, row.Idea.Lane) is { } range && range.From < picked.End && range.To > picked.Start)
                ChordIdeas.Add(row);
        var chord = picked.Chord;
        // A bass under another root stays under every reading; otherwise each is in root position, the lowest note
        // sounding then leaning the order (F# under A C# E G# B: F#m11 first).
        var lowest = PlaygroundSession.Audible(Section).Where(l => l.Role != MidiRoles.Drums).SelectMany(l => l.Notes)
            .Where(n => !n.Muted && n.Start < picked.End && n.Start + n.Length > picked.Start && (chord.Mask >> n.Key % 12 & 1) != 0)
            .Select(n => n.Key).DefaultIfEmpty(-1).Min();
        foreach (var c in ChordSpeller.Readings(chord.Mask, chord.Bass != chord.Root ? chord.Bass : -1, lowest >= 0 ? lowest % 12 : chord.Bass)
                                      .Where(c => c.Root != chord.Root).Take(4))
            ChordReadings.Add(new ChordReadingRow(this, c, c.Name));
        for (var i = 1; i < 12; i++)
            if (chord.Has(i) && ChordSpeller.On(chord, chord.Root + i) is var on)
                ChordRoots.Add(new ChordReadingRow(this, on, on.RootName));
    }

    /// <summary>
    /// The picked chord heard as <paramref name="chord"/> instead: on the chord track for as long as it lasts (over a chord
    /// that was there), so the parts follow it there. Undo takes it back.
    /// </summary>
    internal void HearAs(ChordSymbol chord)
    {
        if (PickedChord is not { } picked) return;
        if (TryingIdea is not null) BackIdea();
        Remember();
        PutOnChordTrack(Section, [picked.Timed with { Chord = chord }], picked.Timed.Start, picked.Timed.End);
        Changed();
        Message = $"{picked.Name} is {chord.Name} now, on the chord track: the parts follow it. Undo takes it back.";
    }

    /// <summary>The chords changed (an edit, Undo, an idea kept): the same place stays picked if it still has a chord.</summary>
    void ChordsChangedUnderReharm()
    {
        if (_tryBefore is not null) return;   // an idea being tried: the chord it was tried on stays picked
        TryingIdea = null;
        ProposedChords = null;
        if (PickedChord is { } old)
            PickedChord = Chords.FirstOrDefault(c => c.Timed.Start == old.Timed.Start)
                          ?? Chords.FirstOrDefault(c => c.Timed.Start <= old.Timed.Start && c.Timed.End > old.Timed.Start);
        RefreshChordIdeas();
    }

    /// <summary>
    /// The idea as it was before the idea being tried went in; null when none is (Solomon, 2026-10-27: "notes dont follow
    /// the reharm on screen but i hear the difference"). Trying an idea puts it in for real, so the chord lane, the clips
    /// and the piano roll show what plays. Back puts this back, Keep makes it one Undo step, and nothing tried is saved
    /// until it's kept. The ideas listed stay those for the chords as they were meanwhile, to try another.
    /// </summary>
    string? _tryBefore;
    /// <summary>Where the idea being tried changes the chords (worked out before it went in).</summary>
    (long From, long To) _tryRange;

    /// <summary>Step 2: the idea goes in, striped on the chord lane, every part following it on screen, and it plays.</summary>
    internal async Task TryIdea(ReharmRow row)
    {
        if (TryingIdea == row) { await PreviewIdea(row); return; }   // its tile again: stop or play
        if (RangeOf(row.Idea) is not { } range) return;
        var playing = _playing;
        if (_tryBefore is { } before) Restore(before);   // another idea was being tried: this one goes in on the chords as they were
        else _tryBefore = _session.ToJson();
        _tryRange = range;
        PutIdea(row.Idea, range, asPart: false);
        Changed(settled: false);
        var lane = row.Idea.Lane;
        var key = Harmony.Key;
        var funcs = key is { } k ? ChordFunctions.Analyse(lane.Select(c => c.Chord).ToList(), k.Tonic, k.Minor) : null;
        var length = Harmony.Length;
        ProposedChords = lane.Select((c, i) => (c, i)).Where(p => p.c.Start < range.To && p.c.End > range.From)
                             .Select(p => new ChordCell(p.c, funcs?[p.i], length, set: true)).ToList();
        TryingIdea = row;
        Message = $"Trying {row.Title}: {row.Swap}. Keep it, or go back to {row.Idea.Before}.";
        PlayInBitwigSoon();
        // Heard straight away: carrying on from where it was, else from the bar before the change.
        if (playing) _ = PlayAsync(keepPlace: true);
        else row.IsPlaying = await PlayAsync(startAt: Math.Max(0, range.From / Bar * Bar - Bar));
    }

    /// <summary>Step 3: the tried idea becomes the chords. Undo takes it back.</summary>
    [RelayCommand]
    void KeepIdea() => Keep(asPart: false);

    /// <summary>Step 3 for chords made for a melody or bassline: kept as a plain Chords part instead of on the chord track.</summary>
    [RelayCommand]
    void KeepIdeaAsPart() => Keep(asPart: true);

    void Keep(bool asPart)
    {
        if (TryingIdea is not { } row) return;
        if (asPart || _tryBefore is null)
        {
            // As a part: the chord track goes back as it was, and a plain Chords part plays the idea instead.
            if (_tryBefore is { } before) { _tryBefore = null; Restore(before); }
            TryingIdea = null;
            ProposedChords = null;
            ApplyIdea(row.Idea, asPart);
            return;
        }
        var range = _tryRange;
        KeepTried();
        Message = KeptText(row.Idea, range, asPart: false);
    }

    /// <summary>The idea being tried kept as it plays (Keep, or anything else changed meanwhile): one Undo step takes it back.</summary>
    void KeepTried()
    {
        if (_tryBefore is not { } before) return;
        var row = TryingIdea;
        _tryBefore = null;
        Remember(before);
        TryingIdea = null;
        ProposedChords = null;
        SaveCurrent();
        RefreshIdeas();   // the ideas for the chords as kept, with the chord at the same place picked
        if (row is not null) Message = $"Kept {row.Title}: {row.Swap}. Undo takes it back.";
    }

    /// <summary>Step 3: back to the chords as they were, to try another idea. It carries on playing, so the two can be compared.</summary>
    [RelayCommand]
    void BackIdea()
    {
        TryingIdea = null;
        ProposedChords = null;
        if (_tryBefore is not { } before) return;
        _tryBefore = null;
        Restore(before);
    }

    // ---- harmonise: chords for a melody or a bassline with none ----

    /// <summary>
    /// Whole chord lanes for the idea's melody, bassline or both when no chord part plays (Solomon, 2026-10-21:
    /// "should be able to reharmonise a melody on its own", "or harmonise a bassline or bass an melody").
    /// </summary>
    public ObservableCollection<ReharmRow> HarmoniseIdeas { get; } = [];
    [ObservableProperty] string _harmoniseText = "";
    public bool CanHarmonise => HarmoniseIdeas.Count > 0;

    int _harmoniseRequest;

    /// <summary>
    /// Works the chord lanes out away from the screen (five searches through the chords for every edit), with what your
    /// chord parts usually do next once that's learned. The rows already there stay until the new ones are ready.
    /// </summary>
    async Task RefreshHarmoniseAsync()
    {
        if (_tryBefore is not null) return;   // the ones listed are tried from, until it's kept or taken back
        var request = ++_harmoniseRequest;
        var layers = PlaygroundSession.Audible(Section).Where(l => l.Role != MidiRoles.Drums).ToList();
        var chordParts = layers.Any(l => l.Clips.Any(c => c.Role == MidiRoles.Chords || c.Role == MidiRoles.Other && l.Role == MidiRoles.Chords));
        // Chords on the chord track are the harmony already: they're reharmonised a chord at a time, as a chord part's are.
        if (IsQuick || chordParts || HasChordTrack || layers.Count == 0)
        {
            HarmoniseIdeas.Clear();
            HarmoniseText = "";
            OnPropertyChanged(nameof(CanHarmonise));
            return;
        }
        var length = Section.Length;
        bool IsBass(PlaygroundLayer l) => l.Role == MidiRoles.Bass;
        var melody = layers.Where(l => !IsBass(l)).SelectMany(l => l.Notes).Where(n => !n.Muted && n.Start < length).ToList();
        var bass = layers.Where(IsBass).SelectMany(l => l.Notes).Where(n => !n.Muted && n.Start < length).ToList();
        var (tonic, minor) = HarmoniseKey(melody.Concat(bass));
        var model = await ModelAsync();
        if (request != _harmoniseRequest) return;
        var ideas = await UiWork.Run(() => Harmoniser.Harmonise(melody, bass, length, tonic, minor, model is null ? null : model.Chance).ToList());
        // Edited again meanwhile, the newer call shows its own; one being tried meanwhile keeps the ones it came from.
        if (request != _harmoniseRequest || _tryBefore is not null) return;
        HarmoniseIdeas.Clear();
        foreach (var idea in ideas) HarmoniseIdeas.Add(new ReharmRow(this, idea));
        HarmoniseText = HarmoniseIdeas.Count == 0 ? "" : $"Chords for {HarmoniseIdeas[0].Idea.Before}, in {ChordSymbol.Names[tonic]} {(minor ? "minor" : "major")}";
        OnPropertyChanged(nameof(CanHarmonise));
    }

    /// <summary>The idea's key: the one typed or heard, else the notes' likeliest even when it's only a guess.</summary>
    (int Tonic, bool Minor) HarmoniseKey(IEnumerable<NoteEvent> notes)
    {
        if (Harmony.Key is { } k) return (k.Tonic, k.Minor);
        var chroma = new double[12];
        foreach (var n in notes) chroma[n.Key % 12] += Math.Clamp(n.Length / (double)Bar, 1 / 16.0, 2);
        return MusicTheory.EstimateMidiKey(chroma) is { } guess ? (guess.Tonic, guess.Minor) : (0, false);
    }

    /// <summary>Where an idea changes the chords: all of it for chords made for a melody, else where its lane differs.</summary>
    (long From, long To)? RangeOf(Reharm idea) =>
        idea.Whole && idea.Lane.Count > 0 ? (idea.Lane[0].Start, idea.Lane[^1].End) : Changes(Harmony.Lane, idea.Lane);

    /// <summary>Back to step 1: no chord picked.</summary>
    [RelayCommand]
    void ClearPickedChord()
    {
        BackIdea();
        PickedChord = null;
        RefreshChordIdeas();
    }

    /// <summary>Harmony › Reharmonise…: the Reharm tab, with the chord under the play marker picked when none is.</summary>
    void OpenReharm()
    {
        RightTab = ReharmTab;
        if (PickedChord is not null || CanHarmonise) return;   // with no chord part, it offers chords for the melody first
        var at = Math.Max(0, Cursor);
        if ((Chords.FirstOrDefault(c => c.Timed.Start <= at && c.Timed.End > at) ?? Chords.FirstOrDefault()) is { } cell) Pick(cell);
    }
}
