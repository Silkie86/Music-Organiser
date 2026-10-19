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
    /// <param name="ownTables">
    /// Like Solomon's "requiem serum 1" instead: tables of its own (two frames for A, one for B, in a second zlib stream
    /// after the first, then the first one's packed length), mono, a hall reverb, the noise following pitch, Osc A on
    /// FM from B, and distortion, compressor and filter effects, with a row moving the filter's cutoff.
    /// </param>
    internal static byte[] Serum1(bool flWrapped = false, bool ownTables = false)
    {
        var raw = new byte[Serum1State.Size];
        void F(int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(at), v);
        void T(int at, string s) => Encoding.Latin1.GetBytes(s).CopyTo(raw, at);
        void K(int knob, float v) => F(knob < 228 ? 13_408 + 4 * knob : 19_168 + 4 * (knob - 228), v);
        F(13_408 + 4 * 1, 0.4809f);           // knob 1 (Osc A level)
        F(13_408 + 4 * 45, 0.2829f);          // knob 45 (filter cutoff)
        F(19_168 + 4 * (228 - 228), 0.6974f); // knob 228 (Mod 17 amount), in the later block
        if (ownTables)
        {
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(18_792), 2 * 2048 * 4);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(18_796), 2048 * 4);
            raw[18_800] = 1;                  // Osc A smooths between its frames
            F(19_544, 1);                     // mono
            F(19_568, 1);                     // noise follows pitch
            F(19_620, 1);                     // hall reverb
            K(168, 18 / 23f);                 // Osc A warp: FM (from B)
            K(154, 1); K(99, 2 / 15f);        // distortion on, HardClip
            K(159, 1); K(139, 8 / 57f);       // compressor on, 4 dB of gain
            K(160, 1); K(83, 0.3305f); K(84, 0.8553f); K(85, 0.3063f);   // reverb on: pre-delay, low cut, damping
            K(162, 1); K(143, 16 / 19f);      // filter effect on
            // Mod row 5: Macro 1 to the filter effect's cutoff.
            F(4 * 40 + 4, 0.7719f); BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4 * 40 + 20), 25);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(4 * 40 + 26), 143);
        }
        else
        {
            T(15_368, @"Analog\Analog_BD_Sin.wav");
            T(15_880, "/User/grime.wav");
            T(16_392, @"Attacks_Kick\XF_KikAtk_32.wav");
            T(18_802, "BA Organ Bass [FP]");
            T(18_848, "Francis Preve");
            T(18_896, "M1 Preset #17");
        }
        // Mod matrix: row 1 (source 3 to knob 45), and row 17, which is kept further on.
        F(40 + 4, 0.7254f); BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(40 + 20), 3); BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(40 + 26), 45);
        var row17 = 20_704 + 40;
        F(row17 + 4, -0.5f); raw[row17 + 12] = 1; BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(row17 + 20), 5); BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(row17 + 26), 14);

        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        if (ownTables)
        {
            var first = (int)packed.Length;
            using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
                foreach (var w in OwnWaves()) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, w); z.Write(b); }
            Span<byte> n = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(n, first);
            packed.Write(n);
        }
        var bytes = packed.ToArray();
        // FL Studio's wrapper puts its own 21 bytes in front.
        return flWrapped ? [.. Convert.FromHexString("F7FFFFFF0DFEFFFFFF180D0000000000000000000000"), .. bytes] : bytes;
    }

    /// <summary>The own tables' waves: a falling ramp and a rising one for A's two frames, a saw for B.</summary>
    static float[] OwnWaves() =>
        [.. Enumerable.Range(0, 2048).Select(i => 1 - i / 1024f), .. Enumerable.Range(0, 2048).Select(i => i / 1024f - 1),
         .. Enumerable.Range(0, 2048).Select(i => i / 1024f)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_sound_with_its_own_tables_comes_across_as_Serum_2_made_requiem(bool flWrapped)
    {
        var s1 = Serum1State.Read(Serum1(flWrapped, ownTables: true))!;
        Assert.Equal(OwnWaves()[..4096], s1.OwnTable(0));
        Assert.Equal(OwnWaves()[4096..], s1.OwnTable(1));
        Assert.Equal((true, true, true, false), (s1.Mono, s1.HallReverb, s1.NoisePitchTrack, s1.NoiseOneShot));

        var sound = Serum1To2.Translate(s1, _ => null).State;
        var settings = Serum2State.Read(sound.Write())!.Settings;   // as Serum 2 would read it
        var a = settings["Oscillator0"]!["WTOsc0"]!;
        Assert.Equal(OwnWaves()[..4096].Select(w => (double)w), a["embeddedWTData"]!.AsArray().Select(w => w!.GetValue<double>()));
        Assert.Equal(1, a["interpolateAfterLoad"]!.GetValue<long>());
        Assert.Null(a["relativePathToWT"]);
        Assert.Equal("kPD_OSC", a["plainParams"]!["kParamWarpMenu"]!.GetValue<string>());
        Assert.Equal(0, settings["Oscillator1"]!["WTOsc1"]!["interpolateAfterLoad"]!.GetValue<long>());
        Assert.Equal(2048, settings["Oscillator1"]!["WTOsc1"]!["embeddedWTData"]!.AsArray().Count);

        var global = settings["Global0"]!["plainParams"]!;
        Assert.Equal((1.0, 16.0), (global["kParamMonoToggle"]!.GetValue<double>(), global["kParamPolyCount"]!.GetValue<double>()));
        Assert.Equal(["Wavetable", "Embedded-Data", "Mono"], settings["tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(1, settings["Oscillator3"]!["plainParams"]!["kParamPitchTrack"]!.GetValue<double>());
        Assert.Null(settings["Oscillator3"]!["NoiseOsc3"]!["plainParams"]?["kParamOneShot"]);

        // The effects, with the values Serum 2 gave requiem's.
        var fx = settings["FXRack0"]!["FX"]!.AsArray().Select(f => f!.AsObject().First(kv => kv.Key.StartsWith("FX")))
            .ToDictionary(kv => kv.Key, kv => kv.Value!["plainParams"]!);
        Assert.Equal("kHardClip", fx["FXDistortion"]["kParamMode"]!.GetValue<string>());
        Assert.Equal(1.5909, fx["FXComp"]["kParamMakeup"]!.GetValue<double>(), 3);
        Assert.Equal("kHall", fx["FXReverb"]["kParamType"]!.GetValue<string>());
        Assert.Equal(27.31, fx["FXReverb"]["kParamDelay"]!.GetValue<double>(), 2);
        Assert.Equal(85.53, fx["FXReverb"]["kParamFreq"]!.GetValue<double>(), 2);
        Assert.Equal(30.63, fx["FXReverb"]["kParamFeedback"]!.GetValue<double>(), 2);
        Assert.Null(fx["FXReverb"]["kParamPreDelay"]);
        Assert.Equal(0.7879, fx["FXFilter"]["kParamFreq"]!.GetValue<double>(), 3);

        // The row moving the filter effect's cutoff points at it, by its place in the rack.
        var row = settings["ModSlot4"]!;
        var place = fx.Keys.ToList().IndexOf("FXFilter");
        Assert.Equal(("FXFilter", place, "kParamFreq", 3L), (row["destModuleTypeString"]!.GetValue<string>(), (int)row["destModuleID"]!.GetValue<long>(),
                                                          row["destModuleParamName"]!.GetValue<string>(), row["destModuleParamID"]!.GetValue<long>()));
        Assert.Equal(77.19, row["plainParams"]!["kParamAmount"]!.GetValue<double>(), 2);
    }

    [Fact]
    public void A_Serum_1_sound_is_written_out_as_a_preset_Serum_2_can_open()
    {
        var s1 = Serum1State.Read(Serum1(flWrapped: true, ownTables: true))!;
        var fxp = s1.ToFxp("Lead");
        Assert.Equal("CcnK", Encoding.ASCII.GetString(fxp, 0, 4));
        Assert.Equal(fxp.Length, BinaryPrimitives.ReadInt32BigEndian(fxp.AsSpan(4)));   // Serum counts the whole file
        Assert.Equal("FPChXfsX", Encoding.ASCII.GetString(fxp, 8, 4) + Encoding.ASCII.GetString(fxp, 16, 4));
        Assert.Equal("Lead", Encoding.Latin1.GetString(fxp, 28, 4));
        var again = Serum1State.Read(fxp)!;
        Assert.Equal(s1.Knobs, again.Knobs);
        Assert.Equal(s1.OwnTable(0), again.OwnTable(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_Serum_1_state_gives_its_knobs_mod_matrix_tables_and_preset_name(bool flWrapped)
    {
        var s = Serum1State.Read(Serum1(flWrapped))!;

        Assert.Equal(0.4809f, s.Knob(1));
        Assert.Equal(0.2829f, s.Knobs[45]);
        Assert.Equal(0.6974f, s.Knob(228));
        Assert.Equal(316, s.Knobs.Count);
        Assert.Equal(@"Analog\Analog_BD_Sin.wav", s.OscATable);
        Assert.Equal("/User/grime.wav", s.OscBTable);
        Assert.Equal(@"Attacks_Kick\XF_KikAtk_32.wav", s.NoiseSample);
        Assert.Equal(("BA Organ Bass [FP]", "Francis Preve", "M1 Preset #17"), (s.PresetName, s.Author, s.Description));
        Assert.Equal(
            [new Serum1State.ModSlot(1, 3, 0.7254f, 45, false), new Serum1State.ModSlot(17, 5, -0.5f, 14, true)],
            s.ModSlots);
    }

    [Fact]
    public void A_Serum_1_preset_file_is_read_with_the_later_knobs_at_their_starting_values()
    {
        // An .fxp holds only the first 20,704 bytes; an older Serum leaves Pitch Bend and the later knobs at zero.
        var raw = new byte[Serum1State.PresetSize];
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(13_408 + 4 * 1), 0.4809f);
        Encoding.Latin1.GetBytes("Organ Bass").CopyTo(raw, 18_802);
        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        var header = new byte[60];
        "CcnK"u8.CopyTo(header);
        "FPCh"u8.CopyTo(header.AsSpan(8));
        "XfsX"u8.CopyTo(header.AsSpan(16));
        Encoding.Latin1.GetBytes("xx Organ Bass").CopyTo(header, 28); // a lowercase x in the name looks like zlib's first byte

        var s = Serum1State.Read([.. header, .. packed.ToArray()])!;

        Assert.True(s.IsPreset);
        Assert.Equal(0.4809f, s.Knob(1));
        Assert.Equal("Organ Bass", s.PresetName);
        Assert.Equal((0.5f, 0.5f, 1f, 0.5f, 0f, 1f, 0.5f, 0f), (s.Knob(227), s.Knob(228), s.Knob(229), s.Knob(260),
            s.Knob(264), s.Knob(269), s.Knob(298), s.Knob(315)));
        Assert.Empty(s.ModSlots);
    }

    [Fact]
    public void A_preset_from_a_later_Serum_1_is_read_with_its_later_modulation_rows()
    {
        // Later versions save modulation rows 17 to 32 after the first 20,704 bytes, then more that isn't read
        // (21,524 to 33,872 bytes seen).
        var raw = new byte[21_524];
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(13_408 + 4 * 1), 0.25f);
        var row17 = 20_704;
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(row17 + 4), -0.2f);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(row17 + 20), 27);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(row17 + 26), 148);
        raw.AsSpan(row17 + 16 * 40).Fill(0xff);
        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);

        var s = Serum1State.Read(packed.ToArray())!;

        Assert.True(s.IsPreset);
        Assert.Equal(0.25f, s.Knob(1));
        Assert.Equal(new Serum1State.ModSlot(16, 27, -0.2f, 148, false), Assert.Single(s.ModSlots));
    }

    [Theory]
    [InlineData(0f, null)]                  // off, Serum 2's default
    [InlineData(1 / 23f, "kSync")]          // Serum 2 saves all three of Serum 1's sync modes as kSync
    [InlineData(3 / 23f, "kSync")]
    [InlineData(4 / 23f, "kBendPos")]
    [InlineData(18 / 23f, "kPD_OSC")]       // FM from the other oscillator, which Serum 2 calls phase distortion
    [InlineData(1f, null)]                  // past the last mode, which Serum 1 shows blank
    public void A_Serum_1_warp_mode_is_saved_under_Serum_2s_name_for_it(float knob, string? expected)
    {
        var raw = new byte[Serum1State.Size];
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(13_408 + 4 * 168), knob);
        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);

        var wt = Serum1To2.Translate(Serum1State.Read(packed.ToArray())!).State.Settings["Oscillator0"]!["WTOsc0"]!["plainParams"];
        Assert.Equal(expected, (wt as JsonObject)?["kParamWarpMenu"]?.GetValue<string>());
    }

    [Theory]
    [InlineData(6 * 2048, 3.5)]  // a 6-frame table: halfway is between frames 3 and 4
    [InlineData(null, 128.5)]   // table not found: taken as 256 frames
    public void A_wavetable_position_is_saved_as_a_frame_number_from_1(int? samples, double expected)
    {
        var raw = new byte[Serum1State.Size];
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(13_408 + 4 * 11), 0.5f);
        Encoding.Latin1.GetBytes(@"Analog\Basic Shapes.wav").CopyTo(raw, 15_368);
        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);

        var result = Serum1To2.Translate(Serum1State.Read(packed.ToArray())!,
            path => samples is { } n && path == "Analog/Basic Shapes.wav" ? new Serum1To2.AudioInfo(n, 1, 44_100) : null);

        var wt = result.State.Settings["Oscillator0"]!["WTOsc0"]!;
        Assert.Equal(expected, wt["plainParams"]!["kParamTablePos"]!.GetValue<double>(), 6);
        Assert.Equal(samples is null, result.Rough.Contains("oscillator A's wavetable position"));
    }

    [Fact]
    public void A_synced_delay_time_is_left_to_Serum_2_and_listed_as_rough()
    {
        var raw = new byte[Serum1State.Size];
        void F(int knob, float v) => BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(13_408 + 4 * knob), v);
        F(158, 1); F(127, 1); F(129, 0.6726f); F(130, 0.6405f); // delay on, synced
        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);

        var result = Serum1To2.Translate(Serum1State.Read(packed.ToArray())!);

        var delay = ((JsonArray)result.State.Settings["FXRack0"]!["FX"]!).Single(fx => fx!["FXDelay"] is not null)!["FXDelay"]!["plainParams"]!;
        Assert.Null(delay["kParamTimeL"]);
        Assert.Contains("the delay's synced time", result.Rough);
    }

    [Fact]
    public void Something_that_isnt_a_Serum_1_state_isnt_read_as_one()
    {
        Assert.Null(Serum1State.Read("not serum"u8));
        var small = new MemoryStream();
        using (var z = new ZLibStream(small, CompressionLevel.Fastest, leaveOpen: true)) z.Write(new byte[1000]);
        Assert.Null(Serum1State.Read(small.ToArray()));
    }

    /// <summary>
    /// A Serum 1 state with BA Organ Bass's main knobs and its first three modulation rows, as Serum 1 saved them in
    /// Solomon's project.
    /// </summary>
    static Serum1State OrganBass()
    {
        var raw = new byte[Serum1State.Size];
        void K(int knob, float v) => BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(knob < 228 ? 13_408 + 4 * knob : 19_168 + 4 * (knob - 228)), v);
        void Row(int row, float amount, int source, int knob)
        {
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(row * 40 + 4), amount);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(row * 40 + 20), source);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(row * 40 + 26), (ushort)knob);
        }
        K(0, 0.7f); K(1, 0.48094845f);                                              // master, Osc A level
        K(27, 0.33790776f); K(28, 0.44589499f); K(30, 0.5f); K(31, 0.17374246f); K(32, 0.020781932f); // noise
        K(33, 0.83381307f); K(34, 0.5f); K(171, 0.375f);                            // sub level, pan, octave
        foreach (var on in new[] { 40, 41, 160, 164, 165, 212, 213, 214, 215, 216 }) K(on, 1);
        K(44, 2 / 95f); K(45, 0.28294614f); K(47, 0.2225192f); K(48, 0.54303724f); K(49, 1); K(50, 0.5f); // filter
        K(69, 0.25118899f);                                                         // Chaos 1 rate
        K(81, 0.10234101f); K(82, 0.24572438f); K(83, 0.12f); K(85, 0.28f); K(86, 0.5f); K(87, 1); // reverb
        for (var i = 0; i < 10; i++) BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(15_324 + 4 * i), i); // FX order
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(19_564), 1);   // the noise plays once
        Row(0, 0.7314967f, 4, 14);     // Env 3 to Osc B level
        Row(1, 0.72539747f, 3, 45);    // Env 2 to the cutoff
        Row(2, -0.12085938f, 33, 222); // Fixed to Amp
        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        return Serum1State.Read(packed.ToArray())!;
    }

    [Fact]
    public void A_Serum_1_sound_is_translated_to_the_values_Serum_2_gives_it()
    {
        // The expected values are what Serum 2 wrote when it opened BA Organ Bass itself.
        var result = Serum1To2.Translate(OrganBass());
        var s = result.State.Settings;
        double P(string module, string key, string? sub = null) =>
            (sub == null ? s[module] : s[module]![sub])!["plainParams"]![key]!.GetValue<double>();

        Assert.Equal(0.29297, P("Global0", "kParamMasterVolume"), 3);
        Assert.Equal(1.0, P("Global0", "kParamS1Compatibility"));
        Assert.Equal(0.2313, P("Oscillator0", "kParamVolume"), 4);
        Assert.Equal("MgL18", s["VoiceFilter0"]!["plainParams"]!["kParamType"]!.GetValue<string>());
        Assert.Equal(0.2829, P("VoiceFilter0", "kParamFreq"), 4);
        Assert.Equal(22.25, P("VoiceFilter0", "kParamDrive"), 2);
        Assert.Equal(54.30, P("VoiceFilter0", "kParamVar"), 2);
        Assert.Equal(0.0, P("VoiceFilter0", "kParamReso"));
        Assert.Equal("kRoutingDestFilter", s["RoutingSlot1"]!["plainParams"]!["kParamRoutingDest"]!.GetValue<string>());
        Assert.Equal(-1.0, P("Oscillator4", "kParamOctave"));
        Assert.Equal(0.6952, P("Oscillator4", "kParamVolume"), 4);
        Assert.Equal(0.4459, P("Oscillator3", "kParamColor", "NoiseOsc3"), 4);
        Assert.Equal(-1.0, P("Oscillator3", "kParamFine", "NoiseOsc3"));
        Assert.Equal(2.078, P("Oscillator3", "kParamInitialPhase", "NoiseOsc3"), 3);
        Assert.Equal(17.37, P("Oscillator3", "kParamRandomPhase", "NoiseOsc3"), 2);
        Assert.Equal(1.0, P("Oscillator3", "kParamOneShot", "NoiseOsc3"));
        Assert.Equal(0.1, P("LFO8", "kParamRate"), 4);

        var fx = Assert.Single((JsonArray)s["FXRack0"]!["FX"]!)!;
        Assert.Equal(6, fx["type"]!.GetValue<long>());
        var reverb = fx["FXReverb"]!["plainParams"]!;
        Assert.Equal((10.23, 24.57, 28.0, 50.0, 100.0), (Math.Round(reverb["kParamWet"]!.GetValue<double>(), 2),
            Math.Round(reverb["kParamSize"]!.GetValue<double>(), 2), Math.Round(reverb["kParamFeedback"]!.GetValue<double>()),
            reverb["kParamFreqB"]!.GetValue<double>(), reverb["kParamWidth"]!.GetValue<double>()));

        var toOscB = s["ModSlot0"]!;
        Assert.Equal(("Oscillator", 1L, "kParamVolume"), (toOscB["destModuleTypeString"]!.GetValue<string>(),
            toOscB["destModuleID"]!.GetValue<long>(), toOscB["destModuleParamName"]!.GetValue<string>()));
        Assert.Equal(73.15, P("ModSlot0", "kParamAmount"), 2);
        Assert.Equal([4L, 0L], toOscB["source"]!.AsArray().Select(n => n!.GetValue<long>()));
        Assert.Equal("VoiceFilter", s["ModSlot1"]!["destModuleTypeString"]!.GetValue<string>());
        Assert.Equal(38L, s["ModSlot2"]!["source"]![0]!.GetValue<long>());
        Assert.Equal(-12.09, P("ModSlot2", "kParamAmount"), 2);

        // Only the reverb's decay curve is still a guess for this sound.
        Assert.Equal(["the reverb's decay and spin"], result.Rough);
    }

    [Fact]
    public void A_translated_sound_keeps_Serum_2s_floats_as_floats()
    {
        // Serum 2 saves its switches as 1.0, not 1; the template's must come out the same way.
        var global = Serum2State.Cbor.Write(Serum1To2.Translate(OrganBass()).State.Settings["Global0"]!["plainParams"]);
        Assert.Contains(Convert.ToHexString("kParamS1Compatibility"u8) + "FA3F800000", Convert.ToHexString(global));

        var back = Serum2State.Read(Serum1To2.Translate(OrganBass()).State.Write())!;
        Assert.Equal("Serum2", back.Header["product"]!.GetValue<string>());
    }
}
