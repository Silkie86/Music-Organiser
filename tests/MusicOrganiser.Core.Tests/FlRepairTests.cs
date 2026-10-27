using System.Buffers.Binary;
using System.Text;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Preview;
using Xunit;

namespace MusicOrganiser.Core.Tests;

/// <summary>
/// FL Studio saves FL stopped writing part-way, like Solomon's "i dont know_8" (FL 7): junk where "FLdt" and its length
/// belong, and nothing after the 15th channel, so no mixer or playlist and FL won't open it. Its "_7" is whole.
/// </summary>
public class FlRepairTests
{
    static byte[] Note20(uint pos, ushort channel, ushort key)
    {
        var b = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(b, pos);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 0x4000);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), channel);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 96);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), key);
        b[16] = 0x40; b[17] = 100;
        return b;
    }

    /// <summary>An FL 6 to 8 playlist item (28 bytes): pattern n on row 30951 - n, or a channel's clip on a row of its own.</summary>
    static byte[] Item28(uint pos, uint length, int pattern = 0, int channel = -1, int row = 30818)
    {
        var b = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(b, pos);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)(channel >= 0 ? 20480 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)(channel >= 0 ? channel : 0x6000));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), (ushort)(channel >= 0 ? row : 30951 - pattern));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), 3);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(20), -1);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(24), -1);
        return b;
    }

    static readonly string[] Names = ["Kick", "Snare", "Bass"];

    /// <summary>
    /// An FL 7 song: pattern 1's notes, then a channel each (its name and plugin, and <paramref name="extra"/> more
    /// events on channel 1), then, when FL finished, the playlist and the mixer.
    /// </summary>
    internal static byte[] Song(int channels, byte[] notes, bool finished, bool extra = false, string snare = "Snare")
    {
        var b = new Fixtures.FlpBuilder().Ascii(199, "7.0.0").Word(66, 140).Word(65, 1).Data(224, notes);
        for (var c = 0; c < channels; c++)
        {
            b.Word(64, (ushort)c).Byte(21, 0).Ascii(192, c == 1 ? snare : Names[c]).Ascii(201, c == 2 ? "3x Osc" : "Sampler");
            if (extra && c == 1) b.Data(219, Fixtures.Levels(6400, 10000, 0));
        }
        if (finished)
            b.Data(233, [.. Item28(0, 384, pattern: 1), .. Item28(384, 384, pattern: 1)]).Data(236, new byte[12]);
        var data = b.Build();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), (ushort)channels);
        return data;
    }

    /// <summary>As FL leaves a save it didn't finish: junk where "FLdt" and its length belong (from Solomon's "_8").</summary>
    internal static byte[] Unfinished(byte[] data)
    {
        new byte[] { 0xa4, 0xfb, 0x12, 0x00, 0xd9, 0x4a, 0xd7, 0x00 }.CopyTo(data, 14);
        return data;
    }

    internal static readonly byte[] OldNotes = Note20(0, 0, 36);
    internal static readonly byte[] NewNotes = [.. Note20(0, 0, 36), .. Note20(96, 1, 38)];

    [Fact]
    public void TellsASaveFlStoppedWritingPartWayFromOneWithOnlyItsHeaderGarbled()
    {
        var stopped = Unfinished(Song(2, NewNotes, finished: false));
        Assert.Equal(new FlDamage(EndsEarly: true, Channels: 2), FlStudioReader.Damage(stopped));
        Assert.Equal(new FlDamage(EndsEarly: false, Channels: 3), FlStudioReader.Damage(Unfinished(Song(3, OldNotes, finished: true))));
        Assert.Null(FlStudioReader.Damage(Song(3, OldNotes, finished: true)));

        var path = Path.Combine(Fixtures.TempDir(), "i dont know_8.flp");
        File.WriteAllBytes(path, stopped);
        var info = new FlStudioReader().Read(path);
        Assert.Equal([FlStudioReader.StoppedPartWay], info.Warnings);
        Assert.Equal(["Kick", "Snare"], info.Tracks);
    }

    [Fact]
    public void RepairKeepsWhatTheSaveGotAsFarAsAndTakesTheRestFromTheOlderSave()
    {
        var older = Song(3, OldNotes, finished: true);
        // The newer save renamed the snare: the channel it stopped in is its own up to there.
        var (data, fromOlder) = FlStudioReader.Repair(Unfinished(Song(2, NewNotes, finished: false, snare: "Snare 2")), older);

        Assert.Equal(1, fromOlder);
        Assert.Equal("FLdt", Encoding.ASCII.GetString(data, 14, 4));
        Assert.Equal(data.Length - 22, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18)));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10)));   // FLhd counts the channels
        Assert.Null(FlStudioReader.Damage(data));

        var path = Path.Combine(Fixtures.TempDir(), "i dont know_8 (fixed).flp");
        File.WriteAllBytes(path, data);
        var info = new FlStudioReader().Read(path);
        Assert.Empty(info.Warnings);
        Assert.Equal(["Kick", "Snare 2", "Bass"], info.Tracks);
        // The newer save's notes (two, where the older save had one), played twice by the older save's playlist.
        var song = Readers.FlStudio.ReadSong(path);
        Assert.Equal([2, 2, 0], song.Channels.Select(c => c.Notes.Count));
        // Every event the damaged save got as far as is there as it was, then the older save's from the same point on.
        var ids = FlStudioReader.ReadEvents(data).Events.Select(e => e.Id).ToList();
        Assert.Equal(FlStudioReader.ReadEvents(older).Events.Select(e => e.Id), ids);
    }

    [Fact]
    public void RepairTakesTheWholeChannelFromTheOlderSaveWhenTheirEventsDontLineUp()
    {
        // The damaged save stopped in channel 1 with an event the older save's channel 1 doesn't have.
        var (data, fromOlder) = FlStudioReader.Repair(Unfinished(Song(2, NewNotes, finished: false, extra: true)), Song(3, OldNotes, finished: true));
        Assert.Equal(2, fromOlder);
        var events = FlStudioReader.ReadEvents(data).Events;
        Assert.DoesNotContain(events, e => e.Id == 219);
        Assert.Equal(3, events.Count(e => e.Id == 64));
        Assert.Equal(NewNotes, events.Single(e => e.Id == 224).Payload.ToArray());
    }

    [Fact]
    public void RepairRefusesACopyWhoseEventsDontReadBackAsTheTwoSavesPutTogether()
    {
        // The older save is FL 24's, which writes event 172 with four bytes; the damaged one is FL 25's, which reads it
        // with three, so the older save's last channel would come out garbled in the copy.
        Fixtures.FlpBuilder Start(string version) => new Fixtures.FlpBuilder().Ascii(199, version).Word(66, 140)
            .Word(64, 0).Byte(21, 0).Ascii(192, "Kick").Ascii(201, "Sampler")
            .Word(64, 1).Byte(21, 0).Ascii(192, "Snare").Ascii(201, "Sampler");
        var older = Start("24.1.0").Word(64, 2).Byte(21, 0).Ascii(192, "Bass").Ascii(201, "3x Osc").Dword(172, 0x01020304)
            .Data(233, Fixtures.PlaylistItem25(0, 1, 384)).Data(236, new byte[12]).Build();
        var damaged = Unfinished(Start("25.1.0").Build());

        var e = Assert.Throws<InvalidDataException>(() => FlStudioReader.Repair(damaged, older));
        Assert.Contains("wasn't saved", e.Message);
    }

    [Fact]
    public void RepairWithoutAnOlderSaveWritesTheHeaderAndKeepsWhatThereIs()
    {
        var damaged = Unfinished(Song(2, NewNotes, finished: false));
        var (data, fromOlder) = FlStudioReader.Repair(damaged, null);
        Assert.Equal(-1, fromOlder);
        Assert.Equal("FLdt", Encoding.ASCII.GetString(data, 14, 4));
        Assert.Equal(damaged.AsSpan(22).ToArray(), data.AsSpan(22).ToArray());
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10)));
    }

    [Fact]
    public void FindsTheNewestOlderSaveFlFinishedAndShowsItsArrangement()
    {
        var dir = Fixtures.TempDir();
        File.WriteAllBytes(Path.Combine(dir, "i dont know.flp"), Song(3, OldNotes, finished: true));
        File.WriteAllBytes(Path.Combine(dir, "i dont know_6.flp"), Song(3, OldNotes, finished: true));
        File.WriteAllBytes(Path.Combine(dir, "i dont know_7.flp"), Unfinished(Song(2, OldNotes, finished: false)));
        var newest = Path.Combine(dir, "i dont know_8.flp");
        File.WriteAllBytes(newest, Unfinished(Song(2, NewNotes, finished: false)));
        File.WriteAllBytes(Path.Combine(dir, "something else_5.flp"), Song(3, OldNotes, finished: true));

        Assert.Equal(Path.Combine(dir, "i dont know_6.flp"), FlStudioReader.WholeOlderSave(newest));
        var shown = ArrangementReader.ReadNewestWhole(newest)[0];
        Assert.StartsWith("i dont know_8 stopped part-way through saving, before its playlist, so this is i dont know_6's arrangement", shown.Notes[0]);
        Assert.Equal(2, shown.ClipCount);
        // A whole save shows its own.
        Assert.Empty(ArrangementReader.ReadNewestWhole(Path.Combine(dir, "i dont know_6.flp"))[0].Notes);
    }

    [Theory]
    [InlineData(new byte[] { 0xff, 0xff, 0xff, 0xff, 0x0f })]         // a size that goes negative
    [InlineData(new byte[] { 0xff, 0xff, 0xff, 0xff, 0x07 })]         // int.MaxValue: adding it to where it starts overflows
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 })]   // a size that doesn't end after five bytes
    public void ADamagedEventSizeIsReadAsTheFileEndingThere(byte[] junk)
    {
        var dir = Fixtures.TempDir();
        var whole = Song(3, OldNotes, finished: true);
        File.WriteAllBytes(Path.Combine(dir, "i dont know_6.flp"), whole);
        // An older save that's whole apart from junk after its mixer.
        byte[] damaged = [.. whole, 0xd0, .. junk, .. new byte[8]];
        BinaryPrimitives.WriteInt32LittleEndian(damaged.AsSpan(18), damaged.Length - 22);
        var junky = Path.Combine(dir, "i dont know_7.flp");
        File.WriteAllBytes(junky, damaged);
        var newest = Path.Combine(dir, "i dont know_8.flp");
        File.WriteAllBytes(newest, Unfinished(Song(2, NewNotes, finished: false)));

        Assert.Equal(FlStudioReader.ReadEvents(whole).Events.Select(e => e.Id), FlStudioReader.ReadEvents(damaged).Events.Select(e => e.Id));
        Assert.Equal(["Kick", "Snare", "Bass"], new FlStudioReader().Read(junky).Tracks);
        Assert.Equal(junky, FlStudioReader.WholeOlderSave(newest));
    }

    [Fact]
    public void FixRepairsTheSaveIntoACopyNextToIt()
    {
        var root = Fixtures.TempDir();
        var dir = Path.Combine(root, "Projects");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "i dont know_7.flp"), Song(3, OldNotes, finished: true));
        var path = Path.Combine(dir, "i dont know_8.flp");
        var before = Unfinished(Song(2, NewNotes, finished: false));
        File.WriteAllBytes(path, before);
        using var db = new LibraryDb(Path.Combine(root, "library.db"));
        db.AddRoot(dir);
        new Scanner(db).ScanProjects();
        var detail = db.Project(db.Projects().Single(p => p.Path == path).Id)!;
        Assert.True(ProjectFixer.NeedsRepair(detail));

        var repair = ProjectFixer.Plan(db, detail).First();
        Assert.True(repair.IsRepair && repair.CanWrite);
        Assert.Equal(Path.Combine(dir, "i dont know_7.flp"), repair.Use);
        Assert.Contains("takes the rest, its last channels, mixer and playlist, from i dont know_7", repair.Note);

        var result = ProjectFixer.Write(path, [repair]);
        Assert.Equal(Path.Combine(dir, "i dont know_8 (fixed).flp"), result.Path);
        Assert.True(result.Repaired);
        Assert.Equal(["Repaired the save, with its last channel, mixer and playlist from i dont know_7."], result.Notes);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(new FlStudioReader().Read(result.Path).Warnings);
    }

    [Fact]
    public void FixFindsEverySaveOfASongFlDidntFinishWritingNotJustTheNewest()
    {
        // Solomon's other "i dont know" (27 Oct): FL stopped writing _8, and a later _9 is the song's newest.
        var root = Fixtures.TempDir();
        var dir = Path.Combine(root, "dub");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "i dont know_6.flp"), Unfinished(Song(3, OldNotes, finished: true)));   // only its header garbled
        File.WriteAllBytes(Path.Combine(dir, "i dont know_7.flp"), Song(3, OldNotes, finished: true));
        File.WriteAllBytes(Path.Combine(dir, "i dont know_8.flp"), Unfinished(Song(2, NewNotes, finished: false)));
        File.WriteAllBytes(Path.Combine(dir, "i dont know_9.flp"), Song(3, NewNotes, finished: true));
        using var db = new LibraryDb(Path.Combine(root, "library.db"));
        db.AddRoot(dir);
        new Scanner(db).ScanProjects();

        var saves = db.Projects();
        var damaged = ProjectFixer.NeedingRepair(db, saves);
        Assert.Equal(["i dont know_6", "i dont know_8"], saves.Where(s => damaged.Contains(s.Id)).Select(s => s.Name).Order());
        Assert.Empty(ProjectFixer.NeedingRepair(db, saves.Where(s => s.Name is "i dont know_7" or "i dont know_9")));
    }

    [Fact]
    public void ReadsFl6To8PlaylistsWithEachPatternOnItsOwnRow()
    {
        // Eight 28-byte items are 224 bytes, also seven of FL 9's 32: they're still read as FL 7's.
        var items = new[]
        {
            Item28(0, 384, pattern: 2), Item28(384, 384, pattern: 1), Item28(768, 384, pattern: 1), Item28(1152, 384, pattern: 2),
            Item28(1536, 384, pattern: 1), Item28(1920, 384, pattern: 1), Item28(2304, 384, pattern: 1), Item28(0, 768, channel: 2),
        }.SelectMany(i => i).ToArray();
        var path = Path.Combine(Fixtures.TempDir(), "gypsy_7.flp");
        File.WriteAllBytes(path, new Fixtures.FlpBuilder().Ascii(199, "7.0.2").Word(66, 140)
            .Word(65, 1).Data(224, Note20(0, 0, 36)).Word(65, 2).Data(224, Note20(0, 1, 38))
            .Word(64, 0).Byte(21, 0).Ascii(192, "Kick").Word(64, 1).Byte(21, 0).Ascii(192, "Snare")
            .Word(64, 2).Byte(21, 0).Ascii(192, "Vox").Ascii(196, @"C:\Samples\Vox.wav")
            .Data(233, items).Data(236, new byte[12]).Build());

        var playlist = ArrangementReader.Read(path)[0];
        Assert.Empty(playlist.Notes);   // not "No patterns are in the playlist"
        Assert.Equal(["Track 1", "Track 2", "Track 3"], playlist.Lanes.Select(l => l.Name));
        Assert.Equal([4.0, 8, 16, 20, 24], playlist.Lanes[0].Clips.Select(c => c.Start));   // pattern 1, in beats
        Assert.Equal([0.0, 12], playlist.Lanes[1].Clips.Select(c => c.Start));   // pattern 2
        Assert.Equal((36, 38), (playlist.Lanes[0].Clips[0].Notes.Single().Key, playlist.Lanes[1].Clips[0].Notes.Single().Key));
        Assert.Equal(LaneKind.Audio, playlist.Lanes[2].Kind);
        Assert.Equal("Vox", playlist.Lanes[2].Clips.Single().Name);
    }
}
