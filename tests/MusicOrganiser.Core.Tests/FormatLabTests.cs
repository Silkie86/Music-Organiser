using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Formats;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Core.Tests;

public class FormatRegistryTests
{
    static readonly FormatRegistry Fl = FormatRegistry.FlStudio, Bw = FormatRegistry.Bitwig;

    static IEnumerable<(string Name, int Value)> Constants(Type t) =>
        t.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
         .Where(f => f.IsLiteral && f.FieldType == typeof(int))
         .Select(f => (f.Name, (int)f.GetRawConstantValue()!));

    [Fact]
    public void BothRegistriesLoadWithTheirNames()
    {
        Assert.Equal("tempo", Fl.Event(156)?.Name);
        Assert.Equal("ascii", Fl.Event(199)?.Type);
        Assert.Equal("plugin name", Fl.WrapperRecord(54)?.Name);
        Assert.Equal("setting IDs", Fl.Vst3Record(4)?.Name);
        Assert.Equal("VST3 plugin", Bw.Class(0x728)?.Name);
        Assert.Equal("device name", Bw.Field(0x9a)?.Name);
        Assert.NotNull(Bw.MetaKey("application_version_name"));
        Assert.Null(Fl.Event(150));
    }

    [Fact]
    public void IdsAreWrittenOnceAndEveryLayoutReads()
    {
        foreach (var list in new[] { Fl.Events, Fl.Wrapper, Fl.Vst3, Bw.Classes, Bw.Fields })
        {
            var twice = list.GroupBy(e => e.Number).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(twice.Count == 0, $"Written twice: {string.Join(", ", twice)}");
            Assert.All(list, e => Assert.False(string.IsNullOrWhiteSpace(e.Name), $"{e.Id} has no name"));
        }
        Assert.All(Fl.Events.Where(e => e.Layout is not null || e.RecordLayout is not null), e =>
        {
            LayoutField.Parse(e.Layout);
            var smallest = e.RecordByVersion?.Values.Min() ?? e.Record;
            foreach (var f in LayoutField.Parse(e.RecordLayout))
                Assert.True(f.Offset + f.Size <= smallest, $"{e.Id}: {f.Name} runs past its {smallest}-byte record");
        });
        Assert.Throws<FormatException>(() => LayoutField.Parse("pan i32"));
        Assert.Throws<FormatException>(() => LayoutField.Parse("pan x7@0"));
    }

    [Fact]
    public void EveryIdTheFlReaderUsesIsInTheRegistry()
    {
        var used = Constants(typeof(FlStudioReader)).ToList();
        foreach (var (name, value) in used.Where(c => c.Name.StartsWith("Ev")))
            Assert.True(Fl.Event(value)?.Code == name, $"FlStudioReader.{name} ({value}) isn't in fl-studio.json under that name");
        foreach (var (name, value) in used.Where(c => c.Name.StartsWith("Vst")))
            Assert.True(Fl.WrapperRecord(value)?.Code == name, $"FlStudioReader.{name} ({value}) isn't in fl-studio.json's wrapper records");
        foreach (var e in Fl.Events.Concat(Fl.Wrapper).Where(e => e.Code is not null))
            Assert.Contains((e.Code!, e.Number), used);
    }

    [Fact]
    public void EveryIdTheBitwigCodeUsesIsInTheRegistry()
    {
        var used = new[] { typeof(BitwigReader), typeof(BitwigPreset), typeof(BitwigProject) }.SelectMany(Constants).ToList();
        foreach (var (name, value) in used.Where(c => c.Name.StartsWith("Cls") || c.Name is "Vst2" or "Vst3"))
            Assert.True(Bw.Class(value) is not null, $"{name} (0x{value:x}) isn't a class in bitwig.json");
        foreach (var (name, value) in used.Where(c => Regex.IsMatch(c.Name, "^F[A-Z]")))
            Assert.True(Bw.Field(value) is not null, $"{name} (0x{value:x}) isn't a field in bitwig.json");
        foreach (var e in Bw.Classes.Concat(Bw.Fields).Where(e => e.Code is not null))
            Assert.Contains((e.Code!, e.Number), used);

        // Ids written straight into the code that reads and writes Bitwig's objects.
        var core = Path.GetDirectoryName(Fixtures.RepoFile("src/MusicOrganiser.Core/MusicOrganiser.Core.csproj"))!;
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var code = File.ReadAllText(file);
            if (!code.Contains("BwObject")) continue;
            foreach (Match m in Regex.Matches(code, @"\.(?:Obj|Set|Str|Num|List|Has)\(\s*0x([0-9a-fA-F]+)|\[\s*0x([0-9a-fA-F]+)\s*\]"))
            {
                var id = Convert.ToInt32(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, 16);
                if (Bw.Field(id) is null) missing.Add($"field 0x{id:x} in {Path.GetFileName(file)}");
            }
            foreach (Match m in Regex.Matches(code, @"Class\s*(?:==|:|is)\s*0x([0-9a-fA-F]+)"))
                if (Bw.Class(Convert.ToInt32(m.Groups[1].Value, 16)) is null) missing.Add($"class 0x{m.Groups[1].Value} in {Path.GetFileName(file)}");
        }
        Assert.True(missing.Count == 0, "Not in bitwig.json: " + string.Join(", ", missing.Distinct()));
    }

    [Fact]
    public void EveryTestARegistryEntryCitesExists()
    {
        var tests = typeof(FormatRegistryTests).Assembly.GetTypes();
        var cited = Fl.Events.Concat(Fl.Wrapper).Concat(Fl.Vst3).Concat(Bw.Classes).Concat(Bw.Fields).Concat(Bw.Meta)
            .SelectMany(e => Regex.Matches(e.Proof ?? "", @"\b([A-Z]\w*Tests)\.(\w+)").Select(m => (e.Id, Class: m.Groups[1].Value, Test: m.Groups[2].Value)))
            .ToList();
        Assert.NotEmpty(cited);
        foreach (var (id, cls, test) in cited)
            Assert.True(tests.FirstOrDefault(t => t.Name == cls)?.GetMethod(test) is not null, $"{id} cites {cls}.{test}, which doesn't exist");
    }
}

public class FormatLabTests
{
    readonly string _dir = Fixtures.TempDir();

    static LabFile Fl(byte[] raw) => LabFile.Read(raw, "test.flp");

    static byte[] Patched(string path, byte[] from, byte[] to)
    {
        var raw = File.ReadAllBytes(path);
        var at = raw.AsSpan().IndexOf(from);
        Assert.True(at >= 0);
        to.CopyTo(raw, at);
        return raw;
    }

    static byte[] Dword(int id, uint v) => [(byte)id, .. BitConverter.GetBytes(v)];

    [Fact]
    public void TreeNamesFlEventsAndTakesTheirDataApart()
    {
        var f = LabFile.Open(Fixtures.FlStudio25Project(_dir, withNotes: true));
        Assert.Equal(LabFormat.FlStudio, f.Format);
        Assert.Equal("25.1.0.4868", f.Version);
        var tree = FormatLab.Tree(f);
        Assert.Contains("  156 tempo = 126000", tree);
        Assert.Contains("channel 2\n", tree.ReplaceLineEndings("\n"));
        Assert.Contains("    wrapper 51 VST2 ID = 1483109208 (\"XfsX\")", tree);
        Assert.Contains("    wrapper 54 plugin name = \"Arcade\"", tree);
        Assert.Contains("      vst3 4 setting IDs = <16 bytes>", tree);
        Assert.Contains("    [1]: <24 bytes>", tree);
        Assert.Contains("      key = 64", tree);

        var notes = f.Items.Single(i => i.Key == "pattern 1/224/[1]");
        Assert.Equal((24, 24), (notes.Start, notes.Length));
        Assert.Equal(2L, f.Items.Single(i => i.Key == "pattern 1/224/[1]/channel").Data);
        Assert.DoesNotContain("velocity", FormatLab.Tree(f, maxDepth: 2));
    }

    [Fact]
    public void FlScopesFollowChannelsPatternsAndMixerSlots()
    {
        var f = LabFile.Open(Fixtures.FlStudioProject(_dir));
        var keys = f.Items.Select(i => i.Key).ToHashSet();
        Assert.Contains("channel 1/196", keys);
        Assert.Contains("pattern 2/193", keys);
        Assert.Contains("insert 0, slot 0/201", keys);
        Assert.Contains("insert 0, slot 1/213/wrapper 54", keys);
        Assert.Equal("\"Pro-Q 3\"", f.Items.Single(i => i.Key == "insert 0, slot 1/213/wrapper 54").Value);
    }

    [Fact]
    public void DiffNamesEachChangedValue()
    {
        var path = Fixtures.FlStudioProject(_dir);
        var before = LabFile.Open(path);
        var after = Fl(Patched(path, Dword(156, 128_000), Dword(156, 121_500)));
        var change = Assert.Single(FormatLab.Diff(before, after));
        Assert.Equal(("changed", "project/156", "128000", "121500"), (change.Kind, change.Key, change.Before, change.After));
        Assert.Contains("changed  project › 156 tempo", FormatLab.DiffText(before, after, [change]));

        var renamed = Fl(Patched(path, Encoding.Unicode.GetBytes("Kick"), Encoding.Unicode.GetBytes("Kock")));
        Assert.Equal(["channel 1/203"], FormatLab.Diff(before, renamed).Select(c => c.Key));
        Assert.Empty(FormatLab.Diff(before, LabFile.Open(path)));
    }

    [Fact]
    public void DiffListsAnAddedPartOnceWithWhatItHolds()
    {
        var plain = LabFile.Open(Fixtures.FlStudioProject(_dir, "Plain"));
        var arranged = LabFile.Open(Fixtures.FlStudioProject(_dir, "Arranged",
            playlist: [.. Fixtures.PlaylistItem(0, 1, 384), .. Fixtures.PlaylistItem(384, 2, 384)]));
        var change = Assert.Single(FormatLab.Diff(plain, arranged));
        Assert.Equal(("added", "arrangement"), (change.Kind, change.Key));
        Assert.True(change.Inside >= 3);   // the playlist event, its records and their numbers
        var back = Assert.Single(FormatLab.Diff(arranged, plain));
        Assert.Equal(("removed", "arrangement"), (back.Kind, back.Key));
    }

    [Fact]
    public void DiffSaysWhereDataDiffers()
    {
        var path = Fixtures.FlStudio25Project(_dir);
        var before = LabFile.Open(path);
        var after = Fl(Patched(path, Encoding.ASCII.GetBytes("XsfX"), Encoding.ASCII.GetBytes("XsfY")));
        var change = Assert.Single(FormatLab.Diff(before, after));
        Assert.Equal("channel 1/213/wrapper 51", change.Key);
    }

    [Fact]
    public void FindLooksForNumbersInFieldsAndRawBytes()
    {
        var f = LabFile.Open(Fixtures.FlStudio25Project(_dir));
        var tempo = Assert.Single(FormatLab.Find(f, "126000"));
        Assert.Equal(("project › 156 tempo", "number"), (tempo.Path, tempo.How));

        // Predator's first setting, 0.25, is a float inside the plugin's own state.
        var setting = Assert.Single(FormatLab.Find(f, "0.25"));
        Assert.Equal("channel 3 › 213 plugin state › wrapper 53 plugin's own state", setting.Path);
        Assert.StartsWith("32-bit float at byte ", setting.How);

        var text = FormatLab.Find(f, "arcade");
        Assert.Contains(text, h => h.Path.EndsWith("wrapper 54 plugin name") && h.How == "text");
        Assert.Empty(FormatLab.Find(f, "126000", textOnly: true));
        Assert.Contains("isn't in it", FormatLab.FindText(f, "Nope", FormatLab.Find(f, "Nope")));
    }

    [Fact]
    public void FindDoesNotRepeatWhatARecordFoundForTheDataAroundIt()
    {
        var f = LabFile.Open(Fixtures.FlStudioProject(_dir));
        // The second note's velocity is 128: found as that number, not again as bytes of its record or event.
        var hits = FormatLab.Find(f, "128").Where(h => h.Path.StartsWith("pattern 1")).ToList();
        var hit = Assert.Single(hits);
        Assert.Equal(("pattern 1 › 224 pattern notes[1] › velocity", "number"), (hit.Path, hit.How));
    }

    [Fact]
    public void TreeShowsBitwigObjectsNestedDocumentsAndZipFiles()
    {
        var f = LabFile.Open(Fixtures.BitwigProject(_dir));
        Assert.Equal(LabFormat.Bitwig, f.Format);
        Assert.Equal("5.2.4", f.Version);
        Assert.Null(f.Problem);
        var tree = FormatLab.Tree(f);
        Assert.Contains("meta structure (summary): <", tree);
        Assert.Contains("0xad1 shown name = \"Keys Display\"", tree);
        Assert.Contains("0x2c8 value = 128.5", tree);
        Assert.Contains("[0]: 0x728 VST3 plugin", tree);
        Assert.Contains("0x9a device name = \"Keyscape\"", tree);
        Assert.Contains("0x4e0 master track = → project group › tracks[0] › 0x2bb0 (0x90 track)", tree);
        Assert.Contains("plugin-states/aaaa-keys.vstpreset = <15 bytes> \"VST3 keys state\"", tree);
    }

    [Fact]
    public void BitwigDiffFindsAChangedTempo()
    {
        var path = Fixtures.BitwigProject(_dir);
        var before = LabFile.Open(path);
        var be = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(be, 128.5);
        var to = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(to, 133);
        var after = LabFile.Read(Patched(path, be, to), "Loop Idea.bwproject");
        var change = Assert.Single(FormatLab.Diff(before, after));
        Assert.Equal(("128.5", "133"), (change.Before, change.After));
        Assert.EndsWith("value", change.Path);

        Assert.Contains(FormatLab.Find(after, "133"), h => h.How == "number" && h.Path.EndsWith("value"));
        Assert.Equal(2, FormatLab.Find(after, "keyscape").Count);   // the device and its file
    }

    [Fact]
    public void RefusesFilesFromOtherApps()
    {
        Assert.Throws<InvalidDataException>(() => LabFile.Read("RIFF....WAVE"u8.ToArray(), "kick.wav"));
    }
}

public class FormatCensusTests
{
    [Fact]
    public void CountsWhatTheRegistryDoesNotKnow()
    {
        var dir = Fixtures.TempDir();
        Fixtures.FlStudioProject(dir);
        Fixtures.FlStudio25Project(dir);
        File.WriteAllBytes(Path.Combine(dir, "Odd.flp"),
            new Fixtures.FlpBuilder().Ascii(199, "24.1.1.4234").Dword(150, 7).Dword(150, 8).Build());
        Fixtures.BitwigProject(dir);
        File.WriteAllBytes(Path.Combine(dir, "Broken.flp"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a project");

        var census = FormatCensus.Run(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories));
        Assert.Equal((3, 1, 1), (census.FlProjects, census.BitwigProjects, census.UnreadableCount));
        Assert.Equal(["21", "24", "25"], census.FlVersions.Keys);
        var (id, tally) = Assert.Single(census.UnknownFlEvents);
        Assert.Equal((150, 1, 2L), (id, tally.Projects, tally.Times));
        Assert.Contains(census.UnknownBitwigClasses, c => c.Id == 0x2e);
        Assert.DoesNotContain(census.UnknownBitwigClasses, c => c.Id == 0x728);
        Assert.DoesNotContain(census.UnknownBitwigFields, c => c.Id == 0x9a);

        var report = census.Report();
        Assert.Contains("Format census of 5 projects", report);
        Assert.Contains("event 150  in 1 (33.3%)", report);
        Assert.Contains("Broken.flp", report);
    }
}

public class MoProbeTests
{
    const string Instance = "2E044C2E5009EFCE";

    // As the probe writes them (from a run of native/mo-probe's test host), with the save made on track "Bass".
    internal static readonly string[] SavingRun =
    [
        """{"t":"2026-10-14T06:35:17.528Z","event":"create","n":1,"instance":"2E044C2E5009EFCE","plugin":"org.musicorganiser.probe.instrument","probeVersion":"1.0.0","host":"Bitwig Studio","hostVendor":"Bitwig GmbH","hostVersion":"6.1.3","hostUrl":"","hostClap":"1.2.6","os":"windows","format":"CLAP","binary":"C:\\Program Files\\Common Files\\CLAP\\MO Probe.clap"}""",
        """{"t":"2026-10-14T06:35:17.528Z","event":"track","n":2,"instance":"2E044C2E5009EFCE","name":"Bass","color":"#4080C0","alpha":255,"return":false,"bus":false,"master":false}""",
        """{"t":"2026-10-14T06:35:17.528Z","event":"activate","n":3,"instance":"2E044C2E5009EFCE","sampleRate":48000,"minFrames":1,"maxFrames":480}""",
        """{"t":"2026-10-14T06:35:17.528Z","event":"param","n":6,"id":1007,"name":"MO Ramp","value":75,"offset":0,"playing":true,"beats":0.02,"seconds":0.01,"bar":1,"beat":1.02}""",
        """{"t":"2026-10-14T06:35:17.528Z","event":"note-on","n":7,"key":60,"channel":0,"port":0,"velocity":0.8,"noteId":7,"offset":240,"playing":true,"beats":0.03,"seconds":0.015,"bar":1,"beat":1.03}""",
        """{"t":"2026-10-14T06:35:17.528Z","event":"note-off","n":8,"key":60,"channel":0,"port":0,"velocity":0,"noteId":7,"offset":0,"playing":true,"beats":0.06,"seconds":0.03,"bar":1,"beat":1.06}""",
        """{"t":"2026-10-14T06:35:18.000Z","event":"save","n":9,"instance":"2E044C2E5009EFCE","context":"project","bytes":243,"save":1,"identity":"2E044C2E5009EFCE"}""",
        """{"t":"2026-10-14T06:35:19.000Z","event":"destroy","n":11,"instance":"2E044C2E5009EFCE","saves":1}""",
        """{"t":"2026-10-14T06:35:19.000Z","event":"note-on","n":12,"key":6""",   // the DAW quit mid-line
    ];

    internal static string Report(string dir, string name, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    static byte[] State(int save = 1, string label = "") => Encoding.ASCII.GetBytes(
        $"MOPROBE1\n{{\"probe\":\"instrument\",\"version\":\"1.0.0\",\"identity\":\"{Instance}\",\"label\":\"{label}\"," +
        $"\"instance\":\"{Instance}\",\"save\":{save},\"context\":\"project\",\"params\":{{\"1001\":0.25,\"1002\":0," +
        "\"1003\":440,\"1004\":3,\"1005\":0,\"1006\":1,\"1007\":75,\"1008\":0}}\n");

    [Fact]
    public void ReadsAReportAndSkipsAHalfWrittenLine()
    {
        var r = ProbeReport.Read(Report(Fixtures.TempDir(), "20261014T063517Z-instrument-2E044C2E.jsonl", SavingRun))!;
        Assert.Equal(8, r.Lines.Count);
        Assert.Equal(("instrument", Instance, "Bitwig Studio", "6.1.3", "CLAP"), (r.Kind, r.Instance, r.Host, r.HostVersion, r.Format));
        Assert.Equal(("Bass", "#4080C0", true), (r.Track, r.TrackColour, r.Finished));
        Assert.Equal((1, 1, 0), (r.Notes, r.KnobMoves, r.Loads));
        var save = Assert.Single(r.Saves);
        Assert.Equal((1, "project", 243), (save.Number, save.Context, save.Bytes));
        Assert.Equal("Bitwig Studio 6.1.3 · CLAP · on “Bass” · 1 save · 1 note · 1 knob move", r.Summary);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 6, 35, 19, TimeSpan.Zero), r.Ended);
    }

    [Fact]
    public void ListsReportsNewestFirst()
    {
        var dir = Fixtures.TempDir();
        Report(dir, "older-instrument-1.jsonl", [SavingRun[0].Replace("06:35:17", "05:00:00")]);
        Report(dir, "newer-effect-2.jsonl", [SavingRun[0].Replace("probe.instrument", "probe.effect")]);
        Report(dir, "empty-instrument-3.jsonl", []);
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "{}");
        var reports = ProbeReport.ReadAll(dir);
        Assert.Equal(["newer-effect-2.jsonl", "older-instrument-1.jsonl"], reports.Select(r => Path.GetFileName(r.File)));
        Assert.Equal("effect", reports[0].Kind);
        Assert.Empty(ProbeReport.ReadAll(Path.Combine(dir, "missing")));
    }

    [Fact]
    public void FindsTheProbesStateInAnFlProjectAndMatchesItsReport()
    {
        var dir = Fixtures.TempDir();
        var flp = new Fixtures.FlpBuilder().Ascii(199, "25.2.5.5319")
            .Word(64, 0).Byte(21, 2).Text(201, "Fruity Wrapper").Text(203, "Probe")
            .Data(213, Fixtures.WrapperBlob("MO Probe Instrument", @"C:\Program Files\Common Files\CLAP\MO Probe.clap",
                                            "Music Organiser", State(), version: 12))
            .Build();
        var file = LabFile.Read(flp, "Probe.flp");
        var state = Assert.Single(ProbeState.In(file));
        Assert.Equal(("instrument", Instance, 1, "project"), (state.Kind, state.Identity, state.Save, state.Context));
        Assert.Equal("channel 0 › 213 plugin state › wrapper 53 plugin's own state", state.Where);
        Assert.Equal("plain text", state.How);
        Assert.Equal(75, state.Knobs[1007]);

        var reports = new[] { ProbeReport.Read(Report(dir, "a.jsonl", SavingRun))! };
        Assert.Same(reports[0], state.SavedBy(reports));
        Assert.Null((state with { Save = 2 }).SavedBy(reports));
        var text = ProbeState.Describe(file, [state], reports);
        Assert.Contains("MO Probe Instrument, save 1 (project)", text);
        Assert.Contains("MO Ramp 75", text);
        Assert.Contains("Saved by Bitwig Studio 6.1.3 as CLAP, on “Bass”", text);
        Assert.Contains("No report has this save", ProbeState.Describe(file, [state with { Save = 2 }], reports));
    }

    [Fact]
    public void FindsTheProbesStateInABitwigProjectsZip()
    {
        byte[] zip;
        using (var zs = new MemoryStream())
        {
            using (var z = new ZipArchive(zs, ZipArchiveMode.Create, leaveOpen: true))
            using (var s = z.CreateEntry("plugin-states/probe.clap-preset").Open())
                s.Write([.. "CLAP"u8, 0, 0, 0, 0, .. State(label: "Kick")]);
            zip = zs.ToArray();
        }
        var raw = Fixtures.BwWriter.File(m => { m.MetaEntry("application_version_name", 0x08); m.StrVal("6.1.3"); },
                                         b => { b.Begin(0x2e); b.End(); }, zip);
        var file = LabFile.Read(raw, "Probe.bwproject");
        var state = Assert.Single(ProbeState.In(file));
        Assert.Equal(("zip › plugin-states/probe.clap-preset", "plain text, 8 bytes in", "Kick"), (state.Where, state.How, state.Label));
        Assert.Contains("“Kick”", ProbeState.Describe(file, [state], []));
    }

    [Fact]
    public void KnowsTheProbesKnobsAsThePluginDefinesThem()
    {
        var source = Fixtures.RepoFile("native/mo-probe/src/probe.cpp");
        Assert.NotNull(source);
        var defined = Regex.Matches(File.ReadAllText(source), @"\{\s*(\d{4}),\s*""([^""]+)"",\s*(-?[\d.]+),\s*(-?[\d.]+),\s*(-?[\d.]+),")
            .Select(m => (int.Parse(m.Groups[1].Value), m.Groups[2].Value, double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                          double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture),
                          double.Parse(m.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
        Assert.Equal(defined, MoProbe.Knobs);
        Assert.Equal("MO Gain", MoProbe.KnobName(1002));
    }

    [Fact]
    public void ReadsTheProbesStateText()
    {
        var s = ProbeState.Parse(State(save: 3, label: "Pad"))!;
        Assert.Equal((3, "Pad", 8), (s.Save, s.Label, s.Knobs.Count));
        Assert.Null(ProbeState.Parse("MOPROBE1\nnot json"u8));
        Assert.Null(ProbeState.Parse("MOPROBE1"u8));
    }
}

public class FormatLabCommandTests
{
    static (int Code, string Out, string Err) Run(params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var code = FormatLabCommand.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void RunsEachCommand()
    {
        var dir = Fixtures.TempDir();
        var flp = Fixtures.FlStudioProject(dir);
        var bw = Fixtures.BitwigProject(dir);

        Assert.Equal(2, Run().Code);
        Assert.Contains("formatlab tree", Run("help").Out);

        var tree = Run("tree", flp, "--depth", "1");
        Assert.Equal(0, tree.Code);
        Assert.Contains("156 tempo = 128000", tree.Out);
        Assert.Contains("128000", Run("find", flp, "128000").Out);
        Assert.Contains("No differences.", Run("diff", flp, flp).Out);
        Assert.Equal(1, Run("diff", flp, bw).Code);

        var census = Run("census", dir);
        Assert.Equal(0, census.Code);
        Assert.Contains("Format census of 2 projects", census.Out);
        Assert.Contains("Counting 2 projects", census.Err);

        var reports = Path.Combine(dir, "probe");
        Directory.CreateDirectory(reports);
        Assert.Contains("MO Probe reports in", Run("probe", "--reports", reports).Out);
        Assert.Contains("No MO Probe state", Run("probe", flp, "--reports", reports).Out);

        var fields = Run("fields", "fl", "156");
        Assert.Contains("tempo", fields.Out);
        Assert.Contains("how we know:", fields.Out);
        Assert.Contains("VST3 plugin", Run("fields", "bitwig").Out);
    }

    [Fact]
    public void SaysWhatWentWrong()
    {
        var missing = Run("tree", Path.Combine(Fixtures.TempDir(), "gone.flp"));
        Assert.Equal(1, missing.Code);
        Assert.StartsWith("Format Lab:", missing.Err);
        Assert.Equal(2, Run("tree").Code);
        Assert.Equal(2, Run("fields", "studio one").Code);
        var unknown = Run("explode");
        Assert.Equal(2, unknown.Code);
        Assert.Contains("there's no command \"explode\"", unknown.Err);
    }
}

public class ProbeReportsViewModelTests
{
    [Fact]
    public async Task ListsTheNewestReportsAndSaysWhetherTheProbeIsInstalled()
    {
        var dir = Fixtures.TempDir();
        var reports = Path.Combine(dir, "probe");
        var plugins = Path.Combine(dir, "CLAP");
        Directory.CreateDirectory(plugins);
        var vm = new ProbeReportsViewModel(reports, () => [plugins]);
        await vm.RefreshAsync();
        Assert.Empty(vm.Reports);
        Assert.StartsWith("MO Probe isn't in your plugin folders", vm.Status);

        File.WriteAllBytes(Path.Combine(plugins, "MO Probe.clap"), [0]);
        Directory.CreateDirectory(Path.Combine(plugins, "MO Probe.vst3"));   // a bundle, as on a Mac
        await vm.RefreshAsync();
        Assert.Equal("MO Probe is installed (CLAP, VST3). Put it on a track in your DAW, and its reports show here.", vm.Status);

        MoProbeTests.Report(reports, "20261014T063517Z-instrument-2E044C2E.jsonl", MoProbeTests.SavingRun);
        MoProbeTests.Report(reports, "20261014T070000Z-effect-0000ABCD.jsonl", [MoProbeTests.SavingRun[0].Replace("probe.instrument", "probe.effect")]);
        await vm.RefreshAsync();
        Assert.Equal(2, vm.Reports.Count);
        Assert.StartsWith("MO Probe is installed (CLAP, VST3). Its newest reports:", vm.Status);
        var saved = vm.Reports.Single(r => r.Title == "MO Probe Instrument");
        Assert.Equal("Bitwig Studio 6.1.3 · CLAP · on “Bass” · 1 save · 1 note · 1 knob move", saved.Summary);
        Assert.Equal("MO Probe Effect, still open", vm.Reports.Single(r => r.File.Contains("effect")).Title);
    }
}
