using System.Buffers.Binary;
using System.Text;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi;

/// <summary>One part found in a MIDI file: a track (or, in a single-track file, a MIDI channel) with its notes.</summary>
/// <param name="Drums">On MIDI channel 10, which General MIDI keeps for drums.</param>
public sealed record MidiFilePart(string Name, int Ppq, IReadOnlyList<NoteEvent> Notes, bool Drums, double? Tempo);

/// <summary>
/// Reads Standard MIDI Files (.mid, types 0, 1 and 2) for the MIDI browser. It only reads: library files stay exactly as
/// they are. Each track becomes a part; a track that holds several MIDI channels (a type 0 file) is split by channel.
/// </summary>
public static class MidiFileReader
{
    public static readonly IReadOnlyList<string> Extensions = [".mid", ".midi", ".smf", ".kar"];

    public static bool IsMidiPath(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<MidiFilePart> Read(string path) => Read(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));

    public static IReadOnlyList<MidiFilePart> Read(ReadOnlySpan<byte> data, string fileName)
    {
        // RIFF-wrapped MIDI (.rmi): the SMF sits in the "data" chunk.
        if (data.Length > 20 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("RMID"u8))
            data = data[20..];
        if (data.Length < 14 || !data[..4].SequenceEqual("MThd"u8)) throw new InvalidDataException("Not a MIDI file");
        var headerLength = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
        var trackCount = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
        var division = BinaryPrimitives.ReadInt16BigEndian(data[12..]);
        // SMPTE timing (negative division) is rare in loop packs: treat its ticks per frame as ticks per beat at 120 BPM.
        var ppq = division > 0 ? (int)division : Math.Max(24, -(division >> 8) * (division & 0xFF) / 2);

        var pos = 8 + headerLength;
        var tracks = new List<(string Name, Dictionary<int, List<NoteEvent>> ByChannel)>();
        double? tempo = null;
        for (var t = 0; t < trackCount && pos + 8 <= data.Length; t++)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data[(pos + 4)..]);
            var isTrack = data.Slice(pos, 4).SequenceEqual("MTrk"u8);
            var body = data.Slice(pos + 8, Math.Clamp(length, 0, data.Length - pos - 8));
            pos += 8 + Math.Max(0, length);
            if (!isTrack) { t--; continue; }   // unknown chunk: skip it, it isn't a track
            tracks.Add(ReadTrack(body, ref tempo));
        }

        var parts = new List<MidiFilePart>();
        var named = tracks.Count(tr => tr.ByChannel.Count > 0);
        foreach (var (name, byChannel) in tracks)
        {
            foreach (var (channel, notes) in byChannel.OrderBy(kv => kv.Key))
            {
                if (notes.Count == 0) continue;
                var label = name.Length > 0 && !name.Equals(fileName, StringComparison.OrdinalIgnoreCase) ? name : "";
                if (byChannel.Count > 1) label = (label.Length > 0 ? label + " · " : "") + $"Ch {channel + 1}";
                parts.Add(new MidiFilePart(named == 1 && byChannel.Count == 1 ? "" : label, ppq,
                    notes.OrderBy(n => n.Start).ThenBy(n => n.Key).ToList(), channel == 9, tempo));
            }
        }
        return parts;
    }

    static (string Name, Dictionary<int, List<NoteEvent>> ByChannel) ReadTrack(ReadOnlySpan<byte> s, ref double? tempo)
    {
        var name = "";
        var byChannel = new Dictionary<int, List<NoteEvent>>();
        // Notes still held: (channel, key) → when they started and how hard. A key struck again before it's released
        // stacks, so each release ends the earliest one.
        var held = new Dictionary<(int, int), Queue<(long Start, int Velocity)>>();
        long tick = 0;
        var i = 0;
        byte status = 0, running = 0;
        while (i < s.Length)
        {
            tick += Vlq(s, ref i);
            if (i >= s.Length) break;
            var b = s[i];
            if (b >= 0x80) { status = b; i++; if (b < 0xF0) running = b; }
            else if (status < 0x80) break;   // data with no status to run on: the track is damaged from here
            if (status == 0xFF)
            {
                if (i >= s.Length) break;
                var type = s[i++];
                var len = (int)Vlq(s, ref i);
                if (len < 0 || i + len > s.Length) break;
                var payload = s.Slice(i, len);
                if (type == 0x03 && name.Length == 0) name = Text(payload);
                else if (type == 0x51 && len == 3 && tempo is null)
                {
                    var us = payload[0] << 16 | payload[1] << 8 | payload[2];
                    if (us > 0) tempo = Math.Round(60_000_000.0 / us, 2);
                }
                else if (type == 0x2F) break;
                i += len;
                // The spec says meta and sysex events cancel running status, but plenty of files (karaoke files with
                // their lyrics, for one) carry on with it after them: going on with the last notes' status reads those.
                status = running;
                continue;
            }
            if (status is 0xF0 or 0xF7)
            {
                var len = (int)Vlq(s, ref i);
                i += Math.Max(0, len);
                status = running;
                continue;
            }
            var kind = status & 0xF0;
            var channel = status & 0x0F;
            var dataBytes = kind is 0xC0 or 0xD0 ? 1 : 2;
            if (i + dataBytes > s.Length) break;
            int d1 = s[i], d2 = dataBytes == 2 ? s[i + 1] : 0;
            i += dataBytes;
            if (kind == 0x90 && d2 > 0)
            {
                if (!held.TryGetValue((channel, d1), out var q)) held[(channel, d1)] = q = new();
                q.Enqueue((tick, d2));
            }
            else if (kind == 0x80 || kind == 0x90)
            {
                if (held.TryGetValue((channel, d1), out var q) && q.Count > 0)
                {
                    var (start, velocity) = q.Dequeue();
                    Add(byChannel, channel, new NoteEvent(start, Math.Max(1, tick - start), d1 & 0x7F, Math.Clamp(velocity, 1, 127)));
                }
            }
        }
        // Notes never released end with the track.
        foreach (var ((channel, key), q) in held)
            while (q.Count > 0)
            {
                var (start, velocity) = q.Dequeue();
                Add(byChannel, channel, new NoteEvent(start, Math.Max(1, tick - start), key & 0x7F, Math.Clamp(velocity, 1, 127)));
            }
        return (name.Trim(), byChannel);
    }

    static void Add(Dictionary<int, List<NoteEvent>> byChannel, int channel, NoteEvent n)
    {
        if (!byChannel.TryGetValue(channel, out var list)) byChannel[channel] = list = new();
        list.Add(n);
    }

    static long Vlq(ReadOnlySpan<byte> s, ref int i)
    {
        long v = 0;
        for (var n = 0; n < 4 && i < s.Length; n++)
        {
            var b = s[i++];
            v = (v << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return v;
    }

    static string Text(ReadOnlySpan<byte> b)
    {
        // Track names are usually plain ASCII; Latin-1 keeps odd bytes readable rather than failing.
        try { return new UTF8Encoding(false, true).GetString(b).Trim('\0', ' '); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(b).Trim('\0', ' '); }
    }
}
