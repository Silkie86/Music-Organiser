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
public sealed partial class FlStudioReader : IProjectReader
{
    public string Daw => "FL Studio";
    public int Version => 4;   // 2: FL 9 plugin states read whole, plugins that saved nothing, FL's own effects in the old wrapper; 3: FL 9 steps and playlist; 4: samples that don't play
    public IReadOnlyList<string> Extensions { get; } = [".flp"];

    const int Text = 192, Data = 208;
    const int EvChannelType = 21;
    const int EvChannelNew = 64, EvPatternNew = 65, EvTempoCoarse = 66;
    const int EvMixerSlot = 98;                 // WORD + 34: closes a mixer slot's events
    const int EvTempo = 156;                    // DWORD: tempo × 1000
    const int Ev3Bytes = 172;                   // a DWORD id, but three bytes from FL 25 (meaning unknown)
    const int EvChannelNameOld = Text + 0, EvPatternName = Text + 1, EvTitle = Text + 2;
    const int EvSamplePath = Text + 4, EvVersion = Text + 7, EvPluginInternal = Text + 9;
    const int EvPluginName = Text + 11, EvGenre = Text + 14, EvArtists = Text + 15;
    const int EvPluginState = Data + 5;         // 213: plugin state; VST metadata for the Fruity Wrapper
    const int EvPatternNotes = Data + 16;       // 224: 24-byte note records
    const int EvInsertFlags = Data + 28;        // 236: starts each mixer insert (0 is the master)
    const int EvInsertName = Text + 12;         // 204: an insert's name, just before the flags that start it
    const int EvProjectTime = Data + 29;        // 237: when the project was made and how long it has been open, as two Delphi dates

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

    /// <summary>What's said of a file named .flp that holds no FL project any more: empty, or overwritten (disk damage).</summary>
    internal const string Damaged = "This file is empty or damaged: it doesn't start the way an FL Studio project does, so there's nothing in it to read";

    sealed class Flp
    {
        public int Ppq;
        public string Version = "";
        public bool Utf16 = true;
        public readonly List<Event> Events = new();
        /// <summary>Said about the project when it could still be read but something about the file was off.</summary>
        public string? Warning;

        public string Str(Event e)
        {
            var s = e.Payload.Span;
            return (Utf16 ? Encoding.Unicode.GetString(s) : Encoding.Latin1.GetString(s)).TrimEnd('\0');
        }
    }

    static Flp Load(string path) => ReadOnce.Get(path, p => ReadOnce.Bytes(p, out var length) is { } raw ? Load(raw, length) : Load(File.ReadAllBytes(p)));

    static Flp Load(byte[] raw) => Load(raw, raw.Length);

    /// <param name="length">How much of <paramref name="raw"/> is the file (a borrowed buffer can be longer).</param>
    static Flp Load(byte[] raw, int length)
    {
        if (length < 22 || Encoding.ASCII.GetString(raw, 0, 4) != "FLhd") throw new InvalidDataException(Damaged);
        var headerLen = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4));
        if (headerLen < 0 || headerLen > length - 16) throw new InvalidDataException(Damaged);
        var flp = new Flp { Ppq = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(12)) };
        var dt = 8 + headerLen;
        var pos = dt + 8;
        int end;
        if (Encoding.ASCII.GetString(raw, dt, 4) == "FLdt")
            end = (int)Math.Min(length, (long)pos + BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(dt + 4)));
        else
        {
            // Some FL Studio 4 to 7 saves have junk (what looks like a leftover memory address and a size) where the
            // FLdt chunk's name and length belong. Their events follow it as usual, to the end of the file.
            end = length;
            flp.Warning = "Saved by an old FL Studio that garbled the start of its data; everything after it was read as usual.";
        }

        var major = 0;
        while (pos < end)
        {
            int id = raw[pos++];
            int size;
            if (id < 64) size = 1;
            else if (id < 128) size = 2;
            // FL 25 and 26 write event 172 with three bytes, not the four its id says. Reading four
            // swallows the next event and garbles what follows (the tempo, title and first pattern).
            else if (id == Ev3Bytes && major >= 25) size = 3;
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
            if (id == EvVersion && major == 0) int.TryParse(Encoding.ASCII.GetString(raw, pos, size).Split('.')[0], out major);
            flp.Events.Add(new Event(id, raw.AsMemory(pos, size)));
            pos += size;
        }

        var ver = flp.Events.FirstOrDefault(e => e.Id == EvVersion);
        // Without the FLdt chunk to go by, only events that include FL's version show it's really a project.
        if (flp.Warning is not null && ver.Id != EvVersion) throw new InvalidDataException(Damaged);
        if (ver.Id == EvVersion)
        {
            flp.Version = Encoding.ASCII.GetString(ver.Payload.Span).TrimEnd('\0');
            var parts = flp.Version.Split('.');
            if (parts.Length >= 2 && major > 0 && int.TryParse(parts[1], out var minor))
                flp.Utf16 = major > 11 || (major == 11 && minor >= 5);
        }
        if (major is > 0 and < 12) StepsAsNotes(flp);
        return flp;
    }

    // FL 9 and older keep the step sequencer's steps in each channel's events, after the pattern they're in (event 65):
    // a dot (WORD 91: step + 256), the step's volume (WORD 72: step, volume) and its shift (WORD 92: step, shift).
    // Newer FL saves steps as notes with no length, as FL 25 did when funk step_8 was saved again.
    const int EvShuffle = 11, EvDot = 91, EvDotVolume = 72, EvDotShift = 92;

    /// <summary>
    /// Turns old step sequencer dots into note events (224) placed right after them, so they're read like FL 25's
    /// steps: key C5, no length, the step's volume as velocity. Odd steps are swung by the song's shuffle, and a shift
    /// moves its step in 192nds of a beat; both checked against FL 25's re-save of funk step_8 (shuffle 36 moves odd
    /// steps 3 ticks at 96 PPQ, shift 24 moves a step 12).
    /// </summary>
    static void StepsAsNotes(Flp flp)
    {
        if (!flp.Events.Any(e => e.Id == EvDot)) return;
        var step = flp.Ppq / 4;
        var swing = (int)(flp.Events.FirstOrDefault(e => e.Id == EvShuffle).Int * step / 256);
        var events = new List<Event>(flp.Events.Count);
        int? channel = null;
        var dots = new SortedSet<int>();
        var volumes = new Dictionary<int, int>();
        var shifts = new Dictionary<int, int>();
        void Flush()
        {
            if (channel is { } ch && dots.Count > 0)
            {
                var notes = new byte[24 * dots.Count];
                var i = 0;
                foreach (var d in dots)
                {
                    var n = notes.AsSpan(24 * i++, 24);
                    var at = d * step + (d % 2 == 1 ? swing : 0) + shifts.GetValueOrDefault(d) * flp.Ppq / 192;
                    BinaryPrimitives.WriteInt32LittleEndian(n, Math.Max(at, 0));
                    BinaryPrimitives.WriteUInt16LittleEndian(n[4..], 0x4000);
                    BinaryPrimitives.WriteUInt16LittleEndian(n[6..], (ushort)ch);
                    BinaryPrimitives.WriteUInt16LittleEndian(n[12..], (ushort)DefaultRoot);
                    n[16] = 120; n[18] = 64; n[20] = 64; n[22] = 128; n[23] = 128;   // fine pitch, pan, release and mods centred, as FL writes them
                    n[21] = (byte)Math.Clamp(volumes.GetValueOrDefault(d, 100), 0, 128);
                }
                events.Add(new Event(EvPatternNotes, notes));
            }
            dots.Clear(); volumes.Clear(); shifts.Clear();
        }
        foreach (var e in flp.Events)
        {
            switch (e.Id)
            {
                case EvDot when channel is not null: dots.Add((int)(e.Int & 0xff)); break;
                case EvDotVolume when channel is not null: volumes[(int)(e.Int & 0xff)] = (int)(e.Int >> 8); break;
                case EvDotShift when channel is not null: shifts[(int)(e.Int & 0xff)] = (int)(e.Int >> 8); break;
                default:
                    Flush();
                    if (e.Id == EvChannelNew) channel = (int)e.Int;
                    break;
            }
            events.Add(e);
        }
        Flush();
        flp.Events.Clear();
        flp.Events.AddRange(events);
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
        Flp flp;
        try { flp = Load(path); }
        // Listed with what's wrong with it rather than as a file the app failed on.
        catch (InvalidDataException e) when (e.Message == Damaged) { return new ProjectInfo { Daw = Daw, Confidence = Confidence.Catalogue, Warnings = { e.Message + "." } }; }
        var info = new ProjectInfo
        {
            Daw = Daw, Version = flp.Version, Confidence = Confidence.Full,
            CanExtractMidi = true, CanExtractPresets = true,
        };
        if (flp.Warning is not null) info.Warnings.Add(flp.Warning);
        info.Details["PPQ"] = flp.Ppq.ToString();

        var channelNames = new Dictionary<int, string>();
        var channelTypes = new Dictionary<int, int>();
        int? channel = null;
        var inMixer = false;
        var pendingInternal = "";
        var channelHasPlugin = false;
        int patterns = 0, notes = 0;
        var nothingSaved = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

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
                case EvProjectTime when e.Payload.Length >= 16:
                    (info.Created, var spent) = ProjectTime(e.Payload.Span);
                    if (spent is { } time) info.Details["Time spent"] = time;
                    break;
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
                    if (f.TryGetValue(VstState, out var st) && SavedNothing(st) && (name.Length > 0 || vpath.Length > 0))
                        nothingSaved.Add(name.Length > 0 ? name : TextScan.PluginNameFromPath(vpath));
                    if (name.Length > 0 || vpath.Length > 0)
                    {
                        var isInst = !inMixer && channel is not null && channelTypes.GetValueOrDefault(channel.Value) == ChannelTypeInstrument;
                        var vendor = WrapperText(f, VstVendor);
                        var shown = name.Length > 0 ? name : TextScan.PluginNameFromPath(vpath);
                        // Old FL (FL 9) hosted its own effects, such as Fruity Compressor, in the wrapper: they're still FL's own.
                        var own = PluginCatalog.IsFlStudioBundled(shown, vendor, vpath);
                        info.Plugins.Add(new PluginRef(shown, own && vendor.Length == 0 ? "Image-Line" : vendor,
                            isInst ? PluginKind.Instrument : PluginKind.Effect,
                            own ? "Native" : TextScan.PluginFormatFromPath(vpath) is { Length: > 0 } fmt ? fmt : "VST",
                            vpath));
                    }
                    pendingInternal = "";
                    break;
            }
        }

        foreach (var i in channelNames.Keys.Union(channelTypes.Keys).Order())
            info.Tracks.Add(channelNames.GetValueOrDefault(i, $"Channel {i + 1}"));
        foreach (var n in nothingSaved)
            info.Warnings.Add($"{n} has no saved sound in this file (it gave FL Studio nothing when the project was saved), " +
                              "so it opens at its default sound. An earlier save of the project may still have it; converting to Bitwig takes it from there.");
        info.Details["Channels"] = info.Tracks.Count.ToString();
        info.Details["Patterns"] = patterns.ToString();
        info.Details["Notes"] = notes.ToString();
        try { NoteUnplayed(ReadSong(flp, path), info); }
        catch (Exception e) when (e is not OutOfMemoryException) { }   // the rest is still worth listing
        return info.Dedupe();
    }

    /// <summary>
    /// The samples this save holds but doesn't play (<see cref="ProjectInfo.Unplayed"/>): on a channel with nothing in the
    /// playlist, or muted (the channel switched off, or every clip of it muted or on a muted playlist track). A sample on
    /// several channels plays when any of them does.
    /// </summary>
    static void NoteUnplayed(FlSong song, ProjectInfo info)
    {
        foreach (var bySample in song.Channels.Where(c => c.Sample.Length > 0).GroupBy(c => c.Sample, StringComparer.OrdinalIgnoreCase))
        {
            static bool Placed(FlChannel c) => c.Clips.Any(clip => clip.Notes.Count > 0) || c.AudioClips.Count > 0;
            static bool Plays(FlChannel c) => c.Enabled && (c.Notes.Count > 0 || c.AudioClips.Any(a => !a.Muted && !a.OnMutedTrack));
            if (bySample.Any(Plays)) continue;
            info.Unplayed[bySample.Key] = bySample.Any(Placed) ? SampleUse.Muted : SampleUse.NoNotes;
        }
    }

    /// <summary>
    /// Event 237: the day the project was first made (a Delphi date: days since 30 Dec 1899) and how long FL Studio has
    /// had it open, in days. Saving again, even in a newer FL Studio, keeps the date. Out-of-range dates are left out.
    /// </summary>
    public static (DateTime? Created, string? Spent) ProjectTime(ReadOnlySpan<byte> payload)
    {
        var made = BitConverter.ToDouble(payload[..8]);
        var open = BitConverter.ToDouble(payload[8..16]);
        DateTime? created = double.IsFinite(made) && made > 35_000 && made < 80_000   // 1995 to 2119
            ? DateTime.SpecifyKind(new DateTime(1899, 12, 30).AddDays(made), DateTimeKind.Local) : null;
        string? spent = double.IsFinite(open) && open > 0 && open < 3650 ? Duration(TimeSpan.FromDays(open)) : null;
        return (created, spent);
    }

    static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, (int)t.TotalMinutes)} min";

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

        var id = f.TryGetValue(VstFourCc, out var cc) && cc.Length == 4
            ? BinaryPrimitives.ReadInt32LittleEndian(cc)
            : 0;
        var chunk = state;
        var selected = 0;
        if (Vst2Header(state) is var (start, len, current))
        {
            selected = current;
            if (len == 0 && (state[4] & 0x08) == 0 && ParamState(state, start) is var (values, program))
                return new PresetBlob(name, plugin, ".fxp", Fxp.WrapParams(values, program.Length > 0 ? program : plugin, id)) { Program = selected };
            if (start + (long)len <= state.Length) chunk = state.AsSpan(start, (int)len).ToArray();
        }
        return new PresetBlob(name, plugin, ".fxp", Fxp.WrapChunk(chunk, plugin, id)) { Program = selected };
    }

    /// <summary>
    /// Where the plugin's own state starts after the header FL puts in front of a VST2's state, and its chunk length.
    /// Newer FL (seen in FL 21 and 25): i32 -9, u8 flags (0x08 = the plugin saves a chunk), i32 -2,
    /// u32 chunk length, u32 0, u32 program, so 21 bytes. FL 9 (wrapper version 6) has no -2 or 0:
    /// i32 -9, u8 flags, u32 chunk length, u32 program, so 13 bytes, and reading it as the newer one
    /// kept only a few bytes of a big chunk, so the plugin came up at its default sound. Program is the plugin's
    /// program that was selected, which FL selects again after giving the plugin its chunk.
    /// </summary>
    static (int Start, uint Length, int Program)? Vst2Header(byte[] state)
    {
        if (state.Length < 13 || BinaryPrimitives.ReadInt32LittleEndian(state) != -9) return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(5)) == -2)
            return state.Length >= 21
                ? (21, BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(9)), Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(17))))
                : null;
        return (13, BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(5)), Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(9))));
    }

    /// <summary>A VST2 that saves its sound as a chunk but gave FL an empty one, so FL kept none of it.</summary>
    static bool SavedNothing(byte[] state) => Vst2Header(state) is (_, 0, _) && (state[4] & 0x08) != 0;

    /// <summary>
    /// Plugins that don't save a chunk (Predator, older FabFilter) have their parameters after
    /// FL's header instead: u32 count, the values as floats, then u32 1 and the program name.
    /// </summary>
    static (float[] Values, string Program)? ParamState(byte[] state, int start)
    {
        if (state.Length < start + 4) return null;
        var n = BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(start));
        if (n == 0 || start + 4 + 4L * n > state.Length) return null;
        var values = new float[n];
        for (var i = 0; i < n; i++) values[i] = BinaryPrimitives.ReadSingleLittleEndian(state.AsSpan(start + 4 + 4 * i));
        var program = "";
        var at = start + 4 + 4 * (int)n;
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
    /// 3 is the component (processor) state, 2 the controller state and 4 the plugin's parameter ids.
    /// </summary>
    static (byte[] Component, byte[] Controller)? Vst3State(byte[] state) =>
        Vst3Records(state) is { } r && r.TryGetValue(3, out var comp) ? (comp, r.TryGetValue(2, out var cont) ? cont : []) : null;

    /// <summary>
    /// The plugin's parameter ids in the order FL lists its settings (record 4: u32 count, then the ids),
    /// which is how FL's automation names a setting. Seen in FL 26 with Serum 2: setting 205 is id 2000003.
    /// </summary>
    static IReadOnlyList<uint>? Vst3ParameterIds(byte[] state)
    {
        if (Vst3Records(state) is not { } records || !records.TryGetValue(4, out var r) || r.Length < 4) return null;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(r);
        if (count == 0 || 4 + 4L * count > r.Length) return null;
        var ids = new uint[count];
        for (var i = 0; i < count; i++) ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(4 + 4 * i));
        return ids;
    }

    static Dictionary<uint, byte[]>? Vst3Records(byte[] state)
    {
        var records = new Dictionary<uint, byte[]>();
        var pos = 4;
        while (pos + 12 <= state.Length)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(pos));
            var size = BinaryPrimitives.ReadUInt64LittleEndian(state.AsSpan(pos + 4));
            pos += 12;
            if (size > (ulong)(state.Length - pos)) return null;
            records[id] = state.AsSpan(pos, (int)size).ToArray();
            pos += (int)size;
        }
        return records;
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
                    list.AddRange(DecodeNotes(e.Payload.Span, null).Where(n => !n.Note.Muted));   // MIDI has no muted notes
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
    /// In 24-byte records bit 0x20 of byte 19 marks a note muted in the piano roll, and a length
    /// of 0 a step from the step sequencer, which is kept 1 tick long and marked.
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
            var length = BinaryPrimitives.ReadUInt32LittleEndian(r[8..]);
            notes.Add((BinaryPrimitives.ReadUInt16LittleEndian(r[6..]), new NoteEvent(
                BinaryPrimitives.ReadUInt32LittleEndian(r), Math.Max(1, length),
                BinaryPrimitives.ReadUInt16LittleEndian(r[12..]) & 0x7F, vel,
                Muted: size == 24 && (r[19] & 0x20) != 0, Step: length == 0)));
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
    /// The first arrangement's playlist tracks and which are muted. FL saves every track, used or not, after its
    /// arrangement's playlist (500 from FL 12.9), so their count is how many the playlist has. A track's settings (event
    /// 238, 70 bytes in FL 25 and 26) start with u32 its number from 1, colour and icon, then a byte at 12 that's 1 when
    /// the track plays and 0 when it's muted.
    /// </summary>
    sealed class PlaylistTracks
    {
        const int EvArrangementNew = 99;        // WORD: starts an arrangement (FL 12.9 onwards); its name, playlist and tracks follow
        const int EvTrackData = Data + 30;      // 238: a playlist track's settings
        const int EvTrackName = Text + 47;      // 239: the track's name, after its settings (only when it was named)

        readonly Dictionary<int, (FlTrack Track, bool Muted)> tracks = new();
        int arrangements, last;

        /// <summary>Takes in the event if it's one of the first arrangement's tracks (or starts an arrangement).</summary>
        public void Read(Event e, Flp flp)
        {
            switch (e.Id)
            {
                case EvArrangementNew: arrangements++; break;
                case EvTrackData when arrangements <= 1 && e.Payload.Length > 12:
                    last = (int)e.Int;
                    tracks[last] = (new FlTrack(last, ""), e.Payload.Span[12] == 0);
                    break;
                case EvTrackName when arrangements <= 1 && tracks.TryGetValue(last, out var t):
                    tracks[last] = t with { Track = t.Track with { Name = flp.Str(e) } };
                    break;
            }
        }

        /// <summary>
        /// The track a clip is on, from the slot FL saves with it, which counts down from the first track (499 is track 1
        /// of 500), and whether that track is muted.
        /// </summary>
        public (FlTrack Track, bool Muted)? At(int slot) =>
            tracks.TryGetValue(tracks.Count - slot, out var t) ? t : null;

        /// <summary>The number of the track a clip's slot is, or 0 when the file doesn't say.</summary>
        public int Number(int slot) => At(slot)?.Track.Number ?? 0;
    }

    /// <summary>The playlist's clips, with those on a muted track muted too (for automation, which then doesn't play).</summary>
    static List<PlaylistItem> PlaylistItems(ReadOnlySpan<byte> d, PlaylistTracks tracks) =>
        PlaylistItems(d).Select(i => tracks.At(i.Slot) is { Muted: true } ? i with { Muted = true } : i).ToList();

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
        var samples = new Dictionary<int, string>();
        var roots = new Dictionary<int, int>();
        var pitches = new Dictionary<int, int>();
        var order = new List<int>();
        var rawNotes = new List<(int Pattern, ReadOnlyMemory<byte> Data)>();
        var patternNames = new Dictionary<int, string>();
        ReadOnlyMemory<byte>? playlist = null;
        var tracks = new PlaylistTracks();
        int? channel = null, pattern = null;
        var inMixer = false;
        var channelHasPlugin = false;
        var pendingInternal = "";

        foreach (var e in flp.Events)
        {
            tracks.Read(e, flp);
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
                    if (sample.Length > 0)
                    {
                        sources[channel.Value] = sample.Split('\\', '/')[^1];
                        samples[channel.Value] = ResolveSamplePath(sample, path);
                    }
                    break;
                case EvRootNote when channel is not null && !inMixer && e.Int <= 127: roots[channel.Value] = (int)e.Int; break;
                case EvChannelLevels when channel is not null && !inMixer && Levels(e.Payload.Span, flp.Version) is { } lv:
                    pitches[channel.Value] = lv.Pitch;
                    break;
                case EvPatternNew: pattern = (int)e.Int; break;
                case EvPatternName when pattern is not null: patternNames[pattern.Value] = flp.Str(e); break;
                case EvPatternNotes when pattern is not null: rawNotes.Add((pattern.Value, e.Payload)); break;
                case EvPlaylist when playlist is null: playlist = e.Payload; break;
            }
        }

        var notes = PlaceClips(flp, rawNotes, playlist, tracks, order.ToHashSet(), patternNames)
            .Select(kv => (Channel: kv.Key, Notes: Audible(kv.Value))).Where(x => x.Notes.Count > 0)
            .ToDictionary(x => x.Channel, x => x.Notes);

        return order.Union(notes.Keys).Where(notes.ContainsKey)
            .Select(ch => new MidiChannel(names.GetValueOrDefault(ch, $"Channel {ch + 1}"), sources.GetValueOrDefault(ch, ""), flp.Ppq,
                                          notes[ch],
                                          plugins.GetValueOrDefault(ch, ""), samples.GetValueOrDefault(ch, ""),
                                          roots.GetValueOrDefault(ch, DefaultRoot), pitches.GetValueOrDefault(ch)) { Channel = ch })
            .ToList();
    }

    /// <summary>
    /// Each channel's clips across the song: one per pattern placed in the playlist (the first
    /// arrangement), holding the channel's notes in that part of the pattern, timed from the clip's
    /// start. With nothing in the playlist, patterns play one after another. Clips on a muted track are marked
    /// <see cref="FlClip.OnMutedTrack"/>; <see cref="FlClip.Muted"/> is only the clip's own mute.
    /// </summary>
    static Dictionary<int, List<FlClip>> PlaceClips(Flp flp, List<(int Pattern, ReadOnlyMemory<byte> Data)> rawNotes,
                                                    ReadOnlyMemory<byte>? playlist, PlaylistTracks tracks, IReadOnlySet<int> known,
                                                    IReadOnlyDictionary<int, string> patternNames)
    {
        var patterns = new SortedDictionary<int, List<(int Channel, NoteEvent Note)>>();
        foreach (var (pat, data) in rawNotes)
        {
            if (!patterns.TryGetValue(pat, out var list)) patterns[pat] = list = new();
            list.AddRange(DecodeNotes(data.Span, known));
        }

        var clips = new Dictionary<int, List<FlClip>>();
        void Place(int pat, long at, long from, long length, bool muted, int track, bool onMutedTrack)
        {
            if (!patterns.TryGetValue(pat, out var pn)) return;
            foreach (var channel in pn.Where(x => x.Note.Start >= from && x.Note.Start < from + length).GroupBy(x => x.Channel))
            {
                var notes = channel.Select(x => x.Note with { Start = x.Note.Start - from, Length = Math.Min(x.Note.Length, from + length - x.Note.Start) })
                                   .OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
                if (!clips.TryGetValue(channel.Key, out var cl)) clips[channel.Key] = cl = new();
                cl.Add(new FlClip(patternNames.GetValueOrDefault(pat, $"Pattern {pat}"), at, length, notes, muted)
                {
                    Track = track,
                    OnMutedTrack = onMutedTrack,
                });
            }
        }

        var items = playlist is { } pl ? PlaylistItems(pl.Span).Where(i => i.Index > PatternBase).ToList() : [];
        if (items.Count > 0)
            foreach (var i in items.OrderBy(i => i.At))
                Place(i.Index - PatternBase, i.At, i.From, i.Length, i.Muted, tracks.Number(i.Slot), tracks.At(i.Slot) is { Muted: true });
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
                Place(pat, at, 0, length, false, 0, false);
                at += length;
            }
        }
        return clips;
    }

    /// <summary>
    /// Audio clips in the playlist (the first arrangement), by channel: items whose index is a channel rather than a
    /// pattern. An audio clip's start offset is in milliseconds of the sample, as FL 25 saves it (a clip trimmed to
    /// start halfway through a 32 second sample says 16000).
    /// </summary>
    static Dictionary<int, List<FlAudioClip>> PlaceAudioClips(ReadOnlyMemory<byte>? playlist, PlaylistTracks tracks, IReadOnlySet<int> channels)
    {
        var clips = new Dictionary<int, List<FlAudioClip>>();
        if (playlist is not { } pl) return clips;
        foreach (var i in PlaylistItems(pl.Span).Where(i => i.Index < PatternBase && channels.Contains(i.Index) && i.Length > 0).OrderBy(i => i.At))
        {
            if (!clips.TryGetValue(i.Index, out var list)) clips[i.Index] = list = new();
            list.Add(new FlAudioClip(i.At, i.Length, i.Offset / 1000, i.Muted)
            {
                Track = tracks.Number(i.Slot),
                OnMutedTrack = tracks.At(i.Slot) is { Muted: true },
            });
        }
        return clips;
    }

    /// <summary>What plays of a channel's clips: the notes, less muted ones and those in muted clips or on muted tracks, in song ticks.</summary>
    static List<NoteEvent> Audible(IEnumerable<FlClip> clips) =>
        clips.Where(c => !c.Muted && !c.OnMutedTrack)
             .SelectMany(c => c.Notes.Where(n => !n.Muted).Select(n => n with { Start = c.Start + n.Start }))
             .OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();

    /// <summary>
    /// A clip in the playlist, in ticks. <see cref="Index"/> above 20480 is a pattern (index - 20480); otherwise a
    /// channel's clip. A muted clip has bit 0x2000 set in the flags at 18. <see cref="Offset"/> is where the clip
    /// starts in what it plays: ticks into a pattern, milliseconds into an audio clip's sample. <see cref="Slot"/> is the
    /// playlist track it sits on as FL saves it, counting down from the first track (see <see cref="TopSlot"/>).
    /// </summary>
    readonly record struct PlaylistItem(int Index, long At, long Length, double Offset, bool Muted, int Slot = 0)
    {
        /// <summary>Where a pattern clip starts in its pattern, in ticks.</summary>
        public long From => (long)Offset;
    }

    /// <summary>
    /// Clips in the playlist. FL 9 onwards writes 32-byte items (60 from FL 21, 80 from FL 25, 88 from FL 26):
    /// u32 position, u16 pattern base (20480), u16 item index, u32 length, ..., u16 flags at 18, start offset at 24
    /// (an integer before FL 21, a float since; -1 = none). Older layouts aren't read.
    /// </summary>
    static List<PlaylistItem> PlaylistItems(ReadOnlySpan<byte> d)
    {
        var items = new List<PlaylistItem>();
        foreach (var size in new[] { 88, 80, 60, 32 })
        {
            if (d.Length == 0 || d.Length % size != 0) continue;
            var ok = true;
            for (var off = 0; off + size <= d.Length && ok; off += size)
                ok = BinaryPrimitives.ReadUInt16LittleEndian(d[(off + 4)..]) == PatternBase;
            if (!ok) continue;
            for (var off = 0; off + size <= d.Length; off += size)
                items.Add(new PlaylistItem(BinaryPrimitives.ReadUInt16LittleEndian(d[(off + 6)..]), BinaryPrimitives.ReadUInt32LittleEndian(d[off..]),
                                           BinaryPrimitives.ReadUInt32LittleEndian(d[(off + 8)..]), ItemOffset(d[(off + 24)..]),
                                           (BinaryPrimitives.ReadUInt16LittleEndian(d[(off + 18)..]) & 0x2000) != 0,
                                           BinaryPrimitives.ReadUInt16LittleEndian(d[(off + 12)..])));
            break;
        }
        // FL 9: 32-byte items with 0 and 0x6000 where newer FL has the pattern, and the playlist row, counted
        // from 999 down, at 12. Each row plays one pattern there: row 999 - n is pattern n (checked against
        // FL 25's re-save of funk step_8, which put the same 123 items on its own tracks). FL 25 gives each
        // pattern played a track, in pattern order from track 1, so the slot is the one newer FL saves for that track.
        if (items.Count == 0 && d.Length > 0 && d.Length % 32 == 0)
        {
            for (var off = 0; off < d.Length; off += 32)
            {
                var row = BinaryPrimitives.ReadUInt16LittleEndian(d[(off + 12)..]);
                if (BinaryPrimitives.ReadUInt16LittleEndian(d[(off + 4)..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(d[(off + 6)..]) != 0x6000 ||
                    row is 0 or > 999)
                    return [];
                items.Add(new PlaylistItem(PatternBase + 999 - row, BinaryPrimitives.ReadUInt32LittleEndian(d[off..]),
                                           BinaryPrimitives.ReadUInt32LittleEndian(d[(off + 8)..]), ItemOffset(d[(off + 24)..]),
                                           (BinaryPrimitives.ReadUInt16LittleEndian(d[(off + 18)..]) & 0x2000) != 0, row));
            }
            var order = items.Select(i => i.Index).Distinct().Order().Select((index, n) => (index, n)).ToDictionary();
            for (var n = 0; n < items.Count; n++)
                items[n] = items[n] with { Slot = 499 - order[items[n].Index] };
        }
        return items;
    }

    /// <summary>A clip's start offset, written as a float or an integer depending on FL's version; none is 0.</summary>
    static double ItemOffset(ReadOnlySpan<byte> d)
    {
        var f = BinaryPrimitives.ReadSingleLittleEndian(d);
        if (f >= 1 && f < 1e9) return f;
        var i = BinaryPrimitives.ReadInt32LittleEndian(d);
        return i is > 0 and < 1_000_000_000 ? i : 0;
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
        var nextInsertName = "";
        var presetInsert = new Dictionary<int, int>();   // preset index → mixer insert
        foreach (var e in flp.Events)
        {
            switch (e.Id)
            {
                case EvChannelNew: channel = (int)e.Int; inMixer = false; channelHasPlugin = false; break;
                case EvChannelType when channel is not null: channelTypes[channel.Value] = (int)e.Int; break;
                case EvInsertFlags:
                    inMixer = true;
                    insert++;
                    if (nextInsertName.Length > 0) insertNames[insert] = nextInsertName;
                    nextInsertName = "";
                    break;
                case EvInsertName: nextInsertName = flp.Str(e); break;
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
                        if (f.TryGetValue(VstState, out var state) && state.Length > 0 && !SavedNothing(state))
                            presets.Add(WrapperPreset(f, state, name, plugin) with
                            {
                                Kind = onChannel ? PluginKind.Instrument : PluginKind.Effect,
                                Channel = onChannel ? channel : null,
                            });
                    }
                    else if (pendingInternal.Length > 0)
                    {
                        presets.Add(new PresetBlob(currentName.Length > 0 ? currentName : pendingInternal,
                                                   pendingInternal, ".flstate", e.Payload.ToArray())
                        {
                            Kind = onChannel || NativeGenerators.Contains(pendingInternal) ? PluginKind.Instrument : PluginKind.Effect,
                            Native = true,
                            Channel = onChannel ? channel : null,
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

/// <summary>Builds VST2 .fxp program files around opaque plugin chunks, and .fxb banks from them.</summary>
public static class Fxp
{
    /// <summary>
    /// The same settings as a .fxb bank, the form Bitwig writes (and reads back) in DAWproject
    /// files: an 'FPCh' chunk becomes an 'FBCh' bank chunk, with <paramref name="program"/> as the
    /// plugin's selected program; an 'FxCk' parameter program goes inside a one-program 'FxBk' bank.
    /// Anything else comes back unchanged.
    /// </summary>
    public static byte[] ToBank(byte[] fxp, int program = 0)
    {
        const int ProgramHeader = 60;   // CcnK, size, magic, version, id, plugin version, count, name[28]
        if (fxp.Length < ProgramHeader - 4 || !fxp.AsSpan(0, 4).SequenceEqual("CcnK"u8)) return fxp;
        var magic = Encoding.ASCII.GetString(fxp, 8, 4);
        var isChunk = magic == "FPCh";
        if (!isChunk && magic != "FxCk") return fxp;
        ReadOnlySpan<byte> body = isChunk ? fxp.AsSpan(ProgramHeader) : fxp;
        if (isChunk && (fxp.Length < ProgramHeader ||
                        BinaryPrimitives.ReadInt32BigEndian(fxp.AsSpan(ProgramHeader - 4)) != fxp.Length - ProgramHeader)) return fxp;

        using var ms = new MemoryStream();
        void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }
        ms.Write("CcnK"u8);
        I32(4 + 20 + 124 + (isChunk ? 4 : 0) + body.Length);
        ms.Write(isChunk ? "FBCh"u8 : "FxBk"u8);
        I32(2);                       // format version
        ms.Write(fxp.AsSpan(16, 8));  // plugin id and version, as the program had them
        // A chunk bank names the program the plugin had selected: Bitwig selects it again after loading the chunk, as
        // FL does, and KORG's M1 plays that program, so 0 brought it up on its first program, not the song's. A
        // parameter bank holds only this one program.
        if (!isChunk) program = 0;
        I32(program + 1);             // number of programs
        I32(program);                 // current program
        ms.Write(new byte[124]);
        if (isChunk) I32(body.Length);
        ms.Write(body);
        return ms.ToArray();
    }

    /// <summary>The plugin's own chunk inside an 'FPCh' .fxp or an 'FBCh' .fxb (as Bitwig saves VST2 settings), or null when it isn't one.</summary>
    public static byte[]? Chunk(byte[] fxp)
    {
        const int ProgramHeader = 60, BankHeader = 160;   // a bank: CcnK, size, magic, version, id, plugin version, count, current, 124 spare
        if (fxp.Length < ProgramHeader || !fxp.AsSpan(0, 4).SequenceEqual("CcnK"u8)) return null;
        var header = fxp.AsSpan(8, 4).SequenceEqual("FPCh"u8) ? ProgramHeader : fxp.AsSpan(8, 4).SequenceEqual("FBCh"u8) ? BankHeader : 0;
        if (header == 0 || fxp.Length < header) return null;
        var length = BinaryPrimitives.ReadInt32BigEndian(fxp.AsSpan(header - 4));
        return length >= 0 && length <= fxp.Length - header ? fxp[header..(header + length)] : null;
    }

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
    public static byte[] Build(ReadOnlySpan<byte> classId, byte[] component, byte[] controller) =>
        Build(ClassIdString(classId), component, controller);

    /// <summary>The component state ("Comp") inside a .vstpreset, or null when it isn't one.</summary>
    public static byte[]? Component(byte[] preset)
    {
        const int HeaderSize = 4 + 4 + 32 + 8;
        if (preset.Length < HeaderSize || !preset.AsSpan(0, 4).SequenceEqual("VST3"u8)) return null;
        var list = BinaryPrimitives.ReadInt64LittleEndian(preset.AsSpan(40));
        if (list < HeaderSize || list + 8 > preset.Length || !preset.AsSpan((int)list, 4).SequenceEqual("List"u8)) return null;
        var count = BinaryPrimitives.ReadInt32LittleEndian(preset.AsSpan((int)list + 4));
        for (var i = 0; i < count && list + 8 + 20 * (i + 1) <= preset.Length; i++)
        {
            var at = (int)list + 8 + 20 * i;
            if (!preset.AsSpan(at, 4).SequenceEqual("Comp"u8)) continue;
            var offset = BinaryPrimitives.ReadInt64LittleEndian(preset.AsSpan(at + 4));
            var size = BinaryPrimitives.ReadInt64LittleEndian(preset.AsSpan(at + 12));
            return offset >= HeaderSize && size >= 0 && offset + size <= list ? preset[(int)offset..(int)(offset + size)] : null;
        }
        return null;
    }

    /// <param name="classId">The class id as a .vstpreset writes it (see <see cref="ClassIdString"/>).</param>
    public static byte[] Build(string classId, byte[] component, byte[] controller)
    {
        using var ms = new MemoryStream();
        var b = new byte[8];
        void I32(int v) { BinaryPrimitives.WriteInt32LittleEndian(b, v); ms.Write(b, 0, 4); }
        void I64(long v) { BinaryPrimitives.WriteInt64LittleEndian(b, v); ms.Write(b, 0, 8); }

        const int HeaderSize = 4 + 4 + 32 + 8;
        // Bitwig lists the controller even when it's empty.
        var chunks = new List<(string Id, long Offset, long Size)>
            { ("Comp", HeaderSize, component.Length), ("Cont", HeaderSize + component.Length, controller.Length) };
        var listOffset = HeaderSize + component.Length + controller.Length;

        ms.Write("VST3"u8);
        I32(1);
        ms.Write(Encoding.ASCII.GetBytes(classId));
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
