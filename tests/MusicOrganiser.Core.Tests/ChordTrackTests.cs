using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Core.Tests;

/// <summary>Chords first (Solomon, 2026-10-22): the chord track, its blocks, and progressions from your chord parts.</summary>
[Collection("Global UI")]
public class ChordTrackTests : IDisposable
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

    // Made-up voicings, root in the bass.
    static readonly int[] Dm7 = [38, 53, 57, 60], G7 = [43, 53, 59, 62], Cmaj7 = [36, 52, 55, 59], Am7 = [45, 55, 60, 64], Fmaj7 = [41, 52, 57, 60];

    static StoredPhrase Stored(long id, string path, List<NoteEvent> notes)
    {
        var a = PhraseAnalyser.Analyse(notes, "keys");
        return new StoredPhrase(id, new MidiSource(path, MidiSource.Project, "FL Studio", 100 - id, 1, 120), "Keys", "Pattern 1",
                                a.Role, a.RoleWhy, null, a.Bars, notes.Count, a.Lowest, a.Highest, a.Key,
                                a.Chroma.Select(c => (byte)(c / a.Chroma.Max() * 255)).ToArray(), PhraseChord.Encode(a.Chords),
                                a.Features.Select(f => (byte)(f * 255)).ToArray(), a.Hash);
    }

    static List<NoteEvent> Up(List<NoteEvent> notes, int by) => notes.Select(n => n with { Key = n.Key + by }).ToList();

    [Fact]
    public void Where_the_chord_track_has_chords_they_are_the_harmony_and_the_parts_decide_the_rest()
    {
        var section = new PlaygroundSection
        {
            Bars = 4,
            Layers = [new PlaygroundLayer { Name = "Keys", Role = MidiRoles.Chords, Notes = Chords(Dm7, G7, Cmaj7, Cmaj7) }],
            ChordTrack = [Block("Fm7", Bar, Bar)],
        };
        var h = SectionHarmony.Of(section);
        Assert.Equal(["Dm7", "Fm7", "Cmaj7"], h.Lane.Select(c => c.Chord.Name));
        Assert.Equal([0L, Bar, 2L * Bar], h.Lane.Select(c => c.Start));

        // Kept when the idea is saved and opened again.
        var again = PlaygroundSession.FromJson(new PlaygroundSession { Sections = [section] }.ToJson())!.Timeline;
        Assert.Equal("Fm7", Assert.Single(again.ChordTrack).Chord.Name);

        // Half a bar in the middle of a heard chord cuts it in two around it.
        var lane = SectionHarmony.Overlay([new TimedChord(0, 2 * Bar, Chord("C"))], [new TimedChord(Bar / 2, Bar / 2, Chord("F"))], 2 * Bar);
        Assert.Equal([(0L, Bar / 2L, "C"), (Bar / 2L, Bar / 2L, "F"), (Bar, Bar, "C")], lane.Select(c => (c.Start, c.Length, c.Chord.Name)));
    }

    [Fact]
    public void A_progression_is_found_once_in_any_key_and_ranked_by_how_many_songs_play_it()
    {
        Assert.Equal(["C", "Am"], ChordProgressions.OneGoRound(PhraseChord.Decode("0:091 9:211 0:091 9:211")).Select(c => c.Name));

        var twoFiveOne = Chords(Dm7, G7, Cmaj7, Cmaj7);
        var catalogue = new MidiCatalogue(
        [
            Stored(1, @"K:\Songs\Sunday.flp", twoFiveOne),
            Stored(2, @"K:\Songs\Monday.flp", Up(twoFiveOne, 3)),   // the same, in Eb
            Stored(3, @"K:\Songs\Tuesday.flp", Chords(Cmaj7, Am7, Fmaj7, G7, Cmaj7, Am7, Fmaj7, G7)),   // round twice
        ]);
        var found = ChordProgressions.From(catalogue.Entries);
        Assert.Equal(2, found.Count);
        Assert.Equal(2, found[0].Songs);
        Assert.Equal(["Dm7", "G7", "Cmaj7"], found[0].NamesIn(new KeyEstimate(0, false, 1)));
        Assert.Equal(["Ebmaj7", "Cm7", "Abmaj7", "Bb7"], found[1].NamesIn(new KeyEstimate(3, false, 1)));
        Assert.Equal(4, found[1].Bars);   // one go round of the eight bars
        // Into a minor key it goes in the relative major: A minor's is C.
        Assert.Equal(["Dm7", "G7", "Cmaj7"], found[0].NamesIn(new KeyEstimate(9, true, 1)));
    }

    PlaygroundViewModel Playground(LibraryDb db, SoundOrganiser sounds) =>
        new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data")).Playground;

    [Fact]
    public void Stretching_a_chord_moves_its_neighbour_as_it_goes_and_the_whole_drag_is_one_undo()
    {
        Ui.Current = new PlaygroundTests.Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var p = Playground(db, sounds);
        p.CurrentSection.ChordTrack = [Block("C", 0, Bar), Block("F", Bar, Bar), Block("G", 2 * Bar, 2 * Bar)];

        // C's right edge pulled half a bar on: F starts later, and the chord lane shows it straight away.
        p.DragChordCommand.Execute(new ChordDrag(0, 0, Bar + Bar / 2, 7, false));
        Assert.Equal([(0L, "C"), (Bar + Bar / 2L, "F"), (2L * Bar, "G")], p.Chords.Select(c => (c.Timed.Start, c.Name)));
        Assert.All(p.Chords, c => Assert.True(c.IsSet));
        // On over F: F keeps a beat; then back again, and F has all it had.
        p.DragChordCommand.Execute(new ChordDrag(0, 0, 3 * Bar, 7, false));
        Assert.Equal(Ppq, p.CurrentSection.ChordTrack[1].Length);
        p.DragChordCommand.Execute(new ChordDrag(0, 0, Bar / 2, 7, true));
        Assert.Equal([(0L, Bar / 2L), (Bar / 2L, Bar + Bar / 2L), (2L * Bar, 2L * Bar)], p.CurrentSection.ChordTrack.Select(c => (c.Start, c.Length)));

        // G slid back a bar, over F: F is cut back to make room.
        p.DragChordCommand.Execute(new ChordDrag(2 * Bar, Bar, 2 * Bar, 8, true));
        Assert.Equal(["C", "F", "G"], p.CurrentSection.ChordTrack.Select(c => c.Chord.Name));
        Assert.Equal((Bar / 2L, Bar / 2L), (p.CurrentSection.ChordTrack[1].Start, p.CurrentSection.ChordTrack[1].Length));

        p.UndoCommand.Execute(null);
        p.UndoCommand.Execute(null);
        Assert.Equal([(0L, Bar), (Bar, Bar), (2L * Bar, 2L * Bar)], p.CurrentSection.ChordTrack.Select(c => (c.Start, c.Length)));
        Assert.False(p.CanUndo);
    }

    [Fact]
    public async Task A_progression_from_your_chord_parts_goes_on_the_chord_track_with_no_midi()
    {
        Ui.Current = new PlaygroundTests.Headless();
        UiWork.InBackground = false;
        // Made-up chord parts in a MIDI folder: the Chords tab finds their progressions by itself.
        var pack = Path.Combine(_root, "Packs");
        Directory.CreateDirectory(pack);
        void Write(string name, List<NoteEvent> notes)
        {
            var clip = new MidiClip { Name = name, Ppq = Ppq };
            clip.Track("Keys").AddRange(notes);
            File.WriteAllBytes(Path.Combine(pack, name + ".mid"), MusicOrganiser.Core.Export.MidiWriter.Write(clip));
        }
        // Dm7 for a bar and a half, G7 for half a bar, then Cmaj7: the timing comes with it.
        var pushed = new List<NoteEvent>();
        pushed.AddRange(Dm7.Select(k => new NoteEvent(0, Bar + Bar / 2, k, 100)));
        pushed.AddRange(G7.Select(k => new NoteEvent(Bar + Bar / 2, Bar / 2, k, 100)));
        pushed.AddRange(Cmaj7.Select(k => new NoteEvent(2 * Bar, 2 * Bar, k, 100)));
        Write("Sunday", pushed);
        Write("Monday", Up(pushed, 2));
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        db.SetList(MidiIndexer.FoldersKey, [pack]);
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        await main.Midi.IndexAsync();
        var p = main.Playground;
        p.Opened();
        for (var i = 0; i < 300 && p.Progressions.Count == 0; i++) await Task.Delay(10);
        var row = Assert.Single(p.Progressions);
        Assert.Equal("in 2 songs", row.SongsText);
        p.ProgressionSearch = "ii V";
        Assert.Single(p.Progressions);
        p.ProgressionSearch = "vi";
        Assert.Empty(p.Progressions);
        p.ProgressionSearch = "";

        await p.UseProgression(p.Progressions[0]);
        Assert.Empty(p.Layers);   // no MIDI
        Assert.Equal([(0L, "Dm7"), (Bar + Bar / 2L, "G7"), (2L * Bar, "Cmaj7")], p.CurrentSection.ChordTrack.Select(c => (c.Start, c.Chord.Name)));
        Assert.Equal(4, p.Bars);
        Assert.Equal(["Dm7", "G7", "Cmaj7"], p.Chords.Select(c => c.Name));

        // What comes next goes on after the chord track's last chord, a bar long.
        p.AddNextChord(Chord("Am7"));
        Assert.Equal((4L * Bar, "Am7"), (p.CurrentSection.ChordTrack[^1].Start, p.CurrentSection.ChordTrack[^1].Chord.Name));
        Assert.Equal(5, p.Bars);
        Assert.Empty(p.Layers);

        // The chord-lane menu takes one off, or clears them all.
        p.PickChordCommand.Execute(p.Chords[^1]);
        Assert.True(p.RunShortcut("playground.removeChord"));
        Assert.Equal(3, p.CurrentSection.ChordTrack.Count);
        Assert.True(p.RunShortcut("playground.clearChordTrack"));
        Assert.Empty(p.Chords);
    }
}
