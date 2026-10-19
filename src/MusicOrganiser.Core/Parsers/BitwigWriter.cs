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
    /// Like Bitwig, an object is written in full where it's first reached and referenced after
    /// that, wherever the decode found it; references are numbered in that order. So objects can
    /// be added, removed or copied, and an untouched document comes back byte for byte.
    /// </summary>
    public static byte[] Encode(Document doc, byte[]? zip = null)
    {
        if (doc.Root is null || doc.BodyError is not null)
            throw new InvalidOperationException("Only a cleanly decoded document can be written back");
        var w = new Writer(doc);
        var header = Encoding.ASCII.GetString(doc.Raw, 4, 38);

        var meta = new MemoryStream();
        w.Out = meta;
        if (doc.MetaEnd > 42)   // documents nested in metadata have none
        {
            w.U32(4);
            w.Str("meta");
            foreach (var (key, type) in doc.MetaTypes)
            {
                w.U32(1);
                w.Str(key);
                meta.WriteByte(type);
                w.Value(type, doc.Meta.GetValueOrDefault(key));
            }
        }
        meta.Write(doc.Raw, doc.MetaEnd, doc.BodyOffset - doc.MetaEnd);   // end marker and padding, as they were

        var body = new MemoryStream();
        w.Out = body;
        w.Number(doc.Root);
        w.Inline(doc.Root);

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

    /// <summary>
    /// A copy of <paramref name="original"/> and everything inside it, for adding another track
    /// or device like it. What counts as inside is what can only be reached through the original:
    /// objects the rest of the document also reaches (the master track a track routes to, which
    /// Bitwig defines inside the first track) aren't copied, the copy links to them. References
    /// inside the copy point at the copied objects. Ids (instances, tracks, chains; not device
    /// types) get fresh values, and other fields holding the same ids follow them.
    /// </summary>
    public static BwObject Clone(Document doc, BwObject original) => new Copier(doc, original).Copy(original);

    /// <summary>
    /// Copies objects into a document, for building a project out of a template's tracks and devices.
    /// Everything the document's root reaches when it's made is shared: copies link to it rather than
    /// copying it. So take the objects to copy out of the tree first (detach them), then copy them as
    /// often as needed.
    /// </summary>
    public sealed class Copier
    {
        readonly HashSet<BwObject> _shared;

        /// <param name="besides">An object still in the tree that will be copied: what's only reachable through it isn't shared.</param>
        public Copier(Document doc, BwObject? besides = null)
        {
            _shared = new HashSet<BwObject>(ReferenceEqualityComparer.Instance);
            if (doc.Root is { } root) Reach(root, _shared, o => o == besides);
        }

        static void Reach(BwObject start, HashSet<BwObject> seen, Func<BwObject, bool> stop)
        {
            var todo = new Stack<BwObject>();
            todo.Push(start);
            while (todo.Count > 0)
            {
                var o = todo.Pop();
                if (stop(o) || !seen.Add(o)) continue;
                foreach (var (_, v) in o.Fields)
                    foreach (var x in Values(v))
                        if (o.Resolve(x) is { } next) todo.Push(next);
            }
        }

        /// <summary>Whether copies link to this object rather than copying it.</summary>
        public bool Shares(BwObject o) => _shared.Contains(o);

        public BwObject Copy(BwObject original)
        {
            var owned = new HashSet<BwObject>(ReferenceEqualityComparer.Instance);
            Reach(original, owned, _shared.Contains);

            // Every id the copied objects carry is theirs (instances, tracks, chains), except a device's type (0x99).
            var ids = new Dictionary<Guid, Guid>();
            foreach (var o in owned)
                foreach (var (id, v) in o.Fields)
                    if (v is Guid g && id != 0x99) ids.TryAdd(g, Guid.NewGuid());

            var copies = new Dictionary<BwObject, BwObject>(ReferenceEqualityComparer.Instance);
            BwObject Copy(BwObject o)
            {
                if (copies.TryGetValue(o, out var done)) return done;
                var c = new BwObject { Class = o.Class, Table = o.Table };
                copies[o] = c;
                for (var i = 0; i < o.Fields.Count; i++)
                {
                    var (id, v) = o.Fields[i];
                    c.Fields.Add((id, Map(v)));
                    if (i < o.Types.Count) c.Types.Add(o.Types[i]);
                }
                return c;
            }
            object? Item(object? x)
            {
                if (x is null) return null;
                if (x is BwObject child) return owned.Contains(child) ? Copy(child) : new BwLink(child);
                if (x is BwRef or BwLink && original.Resolve(x) is { } t)
                    return new BwLink(owned.Contains(t) ? Copy(t) : t);
                return x;
            }
            object? Map(object? v) => v switch
            {
                BwObject or BwRef or BwLink => Item(v),
                List<object?> list => list.Select(Item).ToList(),
                Dictionary<string, object?> map => map.ToDictionary(kv => kv.Key, kv => Item(kv.Value), StringComparer.Ordinal),
                KeyValuePair<object?, string> named => new KeyValuePair<object?, string>(Item(named.Key), named.Value),
                Guid g => ids.GetValueOrDefault(g, g),
                int[] a => a.ToArray(),
                float[] a => a.ToArray(),
                double[] a => a.ToArray(),
                List<string> l => l.ToList(),
                _ => v,
            };
            return Copy(original);
        }
    }

    /// <summary>The values a field holds: a list's or map's items, a named reference's object, or the value itself.</summary>
    internal static IEnumerable<object?> Values(object? v) => v switch
    {
        List<object?> list => list,
        Dictionary<string, object?> map => map.Values,
        KeyValuePair<object?, string> named => [named.Key],
        _ => [v],
    };

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
            BwObject o => o,
            BwLink l => l.Target,
            BwRef x when x.Index >= 1 && x.Index <= doc.Objects.Count => doc.Objects[x.Index - 1],
            _ => throw new InvalidDataException($"reference to an object that isn't there: {r}"),
        };

        int _written;

        bool Written(object r) => _numbers.TryGetValue(Target(r), out var n) && n <= _written;

        /// <summary>A reference's number; the object must already have been written.</summary>
        int WrittenNumberOf(object r) => Written(r)
            ? _numbers[Target(r)] : throw new InvalidDataException("reference to an object that isn't written before it");

        /// <summary>
        /// Numbers every object in the order it will be written: in full where it's first reached
        /// through an object slot, by reference after that.
        /// </summary>
        public void Number(BwObject o)
        {
            _numbers[o] = _numbers.Count + 1;
            for (var i = 0; i < o.Fields.Count; i++)
            {
                var (type, v) = (TypeAt(o, i), o.Fields[i].Value);
                // A bare reference to an object not reached yet (its first home was edited away) is where it's written now.
                foreach (var slot in type == 0x0b ? new[] { v } : Slots(type, v))
                    if (slot is not null && !_numbers.ContainsKey(Target(slot))) Number(Target(slot));
            }
        }

        static byte TypeAt(BwObject o, int i) => i < o.Types.Count ? o.Types[i] : TypeOf(o.Fields[i].Value);

        /// <summary>The object slots in a value, in the order they're written. A bare reference (0x0b) isn't a slot.</summary>
        static IEnumerable<object?> Slots(byte type, object? v) => type switch
        {
            0x09 => [v],
            0x12 => (List<object?>)v!,
            0x14 => ((Dictionary<string, object?>)v!).Values,
            0x1a => [((KeyValuePair<object?, string>)v!).Key],
            _ => [],
        };

        public void Inline(BwObject o)
        {
            _written++;
            if (_numbers.GetValueOrDefault(o) != _written) throw new InvalidDataException("objects written out of order");
            U32(o.Class);
            for (var i = 0; i < o.Fields.Count; i++)
            {
                var (id, v) = o.Fields[i];
                U32(id);
                var type = TypeAt(o, i);
                if (type == 0x0b && v is not null && !Written(v)) type = 0x09;   // first reached here, so written in full, as Bitwig does
                // and one already written is a bare reference: Bitwig can't read a reference in a field that holds one
                // object ("Unknown class ID 1"), only in lists and maps. Solomon's requiem_80 had two automation lanes on
                // one Reaktor, and the second lane's reference to it stopped the whole project opening. (A reference read
                // from the file stays as it was, so an untouched document still comes back byte for byte.)
                else if (type == 0x09 && v is not (null or BwRef) && Written(v)) type = 0x0b;
                Out.WriteByte(type);
                Value(type, v);
            }
            U32(0);
        }

        void Slot(object? v)
        {
            if (v is null) { U32(0); return; }
            var target = Target(v);
            if (_numbers.TryGetValue(target, out var n) && n <= _written)
            {
                U32(1);
                U32(n);
            }
            else Inline(target);
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
            double[] => 0x18,
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
                case 0x0b: U32(WrittenNumberOf(v!)); break;
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
                case 0x17:
                    U32(((float[])v!).Length);
                    foreach (var c in (float[])v!) { BinaryPrimitives.WriteSingleBigEndian(_b, c); Out.Write(_b, 0, 4); }
                    break;
                case 0x18:
                    U32(((double[])v!).Length);
                    foreach (var c in (double[])v!) { BinaryPrimitives.WriteDoubleBigEndian(_b, c); Out.Write(_b, 0, 8); }
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
