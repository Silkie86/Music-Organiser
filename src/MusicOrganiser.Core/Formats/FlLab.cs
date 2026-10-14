using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Formats;

/// <summary>
/// Lays out an FL Studio project for Format Lab: every event in file order, under the part of the project it
/// belongs to (the project, a channel, a pattern, the arrangement, a mixer insert or one of its slots), with
/// the registry's layouts and a Fruity Wrapper's records taken apart.
/// </summary>
static class FlLab
{
    const int EvChannelNew = 64, EvPatternNew = 65, EvMixerSlot = 98, EvVersion = 199, EvPluginType = 201;
    const int EvPluginState = 213, EvPlaylist = 233, EvInsertFlags = 236, EvInsertName = 204;
    const int WrapperFormat = 50, WrapperState = 53, FormatVst3 = 8;

    static FormatRegistry Registry => FormatRegistry.FlStudio;

    public static LabFile Read(byte[] raw, string name)
    {
        var s = FlStudioReader.ReadEvents(raw);
        var scopes = Scopes(s);
        var items = new List<LabItem>();
        var counts = new Dictionary<(string, int), int>();
        var pluginTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        string? last = null;
        foreach (var e in s.Events)
        {
            var scope = scopes[e.Index];
            if (scope != last)
            {
                if (!counts.ContainsKey((scope, -1))) items.Add(new LabItem(scope, scope, 0, scope, "", null, true));
                counts[(scope, -1)] = 1;
                last = scope;
            }
            var n = counts.GetValueOrDefault((scope, e.Id));
            counts[(scope, e.Id)] = n + 1;
            var entry = Registry.Event(e.Id);
            var label = (entry is null ? $"{e.Id}" : $"{e.Id} {entry.Name}") + (n > 0 ? $" #{n + 1}" : "");
            var key = $"{scope}/{e.Id}{(n > 0 ? $"#{n + 1}" : "")}";
            var path = $"{scope} › {label}";
            var span = e.Payload.Span;

            if (e.IsNumber)
            {
                items.Add(new LabItem(key, path, 1, label, LabText.Number(e.Int), (long)e.Int));
                continue;
            }
            var text = entry?.Type switch
            {
                "text" => s.Text(span),
                "ascii" => Encoding.Latin1.GetString(span).TrimEnd('\0'),
                _ => entry is null && LooksLikeText(span) ? s.Text(span) : null,
            };
            if (text is not null)
            {
                items.Add(new LabItem(key, path, 1, label, LabText.Quote(text), text));
                if (e.Id == EvPluginType) pluginTypes[scope] = text;
                continue;
            }

            var bytes = span.ToArray();
            var parts = new List<LabItem>();
            if (entry is not null) Layout(entry, bytes, s.Major, key, path, parts);
            if (e.Id == EvPluginState && pluginTypes.GetValueOrDefault(scope) == "Fruity Wrapper") Wrapper(bytes, key, path, parts);
            items.Add(new LabItem(key, path, 1, label, LabText.Bytes(bytes), bytes, parts.Count > 0));
            items.AddRange(parts);
        }
        return new LabFile { Name = name, Format = LabFormat.FlStudio, Version = s.Version, Items = items };
    }

    /// <summary>
    /// Which part of the project each event belongs to. Channels and patterns start with their own events
    /// (64, 65) and the arrangement with the playlist (233). A mixer insert starts with its flags (236), its
    /// name just before them; a slot's events end with the slot's number (98), so they're only known to be
    /// the slot's once that comes.
    /// </summary>
    static string[] Scopes(FlEventStream s)
    {
        var scopes = new string[s.Events.Count];
        var current = "project";
        var insert = -1;
        var held = new List<int>();
        void Release()
        {
            foreach (var i in held) scopes[i] = current;
            held.Clear();
        }
        foreach (var e in s.Events)
        {
            switch (e.Id)
            {
                case EvChannelNew: Release(); current = $"channel {e.Int}"; insert = -1; break;
                case EvPatternNew: Release(); current = $"pattern {e.Int}"; insert = -1; break;
                case EvPlaylist: Release(); current = "arrangement"; insert = -1; break;
                case EvInsertFlags:
                    Release();
                    current = $"insert {++insert}";
                    if (e.Index > 0 && s.Events[e.Index - 1].Id == EvInsertName) scopes[e.Index - 1] = current;
                    scopes[e.Index] = current;
                    continue;
            }
            if (insert < 0)
            {
                scopes[e.Index] = current;
                continue;
            }
            held.Add(e.Index);
            if (e.Id == EvMixerSlot)
            {
                foreach (var i in held) scopes[i] = $"insert {insert}, slot {e.Int}";
                held.Clear();
            }
        }
        Release();
        return scopes;
    }

    /// <summary>Text FL didn't say is text: UTF-16 with a terminator and nothing unprintable.</summary>
    static bool LooksLikeText(ReadOnlySpan<byte> b) =>
        b.Length >= 4 && b.Length % 2 == 0 && b[^1] == 0 && b[^2] == 0 && LabText.AsText(b) is { Length: > 0 };

    /// <summary>The numbers the registry's layout names, and the data's records with theirs.</summary>
    static void Layout(FormatEntry entry, byte[] data, int major, string key, string path, List<LabItem> parts)
    {
        foreach (var f in LayoutField.Parse(entry.Layout))
            if (f.Read(data) is { } v)
                parts.Add(new LabItem($"{key}/{f.Name}", $"{path} › {f.Name}", 2, f.Name, Text(v), v, Start: f.Offset, Length: f.Size));
        if (entry.RecordSize(major) is not { } size || size <= 0 || entry.RecordLayout is null) return;
        var fields = LayoutField.Parse(entry.RecordLayout);
        var at = entry.RecordAt ?? 0;
        for (int r = 0, off = at; off + size <= data.Length; r++, off += size)
        {
            var record = data.AsSpan(off, size).ToArray();
            parts.Add(new LabItem($"{key}/[{r}]", $"{path}[{r}]", 2, $"[{r}]", LabText.Bytes(record), record, true, off, size));
            foreach (var f in fields)
                if (f.Read(record) is { } v)
                    parts.Add(new LabItem($"{key}/[{r}]/{f.Name}", $"{path}[{r}] › {f.Name}", 3, f.Name, Text(v), v, Start: f.Offset, Length: f.Size));
        }
    }

    static string Text(object v) => v is double d ? LabText.Number(d) : LabText.Number((long)v);

    /// <summary>A Fruity Wrapper's records, and a VST3 plugin's own records inside its state.</summary>
    static void Wrapper(byte[] data, string key, string path, List<LabItem> parts)
    {
        if (FlStudioReader.Records(data) is not { } records) return;
        var vst3 = records.Any(r => r.Id == WrapperFormat && r.Length > 0 && data[r.Offset] == FormatVst3);
        foreach (var r in records)
        {
            var entry = Registry.WrapperRecord(r.Id);
            var label = entry is null ? $"wrapper {r.Id}" : $"wrapper {r.Id} {entry.Name}";
            var k = $"{key}/wrapper {r.Id}";
            var p = $"{path} › {label}";
            var bytes = data.AsSpan(r.Offset, r.Length).ToArray();
            parts.Add(Part(entry, k, p, 2, label, bytes) with { Start = r.Offset, Length = r.Length });
            if (!vst3 || r.Id != WrapperState || FlStudioReader.Records(bytes) is not { } inner) continue;
            foreach (var x in inner)
            {
                var e = Registry.Vst3Record(x.Id);
                var l = e is null ? $"vst3 {x.Id}" : $"vst3 {x.Id} {e.Name}";
                parts.Add(Part(e, $"{k}/vst3 {x.Id}", $"{p} › {l}", 3, l, bytes.AsSpan(x.Offset, x.Length).ToArray())
                          with { Start = x.Offset, Length = x.Length });
            }
        }
    }

    static LabItem Part(FormatEntry? entry, string key, string path, int depth, string label, byte[] bytes)
    {
        switch (entry?.Type)
        {
            case "utf8":
                var s = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                return new LabItem(key, path, depth, label, LabText.Quote(s), s);
            case "u32" when bytes.Length == 4:
                return new LabItem(key, path, depth, label, LabText.Number(BinaryPrimitives.ReadUInt32LittleEndian(bytes)),
                                   (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes));
            case "i32" when bytes.Length == 4:
                return new LabItem(key, path, depth, label, LabText.Number(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
                                   (long)BinaryPrimitives.ReadInt32LittleEndian(bytes));
            case "fourcc" when bytes.Length == 4:
                // A VST2 ID: a number that's also four letters, read most significant byte first ("XfsX" is Serum).
                var id = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                var letters = Encoding.Latin1.GetString([bytes[3], bytes[2], bytes[1], bytes[0]]);
                return new LabItem(key, path, depth, label,
                                   LabText.Number(id) + (letters.All(c => c is >= ' ' and <= '~') ? $" ({LabText.Quote(letters)})" : ""), (long)id);
            default:
                return new LabItem(key, path, depth, label, LabText.Bytes(bytes), bytes);
        }
    }
}
