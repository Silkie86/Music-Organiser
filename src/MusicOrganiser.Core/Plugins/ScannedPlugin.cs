using System.Text.Json;

namespace MusicOrganiser.Core.Plugins;

/// <summary>
/// One plugin inside a plugin file, as the native scanner (native/mo-scan) read it. A VST3 or CLAP
/// file can hold several plugins, and a VST2 "shell" like Waves' holds hundreds.
/// </summary>
public sealed record ScannedPlugin
{
    /// <summary>CLAP, VST3 or VST2.</summary>
    public string Format { get; init; } = "";
    public string Path { get; init; } = "";
    /// <summary>
    /// The exact ID DAW projects store: the VST3 class ID (32 hex digits), the CLAP ID
    /// ("com.u-he.diva") or the VST2 four-letter ID.
    /// </summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string Version { get; init; } = "";
    /// <summary>instrument, effect, note-effect or analyzer; empty when the file doesn't say.</summary>
    public string Kind { get; init; } = "";
    /// <summary>The VST2 shell file the plugin lives in (Waves), if any.</summary>
    public string Shell { get; init; } = "";
    public IReadOnlyList<string> Features { get; init; } = [];
    public int? AudioIn { get; init; }
    public int? AudioOut { get; init; }
    public int? NoteIn { get; init; }
    public int? NoteOut { get; init; }
    /// <summary>Null until its parameters are read, which means loading the plugin.</summary>
    public IReadOnlyList<ScannedParam>? Params { get; init; }
    /// <summary>Why the scanner couldn't load this plugin, if it couldn't.</summary>
    public string Error { get; init; } = "";
    /// <summary>The scanner's line for this plugin, kept as it was.</summary>
    public string Json { get; init; } = "";

    /// <summary>One line of mo-scan output, or null if it isn't a plugin (a whole-file error, or not JSON).</summary>
    public static ScannedPlugin? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object || Str(o, "id") is not { Length: > 0 } id) return null;
            return new ScannedPlugin
            {
                Format = Str(o, "format"), Path = Str(o, "path"), Id = id, Name = Str(o, "name"),
                Vendor = Str(o, "vendor"), Version = Str(o, "version"), Kind = Str(o, "kind"), Shell = Str(o, "shell"),
                Features = o.TryGetProperty("features", out var f) && f.ValueKind == JsonValueKind.Array
                    ? f.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : [],
                AudioIn = Int(o, "audioIn"), AudioOut = Int(o, "audioOut"), NoteIn = Int(o, "noteIn"), NoteOut = Int(o, "noteOut"),
                Params = o.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Array
                    ? p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(ScannedParam.From).ToList()
                    : null,
                Error = Str(o, "error"),
                Json = line,
            };
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A whole-file error line ({"path":…,"error":…}), if this is one.</summary>
    public static string? FileError(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            return o.ValueKind == JsonValueKind.Object && !o.TryGetProperty("id", out _) && Str(o, "error") is { Length: > 0 } e ? e : null;
        }
        catch (JsonException) { return null; }
    }

    internal static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static int? Int(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    internal static double? Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    internal static bool Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>One of a plugin's parameters, as it describes itself.</summary>
public sealed record ScannedParam
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>The value when the plugin is first loaded, as the plugin shows it ("-6.0 dB", "Saw").</summary>
    public string Text { get; init; } = "";
    public string Units { get; init; } = "";
    /// <summary>The range in the plugin's own units, when the format says (CLAP, VST3).</summary>
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Default { get; init; }
    /// <summary>How many steps a stepped parameter has (a switch has 1), or 0 for a smooth one.</summary>
    public int Steps { get; init; }
    public bool Automatable { get; init; }
    public bool Hidden { get; init; }
    public bool ReadOnly { get; init; }
    public bool Bypass { get; init; }
    /// <summary>The group the plugin files it under (CLAP module path), if any.</summary>
    public string Group { get; init; } = "";

    /// <summary>The value and units together, without doubling units the text already has.</summary>
    public string Display => Units.Length == 0 || Text.EndsWith(Units, StringComparison.OrdinalIgnoreCase) ? Text : $"{Text} {Units}";

    /// <summary>"20 to 20000 Hz, starts at 440", or empty when the format doesn't say.</summary>
    public string Range
    {
        get
        {
            if (Min is not { } lo || Max is not { } hi) return "";
            var units = Units.Length > 0 ? $" {Units}" : "";
            var text = $"{lo:G6} to {hi:G6}{units}";
            if (Steps > 0) text += $", {Steps + 1} positions";
            return Default is { } d ? $"{text}, starts at {d:G6}" : text;
        }
    }

    internal static ScannedParam From(JsonElement o)
    {
        // CLAP gives its range and default in the parameter's own units; VST3 gives the range as
        // plainMin/plainMax and the default 0-1 (plainDefault in its own units); VST2 only 0-1.
        var min = ScannedPlugin.Num(o, "min") ?? ScannedPlugin.Num(o, "plainMin");
        var max = ScannedPlugin.Num(o, "max") ?? ScannedPlugin.Num(o, "plainMax");
        var steps = ScannedPlugin.Int(o, "steps")
                    ?? (ScannedPlugin.Bool(o, "stepped") && min is { } lo && max is { } hi && hi - lo is > 0 and < int.MaxValue ? (int)(hi - lo) : 0);
        return new ScannedParam
        {
            Id = o.TryGetProperty("id", out var id) && id.TryGetInt64(out var i) ? i : 0,
            Name = ScannedPlugin.Str(o, "name"),
            Text = ScannedPlugin.Str(o, "text"),
            Units = ScannedPlugin.Str(o, "units"),
            Min = min,
            Max = max,
            Default = o.TryGetProperty("plainMin", out _) ? ScannedPlugin.Num(o, "plainDefault") : ScannedPlugin.Num(o, "default"),
            Steps = steps,
            // VST2 has no flags: every parameter can be automated.
            Automatable = o.TryGetProperty("automatable", out _) ? ScannedPlugin.Bool(o, "automatable") : true,
            Hidden = ScannedPlugin.Bool(o, "hidden"),
            ReadOnly = ScannedPlugin.Bool(o, "readOnly"),
            Bypass = ScannedPlugin.Bool(o, "bypass"),
            Group = ScannedPlugin.Str(o, "module"),
        };
    }
}
