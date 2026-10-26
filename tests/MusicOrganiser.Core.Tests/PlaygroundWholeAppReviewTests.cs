using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Core.Tests;

// The 2026-10-26 whole-app review's Playground findings that a test can show. Made-up parts only.
[Collection("Global UI")]
public class PlaygroundWholeAppReviewTests : IDisposable
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

    static List<NoteEvent> Tune(int bars) =>
        Enumerable.Range(0, bars * 4).Select(i => new NoteEvent(i * (long)Ppq, Ppq, new[] { 64, 67, 69, 67 }[i % 4], 100)).ToList();

    static ChordSymbol Chord(string name) => ChordLane.ParseChart(name, out _)![0].Chord;
    static PlaygroundChord Block(string name, long start, long length) => new() { Start = start, Length = length, Chord = Chord(name) };
    static readonly int[] Dm7 = [38, 53, 57, 60], G7 = [43, 53, 59, 62];

    sealed class Page(PlaygroundViewModel p, MainViewModel main, IDisposable[] keep) : IDisposable
    {
        public PlaygroundViewModel P => p;
        public MainViewModel Main => main;
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
        return new Page(main.Playground, main, [db, soundDb, sounds]);
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

    [Fact]
    public async Task Saving_a_new_idea_never_saves_over_another_saved_idea_of_the_same_name()
    {
        using var page = Fresh();
        var p = page.P;
        Drop(p, Mid("Keys", Chords(Dm7, G7)), 0);
        p.SaveIdeaCommand.Execute(null);
        Assert.Equal(["Untitled idea"], p.SavedIdeas);
        // Saved again, it replaces itself.
        p.SaveIdeaCommand.Execute(null);
        Assert.Single(p.SavedIdeas);

        await p.NewIdeaCommand.ExecuteAsync(null);
        Drop(p, Mid("Lead", Tune(2)), 0);
        p.SaveIdeaCommand.Execute(null);
        Assert.Equal(2, p.SavedIdeas.Count);
        Assert.Equal("Untitled idea 2", p.IdeaName);
        Assert.Contains("kept", p.Message);

        // The first is still the keys idea, and opened and saved again it goes back where it came from.
        p.OpenIdeaCommand.Execute("Untitled idea");
        Assert.Equal("Keys", Assert.Single(p.Layers).Name);
        p.SaveIdeaCommand.Execute(null);
        Assert.Equal(2, p.SavedIdeas.Count);
        p.OpenIdeaCommand.Execute("Untitled idea 2");
        Assert.Equal("Lead", Assert.Single(p.Layers).Name);

        // And it's remembered after the app is closed and opened again.
        var store = new PlaygroundStore(Path.Combine(_root, "data", "playground"));
        Assert.Equal("Untitled idea 2", store.LoadCurrent().SavedAs);
    }

    [Fact]
    public void A_clip_pasted_onto_a_track_written_from_the_chords_keeps_its_notes()
    {
        using var page = Fresh();
        var p = page.P;
        p.CurrentSection.Bars = 2;
        Drop(p, Mid("Keys", Chords(Dm7, G7)), 0);
        p.WriteBassCommand.Execute(null);
        Drop(p, Mid("Lead", Tune(2)), p.Layers.Count);
        var lead = p.Layers.Single(l => l.Name == "Lead");
        var written = p.Layers.Single(l => l.IsWritten);
        p.SelectClipCommand.Execute(new ClipPick(lead.Layer, lead.Layer.Clips[0]));
        var tune = lead.Layer.Clips[0].Notes;
        Assert.True(p.RunShortcut("playground.copy"));

        p.SelectClipCommand.Execute(new ClipPick(written.Layer, written.Layer.Clips[0]));
        Assert.True(p.RunShortcut("playground.paste"));
        Assert.Single(written.Layer.Clips);   // the bassline as it was
        Assert.Equal(lead.Layer, p.SelectedClip!.Track.Layer);   // the copy went back on the track it came from
        Assert.Equal(tune, p.SelectedClip.Clip.Notes);
    }

    [Fact]
    public void A_part_added_to_chords_on_the_chord_track_doesnt_cut_them_short()
    {
        using var page = Fresh();
        var p = page.P;
        p.CurrentSection.ChordTrack = Enumerable.Range(0, 8).Select(i => Block(i % 2 == 0 ? "Dm7" : "G7", i * (long)Bar, Bar)).ToList();
        p.Bars = 8;
        Assert.False(p.IsEmpty);
        Assert.Equal("Suggest", p.SuggestTab);

        var bass = new List<NoteEvent> { new(0, Bar, 38, 100), new(Bar, Bar, 43, 100) };
        p.AddFromLibrary(new MidiCatalogue([PlaygroundTests.Stored(1, @"K:\Other\Bass.flp", "Bass", bass, "bass")]).Entries[0], bass, "Bass");
        Assert.Equal(8, p.Bars);
        Assert.Equal(8, p.Chords.Count(c => c.IsSet));
    }

    [Fact]
    public async Task Changing_the_style_key_name_or_tempo_is_an_undo_step_of_its_own()
    {
        using var page = Fresh();
        var p = page.P;
        Drop(p, Mid("Keys", Chords(Dm7, G7)), 0);

        p.IsClassical = true;
        p.UndoCommand.Execute(null);
        Assert.False(p.IsClassical);
        Assert.Single(p.Layers);   // the part added before stays

        p.KeyText = "Eb";
        p.UndoCommand.Execute(null);
        Assert.Equal("", p.KeyText);
        Assert.Single(p.Layers);

        p.IdeaName = "Late night";
        p.UndoCommand.Execute(null);
        Assert.Equal("Untitled idea", p.IdeaName);
        Assert.Single(p.Layers);

        // Steps close together (scrolling over the box) are one undo step.
        var was = p.Tempo;
        p.Tempo = was + 5;
        p.Tempo = was + 6;
        await Task.Delay(500);   // it settles
        p.UndoCommand.Execute(null);
        Assert.Equal(was, p.Tempo);
        Assert.Single(p.Layers);
        p.UndoCommand.Execute(null);
        Assert.Empty(p.Layers);
    }

    [Fact]
    public void A_next_chord_added_with_the_chord_part_muted_never_plays_the_old_chord_over_it()
    {
        using var page = Fresh();
        var p = page.P;
        p.CurrentSection.Bars = 2;
        Drop(p, Mid("Keys", Chords(Dm7, G7)), 0);
        var keys = p.Layers[0].Layer;
        p.Layers[0].MuteCommand.Execute(null);
        p.AddNextChord(Chord("Cmaj7"));
        Assert.Equal(3, p.Bars);

        // The new bar is heard, from a plain chord part, and the muted keys play it too once they're heard again.
        var c = new[] { 0, 4, 7, 11 };
        var plain = p.Layers.Single(l => l.Layer != keys).Layer;
        Assert.Single(plain.Clips, k => k.Start < 3 * Bar && k.End > 2 * Bar);
        Assert.All(plain.Notes.Where(n => n.Start >= 2 * Bar), n => Assert.Contains(n.Key % 12, c));
        p.Layers.Single(l => l.Layer == keys).MuteCommand.Execute(null);
        var bar3 = keys.Notes.Where(n => n.Start >= 2 * Bar).ToList();
        Assert.NotEmpty(bar3);
        Assert.All(bar3, n => Assert.Contains(n.Key % 12, c));
    }

    [Fact]
    public void A_fit_to_chip_stays_in_step_with_its_part_after_undo()
    {
        using var page = Fresh();
        var q = page.P.Quick();
        Drop(q, Mid("Piano", Chords(Dm7, G7)), 0);
        Drop(q, Mid("Lead", Tune(2)), q.Layers.Count);
        var piano = q.Layers.Single(l => l.Name == "Piano");
        piano.Layer.SongPart = "Piano";
        q.Layers.Single(l => l.Name == "Lead").UpCommand.Execute(null);   // an edit to undo

        q.MuteSongParts(["Piano"], ["Piano"]);
        Assert.True(piano.Layer.Muted);
        q.UndoCommand.Execute(null);
        Assert.True(q.Layers.Single(l => l.Name == "Piano").Layer.Muted);   // still off, as its chip shows
        q.MuteSongParts(["Piano"], []);
        Assert.False(q.Layers.Single(l => l.Name == "Piano").Layer.Muted);   // one click turns it back on
    }
}
