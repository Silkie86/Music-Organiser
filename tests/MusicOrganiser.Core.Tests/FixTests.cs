using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Plugins.Serum;
using Xunit;

namespace MusicOrganiser.Core.Tests;

/// <summary>Fix: a copy of a project with its missing samples relinked, next to the untouched original.</summary>
public class FixTests : IDisposable
{
    readonly string _root = Fixtures.TempDir();
    readonly string _lib;
    readonly LibraryDb _db;

    public FixTests()
    {
        _lib = Path.Combine(_root, "Sample Library");
        Directory.CreateDirectory(Path.Combine(_lib, "Drums"));
        File.WriteAllBytes(Path.Combine(_lib, "Drums", "Kick 01.wav"), [0]);
        File.WriteAllBytes(Path.Combine(_lib, "Drums", "Snare 808 v2.wav"), [0]);
        _db = new LibraryDb(Path.Combine(_root, "library.db"));
        _db.AddRoot(Path.Combine(_root, "Projects"));
        _db.SetList(Scanner.SampleFoldersKey, [_lib]);
    }

    public void Dispose() => _db.Dispose();

    ProjectDetail Scan(string daw)
    {
        var scanner = new Scanner(_db);
        scanner.ScanSampleFolders();
        scanner.ScanProjects();
        return _db.Project(_db.Projects().First(p => p.Daw == daw).Id)!;
    }

    [Fact]
    public void An_FL_project_gets_a_fixed_copy_with_the_sample_relinked_and_the_original_untouched()
    {
        var dir = Path.Combine(_root, "Projects");
        Directory.CreateDirectory(dir);
        var path = Fixtures.FlStudioProject(dir);
        var before = File.ReadAllBytes(path);

        var plan = ProjectFixer.Plan(_db, Scan("FL Studio"));
        var kick = plan.Single(l => l.IsSample);
        Assert.Equal((FixAction.Relink, "Kick 01.wav"), (kick.Action, kick.Name));
        Assert.Equal(Path.Combine(_lib, "Drums", "Kick 01.wav"), kick.Use);

        var (fixedPath, changed, _, _) = ProjectFixer.Write(path, plan);
        Assert.Equal(1, changed);
        Assert.Equal(Path.Combine(dir, "Night Drive (fixed).flp"), fixedPath);
        Assert.Equal(before, File.ReadAllBytes(path));

        var info = new FlStudioReader().Read(fixedPath);
        Assert.Equal([kick.Use!], info.Samples);
        Assert.Equal(new FlStudioReader().Read(path).Plugins.Select(p => p.Name), info.Plugins.Select(p => p.Name));

        // Every other event comes through as it was.
        var a = FlStudioReader.ReadEvents(before).Events;
        var b = FlStudioReader.ReadEvents(File.ReadAllBytes(fixedPath)).Events;
        Assert.Equal(a.Select(e => e.Id), b.Select(e => e.Id));
        Assert.All(a.Zip(b).Where(p => p.First.Id != 196), p => Assert.Equal(p.First.Payload.ToArray(), p.Second.Payload.ToArray()));

        // A second fix doesn't overwrite the first.
        Assert.Equal(Path.Combine(dir, "Night Drive (fixed 2).flp"), ProjectFixer.FixedPath(path));
        // Fixing the fixed copy again makes the next copy of the same song, not "Night Drive (fixed) (fixed)".
        Assert.Equal(Path.Combine(dir, "Night Drive (fixed 2).flp"), ProjectFixer.FixedPath(fixedPath));
    }

    [Fact]
    public void Of_several_copies_the_one_in_the_most_similar_folders_is_used_and_the_others_can_be_picked()
    {
        // Same name in another pack, listed first by the index: the folder the project looked in ("Drums") decides.
        Directory.CreateDirectory(Path.Combine(_lib, "Aardvark Pack"));
        File.WriteAllBytes(Path.Combine(_lib, "Aardvark Pack", "Kick 01.wav"), [0]);
        var dir = Path.Combine(_root, "Projects");
        Directory.CreateDirectory(dir);
        Fixtures.FlStudioProject(dir);   // looks for C:\Samples\Drums\Kick 01.wav

        var kick = ProjectFixer.Plan(_db, Scan("FL Studio")).Single(l => l.IsSample);
        Assert.Equal(FixAction.Relink, kick.Action);
        Assert.Equal(Path.Combine(_lib, "Drums", "Kick 01.wav"), kick.Use);
        Assert.Equal([Path.Combine(_lib, "Drums", "Kick 01.wav"), Path.Combine(_lib, "Aardvark Pack", "Kick 01.wav")], kick.Choices);
        Assert.Contains("most similar folders", kick.Note);
    }

    [Fact]
    public void A_weaker_name_is_suggested_when_it_sits_in_the_same_pack()
    {
        Directory.CreateDirectory(Path.Combine(_lib, "Soundscan Vol 46 - 70s Breakbeats"));
        File.WriteAllBytes(Path.Combine(_lib, "Soundscan Vol 46 - 70s Breakbeats", "Break 12 Funky.wav"), [0]);
        File.WriteAllBytes(Path.Combine(_lib, "Drums", "Break 12 Funky.wav.bak.wav"), [0]);
        new Scanner(_db).ScanSampleFolders();

        var matches = MatchFinder.ForSample(_db, @"C:\Program Files\Image-Line\FL Studio\Data\Patches\Packs\Soundscan 46 - 70's Breakbeats\Break 12.wav");
        Assert.EndsWith(Path.Combine("Soundscan Vol 46 - 70s Breakbeats", "Break 12 Funky.wav"), matches[0].Candidate);
        Assert.True(matches[0].Folders > 0.9);
        Assert.Equal("Similar name, in matching folders", matches[0].Reason);
    }

    [Fact]
    public void A_Bitwig_project_gets_a_ticked_suggestion_relinked()
    {
        var dir = Path.Combine(_root, "Projects");
        Directory.CreateDirectory(dir);
        var path = Fixtures.BitwigProject(dir);

        var plan = ProjectFixer.Plan(_db, Scan("Bitwig Studio"));
        var snare = plan.Single(l => l.IsSample && l.Name == "Snare 808.wav");
        // Only a similar name: offered, not used unless it's ticked.
        Assert.Equal(FixAction.Suggestion, snare.Action);
        Assert.EndsWith("Snare 808 v2.wav", snare.Use);

        var (fixedPath, changed, _, _) = ProjectFixer.Write(path, [snare]);
        Assert.Equal(1, changed);
        var samples = new BitwigReader().Read(fixedPath).Samples;
        Assert.Contains(snare.Use, samples);
        Assert.DoesNotContain(@"D:\Splice\Snare 808.wav", samples);
    }

    [Fact]
    public void Plugins_other_than_Serum_are_listed_as_swaps_to_make_by_hand_never_written()
    {
        var dir = Path.Combine(_root, "Projects");
        Directory.CreateDirectory(dir);
        var path = Fixtures.FlStudioProject(dir);
        _db.ReplaceInstalledPlugins(["Serum 2", "FabFilter Pro-Q 4"]);

        var plan = ProjectFixer.Plan(_db, Scan("FL Studio"));
        // Serum is the one the app can swap itself, with its sound translated.
        var serum = plan.Single(l => l.Name == "Serum");
        Assert.Equal((FixAction.Relink, "Serum 2", true), (serum.Action, serum.Use, serum.IsSwap));
        var proq = plan.Single(l => l.Name == "Pro-Q 3");
        Assert.Equal((FixAction.Suggestion, "Pro-Q 3", "FabFilter Pro-Q 4"), (proq.Action, proq.Name, proq.Use));
        Assert.False(proq.CanWrite);
        Assert.Throws<InvalidOperationException>(() => ProjectFixer.Write(path, [proq]));
        Assert.False(File.Exists(Path.Combine(dir, "Night Drive (fixed).flp")));
    }

    [Theory]
    [InlineData("VST2")]
    [InlineData("VST3")]
    public void A_Bitwig_projects_Serum_is_swapped_for_Serum_2_with_its_sound_translated(string format)
    {
        // A Bitwig project with Serum on the track whose cutoff is automated, made the way the FL Studio converter does.
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25Automation(Fixtures.TempDir()));
        var channels = song.Channels.ToList();
        var chunk = SerumTests.Serum1();
        const string serum1Vst3 = "56535458667358736572756D00000000";
        channels[1] = channels[1] with
        {
            Instrument = format == "VST2"
                ? channels[1].Instrument! with
                {
                    DeviceId = "1483109208",   // 'XfsX'
                    State = new PresetBlob("Serum", "Serum", ".fxp", Fxp.WrapChunk(chunk, "Serum", 0x58667358)),
                }
                : new FlPlugin("Serum", "Xfer Records", "VST3", serum1Vst3,
                    new PresetBlob("Serum", "Serum", ".vstpreset", VstPreset.Build(serum1Vst3, chunk, [])),
                    @"C:\Program Files\Common Files\VST3\Xfer\Serum.vst3\Contents\x86_64-win\Serum.vst3"),
        };
        var dir = Path.Combine(_root, "Projects", "Serum Song");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Serum Song.bwproject");
        File.WriteAllBytes(path, BitwigProject.FromFl(song with { Channels = channels }, Fixtures.BitwigTemplate()).Bytes);
        var before = File.ReadAllBytes(path);
        _db.ReplaceInstalledPlugins(["Serum 2"]);

        // Serum isn't installed, so swapping in Serum 2 is the fix, and it's ticked.
        var swap = ProjectFixer.Plan(_db, Scan("Bitwig Studio")).Single(l => l.IsSwap);
        Assert.Equal(("Serum", "Serum 2", FixAction.Relink, true), (swap.Name, swap.Use, swap.Action, swap.CanWrite));

        var result = ProjectFixer.Write(path, [swap], _ => null);
        Assert.Equal((1, 0), (result.Swapped, result.Relinked));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Contains(result.Notes, n => n.StartsWith("Serum: Serum swapped for Serum 2, with its sound translated"));
        // The converter carries the cutoff's automation for the VST2 only (the VST3's setting ids aren't known).
        if (format == "VST2") Assert.Contains(result.Notes, n => n.StartsWith("1 automation lane of Serum's knobs left out"));

        var plugins = new BitwigReader().Read(result.Path).Plugins.Select(p => p.Name).ToList();
        Assert.Contains("Serum 2", plugins);
        Assert.DoesNotContain("Serum", plugins);

        // The device is Serum 2's VST3, and its settings are the translated sound.
        var doc = BitwigFormat.Decode(File.ReadAllBytes(result.Path));
        var serum2 = doc.Objects.Single(o => o.Str(0x9a) == "Serum 2");
        Assert.Equal((0x728, 0x729, "56534558667350736572756D20320000"), (serum2.Class, serum2.Parent!.Class, serum2.Str(0xce4)));
        Assert.DoesNotContain(doc.Objects, o => o.Class == 0x65 && o.Obj(0xec) == serum2.Parent);
        using var zip = doc.OpenZip()!;
        using var file = new MemoryStream();
        using (var entry = zip.GetEntry($"plugin-states/{serum2.Str(0xbe5)}")!.Open()) entry.CopyTo(file);
        var sound = Serum2State.Read(VstPreset.Component(file.ToArray()))!;
        Assert.Equal(0.4809 * 0.4809, sound.Settings["Oscillator0"]!["plainParams"]!["kParamVolume"]!.GetValue<double>(), 4);
    }

    [Fact]
    public void An_FL_projects_Serum_is_swapped_for_Serum_2_with_its_sound_translated()
    {
        var path = Fixtures.FlStudio25Project(Path.Combine(_root, "Projects"), serumChunk: SerumTests.Serum1());
        var before = File.ReadAllBytes(path);
        _db.ReplaceInstalledPlugins(["Serum2", "Arcade", "Predator 2"]);

        var swap = ProjectFixer.Plan(_db, Scan("FL Studio")).Single(l => l.IsSwap);
        Assert.Equal(("Serum", FixAction.Relink), (swap.Name, swap.Action));

        var serum2User = Path.Combine(_root, "Serum 2 Presets", "Presets", "User");
        var result = ProjectFixer.Write(path, [swap], _ => null, serum2User);
        Assert.Equal((1, 0), (result.Swapped, result.Relinked));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.StartsWith("Lead: Serum swapped for Serum 2, with its sound translated", result.Notes[0]);

        // Serum's own sound is saved where Serum 2's browser finds it, to load by hand for the exact sound.
        var song1 = Path.GetFileNameWithoutExtension(path);
        Assert.Equal($"Serum's own sound is also saved in Serum 2's browser, under User › From Serum 1 › {song1}: load it there for the exact Serum 1 sound.",
                     result.Notes[1]);
        var fxp = File.ReadAllBytes(Path.Combine(serum2User, "From Serum 1", song1, "Lead.fxp"));
        Assert.Equal(Serum1State.Read(SerumTests.Serum1())!.Knobs, Serum1State.Read(fxp)!.Knobs);

        // The channel now holds Serum 2's VST3, as FL saves it, with the translated sound; the others are as they were.
        var song = Readers.FlStudio.ReadSong(result.Path);
        var lead = song.Channels.Single(c => c.Name == "Lead").Instrument!;
        Assert.Equal(("Serum2", "VST3", "56534558667350736572756D20320000"), (lead.Name, lead.Format, lead.DeviceId));
        var sound = Serum2State.Read(VstPreset.Component(lead.State!.Data))!;
        Assert.Equal(0.4809 * 0.4809, sound.Settings["Oscillator0"]!["plainParams"]!["kParamVolume"]!.GetValue<double>(), 4);
        var old = Readers.FlStudio.ReadSong(path);
        Assert.Equal(old.Channels.Where(c => c.Name != "Lead").Select(c => c.Instrument?.State?.Data),
                     song.Channels.Where(c => c.Name != "Lead").Select(c => c.Instrument?.State?.Data));
        Assert.Equal(FlStudioReader.ReadEvents(before).Events.Select(e => e.Id),
                     FlStudioReader.ReadEvents(File.ReadAllBytes(result.Path)).Events.Select(e => e.Id));
    }

    [Fact]
    public void Without_sample_folders_it_says_where_to_add_them()
    {
        _db.SetList(Scanner.SampleFoldersKey, []);
        var dir = Path.Combine(_root, "Projects");
        Directory.CreateDirectory(dir);
        Fixtures.FlStudioProject(dir);
        var line = ProjectFixer.Plan(_db, Scan("FL Studio")).Single(l => l.IsSample);
        Assert.Equal(FixAction.StillMissing, line.Action);
        Assert.Contains("Settings", line.Note);
    }

    [Fact]
    public void Serum_1_sounds_of_two_songs_with_one_name_dont_overwrite_each_other()
    {
        // Code review U9 (19 Oct): two "beat" projects in different folders, and two channels both called Serum.
        var user = Fixtures.TempDir();
        string Folder(string name) => Path.Combine(user, "From Serum 1", name);
        Assert.NotNull(Serum1To2.KeepOriginals("beat", [("Serum", [1]), ("Serum", [2])], user));
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(Folder("beat"), "Serum.fxp")));
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(Folder("beat"), "Serum (2).fxp")));

        // The same sounds again (the same song fixed twice) go where they are; another song's go next to them.
        Serum1To2.KeepOriginals("beat", [("Serum", [1]), ("Serum", [2])], user);
        Assert.False(Directory.Exists(Folder("beat (2)")));
        var note = Serum1To2.KeepOriginals("beat", [("Lead", [3])], user);
        Assert.Equal([3], File.ReadAllBytes(Path.Combine(Folder("beat (2)"), "Lead.fxp")));
        Assert.Contains("From Serum 1 › beat (2)", note);
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(Folder("beat"), "Serum.fxp")));
    }
}
