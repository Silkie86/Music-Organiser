using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicOrganiser.Core.Formats;

/// <summary>
/// One thing the app knows about a DAW's file format: a Bitwig class or field, an FL Studio event, or a
/// record inside one, with what it means and how that was found out.
/// </summary>
public sealed record FormatEntry
{
    /// <summary>The id as the format writes it: hex for Bitwig ("0x9a"), decimal for FL Studio ("156").</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string About { get; init; } = "";
    /// <summary>
    /// Bitwig: the value's type. FL Studio events: byte, word, dword, text, ascii or data. Records inside them:
    /// u32, i32, fourcc (a VST2 ID), utf8 or bytes.
    /// </summary>
    public string? Type { get; init; }
    /// <summary>FL Studio data: named numbers at fixed offsets, e.g. "pan i32@0, volume i32@4".</summary>
    public string? Layout { get; init; }
    /// <summary>FL Studio data made of equal records: their size, where the first starts, and their layout.</summary>
    public int? Record { get; init; }
    public int? RecordAt { get; init; }
    public string? RecordLayout { get; init; }
    /// <summary>A record size that changed with FL's version: from version (major) → size.</summary>
    public Dictionary<string, int>? RecordByVersion { get; init; }
    /// <summary>The DAW versions it has been seen in.</summary>
    public string? Seen { get; init; }
    /// <summary>How it was found out or checked: a file, a test, or a confirmation.</summary>
    public string? Proof { get; init; }
    /// <summary>The constant that holds this id in the app's code, if one does.</summary>
    public string? Code { get; init; }

    [JsonIgnore]
    public int Number => Id.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? int.Parse(Id.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : int.Parse(Id, CultureInfo.InvariantCulture);

    /// <summary>The record size for an FL Studio version, from <see cref="RecordByVersion"/> or <see cref="Record"/>.</summary>
    public int? RecordSize(int major)
    {
        if (RecordByVersion is null) return Record;
        int? size = null;
        var from = -1;
        foreach (var (v, s) in RecordByVersion)
            if (int.TryParse(v, out var n) && n <= major && n > from) (from, size) = (n, s);
        return size ?? Record;
    }
}

/// <summary>One named number in an FL Studio layout: "pan i32@0" is a 32-bit integer called pan at byte 0.</summary>
public readonly record struct LayoutField(string Name, string Type, int Offset)
{
    /// <summary>How many bytes it takes: 1 to 8.</summary>
    public int Size => SizeOf(Type);

    /// <summary>Reads "pan i32@0, volume i32@4": each a name, a type (u8 to u64, i8 to i64, f32, f64) and @offset.</summary>
    /// <exception cref="FormatException">A part isn't written that way.</exception>
    public static IReadOnlyList<LayoutField> Parse(string? layout)
    {
        var fields = new List<LayoutField>();
        if (string.IsNullOrWhiteSpace(layout)) return fields;
        foreach (var part in layout.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var space = part.LastIndexOf(' ');
            var spec = part[(space + 1)..].Split('@');
            if (space <= 0 || spec.Length != 2 || SizeOf(spec[0]) == 0 ||
                !int.TryParse(spec[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                throw new FormatException($"Can't read the layout \"{part}\": it should be a name, a type and @offset");
            fields.Add(new LayoutField(part[..space], spec[0], offset));
        }
        return fields;
    }

    /// <summary>The number at its offset in <paramref name="data"/> (little-endian, as FL writes them), or null past the end.</summary>
    public object? Read(ReadOnlySpan<byte> data)
    {
        if (Offset + Size > data.Length) return null;
        var b = data[Offset..];
        return Type switch
        {
            "u8" => (long)b[0],
            "i8" => (long)(sbyte)b[0],
            "u16" => (long)BinaryPrimitives.ReadUInt16LittleEndian(b),
            "i16" => (long)BinaryPrimitives.ReadInt16LittleEndian(b),
            "u32" => (long)BinaryPrimitives.ReadUInt32LittleEndian(b),
            "i32" => (long)BinaryPrimitives.ReadInt32LittleEndian(b),
            "u64" => (long)BinaryPrimitives.ReadUInt64LittleEndian(b),
            "i64" => BinaryPrimitives.ReadInt64LittleEndian(b),
            "f32" => (double)BinaryPrimitives.ReadSingleLittleEndian(b),
            "f64" => BinaryPrimitives.ReadDoubleLittleEndian(b),
            _ => null,
        };
    }

    static int SizeOf(string type) => type switch
    {
        "u8" or "i8" => 1, "u16" or "i16" => 2, "u32" or "i32" or "f32" => 4, "u64" or "i64" or "f64" => 8, _ => 0,
    };
}

/// <summary>
/// The field registry: everything the app knows about one DAW's file format, written down once. Format Lab
/// names what it shows with it, the census counts what isn't in it, and the tests check that every id the
/// readers and writers use is in it. The data is in Formats/Registry/*.json.
/// </summary>
public sealed class FormatRegistry
{
    public string Format { get; init; } = "";
    public string About { get; init; } = "";
    /// <summary>Bitwig: classes of object.</summary>
    public List<FormatEntry> Classes { get; init; } = [];
    /// <summary>Bitwig: fields of objects (one numbering for all classes).</summary>
    public List<FormatEntry> Fields { get; init; } = [];
    /// <summary>Bitwig: metadata keys, by name in <see cref="FormatEntry.Id"/>.</summary>
    public List<FormatEntry> Meta { get; init; } = [];
    /// <summary>FL Studio: events.</summary>
    public List<FormatEntry> Events { get; init; } = [];
    /// <summary>FL Studio: records in a Fruity Wrapper state (event 213 of a plugin FL hosts).</summary>
    public List<FormatEntry> Wrapper { get; init; } = [];
    /// <summary>FL Studio: records in a VST3 plugin's state (the Fruity Wrapper's record 53).</summary>
    public List<FormatEntry> Vst3 { get; init; } = [];

    // Loaded on first use, once the JSON options below exist (static fields are set in the order they're written).
    static readonly Lazy<FormatRegistry> BitwigJson = new(() => Load("bitwig.json")), FlStudioJson = new(() => Load("fl-studio.json"));
    public static FormatRegistry Bitwig => BitwigJson.Value;
    public static FormatRegistry FlStudio => FlStudioJson.Value;

    Dictionary<int, FormatEntry>? _classes, _fields, _events, _wrapper, _vst3;
    Dictionary<string, FormatEntry>? _meta;

    public FormatEntry? Class(int id) => (_classes ??= Index(Classes)).GetValueOrDefault(id);
    public FormatEntry? Field(int id) => (_fields ??= Index(Fields)).GetValueOrDefault(id);
    public FormatEntry? Event(int id) => (_events ??= Index(Events)).GetValueOrDefault(id);
    public FormatEntry? WrapperRecord(int id) => (_wrapper ??= Index(Wrapper)).GetValueOrDefault(id);
    public FormatEntry? Vst3Record(int id) => (_vst3 ??= Index(Vst3)).GetValueOrDefault(id);
    public FormatEntry? MetaKey(string key) =>
        (_meta ??= Meta.ToDictionary(m => m.Id, StringComparer.Ordinal)).GetValueOrDefault(key);

    static Dictionary<int, FormatEntry> Index(IEnumerable<FormatEntry> entries) => entries.ToDictionary(e => e.Number);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads a registry from its JSON.</summary>
    public static FormatRegistry Parse(string json) =>
        JsonSerializer.Deserialize<FormatRegistry>(json, Json) ?? throw new InvalidDataException("The field registry is empty");

    static FormatRegistry Load(string name)
    {
        using var s = typeof(FormatRegistry).Assembly.GetManifestResourceStream($"MusicOrganiser.Formats.{name}")
                      ?? throw new InvalidOperationException($"The field registry {name} isn't built into the app");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }
}
