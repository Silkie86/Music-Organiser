using System.Text;

namespace MusicOrganiser.Core.DawLink;

/// <summary>
/// A connection to the MIDI port FL Studio runs the Music Organiser script for: whole SysEx messages out and in.
/// <see cref="FlMidi"/> is the real port; the tests use the script itself, run in a stand-in FL Studio.
/// </summary>
public interface IFlMidi
{
    /// <summary>Sends one SysEx message, F0 to F7, into the port.</summary>
    void Send(byte[] message);

    /// <summary>Each SysEx message that comes in, F0 to F7, on whatever thread it arrives.</summary>
    event Action<byte[]>? Received;
}

/// <summary>One of the link's messages: part of a request or an answer.</summary>
/// <param name="Text">This part of the JSON text, plain ASCII.</param>
public sealed record FlMessage(byte Kind, int Id, int Part, int Parts, byte[] Text);

/// <summary>
/// The SysEx messages the app and FL Studio's script talk in (FL Studio doesn't let scripts open files or connect to
/// anything, but they can send and receive MIDI). Each message is F0, 7D (the maker ID MIDI keeps for
/// non-commercial use), "MO", whether it's a request (1) or an answer (2), the request's id in three 7-bit bytes,
/// this part's number and how many parts there are in two 7-bit bytes each (lowest first), up to
/// <see cref="PartBytes"/> of the JSON text, which is plain ASCII, and F7. The script does the same
/// (read_message and messages in extensions/fl-studio/device_MusicOrganiser.py).
/// </summary>
public static class FlMessages
{
    public const byte Request = 1, Answer = 2;
    public const int PartBytes = 512;
    /// <summary>Ids fit in three 7-bit bytes.</summary>
    public const int Ids = 1 << 21;
    static readonly byte[] Head = [0xF0, 0x7D, 0x4D, 0x4F];

    /// <summary>The messages that carry this JSON text, which must be plain ASCII.</summary>
    public static IReadOnlyList<byte[]> Write(byte kind, int id, string json)
    {
        if (id is < 0 or >= Ids) throw new ArgumentOutOfRangeException(nameof(id));
        if (json.Any(c => c > 0x7F)) throw new ArgumentException("The link's messages only carry plain ASCII.", nameof(json));
        var text = Encoding.ASCII.GetBytes(json);
        var parts = Math.Max(1, (text.Length + PartBytes - 1) / PartBytes);
        if (parts >= 1 << 14) throw new ArgumentException("Too long for the link's messages.", nameof(json));
        var found = new List<byte[]>(parts);
        for (var part = 0; part < parts; part++)
        {
            var chunk = text.AsSpan(part * PartBytes, Math.Min(PartBytes, text.Length - part * PartBytes));
            var m = new byte[Head.Length + 8 + chunk.Length + 1];
            Head.CopyTo(m, 0);
            m[4] = kind;
            m[5] = (byte)(id & 0x7F);
            m[6] = (byte)(id >> 7 & 0x7F);
            m[7] = (byte)(id >> 14 & 0x7F);
            m[8] = (byte)(part & 0x7F);
            m[9] = (byte)(part >> 7 & 0x7F);
            m[10] = (byte)(parts & 0x7F);
            m[11] = (byte)(parts >> 7 & 0x7F);
            chunk.CopyTo(m.AsSpan(12));
            m[^1] = 0xF7;
            found.Add(m);
        }
        return found;
    }

    /// <summary>The message, if it's one of the link's, or null for any other SysEx message.</summary>
    public static FlMessage? Read(ReadOnlySpan<byte> message)
    {
        if (message.Length < 13 || !message[..4].SequenceEqual(Head) || message[^1] != 0xF7) return null;
        foreach (var b in message[4..^1])
            if (b > 0x7F) return null;
        var id = message[5] | message[6] << 7 | message[7] << 14;
        int part = message[8] | message[9] << 7, parts = message[10] | message[11] << 7;
        return part < parts ? new(message[4], id, part, parts, message[12..^1].ToArray()) : null;
    }
}

/// <summary>The parts of one request's answer as they come in, and its whole text once they're all there.</summary>
public sealed class FlAnswer(int id)
{
    readonly Dictionary<int, byte[]> _parts = [];
    int _expected;

    public int Id => id;

    /// <summary>Whether any part has come in.</summary>
    public bool Started { get; private set; }

    /// <summary>Takes a message if it's part of this answer, and returns the whole text once it's complete.</summary>
    public string? Take(FlMessage m)
    {
        if (m.Kind != FlMessages.Answer || m.Id != id) return null;
        if (_expected != m.Parts)
        {
            _parts.Clear();   // sent again, split differently
            _expected = m.Parts;
        }
        Started = true;
        _parts[m.Part] = m.Text;
        if (_parts.Count < _expected) return null;
        var text = new StringBuilder();
        for (var p = 0; p < _expected; p++) text.Append(Encoding.ASCII.GetString(_parts[p]));
        return text.ToString();
    }
}
