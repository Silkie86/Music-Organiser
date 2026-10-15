using System.Text;
using System.Text.Json;

namespace MusicOrganiser.Core.Plugins;

/// <summary>
/// A VST3 bundle's moduleinfo.json (in Contents/Resources, written by plugins built with the VST3 SDK 3.7.5 or
/// later): the plugins inside, as the scanner would list them, read without loading the plugin. That's quicker, and
/// a plugin that shows a licence window or crashes when loaded isn't loaded at all.
/// </summary>
public static class Vst3ModuleInfo
{
    /// <summary>VST3's category for a plugin a DAW can load (kVstAudioEffectClass); a bundle also lists its editors and the like.</summary>
    const string AudioClass = "Audio Module Class";

    /// <summary>A real one is a few kilobytes; anything much bigger isn't one.</summary>
    const long MaxSize = 1 << 20;

    static readonly JsonDocumentOptions Json5ish = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static string FileIn(string bundle) => Path.Combine(bundle, "Contents", "Resources", "moduleinfo.json");

    /// <summary>
    /// The plugins a bundle's moduleinfo.json lists, as mo-scan lists them without loading anything (no ports or
    /// parameters). Null when the bundle has none, or it can't be read or lists no plugin: the scanner loads it then.
    /// </summary>
    public static IReadOnlyList<ScannedPlugin>? Read(string bundle)
    {
        try
        {
            var file = new FileInfo(FileIn(bundle));
            if (!Directory.Exists(bundle) || !file.Exists || file.Length > MaxSize) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file.FullName), Json5ish);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Classes", out var classes) ||
                classes.ValueKind != JsonValueKind.Array) return null;
            var factoryVendor = root.TryGetProperty("Factory Info", out var factory) && factory.ValueKind == JsonValueKind.Object
                ? Str(factory, "Vendor") : "";
            var plugins = classes.EnumerateArray()
                .Where(c => c.ValueKind == JsonValueKind.Object && Str(c, "Category") == AudioClass)
                .Select(c => Plugin(c, bundle, factoryVendor))
                .OfType<ScannedPlugin>()
                .ToList();
            return plugins.Count > 0 ? plugins : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>One class as mo-scan's line for it (src/scan_vst3.cpp), so it's kept and read back the same way.</summary>
    static ScannedPlugin? Plugin(JsonElement c, string bundle, string factoryVendor)
    {
        // The SDK writes the ID as mo-scan does: 32 hex digits of the four numbers DAWs save.
        var id = Str(c, "CID").Trim().ToUpperInvariant();
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) return null;
        var features = c.TryGetProperty("Sub Categories", out var sub) ? sub.ValueKind switch
        {
            JsonValueKind.Array => sub.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!).ToList(),
            JsonValueKind.String => sub.GetString()!.Split('|').ToList(),
            _ => [],
        } : [];
        var vendor = Str(c, "Vendor") is { Length: > 0 } v ? v : factoryVendor;

        using var line = new MemoryStream();
        using (var w = new Utf8JsonWriter(line))
        {
            w.WriteStartObject();
            w.WriteString("format", "VST3");
            w.WriteString("path", bundle);
            w.WriteString("id", id);
            w.WriteString("name", Str(c, "Name"));
            w.WriteString("vendor", vendor);
            w.WriteString("version", Str(c, "Version"));
            w.WriteString("kind", features.Any(f => f.Contains("Instrument", StringComparison.Ordinal)) ? "instrument" : "effect");
            w.WriteStartArray("features");
            foreach (var f in features) w.WriteStringValue(f);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ScannedPlugin.Parse(Encoding.UTF8.GetString(line.ToArray()));
    }

    static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}
