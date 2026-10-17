using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Export;

/// <summary>
/// Makes Bitwig's own preset files (.bwpreset) from plugin states exported elsewhere.
///
/// A .bwpreset is a BtWg document like a project: the device and which plugin it loads are
/// in the body, and the plugin's settings sit in the embedded zip as a standard .fxp/.fxb
/// (VST2) or .vstpreset (VST3) file named by the device's state field. So a preset that
/// Bitwig itself saved for a plugin is used as a template: the body is kept, the state file
/// in the zip is swapped for the new one, and the preset name is changed.
/// </summary>
public static class BitwigPreset
{
    const int FDeviceName = 0x9a, FDevicePreset = 0x12de, FPluginState = 0xbe5;

    /// <summary>A preset Bitwig saved for <see cref="Plugin"/>, usable as a template.</summary>
    public sealed record Template(string Path, string Plugin, string StateEntry, string PresetName = "")
    {
        public string Format => StateFormat(System.IO.Path.GetExtension(StateEntry));
    }

    /// <summary>"VST2" for .fxp/.fxb, "VST3" for .vstpreset, otherwise "".</summary>
    public static string StateFormat(string extension) => extension.ToLowerInvariant() switch
    {
        ".fxp" or ".fxb" => "VST2",
        ".vstpreset" => "VST3",
        _ => "",
    };

    /// <summary>Reads a .bwpreset and says whether it can serve as a template (one plugin, state in the zip).</summary>
    public static Template? Inspect(string path)
    {
        try
        {
            var raw = File.ReadAllBytes(path);
            if (!BitwigFormat.IsBitwig(raw)) return null;
            var doc = BitwigFormat.Decode(raw);
            if (doc.BodyError is not null) return null;
            var devices = doc.Objects.Where(o => o.Str(FPluginState).Length > 0 && o.Str(FDeviceName).Length > 0).ToList();
            if (devices.Count != 1) return null;   // a chain of several plugins isn't a template for one
            var entry = devices[0].Str(FPluginState);
            using var zip = doc.OpenZip();
            if (zip?.GetEntry("plugin-states/" + entry) is null || StateFormat(Path.GetExtension(entry)).Length == 0) return null;
            return new Template(path, devices[0].Str(FDeviceName), entry, devices[0].Str(FDevicePreset));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    /// <summary>Where Bitwig keeps the presets you save: Documents\Bitwig Studio.</summary>
    public static IReadOnlyList<string> DefaultFolders() =>
        [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Bitwig Studio")];

    /// <summary>Every usable template under these folders, best one per plugin and format (newest file wins).</summary>
    public static IReadOnlyDictionary<string, Template> FindTemplates(IEnumerable<string> folders, CancellationToken ct = default)
    {
        var files = Parallelism.FindFiles(folders, f => f.EndsWith(".bwpreset", StringComparison.OrdinalIgnoreCase),
            (name, _) => name.StartsWith('.'), ct: ct);
        var found = new ConcurrentBag<Template>();
        Parallel.ForEach(files, Parallelism.Options(ct), f =>
        {
            if (Parallelism.Background(() => Inspect(f)) is { } t) found.Add(t);
        });
        return found.GroupBy(t => Key(t.Plugin, t.Format))
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(t => File.GetLastWriteTimeUtc(t.Path)).First());
    }

    /// <summary>The lookup key for a plugin and format, so "Serum_x64" and "Serum" match.</summary>
    public static string Key(string plugin, string format) => $"{LibraryDb.NameKey(plugin)}|{format}";

    /// <summary>The template for this exported preset, if one was found.</summary>
    public static Template? For(PresetBlob preset, IReadOnlyDictionary<string, Template> templates) =>
        templates.GetValueOrDefault(Key(preset.Plugin, StateFormat(preset.Extension)));

    /// <summary>
    /// Builds a .bwpreset from <paramref name="template"/> with <paramref name="state"/> as the plugin's
    /// settings. <paramref name="stateExtension"/> is the state file's type (.fxp, .fxb or .vstpreset).
    /// <paramref name="oldNames"/> are the template's own preset names (file name, device preset name),
    /// replaced by <paramref name="presetName"/> where each appears exactly once; <paramref name="plugin"/>
    /// is never renamed, even when a preset shares its name.
    /// </summary>
    public static byte[] Build(byte[] template, string stateEntry, byte[] state, string stateExtension, string presetName,
                               IEnumerable<string> oldNames, string plugin)
    {
        var hex = Encoding.ASCII.GetString(template, 4, 38);
        var bodyOffset = Convert.ToInt32(hex[12..20], 16);
        var zipOffset = (int)Convert.ToInt64(hex[20..36], 16);
        if (bodyOffset <= 42 || zipOffset <= bodyOffset || zipOffset > template.Length)
            throw new InvalidDataException("The template preset's header doesn't add up");

        // Rename the state file when its type changes (say .fxb in the template, .fxp here).
        var newEntry = Path.GetFileNameWithoutExtension(stateEntry) + stateExtension;
        var meta = template[42..bodyOffset];
        var body = template[bodyOffset..zipOffset];
        if (!newEntry.Equals(stateEntry, StringComparison.Ordinal))
            body = ReplaceString(body, stateEntry, newEntry, required: true);
        // The preset's name, wherever the template stored its own (metadata and the device).
        foreach (var old in oldNames.Distinct(StringComparer.Ordinal))
        {
            if (old.Length == 0 || presetName.Length == 0 || old.Equals(plugin, StringComparison.OrdinalIgnoreCase)) continue;
            meta = ReplaceString(meta, old, presetName, required: false);
            body = ReplaceString(body, old, presetName, required: false);
        }

        var zip = SwapZipEntry(template.AsMemory(zipOffset), stateEntry, newEntry, state);

        var newBody = 42 + meta.Length;
        var newZip = newBody + body.Length;
        using var ms = new MemoryStream();
        ms.Write(template, 0, 4);
        ms.Write(Encoding.ASCII.GetBytes(hex[..12] + newBody.ToString("x8") + ((long)newZip).ToString("x16") + hex[36..]));
        ms.Write(meta);
        ms.Write(body);
        ms.Write(zip);
        return ms.ToArray();
    }

    /// <summary>
    /// Builds the preset and checks it reads back with the new state, as Bitwig would read it. A VST2
    /// program (.fxp) goes in as a one-program bank (.fxb), the form Bitwig saves and restores; it
    /// loaded a plugin's defaults from a .fxp in DAWproject files.
    /// </summary>
    public static byte[] Build(Template template, PresetBlob preset, string presetName)
    {
        var (state, extension) = (preset.Data, preset.Extension);
        if (extension == ".fxp" && Fxp.ToBank(state, preset.Program) is var bank && !ReferenceEquals(bank, state)) (state, extension) = (bank, ".fxb");
        var bytes = Build(File.ReadAllBytes(template.Path), template.StateEntry, state, extension, presetName,
                          [Path.GetFileNameWithoutExtension(template.Path), template.PresetName], template.Plugin);
        var doc = BitwigFormat.Decode(bytes);
        if (doc.BodyError is not null) throw new InvalidDataException($"The new preset doesn't read back: {doc.BodyError}");
        using var zip = doc.OpenZip();
        var entry = zip?.GetEntry("plugin-states/" + Path.GetFileNameWithoutExtension(template.StateEntry) + extension);
        if (entry is null || entry.Length != state.Length) throw new InvalidDataException("The new preset lost its plugin settings");
        return bytes;
    }

    /// <summary>
    /// Replaces a length-prefixed string (as BtWg stores it, UTF-8 or UTF-16) with another.
    /// The format has no absolute positions inside a section, so lengths may change.
    /// </summary>
    static byte[] ReplaceString(byte[] section, string from, string to, bool required)
    {
        foreach (var (find, repl) in new[] { (Utf8(from), Utf8(to)), (Utf16(from), Utf16(to)) })
        {
            var at = IndexOf(section, find);
            if (at < 0) continue;
            if (IndexOf(section, find, at + 1) >= 0)
            {
                // Ambiguous: leave a name alone rather than risk changing the wrong field.
                if (required) throw new InvalidDataException($"\"{from}\" appears more than once in the template");
                return section;
            }
            return [.. section[..at], .. repl, .. section[(at + find.Length)..]];
        }
        if (required) throw new InvalidDataException($"Couldn't find \"{from}\" in the template");
        return section;
    }

    static byte[] Utf8(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        var r = new byte[4 + b.Length];
        BinaryPrimitives.WriteUInt32BigEndian(r, (uint)b.Length);
        b.CopyTo(r, 4);
        return r;
    }

    static byte[] Utf16(string s)
    {
        var b = Encoding.BigEndianUnicode.GetBytes(s);
        var r = new byte[4 + b.Length];
        BinaryPrimitives.WriteUInt32BigEndian(r, 0x8000_0000 | (uint)s.Length);
        b.CopyTo(r, 4);
        return r;
    }

    static int IndexOf(byte[] hay, byte[] needle, int start = 0) =>
        hay.AsSpan(start).IndexOf(needle) is var i and >= 0 ? start + i : -1;

    static byte[] SwapZipEntry(ReadOnlyMemory<byte> zipBytes, string oldEntry, string newEntry, byte[] state)
    {
        using var input = new ZipArchive(new MemoryStream(zipBytes.ToArray()), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var e in input.Entries)
            {
                var isState = e.FullName == "plugin-states/" + oldEntry;
                var copy = zip.CreateEntry(isState ? "plugin-states/" + newEntry : e.FullName, CompressionLevel.Optimal);
                copy.LastWriteTime = isState ? DateTimeOffset.Now : e.LastWriteTime;
                using var to = copy.Open();
                if (isState) to.Write(state);
                else
                {
                    using var from = e.Open();
                    from.CopyTo(to);
                }
            }
        }
        return output.ToArray();
    }
}
