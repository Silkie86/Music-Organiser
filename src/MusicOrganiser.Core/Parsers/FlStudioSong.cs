using System.Buffers.Binary;

namespace MusicOrganiser.Core.Parsers;

/// <summary>A plugin in an FL Studio project, with what another DAW needs to load it.</summary>
/// <param name="Format">"VST2", "VST3", or "FL" for FL Studio's own plugins.</param>
/// <param name="DeviceId">How other DAWs identify the plugin: the VST2 unique id as a number, or the VST3 class id.</param>
/// <param name="State">The plugin's settings as a file the plugin can load (.fxp or .vstpreset), when there is one.</param>
/// <param name="Path">Where the plugin was installed on the PC that saved the project (.dll or .vst3).</param>
/// <param name="ParameterIds">A VST3 plugin's own ids for its settings, in FL's order: FL automates setting n, other DAWs ParameterIds[n].</param>
public sealed record FlPlugin(string Name, string Vendor, string Format, string DeviceId, PresetBlob? State, string Path = "",
                              IReadOnlyList<uint>? ParameterIds = null)
{
    public bool Native => Format == "FL";
}

/// <summary>
/// A channel in the Channel Rack: what plays it, which mixer insert it feeds, and the notes it plays across
/// the song (in song ticks, without muted notes or clips). <paramref name="RootKey"/> is the MIDI note the
/// sample plays at its own pitch (60, FL's C5, unless changed); <paramref name="PitchCents"/> the channel's
/// pitch knob. Volume and pan are FL's 0..1 knob positions.
/// </summary>
public sealed record FlChannel(int Index, string Name, FlPlugin? Instrument, string Sample, int Insert, IReadOnlyList<NoteEvent> Notes,
                               int RootKey = FlStudioReader.DefaultRoot, int PitchCents = 0,
                               double Volume = FlStudioReader.DefaultVolume, double Pan = 0.5)
{
    /// <summary>The channel's part of each pattern clip in the playlist, muted ones and muted notes included.</summary>
    public IReadOnlyList<FlClip> Clips { get; init; } = [];

    /// <summary>The channel's volume envelope (ADSR), when it's switched on.</summary>
    public FlEnvelope? VolumeEnvelope { get; init; }
}

/// <summary>
/// An envelope in FL's own knob units: times from 100 to 65536 (how FL turns them into seconds isn't worked
/// out yet), and the sustain level from 0 to 1.
/// </summary>
public sealed record FlEnvelope(int Delay, int Attack, int Hold, int Decay, double Sustain, int Release);

/// <summary>
/// One pattern placed in the playlist, as one channel plays it: where it starts and how long it is (song
/// ticks), and the channel's notes in it, timed from the clip's start.
/// </summary>
public sealed record FlClip(string Name, long Start, long Length, IReadOnlyList<NoteEvent> Notes, bool Muted = false);

/// <summary>What an automation clip moves.</summary>
public enum FlControl { Volume, Pan, Pitch, PluginParameter, Other }

/// <summary>A point on an automation curve: beats from the start of the song, and the value (0..1).</summary>
public readonly record struct FlPoint(double Beat, double Value);

/// <summary>
/// An automation clip's curve across the song, as placed in the playlist. It moves <paramref name="Control"/>
/// on <paramref name="Channel"/>; for a plugin parameter, <paramref name="Parameter"/> is its index.
/// </summary>
public sealed record FlAutomation(string Name, int Channel, FlControl Control, int Parameter, IReadOnlyList<FlPoint> Points);

/// <summary>A mixer insert (0 is the master) and its effects in slot order.</summary>
public sealed record FlInsert(int Index, string Name, IReadOnlyList<FlPlugin> Effects);

/// <summary>An FL Studio project laid out for moving to another DAW.</summary>
public sealed record FlSong(string Title, int Ppq, double Tempo, IReadOnlyList<FlChannel> Channels, IReadOnlyList<FlInsert> Inserts,
                           IReadOnlyList<FlAutomation> Automation);

public sealed partial class FlStudioReader
{
    const int EvChannelInsert = 22;             // BYTE: the mixer insert a channel is routed to (older versions)
    const int EvChannelRoute = 104;             // WORD: the mixer insert a channel is routed to (seen from FL 20 to 25)
    const int EvRootNote = 135;                 // DWORD: the channel's root key as a MIDI note; absent when it's C5 (60)
    const int EvEnvelope = Data + 10;           // 218: an envelope and LFO, one each for pan, volume, mod X, mod Y, pitch
    const int EvChannelLevels = Data + 11;      // 219: i32 pan (0..12800), volume (0..12800), pitch in cents
    const int EvAutomationLink = Data + 19;     // 227: which control an automation clip moves
    const int EvAutomationPoints = Data + 26;   // 234: an automation clip's points
    const int ChannelTypeAutomation = 5;
    const double LevelsFull = 12800;

    /// <summary>FL Studio's default root key, C5 (MIDI 60).</summary>
    public const int DefaultRoot = 60;
    /// <summary>FL Studio's default channel volume, 78% (10000 of 12800).</summary>
    public const double DefaultVolume = 10000 / LevelsFull;

    /// <summary>Reads what's needed to rebuild the song elsewhere: channels, their plugins and notes, and the mixer.</summary>
    public FlSong ReadSong(string path)
    {
        var flp = Load(path);
        var title = "";
        double? tempo = null;
        var order = new List<int>();
        var names = new Dictionary<int, string>();
        var samples = new Dictionary<int, string>();
        var routes = new Dictionary<int, int>();
        var instruments = new Dictionary<int, FlPlugin>();
        var insertNames = new Dictionary<int, string>();
        var effects = new SortedDictionary<int, List<FlPlugin>>();
        var roots = new Dictionary<int, int>();
        var levels = new Dictionary<int, (double Pan, double Volume, int Pitch)>();
        var envelopes = new Dictionary<int, int>();
        var volumeEnvelopes = new Dictionary<int, FlEnvelope>();
        var types = new Dictionary<int, int>();
        var curves = new Dictionary<int, List<FlPoint>>();
        var links = new List<(int Clip, uint Target)>();
        var rawNotes = new List<(int Pattern, ReadOnlyMemory<byte> Data)>();
        var patternNames = new Dictionary<int, string>();
        ReadOnlyMemory<byte>? playlist = null;
        int? channel = null, pattern = null;
        var inMixer = false;
        var channelHasPlugin = false;
        var pendingInternal = "";
        var pluginName = "";
        var insert = -1;
        var nextInsertName = "";

        foreach (var e in flp.Events)
        {
            switch (e.Id)
            {
                case EvTempo: tempo = e.Int / 1000.0; break;
                case EvTempoCoarse when tempo is null: tempo = e.Int; break;
                case EvTitle: title = flp.Str(e); break;
                case EvChannelNew:
                    channel = (int)e.Int; inMixer = false; channelHasPlugin = false;
                    if (!order.Contains(channel.Value)) order.Add(channel.Value);
                    break;
                case EvChannelInsert or EvChannelRoute when channel is not null && !inMixer: routes[channel.Value] = (int)e.Int; break;
                case EvChannelType when channel is not null && !inMixer: types[channel.Value] = (int)e.Int; break;
                case EvRootNote when channel is not null && !inMixer && e.Int <= 127: roots[channel.Value] = (int)e.Int; break;
                case EvChannelLevels when channel is not null && !inMixer && Levels(e.Payload.Span) is { } lv:
                    levels[channel.Value] = lv;
                    break;
                case EvEnvelope when channel is not null && !inMixer:
                    // The second of a channel's envelopes is its volume's.
                    if (envelopes.GetValueOrDefault(channel.Value) == 1 && Envelope(e.Payload.Span) is { } env) volumeEnvelopes[channel.Value] = env;
                    envelopes[channel.Value] = envelopes.GetValueOrDefault(channel.Value) + 1;
                    break;
                case EvAutomationPoints when channel is not null && !inMixer: curves[channel.Value] = AutomationPoints(e.Payload.Span); break;
                case EvAutomationLink when e.Payload.Length >= 12:
                    links.Add((BinaryPrimitives.ReadUInt16LittleEndian(e.Payload.Span[2..]), BinaryPrimitives.ReadUInt32LittleEndian(e.Payload.Span[8..])));
                    break;
                case EvChannelNameOld when channel is not null && !inMixer: names.TryAdd(channel.Value, flp.Str(e)); break;
                case EvSamplePath when channel is not null && !inMixer:
                    if (flp.Str(e) is { Length: > 0 } sample) samples[channel.Value] = ResolveSamplePath(sample, path);
                    break;
                case EvInsertFlags:
                    inMixer = true;
                    insert++;
                    if (nextInsertName.Length > 0) insertNames[insert] = nextInsertName;
                    nextInsertName = "";
                    break;
                // An insert's name (with its colour and icon) comes just before the flags that start it.
                case EvInsertName: nextInsertName = flp.Str(e); break;
                case EvMixerSlot: inMixer = true; break;
                case EvPluginInternal:
                    pendingInternal = flp.Str(e);
                    pluginName = "";
                    if (channelHasPlugin) inMixer = true;
                    channelHasPlugin = true;
                    break;
                case EvPluginName:
                    pluginName = flp.Str(e);
                    if (channel is not null && !inMixer) names.TryAdd(channel.Value, pluginName);
                    break;
                case EvPluginState when e.Payload.Length > 0 && pendingInternal.Length > 0:
                    var plugin = SongPlugin(pendingInternal, e.Payload, pluginName);
                    if (!inMixer && channel is not null) instruments.TryAdd(channel.Value, plugin);
                    else if (inMixer && insert >= 0)
                    {
                        if (!effects.TryGetValue(insert, out var list)) effects[insert] = list = new();
                        list.Add(plugin);
                    }
                    pendingInternal = "";
                    break;
                case EvPatternNew: pattern = (int)e.Int; break;
                case EvPatternName when pattern is not null: patternNames[pattern.Value] = flp.Str(e); break;
                case EvPatternNotes when pattern is not null: rawNotes.Add((pattern.Value, e.Payload)); break;
                case EvPlaylist when playlist is null: playlist = e.Payload; break;
            }
        }

        var clips = PlaceClips(flp, rawNotes, playlist, order.ToHashSet(), patternNames);
        var channels = order
            .Where(ch => types.GetValueOrDefault(ch) != ChannelTypeAutomation)
            .Select(ch =>
            {
                var lv = levels.GetValueOrDefault(ch, (Pan: 0.5, Volume: DefaultVolume, Pitch: 0));
                var placed = clips.GetValueOrDefault(ch) ?? [];
                return new FlChannel(ch, names.GetValueOrDefault(ch, $"Channel {ch + 1}"), instruments.GetValueOrDefault(ch),
                                     samples.GetValueOrDefault(ch, ""), routes.GetValueOrDefault(ch), Audible(placed),
                                     roots.GetValueOrDefault(ch, DefaultRoot), lv.Pitch, lv.Volume, lv.Pan)
                {
                    Clips = placed,
                    VolumeEnvelope = volumeEnvelopes.GetValueOrDefault(ch),
                };
            })
            .ToList();
        var automation = PlaceAutomation(links, curves, names, playlist, flp.Ppq);
        var inserts = effects.Keys.Union(insertNames.Keys).Union(channels.Select(c => c.Insert)).Distinct().Order()
            .Select(i => new FlInsert(i, insertNames.GetValueOrDefault(i, ""), effects.GetValueOrDefault(i) ?? []))
            .ToList();
        return new FlSong(title, flp.Ppq, tempo ?? 140, channels, inserts, automation);
    }

    /// <summary>A channel's levels (event 219): pan and volume as 0..1 knob positions, and pitch in cents.</summary>
    static (double Pan, double Volume, int Pitch)? Levels(ReadOnlySpan<byte> d) =>
        d.Length < 12 ? null
        : (Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(d) / LevelsFull, 0, 1),
           Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(d[4..]) / LevelsFull, 0, 1),
           Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(d[8..]), -4800, 4800));

    /// <summary>
    /// An envelope (event 218, 68 bytes), or null when it's switched off: i32 flags, i32 on, then i32 delay,
    /// attack, hold, decay, sustain (0..128) and release, then the LFO and the curves. The layout is PyFLP's;
    /// FL's defaults match it, but no real file with an envelope switched on has been checked yet.
    /// </summary>
    static FlEnvelope? Envelope(ReadOnlySpan<byte> d)
    {
        if (d.Length < 32 || BinaryPrimitives.ReadInt32LittleEndian(d[4..]) == 0) return null;
        var v = new int[8];
        for (var i = 0; i < v.Length; i++) v[i] = BinaryPrimitives.ReadInt32LittleEndian(d[(i * 4)..]);
        return new FlEnvelope(v[2], v[3], v[4], v[5], Math.Clamp(v[6] / 128.0, 0, 1), v[7]);
    }

    /// <summary>
    /// An automation clip's points (event 234): a 21-byte header with the count at 17, then 24 bytes
    /// per point: f64 beats since the previous point, f64 value (0..1), f32 tension, u32 flags.
    /// Curves between points (tension) aren't kept; the points are joined by straight lines.
    /// </summary>
    static List<FlPoint> AutomationPoints(ReadOnlySpan<byte> d)
    {
        var points = new List<FlPoint>();
        if (d.Length < 21) return points;
        var count = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(d[17..]), (uint)(d.Length - 21) / 24);
        var beat = 0.0;
        for (var k = 0; k < count; k++)
        {
            var p = d[(21 + k * 24)..];
            beat += BinaryPrimitives.ReadDoubleLittleEndian(p);
            var value = BinaryPrimitives.ReadDoubleLittleEndian(p[8..]);
            if (!double.IsFinite(beat) || beat < 0 || !double.IsFinite(value)) break;
            points.Add(new FlPoint(beat, Math.Clamp(value, 0, 1)));
        }
        return points;
    }

    /// <summary>
    /// Automation clips placed where the playlist puts them, cut to each clip's length, joined per
    /// clip. A link (event 227) gives the clip's channel and its target: the low word is the control
    /// (0 volume, 1 pan, 4 pitch, 0x8000 + n plugin parameter n), the high word the channel it's on.
    /// Clips that aren't in the playlist, or are muted there, don't play, so they're left out. A clip placed several times,
    /// with gaps between, becomes one curve that holds its value through the gaps.
    /// </summary>
    static List<FlAutomation> PlaceAutomation(List<(int Clip, uint Target)> links, Dictionary<int, List<FlPoint>> curves,
                                              Dictionary<int, string> names, ReadOnlyMemory<byte>? playlist, int ppq)
    {
        var result = new List<FlAutomation>();
        if (playlist is not { } pl || ppq <= 0) return result;
        var items = PlaylistItems(pl.Span).Where(i => i.Index <= PatternBase).OrderBy(i => i.At).ToList();
        foreach (var (clip, target) in links)
        {
            if (!curves.TryGetValue(clip, out var curve) || curve.Count == 0) continue;
            var points = new List<FlPoint>();
            foreach (var item in items.Where(i => i.Index == clip && !i.Muted))
            {
                var placed = CutCurve(curve, item.At / (double)ppq, item.From / (double)ppq, item.Length / (double)ppq).ToList();
                // Between clips FL holds the value the last one ended on, then jumps to where the next starts.
                if (points.Count > 0 && placed.Count > 0 && points[^1].Beat < placed[0].Beat && points[^1].Value != placed[0].Value)
                    points.Add(points[^1] with { Beat = placed[0].Beat });
                points.AddRange(placed);
            }
            if (points.Count == 0) continue;
            var low = (int)(target & 0xFFFF);
            var control = low switch { 0 => FlControl.Volume, 1 => FlControl.Pan, 4 => FlControl.Pitch, >= 0x8000 => FlControl.PluginParameter, _ => FlControl.Other };
            result.Add(new FlAutomation(names.GetValueOrDefault(clip, $"Automation {clip + 1}"), (int)(target >> 16), control,
                                        control == FlControl.PluginParameter ? low - 0x8000 : low, points));
        }
        return result;
    }

    /// <summary>The part of a curve from <paramref name="from"/> to from + length (beats into the clip), moved to start at <paramref name="at"/>.</summary>
    static IEnumerable<FlPoint> CutCurve(List<FlPoint> curve, double at, double from, double length)
    {
        double ValueAt(double beat)
        {
            if (beat <= curve[0].Beat) return curve[0].Value;
            for (var k = 1; k < curve.Count; k++)
                if (beat <= curve[k].Beat)
                {
                    var (a, b) = (curve[k - 1], curve[k]);
                    return b.Beat <= a.Beat ? b.Value : a.Value + (b.Value - a.Value) * (beat - a.Beat) / (b.Beat - a.Beat);
                }
            return curve[^1].Value;
        }
        if (length <= 0) yield break;
        yield return new FlPoint(at, ValueAt(from));
        foreach (var p in curve)
            if (p.Beat > from && p.Beat < from + length) yield return p with { Beat = at + p.Beat - from };
        yield return new FlPoint(at + length, ValueAt(from + length));
    }

    static FlPlugin SongPlugin(string internalName, ReadOnlyMemory<byte> payload, string displayName)
    {
        if (!internalName.Equals("Fruity Wrapper", StringComparison.OrdinalIgnoreCase))
            return new FlPlugin(internalName, "Image-Line", "FL", "", null);

        var f = WrapperFields(payload.Span);
        var name = WrapperText(f, VstName) is { Length: > 0 } n ? n : TextScan.PluginNameFromPath(WrapperText(f, VstPath));
        var isVst3 = (f.TryGetValue(VstKind, out var k) && k.Length > 0 && k[0] == KindVst3) ||
                     WrapperText(f, VstPath).EndsWith(".vst3", StringComparison.OrdinalIgnoreCase);
        var id = isVst3
            ? f.TryGetValue(VstClassId, out var cid) && cid.Length == 16 ? VstPreset.ClassIdString(cid) : ""
            : f.TryGetValue(VstFourCc, out var cc) && cc.Length == 4 ? BinaryPrimitives.ReadInt32LittleEndian(cc).ToString() : "";
        PresetBlob? state = null;
        if (f.TryGetValue(VstState, out var raw) && raw.Length > 0)
        {
            state = WrapperPreset(f, raw, displayName.Length > 0 ? displayName : name, name);
            if (state.Extension is not (".fxp" or ".vstpreset")) state = null;   // raw states other DAWs can't load
        }
        return new FlPlugin(name, WrapperText(f, VstVendor), isVst3 ? "VST3" : "VST2", id, state, WrapperText(f, VstPath),
                            isVst3 && raw is { Length: > 0 } ? Vst3ParameterIds(raw) : null);
    }
}
