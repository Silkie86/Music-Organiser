using System.Text;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Parsers;
using Xunit;

namespace MusicOrganiser.Core.Tests;

public class FlStudioTests
{
    readonly string _path = Fixtures.FlStudioProject(Fixtures.TempDir());

    [Fact]
    public void ReadsTempoVersionAndChannels()
    {
        var info = new FlStudioReader().Read(_path);
        Assert.Equal(128.0, info.Tempo);
        Assert.Equal("21.2.3.4004", info.Version);
        Assert.Equal("Night Drive (title)", info.Title);
        Assert.Equal(["Lead", "Kick", "Bass"], info.Tracks);
        Assert.Equal("4", info.Details["Notes"]);
        Assert.Equal(Confidence.Full, info.Confidence);
    }

    [Fact]
    public void FindsPluginsAndTheirRoles()
    {
        var plugins = new FlStudioReader().Read(_path).Plugins.ToDictionary(p => p.Name);
        Assert.Equal(PluginKind.Instrument, plugins["Serum"].Kind);
        Assert.Equal("Xfer Records", plugins["Serum"].Vendor);
        Assert.Equal("VST2", plugins["Serum"].Format);
        Assert.Equal(PluginKind.Instrument, plugins["3x Osc"].Kind);
        Assert.Equal(PluginKind.Effect, plugins["Fruity Limiter"].Kind);
        Assert.Equal(PluginKind.Effect, plugins["Pro-Q 3"].Kind);
        Assert.Equal("VST2", plugins["Pro-Q 3"].Format);
        Assert.Equal(4, plugins.Count);
    }

    [Fact]
    public void FindsSamples()
    {
        Assert.Equal([@"C:\Samples\Drums\Kick 01.wav"], new FlStudioReader().Read(_path).Samples);
    }

    [Fact]
    public void ExtractsPatternsAsMidiClips()
    {
        var clips = new FlStudioReader().ExtractMidi(_path);
        Assert.Equal(["Intro", "Drop"], clips.Select(c => c.Name));
        var intro = clips[0];
        Assert.Equal(96, intro.Ppq);
        Assert.Equal(["Lead", "Kick"], intro.Tracks.Select(t => t.Key));
        Assert.Equal(new NoteEvent(96, 96, 64, 127), intro.Track("Lead")[1]);
        Assert.Equal("Bass", clips[1].Tracks.Single().Key);
    }

    [Fact]
    public void ExtractsEachChannelWithWhatPlaysIt()
    {
        // No playlist: patterns one after another, each rounded up to a bar (4 × 96 ticks).
        var channels = new FlStudioReader().ExtractChannels(_path);
        Assert.Equal(["Lead (Serum)", "Kick (Kick 01.wav)", "Bass (3x Osc)"], channels.Select(c => c.Label));
        Assert.Equal([0L, 96L], channels[0].Notes.Select(n => n.Start));
        Assert.Equal(384, channels[2].Notes.Single().Start);
        Assert.Equal("Drums", channels[1].Kind);
    }

    [Fact]
    public void PlacesPatternsWhereThePlaylistPutsThem()
    {
        // Drop at the start; then the second half of Intro from tick 768.
        var path = Fixtures.FlStudioProject(Fixtures.TempDir(), "Arranged",
            [.. Fixtures.PlaylistItem(0, 2, 384), .. Fixtures.PlaylistItem(768, 1, 192, offset: 96)]);
        var channels = new FlStudioReader().ExtractChannels(path).ToDictionary(c => c.Name);
        Assert.Equal(0, channels["Bass"].Notes.Single().Start);
        Assert.Equal(new NoteEvent(768, 96, 64, 127), channels["Lead"].Notes.Single());
        Assert.False(channels.ContainsKey("Kick"));   // its note sits before the offset
    }

    [Fact]
    public void ExtractsPluginStates()
    {
        var presets = new FlStudioReader().ExtractPresets(_path);
        Assert.Equal(["Lead", "Bass", "Fruity Limiter", "Pro-Q 3"], presets.Select(p => p.Name));
        var serum = presets[0];
        Assert.Equal(".fxp", serum.Extension);
        Assert.Equal("CcnK", Encoding.ASCII.GetString(serum.Data, 0, 4));
        Assert.Equal("FPCh", Encoding.ASCII.GetString(serum.Data, 8, 4));
        Assert.Equal([1, 2, 3, 4], serum.Data[^4..]);
        Assert.Equal(".flstate", presets[1].Extension);
        Assert.Equal([(PluginKind.Instrument, false), (PluginKind.Instrument, true), (PluginKind.Effect, true), (PluginKind.Effect, false)],
                     presets.Select(p => (p.Kind, p.Native)));
        Assert.Equal(["", "", "Master", "Master"], presets.Select(p => p.Where));
    }

    [Fact]
    public void FiltersPresetsByMakerAndKind()
    {
        var presets = new FlStudioReader().ExtractPresets(_path);
        string[] Names(PresetFilter f) => presets.Where(f.Matches).Select(p => p.Name).ToArray();
        Assert.Equal(["Lead", "Pro-Q 3"], Names(new PresetFilter(Native: false)));
        Assert.Equal(["Bass", "Fruity Limiter"], Names(new PresetFilter(ThirdParty: false)));
        Assert.Equal(["Lead"], Names(new PresetFilter(Native: false, Effects: false)));
        Assert.Equal(["Fruity Limiter", "Pro-Q 3"], Names(new PresetFilter(Synths: false)));
    }

    [Fact]
    public void KnowsWhichPluginsPlayNotes()
    {
        var players = MidiChannel.Players(new FlStudioReader().ExtractChannels(_path));
        Assert.Contains("serum", players);
        Assert.Contains("3x Osc", players);
        Assert.DoesNotContain("Pro-Q 3", players);
        // A slicer channel shows its sample but still counts its plugin as playing.
        var chops = new MidiChannel("Chops", "Loop.wav", 96, [new NoteEvent(0, 96, 60, 100)], "Slicex");
        Assert.Contains("Slicex", MidiChannel.Players([chops]));
    }

    [Fact]
    public void ListsEmptyAndDamagedFilesWithWhatsWrong()
    {
        const string damaged = "This file is empty or damaged: it doesn't start the way an FL Studio project does, so there's nothing in it to read";
        var dir = Fixtures.TempDir();
        var files = new Dictionary<string, byte[]>
        {
            ["empty.flp"] = [],
            ["text.flp"] = "not a project"u8.ToArray(),
            // What disk damage left in one: an NTFS directory index record.
            ["index.flp"] = [.. "INDX"u8, 0x28, 0, 0x09, 0, .. new byte[40]],
            // FL's header, then neither an FLdt chunk nor events with FL's version in them.
            ["junk.flp"] = [.. "FLhd"u8, 6, 0, 0, 0, 0, 0, 3, 0, 96, 0, .. new byte[8], 0xff, 0xff, 0xff, 0xff, 0x7f, 1, 2, 3],
        };
        foreach (var (name, bytes) in files)
        {
            var p = Path.Combine(dir, name);
            File.WriteAllBytes(p, bytes);
            var info = new FlStudioReader().Read(p);
            Assert.Equal(Confidence.Catalogue, info.Confidence);
            Assert.Equal([damaged + "."], info.Warnings);
            Assert.False(info.CanExtractMidi);
            Assert.Equal(damaged, Assert.Throws<InvalidDataException>(() => Readers.FlStudio.ReadSong(p)).Message);
        }
    }

    [Fact]
    public void ReadsOldSavesWithAGarbledDataHeader()
    {
        // Some FL Studio 4 to 7 saves have what looks like a memory address and a size where "FLdt" and its length belong.
        var dir = Fixtures.TempDir();
        var good = new Fixtures.FlpBuilder()
            .Ascii(199, "7.0.2").Word(66, 140).Ascii(194, "Side")
            .Word(64, 0).Byte(21, 0).Ascii(192, "Kick").Ascii(196, @"C:\Samples\Kick.wav")
            .Word(65, 1).Data(224, Fixtures.Note(0, 0, 96, 60, 100))
            .Build();
        var garbled = good.ToArray();
        new byte[] { 0x60, 0xfb, 0x12, 0x00, 0xd9, 0x4a, 0x06, 0x01 }.CopyTo(garbled, 14);
        File.WriteAllBytes(Path.Combine(dir, "good.flp"), good);
        File.WriteAllBytes(Path.Combine(dir, "side.flp"), garbled);

        var (a, b) = (new FlStudioReader().Read(Path.Combine(dir, "good.flp")), new FlStudioReader().Read(Path.Combine(dir, "side.flp")));
        Assert.Equal(("7.0.2", 140.0, "Side", Confidence.Full), (b.Version, b.Tempo, b.Title, b.Confidence));
        Assert.Equal(["Kick"], b.Tracks);
        Assert.Equal(a.Tracks, b.Tracks);
        Assert.Equal(a.Samples, b.Samples);
        Assert.Equal(a.Details, b.Details);
        Assert.Empty(a.Warnings);
        Assert.Equal(["Saved by an old FL Studio that garbled the start of its data; everything after it was read as usual."], b.Warnings);
        Assert.Single(Readers.FlStudio.ReadSong(Path.Combine(dir, "side.flp")).Channels.Single().Notes);

        // Fix can relink one too, even when the junk "length" reads as a negative number.
        new byte[] { 0x60, 0xfb, 0x12, 0x00, 0xd9, 0x4a, 0x06, 0x91 }.CopyTo(garbled, 14);
        var (data, changed, _) = FlStudioReader.RelinkSamples(garbled, Path.Combine(dir, "side.flp"),
            new Dictionary<string, string> { [@"C:\Samples\Kick.wav"] = @"D:\Packs\Kick 2.wav" });
        Assert.Equal(1, changed);
        Assert.Equal(garbled.AsSpan(0, 22).ToArray(), data.AsSpan(0, 22).ToArray());
        File.WriteAllBytes(Path.Combine(dir, "relinked.flp"), data);
        Assert.Equal([@"D:\Packs\Kick 2.wav"], new FlStudioReader().Read(Path.Combine(dir, "relinked.flp")).Samples);

        // FL 7 keeps paths one byte a letter: a file named in letters it can't store is left as it was, and said.
        var (same, none, cantWrite) = FlStudioReader.RelinkSamples(garbled, Path.Combine(dir, "side.flp"),
            new Dictionary<string, string> { [@"C:\Samples\Kick.wav"] = @"D:\Łódź\Kick.wav" });
        Assert.Equal(0, none);
        Assert.Equal([@"D:\Łódź\Kick.wav"], cantWrite);
        Assert.Equal(garbled, same);
        // Western European letters it can store.
        Assert.Equal(1, FlStudioReader.RelinkSamples(garbled, Path.Combine(dir, "side.flp"),
            new Dictionary<string, string> { [@"C:\Samples\Kick.wav"] = @"D:\Café\Kick.wav" }).Changed);
    }

    [Fact]
    public void SurvivesTruncatedFiles()
    {
        var bytes = File.ReadAllBytes(_path);
        var p = Path.Combine(Fixtures.TempDir(), "cut.flp");
        File.WriteAllBytes(p, bytes[..(bytes.Length - 30)]);
        var info = new FlStudioReader().Read(p);
        Assert.Equal(128.0, info.Tempo);
    }
}

public class StudioOneTests
{
    readonly string _path = Fixtures.StudioOneSong(Fixtures.TempDir());

    [Fact]
    public void ReadsMetadataPluginsAndSamples()
    {
        var info = new StudioOneReader().Read(_path);
        Assert.Equal(92.0, info.Tempo);
        Assert.Equal("Studio One/6.5.2", info.Version);
        Assert.Equal([@"C:\Users\Sol\Samples\Vox Chop.wav", @"C:\Users\Sol\Samples\Rim.aif"], info.Samples);
        var plugins = info.Plugins.ToDictionary(p => p.Name);
        Assert.Equal(PluginKind.Instrument, plugins["Diva"].Kind);
        Assert.Equal("VST3", plugins["Diva"].Format);
        Assert.Equal("Native", plugins["Mai Tai"].Format);
        Assert.Equal(PluginKind.Effect, plugins["Pro-C 2"].Kind);
        Assert.Equal(["Keys", "Vocals"], info.Tracks);
    }

    [Fact]
    public void ExtractsNotesAndPresets()
    {
        var r = new StudioOneReader();
        var clip = Assert.Single(r.ExtractMidi(_path));
        Assert.Equal("Verse Chords", clip.Name);
        Assert.Equal([new NoteEvent(0, 960, 60, 102), new NoteEvent(960, 480, 64, 127)], clip.Track("Keys"));
        var preset = Assert.Single(r.ExtractPresets(_path));
        Assert.Equal(("Inst 1", "Diva", ".vstpreset"), (preset.Name, preset.Plugin, preset.Extension));
    }
}

public class FlStudio25Tests
{
    readonly string _path = Fixtures.FlStudio25Project(Fixtures.TempDir());

    [Fact]
    public void FindsVstPluginsInNewerWrapperStates()
    {
        var plugins = new FlStudioReader().Read(_path).Plugins.ToDictionary(p => p.Name);
        Assert.Equal(("Xfer Records", "VST2", PluginKind.Instrument), (plugins["Serum"].Vendor, plugins["Serum"].Format, plugins["Serum"].Kind));
        Assert.Equal(("Output", "VST3"), (plugins["Arcade"].Vendor, plugins["Arcade"].Format));
    }

    [Fact]
    public void ResolvesTheUserDataFolderFromTheProjectsLocation()
    {
        var sample = Assert.Single(new FlStudioReader().Read(_path).Samples);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(_path)!, "Snare 01.wav"), sample);
        Assert.True(File.Exists(sample));
    }

    [Fact]
    public void ExportsVst2AsFxpWithThePluginId()
    {
        var serum = new FlStudioReader().ExtractPresets(_path).Single(p => p.Plugin == "Serum");
        Assert.Equal(".fxp", serum.Extension);
        Assert.Equal("XfsX", Encoding.ASCII.GetString(serum.Data, 16, 4));   // fxID, big-endian
        Assert.Equal([1, 2, 3], serum.Data[^3..]);                             // FL's header removed
        Assert.Equal(3, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(serum.Data.AsSpan(56)));
    }

    [Fact]
    public void ExportsParameterPluginsAsParameterPresets()
    {
        var predator = new FlStudioReader().ExtractPresets(_path).Single(p => p.Plugin == "Predator 2");
        var d = predator.Data;
        Assert.Equal(".fxp", predator.Extension);
        Assert.Equal("FxCk", Encoding.ASCII.GetString(d, 8, 4));
        Assert.Equal("Prd2", Encoding.ASCII.GetString(d, 16, 4));
        Assert.Equal(3, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(24)));
        Assert.Equal("arp- sine beat", Encoding.ASCII.GetString(d, 28, 28).TrimEnd('\0'));
        Assert.Equal(d.Length - 8, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(4)));
        Assert.Equal([0.25f, 1f, 0.5f], Enumerable.Range(0, 3)
            .Select(i => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(56 + 4 * i))));
    }

    [Fact]
    public void ExportsVst3AsVstPreset()
    {
        var arcade = new FlStudioReader().ExtractPresets(_path).Single(p => p.Plugin == "Arcade");
        Assert.Equal(".vstpreset", arcade.Extension);
        var d = arcade.Data;
        Assert.Equal("VST3", Encoding.ASCII.GetString(d, 0, 4));
        // The same class id Bitwig writes for Arcade.
        Assert.Equal("56535441524344617263616465000000", Encoding.ASCII.GetString(d, 8, 32));
        Assert.Equal([7, 7, 7], d[48..51]);
        Assert.Equal([8], d[51..52]);
        var list = (int)System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(40));
        Assert.Equal("List", Encoding.ASCII.GetString(d, list, 4));
        Assert.Equal(2, BitConverter.ToInt32(d, list + 4));
        Assert.Equal("Comp", Encoding.ASCII.GetString(d, list + 8, 4));
    }
}

public class BitwigTests
{
    readonly string _path = Fixtures.BitwigProject(Fixtures.TempDir());

    [Fact]
    public void DecodesTheWholeFile()
    {
        var doc = BitwigFormat.Decode(File.ReadAllBytes(_path));
        Assert.Null(doc.BodyError);
        Assert.Equal("5.2.4", doc.MetaString("application_version_name"));
        Assert.Contains(doc.Objects, o => o.Str(0x133b) == "Delay » Feedback");   // UTF-16 string
    }

    [Fact]
    public void ReadsTempoVersionPluginsAndHardware()
    {
        var info = new BitwigReader().Read(_path);
        Assert.Equal(Confidence.Partial, info.Confidence);
        Assert.Empty(info.Warnings);
        Assert.Equal("5.2.4", info.Version);
        Assert.Equal(128.5, info.Tempo);
        var plugins = info.Plugins.ToDictionary(p => p.Name);
        Assert.Equal(("Spectrasonics", "VST3", PluginKind.Instrument), (plugins["Keyscape"].Vendor, plugins["Keyscape"].Format, plugins["Keyscape"].Kind));
        Assert.EndsWith("Keyscape.vst3", plugins["Keyscape"].Path);
        Assert.Equal(PluginKind.Effect, plugins["FabFilter Pro-L 2"].Kind);
        Assert.Equal(("Bitwig", "Native"), (plugins["Delay+"].Vendor, plugins["Delay+"].Format));
        Assert.Equal("Elektron Analog Rytm MKII", info.Details["Hardware I/O"]);
        Assert.True(info.CanExtractMidi);
        Assert.True(info.CanExtractPresets);
    }

    [Fact]
    public void UsesBitwigsDisplayNamesForTracks()
    {
        Assert.Equal(["Keys Display", "FX 1"], new BitwigReader().Read(_path).Tracks);
    }

    [Fact]
    public void FindsSamples()
    {
        var samples = new BitwigReader().Read(_path).Samples;
        Assert.Contains(@"D:\Splice\Snare 808.wav", samples);
        Assert.Contains(samples, s => s.EndsWith("Kick 01.wav"));
        Assert.Contains(samples, s => s.EndsWith("Pad.multisample"));
        Assert.Contains(samples, s => s.EndsWith("Recorded Bass.wav"));
    }

    [Fact]
    public void ExtractsNoteClips()
    {
        var clip = Assert.Single(new BitwigReader().ExtractMidi(_path));
        Assert.Equal("Keys Display 1", clip.Name);
        Assert.Equal(960, clip.Ppq);
        Assert.Equal(
            [new NoteEvent(0, 960, 60, 64), new NoteEvent(1440, 480, 60, 127), new NoteEvent(1920, 960, 64, 102)],
            clip.Track("Keys Display"));
    }

    [Fact]
    public void ExtractsPluginStatesNamedByTrack()
    {
        var presets = new BitwigReader().ExtractPresets(_path).ToDictionary(p => p.Plugin);
        Assert.Equal(("Keys Display - Keyscape", ".vstpreset"), (presets["Keyscape"].Name, presets["Keyscape"].Extension));
        Assert.Equal("VST3 keys state", Encoding.ASCII.GetString(presets["Keyscape"].Data));
        Assert.Equal("Master - FabFilter Pro-L 2", presets["FabFilter Pro-L 2"].Name);
    }

    [Fact]
    public void FallsBackToTextScanWhenTheFileCantBeDecoded()
    {
        var path = Fixtures.BitwigUndecodable(Fixtures.TempDir());
        var info = new BitwigReader().Read(path);
        Assert.Equal(Confidence.Heuristic, info.Confidence);
        Assert.Equal("5.2.4", info.Version);
        var names = info.Plugins.Select(p => p.Name).ToHashSet();
        Assert.Contains("Polymer", names);
        Assert.Contains("Delay+", names);
        Assert.Contains("Valhalla Room", names);
        Assert.Contains("Analog Rytm MKII", names);
        Assert.DoesNotContain("Serum", names);   // not installed, so plain text isn't trusted as a plugin
        Assert.Contains(@"D:\Splice\Snare 808.wav", info.Samples);   // from the compressed section
        Assert.Contains(info.Samples, s => s.EndsWith("Recorded Bass.wav"));
        Assert.False(info.CanExtractMidi);
        Assert.Empty(new BitwigReader().ExtractMidi(path));

        var known = new BitwigReader { KnownPlugins = new HashSet<string>(["Serum"], StringComparer.OrdinalIgnoreCase) };
        Assert.Contains(known.Read(path).Plugins, p => p.Name == "Serum");
    }

    [Fact]
    public void SurvivesTruncatedFiles()
    {
        var bytes = File.ReadAllBytes(_path);
        var p = Path.Combine(Fixtures.TempDir(), "cut.bwproject");
        File.WriteAllBytes(p, bytes[..(bytes.Length / 2)]);
        var info = new BitwigReader().Read(p);
        Assert.Equal(Confidence.Heuristic, info.Confidence);
        Assert.Equal("5.2.4", info.Version);
    }
}

public class MidiWriterTests
{
    [Fact]
    public void WritesAValidType1File()
    {
        var clip = new MidiClip { Name = "Test", Ppq = 96 };
        clip.Track("Lead").Add(new NoteEvent(0, 96, 60, 100));
        clip.Track("Lead").Add(new NoteEvent(96, 96, 60, 100));
        var bytes = MidiWriter.Write(clip, 120);

        Assert.Equal("MThd", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(1, bytes[9]);                 // format 1
        Assert.Equal(2, bytes[11]);                // tempo track + 1 instrument track
        Assert.Equal(96, bytes[13]);               // PPQ
        var tracks = Encoding.Latin1.GetString(bytes).Split("MTrk").Length - 1;
        Assert.Equal(2, tracks);
        // Tempo 120 bpm = 500000 µs per quarter.
        Assert.Contains("\xFF\x51\x03\x07\xA1\x20", Encoding.Latin1.GetString(bytes));
        // At tick 96 the first note's off must come before the second note's on.
        var s = Encoding.Latin1.GetString(bytes);
        Assert.True(s.IndexOf("\x80\x3C\x00", StringComparison.Ordinal) < s.LastIndexOf("\x90\x3C\x64", StringComparison.Ordinal));
    }
}
