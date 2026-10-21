using System.Collections;
using System.ComponentModel;
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

    static StoredPhrase Stored(long id, string path, string part, List<NoteEvent> notes, string hints = "")
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

        // Add on a part of that list: on the timeline, and Suggest lists parts that fit it.
        for (var i = 0; i < 300 && p.Midi.Results.Count != 1; i++) await Task.Delay(10);
        p.AddMidiRowCommand.Execute(p.Midi.Results.Single(r => r.Entry.Role == MidiRoles.Bass));
        Assert.False(p.IsEmpty, p.Message);
        Assert.Single(p.Layers);
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

        await p.PreviewMatch(row);   // pressed again: stops, and the timeline is just the idea again
        Assert.Null(p.Trying);
        Assert.False(row.IsPlaying);
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
    public void Copying_a_groove_onto_written_chords_keeps_it_when_the_chords_change()
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
        // Copy the walking bass's rhythm: the held chords are now struck on every beat.
        p.GrooveSource = p.GrooveSources.Single(r => r.Role == MidiRoles.Bass);
        p.GrooveAmount = 5;
        p.CopyGrooveCommand.Execute(null);
        Assert.Equal(16, chords.Notes.Select(n => n.Start).Distinct().Count());
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

    sealed class Headless : IPlatformUi
    {
        public IItemsView CreateView(IList items) => new ListView(items);
        public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new NoTimer();
        public IAudioPlayer CreateAudioPlayer() => new Silence();
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

    sealed class Silence : IAudioPlayer
    {
        public event Action? Stopped { add { } remove { } }
        public string? Playing { get; private set; }
        public double? Position => Playing is null ? null : 0;
        public void Seek(double seconds) { }
        public void Play(string path, double startSeconds = 0) => Playing = path;
        public void Play(float[] mono, int sampleRate, string key, double startSeconds = 0) => Playing = key;
        public void Stop() => Playing = null;
        public void Dispose() { }
    }
}
