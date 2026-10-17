using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MusicOrganiser.Core.Plugins.Serum;

/// <summary>
/// The original Serum's saved state. Its VST2 chunk and its VST3 component state are the same thing: a zlib stream
/// that unpacks to 172,736 bytes laid out the same way every time (FL Studio's wrapper puts a few bytes of its own
/// in front). Inside are the knob values (0 to 1, in the plugin's parameter order), the modulation matrix, the
/// wavetables themselves and their file names, and the preset's name, author and description.
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
    const int FxOrderAt = 15_324, FxCount = 10, NoiseFlagsAt = 18_768;

    /// <summary>How many knob values the state holds, one per parameter Serum 1.3 shows a DAW (316, checked on Solomon's PC).</summary>
    public const int KnobCount = 316;

    readonly byte[] _raw;

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
        try
        {
            using var z = new ZLibStream(new MemoryStream(data[start..].ToArray()), CompressionMode.Decompress);
            var raw = new byte[Size + 1];
            var n = 0;
            while (n < raw.Length && z.Read(raw, n, raw.Length - n) is > 0 and var got) n += got;
            if (n == Size) return new Serum1State(raw[..Size]);
            // Presets from later Serum 1 versions carry more after the first part (21,524 to 33,872 bytes seen in
            // Serum's own presets): modulation rows 17 to 32, where a whole state has them, then more that isn't
            // understood yet, so that reads as empty.
            if (n >= PresetSize && n < Size)
            {
                Array.Clear(raw, MoreSlotsEnd, raw.Length - MoreSlotsEnd);
                return new Serum1State(raw[..Size]) { IsPreset = true };
            }
            return null;
        }
        catch (InvalidDataException) { return null; }
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
    /// Whether the noise oscillator plays its sample once rather than looping. Taken from the first of two switch
    /// bytes after the LFO shapes; both were on in the one state checked, where Serum 2 also turned one-shot on, so
    /// which byte is which isn't confirmed (<see cref="NoiseFlagsAsChecked"/>).
    /// </summary>
    public bool NoiseOneShot => _raw[NoiseFlagsAt] != 0;

    /// <summary>Whether both of those switch bytes are as they were in the state that was checked.</summary>
    public bool NoiseFlagsAsChecked => _raw[NoiseFlagsAt] == 1 && _raw[NoiseFlagsAt + 1] == 1;

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
