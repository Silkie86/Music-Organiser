using System.IO.Compression;
using MusicOrganiser.Core.Parsers;
using static MusicOrganiser.Core.Parsers.BitwigFormat;

namespace MusicOrganiser.Core.Formats;

/// <summary>
/// Lays out a Bitwig file for Format Lab: its metadata (the summary in "structure" decoded too), its object
/// tree, and the files in its zip. An object is shown where Bitwig first wrote it; a reference elsewhere
/// shows where that is.
/// </summary>
static class BitwigLab
{
    const int FId = 0xf;

    static FormatRegistry Registry => FormatRegistry.Bitwig;

    public static LabFile Read(byte[] raw, string name)
    {
        var doc = Decode(raw);
        var items = new List<LabItem>();
        new Walker(doc, items).All("", "", 0);
        return new LabFile
        {
            Name = name, Format = LabFormat.Bitwig, Version = doc.MetaString("application_version_name"),
            Items = items, Problem = doc.BodyError,
        };
    }

    /// <summary>A field's label: its id, and its name when the registry has one.</summary>
    static string FieldLabel(int id) => Registry.Field(id) is { } f ? $"0x{id:x} {f.Name}" : $"0x{id:x}";
    static string FieldName(int id) => Registry.Field(id)?.Name ?? $"0x{id:x}";
    static string ClassLabel(int id) => Registry.Class(id) is { } c ? $"0x{id:x} {c.Name}" : $"0x{id:x}";

    sealed class Walker(Document doc, List<LabItem> items)
    {
        readonly Dictionary<BwObject, string> _paths = new(ReferenceEqualityComparer.Instance);

        public void All(string keyPrefix, string pathPrefix, int depth)
        {
            foreach (var (key, _) in doc.MetaTypes) Meta(key, keyPrefix, pathPrefix, depth);
            if (doc.Root is { } root)
            {
                items.Add(new LabItem(keyPrefix + "body", Join(pathPrefix, "body"), depth, "body", ClassLabel(root.Class), null, true));
                Fields(root, keyPrefix + "body", pathPrefix, depth + 1);
            }
            if (keyPrefix.Length == 0) Zip();
        }

        void Meta(string key, string keyPrefix, string pathPrefix, int depth)
        {
            var entry = Registry.MetaKey(key);
            var k = $"{keyPrefix}meta/{key}";
            var label = entry is null ? $"meta {key}" : $"meta {key} ({entry.Name})";
            var path = Join(pathPrefix, entry is null ? $"meta {key}" : entry.Name);
            var v = doc.Meta.GetValueOrDefault(key);
            if (v is BwBlob blob && blob.Offset + blob.Length <= doc.Raw.Length)
            {
                var bytes = doc.Raw.AsSpan(blob.Offset, blob.Length).ToArray();
                if (IsBitwig(bytes))
                {
                    // A Bitwig document of its own, like the summary of the project's tracks.
                    items.Add(new LabItem(k, path, depth, label, $"<{bytes.Length:N0} bytes, a Bitwig document>", null, true));
                    var inner = Decode(bytes);
                    new Walker(inner, items).All(k + "/", path, depth + 1);
                    return;
                }
                items.Add(new LabItem(k, path, depth, label, LabText.Bytes(bytes), bytes));
                return;
            }
            Add(k, path, depth, label, v);
        }

        void Fields(BwObject o, string key, string path, int depth)
        {
            var seen = new Dictionary<int, int>();
            for (var i = 0; i < o.Fields.Count; i++)
            {
                var (id, v) = o.Fields[i];
                var n = seen.GetValueOrDefault(id);
                seen[id] = n + 1;
                var again = n > 0 ? $" #{n + 1}" : "";
                Value(o, v, $"{key}/0x{id:x}{(n > 0 ? $"#{n + 1}" : "")}", Join(path, FieldName(id) + again),
                      depth, FieldLabel(id) + again);
            }
        }

        void Value(BwObject owner, object? v, string key, string path, int depth, string label)
        {
            switch (v)
            {
                case BwObject child:
                    _paths[child] = path;
                    items.Add(new LabItem(key, path, depth, label, ClassLabel(child.Class), null, true));
                    Fields(child, key, path, depth + 1);
                    break;
                case BwRef r:
                    var target = owner.Resolve(r);
                    var where = target is not null && _paths.TryGetValue(target, out var p) ? p : $"object #{r.Index}";
                    items.Add(new LabItem(key, path, depth, label, $"→ {where}" + (target is null ? "" : $" ({ClassLabel(target.Class)})"), null));
                    break;
                case List<object?> list:
                    items.Add(new LabItem(key, path, depth, label, $"list of {list.Count}", null, true));
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    for (var i = 0; i < list.Count; i++)
                    {
                        // An object with its own ID keeps its key when others are added or removed before it.
                        var element = list[i] is BwObject { } eo && eo[FId] is Guid g && keys.Add($"[id {g.ToString("N")[..8]}]")
                            ? $"[id {g.ToString("N")[..8]}]" : $"[{i}]";
                        Value(owner, list[i], $"{key}/{element}", $"{path}[{i}]", depth + 1, $"[{i}]");
                    }
                    break;
                case Dictionary<string, object?> map:
                    items.Add(new LabItem(key, path, depth, label, $"map of {map.Count}", null, true));
                    foreach (var (k, x) in map) Value(owner, x, $"{key}/{{{k}}}", $"{path}{{{k}}}", depth + 1, $"{{{k}}}");
                    break;
                case KeyValuePair<object?, string> named:
                    Value(owner, named.Key, key, path, depth, label);
                    Add($"{key}/text", $"{path} text", depth + 1, "text", named.Value);
                    break;
                case BwBlob blob when blob.Offset >= 0 && blob.Offset + blob.Length <= doc.Raw.Length:
                    var bytes = doc.Raw.AsSpan(blob.Offset, blob.Length).ToArray();
                    items.Add(new LabItem(key, path, depth, label, LabText.Bytes(bytes), bytes));
                    break;
                default:
                    Add(key, path, depth, label, v);
                    break;
            }
        }

        void Add(string key, string path, int depth, string label, object? v)
        {
            var (text, data) = v switch
            {
                null => ("null", (object?)null),
                string s => (LabText.Quote(s), s),
                byte b => (LabText.Number(b), (long)b),
                short s => (LabText.Number(s), (long)s),
                int i => (LabText.Number(i), (long)i),
                long l => (LabText.Number(l), l),
                float f => (LabText.Number(f), (double)f),
                double d => (LabText.Number(d), d),
                Guid g => (g.ToString(), g.ToString()),
                int[] a => ($"[{string.Join(", ", a.Take(32))}{(a.Length > 32 ? ", …" : "")}]", a.Select(x => (long)x).ToArray()),
                float[] a => ($"[{string.Join(", ", a.Take(32).Select(x => LabText.Number(x)))}{(a.Length > 32 ? ", …" : "")}]",
                              a.Select(x => (double)x).ToArray()),
                List<string> l => ($"[{string.Join(", ", l.Take(32).Select(LabText.Quote))}{(l.Count > 32 ? ", …" : "")}]", l.ToArray()),
                _ => (v.ToString() ?? "", v.ToString()),
            };
            items.Add(new LabItem(key, path, depth, label, text, data));
        }

        void Zip()
        {
            using var zip = doc.OpenZip();
            if (zip is null) return;
            items.Add(new LabItem("zip", "zip", 0, "zip", $"{zip.Entries.Count} files", null, true));
            foreach (var e in zip.Entries)
            {
                byte[] bytes;
                try
                {
                    using var s = e.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    bytes = ms.ToArray();
                }
                catch (InvalidDataException) { bytes = []; }
                items.Add(new LabItem($"zip/{e.FullName}", $"zip › {e.FullName}", 1, e.FullName, LabText.Bytes(bytes), bytes));
            }
        }

        static string Join(string path, string part) => path.Length == 0 ? part : $"{path} › {part}";
    }
}
