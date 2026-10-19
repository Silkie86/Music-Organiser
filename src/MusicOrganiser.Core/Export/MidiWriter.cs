using System.Buffers.Binary;
using System.Text;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Export;

/// <summary>Writes Standard MIDI Files (type 1): a first track with the clip's name, plus one track per instrument.</summary>
public static class MidiWriter
{
    /// <summary>
    /// The clip as a MIDI file, with no tempo in it: a DAW asks whether to take a MIDI file's tempo for the project (Bitwig
    /// does every time), and the app's MIDI never should (asked for on 2026-10-18). The notes are in beats, so they land
    /// in the same places at any tempo.
    /// </summary>
    /// <param name="tempo">Not written, for the reason above; kept so that callers passing the project's tempo still build.</param>
    public static byte[] Write(MidiClip clip, double? tempo = null)
    {
        var tracks = new List<byte[]>();
        tracks.Add(Track([(0, Meta(0x03, clip.Name))]));

        var i = 0;
        foreach (var (name, notes) in clip.Tracks)
        {
            var ch = (byte)(i++ % 16);
            var events = new List<(long, byte[])> { (0, Meta(0x03, name)) };
            foreach (var n in notes)
            {
                events.Add((n.Start, [(byte)(0x90 | ch), (byte)n.Key, (byte)n.Velocity]));
                events.Add((n.Start + n.Length, [(byte)(0x80 | ch), (byte)n.Key, 0]));
            }
            tracks.Add(Track(events));
        }

        using var ms = new MemoryStream();
        ms.Write("MThd"u8);
        WriteBE32(ms, 6);
        WriteBE16(ms, 1);
        WriteBE16(ms, (ushort)tracks.Count);
        WriteBE16(ms, (ushort)clip.Ppq);
        foreach (var t in tracks) ms.Write(t);
        return ms.ToArray();
    }

    static byte[] Track(List<(long Tick, byte[] Msg)> events)
    {
        // Stable sort by time; at equal times note-offs go before note-ons so repeated notes don't cut each other.
        var ordered = events.Select((e, idx) => (e, idx))
            .OrderBy(x => x.e.Tick)
            .ThenBy(x => (x.e.Msg[0] & 0xF0) == 0x80 ? 0 : 1)
            .ThenBy(x => x.idx)
            .Select(x => x.e);
        using var body = new MemoryStream();
        long last = 0;
        foreach (var (tick, msg) in ordered)
        {
            WriteVlq(body, tick - last);
            body.Write(msg);
            last = tick;
        }
        body.Write([0x00, 0xFF, 0x2F, 0x00]);

        using var ms = new MemoryStream();
        ms.Write("MTrk"u8);
        WriteBE32(ms, (int)body.Length);
        body.Position = 0;
        body.CopyTo(ms);
        return ms.ToArray();
    }

    static byte[] Meta(byte type, string text)
    {
        var b = Encoding.UTF8.GetBytes(text);
        if (b.Length > 255) b = b[..255];
        using var ms = new MemoryStream();
        ms.WriteByte(0xFF);
        ms.WriteByte(type);
        WriteVlq(ms, b.Length);
        ms.Write(b);
        return ms.ToArray();
    }

    static void WriteVlq(Stream s, long value)
    {
        Span<byte> buf = stackalloc byte[10];
        var i = buf.Length;
        buf[--i] = (byte)(value & 0x7F);
        while ((value >>= 7) > 0) buf[--i] = (byte)((value & 0x7F) | 0x80);
        s.Write(buf[i..]);
    }

    static void WriteBE32(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
    static void WriteBE16(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }
}
