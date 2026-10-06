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
        Assert.Equal("VST3", plugins["Serum"].Format);
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
    }

    [Fact]
    public void RejectsNonFlpFiles()
    {
        var p = Path.Combine(Fixtures.TempDir(), "bad.flp");
        File.WriteAllText(p, "not a project");
        Assert.Throws<InvalidDataException>(() => new FlStudioReader().Read(p));
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

public class BitwigTests
{
    readonly string _path = Fixtures.BitwigProject(Fixtures.TempDir());

    [Fact]
    public void FindsDevicesPluginsAndSamplesHeuristically()
    {
        var reader = new BitwigReader();
        var info = reader.Read(_path);
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
    }

    [Fact]
    public void RecognisesInstalledPluginNames()
    {
        var reader = new BitwigReader { KnownPlugins = new HashSet<string>(["Serum"], StringComparer.OrdinalIgnoreCase) };
        Assert.Contains(reader.Read(_path).Plugins, p => p.Name == "Serum");
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
