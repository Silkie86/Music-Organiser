using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZstdSharp;

namespace MusicOrganiser.Core.Plugins.Serum;

/// <summary>
/// Serum 2's saved state, as its VST3 component ('Comp') or controller ('Cont') state holds it:
/// "XferJson", a zero byte, the length of a JSON header, the header, the length of the settings once unpacked,
/// the number 2, then the settings packed with zstd. The settings are CBOR: a map of modules ("Oscillator0",
/// "Env0", "VoiceFilter0", "ModSlot0", …), each with "plainParams" set to "default" or to a map of named values
/// in real units (seconds, percent, …). The header's "hash" is the MD5 of the packed bytes.
/// </summary>
/// <remarks>Checked byte for byte against Serum 2.0.16's own states, which this writes back unchanged.</remarks>
public sealed record Serum2State(JsonObject Header, JsonObject Settings)
{
    static readonly byte[] Magic = "XferJson\0"u8.ToArray();

    public static bool Is(ReadOnlySpan<byte> data) => data.StartsWith(Magic);

    /// <summary>The header alone (preset name, author and so on are in the controller's), without unpacking anything.</summary>
    public static JsonObject? ReadHeader(ReadOnlySpan<byte> data) =>
        Split(data, out var header, out _) ? header : null;

    public static Serum2State? Read(ReadOnlySpan<byte> data)
    {
        if (!Split(data, out var header, out var packed)) return null;
        try
        {
            using var zstd = new Decompressor();
            var raw = zstd.Unwrap(packed).ToArray();
            var pos = 0;
            return Cbor.Read(raw, ref pos) is JsonObject settings && pos == raw.Length ? new(header, settings) : null;
        }
        catch (Exception e) when (e is ZstdException or InvalidDataException or ArgumentException) { return null; }
    }

    static bool Split(ReadOnlySpan<byte> data, out JsonObject header, out ReadOnlySpan<byte> packed)
    {
        header = null!;
        packed = default;
        if (!Is(data) || data.Length < Magic.Length + 8) return false;
        var length = BinaryPrimitives.ReadInt64LittleEndian(data[Magic.Length..]);
        var start = Magic.Length + 8;
        if (length < 2 || length > data.Length - start - 8) return false;
        try
        {
            if (JsonNode.Parse(data.Slice(start, (int)length)) is not JsonObject o) return false;
            header = o;
        }
        catch (JsonException) { return false; }
        var rest = data[(start + (int)length)..];
        if (BinaryPrimitives.ReadInt32LittleEndian(rest[4..]) != 2) return false;
        packed = rest[8..];
        return true;
    }

    public byte[] Write()
    {
        var raw = Cbor.Write(Settings);
        using var zstd = new Compressor(3);
        var packed = zstd.Wrap(raw).ToArray();
        var header = (JsonObject)Header.DeepClone();
        header["hash"] = Convert.ToHexStringLower(MD5.HashData(packed));
        // Serum writes its format version as 6.0; a double made in code would come out as 6.
        if (header["version"] is JsonValue v && v.TryGetValue<double>(out var version))
            header["version"] = JsonNode.Parse(version.ToString("0.0###############", CultureInfo.InvariantCulture));
        var json = Encoding.UTF8.GetBytes(header.ToJsonString());

        var ms = new MemoryStream();
        ms.Write(Magic);
        Span<byte> n = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(n, json.Length);
        ms.Write(n);
        ms.Write(json);
        BinaryPrimitives.WriteInt32LittleEndian(n, raw.Length);
        BinaryPrimitives.WriteInt32LittleEndian(n[4..], 2);
        ms.Write(n);
        ms.Write(packed);
        return ms.ToArray();
    }

    /// <summary>
    /// The CBOR Serum writes: maps keyed by text, numbers as whole numbers or as the shortest of a 32-bit and a
    /// 64-bit float that keeps the value exactly. Whole numbers stay whole and floats stay floats when read back
    /// (a JsonValue made from a long or a double), so writing it again gives the same bytes.
    /// </summary>
    public static class Cbor
    {
        public static JsonNode? Read(byte[] b, ref int p)
        {
            var initial = b[p++];
            int major = initial >> 5, info = initial & 31;
            if (major == 7)
            {
                switch (info)
                {
                    case 20: return JsonValue.Create(false);
                    case 21: return JsonValue.Create(true);
                    case 22: return null;
                    case 25: { var v = (double)BinaryPrimitives.ReadHalfBigEndian(b.AsSpan(p)); p += 2; return JsonValue.Create(v); }
                    case 26: { var v = (double)BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(p)); p += 4; return JsonValue.Create(v); }
                    case 27: { var v = BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(p)); p += 8; return JsonValue.Create(v); }
                    default: throw new InvalidDataException($"CBOR simple value {info} isn't one Serum writes");
                }
            }
            var n = info switch
            {
                < 24 => (ulong)info,
                24 => b[p++],
                25 => Take(b, ref p, 2),
                26 => Take(b, ref p, 4),
                27 => Take(b, ref p, 8),
                _ => throw new InvalidDataException("CBOR of unknown length isn't something Serum writes"),
            };
            switch (major)
            {
                case 0: return JsonValue.Create((long)n);
                case 1: return JsonValue.Create(-1 - (long)n);
                case 3:
                {
                    var s = Encoding.UTF8.GetString(b, p, (int)n);
                    p += (int)n;
                    return JsonValue.Create(s);
                }
                case 4:
                {
                    var a = new JsonArray();
                    for (ulong i = 0; i < n; i++) a.Add(Read(b, ref p));
                    return a;
                }
                case 5:
                {
                    var o = new JsonObject();
                    for (ulong i = 0; i < n; i++)
                    {
                        if (Read(b, ref p) is not JsonValue key || !key.TryGetValue<string>(out var name))
                            throw new InvalidDataException("A CBOR map key that isn't text");
                        o[name] = Read(b, ref p);
                    }
                    return o;
                }
                default: throw new InvalidDataException($"CBOR type {major} isn't one Serum writes");
            }
        }

        static ulong Take(byte[] b, ref int p, int size)
        {
            ulong v = 0;
            for (var i = 0; i < size; i++) v = v << 8 | b[p++];
            return v;
        }

        public static byte[] Write(JsonNode? node)
        {
            var ms = new MemoryStream();
            Write(ms, node);
            return ms.ToArray();
        }

        static void Head(Stream s, int major, ulong n)
        {
            var m = (byte)(major << 5);
            Span<byte> b = stackalloc byte[8];
            if (n < 24) s.WriteByte((byte)(m | (byte)n));
            else if (n <= byte.MaxValue) { s.WriteByte((byte)(m | 24)); s.WriteByte((byte)n); }
            else if (n <= ushort.MaxValue) { s.WriteByte((byte)(m | 25)); BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)n); s.Write(b[..2]); }
            else if (n <= uint.MaxValue) { s.WriteByte((byte)(m | 26)); BinaryPrimitives.WriteUInt32BigEndian(b, (uint)n); s.Write(b[..4]); }
            else { s.WriteByte((byte)(m | 27)); BinaryPrimitives.WriteUInt64BigEndian(b, n); s.Write(b); }
        }

        static void Write(Stream s, JsonNode? node)
        {
            switch (node)
            {
                case null: s.WriteByte(0xf6); return;
                case JsonObject o:
                    Head(s, 5, (ulong)o.Count);
                    foreach (var (k, v) in o) { Write(s, JsonValue.Create(k)); Write(s, v); }
                    return;
                case JsonArray a:
                    Head(s, 4, (ulong)a.Count);
                    foreach (var v in a) Write(s, v);
                    return;
                case JsonValue v:
                    if (v.TryGetValue<bool>(out var flag)) { s.WriteByte(flag ? (byte)0xf5 : (byte)0xf4); return; }
                    if (v.TryGetValue<string>(out var text))
                    {
                        var bytes = Encoding.UTF8.GetBytes(text);
                        Head(s, 3, (ulong)bytes.Length);
                        s.Write(bytes);
                        return;
                    }
                    if (v.TryGetValue<long>(out var l) || v.TryGetValue<int>(out var i) && (l = i) == i)
                    {
                        if (l >= 0) Head(s, 0, (ulong)l); else Head(s, 1, (ulong)(-1 - l));
                        return;
                    }
                    var d = v.TryGetValue<double>(out var dd) ? dd : v.TryGetValue<float>(out var f) ? f : v.GetValue<double>();
                    Span<byte> b = stackalloc byte[8];
                    if ((double)(float)d == d || double.IsNaN(d))
                    {
                        s.WriteByte(0xfa);
                        BinaryPrimitives.WriteSingleBigEndian(b, (float)d);
                        s.Write(b[..4]);
                    }
                    else
                    {
                        s.WriteByte(0xfb);
                        BinaryPrimitives.WriteDoubleBigEndian(b, d);
                        s.Write(b);
                    }
                    return;
            }
        }
    }
}
