using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.ViewModels;

/// <summary>A chord block dragged on the chord lane: where it started the drag, where it is now, and whether the drag is over.</summary>
public sealed record ChordDrag(long From, long Start, long Length, int Gesture, bool Done);

/// <summary>
/// Chords first (Solomon, 2026-10-22: "extract chord progressions from your chord library so it will go on the chord track
/// but not place any MIDI"): the chord track holds chords with no notes, put there from the Chords tab's progressions (found
/// in every chord part read from his projects and MIDI folders), from what usually comes next, or from a song's chords.
/// Its blocks stretch and move on the chord lane, with everything following as they're dragged.
/// </summary>
public sealed partial class PlaygroundViewModel
{
    public const string HearChordTrackKey = "playground_hear_chord_track";

    /// <summary>Whether the chord track has any chords: they then decide the harmony where they are.</summary>
    public bool HasChordTrack => Section.ChordTrack.Count > 0;

    /// <summary>The chord track played as plain chords with the idea, and in Bitwig with Play in Bitwig (Send to Bitwig and dragging out leave it: it has no MIDI of its own).</summary>
    [ObservableProperty] bool _hearChordTrack = true;
    partial void OnHearChordTrackChanged(bool value)
    {
        SaveOption(HearChordTrackKey, value ? "1" : "0");
        OnPropertyChanged(nameof(ChordTrackMuted));
        if (_playing) _ = PlayAsync(keepPlace: true);
        PlayInBitwigSoon();
    }

    /// <summary>
    /// The M on the chord lane, as each track has (Solomon, 2026-10-27: "no ease way mute the chord track"): its plain
    /// chords aren't played, and the parts still follow it.
    /// </summary>
    public bool ChordTrackMuted
    {
        get => !HearChordTrack;
        set => HearChordTrack = !value;
    }

    /// <summary>Where the parts ended at the last change, so the chord track goes round only when they grow past it.</summary>
    long _partsEnd;

    long PartsEnd() => Section.Layers.SelectMany(l => l.Clips).Select(c => c.End).DefaultIfEmpty(0).Max();

    /// <summary>
    /// The chord track going round under parts longer than it (Solomon, 2026-10-27: "chord track doesnt loop. it should
    /// play along with the midi if the midi is longer that it"): when the parts now go past its end, and it reached their
    /// end before, its chords play round again to their new end. One stopped short of the parts on purpose (its last
    /// chord taken off) stays as it is, and chords already on it never change.
    /// </summary>
    void ChordTrackGoesRound()
    {
        var end = PartsEnd();
        var was = _partsEnd;
        _partsEnd = end;
        if (end <= was || !HasChordTrack) return;
        var track = Section.ChordTrack;
        var (from, to) = (track.Min(c => c.Start), track.Max(c => c.End));
        if (to < was || to >= end) return;
        var round = to - from;
        var added = new List<PlaygroundChord>();
        for (var shift = round; from + shift < end; shift += round)
            foreach (var c in track.Where(c => c.Start + shift < end))
                added.Add(new PlaygroundChord { Start = c.Start + shift, Length = Math.Min(c.Length, end - c.Start - shift), Chord = c.Chord });
        Section.ChordTrack = track.Concat(added).OrderBy(c => c.Start).ToList();
    }

    /// <summary>The chord track's notes as heard: each chord voiced plainly, root at the bottom.</summary>
    List<NoteEvent> ChordTrackNotes(PlaygroundSection section) =>
        Voicer.Plain(section.ChordTrack.OrderBy(c => c.Start).Select(c => c.Timed).ToList(), velocity: 70);

    /// <summary>The chord track to play along with the parts, if it's heard and no part is soloed.</summary>
    MidiChannel? ChordTrackChannel(PlaygroundSection section) =>
        HearChordTrack && section.ChordTrack.Count > 0 && !section.Layers.Any(l => l.Solo)
            ? Channel("Chord track", GmSounds.DefaultFor(MidiRoles.Chords), ChordTrackNotes(section), 0.8)
            : null;

    // ---- the blocks on the chord lane ----

    List<PlaygroundChord>? _chordDragBase;
    int _chordGesture;
    /// <summary>The idea as it was when the drag began, remembered for Undo once the drag changes something.</summary>
    string? _chordDragBefore;

    /// <summary>
    /// A chord block stretched by either edge or slid along (Solomon, 2026-10-22: "resize the chord clips in the chord track
    /// and everything updates on the fly"): each step is worked out from the track as it was when the drag began, so going
    /// back over a neighbour gives it back. A neighbour touching the edge being dragged follows it; one it's dragged over is
    /// cut back. The whole drag is one Undo, and nothing at all when it ends where it began. Parts follow at each step;
    /// the idea is saved and the sound made again when it's let go.
    /// </summary>
    [RelayCommand]
    void DragChord(ChordDrag? drag)
    {
        if (drag is null) return;
        if (_chordDragBase is null || drag.Gesture != _chordGesture)
        {
            if (Section.ChordTrack.All(c => c.Start != drag.From)) return;
            _chordDragBefore = _session.ToJson();
            _chordDragBase = Section.ChordTrack.Select(c => c.Copy()).ToList();
            _chordGesture = drag.Gesture;
            _chordDragRemembered = false;
        }
        var original = _chordDragBase;
        var track = original.Select(c => c.Copy()).ToList();
        var before = _chordDragBefore;
        if (drag.Done) { _chordDragBase = null; _chordDragBefore = null; }
        if (track.FirstOrDefault(c => c.Start == drag.From) is not { } moved) return;
        var least = Math.Min(Grid, (long)Ppq);
        var (was, wasEnd) = (moved.Start, moved.End);
        long start, length;
        if (drag.Start != was && drag.Start + drag.Length == wasEnd)
        {
            // Its left edge dragged: its end stays put, and the chord before it ends where it now starts.
            var left = track.FirstOrDefault(c => c != moved && c.End == was);
            start = Math.Clamp(drag.Start, Math.Min(left is null ? 0 : left.Start + least, wasEnd - least), wasEnd - least);
            length = wasEnd - start;
            if (left is not null) left.Length = start - left.Start;
        }
        else if (drag.Start == was && drag.Start + drag.Length != wasEnd)
        {
            // Its right edge: the chord after it starts where it now ends.
            var right = track.FirstOrDefault(c => c != moved && c.Start == wasEnd);
            start = was;
            var end = Math.Clamp(drag.Start + drag.Length, was + least, Math.Max(was + least, right is null ? Length : right.End - least));
            length = end - start;
            if (right is not null)
            {
                right.Length = right.End - end;
                right.Start = end;
            }
        }
        else
        {
            // Slid along.
            start = Math.Clamp(drag.Start, 0, Math.Max(0, Length - least));
            length = Math.Clamp(drag.Length, least, Math.Max(least, Length - start));
        }
        moved.Start = start;
        moved.Length = length;
        track = CutOut(track.Where(c => c != moved), start, start + length).Append(moved).OrderBy(c => c.Start).ToList();
        var changed = !Same(track, Section.ChordTrack);
        if (changed)
        {
            if (before is not null && !_chordDragRemembered) Remember(before);
            _chordDragRemembered = true;
            Section.ChordTrack = track;
        }
        if (drag.Done)
        {
            var any = _chordDragRemembered;
            _chordDragRemembered = false;
            // Back where it began: the step taken off again, so there's nothing to undo.
            if (any && Same(track, original) && _undo.Count > 0 && ReferenceEquals(_undo[^1], before))
            {
                _undo.RemoveAt(_undo.Count - 1);
                CanUndo = _undo.Count > 0;
            }
            if (any) Changed();
        }
        else if (changed) Changed(settled: false);
    }

    bool _chordDragRemembered;

    static bool Same(IReadOnlyList<PlaygroundChord> a, IReadOnlyList<PlaygroundChord> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Start == p.Second.Start && p.First.Length == p.Second.Length && p.First.Chord == p.Second.Chord);

    /// <summary>The chords with <paramref name="from"/> to <paramref name="to"/> taken out of them: cut back, cut in two, or gone.</summary>
    static IEnumerable<PlaygroundChord> CutOut(IEnumerable<PlaygroundChord> chords, long from, long to)
    {
        foreach (var c in chords)
        {
            if (c.End <= from || c.Start >= to) { yield return c; continue; }
            if (c.Start < from) { var left = c.Copy(); left.Length = from - c.Start; yield return left; }
            if (c.End > to) { var right = c.Copy(); right.Start = to; right.Length = c.End - to; yield return right; }
        }
    }

    /// <summary>The chords of <paramref name="lane"/> from <paramref name="from"/> to <paramref name="to"/> put on the chord track there, over what it had.</summary>
    static void PutOnChordTrack(PlaygroundSection section, IReadOnlyList<TimedChord> lane, long from, long to)
    {
        var put = lane.Where(c => c.End > from && c.Start < to)
                      .Select(c => PlaygroundChord.Of(new TimedChord(Math.Max(c.Start, from), Math.Min(c.End, to) - Math.Max(c.Start, from), c.Chord)));
        section.ChordTrack = CutOut(section.ChordTrack, from, to).Concat(put).OrderBy(c => c.Start).ToList();
    }

    /// <summary>
    /// New chords from <paramref name="from"/> to <paramref name="to"/>, on the chord track (Solomon, 2026-10-27: "notes dont
    /// follow the reharm on screen", "reharm by default should keep the same amount of notes"): every part follows them as
    /// it's set to, a chord part with as many notes as it had, in its rhythm. A chord part changed by hand doesn't follow, so
    /// its notes there are re-voiced on the new chords the same way, and it stays as it is everywhere else.
    /// </summary>
    void SetChords(IReadOnlyList<TimedChord> lane, long from, long to)
    {
        PutOnChordTrack(Section, lane, from, to);
        var under = lane.Where(c => c.End > from && c.Start < to)
                        .Select(c => new TimedChord(Math.Max(c.Start, from), Math.Min(c.End, to) - Math.Max(c.Start, from), c.Chord)).ToList();
        bool Here(NoteEvent n) => n.Start >= from && n.Start < to;
        foreach (var layer in Section.Layers.Where(l => l.Written.Length == 0))
        {
            var pinned = layer.Clips.Where(c => c.Pinned && c.Start < to && c.End > from &&
                                                (c.Role == MidiRoles.Chords || c.Role == MidiRoles.Other && layer.Role == MidiRoles.Chords)).ToList();
            foreach (var c in pinned)
            {
                var played = c.Render();
                var revoiced = ChordFollow.Apply(played.Where(Here).ToList(), under, FollowWay.Degree, MidiRoles.Chords, _session.Style);
                c.Notes = played.Where(n => !Here(n)).Concat(revoiced).Select(n => n with { Start = n.Start - c.Start }).ToList();
                c.Loop = c.Length;
                c.Offset = 0;
                c.Transpose = 0;
                c.Inversion = 0;   // Render already turned them
                c.Fits = [];
                c.Under = [];
            }
            if (pinned.Count > 0) layer.Render();
        }
    }

    /// <summary>The chord-lane menu's "Remove this chord", and Delete or Backspace after clicking it: the picked chord off the chord track.</summary>
    void RemoveChord()
    {
        if (PickedChord is not { } picked) return;
        if (!picked.IsSet)
        {
            Message = $"{picked.Name} is heard from the parts, so it isn't on the chord track to take off. Change the notes, or hear it as another chord in Reharm.";
            return;
        }
        Remember();
        Section.ChordTrack = CutOut(Section.ChordTrack, picked.Timed.Start, picked.Timed.End).ToList();
        Changed();
        Message = $"{picked.Name} taken off the chord track. Undo puts it back.";
    }

    [RelayCommand]
    void ClearChordTrack()
    {
        if (!HasChordTrack) return;
        Remember();
        Section.ChordTrack = [];
        Changed();
        Message = "The chord track is empty: the chords are heard from the parts again. Undo puts them back.";
    }

    /// <summary>The chords heard from the parts put on the chord track, so they stay put whatever the parts do and can be stretched.</summary>
    void KeepHeardChords()
    {
        var (start, length) = Place;
        if (KeepHeardChords(start, start + length))
            Message = "The chords heard are on the chord track now: drag their edges to stretch them. Undo takes them back off.";
    }

    bool KeepHeardChords(long from, long to)
    {
        var lane = Harmony.Lane.Where(c => c.Start < to && c.End > from).ToList();
        if (lane.Count == 0) { Message = "No chords heard there to keep."; return false; }
        Remember();
        PutOnChordTrack(Section, lane, from, to);
        Changed();
        return true;
    }

    /// <summary>
    /// The inspector's "Put the chords heard there", for a clip with no chord-track chords under it: all the chords heard
    /// when the chord track is empty (the menu's "Put the chords heard on the chord track"), else just those under the
    /// clip, so the way it follows them shows as soon as one is changed.
    /// </summary>
    internal void KeepHeardChordsUnder(ClipRow row)
    {
        var (from, to) = HasChordTrack ? (row.Clip.Start, row.Clip.End) : (Place.Start, Place.Start + Place.Length);
        if (!KeepHeardChords(from, to)) return;
        Message = $"The chords heard are on the chord track now, and {row.Clip.Name} follows them: " +
                  $"{ChordFollow.Label(row.Clip.Way).ToLowerInvariant()}. Click a chord to change it and hear it follow.";
    }

    /// <summary>
    /// A double-click on the chord lane (Solomon, 2026-10-27: "should be able to double click chord track to add a chord"):
    /// a chord goes on the chord track at that bar and is picked, so Reharm shows what else it could be. In a gap it's what
    /// usually comes after the chord before (the key's home chord when there's none), lasting a bar or to the next chord;
    /// on a chord heard from the parts, that chord goes on the track; inside a longer chord-track chord, it's cut there and
    /// the new half picked.
    /// </summary>
    [RelayCommand]
    void AddChordAt(long tick)
    {
        if (_tryBefore is not null) BackIdea();
        var at = Math.Clamp(tick / Bar * Bar, 0, Math.Max(0, Section.Length - Bar));
        var key = Harmony.Key;
        if (Section.ChordTrack.FirstOrDefault(c => c.Start <= at && c.End > at) is { } set)
        {
            if (set.Start < at)
            {
                Remember();
                var half = new PlaygroundChord { Start = at, Length = set.End - at, Chord = set.Chord };
                set.Length = at - set.Start;
                Section.ChordTrack = Section.ChordTrack.Append(half).OrderBy(c => c.Start).ToList();
                Changed();
                Message = $"{set.Chord.NameIn(key)} cut at bar {at / Bar + 1}: pick what its second half plays under Reharm, or drag its edges. Undo puts it back together.";
            }
            PickChordAt(at);
            return;
        }
        var lane = Harmony.Lane;
        if (lane.FirstOrDefault(c => c.Start <= at && c.End > at) is { } heard)
        {
            Remember();
            PutOnChordTrack(Section, [heard], heard.Start, heard.End);
            Changed();
            PickChordAt(heard.Start);
            Message = $"{heard.Chord.NameIn(key)} is on the chord track now, so it stays whatever the parts do: pick another under Reharm to change it. Undo takes it off.";
            return;
        }
        var before = lane.Where(c => c.End <= at).ToList();
        var chord = before.Count > 0
            ? (_model ?? ProgressionModel.Learn([]).With(ProgressionModel.FromCharts)).Next(before[^1].Chord, before.Count > 1 ? before[^2].Chord : null)
                .Select(n => (ChordSymbol?)n.Chord).FirstOrDefault()
            : null;
        chord ??= key is { } k ? ChordSymbol.Parse(ChordSymbol.Names[k.Tonic] + (k.Minor ? "m" : "")) : ChordSymbol.Parse("C");
        if (chord is not { } adding) return;
        var next = lane.Where(c => c.Start > at).Select(c => c.Start).DefaultIfEmpty(Section.Length).Min();
        Remember();
        Section.ChordTrack = Section.ChordTrack.Append(new PlaygroundChord { Start = at, Length = Math.Min(Bar, next - at), Chord = adding })
                                   .OrderBy(c => c.Start).ToList();
        Changed();
        PickChordAt(at);
        Message = $"{adding.NameIn(key)} added to the chord track at bar {at / Bar + 1}" +
                  (before.Count > 0 ? $", what usually comes after {before[^1].Chord.NameIn(key)}" : "") +
                  ": pick another under Reharm, or drag its edges. Undo takes it off.";
    }

    /// <summary>The chord on the lane at <paramref name="at"/> picked, as clicking it does.</summary>
    void PickChordAt(long at)
    {
        if (Chords.FirstOrDefault(c => c.Timed.Start <= at && c.Timed.End > at) is { } cell) PickChord(cell);
    }

    /// <summary>A chord on the end of the chord track, a bar long, after its last chord.</summary>
    void AddToChordTrack(ChordSymbol chord)
    {
        var at = Section.ChordTrack.Max(c => c.End);
        if (at + Bar > (long)MaxBars * Bar) { Message = $"The timeline is full ({MaxBars} bars)."; return; }
        Remember();
        Reach(at + Bar);
        Section.ChordTrack.Add(new PlaygroundChord { Start = at, Length = Bar, Chord = chord });
        Changed();
        Message = $"{chord.NameIn(Harmony.Key)} added to the chord track at bar {at / Bar + 1}. Undo takes it back.";
    }

    // ---- the Chords tab: progressions from your chord parts ----

    public ObservableCollection<ProgressionRow> Progressions { get; } = [];
    [ObservableProperty] string _progressionSearch = "";
    [ObservableProperty] string _progressionBars = AnyBars;
    [ObservableProperty] string _progressionsText = "";
    public const string AnyBars = "Any length";
    public static IReadOnlyList<string> ProgressionBarChoices { get; } = [AnyBars, "1 bar", "2 bars", "4 bars", "8 bars"];
    /// <summary>How many rows the list shows at most: the best by how many songs play them.</summary>
    public const int MostProgressions = 120;

    IReadOnlyList<Progression> _progressions = [];
    /// <summary>The key the rows are spelled in: they're shown again when the idea's key changes.</summary>
    string? _progressionsKey;
    MidiCatalogue? _progressionsFrom;
    int _progressionsRequest;

    partial void OnProgressionSearchChanged(string value) => ShowProgressions();
    partial void OnProgressionBarsChanged(string value) => ShowProgressions();

    /// <summary>
    /// The progressions found again in the MIDI read so far (Solomon, 2026-10-22: "the progressions should automatically be
    /// populated by all the midi that the app gets from projects and midi folders"): worked out away from the screen when
    /// the page opens and each time more MIDI has been read. Nothing to press.
    /// </summary>
    async Task RefreshProgressionsAsync(bool load = false)
    {
        if (IsQuick) return;
        var request = ++_progressionsRequest;
        var catalogue = load ? await _main.Midi.CatalogueAsync() : _main.Midi.LoadedCatalogue;
        if (catalogue is null)
        {
            ProgressionsText = "Reading your MIDI…";
            return;
        }
        if (catalogue == _progressionsFrom) { ShowProgressions(); return; }
        if (_progressions.Count == 0) ProgressionsText = "Finding the progressions in your chord parts…";
        var found = await UiWork.Run(() => ChordProgressions.From(catalogue.Entries));
        if (request != _progressionsRequest) return;
        (_progressions, _progressionsFrom) = (found, catalogue);
        ShowProgressions();
    }

    int _showProgressionsRequest;

    /// <summary>
    /// The progressions that match the search and length, in the idea's key, the ones most songs play first. A big library
    /// has a great many, so they're searched away from the screen (each key typed starts again) and only the rows shown are made.
    /// </summary>
    void ShowProgressions()
    {
        if (_progressionsFrom is null) return;
        var key = Harmony.Key;
        _progressionsKey = key?.Name;
        _ = ShowProgressionsAsync(++_showProgressionsRequest, _progressions, key);
    }

    async Task ShowProgressionsAsync(int request, IReadOnlyList<Progression> all, KeyEstimate? key)
    {
        var words = ProgressionSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bars = int.TryParse(ProgressionBars.Split(' ')[0], out var b) ? b : 0;
        var found = await UiWork.Run(() => all.Where(p => (bars == 0 || p.Bars == bars) && ProgressionRow.MatchesAll(p, key, words))
                                              .Take(MostProgressions).ToList());
        if (request != _showProgressionsRequest) return;   // typed on meanwhile: the newer search shows its own
        var playing = Progressions.FirstOrDefault(r => r.IsPlaying)?.Progression;
        Progressions.Clear();
        foreach (var p in found) Progressions.Add(new ProgressionRow(this, p, key) { IsPlaying = p == playing });
        ProgressionsText = all.Count == 0 ? "No chord parts read yet: add projects or MIDI folders on the MIDI page."
            : found.Count == 0 ? $"None like that among the {all.Count:N0} progressions in your chord parts. Try other chords or numerals."
            : $"{all.Count:N0} progressions in your chord parts, the ones most songs play first{(key is null ? "" : $", in {key.Name}")}.";
    }

    /// <summary>The example part's notes moved into the idea's key (as close as it goes, up or down).</summary>
    async Task<List<NoteEvent>> ProgressionNotesAsync(Progression p)
    {
        var notes = await UiWork.Run(() => _main.Midi.NotesOf(p.Example));
        var shift = ChordSymbol.Mod(p.Shift + p.ShiftInto(Harmony.Key));
        if (shift > 6) shift -= 12;
        return notes.Select(n => n with { Key = Math.Clamp(n.Key + shift, 0, 127) }).ToList();
    }

    /// <summary>
    /// One go round of the progression with its timing, read from the example part as the chord lane reads parts, and
    /// starting on bar 1. Its chords in plain bars when the part can't be read.
    /// </summary>
    async Task<List<TimedChord>> ProgressionLaneAsync(Progression p) => ProgressionLane(p, await ProgressionNotesAsync(p));

    /// <summary>The same from the example part's <paramref name="notes"/>, already read.</summary>
    List<TimedChord> ProgressionLane(Progression p, List<NoteEvent> notes)
    {
        var lane = notes.Count == 0 ? [] : ChordReader.Read(notes, Math.Max(Bar, PhraseAnalyser.BarsOf(notes) * (long)Bar));
        var shift = p.ShiftInto(Harmony.Key);
        if (lane.Count == 0)
            return p.Chords.Select((c, i) => new TimedChord((long)i * Bar, Bar, c.Transposed(shift).Symbol)).ToList();
        // From the bar the first chord comes in, up to where the progression starts round again.
        var from = lane[0].Start / Bar * Bar;
        var round = new List<TimedChord>();
        var changes = 0;
        foreach (var c in lane)
        {
            if (round.Count == 0 || !ProgressionModel.SameChord(round[^1].Chord, c.Chord)) changes++;
            if (changes > p.Chords.Count) break;
            round.Add(c with { Start = c.Start - from });
        }
        var end = (round[^1].End + Bar - 1) / Bar * Bar;
        round[^1] = round[^1] with { Length = end - round[^1].Start };
        return round;
    }

    /// <summary>A progression heard on its own, its example part moved into the idea's key; clicked again, it stops.</summary>
    internal async Task PreviewProgression(ProgressionRow row)
    {
        if (row.IsPlaying) { StopPlaying(); return; }
        StopPlaying();
        var notes = await ProgressionNotesAsync(row.Progression);
        if (notes.Count == 0) notes = Voicer.Plain(ProgressionLane(row.Progression, notes));   // its chords, plainly
        if (!await PlayAsync(instead: [Channel(row.Names, GmSounds.DefaultFor(MidiRoles.Chords), notes)])) return;
        foreach (var r in Progressions) r.IsPlaying = r == row;
    }

    /// <summary>
    /// The progression's chords on the chord track, with no MIDI: over the highlight, else from bar 1 when the track is
    /// empty, else after its last chord. The timeline grows to fit it.
    /// </summary>
    internal async Task UseProgression(ProgressionRow row)
    {
        StopPlaying();
        var lane = await ProgressionLaneAsync(row.Progression);
        if (lane.Count == 0) return;
        if (PlaceOnChordTrack(lane) is not var (from, to)) return;
        Message = $"{row.Names} on the chord track, bars {from / Bar + 1}–{(to + Bar - 1) / Bar}. Drag the chords' edges to change how long each lasts; Undo takes them off.";
    }

    /// <summary>
    /// One go round of chords (from 0) on the chord track: over the highlight, going round to fill it, else from bar 1 when
    /// the track is empty, going round to the end of the parts when they're longer, else after its last chord. The
    /// timeline grows to fit. Where they went; null when it's full.
    /// </summary>
    (long From, long To)? PlaceOnChordTrack(IReadOnlyList<TimedChord> lane)
    {
        var length = lane[^1].End;
        var at = Loop?.Start ?? (HasChordTrack ? Section.ChordTrack.Max(c => c.End) : 0);
        if (at >= (long)MaxBars * Bar) { Message = $"The timeline is full ({MaxBars} bars)."; return null; }
        // Over a highlight it goes round to fill it; on an empty track, under all the parts.
        var to = Loop is { } r ? r.End : HasChordTrack ? at + length : Math.Max(at + length, PartsEnd());
        var placed = new List<TimedChord>();
        for (var go = at; go < to; go += length)
            placed.AddRange(lane.Select(c => c with { Start = c.Start + go }));
        Remember();
        if (IsEmpty && !HasChordTrack && Loop is null) SetBars((int)(to / Bar));
        else Reach(to);
        PutOnChordTrack(Section, placed, at, Math.Min(to, Section.Length));
        Changed();
        return (at, to);
    }

    // ---- chords from Bitwig ----

    /// <summary>
    /// Asks Bitwig for the clips selected in its arranger and clip launcher, with their notes. Tests put a stand-in here.
    /// </summary>
    internal Func<CancellationToken, Task<IReadOnlyList<BitwigClipNotes>>> BitwigClips { get; set; }

    async Task<IReadOnlyList<BitwigClipNotes>> AskBitwigAsync(CancellationToken ct)
    {
        using var link = await _main.Live.Watch.ConnectBitwigAsync(ct);
        return await link.ClipNotesAsync(ct);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChordsFromBitwigCommand))]
    bool _isAskingBitwig;

    /// <summary>
    /// Chords first, step 3 (Solomon, 2026-10-22: "How will it get the chords from bitwig"): the chords of the clip selected
    /// in Bitwig go on the chord track, as a progression's do. Bitwig doesn't tell extensions its chords, so they're read
    /// from the clip's notes as the chord lane reads a part. One clip each time, the one clicked in Bitwig.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAskBitwig))]
    async Task ChordsFromBitwig()
    {
        IsAskingBitwig = true;
        Message = "Asking Bitwig for the clip you've selected…";
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var clips = await BitwigClips(limit.Token);
            if (BitwigClipChords.Pick(clips) is not { } clip)
            {
                Message = "No clip is selected in Bitwig. Click your chord clip in the arranger or the clip launcher, then try again.";
                return;
            }
            var where = $"the clip on {clip.Track} ({(clip.From == "arranger" ? "arranger" : "clip launcher")})";
            var lane = await UiWork.Run(() => BitwigClipChords.Lane(clip));
            if (lane.Count == 0)
            {
                Message = clip.Notes.Count == 0 ? $"{Capital(where)} has no notes. Click your chord clip in Bitwig, then try again."
                                                : $"No chords heard in {where}.";
                return;
            }
            StopPlaying();
            if (PlaceOnChordTrack(lane) is not var (from, to)) return;
            var names = string.Join(" ", Changes(lane).Take(8).Select(c => c.NameIn(Harmony.Key)));
            Message = $"The chords of {where} are on the chord track, bars {from / Bar + 1}–{(to + Bar - 1) / Bar}: {names}" +
                      (Changes(lane).Count > 8 ? " …" : "") + "." +
                      (clip.Cut ? " Its loop ends past its first 64 bars, and notes after those didn't come across." : "") + " Undo takes them off.";
        }
        catch (DawLinkException e)
        {
            Message = e.Message;
        }
        catch (OperationCanceledException)
        {
            Message = "Bitwig didn't answer. Check it's open with the Music Organiser extension on, then try again.";
        }
        finally
        {
            IsAskingBitwig = false;
        }
    }

    bool CanAskBitwig() => !IsAskingBitwig;

    static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>The chords as they change: a chord that only changes its colour (Fmaj7 to Fmaj7(#11)) isn't a new one.</summary>
    static List<ChordSymbol> Changes(IEnumerable<TimedChord> lane) => ProgressionModel.Changes(lane.Select(c => c.Chord));

    // ---- a song's chords only ----

    /// <summary>The Song tab brings in a song's chords only, onto the chord track, with none of its parts.</summary>
    [ObservableProperty] bool _songChordsOnly;

    /// <summary>The chords the song's parts make (heard as the chord lane hears parts), put on the chord track over the place.</summary>
    void PutSongChords(List<(string Name, string Role, List<NoteEvent> Notes)> parts, long start, long length, string songName)
    {
        var heard = new PlaygroundSection { Bars = (int)Math.Max(1, (length + Bar - 1) / Bar) };
        foreach (var (name, role, notes) in parts)
            heard.Layers.Add(new PlaygroundLayer { Name = name, Role = role, Notes = notes });
        var lane = SectionHarmony.Of(heard, _session.Key, _session.Style).Lane.Select(c => c with { Start = c.Start + start }).ToList();
        if (lane.Count == 0) { Message = $"No chords heard in {songName} from bar {FromBar}."; return; }
        Remember();
        PutOnChordTrack(Section, lane, start, start + length);
        Changed();
        Message = $"{songName}'s chords from its bar {FromBar} are on the chord track, with none of its parts. Undo takes them off.";
        SongSearch = "";
    }
}

/// <summary>A progression on the Chords tab: its chords in the idea's key, its Roman numerals, and how many songs play it.</summary>
public sealed partial class ProgressionRow(PlaygroundViewModel owner, Progression progression, KeyEstimate? key) : ObservableObject
{
    public Progression Progression => progression;
    public string Names { get; } = string.Join("  ", progression.NamesIn(key));
    public string Romans { get; } = string.Join(" ", progression.Romans);
    public string SongsText => progression.Songs == 1 ? "in 1 song" : $"in {progression.Songs:N0} songs";
    public string BarsText => progression.Bars == 1 ? "1 bar" : $"{progression.Bars} bars";
    public string Mode => progression.Minor ? "minor" : "major";
    public string Tip => $"{Names}\n{Romans} ({Mode})\nHeard in {progression.Example.Title}, {progression.Example.SourceName}";
    [ObservableProperty] bool _isPlaying;

    /// <summary>Whether a search word is in its chord names, its Roman numerals, or says major or minor.</summary>
    public bool Matches(string word) => InNumerals(progression, word) || InNames(Names, word);

    /// <summary>
    /// Whether every search word is in the progression's chord names in <paramref name="key"/>, its Roman numerals, or says
    /// major or minor, as <see cref="Matches"/> has it; the names are only worked out when the numerals don't match.
    /// </summary>
    internal static bool MatchesAll(Progression p, KeyEstimate? key, IReadOnlyList<string> words)
    {
        string? names = null;
        return words.All(w => InNumerals(p, w) || InNames(names ??= string.Join("  ", p.NamesIn(key)), w));
    }

    static bool InNumerals(Progression p, string word) =>
        p.Romans.Any(r => r.Equals(word, StringComparison.OrdinalIgnoreCase) || r.StartsWith(word, StringComparison.Ordinal)) ||
        (p.Minor ? "minor" : "major").StartsWith(word, StringComparison.OrdinalIgnoreCase);

    static bool InNames(string names, string word) =>
        names.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(n => n.StartsWith(word, StringComparison.OrdinalIgnoreCase));

    [RelayCommand] Task Play() => owner.PreviewProgression(this);
    [RelayCommand] Task Use() => owner.UseProgression(this);
}
