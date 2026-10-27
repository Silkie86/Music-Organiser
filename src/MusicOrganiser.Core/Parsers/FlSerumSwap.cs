using System.Buffers.Binary;
using System.Text;
using MusicOrganiser.Core.Plugins.Serum;

namespace MusicOrganiser.Core.Parsers;

/// <summary>
/// What a Serum swap did: the new file, how many Serums it swapped, and notes for the person; and each swapped
/// Serum's own sound as a Serum 1 preset, by the channel or track it was on, which Serum 2 opens exactly.
/// </summary>
public sealed record SerumSwap(byte[] Data, int Swapped, IReadOnlyList<string> Notes, IReadOnlyList<(string Name, byte[] Fxp)>? Originals = null);

public sealed partial class FlStudioReader
{
    /// <summary>Serum 1's VST2 id as FL stores it, and the two Serums' VST3 class ids as a .vstpreset writes them.</summary>
    static ReadOnlySpan<byte> Serum1FourCc => "XsfX"u8;
    const string Serum1Vst3 = "56535458667358736572756D00000000", Serum2Vst3 = "56534558667350736572756D20320000";
    const int KindVst3Synth = 8;

    /// <summary>
    /// A copy of an FL Studio project with every Serum 1 (VST2 or VST3) swapped for Serum 2's VST3, with its sound
    /// translated (<see cref="Serum1To2"/>): the Fruity Wrapper's state is rewritten as FL saves Serum 2's, and
    /// every other event is copied as it was. FL reads Serum 2's own list of settings when it loads it.
    /// </summary>
    /// <param name="serum2Path">Where Serum 2's VST3 is, which FL is pointed at.</param>
    /// <param name="audio">Finds Serum's wavetables, as for <see cref="Serum1To2.Translate"/>.</param>
    public static SerumSwap SwapSerum(byte[] raw, string serum2Path, Func<string, Serum1To2.AudioInfo?>? audio = null)
    {
        var stream = ReadEvents(raw);
        var changes = new Dictionary<int, byte[]>();
        var notes = new List<string>();
        var swapped = 0;
        var originals = new List<(string, byte[])>();
        FlEvent? name = null;
        foreach (var e in stream.Events)
        {
            if (e.Id is EvChannelNew or EvMixerSlot) name = null;
            if (e.Id == EvPluginName) name = e;
            if (e.Id != EvPluginState || Records(e.Payload.Span) is not { } records) continue;
            var payload = e.Payload;
            byte[]? Field(int id) => records.FirstOrDefault(r => r.Id == id) is { Length: > 0 } r ? payload.Span.Slice(r.Offset, r.Length).ToArray() : null;
            var vst3 = Field(VstClassId) is { Length: 16 } cid && VstPreset.ClassIdString(cid) == Serum1Vst3;
            if (!vst3 && !(Field(VstFourCc) is { } cc && cc.AsSpan().SequenceEqual(Serum1FourCc))) continue;

            var channel = name is { } n ? stream.Text(n.Payload.Span) : "A channel";
            var state = Field(VstState) ?? [];
            var chunk = vst3 ? Vst3State(state)?.Component : state;   // a VST2's state has FL's own 21 bytes in front, which Serum1State skips
            if (chunk is null || Serum1State.Read(chunk) is not { } serum1)
            {
                notes.Add($"{channel}: Serum's sound couldn't be read, so it's left as Serum.");
                continue;
            }
            var result = Serum1To2.Translate(serum1, audio);
            originals.Add((channel, serum1.ToFxp(channel)));
            changes[e.Index] = Serum2Wrapper(payload.Span, records, result.State.Write(), serum2Path);
            if (name is { } plugin && channel == "Serum") changes[plugin.Index] = TextPayload(stream, "Serum 2");
            swapped++;
            notes.Add(result.Rough.Count == 0
                ? $"{channel}: Serum swapped for Serum 2, with its sound translated."
                : $"{channel}: Serum swapped for Serum 2, with its sound translated (only roughly: {Serum1To2.Describe(result.Rough)}).");
        }
        if (swapped == 0) return new SerumSwap(raw, 0, notes, originals);
        var data = Rewrite(raw, stream, changes);
        SameApartFrom(stream, ReadEvents(data), changes);
        return new SerumSwap(data, swapped, notes, originals);
    }

    /// <summary>
    /// Serum 2's Fruity Wrapper state in place of Serum 1's: FL's own settings for the plugin (window, routing) kept,
    /// the plugin's identity changed, and its state as FL saves a VST3's (record 1, then the component state, then
    /// an empty controller state, which Serum 2 rebuilds from the component).
    /// </summary>
    static byte[] Serum2Wrapper(ReadOnlySpan<byte> wrapper, IReadOnlyList<FlRecord> records, byte[] component, string serum2Path)
    {
        var classId = Convert.FromHexString(Serum2Vst3);
        classId.AsSpan(0, 4).Reverse();   // in memory order: the first three groups byte-swapped
        classId.AsSpan(4, 2).Reverse();
        classId.AsSpan(6, 2).Reverse();

        var vst3 = new MemoryStream();
        U32(vst3, 1);
        var first = new byte[64];
        first[0] = 1;   // as FL saves Serum 2
        Record(vst3, 1, first);
        Record(vst3, 3, component);
        Record(vst3, 2, []);

        var o = new MemoryStream();
        o.Write(wrapper[..4]);   // the wrapper's version
        foreach (var r in records)
        {
            var data = wrapper.Slice(r.Offset, r.Length);
            switch (r.Id)
            {
                case VstKind:
                    var kind = data.ToArray();
                    BinaryPrimitives.WriteInt32LittleEndian(kind, KindVst3Synth);
                    Record(o, VstKind, kind);
                    Record(o, VstClassId, classId);
                    break;
                case VstFourCc or VstClassId or 57: break;   // the VST2's id, an old class id, and the VST2-only record 57
                case VstName: Record(o, VstName, "Serum2"u8.ToArray()); break;
                case VstPath: Record(o, VstPath, Encoding.UTF8.GetBytes(serum2Path)); break;
                case VstVendor: Record(o, VstVendor, "Xfer Records"u8.ToArray()); break;
                case VstState: Record(o, VstState, vst3.ToArray()); break;
                default: Record(o, r.Id, data.ToArray()); break;
            }
        }
        return o.ToArray();

        static void U32(Stream s, uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            s.Write(b);
        }
        static void Record(Stream s, int id, byte[] data)
        {
            Span<byte> b = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)id);
            BinaryPrimitives.WriteUInt64LittleEndian(b[4..], (ulong)data.Length);
            s.Write(b);
            s.Write(data);
        }
    }

    static byte[] TextPayload(FlEventStream stream, string text) =>
        stream.Utf16 ? Encoding.Unicode.GetBytes(text + "\0") : Encoding.Latin1.GetBytes(text + "\0");

    /// <summary>
    /// The project with some events' data replaced (by event index); every other byte is copied as it was, and the
    /// FLdt chunk's length follows the new data.
    /// </summary>
    internal static byte[] Rewrite(byte[] raw, FlEventStream stream, IReadOnlyDictionary<int, byte[]> changes)
    {
        var dt = 8 + BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4));
        var start = dt + 8;
        // As the reader does: an old save with junk where "FLdt" and its length belong has its events run to the end
        // of the file, and its junk is copied as it was (FL opened it like that).
        var named = Encoding.ASCII.GetString(raw, dt, 4) == "FLdt";
        var dataEnd = named
            ? (int)Math.Clamp((long)start + BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(dt + 4)), start, raw.Length)
            : raw.Length;

        var body = new MemoryStream(dataEnd - start + 256);
        var pos = start;   // events sit back to back, so each one's id byte starts where the last one's data ended
        foreach (var e in stream.Events)
        {
            var end = e.Offset + e.Payload.Length;
            if (changes.TryGetValue(e.Index, out var data))
            {
                body.WriteByte((byte)e.Id);
                for (var n = data.Length; ; )
                {
                    var b = (byte)(n & 0x7F);
                    n >>= 7;
                    body.WriteByte(n > 0 ? (byte)(b | 0x80) : b);
                    if (n == 0) break;
                }
                body.Write(data);
            }
            else body.Write(raw, pos, end - pos);
            pos = end;
        }
        body.Write(raw, pos, dataEnd - pos);   // anything after the last whole event

        var file = new MemoryStream(raw.Length + (int)body.Length - (dataEnd - start));
        file.Write(raw, 0, dt + 4);
        Span<byte> len = stackalloc byte[4];
        if (named) BinaryPrimitives.WriteInt32LittleEndian(len, (int)body.Length);
        else raw.AsSpan(dt + 4, 4).CopyTo(len);
        file.Write(len);
        body.WriteTo(file);
        file.Write(raw, dataEnd, raw.Length - dataEnd);   // whatever FL keeps after the chunk, as it was
        return file.ToArray();
    }
}
