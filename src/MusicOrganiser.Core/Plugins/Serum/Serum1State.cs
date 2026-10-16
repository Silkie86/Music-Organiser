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
    const int KnobsAt = 13_408, LaterKnobsAt = 19_168, FirstLaterKnob = 228;
    const int OscANameAt = 15_368, OscBNameAt = 15_880, NoiseNameAt = 16_392, NameLength = 256;
    const int PresetNameAt = 18_802, AuthorAt = 18_848, DescriptionAt = 18_896;
    const int SlotSize = 40, SlotsAt = 0, MoreSlotsAt = 20_704, SlotCount = 32;

    /// <summary>How many knob values the state holds, one per parameter Serum 1.3 shows a DAW (316, checked on Solomon's PC).</summary>
    public const int KnobCount = 316;

    readonly byte[] _raw;

    Serum1State(byte[] raw) => _raw = raw;

    /// <summary>Reads a Serum 1 state, from a VST2 chunk, a VST3 component state or FL Studio's copy of either.</summary>
    public static Serum1State? Read(ReadOnlySpan<byte> data)
    {
        // A zlib stream starts 78 01 (Serum packs with the lowest setting); FL Studio puts 21 bytes before it.
        var start = -1;
        for (var i = 0; i + 1 < Math.Min(data.Length, 64); i++)
            if (data[i] == 0x78 && (data[i] << 8 | data[i + 1]) % 31 == 0) { start = i; break; }
        if (start < 0) return null;
        try
        {
            using var z = new ZLibStream(new MemoryStream(data[start..].ToArray()), CompressionMode.Decompress);
            var raw = new byte[Size + 1];
            var n = 0;
            while (n < raw.Length && z.Read(raw, n, raw.Length - n) is > 0 and var got) n += got;
            return n == Size ? new Serum1State(raw[..Size]) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>
    /// A knob's value from 0 to 1, by its parameter number. The first 228 (up to Macro 4, Amp and the LFO 1 to 4
    /// smooths) are in one block; those added later (Mod 17 on, LFO 5 to 8, FX levels, LFO buses) in another.
    /// </summary>
    public float Knob(int index) => BinaryPrimitives.ReadSingleLittleEndian(_raw.AsSpan(
        index < FirstLaterKnob ? KnobsAt + 4 * index : LaterKnobsAt + 4 * (index - FirstLaterKnob)));

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
