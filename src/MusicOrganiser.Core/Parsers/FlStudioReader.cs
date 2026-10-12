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
    const int EvInsertFlags = Data + 28;        // 236: starts each mixer insert (0 is the master)
    const int EvInsertName = Text + 12;         // 204: an insert's name, after its slots

    // Fruity Wrapper (VST host) state: u32 version (8, 10, 12 in FL 25), then records of (u32 id, u64 size, data).
    const int VstKind = 50, VstFourCc = 51, VstClassId = 52;
    const int VstState = 53, VstName = 54, VstPath = 55, VstVendor = 56;
    const byte KindVst3 = 8;

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
        var version = BinaryPrimitives.ReadUInt32LittleEndian(blob);
        if (version is 0 or > 64) return fields;
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
                case EvSamplePath: info.Samples.Add(ResolveSamplePath(flp.Str(e), path)); break;
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

    /// <summary>
    /// FL writes paths under its user data folder as %FLStudioUserData%\..., which is the
    /// folder that holds the Projects folder. Resolve it from where this project lives
    /// (e.g. I:\FL Studio Data\FL Studio\Projects\Song.flp → I:\FL Studio Data\FL Studio).
    /// </summary>
    internal static string ResolveSamplePath(string sample, string projectPath)
    {
        const string UserData = "%FLStudioUserData%";
        if (!sample.StartsWith(UserData, StringComparison.OrdinalIgnoreCase)) return sample;
        var rest = sample[UserData.Length..].TrimStart('\\', '/');
        var firstDir = rest.Split('\\', '/')[0];
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath)); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (rest.Contains('\\') || rest.Contains('/'))
            {
                if (Directory.Exists(Path.Combine(dir, firstDir)))
                    return Path.Combine(dir, rest.Replace('\\', Path.DirectorySeparatorChar));
            }
            else if (File.Exists(Path.Combine(dir, rest)))
                return Path.Combine(dir, rest);
        }
        return sample;
    }

    /// <summary>
    /// A Fruity Wrapper plugin state as a file the plugin can load: .fxp for VST2
    /// (with the plugin's own id), .vstpreset for VST3, raw bytes if the layout is unfamiliar.
    /// </summary>
    static PresetBlob WrapperPreset(Dictionary<int, byte[]> f, byte[] state, string name, string plugin)
    {
        var isVst3 = (f.TryGetValue(VstKind, out var k) && k.Length > 0 && k[0] == KindVst3) ||
                     WrapperText(f, VstPath).EndsWith(".vst3", StringComparison.OrdinalIgnoreCase);
        if (isVst3)
        {
            if (f.TryGetValue(VstClassId, out var cid) && cid.Length == 16 && Vst3State(state) is var (comp, cont))
                return new PresetBlob(name, plugin, ".vstpreset", VstPreset.Build(cid, comp, cont));
            return new PresetBlob(name, plugin, ".vst3state", state);
        }

        // VST2: FL puts a 21-byte header in front of the plugin's state:
        // i32 -9, u8 flags (0x08 = the plugin saves a chunk), i32 -2, u32 chunk length, 8 zero bytes.
        var id = f.TryGetValue(VstFourCc, out var cc) && cc.Length == 4
            ? BinaryPrimitives.ReadInt32LittleEndian(cc)
            : 0;
        var chunk = state;
        if (state.Length >= 21 && BinaryPrimitives.ReadInt32LittleEndian(state) == -9)
        {
            var len = BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(9));
            if (len == 0 && (state[4] & 0x08) == 0 && ParamState(state) is var (values, program))
                return new PresetBlob(name, plugin, ".fxp", Fxp.WrapParams(values, program.Length > 0 ? program : plugin, id));
            if (21 + (long)len <= state.Length) chunk = state.AsSpan(21, (int)len).ToArray();
        }
        return new PresetBlob(name, plugin, ".fxp", Fxp.WrapChunk(chunk, plugin, id));
    }

    /// <summary>
    /// Plugins that don't save a chunk (Predator, older FabFilter) have their parameters after
    /// FL's header instead: u32 count, the values as floats, then u32 1 and the program name.
    /// </summary>
    static (float[] Values, string Program)? ParamState(byte[] state)
    {
        if (state.Length < 25) return null;
        var n = BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(21));
        if (n == 0 || 25 + 4L * n > state.Length) return null;
        var values = new float[n];
        for (var i = 0; i < n; i++) values[i] = BinaryPrimitives.ReadSingleLittleEndian(state.AsSpan(25 + 4 * i));
        var program = "";
        var at = 25 + 4 * (int)n;
        if (at + 4 < state.Length && BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(at)) == 1)
        {
            var text = state.AsSpan(at + 4, Math.Min(28, state.Length - at - 4));
            var end = text.IndexOf((byte)0);
            program = Encoding.Latin1.GetString(end < 0 ? text : text[..end]).Trim();
        }
        return (values, program);
    }

    /// <summary>
    /// FL's VST3 state: u32 version, then records of (u32 id, u64 size, data):
    /// 3 is the component (processor) state and 2 the controller state.
    /// </summary>
    static (byte[] Component, byte[] Controller)? Vst3State(byte[] state)
    {
        byte[]? comp = null, cont = null;
        var pos = 4;
        while (pos + 12 <= state.Length)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(pos));
            var size = BinaryPrimitives.ReadUInt64LittleEndian(state.AsSpan(pos + 4));
            pos += 12;
            if (size > (ulong)(state.Length - pos)) return null;
            if (id == 3) comp = state.AsSpan(pos, (int)size).ToArray();
            else if (id == 2) cont = state.AsSpan(pos, (int)size).ToArray();
            pos += (int)size;
        }
        return comp is null ? null : (comp, cont ?? []);
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
                    list.AddRange(DecodeNotes(e.Payload.Span, null));
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
    /// Pattern note records: u32 position, u16 flags, u16 channel, u32 length, u16 key, ...
    /// FL 7 onwards uses 24 bytes with velocity at 21; older versions 20 bytes with velocity at 17.
    /// When the channels are known, the record size whose channel numbers all exist wins.
    /// </summary>
    static List<(int Channel, NoteEvent Note)> DecodeNotes(ReadOnlySpan<byte> s, IReadOnlySet<int>? channels)
    {
        var size = 24;
        if (s.Length % 24 != 0 || channels is not null && !AllChannelsKnown(s, 24, channels))
            if (s.Length % 20 == 0 && (channels is null || AllChannelsKnown(s, 20, channels))) size = 20;
        var velocityAt = size - 3;
        var notes = new List<(int, NoteEvent)>(s.Length / size);
        for (var off = 0; off + size <= s.Length; off += size)
        {
            var r = s.Slice(off, size);
            // FL velocity is 0-128 (100 default); MIDI wants 1-127.
            var vel = Math.Clamp((int)Math.Round(r[velocityAt] * 127 / 128.0), 1, 127);
            notes.Add((BinaryPrimitives.ReadUInt16LittleEndian(r[6..]), new NoteEvent(
                BinaryPrimitives.ReadUInt32LittleEndian(r),
                Math.Max(1, BinaryPrimitives.ReadUInt32LittleEndian(r[8..])),
                BinaryPrimitives.ReadUInt16LittleEndian(r[12..]) & 0x7F, vel)));
        }
        return notes;
    }

    static bool AllChannelsKnown(ReadOnlySpan<byte> s, int size, IReadOnlySet<int> channels)
    {
        for (var off = 0; off + size <= s.Length; off += size)
            if (!channels.Contains(BinaryPrimitives.ReadUInt16LittleEndian(s[(off + 6)..]))) return false;
        return true;
    }

    const int EvPlaylist = Data + 25;           // 233: playlist items (arrangement)
    const int PatternBase = 20480;              // playlist item index above this = pattern (index - base)

    /// <summary>
    /// Each channel's notes across the song. Patterns are placed where the playlist puts them
    /// (the first arrangement); a song with nothing in the playlist gets its patterns one after
    /// another, in pattern order.
    /// </summary>
    public IReadOnlyList<MidiChannel> ExtractChannels(string path)
    {
        var flp = Load(path);
        var names = new Dictionary<int, string>();
        var sources = new Dictionary<int, string>();
        var plugins = new Dictionary<int, string>();
        var order = new List<int>();
        var rawNotes = new List<(int Pattern, ReadOnlyMemory<byte> Data)>();
        ReadOnlyMemory<byte>? playlist = null;
        int? channel = null, pattern = null;
        var inMixer = false;
        var channelHasPlugin = false;
        var pendingInternal = "";

        foreach (var e in flp.Events)
        {
            switch (e.Id)
            {
                case EvChannelNew:
                    channel = (int)e.Int; inMixer = false; channelHasPlugin = false;
                    if (!order.Contains(channel.Value)) order.Add(channel.Value);
                    break;
                case EvMixerSlot or EvInsertFlags: inMixer = true; break;
                case EvPluginName or EvChannelNameOld when channel is not null && !inMixer:
                    names.TryAdd(channel.Value, flp.Str(e));
                    break;
                case EvPluginInternal:
                    pendingInternal = flp.Str(e);
                    if (channelHasPlugin) inMixer = true;
                    channelHasPlugin = true;
                    if (!inMixer && channel is not null && pendingInternal.Length > 0 &&
                        !pendingInternal.Equals("Fruity Wrapper", StringComparison.OrdinalIgnoreCase))
                    {
                        sources.TryAdd(channel.Value, pendingInternal);
                        plugins.TryAdd(channel.Value, pendingInternal);
                    }
                    break;
                case EvPluginState when !inMixer && channel is not null &&
                                        pendingInternal.Equals("Fruity Wrapper", StringComparison.OrdinalIgnoreCase):
                    var f = WrapperFields(e.Payload.Span);
                    var vst = WrapperText(f, VstName) is { Length: > 0 } n ? n : TextScan.PluginNameFromPath(WrapperText(f, VstPath));
                    if (vst.Length > 0)
                    {
                        sources.TryAdd(channel.Value, vst);
                        plugins.TryAdd(channel.Value, vst);
                    }
                    pendingInternal = "";
                    break;
                case EvSamplePath when channel is not null && !inMixer:
                    var sample = flp.Str(e);
                    if (sample.Length > 0) sources[channel.Value] = sample.Split('\\', '/')[^1];
                    break;
                case EvPatternNew: pattern = (int)e.Int; break;
                case EvPatternNotes when pattern is not null: rawNotes.Add((pattern.Value, e.Payload)); break;
                case EvPlaylist when playlist is null: playlist = e.Payload; break;
            }
        }

        var patterns = new SortedDictionary<int, List<(int Channel, NoteEvent Note)>>();
        var known = order.ToHashSet();
        foreach (var (pat, data) in rawNotes)
        {
            if (!patterns.TryGetValue(pat, out var list)) patterns[pat] = list = new();
            list.AddRange(DecodeNotes(data.Span, known));
        }

        var notes = new Dictionary<int, List<NoteEvent>>();
        void Place(int pat, long at, long from, long length)
        {
            if (!patterns.TryGetValue(pat, out var pn)) return;
            foreach (var (ch, n) in pn)
            {
                if (n.Start < from || n.Start >= from + length) continue;
                if (!notes.TryGetValue(ch, out var cn)) notes[ch] = cn = new();
                cn.Add(n with { Start = at + n.Start - from, Length = Math.Min(n.Length, from + length - n.Start) });
            }
        }

        var items = playlist is { } pl ? PatternItems(pl.Span) : [];
        if (items.Count > 0)
            foreach (var (pat, at, length, from) in items) Place(pat, at, from, length);
        else
        {
            // Pattern mode: one pattern after another, each rounded up to whole bars.
            var bar = 4L * flp.Ppq;
            long at = 0;
            foreach (var (pat, pn) in patterns)
            {
                if (pn.Count == 0) continue;
                var end = pn.Max(x => x.Note.Start + x.Note.Length);
                var length = Math.Max(bar, (end + bar - 1) / bar * bar);
                Place(pat, at, 0, length);
                at += length;
            }
        }

        return order.Union(notes.Keys).Where(notes.ContainsKey)
            .Select(ch => new MidiChannel(names.GetValueOrDefault(ch, $"Channel {ch + 1}"), sources.GetValueOrDefault(ch, ""), flp.Ppq,
                                          notes[ch].OrderBy(n => n.Start).ThenBy(n => n.Key).ToList(),
                                          plugins.GetValueOrDefault(ch, "")))
            .ToList();
    }

    /// <summary>
    /// Pattern clips in the playlist: (pattern, position, length, offset into the pattern).
    /// FL 9 onwards writes 32-byte items (60 from FL 21): u32 position, u16 pattern base (20480),
    /// u16 item index, u32 length, ..., i32 start offset at 24 (-1 = none). Older layouts aren't read.
    /// </summary>
    static List<(int Pattern, long At, long Length, long From)> PatternItems(ReadOnlySpan<byte> d)
    {
        var items = new List<(int, long, long, long)>();
        foreach (var size in new[] { 60, 32 })
        {
            if (d.Length == 0 || d.Length % size != 0) continue;
            var ok = true;
            for (var off = 0; off + size <= d.Length && ok; off += size)
                ok = BinaryPrimitives.ReadUInt16LittleEndian(d[(off + 4)..]) == PatternBase;
            if (!ok) continue;
            for (var off = 0; off + size <= d.Length; off += size)
            {
                var index = BinaryPrimitives.ReadUInt16LittleEndian(d[(off + 6)..]);
                if (index <= PatternBase) continue;   // audio or automation clip
                var from = BinaryPrimitives.ReadInt32LittleEndian(d[(off + 24)..]);
                items.Add((index - PatternBase, BinaryPrimitives.ReadUInt32LittleEndian(d[off..]),
                           BinaryPrimitives.ReadUInt32LittleEndian(d[(off + 8)..]), Math.Max(0, from)));
            }
            break;
        }
        return items;
    }

    /// <summary>
    /// Plugin states. VST states become .fxp or .vstpreset files the plugin can load; native
    /// FL plugin states are saved raw (.flstate) since they aren't complete .fst files.
    /// Each knows whether it's a generator (on a channel) or an effect (in the mixer).
    /// </summary>
    public IReadOnlyList<PresetBlob> ExtractPresets(string path)
    {
        var flp = Load(path);
        var presets = new List<PresetBlob>();
        var channelTypes = new Dictionary<int, int>();
        int? channel = null;
        var inMixer = false;
        var channelHasPlugin = false;
        var pendingInternal = "";
        var currentName = "";
        var insert = -1;
        var insertNames = new Dictionary<int, string>();
        var presetInsert = new Dictionary<int, int>();   // preset index → mixer insert
        foreach (var e in flp.Events)
        {
            switch (e.Id)
            {
                case EvChannelNew: channel = (int)e.Int; inMixer = false; channelHasPlugin = false; break;
                case EvChannelType when channel is not null: channelTypes[channel.Value] = (int)e.Int; break;
                case EvInsertFlags: inMixer = true; insert++; break;
                case EvInsertName when insert >= 0: insertNames[insert] = flp.Str(e); break;
                case EvMixerSlot: inMixer = true; break;
                case EvPluginInternal:
                    pendingInternal = flp.Str(e);
                    currentName = "";
                    if (channelHasPlugin) inMixer = true;
                    channelHasPlugin = true;
                    break;
                case EvPluginName: currentName = flp.Str(e); break;
                case EvPluginState when e.Payload.Length > 0:
                    var onChannel = !inMixer && channel is not null &&
                                    channelTypes.GetValueOrDefault(channel.Value) == ChannelTypeInstrument;
                    if (inMixer && insert >= 0) presetInsert[presets.Count] = insert;
                    if (pendingInternal.Equals("Fruity Wrapper", StringComparison.OrdinalIgnoreCase))
                    {
                        var f = WrapperFields(e.Payload.Span);
                        var plugin = WrapperText(f, VstName) is { Length: > 0 } n ? n : TextScan.PluginNameFromPath(WrapperText(f, VstPath));
                        var name = currentName.Length > 0 ? currentName : plugin;
                        if (f.TryGetValue(VstState, out var state) && state.Length > 0)
                            presets.Add(WrapperPreset(f, state, name, plugin) with
                            {
                                Kind = onChannel ? PluginKind.Instrument : PluginKind.Effect,
                            });
                    }
                    else if (pendingInternal.Length > 0)
                    {
                        presets.Add(new PresetBlob(currentName.Length > 0 ? currentName : pendingInternal,
                                                   pendingInternal, ".flstate", e.Payload.ToArray())
                        {
                            Kind = onChannel || NativeGenerators.Contains(pendingInternal) ? PluginKind.Instrument : PluginKind.Effect,
                            Native = true,
                        });
                    }
                    pendingInternal = "";
                    break;
            }
        }
        foreach (var (i, ins) in presetInsert)
        {
            if (i >= presets.Count) continue;
            var where = ins == 0 ? "Master" : $"Insert {ins}";
            if (insertNames.GetValueOrDefault(ins) is { Length: > 0 } n) where += $" · {n}";
            presets[i] = presets[i] with { Where = where };
        }
        return presets;
    }
}

/// <summary>Builds VST2 .fxp program files around opaque plugin chunks.</summary>
public static class Fxp
{
    /// <summary>
    /// Wraps a VST chunk in an 'FPCh' (opaque chunk) .fxp header. Pass the plugin's
    /// four-character id when known so hosts can match the preset to the plugin.
    /// </summary>
    public static byte[] WrapChunk(byte[] chunk, string programName, int pluginId = 0)
    {
        using var ms = new MemoryStream();
        void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }
        var name = ProgramName(programName);
        var bodyLen = 4 + 16 + 28 + 4 + chunk.Length;
        ms.Write("CcnK"u8);
        I32(bodyLen);
        ms.Write("FPCh"u8);
        I32(1);      // format version
        I32(pluginId);
        I32(1);      // plugin version
        I32(1);      // number of programs
        ms.Write(name);
        I32(chunk.Length);
        ms.Write(chunk);
        return ms.ToArray();
    }

    /// <summary>
    /// An 'FxCk' (parameter list) .fxp, for plugins that save their parameters rather than a chunk.
    /// The values are written big-endian, as the format wants.
    /// </summary>
    public static byte[] WrapParams(IReadOnlyList<float> values, string programName, int pluginId = 0)
    {
        using var ms = new MemoryStream();
        void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }
        ms.Write("CcnK"u8);
        I32(4 + 16 + 28 + 4 * values.Count);
        ms.Write("FxCk"u8);
        I32(1);      // format version
        I32(pluginId);
        I32(1);      // plugin version
        I32(values.Count);
        ms.Write(ProgramName(programName));
        Span<byte> f = stackalloc byte[4];
        foreach (var v in values)
        {
            BinaryPrimitives.WriteSingleBigEndian(f, v);
            ms.Write(f);
        }
        return ms.ToArray();
    }

    static byte[] ProgramName(string programName)
    {
        var name = new byte[28];
        Encoding.ASCII.GetBytes(programName.Length > 27 ? programName[..27] : programName).CopyTo(name, 0);
        return name;
    }
}

/// <summary>Builds VST3 .vstpreset files from a plugin's saved state.</summary>
public static class VstPreset
{
    /// <summary>
    /// The class id as a .vstpreset writes it: 32 hex digits in Windows GUID order,
    /// i.e. the first three groups byte-swapped from their in-memory form.
    /// </summary>
    public static string ClassIdString(ReadOnlySpan<byte> tuid)
    {
        Span<byte> b = stackalloc byte[16];
        tuid.CopyTo(b);
        b[..4].Reverse();
        b[4..6].Reverse();
        b[6..8].Reverse();
        return Convert.ToHexString(b);
    }

    /// <summary>
    /// "VST3", version 1, class id, offset of the chunk list; then the component
    /// state ("Comp") and controller state ("Cont"); then the chunk list.
    /// </summary>
    public static byte[] Build(ReadOnlySpan<byte> classId, byte[] component, byte[] controller)
    {
        using var ms = new MemoryStream();
        var b = new byte[8];
        void I32(int v) { BinaryPrimitives.WriteInt32LittleEndian(b, v); ms.Write(b, 0, 4); }
        void I64(long v) { BinaryPrimitives.WriteInt64LittleEndian(b, v); ms.Write(b, 0, 8); }

        const int HeaderSize = 4 + 4 + 32 + 8;
        var chunks = new List<(string Id, long Offset, long Size)> { ("Comp", HeaderSize, component.Length) };
        if (controller.Length > 0) chunks.Add(("Cont", HeaderSize + component.Length, controller.Length));
        var listOffset = HeaderSize + component.Length + controller.Length;

        ms.Write("VST3"u8);
        I32(1);
        ms.Write(Encoding.ASCII.GetBytes(ClassIdString(classId)));
        I64(listOffset);
        ms.Write(component);
        ms.Write(controller);
        ms.Write("List"u8);
        I32(chunks.Count);
        foreach (var (id, offset, size) in chunks)
        {
            ms.Write(Encoding.ASCII.GetBytes(id));
            I64(offset);
            I64(size);
        }
        return ms.ToArray();
    }
}
