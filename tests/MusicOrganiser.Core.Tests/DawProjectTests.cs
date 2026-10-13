using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Linq;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Parsers;
using Xunit;

namespace MusicOrganiser.Core.Tests;

public class DawProjectTests
{
    /// <summary>Opens the export and checks project.xml and metadata.xml against the official DAWproject schemas (Data/dawproject, MIT).</summary>
    static (XDocument Project, ZipArchive Zip) Open(string file)
    {
        var zip = ZipFile.OpenRead(file);
        foreach (var (entry, xsd) in new[] { ("project.xml", "Project.xsd"), ("metadata.xml", "MetaData.xsd") })
        {
            var schemas = new XmlSchemaSet();
            schemas.Add(null, Path.Combine(AppContext.BaseDirectory, "Data", "dawproject", xsd));
            var errors = new List<string>();
            using var s = zip.GetEntry(entry)!.Open();
            XDocument.Load(s).Validate(schemas, (_, e) => errors.Add($"{entry}: {e.Message}"));
            Assert.Empty(errors);
        }
        using var p = zip.GetEntry("project.xml")!.Open();
        return (XDocument.Load(p), zip);
    }

    [Fact]
    public void ReadsAnFlSongForMovingElsewhere()
    {
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudioProject(Fixtures.TempDir()));
        Assert.Equal(128, song.Tempo);
        var lead = song.Channels.Single(c => c.Name == "Lead");
        Assert.Equal(("Serum", "VST2"), (lead.Instrument!.Name, lead.Instrument.Format));
        Assert.Equal(".fxp", lead.Instrument.State!.Extension);
        Assert.Equal(2, lead.Notes.Count);
        Assert.Contains(song.Channels, c => c.Name == "Kick" && c.Sample.EndsWith("Kick 01.wav") && c.Instrument is null);
        Assert.True(song.Channels.Single(c => c.Name == "Bass").Instrument!.Native);
        Assert.Equal(["Fruity Limiter", "Pro-Q 3"], song.Inserts.Single(i => i.Index == 0).Effects.Select(e => e.Name));
    }

    [Fact]
    public void ReadsRootKeyPitchLevelsAndAutomationFromFl25()
    {
        var path = Fixtures.FlStudio25Automation(Fixtures.TempDir());
        var song = Readers.FlStudio.ReadSong(path);

        Assert.Equal(["Snare", "Serum"], song.Channels.Select(c => c.Name));   // automation clips aren't channels here
        var snare = song.Channels[0];
        Assert.Equal((57, -200, 1.0, 0.25), (snare.RootKey, snare.PitchCents, snare.Volume, snare.Pan));
        Assert.Equal(4 * 96, snare.Notes[0].Start);   // the pattern sits in bar 2 of the FL 25 playlist

        var volume = song.Automation.Single(a => a.Control == FlControl.Volume);
        Assert.Equal((0, "Snare - Channel volume"), (volume.Channel, volume.Name));
        Assert.Equal([new FlPoint(4, 0), new FlPoint(8, 1)], volume.Points);

        // Points are stored as steps from the one before: 0, +2, +2.
        var cutoff = song.Automation.Single(a => a.Control == FlControl.PluginParameter);
        Assert.Equal((2, 205), (cutoff.Channel, cutoff.Parameter));
        Assert.Equal([0, 2, 4], cutoff.Points.Select(p => p.Beat));
        Assert.Equal([0.2, 1, 0.5], cutoff.Points.Select(p => p.Value));

        var midi = Readers.FlStudio.ExtractChannels(path).Single(c => c.Name == "Snare");
        Assert.Equal((57, -200), (midi.RootKey, midi.PitchCents));
    }

    [Fact]
    public void ReadsTheIdsAVst3PluginGivesItsSettingsInFlsOrder()
    {
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25Project(Fixtures.TempDir()));
        Assert.Equal([10u, 20u, 2000003u], song.Channels.Single(c => c.Name == "Keys").Instrument!.ParameterIds);
        Assert.Null(song.Channels.Single(c => c.Name == "Lead").Instrument!.ParameterIds);   // VST2 settings are numbered alike everywhere
    }

    [Fact]
    public void AnAutomationClipPlacedTwiceHoldsItsValueThroughTheGap()
    {
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25Automation(Fixtures.TempDir(), volumePlacedTwice: true));
        var volume = song.Automation.Single(a => a.Control == FlControl.Volume);
        // Beats 4-8, then 12-16: it stays at 1 until the second clip starts back at 0.
        Assert.Equal([new FlPoint(4, 0), new FlPoint(8, 1), new FlPoint(12, 1), new FlPoint(12, 0), new FlPoint(16, 1)], volume.Points);
    }

    [Fact]
    public void CarriesSamplerSetupLevelsAndAutomationIntoTheDawProject()
    {
        var dir = Fixtures.TempDir();
        var (file, leftOut, setup) = Exporter.DawProjectFromFl(Fixtures.FlStudio25Automation(dir), Path.Combine(dir, "out"));
        Assert.Empty(leftOut);
        Assert.Equal([@"Snare: in its Sampler, load C:\Samples\Snare 01.wav, then set Root key to A2 (A4 in FL Studio), tune it -2 semitones.",
                      "Serum: add Bitwig's Transpose note effect before Serum and set it to -4 semitones (its FL root key was E5)."],
                     setup);
        Assert.Contains("A2", File.ReadAllText(Path.Combine(Path.GetDirectoryName(file)!, "Automated - Bitwig setup.txt")));

        var (project, zip) = Open(file);
        using (zip)
        {
            var root = project.Root!;
            XElement TrackNamed(string name) => root.Element("Structure")!.Elements("Track").Single(t => (string?)t.Attribute("name") == name);
            XElement LanesOf(XElement track) => root.Descendants("Lanes").Single(l => (string?)l.Attribute("track") == (string?)track.Attribute("id"));
            string Id(XElement? e) => (string)e!.Attribute("id")!;

            var snare = TrackNamed("Snare");
            var sampler = snare.Descendants("BuiltinDevice").Single();
            Assert.Equal(("468bc14b-b2e7-45a1-9666-e83117fe404e", "Sampler"), ((string?)sampler.Attribute("deviceID"), (string?)sampler.Attribute("deviceName")));
            Assert.Contains("Root key to A2", (string?)snare.Attribute("comment"));
            var channel = snare.Element("Channel")!;
            Assert.Equal("0.25", (string?)channel.Element("Pan")!.Attribute("value"));
            Assert.Equal("1.6384", (string?)channel.Element("Volume")!.Attribute("value"));   // FL's 100% is above its 78% default

            var volume = LanesOf(snare).Element("Points")!;
            Assert.Equal(Id(channel.Element("Volume")), (string?)volume.Element("Target")!.Attribute("parameter"));
            Assert.Equal([("4", "0"), ("8", "1.6384")],
                         volume.Elements("RealPoint").Select(p => ((string)p.Attribute("time")!, (string)p.Attribute("value")!)));

            var serum = TrackNamed("Serum");
            var param = serum.Descendants("RealParameter").Single();
            Assert.Equal("205", (string?)param.Attribute("parameterID"));
            var cutoff = LanesOf(serum).Element("Points")!;
            Assert.Equal(Id(param), (string?)cutoff.Element("Target")!.Attribute("parameter"));
            Assert.Equal(["0", "2", "4"], cutoff.Elements("RealPoint").Select(p => (string)p.Attribute("time")!));
        }
    }

    [Fact]
    public void WritesADawProjectBitwigCanOpen()
    {
        var dir = Fixtures.TempDir();
        var (file, leftOut, _) = Exporter.DawProjectFromFl(Fixtures.FlStudio25Project(dir, withNotes: true), Path.Combine(dir, "out"));
        Assert.EndsWith("Shuffle.dawproject", file);

        var (project, zip) = Open(file);
        using (zip)
        {
            var root = project.Root!;
            Assert.Equal("1.0", (string?)root.Attribute("version"));
            Assert.Equal("126", (string?)root.Element("Transport")!.Element("Tempo")!.Attribute("value"));

            var tracks = root.Element("Structure")!.Elements("Track").ToList();
            Assert.Equal("Master", (string?)tracks[^1].Attribute("name"));
            var masterId = (string?)tracks[^1].Element("Channel")!.Attribute("id");

            // Each plugin is identified the way hosts look it up, with its settings in the zip.
            var plugins = root.Descendants().Where(e => e.Name.LocalName is "Vst2Plugin" or "Vst3Plugin").ToList();
            var serum = plugins.Single(p => (string?)p.Attribute("deviceName") == "Serum");
            Assert.Equal("Vst2Plugin", serum.Name.LocalName);
            Assert.Equal(0x58667358.ToString(), (string?)serum.Attribute("deviceID"));   // 'XfsX'
            var arcade = plugins.Single(p => (string?)p.Attribute("deviceName") == "Arcade");
            Assert.Equal("Vst3Plugin", arcade.Name.LocalName);
            Assert.Equal(32, ((string?)arcade.Attribute("deviceID"))!.Length);
            foreach (var p in plugins)
                Assert.NotNull(zip.GetEntry((string)p.Element("State")!.Attribute("path")!));

            // VST2 settings go in as one-program banks, the way Bitwig writes them.
            byte[] StateOf(string name)
            {
                var path = (string)plugins.Single(p => (string?)p.Attribute("deviceName") == name).Element("State")!.Attribute("path")!;
                Assert.EndsWith(name == "Arcade" ? ".vstpreset" : ".fxb", path);
                using var ms = new MemoryStream();
                using (var s = zip.GetEntry(path)!.Open()) s.CopyTo(ms);
                return ms.ToArray();
            }
            var bank = StateOf("Serum");
            Assert.Equal("CcnKFBCh", Encoding.ASCII.GetString(bank, 0, 4) + Encoding.ASCII.GetString(bank, 8, 4));
            Assert.Equal(bank.Length - 8, BinaryPrimitives.ReadInt32BigEndian(bank.AsSpan(4)));
            Assert.Equal("XfsX", Encoding.ASCII.GetString(bank, 16, 4));
            Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(bank.AsSpan(156)));
            Assert.Equal([1, 2, 3], bank[160..]);
            var paramBank = Fxp.ToBank(Fxp.WrapParams([0.25f, 1f], "arp", 0x32647250));
            Assert.Equal("FxBk", Encoding.ASCII.GetString(paramBank, 8, 4));
            Assert.Equal("CcnKFxCk", Encoding.ASCII.GetString(paramBank, 156, 4) + Encoding.ASCII.GetString(paramBank, 164, 4));
            Assert.Equal(paramBank.Length - 8, BinaryPrimitives.ReadInt32BigEndian(paramBank.AsSpan(4)));
            Assert.All(plugins, p => Assert.Equal("instrument", (string?)p.Attribute("deviceRole")));
            Assert.All(tracks.SkipLast(1), t => Assert.Equal(masterId, (string?)t.Element("Channel")!.Attribute("destination")));
            Assert.DoesNotContain(leftOut, n => n.Contains("Serum") || n.Contains("Arcade"));
        }
    }

    [Fact]
    public void PlacesNotesInBeatsAndRoutesThroughInserts()
    {
        var dir = Fixtures.TempDir();
        var (file, leftOut, _) = Exporter.DawProjectFromFl(Fixtures.FlStudioProject(dir), Path.Combine(dir, "out"));
        var (project, zip) = Open(file);
        using (zip)
        {
            var root = project.Root!;
            var lead = root.Element("Structure")!.Elements("Track").Single(t => (string?)t.Attribute("name") == "Lead");
            var lane = root.Descendants("Lanes").Single(l => (string?)l.Attribute("track") == (string?)lead.Attribute("id"));
            var notes = lane.Descendants("Note").ToList();
            Assert.Equal(["0", "1"], notes.Select(n => (string)n.Attribute("time")!));   // 96 ticks = 1 beat at 96 PPQ
            Assert.Equal(["60", "64"], notes.Select(n => (string)n.Attribute("key")!));

            // FL Studio's own plugins can't come across; they're named instead.
            Assert.Contains(leftOut, n => n.StartsWith("Bass") && n.Contains("3x Osc"));
            Assert.Contains(leftOut, n => n.StartsWith("Master") && n.Contains("Fruity Limiter"));
            var master = root.Element("Structure")!.Elements("Track").Last();
            Assert.Single(master.Descendants("Vst2Plugin"));   // Pro-Q 3 on the master insert
        }
    }
}
