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

    /// <summary>An effect's slot in its mixer insert, from 0 at the top; -1 for an instrument, or when the file doesn't say.</summary>
    public int Slot { get; init; } = -1;

    /// <summary>False for an effect switched off in its mixer slot (its green light is off).</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// A channel in the Channel Rack: what plays it, which mixer insert it feeds, and the notes it plays across
/// the song (in song ticks, without muted notes, muted clips or clips on muted playlist tracks). <paramref name="RootKey"/> is the MIDI note the
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

    /// <summary>An audio clip channel's clips in the playlist, muted ones included, in order.</summary>
    public IReadOnlyList<FlAudioClip> AudioClips { get; init; } = [];

    /// <summary>How the channel stretches and pitches its sample (its time stretching knobs), and whether it plays it backwards.</summary>
    public FlStretch Stretch { get; init; } = new();

    /// <summary>
    /// False when the channel is switched off in the Channel Rack (its green light is off), so it plays nothing. A channel
    /// whose light is automated (<see cref="SwitchedBy"/>) counts as on: what it plays while it's off is muted instead.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The automation clips that switch the channel on and off (none when it isn't). Its clips that are off all the way through are
    /// muted, as are the notes in its other clips that start while it's off (FL's switch stops notes starting, not sound).
    /// </summary>
    public IReadOnlyList<string> SwitchedBy { get; init; } = [];
}

/// <summary>
/// An audio clip in the playlist: where it starts and how long it plays (song ticks), and where in the sample it
/// starts playing, in seconds of the sample as recorded (0 when it plays from the start).
/// </summary>
public sealed record FlAudioClip(long Start, long Length, double From = 0, bool Muted = false)
{
    /// <summary>The playlist track it's on, from 1 at the top; 0 when the file doesn't say.</summary>
    public int Track { get; init; }

    /// <summary>Whether that playlist track is muted, so the clip doesn't play even when it isn't <see cref="Muted"/> itself.</summary>
    public bool OnMutedTrack { get; init; }

    /// <summary>Whether automation switches its channel off part of the way through it (see <see cref="FlChannel.SwitchedBy"/>).</summary>
    public bool PartlyOff { get; init; }
}

/// <summary>
/// A channel's time stretching. The sample plays <paramref name="Multiplier"/> times as long (FL's Mul knob) and
/// <paramref name="PitchCents"/> higher (its Pitch knob); <paramref name="Time"/> (its Time knob, 0 when off) stretches it
/// to a length first, in FL's units of 768 a bar. <paramref name="Mode"/> is how: in <see cref="Resample"/> the
/// pitch and length change together, like speeding up a record; the other modes keep them apart.
/// </summary>
public sealed record FlStretch(double Multiplier = 1, int PitchCents = 0, int Mode = FlStretch.Resample, int Time = 0, bool Reversed = false)
{
    public const int Resample = 0;
    const double TimePerBeat = 192;

    /// <summary>How many seconds a sample <paramref name="seconds"/> long plays for at <paramref name="tempo"/>.</summary>
    public double PlayedSeconds(double seconds, double tempo)
    {
        var length = (Time > 0 && tempo > 0 ? Time / TimePerBeat * 60 / tempo : seconds) * Multiplier;
        return Mode == Resample ? length / Math.Pow(2, PitchCents / 1200.0) : length;
    }

    /// <summary>How many semitones higher than recorded a sample <paramref name="seconds"/> long sounds when it plays for <paramref name="played"/> seconds.</summary>
    public double Semitones(double seconds, double played) =>
        Mode == Resample ? (seconds > 0 && played > 0 ? -12 * Math.Log2(played / seconds) : PitchCents / 100.0) : PitchCents / 100.0;
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
public sealed record FlClip(string Name, long Start, long Length, IReadOnlyList<NoteEvent> Notes, bool Muted = false)
{
    /// <summary>The playlist track it's on, from 1 at the top; 0 when the file doesn't say or the song has no playlist.</summary>
    public int Track { get; init; }

    /// <summary>Whether that playlist track is muted, so the clip doesn't play even when it isn't <see cref="Muted"/> itself.</summary>
    public bool OnMutedTrack { get; init; }
}

/// <summary>A track (row) in FL's playlist, numbered from 1 at the top, with its name when it was given one.</summary>
public sealed record FlTrack(int Number, string Name)
{
    /// <summary>How a note names it: "playlist track 4", or "Vocals, playlist track 4" when it has a name.</summary>
    public string Label => Name.Length > 0 ? $"{Name}, playlist track {Number}" : $"playlist track {Number}";
}

/// <summary>
/// What an automation clip moves: a channel's volume, pan, pitch, on/off (its Channel Rack light) or a setting of its
/// plugin; a mixer insert's volume, pan, an effect slot's on/off or a setting of the effect in it; or the song's tempo
/// or master volume.
/// </summary>
public enum FlControl { Volume, Pan, Pitch, PluginParameter, Other, OnOff, InsertVolume, InsertPan, InsertSlotOnOff, InsertEffect, Tempo, MainVolume }

/// <summary>A point on an automation curve: beats from the start of the song, and the value (0..1).</summary>
public readonly record struct FlPoint(double Beat, double Value);

/// <summary>
/// An automation clip's curve across the song, as placed in the playlist. It moves <paramref name="Control"/>
/// on <paramref name="Channel"/> (-1 when it moves a mixer insert or the song); for a plugin parameter,
/// <paramref name="Parameter"/> is its index.
/// </summary>
public sealed record FlAutomation(string Name, int Channel, FlControl Control, int Parameter, IReadOnlyList<FlPoint> Points)
{
    /// <summary>The mixer insert it moves something on, or -1.</summary>
    public int Insert { get; init; } = -1;

    /// <summary>For an insert's effect: its slot (see <see cref="FlPlugin.Slot"/>), or -1.</summary>
    public int Slot { get; init; } = -1;

    /// <summary>The beats where each of its playlist clips starts.</summary>
    public IReadOnlyList<double> Starts { get; init; } = [];

    /// <summary>
    /// Where the playlist clip a point belongs to starts. Where clips moving one thing meet on a beat, the points of the
    /// one starting there go last, so it has the last word.
    /// </summary>
    public double StartOf(FlPoint point) => Starts.LastOrDefault(s => s <= point.Beat + 1e-4);
}

/// <summary>A mixer insert (0 is the master) and its effects in slot order.</summary>
public sealed record FlInsert(int Index, string Name, IReadOnlyList<FlPlugin> Effects)
{
    /// <summary>FL's fader value, when the file says: <see cref="FlStudioReader.InsertFaderDefault"/> where FL leaves it (how it maps to gain isn't worked out).</summary>
    public int? Fader { get; init; }

    /// <summary>FL's pan value, when the file says: 0 is the centre.</summary>
    public int? Pan { get; init; }

    /// <summary>Whether the fader or pan isn't where FL leaves it.</summary>
    public bool Moved => Fader is { } f && f != FlStudioReader.InsertFaderDefault || Pan is { } p && p != 0;
}

/// <summary>An FL Studio project laid out for moving to another DAW.</summary>
public sealed record FlSong(string Title, int Ppq, double Tempo, IReadOnlyList<FlChannel> Channels, IReadOnlyList<FlInsert> Inserts,
                           IReadOnlyList<FlAutomation> Automation)
{
    /// <summary>The playlist tracks that are muted and have something on them: their clips are <see cref="FlClip.OnMutedTrack"/>, and their automation is left out.</summary>
    public IReadOnlyList<FlTrack> MutedTracks { get; init; } = [];
}

public sealed partial class FlStudioReader
{
    const int EvChannelOn = 0;                  // BYTE: 0 when the channel is switched off in the Channel Rack, 1 when it plays
    const int EvChannelInsert = 22;             // BYTE: the mixer insert a channel is routed to (older versions)
    const int EvChannelFx = 70;                 // WORD: sample flags; 0x2 plays it backwards (PyFLP's reading, not yet seen in a save)
    const int EvChannelRoute = 104;             // WORD: the mixer insert a channel is routed to (seen from FL 20 to 25)
    const int EvRootNote = 135;                 // DWORD: the channel's root key as a MIDI note; absent when it's C5 (60)
    const int EvChannelParams = Data + 7;       // 215: the sampler's settings, its time stretching among them
    const int EvEnvelope = Data + 10;           // 218: an envelope and LFO, one each for pan, volume, mod X, mod Y, pitch
    const int EvChannelLevels = Data + 11;      // 219: i32 pan (0..12800), volume (0..12800), pitch in cents
    const int EvAutomationLink = Data + 19;     // 227: which control an automation clip moves
    const int EvAutomationPoints = Data + 26;   // 234: an automation clip's points
    const int EvMixerSettings = Data + 17;      // 225: the mixer's settings, 12 bytes each (see MixerSettings)
    const int ChannelTypeAutomation = 5;
    const double LevelsFull = 12800;

    // What an automation link (227) or a mixer setting (225) moves: the target's high word is a channel, a mixer insert's
    // slot (see MixerSettings) or the song, and its low word what on it. The insert and song ones match the ids FL's
    // scripting docs give; only channel volume, on/off and plugin settings have been seen in an automation link so far.
    const int TargetVolume = 0, TargetPan = 1, TargetPitch = 4, TargetOnOff = 7, TargetPlugin = 0x8000;
    const int TargetSlotOnOff = 0x1f00, TargetInsertVolume = 0x1fc0, TargetInsertPan = 0x1fc1;
    const int TargetSong = 0x4000, TargetMainVolume = 0, TargetTempo = 5;

    /// <summary>A mixer insert's fader where FL leaves it (its value in event 225).</summary>
    public const int InsertFaderDefault = 12800;

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
        var stretches = new Dictionary<int, FlStretch>();
        var reversed = new HashSet<int>();
        var off = new HashSet<int>();
        var curves = new Dictionary<int, List<FlPoint>>();
        var links = new List<(int Clip, uint Target)>();
        var rawNotes = new List<(int Pattern, ReadOnlyMemory<byte> Data)>();
        var patternNames = new Dictionary<int, string>();
        ReadOnlyMemory<byte>? playlist = null;
        var tracks = new PlaylistTracks();
        int? channel = null, pattern = null;
        var inMixer = false;
        var channelHasPlugin = false;
        var pendingInternal = "";
        var pluginName = "";
        var insert = -1;
        var nextInsertName = "";
        ReadOnlyMemory<byte>? mixerSettings = null;
        // The effect added last, until its slot's closing event says which slot it's in.
        (int Insert, int Index)? unslotted = null;

        foreach (var e in flp.Events)
        {
            tracks.Read(e, flp);
            switch (e.Id)
            {
                case EvTempo: tempo = e.Int / 1000.0; break;
                case EvTempoCoarse when tempo is null: tempo = e.Int; break;
                case EvTitle: title = flp.Str(e); break;
                case EvChannelNew:
                    channel = (int)e.Int; inMixer = false; channelHasPlugin = false;
                    if (!order.Contains(channel.Value)) order.Add(channel.Value);
                    break;
                case EvChannelOn when channel is not null && !inMixer:
                    if (e.Int == 0) off.Add(channel.Value); else off.Remove(channel.Value);
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
                case EvChannelParams when channel is not null && !inMixer && Stretch(e.Payload.Span) is { } stretch:
                    stretches[channel.Value] = stretch;
                    break;
                case EvChannelFx when channel is not null && !inMixer:
                    if ((e.Int & 0x2) != 0) reversed.Add(channel.Value); else reversed.Remove(channel.Value);
                    break;
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
                case EvMixerSlot:
                    inMixer = true;
                    // An effect's events come before the event that closes its slot, which holds the slot's number.
                    if (unslotted is var (atInsert, index) && effects.TryGetValue(atInsert, out var slotted))
                        slotted[index] = slotted[index] with { Slot = (int)e.Int };
                    unslotted = null;
                    break;
                case EvMixerSettings: mixerSettings = e.Payload; break;
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
                        unslotted = (insert, list.Count - 1);
                    }
                    pendingInternal = "";
                    break;
                case EvPatternNew: pattern = (int)e.Int; break;
                case EvPatternName when pattern is not null: patternNames[pattern.Value] = flp.Str(e); break;
                case EvPatternNotes when pattern is not null: rawNotes.Add((pattern.Value, e.Payload)); break;
                case EvPlaylist when playlist is null: playlist = e.Payload; break;
            }
        }

        var mixer = new MixerSettings(mixerSettings);
        // An automation clip switched off in the Channel Rack doesn't play, like a muted one.
        var automation = PlaceAutomation(links.Where(l => !off.Contains(l.Clip)).ToList(), curves, names, playlist, tracks, mixer, flp.Ppq);
        // A channel whose on/off is automated plays only while it's on. When several clips switch it, it follows each in turn:
        // where one clip ends and another starts, the one starting there has the last word.
        var switching = automation.Where(a => a.Control == FlControl.OnOff).GroupBy(a => a.Channel)
            .ToDictionary(g => g.Key, g => (Names: g.Select(a => a.Name).Distinct().ToList(),
                Points: g.SelectMany(a => a.Points.Select(p => (Point: p, From: a.StartOf(p))))
                         .OrderBy(x => Math.Round(x.Point.Beat * flp.Ppq)).ThenBy(x => x.From).Select(x => x.Point).ToList()));
        var clips = PlaceClips(flp, rawNotes, playlist, tracks, order.ToHashSet(), patternNames);
        var audioClips = PlaceAudioClips(playlist, tracks, order.Where(ch => types.GetValueOrDefault(ch) != ChannelTypeAutomation).ToHashSet());
        var channels = order
            .Where(ch => types.GetValueOrDefault(ch) != ChannelTypeAutomation)
            .Select(ch =>
            {
                var lv = levels.GetValueOrDefault(ch, (Pan: 0.5, Volume: DefaultVolume, Pitch: 0));
                var placed = clips.GetValueOrDefault(ch) ?? [];
                var placedAudio = audioClips.GetValueOrDefault(ch) ?? [];
                var switchedBy = switching.TryGetValue(ch, out var sw) ? sw : default;
                if (switchedBy.Names is not null)
                {
                    var onOff = new OnOff(switchedBy.Points, flp.Ppq);
                    placed = placed.Select(onOff.Mute).ToList();
                    placedAudio = placedAudio.Select(onOff.Mute).ToList();
                }
                return new FlChannel(ch, names.GetValueOrDefault(ch, $"Channel {ch + 1}"), instruments.GetValueOrDefault(ch),
                                     samples.GetValueOrDefault(ch, ""), routes.GetValueOrDefault(ch), Audible(placed),
                                     roots.GetValueOrDefault(ch, DefaultRoot), lv.Pitch, lv.Volume, lv.Pan)
                {
                    Clips = placed,
                    VolumeEnvelope = volumeEnvelopes.GetValueOrDefault(ch),
                    AudioClips = placedAudio,
                    Stretch = (stretches.GetValueOrDefault(ch) ?? new FlStretch()) with { Reversed = reversed.Contains(ch) },
                    Enabled = !off.Contains(ch) || switchedBy.Names is not null,
                    SwitchedBy = switchedBy.Names ?? [],
                };
            })
            .ToList();
        var mutedTracks = playlist is { } pl
            ? PlaylistItems(pl.Span).Select(i => tracks.At(i.Slot)).OfType<(FlTrack Track, bool Muted)>()
                .Where(t => t.Muted).Select(t => t.Track).Distinct().OrderBy(t => t.Number).ToList()
            : [];
        // An effect whose slot is switched on and off by automation stays on, as a channel whose light is automated does.
        var switchedSlots = automation.Where(a => a.Control == FlControl.InsertSlotOnOff).Select(a => (a.Insert, a.Slot)).ToHashSet();
        var inserts = effects.Keys.Union(insertNames.Keys).Union(channels.Select(c => c.Insert)).Distinct().Order()
            .Select(i => new FlInsert(i, insertNames.GetValueOrDefault(i, ""),
                                      (effects.GetValueOrDefault(i) ?? [])
                                          .Select(fx => mixer.Value(i, fx.Slot, TargetSlotOnOff) == 0 && !switchedSlots.Contains((i, fx.Slot)) ? fx with { Enabled = false } : fx)
                                          .ToList())
            {
                Fader = mixer.Value(i, 0, TargetInsertVolume),
                Pan = mixer.Value(i, 0, TargetInsertPan),
            })
            .ToList();
        return new FlSong(title, flp.Ppq, tempo ?? 140, channels, inserts, automation) { MutedTracks = mutedTracks };
    }

    /// <summary>A channel's levels (event 219): pan and volume as 0..1 knob positions, and pitch in cents.</summary>
    static (double Pan, double Volume, int Pitch)? Levels(ReadOnlySpan<byte> d) =>
        d.Length < 12 ? null
        : (Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(d) / LevelsFull, 0, 1),
           Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(d[4..]) / LevelsFull, 0, 1),
           Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(d[8..]), -4800, 4800));

    /// <summary>
    /// A channel's time stretching, from its sampler settings (event 215, 168 bytes in FL 25): i32 Time at 96, the
    /// pitch in cents at 100, the multiplier at 104 (2 to the power raw / 10000, so 10000 is twice as long) and the
    /// mode at 108 (-1 stretch, 0 resample, 1 and up FL's stretch engines). The multiplier, pitch and mode are checked
    /// against an FL 25 save; Time is PyFLP's reading.
    /// </summary>
    static FlStretch? Stretch(ReadOnlySpan<byte> d)
    {
        if (d.Length < 112) return null;
        var (time, cents, multiplier, mode) = (BinaryPrimitives.ReadInt32LittleEndian(d[96..]), BinaryPrimitives.ReadInt32LittleEndian(d[100..]),
                                               BinaryPrimitives.ReadInt32LittleEndian(d[104..]), BinaryPrimitives.ReadInt32LittleEndian(d[108..]));
        return new FlStretch(Math.Pow(2, Math.Clamp(multiplier, -100000, 100000) / 10000.0), Math.Clamp(cents, -4800, 4800), mode, Math.Max(time, 0));
    }

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
    /// clip. A link (event 227) gives the clip's channel and its target: what it moves (see TargetVolume and
    /// the rest; <see cref="MixerSettings"/> tells a mixer insert's targets from a channel's).
    /// Clips that aren't in the playlist, or are muted there or on a muted track, don't play, so they're left out. A clip
    /// placed several times, with gaps between, becomes one curve that holds its value through the gaps. An on/off
    /// switches rather than slides, so its curve holds each point's value until the next.
    /// </summary>
    static List<FlAutomation> PlaceAutomation(List<(int Clip, uint Target)> links, Dictionary<int, List<FlPoint>> curves,
                                              Dictionary<int, string> names, ReadOnlyMemory<byte>? playlist, PlaylistTracks tracks,
                                              MixerSettings mixer, int ppq)
    {
        var result = new List<FlAutomation>();
        if (playlist is not { } pl || ppq <= 0) return result;
        var items = PlaylistItems(pl.Span, tracks).Where(i => i.Index <= PatternBase).OrderBy(i => i.At).ToList();
        foreach (var (clip, target) in links)
        {
            if (!curves.TryGetValue(clip, out var curve) || curve.Count == 0) continue;
            var name = names.GetValueOrDefault(clip, $"Automation {clip + 1}");
            var (high, low) = ((int)(target >> 16), (int)(target & 0xFFFF));
            var plugin = low >= TargetPlugin ? low - TargetPlugin : low;
            FlAutomation Moves(IReadOnlyList<FlPoint> points) =>
                high == TargetSong
                    ? new(name, -1, low switch { TargetTempo => FlControl.Tempo, TargetMainVolume => FlControl.MainVolume, _ => FlControl.Other }, low, points)
                : mixer.Slot(high) is var (insert, slot)
                    ? new(name, -1, (low, slot) switch
                    {
                        (TargetInsertVolume, 0) => FlControl.InsertVolume,
                        (TargetInsertPan, 0) => FlControl.InsertPan,
                        (TargetSlotOnOff, _) => FlControl.InsertSlotOnOff,
                        ( >= TargetPlugin, _) => FlControl.InsertEffect,
                        _ => FlControl.Other,
                    }, plugin, points) { Insert = insert, Slot = slot }
                : new(name, high, low switch
                {
                    TargetVolume => FlControl.Volume,
                    TargetPan => FlControl.Pan,
                    TargetPitch => FlControl.Pitch,
                    TargetOnOff => FlControl.OnOff,
                    >= TargetPlugin => FlControl.PluginParameter,
                    _ => FlControl.Other,
                }, plugin, points);
            var hold = Moves([]).Control == FlControl.OnOff;
            var placed = new List<FlPoint>();
            var starts = new List<double>();
            foreach (var item in items.Where(i => i.Index == clip && !i.Muted))
            {
                var part = CutCurve(curve, item.At / (double)ppq, item.From / (double)ppq, item.Length / (double)ppq, hold).ToList();
                if (part.Count == 0) continue;
                starts.Add(part[0].Beat);
                // A clip placed over an earlier placement takes over from where it starts to where it ends; if the
                // earlier one runs on past that, it carries on from there.
                List<FlPoint> rest = [];
                if (placed.Count > 0 && placed[^1].Beat > part[0].Beat + SameBeat)
                {
                    if (placed[^1].Beat > part[^1].Beat + SameBeat)
                        rest = CutCurve(placed, part[^1].Beat, part[^1].Beat, placed[^1].Beat - part[^1].Beat, hold).ToList();
                    placed = CutCurve(placed, placed[0].Beat, placed[0].Beat, part[0].Beat - placed[0].Beat, hold).ToList();
                }
                // Between clips FL holds the value the last one ended on, then jumps to where the next starts.
                if (placed.Count > 0 && placed[^1].Beat < part[0].Beat - SameBeat && placed[^1].Value != part[0].Value)
                    placed.Add(placed[^1] with { Beat = part[0].Beat });
                // A clip straight after another starts where that one ended: the shared point is kept once.
                if (placed.Count > 0 && Math.Abs(placed[^1].Beat - part[0].Beat) <= SameBeat && placed[^1].Value == part[0].Value)
                    part.RemoveAt(0);
                placed.AddRange(part);
                if (rest.Count > 0 && rest[0].Value == placed[^1].Value) rest.RemoveAt(0);
                placed.AddRange(rest);
            }
            if (placed.Count > 0) result.Add(Moves(placed) with { Starts = starts });
        }
        return result;
    }

    /// <summary>How far apart two points can be and still count as at the same beat: FL adds up the gaps between points, which drifts.</summary>
    const double SameBeat = 1e-4;

    /// <summary>
    /// The part of a curve from <paramref name="from"/> to from + length (beats into the clip), moved to start at
    /// <paramref name="at"/>. Between points the value slides in a straight line, or with <paramref name="hold"/> stays
    /// at the earlier point's until the next. Where several points share a beat (a jump), the part starts from the
    /// value after the jump and ends on the value before it; a jump right at its end is kept as a second point there,
    /// so what's held after the clip is where the curve ends (FL saves a clip's last points at its very end).
    /// </summary>
    static IEnumerable<FlPoint> CutCurve(List<FlPoint> curve, double at, double from, double length, bool hold = false)
    {
        double ValueAt(double beat, bool arriving = false)
        {
            var first = curve.FindIndex(p => p.Beat >= beat - SameBeat);
            if (arriving && first >= 0 && curve[first].Beat <= beat + SameBeat) return curve[first].Value;
            var last = curve.FindLastIndex(p => p.Beat <= beat + SameBeat);
            if (last < 0) return curve[0].Value;
            var (a, next) = (curve[last], last + 1 < curve.Count ? curve[last + 1] : (FlPoint?)null);
            if (hold || next is not { } b || a.Beat >= beat - SameBeat) return a.Value;
            return a.Value + (b.Value - a.Value) * (beat - a.Beat) / (b.Beat - a.Beat);
        }
        if (length <= 0) yield break;
        var end = from + length;
        yield return new FlPoint(at, ValueAt(from));
        foreach (var p in curve)
            if (p.Beat > from + SameBeat && p.Beat < end - SameBeat) yield return p with { Beat = at + p.Beat - from };
        var arrived = ValueAt(end, arriving: true);
        yield return new FlPoint(at + length, arrived);
        if (ValueAt(end) is var after && after != arrived) yield return new FlPoint(at + length, after);
    }

    /// <summary>
    /// The mixer's settings (event 225): 12 bytes each, u32 0, u32 the setting's target (as an automation link names it) and
    /// i32 its value. A setting on a mixer insert has base + 64 × insert + slot in its target's high word (slot 0 for the
    /// insert's own fader and pan). The base is 0x2000 in FL 21 and 0x7000 in FL 25 and 26, so it's taken from the file: the
    /// lowest high word of an insert's fader.
    /// </summary>
    sealed class MixerSettings
    {
        readonly Dictionary<uint, int> _values = [];
        readonly int? _base;

        public MixerSettings(ReadOnlyMemory<byte>? data)
        {
            var d = data is { } m ? m.Span : [];
            for (var k = 0; k + 12 <= d.Length; k += 12)
                _values[BinaryPrimitives.ReadUInt32LittleEndian(d[(k + 4)..])] = BinaryPrimitives.ReadInt32LittleEndian(d[(k + 8)..]);
            var faders = _values.Keys.Where(t => (t & 0xFFFF) == TargetInsertVolume && t >> 16 != TargetSong).Select(t => (int)(t >> 16)).ToList();
            if (faders.Count > 0) _base = faders.Min();
        }

        /// <summary>A setting's value on an insert's slot, if the file has it.</summary>
        public int? Value(int insert, int slot, int setting) =>
            _base is { } b && slot >= 0 && _values.TryGetValue((uint)((b + 64 * insert + slot) << 16 | setting), out var v) ? v : null;

        /// <summary>The insert and slot a target's high word names, when it names one of the file's inserts.</summary>
        public (int Insert, int Slot)? Slot(int high) =>
            _base is { } b && high >= b && high != TargetSong && Value((high - b) / 64, 0, TargetInsertVolume) is not null
                ? ((high - b) / 64, (high - b) % 64) : null;
    }

    /// <summary>
    /// A channel's on/off (its Channel Rack light) as automation switches it: each point's value holds until the next,
    /// 0.5 and above is on, and before the first point it's at the first point's value.
    /// </summary>
    sealed class OnOff(IReadOnlyList<FlPoint> points, int ppq)
    {
        bool On(double beat)
        {
            var at = points[0];
            foreach (var p in points)
                if (p.Beat <= beat + SameBeat) at = p;
                else break;
            return at.Value >= 0.5;
        }

        /// <summary>Whether it's at <paramref name="on"/> all the way from one tick to another.</summary>
        bool Throughout(long from, long to, bool on)
        {
            var (a, b) = (from / (double)ppq, to / (double)ppq);
            return On(a) == on && points.All(p => p.Beat <= a + SameBeat || p.Beat >= b - SameBeat || p.Value >= 0.5 == on);
        }

        /// <summary>A clip muted when the channel is off all the way through it, else with the notes that start while it's off muted.</summary>
        public FlClip Mute(FlClip clip) =>
            Throughout(clip.Start, clip.Start + clip.Length, on: false) ? clip with { Muted = true }
            : clip with { Notes = clip.Notes.Select(n => n.Muted || On((clip.Start + n.Start) / (double)ppq) ? n : n with { Muted = true }).ToList() };

        /// <summary>An audio clip muted when the channel is off all the way through it, else marked when it's off part of the way.</summary>
        public FlAudioClip Mute(FlAudioClip clip) =>
            Throughout(clip.Start, clip.Start + clip.Length, on: false) ? clip with { Muted = true }
            : Throughout(clip.Start, clip.Start + clip.Length, on: true) ? clip : clip with { PartlyOff = true };
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
