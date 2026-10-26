using System.Collections;
using System.ComponentModel;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Core.Tests;

// Ui.Current and UiWork.InBackground are app-wide: tests that set them run one class at a time.
[Collection("Global UI")]
public class PlaygroundTests : IDisposable
{
    const int Ppq = PhraseAnalyser.Ppq, Beat = Ppq, Bar = 4 * Ppq;
    readonly string _root = Fixtures.TempDir();

    public void Dispose()
    {
        UiWork.InBackground = true;
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    static List<NoteEvent> Chords(params int[][] chords) =>
        chords.SelectMany((c, bar) => c.Select(k => new NoteEvent(bar * (long)Bar, Bar, k, 100))).ToList();

    static List<NoteEvent> Line(int length, params int[] keys) =>
        keys.Select((k, i) => new NoteEvent(i * (long)length, length, k, 100)).ToList();

    internal static StoredPhrase Stored(long id, string path, string part, List<NoteEvent> notes, string hints = "")
    {
        var a = PhraseAnalyser.Analyse(notes, hints);
        return new StoredPhrase(id, new MidiSource(path, MidiSource.Project, "FL Studio", 100 - id, 1, 120), part, "Pattern 1",
                                a.Role, a.RoleWhy, null, a.Bars, notes.Count, a.Lowest, a.Highest, a.Key,
                                a.Chroma.Select(c => (byte)(c / a.Chroma.Max() * 255)).ToArray(), PhraseChord.Encode(a.Chords),
                                a.Features.Select(f => (byte)(f * 255)).ToArray(), a.Hash);
    }

    // Dm9 – G13 – Cmaj9 – Cmaj9 as rootless voicings over roots.
    static readonly int[][] TwoFiveOne = [[38, 53, 57, 60, 64], [43, 53, 59, 64, 69], [36, 52, 55, 59, 62], [36, 52, 55, 59, 62]];

    static PlaygroundSection SectionWith(params (string Name, string Role, List<NoteEvent> Notes, string Source)[] parts) => new()
    {
        Bars = 4,
        Layers = parts.Select(p => new PlaygroundLayer { Name = p.Name, Role = p.Role, Notes = p.Notes, SourcePath = p.Source }).ToList(),
    };

    [Fact]
    public void A_section_hears_its_chords_and_key()
    {
        var h = SectionHarmony.Of(SectionWith(("Keys", MidiRoles.Chords, Chords(TwoFiveOne), "")));
        Assert.Equal(["Dm9", "G13", "Cmaj9"], h.Lane.Select(c => c.Chord.Name));
        Assert.Equal(0, h.Key!.Tonic);
        Assert.False(h.Key.Minor);

        // A typed chart wins and goes round to fill the section; a typed key wins too.
        var typed = SectionWith();
        typed.Chart = "Am7 | D7";
        var t = SectionHarmony.Of(typed, "G");
        Assert.Equal(["Am7", "D7", "Am7", "D7"], t.Lane.Select(c => c.Chord.Name));
        Assert.Equal(7, t.Key!.Tonic);
        Assert.True(t.KeyTyped && t.ChordsTyped);
    }

    [Fact]
    public void A_melody_that_follows_the_chords_beats_one_that_only_shares_the_key()
    {
        var lane = ChordLane.ParseChart("Dm9 | G13 | Cmaj9 | %", out _)!;
        // Chord notes and tensions on the strong beats of each bar...
        var follows = Line(2 * Beat, 65, 69, 65, 64, 64, 67, 71, 74);
        // ...against the same notes, in the same key, a bar late.
        var late = Line(2 * Beat, 64, 67, 65, 69, 65, 64, 71, 74);
        var a = SectionFit.Score(follows, lane, 4 * Bar, MidiRoles.Melody)!.Value;
        var b = SectionFit.Score(late, lane, 4 * Bar, MidiRoles.Melody)!.Value;
        Assert.True(a > b, $"{a:0.00} vs {b:0.00}");
    }

    [Fact]
    public void A_part_that_plays_through_the_chords_covers_more_than_a_short_chunk()
    {
        // Solomon, 2026-10-21: Suggest favoured small chunks of MIDI because they're easy to fit.
        var lane = ChordLane.ParseChart("Dm7 | G7 | Cmaj7 | %", out _)!;
        var through = Chords([62, 65, 69, 72], [59, 62, 65, 67], [60, 64, 67, 71], [60, 64, 67, 71]);
        var chunk = new List<NoteEvent> { new(3 * Beat, Beat / 2, 62, 90), new(3 * Beat, Beat / 2, 65, 90) };
        Assert.Equal(1, SectionFit.Cover(through, lane, 4 * Bar));
        Assert.Equal(1 / 16.0, SectionFit.Cover(chunk, lane, 4 * Bar), 3);
        // Staccato on every beat still counts as playing through.
        Assert.Equal(1, SectionFit.Cover(Line(Beat / 4, Enumerable.Repeat(0, 16).Select((_, i) => 0).ToArray()).Select((n, i) => n with { Start = i * (long)Beat, Key = 50 }).ToList(), lane, 4 * Bar));
    }

    [Fact]
    public void Approach_notes_are_let_off_but_a_held_clash_is_not()
    {
        var lane = ChordLane.ParseChart("Cmaj7", out _)!;
        // A quick C# on the "and" stepping up to D (the 9): bebop approach.
        var approach = new List<NoteEvent> { new(0, Beat, 64, 90), new(Beat + Beat / 2, Beat / 2, 61, 90), new(2 * Beat, 2 * Beat, 62, 90) };
        var held = new List<NoteEvent> { new(0, Beat, 64, 90), new(Beat, 3 * Beat, 61, 90) };
        Assert.True(SectionFit.Score(approach, lane, Bar, MidiRoles.Melody) > SectionFit.Score(held, lane, Bar, MidiRoles.Melody) + 0.2);
    }

    [Fact]
    public void Suggestions_never_come_from_a_song_already_in_the_section()
    {
        var keys = Chords(TwoFiveOne);
        var tune = Line(2 * Beat, 65, 69, 65, 64, 64, 67, 71, 74);
        var notes = new Dictionary<long, List<NoteEvent>>
        {
            [1] = tune, [2] = tune.Select(n => n with { Key = n.Key + 1 }).ToList(), [3] = Line(2 * Beat, 66, 70, 66, 65, 61, 68, 70, 73),
        };
        var catalogue = new MidiCatalogue([
            Stored(1, @"K:\Songs\Blue_3.flp", "Lead", notes[1]),
            Stored(2, @"K:\Other\Tune.flp", "Lead", notes[2]),
            Stored(3, @"K:\Third\Song.flp", "Lead", notes[3]),
        ]);
        // The section's chords come from Blue_2, an older save of the song the first lead is in.
        var section = SectionWith(("Keys", MidiRoles.Chords, keys, @"K:\Songs\Blue_2.flp"));
        var found = SectionMatcher.Rank(catalogue, SectionHarmony.Of(section), MidiRoles.Melody, e => notes[e.Id]);
        Assert.DoesNotContain(found, m => m.Entry.Id == 1);
        Assert.Equal(2, found[0].Entry.Id);           // the same tune saved a semitone up: moved back down, it fits best
        Assert.Equal(-1, found[0].Move);
        Assert.Equal(tune.Select(n => n.Key), found[0].Notes.Select(n => n.Key));
        Assert.Contains("Sits on the chords", found[0].Why);
    }

    [Fact]
    public async Task The_browse_list_says_its_getting_ready_while_the_midi_page_reads_not_that_there_is_no_midi()
    {
        // Solomon's Playground (2026-10-22) said "No MIDI here yet" for the minute the MIDI page took to read its list.
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        var pack = Path.Combine(_root, "Packs");
        Directory.CreateDirectory(pack);
        var song = new MidiClip { Name = "Sunday", Ppq = PhraseAnalyser.Ppq };
        song.Track("Keys").AddRange(Chords(TwoFiveOne));
        File.WriteAllBytes(Path.Combine(pack, "Sunday.mid"), MusicOrganiser.Core.Export.MidiWriter.Write(song));
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        db.SetList(MidiIndexer.FoldersKey, [pack]);
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var list = main.Playground.Midi;
        var changed = new List<string?>();
        list.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Nothing read and nothing reading: how to get MIDI in.
        Assert.True(list.IsEmpty);
        Assert.False(list.IsGettingReady);

        // The MIDI page reading its list, or its files: getting ready, here and there.
        main.Midi.IsLoading = true;
        Assert.False(list.IsEmpty);
        Assert.True(list.IsGettingReady);
        Assert.True(main.Midi.IsGettingReady);
        Assert.Contains(nameof(MidiBrowserViewModel.IsEmpty), changed);
        main.Midi.IsLoading = false;
        main.Midi.IsIndexing = true;
        Assert.True(list.IsGettingReady);
        main.Midi.IsIndexing = false;
        Assert.True(list.IsEmpty);

        // Read: still getting ready until the copy has taken the parts, then the list.
        await main.Midi.IndexAsync();
        Assert.True(main.Midi.HasParts);
        Assert.False(main.Midi.IsGettingReady);
        Assert.False(list.IsEmpty);
        main.Playground.Opened();
        for (var i = 0; i < 300 && !list.HasParts; i++) await Task.Delay(10);
        Assert.True(list.HasParts);
        Assert.False(list.IsEmpty);
        Assert.False(list.IsGettingReady);
    }

    [Fact]
    public async Task An_empty_idea_shows_the_midi_pages_list_and_the_first_part_turns_suggest_on()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        // A real file in a MIDI library folder: opening the list indexes the folders, which drops parts whose file is gone.
        var pack = Path.Combine(_root, "Packs");
        Directory.CreateDirectory(pack);
        var song = new MidiClip { Name = "Sunday", Ppq = PhraseAnalyser.Ppq };
        song.Track("Keys").AddRange(Chords(TwoFiveOne));
        song.Track("Bass").AddRange(Line(Bar, 38, 43, 36, 36));
        File.WriteAllBytes(Path.Combine(pack, "Sunday.mid"), MusicOrganiser.Core.Export.MidiWriter.Write(song));
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        db.SetList(MidiIndexer.FoldersKey, [pack]);
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        await main.Midi.IndexAsync();
        var p = main.Playground;

        // Nothing on the timeline: Suggest has nothing to fit to, so the page shows the MIDI page's own list.
        Assert.True(p.IsEmpty);
        Assert.NotSame(main.Midi, p.Midi);   // a copy: its own search and kind
        await p.FindMatchesAsync();
        Assert.Empty(p.Matches);
        p.Opened();
        for (var i = 0; i < 300 && p.Midi.Results.Count < 2; i++) await Task.Delay(10);
        Assert.Equal(2, p.Midi.Results.Count);
        p.Midi.Search = "bass";   // searching here leaves the MIDI page's search alone
        Assert.Equal("", main.Midi.Search);

        // Only your projects: the pack is a MIDI folder, so nothing (Solomon, 2026-10-21: "filter whether you see midi
        // from your projects or from midi folders").
        for (var i = 0; i < 300 && p.Midi.Results.Count != 1; i++) await Task.Delay(10);
        p.Midi.From = MidiBrowserViewModel.YourProjects;
        for (var i = 0; i < 300 && p.Midi.Results.Count != 0; i++) await Task.Delay(10);
        Assert.Empty(p.Midi.Results);
        p.Midi.From = MidiBrowserViewModel.YourLibraries;
        for (var i = 0; i < 300 && p.Midi.Results.Count != 1; i++) await Task.Delay(10);

        // Add on a part of that list: on the timeline, and Suggest lists parts that fit it.
        var bass = p.Midi.Results.Single(r => r.Entry.Role == MidiRoles.Bass);
        p.AddMidiRowCommand.Execute(bass);
        Assert.False(p.IsEmpty, p.Message);
        Assert.Single(p.Layers);
        // A part dragged from the list lands where it's let go: here a new track from bar 3.
        p.DropPartCommand.Execute(new PartDrop(bass, 1, 2L * Bar));
        Assert.Equal(2, p.Layers.Count);
        Assert.Equal(2L * Bar, p.Layers[1].Layer.Clips.Single().Start);
        for (var i = 0; i < 300 && (p.IsMatching || p.Matches.Count == 0); i++) await Task.Delay(10);
        Assert.True(p.Matches.Count > 0 || p.MatchStatus.Contains("Nothing found"), p.MatchStatus);   // Suggest ran, for chords
    }

    [Fact]
    public async Task A_suggestion_heard_with_the_idea_shows_faintly_on_the_timeline_until_stopped()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        p.PutChords("C | F | G | C");
        var bass = Line(Bar, 36, 41, 43, 36);
        var entry = new MidiCatalogue([Stored(1, @"K:\Other\Bass.flp", "Bass", bass, "bass")]).Entries[0];
        var row = new PlaygroundMatchRow(p, new PlaygroundMatch(entry, null, 0.8, bass, ""));
        Assert.Equal("▶ With the idea", row.PreviewText);

        await p.PreviewMatch(row);
        Assert.True(row.IsPlaying);
        Assert.Equal("■ Stop", row.PreviewText);
        var trying = Assert.IsType<TryingPart>(p.Trying);
        Assert.Equal(p.Layers.Count, trying.Row);   // where Add would put it: a new track
        Assert.Equal(bass.Select(n => n.Key), trying.Notes.Select(n => n.Key));
        // and outlined on the piano roll over the chords' clip, picked for it, so the notes are seen together
        Assert.NotNull(p.SelectedClip);
        Assert.Equal(bass.Select(n => n.Key), p.SelectedClip!.Overlay!.Select(n => n.Key));

        await p.PreviewMatch(row);   // pressed again: stops, and the timeline is just the idea again
        Assert.Null(p.Trying);
        Assert.Null(p.SelectedClip.Overlay);
        Assert.False(row.IsPlaying);
    }

    [Fact]
    public async Task Picking_another_suggestion_while_one_plays_plays_it_from_the_same_place()
    {
        // F12 note, 22 Oct 09:34: "press the arrow key or select another clip and it starts playing straight away from where the playhead is".
        var ui = new Headless();
        Ui.Current = ui;
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        p.PutChords("C | F | G | C");
        var bass = Line(Bar, 36, 41, 43, 36);
        var other = Line(Bar, 36, 36, 43, 43);
        var entries = new MidiCatalogue([Stored(1, @"K:\Other\Bass.flp", "Bass", bass, "bass"), Stored(2, @"K:\Else\Bass.flp", "Bass", other, "bass")]).Entries;
        var first = new PlaygroundMatchRow(p, new PlaygroundMatch(entries[0], null, 0.8, bass, ""));
        var second = new PlaygroundMatchRow(p, new PlaygroundMatch(entries[1], null, 0.7, other, ""));
        p.Matches.Add(first);
        p.Matches.Add(second);

        await p.PreviewMatch(first);
        Assert.True(first.IsPlaying);
        Assert.Same(first, p.SelectedMatch);   // the arrow keys go on from here
        p.Position = 2 * Bar;   // the playhead at bar 3

        p.SelectedMatch = second;   // the down arrow
        Assert.True(second.IsPlaying);
        Assert.False(first.IsPlaying);
        Assert.True(ui.Player!.StartedAt > 0);   // from bar 3, not from the start

        p.StopPlaying();
        p.SelectedMatch = first;   // nothing playing: picking a row doesn't start it
        Assert.False(first.IsPlaying);
    }

    [Fact]
    public void A_note_about_to_be_clicked_in_shows_the_colour_it_will_have()
    {
        // F12 note, 22 Oct 09:47: "the preview colour of the piano roll doesnt make the colour when you click the note in".
        // The chords are heard from the chord part being edited, so a note put in it can change the chord.
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        p.PutChords("Cmaj7 | Am7 D9 | Dm7 | G7");
        p.WriteChordsCommand.Execute(null);
        var track = p.Layers.Single(l => !l.IsWritten).Layer;
        p.SelectClipCommand.Execute(new ClipPick(track, track.Clips[0]));
        var row = p.SelectedClip!;
        var before = row.Notes.ToList();
        var lane = p.Harmony.Lane.Select(c => c.Chord.Name).ToList();
        var checkedAny = false;
        foreach (var note in new[] { new NoteEvent(24, Beat / 4, 34, 100), new NoteEvent(Bar + Beat / 4, Beat / 4, 44, 100), new NoteEvent(Bar + Beat / 4, Beat / 4, 41, 100) })
        {
            var now = p.SelectedClip!.StrengthOf!(note);
            var would = p.SelectedClip.WouldBe!(note);
            Assert.Equal(lane, p.Harmony.Lane.Select(c => c.Chord.Name));   // looking doesn't change anything
            Assert.Equal(before, p.SelectedClip.Notes);
            p.SelectedClip.EditedCommand.Execute(before.Append(note).ToList());
            Assert.Equal(p.SelectedClip!.StrengthOf!(note), would);
            checkedAny |= now != would;
            p.UndoCommand.Execute(null);   // the idea as it was, read back afresh
            track = p.Layers.Single(l => !l.IsWritten).Layer;
            p.SelectClipCommand.Execute(new ClipPick(track, track.Clips[0]));
        }
        Assert.True(checkedAny);   // one of them did change the chord it's judged against
    }

    [Fact]
    public void A_clips_own_playhead_goes_round_with_it_where_it_repeats()
    {
        // F12 note, 22 Oct 10:05: "playhead goes one time through a looping clip".
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        p.PutChords("C | F | G | C");
        p.WriteChordsCommand.Execute(null);
        var layer = p.Layers[^1];
        var row = new ClipRow(p, layer, new PlaygroundClip { Start = Bar, Loop = Bar, Length = 3 * Bar });
        row.PlayheadAt(Bar + Beat);
        Assert.Equal(Beat, row.Position);
        row.PlayheadAt(3 * Bar + 2 * Beat);   // its third go round
        Assert.Equal(2 * Beat, row.Position);
        row.PlayheadAt(Beat);                  // before it starts
        Assert.Equal(-1, row.Position);
        row.PlayheadAt(4 * Bar);               // after it ends
        Assert.Equal(-1, row.Position);
    }

    [Fact]
    public void A_tracks_volume_is_dragged_in_one_undo_step_and_kept_with_the_idea()
    {
        // F12 note, 22 Oct 09:40: "should be able to change the volume of the tracks individually".
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        p.PutChords("C | F |");
        p.WriteChordsCommand.Execute(null);
        var row = p.Layers[^1];
        Assert.Equal(1, row.Volume);
        Assert.Equal("0.0 dB", row.VolumeText);

        row.SetVolumeCommand.Execute(new FaderMove(0.8, 7, false));
        row.SetVolumeCommand.Execute(new FaderMove(0.5, 7, false));
        row.SetVolumeCommand.Execute(new FaderMove(0.5, 7, true));
        Assert.Equal(0.5, row.Layer.Volume);
        Assert.Equal("−12.0 dB", row.VolumeText);
        var saved = new PlaygroundSession { Sections = [new() { Name = "A", Bars = 2, Layers = [new PlaygroundLayer { Name = "Keys", Volume = 0.5 }] }] };
        Assert.Equal(0.5, PlaygroundSession.FromJson(saved.ToJson())!.Sections[0].Layers[0].Volume);   // kept with the idea

        p.RunShortcut("playground.undo");   // the whole drag at once
        Assert.Equal(1, p.Layers[^1].Layer.Volume);
    }

    [Fact]
    public void The_menus_copy_cut_and_paste_clips_and_every_line_has_a_shortcut_entry()
    {
        // Solomon, 2026-10-21: "no right click menus or file edit view menus… it should look like a modern daw".
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        var lines = PlaygroundViewModel.MenuBar.SelectMany(m => m.Items)
            .Concat(PlaygroundViewModel.ClipMenu).Concat(PlaygroundViewModel.TrackMenu)
            .Concat(PlaygroundViewModel.TimelineMenu).Concat(PlaygroundViewModel.ChordMenu).Where(l => !l.IsSeparator);
        foreach (var line in lines) Assert.Contains(ShortcutsViewModel.Actions, a => a.Id == line.Id);

        var bass = Line(Bar, 36, 41, 43, 36);
        var entry = new MidiCatalogue([Stored(1, @"K:\Other\Bass.flp", "Bass", bass, "bass")]).Entries[0];
        p.AddFromLibrary(entry, bass);
        var track = Assert.Single(p.Layers);
        p.PickClip(track, track.Layer.Clips[0]);
        Assert.False(p.CanRun("playground.paste"));
        Assert.True(p.RunShortcut("playground.copy"));
        Assert.True(p.CanRun("playground.paste"));

        p.Cursor = 4 * Bar;
        Assert.True(p.RunShortcut("playground.paste"));
        Assert.Equal([0L, 4L * Bar], track.Layer.Clips.Select(c => c.Start).Order());
        Assert.True(p.Bars >= 8);

        Assert.True(p.RunShortcut("playground.cut"));   // the pasted one, picked after pasting
        Assert.Single(track.Layer.Clips);
        p.RunShortcut("playground.undo");
        Assert.Equal(2, p.Layers.Single().Layer.Clips.Count);

        var renamed = false;
        p.RenameAsked += (_, _) => renamed = true;
        Assert.True(p.RunShortcut("playground.rename"));
        Assert.True(renamed);
        Assert.True(p.RunShortcut("playground.reharm"));
        Assert.Equal(PlaygroundViewModel.ReharmTab, p.RightTab);
    }

    [Fact]
    public void A_cut_clip_pasted_onto_a_new_track_keeps_its_moves_and_undoes_on_its_own()
    {
        // Code review, 22 Oct: the new-track paste rebuilt the clip plain and took no undo step.
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        var bass = Line(Bar, 36, 41, 43, 36);
        var entry = new MidiCatalogue([Stored(1, @"K:\Other\Bass.flp", "Bass", bass, "bass")]).Entries[0];
        p.AddFromLibrary(entry, bass);
        var track = Assert.Single(p.Layers);
        p.PickClip(track, track.Layer.Clips[0]);
        p.SelectedClip!.SemitoneByCommand.Execute(new ValueStep(5, 1));
        p.SelectedClip.OctaveDownCommand.Execute(null);
        // +5 st an octave down: one octave down and up 5, not "octave 0, -7 st".
        Assert.Equal(-1, p.SelectedClip.OctaveValue);
        Assert.Equal("+5 st", p.SelectedClip.SemitoneText);

        Assert.True(p.RunShortcut("playground.cut"));   // its only clip: the track goes too
        Assert.Empty(p.Layers);
        Assert.True(p.RunShortcut("playground.paste"));
        var pasted = Assert.Single(Assert.Single(p.Layers).Layer.Clips);
        Assert.Equal(-7, pasted.Transpose);
        Assert.True(pasted.Pinned);
        p.RunShortcut("playground.undo");   // takes the paste back, not the cut too
        Assert.Empty(p.Layers);
    }

    [Fact]
    public void Show_more_lists_the_same_parts_as_asking_for_more_from_scratch()
    {
        // Code review, 22 Oct: Show more scored every part again; the ones scored already are kept.
        var keys = Chords(TwoFiveOne);
        var notes = Enumerable.Range(1, 5).ToDictionary(i => (long)i, i => Line(2 * Beat, 65 + i, 69, 65, 64));
        var catalogue = new MidiCatalogue([.. notes.Select(n => Stored(n.Key, $@"K:\Song{n.Key}\Song.flp", "Lead", n.Value))]);
        var harmony = SectionHarmony.Of(SectionWith(("Keys", MidiRoles.Chords, keys, @"K:\Mine\Idea.flp")));
        var scored = new System.Collections.Concurrent.ConcurrentDictionary<MidiEntry, PlaygroundMatch?>(ReferenceEqualityComparer.Instance);
        SectionMatcher.Rank(catalogue, harmony, MidiRoles.Melody, e => notes[e.Id], out _, limit: 2, scored: scored);
        var kept = scored.Where(k => k.Value is not null).ToDictionary(k => k.Key, k => k.Value!);
        Assert.NotEmpty(kept);
        var more = SectionMatcher.Rank(catalogue, harmony, MidiRoles.Melody, e => notes[e.Id], out _, limit: 4, scored: scored);
        var fresh = SectionMatcher.Rank(catalogue, harmony, MidiRoles.Melody, e => notes[e.Id], out _, limit: 4);
        Assert.Equal(fresh.Select(m => m.Entry.Id), more.Select(m => m.Entry.Id));
        foreach (var m in more)
            if (kept.TryGetValue(m.Entry, out var was)) Assert.Same(was, m);   // not scored again
    }

    [Fact]
    public void Suggestions_say_how_many_parts_they_were_picked_from()
    {
        var keys = Chords(TwoFiveOne);
        var notes = Enumerable.Range(1, 5).ToDictionary(i => (long)i, i => Line(2 * Beat, 65 + i, 69, 65, 64));
        var catalogue = new MidiCatalogue([.. notes.Select(n => Stored(n.Key, $@"K:\Song{n.Key}\Song.flp", "Lead", n.Value))]);
        var section = SectionWith(("Keys", MidiRoles.Chords, keys, @"K:\Mine\Idea.flp"));
        var found = SectionMatcher.Rank(catalogue, SectionHarmony.Of(section), MidiRoles.Melody, e => notes[e.Id], out var matching, limit: 2);
        Assert.Equal(2, found.Count);   // the best two, as the Settings number asks
        Assert.Equal(5, matching);      // of the five leads there were, so the page can offer Show more
    }

    [Fact]
    public void An_idea_saved_as_sections_opens_on_one_timeline()
    {
        // As saved before the timeline: no version, sections played A B A, a typed chart in B.
        var old = new PlaygroundSession
        {
            Sections =
            [
                new() { Name = "A", Bars = 2, Feel = SectionFeel.Verse, Layers = [new PlaygroundLayer { Name = "Keys", Notes = Line(Bar, 60, 62) }] },
                new() { Name = "B", Bars = 1, Chart = "G7", Layers = [new PlaygroundLayer { Name = "Keys", Notes = Line(Bar, 65) }, new PlaygroundLayer { Name = "Bass", Role = MidiRoles.Bass, Notes = Line(Bar, 41), Muted = true }] },
            ],
            Arrangement = "A B A",
        };
        var json = old.ToJson().Replace("\"Version\": 2,", "");
        var s = PlaygroundSession.FromJson(json)!;
        Assert.Equal(2, s.Version);
        var t = Assert.Single(s.Sections);
        Assert.Equal(5, t.Bars);
        Assert.Equal(["A", "B", "A"], t.Labels.Select(l => l.Name));
        Assert.Equal([0L, 2L * Bar, 3L * Bar], t.Labels.Select(l => l.Start));
        Assert.Equal(SectionFeel.Verse, t.Labels[0].Feel);
        // Parts of the same name are one track, a clip where each section played; B's chart is a chord part now.
        Assert.Equal(["Keys", "Bass", "Chords"], t.Layers.Select(l => l.Name));
        var keys = t.Layers[0];
        Assert.Equal([0L, 2L * Bar, 3L * Bar], keys.Clips.Select(c => c.Start));
        Assert.All(keys.Clips, c => Assert.False(c.Pinned));   // not changed by hand, so they fit the chords when moved
        Assert.Equal([0L, Bar, 2L * Bar, 3L * Bar, 4L * Bar], keys.Notes.Select(n => n.Start));
        Assert.Equal([60, 62, 65, 60, 62], keys.Notes.Select(n => n.Key));
        Assert.True(t.Layers[1].Muted);
        Assert.Equal(2L * Bar, Assert.Single(t.Layers[2].Clips).Start);
        Assert.Equal("G7", SectionHarmony.Of(t).Lane.Single(c => c.Start == 2L * Bar).Chord.Name);

        // Saved again, it opens the same.
        var back = PlaygroundSession.FromJson(s.ToJson())!;
        Assert.Equal(keys.Notes, back.Timeline.Layers[0].Notes);
        Assert.Equal(3, back.Timeline.Labels.Count);
    }

    [Fact]
    public void An_idea_that_cant_be_opened_is_kept_and_never_saved_over()
    {
        var folder = Path.Combine(_root, "playground");
        Directory.CreateDirectory(folder);
        var current = Path.Combine(folder, "current.json");
        // Saved by a newer build: a kind of harmony this one doesn't know.
        var newer = new PlaygroundSession { Name = "Late night" }.ToJson().Replace("\"JazzGospel\"", "\"Bebop\"");
        File.WriteAllText(current, newer);

        var store = new PlaygroundStore(folder);
        var opened = store.LoadCurrent();
        Assert.True(store.OpenFailed);
        Assert.Equal("Untitled idea", opened.Name);
        store.SaveCurrent(opened);   // the first change after opening
        // Kept in Ideas, where it's listed, not hidden in the folder.
        Assert.StartsWith("Kept idea ", store.KeptAs);
        Assert.Equal([store.KeptAs], store.Ideas());
        Assert.Equal(newer, File.ReadAllText(Path.Combine(folder, "ideas", store.KeptAs + ".json")));
        Assert.Equal("Untitled idea", PlaygroundSession.FromJson(File.ReadAllText(current))!.Name);

        // Opened again, the new one is there and nothing more is put aside.
        var again = new PlaygroundStore(folder);
        again.LoadCurrent();
        Assert.False(again.OpenFailed);
        Assert.Single(again.Ideas());

        // The page says so, and where to find it.
        File.WriteAllText(current, "{ \"Sections\": [ { \"Layers\": [ { \"Data\": \"not base64!\" } ] } ] }");
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var store3 = new PlaygroundStore(folder);
        var page = new PlaygroundViewModel(db, main, store3);
        Assert.Contains("couldn't be opened", page.Message);
        Assert.Contains($"kept in Ideas as “{store3.KeptAs}”", page.Message);
        Assert.Equal(2, page.SavedIdeas.Count);

        // One that still can't be opened says so, and stays.
        page.OpenIdeaCommand.Execute(store3.KeptAs);
        Assert.Contains("couldn't be opened", page.Message);
        Assert.Equal(2, page.SavedIdeas.Count);
    }

    [Fact]
    public void An_idea_another_program_has_open_is_kept_in_ideas_and_opens_from_there()
    {
        var folder = Path.Combine(_root, "playground");
        Directory.CreateDirectory(folder);
        var current = Path.Combine(folder, "current.json");
        File.WriteAllText(current, new PlaygroundSession { Name = "Late night" }.ToJson());

        var store = new PlaygroundStore(folder);
        using (new FileStream(current, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // a virus check that won't let go
        {
            Assert.Equal("Untitled idea", store.LoadCurrent().Name);
            Assert.True(store.OpenFailed);
        }
        store.SaveCurrent(new PlaygroundSession());
        Assert.Equal("Late night", store.LoadIdea(Assert.Single(store.Ideas()))!.Name);
    }

    [Fact]
    public void An_idea_with_empty_lists_written_as_null_still_opens()
    {
        var json = new PlaygroundSession { Sections = [new() { Layers = [new PlaygroundLayer { Name = "Keys", Notes = Line(Bar, 60) }] }] }.ToJson();
        var noClips = PlaygroundSession.FromJson(json.Replace("\"Clips\": []", "\"Clips\": null").Replace("\"Labels\": []", "\"Labels\": null"))!;
        Assert.Equal([60], noClips.Timeline.Layers.Single().Notes.Select(n => n.Key));
        var noSections = PlaygroundSession.FromJson("{ \"Version\": 2, \"Sections\": null }")!;
        Assert.Empty(Assert.Single(noSections.Sections).Layers);
    }

    [Fact]
    public void A_clip_plays_round_trimmed_and_moved_and_only_chord_and_bass_clips_say_the_chords()
    {
        // A 1-bar riff, its start trimmed by a beat, stretched to 2½ bars from bar 3, the second go round up a tone.
        var clip = PlaygroundClip.Of("Riff", MidiRoles.Melody, Line(Beat, 60, 62, 64, 65), Bar, 2L * Bar, 5L * Bar / 2);
        clip.Offset = Beat;
        clip.Fits = [0, 2];
        var notes = clip.Render();
        Assert.Equal(3, clip.Repeats);
        Assert.Equal([62, 64, 65, 62, 64, 66, 67, 60, 62, 64], notes.Select(n => n.Key));
        Assert.Equal(2L * Bar, notes[0].Start);
        Assert.True(notes.All(n => n.Start < clip.End));

        // Chords under the first two bars only: a melody there is judged against them, but after them it's heard.
        var timeline = new PlaygroundSection
        {
            Bars = 4,
            Layers =
            [
                new PlaygroundLayer { Name = "Keys", Role = MidiRoles.Chords, Clips = [PlaygroundClip.Of("Keys", MidiRoles.Chords, Chords([48, 52, 55, 59]), Bar, 0, 2L * Bar)] },
                new PlaygroundLayer { Name = "Lead", Role = MidiRoles.Melody, Clips = [PlaygroundClip.Of("Lead", MidiRoles.Melody, Chords([50, 53, 57, 60]), 2L * Bar, 0, 4L * Bar)] },
            ],
        };
        foreach (var l in timeline.Layers) l.Render();
        var lane = SectionHarmony.Of(timeline).Lane;
        Assert.Equal("Cmaj7", lane.First().Chord.Name);
        Assert.Contains(lane, c => c.Start >= 2L * Bar && c.Chord.Name == "Dm7");
        Assert.DoesNotContain(lane, c => c.Start < 2L * Bar && c.Chord.Name == "Dm7");

        // Shift to fit: a C triad over D moves up a tone; with no chords it stays.
        var d = ChordLane.ParseChart("D", out _)!;
        Assert.Equal(2, ClipFit.Best(Chords([60, 64, 67]), d, Bar, HarmonyStyle.JazzGospel));
        Assert.Equal(0, ClipFit.Best(Chords([60, 64, 67]), [], Bar, HarmonyStyle.JazzGospel));
    }

    [Fact]
    public void The_progression_model_learns_from_other_songs_only()
    {
        // ii–V–I chord parts in two keys, and one song that always goes I – vi.
        var entries = new List<StoredPhrase>();
        for (var i = 0; i < 6; i++)
        {
            var t = i * 2;
            entries.Add(Stored(i + 1, $@"K:\S{i}\Song.flp", "Keys", Chords([50 + t, 53 + t, 57 + t, 60 + t], [43 + t, 53 + t, 59 + t, 62 + t], [48 + t, 52 + t, 55 + t, 59 + t])));
        }
        entries.Add(Stored(20, @"K:\Mine\Here.flp", "Keys", Chords([43, 53, 59, 62], [45, 48, 52, 55], [43, 53, 59, 62], [45, 48, 52, 55])));
        var catalogue = new MidiCatalogue(entries);
        var model = ProgressionModel.Learn(catalogue.Entries, [MidiEntry.SongOf(@"K:\Mine\Here_2.flp")]);
        Assert.Equal(6, model.Learned);
        var next = model.Next(ChordSymbol.Parse("G7")!.Value, ChordSymbol.Parse("Dm7")!.Value);
        Assert.Equal("Cmaj7", next[0].Chord.Name);
    }

    [Fact]
    public void What_followed_two_chords_counts_even_when_seen_only_a_few_times()
    {
        // Five ii–V–Is, and six chains of dominants (C7 – F7 – Bb7 – Eb7) where a 7 chord goes on to another 7 a 4th up.
        // After Dm7 – G7 the two-chord count has only seen five, but they all went to Cmaj7: that wins over the
        // one-chord count's "a 7 goes to another 7".
        var entries = new List<StoredPhrase>();
        // Each in its own key: the same notes twice would be one part with two copies.
        for (var t = 0; t < 5; t++)
            entries.Add(Stored(t + 1, $@"K:\Two{t}\Song.flp", "Keys", Chords([50 + t, 53 + t, 57 + t, 60 + t], [43 + t, 53 + t, 59 + t, 62 + t], [48 + t, 52 + t, 55 + t, 59 + t])));
        for (var t = 0; t < 6; t++)
            entries.Add(Stored(t + 10, $@"K:\Chain{t}\Song.flp", "Keys", Chords([48 + t, 52 + t, 55 + t, 58 + t], [53 + t, 57 + t, 60 + t, 63 + t], [46 + t, 50 + t, 53 + t, 56 + t], [51 + t, 55 + t, 58 + t, 61 + t])));
        var model = ProgressionModel.Learn(new MidiCatalogue(entries).Entries, []);
        var next = model.Next(ChordSymbol.Parse("G7")!.Value, ChordSymbol.Parse("Dm7")!.Value);
        Assert.Equal("Cmaj7", next[0].Chord.Name);
        Assert.Equal("C7", next[1].Chord.Name);
        // With nothing before it, a 7 chord goes on to the next 7.
        Assert.Equal("C7", model.Next(ChordSymbol.Parse("G7")!.Value, null)[0].Chord.Name);
    }

    // ---- the page ----

    [Fact]
    public void Writing_parts_from_a_chart_and_reharmonising_them()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        db.SetSetting(ProjectDetailViewModel.ExportFolderKey, Path.Combine(_root, "export"));   // drags write here, not into ~/Documents
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;

        p.PutChords("Dm7 | G7 | Cmaj7 | %");
        Assert.Equal(["Dm7", "G7", "Cmaj7"], p.Chords.Select(c => c.Name));
        Assert.Equal(["ii7", "V7", "Imaj7"], p.Chords.Select(c => c.Roman));
        Assert.StartsWith("Key: C major", p.KeySummary);

        p.Voicing = "Rootless (jazz piano)";
        p.WriteChordsCommand.Execute(null);
        p.BassPick = "Walking";
        p.WriteBassCommand.Execute(null);
        Assert.Equal(3, p.Layers.Count);
        Assert.Equal(16, p.Layers[2].Notes.Count);
        // Chords are heard, never typed: the rootless voicings' 9ths and 13ths are heard too.
        Assert.Equal(["Dm9", "G13", "Cmaj9"], p.Chords.Select(c => c.Name));

        // The tritone sub: the chord part plays it, and the written parts follow the new chords.
        var sub = p.Ideas.First(i => i.Title == "Tritone sub");
        sub.ApplyCommand.Execute(null);
        Assert.StartsWith("Db", p.Chords[1].Name);
        Assert.Equal(1, p.Layers[2].Notes.First(n => n.Start == Bar).Key % 12);   // the walking bass starts bar 2 on Db

        // Undo puts the chords back, and the idea was saved: a new page opens it as it was.
        p.UndoCommand.Execute(null);
        Assert.Equal(["Dm9", "G13", "Cmaj9"], p.Chords.Select(c => c.Name));
        var again = new PlaygroundViewModel(db, main);
        Assert.Equal(3, again.Layers.Count);
        Assert.Equal(["Dm9", "G13", "Cmaj9"], again.Chords.Select(c => c.Name));

        // A chord added on the end is played by the chord part, which goes round into the new bar.
        p.NextChords.Clear();
        p.AddNextChord(ChordSymbol.Parse("A7")!.Value);
        Assert.Equal(5, p.Bars);
        Assert.Equal("A7", p.Chords[^1].Name);

        // Dragging the arrangement out writes one MIDI file with a track per part.
        var drag = p.MakeDrag(["arrangement"])!;
        var tracks = MidiFileReader.Read(File.ReadAllBytes(drag.Files[0]), "x");
        Assert.Equal(3, tracks.Count);
    }

    [Fact]
    public async Task A_melody_on_its_own_gets_whole_sets_of_chords_to_try_and_keep_on_the_chord_track_or_as_a_part()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        // Twinkle, twinkle, on its own: C C G G | A A G G | F F E E | D D C C
        var tune = Line(Beat, 72, 72, 79, 79, 81, 81, 79, 79, 77, 77, 76, 76, 74, 74, 72, 72);
        var entry = new MidiCatalogue([Stored(1, @"K:\Other\Tune.flp", "Lead", tune, "melody lead")]).Entries[0];
        p.AddFromLibrary(entry, tune, "Lead");
        Assert.True(p.CanHarmonise);
        Assert.StartsWith("Chords for your melody", p.HarmoniseText);
        var plain = p.HarmoniseIdeas.First(i => i.Title == "Harmonise: plain");

        // Tried: the chords show striped all the way along, and nothing changes yet.
        Assert.True(p.RunShortcut("playground.reharm"));
        Assert.Null(p.PickedChord);   // Reharmonise opens on these, not on a chord heard in the melody
        await p.TryIdea(plain);
        Assert.Equal(3, p.ReharmStep);
        Assert.Equal(plain.Idea.Lane.Count, p.ProposedChords!.Count);
        Assert.Single(p.Layers);

        // Kept: they go on the chord track (no MIDI), the chord lane reads them, and there's nothing left to harmonise.
        p.KeepIdeaCommand.Execute(null);
        Assert.Single(p.Layers);
        Assert.Equal(plain.Idea.Lane.Select(c => c.Chord.Name), p.CurrentSection.ChordTrack.Select(c => c.Chord.Name));
        Assert.Equal(plain.Idea.Lane.Select(c => c.Chord.Name), p.Chords.Select(c => c.Name));
        Assert.Contains("on the chord track", p.Message);
        // The melody they were made for stays exactly as it was.
        Assert.Equal(tune.Select(n => (n.Start, n.Key)), p.Layers[0].Layer.Notes.Select(n => (n.Start, n.Key)));
        Assert.Equal(FollowWay.None, p.Layers[0].Layer.Clips[0].Follow);
        Assert.False(p.CanHarmonise);
        p.UndoCommand.Execute(null);
        Assert.Empty(p.CurrentSection.ChordTrack);
        Assert.True(p.CanHarmonise);

        // Or kept as a part: a Chords track plays them instead.
        await p.TryIdea(p.HarmoniseIdeas.First(i => i.Title == "Harmonise: plain"));
        p.KeepIdeaAsPartCommand.Execute(null);
        Assert.Equal(2, p.Layers.Count);
        Assert.Empty(p.CurrentSection.ChordTrack);
        Assert.Equal(plain.Idea.Lane.Select(c => c.Chord.Name), p.Chords.Select(c => c.Name));
        Assert.False(p.CanHarmonise);
        p.UndoCommand.Execute(null);
        Assert.Single(p.Layers);
        Assert.True(p.CanHarmonise);
    }

    [Fact]
    public async Task Reharm_in_three_steps_pick_a_chord_try_an_idea_striped_then_keep_it_or_go_back()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        p.PutChords("Dm7 | G7 | Cmaj7 | %");
        Assert.Equal(1, p.ReharmStep);

        // 1: clicking G7 on the chord lane opens Reharm with its ideas, and only ideas that change it.
        p.PickChordCommand.Execute(p.Chords[1]);
        Assert.Equal(2, p.ReharmStep);
        Assert.Equal(PlaygroundViewModel.ReharmTab, p.RightTab);
        Assert.StartsWith("G7 (V7) · bar 2", p.PickedChordText);
        var sub = p.ChordIdeas.First(i => i.Title == "Tritone sub");
        Assert.DoesNotContain(p.ChordIdeas, i => i.Idea.Bar == 4 && i.Title != "Turnaround" && i.Title != "Richer chords");

        // 2: trying it shows Db9 striped where G7 is, and plays; the chords themselves don't change yet.
        await p.TryIdea(sub);
        Assert.Equal(3, p.ReharmStep);
        Assert.StartsWith("Db", Assert.Single(p.ProposedChords!).Name);
        Assert.Equal("G7", p.Chords[1].Name);
        Assert.Equal("Back to G7", p.BackText);

        // 3: Back leaves G7 picked to try another; Keep changes it, and the new chord stays picked.
        p.BackIdeaCommand.Execute(null);
        Assert.Equal(2, p.ReharmStep);
        Assert.Null(p.ProposedChords);
        Assert.Equal("G7", p.Chords[1].Name);
        await p.TryIdea(sub);
        p.KeepIdeaCommand.Execute(null);
        Assert.StartsWith("Db", p.Chords[1].Name);
        Assert.Equal(2, p.ReharmStep);
        Assert.StartsWith("Db", p.PickedChord!.Name);
        Assert.Null(p.ProposedChords);

        // Undo puts G7 back, still picked; Harmony › Reharmonise picks the chord under the play marker when none is.
        p.UndoCommand.Execute(null);
        Assert.Equal("G7", p.PickedChord!.Name);
        p.ClearPickedChordCommand.Execute(null);
        Assert.Equal(1, p.ReharmStep);
        p.Cursor = 2 * Bar + 10;
        p.RunShortcut("playground.reharm");
        Assert.Equal("Cmaj7", p.PickedChord!.Name);
    }

    [Fact]
    public void Clips_slide_onto_new_tracks_repeat_when_stretched_and_written_ones_follow_their_bars()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        p.PutChords("Dm7 | G7");
        p.WriteBassCommand.Execute(null);
        var chords = p.Layers[0];
        var bass = p.Layers[1];
        Assert.Equal(2, p.Bars);

        // The chords pulled out to 4 bars: the timeline grows and they play round again.
        p.ChangeClipCommand.Execute(new ClipEdit(chords.Layer, chords.Clips[0], 0, 0, 4L * Bar, 0));
        Assert.Equal(4, p.Bars);
        Assert.Equal(["Dm7", "G7", "Dm7", "G7"], p.Chords.Select(c => c.Name));

        // The written bass slid to bar 3, below the last track: a new track, written again for the chords there.
        p.ChangeClipCommand.Execute(new ClipEdit(bass.Layer, bass.Clips[0], 2, 2L * Bar, 2L * Bar, 0));
        Assert.Equal(["Chords", "Bass · Walking", "Bassline"], p.Layers.Select(l => l.Name));
        Assert.Empty(p.Layers[1].Clips);
        var moved = p.Layers[2];
        Assert.Same(moved, p.SelectedLayer);
        Assert.All(moved.Notes, n => Assert.True(n.Start >= 2L * Bar));
        Assert.Equal(2, moved.Notes.First().Key % 12);   // D, the root of bar 3's Dm7

        // Trimmed by a bar: it starts on G7, its go round starting a bar in.
        p.ChangeClipCommand.Execute(new ClipEdit(chords.Layer, chords.Clips[0], 0, Bar, 3L * Bar, Bar));
        Assert.Equal(7, chords.Notes.Where(n => n.Start == Bar).Min(n => n.Key) % 12);

        p.UndoCommand.Execute(null);
        p.UndoCommand.Execute(null);
        Assert.Equal(["Chords", "Bass · Walking"], p.Layers.Select(l => l.Name));
        Assert.Equal(0L, p.Layers[1].Clips[0].Start);
    }

    [Fact]
    public void A_moved_clip_shifts_to_fit_each_time_round_until_changed_by_hand()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        p.PutChords("C | D");
        // A one-bar C major arpeggio from the library, going round over both bars.
        var arp = Line(Beat, 60, 64, 67, 72);
        var entry = new MidiCatalogue([Stored(1, @"K:\Other\Arp.flp", "Arp", arp)]).Entries[0];
        p.AddFromLibrary(entry, arp, "Arp");
        var track = p.Layers[1];
        var clip = track.Clips[0];
        Assert.Equal(2L * Bar, clip.Length);
        Assert.False(clip.Pinned);

        // Slid (here: put back where it is): each time round moves to fit, the second up a tone onto D.
        p.ChangeClipCommand.Execute(new ClipEdit(track.Layer, clip, 1, 0, 2L * Bar, 0));
        Assert.Equal([0, 2], clip.Fits);
        Assert.Equal([62, 66, 69, 74], track.Notes.Where(n => n.Start >= Bar).Select(n => n.Key));
        Assert.Contains("+2", p.SelectedClip!.FitText);

        // Moved by hand: pinned, and sliding it no longer moves it.
        p.SelectedClip.UpCommand.Execute(null);
        Assert.True(clip.Pinned);
        Assert.True(p.SelectedClip.CanFitAgain);
        p.ChangeClipCommand.Execute(new ClipEdit(track.Layer, clip, 1, Bar, Bar, 0));
        // Trimmed to its second bar: it plays just as it did there (up a tone to fit, up a semitone by hand), left as it is
        Assert.Equal(63, track.Notes.First().Key);

        // Fit again: back to fitting (the semitone by hand goes, no octave was set).
        p.SelectedClip.FitAgainCommand.Execute(null);
        Assert.False(clip.Pinned);
        Assert.Equal(0, clip.Transpose);
        Assert.Equal(62, track.Notes.First().Key);

        // Chords turned over by hand: the bottom note goes up an octave.
        var keys = p.Layers[0];
        p.SelectClipCommand.Execute(new ClipPick(keys.Layer, keys.Clips[0]));
        var before = keys.Notes.Where(n => n.Start == 0).Select(n => n.Key).Order().ToList();
        p.SelectedClip!.TurnUpCommand.Execute(null);
        var after = keys.Notes.Where(n => n.Start == 0).Select(n => n.Key).Order().ToList();
        Assert.NotEqual(before, after);
        Assert.Equal(before.Select(k => k % 12).Order(), after.Select(k => k % 12).Order());
    }

    [Fact]
    public void Highlighted_bars_of_a_clip_swap_for_another_part_and_mid_files_drop_onto_tracks()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        p.PutChords("C | F | G | C");
        var bass = Line(Bar, 36, 41, 43, 36);
        var entry = new MidiCatalogue([Stored(1, @"K:\Other\Bass.flp", "Bass", bass, "bass")]).Entries[0];
        p.AddFromLibrary(entry, bass, "Bass");
        var track = p.Layers[1];
        var clip = track.Clips[0];

        // Bars 2–3 highlighted with the bass clip picked: Suggest turns to basslines for those bars of it.
        p.SetLoopCommand.Execute(new RollRange(Bar, 3L * Bar));
        Assert.Same(clip, p.SwapTarget!.Clip);
        Assert.Equal(MidiRoles.Bass, p.SuggestRole.Role);

        // Swapped in: the new part plays those bars, and the old clip keeps bar 1 and bar 4 either side.
        var walk = Line(Beat, 36, 38, 40, 41);
        var other = new MidiCatalogue([Stored(2, @"K:\Other\Walk.flp", "Bass", walk, "bass")]).Entries[0];
        p.SwapIn(p.SwapTarget, (Bar, 3L * Bar), other, walk);
        Assert.Equal([0L, Bar, 3L * Bar], track.Clips.Select(c => c.Start).Order());
        Assert.Equal(36, track.Notes.Single(n => n.Start == 0).Key);
        Assert.Equal(36, track.Notes.Single(n => n.Start == 3L * Bar).Key);
        Assert.Equal(8, track.Notes.Count(n => n.Start >= Bar && n.Start < 3L * Bar));
        p.UndoCommand.Execute(null);
        Assert.Single(p.Layers[1].Clips);

        // A two-part .mid file let go at bar 3: both parts on new tracks there, in time with each other.
        var file = new MidiClip { Name = "Idea", Ppq = 96 };
        file.Track("Keys").AddRange(Line(Bar, 60, 65));
        file.Track("Lead").AddRange(Line(Beat, 72, 74, 76, 77).Select(n => n with { Start = n.Start + Bar }));
        var path = Path.Combine(_root, "Idea.mid");
        File.WriteAllBytes(path, MusicOrganiser.Core.Export.MidiWriter.Write(file, 90));
        var tracks = p.Layers.Count;
        p.DropPartCommand.Execute(new PartDrop(new List<string> { path }, 0, 2L * Bar));
        Assert.Equal(tracks + 2, p.Layers.Count);
        Assert.Equal(2L * Bar, p.Layers[^2].Clips[0].Start);
        Assert.Equal(3L * Bar, p.Layers[^1].Notes.Min(n => n.Start));   // the lead came in a bar after the keys

        // One part let go on the chords track: it takes those bars, the chords keep the rest.
        var one = new MidiClip { Name = "Stab", Ppq = 96 };
        one.Track("Stab").AddRange(Line(Bar, 60));
        var stab = Path.Combine(_root, "Stab.mid");
        File.WriteAllBytes(stab, MusicOrganiser.Core.Export.MidiWriter.Write(one, 90));
        p.DropPartCommand.Execute(new PartDrop(new List<string> { stab }, 0, Bar));
        Assert.Equal([0L, Bar, 2L * Bar], p.Layers[0].Clips.Select(c => c.Start).Order());

        // Pulled out of the timeline: a clip goes out alone from its start, a label as its bars with a track per part.
        var stabClip = p.Layers[0].Clips.Single(c => c.Start == Bar);
        var alone = MidiFileReader.Read(File.ReadAllBytes(p.MakeDrag([new ClipPick(p.Layers[0].Layer, stabClip)])!.Files[0]), "x").Single();
        Assert.Equal([60], alone.Notes.Select(n => n.Key));
        Assert.Equal(0, alone.Notes[0].Start);
        var bars = MidiFileReader.Read(File.ReadAllBytes(p.MakeDrag([new PlaygroundLabel { Name = "B", Start = 2L * Bar, Length = 2L * Bar }])!.Files[0]), "x");
        Assert.True(bars.Count >= 3);

        // Where Play starts, and the zoomed view as shares for the chord lane above.
        p.SetCursorCommand.Execute(2L * Bar);
        Assert.Equal(2L * Bar, p.Cursor);
        p.ViewLength = p.Length / 2;
        p.ViewStart = p.Length / 4;
        Assert.Equal((0.25, 0.75), (p.ViewFrom, p.ViewTo));
    }

    [Fact]
    public void A_bassline_leans_on_the_root_and_fifth_and_other_parts_on_the_chords_own_notes()
    {
        // F12 note, 20 Oct: "for bass the root and the 5th should be the strongest fit then 3rd".
        FitStrength S(string chord, int pc, string role) => NoteStrength.Of(ChordSymbol.Parse(chord)!.Value, pc, role);
        Assert.Equal([FitStrength.Strongest, FitStrength.Strongest, FitStrength.Strong, FitStrength.Colour, FitStrength.Weak, FitStrength.Fights],
                     new[] { 0, 7, 4, 10, 2, 11 }.Select(pc => S("C7", pc, MidiRoles.Bass)));   // C G E Bb D B
        Assert.Equal(FitStrength.Strongest, S("C/E", 4, MidiRoles.Bass));          // a slash chord's bass note
        Assert.Equal(FitStrength.Strongest, S("Cm7b5", 6, MidiRoles.Bass));        // its 5th is flat
        Assert.Equal(FitStrength.Strongest, S("C7b5", 6, MidiRoles.Bass));         // a dominant's flat 5th too
        Assert.NotEqual(FitStrength.Strongest, S("C7b5", 7, MidiRoles.Bass));      // not the natural 5th it replaces
        Assert.Equal(FitStrength.Strongest, S("C7", 7, MidiRoles.Bass));           // a shell voicing's 5th, left out, still counts
        // Any other part: root, 3rd and 5th, then the 7th and the chord's other notes, then tensions; the avoid note fights.
        Assert.Equal([FitStrength.Strongest, FitStrength.Strongest, FitStrength.Strongest, FitStrength.Strong, FitStrength.Colour, FitStrength.Fights],
                     new[] { 0, 4, 7, 11, 2, 5 }.Select(pc => S("Cmaj7", pc, MidiRoles.Melody)));   // C E G B D F
        Assert.Equal(FitStrength.Strong, S("Cmaj9", 2, MidiRoles.Chords));
        Assert.Equal(FitStrength.Weak, S("Cmaj9", 2, MidiRoles.Bass));
        Assert.Equal(FitStrength.Strongest, S("C7sus4", 5, MidiRoles.Chords));     // the sus note stands in for the 3rd
        Assert.Equal(FitStrength.Strong, S("Cdim7", 9, MidiRoles.Chords));          // a diminished 7th is its 7th
    }

    [Fact]
    public void A_notes_colour_matches_what_its_label_calls_it_over_the_chord()
    {
        // F12 note, 22 Oct 10:01: "colour doesnt match the what degree the note says". The legend: green root, 3rd, 5th;
        // blue 7th, 6th; violet 9, 11, 13.
        FitStrength S(string chord, int pc, string role = MidiRoles.Chords) => NoteStrength.Shown(ChordSymbol.Parse(chord)!.Value, pc, role);
        string D(string chord, int pc) => ChordScales.Degree(ChordSymbol.Parse(chord)!.Value, pc);
        Assert.Equal(("9", FitStrength.Colour), (D("Cmaj9", 2), S("Cmaj9", 2)));     // the chord's own 9 is still a 9
        Assert.Equal(("11", FitStrength.Colour), (D("Cm11", 5), S("Cm11", 5)));
        Assert.Equal(("13", FitStrength.Colour), (D("C13", 9), S("C13", 9)));
        Assert.Equal(("7", FitStrength.Strong), (D("Cmaj9", 11), S("Cmaj9", 11)));
        Assert.Equal(("6", FitStrength.Strong), (D("C", 9), S("C", 9)));             // a 6 over a plain triad is a 6
        Assert.Equal(("4", FitStrength.Strongest), (D("Csus4", 5), S("Csus4", 5)));  // a sus chord's 4 in place of its 3rd
        Assert.Equal(("2", FitStrength.Strongest), (D("Csus2", 2), S("Csus2", 2)));
        Assert.Equal("11", D("C7", 5));                                                // not a sus: still an 11
        Assert.Equal(FitStrength.Fights, S("Cmaj7", 5));                               // the avoid note still fights
        // A bassline keeps its own legend (root and 5th, then the 3rd, then the 7th), and scoring keeps Of.
        Assert.Equal(NoteStrength.Of(ChordSymbol.Parse("C7")!.Value, 4, MidiRoles.Bass), S("C7", 4, MidiRoles.Bass));
        Assert.Equal(FitStrength.Strong, NoteStrength.Of(ChordSymbol.Parse("Cmaj9")!.Value, 2, MidiRoles.Chords));
    }

    [Fact]
    public void Swapping_a_bar_finds_the_bit_of_a_part_that_fits_there_and_joins_on()
    {
        // F12 note, 20 Oct: "select a part of a clip and have suggestions to replace that ... so it finds a fragment that
        // goes". The part plays Cmaj7 – Dm7 – G7 – Cmaj7, and the bar being swapped is under a Dm7: its second bar fits.
        var keys = Chords([48, 52, 55, 59], [50, 53, 57, 60], [43, 53, 59, 62], [48, 52, 55, 59]);
        var catalogue = new MidiCatalogue([Stored(1, @"K:\Other\Keys.flp", "Keys", keys)]);
        var section = new PlaygroundSection { Bars = 1, Chart = "Dm7" };
        // The clip around it plays a Cmaj7 an octave up in the bar before.
        var around = new SwapAround(Chords([60, 64, 67, 71]).Select(n => n with { Start = n.Start - Bar }).ToList(), [], 0, 65.5, 4);
        var best = Assert.Single(SectionMatcher.Rank(catalogue, SectionHarmony.Of(section), MidiRoles.Chords, _ => keys, around: around));
        Assert.StartsWith("Its bar 2", best.Why);
        Assert.Contains("joins on", best.Why);
        Assert.Equal(Bar, best.Period);
        Assert.Equal([2, 5, 9, 0], best.Notes.OrderBy(n => n.Key).Select(n => n.Key % 12));   // D F A C
        Assert.InRange(best.Notes.Average(n => n.Key), 60, 72);                             // up an octave, by the chords before it
        Assert.All(best.Notes, n => Assert.True(n.Start + n.Length <= Bar));

        // A stretch that steps on smoothly joins better than one that leaps.
        var line = new SwapAround(Line(Beat, 60, 62).Select(n => n with { Start = n.Start - 2 * Beat }).ToList(), Line(Beat, 67), 0, 64, 4);
        Assert.True(SectionMatcher.Joins(Line(Beat, 64, 65, 64, 65), line, MidiRoles.Melody, Bar) >
                    SectionMatcher.Joins(Line(Beat, 76, 77, 76, 77), line, MidiRoles.Melody, Bar) + 0.3);

        // Drums join on by playing the same kit, not by how far apart their note numbers are: kick, snare and hats around
        // it, a crash going to a kick is no leap.
        var kit = new SwapAround(Line(Beat, 36, 38, 42, 38).Select(n => n with { Start = n.Start - Bar }).ToList(), Line(Beat, 49, 36), 0, null, 4);
        Assert.True(SectionMatcher.Joins(Line(Beat, 36, 42, 38, 42), kit, MidiRoles.Drums, Bar) >
                    SectionMatcher.Joins(Line(Beat, 60, 61, 62, 63), kit, MidiRoles.Drums, Bar) + 0.3);
    }

    [Fact]
    public void Bars_picked_in_the_piano_roll_are_swapped_for_a_stretch_and_notes_show_how_strongly_they_fit()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        p.PutChords("C | F | G | C");
        var bass = Line(Bar, 36, 41, 43, 36);
        var entry = new MidiCatalogue([Stored(1, @"K:\Other\Bass.flp", "Bass", bass, "bass")]).Entries[0];
        p.AddFromLibrary(entry, bass, "Bass");
        var track = p.Layers[1];
        var row = p.SelectedClip!;

        // Coloured by how strongly each note fits: on the C, its root and 5th, then its 3rd; a C# fights it.
        var strength = row.StrengthOf!;
        Assert.Equal([FitStrength.Strongest, FitStrength.Strongest, FitStrength.Strong, FitStrength.Fights],
                     new[] { 36, 43, 40, 37 }.Select(k => strength(new NoteEvent(0, Beat, k, 100))));
        Assert.True(row.IsBass);
        var under = row.FitGuide!;
        Assert.Equal(4, under.Count);
        Assert.Equal(FitStrength.Strongest, under[1].Strengths[5]);   // bar 2 is the F
        p.NoteColourPick = "Plain";
        Assert.Null(p.SelectedClip!.StrengthOf);
        Assert.Null(p.SelectedClip.FitGuide);
        p.NoteColourPick = PlaygroundViewModel.ByFit;

        // Bars 2–3 picked along the piano roll's bar numbers: the same bars of the timeline, for Suggest to fill.
        p.SelectedClip.PickBarsCommand.Execute(new RollRange(Bar, 3L * Bar));
        Assert.Equal(new RollRange(Bar, 3L * Bar), p.Loop);
        Assert.Equal(new RollRange(Bar, 3L * Bar), p.SelectedClip.Highlight);
        Assert.Same(row.Clip, p.SwapTarget!.Clip);
        Assert.Equal(MidiRoles.Bass, p.SuggestRole.Role);

        // A stretch two bars long that only plays in its first: swapped in, its second bar stays quiet (it doesn't go round).
        var other = new MidiCatalogue([Stored(2, @"K:\Other\Walk.flp", "Bass", Line(Bar, 41, 43), "bass")]).Entries[0];
        p.SwapIn(p.SwapTarget, (Bar, 3L * Bar), other, Line(Bar, 41), 2L * Bar);
        Assert.Single(track.Notes, n => n.Start >= Bar && n.Start < 3L * Bar);

        // A right-click on the bar numbers lets go.
        p.SelectedClip!.PickBarsCommand.Execute(null);
        Assert.Null(p.Loop);
    }

    [Fact]
    public void Notes_are_named_by_what_they_are_over_the_chord()
    {
        string D(string chord, int pc) => ChordScales.Degree(ChordSymbol.Parse(chord)!.Value, pc);
        Assert.Equal(["1", "3", "5", "7", "9", "♯11", "13"], new[] { 0, 4, 7, 11, 2, 6, 9 }.Select(pc => D("Cmaj7", pc)));
        Assert.Equal(["♭3", "♭7", "11"], new[] { 3, 10, 5 }.Select(pc => D("Cm7", pc)));
        Assert.Equal(["♯9", "♭9", "♭13"], new[] { 3, 1, 8 }.Select(pc => D("C7", pc)));
        Assert.Equal("♭5", D("Cdim7", 6));
        Assert.Equal("3", D("G7", 11));
    }

    [Fact]
    public void Review_fixes_keep_tracks_clips_and_selection_in_step()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;

        // A next chord with no chord clip to play it makes a chord track, and it shows on the timeline.
        var bass = Line(Bar, 36, 41);
        p.AddFromLibrary(new MidiCatalogue([Stored(1, @"K:\Other\Bass.flp", "Bass", bass, "bass")]).Entries[0], bass, "Bass");
        p.AddNextChord(ChordSymbol.Parse("G7")!.Value);
        Assert.Equal(p.CurrentSection.Layers.Count, p.Layers.Count);
        Assert.Contains(p.Layers, r => r.Role == MidiRoles.Chords);

        // A melody dragged onto a track written from the chords gets a new track, and keeps its notes.
        p.WriteBassCommand.Execute(null);
        var written = p.Layers.IndexOf(p.Layers.First(r => r.IsWritten));
        var tune = Line(Beat, 72, 74, 76, 77);
        p.AddFromLibrary(new MidiCatalogue([Stored(2, @"K:\Other\Tune.flp", "Lead", tune)]).Entries[0], tune, "Tune");
        var lead = p.Layers[^1];
        var clip = lead.Clips[0];
        var count = p.Layers.Count;
        p.ChangeClipCommand.Execute(new ClipEdit(lead.Layer, clip, written, clip.Start, clip.Length, clip.Offset));
        Assert.DoesNotContain(clip, p.Layers[written].Clips);
        Assert.Equal(count + 1, p.Layers.Count);
        Assert.Equal([72, 74, 76, 77], clip.Notes.Select(n => n.Key));

        // Undo keeps the place: the same track's clip stays picked, not the first track's.
        p.UndoCommand.Execute(null);
        var picked = p.Layers.Count - 1;
        p.SelectClipCommand.Execute(new ClipPick(p.Layers[picked].Layer, p.Layers[picked].Clips[0]));
        p.SelectedClip!.UpCommand.Execute(null);
        p.UndoCommand.Execute(null);
        Assert.Equal(picked, p.Layers.IndexOf(p.SelectedClip!.Track));

        // Duplicated over the next clip on its track: what it covers is cut out, not played on top.
        lead = p.Layers.First(r => r.Clips.Any(c => c.Name == "Tune"));
        var one = lead.Clips[0];
        one.Length = Bar;
        p.DuplicateClip(new ClipRow(p, lead, one));
        p.DuplicateClip(new ClipRow(p, lead, one));
        Assert.Equal([0L, Bar], lead.Clips.Select(c => c.Start).Order());

        // A new idea doesn't keep the old one's highlight.
        p.SetLoopCommand.Execute(new RollRange(Bar, 2L * Bar));
        p.NewIdeaCommand.Execute(null);
        Assert.Null(p.Loop);
    }

    [Fact]
    public void Pane_sizes_are_remembered_and_odd_ones_ignored()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        p.PutChords("C | F");
        Assert.Equal(PlaygroundViewModel.DefaultTrackHeight, p.Layers[0].Height);
        p.TrackHeight = 70;
        p.KeepTrackHeight();
        p.ClipPanelHeight = 420;
        p.SidePanelWidth = 520;
        p.FoldPianoCommand.Execute(null);
        Assert.Equal(70, p.Layers[0].Height);
        Assert.Equal("▸ Show piano roll", p.FoldText);

        var again = new PlaygroundViewModel(db, main);
        Assert.Equal((70.0, 420.0, 520.0, true), (again.TrackHeight, again.ClipPanelHeight, again.SidePanelWidth, again.PianoFolded));
        db.SetSetting(PlaygroundViewModel.TrackHeightKey, "5");
        db.SetSetting(PlaygroundViewModel.SidePanelWidthKey, "lots");
        var odd = new PlaygroundViewModel(db, main);
        Assert.Equal((PlaygroundViewModel.DefaultTrackHeight, PlaygroundViewModel.DefaultSidePanelWidth), (odd.TrackHeight, odd.SidePanelWidth));
    }

    [Fact]
    public void Keys_start_as_bitwigs_can_be_changed_and_drive_the_playground()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var keys = main.Shortcuts;
        const string Pg = ShortcutsViewModel.Playground;

        // Bitwig's keys to start with, however the key is written
        Assert.Equal("playground.duplicate", keys.Find(Pg, "ctrl+d"));
        Assert.Equal("playground.octaveUp", keys.Find(Pg, "Alt+Shift+="));
        Assert.Equal("playground.zoomIn", keys.Find(Pg, "Ctrl++"));
        Assert.True(ShortcutsViewModel.WorksWhileTyping("Ctrl+Z"));
        Assert.False(ShortcutsViewModel.WorksWhileTyping("Z"));

        // A key already in use moves over, the other action is left with none, and it says so
        keys.Set("playground.fitAgain", "Ctrl+D");
        Assert.Equal("playground.fitAgain", keys.Find(Pg, "Ctrl+D"));
        Assert.Equal("", keys.KeyOf("playground.duplicate"));
        Assert.Contains("Duplicate the picked clip", keys.Message);
        Assert.True(keys.Rows.Single(r => r.Action.Id == "playground.duplicate").IsChanged);
        // ...and is remembered
        Assert.Equal("playground.fitAgain", new ShortcutsViewModel(db).Find(Pg, "Ctrl+D"));
        keys.ResetAll();
        Assert.Equal("playground.duplicate", keys.Find(Pg, "Ctrl+D"));
        Assert.False(keys.AnyChanged);
        Assert.DoesNotContain(keys.For(Pg), r => r.Key.Length == 0);   // the cheat sheet leaves out actions with no key

        // The keys work the playground: duplicate, nudge, transpose, split at the play marker, zoom
        var p = main.Playground;
        p.PutChords("C | F | G | C");
        var bass = Line(Bar, 36, 41);
        var entry = new MidiCatalogue([Stored(1, @"K:\Other\Bass.flp", "Bass", bass, "bass")]).Entries[0];
        p.AddFromLibrary(entry, bass, "Bass");
        var track = p.Layers[1];
        p.SelectClipCommand.Execute(new ClipPick(track.Layer, track.Clips[0]));
        var len = track.Clips[0].Length;
        Assert.True(p.RunShortcut(keys.Find(Pg, "Ctrl+D")!));
        Assert.Equal([0L, len], track.Clips.Select(c => c.Start).Order());
        Assert.True(p.RunShortcut(keys.Find(Pg, "Shift+Right")!));
        Assert.Equal(len + Bar, p.SelectedClip!.Clip.Start);
        Assert.True(p.RunShortcut(keys.Find(Pg, "Shift+Alt+=")!));
        Assert.Equal(12, p.SelectedClip!.Clip.Transpose);
        Assert.True(p.SelectedClip.Clip.Pinned);

        p.SelectClipCommand.Execute(new ClipPick(track.Layer, track.Clips.First(c => c.Start == 0)));
        p.SetCursorCommand.Execute((long)Bar);
        Assert.True(p.RunShortcut("playground.split"));
        var pieces = track.Clips.Where(c => c.End <= len).OrderBy(c => c.Start).ToList();
        Assert.Equal([(0L, (long)Bar), (Bar, len - Bar)], pieces.Select(c => (c.Start, c.Length)));
        Assert.Equal([36, 41], track.Notes.Where(n => n.Start < 2L * Bar).Select(n => n.Key));   // still plays as it did
        p.UndoCommand.Execute(null);
        Assert.Equal(2, p.Layers[1].Clips.Count);

        Assert.True(p.RunShortcut("playground.zoomIn"));
        Assert.True(p.ViewLength > 0 && p.ViewLength < p.Length);
        Assert.True(p.RunShortcut("playground.zoomFit"));
        Assert.Equal(0, p.ViewLength);
        Assert.True(p.RunShortcut("playground.help"));
        Assert.True(p.CheatSheetOpen);
        p.SelectClipCommand.Execute(null);
        Assert.False(p.RunShortcut("playground.delete"));   // nothing picked: the key goes on to whatever has it
    }

    [Fact]
    public async Task Open_in_playground_from_a_song_starts_a_section_with_the_part()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        p.PutChords("Dm7 | G7 | Cmaj7 | %");

        var lead = Line(2 * Beat, 65, 69, 65, 64, 64, 67, 71, 74);
        var entry = new MidiCatalogue([Stored(1, @"K:\Other\Tune.flp", "Lead", lead)]).Entries[0];
        // A song the app can't read (no reader for it): the part still comes in, after the chords, labelled with the song.
        await p.OpenFromSongAsync(Path.Combine(_root, "song.txt"), "Song", entry, lead, "Lead");
        Assert.Equal(8, p.Bars);
        Assert.Equal(["Chords", "Lead"], p.Layers.Select(l => l.Name));
        Assert.Equal(4L * Bar, p.Layers[1].Clips.Single().Start);
        Assert.Equal(("Song", 4L * Bar), (p.Labels.Single().Name, p.Labels.Single().Start));
        Assert.Same(p, main.SelectedNav.Page);
    }

    [Fact]
    public async Task A_cut_down_playground_works_apart_and_hands_its_section_to_the_playground()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        main.Playground.PutChords("Dm7 | G7 | Cmaj7 | %");

        var lead = Line(2 * Beat, 65, 69, 65, 64, 64, 67, 71, 74);
        var entry = new MidiCatalogue([Stored(1, @"K:\Other\Tune.flp", "Lead", lead)]).Entries[0];
        var quick = main.Playground.Quick();
        await quick.LoadSongAsync(Path.Combine(_root, "song.txt"), "Song", entry, lead, "Lead");
        Assert.Equal(["Lead"], quick.Layers.Select(l => l.Name));
        Assert.Equal(["Chords"], main.Playground.Layers.Select(l => l.Name));   // the Playground's own idea is left alone

        // Taken over, its parts go after the Playground's bars, labelled.
        quick.OpenInMainCommand.Execute(null);
        Assert.Equal(["Chords", "Lead"], main.Playground.Layers.Select(l => l.Name));
        Assert.Equal(4L * Bar, main.Playground.Layers[1].Clips.Single().Start);
        Assert.Equal("Song", main.Playground.Labels.Single().Name);
        Assert.Same(main.Playground, main.SelectedNav.Page);
    }

    [Fact]
    public void Settings_options_are_kept_and_start_new_ideas()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Settings.PlaygroundPage;
        p.NewTempo = 120;
        p.NewBars = 8;
        p.NewStyle = PlaygroundViewModel.Classical;
        p.SuggestCount = 100;
        p.SmoothIncoming = false;

        // Read back by a fresh app, and the first idea there starts the new way.
        var again = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data2")).Playground;
        Assert.Equal(100, again.SuggestCount);
        Assert.False(again.SmoothIncoming);
        Assert.Equal(120, again.Tempo);
        Assert.Equal(8, again.Bars);
        Assert.True(again.IsClassical);
    }

    [Fact]
    public void A_groove_copied_onto_written_chords_in_an_older_idea_is_kept_when_the_chords_change()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;

        p.PutChords("Dm7 | G7 | Cmaj7 | %");
        p.Rhythm = "Held";
        p.WriteChordsCommand.Execute(null);
        p.WriteBassCommand.Execute(null);
        var chords = p.Layers[1];
        p.SelectedLayer = chords;
        // The walking bass's rhythm copied onto the chords (the Groove tab did this before chords first, step 2).
        chords.Layer.GrooveNotes = p.Layers.Single(r => r.Role == MidiRoles.Bass).Layer.Notes;
        chords.Layer.GrooveDepth = GrooveDepth.Rhythm;
        chords.Layer.GrooveFrom = "Bass";
        Assert.Contains("groove of", chords.Details);

        // New chords played on the chord part: the written part is written again and struck on the beats again.
        p.SelectClipCommand.Execute(new ClipPick(p.Layers[0].Layer, p.Layers[0].Clips[0]));
        p.SelectedClip!.EditedCommand.Execute(Chords([52, 55, 59, 62], [45, 55, 61, 64], [50, 54, 57, 61], [50, 54, 57, 61]));
        p.SelectedLayer = chords;
        Assert.Equal(16, chords.Notes.Select(n => n.Start).Distinct().Count());
        Assert.Contains(chords.Notes, n => n.Start == 0 && n.Key % 12 == 4);

        // Smooth inversions never change the notes of a chord.
        static string Sets(IEnumerable<NoteEvent> notes) =>
            string.Join("|", notes.GroupBy(n => n.Start).OrderBy(g => g.Key).Select(g => string.Join(",", g.Select(n => n.Key % 12).Distinct().OrderBy(k => k))));
        var before = Sets(chords.Notes);
        p.SmoothInversionsCommand.Execute(null);
        Assert.Equal(before, Sets(chords.Notes));
    }

    // ---- playing along with Bitwig (2026-10-22) ----

    [Fact]
    public void Following_Bitwig_starts_at_its_place_in_the_bar_jumps_only_when_out_and_stops_with_it()
    {
        var f = new DawFollow();
        long length = 4L * Bar;
        Assert.Equal(FollowMove.Wait, f.Next(false, 0, 120, Ppq, length, null).Move);

        // Bitwig plays from beat 21 (bar 6, beat 2): a 4-bar idea is at its own bar 2, beat 2.
        Assert.Equal(new FollowStep(FollowMove.Start, 5L * Beat), f.Next(true, 21, 120, Ppq, length, null));
        Assert.True(f.Following);
        // In step, give or take the little each answer is late: it plays on.
        for (var i = 1; i <= 6; i++)
            Assert.Equal(FollowMove.Wait, f.Next(true, 21 + i * 0.1, 120, Ppq, length, 5L * Beat + (long)(i * 0.1 * Beat) + Beat / 50).Move);

        // Bitwig goes back to its beat 0 (its loop going round): a few looks on, the Playground goes there too.
        var step = new FollowStep(FollowMove.Wait);
        var looks = 0;
        for (; looks < 10 && step.Move == FollowMove.Wait; looks++)
            step = f.Next(true, looks * 0.1, 120, Ppq, length, 6L * Beat + (long)(looks * 0.1 * Beat));
        Assert.Equal(FollowMove.Seek, step.Move);
        Assert.InRange(looks, DawFollow.Looks, DawFollow.Looks + 3);
        Assert.InRange(step.Tick, (long)((looks - 1) * 0.1 * Beat) - 1, (long)((looks - 1) * 0.1 * Beat) + 1);

        // Bitwig stops: so does the Playground. Played on its own while Bitwig's stopped, it's left alone.
        Assert.Equal(FollowMove.Stop, f.Next(false, 3, 120, Ppq, length, 3L * Beat).Move);
        Assert.False(f.Following);
        Assert.Equal(FollowMove.Wait, f.Next(false, 0, 120, Ppq, length, 2L * Beat).Move);

        // Played a tenth of a second early, at 120 BPM: a fifth of a beat on.
        Assert.Equal(new FollowStep(FollowMove.Start, (long)Math.Round(Beat / 5.0)), f.Next(true, 0, 120, Ppq, length, null, nudge: 0.1));
    }

    [Fact]
    public async Task Follow_Bitwig_plays_along_from_Bitwigs_place_at_its_tempo_and_stops_with_it()
    {
        var ui = new Headless();
        Ui.Current = ui;
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var p = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data")).Playground;
        var tune = Line(Beat, 72, 74, 76, 77);   // one bar
        p.AddFromLibrary(new MidiCatalogue([Stored(1, @"K:\Other\Tune.flp", "Lead", tune, "melody lead")]).Entries[0], tune, "Lead");
        p.ClearLoopCommand.Execute(null);
        var tempo = p.Tempo;

        await p.FollowLook(new BitwigTransport(false, 0, 0, 100, "4/4"));
        Assert.False(p.IsPlaying);

        // Bitwig plays from beat 6 at 100 BPM: the one-bar idea plays from its third beat, at Bitwig's tempo.
        await p.FollowLook(new BitwigTransport(true, 6, 0, 100, "4/4"));
        Assert.True(p.IsPlaying);
        Assert.Equal(MidiRender.TicksToSeconds(2L * Beat, Ppq, 100), ui.Player!.StartedAt, 3);
        Assert.Equal(tempo, p.Tempo);   // the idea keeps its own

        // Bitwig at beat 9 while the Playground's still on beat 2: it jumps to beat 1 once the looks agree.
        ui.Player.At = MidiRender.TicksToSeconds(2L * Beat, Ppq, 100);
        for (var i = 0; i < DawFollow.Looks; i++) await p.FollowLook(new BitwigTransport(true, 9, 0, 100, "4/4"));
        Assert.Equal(MidiRender.TicksToSeconds(Beat, Ppq, 100), Assert.Single(ui.Player.Seeks), 3);

        // Stop in the Playground stops following; Bitwig stopping stops it too.
        await p.FollowLook(new BitwigTransport(false, 9, 0, 100, "4/4"));
        Assert.False(p.IsPlaying);
    }

    [Fact]
    public async Task Following_Bitwig_waits_out_a_go_round_spaces_out_tempo_changes_and_lets_go_of_its_tempo()
    {
        var ui = new Headless();
        Ui.Current = ui;
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var p = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data")).Playground;
        var tune = Line(Beat, 72, 74, 76, 77);
        p.AddFromLibrary(new MidiCatalogue([Stored(1, @"K:\Other\Tune.flp", "Lead", tune, "melody lead")]).Entries[0], tune, "Lead");
        p.ClearLoopCommand.Execute(null);
        p.Tempo = 120;
        var now = TimeSpan.FromSeconds(100);
        p.Clock = () => now;
        await p.PlayCommand.ExecuteAsync(null);
        var ownLength = ui.Player!.Samples;
        await p.PlayCommand.ExecuteAsync(null);   // stop

        await p.FollowLook(new BitwigTransport(true, 0, 0, 100, "4/4"));
        Assert.True(p.IsPlaying);
        var plays = ui.Player.Plays;

        // Going round again, the sound seems stopped for a moment: it isn't started again on top of the next go round.
        ui.Player.Stop();
        for (var i = 0; i < 3; i++) await p.FollowLook(new BitwigTransport(true, 4 + i * 0.1, 0, 100, "4/4"));
        Assert.Equal(plays, ui.Player.Plays);

        // A tempo ramp in Bitwig: the sound is made again at most every half second, but at once for a big jump.
        ui.Player.At = 0;
        await p.FollowLook(new BitwigTransport(true, 0, 0, 100.2, "4/4"));
        var afterFirst = ui.Player.Plays;
        Assert.Equal(plays + 1, afterFirst);
        now += TimeSpan.FromMilliseconds(100);
        await p.FollowLook(new BitwigTransport(true, 0, 0, 100.4, "4/4"));
        Assert.Equal(afterFirst, ui.Player.Plays);
        await p.FollowLook(new BitwigTransport(true, 0, 0, 104, "4/4"));
        Assert.Equal(afterFirst + 1, ui.Player.Plays);

        // A song in 3/4 is said to follow Bitwig's beats, not its bars.
        await p.FollowLook(new BitwigTransport(true, 0, 0, 104, "3/4"));
        Assert.Contains("3/4", p.Message);

        // Bitwig stops: the Playground stops, and Play here plays at the idea's own tempo again.
        await p.FollowLook(new BitwigTransport(false, 0, 0, 104, "3/4"));
        Assert.False(p.IsPlaying);
        await p.PlayCommand.ExecuteAsync(null);
        Assert.Equal(ownLength, ui.Player.Samples);
    }

    [Fact]
    public async Task Play_in_Bitwig_puts_each_part_on_a_track_of_its_own_there_and_sends_only_what_changed()
    {
        Ui.Current = new Headless();
        UiWork.InBackground = false;
        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        db.SetSetting(ProjectDetailViewModel.ExportFolderKey, Path.Combine(_root, "exports"));
        using var soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        using var sounds = new SoundOrganiser(soundDb);
        var p = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data")).Playground;
        var tune = Line(Beat, 72, 74, 76, 77);
        var bass = Line(2 * Beat, 36, 43);
        var catalogue = new MidiCatalogue([Stored(1, @"K:\Other\Tune.flp", "Lead", tune, "melody lead"), Stored(2, @"K:\Other\Tune.flp", "Bass", bass, "bass")]);
        p.AddFromLibrary(catalogue.Entries.First(e => e.Phrase.Part == "Lead"), tune, "Lead");
        p.AddFromLibrary(catalogue.Entries.First(e => e.Phrase.Part == "Bass"), bass, "Bass");
        p.ClearLoopCommand.Execute(null);
        var made = new List<string>();
        var put = new List<(string Track, string File)>();
        var stopped = new List<string>();
        p.BitwigNewTrack = (name, _) =>
        {
            made.Add(name);
            return Task.FromResult($"track {made.Count}");
        };
        p.BitwigPutClip = (track, file, _) =>
        {
            put.Add((track, file));
            return Task.CompletedTask;
        };
        p.BitwigStopTracks = (tracks, _) =>
        {
            stopped.AddRange(tracks);
            return Task.CompletedTask;
        };
        p.SendAfter = TimeSpan.FromHours(1);
        p.BitwigTransport = _ => Task.FromException<BitwigTransport>(new OperationCanceledException());
        p.FollowBitwig = true;

        // On: a track for each part, its notes there as a clip a bar long; Follow Bitwig goes off, as both would play twice.
        p.PlayInBitwig = true;
        await p.SendToPlayAsync();
        Assert.False(p.FollowBitwig);
        Assert.Equal(["Lead (Playground)", "Bass (Playground)"], made);
        Assert.Equal(["track 1", "track 2"], put.Select(x => x.Track));
        var lead = Assert.Single(MidiFileReader.Read(put[0].File));
        Assert.Equal(tune.Select(n => n.Key), lead.Notes.Select(n => n.Key));
        Assert.StartsWith("Playing in Bitwig: 2 parts", p.Message);

        // Nothing changed: nothing goes across. The bass muted: its track stops; back on, it goes back on its own track.
        await p.SendToPlayAsync();
        Assert.Equal(2, put.Count);
        p.ToggleMute(p.Layers.Single(l => l.Layer.Name == "Bass"));
        await p.SendToPlayAsync();
        Assert.Equal(["track 2"], stopped);
        p.ToggleMute(p.Layers.Single(l => l.Layer.Name == "Bass"));
        await p.SendToPlayAsync();
        Assert.Equal(2, made.Count);
        Assert.Equal("track 2", put[^1].Track);

        // Its track deleted in Bitwig: the part goes on a new one.
        p.BitwigPutClip = (track, file, _) =>
        {
            if (track == "track 1") throw new DawLinkException("There's no track with ID track 1 in the project listing.");
            put.Add((track, file));
            return Task.CompletedTask;
        };
        var leadRow = p.Layers.Single(l => l.Layer.Name == "Lead");
        p.ToggleMute(leadRow);
        await p.SendToPlayAsync();
        p.ToggleMute(leadRow);
        await p.SendToPlayAsync();
        Assert.Equal(["Lead (Playground)", "Bass (Playground)", "Lead (Playground)"], made);
        Assert.Equal("track 3", put[^1].Track);

        // Off: the tracks playing stop, and stay in Bitwig.
        stopped.Clear();
        p.PlayInBitwig = false;
        Assert.Equal(["track 2", "track 3"], stopped.Order());
        Assert.Contains("Stopped playing in Bitwig", p.Message);
    }

    internal sealed class Headless : IPlatformUi
    {
        public IItemsView CreateView(IList items) => new ListView(items);
        public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new NoTimer();
        public Silence? Player { get; private set; }
        public IAudioPlayer CreateAudioPlayer() => Player = new Silence();
        public Task<string?> PickFolder(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFile(string title, string filterName, string extension) => Task.FromResult<string?>(null);
        public Task<string?> PickProgram(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveFile(string fileName, string filterName, string extension, string? initialFolder) => Task.FromResult<string?>(null);
        public Task<bool> Confirm(string message, string title) => Task.FromResult(true);
        public void CopyText(string text) { }
        public void ShowMidi(MidiPreviewViewModel vm) { }
        public void ShowPresets(PresetsViewModel vm) { }
    }

    sealed class ListView(IList items) : IItemsView
    {
        public Predicate<object>? Filter { get; set; }
        public IComparer? CustomSort { get; set; }
        public void SortBy(string property, ListSortDirection direction) { }
        public void GroupBy(string? property) { }
        public void Refresh() { }
        public int Count => items.Cast<object>().Count(i => Filter?.Invoke(i) ?? true);
        public IEnumerator GetEnumerator() => items.Cast<object>().Where(i => Filter?.Invoke(i) ?? true).ToList().GetEnumerator();
    }

    sealed class NoTimer : IUiTimer { public void Start() { } public void Stop() { } }

    internal sealed class Silence : IAudioPlayer
    {
        public event Action? Stopped { add { } remove { } }
        public string? Playing { get; private set; }
        public double? Position => Playing is null ? null : At ?? 0;
        /// <summary>Where a test says it's got to (else the start).</summary>
        public double? At { get; set; }
        public double StartedAt { get; private set; }
        /// <summary>How long the last sound played was, in samples: longer at a slower tempo.</summary>
        public int Samples { get; private set; }
        /// <summary>How many sounds were played.</summary>
        public int Plays { get; private set; }
        public List<double> Seeks { get; } = [];
        public void Seek(double seconds) => Seeks.Add(seconds);
        public void Play(string path, double startSeconds = 0) => Playing = path;
        public void Play(float[] mono, int sampleRate, string key, double startSeconds = 0)
        {
            (Playing, StartedAt, Samples) = (key, startSeconds, mono.Length);
            Plays++;
        }
        public void Stop() => Playing = null;
        public void Dispose() { }
    }
}
