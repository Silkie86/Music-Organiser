using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Midi;

namespace MusicOrganiser.Core.Tests;

/// <summary>Naming the instrument a MIDI part is for from what comes with it: names, plugins, channel 10, General MIDI sounds.</summary>
public class InstrumentTests
{
    [Theory]
    [InlineData("Keyscape", Instruments.Piano)]
    [InlineData("Addictive Drums 2", Instruments.Drums)]
    [InlineData("Kick 1", Instruments.Drums)]
    [InlineData("Bass 2", Instruments.Bass)]
    [InlineData("Bass Drum", Instruments.Drums)]
    [InlineData("Pad2", Instruments.Pad)]
    [InlineData("HiHats_open", Instruments.Drums)]
    [InlineData("Rhodes chords", Instruments.Piano)]
    [InlineData("Cowbell", Instruments.Drums)]
    public void Names_that_say_what_plays_name_it(string name, string family) =>
        Assert.Equal(family, Instruments.Guess([name])?.Name);

    [Fact]
    public void A_plugin_says_it_is_a_plugin_and_a_word_says_it_is_a_word()
    {
        Assert.Equal("“Keyscape” is a piano plugin", Instruments.Guess(["Keyscape"])!.Why);
        Assert.Equal("“Addictive Drums 2” is a drum plugin", Instruments.Guess(["Addictive Drums 2"])!.Why);
        Assert.Equal("The name “Kick 1” says kick", Instruments.Guess(["Kick 1"])!.Why);
    }

    [Fact]
    public void Names_that_say_nothing_name_nothing()
    {
        Assert.Null(Instruments.Guess(["Serum", "Idea 01", "Zenology", ""]));
        Assert.Null(Instruments.Guess([]));
        // "Ep" and "chops" are too short or too vague to mean a piano.
        Assert.Null(Instruments.Guess(["Ep 3", "Chops"]));
    }

    [Fact]
    public void Channel_10_and_general_midi_sounds_name_it_but_the_default_piano_does_not()
    {
        Assert.Equal(Instruments.Drums, Instruments.Guess(["Idea"], channel10: true)!.Name);
        var bass = Instruments.Guess(["Idea"], program: 33)!;
        Assert.Equal(Instruments.Bass, bass.Name);
        Assert.Equal("The file asks for General MIDI sound 34: Electric Bass (finger)", bass.Why);
        Assert.Null(Instruments.Guess(["Idea"], program: 0));   // what files write when nobody chose a sound
        // A name that says wins over the sound the file asks for.
        Assert.Equal(Instruments.Piano, Instruments.Guess(["Piano"], program: 33)!.Name);
        Assert.Equal(128, Instruments.GeneralMidi.Count);
        Assert.Equal(Instruments.Vocal, Instruments.FamilyOf(52));
        Assert.Equal(Instruments.Pad, Instruments.FamilyOf(89));
    }

    static StoredPhrase Phrase(string path, string kind, string part, bool drums = false, int? program = null) =>
        new(1, new MidiSource(path, kind, kind == MidiSource.File ? "MIDI" : "FL Studio", 1, 1, 120), part, "", MidiRoles.Chords, "", null,
            4, 12, 48, 72, null, new byte[12], "", new byte[16], "h") { Drums = drums, Program = program };

    [Fact]
    public void Library_files_are_named_by_their_folders_but_projects_are_not()
    {
        // A loop pack keeps its pianos in a folder called Piano.
        Assert.Equal(Instruments.Piano, MidiCatalogue.InstrumentOf(Phrase(Path.Combine("d:", "Packs", "Soul", "Piano", "Loop 3.mid"), MidiSource.File, ""))?.Name);
        // The nearest folder wins.
        Assert.Equal(Instruments.Bass, MidiCatalogue.InstrumentOf(Phrase(Path.Combine("d:", "Drums pack", "Bass", "Loop 3.mid"), MidiSource.File, ""))?.Name);
        // A song called "Piano dreams" says nothing about the synth playing in it.
        Assert.Null(MidiCatalogue.InstrumentOf(Phrase(Path.Combine("k:", "Piano dreams", "Piano dreams.flp"), MidiSource.Project, "Serum")));
        // A project track on a drum machine or sampler of hits is drums.
        Assert.Equal(Instruments.Drums, MidiCatalogue.InstrumentOf(Phrase(@"k:\song.flp", MidiSource.Project, "Sampler", drums: true))?.Name);
        Assert.Equal(Instruments.Bass, MidiCatalogue.InstrumentOf(Phrase(@"d:\x\Loop.mid", MidiSource.File, "", program: 38))?.Name);
    }

    [Fact]
    public void Search_finds_parts_by_their_instrument()
    {
        var catalogue = new MidiCatalogue([Phrase(@"d:\x\Loop.mid", MidiSource.File, "Keyscape")]);
        Assert.Single(catalogue.Search(new MidiQuery("piano"), out _));
        Assert.Single(catalogue.Search(new MidiQuery("keys"), out _));
        Assert.Equal(Instruments.Piano, catalogue.Entries[0].Instrument?.Name);
    }

    /// <summary>A one-track MIDI file by hand, since the app's own writer sets no sounds: a program change, then four notes.</summary>
    static byte[] FileWithProgram(int channel, int program)
    {
        var track = new List<byte> { 0x00, 0xFF, 0x03, 0x04, (byte)'L', (byte)'o', (byte)'o', (byte)'p' };
        track.AddRange([0x00, (byte)(0xC0 | channel), (byte)program]);
        foreach (var key in new byte[] { 36, 36, 43, 41 })
            track.AddRange([0x00, (byte)(0x90 | channel), key, 100, 0x60, (byte)(0x80 | channel), key, 0]);
        track.AddRange([0x00, 0xFF, 0x2F, 0x00]);
        byte[] header = [(byte)'M', (byte)'T', (byte)'h', (byte)'d', 0, 0, 0, 6, 0, 0, 0, 1, 0, 96,
                         (byte)'M', (byte)'T', (byte)'r', (byte)'k', 0, 0, 0, (byte)track.Count];
        return [.. header, .. track];
    }

    [Fact]
    public void A_midi_files_sound_is_read_kept_and_names_the_part()
    {
        var part = Assert.Single(MidiFileReader.Read(FileWithProgram(0, 38), "x"));
        Assert.Equal(38, part.Program);
        var drums = Assert.Single(MidiFileReader.Read(FileWithProgram(9, 0), "x"));
        Assert.True(drums.Drums);

        var dir = Fixtures.TempDir();
        var lib = Path.Combine(dir, "MIDI");
        Directory.CreateDirectory(lib);
        File.WriteAllBytes(Path.Combine(lib, "Idea.mid"), FileWithProgram(0, 38));
        using var db = new LibraryDb(Path.Combine(dir, "library.db"));
        db.SetList(MidiIndexer.FoldersKey, [lib]);
        using var index = new MidiIndex(MidiIndex.PathBeside(db.FilePath));
        new MidiIndexer(index, db).Run();
        var stored = Assert.Single(index.All());
        Assert.Equal(38, stored.Program);
        Assert.Equal(Instruments.Bass, MidiCatalogue.InstrumentOf(stored)?.Name);
        Assert.Equal(MidiIndexer.FileVersion, index.Sources()[stored.Source.Path].Version);
    }
}
