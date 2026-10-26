using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;
using Xunit.Abstractions;

namespace MusicOrganiser.Core.Tests;

// Solomon's notes from testing the chords-first Playground (2026-10-23): a chord heard wrongly, a way to follow the
// chords with nothing to follow, and deleting chords from the keyboard.
[Collection("Global UI")]
public class PlaygroundNotesTests(ITestOutputHelper output) : IDisposable
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
    static int Mask(params int[] keys) => keys.Aggregate(0, (m, k) => m | 1 << k % 12);

    string Show(IEnumerable<NoteEvent> notes) =>
        string.Join("  ", notes.GroupBy(n => n.Start).Select(g => $"{g.Key}:[{string.Join(",", g.Select(n => n.Key))}]"));

    // A C# E G# B over F#, F# lowest: heard as Amaj13 (as in his picture) or F#m11.
    static readonly int[] Ps05 = [54, 57, 61, 64, 68, 71];

    [Fact]
    public void The_same_notes_can_be_named_on_another_root()
    {
        var mask = Mask(Ps05);
        var names = ChordSpeller.Readings(mask, lowest: 6).Select(c => c.Name).ToList();
        output.WriteLine(string.Join(", ", names));
        Assert.Contains("Amaj13", names.Take(2));
        Assert.Contains("F#m11", names.Take(2));
        // Over an F# bass, the others are slash chords.
        Assert.Contains("Amaj13/F#", ChordSpeller.Readings(mask, bass: 6).Select(c => c.Name));
        // Never a name that leaves a note out: C E G isn't offered as an Em with no C.
        Assert.Equal(["C"], ChordSpeller.Readings(Mask(60, 64, 67)).Select(c => c.Name));
        // A dim7 is the same from four roots.
        Assert.Equal(4, ChordSpeller.Readings(Mask(60, 63, 66, 69)).Count);

        // Saying which note is the root.
        Assert.Equal("F#m11", ChordSpeller.On(Chord("Amaj13"), 6).Name);
        Assert.Equal("Am7", ChordSpeller.On(Chord("C6"), 9).Name);
        Assert.Equal("Am7/E", ChordSpeller.On(Chord("C6/E"), 9).Name);   // a bass of its own stays under it
    }

    [Fact]
    public void A_chord_part_already_playing_the_chord_tracks_notes_stays_as_it_is()
    {
        // The same notes named on another root: nothing to re-voice.
        var part = Chords(Ps05);
        foreach (var name in new[] { "F#m11", "Amaj13" })
            Assert.Equal(Show(part), Show(ChordFollow.Apply(part, [new TimedChord(0, Bar, Chord(name))], FollowWay.Degree, MidiRoles.Chords)));
    }

    (PlaygroundViewModel P, LibraryDb Db, SoundLibrary SoundDb, SoundOrganiser Sounds) Fresh()
    {
        Ui.Current = new PlaygroundTests.Headless();
        UiWork.InBackground = false;
        var db = new LibraryDb(Path.Combine(_root, "library.db"));
        var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        var sounds = new SoundOrganiser(soundDb);
        return (new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data")).Playground, db, soundDb, sounds);
    }

    string Mid(string name, List<NoteEvent> notes)
    {
        var clip = new MidiClip { Name = name, Ppq = Ppq };
        clip.Track(name).AddRange(notes);
        var file = Path.Combine(_root, name + ".mid");
        File.WriteAllBytes(file, MusicOrganiser.Core.Export.MidiWriter.Write(clip));
        return file;
    }

    [Fact]
    public void A_chord_heard_wrongly_can_be_heard_as_another_and_the_parts_keep_their_notes()
    {
        var (p, db, soundDb, sounds) = Fresh();
        using (db) using (soundDb) using (sounds)
        {
            p.CurrentSection.Bars = 2;
            p.DropPartCommand.Execute(new PartDrop(new[] { Mid("PS05", Chords(Ps05, [53, 57, 60, 64])) }, 0, 0));
            var heard = p.Chords[0];
            output.WriteLine("heard: " + string.Join(" ", p.Chords.Select(c => c.Name)));
            Assert.False(p.HasChordTrack);
            var before = Show(p.Layers[0].Layer.Notes);

            p.PickChordCommand.Execute(heard);
            output.WriteLine("hear it as: " + string.Join(", ", p.ChordReadings.Select(r => r.Text)) + "   roots: " + string.Join(", ", p.ChordRoots.Select(r => r.Text)));
            Assert.Equal("F#m11", p.ChordReadings[0].Text);   // F# is the lowest note
            Assert.DoesNotContain(p.ChordReadings, r => r.Chord.Root == heard.Timed.Chord.Root);
            Assert.Equal(ChordSymbol.Count(heard.Timed.Chord.Mask) - 1, p.ChordRoots.Count);   // every other note

            // Say F# is the root: F#m11 goes on the chord track for that chord's bar, and the part plays as it did.
            var fSharp = p.ChordRoots.Single(r => r.Chord.Root == 6);
            fSharp.PickCommand.Execute(null);
            var put = Assert.Single(p.CurrentSection.ChordTrack);
            Assert.Equal((heard.Timed.Start, heard.Timed.Length, "F#m11"), (put.Start, put.Length, put.Chord.Name));
            Assert.Equal("F#m11", p.Chords[0].Name);
            Assert.True(p.Chords[0].IsSet);
            Assert.Equal(before, Show(p.Layers[0].Layer.Notes));
            Assert.Equal("F#m11", p.PickedChord?.Name);   // still picked, with its other names now

            p.UndoCommand.Execute(null);
            Assert.False(p.HasChordTrack);
            Assert.Equal(heard.Name, p.Chords[0].Name);
        }
    }

    [Fact]
    public void Delete_after_clicking_the_chord_lane_takes_the_chord_off_not_the_clip()
    {
        var (p, db, soundDb, sounds) = Fresh();
        using (db) using (soundDb) using (sounds)
        {
            p.CurrentSection.Bars = 2;
            p.CurrentSection.ChordTrack = [Block("Dm7", 0, Bar), Block("G7", Bar, Bar)];
            p.DropPartCommand.Execute(new PartDrop(new[] { Mid("Keys", Chords([50, 53, 57, 60], [50, 53, 57, 60])) }, 0, 0));
            var row = p.Layers[0];
            p.PickClip(row, row.Layer.Clips[0]);
            Assert.NotNull(p.SelectedClip);
            Assert.Equal("Backspace", p.Shortcuts.KeyOf("playground.removeChord"));
            Assert.False(p.RunShortcut("playground.removeChord"));   // Backspace: nothing picked on the chord lane yet

            p.PickChordCommand.Execute(p.Chords[1]);
            Assert.True(p.CanRun("playground.delete"));
            Assert.True(p.RunShortcut("playground.delete"));
            Assert.Equal(["Dm7"], p.CurrentSection.ChordTrack.Select(c => c.Chord.Name));
            Assert.Single(row.Layer.Clips);

            // Backspace does the same; a chord heard from the parts isn't on the chord track to take off.
            p.PickChordCommand.Execute(p.Chords[0]);
            Assert.True(p.RunShortcut("playground.removeChord"));
            Assert.Empty(p.CurrentSection.ChordTrack);
            p.PickChordCommand.Execute(p.Chords[0]);
            Assert.False(p.Chords[0].IsSet);
            Assert.False(p.CanRun("playground.delete"));
            Assert.True(p.RunShortcut("playground.delete"));
            Assert.Contains("heard from the parts", p.Message);
            Assert.Single(row.Layer.Clips);

            // Clicking the clip again: Delete is for the clip.
            p.PickClip(row, row.Layer.Clips[0]);
            Assert.True(p.RunShortcut("playground.delete"));
            Assert.Empty(p.Layers.SelectMany(l => l.Layer.Clips));
        }
    }

    [Fact]
    public void A_way_to_follow_with_nothing_on_the_chord_track_says_so_and_puts_the_chords_heard_there()
    {
        var (p, db, soundDb, sounds) = Fresh();
        using (db) using (soundDb) using (sounds)
        {
            p.CurrentSection.Bars = 2;
            var arp = Enumerable.Range(0, 16).Select(i => new NoteEvent(i * (long)Ppq / 2, Ppq / 2, new[] { 62, 65, 69, 72 }[i % 4], 100)).ToList();
            p.DropPartCommand.Execute(new PartDrop(new[] { Mid("Keys", Chords([50, 53, 57, 60], [43, 53, 59, 62])) }, 0, 0));
            p.DropPartCommand.Execute(new PartDrop(new[] { Mid("ARP House", arp) }, 1, 0));
            var row = p.Layers[1];
            p.PickClip(row, row.Layer.Clips[0]);
            var clip = p.SelectedClip!;
            Assert.False(clip.NothingToFollow);   // nothing picked by hand yet: the inspector stays quiet

            clip.FollowPick = ChordFollow.ShortLabel(FollowWay.Root);
            Assert.Equal(FollowWay.Root, clip.Clip.Follow);
            Assert.True(clip.NothingToFollow);
            Assert.Contains("chord track is empty", clip.NothingToFollowText);
            Assert.Contains("once the chord track has chords under it", p.Message);
            var before = p.Layers.Select(l => Show(l.Layer.Notes)).ToList();

            clip.PutChordsUnderCommand.Execute(null);
            output.WriteLine("track: " + string.Join(" ", p.CurrentSection.ChordTrack.Select(c => c.Chord.Name)));
            Assert.True(p.HasChordTrack);
            Assert.NotEmpty(clip.Clip.Under);
            Assert.False(clip.NothingToFollow);
            Assert.Equal("Follows the chord track", clip.FitText);
            // The keys stay as they are; the arp, on D, moves with the root: as it is over Dm7, up to G over G7.
            var after = p.Layers.Select(l => Show(l.Layer.Notes)).ToList();
            Assert.Equal(before[0], after[0]);
            Assert.Equal(arp.Select(n => n.Start < Bar ? n.Key : n.Key + 5), row.Layer.Notes.Select(n => n.Key));

            // Change the last chord: the arp moves with its root there, every note by the same step.
            var last = p.Chords[^1];
            List<int> Under() => row.Layer.Notes.Where(n => n.Start >= last.Timed.Start && n.Start < last.Timed.End).Select(n => n.Key).ToList();
            var was = Under();
            p.PickChordCommand.Execute(last);
            p.HearAs(Chord(last.Timed.Chord.Root == 9 ? "Bb7" : "A7"));
            var now = Under();
            output.WriteLine($"{last.Name}: {string.Join(",", was)} -> {p.Chords[^1].Name}: {string.Join(",", now)}");
            Assert.Equal(was.Count, now.Count);
            var moved = Assert.Single(was.Zip(now, (a, b) => b - a).Distinct());
            Assert.NotEqual(0, moved);
        }
    }
}
