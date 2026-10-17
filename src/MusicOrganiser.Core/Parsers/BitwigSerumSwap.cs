using System.IO.Compression;
using MusicOrganiser.Core.Plugins.Serum;
using static MusicOrganiser.Core.Parsers.BitwigFormat;

namespace MusicOrganiser.Core.Parsers;

public sealed partial class BitwigReader
{
    /// <summary>Serum 1's VST3 class id and VST2 id ('XfsX'), and Serum 2's VST3 class id.</summary>
    const string Serum1Vst3 = "56535458667358736572756D00000000", Serum2Vst3 = "56534558667350736572756D20320000";
    const int Serum1Vst2 = 0x58667358;
    const int ClsVst3Slot = 0x729, ClsVst2Slot = 0x1a6, ClsPluginTarget = 0x65;

    /// <summary>What <see cref="SwapSerum"/> did: the new file, how many Serums it swapped, and notes for the person.</summary>
    public sealed record SerumSwap(byte[] Data, int Swapped, IReadOnlyList<string> Notes);

    /// <summary>
    /// A copy of a Bitwig project with every Serum 1 (VST2 or VST3) swapped for Serum 2, with its sound translated
    /// (<see cref="Serum1To2"/>). Automation of Serum 1's knobs is left out, since Serum 2's are other knobs. Null
    /// when the project can't be decoded cleanly enough to write back.
    /// </summary>
    /// <param name="serum2Path">Where Serum 2's VST3 is, which Bitwig is pointed at (it finds it by its id too).</param>
    /// <param name="audio">Finds Serum's wavetables, as for <see cref="Serum1To2.Translate"/>.</param>
    public static SerumSwap? SwapSerum(byte[] raw, string serum2Path, Func<string, Serum1To2.AudioInfo?>? audio = null)
    {
        if (!IsBitwig(raw)) return null;
        var doc = Decode(raw);
        if (doc.Root is null || doc.BodyError is not null) return null;

        var states = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var zip = doc.OpenZip())
            foreach (var e in zip?.Entries ?? [])
            {
                using var s = e.Open();
                var ms = new MemoryStream();
                s.CopyTo(ms);
                states[e.FullName] = ms.ToArray();
            }

        var notes = new List<string>();
        var swapped = new HashSet<BwObject>();
        var tracks = TrackNames(doc);
        foreach (var def in doc.Objects.Where(IsSerum1).ToList())
        {
            var track = def.Ancestor(tracks.ContainsKey) is { } t ? tracks[t] : "A track";
            var entry = $"plugin-states/{def.Str(FPluginState)}";
            var chunk = states.TryGetValue(entry, out var data)
                ? def.Class == ClsVst3Plugin ? VstPreset.Component(data) : Fxp.Chunk(data)
                : null;
            if (chunk is null || Serum1State.Read(chunk) is not { } serum1)
            {
                notes.Add($"{track}: Serum's sound couldn't be read, so it's left as Serum.");
                continue;
            }
            var result = Serum1To2.Translate(serum1, audio);
            var name = $"{Guid.NewGuid()}.vstpreset";
            states.Remove(entry);
            states[$"plugin-states/{name}"] = VstPreset.Build(Serum2Vst3, result.State.Write(), []);

            if (def.Class == ClsVst2Plugin)
            {
                // Bitwig keeps a VST2 and a VST3 plugin in objects of their own kinds, otherwise laid out the same.
                def.Class = ClsVst3Plugin;
                if (def.Parent is { Class: ClsVst2Slot } slot) slot.Class = ClsVst3Slot;
                def.Set(0x1b75, null, 0x0a);   // a VST2's program, which a VST3 has none of
            }
            def.Set(FDeviceName, "Serum 2");
            def.Set(0xce4, Serum2Vst3, 0x08);
            def.Set(0xc5e, "", 0x08);          // the version, which Bitwig reads when it loads the plugin
            def.Set(0x1809, (byte)1, 0x05);    // as Bitwig saves Serum 2
            def.Set(0x1318, new List<object?>());
            def.Set(FPluginState, name);
            if (def.Obj(0xc5a)?.List(0xc5b).FirstOrDefault() is { } file) file.Set(FFilePath, serum2Path);
            if (def.Obj(0x17cd) is { } cache && cache[0x17cc] is BwBlob blob)
                cache.Set(0x17cc, PluginCache(doc.Raw.AsSpan(blob.Offset, blob.Length).ToArray(), serum2Path));
            swapped.Add(def.Parent ?? def);
            notes.Add(result.Rough.Count == 0
                ? $"{track}: Serum swapped for Serum 2, with its sound translated."
                : $"{track}: Serum swapped for Serum 2, with its sound translated (only roughly: {string.Join(", ", result.Rough.Distinct())}).");
        }
        if (swapped.Count == 0) return new SerumSwap(raw, 0, notes);

        // Automation lanes of the old Serum's knobs: Serum 2's knobs are others, so they'd move the wrong ones.
        var dropped = 0;
        foreach (var target in doc.Objects.Where(o => o.Class == ClsPluginTarget && o.Obj(0xec) is { } d && swapped.Contains(d)).ToList())
            if (target.Parent is { } lane && lane.Parent?[lane.ParentField] is List<object?> lanes &&
                lanes.RemoveAll(l => lane.Parent.Resolve(l) == lane) > 0)
                dropped++;
        if (dropped > 0) notes.Add($"{dropped} automation lane{(dropped == 1 ? "" : "s")} of Serum's knobs left out: Serum 2's knobs are different ones.");

        var packed = new MemoryStream();
        using (var z = new ZipArchive(packed, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, bytes) in states)
            {
                using var s = z.CreateEntry(path, CompressionLevel.Optimal).Open();
                s.Write(bytes);
            }
        return new SerumSwap(Encode(doc, packed.ToArray()), swapped.Count, notes);
    }

    static bool IsSerum1(BwObject o) => o.Class switch
    {
        ClsVst3Plugin => o[0xce4] is string id && id.Equals(Serum1Vst3, StringComparison.OrdinalIgnoreCase),
        ClsVst2Plugin => o.Num(0xce4) == Serum1Vst2,
        _ => false,
    };

    /// <summary>
    /// The device's copy of Bitwig's browser entry for the plugin (a small document of its own: path, id, name,
    /// version), pointed at Serum 2. Left as it was if it can't be read.
    /// </summary>
    static byte[] PluginCache(byte[] cache, string serum2Path)
    {
        var doc = Decode(cache);
        if (doc.Root is null || doc.BodyError is not null) return cache;
        foreach (var o in doc.Objects)
            foreach (var (id, value) in o.Fields.ToList())
                if (value is string s && Serum2Text(s, serum2Path) is { } to && to != s) o.Set(id, to);
        return Encode(doc);
    }

    static string? Serum2Text(string s, string serum2Path)
    {
        if (s.Equals(Serum1Vst3, StringComparison.OrdinalIgnoreCase)) return Serum2Vst3;
        if (s.Contains(Serum1Vst3, StringComparison.OrdinalIgnoreCase)) return $"{serum2Path}\n{Serum2Vst3}";
        // Serum 1's VST3, or its VST2 (Serum_x64.dll and the like).
        if (s.EndsWith("Serum.vst3", StringComparison.OrdinalIgnoreCase) ||
            s.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(s.Replace('\\', '/')).StartsWith("Serum", StringComparison.OrdinalIgnoreCase))
        {
            if (s.Length > 2 && s[1] == ':' || s.StartsWith('\\') || s.StartsWith('/')) return serum2Path;
            // Inside the VST3 folder, as Bitwig's browser lists it.
            var at = serum2Path.IndexOf(@"\VST3\", StringComparison.OrdinalIgnoreCase);
            return at >= 0 ? serum2Path[(at + 6)..].Replace('\\', '/') : "Serum2.vst3/Contents/x86_64-win/Serum2.vst3";
        }
        if (s == "Serum") return "Serum 2";
        return s.StartsWith("1.3.", StringComparison.Ordinal) ? "" : null;   // Serum 1's version
    }
}
