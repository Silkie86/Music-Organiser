using System.IO.Compression;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Parsers;
using Xunit;

namespace MusicOrganiser.Core.Tests;

public class BitwigPresetTests
{
    /// <summary>A Bitwig-style preset for one VST2 plugin, its settings in the zip as a .fxb.</summary>
    static string SavedInBitwig(string dir, string file, string plugin, string preset)
    {
        byte[] zip;
        using (var zs = new MemoryStream())
        {
            using (var z = new ZipArchive(zs, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var s = z.CreateEntry("plugin-states/0f1e2d.fxb").Open()) s.Write("CcnK bank from Bitwig"u8);
                using (var s = z.CreateEntry("preview.png").Open()) s.Write("png"u8);
            }
            zip = zs.ToArray();
        }
        var bytes = Fixtures.BwWriter.File(meta: m =>
        {
            m.MetaEntry("application_version_name", 0x08); m.StrVal("6.1");
            m.MetaEntry("preset_name", 0x08); m.StrVal(file);
            m.MetaEntry("creator", 0x08); m.StrVal("silkie86");
        }, body: b =>
        {
            b.Begin(0x1a5);
            b.Str(0x12de, preset); b.Str(0x9a, plugin); b.Str(0x9b, "Xfer Records"); b.Str(0x9c, "Synth");
            b.Str(0xbe5, "0f1e2d.fxb");
            b.End();
        }, zip);
        var path = Path.Combine(dir, file + ".bwpreset");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void BuildsABitwigPresetFromOneSavedForTheSamePlugin()
    {
        var dir = Fixtures.TempDir();
        var t = BitwigPreset.Inspect(SavedInBitwig(dir, "Init Lead", "Serum", "Init Lead"));
        Assert.NotNull(t);
        Assert.Equal(("Serum", "VST2"), (t.Plugin, t.Format));

        var fl = new PresetBlob("Big Bass", "Serum_x64", ".fxp", "CcnK program from FL"u8.ToArray());
        Assert.Same(t, BitwigPreset.For(fl, new Dictionary<string, BitwigPreset.Template> { [BitwigPreset.Key(t.Plugin, t.Format)] = t }));

        var doc = BitwigFormat.Decode(BitwigPreset.Build(t, fl, "Big Bass"));
        Assert.Null(doc.BodyError);
        Assert.Equal("Big Bass", doc.MetaString("preset_name"));
        Assert.Equal("silkie86", doc.MetaString("creator"));
        var device = doc.Root!;
        Assert.Equal(("Serum", "Big Bass", "0f1e2d.fxp"), (device.Str(0x9a), device.Str(0x12de), device.Str(0xbe5)));
        using var zip = doc.OpenZip()!;
        Assert.Null(zip.GetEntry("plugin-states/0f1e2d.fxb"));
        using var state = new StreamReader(zip.GetEntry("plugin-states/0f1e2d.fxp")!.Open());
        Assert.Equal("CcnK program from FL", state.ReadToEnd());
        Assert.NotNull(zip.GetEntry("preview.png"));
    }

    [Fact]
    public void NeverRenamesThePluginItself()
    {
        // A preset simply called "Serum" must not turn the device into a different plugin.
        var dir = Fixtures.TempDir();
        var t = BitwigPreset.Inspect(SavedInBitwig(dir, "Serum", "Serum", "Serum"))!;
        var doc = BitwigFormat.Decode(BitwigPreset.Build(t, new PresetBlob("Pad", "Serum", ".fxp", [1, 2, 3]), "Pad"));
        Assert.Equal("Serum", doc.Root!.Str(0x9a));
    }

    [Fact]
    public void FindsTemplatesInFoldersAndMatchesByFormat()
    {
        var dir = Fixtures.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "Serum"));
        SavedInBitwig(Path.Combine(dir, "Serum"), "Init", "Serum", "Init");
        File.WriteAllText(Path.Combine(dir, "broken.bwpreset"), "not a preset");

        var templates = BitwigPreset.FindTemplates([dir]);
        Assert.Single(templates);
        Assert.NotNull(BitwigPreset.For(new PresetBlob("A", "Serum", ".fxp", [1]), templates));
        Assert.Null(BitwigPreset.For(new PresetBlob("A", "Serum", ".vstpreset", [1]), templates));   // a VST3 state needs a VST3 template
        Assert.Null(BitwigPreset.For(new PresetBlob("A", "Diva", ".fxp", [1]), templates));
    }

    [Fact]
    public void ExportWritesABitwigPresetBesideThePluginPreset()
    {
        var dir = Fixtures.TempDir();
        var templates = new Dictionary<string, BitwigPreset.Template>();
        var t = BitwigPreset.Inspect(SavedInBitwig(dir, "Init", "Serum", "Init"))!;
        templates[BitwigPreset.Key(t.Plugin, t.Format)] = t;

        var result = Exporter.Presets(
            [new PresetBlob("Big Bass", "Serum", ".fxp", [1, 2, 3]), new PresetBlob("Wide", "Diva", ".fxp", [4])],
            Path.Combine(dir, "Song.flp"), Path.Combine(dir, "out"), templates);
        Assert.Equal(["Serum - Big Bass.fxp", "Serum - Big Bass.bwpreset", "Diva - Wide.fxp"], result.Files);
    }
}
