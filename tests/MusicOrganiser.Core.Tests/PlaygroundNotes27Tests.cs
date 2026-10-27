using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;
using Xunit.Abstractions;

namespace MusicOrganiser.Core.Tests;

// Solomon's notes from testing the Playground on 2026-10-27 ("buggy and unusable at the moment"): Reharm not showing on
// screen, the notes it changes, the chord track's mute and length, musical choices, and editing notes to the chords.
// Made-up parts only.
[Collection("Global UI")]
public class PlaygroundNotes27Tests(ITestOutputHelper output) : IDisposable
{
    const int Ppq = PhraseAnalyser.Ppq, Bar = 4 * Ppq;
    readonly string _root = Fixtures.TempDir();

    public void Dispose()
    {
        UiWork.InBackground = true;
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    static List<NoteEvent> Chords(params int[][] chords) =>
        chords.SelectMany((c, bar) => c.Select(k => new NoteEvent(bar * (long)Bar, Bar, k, 100))).ToList();

    /// <summary>Each chord struck on every beat of its bar, as a comping part plays it.</summary>
    static List<NoteEvent> Comp(params int[][] chords) =>
        chords.SelectMany((c, bar) => Enumerable.Range(0, 4).SelectMany(beat => c.Select(k => new NoteEvent(bar * (long)Bar + beat * Ppq, Ppq - 20, k, 100)))).ToList();

    static ChordSymbol Chord(string name) => ChordLane.ParseChart(name, out _)![0].Chord;
    static int[] Pcs(IEnumerable<NoteEvent> notes) => notes.Select(n => n.Key % 12).Distinct().Order().ToArray();
    static readonly int[] Am = [57, 60, 64], Dm = [62, 65, 69], E = [56, 59, 64];

    sealed class Page(PlaygroundViewModel p, MainViewModel main, LibraryDb db, IDisposable[] keep) : IDisposable
    {
        public PlaygroundViewModel P => p;
        public MainViewModel Main => main;
        public LibraryDb Db => db;
        public void Dispose() { foreach (var k in keep.Reverse()) k.Dispose(); }
    }

    Page Fresh()
    {
        Ui.Current = new PlaygroundTests.Headless();
        UiWork.InBackground = false;
        var db = new LibraryDb(Path.Combine(_root, "library.db"));
        db.SetSetting(ProjectDetailViewModel.ExportFolderKey, Path.Combine(_root, "export"));
        var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        return new Page(main.Playground, main, db, [db, soundDb, sounds]);
    }

    string Mid(string name, List<NoteEvent> notes)
    {
        var clip = new MidiClip { Name = name, Ppq = Ppq };
        clip.Track(name).AddRange(notes);
        var file = Path.Combine(_root, name + ".mid");
        File.WriteAllBytes(file, MusicOrganiser.Core.Export.MidiWriter.Write(clip));
        return file;
    }

    static void Drop(PlaygroundViewModel p, string file, int row, long start = 0) => p.DropPartCommand.Execute(new PartDrop(new[] { file }, row, start));

    string Show(IEnumerable<NoteEvent> notes) =>
        string.Join("  ", notes.GroupBy(n => n.Start).Select(g => $"{g.Key / (double)Bar:0.##}:[{string.Join(",", g.Select(n => n.Key))}]"));

    // ---- notes 3, 4, 5: Reharm shows on screen, tried and kept ----

    [Fact]
    public async Task A_reharm_tried_shows_on_the_piano_roll_as_it_plays_with_as_many_notes()
    {
        using var page = Fresh();
        var p = page.P;
        p.CurrentSection.Bars = 2;
        Drop(p, Mid("Keys", Comp(Am, Dm)), 0);
        var row = p.Layers[0];
        p.PickClip(row, row.Layer.Clips[0]);
        var before = p.SelectedClip!.Notes.ToList();

        // Am picked, its secondary dominant tried: the piano roll shows the keys playing it, three notes a beat as before.
        p.PickChordCommand.Execute(p.Chords[0]);
        var idea = p.ChordIdeas.First(i => i.Title.StartsWith("Secondary dominant"));
        await p.TryIdea(idea);
        var shown = p.SelectedClip!.Notes.ToList();
        output.WriteLine(Show(before));
        output.WriteLine(Show(shown));
        Assert.NotEqual(Show(before), Show(shown));
        Assert.Equal(before.Count, shown.Count);
        Assert.All(shown.GroupBy(n => n.Start), g => Assert.Equal(3, g.Count()));
        // What shows is what plays.
        Assert.Equal(Show(p.CurrentSection.Layers[0].Notes), Show(shown));
        var a7 = Chord("A7(b9)");
        Assert.All(shown.Where(n => n.Start >= Bar / 2 && n.Start < Bar), n => Assert.True((a7.Mask >> n.Key % 12 & 1) != 0, $"{n.Key} isn't in A7(b9)"));

        // Kept, it stays just as it was shown.
        p.KeepIdeaCommand.Execute(null);
        Assert.Equal(Show(shown), Show(p.SelectedClip!.Notes));
        Assert.Contains("Kept Secondary dominant", p.Message);
    }

    [Fact]
    public async Task Undo_while_trying_takes_the_idea_back_and_an_edit_keeps_it()
    {
        using var page = Fresh();
        var p = page.P;
        p.CurrentSection.Bars = 2;
        Drop(p, Mid("Keys", Comp(Am, Dm)), 0);
        var notes = Show(p.CurrentSection.Layers[0].Notes);
        p.PickChordCommand.Execute(p.Chords[0]);
        await p.TryIdea(p.ChordIdeas.First(i => i.Title.StartsWith("Secondary dominant")));
        Assert.NotEmpty(p.CurrentSection.ChordTrack);

        // Undo is Back while trying: the chords as they were, and the drop still there to undo next.
        p.UndoCommand.Execute(null);
        Assert.Null(p.TryingIdea);
        Assert.Empty(p.CurrentSection.ChordTrack);
        Assert.Equal(notes, Show(p.CurrentSection.Layers[0].Notes));
        Assert.Single(p.Layers);

        // Anything else changed while trying keeps the idea first: one Undo for the change, one for the idea.
        p.PickChordCommand.Execute(p.Chords[0]);
        await p.TryIdea(p.ChordIdeas.First(i => i.Title.StartsWith("Secondary dominant")));
        p.Layers[0].MuteCommand.Execute(null);
        Assert.Null(p.TryingIdea);
        Assert.NotEmpty(p.CurrentSection.ChordTrack);
        p.UndoCommand.Execute(null);
        Assert.False(p.CurrentSection.Layers[0].Muted);
        Assert.NotEmpty(p.CurrentSection.ChordTrack);
        p.UndoCommand.Execute(null);
        Assert.Empty(p.CurrentSection.ChordTrack);
    }

    [Fact]
    public async Task Nothing_tried_is_saved_until_it_is_kept()
    {
        using var page = Fresh();
        var p = page.P;
        p.CurrentSection.Bars = 2;
        Drop(p, Mid("Keys", Comp(Am, Dm)), 0);
        p.PickChordCommand.Execute(p.Chords[0]);
        await p.TryIdea(p.ChordIdeas.First(i => i.Title.StartsWith("Secondary dominant")));
        p.Layers[0].Layer.Volume = p.Layers[0].Layer.Volume;   // nothing changed by hand
        Assert.Empty(new PlaygroundViewModel(page.Db, page.Main).CurrentSection.ChordTrack);
        p.KeepIdeaCommand.Execute(null);
        Assert.NotEmpty(new PlaygroundViewModel(page.Db, page.Main).CurrentSection.ChordTrack);
    }

    [Fact]
    public async Task Trying_another_idea_starts_from_the_chords_as_they_were()
    {
        using var page = Fresh();
        var p = page.P;
        p.CurrentSection.Bars = 2;
        Drop(p, Mid("Keys", Comp(Am, Dm)), 0);
        var notes = Show(p.CurrentSection.Layers[0].Notes);
        p.PickChordCommand.Execute(p.Chords[0]);
        var ideas = p.ChordIdeas.ToList();
        Assert.True(ideas.Count >= 2);
        await p.TryIdea(ideas[0]);
        await p.TryIdea(ideas[1]);
        Assert.Equal(ideas, p.ChordIdeas);   // the list stays put while trying
        Assert.Equal(ideas[1], p.TryingIdea);
        p.BackIdeaCommand.Execute(null);
        Assert.Equal(notes, Show(p.CurrentSection.Layers[0].Notes));
        Assert.Empty(p.CurrentSection.ChordTrack);
    }

    // ---- note 2: the chord track's mute, and going round under longer parts ----

    [Fact]
    public void The_chord_lane_has_its_own_mute_and_the_parts_still_follow_it()
    {
        using var page = Fresh();
        var p = page.P;
        Assert.False(p.ChordTrackMuted);
        p.ChordTrackMuted = true;
        Assert.False(p.HearChordTrack);
        Assert.Equal("0", page.Db.GetSetting(PlaygroundViewModel.HearChordTrackKey));
        p.HearChordTrack = true;
        Assert.False(p.ChordTrackMuted);
    }

    [Fact]
    public void The_chord_track_goes_round_under_a_part_longer_than_it_until_its_cut_short_on_purpose()
    {
        using var page = Fresh();
        var p = page.P;
        p.CurrentSection.Bars = 2;
        p.CurrentSection.ChordTrack = new[] { "Dm7", "G7" }.Select((c, i) => new PlaygroundChord { Start = i * (long)Bar, Length = Bar, Chord = Chord(c) }).ToList();
        List<string> Track() => p.CurrentSection.ChordTrack.OrderBy(c => c.Start).Select(c => $"{c.Start / Bar + 1}:{c.Chord.Name}").ToList();

        // A four-bar part under two bars of chords: they go round under it, and it follows them all the way.
        Drop(p, Mid("Keys", Chords([62, 65, 69, 72], [62, 65, 69, 72], [62, 65, 69, 72], [62, 65, 69, 72])), 0);
        Assert.Equal(["1:Dm7", "2:G7", "3:Dm7", "4:G7"], Track());
        var keys = p.CurrentSection.Layers[0];
        Assert.Equal(7, keys.Notes.Where(n => n.Start == 3 * Bar).Min(n => n.Key) % 12);   // the fourth bar plays G7

        // The last chord taken off: it stays off, even when the part is stretched further.
        p.PickChordCommand.Execute(p.Chords[^1]);
        Assert.True(p.RunShortcut("playground.removeChord"));
        Assert.Equal(["1:Dm7", "2:G7", "3:Dm7"], Track());
        var row = p.Layers[0];
        p.PickClip(row, row.Layer.Clips[0]);
        p.CurrentSection.Bars = 6;
        row.Layer.Clips[0].Length = 6 * Bar;
        p.SelectedClip!.FitAgainCommand.Execute(null);
        Assert.Equal(["1:Dm7", "2:G7", "3:Dm7"], Track());
    }

    [Fact]
    public async Task Chords_put_on_an_empty_chord_track_go_round_under_the_parts_already_there()
    {
        using var page = Fresh();
        var p = page.P;
        p.CurrentSection.Bars = 4;
        Drop(p, Mid("Melody", Enumerable.Range(0, 16).Select(i => new NoteEvent(i * (long)Ppq, Ppq, 72 + i % 3, 100)).ToList()), 0);
        Assert.Equal(4L * Bar, p.CurrentSection.Layers[0].Clips.Max(c => c.End));
        // Two bars of chords from Bitwig, Dm7 then G7, under four bars of melody.
        p.BitwigClips = _ => Task.FromResult<IReadOnlyList<BitwigClipNotes>>([new BitwigClipNotes("arranger", "Keys", false, 0, 8, false,
            [.. new[] { 62, 65, 69, 72 }.Select(k => new BitwigNote(0, 4, k, 90)), .. new[] { 55, 59, 62, 65 }.Select(k => new BitwigNote(4, 4, k, 90))])]);
        await p.ChordsFromBitwigCommand.ExecuteAsync(null);
        Assert.Equal(["Dm7", "G7", "Dm7", "G7"], p.CurrentSection.ChordTrack.OrderBy(c => c.Start).Select(c => c.Chord.Name));
        Assert.Equal(4L * Bar, p.CurrentSection.ChordTrack.Max(c => c.End));
        Assert.Equal(4, p.CurrentSection.Bars);
    }

    // ---- note 8: double-click the chord lane to add a chord ----

    [Fact]
    public void A_double_click_on_the_chord_lane_adds_a_chord_there_and_picks_it()
    {
        using var page = Fresh();
        var p = page.P;
        p.CurrentSection.Bars = 4;
        List<string> Track() => p.CurrentSection.ChordTrack.OrderBy(c => c.Start).Select(c => $"{c.Start / Bar + 1}:{c.Chord.Name}").ToList();

        // Nothing anywhere: the home chord (C with no key yet), a bar long, picked for Reharm.
        p.AddChordAtCommand.Execute(Bar / 3);
        Assert.Equal(["1:C"], Track());
        Assert.Equal("C", p.PickedChord?.Name);
        Assert.True(p.CanUndo);

        // After it, what usually comes next.
        p.AddChordAtCommand.Execute(Bar + Bar / 2);
        Assert.Equal(2, p.CurrentSection.ChordTrack.Count);
        Assert.Equal(Bar, p.CurrentSection.ChordTrack[1].Start);
        Assert.Equal(Bar, p.PickedChord?.Timed.Start);
        Assert.Contains("what usually comes after C", p.Message);

        // Inside a longer chord: cut there, the second half picked to change.
        p.CurrentSection.ChordTrack[1].Length = 3 * Bar;
        p.AddChordAtCommand.Execute(3 * Bar + 10);
        var second = p.CurrentSection.ChordTrack[1].Chord.Name;
        Assert.Equal(["1:C", $"2:{second}", $"4:{second}"], Track());
        Assert.Equal(3 * Bar, p.PickedChord?.Timed.Start);
        p.UndoCommand.Execute(null);
        Assert.Equal(["1:C", $"2:{second}"], Track());
    }

    [Fact]
    public void A_double_click_on_a_chord_heard_from_the_parts_puts_it_on_the_chord_track()
    {
        using var page = Fresh();
        var p = page.P;
        p.CurrentSection.Bars = 2;
        Drop(p, Mid("Keys", Chords([57, 60, 64], [62, 65, 69])), 0);
        Assert.Empty(p.CurrentSection.ChordTrack);
        p.AddChordAtCommand.Execute(Bar + 5);
        Assert.Equal("Dm", Assert.Single(p.CurrentSection.ChordTrack).Chord.Name);
        Assert.True(p.PickedChord?.IsSet);
    }

    // ---- notes 9, 10: musical choices ----

    [Fact]
    public void A_melody_over_the_chord_track_stays_in_its_key_and_only_its_clashes_move()
    {
        using var page = Fresh();
        var p = page.P;
        p.CurrentSection.Bars = 4;
        p.CurrentSection.ChordTrack = new[] { "C", "F", "G", "C" }.Select((c, i) => new PlaygroundChord { Start = i * (long)Bar, Length = Bar, Chord = Chord(c) }).ToList();
        // C held for two beats, then E and G: one bar going round under C F G C.
        Drop(p, Mid("Melody", [new NoteEvent(0, 2 * Ppq, 72, 100), new NoteEvent(2 * Ppq, Ppq, 76, 100), new NoteEvent(3 * Ppq, Ppq, 79, 100)]), 0);
        var row = p.Layers[0];
        var clip = row.Layer.Clips[0];
        p.PickClip(row, clip);
        clip.Length = 4 * Bar;
        p.SelectedClip!.FitAgainCommand.Execute(null);
        output.WriteLine($"{clip.Way}: fits {string.Join(",", clip.Fits)}  {Show(clip.Render())}");
        Assert.Equal(FollowWay.Nudge, clip.Way);
        // Every go round moves by the same step (here none), never a bar at a time to each chord...
        Assert.Single(clip.Fits.Distinct());
        var played = clip.Render();
        int[] Bar_(int bar) => played.Where(n => n.Start >= bar * Bar && n.Start < (bar + 1) * Bar).Select(n => n.Key).ToArray();
        Assert.Equal([72, 76, 79], Bar_(0));
        Assert.Equal([72, 76, 79], Bar_(1));   // C E G all sit on F
        // ...and over G only the held C, its avoid note, moves: to B, not the whole bar to G B D.
        Assert.Equal([71, 76, 79], Bar_(2));
    }

    // ---- note 10: pick a few notes and snap them to the chords ----

    [Fact]
    public void Picked_notes_snap_onto_the_chords_and_the_rest_stay_where_they_are()
    {
        using var page = Fresh();
        var p = page.P;
        p.CurrentSection.Bars = 2;
        p.CurrentSection.ChordTrack = new[] { "C", "F" }.Select((c, i) => new PlaygroundChord { Start = i * (long)Bar, Length = Bar, Chord = Chord(c) }).ToList();
        // An eighth on each beat (quick notes, so following the chords leaves them as they are): E G C♯ E over C, A C B A over F.
        int[] keys = [76, 79, 73, 76, 69, 72, 71, 69];
        Drop(p, Mid("Melody", keys.Select((k, i) => new NoteEvent(i * (long)Ppq, Ppq / 2, k, 100)).ToList()), 0);
        var row = p.Layers[0];
        p.PickClip(row, row.Layer.Clips[0]);
        int[] Keys() => p.SelectedClip!.Notes.Select(n => n.Key).ToArray();
        Assert.Equal(keys, Keys());
        var fits = p.SelectedClip!.FitsChords!;
        Assert.False(fits(p.SelectedClip.Notes[2]));   // C♯ over C
        Assert.False(fits(p.SelectedClip.Notes[6]));   // B over F: its ♯11, a colour, not one of its notes
        Assert.True(fits(p.SelectedClip.Notes[7]));

        // Only the C♯ picked: it goes to C, and the B stays.
        p.PickedNotes = [p.SelectedClip.Notes[2]];
        p.SnapToChordsCommand.Execute(null);
        Assert.Equal([76, 79, 72, 76, 69, 72, 71, 69], Keys());
        Assert.Equal(new NoteEvent(2 * Ppq, Ppq / 2, 72, 100), Assert.Single(p.PickedNotes!));   // still picked, where it went
        Assert.StartsWith("1 note moved onto the chords", p.Message);

        // Nothing picked: all of them, so the B goes to the nearest of F's notes, C.
        p.PickedNotes = null;
        p.SnapToChordsCommand.Execute(null);
        Assert.Equal([76, 79, 72, 76, 69, 72, 72, 69], Keys());
        p.SnapToChordsCommand.Execute(null);
        Assert.Equal("Its notes all go with the chords already.", p.Message);

        // Undo puts them back a step at a time.
        p.UndoCommand.Execute(null);
        Assert.Equal([76, 79, 72, 76, 69, 72, 71, 69], Keys());
        p.UndoCommand.Execute(null);
        Assert.Equal(keys, Keys());
    }

    [Fact]
    public void Snapping_needs_a_clip_and_notes_picked_go_with_their_clip()
    {
        using var page = Fresh();
        var p = page.P;
        p.CurrentSection.Bars = 1;
        p.SnapToChordsCommand.Execute(null);
        Assert.StartsWith("Pick a clip first", p.Message);

        Drop(p, Mid("Lead", [new NoteEvent(0, Ppq, 73, 100), new NoteEvent(Ppq, Ppq, 75, 100)]), 0);
        Drop(p, Mid("Keys", Chords([57, 60, 64])), 1);
        var lead = p.Layers[0];
        p.PickClip(lead, lead.Layer.Clips[0]);
        Assert.NotNull(p.SelectedClip!.FitsChords);   // the Am heard from the keys
        p.PickedNotes = [p.SelectedClip.Notes[0]];

        // The same clip picked again keeps its notes picked; another clip starts with none.
        p.PickClip(lead, lead.Layer.Clips[0]);
        Assert.Single(p.PickedNotes!);
        var keys = p.Layers[1];
        p.PickClip(keys, keys.Layer.Clips[0]);
        Assert.Null(p.PickedNotes);
    }

    [Fact]
    public void Snapping_finds_the_nearest_key_that_fits_the_way_the_note_was_going_first()
    {
        static bool C(NoteEvent n) => n.Key % 12 is 0 or 4 or 7;
        var d = new NoteEvent(0, Ppq, 62, 100);
        Assert.Equal(60, ClipRow.NearestFit(d, C, -1));
        Assert.Equal(64, ClipRow.NearestFit(d, C, +1));
        Assert.Equal(64, ClipRow.NearestFit(d with { Key = 64 }, C, -1));   // already fits: stays
        Assert.Equal(67, ClipRow.NearestFit(d with { Key = 66 }, C, -1));   // a semitone up beats two down
        Assert.Equal(62, ClipRow.NearestFit(d, _ => false, -1));            // nothing fits: stays
    }

    [Fact]
    public void Snap_to_chord_is_a_switch_that_stays_and_drums_dont_snap()
    {
        using var page = Fresh();
        var p = page.P;
        Assert.False(p.SnapChord);
        Assert.True(p.RunShortcut("playground.snapChord"));
        Assert.True(p.SnapChord);
        Assert.Equal("1", page.Db.GetSetting(PlaygroundViewModel.SnapChordKey));

        p.CurrentSection.Bars = 1;
        Drop(p, Mid("Keys", Chords([57, 60, 64])), 0);
        Drop(p, Mid("Kick", Enumerable.Range(0, 4).Select(i => new NoteEvent(i * (long)Ppq, Ppq / 2, 36, 100)).ToList()), 1);
        var row = p.Layers[1];
        row.Layer.Clips[0].Role = MidiRoles.Drums;
        p.PickClip(row, row.Layer.Clips[0]);
        Assert.True(p.SelectedClip!.IsDrums);
        Assert.Null(p.SelectedClip.FitsChords);
        Assert.False(p.RunShortcut("playground.snapToChords"));
    }
}

// How parts follow the chord track: the choices a player would make (Solomon, 2026-10-27: "seems to be making silly
// decisions musically", "its not favouring good choices").
public class FollowChoicesTests
{
    const int Ppq = PhraseAnalyser.Ppq, Bar = 4 * Ppq;
    static ChordSymbol Chord(string name) => ChordLane.ParseChart(name, out _)![0].Chord;
    static List<TimedChord> Track(params string[] names) => names.Select((n, i) => new TimedChord(i * (long)Bar, Bar, Chord(n))).ToList();
    static List<NoteEvent> Line(params int[] keys) => keys.Select((k, i) => new NoteEvent(i * (long)Ppq, Ppq, k, 100)).ToList();
    static List<NoteEvent> TwoBars(List<NoteEvent> bar) => bar.Concat(bar.Select(n => n with { Start = n.Start + Bar })).ToList();
    static int[] Second(IEnumerable<NoteEvent> notes) => notes.Where(n => n.Start >= Bar).Select(n => n.Key).ToArray();

    [Fact]
    public void A_bassline_plays_the_new_chords_root_going_down_a_tritone()
    {
        // G G D G under G7, then a tritone sub: the bass plays D♭, down where a bass plays, not G under D♭9.
        var bass = TwoBars(Line(43, 43, 50, 43));
        Assert.Equal([37, 37, 44, 37], Second(ChordFollow.Apply(bass, Track("G7", "Db9"), FollowWay.Auto, MidiRoles.Bass)));
    }

    [Fact]
    public void Moving_with_the_root_plays_the_new_chords_quality()
    {
        // A C major riff moved onto D minor: D F A F, in the key, not D F# A F#.
        var riff = TwoBars(Line(60, 64, 67, 64));
        Assert.Equal([62, 65, 69, 65], Second(ChordFollow.Apply(riff, Track("C", "Dm"), FollowWay.Root, MidiRoles.Melody)));
        // Onto C minor, the same root: the 3rd goes minor, where before nothing moved.
        Assert.Equal([60, 63, 67, 63], Second(ChordFollow.Apply(riff, Track("C", "Cm"), FollowWay.Root, MidiRoles.Melody)));
        // An A minor riff onto D7: D F# A.
        Assert.Equal([62, 66, 69, 66], Second(ChordFollow.Apply(TwoBars(Line(57, 60, 64, 60)), Track("Am", "D7"), FollowWay.Root, MidiRoles.Melody)));
    }

    [Fact]
    public void A_held_note_needs_to_sit_well_and_a_quick_one_only_not_to_fight()
    {
        static NoteEvent N(long start, long length, int key) => new(start, length, key, 100);
        List<NoteEvent> Nudged(string chord, params NoteEvent[] notes) => ChordFollow.Apply(notes, Track(chord), FollowWay.Nudge, MidiRoles.Melody);
        // F held a bar over C, its avoid note: to E.
        Assert.Equal(64, Nudged("C", N(0, Bar, 65)).Single().Key);
        // B held over Dm7 is its Dorian 6: it stays. So does a 9 on the beat.
        Assert.Equal(71, Nudged("Dm7", N(0, Bar, 71)).Single().Key);
        Assert.Equal(62, Nudged("C", N(0, Ppq, 62)).Single().Key);
        // A quick passing note stays, and so does an avoid note off the beat for less than a beat.
        Assert.Equal(66, Nudged("C", N(Ppq / 2, Ppq / 2, 66)).Single().Key);
        Assert.Equal(65, Nudged("C", N(Ppq / 2, Ppq * 3 / 4, 65)).Single().Key);
        // The blue 3rd held over a major chord rubs: it goes to the 3rd... or the 9, whichever's nearer and sits best.
        Assert.Contains(Nudged("C", N(0, Bar, 63)).Single().Key, new[] { 62, 64 });
    }
}

// The revoicer on its own: the musical choices it makes for one hit.
public class RevoicerTests(ITestOutputHelper output)
{
    static ChordSymbol Chord(string name) => ChordLane.ParseChart(name, out _)![0].Chord;
    static string Names(IEnumerable<int> keys) => string.Join(" ", keys.Select(k => ChordSymbol.Names[k % 12] + (k / 12 - 1)));

    int[] Voice(int[] keys, string from, string to)
    {
        var placed = Revoicer.Keys(keys, Chord(from), Chord(to));
        output.WriteLine($"{from} {Names(keys)} → {to}: {Names(placed)}");
        return placed;
    }

    [Theory]
    [InlineData(new[] { 57, 60, 64 }, "Am", "A7(b9)")]
    [InlineData(new[] { 62, 65, 69, 72 }, "Dm7", "Dm9")]
    [InlineData(new[] { 57, 60, 64 }, "Am", "Am9")]
    [InlineData(new[] { 55, 59, 62, 65 }, "G7", "G13")]
    [InlineData(new[] { 48, 52, 55, 59 }, "Cmaj7", "Cmaj9")]
    [InlineData(new[] { 55, 59, 62, 65 }, "G7", "Cmaj7")]
    [InlineData(new[] { 60, 64, 67 }, "C", "Am")]
    [InlineData(new[] { 48, 64, 67, 71 }, "Cmaj7", "Fmaj7")]
    [InlineData(new[] { 48, 55 }, "C5", "F")]
    [InlineData(new[] { 60, 64, 67 }, "C", "G/B")]
    [InlineData(new[] { 43, 53, 55, 59, 62 }, "G7", "Db9")]
    [InlineData(new[] { 64, 67, 71, 74 }, "Em7", "A7")]
    public void Each_note_moves_as_little_as_it_can_and_the_new_chord_s_telling_notes_come_first(int[] keys, string from, string to)
    {
        var placed = Voice(keys, from, to);
        var chord = Chord(to);
        Assert.Equal(keys.Length, placed.Length);
        Assert.Equal(placed.Order(), placed);
        Assert.Equal(placed.Length, placed.Distinct().Count());
        // Every note is the new chord's, and its 3rd and 7th are there when there's room.
        Assert.All(placed, k => Assert.True((chord.Mask >> k % 12 & 1) != 0 || k % 12 == chord.Bass, $"{k} isn't in {to}"));
        var telling = Revoicer.Telling(chord);
        if (keys.Length >= 3 && Chord(from).Family != ChordFamily.Power) Assert.Contains(telling[0], placed.Select(k => k % 12));
        if (keys.Length >= 3 && (chord.Has(10) || chord.Has(11))) Assert.Contains(telling[1], placed.Select(k => k % 12));
    }

    [Fact]
    public void A_voicing_that_already_plays_the_chord_stays_and_one_that_doesnt_changes()
    {
        // E G B D over the bass's C is already Cmaj9, though alone it's heard as Em7.
        Assert.True(Revoicer.Voices([64, 67, 71, 74], Chord("Cmaj9"), fromRoot: true));
        // A C E is Am, but not Am9: richer chords change it.
        Assert.True(Revoicer.Voices([57, 60, 64], Chord("Am"), fromRoot: true));
        Assert.False(Revoicer.Voices([57, 60, 64], Chord("Am9"), fromRoot: true));
        // C E G built up from C isn't an A minor, though every note sits on it, nor an Am7 with nothing under it.
        Assert.False(Revoicer.Voices([60, 64, 67], Chord("Am"), fromRoot: true));
        Assert.False(Revoicer.Voices([60, 64, 67, 72], Chord("Am7"), fromRoot: true));
        Assert.False(Revoicer.Voices([64, 67, 72], Chord("Am7"), fromRoot: false));
        // A note the chord fights isn't kept because it has a number: F over C major.
        Assert.False(Revoicer.Voices([60, 65, 67], Chord("C"), fromRoot: true));
        // Bm7 heard as D6 is the same notes: nothing to change.
        Assert.True(Revoicer.Voices([59, 62, 66, 69], Chord("D6"), fromRoot: true));
    }

    [Fact]
    public void A_power_chord_stays_a_power_chord_and_a_left_hand_bass_plays_the_new_bass()
    {
        Assert.Equal([5, 0], Voice([48, 55], "C5", "F").Select(k => k % 12));
        var placed = Voice([36, 60, 64, 67], "C", "Am7");
        Assert.Equal(9, placed[0] % 12);
        Assert.True(placed[1] - placed[0] >= 7);
    }

    [Fact]
    public void An_arp_keeps_its_shape_and_plays_a_richer_chord_s_colours()
    {
        const int Ppq = PhraseAnalyser.Ppq, Bar = 4 * Ppq;
        // C E G C in sixteenths for two bars, the second bar under a richer chord.
        var arp = Enumerable.Range(0, 32).Select(i => new NoteEvent(i * (long)(Ppq / 4), Ppq / 4, new[] { 48, 52, 55, 60 }[i % 4], 100)).ToList();
        int[] Bar2(string to)
        {
            var played = ChordFollow.Apply(arp, [new TimedChord(0, Bar, Chord("C")), new TimedChord(Bar, Bar, Chord(to))], FollowWay.Auto, MidiRoles.Arp);
            var keys = played.Where(n => n.Start >= Bar).Take(4).Select(n => n.Key).ToArray();
            output.WriteLine($"C E G C → {to}: {Names(keys)}");
            Assert.Equal(32, played.Count);
            return keys;
        }
        // Over Cmaj9 the doubled notes play its 7th and 9, the root still first: C E B D.
        Assert.Equal([48, 52, 59, 62], Bar2("Cmaj9"));
        // Over Fmaj9 it moves up to F, as it does to F, and plays F A E G, a maj9 arp.
        Assert.Equal([53, 57, 64, 67], Bar2("Fmaj9"));
        // Over plain F it's F A C F, the same shape, and over F/A the root still starts it (the A is the bass's).
        Assert.Equal([53, 57, 60, 65], Bar2("F"));
        Assert.Equal([53, 57, 60, 65], Bar2("F/A"));
        // Over Am7 it moves to A, the doubled root playing the 7th: A C E G. A C major arp isn't an Am7 with nothing under it.
        Assert.Equal([0, 4, 7, 9], Bar2("Am7").Select(k => k % 12).Order());
    }
}
