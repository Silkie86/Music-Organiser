using System.IO.Compression;
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
    public void WritesADawProjectBitwigCanOpen()
    {
        var dir = Fixtures.TempDir();
        var (file, leftOut) = Exporter.DawProjectFromFl(Fixtures.FlStudio25Project(dir, withNotes: true), Path.Combine(dir, "out"));
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
            Assert.All(plugins, p => Assert.Equal("instrument", (string?)p.Attribute("deviceRole")));
            Assert.All(tracks.SkipLast(1), t => Assert.Equal(masterId, (string?)t.Element("Channel")!.Attribute("destination")));
            Assert.DoesNotContain(leftOut, n => n.Contains("Serum") || n.Contains("Arcade"));
        }
    }

    [Fact]
    public void PlacesNotesInBeatsAndRoutesThroughInserts()
    {
        var dir = Fixtures.TempDir();
        var (file, leftOut) = Exporter.DawProjectFromFl(Fixtures.FlStudioProject(dir), Path.Combine(dir, "out"));
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
