using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MusicOrganiser.Core.Formats;

/// <summary>One difference between two saves of a project.</summary>
/// <param name="Kind">changed, added or removed.</param>
/// <param name="Inside">For something added or removed: how many things it holds, which aren't listed one by one.</param>
/// <param name="Detail">For bytes: where they differ.</param>
public sealed record LabChange(string Kind, string Key, string Path, string? Before, string? After, int Inside = 0, string? Detail = null);

/// <summary>A place in a file that holds what was looked for.</summary>
/// <param name="How">How it was found: as text, as a number, or as bytes at an offset in an item's data.</param>
public sealed record LabHit(string Path, string Value, string How);

/// <summary>
/// Format Lab: shows what's inside a Bitwig or FL Studio file (Tree), names every value that differs between
/// two saves (Diff), and finds a value anywhere in a file (Find), so a change made in the DAW can be traced
/// to the bytes that hold it.
/// </summary>
public static class FormatLab
{
    // ---- Tree ----

    /// <summary>Everything in the file, one line each, indented by depth.</summary>
    public static string Tree(LabFile f, int maxDepth = int.MaxValue)
    {
        var sb = new StringBuilder();
        Heading(sb, f);
        string? scope = null;
        foreach (var i in f.Items)
        {
            if (f.Format == LabFormat.FlStudio)
            {
                // FL's parts come back now and then (a pattern's events in two places), so name the part each time it changes.
                if (i.Depth == 0) continue;
                var s = i.Key[..i.Key.IndexOf('/')];
                if (s != scope) sb.AppendLine(scope = s);
            }
            if (i.Depth > maxDepth) continue;
            sb.Append(' ', i.Depth * 2).Append(i.Label);
            if (i.Value.Length > 0) sb.Append(i.IsGroup ? ": " : " = ").Append(i.Value);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    static void Heading(StringBuilder sb, LabFile f)
    {
        sb.AppendLine($"{f.Daw} {f.Version} · {f.Name}".Replace("  ", " "));
        if (f.Problem is not null) sb.AppendLine($"Only part of it could be read: {f.Problem}");
    }

    // ---- Diff ----

    /// <summary>
    /// What differs between two saves: values that changed, and things added or removed (each listed once,
    /// with how much it holds). A group (an object, a list, an event made of parts) is only listed as changed
    /// when nothing inside it is.
    /// </summary>
    public static IReadOnlyList<LabChange> Diff(LabFile before, LabFile after)
    {
        var a = Index(before.Items);
        var b = Index(after.Items);
        var changed = new List<(int Order, LabChange Change)>();
        var touched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, (y, order)) in b)
            if (a.TryGetValue(key, out var x) && !Same(x.Item, y))
            {
                changed.Add((order, new LabChange("changed", key, y.Path, x.Item.Value, y.Value, Detail: BytesDetail(x.Item.Data, y.Data))));
                touched.Add(key);
            }
        var added = b.Keys.Where(k => !a.ContainsKey(k)).ToHashSet(StringComparer.Ordinal);
        var removed = a.Keys.Where(k => !b.ContainsKey(k)).ToHashSet(StringComparer.Ordinal);
        touched.UnionWith(added);
        touched.UnionWith(removed);

        foreach (var (key, inside) in Outermost(added))
        {
            var (y, order) = b[key];
            changed.Add((order, new LabChange("added", key, y.Path, null, y.Value, Inside: inside)));
        }
        foreach (var (key, inside) in Outermost(removed))
        {
            var (x, order) = a[key];
            changed.Add((NearestOrder(key, b, order), new LabChange("removed", key, x.Path, x.Value, null, Inside: inside)));
        }
        // A group whose own text changed because something inside it did ("list of 3" → "list of 4") isn't news.
        var holders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in touched)
            for (var i = t.IndexOf('/'); i > 0; i = t.IndexOf('/', i + 1))
                holders.Add(t[..i]);
        changed.RemoveAll(c => c.Change.Kind == "changed" && holders.Contains(c.Change.Key));
        return changed.OrderBy(c => c.Order).Select(c => c.Change).ToList();
    }

    static Dictionary<string, (LabItem Item, int Order)> Index(IReadOnlyList<LabItem> items)
    {
        var d = new Dictionary<string, (LabItem, int)>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++) d.TryAdd(items[i].Key, (items[i], i));
        return d;
    }

    /// <summary>The keys not inside another of them, each with how many of the others are inside it.</summary>
    static Dictionary<string, int> Outermost(HashSet<string> keys)
    {
        var top = new Dictionary<string, int>(StringComparer.Ordinal);
        var inner = new List<string>();
        foreach (var k in keys)
            if (Holder(k, keys) is null) top[k] = 0;
            else inner.Add(k);
        foreach (var k in inner)
            if (Holder(k, keys) is { } h && top.ContainsKey(h)) top[h]++;
        return top;
    }

    /// <summary>The outermost of <paramref name="keys"/> that holds <paramref name="key"/>, or null.</summary>
    static string? Holder(string key, HashSet<string> keys)
    {
        for (var i = key.IndexOf('/'); i > 0; i = key.IndexOf('/', i + 1))
            if (keys.Contains(key[..i])) return key[..i];
        return null;
    }

    /// <summary>Where a removed thing would have been in the newer file: after what came before it.</summary>
    static int NearestOrder(string key, Dictionary<string, (LabItem Item, int Order)> b, int fallback)
    {
        for (var i = key.LastIndexOf('/'); i > 0; i = key.LastIndexOf('/', i - 1))
            if (b.TryGetValue(key[..i], out var parent)) return parent.Order;
        return fallback;
    }

    static bool Same(LabItem x, LabItem y) =>
        x.Value == y.Value && (x.Data is not byte[] p || y.Data is not byte[] q || p.AsSpan().SequenceEqual(q));

    static string? BytesDetail(object? x, object? y)
    {
        if (x is not byte[] p || y is not byte[] q) return null;
        if (p.Length != q.Length)
        {
            var common = 0;
            while (common < p.Length && common < q.Length && p[common] == q[common]) common++;
            return $"{p.Length:N0} → {q.Length:N0} bytes; the same for the first {common:N0}";
        }
        int first = -1, last = -1, count = 0;
        for (var i = 0; i < p.Length; i++)
            if (p[i] != q[i])
            {
                if (first < 0) first = i;
                last = i;
                count++;
            }
        if (first < 0) return null;
        var from = first;
        var to = Math.Min(Math.Max(last + 1, first + 4), Math.Min(first + 16, p.Length));
        var where = count == 1 ? $"byte {first}" : $"{count:N0} bytes between {first} and {last}";
        return $"{where}: {LabText.Hex(p.AsSpan(from, to - from))} → {LabText.Hex(q.AsSpan(from, to - from))}";
    }

    public static string DiffText(LabFile before, LabFile after, IReadOnlyList<LabChange> changes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{before.Daw} {before.Version} → {after.Version} · {before.Name} → {after.Name}");
        if (before.Problem is not null) sb.AppendLine($"Only part of {before.Name} could be read: {before.Problem}");
        if (after.Problem is not null) sb.AppendLine($"Only part of {after.Name} could be read: {after.Problem}");
        sb.AppendLine(changes.Count switch { 0 => "No differences.", 1 => "1 difference:", _ => $"{changes.Count:N0} differences:" });
        foreach (var c in changes)
        {
            var inside = c.Inside == 0 ? "" : c.Inside == 1 ? " (with 1 thing inside)" : $" (with {c.Inside:N0} things inside)";
            switch (c.Kind)
            {
                case "changed":
                    sb.AppendLine($"changed  {c.Path}");
                    sb.AppendLine($"         {c.Before} → {c.After}");
                    if (c.Detail is not null) sb.AppendLine($"         {c.Detail}");
                    break;
                case "added":
                    sb.AppendLine($"added    {c.Path}{(c.After is { Length: > 0 } v ? $": {v}" : "")}{inside}");
                    break;
                default:
                    sb.AppendLine($"removed  {c.Path}{(c.Before is { Length: > 0 } w ? $": {w}" : "")}{inside}");
                    break;
            }
        }
        return sb.ToString();
    }

    // ---- Find ----

    /// <summary>
    /// Everywhere the file holds <paramref name="query"/>: as text (ignoring case), and, when it's a number, as
    /// a number in any field, or in any data as a 32-bit integer, a 32- or 64-bit float (either byte order).
    /// Text is also looked for inside data, as UTF-8 and UTF-16.
    /// </summary>
    public static IReadOnlyList<LabHit> Find(LabFile f, string query, bool textOnly = false)
    {
        var hits = new List<LabHit>();
        if (query.Length == 0) return hits;
        double? number = !textOnly && double.TryParse(query, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
        var patterns = Patterns(query, number);
        foreach (var i in f.Items)
        {
            switch (i.Data)
            {
                case string s when s.Contains(query, StringComparison.OrdinalIgnoreCase):
                    hits.Add(new LabHit(i.Path, i.Value, "text"));
                    break;
                case long l when number is { } x && l == x:
                case double d when number is { } y && Close(d, y):
                    hits.Add(new LabHit(i.Path, i.Value, "number"));
                    break;
                case long[] a when number is { } x && Array.FindIndex(a, v => v == x) is var k and >= 0:
                    hits.Add(new LabHit(i.Path, i.Value, $"number, item {k} of the list"));
                    break;
                case double[] a when number is { } x && Array.FindIndex(a, v => Close(v, x)) is var k and >= 0:
                    hits.Add(new LabHit(i.Path, i.Value, $"number, item {k} of the list"));
                    break;
                case string[] a when Array.FindIndex(a, v => v.Contains(query, StringComparison.OrdinalIgnoreCase)) is var k and >= 0:
                    hits.Add(new LabHit(i.Path, i.Value, $"text, item {k} of the list"));
                    break;
                case byte[] b:
                    foreach (var (name, bytes) in patterns)
                    {
                        var found = 0;
                        for (var at = b.AsSpan().IndexOf(bytes); at >= 0 && found < 10; found++)
                        {
                            hits.Add(new LabHit(i.Path, i.Value, $"{name} at byte {at:N0}"));
                            var next = b.AsSpan(at + 1).IndexOf(bytes);
                            at = next < 0 ? -1 : at + 1 + next;
                        }
                    }
                    break;
            }
        }
        return hits;
    }

    static bool Close(double a, double b) => Math.Abs(a - b) <= Math.Max(1e-9, Math.Abs(b) * 1e-6);

    static List<(string Name, byte[] Bytes)> Patterns(string query, double? number)
    {
        var p = new List<(string, byte[])>();
        if (number is { } n)
        {
            if (n == Math.Floor(n) && n >= int.MinValue && n <= uint.MaxValue && Math.Abs(n) >= 2)
            {
                var b = new byte[4];
                if (n <= int.MaxValue) BinaryPrimitives.WriteInt32LittleEndian(b, (int)n); else BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)n);
                p.Add(("32-bit integer", b));
                p.Add(("32-bit integer, big-endian", b.Reverse().ToArray()));
            }
            var f = new byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(f, (float)n);
            p.Add(("32-bit float", f));
            p.Add(("32-bit float, big-endian", f.Reverse().ToArray()));
            var d = new byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(d, n);
            p.Add(("64-bit float", d));
            p.Add(("64-bit float, big-endian", d.Reverse().ToArray()));
        }
        else
        {
            p.Add(("UTF-8 text", Encoding.UTF8.GetBytes(query)));
            p.Add(("UTF-16 text", Encoding.Unicode.GetBytes(query)));
            p.Add(("UTF-16 text, big-endian", Encoding.BigEndianUnicode.GetBytes(query)));
        }
        return p;
    }

    public static string FindText(LabFile f, string query, IReadOnlyList<LabHit> hits, int max = 200)
    {
        var sb = new StringBuilder();
        Heading(sb, f);
        sb.AppendLine(hits.Count switch
        {
            0 => $"{Quote(query)} isn't in it.",
            1 => $"Found {Quote(query)} once:",
            _ => $"Found {Quote(query)} {hits.Count:N0} times:",
        });
        foreach (var h in hits.Take(max))
            sb.AppendLine($"  {h.Path} = {h.Value}").AppendLine($"    as {h.How}");
        if (hits.Count > max) sb.AppendLine($"  … and {hits.Count - max:N0} more");
        return sb.ToString();
    }

    static string Quote(string s) => LabText.Quote(s);
}
