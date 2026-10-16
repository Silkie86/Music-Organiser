using System.Buffers.Binary;
using System.Text;

namespace MusicOrganiser.Core.Parsers;

public sealed partial class FlStudioReader
{
    /// <summary>
    /// A copy of an FL Studio project with sample paths pointed somewhere else. <paramref name="relink"/> maps a sample
    /// path, as the app lists it (with %FLStudioUserData% worked out), to the file to use instead. Only those sample
    /// events change; every other byte is copied as it was, and the FLdt chunk's length follows the new paths.
    /// </summary>
    public static (byte[] Data, int Changed) RelinkSamples(byte[] raw, string projectPath, IReadOnlyDictionary<string, string> relink)
    {
        var stream = ReadEvents(raw);
        var dt = 8 + BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4));
        var start = dt + 8;
        var dataEnd = Math.Min(raw.Length, start + BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(dt + 4)));

        var body = new MemoryStream(dataEnd - start + 256);
        var changed = 0;
        var pos = start;   // events sit back to back, so each one's id byte starts where the last one's data ended
        foreach (var e in stream.Events)
        {
            var end = e.Offset + e.Payload.Length;
            if (e.Id == EvSamplePath && New(stream.Text(e.Payload.Span)) is { } to)
            {
                body.WriteByte((byte)e.Id);
                var text = stream.Utf16 ? Encoding.Unicode.GetBytes(to + "\0") : Encoding.Latin1.GetBytes(to + "\0");
                for (var n = text.Length; ; )
                {
                    var b = (byte)(n & 0x7F);
                    n >>= 7;
                    body.WriteByte(n > 0 ? (byte)(b | 0x80) : b);
                    if (n == 0) break;
                }
                body.Write(text);
                changed++;
            }
            else body.Write(raw, pos, end - pos);
            pos = end;
        }
        body.Write(raw, pos, dataEnd - pos);   // anything after the last whole event

        var file = new MemoryStream(raw.Length + (int)body.Length - (dataEnd - start));
        file.Write(raw, 0, dt + 4);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, (int)body.Length);
        file.Write(len);
        body.WriteTo(file);
        file.Write(raw, dataEnd, raw.Length - dataEnd);   // whatever FL keeps after the chunk, as it was
        return (file.ToArray(), changed);

        string? New(string sample) =>
            relink.TryGetValue(sample, out var to) || relink.TryGetValue(ResolveSamplePath(sample, projectPath), out to) ? to : null;
    }
}

public sealed partial class BitwigReader
{
    /// <summary>
    /// A copy of a Bitwig project with sample paths pointed somewhere else: every file reference (and used_files entry)
    /// whose path, or path inside the project folder, is a key of <paramref name="relink"/>. Null when the project can't
    /// be decoded cleanly enough to write back.
    /// </summary>
    public static (byte[] Data, int Changed)? RelinkSamples(byte[] raw, string projectPath, IReadOnlyDictionary<string, string> relink)
    {
        if (!BitwigFormat.IsBitwig(raw)) return null;
        var doc = BitwigFormat.Decode(raw);
        if (doc.Root is null || doc.BodyError is not null) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
        var changed = 0;

        foreach (var o in doc.Objects.Where(o => o.Class == ClsFileRef))
            if (o.Str(FFilePath) is { Length: > 0 } f && TextScan.IsAudioPath(f) && New(f) is { } to)
            {
                o.Set(FFilePath, to);
                changed++;
            }
        if (doc.Meta.GetValueOrDefault("used_files") is List<string> used)
            for (var i = 0; i < used.Count; i++)
                if (New(used[i]) is { } to)
                {
                    used[i] = to;
                    changed++;
                }
        return (BitwigFormat.Encode(doc), changed);

        string? New(string f)
        {
            if (relink.TryGetValue(f, out var to)) return to;
            if (IsAbsolute(f)) return null;
            return relink.TryGetValue(Path.GetFullPath(Path.Combine(dir, f)), out to) ? to : null;
        }
    }
}
