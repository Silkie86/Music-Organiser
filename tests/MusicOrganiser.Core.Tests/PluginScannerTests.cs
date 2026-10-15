using System.Diagnostics;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Plugins;

namespace MusicOrganiser.Core.Tests;

/// <summary>
/// The app's side of the native plugin scanner: reading its output, running it with a time limit,
/// and keeping what it found. native/tests covers the scanner itself.
/// </summary>
public class PluginScannerTests : IDisposable
{
    readonly string _root = Fixtures.TempDir();

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Lines as mo-scan prints them (from the MO Probe and the fake VST2 shell in native/tests).
    const string ClapLine = """{"format":"CLAP","path":"/p/MO Probe.clap","id":"org.musicorganiser.probe.effect","name":"MO Probe Effect","vendor":"Music Organiser","version":"1.0.0","kind":"effect","features":["audio-effect","utility","stereo"],"audioIn":2,"audioOut":2,"noteIn":1,"noteOut":0,"params":[{"id":1003,"name":"MO Frequency","module":"Probe","min":20,"max":20000,"default":440,"value":440,"text":"440.0 Hz","stepped":false,"automatable":true,"hidden":false,"readOnly":false,"bypass":false},{"id":1004,"name":"MO Steps","module":"Probe","min":0,"max":7,"default":3,"value":3,"text":"3","stepped":true,"automatable":true,"hidden":false,"readOnly":false,"bypass":false},{"id":1008,"name":"MO Bypass","module":"","min":0,"max":1,"default":0,"value":0,"text":"Off","stepped":true,"automatable":true,"hidden":true,"readOnly":false,"bypass":true}]}""";
    const string Vst3Line = """{"format":"VST3","path":"/p/MO Probe.vst3","id":"805ECA49563E43EA8119369F0ED6096D","name":"MO Probe Effect","vendor":"Music Organiser","version":"1.0.0","kind":"effect","features":["Fx","Tools"],"audioIn":2,"audioOut":2,"noteIn":1,"noteOut":0,"params":[{"id":1002,"name":"MO Gain","shortName":"Gain","units":"dB","steps":0,"default":0.8333,"value":0.8333,"plainMin":-60,"plainMax":12,"plainDefault":0,"text":"0.0","unit":0,"automatable":true,"readOnly":false,"list":false,"hidden":false,"bypass":false,"program":false}]}""";
    const string ShellLine = """{"shell":"WaveShell1-VST 15.0","format":"VST2","path":"C:\\VST\\WaveShell1-VST 15.0.dll","id":"MoB2","uniqueId":1299137074,"name":"L2 Stereo","vendor":"Waves"}""";
    const string ErrorLine = """{"path":"C:\\VST\\Old.dll","error":"32-bit plugin: this scanner is 64-bit"}""";

    [Fact]
    public void ReadsTheScannersLines()
    {
        var clap = ScannedPlugin.Parse(ClapLine)!;
        Assert.Equal(("CLAP", "org.musicorganiser.probe.effect", "MO Probe Effect", "Music Organiser", "effect"),
                     (clap.Format, clap.Id, clap.Name, clap.Vendor, clap.Kind));
        Assert.Equal((2, 2, 1), (clap.AudioIn, clap.AudioOut, clap.NoteIn));
        var freq = clap.Params![0];
        Assert.Equal((1003L, 20.0, 20000.0, 440.0, 0), (freq.Id, freq.Min, freq.Max, freq.Default, freq.Steps));
        Assert.Equal("440.0 Hz", freq.Display);
        Assert.Equal(7, clap.Params[1].Steps);
        Assert.True(clap.Params[2].Bypass && clap.Params[2].Hidden);

        // VST3 gives its range and default in the parameter's own units separately from 0-1.
        var gain = ScannedPlugin.Parse(Vst3Line)!.Params!.Single();
        Assert.Equal((-60.0, 12.0, 0.0), (gain.Min, gain.Max, gain.Default));
        Assert.Equal("0.0 dB", gain.Display);
        Assert.Equal("-60 to 12 dB, starts at 0", gain.Range);

        // A plugin in a Waves-style shell, listed without loading it: no parameters yet.
        var shell = ScannedPlugin.Parse(ShellLine)!;
        Assert.Equal(("VST2", "MoB2", "L2 Stereo", "WaveShell1-VST 15.0"), (shell.Format, shell.Id, shell.Name, shell.Shell));
        Assert.Null(shell.Params);

        Assert.Null(ScannedPlugin.Parse(ErrorLine));
        Assert.Equal("32-bit plugin: this scanner is 64-bit", ScannedPlugin.FileError(ErrorLine));
        Assert.Null(ScannedPlugin.FileError(ClapLine));
        Assert.Null(ScannedPlugin.Parse("not json"));
    }

    [Fact]
    public async Task RunsTheScannerAndKeepsItsJsonOnly()
    {
        var scanner = new PluginScanner(FakeScanner("ok", [ClapLine, Vst3Line], stderr: "a chatty plugin"));
        var scan = await scanner.ScanAsync(Path.Combine(_root, "MO Probe.clap"));
        Assert.Equal("", scan.Error);
        Assert.Equal(["CLAP", "VST3"], scan.Plugins.Select(p => p.Format));
    }

    /// <summary>A moduleinfo.json as the VST3 SDK's moduleinfotool writes it: commas after the last item included.</summary>
    const string ModuleInfo = """
        {
          "Name": "Twin Pack",
          "Version": "2.1.0",
          "Factory Info": {
            "Vendor": "Example Audio",
            "URL": "https://example.com",
            "E-Mail": "mailto:help@example.com",
            "Flags": {
              "Classes Discardable": false,
              "Component Non Discardable": false,
              "Unicode": true,
            },
          },
          "Classes": [
            {
              "CID": "5c2f1e3a9b8d4c7e8f6a1b2c3d4e5f60",
              "Category": "Audio Module Class",
              "Name": "Twin Synth",
              "Vendor": "",
              "Version": "2.1.0",
              "SDKVersion": "VST 3.7.12",
              "Sub Categories": [
                "Instrument",
                "Synth",
              ],
              "Class Flags": 1,
              "Cardinality": 2147483647,
              "Snapshots": [
              ],
            },
            {
              "CID": "A1B2C3D4E5F60718293A4B5C6D7E8F90",
              "Category": "Component Controller Class",
              "Name": "Twin Synth Controller",
              "Vendor": "Example Audio",
              "Version": "2.1.0",
              "SDKVersion": "VST 3.7.12",
              "Class Flags": 0,
              "Cardinality": 2147483647,
              "Snapshots": [
              ],
            },
            {
              "CID": "0F1E2D3C4B5A69788796A5B4C3D2E1F0",
              "Category": "Audio Module Class",
              "Name": "Twin Delay",
              "Vendor": "Example Audio FX",
              "Version": "2.0.3",
              "SDKVersion": "VST 3.7.12",
              "Sub Categories": [
                "Fx",
                "Delay",
              ],
              "Class Flags": 1,
              "Cardinality": 2147483647,
              "Snapshots": [
              ],
            },
          ],
        }
        """;

    /// <summary>A plugin bundle (.vst3 unless <paramref name="name"/> says otherwise), with a library for this computer unless it's told not to.</summary>
    string Bundle(string name, string? moduleInfo, bool library = true)
    {
        var bundle = Path.Combine(_root, Path.HasExtension(name) ? name : name + ".vst3");
        if (library)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Vst3ModuleInfo.LibraryIn(bundle))!);
            File.WriteAllText(Vst3ModuleInfo.LibraryIn(bundle), "not really a library");
        }
        Directory.CreateDirectory(bundle);
        if (moduleInfo is not null)
        {
            Directory.CreateDirectory(Path.Combine(bundle, "Contents", "Resources"));
            File.WriteAllText(Vst3ModuleInfo.FileIn(bundle), moduleInfo);
        }
        return bundle;
    }

    [Fact]
    public void ReadsWhatAVst3SaysIsInsideIt()
    {
        var bundle = Bundle("Twin Pack", ModuleInfo);
        var plugins = Vst3ModuleInfo.Read(bundle)!;
        // The editor class is left out, as the scanner leaves it out.
        Assert.Equal(["Twin Synth", "Twin Delay"], plugins.Select(p => p.Name));
        var synth = plugins[0];
        Assert.Equal(("VST3", bundle, "5C2F1E3A9B8D4C7E8F6A1B2C3D4E5F60", "Example Audio", "2.1.0", "instrument"),
                     (synth.Format, synth.Path, synth.Id, synth.Vendor, synth.Version, synth.Kind));   // the module's vendor when the class has none
        Assert.Equal(["Instrument", "Synth"], synth.Features);
        Assert.Null(synth.Params);
        var delay = plugins[1];
        Assert.Equal(("0F1E2D3C4B5A69788796A5B4C3D2E1F0", "Example Audio FX", "2.0.3", "effect"), (delay.Id, delay.Vendor, delay.Version, delay.Kind));
        // Kept as the scanner's own line for it would be.
        Assert.StartsWith("{\"format\":\"VST3\",\"path\":", delay.Json);
        Assert.EndsWith("\"version\":\"2.0.3\",\"kind\":\"effect\",\"features\":[\"Fx\",\"Delay\"]}", delay.Json);

        // Anything else leaves it to the scanner.
        Assert.Null(Vst3ModuleInfo.Read(Bundle("Older", null)));
        Assert.Null(Vst3ModuleInfo.Read(Bundle("Broken", ModuleInfo[..^40])));
        Assert.Null(Vst3ModuleInfo.Read(Bundle("Only an editor", ModuleInfo.Replace("Audio Module Class", "Something Else"))));
        Assert.Null(Vst3ModuleInfo.Read(Bundle("Bad ID", ModuleInfo.Replace("\"CID\": \"", "\"CID\": \"x"))));
        // Listing only the one it can read would lose the other for good.
        Assert.Null(Vst3ModuleInfo.Read(Bundle("One bad ID", ModuleInfo.Replace("0F1E2D3C4B5A69788796A5B4C3D2E1F0", "0F1E2D3C-4B5A-6978-8796-A5B4C3D2E1F0"))));
        // Built for another computer, or its library deleted: the scanner says why it can't be used.
        Assert.Null(Vst3ModuleInfo.Read(Bundle("Not for here", ModuleInfo, library: false)));
        Assert.Null(Vst3ModuleInfo.Read(Bundle("Twin Pack.vst", ModuleInfo)));   // a Mac VST2 bundle: mo-scan reads its own ID
        var oldCodePage = Bundle("Not UTF-8", ModuleInfo);   // a name saved as Latin-1, not UTF-8
        File.WriteAllBytes(Vst3ModuleInfo.FileIn(oldCodePage), System.Text.Encoding.Latin1.GetBytes(ModuleInfo.Replace("Twin Delay", "Twin Délay")));
        Assert.Null(Vst3ModuleInfo.Read(oldCodePage));
        var file = Path.Combine(_root, "Single file.vst3");   // an old-style VST3 is just the library
        File.WriteAllText(file, "MZ");
        Assert.Null(Vst3ModuleInfo.Read(file));
    }

    [Fact]
    public async Task ListsAVst3FromItsModuleInfoWithoutRunningTheScanner()
    {
        var bundle = Bundle("Twin Pack", ModuleInfo);
        var scanner = new PluginScanner(FakeScanner("fail", [ErrorLine], exitCode: 2));   // says so if it's run
        var scan = await scanner.ScanAsync(bundle);
        Assert.Equal(("", 2), (scan.Error, scan.Plugins.Count));
        Assert.Equal(["Twin Delay"], (await scanner.ScanAsync(bundle, only: "0f1e2d3c4b5a69788796a5b4c3d2e1f0")).Plugins.Select(p => p.Name));

        // Its parameters need the plugin loaded, as does one the file doesn't list.
        Assert.Equal("32-bit plugin: this scanner is 64-bit", (await scanner.ScanAsync(bundle, parameters: true)).Error);
        Assert.Equal("32-bit plugin: this scanner is 64-bit", (await scanner.ScanAsync(bundle, only: "00000000000000000000000000000000")).Error);
    }

    [Fact]
    public async Task SaysWhenAPluginCrashesTheScanner()
    {
        var scanner = new PluginScanner(FakeScanner("crash", [], exitCode: 3, stderr: "boom"));
        var scan = await scanner.ScanAsync(Path.Combine(_root, "Crashy.vst3"));
        Assert.Empty(scan.Plugins);
        Assert.Contains("crashed", scan.Error);
        Assert.Contains("boom", scan.Error);

        // The scanner's own error line wins over its exit code.
        scanner = new PluginScanner(FakeScanner("fail", [ErrorLine], exitCode: 2));
        Assert.Equal("32-bit plugin: this scanner is 64-bit", (await scanner.ScanAsync(@"C:\VST\Old.dll")).Error);
    }

    [Fact]
    public async Task RunsTheScannerBelowNormalPriority()
    {
        // A stand-in that waits a moment, then gives its own niceness (10 is below normal) as its plugin's name. On
        // Windows a script can't read its priority class, and the app sets it the same way there.
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_root, "nice.sh");
        File.WriteAllLines(path, [
            "#!/bin/sh",
            "sleep 1",
            """printf '{"format":"CLAP","path":"/p/Nice.clap","id":"nice","name":"%s"}\n' "$(ps -o ni= -p $$ | tr -d ' ')" """.TrimEnd(),
        ]);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var scan = await new PluginScanner(path).ScanAsync(Path.Combine(_root, "Nice.clap"));
        Assert.Equal("", scan.Error);
        var niceness = scan.Plugins.Single().Name;
        Assert.True(int.TryParse(niceness, out var n) && n >= 10, $"the scanner ran at niceness “{niceness}”");
    }

    [Fact]
    public async Task StopsAScanThatHangs()
    {
        var scanner = new PluginScanner(FakeScanner("hang", [], hang: true)) { Timeout = TimeSpan.FromSeconds(1) };
        var clock = Stopwatch.StartNew();
        var scan = await scanner.ScanAsync(Path.Combine(_root, "Stuck.clap"));
        Assert.Contains("didn't finish", scan.Error);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task StopAllEndsTheScannersRunningNow()
    {
        var scanner = new PluginScanner(FakeScanner("hang", [], hang: true));   // 30 seconds before it would give up
        var clock = Stopwatch.StartNew();
        var scan = scanner.ScanAsync(Path.Combine(_root, "Stuck.clap"));
        while (!scan.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            scanner.StopAll();   // as the app closes (it may not have started yet)
            await Task.Delay(50);
        }
        Assert.True(scan.IsCompleted, $"still running after {clock.Elapsed}");
        Assert.Contains("crashed", (await scan).Error);
    }

    [Fact]
    public async Task ReadsEachPluginFileOnceUntilItChanges()
    {
        var folder = Path.Combine(_root, "VST3");
        var bundle = Path.Combine(folder, "WaveShell1-VST3 15.0.vst3", "Contents", "x86_64-win");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "WaveShell1-VST3 15.0.vst3"), "code");
        File.WriteAllText(Path.Combine(folder, "Serum.clap"), "code");
        File.WriteAllText(Path.Combine(folder, "Diva.component"), "an Audio Unit: not read by the scanner");

        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        db.SetList(Scanner.PluginFoldersKey, [folder]);
        var lib = new Scanner(db);
        lib.ScanInstalledPlugins(out var toRead);
        Assert.Equal(["Serum.clap", "WaveShell1-VST3 15.0.vst3"], toRead.Select(Path.GetFileName).Order());

        // The shell holds plugins named nothing like its file; once read, they count as installed.
        var waves = Path.Combine(folder, "WaveShell1-VST3 15.0.vst3");
        var scanner = new PluginScanner(FakeScanner("shell", [
            ShellLine.Replace("C:\\\\VST\\\\WaveShell1-VST 15.0.dll", waves.Replace("\\", "\\\\")),
            ShellLine.Replace("C:\\\\VST\\\\WaveShell1-VST 15.0.dll", waves.Replace("\\", "\\\\")).Replace("MoB2", "MoC3").Replace("L2 Stereo", "CLA-76 Stereo"),
        ]));
        await lib.ReadPluginFilesAsync(scanner, toRead.Where(p => p.EndsWith(".vst3")).ToList());
        db.SavePluginScan(new PluginFileScan(Path.Combine(folder, "Serum.clap"), [], "no clap_entry"), PluginScanner.Stamp(Path.Combine(folder, "Serum.clap")));

        lib.ScanInstalledPlugins(out toRead);
        Assert.Empty(toRead);
        Assert.Contains("CLA-76 Stereo", db.InstalledNames());
        Assert.Equal((2, 2, 1), db.PluginScanCounts());

        // A project's name with the vendor in front still finds it.
        var found = db.ScannedPluginsNamed("Waves L2 Stereo");
        Assert.Equal(["MoB2"], found.Select(p => p.Id));

        // Reading a plugin's parameters replaces its stored line.
        db.SaveScannedPlugin(found[0] with { Json = ClapLine.Replace("org.musicorganiser.probe.effect", "MoB2") });
        Assert.NotNull(db.ScannedPluginsNamed("L2 Stereo")[0].Params);

        // A changed file is read again; a removed one is forgotten.
        File.AppendAllText(Path.Combine(folder, "Serum.clap"), " updated");
        Directory.Delete(Path.Combine(folder, "WaveShell1-VST3 15.0.vst3"), true);
        lib.ScanInstalledPlugins(out toRead);
        Assert.Equal(["Serum.clap"], toRead.Select(Path.GetFileName));
        Assert.Empty(db.ScannedPluginsNamed("L2 Stereo"));
        Assert.DoesNotContain("CLA-76 Stereo", db.InstalledNames());
    }

    [Fact]
    public async Task TheRealScannerRunsWhenItsBuilt()
    {
        // mo-scan is built and copied next to the tests on a computer with CMake and a C++ compiler
        // (not on CI, apart from the Windows build). Elsewhere there's nothing to check here.
        if (PluginScanner.Find() is not { } exe) return;
        var notAPlugin = Path.Combine(_root, "Not a plugin.clap");
        File.WriteAllText(notAPlugin, "not a plugin");
        var scan = await new PluginScanner(exe).ScanAsync(notAPlugin);
        Assert.Empty(scan.Plugins);
        Assert.NotEqual("", scan.Error);
        Assert.DoesNotContain("crashed", scan.Error);
    }

    [Fact]
    public async Task ReadsEveryPluginsParametersOnce()
    {
        var file = Path.Combine(_root, "Plugins.clap");
        string Line(string id, string name, object? parameters = null, string? error = null) => System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["format"] = "CLAP", ["path"] = file, ["id"] = id, ["name"] = name, ["vendor"] = "Tests", ["params"] = parameters, ["error"] = error }
                .Where(kv => kv.Value is not null).ToDictionary());
        object[] Knobs(params string[] names) => names.Select((n, i) => (object)new { id = i, name = n, text = "0.5" }).ToArray();

        using var db = new LibraryDb(Path.Combine(_root, "library.db"));
        db.SavePluginScan(new PluginFileScan(file, [.. new[] { Line("good", "Good Synth"), Line("broken", "Broken Delay"), Line("crashes", "Crashy Verb"),
                                                               Line("done", "Done EQ", Knobs("Gain")) }.Select(l => ScannedPlugin.Parse(l)!)]),
                          PluginScanner.Stamp(file));
        Assert.Equal((1, 3), db.ParameterCounts());

        // The scanner, asked for one plugin by ID: one loads, one can't be created, one crashes it.
        var asked = Directory.CreateDirectory(Path.Combine(_root, "asked")).FullName;
        int Asked(string id) => File.Exists(Path.Combine(asked, id)) ? File.ReadAllLines(Path.Combine(asked, id)).Length : 0;
        var scanner = new PluginScanner(FakeScannerById(asked, new()
        {
            ["good"] = [Line("good", "Good Synth", Knobs("Cutoff", "Resonance"))],
            ["broken"] = [Line("broken", "Broken Delay", error: "couldn't create the plugin")],
        }, crashes: "crashes"));
        var lib = new Scanner(db);
        var reports = new Reports();
        Assert.Equal((1, 2), await lib.ReadAllParametersAsync(scanner, reports));
        Assert.Equal(["broken", "crashes", "good"], Directory.GetFiles(asked).Select(Path.GetFileName).Order());
        Assert.All(new[] { "broken", "crashes", "good" }, id => Assert.Equal(1, Asked(id)));
        Assert.All(reports.Seen, r => Assert.Equal(3, r.Total));
        Assert.Contains(reports.Seen, r => r.Done == 3);

        Assert.Equal(["Cutoff", "Resonance"], db.ScannedPluginsNamed("Good Synth").Single().Params!.Select(p => p.Name));
        var broken = db.ScannedPluginsNamed("Broken Delay").Single();
        Assert.Equal(("couldn't create the plugin", "Tests"), (broken.Error, broken.Vendor));
        Assert.Null(broken.Params);
        Assert.Contains("crashed", db.ScannedPluginsNamed("Crashy Verb").Single().Error);
        Assert.Equal((2, 0), db.ParameterCounts());

        // A try that failed doesn't wipe parameters another try read meanwhile.
        db.SaveScannedPlugin(ScannedPlugin.Parse(Line("good", "Good Synth", error: "didn't finish within 30 seconds"))! with { Path = file });
        Assert.Equal(["Cutoff", "Resonance"], db.ScannedPluginsNamed("Good Synth").Single().Params!.Select(p => p.Name));

        // Next time there's nothing left to read: the ones that failed wait for Read parameters on each.
        Assert.Equal((0, 0), await lib.ReadAllParametersAsync(scanner));
        Assert.Equal(3, Directory.GetFiles(asked).Length);
        Assert.All(new[] { "broken", "crashes", "good" }, id => Assert.Equal(1, Asked(id)));
        var again = await scanner.ReadParametersAsync(broken);
        Assert.Equal("couldn't create the plugin", again.Error);
        Assert.Equal(2, Asked("broken"));
    }

    [Fact]
    public void SaysWhyTheScannerWasntBuilt()
    {
        // The app runs from src/<project>/bin/Debug/<framework> under the source folders; the build's log is in native/out/app.
        var app = Directory.CreateDirectory(Path.Combine(_root, "src", "MusicOrganiser.App", "bin", "Debug", "net10.0-windows")).FullName;
        var log = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "native", "out", "app")).FullName, "build.log");
        Assert.Null(PluginScanner.BuildProblem(app));

        File.WriteAllText(log, "Visual Studio has no C++ compiler. In Visual Studio Installer, add the \"Desktop development with C++\" workload.\r\n");
        Assert.Equal("Visual Studio has no C++ compiler. In Visual Studio Installer, add the \"Desktop development with C++\" workload.",
                     PluginScanner.BuildProblem(app));

        File.WriteAllLines(log, ["Using C:\\Program Files\\Microsoft Visual Studio\\18\\Community", "[1/3] Building CXX object mo-scan\\CMakeFiles\\mo-scan.dir\\src\\main.cpp.obj",
                                 "C:\\repo\\native\\mo-scan\\src\\main.cpp(12): error C2065: 'x': undeclared identifier", "ninja: build stopped: subcommand failed."]);
        Assert.Equal("C:\\repo\\native\\mo-scan\\src\\main.cpp(12): error C2065: 'x': undeclared identifier", PluginScanner.BuildProblem(app));

        // A build that worked leaves a log too.
        File.WriteAllLines(log, ["Using C:\\Program Files\\Microsoft Visual Studio\\18\\Community", "[3/3] Linking CXX executable bin\\mo-scan.exe"]);
        Assert.Null(PluginScanner.BuildProblem(app));
    }

    /// <summary>Progress as it's reported, without waiting for a UI thread.</summary>
    sealed class Reports : IProgress<ScanProgress>
    {
        readonly List<ScanProgress> _seen = [];
        public IReadOnlyList<ScanProgress> Seen { get { lock (_seen) return [.. _seen]; } }
        public void Report(ScanProgress value) { lock (_seen) _seen.Add(value); }
    }

    /// <summary>
    /// A stand-in for mo-scan run with --only &lt;id&gt;: it adds a line to a file named after each ID it's asked for in
    /// <paramref name="asked"/> (one file each, as cmd can't append to one file from several at once), prints that
    /// plugin's lines, and for <paramref name="crashes"/> exits as if the plugin crashed it.
    /// </summary>
    string FakeScannerById(string asked, Dictionary<string, string[]> lines, string crashes)
    {
        string path;
        if (OperatingSystem.IsWindows())
        {
            path = Path.Combine(_root, "by-id.cmd");
            var cmd = new List<string> { "@echo off", $">>\"{asked}\\%~2\" echo %~2", $"if \"%~2\"==\"{crashes}\" exit /b 3" };
            var ids = lines.Keys.ToList();
            cmd.AddRange(ids.Select((id, i) => $"if \"%~2\"==\"{id}\" goto id{i}"));
            cmd.Add("exit /b 0");
            for (var i = 0; i < ids.Count; i++)
            {
                cmd.Add($":id{i}");
                cmd.AddRange(lines[ids[i]].Select(l => "echo " + l.Replace("%", "%%").Replace("^", "^^").Replace("&", "^&").Replace("<", "^<").Replace(">", "^>").Replace("|", "^|")));
                cmd.Add("exit /b 0");
            }
            File.WriteAllLines(path, cmd);
        }
        else
        {
            path = Path.Combine(_root, "by-id.sh");
            var sh = new List<string> { "#!/bin/sh", $"printf '%s\\n' \"$2\" >> '{asked}/'\"$2\"", $"if [ \"$2\" = '{crashes}' ]; then exit 3; fi", "case \"$2\" in" };
            foreach (var (id, idLines) in lines)
                sh.Add($"  '{id}') " + string.Join("; ", idLines.Select(l => "printf '%s\\n' '" + l.Replace("'", "'\\''") + "'")) + " ;;");
            sh.Add("esac");
            File.WriteAllLines(path, sh);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    /// <summary>A stand-in for mo-scan: a script that prints the given lines (or hangs, or fails).</summary>
    string FakeScanner(string name, string[] lines, int exitCode = 0, string stderr = "", bool hang = false)
    {
        string path;
        if (OperatingSystem.IsWindows())
        {
            path = Path.Combine(_root, name + ".cmd");
            var cmd = new List<string> { "@echo off" };
            if (stderr.Length > 0) cmd.Add($"echo {stderr} 1>&2");
            cmd.AddRange(lines.Select(l => "echo " + l.Replace("%", "%%").Replace("^", "^^").Replace("&", "^&").Replace("<", "^<").Replace(">", "^>").Replace("|", "^|")));
            if (hang) cmd.Add("ping -n 60 127.0.0.1 >nul");
            cmd.Add($"exit /b {exitCode}");
            File.WriteAllLines(path, cmd);
        }
        else
        {
            path = Path.Combine(_root, name + ".sh");
            var sh = new List<string> { "#!/bin/sh" };
            if (stderr.Length > 0) sh.Add($"echo '{stderr}' >&2");
            sh.AddRange(lines.Select(l => "printf '%s\\n' '" + l.Replace("'", "'\\''") + "'"));
            if (hang) sh.Add("sleep 60");
            sh.Add($"exit {exitCode}");
            File.WriteAllLines(path, sh);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }
}
