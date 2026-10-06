using System.Buffers.Binary;
using System.Text;

namespace MusicOrganiser.Core.Parsers;

/// <summary>
/// FL Studio .flp reader.
///
/// The FLP format is undocumented but well reverse-engineered (see the PyFLP
/// project). After the FLhd header comes an FLdt chunk holding a stream of
/// events. The event id decides the payload size:
///   0-63 → 1 byte, 64-127 → 2 bytes, 128-191 → 4 bytes, 192-255 → varint length + bytes.
/// From these we get tempo, version, channels, sample paths, plugin names
/// (native, and VST via the Fruity Wrapper state), plugin states and pattern notes.
/// </summary>
public sealed class FlStudioReader : IProjectReader
{
    public string Daw => "FL Studio";
    public IReadOnlyList<string> Extensions { get; } = [".flp"];

    const int Text = 192, Data = 208;
    const int EvChannelType = 21;
    const int EvChannelNew = 64, EvPatternNew = 65, EvTempoCoarse = 66;
    const int EvMixerSlot = 98;                 // WORD + 34: closes a mixer slot's events
    const int EvTempo = 156;                    // DWORD: tempo × 1000
    const int EvChannelNameOld = Text + 0, EvPatternName = Text + 1, EvTitle = Text + 2;
    const int EvSamplePath = Text + 4, EvVersion = Text + 7, EvPluginInternal = Text + 9;
    const int EvPluginName = Text + 11, EvGenre = Text + 14, EvArtists = Text + 15;
    const int EvPluginState = Data + 5;         // 213: plugin state; VST metadata for the Fruity Wrapper
    const int EvPatternNotes = Data + 16;       // 224: 24-byte note records
    const int EvInsertFlags = Data + 28;        // 236: starts each mixer insert

    // Fruity Wrapper (VST host) state: u32 kind (8 or 10), then records of (u32 id, u64 size, data).
    const int VstState = 53, VstName = 54, VstPath = 55, VstVendor = 56;

    const int ChannelTypeInstrument = 2;

    static readonly HashSet<string> NativeGenerators = new(StringComparer.OrdinalIgnoreCase)
    {
        "3x Osc", "FL Keys", "FPC", "Sytrus", "Harmor", "Harmless", "FLEX", "Fruity DX10", "Transistor Bass",
        "GMS", "Sakura", "PoiZone", "Toxic Biohazard", "Morphine", "Ogun", "Kepler", "Kepler Exo", "Slicex",
        "Fruity Slicer", "DirectWave", "BooBass", "Drumaxx", "Drumpad", "Autogun", "MiniSynth", "Plucked!",
        "BeepMap", "Fruity Granulizer", "Sawer", "SimSynth", "Wasp", "Wasp XT", "Groove Machine Synth",
        "Vital", "Transporter", "PyroSyn", "Fruity DrumSynth Live", "Fruity Envelope Controller",
        "Fruity Keyboard Controller", "Sampler", "Audio Clip", "Layer", "MIDI Out", "Fruit Kick", "Fruity Kick",
    };

    readonly record struct Event(int Id, ReadOnlyMemory<byte> Payload)
    {
        public uint Int => Payload.Length switch
        {
            1 => Payload.Span[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(Payload.Span),
            >= 4 => BinaryPrimitives.ReadUInt32LittleEndian(Payload.Span),
            _ => 0,
        };
    }

    sealed class Flp
    {
        public int Ppq;
        public string Version = "";
        public bool Utf16 = true;
        public readonly List<Event> Events = new();

        public string Str(Event e)
        {
            var s = e.Payload.Span;
            return (Utf16 ? Encoding.Unicode.GetString(s) : Encoding.Latin1.GetString(s)).TrimEnd('\0');
        }
    }

    static Flp Load(string path)
    {
        var raw = File.ReadAllBytes(path);
        if (raw.Length < 22 || Encoding.ASCII.GetString(raw, 0, 4) != "FLhd")
            throw new InvalidDataException("Not an FL Studio project (missing FLhd header)");
        var headerLen = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4));
        var flp = new Flp { Ppq = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(12)) };
        var dt = 8 + headerLen;
        if (dt + 8 > raw.Length || Encoding.ASCII.GetString(raw, dt, 4) != "FLdt")
            throw new InvalidDataException("FL Studio project is missing its FLdt chunk");
        var dataLen = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(dt + 4));
        var pos = dt + 8;
        var end = Math.Min(raw.Length, pos + dataLen);

        while (pos < end)
        {
            int id = raw[pos++];
            int size;
            if (id < 64) size = 1;
            else if (id < 128) size = 2;
            else if (id < 192) size = 4;
            else
            {
                size = 0;
                for (int shift = 0; pos < end; shift += 7)
                {
                    var b = raw[pos++];
                    size |= (b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                }
            }
            if (pos + size > end) break;   // truncated file: keep what we have
            flp.Events.Add(new Event(id, raw.AsMemory(pos, size)));
            pos += size;
        }

        var ver = flp.Events.FirstOrDefault(e => e.Id == EvVersion);
        if (ver.Id == EvVersion)
        {
            flp.Version = Encoding.ASCII.GetString(ver.Payload.Span).TrimEnd('\0');
            var parts = flp.Version.Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor))
                flp.Utf16 = major > 11 || (major == 11 && minor >= 5);
        }
        return flp;
    }

    static Dictionary<int, byte[]> WrapperFields(ReadOnlySpan<byte> blob)
    {
        var fields = new Dictionary<int, byte[]>();
        if (blob.Length < 4) return fields;
        var kind = BinaryPrimitives.ReadUInt32LittleEndian(blob);
        if (kind != 8 && kind != 10) return fields;
        var pos = 4;
        while (pos + 12 <= blob.Length)
        {
            var id = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[pos..]);
            var size = BinaryPrimitives.ReadUInt64LittleEndian(blob[(pos + 4)..]);
            pos += 12;
            if (size > (ulong)(blob.Length - pos)) break;
            fields[id] = blob.Slice(pos, (int)size).ToArray();
            pos += (int)size;
        }
        return fields;
    }

    static string WrapperText(Dictionary<int, byte[]> f, int id) =>
        f.TryGetValue(id, out var b) ? Encoding.UTF8.GetString(b).TrimEnd('\0') : "";

    public ProjectInfo Read(string path)
    {
        var flp = Load(path);
        var info = new ProjectInfo
        {
            Daw = Daw, Version = flp.Version, Confidence = Confidence.Full,
            CanExtractMidi = true, CanExtractPresets = true,
        };
        info.Details["PPQ"] = flp.Ppq.ToString();

        var channelNames = new Dictionary<int, string>();
        var channelTypes = new Dictionary<int, int>();
        int? channel = null;
        var inMixer = false;
        var pendingInternal = "";
        var channelHasPlugin = false;
        int patterns = 0, notes = 0;

        foreach (var e in flp.Events)
        {
            switch (e.Id)
            {
                case EvTempo: info.Tempo = e.Int / 1000.0; break;
                case EvTempoCoarse when info.Tempo is null: info.Tempo = e.Int; break;
                case EvTitle: info.Title = flp.Str(e); break;
                case EvGenre: info.Details["Genre"] = flp.Str(e); break;
                case EvArtists: info.Details["Artists"] = flp.Str(e); break;
                case EvChannelNew: channel = (int)e.Int; inMixer = false; channelHasPlugin = false; break;
                case EvChannelType when channel is not null: channelTypes[channel.Value] = (int)e.Int; break;
                case EvPatternNew: patterns++; break;
                case EvMixerSlot or EvInsertFlags: inMixer = true; break;
                case EvSamplePath: info.Samples.Add(flp.Str(e)); break;
                case EvPatternNotes: notes += e.Payload.Length / 24; break;
                case EvPluginName or EvChannelNameOld when channel is not null && !inMixer:
                    channelNames.TryAdd(channel.Value, flp.Str(e));
                    break;
                case EvPluginInternal:
                    pendingInternal = flp.Str(e);
                    // A channel hosts one generator; a second plugin before the next
                    // channel means we've reached the mixer's effect slots.
                    if (channelHasPlugin) inMixer = true;
                    channelHasPlugin = true;
                    if (pendingInternal.Length > 0 && !pendingInternal.Equals("Fruity Wrapper", StringComparison.OrdinalIgnoreCase))
                    {
                        var isGen = NativeGenerators.Contains(pendingInternal) ||
                                    (!inMixer && channel is not null && channelTypes.GetValueOrDefault(channel.Value) == ChannelTypeInstrument);
                        info.Plugins.Add(new PluginRef(pendingInternal, "Image-Line",
                            isGen ? PluginKind.Instrument : PluginKind.Effect, "Native"));
                    }
                    break;
                case EvPluginState when pendingInternal.Equals("Fruity Wrapper", StringComparison.OrdinalIgnoreCase):
                    var f = WrapperFields(e.Payload.Span);
                    var name = WrapperText(f, VstName);
                    var vpath = WrapperText(f, VstPath);
                    if (name.Length > 0 || vpath.Length > 0)
                    {
                        var isInst = !inMixer && channel is not null && channelTypes.GetValueOrDefault(channel.Value) == ChannelTypeInstrument;
                        info.Plugins.Add(new PluginRef(
                            name.Length > 0 ? name : TextScan.PluginNameFromPath(vpath),
                            WrapperText(f, VstVendor),
                            isInst ? PluginKind.Instrument : PluginKind.Effect,
                            TextScan.PluginFormatFromPath(vpath) is { Length: > 0 } fmt ? fmt : "VST",
                            vpath));
                    }
                    pendingInternal = "";
                    break;
            }
        }

        foreach (var i in channelNames.Keys.Union(channelTypes.Keys).Order())
            info.Tracks.Add(channelNames.GetValueOrDefault(i, $"Channel {i + 1}"));
        info.Details["Channels"] = info.Tracks.Count.ToString();
        info.Details["Patterns"] = patterns.ToString();
        info.Details["Notes"] = notes.ToString();
        return info.Dedupe();
    }

    /// <summary>One clip per pattern, one track per channel with notes in it.</summary>
    public IReadOnlyList<MidiClip> ExtractMidi(string path)
    {
        var flp = Load(path);
        var channelNames = new Dictionary<int, string>();
        var patternNames = new Dictionary<int, string>();
        var patternNotes = new SortedDictionary<int, List<(int Channel, NoteEvent Note)>>();
        int? channel = null, pattern = null;
        var inMixer = false;

        foreach (var e in flp.Events)
        {
            switch (e.Id)
            {
                case EvChannelNew: channel = (int)e.Int; inMixer = false; break;
                case EvMixerSlot or EvInsertFlags: inMixer = true; break;
                case EvPluginName or EvChannelNameOld when channel is not null && !inMixer:
                    channelNames.TryAdd(channel.Value, flp.Str(e));
                    break;
                case EvPatternNew: pattern = (int)e.Int; break;
                case EvPatternName when pattern is not null: patternNames[pattern.Value] = flp.Str(e); break;
                case EvPatternNotes when pattern is not null:
                    if (!patternNotes.TryGetValue(pattern.Value, out var list))
                        patternNotes[pattern.Value] = list = new();
                    var s = e.Payload.Span;
                    for (var off = 0; off + 24 <= s.Length; off += 24)
                    {
                        var r = s.Slice(off, 24);
                        var pos = BinaryPrimitives.ReadUInt32LittleEndian(r);
                        var chan = BinaryPrimitives.ReadUInt16LittleEndian(r[6..]);
                        var len = BinaryPrimitives.ReadUInt32LittleEndian(r[8..]);
                        var key = BinaryPrimitives.ReadUInt16LittleEndian(r[12..]);
                        // FL velocity is 0-128 (100 default); MIDI wants 1-127.
                        var vel = Math.Clamp((int)Math.Round(r[21] * 127 / 128.0), 1, 127);
                        list.Add((chan, new NoteEvent(pos, Math.Max(1, len), key & 0x7F, vel)));
                    }
                    break;
            }
        }

        var clips = new List<MidiClip>();
        foreach (var (pid, notes) in patternNotes)
        {
            var clip = new MidiClip { Name = patternNames.GetValueOrDefault(pid, $"Pattern {pid}"), Ppq = flp.Ppq };
            foreach (var (chan, note) in notes)
                clip.Track(channelNames.GetValueOrDefault(chan, $"Channel {chan + 1}")).Add(note);
            if (clip.Tracks.Count > 0) clips.Add(clip);
        }
        return clips;
    }

    /// <summary>
    /// Plugin states. VST states are wrapped as .fxp chunk presets; native FL
    /// plugin states are saved raw (.flstate) since they aren't complete .fst files.
    /// </summary>
    public IReadOnlyList<PresetBlob> ExtractPresets(string path)
    {
        var flp = Load(path);
        var presets = new List<PresetBlob>();
        var pendingInternal = "";
        var currentName = "";
        foreach (var e in flp.Events)
        {
            switch (e.Id)
            {
                case EvPluginInternal: pendingInternal = flp.Str(e); currentName = ""; break;
                case EvPluginName: currentName = flp.Str(e); break;
                case EvPluginState when e.Payload.Length > 0:
                    if (pendingInternal.Equals("Fruity Wrapper", StringComparison.OrdinalIgnoreCase))
                    {
                        var f = WrapperFields(e.Payload.Span);
                        var plugin = WrapperText(f, VstName);
                        if (f.TryGetValue(VstState, out var state) && state.Length > 0)
                            presets.Add(new PresetBlob(currentName.Length > 0 ? currentName : plugin, plugin, ".fxp",
                                                       Fxp.WrapChunk(state, plugin)));
                    }
                    else if (pendingInternal.Length > 0)
                    {
                        presets.Add(new PresetBlob(currentName.Length > 0 ? currentName : pendingInternal,
                                                   pendingInternal, ".flstate", e.Payload.ToArray()));
                    }
                    pendingInternal = "";
                    break;
            }
        }
        return presets;
    }
}

/// <summary>Builds VST2 .fxp program files around opaque plugin chunks.</summary>
public static class Fxp
{
    /// <summary>
    /// Wraps a VST chunk in an 'FPCh' (opaque chunk) .fxp header. The plugin's
    /// four-character id isn't stored where we can read it reliably, so it is
    /// left as zero; most plugins load it from their own preset menu regardless.
    /// </summary>
    public static byte[] WrapChunk(byte[] chunk, string programName)
    {
        using var ms = new MemoryStream();
        void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }
        var name = new byte[28];
        Encoding.ASCII.GetBytes(programName.Length > 27 ? programName[..27] : programName).CopyTo(name, 0);
        var bodyLen = 4 + 16 + 28 + 4 + chunk.Length;
        ms.Write("CcnK"u8);
        I32(bodyLen);
        ms.Write("FPCh"u8);
        I32(1);      // format version
        I32(0);      // plugin id (unknown)
        I32(1);      // plugin version
        I32(1);      // number of programs
        ms.Write(name);
        I32(chunk.Length);
        ms.Write(chunk);
        return ms.ToArray();
    }
}
