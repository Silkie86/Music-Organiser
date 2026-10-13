using System.Buffers.Binary;
using System.Text;

namespace MusicOrganiser.Core.Parsers;

/// <summary>Writing Bitwig's BtWg format back out, for building projects from a template Bitwig saved.</summary>
public static partial class BitwigFormat
{
    /// <summary>A reference to an object by identity, for trees edited in code. It's written as a reference.</summary>
    public sealed record BwLink(BwObject Target);

    /// <summary>
    /// Writes a decoded document back out: header, metadata, the object tree from
    /// <see cref="Document.Root"/>, then the zip (the original one unless <paramref name="zip"/> is given).
    /// References are renumbered in the order objects are written, so objects can be added,
    /// removed or copied; an untouched document comes back byte for byte.
    /// </summary>
    public static byte[] Encode(Document doc, byte[]? zip = null)
    {
        if (doc.Root is null || doc.BodyError is not null)
            throw new InvalidOperationException("Only a cleanly decoded document can be written back");
        var w = new Writer(doc);
        var header = Encoding.ASCII.GetString(doc.Raw, 4, 38);

        var meta = new MemoryStream();
        w.Out = meta;
        w.U32(4);
        w.Str("meta");
        foreach (var (key, type) in doc.MetaTypes)
        {
            w.U32(1);
            w.Str(key);
            meta.WriteByte(type);
            w.Value(type, doc.Meta.GetValueOrDefault(key));
        }
        meta.Write(doc.Raw, doc.MetaEnd, doc.BodyOffset - doc.MetaEnd);   // end marker and padding, as they were

        var body = new MemoryStream();
        w.Out = body;
        w.Number(doc.Root);
        w.Object(doc.Root);

        var originalZip = Convert.ToInt64(header[20..36], 16);
        zip ??= originalZip > 0 ? doc.Raw[doc.ZipOffset..] : [];
        var bodyOffset = 42 + (int)meta.Length;
        var zipOffset = zip.Length > 0 ? bodyOffset + body.Length : 0;

        var file = new MemoryStream();
        file.Write("BtWg"u8);
        file.Write(Encoding.ASCII.GetBytes(header[..12] + bodyOffset.ToString("x8") + zipOffset.ToString("x16") + header[36..]));
        meta.WriteTo(file);
        body.WriteTo(file);
        file.Write(zip);
        return file.ToArray();
    }

    sealed class Writer(Document doc)
    {
        public Stream Out = Stream.Null;
        readonly Dictionary<BwObject, int> _numbers = new(ReferenceEqualityComparer.Instance);
        readonly byte[] _b = new byte[8];

        public void U32(int v) { BinaryPrimitives.WriteInt32BigEndian(_b, v); Out.Write(_b, 0, 4); }

        public void Str(string s)
        {
            // Bitwig writes plain text as UTF-8 and anything else as UTF-16 (length in characters, top bit set).
            if (s.All(c => c < 0x80))
            {
                var bytes = Encoding.UTF8.GetBytes(s);
                U32(bytes.Length);
                Out.Write(bytes);
            }
            else
            {
                U32(unchecked((int)0x8000_0000) | s.Length);
                Out.Write(Encoding.BigEndianUnicode.GetBytes(s));
            }
        }

        BwObject Target(object r) => r switch
        {
            BwLink l => l.Target,
            BwRef x when x.Index >= 1 && x.Index <= doc.Objects.Count => doc.Objects[x.Index - 1],
            _ => throw new InvalidDataException($"reference to an object that isn't there: {r}"),
        };

        int NumberOf(object r) => _numbers.TryGetValue(Target(r), out var n)
            ? n : throw new InvalidDataException("reference to an object that isn't written");

        /// <summary>Numbers every object written inline, in the order they'll be written (objects are numbered as created).</summary>
        public void Number(BwObject o)
        {
            if (!_numbers.TryAdd(o, _numbers.Count + 1)) throw new InvalidDataException("the same object is written twice");
            foreach (var (_, v) in o.Fields)
                foreach (var child in Children(v))
                    Number(child);
        }

        static IEnumerable<BwObject> Children(object? v) => v switch
        {
            BwObject o => [o],
            List<object?> list => list.OfType<BwObject>(),
            Dictionary<string, object?> map => map.Values.OfType<BwObject>(),
            KeyValuePair<object?, string> { Key: BwObject o } => [o],
            _ => [],
        };

        public void Object(BwObject o)
        {
            U32(o.Class);
            for (var i = 0; i < o.Fields.Count; i++)
            {
                var (id, v) = o.Fields[i];
                U32(id);
                var type = i < o.Types.Count ? o.Types[i] : TypeOf(v);
                Out.WriteByte(type);
                Value(type, v);
            }
            U32(0);
        }

        void Slot(object? v)
        {
            switch (v)
            {
                case null: U32(0); break;
                case BwRef or BwLink: U32(1); U32(NumberOf(v)); break;
                case BwObject o: Object(o); break;
                default: throw new InvalidDataException($"not an object: {v.GetType().Name}");
            }
        }

        /// <summary>The type byte for a value added in code without one.</summary>
        public static byte TypeOf(object? v) => v switch
        {
            null => 0x0a,
            byte => 0x01, short => 0x02, int => 0x03, long => 0x04, float => 0x06, double => 0x07, string => 0x08,
            BwObject or BwLink => 0x09,
            BwRef => 0x0b,
            BwBlob or byte[] => 0x0d,
            int[] => 0x0f,
            List<object?> => 0x12,
            Dictionary<string, object?> => 0x14,
            Guid => 0x15,
            float[] => 0x16,
            List<string> => 0x19,
            KeyValuePair<object?, string> => 0x1a,
            _ => throw new InvalidDataException($"can't write a {v.GetType().Name}"),
        };

        public void Value(byte type, object? v)
        {
            switch (type)
            {
                case 0x01: case 0x05: Out.WriteByte(Convert.ToByte(v)); break;
                case 0x02: BinaryPrimitives.WriteInt16BigEndian(_b, Convert.ToInt16(v)); Out.Write(_b, 0, 2); break;
                case 0x03: U32(Convert.ToInt32(v)); break;
                case 0x04: BinaryPrimitives.WriteInt64BigEndian(_b, Convert.ToInt64(v)); Out.Write(_b, 0, 8); break;
                case 0x06: BinaryPrimitives.WriteSingleBigEndian(_b, Convert.ToSingle(v)); Out.Write(_b, 0, 4); break;
                case 0x07: BinaryPrimitives.WriteDoubleBigEndian(_b, Convert.ToDouble(v)); Out.Write(_b, 0, 8); break;
                case 0x08: Str((string)v!); break;
                case 0x09: Slot(v); break;
                case 0x0a: break;
                case 0x0b: U32(NumberOf(v!)); break;
                case 0x0d:
                    var blob = v switch { BwBlob x => doc.Raw.AsSpan(x.Offset, x.Length), byte[] bytes => bytes, _ => [] };
                    U32(blob.Length);
                    Out.Write(blob);
                    break;
                case 0x0f:
                    var ints = (int[])v!;
                    U32(ints.Length);
                    foreach (var i in ints) U32(i);
                    break;
                case 0x12:
                    foreach (var item in (List<object?>)v!) Slot(item);
                    U32(3);
                    break;
                case 0x14:
                    foreach (var (key, item) in (Dictionary<string, object?>)v!)
                    {
                        Out.WriteByte(1);
                        Str(key);
                        Slot(item);
                    }
                    Out.WriteByte(0);
                    break;
                case 0x15: Out.Write(((Guid)v!).ToByteArray(bigEndian: true)); break;
                case 0x16:
                    foreach (var c in (float[])v!) { BinaryPrimitives.WriteSingleBigEndian(_b, c); Out.Write(_b, 0, 4); }
                    break;
                case 0x19:
                    var strings = (List<string>)v!;
                    U32(strings.Count);
                    foreach (var s in strings) Str(s);
                    break;
                case 0x1a:
                    var (o, name) = (KeyValuePair<object?, string>)v!;
                    Slot(o);
                    Str(name);
                    break;
                default: throw new InvalidDataException($"can't write value type 0x{type:x2}");
            }
        }
    }
}
