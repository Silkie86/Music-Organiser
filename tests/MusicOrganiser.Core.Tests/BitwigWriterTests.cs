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
    public void ReadsAndWritesListsOfFloats()
    {
        // FM-4 keeps its modulation matrix as a counted list of floats (type 0x17).
        var raw = Fixtures.BwWriter.File(meta: _ => { }, body: b =>
        {
            b.Begin(0x2e);
            b.Obj(0xa4); b.Begin(0x3c); b.Str(0x2b9, "MATRIX"); b.Floats(0xe75, 0.43f, 0, 0.35f); b.End();
            b.Str(0x15b, "after");
            b.End();
        }, zip: null);
        var doc = BitwigFormat.Decode(raw);
        Assert.Null(doc.BodyError);
        Assert.Equal([0.43f, 0f, 0.35f], (float[])doc.Root!.Obj(0xa4)![0xe75]!);
        Assert.Equal("after", doc.Root.Str(0x15b));
        Assert.Equal(raw, BitwigFormat.Encode(doc));
    }

    [Fact]
    public void ReadsAndWritesListsOfDoubles()
    {
        // Bitwig 6.1 keeps a Sampler sample's analysis as counted lists of doubles (type 0x18).
        // Not knowing the type stopped Solomon's "analog full set" from being read at all.
        var raw = Fixtures.BwWriter.File(meta: _ => { }, body: b =>
        {
            b.Begin(0x2e);
            b.Obj(0x40fe); b.Begin(0x15df); b.Doubles(0x40fd, 28.074285715, 31.502857142, 34.931519273); b.End();
            b.Str(0x15b, "after");
            b.End();
        }, zip: null);
        var doc = BitwigFormat.Decode(raw);
        Assert.Null(doc.BodyError);
        Assert.Equal([28.074285715, 31.502857142, 34.931519273], (double[])doc.Root!.Obj(0x40fe)![0x40fd]!);
        Assert.Equal("after", doc.Root.Str(0x15b));
        Assert.Equal(raw, BitwigFormat.Encode(doc));
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

    [Fact]
    public void AFieldPointingAtAnObjectWrittenEarlierIsABareReference()
    {
        // Two automation lanes on one plugin both point at it; the second is written after the plugin.
        var doc = BitwigFormat.Decode(File.ReadAllBytes(Fixtures.BitwigProject(Fixtures.TempDir())));
        var group = doc.Root!.Obj(0x4dd)!;
        var fx = group.List(0x4df).Single();
        group.Set(0x4e0, new BitwigFormat.BwLink(fx), 0x09);
        Assert.True(group.Fields.FindIndex(f => f.Id == 0x4df) < group.Fields.FindIndex(f => f.Id == 0x4e0));

        var back = BitwigFormat.Decode(BitwigFormat.Encode(doc));
        Assert.Null(back.BodyError);
        var backGroup = back.Root!.Obj(0x4dd)!;
        Assert.Same(backGroup.List(0x4df).Single(), backGroup.Obj(0x4e0));
        Assert.Equal(0x0b, backGroup.Types[backGroup.Fields.FindIndex(f => f.Id == 0x4e0)]);
    }

    [Fact]
    public void ACopiedTrackSharesTheMasterItRoutesToAndGetsItsOwnIds()
    {
        var doc = BitwigFormat.Decode(File.ReadAllBytes(Fixtures.BitwigProject(Fixtures.TempDir())));
        var group = doc.Root!.Obj(0x4dd)!;
        var tracks = (List<object?>)group[0x4de]!;
        var first = (BitwigFormat.BwObject)tracks[0]!;
        var copy = BitwigFormat.Clone(doc, first);
        copy.Set(0x15b, "Copy");
        tracks.Add(copy);

        var back = BitwigFormat.Decode(BitwigFormat.Encode(doc));
        Assert.Null(back.BodyError);
        var backGroup = back.Root!.Obj(0x4dd)!;
        var (a, b) = (backGroup.List(0x4de).First(), backGroup.List(0x4de).Last());
        Assert.Equal("Copy", b.Str(0x15b));
        Assert.Equal(["Keyscape", "Delay+"], b.List(0x4ba).Select(d => d.Str(0x9a)));
        Assert.NotEqual(a[0xf], b[0xf]);
        Assert.NotSame(a.List(0x4ba).First(), b.List(0x4ba).First());

        // The master is written once, inside the first track, and both tracks point at it.
        var master = backGroup.Obj(0x4e0)!;
        Assert.Same(master, a.Obj(0x2bb0));
        Assert.Same(master, b.Obj(0x2bb0));
        Assert.Single(back.Objects, o => o.List(0x4ba).Any(d => d.Str(0x9a) == "FabFilter Pro-L 2"));
    }

    [Fact]
    public void AnObjectIsWrittenWhereItsFirstReached()
    {
        var doc = BitwigFormat.Decode(File.ReadAllBytes(Fixtures.BitwigProject(Fixtures.TempDir())));
        var group = doc.Root!.Obj(0x4dd)!;
        var tracks = (List<object?>)group[0x4de]!;
        var first = (BitwigFormat.BwObject)tracks[0]!;
        var master = first.Obj(0x2bb0)!;
        // Drop the first track, which is where the master was written: it moves to the project's master slot.
        tracks.RemoveAt(0);

        var back = BitwigFormat.Decode(BitwigFormat.Encode(doc));
        Assert.Null(back.BodyError);
        Assert.Equal("FabFilter Pro-L 2", back.Root!.Obj(0x4dd)!.Obj(0x4e0)!.List(0x4ba).Single().Str(0x9a));
        Assert.Empty(back.Root.Obj(0x4dd)!.List(0x4de));
        Assert.NotNull(master);
    }
}
