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
    [NotifyPropertyChangedFor(nameof(ReharmStep), nameof(IsReharmChoosing), nameof(IsReharmTrying), nameof(BackText))]
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

    [RelayCommand]
    void PickChord(ChordCell? cell)
    {
        if (cell is null) return;
        if (TryingIdea is not null) BackIdea();
        PickedChord = cell;
        RefreshChordIdeas();
        RightTab = ReharmTab;
    }

    /// <summary>The ideas whose changes reach the picked chord.</summary>
    void RefreshChordIdeas()
    {
        ChordIdeas.Clear();
        if (PickedChord?.Timed is not { } picked) return;
        foreach (var row in Ideas)
            if (Changes(Harmony.Lane, row.Idea.Lane) is { } range && range.From < picked.End && range.To > picked.Start)
                ChordIdeas.Add(row);
    }

    /// <summary>The chords changed (an edit, Undo, an idea kept): the same place stays picked if it still has a chord.</summary>
    void ChordsChangedUnderReharm()
    {
        TryingIdea = null;
        ProposedChords = null;
        if (PickedChord is { } old)
            PickedChord = Chords.FirstOrDefault(c => c.Timed.Start == old.Timed.Start)
                          ?? Chords.FirstOrDefault(c => c.Timed.Start <= old.Timed.Start && c.Timed.End > old.Timed.Start);
        RefreshChordIdeas();
    }

    /// <summary>Step 2: the idea goes on the chord lane, striped, and plays with the parts following it.</summary>
    internal async Task TryIdea(ReharmRow row)
    {
        if (TryingIdea == row) { await PreviewIdea(row); return; }   // its tile again: stop or play
        StopPlaying();
        var lane = row.Idea.Lane;
        var range = RangeOf(row.Idea);
        var key = Harmony.Key;
        var funcs = key is { } k ? ChordFunctions.Analyse(lane.Select(c => c.Chord).ToList(), k.Tonic, k.Minor) : null;
        var length = Harmony.Length;
        ProposedChords = range is { } r
            ? lane.Select((c, i) => (c, i)).Where(p => p.c.Start < r.To && p.c.End > r.From).Select(p => new ChordCell(p.c, funcs?[p.i], length)).ToList()
            : [];
        TryingIdea = row;
        Message = $"Trying {row.Title}: {row.Swap}. Keep it, or go back to {row.Idea.Before}.";
        await PreviewIdea(row);
    }

    /// <summary>Step 3: the tried idea becomes the chords. Undo takes it back.</summary>
    [RelayCommand]
    void KeepIdea()
    {
        if (TryingIdea is not { } row) return;
        StopPlaying();
        ApplyIdea(row.Idea);   // the chords change, which picks the chord now at the same place
        Message = $"Kept {row.Title}: {row.Swap}. Undo takes it back.";
    }

    /// <summary>Step 3: back to the chords as they were, to try another idea.</summary>
    [RelayCommand]
    void BackIdea()
    {
        StopPlaying();
        TryingIdea = null;
        ProposedChords = null;
    }

    // ---- harmonise: chords for a melody or a bassline with none ----

    /// <summary>
    /// Whole chord lanes for the idea's melody, bassline or both when no chord part plays (Solomon, 2026-10-21:
    /// "should be able to reharmonise a melody on its own", "or harmonise a bassline or bass an melody").
    /// </summary>
    public ObservableCollection<ReharmRow> HarmoniseIdeas { get; } = [];
    [ObservableProperty] string _harmoniseText = "";
    public bool CanHarmonise => HarmoniseIdeas.Count > 0;

    void RefreshHarmonise()
    {
        HarmoniseIdeas.Clear();
        var layers = PlaygroundSession.Audible(Section).Where(l => l.Role != MidiRoles.Drums).ToList();
        var chordParts = layers.Any(l => l.Clips.Any(c => c.Role == MidiRoles.Chords || c.Role == MidiRoles.Other && l.Role == MidiRoles.Chords));
        if (IsQuick || chordParts || layers.Count == 0)
        {
            HarmoniseText = "";
            OnPropertyChanged(nameof(CanHarmonise));
            return;
        }
        var length = Section.Length;
        bool IsBass(PlaygroundLayer l) => l.Role == MidiRoles.Bass;
        var melody = layers.Where(l => !IsBass(l)).SelectMany(l => l.Notes).Where(n => !n.Muted && n.Start < length).ToList();
        var bass = layers.Where(IsBass).SelectMany(l => l.Notes).Where(n => !n.Muted && n.Start < length).ToList();
        var (tonic, minor) = HarmoniseKey(melody.Concat(bass));
        var model = _model;
        foreach (var idea in Harmoniser.Harmonise(melody, bass, length, tonic, minor, model is null ? null : model.Chance))
            HarmoniseIdeas.Add(new ReharmRow(this, idea));
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
        ChordIdeas.Clear();
    }

    /// <summary>Harmony › Reharmonise…: the Reharm tab, with the chord under the play marker picked when none is.</summary>
    void OpenReharm()
    {
        RightTab = ReharmTab;
        if (PickedChord is not null || CanHarmonise) return;   // with no chord part, it offers chords for the melody first
        var at = Math.Max(0, Cursor);
        PickChord(Chords.FirstOrDefault(c => c.Timed.Start <= at && c.Timed.End > at) ?? Chords.FirstOrDefault());
    }
}
