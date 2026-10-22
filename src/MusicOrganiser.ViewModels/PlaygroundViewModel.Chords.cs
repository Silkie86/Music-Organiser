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
        if (_playing) _ = PlayAsync(keepPlace: true);
        PlayInBitwigSoon();
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

    /// <summary>
    /// A chord block stretched by either edge or slid along (Solomon, 2026-10-22: "resize the chord clips in the chord track
    /// and everything updates on the fly"): each step is worked out from the track as it was when the drag began, so going
    /// back over a neighbour gives it back. A neighbour touching the edge being dragged follows it; one it's dragged over is
    /// cut back. The whole drag is one Undo.
    /// </summary>
    [RelayCommand]
    void DragChord(ChordDrag? drag)
    {
        if (drag is null) return;
        if (_chordDragBase is null || drag.Gesture != _chordGesture)
        {
            if (Section.ChordTrack.All(c => c.Start != drag.From)) return;
            Remember();
            _chordDragBase = Section.ChordTrack.Select(c => c.Copy()).ToList();
            _chordGesture = drag.Gesture;
        }
        var track = _chordDragBase.Select(c => c.Copy()).ToList();
        if (drag.Done) _chordDragBase = null;
        if (track.FirstOrDefault(c => c.Start == drag.From) is not { } moved) return;
        var least = Math.Min(Grid, (long)Ppq);
        var start = Math.Clamp(drag.Start, 0, Math.Max(0, Length - least));
        var length = Math.Clamp(drag.Length, least, Length - start);
        var (was, wasEnd) = (moved.Start, moved.End);
        if (start != was && start + length == wasEnd && track.FirstOrDefault(c => c != moved && c.End == was) is { } before)
        {
            // Its left edge dragged: the chord before it ends where it now starts.
            start = Math.Max(start, before.Start + least);
            length = wasEnd - start;
            before.Length = start - before.Start;
        }
        else if (start == was && start + length != wasEnd && track.FirstOrDefault(c => c != moved && c.Start == wasEnd) is { } after)
        {
            // Its right edge: the chord after it starts where it now ends.
            length = Math.Min(length, after.End - least - start);
            after.Length = after.End - (start + length);
            after.Start = start + length;
        }
        moved.Start = start;
        moved.Length = length;
        track = CutOut(track.Where(c => c != moved), start, start + length).Append(moved).OrderBy(c => c.Start).ToList();
        if (Same(track, Section.ChordTrack)) return;
        Section.ChordTrack = track;
        Changed();
    }

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

    /// <summary>Whether the chord track has chords anywhere from <paramref name="from"/> to <paramref name="to"/>.</summary>
    static bool OnChordTrack(PlaygroundSection section, long from, long to) => section.ChordTrack.Any(c => c.Start < to && c.End > from);

    /// <summary>The chords of <paramref name="lane"/> from <paramref name="from"/> to <paramref name="to"/> put on the chord track there, over what it had.</summary>
    static void PutOnChordTrack(PlaygroundSection section, IReadOnlyList<TimedChord> lane, long from, long to)
    {
        var put = lane.Where(c => c.End > from && c.Start < to)
                      .Select(c => PlaygroundChord.Of(new TimedChord(Math.Max(c.Start, from), Math.Min(c.End, to) - Math.Max(c.Start, from), c.Chord)));
        section.ChordTrack = CutOut(section.ChordTrack, from, to).Concat(put).OrderBy(c => c.Start).ToList();
    }

    /// <summary>
    /// New chords from <paramref name="from"/> to <paramref name="to"/>: on the chord track where it has chords there, else
    /// played by the chord parts (<see cref="Rechord"/>).
    /// </summary>
    /// <returns>Whether it made a new chord part's track.</returns>
    static bool SetChords(PlaygroundSection section, IReadOnlyList<TimedChord> lane, long from, long to)
    {
        if (!OnChordTrack(section, from, to)) return Rechord(section, lane, from, to);
        PutOnChordTrack(section, lane, from, to);
        return false;
    }

    /// <summary>The chord-lane menu's "Remove this chord": the picked chord off the chord track.</summary>
    void RemoveChord()
    {
        if (PickedChord is not { IsSet: true } picked) return;
        Remember();
        Section.ChordTrack = CutOut(Section.ChordTrack, picked.Timed.Start, picked.Timed.End).ToList();
        Changed();
        Message = $"{picked.Name} taken off the chord track. Undo puts it back.";
    }

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
        var (from, to) = (start, start + length);
        var lane = Harmony.Lane.Where(c => c.Start < to && c.End > from).ToList();
        if (lane.Count == 0) { Message = "No chords heard there to keep."; return; }
        Remember();
        PutOnChordTrack(Section, lane, from, to);
        Changed();
        Message = "The chords heard are on the chord track now: drag their edges to stretch them. Undo takes them back off.";
    }

    /// <summary>
    /// A chord on the end of the chord track, a bar long: after its last chord, or (with a highlight) at its start when
    /// the track has none there yet.
    /// </summary>
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

    /// <summary>The progressions that match the search and length, in the idea's key, the ones most songs play first.</summary>
    void ShowProgressions()
    {
        if (_progressionsFrom is null) return;
        var key = Harmony.Key;
        var words = ProgressionSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bars = int.TryParse(ProgressionBars.Split(' ')[0], out var b) ? b : 0;
        var playing = Progressions.FirstOrDefault(r => r.IsPlaying)?.Progression;
        Progressions.Clear();
        var shown = 0;
        foreach (var p in _progressions)
        {
            if (bars > 0 && p.Bars != bars) continue;
            var row = new ProgressionRow(this, p, key);
            if (words.Length > 0 && !words.All(row.Matches)) continue;
            row.IsPlaying = p == playing;
            Progressions.Add(row);
            if (++shown >= MostProgressions) break;
        }
        ProgressionsText = _progressions.Count == 0 ? "No chord parts read yet: add projects or MIDI folders on the MIDI page."
            : shown == 0 ? $"None like that among the {_progressions.Count:N0} progressions in your chord parts. Try other chords or numerals."
            : $"{_progressions.Count:N0} progressions in your chord parts, the ones most songs play first{(key is null ? "" : $", in {key.Name}")}.";
        _progressionsKey = key?.Name;
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
    async Task<List<TimedChord>> ProgressionLaneAsync(Progression p)
    {
        var notes = await ProgressionNotesAsync(p);
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
            if (round.Count == 0 || !SameKind(round[^1].Chord, c.Chord)) changes++;
            if (changes > p.Chords.Count) break;
            round.Add(c with { Start = c.Start - from });
        }
        var end = (round[^1].End + Bar - 1) / Bar * Bar;
        round[^1] = round[^1] with { Length = end - round[^1].Start };
        return round;
    }

    static bool SameKind(ChordSymbol a, ChordSymbol b) => a.Root == b.Root && ProgressionModel.Kind(a) == ProgressionModel.Kind(b);

    /// <summary>A progression heard on its own, its example part moved into the idea's key; clicked again, it stops.</summary>
    internal async Task PreviewProgression(ProgressionRow row)
    {
        if (row.IsPlaying) { StopPlaying(); return; }
        StopPlaying();
        var notes = await ProgressionNotesAsync(row.Progression);
        if (notes.Count == 0)
            notes = Voicer.Plain(await ProgressionLaneAsync(row.Progression));
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
    /// the track is empty, else after its last chord. The timeline grows to fit. Where they went; null when it's full.
    /// </summary>
    (long From, long To)? PlaceOnChordTrack(IReadOnlyList<TimedChord> lane)
    {
        var length = lane[^1].End;
        var at = Loop?.Start ?? (HasChordTrack ? Section.ChordTrack.Max(c => c.End) : 0);
        if (at >= (long)MaxBars * Bar) { Message = $"The timeline is full ({MaxBars} bars)."; return null; }
        // Over a highlight it goes round to fill it.
        var to = Loop is { } r ? r.End : at + length;
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
                      (clip.Cut ? " The clip is longer than 64 bars, so only its first 64 came across." : "") + " Undo takes them off.";
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
    static List<ChordSymbol> Changes(IEnumerable<TimedChord> lane)
    {
        var list = new List<ChordSymbol>();
        foreach (var c in lane)
            if (list.Count == 0 || !SameKind(list[^1], c.Chord)) list.Add(c.Chord);
        return list;
    }

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
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PlayText))] bool _isPlaying;
    public string PlayText => IsPlaying ? "■" : "▶";

    /// <summary>Whether a search word is in its chord names, its Roman numerals, or says major or minor.</summary>
    public bool Matches(string word) =>
        Names.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(n => n.StartsWith(word, StringComparison.OrdinalIgnoreCase)) ||
        progression.Romans.Any(r => r.Equals(word, StringComparison.OrdinalIgnoreCase) || r.StartsWith(word, StringComparison.Ordinal)) ||
        Mode.StartsWith(word, StringComparison.OrdinalIgnoreCase);

    [RelayCommand] Task Play() => owner.PreviewProgression(this);
    [RelayCommand] Task Use() => owner.UseProgression(this);
}
