using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using static MusicOrganiser.Core.Parsers.BitwigFormat;

namespace MusicOrganiser.Core.Parsers;

/// <summary>
/// Bitwig Studio .bwproject reader.
///
/// Projects are decoded with <see cref="BitwigFormat"/>: devices, plugins, tracks, tempo,
/// note clips and the plugin states stored in the embedded zip. The field and class ids
/// below were mapped from projects saved by Bitwig 5.1-6.1. If a file can't be decoded
/// (a newer format, or damage), the reader falls back to scanning the readable text,
/// as earlier versions of this app did.
///
/// A Bitwig project folder usually looks like
///   My Song/My Song.bwproject, samples/, recordings/, bounce/
/// so audio in those folders is attributed to the project too.
/// </summary>
public sealed partial class BitwigReader : IProjectReader
{
    public string Daw => "Bitwig Studio";
    public IReadOnlyList<string> Extensions { get; } = [".bwproject"];

    // Classes.
    const int ClsBitwigDevice = 0x40, ClsVst3Plugin = 0x728, ClsVst2Plugin = 0x1a5;
    const int ClsTempo = 0x4c1, ClsClip = 0x47, ClsKeyLane = 0x42, ClsNote = 0x66;
    const int ClsFileRef = 0x2a, ClsHardwareIo = 0x461;
    // Fields.
    const int FDeviceName = 0x9a, FDeviceVendor = 0x9b, FDeviceCategory = 0x9c, FDevicePreset = 0x12de;
    const int FPluginState = 0xbe5;
    const int FTempo = 0x2c8;
    const int FTrackName = 0x15b;
    const int FStart = 0x2af, FDuration = 0x26, FVelocity = 0xef, FPitch = 0xee, FNotes = 0x21f;
    const int FFilePath = 0x3b, FHardwareName = 0x11a7;

    const int Ppq = 960;

    static readonly HashSet<string> Instruments = new(StringComparer.Ordinal)
    {
        "Polymer", "Phase-4", "Polysynth", "FM-4", "Sampler", "E-Kick", "E-Snare", "E-Hat", "E-Clap", "E-Cowbell",
        "E-Tom", "Drum Machine", "Organ", "Poly Grid", "Wavetable", "Instrument Layer", "Instrument Selector",
        "HW Instrument",
    };

    static readonly HashSet<string> Effects = new(StringComparer.Ordinal)
    {
        "EQ+", "EQ-2", "EQ-5", "EQ-DJ", "Filter+", "Delay+", "Delay-1", "Delay-2", "Delay-4", "Reverb", "Convolution",
        "Compressor", "Compressor+", "Dynamics", "Transient Control", "Peak Limiter", "Chorus", "Chorus+", "Flanger",
        "Flanger+", "Phaser", "Phaser+", "Distortion", "Saturator", "Bit-8", "Amp", "Treemonster", "Tool",
        "Spectral Split", "Multiband FX-2", "Multiband FX-3", "FX Grid", "FX Layer", "FX Selector", "Freq Shifter",
        "Pitch Shifter", "Resonator Bank", "Ladder", "Sweep", "Fuzz", "Over", "Heat", "Rotary", "Mid-Side Split",
        "Gate", "Expander", "De-Esser", "Ring-Mod", "Comb", "Tape", "Pitch Echo", "Frequency Shifter+",
    };

    static readonly HashSet<string> NoteEffects = new(StringComparer.Ordinal)
    {
        "Arpeggiator", "Note Echo", "Note Latch", "Multi-Note", "Note Length", "Quantize", "Note Harmonizer",
        "Ricochet", "Key Filter", "Note Filter", "Velocity Curve", "Transposition Map", "Micro-pitch", "Note Grid",
    };

    static readonly HashSet<string> InstrumentCategories = new(StringComparer.OrdinalIgnoreCase)
        { "Synth", "Instrument", "Drum Kit", "Drum", "Drums", "Sampler", "Organ", "Piano", "Hardware" };

    /// <summary>
    /// Names of plugins installed on this PC (see <see cref="PluginCatalog"/>). Only used by the
    /// text-scan fallback, which can't tell a plugin name from any other text otherwise.
    /// </summary>
    public IReadOnlySet<string> KnownPlugins { get; set; } = PluginCatalog.AlwaysKnown;

    static Document? TryDecode(string path, out byte[] raw)
    {
        raw = File.ReadAllBytes(path);
        return IsBitwig(raw) ? BitwigFormat.Decode(raw) : null;
    }

    public ProjectInfo Read(string path)
    {
        var doc = TryDecode(path, out var raw);
        var info = new ProjectInfo { Daw = Daw };

        if (doc?.Root is not null && doc.BodyError is null)
            ReadDecoded(doc, path, info);
        else
            ReadByTextScan(raw, Path.GetDirectoryName(path) ?? ".", info, doc?.BodyError,
                           doc?.MetaList("used_files").Select(CollectedKey).ToHashSet(StringComparer.OrdinalIgnoreCase));

        if (doc is not null)
        {
            if (doc.MetaString("application_version_name") is { Length: > 0 } v) info.Version = v;
            if (doc.MetaString("title") is { Length: > 0 } t) info.Title = t;
            var dir = Path.GetDirectoryName(path) ?? ".";
            foreach (var f in doc.MetaList("used_files"))
                if (TextScan.IsAudioPath(f) || f.EndsWith(".multisample", StringComparison.OrdinalIgnoreCase))
                    info.Samples.Add(Path.GetFullPath(Path.Combine(dir, f)));
        }

        AddProjectFolderAudio(path, info);
        return info.Dedupe();
    }

    void ReadDecoded(Document doc, string path, ProjectInfo info)
    {
        info.Confidence = Confidence.Partial;

        var pluginPaths = doc.Objects.Where(o => o.Class == ClsFileRef)
                             .Select(o => o.Str(FFilePath))
                             .Where(TextScan.IsPluginPath)
                             .ToList();

        foreach (var o in doc.Objects)
        {
            if (o.Class == ClsTempo && info.Tempo is null && o.Num(FTempo) is { } bpm and >= 10 and <= 999)
                info.Tempo = bpm;

            if (!o.Has(FDeviceName) || !o.Has(FDeviceVendor)) continue;
            var name = o.Str(FDeviceName);
            var vendor = o.Str(FDeviceVendor);
            if (name.Length == 0) continue;

            if (o.Class == ClsBitwigDevice)
            {
                info.Plugins.Add(new PluginRef(name, "Bitwig", BitwigKind(name, o.Str(FDeviceCategory)), "Native"));
            }
            else if (o.Class is ClsVst3Plugin or ClsVst2Plugin || o.Has(FPluginState))
            {
                var format = o.Class switch { ClsVst3Plugin => "VST3", ClsVst2Plugin => "VST2", _ => "" };
                var file = pluginPaths.FirstOrDefault(p => TextScan.PluginNameFromPath(p).Equals(name, StringComparison.OrdinalIgnoreCase)) ?? "";
                if (format.Length == 0 && file.Length > 0) format = TextScan.PluginFormatFromPath(file);
                var kind = InstrumentCategories.Contains(o.Str(FDeviceCategory)) ? PluginKind.Instrument : PluginKind.Effect;
                info.Plugins.Add(new PluginRef(name, vendor, kind, format, file));
            }
        }

        foreach (var t in Tracks(doc))
            info.Tracks.Add(t);

        var hardware = doc.Objects.Where(o => o.Class == ClsHardwareIo)
                          .Select(o => o.Str(FHardwareName)).Where(s => s.Length > 0)
                          .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        info.Hardware.AddRange(hardware);
        if (hardware.Count > 0) info.Details["Hardware I/O"] = string.Join(", ", hardware);

        // Audio referenced by absolute path. Bitwig also keeps the original location of every
        // file it collected into the project folder; those copies are listed in used_files,
        // so an original is only counted when the project has no copy of that file. Copies
        // can carry a "-2" style suffix ("Snare - c1.wav" is collected as "Snare - c1-2.wav").
        var collected = doc.MetaList("used_files").Select(CollectedKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var o in doc.Objects.Where(o => o.Class == ClsFileRef))
            if (o.Str(FFilePath) is { Length: > 0 } f && TextScan.IsAudioPath(f) && IsAbsolute(f) &&
                !collected.Contains(CollectedKey(f)))
                info.Samples.Add(f);

        var notes = doc.Objects.Count(o => o.Class == ClsNote);
        info.Details["Notes"] = notes.ToString();
        info.CanExtractMidi = notes > 0;

        using var zip = doc.OpenZip();
        var states = zip?.Entries.Count(e => e.FullName.StartsWith("plugin-states/", StringComparison.Ordinal)) ?? 0;
        if (states > 0) info.Details["Stored plugin states"] = states.ToString();
        info.CanExtractPresets = states > 0;
    }

    static string CollectedKey(string path)
    {
        var name = path.Split('\\', '/')[^1];
        var ext = Path.GetExtension(name);
        return CopySuffix().Replace(name[..^ext.Length], "") + ext;
    }

    [GeneratedRegex(@"(-\d+)+$")]
    private static partial Regex CopySuffix();

    /// <summary>Rooted on this OS, or a Windows drive or network path (the files come from Windows).</summary>
    static bool IsAbsolute(string p) =>
        Path.IsPathRooted(p) || p.StartsWith(@"\\", StringComparison.Ordinal) ||
        (p.Length > 2 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] is '\\' or '/');

    static PluginKind BitwigKind(string name, string category)
    {
        if (Instruments.Contains(name) || InstrumentCategories.Contains(category)) return PluginKind.Instrument;
        if (NoteEffects.Contains(name) || category.Equals("Note FX", StringComparison.OrdinalIgnoreCase)) return PluginKind.NoteEffect;
        return PluginKind.Effect;
    }

    // Track tree: the root's project group holds tracks (and nested groups) in one list,
    // effect tracks in another and the master track in a third field. The "structure"
    // metadata mirrors it with the names Bitwig displays.
    const int FProjectGroup = 0x4dd, FGroupTracks = 0x4de, FGroupEffectTracks = 0x4df, FMasterTrack = 0x4e0;
    const int FStructRoot = 0xad2, FStructTracks = 0xace, FStructEffectTracks = 0xacf, FStructName = 0xad1;

    sealed record TrackTree<T>(List<T> Tracks, List<T> EffectTracks);

    static TrackTree<BwObject> BodyTracks(Document doc, out BwObject? master)
    {
        var tracks = new List<BwObject>();
        void Walk(BwObject group)
        {
            foreach (var t in group.List(FGroupTracks))
            {
                tracks.Add(t);
                Walk(t);
            }
        }
        master = null;
        if (doc.Root?.Obj(FProjectGroup) is not { } project) return new([], []);
        Walk(project);
        master = project.Obj(FMasterTrack);
        return new(tracks, project.List(FGroupEffectTracks).ToList());
    }

    static TrackTree<string>? StructureNames(Document doc)
    {
        if (doc.Meta.GetValueOrDefault("structure") is not BwBlob blob) return null;
        try
        {
            var s = BitwigFormat.Decode(doc.Raw.AsSpan(blob.Offset, blob.Length).ToArray());
            if (s.Root?.Obj(FStructRoot) is not { } project || s.BodyError is not null) return null;
            var names = new List<string>();
            void Walk(BwObject group)
            {
                foreach (var t in group.List(FStructTracks))
                {
                    names.Add(t.Str(FStructName));
                    Walk(t);
                }
            }
            Walk(project);
            return new(names, project.List(FStructEffectTracks).Select(t => t.Str(FStructName)).ToList());
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>
    /// Display names for tracks, groups, effect tracks and the master. Uses Bitwig's own names
    /// when the structure metadata lines up; otherwise the track's name, else (as Bitwig does)
    /// its first device's preset or the device itself.
    /// </summary>
    static Dictionary<BwObject, string> TrackNames(Document doc) => TrackNames(doc, out _);

    static Dictionary<BwObject, string> TrackNames(Document doc, out BwObject? master)
    {
        var body = BodyTracks(doc, out master);
        var shown = StructureNames(doc);
        var all = body.Tracks.Concat(body.EffectTracks).Append(master).OfType<BwObject>()
                      .ToHashSet(ReferenceEqualityComparer.Instance);

        var deviceNames = new Dictionary<BwObject, string>(ReferenceEqualityComparer.Instance);
        foreach (var o in doc.Objects)
        {
            if (!o.Has(FDeviceName) || o.Class is not (ClsBitwigDevice or ClsVst3Plugin or ClsVst2Plugin)) continue;
            if (o.Ancestor(all.Contains) is not { } track || deviceNames.ContainsKey(track)) continue;
            var name = o.Str(FDevicePreset) is { Length: > 0 } preset ? preset : o.Str(FDeviceName);
            if (name.Length > 0) deviceNames[track] = name;
        }

        var names = new Dictionary<BwObject, string>(ReferenceEqualityComparer.Instance);
        void Name(List<BwObject> tracks, List<string>? display, string fallback)
        {
            var useDisplay = display is not null && display.Count == tracks.Count;
            for (var i = 0; i < tracks.Count; i++)
                names[tracks[i]] = useDisplay && display![i].Length > 0 ? display[i]
                                 : tracks[i].Str(FTrackName) is { Length: > 0 } name ? name
                                 : deviceNames.TryGetValue(tracks[i], out var device) ? device
                                 : $"{fallback} {i + 1}";
        }
        Name(body.Tracks, shown?.Tracks, "Track");
        Name(body.EffectTracks, shown?.EffectTracks, "FX");
        if (master is not null) names[master] = "Master";
        return names;
    }

    /// <summary>Tracks, groups and effect tracks as Bitwig lists them (the master isn't listed).</summary>
    static IEnumerable<string> Tracks(Document doc)
    {
        var names = TrackNames(doc, out var master);
        return names.Where(kv => !ReferenceEquals(kv.Key, master)).Select(kv => kv.Value);
    }

    static string TrackName(BwObject o, Dictionary<BwObject, string> names) =>
        o.Ancestor(names.ContainsKey) is { } track ? names[track] : "Project";

    /// <summary>One MIDI clip per Bitwig note clip, named after its track.</summary>
    public IReadOnlyList<MidiClip> ExtractMidi(string path)
    {
        var doc = TryDecode(path, out _);
        if (doc?.Root is null || doc.BodyError is not null) return [];

        var names = TrackNames(doc);
        var clips = new Dictionary<BwObject, (string Track, List<NoteEvent> Notes)>(ReferenceEqualityComparer.Instance);
        foreach (var lane in doc.Objects.Where(o => o.Class == ClsKeyLane))
        {
            if (lane.Num(FPitch) is not { } pitch) continue;
            var clip = lane.Ancestor(a => a.Class == ClsClip) ?? lane.Parent;
            if (clip is null) continue;
            if (!clips.TryGetValue(clip, out var entry))
                clips[clip] = entry = (TrackName(lane, names), new List<NoteEvent>());
            foreach (var note in lane.List(FNotes).Where(n => n.Class == ClsNote))
            {
                var start = note.Num(FStart) ?? 0;
                var length = note.Num(FDuration) ?? 0.25;
                var velocity = note.Num(FVelocity) ?? 0.8;
                entry.Notes.Add(new NoteEvent(
                    (long)Math.Round(Math.Max(0, start) * Ppq),
                    Math.Max(1, (long)Math.Round(length * Ppq)),
                    Math.Clamp((int)pitch, 0, 127),
                    Math.Clamp((int)Math.Round(velocity * 127), 1, 127)));
            }
        }

        var result = new List<MidiClip>();
        var perTrack = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, (track, notes)) in clips)
        {
            if (notes.Count == 0) continue;
            var n = perTrack[track] = perTrack.GetValueOrDefault(track) + 1;
            var clip = new MidiClip { Name = $"{track} {n}", Ppq = Ppq };
            clip.Track(track).AddRange(notes.OrderBy(x => x.Start).ThenBy(x => x.Key));
            result.Add(clip);
        }
        return result;
    }

    /// <summary>
    /// Plugin states from the project's embedded zip. Bitwig stores them as standard
    /// .vstpreset (VST3) and .fxb/.fxp (VST2) files, so they load straight into the plugin.
    /// </summary>
    public IReadOnlyList<PresetBlob> ExtractPresets(string path)
    {
        var doc = TryDecode(path, out _);
        using var zip = doc?.OpenZip();
        if (doc is null || zip is null) return [];

        var names = TrackNames(doc);
        var owners = new Dictionary<string, BwObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in doc.Objects)
            if (o.Str(FPluginState) is { Length: > 0 } state)
                owners.TryAdd(state, o);

        var presets = new List<PresetBlob>();
        foreach (var e in zip.Entries)
        {
            if (!e.FullName.StartsWith("plugin-states/", StringComparison.Ordinal) || e.Length == 0) continue;
            using var ms = new MemoryStream();
            using (var s = e.Open()) s.CopyTo(ms);
            var ext = Path.GetExtension(e.Name);
            if (owners.TryGetValue(e.Name, out var owner) || owners.TryGetValue(Path.GetFileNameWithoutExtension(e.Name), out owner))
            {
                var plugin = owner.Str(FDeviceName);
                var preset = owner.Str(FDevicePreset);
                var track = TrackName(owner, names);
                var name = preset.Length > 0 ? $"{track} - {preset}" : $"{track} - {plugin}";
                presets.Add(new PresetBlob(name, plugin, ext, ms.ToArray()));
            }
            else
            {
                presets.Add(new PresetBlob(Path.GetFileNameWithoutExtension(e.Name), "Unknown plugin", ext, ms.ToArray()));
            }
        }
        return presets;
    }

    static void AddProjectFolderAudio(string path, ProjectInfo info)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        foreach (var sub in new[] { "samples", "recordings", "audio" })
        {
            var d = Path.Combine(dir, sub);
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                if (TextScan.IsAudioPath(f)) info.Samples.Add(f);
        }

        var bounce = Path.Combine(dir, "bounce");
        if (Directory.Exists(bounce))
            info.Details["Bounces"] = Directory.EnumerateFiles(bounce, "*", SearchOption.AllDirectories)
                                               .Count(TextScan.IsAudioPath).ToString();
    }

    // ---- Fallback: text scan ----------------------------------------------------------

    void ReadByTextScan(byte[] raw, string dir, ProjectInfo info, string? why, HashSet<string>? collected)
    {
        info.Confidence = Confidence.Heuristic;
        if (!IsBitwig(raw))
            info.Warnings.Add("The file doesn't start with Bitwig's header, so results may be incomplete.");
        info.Warnings.Add("This project couldn't be fully decoded" + (why is null ? "" : $" ({why})") +
                          ", so plugins and samples were found by scanning text inside the file. " +
                          "MIDI and preset extraction aren't available for it.");

        foreach (var blob in Blobs(raw))
        {
            var expectVersion = false;
            foreach (var str in ScanStrings(blob))
            {
                var s = str.Trim('\0', ' ');
                // The version follows its key, either in the same text run or as the next one.
                if (VersionRx().Match(s) is { Success: true } v)
                    info.Version = v.Groups[1].Value;
                else if (expectVersion && VersionOnly().IsMatch(s))
                    info.Version = s;
                expectVersion = s.EndsWith("application_version_name", StringComparison.Ordinal);
                if (TextScan.IsAudioPath(s))
                {
                    // As in a full read, an original that was collected into the project counts once, as its copy.
                    if (ScannedPath(s) is { } p && !(IsAbsolute(p) && collected?.Contains(CollectedKey(p)) == true))
                        info.Samples.Add(IsAbsolute(p) ? p : Path.GetFullPath(Path.Combine(dir, p)));
                }
                else if (TextScan.IsPluginPath(s))
                {
                    var p = ScannedPath(s) ?? s;
                    info.Plugins.Add(new PluginRef(TextScan.PluginNameFromPath(p), "", PluginKind.Unknown,
                                                   TextScan.PluginFormatFromPath(p), p));
                }
                else if (KnownPlugins.Contains(s))
                    info.Plugins.Add(new PluginRef(s, "", PluginCatalog.KindOf(s), ""));
                else if (Instruments.Contains(s))
                    info.Plugins.Add(new PluginRef(s, "Bitwig", PluginKind.Instrument, "Native"));
                else if (Effects.Contains(s))
                    info.Plugins.Add(new PluginRef(s, "Bitwig", PluginKind.Effect, "Native"));
                else if (NoteEffects.Contains(s))
                    info.Plugins.Add(new PluginRef(s, "Bitwig", PluginKind.NoteEffect, "Native"));
            }
        }
    }

    /// <summary>
    /// A path the text scan found, without what was read along with it: Bitwig writes a string's length just before
    /// it, and a length that happens to be a readable character ("D" for 68) joins the text ("DF:\Samples\..."). Null for
    /// a bare file name: those name the sounds inside a multisample or a Grid's sample list, not files on disk.
    /// </summary>
    internal static string? ScannedPath(string s)
    {
        if (DrivePath().Match(s) is { Success: true, Index: > 0 } m)
            s = s[m.Index..];
        else if (s.Length > 2 && s[0] == (char)((s.Length - 1) & 0xff) &&
                 (s[1] == '/' || s.AsSpan(1).StartsWith(@"\\") || ProjectFolders.Any(f => s.AsSpan(1).StartsWith(f + "/", StringComparison.OrdinalIgnoreCase))))
            s = s[1..];
        return s.Contains('/') || s.Contains('\\') ? s : null;
    }

    /// <summary>
    /// The text in a blob. Bitwig writes a string with a character outside ASCII ("Beyoncé") as UTF-16BE, with the top
    /// bit of its length set; those are read whole first and blanked out, so the plain scan doesn't find pieces of them.
    /// </summary>
    static IEnumerable<string> ScanStrings(ReadOnlyMemory<byte> blob)
    {
        var bytes = blob.ToArray();
        var wide = new List<string>();
        for (var i = 0; i + 4 <= bytes.Length; i++)
        {
            if (bytes[i] != 0x80 || bytes[i + 1] != 0) continue;
            var chars = bytes[i + 2] << 8 | bytes[i + 3];
            if (chars < 4 || chars > 4096 || i + 4 + 2 * chars > bytes.Length) continue;
            var s = Encoding.BigEndianUnicode.GetString(bytes, i + 4, 2 * chars);
            if (s.Any(c => char.IsControl(c) || c == '\uFFFD') || s.Count(char.IsAscii) * 2 < s.Length) continue;
            wide.Add(s);
            Array.Clear(bytes, i, 4 + 2 * chars);
            i += 3 + 2 * chars;
        }
        return TextScan.Strings(bytes).Concat(wide);
    }

    /// <summary>The folders Bitwig keeps a project's own files in, next to the .bwproject.</summary>
    static readonly string[] ProjectFolders = ["samples", "recordings", "bounce", "audio", "multi-samples", "wavetables"];

    /// <summary>The raw file plus any zlib streams embedded in it.</summary>
    static IEnumerable<ReadOnlyMemory<byte>> Blobs(byte[] raw)
    {
        yield return raw;
        var attempts = 0;
        for (var i = 0; i + 2 < raw.Length && attempts < 200; i++)
        {
            // zlib header: CMF 0x78, and (CMF*256 + FLG) divisible by 31.
            if (raw[i] != 0x78 || ((0x78 << 8) | raw[i + 1]) % 31 != 0) continue;
            attempts++;
            byte[]? inflated = null;
            try
            {
                using var z = new ZLibStream(new MemoryStream(raw, i, raw.Length - i), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                var buf = new byte[81920];
                int n;
                while ((n = z.Read(buf)) > 0 && ms.Length < 200_000_000) ms.Write(buf, 0, n);
                if (ms.Length >= 8) inflated = ms.ToArray();
            }
            // Random bytes that happen to look like a zlib header fail in several ways.
            catch (Exception e) when (e is InvalidDataException or IOException) { }
            if (inflated is not null) yield return inflated;
        }
    }

    [GeneratedRegex(@"application_version_name\W+(\d+(?:\.\d+)*)")]
    private static partial Regex VersionRx();

    [GeneratedRegex(@"^\d+(?:\.\d+)+$")]
    private static partial Regex VersionOnly();

    [GeneratedRegex(@"[A-Za-z]:[\\/]")]
    private static partial Regex DrivePath();
}
