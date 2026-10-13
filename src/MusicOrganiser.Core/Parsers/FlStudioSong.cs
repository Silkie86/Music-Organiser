using System.Buffers.Binary;

namespace MusicOrganiser.Core.Parsers;

/// <summary>A plugin in an FL Studio project, with what another DAW needs to load it.</summary>
/// <param name="Format">"VST2", "VST3", or "FL" for FL Studio's own plugins.</param>
/// <param name="DeviceId">How other DAWs identify the plugin: the VST2 unique id as a number, or the VST3 class id.</param>
/// <param name="State">The plugin's settings as a file the plugin can load (.fxp or .vstpreset), when there is one.</param>
public sealed record FlPlugin(string Name, string Vendor, string Format, string DeviceId, PresetBlob? State)
{
    public bool Native => Format == "FL";
}

/// <summary>A channel in the Channel Rack: what plays it, which mixer insert it feeds, and its notes across the song.</summary>
public sealed record FlChannel(int Index, string Name, FlPlugin? Instrument, string Sample, int Insert, IReadOnlyList<NoteEvent> Notes);

/// <summary>A mixer insert (0 is the master) and its effects in slot order.</summary>
public sealed record FlInsert(int Index, string Name, IReadOnlyList<FlPlugin> Effects);

/// <summary>An FL Studio project laid out for moving to another DAW.</summary>
public sealed record FlSong(string Title, int Ppq, double Tempo, IReadOnlyList<FlChannel> Channels, IReadOnlyList<FlInsert> Inserts);

public sealed partial class FlStudioReader
{
    const int EvChannelInsert = 22;             // BYTE: the mixer insert a channel is routed to

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
        var rawNotes = new List<(int Pattern, ReadOnlyMemory<byte> Data)>();
        ReadOnlyMemory<byte>? playlist = null;
        int? channel = null, pattern = null;
        var inMixer = false;
        var channelHasPlugin = false;
        var pendingInternal = "";
        var pluginName = "";
        var insert = -1;

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
                case EvChannelInsert when channel is not null && !inMixer: routes[channel.Value] = (int)e.Int; break;
                case EvChannelNameOld when channel is not null && !inMixer: names.TryAdd(channel.Value, flp.Str(e)); break;
                case EvSamplePath when channel is not null && !inMixer:
                    if (flp.Str(e) is { Length: > 0 } sample) samples[channel.Value] = sample;
                    break;
                case EvInsertFlags: inMixer = true; insert++; break;
                case EvInsertName when insert >= 0: insertNames[insert] = flp.Str(e); break;
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
                case EvPatternNotes when pattern is not null: rawNotes.Add((pattern.Value, e.Payload)); break;
                case EvPlaylist when playlist is null: playlist = e.Payload; break;
            }
        }

        var notes = PlaceNotes(flp, rawNotes, playlist, order.ToHashSet());
        var channels = order
            .Select(ch => new FlChannel(ch, names.GetValueOrDefault(ch, $"Channel {ch + 1}"), instruments.GetValueOrDefault(ch),
                                        samples.GetValueOrDefault(ch, ""), routes.GetValueOrDefault(ch),
                                        notes.TryGetValue(ch, out var n) ? n.OrderBy(x => x.Start).ThenBy(x => x.Key).ToList() : []))
            .ToList();
        var inserts = effects.Keys.Union(insertNames.Keys).Union(channels.Select(c => c.Insert)).Distinct().Order()
            .Select(i => new FlInsert(i, insertNames.GetValueOrDefault(i, ""), effects.GetValueOrDefault(i) ?? []))
            .ToList();
        return new FlSong(title, flp.Ppq, tempo ?? 140, channels, inserts);
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
        return new FlPlugin(name, WrapperText(f, VstVendor), isVst3 ? "VST3" : "VST2", id, state);
    }
}
