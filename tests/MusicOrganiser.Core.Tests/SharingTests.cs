using System.IO.Compression;
using System.Text;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sharing;
using Xunit;

namespace MusicOrganiser.Core.Tests;

/// <summary>
/// A project saved on a Mac in FL Studio 25, laid out like real ones: Diva and Arcade as
/// Audio Units (kind 10, no path, AU codes in the class id), a Duck AU effect (kind 9),
/// Serum as a Mac VST2, Pro-Q 4 as a Mac VST3, a native Fruity Limiter and two samples.
/// </summary>
static class MacProject
{
    static byte[] AuId(string manufacturer, string type, string subtype)
    {
        static byte[] Le(string code) => Encoding.ASCII.GetBytes(new string(code.Reverse().ToArray()));
        return [.. Le(manufacturer), .. Le(type), .. Le(subtype), 0, 0, 0, 0];
    }

    /// <summary>FL's AU state: u32 0, u64 length, then the AU's binary property list.</summary>
    public static byte[] AuState(string marker = "x")
    {
        var plist = Encoding.ASCII.GetBytes("bplist00" + marker);
        return [0, 0, 0, 0, .. BitConverter.GetBytes((ulong)plist.Length), .. plist];
    }

    public static string Write(string dir, string name = "Odd Todd")
    {
        var b = new Fixtures.FlpBuilder()
            .Ascii(199, "25.1.5.4642")
            .Dword(156, 140_000)
            .Word(64, 0).Byte(21, 2)
            .Text(201, "Fruity Wrapper").Text(203, "Pad")
            .Data(213, Fixtures.WrapperBlob("Diva", "", "", AuState("diva"), version: 12, kind: 10,
                                            classId: AuId("UHfX", "aumu", "DiVa")))
            .Word(64, 1).Byte(21, 2)
            .Text(201, "Fruity Wrapper").Text(203, "Keys")
            .Data(213, Fixtures.WrapperBlob("Arcade", "", "", AuState(), version: 12, kind: 10,
                                            classId: AuId("Outp", "aumu", "ARCD")))
            .Word(64, 2).Byte(21, 2)
            .Text(201, "Fruity Wrapper").Text(203, "Lead")
            .Data(213, Fixtures.WrapperBlob("Serum", "/Library/Audio/Plug-Ins/VST/Serum.vst", "Xfer Records",
                                            Fixtures.FlVst2State([1]), version: 12, kind: 4, fourCc: "XfsX"))
            .Word(64, 3).Byte(21, 0)
            .Text(203, "Kick").Text(196, "/Users/jay/Splice/Kick 01.wav")
            .Word(64, 4).Byte(21, 0)
            .Text(203, "Hat").Text(196, "/Users/jay/Splice/Hat 03.wav")
            .Data(236, [0, 0, 0, 0])
            .Text(201, "Fruity Limiter").Data(213, [5]).Word(98, 0)
            .Text(201, "Fruity Wrapper")
            .Data(213, Fixtures.WrapperBlob("Duck", "", "", AuState(), version: 12, kind: 9,
                                            classId: AuId("DvMc", "aumf", "DUCK")))
            .Word(98, 1)
            .Text(201, "Fruity Wrapper")
            .Data(213, Fixtures.WrapperBlob("FabFilter Pro-Q 4", "/Library/Audio/Plug-Ins/VST3/FabFilter Pro-Q 4.vst3",
                                            "FabFilter", Fixtures.FlVst3State([1], [2]), version: 12, kind: 7,
                                            classId: new byte[16]))
            .Word(98, 2);
        var path = Path.Combine(dir, name + ".flp");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    /// <summary>A Windows PC with Diva and Arcade as VST3, Serum as VST2 and VST3, no Duck, no Pro-Q 4.</summary>
    public static PluginInventory WindowsPc() => PluginInventory.Parse("This PC", """
        C:\Program Files\Common Files\VST3\u-he\Diva(x64).vst3
        C:\Program Files\Common Files\VST3\Output\Arcade.vst3
        C:\Program Files\VstPlugins\Xfer\Serum_x64.dll
        C:\Program Files\Common Files\VST3\Xfer\Serum.vst3
        """);

    /// <summary>The friend's Mac, as the Terminal command lists it.</summary>
    public static PluginInventory FriendsMac() => PluginInventory.Parse("Jay", """
        /Library/Audio/Plug-Ins/Components/Diva.component
        /Library/Audio/Plug-Ins/Components/Arcade.component
        /Library/Audio/Plug-Ins/Components/Duck.component
        /Library/Audio/Plug-Ins/VST/Serum.vst
        /Library/Audio/Plug-Ins/VST3/FabFilter/FabFilter Pro-Q 4.vst3
        """);
}

public class FlStudioMacProjectTests
{
    readonly string _path = MacProject.Write(Fixtures.TempDir());

    [Fact]
    public void RecognisesAudioUnitsByTheirKind()
    {
        var plugins = new FlStudioReader().Read(_path).Plugins.ToDictionary(p => p.Name);
        Assert.Equal(("AU", "u-he", PluginKind.Instrument), (plugins["Diva"].Format, plugins["Diva"].Vendor, plugins["Diva"].Kind));
        Assert.Equal(("AU", "Output"), (plugins["Arcade"].Format, plugins["Arcade"].Vendor));
        Assert.Equal(("AU", "Devious Machines", PluginKind.Effect), (plugins["Duck"].Format, plugins["Duck"].Vendor, plugins["Duck"].Kind));
        Assert.Equal("VST2", plugins["Serum"].Format);
        Assert.Equal(("VST3", PluginKind.Effect), (plugins["FabFilter Pro-Q 4"].Format, plugins["FabFilter Pro-Q 4"].Kind));
    }

    [Fact]
    public void ExportsAudioUnitStatesAsAupresets()
    {
        var presets = new FlStudioReader().ExtractPresets(_path).ToDictionary(p => p.Plugin);
        Assert.Equal(".aupreset", presets["Diva"].Extension);
        Assert.Equal("bplist00diva", Encoding.ASCII.GetString(presets["Diva"].Data));
        Assert.Equal(".vstpreset", presets["FabFilter Pro-Q 4"].Extension);
    }

    [Fact]
    public void KeepsTheSamePluginInTwoFormats()
    {
        var info = new ProjectInfo { Daw = "FL Studio" };
        info.Plugins.Add(new PluginRef("Diva", "u-he", PluginKind.Instrument, "AU"));
        info.Plugins.Add(new PluginRef("Diva", "u-he", PluginKind.Instrument, "VST3"));
        info.Plugins.Add(new PluginRef("diva", "u-he", PluginKind.Instrument, "VST3"));
        Assert.Equal(["AU", "VST3"], info.Dedupe().Plugins.Select(p => p.Format));
    }

    [Fact]
    public void DropsACopyWithoutAFormatWhenTheSamePluginHasOne()
    {
        var info = new ProjectInfo { Daw = "FL Studio" };
        info.Plugins.Add(new PluginRef("Serum", "Xfer Records", PluginKind.Instrument, ""));
        info.Plugins.Add(new PluginRef("Serum", "Xfer Records", PluginKind.Instrument, "VST3"));
        info.Plugins.Add(new PluginRef("Vital", "", PluginKind.Instrument, ""));
        Assert.Equal([("Serum", "VST3"), ("Vital", "")], info.Dedupe().Plugins.Select(p => (p.Name, p.Format)));
    }
}

public class PluginInventoryTests
{
    [Fact]
    public void ReadsTheMacTerminalListing()
    {
        var inv = PluginInventory.Parse("Jay", """
            /Library/Audio/Plug-Ins/Components/Diva.component
            /Library/Audio/Plug-Ins/VST3/Diva.vst3
            /Library/Audio/Plug-Ins/VST3/Diva.vst3/Contents/MacOS/Diva
            /Users/jay/Library/Audio/Plug-Ins/CLAP/Surge XT.clap
            /Library/Audio/Plug-Ins/VST/Serum.vst
            """);
        Assert.Equal([("Diva", "AU"), ("Diva", "VST3"), ("Serum", "VST2"), ("Surge XT", "CLAP")],
                     inv.Plugins.Select(p => (p.Name, p.Format)).OrderBy(x => x.Name).ThenBy(x => x.Format));
    }

    [Fact]
    public void ReadsFolderListingsAndHandWrittenLists()
    {
        var inv = PluginInventory.Parse("Jay", """
            /Library/Audio/Plug-Ins/Components:
            Kontakt 7
            # a comment
            Serum (AU)
            Pro-Q 4: VST3, AU
            Vital	CLAP
            Massive X
            """);
        var got = inv.Plugins.Select(p => (p.Name, p.Format)).ToHashSet();
        Assert.Contains(("Kontakt 7", "AU"), got);
        Assert.Contains(("Serum", "AU"), got);
        Assert.Contains(("Pro-Q 4", "VST3"), got);
        Assert.Contains(("Pro-Q 4", "AU"), got);
        Assert.Contains(("Vital", "CLAP"), got);
        Assert.Contains(("Massive X", "AU"), got);   // still under the Components heading
    }

    [Fact]
    public void ReadsLsAndDirOutput()
    {
        var inv = PluginInventory.Parse("Jay", """
            /Library/Audio/Plug-Ins/Components:
            total 0
            drwxr-xr-x@ 3 root  wheel  96 12 Jan  2024 Diva.component
            drwxr-xr-x  3 jay   staff  96 Mar  4 10:15 Kontakt 7.component/
            -rw-r--r--  1 jay   staff  12 Mar  4 10:15 .DS_Store

            /Library/Audio/Plug-Ins/VST3:
            Arcade.vst3/	FabFilter Pro-Q 4.vst3/
            Serum.vst3      Vital.vst3

            /Library/Audio/Plug-Ins/VST3/Serum.vst3/Contents:
            Info.plist	MacOS

             Volume in drive C has no label.
             Volume Serial Number is 1234-ABCD

             Directory of C:\Program Files\Common Files\VST3

            07/10/2026  14:02    <DIR>          .
            07/10/2026  14:02    <DIR>          ..
            07/10/2026  14:02    <DIR>          u-he
            07/10/2026  14:02    <DIR>          Surge XT.vst3
            07/10/2026  14:02             2,048 readme.txt
                           1 File(s)          2,048 bytes
                           4 Dir(s)  100,000,000 bytes free
            """);
        Assert.Equal(
            [("Arcade", "VST3"), ("Diva", "AU"), ("FabFilter Pro-Q 4", "VST3"), ("Kontakt 7", "AU"),
             ("Serum", "VST3"), ("Surge XT", "VST3"), ("Vital", "VST3")],
            inv.Plugins.Select(p => (p.Name, p.Format)).OrderBy(x => x.Name));
    }

    [Fact]
    public void WritesAListItReadsBack()
    {
        var pc = MacProject.WindowsPc();
        var again = PluginInventory.Parse("This PC", pc.ToText());
        Assert.Equal(pc.Plugins.Select(p => (p.Name, p.Format)), again.Plugins.Select(p => (p.Name, p.Format)));
    }

    [Theory]
    [InlineData("Serum", "Xfer Records", "VST2", Availability.Same)]       // Serum_x64.dll
    [InlineData("Diva", "u-he", "VST3", Availability.Same)]                // Diva(x64).vst3
    [InlineData("u-he Diva", "u-he", "VST3", Availability.Same)]
    [InlineData("Diva", "u-he", "AU", Availability.OtherFormat)]
    [InlineData("Duck", "", "AU", Availability.None)]
    [InlineData("Verb", "", "VST3", Availability.None)]
    public void MatchesNamesAcrossComputers(string name, string vendor, string format, Availability expected)
    {
        var pc = PluginInventory.Parse("This PC", """
            C:\Program Files\VstPlugins\Xfer\Serum_x64.dll
            C:\Program Files\Common Files\VST3\u-he\Diva(x64).vst3
            C:\Program Files\Common Files\VST3\ValhallaVintageVerb.vst3
            """);
        Assert.Equal(expected, pc.Check(name, vendor, format, out _));
    }

    [Fact]
    public void MatchesAVendorPrefixedFileName()
    {
        var pc = PluginInventory.Parse("This PC", @"C:\Program Files\Common Files\VST3\FabFilter Pro-Q 3.vst3");
        Assert.Equal(Availability.Same, pc.Check("Pro-Q 3", "FabFilter", "VST3", out _));
    }
}

public class ShareCheckTests
{
    readonly string _dir = Fixtures.TempDir();

    [Fact]
    public void FlagsAudioUnitsAndMissingPluginsOnThisPc()
    {
        var r = ShareCheck.Run(MacProject.Write(_dir), MacProject.WindowsPc());
        var p = r.Plugins.ToDictionary(c => c.Name);

        Assert.Equal(ShareStatus.WontLoad, p["Diva"].Here);
        Assert.Equal(["VST3"], p["Diva"].HereHas);
        Assert.Equal(ShareStatus.WontLoad, p["Duck"].Here);
        Assert.Empty(p["Duck"].HereHas);
        Assert.Equal(ShareStatus.Ok, p["Serum"].Here);
        Assert.Equal(ShareStatus.Missing, p["FabFilter Pro-Q 4"].Here);
        Assert.Equal(["Fruity Limiter"], r.NativePlugins);
        Assert.Null(p["Diva"].Friend);

        Assert.Equal("Mac", r.SavedOn);
        Assert.Equal(Readiness.Blocked, r.Readiness);
        Assert.StartsWith("4 plugins won't load on this PC and 2 samples are missing", r.Headline);
        Assert.Contains(r.Actions, a => a.Contains("Audio Units") && a.Contains("You already have Arcade and Diva"));
        Assert.Contains(r.Actions, a => a.StartsWith("Not installed on this PC: FabFilter Pro-Q 4"));
        Assert.Contains(r.Actions, a => a.Contains("plugin list to check their side"));
    }

    [Fact]
    public void ChecksTheFriendsSideFromTheirList()
    {
        // Sending Jay a Windows project: Pro-Q 3 isn't on Jay's list, Serum is (same VST2), Diva only as AU and VST3.
        var info = new Fixtures.FlpBuilder()
            .Ascii(199, "25.1.0.4868")
            .Word(64, 0).Byte(21, 2)
            .Text(201, "Fruity Wrapper")
            .Data(213, Fixtures.WrapperBlob("Serum", @"C:\Program Files\VstPlugins\Xfer\Serum_x64.dll", "Xfer Records", [1], 12, 4))
            .Word(64, 1).Byte(21, 2)
            .Text(201, "Fruity Wrapper")
            .Data(213, Fixtures.WrapperBlob("Diva(x64)", @"C:\Program Files\VstPlugins\Diva(x64).dll", "u-he", [1], 12, 4))
            .Data(236, [0, 0, 0, 0])
            .Text(201, "Fruity Wrapper")
            .Data(213, Fixtures.WrapperBlob("FabFilter Pro-Q 3", @"C:\Program Files\Common Files\VST3\FabFilter Pro-Q 3.vst3", "FabFilter", [1], 12, 7))
            .Build();
        var path = Path.Combine(_dir, "Mine.flp");
        File.WriteAllBytes(path, info);
        var pc = PluginInventory.Parse("This PC", """
            C:\Program Files\VstPlugins\Xfer\Serum_x64.dll
            C:\Program Files\VstPlugins\Diva(x64).dll
            C:\Program Files\Common Files\VST3\FabFilter Pro-Q 3.vst3
            """);
        var friend = PluginInventory.Parse("Jay", """
            /Library/Audio/Plug-Ins/VST/Serum.vst
            /Library/Audio/Plug-Ins/Components/Diva.component
            /Library/Audio/Plug-Ins/VST3/Diva.vst3
            """);

        var r = ShareCheck.Run(path, pc, friend);
        var p = r.Plugins.ToDictionary(c => c.Name);
        Assert.All(r.Plugins, c => Assert.Equal(ShareStatus.Ok, c.Here));
        Assert.Equal(ShareStatus.Ok, p["Serum"].Friend);
        Assert.Equal(ShareStatus.WrongFormat, p["Diva(x64)"].Friend);
        Assert.Equal(["AU", "VST3"], p["Diva(x64)"].FriendHas);
        Assert.Equal(ShareStatus.Missing, p["FabFilter Pro-Q 3"].Friend);
        Assert.Equal("Windows", r.SavedOn);
        Assert.Equal("2 plugins won't load for Jay.", r.Headline);
        Assert.Contains(r.Actions, a => a.StartsWith("Jay can't load FabFilter Pro-Q 3 and Diva(x64). Before sending, render those channels"));

        var message = ShareCheck.MessageForFriend(r);
        Assert.StartsWith("Hey Jay, heads up about Mine", message);
        Assert.Contains("- FabFilter Pro-Q 3 (VST3): I'll render it to audio", message);
    }

    [Fact]
    public void IsReadyWhenEverythingLoadsOnBothSides()
    {
        var path = Path.Combine(_dir, "Simple.flp");
        File.WriteAllBytes(path, new Fixtures.FlpBuilder()
            .Ascii(199, "25.1.0.4868").Word(64, 0).Byte(21, 2)
            .Text(201, "Fruity Wrapper")
            .Data(213, Fixtures.WrapperBlob("Serum", @"C:\Program Files\Common Files\VST3\Serum.vst3", "Xfer Records", [1], 12, 8))
            .Build());
        var r = ShareCheck.Run(path, MacProject.WindowsPc(), MacProject.FriendsMac().Plugins.Append(
            new InstalledPlugin("Serum", "VST3")) is var list ? new PluginInventory("Jay", list) : null);
        Assert.Equal(Readiness.Ready, r.Readiness);
        Assert.Equal("Ready to share: everything loads on both computers.", r.Headline);
        Assert.Equal("", ShareCheck.MessageForFriend(r));
    }

    [Fact]
    public void AsksTheFriendToSwapAudioUnits()
    {
        var r = ShareCheck.Run(MacProject.Write(_dir), MacProject.WindowsPc(), MacProject.FriendsMac());
        var message = ShareCheck.MessageForFriend(r);
        Assert.StartsWith("Hey Jay, I checked Odd Todd and a few plugins don't load on my side:", message);
        Assert.Contains("- Diva (AU): it's the Audio Unit and Windows can't run those. I have the VST3, could you switch to that?", message);
        Assert.Contains("- Duck (AU): it's the Audio Unit and Windows can't run those, and I don't have another version.", message);
        Assert.Contains("These samples didn't come through: Kick 01.wav and Hat 03.wav.", message);
        // Jay has everything his own project uses, so nothing for him to worry about.
        Assert.DoesNotContain("might not have", message);
    }

    [Fact]
    public void ChecksAZipFromAFriend()
    {
        var project = MacProject.Write(_dir);
        var zipPath = Path.Combine(_dir, "Odd Todd.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(project, "Odd Todd.flp");
            zip.CreateEntry("Kick 01.wav").Open().Dispose();
            zip.CreateEntry("__MACOSX/._Odd Todd.flp").Open().Dispose();
        }
        var r = ShareCheck.Run(zipPath, MacProject.WindowsPc(), MacProject.FriendsMac());
        Assert.Equal(("Odd Todd", "Odd Todd.flp"), (r.ProjectName, r.FileInZip));
        Assert.Equal(140.0, r.Tempo);
        var samples = r.Samples.ToDictionary(s => s.FileName);
        Assert.Equal((true, "In the zip"), (samples["Kick 01.wav"].Found, samples["Kick 01.wav"].Where));
        Assert.False(samples["Hat 03.wav"].Found);
    }

    [Fact]
    public void SamplesFromTheSendersUserFolderMustBeInTheZip()
    {
        var project = Path.Combine(_dir, "Loops.flp");
        File.WriteAllBytes(project, new Fixtures.FlpBuilder()
            .Ascii(199, "25.1.0.4868")
            .Word(64, 0).Byte(21, 0).Text(196, @"%FLStudioFactoryData%\Data\Patches\Packs\Drums\Kicks\FPC_Kick_001.wav")
            .Word(64, 1).Byte(21, 0).Text(196, @"%FLStudioUserData%\Audio\Recorded\Vox take 3.wav")
            .Build());
        var zipPath = Path.Combine(_dir, "Loops.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) zip.CreateEntryFromFile(project, "Loops.flp");

        var samples = ShareCheck.Run(zipPath, MacProject.WindowsPc()).Samples.ToDictionary(s => s.FileName);
        Assert.Equal((true, "Comes with FL Studio"), (samples["FPC_Kick_001.wav"].Found, samples["FPC_Kick_001.wav"].Where));
        Assert.Equal((false, "Missing"), (samples["Vox take 3.wav"].Found, samples["Vox take 3.wav"].Where));
    }

    [Fact]
    public void AsksToScanFirstWhenThisPcHasNoPluginList()
    {
        var notScanned = new PluginInventory("This PC", []);
        var path = Path.Combine(_dir, "Simple.flp");
        File.WriteAllBytes(path, new Fixtures.FlpBuilder()
            .Ascii(199, "25.1.0.4868").Word(64, 0).Byte(21, 2)
            .Text(201, "Fruity Wrapper")
            .Data(213, Fixtures.WrapperBlob("Serum", @"C:\Program Files\Common Files\VST3\Serum.vst3", "Xfer Records", [1], 12, 8))
            .Build());
        var r = ShareCheck.Run(path, notScanned);
        Assert.True(r.NotScanned);
        Assert.Equal(ShareStatus.Unknown, Assert.Single(r.Plugins).Here);
        Assert.Equal("This PC hasn't been checked yet: scan your plugins (Scan for changes) first.", r.Headline);
        Assert.StartsWith("Scan your plugins", r.Actions[0]);

        // Audio Units never load on Windows, scanned or not, so those still count.
        var mac = ShareCheck.Run(MacProject.Write(_dir), notScanned);
        Assert.Equal(ShareStatus.WontLoad, mac.Plugins.Single(c => c.Name == "Diva").Here);
        Assert.StartsWith("3 plugins won't load on this PC", mac.Headline);
        Assert.StartsWith("Scan your plugins", mac.Actions[0]);
    }

    [Fact]
    public void DoesNotAskTheFriendToFixYourOwnProject()
    {
        // Checking your own project before sending it: what's missing here is yours to sort out.
        var r = ShareCheck.Run(MacProject.Write(_dir), MacProject.WindowsPc(), MacProject.FriendsMac(), outgoing: true);
        Assert.True(r.Outgoing);
        Assert.Equal("", ShareCheck.MessageForFriend(r));
    }

    [Fact]
    public void RejectsAZipWithoutAProject()
    {
        var zipPath = Path.Combine(_dir, "loops.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) zip.CreateEntry("loop.wav").Open().Dispose();
        Assert.Throws<InvalidDataException>(() => ShareCheck.Run(zipPath, MacProject.WindowsPc()));
    }

    [Fact]
    public void WritesAReadmeIntoTheZip()
    {
        var project = MacProject.Write(_dir);
        var zipPath = Path.Combine(_dir, "share.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) zip.CreateEntryFromFile(project, "Odd Todd.flp");

        var r = ShareCheck.Run(zipPath, MacProject.WindowsPc());
        var readme = ShareCheck.Readme(r, new DateTime(2026, 10, 7));
        Assert.Contains("FL Studio 25.1.5.4642, 140 BPM, last saved on Mac", readme);
        Assert.Contains("  Diva (u-he), AU   << Mac only (Audio Unit)", readme);
        Assert.Contains("  FabFilter Pro-Q 4 (FabFilter), VST3", readme);
        Assert.Contains("BUILT INTO FL STUDIO (open on Mac and Windows)", readme);
        Assert.Contains("SAMPLES: 2, 2 missing", readme);

        ShareCheck.AddReadmeToZip(zipPath, readme);
        ShareCheck.AddReadmeToZip(zipPath, "second version");
        using var check = ZipFile.OpenRead(zipPath);
        var entry = Assert.Single(check.Entries, e => e.FullName == ShareCheck.ReadmeName);
        using var text = new StreamReader(entry.Open());
        Assert.Equal("second version", text.ReadToEnd());
        Assert.Contains(check.Entries, e => e.FullName == "Odd Todd.flp");
    }
}

public class PluginImpactTests : IDisposable
{
    readonly string _root = Fixtures.TempDir();
    readonly LibraryDb _db;

    public PluginImpactTests()
    {
        _db = new LibraryDb(Path.Combine(_root, "library.db"));
        _db.AddRoot(_root);
        Fixtures.FlStudioProject(_root);           // Serum (VST2 .dll), Pro-Q 3 (VST2)
        Fixtures.FlStudio25Project(_root);         // Serum (VST2), Arcade (VST3)
        Fixtures.StudioOneSong(_root);             // Diva (VST3), Pro-C 2, Mai Tai (native)
        _db.ReplaceInstalledPluginFiles(
        [
            new("Serum_x64", "VST2", @"C:\Program Files\VstPlugins\Serum_x64.dll"),
            new("Serum", "VST3", @"C:\Program Files\Common Files\VST3\Serum.vst3"),
            new("Arcade", "VST3", @"C:\Program Files\Common Files\VST3\Arcade.vst3"),
            new("Sylenth1", "VST2", @"C:\Program Files\VstPlugins\Sylenth1.dll"),
        ]);
        new Scanner(_db).ScanProjects();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void ListsTheProjectsThatWouldBreak()
    {
        var p = _db.Projects().First(x => x.Name == "Night Drive");
        p.Status = "Released";
        _db.SaveOrganisation(p);

        var impact = PluginImpact.Of(_db, [("Serum", "Xfer Records")]);
        Assert.Equal(["Night Drive", "Shuffle"], impact.Songs.Select(x => x.Song));   // released first
        Assert.Equal("Uninstalling Serum would affect 2 songs, including 1 released.", impact.Headline);
        var copies = impact.Copies.ToDictionary(c => c.File.Format);
        Assert.Equal((2, false), (copies["VST2"].Songs, copies["VST2"].Unused));
        Assert.True(copies["VST3"].Unused);
        Assert.Contains("No song uses the VST3 copy", impact.Detail);
    }

    [Fact]
    public void AddsUpSeveralPlugins()
    {
        var impact = PluginImpact.Of(_db, [("Serum", ""), ("Diva", "u-he")]);
        Assert.Equal(3, impact.Songs.Count);
        Assert.StartsWith("Uninstalling these 2 plugins would affect 3 songs", impact.Headline);
    }

    [Fact]
    public void CountsEachCopyOnlyForItsOwnPlugin()
    {
        var copies = PluginImpact.Of(_db, [("Serum", ""), ("Arcade", "Output")]).Copies
                                 .ToDictionary(c => (PluginKey.Of(c.File.Name), c.File.Format));
        Assert.Equal(2, copies[("serum", "VST2")].Songs);
        Assert.True(copies[("serum", "VST3")].Unused);
        Assert.Equal(1, copies[("arcade", "VST3")].Songs);
    }

    [Fact]
    public void CountsASongOnceWhateverSavesUseThePlugin()
    {
        var first = _db.Projects().First(x => x.Name == "Night Drive");
        first.Status = "Released";
        _db.SaveOrganisation(first);
        // Two later saves: _2 still uses Serum, _3 (the latest) swapped it for a native synth.
        Fixtures.FlStudioProject(_root, "Night Drive_2");
        File.WriteAllBytes(Path.Combine(_root, "Night Drive_3.flp"), new Fixtures.FlpBuilder()
            .Ascii(199, "21.2.3.4004").Word(64, 0).Byte(21, 2)
            .Text(201, "3x Osc").Text(203, "Lead").Data(213, [9])
            .Build());
        new Scanner(_db).ScanProjects();

        var impact = PluginImpact.Of(_db, [("Serum", "Xfer Records")]);
        Assert.Equal(["Night Drive", "Shuffle"], impact.Songs.Select(x => x.Song));
        var night = impact.Songs[0];
        Assert.Equal(("Night Drive_3", 3, 2, false), (night.Latest.Name, night.Saves, night.SaveIds.Count, night.InLatest));
        Assert.Equal("only 2 older saves", night.SavesText);
        Assert.Equal("Uninstalling Serum would affect 2 songs (3 saves), including 1 released.", impact.Headline);
        Assert.Equal(2, impact.Copies.Single(c => c.File.Format == "VST2").Songs);
    }

    [Fact]
    public void SaysWhenNothingUsesAPlugin()
    {
        var impact = PluginImpact.Of(_db, [("Sylenth1", "")]);
        Assert.Empty(impact.Songs);
        Assert.Equal("No project in your library uses Sylenth1. Uninstalling won't break anything here.", impact.Headline);
    }

    [Fact]
    public void FindsInstalledPluginsNothingUses()
    {
        var unused = PluginImpact.Unused(_db).ToDictionary(u => u.Name);
        Assert.Equal("Not used by any project", unused["Sylenth1"].UsageText);
        // Serum: projects only use the VST2, so the VST3 copy is spare.
        var serum = unused.Values.Single(u => PluginKey.Of(u.Name) == "serum");
        Assert.Equal(["VST3"], serum.Copies.Select(c => c.Format));
        Assert.Equal("Projects only use the VST2", serum.UsageText);
        Assert.DoesNotContain("Arcade", unused.Keys);
    }

    [Fact]
    public void ScanRecordsEachPluginFileAndFormat()
    {
        var folder = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(Path.Combine(folder, "Vital.vst3", "Contents", "x86_64-win"));
        File.WriteAllBytes(Path.Combine(folder, "Vital.vst3", "Contents", "x86_64-win", "Vital.vst3"), [0]);
        File.WriteAllBytes(Path.Combine(folder, "Surge XT.clap"), [0]);
        File.WriteAllBytes(Path.Combine(folder, "Sylenth1.dll"), [0]);
        _db.SetList(Scanner.PluginFoldersKey, [folder]);

        new Scanner(_db).ScanInstalledPlugins();
        Assert.Equal([("Surge XT", "CLAP"), ("Sylenth1", "VST2"), ("Vital", "VST3")],
                     _db.InstalledPluginFiles().Select(f => (f.Name, f.Format)));
        Assert.Contains("Vital", _db.InstalledNames());
    }

    [Fact]
    public void ReReadsEveryProjectAfterAnUpdate()
    {
        // A library indexed by an older version: its plugin rows are out of date.
        var night = _db.KnownFiles().Keys.Single(f => f.EndsWith("Night Drive.flp"));
        var stale = Scanner.Analyse(night);
        _db.Upsert(stale with { Info = null, Error = "old reader" });
        _db.SetSetting("index_version", "1");

        new Scanner(_db).ScanProjects();
        Assert.Contains(_db.PluginRows(), r => r.ProjectId == _db.Projects().Single(p => p.Name == "Night Drive").Id);
        Assert.Equal(Scanner.IndexVersion, _db.GetSetting("index_version"));
    }

    [Fact]
    public void KeepsCollaboratorsPluginLists()
    {
        Collaborators.Upsert(_db, "Jay", MacProject.FriendsMac());
        Collaborators.Upsert(_db, "jay", PluginInventory.Parse("jay", "Serum (AU)"));
        Collaborators.Upsert(_db, "Ana", PluginInventory.Parse("Ana", "Vital\tCLAP"));
        var people = Collaborators.Load(_db);
        Assert.Equal(["Ana", "jay"], people.Select(p => p.Name));
        Assert.Equal(Availability.Same, people[1].Inventory.Check("Serum", "", "AU", out _));
        Collaborators.Remove(_db, "Ana");
        Assert.Single(Collaborators.Load(_db));
    }
}

public class WavesImpactTests : IDisposable
{
    readonly string _root = Fixtures.TempDir();
    readonly LibraryDb _db;

    public WavesImpactTests()
    {
        _db = new LibraryDb(Path.Combine(_root, "library.db"));
        _db.AddRoot(_root);
        const string shell = @"C:\Program Files\Common Files\VST3\WaveShell1-VST3 14.12_x64.vst3";
        File.WriteAllBytes(Path.Combine(_root, "Waves Mix.flp"), new Fixtures.FlpBuilder()
            .Ascii(199, "25.1.0.4868")
            .Data(236, [0, 0, 0, 0])
            .Text(201, "Fruity Wrapper")
            .Data(213, Fixtures.WrapperBlob("SSLChannel Stereo", shell, "Waves", [1], 12, 7))
            .Word(98, 0)
            .Build());
        _db.ReplaceInstalledPluginFiles([new("WaveShell1-VST3 14.12_x64", "VST3", shell)]);
        new Scanner(_db).ScanProjects();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void CountsWavesPluginsAgainstTheirWaveShell()
    {
        var impact = PluginImpact.Of(_db, [("WaveShell1-VST3 14.12_x64", "")]);
        Assert.Equal(["Waves Mix"], impact.Songs.Select(s => s.Song));
        Assert.Equal(1, Assert.Single(impact.Copies).Songs);
        Assert.DoesNotContain(PluginImpact.Unused(_db), u => u.Name.StartsWith("WaveShell"));
    }
}

public class MacProjectImpactTests : IDisposable
{
    readonly string _root = Fixtures.TempDir();
    readonly LibraryDb _db;

    public MacProjectImpactTests()
    {
        _db = new LibraryDb(Path.Combine(_root, "library.db"));
        _db.AddRoot(_root);
        MacProject.Write(_root);                   // Diva as an AU
        Fixtures.StudioOneSong(_root);             // Diva as a VST3
        _db.ReplaceInstalledPluginFiles(MacProject.WindowsPc().Plugins);
        new Scanner(_db).ScanProjects();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void LeavesOutProjectsThatOnlyUseTheMacVersion()
    {
        var impact = PluginImpact.Of(_db, [("Diva", "u-he")]);
        Assert.Equal(["Sunday Song"], impact.Songs.Select(s => s.Song));
        Assert.Equal(1, impact.MacOnlySongs);
        Assert.Contains("1 more song uses the Mac (AU) version", impact.Detail);
    }

    [Fact]
    public void SaysWhenOnlyMacProjectsUseAPlugin()
    {
        var impact = PluginImpact.Of(_db, [("Arcade", "Output")]);
        Assert.Empty(impact.Songs);
        Assert.StartsWith("1 song uses the Mac (AU) version", impact.Detail);
        var arcade = Assert.Single(PluginImpact.Unused(_db), u => u.Name == "Arcade");
        Assert.Equal("Projects only use the AU (Mac)", arcade.UsageText);
    }
}
