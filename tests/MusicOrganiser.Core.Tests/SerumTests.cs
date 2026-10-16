using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MusicOrganiser.Core.Plugins.Serum;

namespace MusicOrganiser.Core.Tests;

public class SerumTests
{
    /// <summary>The shape of Serum 2's settings, cut down: a module at its defaults and one with values set.</summary>
    static Serum2State Sound() => new(
        new JsonObject { ["component"] = "processor", ["product"] = "Serum2", ["productVersion"] = "2.0.16", ["version"] = 6.0 },
        new JsonObject
        {
            ["Env0"] = new JsonObject { ["plainParams"] = new JsonObject { ["kParamAttack"] = 0.000953231488101403, ["kParamSustain"] = 0.5 } },
            ["LFO0"] = new JsonObject { ["plainParams"] = "default" },
            ["ModSlot0"] = new JsonObject
            {
                ["destModuleID"] = 1L, ["destModuleParamName"] = "kParamVolume", ["source"] = new JsonArray(4L, 0L),
                ["plainParams"] = new JsonObject { ["kParamAmount"] = -12.085938453674316 },
            },
            ["mpeEnabled"] = true,
            ["mpePitchBendRange"] = 48L,
            ["tags"] = new JsonArray("Wavetable", "Poly"),
        });

    [Fact]
    public void A_Serum_2_sound_written_reads_back_the_same_with_its_header_and_hash()
    {
        var bytes = Sound().Write();

        Assert.True(Serum2State.Is(bytes));
        var back = Serum2State.Read(bytes)!;
        Assert.Equal(Sound().Settings.ToJsonString(), back.Settings.ToJsonString());
        Assert.Equal("2.0.16", (string?)back.Header["productVersion"]);

        // The header's hash is the MD5 of the packed settings, which follow the header, the unpacked length and a 2.
        var headerLength = (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(9));
        var packed = bytes.AsSpan(9 + 8 + headerLength + 8);
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(packed)), (string?)back.Header["hash"]);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(9 + 8 + headerLength + 4)));
        Assert.Contains("\"version\":6.0", Encoding.UTF8.GetString(bytes, 17, headerLength));
    }

    [Fact]
    public void Serum_2_settings_are_written_as_Serum_writes_them()
    {
        // Whole numbers stay whole; a float that fits 32 bits exactly takes 32 bits, others 64. Serum 2's own
        // states come back byte for byte this way (checked on Solomon's), so the bytes here are pinned.
        var cbor = Serum2State.Cbor.Write(new JsonObject { ["a"] = 0.5, ["b"] = 0.1, ["c"] = 48L, ["d"] = -1L, ["e"] = "x", ["f"] = false });
        Assert.Equal("A6" + "6161FA3F000000" + "6162FB3FB999999999999A" + "61631830" + "616420" + "61656178" + "6166F4",
                     Convert.ToHexString(cbor));

        var pos = 0;
        var back = (JsonObject)Serum2State.Cbor.Read(cbor, ref pos)!;
        Assert.Equal(cbor, Serum2State.Cbor.Write(back));
    }

    [Fact]
    public void Something_that_isnt_a_Serum_2_sound_isnt_read_as_one()
    {
        Assert.Null(Serum2State.Read("XferJson\0"u8));
        Assert.Null(Serum2State.Read(new byte[64]));
        var broken = Sound().Write();
        broken[^5] ^= 0xff;
        Assert.Null(Serum2State.Read(broken));
    }

    /// <summary>A Serum 1 state laid out as Serum 1.3 saves one, with a few things set.</summary>
    static byte[] Serum1(bool flWrapped = false)
    {
        var raw = new byte[Serum1State.Size];
        void F(int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(at), v);
        void T(int at, string s) => Encoding.Latin1.GetBytes(s).CopyTo(raw, at);
        F(13_408 + 4 * 1, 0.4809f);           // knob 1 (Osc A level)
        F(13_408 + 4 * 45, 0.2829f);          // knob 45 (filter cutoff)
        T(15_368, @"Analog\Analog_BD_Sin.wav");
        T(15_880, "/User/grime.wav");
        T(16_392, @"Attacks_Kick\XF_KikAtk_32.wav");
        T(18_802, "BA Organ Bass [FP]");
        T(18_848, "Francis Preve");
        T(18_896, "M1 Preset #17");
        // Mod matrix: row 1 (source 3 to knob 45), and row 17, which is kept further on.
        F(40 + 4, 0.7254f); BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(40 + 20), 3); BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(40 + 26), 45);
        var row17 = 20_704 + 40;
        F(row17 + 4, -0.5f); raw[row17 + 12] = 1; BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(row17 + 20), 5); BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(row17 + 26), 14);

        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        var bytes = packed.ToArray();
        // FL Studio's wrapper puts its own 21 bytes in front.
        return flWrapped ? [.. Convert.FromHexString("F7FFFFFF0DFEFFFFFF180D0000000000000000000000"), .. bytes] : bytes;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_Serum_1_state_gives_its_knobs_mod_matrix_tables_and_preset_name(bool flWrapped)
    {
        var s = Serum1State.Read(Serum1(flWrapped))!;

        Assert.Equal(0.4809f, s.Knob(1));
        Assert.Equal(0.2829f, s.Knobs[45]);
        Assert.Equal(@"Analog\Analog_BD_Sin.wav", s.OscATable);
        Assert.Equal("/User/grime.wav", s.OscBTable);
        Assert.Equal(@"Attacks_Kick\XF_KikAtk_32.wav", s.NoiseSample);
        Assert.Equal(("BA Organ Bass [FP]", "Francis Preve", "M1 Preset #17"), (s.PresetName, s.Author, s.Description));
        Assert.Equal(
            [new Serum1State.ModSlot(1, 3, 0.7254f, 45, false), new Serum1State.ModSlot(17, 5, -0.5f, 14, true)],
            s.ModSlots);
    }

    [Fact]
    public void Something_that_isnt_a_Serum_1_state_isnt_read_as_one()
    {
        Assert.Null(Serum1State.Read("not serum"u8));
        var small = new MemoryStream();
        using (var z = new ZLibStream(small, CompressionLevel.Fastest, leaveOpen: true)) z.Write(new byte[1000]);
        Assert.Null(Serum1State.Read(small.ToArray()));
    }
}
