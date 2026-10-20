using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace MusicOrganiser.Core.Parsers;

/// <summary>
/// Decoder for Bitwig's "BtWg" binary format (.bwproject; .bwpreset and .bwclip share it).
///
/// Worked out from real projects saved by Bitwig 5.1, 5.2 and 6.1; a decode is only
/// trusted when it ends exactly where the header says the embedded zip starts.
///
/// Layout:
///   "BtWg" + 38 ASCII hex chars: 4 + 4 + 4 (format), 8 (body offset), 16 (zip offset), "00".
///   Metadata: u32 4, string "meta", then entries of (u32 1, string key, typed value) until u32 0.
///   Body at the body offset: one object tree.
///   Zip at the zip offset: plugin-states/&lt;id&gt;.vstpreset|.fxb|.fxp and other attachments.
///
/// Everything is big-endian. An object is a u32 class id followed by fields of
/// (u32 field id, u8 type, value) and ends with field id 0. Where an object is expected,
/// class id 0 means null and 1 means "reference to object N" (u32 N follows).
/// Strings are a u32 length then UTF-8; when the top bit of the length is set, the
/// remaining bits count UTF-16BE characters.
/// </summary>
public static partial class BitwigFormat
{
    public sealed class BwObject
    {
        /// <summary>The object's kind. Settable for the few kinds laid out the same, such as a VST2 and a VST3 plugin device.</summary>
        public required int Class { get; set; }
        public List<(int Id, object? Value)> Fields { get; init; } = new();
        /// <summary>Each field's type byte, in step with <see cref="Fields"/>, so the object can be written back.</summary>
        public List<byte> Types { get; init; } = new();
        public BwObject? Parent { get; internal set; }
        public int ParentField { get; internal set; }
        /// <summary>Every object in the document, for resolving references.</summary>
        internal List<BwObject>? Table { get; init; }

        /// <summary>The object a value refers to, following a reference if it is one.</summary>
        public BwObject? Resolve(object? v) => v switch
        {
            BwObject o => o,
            BwLink l => l.Target,
            BwRef r when Table is not null && r.Index >= 1 && r.Index <= Table.Count => Table[r.Index - 1],
            _ => null,
        };

        public object? this[int id]
        {
            get
            {
                foreach (var (fid, v) in Fields)
                    if (fid == id) return v;
                return null;
            }
        }

        public bool Has(int id) => Fields.Any(f => f.Id == id);

        /// <summary>Changes a field's value, keeping its type; a new field is added at the end with the type given or implied.</summary>
        public void Set(int id, object? value, byte? type = null)
        {
            var i = Fields.FindIndex(f => f.Id == id);
            if (i >= 0)
            {
                Fields[i] = (id, value);
                if (type is { } t && i < Types.Count) Types[i] = t;
                return;
            }
            Fields.Add((id, value));
            if (Types.Count == Fields.Count - 1) Types.Add(type ?? Writer.TypeOf(value));
        }
        public string Str(int id) => this[id] as string ?? "";
        public double? Num(int id) => this[id] switch
        {
            double d => d, float f => f, int i => i, byte b => b, long l => l, short s => s, _ => null,
        };
        public BwObject? Obj(int id) => Resolve(this[id]);
        public IEnumerable<BwObject> List(int id) =>
            (this[id] as List<object?>)?.Select(Resolve).OfType<BwObject>() ?? [];

        public BwObject? Ancestor(Func<BwObject, bool> match)
        {
            for (var p = Parent; p is not null; p = p.Parent)
                if (match(p)) return p;
            return null;
        }
    }

    /// <summary>A reference to an earlier object: its 1-based position in <see cref="Document.Objects"/>.</summary>
    public readonly record struct BwRef(int Index);

    /// <summary>An opaque byte payload (type 0x0d): offset and length into the file.</summary>
    public readonly record struct BwBlob(int Offset, int Length);

    public sealed class Document
    {
        public required byte[] Raw { get; init; }
        /// <summary>How much of <see cref="Raw"/> is the file: less than its length when read into a borrowed buffer.</summary>
        public int Length { get => _length < 0 ? Raw.Length : _length; init => _length = value; }
        readonly int _length = -1;
        public Dictionary<string, object?> Meta { get; } = new(StringComparer.Ordinal);
        /// <summary>Each metadata value's type byte, in the order the keys were read.</summary>
        public List<(string Key, byte Type)> MetaTypes { get; } = new();
        /// <summary>Where the metadata ends: the body follows, sometimes after padding.</summary>
        public int MetaEnd { get; set; }
        public BwObject? Root { get; set; }
        public List<BwObject> Objects { get; } = new();
        public int BodyOffset { get; init; }
        public int ZipOffset { get; init; }
        /// <summary>Why the body couldn't be decoded, or null when it decoded cleanly.</summary>
        public string? BodyError { get; set; }

        public string MetaString(string key) => Meta.GetValueOrDefault(key) as string ?? "";
        public IReadOnlyList<string> MetaList(string key) => Meta.GetValueOrDefault(key) as List<string> ?? [];

        /// <summary>The embedded zip (plugin states), or null if there isn't one.</summary>
        public ZipArchive? OpenZip()
        {
            if (ZipOffset <= 0 || ZipOffset + 4 > Length || Raw[ZipOffset] != 'P' || Raw[ZipOffset + 1] != 'K') return null;
            try { return new ZipArchive(new MemoryStream(Raw, ZipOffset, Length - ZipOffset, false), ZipArchiveMode.Read); }
            catch (InvalidDataException) { return null; }
        }
    }

    public static bool IsBitwig(ReadOnlySpan<byte> raw) => raw.Length >= 42 && raw[..4].SequenceEqual("BtWg"u8);

    public static Document Decode(byte[] raw) => Decode(raw, raw.Length);

    /// <summary>The file in the first <paramref name="length"/> bytes of <paramref name="raw"/> (a borrowed buffer can be longer).</summary>
    public static Document Decode(byte[] raw, int length)
    {
        if (!IsBitwig(raw.AsSpan(0, length))) throw new InvalidDataException("Not a Bitwig file (missing BtWg header)");
        var hex = Encoding.ASCII.GetString(raw, 4, 38);
        int body = 0, zip = 0;
        try
        {
            body = Convert.ToInt32(hex[12..20], 16);
            zip = (int)Convert.ToInt64(hex[20..36], 16);
        }
        catch (FormatException) { }
        if (zip <= 0 || zip > length) zip = length;

        var doc = new Document { Raw = raw, Length = length, BodyOffset = body, ZipOffset = zip };
        var r = new Reader(raw, doc);
        try { r.ReadMeta(42); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidDataException or DecoderFallbackException or OverflowException)
        {
            doc.BodyError = $"metadata: {e.Message}";
            return doc;
        }

        if (body <= 42 || body >= zip)
        {
            doc.BodyError = "header has no body offset";
            return doc;
        }
        try
        {
            var pos = body;
            doc.Root = r.ReadObject(ref pos);
            if (pos != zip) doc.BodyError = $"body ended at 0x{pos:x}, expected 0x{zip:x}";
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidDataException or DecoderFallbackException or OverflowException)
        {
            doc.BodyError = e.Message;
        }
        return doc;
    }

    /// <summary>
    /// A big project holds millions of small values; most are the same few (a byte flag, 0, 1, a name used thousands of
    /// times), so those are shared rather than made again each time. Solomon's 53 MB "TEKA TEK dubstep new drums 21" made
    /// 391 MB of memory to clean up, which on his PC paused the app and made reading his songs' MIDI crawl.
    /// </summary>
    static readonly object[] BoxedBytes = Enumerable.Range(0, 256).Select(b => (object)(byte)b).ToArray();
    static readonly object Zero = 0.0, One = 1.0, MinusOne = -1.0, Half = 0.5, Quarter = 0.25;

    static object Boxed(double v) => BitConverter.DoubleToInt64Bits(v) switch
    {
        0 => Zero,   // +0 only: -0 keeps its own box, so it is written back as it was
        0x3FF0_0000_0000_0000 => One,
        unchecked((long)0xBFF0_0000_0000_0000) => MinusOne,
        0x3FE0_0000_0000_0000 => Half,
        0x3FD0_0000_0000_0000 => Quarter,
        _ => v,
    };

    sealed class Reader(byte[] d, Document doc)
    {
        // Only the file's own bytes are read: a borrowed buffer can be longer, holding the song read before it, so a
        // file that's cut short (still syncing, or saved while being read) fails cleanly instead of reading on into that.
        readonly int _end = doc.Length;

        ReadOnlySpan<byte> At(int p, int n) =>
            p >= 0 && p <= _end && (uint)n <= (uint)(_end - p) ? d.AsSpan(p, n) : throw new InvalidDataException("runs past end of file");

        ReadOnlySpan<byte> From(int p) => p >= 0 && p <= _end ? d.AsSpan(p, _end - p) : throw new InvalidDataException("runs past end of file");

        byte Byte(int p) => (uint)p < (uint)_end ? d[p] : throw new InvalidDataException("runs past end of file");

        const int MaxDepth = 512;
        const int ShortString = 64;
        int _depth;
        // Fields gather here while an object is read, then go into lists of just the right size.
        readonly List<(int Id, object? Value)> _fields = new(256);
        readonly List<byte> _types = new(256);
        readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);

        int U32(int p) => (int)BinaryPrimitives.ReadUInt32BigEndian(At(p, 4));

        string Str(ref int p)
        {
            var n = (uint)U32(p);
            p += 4;
            string s;
            if ((n & 0x8000_0000) != 0)
            {
                var chars = (int)(n & 0x7FFF_FFFF);
                s = Encoding.BigEndianUnicode.GetString(At(p, checked(chars * 2)));
                p += chars * 2;
            }
            else if (n == 0) s = "";
            else if (n <= ShortString)
            {
                // A short name: the same one is used again and again, so it's looked up before a new one is made.
                Span<char> chars = stackalloc char[ShortString];
                var count = Encoding.UTF8.GetChars(At(p, (int)n), chars);
                var lookup = _strings.GetAlternateLookup<ReadOnlySpan<char>>();
                if (!lookup.TryGetValue(chars[..count], out s!))
                {
                    s = new string(chars[..count]);
                    _strings[s] = s;
                }
                p += (int)n;
            }
            else
            {
                s = Encoding.UTF8.GetString(At(p, checked((int)n)));
                p += (int)n;
            }
            return s;
        }

        public void ReadMeta(int p)
        {
            doc.MetaEnd = p;
            if (U32(p) != 4) return;
            p += 4;
            if (Str(ref p) != "meta") return;
            while (U32(p) == 1)
            {
                p += 4;
                var key = Str(ref p);
                var type = Byte(p++);
                doc.Meta[key] = Value(ref p, type);
                doc.MetaTypes.Add((key, type));
            }
            doc.MetaEnd = p;
        }

        public BwObject? ReadObject(ref int p)
        {
            var cls = U32(p);
            p += 4;
            if (cls == 0) return null;
            if (cls == 1) throw new InvalidDataException("unexpected reference");
            if (++_depth > MaxDepth) throw new InvalidDataException("objects nested too deeply");
            var start = _fields.Count;
            var fields = new List<(int Id, object? Value)>(0);
            var types = new List<byte>(0);
            var o = new BwObject { Class = cls, Table = doc.Objects, Fields = fields, Types = types };
            doc.Objects.Add(o);
            while (true)
            {
                var fid = U32(p);
                p += 4;
                if (fid == 0) break;
                var type = Byte(p++);
                // An object inside this one gathers its own fields above these and takes them away again when it ends.
                var v = Value(ref p, type);
                _fields.Add((fid, v));
                _types.Add(type);
                Adopt(o, fid, v);
            }
            var count = _fields.Count - start;
            fields.Capacity = count;
            fields.AddRange(CollectionsMarshal.AsSpan(_fields).Slice(start, count));
            types.Capacity = count;
            types.AddRange(CollectionsMarshal.AsSpan(_types).Slice(start, count));
            _fields.RemoveRange(start, count);
            _types.RemoveRange(start, count);
            _depth--;
            return o;
        }

        /// <summary>An object slot: null, a reference, or an inline object.</summary>
        object? ObjectOrRef(ref int p)
        {
            if (U32(p) == 1)
            {
                var r = new BwRef(U32(p + 4));
                p += 8;
                return r;
            }
            return ReadObject(ref p);
        }

        static void Adopt(BwObject parent, int fid, object? v)
        {
            switch (v)
            {
                case BwObject child: child.Parent = parent; child.ParentField = fid; break;
                case List<object?> list:
                    foreach (var c in list.OfType<BwObject>()) { c.Parent = parent; c.ParentField = fid; }
                    break;
                case Dictionary<string, object?> map:
                    foreach (var c in map.Values.OfType<BwObject>()) { c.Parent = parent; c.ParentField = fid; }
                    break;
                case KeyValuePair<object?, string> named when named.Key is BwObject c:
                    c.Parent = parent; c.ParentField = fid;
                    break;
            }
        }

        object? Value(ref int p, byte type)
        {
            switch (type)
            {
                case 0x01: case 0x05: return BoxedBytes[Byte(p++)];
                case 0x02: { var v = BinaryPrimitives.ReadInt16BigEndian(From(p)); p += 2; return v; }
                case 0x03: { var v = BinaryPrimitives.ReadInt32BigEndian(From(p)); p += 4; return v; }
                case 0x04: { var v = BinaryPrimitives.ReadInt64BigEndian(From(p)); p += 8; return v; }
                case 0x06: { var v = BinaryPrimitives.ReadSingleBigEndian(From(p)); p += 4; return v; }
                case 0x07: { var v = BinaryPrimitives.ReadDoubleBigEndian(From(p)); p += 8; return Boxed(v); }
                case 0x08: return Str(ref p);
                case 0x09: return ObjectOrRef(ref p);
                case 0x0a: return null;
                case 0x0b: { var v = new BwRef(U32(p)); p += 4; return v; }
                case 0x0d:
                {
                    var n = U32(p);
                    if (n < 0 || p + 4 + n > _end) throw new InvalidDataException("blob runs past end of file");
                    var v = new BwBlob(p + 4, n);
                    p += 4 + n;
                    return v;
                }
                case 0x0f:
                {
                    var n = U32(p);
                    p += 4;
                    if (n < 0 || p + 4L * n > _end) throw new InvalidDataException("int array runs past end of file");
                    var a = new int[n];
                    for (var i = 0; i < n; i++, p += 4) a[i] = BinaryPrimitives.ReadInt32BigEndian(From(p));
                    return a;
                }
                case 0x12:
                {
                    // A list of objects ended by the marker 3.
                    var list = new List<object?>();
                    while (U32(p) != 3) list.Add(ObjectOrRef(ref p));
                    p += 4;
                    return list;
                }
                case 0x14:
                {
                    // A string-keyed map: (u8 1, string key, object) per entry, then u8 0.
                    var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                    while (Byte(p) == 1)
                    {
                        p++;
                        var key = Str(ref p);
                        map[key] = ObjectOrRef(ref p);
                    }
                    if (Byte(p) != 0) throw new InvalidDataException($"bad map end at 0x{p:x}");
                    p++;
                    return map;
                }
                case 0x15: { var v = new Guid(At(p, 16), bigEndian: true); p += 16; return v; }
                case 0x16:
                {
                    var c = new float[4];
                    for (var i = 0; i < 4; i++, p += 4) c[i] = BinaryPrimitives.ReadSingleBigEndian(From(p));
                    return c;
                }
                case 0x17:
                {
                    // A list of floats: u32 count, then f32s (FM-4's MATRIX).
                    var n = U32(p);
                    p += 4;
                    if (n < 0 || p + 4L * n > _end) throw new InvalidDataException("float list too long");
                    var f = new float[n];
                    for (var i = 0; i < n; i++, p += 4) f[i] = BinaryPrimitives.ReadSingleBigEndian(From(p));
                    return f;
                }
                case 0x18:
                {
                    // A list of doubles: u32 count, then f64s (Bitwig 6.1 keeps a Sampler sample's analysis, such as where its hits are, this way).
                    var n = U32(p);
                    p += 4;
                    if (n < 0 || p + 8L * n > _end) throw new InvalidDataException("double list too long");
                    var f = new double[n];
                    for (var i = 0; i < n; i++, p += 8) f[i] = BinaryPrimitives.ReadDoubleBigEndian(From(p));
                    return f;
                }
                case 0x19:
                {
                    var n = U32(p);
                    p += 4;
                    if (n < 0 || n > _end) throw new InvalidDataException("string list too long");
                    var list = new List<string>(n);
                    for (var i = 0; i < n; i++) list.Add(Str(ref p));
                    return list;
                }
                case 0x1a:
                {
                    // An object (or reference) followed by a string.
                    var o = ObjectOrRef(ref p);
                    return new KeyValuePair<object?, string>(o, Str(ref p));
                }
                default:
                    throw new InvalidDataException($"unknown value type 0x{type:x2} at 0x{p - 1:x}");
            }
        }
    }
}
