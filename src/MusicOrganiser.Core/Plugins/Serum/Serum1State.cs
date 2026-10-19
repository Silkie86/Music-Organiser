using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MusicOrganiser.Core.Plugins.Serum;

/// <summary>
/// The original Serum's saved state. Its VST2 chunk and its VST3 component state are the same thing: a zlib stream
/// that unpacks to 172,736 bytes laid out the same way every time (FL Studio's wrapper puts a few bytes of its own
/// in front). Inside are the knob values (0 to 1, in the plugin's parameter order), the modulation matrix, the
/// wavetables' file names, and the preset's name, author and description. Tables that aren't files follow in a
/// second zlib stream (<see cref="OwnTable"/>).
/// </summary>
/// <remarks>
/// Offsets were found in Serum 1.3 states from Solomon's projects (/mnt/project-files/music-organiser-notes/serum),
/// all the same size, and checked against Serum 2 holding the same preset.
/// </remarks>
public sealed class Serum1State
{
    public const int Size = 172_736;

    /// <summary>
    /// A saved preset (.fxp) holds only the start of the same layout, 20,704 bytes, without the wavetables; one from a
    /// later Serum 1 also holds the second half of the modulation matrix. The rest reads as empty.
    /// </summary>
    public const int PresetSize = 20_704;
    const int FxpChunkAt = 60;
    const int KnobsAt = 13_408, LaterKnobsAt = 19_168, FirstLaterKnob = 228;
    const int OscANameAt = 15_368, OscBNameAt = 15_880, NoiseNameAt = 16_392, NameLength = 256;
    const int PresetNameAt = 18_802, AuthorAt = 18_848, DescriptionAt = 18_896;
    const int SlotSize = 40, SlotsAt = 0, MoreSlotsAt = 20_704, SlotCount = 32, MoreSlotsEnd = MoreSlotsAt + 16 * SlotSize;
    const int FxOrderAt = 15_324, FxCount = 10;
    const int OwnTableBytesAt = 18_792, InterpolateAt = 18_800;
    const int MonoAt = 19_544, NoiseOneShotAt = 19_564, NoisePitchTrackAt = 19_568, HallAt = 19_620;

    /// <summary>How many knob values the state holds, one per parameter Serum 1.3 shows a DAW (316, checked on Solomon's PC).</summary>
    public const int KnobCount = 316;

    readonly byte[] _raw;
    float[]? _ownTables;

    /// <summary>
    /// Serum's own chunk as it saves it (the zlib streams and what follows), for writing the sound out as a Serum 1
    /// preset (<see cref="ToFxp"/>).
    /// </summary>
    public byte[] Chunk { get; private init; } = [];

    /// <summary>Whether this came from a preset file, which has no wavetables (and from an older Serum 1, only modulation rows 1 to 16).</summary>
    public bool IsPreset { get; private init; }

    Serum1State(byte[] raw) => _raw = raw;

    /// <summary>
    /// Reads a Serum 1 state, from a VST2 chunk, a VST3 component state, FL Studio's copy of either, or a preset
    /// file (.fxp).
    /// </summary>
    public static Serum1State? Read(ReadOnlySpan<byte> data)
    {
        // A zlib stream starts 78 01 (Serum packs with the lowest setting); FL Studio puts 21 bytes before it, and
        // a preset file's header ("CcnK", "FPCh", "XfsX", the preset name) is 60 bytes.
        var start = -1;
        if (data.Length > FxpChunkAt && data[..4].SequenceEqual("CcnK"u8)) start = FxpChunkAt;
        else
            for (var i = 0; i + 1 < Math.Min(data.Length, 64); i++)
                if (data[i] == 0x78 && (data[i] << 8 | data[i + 1]) % 31 == 0) { start = i; break; }
        if (start < 0) return null;
        // A preset file says how long its chunk is; anywhere else the chunk runs to the end.
        var chunk = start == FxpChunkAt && BinaryPrimitives.ReadInt32BigEndian(data[56..]) is var size and > 0 && size <= data.Length - start
            ? data.Slice(start, size)
            : data[start..];
        try
        {
            var raw = Unzip(chunk, Size + 1);
            var n = raw.Length;
            if (n == Size)
            {
                var whole = new Serum1State(raw) { Chunk = chunk.ToArray() };
                whole._ownTables = whole.ReadOwnTables(chunk);
                return whole;
            }
            // Presets from later Serum 1 versions carry more after the first part (21,524 to 33,872 bytes seen in
            // Serum's own presets): modulation rows 17 to 32, where a whole state has them, then more that isn't
            // understood yet, so that reads as empty.
            if (n >= PresetSize && n < Size)
            {
                var padded = new byte[Size];
                raw.AsSpan(0, Math.Min(n, MoreSlotsEnd)).CopyTo(padded);
                return new Serum1State(padded) { IsPreset = true, Chunk = chunk.ToArray() };
            }
            return null;
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Unpacks a zlib stream, up to <paramref name="max"/> bytes.</summary>
    static byte[] Unzip(ReadOnlySpan<byte> packed, int max)
    {
        using var z = new ZLibStream(new MemoryStream(packed.ToArray()), CompressionMode.Decompress);
        var raw = new byte[max];
        var n = 0;
        while (n < raw.Length && z.Read(raw, n, raw.Length - n) is > 0 and var got) n += got;
        return raw[..n];
    }

    /// <summary>
    /// The waves of tables that aren't files (drawn, or imported into Serum), oscillator A's then B's. Serum saves
    /// them as a second zlib stream after the first, and after that the first one's packed length; the state says
    /// how many bytes of floats each oscillator has.
    /// </summary>
    float[]? ReadOwnTables(ReadOnlySpan<byte> chunk)
    {
        var bytes = OwnTableBytes(0) + OwnTableBytes(1);
        if (bytes == 0 || chunk.Length < 8) return null;
        var firstLength = BinaryPrimitives.ReadInt32LittleEndian(chunk[^4..]);
        if (firstLength <= 0 || firstLength >= chunk.Length - 4) return null;
        try
        {
            var raw = Unzip(chunk[firstLength..^4], bytes + 1);
            if (raw.Length != bytes) return null;
            var floats = new float[bytes / 4];
            for (var i = 0; i < floats.Length; i++) floats[i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(4 * i));
            return floats;
        }
        catch (InvalidDataException) { return null; }
    }

    int OwnTableBytes(int osc) =>
        BinaryPrimitives.ReadInt32LittleEndian(_raw.AsSpan(OwnTableBytesAt + 4 * osc)) is var n and > 0 and <= 256 * 2048 * 4 && n % (2048 * 4) == 0 ? n : 0;

    /// <summary>
    /// An oscillator's own table (one that isn't a file), as 2048-sample frames one after another, or null when it plays
    /// a file. Checked against Serum 2's own copy when it opened Solomon's "requiem serum 1".
    /// </summary>
    public float[]? OwnTable(int osc)
    {
        if (_ownTables is null || OwnTableBytes(osc) == 0) return null;
        var from = osc == 0 ? 0 : OwnTableBytes(0) / 4;
        return _ownTables[from..(from + OwnTableBytes(osc) / 4)];
    }

    /// <summary>Whether Serum smooths between an own table's frames (Serum 2's "interpolate after load").</summary>
    public bool InterpolatesOwnTable(int osc) => _raw[InterpolateAt + osc] != 0;

    // Settings Serum 1 doesn't show a DAW, kept as 0-to-1 values after the later knobs. Which is which was worked out
    // from two sounds and what Serum 2 made of them, so the mono and hall ones (both on in one, off in the other) could
    // be the other way round.
    bool Hidden(int at) => BinaryPrimitives.ReadSingleLittleEndian(_raw.AsSpan(at)) > 0.5f;
    public bool Mono => Hidden(MonoAt);
    public bool HallReverb => Hidden(HallAt);
    public bool NoisePitchTrack => Hidden(NoisePitchTrackAt);

    /// <summary>
    /// The sound as a Serum 1 preset file (.fxp), which Serum 2 can open from its browser exactly as Serum 1 played it.
    /// </summary>
    public byte[] ToFxp(string name)
    {
        var o = new MemoryStream();
        void U32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); o.Write(b); }
        o.Write("CcnK"u8);
        U32(FxpChunkAt + Chunk.Length);   // Serum counts the whole file here, not the usual whole less 8
        o.Write("FPCh"u8);
        U32(1);
        o.Write("XfsX"u8);
        U32(1);
        U32(1);
        var label = new byte[28];
        Encoding.Latin1.GetBytes(name.Length > 27 ? name[..27] : name).CopyTo(label, 0);
        o.Write(label);
        U32(Chunk.Length);
        o.Write(Chunk);
        return o.ToArray();
    }

    /// <summary>
    /// A knob's value from 0 to 1, by its parameter number. The first 228 (up to Macro 4, Amp and the LFO 1 to 4
    /// smooths) are in one block; those added later (Mod 17 on, LFO 5 to 8, FX levels, LFO buses) in another.
    /// </summary>
    /// <remarks>
    /// A preset saved by an older Serum leaves Pitch Bend and the later knobs out (all zero); they read as Serum's
    /// own starting values instead.
    /// </remarks>
    public float Knob(int index)
    {
        if (index >= PitchBend && LaterKnobsMissing) return LaterKnobDefault(index);
        return BinaryPrimitives.ReadSingleLittleEndian(_raw.AsSpan(
            index < FirstLaterKnob ? KnobsAt + 4 * index : LaterKnobsAt + 4 * (index - FirstLaterKnob)));
    }

    const int PitchBend = 227;

    bool LaterKnobsMissing => IsPreset
        && _raw.AsSpan(KnobsAt + 4 * PitchBend, 4).IndexOfAnyExcept((byte)0) < 0
        && _raw.AsSpan(LaterKnobsAt, 4 * (KnobCount - FirstLaterKnob)).IndexOfAnyExcept((byte)0) < 0;

    /// <summary>Serum 1.3's starting value for Pitch Bend and the knobs after it (read from a fresh Serum on Solomon's PC).</summary>
    static float LaterKnobDefault(int index) => index switch
    {
        PitchBend => 0.5f,
        <= 259 => index % 2 == 0 ? 0.5f : 1f, // Mod 17 to 32: amount in the middle, output full
        <= 263 => 0.5f,                       // LFO 5 to 8 rates
        <= 267 => 0f,                         // their smooths
        268 => 0.5f,                          // FX filter pan
        269 => 1f,                            // compressor wet
        <= 272 => 0.5f,                       // multiband compressor bands
        <= 288 => 0f,                         // LFO rises and delays
        <= 298 => 0.5f,                       // FX levels
        _ => 0f,                              // LFO buses and Bypass
    };

    public IReadOnlyList<float> Knobs => Enumerable.Range(0, KnobCount).Select(Knob).ToArray();

    public string PresetName => Text(PresetNameAt, AuthorAt - PresetNameAt);
    public string Author => Text(AuthorAt, DescriptionAt - AuthorAt);
    public string Description => Text(DescriptionAt, 128);

    /// <summary>
    /// The wavetable each oscillator plays and the noise oscillator's sample, as Serum shows them: a factory table
    /// relative to Serum's Tables folder ("Analog\Analog_BD_Sin.wav") or the user's own, starting with '/'.
    /// </summary>
    public string OscATable => Text(OscANameAt, NameLength);
    public string OscBTable => Text(OscBNameAt, NameLength);
    public string NoiseSample => Text(NoiseNameAt, NameLength);

    /// <summary>
    /// The effects in the order the rack plays them, numbered as the rack starts out (Hyper/Dimension, Distortion,
    /// Flanger, Phaser, Chorus, Delay, Compressor, Reverb, EQ, Filter). An older preset that doesn't hold a proper
    /// order reads as that starting order.
    /// </summary>
    public IReadOnlyList<int> FxOrder
    {
        get
        {
            var order = Enumerable.Range(0, FxCount)
                .Select(i => BinaryPrimitives.ReadInt32LittleEndian(_raw.AsSpan(FxOrderAt + 4 * i))).ToArray();
            return order.Order().SequenceEqual(Enumerable.Range(0, FxCount)) ? order : Enumerable.Range(0, FxCount).ToArray();
        }
    }

    /// <summary>
    /// Whether the noise oscillator plays its sample once rather than looping: on in BA Organ Bass and off in "requiem
    /// serum 1", as Serum 2 read them. (The two switch bytes at 18,768 taken for this before were on in both.)
    /// </summary>
    public bool NoiseOneShot => Hidden(NoiseOneShotAt);

    string Text(int at, int max)
    {
        var span = _raw.AsSpan(at, max);
        var end = span.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? span : span[..end]);
    }

    /// <summary>One row of the modulation matrix.</summary>
    /// <param name="Source">Serum's number for the source (0 is none).</param>
    /// <param name="Amount">−1 to 1.</param>
    /// <param name="Destination">The parameter number of the knob it moves, as for <see cref="Knob"/>.</param>
    /// <param name="Bipolar">Whether the amount swings both ways.</param>
    public sealed record ModSlot(int Index, int Source, float Amount, int Destination, bool Bipolar);

    public IReadOnlyList<ModSlot> ModSlots
    {
        get
        {
            var slots = new List<ModSlot>();
            for (var k = 0; k < SlotCount; k++)
            {
                var row = _raw.AsSpan(k < 16 ? SlotsAt + k * SlotSize : MoreSlotsAt + (k - 16) * SlotSize, SlotSize);
                var source = BinaryPrimitives.ReadInt32LittleEndian(row[20..]);
                var amount = BinaryPrimitives.ReadSingleLittleEndian(row[4..]);
                if (source == 0 || amount == 0) continue;
                slots.Add(new(k, source, amount, BinaryPrimitives.ReadUInt16LittleEndian(row[26..]), row[12] == 1));
            }
            return slots;
        }
    }
}
