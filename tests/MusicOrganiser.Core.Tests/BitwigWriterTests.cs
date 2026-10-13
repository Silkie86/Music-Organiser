using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Tests;

public class BitwigWriterTests
{
    [Fact]
    public void AnUntouchedProjectWritesBackByteForByte()
    {
        var raw = File.ReadAllBytes(Fixtures.BitwigProject(Fixtures.TempDir()));
        Assert.Equal(raw, BitwigFormat.Encode(BitwigFormat.Decode(raw)));
    }

    [Fact]
    public void EditsKeepReferencesPointingAtTheRightObjects()
    {
        var doc = BitwigFormat.Decode(File.ReadAllBytes(Fixtures.BitwigProject(Fixtures.TempDir())));
        var group = doc.Root!.Obj(0x4dd)!;
        var track = group.List(0x4de).First();

        // Take Delay+ out of the first track. The master is written after it, so every
        // reference to the master has to be renumbered.
        var devices = (List<object?>)track[0x4ba]!;
        devices.RemoveAll(d => d is BitwigFormat.BwObject { Class: 0x40 });
        track.Set(0x15b, "Keys from FL");

        var back = BitwigFormat.Decode(BitwigFormat.Encode(doc));
        Assert.Null(back.BodyError);
        var backTrack = back.Root!.Obj(0x4dd)!.List(0x4de).First();
        Assert.Equal("Keys from FL", backTrack.Str(0x15b));
        Assert.Equal(["Keyscape"], backTrack.List(0x4ba).Select(d => d.Str(0x9a)));
        var master = back.Root.Obj(0x4dd)!.Obj(0x4e0)!;
        Assert.Equal("FabFilter Pro-L 2", master.List(0x4ba).Single().Str(0x9a));
        Assert.Equal(doc.Objects.Count - 1, back.Objects.Count);
        Assert.Equal(doc.OpenZip()!.Entries.Count, back.OpenZip()!.Entries.Count);
    }

    [Fact]
    public void ACopiedObjectCanBeLinkedToByIdentity()
    {
        var doc = BitwigFormat.Decode(File.ReadAllBytes(Fixtures.BitwigProject(Fixtures.TempDir())));
        var group = doc.Root!.Obj(0x4dd)!;
        var fx = group.List(0x4df).Single();
        // Point the project's master slot at the effect track instead, by identity.
        group.Set(0x4e0, new BitwigFormat.BwLink(fx));

        var back = BitwigFormat.Decode(BitwigFormat.Encode(doc));
        var backGroup = back.Root!.Obj(0x4dd)!;
        Assert.Same(backGroup.List(0x4df).Single(), backGroup.Obj(0x4e0));
    }
}
