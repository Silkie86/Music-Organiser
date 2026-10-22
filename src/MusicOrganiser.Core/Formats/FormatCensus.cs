using System.Globalization;
using System.Text;
using MusicOrganiser.Core.Parsers;
using static MusicOrganiser.Core.Parsers.BitwigFormat;

namespace MusicOrganiser.Core.Formats;

/// <summary>
/// Counts, across many projects, what's in them that the field registry doesn't know yet: FL Studio events,
/// Bitwig classes and fields. Ranked by how many projects use each, it says what's worth decoding next. It
/// keeps only ids and counts, nothing of the music.
/// </summary>
public sealed class FormatCensus
{
    /// <summary>How often one id turned up.</summary>
    public sealed class Tally
    {
        public int Projects { get; set; }
        public long Times { get; set; }
        /// <summary>FL Studio: payload sizes. Bitwig: value types.</summary>
        public Dictionary<int, int> Shapes { get; } = new();
        /// <summary>Bitwig fields: the classes they were in.</summary>
        public Dictionary<int, int> In { get; } = new();
        public SortedSet<string> Versions { get; } = new(StringComparer.Ordinal);

        internal void Merge(Tally o)
        {
            Projects += o.Projects;
            Times += o.Times;
            foreach (var (k, v) in o.Shapes) Shapes[k] = Shapes.GetValueOrDefault(k) + v;
            foreach (var (k, v) in o.In) In[k] = In.GetValueOrDefault(k) + v;
            Versions.UnionWith(o.Versions);
        }
    }

    public int FlProjects { get; private set; }
    public int BitwigProjects { get; private set; }
    /// <summary>Bitwig projects that didn't decode to the end (what came before the problem is still counted).</summary>
    public int BitwigPartly { get; private set; }
    public SortedDictionary<string, int> FlVersions { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> BitwigVersions { get; } = new(StringComparer.Ordinal);
    public Dictionary<int, Tally> FlEvents { get; } = new();
    public Dictionary<int, Tally> BitwigClasses { get; } = new();
    public Dictionary<int, Tally> BitwigFields { get; } = new();
    /// <summary>Files that couldn't be read at all, with why (the first 20).</summary>
    public List<(string File, string Why)> Unreadable { get; } = new();
    public int UnreadableCount { get; private set; }

    /// <summary>Counts every .flp and .bwproject in <paramref name="files"/>, several at a time.</summary>
    public static FormatCensus Run(IEnumerable<string> files, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var total = new FormatCensus();
        var done = 0;
        Parallel.ForEach(files.Where(IsProject),
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Parallelism.Workers },
            () => new FormatCensus(),
            (file, _, mine) =>
            {
                mine.Add(file);
                var n = Interlocked.Increment(ref done);
                if (n % 100 == 0) progress?.Report(n);
                return mine;
            },
            mine => { lock (total) total.Merge(mine); });
        progress?.Report(done);
        return total;
    }

    public static bool IsProject(string path) =>
        path.EndsWith(".flp", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bwproject", StringComparison.OrdinalIgnoreCase);

    /// <summary>Counts one project.</summary>
    public void Add(string path)
    {
        try
        {
            var raw = File.ReadAllBytes(path);
            if (IsBitwig(raw)) AddBitwig(raw);
            else AddFl(raw);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            UnreadableCount++;
            if (Unreadable.Count < 20) Unreadable.Add((path, e.Message));
        }
    }

    void AddFl(byte[] raw)
    {
        var s = FlStudioReader.ReadEvents(raw);
        var version = s.Major > 0 ? s.Major.ToString(CultureInfo.InvariantCulture) : "unknown";
        FlProjects++;
        FlVersions[version] = FlVersions.GetValueOrDefault(version) + 1;
        var mine = new Dictionary<int, Tally>();
        foreach (var e in s.Events)
        {
            if (!mine.TryGetValue(e.Id, out var t)) mine[e.Id] = t = new Tally { Projects = 1 };
            t.Times++;
            t.Shapes[e.Payload.Length] = t.Shapes.GetValueOrDefault(e.Payload.Length) + 1;
        }
        foreach (var (id, t) in mine)
        {
            t.Versions.Add(version);
            Into(FlEvents, id).Merge(t);
        }
    }

    void AddBitwig(byte[] raw)
    {
        var doc = Decode(raw);
        var v = doc.MetaString("application_version_name");
        var version = string.Join('.', v.Split('.', ' ').Take(2)) is { Length: > 0 } mm ? mm : "unknown";
        BitwigProjects++;
        if (doc.BodyError is not null) BitwigPartly++;
        BitwigVersions[version] = BitwigVersions.GetValueOrDefault(version) + 1;
        var classes = new Dictionary<int, Tally>();
        var fields = new Dictionary<int, Tally>();
        foreach (var o in doc.Objects)
        {
            if (!classes.TryGetValue(o.Class, out var c)) classes[o.Class] = c = new Tally { Projects = 1 };
            c.Times++;
            for (var i = 0; i < o.Fields.Count; i++)
            {
                var id = o.Fields[i].Id;
                if (!fields.TryGetValue(id, out var f)) fields[id] = f = new Tally { Projects = 1 };
                f.Times++;
                if (i < o.Types.Count) f.Shapes[o.Types[i]] = f.Shapes.GetValueOrDefault(o.Types[i]) + 1;
                f.In[o.Class] = f.In.GetValueOrDefault(o.Class) + 1;
            }
        }
        foreach (var (id, t) in classes) { t.Versions.Add(version); Into(BitwigClasses, id).Merge(t); }
        foreach (var (id, t) in fields) { t.Versions.Add(version); Into(BitwigFields, id).Merge(t); }
    }

    static Tally Into(Dictionary<int, Tally> d, int id)
    {
        if (!d.TryGetValue(id, out var t)) d[id] = t = new Tally();
        return t;
    }

    void Merge(FormatCensus o)
    {
        FlProjects += o.FlProjects;
        BitwigProjects += o.BitwigProjects;
        BitwigPartly += o.BitwigPartly;
        UnreadableCount += o.UnreadableCount;
        foreach (var (k, v) in o.FlVersions) FlVersions[k] = FlVersions.GetValueOrDefault(k) + v;
        foreach (var (k, v) in o.BitwigVersions) BitwigVersions[k] = BitwigVersions.GetValueOrDefault(k) + v;
        foreach (var (k, t) in o.FlEvents) Into(FlEvents, k).Merge(t);
        foreach (var (k, t) in o.BitwigClasses) Into(BitwigClasses, k).Merge(t);
        foreach (var (k, t) in o.BitwigFields) Into(BitwigFields, k).Merge(t);
        foreach (var u in o.Unreadable) if (Unreadable.Count < 20) Unreadable.Add(u);
    }

    /// <summary>FL events the registry doesn't have, most used first.</summary>
    public IEnumerable<(int Id, Tally Tally)> UnknownFlEvents =>
        FlEvents.Where(e => FormatRegistry.FlStudio.Event(e.Key) is null).Select(e => (e.Key, e.Value)).OrderByDescending(e => e.Value.Projects).ThenBy(e => e.Key);

    public IEnumerable<(int Id, Tally Tally)> UnknownBitwigClasses =>
        BitwigClasses.Where(e => FormatRegistry.Bitwig.Class(e.Key) is null).Select(e => (e.Key, e.Value)).OrderByDescending(e => e.Value.Projects).ThenBy(e => e.Key);

    public IEnumerable<(int Id, Tally Tally)> UnknownBitwigFields =>
        BitwigFields.Where(e => FormatRegistry.Bitwig.Field(e.Key) is null).Select(e => (e.Key, e.Value)).OrderByDescending(e => e.Value.Projects).ThenBy(e => e.Key);

    /// <summary>The census as text: names and counts only, to paste into a conversation.</summary>
    public string Report(int top = 40)
    {
        var sb = new StringBuilder();
        var files = FlProjects + BitwigProjects + UnreadableCount;
        sb.AppendLine($"Format census of {files:N0} project{(files == 1 ? "" : "s")}");
        if (UnreadableCount > 0)
        {
            sb.AppendLine($"{UnreadableCount:N0} couldn't be read at all, for example:");
            foreach (var (file, why) in Unreadable.Take(5)) sb.AppendLine($"  {Path.GetFileName(file)}: {why}");
        }

        if (FlProjects > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"FL Studio: {FlProjects:N0} project{(FlProjects == 1 ? "" : "s")} ({Versions(FlVersions, "FL ")})");
            var known = FlEvents.Count(e => FormatRegistry.FlStudio.Event(e.Key) is not null);
            var unknown = UnknownFlEvents.ToList();
            sb.AppendLine($"{FlEvents.Count:N0} kinds of event; the registry knows {known:N0}. The rest, by how many projects have them:");
            foreach (var (id, t) in unknown.Take(top))
                sb.AppendLine($"  event {id,-4} {Share(t.Projects, FlProjects),-22} FL {Span(t.Versions),-9} {Sizes(t.Shapes)}");
            More(sb, unknown.Count, top);
        }

        if (BitwigProjects > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Bitwig Studio: {BitwigProjects:N0} project{(BitwigProjects == 1 ? "" : "s")} ({Versions(BitwigVersions, "")})" +
                          (BitwigPartly > 0 ? $"; {BitwigPartly:N0} only partly decoded" : ""));
            var classes = UnknownBitwigClasses.ToList();
            sb.AppendLine($"{BitwigClasses.Count:N0} classes; the registry knows {BitwigClasses.Count - classes.Count:N0}. The rest, by how many projects have them:");
            foreach (var (id, t) in classes.Take(top))
                sb.AppendLine($"  class 0x{id,-6:x} {Share(t.Projects, BitwigProjects),-22} {t.Times:N0} object{(t.Times == 1 ? "" : "s")}");
            More(sb, classes.Count, top);
            var fields = UnknownBitwigFields.ToList();
            sb.AppendLine($"{BitwigFields.Count:N0} fields; the registry knows {BitwigFields.Count - fields.Count:N0}. The rest, by how many projects have them:");
            foreach (var (id, t) in fields.Take(top))
                sb.AppendLine($"  field 0x{id,-6:x} {Share(t.Projects, BitwigProjects),-22} {Types(t.Shapes),-8} mostly in {Class(t.In)}");
            More(sb, fields.Count, top);
        }
        return sb.ToString();
    }

    static void More(StringBuilder sb, int count, int top)
    {
        if (count > top) sb.AppendLine($"  … and {count - top:N0} more");
    }

    static string Versions(SortedDictionary<string, int> v, string prefix) =>
        string.Join(", ", v.OrderBy(x => Version.TryParse(x.Key.Contains('.') ? x.Key : x.Key + ".0", out var p) ? p : new Version(0, 0))
                           .Select(x => $"{prefix}{x.Key}: {x.Value:N0}"));

    static string Share(int n, int of) => $"in {n:N0} ({100.0 * n / Math.Max(1, of):0.#}%)";

    static string Span(SortedSet<string> versions)
    {
        var numbers = versions.Select(v => int.TryParse(v, out var n) ? n : -1).Where(n => n >= 0).Order().ToList();
        return numbers.Count == 0 ? "?" : numbers[0] == numbers[^1] ? $"{numbers[0]}" : $"{numbers[0]}-{numbers[^1]}";
    }

    static string Sizes(Dictionary<int, int> sizes)
    {
        var common = sizes.OrderByDescending(s => s.Value).Take(3).Select(s => s.Key == 1 ? "1 byte" : $"{s.Key:N0} bytes").ToList();
        return sizes.Count <= 3 ? string.Join(" or ", common) : $"{string.Join(", ", common)} or others";
    }

    static string Types(Dictionary<int, int> types) =>
        string.Join("/", types.OrderByDescending(t => t.Value).Take(2).Select(t => TypeName((byte)t.Key)));

    /// <summary>Bitwig's value types (see BitwigFormat).</summary>
    public static string TypeName(byte type) => type switch
    {
        0x01 => "u8", 0x02 => "i16", 0x03 => "i32", 0x04 => "i64", 0x05 => "bool", 0x06 => "f32", 0x07 => "f64",
        0x08 => "string", 0x09 => "object", 0x0a => "null", 0x0b => "ref", 0x0d => "blob", 0x0f => "ints",
        0x12 => "list", 0x14 => "map", 0x15 => "uuid", 0x16 => "rgba", 0x17 => "floats", 0x18 => "doubles", 0x19 => "strings",
        0x1a => "named", _ => $"0x{type:x2}",
    };

    static string Class(Dictionary<int, int> classes)
    {
        if (classes.Count == 0) return "?";
        var id = classes.MaxBy(c => c.Value).Key;
        return FormatRegistry.Bitwig.Class(id) is { } c ? $"0x{id:x} {c.Name}" : $"0x{id:x}";
    }
}
