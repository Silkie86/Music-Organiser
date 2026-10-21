using System.Buffers.Binary;
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
    }

    [Fact]
    public void ReadsMutedPlaylistTracksAndSwitchedOffChannels()
    {
        var path = Fixtures.FlStudio25MutedTracks(Fixtures.TempDir());
        var song = Readers.FlStudio.ReadSong(path);

        // Tracks 2 and 4 are muted in the first arrangement (the second's muted track 1 doesn't count), so what's on them
        // doesn't play: the Verse pattern in bar 2, the Acapella clips and the Pad. The clips themselves aren't muted.
        Assert.Equal([new FlTrack(2, "Acapellas"), new FlTrack(4, "")], song.MutedTracks);
        var snare = song.Channels.Single(c => c.Name == "Snare");
        Assert.Equal([(0L, false, false, 1), (384L, false, true, 2)], snare.Clips.Select(c => (c.Start, c.Muted, c.OnMutedTrack, c.Track)));
        Assert.Equal([0L], snare.Notes.Select(n => n.Start));
        Assert.Empty(song.Channels.Single(c => c.Name == "Pad").Notes);
        Assert.Equal([(false, true, 2), (false, true, 4)],
                     song.Channels.Single(c => c.Name == "Acapella").AudioClips.Select(c => (c.Muted, c.OnMutedTrack, c.Track)));
        Assert.Equal([(false, false, 1), (false, false, 3)],
                     song.Channels.Single(c => c.Name == "Vox").AudioClips.Select(c => (c.Muted, c.OnMutedTrack, c.Track)));
        var midi = Readers.FlStudio.ExtractChannels(path);
        Assert.Equal([0L], midi.Single(c => c.Name == "Snare").Notes.Select(n => n.Start));
        Assert.DoesNotContain(midi, c => c.Name == "Pad");

        // The hat and the vox are switched off in the Channel Rack (the project's own event 0 before them isn't a channel's).
        Assert.Equal([("Snare", true), ("Hat", false), ("Acapella", true), ("Vox", false), ("Pad", true)],
                     song.Channels.Select(c => (c.Name, c.Enabled)));
    }

    [Fact]
    public void LeavesOutAutomationClipsSwitchedOffInTheChannelRack()
    {
        Assert.Equal(["Snare - Channel volume", "Serum - Cutoff"],
                     Readers.FlStudio.ReadSong(Fixtures.FlStudio25Automation(Fixtures.TempDir())).Automation.Select(a => a.Name));
        Assert.Equal(["Serum - Cutoff"],
                     Readers.FlStudio.ReadSong(Fixtures.FlStudio25Automation(Fixtures.TempDir(), volumeOff: true)).Automation.Select(a => a.Name));
    }

    [Fact]
    public void AutomatedOnOffMutesWhatPlaysWhileTheChannelIsOff()
    {
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25SwitchedAndMixer(Fixtures.TempDir()));

        // The on/off holds each point's value until the next, and the points just past the clip's end (as FL saves its
        // sums of point gaps) count as at its end, jump and all: off 0-4, on 4-8, off 8-12, then on.
        var onOff = song.Automation.Single(a => a.Name == "Bass - Mute / solo");
        Assert.Equal((0, FlControl.OnOff), (onOff.Channel, onOff.Control));
        Assert.Equal([(0.0, 0.0), (4.0, 1.0), (8.0, 0.0), (12.0, 0.0), (12.0, 0.97)], onOff.Points.Select(p => (p.Beat, p.Value)));

        // The bass is switched off in the Channel Rack, but that's only where the automation left it: it plays while it's
        // on. The clip at beats 0-4 is off all the way through, so it's muted; at 4-8 it plays; at 10-14 only its first
        // note (beat 10) starts while it's off.
        var bass = song.Channels.Single(c => c.Name == "Bass");
        Assert.True(bass.Enabled);
        Assert.Equal(["Bass - Mute / solo"], bass.SwitchedBy);
        Assert.Equal([(0L, true), (384L, false), (960L, false)], bass.Clips.Select(c => (c.Start, c.Muted)));
        Assert.Equal([true, false], bass.Clips[2].Notes.Select(n => n.Muted));
        Assert.Equal([384L, 576L, 1152L], bass.Notes.Select(n => n.Start));

        // The vox is off from beat 2: its clip at 0-4 plays part of the way, the one at 4-6 is muted.
        var vox = song.Channels.Single(c => c.Name == "Vox");
        Assert.Equal([(false, true), (true, false)], vox.AudioClips.Select(c => (c.Muted, c.PartlyOff)));
    }

    [Fact]
    public void FollowsEveryClipThatSwitchesAChannelAndKeepsAutomatedEffectsOn()
    {
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25SwitchedAndMixer(Fixtures.TempDir(), more: true));

        // The first clip leaves the bass on from beat 12; the second switches it off at 16 and on again at 20.
        var bass = song.Channels.Single(c => c.Name == "Bass");
        Assert.Equal(["Bass - Mute / solo", "Bass - Mute / solo #2"], bass.SwitchedBy);
        Assert.Equal([(0L, true), (384L, false), (960L, false), (1536L, true), (1920L, false)], bass.Clips.Select(c => (c.Start, c.Muted)));

        // WOW2 was off when the song was saved, but automation switches it, so it stays on.
        Assert.Equal([true, true], song.Inserts.Single(i => i.Index == 1).Effects.Select(e => e.Enabled));

        // A rise that jumps back down at the clip's end rises each time the clip plays.
        Assert.Equal([(0.0, 0.0), (4.0, 1.0), (4.0, 0.0), (8.0, 1.0), (8.0, 0.0)],
                     song.Automation.Single(a => a.Name == "Bass - Volume").Points.Select(p => (p.Beat, p.Value)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhereClipsSwitchingAChannelMeetTheOneStartingThereDecides(bool onLinkedFirst)
    {
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25BackToBack(Fixtures.TempDir(), onLinkedFirst));

        // "Kick off" in bars 1 and 3, "Kick on" in bar 2, whichever is linked first in the file.
        var kick = song.Channels.Single(c => c.Name == "Kick");
        Assert.Equal(["Kick off", "Kick on"], kick.SwitchedBy.Order());
        Assert.Equal([(0L, true), (384L, false), (768L, true)], kick.Clips.Select(c => (c.Start, c.Muted)));

        // The volume clip placed again over the second half of itself takes over where it starts: halfway up, it starts again.
        Assert.Equal([(0.0, 0.0), (4.0, 0.5), (4.0, 0.0), (12.0, 1.0)],
                     song.Automation.Single(a => a.Name == "Kick - Volume").Points.Select(p => (p.Beat, p.Value)));
        // Placed again inside itself, it plays that stretch, then the longer placement carries on.
        Assert.Equal([(0.0, 0.0), (2.0, 0.25), (2.0, 0.0), (4.0, 0.25), (4.0, 0.5), (8.0, 1.0)],
                     song.Automation.Single(a => a.Name == "Kick - Pan").Points.Select(p => (p.Beat, p.Value)));
        Assert.Equal([(0.0, 0.0), (4.0, 0.5), (8.0, 1.0)],
                     song.Automation.Single(a => a.Name == "Kick - Pitch").Points.Select(p => (p.Beat, p.Value)));
    }

    [Fact]
    public void ReadsMixerInsertSettingsEffectSlotsAndWhatAutomationMovesOnThem()
    {
        var song = Readers.FlStudio.ReadSong(Fixtures.FlStudio25SwitchedAndMixer(Fixtures.TempDir()));

        // Insert 1's fader is moved and its second effect, in slot 1, is switched off; the master is as FL leaves it.
        var insert = song.Inserts.Single(i => i.Index == 1);
        Assert.Equal([("Pro-Q 3", 0, true), ("WOW2", 1, false)], insert.Effects.Select(e => (e.Name, e.Slot, e.Enabled)));
        Assert.Equal((9600, 0, true), (insert.Fader, insert.Pan, insert.Moved));
        Assert.Equal([(0, 12800, false), (1, 9600, true)], song.Inserts.Select(i => (i.Index, i.Fader!.Value, i.Moved)));

        // Automation of a setting of the effect in insert 1's slot 0, of the tempo and of insert 1's fader.
        Assert.Equal([("Pro-Q 3 - Gain", FlControl.InsertEffect, -1, 1, 0, 3), ("Tempo", FlControl.Tempo, -1, -1, -1, 5),
                      ("Insert 1 - Volume", FlControl.InsertVolume, -1, 1, 0, 0x1fc0)],
                     song.Automation.Where(a => a.Control != FlControl.OnOff).Select(a => (a.Name, a.Control, a.Channel, a.Insert, a.Slot, a.Parameter)));
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

public class FlStudio9Tests
{
    static readonly byte[] Chunk = [.. Enumerable.Range(0, 300).Select(i => (byte)(i * 7))];
    readonly string _path = Fixtures.FlStudio9Project(Fixtures.TempDir(), Chunk);

    [Fact]
    public void KeepsAnFl9PluginsWholeSavedSound()
    {
        var cell = new FlStudioReader().ExtractPresets(_path).Single(p => p.Plugin == "LegacyCell");
        Assert.Equal("FPCh", Encoding.ASCII.GetString(cell.Data, 8, 4));
        Assert.Equal("KLCV", Encoding.ASCII.GetString(cell.Data, 16, 4));
        Assert.Equal(Chunk.Length, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(cell.Data.AsSpan(56)));
        Assert.Equal(Chunk, cell.Data[60..]);

        var song = Readers.FlStudio.ReadSong(_path);
        Assert.Equal(Chunk, song.Channels.Single(c => c.Name == "Cell").Instrument!.State!.Data[60..]);
        // The program it had selected, which FL selects again after loading the chunk.
        Assert.Equal(42, cell.Program);
    }

    [Fact]
    public void ReadsAnFl9PluginsSettingsAfterTheShorterHeader()
    {
        var d = new FlStudioReader().ExtractPresets(_path).Single(p => p.Plugin == "Fruity Compressor").Data;
        Assert.Equal("FxCk", Encoding.ASCII.GetString(d, 8, 4));
        Assert.Equal("Full Reset", Encoding.ASCII.GetString(d, 28, 28).TrimEnd('\0'));
        Assert.Equal([0.5f, 0.02f, 0.5f], Enumerable.Range(0, 3)
            .Select(i => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(56 + 4 * i))));
    }

    [Fact]
    public void ReadsFl9StepSequencerDrumsAndPlaylist()
    {
        var path = Fixtures.FlStudio9Steps(Fixtures.TempDir());
        var kick = Readers.FlStudio.ReadSong(path).Channels.Single();
        // Placed at beat 8 (768 ticks): odd steps swung 3 ticks by shuffle 36, step 7 also shifted 12.
        Assert.Equal([(768L, 60), (768L + 123, 60), (768L + 183, 60)], kick.Notes.Select(n => ((long)n.Start, n.Key)));
        Assert.True(kick.Notes[1].Velocity < kick.Notes[0].Velocity);
        Assert.Equal("3", new FlStudioReader().Read(path).Details["Notes"]);
    }

    [Fact]
    public void ListsFlsOwnEffectsHostedInTheOldWrapperAsNative()
    {
        var plugins = new FlStudioReader().Read(_path).Plugins.ToDictionary(p => p.Name);
        Assert.Equal(("Native", "Image-Line"), (plugins["Fruity Compressor"].Format, plugins["Fruity Compressor"].Vendor));
        Assert.Equal(("VST", PluginKind.Instrument), (plugins["LegacyCell"].Format, plugins["LegacyCell"].Kind));
    }

    [Fact]
    public void SaysWhenAPluginGaveFlNothingToSaveAndCarriesNoEmptySound()
    {
        var path = Fixtures.FlStudio25NothingSaved(Fixtures.TempDir());
        var warning = Assert.Single(new FlStudioReader().Read(path).Warnings);
        Assert.StartsWith("LegacyCell has no saved sound in this file", warning);
        Assert.Empty(new FlStudioReader().ExtractPresets(path));

        var song = Readers.FlStudio.ReadSong(path);
        var cell = song.Channels.Single().Instrument!;
        Assert.Equal((null, true), (cell.State, cell.NothingSaved));
        Assert.Contains("Cell: left out LegacyCell's sound (FL Studio saved none in this file, so it opens at its default)",
                        BitwigProject.FromFl(song, Fixtures.BitwigTemplate()).Notes);

        // DAWproject keeps the plugin, just without settings for it.
        var daw = DawProject.FromFl(song, "funk step_9");
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(daw.Bytes));
        using var xml = zip.GetEntry("project.xml")!.Open();
        var plugin = System.Xml.Linq.XDocument.Load(xml).Descendants("Vst2Plugin").Single();
        Assert.Equal("LegacyCell", (string?)plugin.Attribute("deviceName"));
        Assert.Null(plugin.Element("State"));
        Assert.Contains("Cell: left out LegacyCell's sound (FL Studio saved none in this file, so it opens at its default)", daw.Notes);
    }

    [Fact]
    public void ASoundTheNewestSaveLostIsTakenFromAnOlderSaveOfTheSong()
    {
        // funk step_8 (FL 9) still has LegacyCell's sound; funk step_9, saved again in FL 25, lost it.
        var dir = Fixtures.TempDir();
        Fixtures.FlStudio9Project(dir, Chunk);
        var path = Fixtures.FlStudio25NothingSaved(dir);

        var (song, taken) = LostSounds.FromOlderSaves(Readers.FlStudio.ReadSong(path), path);
        var cell = song.Channels.Single().Instrument!;
        Assert.False(cell.NothingSaved);
        Assert.Equal(Chunk, cell.State!.Data[60..]);
        Assert.Equal(42, cell.State.Program);
        Assert.Equal("Cell: LegacyCell's sound, from funk step_8", Assert.Single(taken));

        var (file, leftOut) = Exporter.BitwigFromFl(path, Path.Combine(dir, "out"), Fixtures.BitwigTemplate());
        Assert.DoesNotContain(leftOut, n => n.Contains("LegacyCell"));
        Assert.Contains("  Cell: LegacyCell's sound, from funk step_8",
                        File.ReadAllLines(Path.Combine(Path.GetDirectoryName(file)!, "funk step_9 - not carried over.txt")));

        // A save with no older one keeps what it has.
        var alone = Fixtures.FlStudio25NothingSaved(Fixtures.TempDir());
        Assert.Empty(LostSounds.FromOlderSaves(Readers.FlStudio.ReadSong(alone), alone).Taken);
    }

    [Fact]
    public void ReadsFl8AndOlder20ByteNotesWhenThePatternIsAlsoAWholeNumberOf24ByteOnes()
    {
        // Six of FL 7's 20-byte notes are 120 bytes, five 24-byte ones too. Read as 24, Solomon's "i dont know_8" had notes
        // at jumbled times lasting 2^31 ticks, its pan and velocity bytes (40 80 80 80) landing in the length.
        static byte[] Note20(uint pos, ushort key)
        {
            var b = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(b, pos);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 0x4000);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 24);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), key);
            b[14] = 0x78; b[16] = 0x40; b[17] = 0x80; b[18] = 0x80; b[19] = 0x80;   // fine pitch, pan, velocity, release, as FL 7 saves them
            return b;
        }
        var path = Path.Combine(Fixtures.TempDir(), "i dont know_8.flp");
        File.WriteAllBytes(path, new Fixtures.FlpBuilder().Ascii(199, "7.0.0").Word(64, 0).Byte(21, 0).Ascii(192, "Kick")
            .Word(65, 1).Data(224, [.. Enumerable.Range(0, 6).SelectMany(i => Note20((uint)i * 24, (ushort)(36 + i)))]).Build());

        var notes = new FlStudioReader().ExtractMidi(path).Single().Tracks.Single().Value;
        Assert.Equal([0L, 24, 48, 72, 96, 120], notes.Select(n => n.Start));
        Assert.All(notes, n => Assert.Equal((24L, 127), (n.Length, n.Velocity)));
        Assert.Equal([36, 37, 38, 39, 40, 41], notes.Select(n => n.Key));
        Assert.Equal(6, Readers.FlStudio.ReadSong(path).Channels.Single().Notes.Count);
    }

    [Theory]
    [InlineData("4.0.1", 64, 70, 0.5, 70 / 128.0)]
    [InlineData("5.0.0", 20, 128, 20 / 128.0, 1.0)]
    [InlineData("6.0.4", 6400, 10000, 0.5, 10000 / 12800.0)]
    public void ReadsChannelPanAndVolumeOnTheScaleOfTheFlVersionThatSavedIt(string version, int pan, int volume, double panKnob, double volumeKnob)
    {
        // FL 4 and 5 count to 128, FL 6 on to 12800: Solomon's FL 4 grimey came into Bitwig panned hard left and silent.
        var path = Path.Combine(Fixtures.TempDir(), "grimey.flp");
        File.WriteAllBytes(path, new Fixtures.FlpBuilder().Ascii(199, version).Word(64, 0).Byte(21, 0).Ascii(192, "snr7")
                                                          .Data(219, Fixtures.Levels(pan, volume, 0)).Build());
        var channel = Readers.FlStudio.ReadSong(path).Channels.Single();
        Assert.Equal(panKnob, channel.Pan, 9);
        Assert.Equal(volumeKnob, channel.Volume, 9);
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
        // What the text scan reads next to a path doesn't stick to it, and bare names aren't files.
        Assert.Contains(@"F:\Bitwig Studio\Projects\Loop Idea\recordings\Syntakt 2-1.wav", info.Samples);
        Assert.Contains(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "recordings/Untitled Project [0m27s-1m22s].wav")), info.Samples);
        Assert.DoesNotContain(info.Samples, s => s.Contains("BASSDRUM"));
        Assert.All(info.Samples, s => Assert.True(s.StartsWith("D:") || s.StartsWith("F:") || Path.IsPathRooted(s), s));
        Assert.False(info.CanExtractMidi);
        Assert.Empty(new BitwigReader().ExtractMidi(path));

        var known = new BitwigReader { KnownPlugins = new HashSet<string>(["Serum"], StringComparer.OrdinalIgnoreCase) };
        Assert.Contains(known.Read(path).Plugins, p => p.Name == "Serum");
    }

    [Fact]
    public void TheTextScanReadsNetworkAndAccentedPathsWholeAndCountsCollectedFilesOnce()
    {
        // The metadata reads but the body doesn't (a value of a kind this app doesn't know, as 6.1's lists of doubles were).
        const string accented = @"F:\Samples\Beyoncé Acapellas\Vocal Chop 03.wav", network = @"\\NAS\Samples\Breaks\Amen Break 136bpm.wav",
                     cyrillic = @"D:\Музыка\Сэмплы\Ударные\Бочка\Kick 01.wav", japanese = @"D:\音楽\サンプル\ドラム\キック.wav";
        var dir = Path.Combine(Fixtures.TempDir(), "Song");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Song.bwproject");
        File.WriteAllBytes(path, Fixtures.BwWriter.File(
            meta: m => { m.MetaEntry("used_files", 0x19); m.U32(1); m.StrVal("recordings/BD 1-2.wav"); },
            body: b =>
            {
                b.Begin(0x2e);
                // Bitwig writes a string's length before it: 42 is "*", which joins the network path's text.
                foreach (var s in new[] { @"F:\Bitwig Studio\Projects\Song\recordings\BD 1-2.wav", network, @"F:\Samples\Drums\Kick 01.wav" })
                    b.Str(0x2b9, s);
                // As Bitwig writes any text with a character outside ASCII, even when most of it is.
                foreach (var s in new[] { accented, cyrillic, japanese })
                    b.Str(0x2b9, s, utf16: true);
                b.U32(0x123); b.S.WriteByte(0x7e);
            }, zip: null));

        var info = new BitwigReader().Read(path);
        Assert.Equal(Confidence.Heuristic, info.Confidence);
        // The recording collected into the project counts once, as its copy there, not again where it was recorded.
        Assert.Equal(new[] { Path.Combine(dir, "recordings", "BD 1-2.wav"), network, @"F:\Samples\Drums\Kick 01.wav", accented, cyrillic, japanese }
                         .Order(StringComparer.Ordinal),
                     info.Samples.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFileCollectedIntoTheProjectHidesOnlyItsOwnOriginal(bool decodable)
    {
        // Kick-01 and Snare (as "Snare-2", the name being taken) were collected into the project; Kick-02 from the same
        // pack wasn't, so it's still listed where it is.
        var dir = Path.Combine(Fixtures.TempDir(), "Song");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Song.bwproject");
        File.WriteAllBytes(path, Fixtures.BwWriter.File(
            meta: m => { m.MetaEntry("used_files", 0x19); m.U32(2); m.StrVal("samples/Kick-01.wav"); m.StrVal("samples/Snare-2.wav"); },
            body: b =>
            {
                b.Begin(0x2e);
                var id = 0x4100;
                foreach (var file in new[] { @"D:\Packs\Drums\Kick-01.wav", @"D:\Packs\Drums\Kick-02.wav", @"D:\Packs\Drums\Snare.wav" })
                {
                    b.Obj(id++); b.Begin(0x2a); b.Str(0x3b, file); b.End();
                }
                if (!decodable) { b.U32(0x123); b.S.WriteByte(0x7e); }   // a value of a kind this app doesn't know
                b.End();
            }, zip: null));

        var info = new BitwigReader().Read(path);
        Assert.Equal(decodable ? Confidence.Partial : Confidence.Heuristic, info.Confidence);
        Assert.Equal(new[] { Path.Combine(dir, "samples", "Kick-01.wav"), Path.Combine(dir, "samples", "Snare-2.wav"), @"D:\Packs\Drums\Kick-02.wav" }
                         .Order(StringComparer.Ordinal),
                     info.Samples.Order(StringComparer.Ordinal));
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
