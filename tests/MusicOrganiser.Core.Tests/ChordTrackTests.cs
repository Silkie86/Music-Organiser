using MusicOrganiser.Core.DawLink;
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

    // ---- step 2: parts follow the chord track ----

    static List<TimedChord> Track(params string[] names) => names.Select((n, i) => new TimedChord(i * (long)Bar, Bar, Chord(n))).ToList();

    static List<NoteEvent> Line(params int[] keys) => keys.Select((k, i) => new NoteEvent(i * (long)Ppq, Ppq, k, 100)).ToList();

    static int[] Pcs(IEnumerable<NoteEvent> notes) => notes.Select(n => n.Key % 12).Distinct().Order().ToArray();

    [Fact]
    public void A_chord_part_keeps_its_place_in_each_chord_of_the_track()
    {
        // Dm7 played for two bars, over a track of Dm7 then G7: the second bar is re-voiced as G7, its root in the bass.
        var part = Chords(Dm7, Dm7);
        var played = ChordFollow.Apply(part, Track("Dm7", "G7"), FollowWay.Auto, MidiRoles.Chords);
        Assert.Equal(Dm7, played.Where(n => n.Start == 0).Select(n => n.Key));
        var second = played.Where(n => n.Start == Bar).ToList();
        Assert.Equal(Pcs(G7.Select(k => new NoteEvent(0, 1, k, 1))), Pcs(second));
        Assert.Equal(7, second.Min(n => n.Key) % 12);
        Assert.True(second.Average(n => n.Key) - Dm7.Average() is > -7 and < 7);   // near where it was
    }

    [Fact]
    public void A_bassline_or_melody_follows_the_way_picked_for_it()
    {
        // A one-bar bass riff in C minor (C C Eb F), played over Cm then Fm.
        var riff = Line(36, 36, 39, 41).Concat(Line(36, 36, 39, 41).Select(n => n with { Start = n.Start + Bar })).ToList();
        var track = Track("Cm", "Fm");
        // Move with the root: the second bar is the riff a fourth up (or a fifth down), note for note.
        var moved = ChordFollow.Apply(riff, track, FollowWay.Root, MidiRoles.Bass);
        Assert.Equal([41, 41, 44, 46], moved.Where(n => n.Start >= Bar).Select(n => n.Key));
        // Keep its place: root, root, minor 3rd, 4th of Fm.
        var placed = ChordFollow.Apply(riff, track, FollowWay.Degree, MidiRoles.Bass);
        Assert.Equal([5, 5, 8, 10], placed.Where(n => n.Start >= Bar).Select(n => n.Key % 12));
        // Don't follow, or a riff that already fits: as it is.
        Assert.Equal(riff, ChordFollow.Apply(riff, track, FollowWay.None, MidiRoles.Bass));
        Assert.Equal(riff.Take(4), ChordFollow.Apply(riff, track, FollowWay.Degree, MidiRoles.Bass).Take(4));

        // Nudge clashes: a melody holding Bb over Cmaj7 moves to B; the notes that fit, and quick passing notes, stay.
        var melody = new List<NoteEvent> { new(0, 2 * Ppq, 70, 100), new(2 * Ppq, Ppq / 2, 61, 100), new(3 * Ppq, Ppq, 67, 100) };
        var nudged = ChordFollow.Apply(melody, Track("Cmaj7"), FollowWay.Auto, MidiRoles.Melody);
        Assert.Equal([71, 61, 67], nudged.Select(n => n.Key));
    }

    [Fact]
    public void A_dropped_part_follows_the_chord_track_live_until_its_changed_by_hand()
    {
        Ui.Current = new PlaygroundTests.Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var p = Playground(db, sounds);
        p.CurrentSection.ChordTrack = [Block("Dm7", 0, Bar), Block("G7", Bar, Bar)];
        p.CurrentSection.Bars = 2;
        // A Dm7 chord part, two bars, dropped from a .mid file.
        var clip = new MidiClip { Name = "Keys", Ppq = Ppq };
        clip.Track("Keys").AddRange(Chords(Dm7, Dm7));
        var file = Path.Combine(_root, "Keys.mid");
        File.WriteAllBytes(file, MusicOrganiser.Core.Export.MidiWriter.Write(clip));
        p.DropPartCommand.Execute(new PartDrop(new[] { file }, 0, 0));
        var keys = Assert.Single(p.Layers);
        Assert.Equal(MidiRoles.Chords, keys.Role);
        Assert.Equal(["Dm7", "G7"], p.Chords.Select(c => c.Name));
        Assert.Equal(7, keys.Layer.Notes.Where(n => n.Start == Bar).Min(n => n.Key) % 12);   // the second bar plays G7

        // G7 dragged back half a bar: the part changes chord half a bar sooner, straight away.
        p.DragChordCommand.Execute(new ChordDrag(Bar, Bar / 2, Bar + Bar / 2, 9, true));
        Assert.Contains(keys.Layer.Notes, n => n.Start == Bar / 2 && n.Key % 12 == 7);
        Assert.Equal("Follows the chord track", p.SelectedClip!.FitText);
        Assert.Equal("Keep its place", p.SelectedClip.FollowPick);

        // Moved with the root instead, from the clip's menu.
        Assert.True(p.RunShortcut("playground.followRoot"));
        Assert.True(p.IsTicked("playground.followRoot"));
        Assert.Equal(Dm7.Select(k => k + 5), keys.Layer.Notes.Where(n => n.Start == Bar).Select(n => n.Key));

        // Up an octave by hand: it stays as it is when the chords change, until Follow the chords again.
        Assert.True(p.RunShortcut("playground.octaveUp"));
        var asItIs = keys.Layer.Notes.ToList();
        Assert.Equal(Dm7.Select(k => k + 17), asItIs.Where(n => n.Start == Bar).Select(n => n.Key));
        p.CurrentSection.ChordTrack[1].Chord = Chord("C7");
        p.DragChordCommand.Execute(new ChordDrag(Bar / 2, Bar / 2, Bar + Bar / 2, 10, true));   // any change to the track
        Assert.Equal(asItIs, keys.Layer.Notes);
        Assert.True(p.RunShortcut("playground.fitAgain"));
        Assert.Equal(Dm7.Select(k => k + 12 - 2), keys.Layer.Notes.Where(n => n.Start == Bar).Select(n => n.Key));   // down a tone to C7, an octave up
    }

    [Theory]
    [InlineData("Cm7 F7 Bbmaj7 Bbmaj7", "Bb", false)]
    [InlineData("Dm7b5 G7 Cm6 Cm6", "C", true)]
    [InlineData("Ebmaj7 Ab7 Dm7 G7 Cm7 F7 Bb6 Bb6", "Bb", false)]
    [InlineData("F7 Bb7 F7 F7 Bb7 Bb7 F7 D7 Gm7 C7 F7 C7", "F", false)]
    public void With_only_chords_on_the_chord_track_the_key_is_the_one_real_tunes_with_those_chords_are_in(string chart, string tonic, bool minor)
    {
        var lane = ChordLane.ParseChart(chart, out _)!;
        var section = new PlaygroundSection
        {
            Bars = lane.Count,
            ChordTrack = lane.Select((c, i) => new PlaygroundChord { Start = i * (long)Bar, Length = Bar, Chord = c.Chord }).ToList(),
        };
        var key = SectionHarmony.Of(section).Key;
        Assert.NotNull(key);
        Assert.Equal((ChordSymbol.ParseNote(tonic, out _), minor), (key.Tonic, key.Minor));
        Assert.True(key.Confidence >= SectionHarmony.MinKeyConfidence);
        // A typed chart with no parts goes the same way.
        Assert.Equal(key.Tonic, SectionHarmony.Of(new PlaygroundSection { Bars = lane.Count, Chart = chart }).Key?.Tonic);
    }

    [Fact]
    public void The_key_counts_the_app_ships_with_read_back_the_same_as_they_were_written()
    {
        var again = ChordKey.Load(new StringReader(ChordKey.Default.Save()));
        var chords = ChordLane.ParseChart("Am7 D7 Gmaj7 Em7 Am7 D7 Bm7 E7", out _)!.Select(c => c.Chord).ToList();
        var lengths = chords.Select(_ => 4.0).ToList();
        Assert.Equal(ChordKey.Default.Guess(chords, lengths), again.Guess(chords, lengths));
        Assert.Equal((7, false), (again.Guess(chords, lengths)!.Tonic, again.Guess(chords, lengths)!.Minor));
    }

    [Fact]
    public void What_comes_next_starts_from_jazz_and_gospel_charts_and_leans_on_your_own_parts_as_they_grow()
    {
        var charts = ProgressionModel.FromCharts;
        Assert.True(charts.Learned > 6000);
        // Nothing of your own yet: after Dm7 then G7, the charts say C.
        var empty = ProgressionModel.Learn([]).With(charts);
        var next = empty.Next(Chord("G7"), Chord("Dm7"));
        Assert.Equal(0, next[0].Chord.Root);
        Assert.True(empty.Chance(Chord("G7"), Chord("Cmaj7")) > 0);

        // Thousands of your own parts where G7 goes to Ab: yours win.
        var mine = ProgressionModel.Learn(Enumerable.Repeat<IReadOnlyList<ChordSymbol>>([Chord("Dm7"), Chord("G7"), Chord("Ab")], 3000)).With(charts);
        Assert.Equal("Ab", mine.Next(Chord("G7"), Chord("Dm7"))[0].Chord.Name);

        // The counts read back the same as they were written.
        var again = ProgressionModel.Load(new StringReader(charts.Save()));
        Assert.Equal(charts.Next(Chord("Bbm7"), Chord("Fm7")), again.Next(Chord("Bbm7"), Chord("Fm7")));
    }

    static BitwigNote[] Struck(double at, double beats, params int[] keys) => keys.Select(k => new BitwigNote(at, beats, k, 90)).ToArray();

    // A clip of Dm7 | G7 | Cmaj7 | Cmaj7, a bar each, with a note before its loop start and one past its end.
    static BitwigClipNotes ChordClip(string from = "arranger", bool onSelected = false) =>
        new(from, "Keys", onSelected, Start: 4, Length: 16, Cut: false,
            [.. Struck(0, 4, 40), .. Struck(4, 4, Dm7), .. Struck(8, 4, G7), .. Struck(12, 8, Cmaj7), .. Struck(20, 4, 70)]);

    [Fact]
    public void A_clip_selected_in_Bitwig_is_read_into_chords_from_its_loop_start()
    {
        var clip = ChordClip();
        var notes = BitwigClipChords.Notes(clip);
        Assert.Equal(0, notes.Min(n => n.Start));
        Assert.DoesNotContain(notes, n => n.Key is 40 or 70);
        Assert.Equal(4L * Bar, notes.Max(n => n.Start + n.Length));   // the last chord held over the end is cut there
        var lane = BitwigClipChords.Lane(clip);
        Assert.Equal(["Dm7", "G7", "Cmaj7"], lane.Select(c => c.Chord.Name));
        Assert.Equal(4L * Bar, lane[^1].End);

        // The clip on the track just clicked wins; else the arranger's; an empty one comes last.
        var launcher = ChordClip("launcher", onSelected: true);
        var empty = new BitwigClipNotes("arranger", "Pad", true, 0, 16, false, []);
        Assert.Same(launcher, BitwigClipChords.Pick([clip, launcher]));
        Assert.Same(clip, BitwigClipChords.Pick([ChordClip("launcher"), clip]));
        Assert.Same(clip, BitwigClipChords.Pick([empty, clip]));
        Assert.Null(BitwigClipChords.Pick([]));
    }

    [Fact]
    public async Task Get_chords_from_Bitwig_puts_the_selected_clips_chords_on_the_chord_track()
    {
        Ui.Current = new PlaygroundTests.Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var p = Playground(db, sounds);

        p.BitwigClips = _ => Task.FromResult<IReadOnlyList<BitwigClipNotes>>([]);
        await p.ChordsFromBitwigCommand.ExecuteAsync(null);
        Assert.Empty(p.CurrentSection.ChordTrack);
        Assert.StartsWith("No clip is selected in Bitwig", p.Message);

        p.BitwigClips = _ => throw new DawLinkException(BitwigLink.NeedsClipNotes);
        await p.ChordsFromBitwigCommand.ExecuteAsync(null);
        Assert.Equal(BitwigLink.NeedsClipNotes, p.Message);

        p.BitwigClips = _ => Task.FromResult<IReadOnlyList<BitwigClipNotes>>([ChordClip()]);
        await p.ChordsFromBitwigCommand.ExecuteAsync(null);
        Assert.Equal(["Dm7", "G7", "Cmaj7"], p.CurrentSection.ChordTrack.OrderBy(c => c.Start).Select(c => c.Chord.Name));
        Assert.Equal(4L * Bar, p.CurrentSection.ChordTrack.Max(c => c.End));
        Assert.Contains("the clip on Keys (arranger)", p.Message);
        Assert.True(p.ChordsFromBitwigCommand.CanExecute(null));

        // Undo takes them off.
        p.UndoCommand.Execute(null);
        Assert.Empty(p.CurrentSection.ChordTrack);
    }
}
