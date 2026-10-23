using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;
using Xunit.Abstractions;

namespace MusicOrganiser.Core.Tests;

// The 2026-10-23 review of the chords-first Playground: each test is one of its findings, by number.
[Collection("Global UI")]
public class PlaygroundReviewTests(ITestOutputHelper output) : IDisposable
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

    static ChordSymbol Chord(string name) => ChordLane.ParseChart(name, out _)![0].Chord;
    static PlaygroundChord Block(string name, long start, long length) => new() { Start = start, Length = length, Chord = Chord(name) };
    static readonly int[] Dm7 = [38, 53, 57, 60];

    string Show(IEnumerable<NoteEvent> notes) =>
        string.Join("  ", notes.GroupBy(n => n.Start).Select(g => $"{g.Key}:[{string.Join(",", g.Select(n => n.Key))}]"));

    [Fact]
    public void Finding2_first_round_of_a_cut_clip_follows_the_chords_it_plays_over()
    {
        // Chord track Dm7 bars 1-2, G7 bars 3-4; a 4-bar Dm7 keys clip, cut in two at bar 3 (as Piece does).
        var clip = PlaygroundClip.Of("Keys", MidiRoles.Chords, Chords(Dm7, Dm7, Dm7, Dm7), 4 * Bar, 0, 4 * Bar);
        clip.PlaysAs = MidiRoles.Chords;
        clip.Under = [Block("Dm7", 0, 2 * Bar), Block("G7", 2 * Bar, 2 * Bar)];
        var right = PlaygroundViewModel.Piece(clip, 2 * Bar, 4 * Bar);
        var plays = right.Render();
        var shown = right.FirstRound();
        output.WriteLine("plays (timeline): " + Show(plays));
        output.WriteLine("piano roll round: " + Show(shown));
        // The audible part of the piano roll's go round is what plays.
        var audible = shown.Where(n => n.Start >= right.Start - right.RepeatAt(0)).Select(n => n with { Start = n.Start + right.RepeatAt(0) }).ToList();
        Assert.Equal(plays, audible);
    }

    [Fact]
    public void Finding3_a_pushed_chord_stays_whole_and_plays_the_chord_it_pushes_into()
    {
        // Track F then Dm; the part plays C, then pushes Am a 16th early (struck at 360, held into bar 2).
        var part = new List<NoteEvent>
        {
            new(0, 360, 48, 100), new(0, 360, 52, 100), new(0, 360, 55, 100),
            new(360, 408, 45, 100), new(360, 408, 48, 100), new(360, 408, 52, 100),
        };
        var track = new List<TimedChord> { new(0, Bar, Chord("F")), new(Bar, Bar, Chord("Dm")) };
        var played = ChordFollow.Apply(part, track, FollowWay.Auto, MidiRoles.Chords);
        output.WriteLine("in:  " + Show(part));
        output.WriteLine("out: " + Show(played) + "   lengths " + string.Join(",", played.Select(n => n.Length)));
        Assert.DoesNotContain(played, n => n.Start == Bar);   // not struck again on the beat
    }

    PlaygroundViewModel Playground(LibraryDb db, SoundOrganiser sounds) =>
        new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data")).Playground;

    [Fact]
    public void Finding4_dragging_the_first_chords_left_edge_left_doesnt_stretch_its_right_edge()
    {
        Ui.Current = new PlaygroundTests.Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var p = Playground(db, sounds);
        p.CurrentSection.ChordTrack = [Block("C", 0, Bar), Block("F", Bar, Bar)];
        // C's left edge pulled a beat to the left of bar 1, as the chord lane sends it (start -96, length 480).
        p.DragChordCommand.Execute(new ChordDrag(0, -Ppq, Bar + Ppq, 1, true));
        output.WriteLine(string.Join("  ", p.CurrentSection.ChordTrack.Select(c => $"{c.Chord.Name} {c.Start}+{c.Length}")));
        Assert.Equal([(0L, (long)Bar), (Bar, Bar)], p.CurrentSection.ChordTrack.Select(c => (c.Start, c.Length)));
    }

    [Fact]
    public void Finding4_dragging_a_left_edge_far_past_bar_1_doesnt_delete_the_next_chord()
    {
        Ui.Current = new PlaygroundTests.Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var p = Playground(db, sounds);
        p.CurrentSection.ChordTrack = [Block("C", Bar, Bar), Block("F", 2 * Bar, Bar)];
        p.DragChordCommand.Execute(new ChordDrag(Bar, -Bar, 3 * Bar, 2, true));
        output.WriteLine(string.Join("  ", p.CurrentSection.ChordTrack.Select(c => $"{c.Chord.Name} {c.Start}+{c.Length}")));
        Assert.Contains(p.CurrentSection.ChordTrack, c => c.Chord.Name == "F");
    }

    [Fact]
    public void Finding1_putting_the_heard_chords_on_the_chord_track_leaves_the_parts_as_they_are()
    {
        Ui.Current = new PlaygroundTests.Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var p = Playground(db, sounds);
        p.CurrentSection.Bars = 2;
        string Mid(string name, List<NoteEvent> notes)
        {
            var clip = new MidiClip { Name = name, Ppq = Ppq };
            clip.Track(name).AddRange(notes);
            var file = Path.Combine(_root, name + ".mid");
            File.WriteAllBytes(file, MusicOrganiser.Core.Export.MidiWriter.Write(clip));
            return file;
        }
        // Rootless voicings over a bass: keys E G B D then A C E G, bass C then F (Cmaj9, Fmaj9). Bass first, so nothing is fitted.
        var keys = Chords([52, 55, 59, 62], [57, 60, 64, 67]);
        var bass = new List<NoteEvent> { new(0, Bar, 36, 100), new(Bar, Bar, 41, 100) };
        p.DropPartCommand.Execute(new PartDrop(new[] { Mid("Bass", bass) }, 0, 0));
        p.DropPartCommand.Execute(new PartDrop(new[] { Mid("Keys", keys) }, 1, 0));
        output.WriteLine("roles: " + string.Join(", ", p.Layers.Select(l => $"{l.Layer.Name}={l.Layer.Role}")));
        output.WriteLine("heard: " + string.Join(" ", p.Chords.Select(c => c.Name)));
        var before = p.Layers.Select(l => Show(l.Layer.Notes)).ToList();
        Assert.True(p.RunShortcut("playground.keepHeardChords"));
        output.WriteLine("track: " + string.Join(" ", p.CurrentSection.ChordTrack.Select(c => c.Chord.Name)));
        var after = p.Layers.Select(l => Show(l.Layer.Notes)).ToList();
        for (var i = 0; i < before.Count; i++) output.WriteLine($"{p.Layers[i].Layer.Name}: {before[i]}  ->  {after[i]}");
        Assert.Equal(before, after);
    }

    // ---- the rest of the review's findings that a test can show ----

    (PlaygroundViewModel P, LibraryDb Db, SoundLibrary SoundDb, SoundOrganiser Sounds) Fresh()
    {
        Ui.Current = new PlaygroundTests.Headless();
        UiWork.InBackground = false;
        var db = new LibraryDb(Path.Combine(_root, "library.db"));
        var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        var sounds = new SoundOrganiser(soundDb);
        return (Playground(db, sounds), db, soundDb, sounds);
    }

    string Mid(string name, List<NoteEvent> notes)
    {
        var clip = new MidiClip { Name = name, Ppq = Ppq };
        clip.Track(name).AddRange(notes);
        var file = Path.Combine(_root, name + ".mid");
        File.WriteAllBytes(file, MusicOrganiser.Core.Export.MidiWriter.Write(clip));
        return file;
    }

    static List<NoteEvent> Tune(int bars) =>
        Enumerable.Range(0, bars * 4).Select(i => new NoteEvent(i * (long)Ppq, Ppq, new[] { 64, 67, 69, 67 }[i % 4], 100)).ToList();

    [Fact]
    public void Finding6_harmonise_keep_leaves_a_melody_moved_to_fit_where_it_was()
    {
        var (p, db, soundDb, sounds) = Fresh();
        using (db) using (soundDb) using (sounds)
        {
            p.CurrentSection.Bars = 2;
            p.CurrentSection.ChordTrack = [Block("Am", 0, 2 * Bar)];
            p.DropPartCommand.Execute(new PartDrop(new[] { Mid("Lead", Tune(2)) }, 0, 0));
            var clip = p.Layers[0].Layer.Clips[0];
            Assert.NotEmpty(clip.Under);
            clip.Fits = [2, 2];   // as fitting to a bassline would have moved it
            p.ApplyIdea(new Reharm("Harmonise", "", "the melody", "C | G", 1, [new(0, Bar, Chord("C")), new(Bar, Bar, Chord("G"))]) { Whole = true });
            Assert.Equal(FollowWay.None, clip.Follow);
            Assert.Equal([2, 2], clip.Fits);
        }
    }

    [Fact]
    public void Finding16_dont_follow_keeps_a_part_playing_as_it_does()
    {
        var (p, db, soundDb, sounds) = Fresh();
        using (db) using (soundDb) using (sounds)
        {
            p.CurrentSection.Bars = 2;
            p.CurrentSection.ChordTrack = [Block("Dm7", 0, Bar), Block("G7", Bar, Bar)];
            p.DropPartCommand.Execute(new PartDrop(new[] { Mid("Keys", Chords([48, 52, 55, 59], [48, 52, 55, 59])) }, 0, 0));
            var row = p.Layers[0];
            var clip = row.Layer.Clips[0];
            Assert.NotEmpty(clip.Under);
            var before = Show(clip.Render());
            p.SetFollow(new ClipRow(p, row, clip), FollowWay.None);
            Assert.Equal(before, Show(clip.Render()));
            Assert.False(clip.Pinned);
        }
    }

    [Fact]
    public void Finding19_a_chord_change_leaves_parts_away_from_it_as_they_are()
    {
        var (p, db, soundDb, sounds) = Fresh();
        using (db) using (soundDb) using (sounds)
        {
            p.CurrentSection.Bars = 4;
            p.CurrentSection.ChordTrack = [Block("C", 0, 2 * Bar), Block("F", 2 * Bar, 2 * Bar)];
            p.DropPartCommand.Execute(new PartDrop(new[] { Mid("Lead", Tune(2)) }, 0, 0));
            var clip = p.Layers[0].Layer.Clips[0];
            clip.Fits = [1, 1];   // as it was fitted when it came in
            // F's right edge pulled in a bar: nothing under the lead (bars 1-2) changed.
            p.DragChordCommand.Execute(new ChordDrag(2 * Bar, 2 * Bar, Bar, 7, true));
            Assert.Equal(Bar, p.CurrentSection.ChordTrack[1].Length);
            Assert.Equal([1, 1], clip.Fits);
        }
    }

    [Fact]
    public void Finding25_a_chord_drag_that_ends_where_it_began_leaves_nothing_to_undo()
    {
        var (p, db, soundDb, sounds) = Fresh();
        using (db) using (soundDb) using (sounds)
        {
            p.CurrentSection.ChordTrack = [Block("C", 0, Bar), Block("F", Bar, Bar)];
            Assert.False(p.CanUndo);
            p.DragChordCommand.Execute(new ChordDrag(Bar, Bar, Bar / 2, 3, false));
            Assert.Equal(Bar / 2, p.CurrentSection.ChordTrack[1].Length);
            Assert.True(p.CanUndo);
            p.DragChordCommand.Execute(new ChordDrag(Bar, Bar, Bar, 3, true));
            Assert.Equal(Bar, p.CurrentSection.ChordTrack[1].Length);
            Assert.False(p.CanUndo);
        }
    }
}
