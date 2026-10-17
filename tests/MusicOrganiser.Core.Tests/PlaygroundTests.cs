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
    public void The_arrangement_joins_parts_of_the_same_name_across_sections()
    {
        var session = new PlaygroundSession
        {
            Sections =
            [
                new() { Name = "A", Bars = 2, Layers = [new PlaygroundLayer { Name = "Keys", Notes = Line(Bar, 60, 62) }] },
                new() { Name = "B", Bars = 1, Layers = [new PlaygroundLayer { Name = "Keys", Notes = Line(Bar, 65) }, new PlaygroundLayer { Name = "Bass", Notes = Line(Bar, 41), Muted = true }] },
            ],
            Arrangement = "A B A",
        };
        var tracks = session.ArrangedTracks();
        var keys = Assert.Single(tracks);
        Assert.Equal([0L, Bar, 2L * Bar, 3L * Bar, 4L * Bar], keys.Notes.Select(n => n.Start));
        Assert.Equal([60, 62, 65, 60, 62], keys.Notes.Select(n => n.Key));

        var back = PlaygroundSession.FromJson(session.ToJson())!;
        Assert.Equal("A B A", back.Arrangement);
        Assert.Equal(Line(Bar, 65), back.Sections[1].Layers[0].Notes);
        Assert.True(back.Sections[1].Layers[1].Muted);
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
        var main = new MainViewModel(db, sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;

        p.ChartText = "Dm7 | G7 | Cmaj7 | %";
        Assert.Equal(["Dm7", "G7", "Cmaj7"], p.Chords.Select(c => c.Name));
        Assert.Equal(["ii7", "V7", "Imaj7"], p.Chords.Select(c => c.Roman));
        Assert.StartsWith("Key: C major", p.KeySummary);

        p.Voicing = "Rootless (jazz piano)";
        p.WriteChordsCommand.Execute(null);
        p.BassPick = "Walking";
        p.WriteBassCommand.Execute(null);
        Assert.Equal(2, p.Layers.Count);
        Assert.Equal(16, p.Layers[1].Notes.Count);

        // The tritone sub: the written parts follow the new chords.
        var sub = p.Ideas.First(i => i.Title == "Tritone sub");
        sub.ApplyCommand.Execute(null);
        Assert.Equal(["Dm7", "Db9", "Cmaj7"], p.Chords.Select(c => c.Name));
        Assert.Equal(1, p.Layers[1].Notes.First(n => n.Start == Bar).Key % 12);   // the walking bass starts bar 2 on Db

        // Undo puts the chords back, and the idea was saved: a new page opens it as it was.
        p.UndoCommand.Execute(null);
        Assert.Equal(["Dm7", "G7", "Cmaj7"], p.Chords.Select(c => c.Name));
        var again = new PlaygroundViewModel(db, main);
        Assert.Equal(2, again.Layers.Count);
        Assert.Equal("Dm7 | G7 | Cmaj7 | %", again.ChartText);

        // Dragging the section out writes one MIDI file with a track per part.
        var drag = p.MakeDrag(["section"])!;
        var tracks = MidiFileReader.Read(File.ReadAllBytes(drag.Files[0]), "x");
        Assert.Equal(2, tracks.Count);
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

        p.ChartText = "Dm7 | G7 | Cmaj7 | %";
        p.Rhythm = "Held";
        p.WriteChordsCommand.Execute(null);
        p.WriteBassCommand.Execute(null);
        var chords = p.Layers[0];
        p.SelectedLayer = chords;
        // Copy the walking bass's rhythm: the held chords are now struck on every beat.
        p.GrooveSource = p.GrooveSources.Single(r => r.Role == MidiRoles.Bass);
        p.GrooveAmount = 5;
        p.CopyGrooveCommand.Execute(null);
        Assert.Equal(16, chords.Notes.Select(n => n.Start).Distinct().Count());
        Assert.Contains("groove of", chords.Details);

        // New chords: the part is written again and struck on the beats again.
        p.ChartText = "Em7 | A7 | Dmaj7 | %";
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
