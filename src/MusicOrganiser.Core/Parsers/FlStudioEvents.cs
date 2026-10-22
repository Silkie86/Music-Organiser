using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace MusicOrganiser.Core.Parsers;

/// <summary>One event in an FL Studio project, as FL saved it: its id, where its data starts in the file, and the data.</summary>
public sealed record FlEvent(int Index, int Id, int Offset, ReadOnlyMemory<byte> Payload)
{
    /// <summary>The value of a one-, two- or four-byte event (ids below 192).</summary>
    public uint Int
    {
        get
        {
            var s = Payload.Span;
            return s.Length switch
            {
                1 => s[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(s),
                3 => (uint)(s[0] | s[1] << 8 | s[2] << 16),
                >= 4 => BinaryPrimitives.ReadUInt32LittleEndian(s),
                _ => 0,
            };
        }
    }

    /// <summary>Ids below 192 hold a fixed-size number; from 192 the data has its own length.</summary>
    public bool IsNumber => Id < 192;
}

/// <summary>An FL Studio project's events in file order, before any of them is interpreted.</summary>
public sealed record FlEventStream(string Version, int Ppq, bool Utf16, IReadOnlyList<FlEvent> Events)
{
    /// <summary>FL's major version, or 0 when the project doesn't say.</summary>
    public int Major => int.TryParse(Version.Split('.')[0], out var v) ? v : 0;

    /// <summary>A text event's text: UTF-16 from FL 11.5, 8-bit before that.</summary>
    public string Text(ReadOnlySpan<byte> data) =>
        (Utf16 ? Encoding.Unicode.GetString(data) : Encoding.Latin1.GetString(data)).TrimEnd('\0');
}

/// <summary>A record in one of FL's record lists (a Fruity Wrapper state, or the VST3 state inside one).</summary>
public readonly record struct FlRecord(int Id, int Offset, int Length);

public sealed partial class FlStudioReader
{
    /// <summary>The project's events as stored. Throws <see cref="InvalidDataException"/> when it isn't an FL Studio project.</summary>
    public static FlEventStream ReadEvents(byte[] raw)
    {
        var flp = Load(raw);
        var events = new List<FlEvent>(flp.Events.Count);
        foreach (var e in flp.Events)
        {
            // Only what's in the file: the reader adds events of its own (an old FL's step sequencer dots read as notes),
            // and a writer copying the file between events would go backwards at one (Fix on Solomon's FL 4 "grimey").
            if (!MemoryMarshal.TryGetArray(e.Payload, out var segment) || segment.Array != raw) continue;
            events.Add(new FlEvent(events.Count, e.Id, segment.Offset, e.Payload));
        }
        return new FlEventStream(flp.Version, flp.Ppq, flp.Utf16, events);
    }

    /// <summary>
    /// The records of a list FL writes as a u32 version, then (u32 id, u64 size, data) for each: the Fruity
    /// Wrapper's state, and the VST3 state inside its record 53. Null when the data isn't laid out that way.
    /// </summary>
    public static IReadOnlyList<FlRecord>? Records(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16) return null;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (version is 0 or > 64) return null;
        var records = new List<FlRecord>();
        var pos = 4;
        while (pos + 12 <= data.Length)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
            var size = BinaryPrimitives.ReadUInt64LittleEndian(data[(pos + 4)..]);
            pos += 12;
            if (id > 0xFFFF || size > (ulong)(data.Length - pos)) return null;
            records.Add(new FlRecord((int)id, pos, (int)size));
            pos += (int)size;
        }
        return records.Count > 0 && data.Length - pos < 12 ? records : null;
    }
}
