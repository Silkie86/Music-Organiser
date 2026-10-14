using System.IO.Compression;
using System.Security.Cryptography;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;
using static MusicOrganiser.Core.Parsers.BitwigFormat;

namespace MusicOrganiser.Core.Export;

/// <summary>
/// Writes an FL Studio song as a native Bitwig project (.bwproject), which carries more of the song
/// than DAWproject can: each Sampler's sample, root key and tune, a Note Transpose in front of
/// plugins whose FL root key was moved, muted notes and clips, and automation of volume, pan and
/// plugin settings.
///
/// Bitwig's format isn't published, so the project is built from a template Bitwig saved: one with
/// a Sampler track, a VST3 instrument track with a Note Transpose before the plugin and a clip of
/// notes, a VST2 instrument track and, for automation, a track with its volume automated. Those
/// tracks, devices, the clip and the lane are copied for each FL channel and filled in; the
/// template's own tracks, effect tracks and sends are left out.
///
/// Each channel that plays notes becomes a track on its own mixer channel, as Bitwig tracks are, with
/// a clip for each FL pattern clip it plays in and the VST effects of the FL insert it feeds after its
/// instrument; the master insert's effects go on the master. FL Studio's own plugins have no Bitwig
/// equivalent, so they're left out and listed.
/// </summary>
public static class BitwigProject
{
    /// <summary>What Bitwig records about a sample file: its size and MD5 (how it recognises the file) and its format.</summary>
    public sealed record SampleFile(long Bytes, byte[] Md5, int Rate, int Channels, double Seconds);

    /// <param name="Notes">What couldn't come across.</param>
    public sealed record Result(byte[] Bytes, IReadOnlyList<string> Notes);

    const string SamplerId = "468bc14b-b2e7-45a1-9666-e83117fe404e";
    const string NoteTransposeId = "0815cd9e-3a31-4429-a268-dabd952a3b68";
    const int Vst3 = 0x728, Vst2 = 0x1a5;                 // plugin device classes
    const int AudioEffect = 0, Instrument = 2;            // device kinds (field 0x9d)

    /// <summary>Reads what Bitwig records about a sample, or null when the file can't be read.</summary>
    public static SampleFile? ReadSample(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] md5;
            long bytes;
            using (var fs = File.OpenRead(path))
            {
                bytes = fs.Length;
                md5 = MD5.HashData(fs);
            }
            var audio = AudioFile.CanRead(path) ? AudioFile.Read(path, maxSeconds: 0.01) : null;
            return new SampleFile(bytes, md5, audio?.SampleRate ?? 44100, audio?.Channels.Length ?? 2, audio?.FileSeconds ?? 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    /// <param name="template">A project saved by Bitwig with the tracks described above.</param>
    /// <param name="readSample">Reads a sample's details; <see cref="ReadSample"/> unless given.</param>
    public static Result FromFl(FlSong song, byte[] template, Func<string, SampleFile?>? readSample = null)
    {
        readSample ??= ReadSample;
        var t = new Template(template);
        var notes = new List<string>();
        var states = new List<(string Name, byte[] Data)>();

        // A plugin as a Bitwig device: a copy of the template's VST3 or VST2 device with this plugin's identity and settings.
        BwObject? Plugin(FlPlugin p, int kind, List<string> skipped)
        {
            if (p.Native || p.Format is not ("VST2" or "VST3"))
            {
                skipped.Add($"{p.Name} (FL Studio plugin)");
                return null;
            }
            var vst3 = p.Format == "VST3";
            var device = t.Copy(vst3 ? t.Vst3Device : t.Vst2Device);
            var def = Def(device)!;
            var samePlugin = Equals(def[0xce4]?.ToString(), p.DeviceId);
            def.Set(0x9a, p.Name);
            def.Set(0x9b, p.Vendor);
            def.Set(0x12de, "");
            def.Set(0x9d, (byte)kind);
            if (kind == AudioEffect)
            {
                def.Set(0x9c, "Audio FX");
                if (def.Has(0x1809)) def.Set(0x1809, (byte)0, 0x05);   // as Bitwig saves effect plugins
            }
            if (vst3) def.Set(0xce4, p.DeviceId);
            else if (int.TryParse(p.DeviceId, out var id)) def.Set(0xce4, id, IntType(id));
            if (!samePlugin)
            {
                // Another plugin than the template's: its version and outputs aren't known, Bitwig reads them on load.
                if (def.Has(0xc5e)) def.Set(0xc5e, vst3 ? "" : 0, vst3 ? (byte)0x08 : (byte)0x01);
                def.Obj(0x137c)?.Set(0x8e0, new List<object?>());
                if (def.Obj(0x1b75) is { } program) program.Set(0x1b72, "");
            }
            def.Set(0x1318, new List<object?>());   // the template's automated settings; this plugin's are added with their lanes
            var location = def.Obj(0xc5a)?[0xc5b] as List<object?>;
            if (location is { Count: > 0 } && def.Obj(0xc5a)!.Resolve(location[0]) is { } file && p.Path.Length > 0)
            {
                file.Set(0x3b, p.Path);
                location.RemoveRange(1, location.Count - 1);
            }
            else location?.Clear();

            var state = "";
            if (p.State is { } s)
            {
                var (data, extension) = !vst3 && s.Extension == ".fxp" ? (Fxp.ToBank(s.Data), ".fxb") : (s.Data, s.Extension);
                state = $"{Guid.NewGuid()}{extension}";
                states.Add((state, data));
            }
            else skipped.Add($"{p.Name}'s settings (opens at its defaults)");
            def.Set(0xbe5, state);
            return device;
        }

        // FL inserts' VST effects as devices; an insert's effects are copied onto every track that feeds it.
        List<BwObject> Effects(FlInsert? insert, List<string> skipped) =>
            (insert?.Effects ?? []).Select(e => Plugin(e, AudioEffect, skipped)).OfType<BwObject>().ToList();

        var automated = song.Automation.Select(a => a.Channel).ToHashSet();
        var playing = song.Channels.Where(c => (c.Notes.Count > 0 || c.Clips.Count > 0 || automated.Contains(c.Index)) &&
                                               (c.Instrument is not null || c.Sample.Length > 0)).ToList();
        var inserts = song.Inserts.ToDictionary(i => i.Index);
        var shared = playing.Where(c => c.Insert > 0).GroupBy(c => c.Insert).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var tracks = new List<BwObject>();
        var instruments = new Dictionary<int, (BwObject Device, FlPlugin Plugin)>();
        foreach (var c in playing)
        {
            var skipped = new List<string>();
            var devices = new List<BwObject>();
            BwObject track;
            Func<int, long?>? sampleLength = null;
            if (c.Instrument is null)
            {
                track = t.Copy(t.SamplerTrack);
                var info = readSample(c.Sample);
                devices.Add(Sampler(Chain(track).Single(), c, info, notes));
                if (info is { Seconds: > 0 })
                    sampleLength = key => SampleTicks(info.Seconds, key - c.RootKey + c.PitchCents / 100.0, song.Tempo, song.Ppq);
            }
            else
            {
                track = t.Copy(c.Instrument.Format == "VST2" ? t.Vst2Track : t.Vst3Track);
                if (Plugin(c.Instrument, Instrument, skipped) is { } plugin)
                {
                    instruments[c.Index] = (plugin, c.Instrument);
                    // FL plays a plugin's root key as C5, so a moved root key transposes it, as does the pitch knob.
                    var shift = FlStudioReader.DefaultRoot - c.RootKey + (int)Math.Round(c.PitchCents / 100.0);
                    if (shift != 0) devices.Add(Transpose(t.Copy(t.NoteTranspose), shift));
                    if (c.PitchCents % 100 != 0)
                        notes.Add($"{c.Name}: its pitch knob ({c.PitchCents / 100.0:+0.##;-0.##} semitones) is rounded to whole semitones");
                    devices.Add(plugin);
                }
            }
            if (c.Insert > 0 && inserts.TryGetValue(c.Insert, out var insert))
            {
                var effects = Effects(insert, skipped);
                devices.AddRange(effects);
                if (effects.Count > 0 && shared.Contains(c.Insert))
                    notes.Add($"{c.Name}: has its own copy of {InsertName(insert)}'s effects, which other channels also feed in FL Studio");
            }
            Chain(track, devices);
            track.Set(0x15b, c.Name);
            Mixer(track, c);
            foreach (var clip in FlClips.Of(c, song.Ppq))
                t.AddClip(track, clip with { Notes = FlClips.Lengthen(clip.Notes, clip.Length, sampleLength) }, song.Ppq);
            tracks.Add(track);
            if (skipped.Count > 0) notes.Add($"{c.Name}: left out {string.Join(", ", skipped)}");
            if (c.VolumeEnvelope is not null) notes.Add($"{c.Name}: {DawProject.EnvelopeLeftOut}");
        }

        // The master insert's effects on Bitwig's master.
        var masterSkipped = new List<string>();
        var masterEffects = Effects(inserts.GetValueOrDefault(0), masterSkipped);
        if (masterEffects.Count > 0) Chain(t.Master, [.. Chain(t.Master), .. masterEffects]);
        if (masterSkipped.Count > 0) notes.Add($"Master: left out {string.Join(", ", masterSkipped)}");

        // Automation clips as automation lanes on the track they move: its volume, pan or a setting of its plugin.
        var placed = playing.Zip(tracks).ToDictionary(p => p.First.Index, p => (Channel: p.First, Track: p.Second));
        foreach (var a in song.Automation)
        {
            if (!placed.TryGetValue(a.Channel, out var on))
            {
                notes.Add($"{a.Name}: automation left out (what it moves isn't converted)");
                continue;
            }
            const string noLane = "the Bitwig template has no automation lane to copy";
            var track = on.Track;
            string? why = null;
            switch (a.Control)
            {
                case FlControl.Volume or FlControl.Pan:
                    var pan = a.Control == FlControl.Pan;
                    Func<double, double> value = pan ? v => Math.Clamp((v - 0.5) * 2, -1, 1) : v => Math.Cbrt(DawProject.Gain(v));
                    if (track.Obj(0x165)?.Obj(pan ? 0x1a5 : 0x1a4) is not { } param) why = "this track has no fader";
                    else if (!t.AddMixerAutomation(track, param, pan ? "Pan" : "Volume", a.Points.Select(p => (p.Beat, value(p.Value)))))
                        why = noLane;
                    break;
                case FlControl.PluginParameter when instruments.TryGetValue(a.Channel, out var instrument):
                    if (SettingPath(instrument.Plugin, a.Parameter) is not { } setting)
                        why = "FL didn't save which of the plugin's settings it moves";
                    else if (!t.AddPluginAutomation(track, instrument.Device, setting.Path, setting.Id, SettingName(a.Name, on.Channel.Name),
                                                    a.Points.Select(p => (p.Beat, p.Value))))
                        why = noLane;
                    break;
                case FlControl.PluginParameter:
                    why = "its plugin isn't converted";
                    break;
                default:
                    why = a.Control == FlControl.Pitch ? "pitch has no equivalent here" : "this control has no equivalent here";
                    break;
            }
            if (why is not null) notes.Add($"{a.Name}: automation left out ({why})");
        }

        return new Result(t.Finish(tracks, song.Tempo, states), notes);
    }

    /// <summary>
    /// How Bitwig names a plugin's setting: a VST2 setting by its number, which FL uses too; a VST3 one by
    /// the plugin's own id for it, from the list FL keeps (FL automates setting n, Bitwig id n of that list).
    /// </summary>
    static (string Path, long Id)? SettingPath(FlPlugin plugin, int setting) => plugin.Format switch
    {
        "VST2" => ($"CONTENTS/ROOT_GENERIC_MODULE/PARAM{setting}", setting),
        "VST3" when plugin.ParameterIds is { } ids && setting >= 0 && setting < ids.Count =>
            ($"CONTENTS/ROOT_GENERIC_MODULE/PID{ids[setting]:x}", ids[setting]),
        _ => null,
    };

    /// <summary>FL names an automation clip "channel - setting".</summary>
    static string SettingName(string clip, string channel) =>
        clip.StartsWith(channel + " - ", StringComparison.Ordinal) ? clip[(channel.Length + 3)..] : clip;

    static string InsertName(FlInsert insert) => insert.Name.Length > 0 ? insert.Name : $"Insert {insert.Index}";

    /// <summary>Fills in a Sampler: its sample, root key and tune (FL's pitch knob, in semitones).</summary>
    static BwObject Sampler(BwObject sampler, FlChannel c, SampleFile? info, List<string> notes)
    {
        var def = Def(sampler)!;
        var fileName = Path.GetFileName(c.Sample.Replace('\\', '/'));
        def.Set(0x12de, Path.GetFileNameWithoutExtension(fileName));
        sampler.Set(0x199, Path.GetFileNameWithoutExtension(fileName));
        Param(def, "TRANSPOSE")?.Set(0x136, c.PitchCents / 100.0);

        var zone = Param(def, "SAMPLE")?.Obj(0x74c);
        var sample = zone?.Obj(0x4112)?.Obj(0x748);
        var file = sample?.Obj(0x129e);
        if (zone is null || sample is null || file is null) throw new InvalidDataException("The template's Sampler has no sample in it");
        zone.Set(0x75c, (byte)Math.Clamp(c.RootKey, 0, 127), 0x01);
        sample.Set(0x129f, fileName);
        file.Set(0x512, fileName);
        file.Obj(0xd3b)?.Set(0x3b, c.Sample);
        file.Obj(0x513)?.Set(0x16, info?.Md5 ?? new byte[16], 0x0d);
        if (info is null)
        {
            notes.Add($"{c.Name}: couldn't read {c.Sample}, so Bitwig will ask where it is");
            return sampler;
        }
        file.Set(0x17, info.Bytes, IntType(info.Bytes));
        sample.Set(0x94, info.Rate, IntType(info.Rate));
        sample.Set(0x95, info.Seconds);
        sample.Set(0x96, (byte)info.Channels, 0x01);
        zone.Set(0x75f, info.Seconds);
        zone.Set(0x761, info.Seconds);
        return sampler;
    }

    /// <summary>
    /// Sets a Note Transpose to shift notes by <paramref name="semitones"/>: whole octaves (up to 3) and the
    /// rest in semitones the same way, so -4 is 4 semitones down rather than an octave down and 8 up.
    /// </summary>
    static BwObject Transpose(BwObject device, int semitones)
    {
        var def = Def(device)!;
        var octaves = Math.Clamp(semitones / 12, -3, 3);
        var rest = Math.Clamp(semitones - octaves * 12, -48, 48);
        Param(def, "OCTAVES")?.Set(0x273, (byte)(octaves + 3), 0x01);
        Param(def, "COARSE")?.Set(0x330, unchecked((byte)(sbyte)rest), 0x01);   // a signed byte: -4 is 252
        return device;
    }

    /// <summary>How long, in ticks, a sample of <paramref name="seconds"/> plays when pitched <paramref name="semitones"/> up.</summary>
    static long SampleTicks(double seconds, double semitones, double tempo, int ppq) =>
        (long)Math.Ceiling(seconds / Math.Pow(2, semitones / 12) * tempo / 60 * ppq - 1e-6);

    /// <summary>Channel volume and pan on the track's mixer. Bitwig's fader holds the cube root of the gain.</summary>
    static void Mixer(BwObject track, FlChannel c)
    {
        var mixer = track.Obj(0x165);
        mixer?.Obj(0x1a4)?.Set(0x2c8, Math.Cbrt(DawProject.Gain(c.Volume)));
        mixer?.Obj(0x1a5)?.Set(0x2c8, Math.Clamp((c.Pan - 0.5) * 2, -1, 1));
    }

    static BwObject? Def(BwObject device) => device.Obj(0x197);

    /// <summary>A device's named setting, from its CONTENTS list.</summary>
    static BwObject? Param(BwObject def, string name) =>
        def.Obj(0xa4)?.List(0x20c).FirstOrDefault(p => p.Str(0x2b9) == name);

    static IEnumerable<BwObject> Chain(BwObject track) => track.Obj(0x164)?.List(0x144) ?? [];

    static void Chain(BwObject track, IEnumerable<BwObject> devices) =>
        track.Obj(0x164)?.Set(0x144, devices.Select(d => (object?)new BwLink(d)).ToList());

    static string BitwigId(BwObject device) => Def(device) is { Class: 0x40 } def ? def[0x99]?.ToString() ?? "" : "";

    static int Kind(BwObject device) => (int)(Def(device)?.Num(0x9d) ?? -1);

    /// <summary>The smallest integer type that holds a value, as Bitwig writes integers.</summary>
    static byte IntType(long v) => v switch
    {
        >= 0 and <= byte.MaxValue => 0x01,
        >= short.MinValue and <= short.MaxValue => 0x02,
        >= int.MinValue and <= int.MaxValue => 0x03,
        _ => 0x04,
    };

    /// <summary>The template, with its tracks taken out and kept as patterns to copy.</summary>
    sealed class Template
    {
        readonly Document _doc;
        readonly BwObject _group;
        readonly BwObject _clip, _keyLane, _note;
        readonly BwObject? _mixerLane, _pluginLane, _point, _setting;
        readonly Copier _copier;
        public BwObject SamplerTrack { get; }
        public BwObject Vst3Track { get; }
        public BwObject Vst2Track { get; }
        public BwObject Vst3Device { get; }
        public BwObject Vst2Device { get; }
        public BwObject NoteTranspose { get; }
        public BwObject Master { get; }

        public Template(byte[] raw)
        {
            _doc = Decode(raw);
            if (_doc.Root is not { } root || _doc.BodyError is not null)
                throw new InvalidDataException($"The Bitwig template couldn't be read ({_doc.BodyError ?? "no project in it"})");
            _group = root.Obj(0x4dd) ?? throw new InvalidDataException("The Bitwig template has no tracks");
            Master = _group.Obj(0x4e0) ?? throw new InvalidDataException("The Bitwig template has no master track");
            var originals = _group.List(0x4de).Concat(_group.List(0x4df)).ToList();

            BwObject? Find(Func<BwObject, bool> instrument) => originals.FirstOrDefault(tr => Chain(tr).Any(instrument));
            bool Vst(BwObject d, int cls) => Def(d)?.Class == cls && Kind(d) == Instrument;
            var missing = new List<string>();
            T Need<T>(T? found, string what) where T : class
            {
                if (found is null) missing.Add(what);
                return found!;
            }
            SamplerTrack = Need(Find(d => BitwigId(d) == SamplerId), "a Sampler track");
            Vst3Track = Need(Find(d => Vst(d, Vst3)), "a VST3 instrument track");
            Vst2Track = Need(Find(d => Vst(d, Vst2)), "a VST2 instrument track");
            NoteTranspose = Need(originals.SelectMany(Chain).FirstOrDefault(d => BitwigId(d) == NoteTransposeId), "a Note Transpose");
            _clip = Need(originals.SelectMany(Clips).FirstOrDefault(c => KeyLanes(c).Any(l => l.List(0x21f).Any())), "a clip with notes");
            if (missing.Count > 0)
                throw new InvalidDataException($"The Bitwig template needs {string.Join(", ", missing)}");
            Vst3Device = Chain(Vst3Track).First(d => Vst(d, Vst3));
            Vst2Device = Chain(Vst2Track).First(d => Vst(d, Vst2));
            _keyLane = KeyLanes(_clip).First(l => l.List(0x21f).Any());
            _note = _keyLane.List(0x21f).First();
            // Automation lanes on a mixer setting and on a plugin's setting, and a point, if the template has them;
            // and how a plugin lists a setting it has automated.
            var lanes = originals.SelectMany(tr => tr.Obj(0x2dc9)?.List(0x4ba) ?? []).Where(l => l.Obj(0x2d8c)?.Obj(0x35ac) is not null).ToList();
            _mixerLane = lanes.FirstOrDefault(l => l.Obj(0x2a3)?.Has(0x41) == true);
            _pluginLane = lanes.FirstOrDefault(l => l.Obj(0x2a3) is { } target && target.Has(0xec) && target.Has(0xed));
            _point = lanes.SelectMany(Reachable).FirstOrDefault(o => o.Has(0x2af) && o.Has(0x28f));
            if (_point is null) _mixerLane = _pluginLane = null;
            _setting = new[] { Vst3Device, Vst2Device }.SelectMany(d => Def(d)!.List(0x1318)).FirstOrDefault(o => o.Has(0x1319));

            // Take the template's tracks out of the project, with what points into them: the per-user
            // view state (selected track, editors), clip selections, sends and effect tracks. They stay
            // as patterns with only their instrument, no clips and no automation lanes.
            if (root.Has(0x4d)) root.Set(0x4d, new Dictionary<string, object?>(StringComparer.Ordinal));
            _group.Obj(0x2bfb)?.Obj(0x2d8b)?.Set(0x21f, new List<object?>());
            _group.Set(0x4de, new List<object?>());
            _group.Set(0x4df, new List<object?>());
            foreach (var tr in originals)
            {
                if (tr.Has(0x168)) tr.Set(0x168, new Dictionary<string, object?>(StringComparer.Ordinal));
                tr.Obj(0x165)?.Set(0xa5, new List<object?>());
                tr.Obj(0x2dc9)?.Set(0x4ba, new List<object?>());
                tr.Obj(0x2dc9)?.Set(0x3f83, new List<object?>());
                tr.Obj(0x2aed)?.Set(0x10e6, new List<object?>());
                tr.Obj(0x2bf8)?.Obj(0x2d8e)?.Set(0x21f, new List<object?>());
            }
            Chain(SamplerTrack, Chain(SamplerTrack).Where(d => BitwigId(d) == SamplerId).Take(1).ToList());
            Chain(Vst3Track, [Vst3Device]);
            Chain(Vst2Track, [Vst2Device]);
            // Sends to effect tracks inside devices (a multi-out plugin's output chains) go with the effect tracks.
            foreach (var o in Reachable(SamplerTrack).Concat(Reachable(Vst3Track)).Concat(Reachable(Vst2Track)))
                foreach (var (_, v) in o.Fields)
                    if (v is List<object?> list)
                        list.RemoveAll(x => o.Resolve(x)?.Str(0x2b9).StartsWith("TRACK_SEND(", StringComparison.Ordinal) == true);
            _keyLane.Set(0x21f, new List<object?>());
            NoteContent(_clip)!.Set(0x18cb, new List<object?>());
            foreach (var lane in new[] { _mixerLane, _pluginLane }.OfType<BwObject>())
            {
                // No clips, points or per-user state, and nothing to move until it's given something.
                var arrangement = lane.Obj(0x2d8c)!;
                arrangement.Set(0x21f, new List<object?>());
                arrangement.Obj(0x35ac)!.Set(0x21f, new List<object?>());
                lane.Obj(0x2aec)?.Set(0x10e6, new List<object?>());
                if (lane.Has(0x4bc)) lane.Set(0x4bc, new Dictionary<string, object?>(StringComparer.Ordinal));
                lane.Obj(0x2a3)!.Set(lane == _mixerLane ? 0x41 : 0xec, null, 0x0a);
            }

            _copier = new Copier(_doc);
            if (new[] { SamplerTrack, Vst3Track, Vst2Track, NoteTranspose, _clip, _mixerLane, _pluginLane, _point, _setting }
                    .OfType<BwObject>().Any(_copier.Shares))
                throw new InvalidDataException("The Bitwig template refers to its tracks from somewhere this app doesn't know to clear");
        }

        public BwObject Copy(BwObject pattern) => _copier.Copy(pattern);

        static IEnumerable<BwObject> Clips(BwObject track) => track.Obj(0x2bf8)?.Obj(0x2d8e)?.List(0x21f) ?? [];
        static BwObject? NoteContent(BwObject clip) => clip.Obj(0x2ae9)?.Obj(0x10e9)?.Obj(0x49c);
        static IEnumerable<BwObject> KeyLanes(BwObject clip) => NoteContent(clip)?.List(0x18cb) ?? [];

        /// <summary>
        /// One of a channel's FL clips as an arrangement clip, muted when it is in FL. Notes sit in one lane
        /// per key, highest first, as Bitwig saves them, and are muted as in FL's piano roll.
        /// </summary>
        public void AddClip(BwObject track, FlClip fl, int ppq)
        {
            double Beats(long ticks) => ticks / (double)ppq;
            var length = Beats(fl.Length);
            var clip = Copy(_clip);
            clip.Set(0x2af, Beats(fl.Start));
            clip.Set(0x26, length);
            if (fl.Muted || clip.Has(0x10f8)) clip.Set(0x10f8, (byte)(fl.Muted ? 1 : 0), 0x05);
            var content = clip.Obj(0x2ae9)!;
            // The loop and play range are timed from the clip's start.
            if (clip.Obj(0x2adb) is { } loop)
            {
                loop.Obj(0x992)?.Set(0x2af, 0.0);
                loop.Obj(0x992)?.Set(0x26, length);
                loop.Obj(0x98c)?.Set(0x2af, 0.0);
                loop.Obj(0x98d)?.Set(0x2af, length);
            }
            var lanes = new List<object?>();
            foreach (var key in fl.Notes.GroupBy(n => Math.Clamp(n.Key, 0, 127)).OrderByDescending(g => g.Key))
            {
                var lane = Copy(_keyLane);
                lane.Set(0xee, (byte)key.Key, 0x01);
                lane.Set(0x21f, key.OrderBy(n => n.Start).Select(n =>
                {
                    var note = Copy(_note);
                    var velocity = Math.Clamp(n.Velocity / 127.0, 0, 1);
                    note.Set(0x2af, Beats(n.Start));
                    note.Set(0x26, Beats(n.Length));
                    if (n.Muted || note.Has(0x10f8)) note.Set(0x10f8, (byte)(n.Muted ? 1 : 0), 0x05);
                    note.Set(0xef, velocity);
                    note.Set(0xf0, velocity);
                    return (object?)note;
                }).ToList());
                lanes.Add(lane);
            }
            NoteContent(clip)!.Set(0x18cb, lanes);

            var arrangement = track.Obj(0x2bf8)!.Obj(0x2d8e)!;
            ((List<object?>)arrangement[0x21f]!).Add(clip);
            // The track lists the contents of its clips.
            if (track.Obj(0x2aed)?[0x10e6] is List<object?> contents && content.Obj(0x10e9) is { } c) contents.Add(new BwLink(c));
        }

        /// <summary>Automates the track's volume or pan fader (<paramref name="param"/>), with values in Bitwig's units.</summary>
        public bool AddMixerAutomation(BwObject track, BwObject param, string name, IEnumerable<(double Beat, double Value)> points) =>
            AddLane(track, _mixerLane, target => ReferenceEquals(target.Obj(0x41), param), target =>
            {
                target.Set(0x41, new BwLink(param), 0x09);
                target.Set(0x133a, name);
            }, points);

        /// <summary>
        /// Automates a plugin's setting, named by its path in the plugin (<see cref="SettingPath"/>), with values
        /// from 0 to 1. The plugin also lists the setting, as Bitwig saves an automated one.
        /// </summary>
        public bool AddPluginAutomation(BwObject track, BwObject device, string path, long id, string name,
                                        IEnumerable<(double Beat, double Value)> points)
        {
            var list = points.ToList();
            var added = AddLane(track, _pluginLane, target => ReferenceEquals(target.Obj(0xec), device) && target.Str(0xed) == path, target =>
            {
                target.Set(0xec, new BwLink(device), 0x09);
                target.Set(0xed, path);
                target.Set(0x133b, Def(device)!.Str(0x9a));
                target.Set(0x133a, name);
            }, list);
            if (added && _setting is not null && Def(device)?[0x1318] is List<object?> settings &&
                !settings.Select(Def(device)!.Resolve).Any(o => o?.Num(0x1319) == id))
            {
                var setting = Copy(_setting);
                setting.Set(0x1319, id, IntType(id));
                if (setting.Has(0x131a) && list.Count > 0) setting.Set(0x131a, list[0].Value);
                settings.Add(setting);
            }
            return added;
        }

        /// <summary>
        /// Adds points, joined by straight lines, to the track's lane for a setting, making the lane from
        /// <paramref name="pattern"/> if the track has none yet; several FL clips moving one setting share it.
        /// False when the template has no lane to copy.
        /// </summary>
        bool AddLane(BwObject track, BwObject? pattern, Func<BwObject, bool> moves, Action<BwObject> setUp,
                     IEnumerable<(double Beat, double Value)> points)
        {
            if (pattern is null || _point is null || track.Obj(0x2dc9)?[0x4ba] is not List<object?> lanes) return false;
            var lane = lanes.Select(track.Resolve).OfType<BwObject>().FirstOrDefault(l => l.Obj(0x2a3) is { } target && moves(target));
            if (lane is null)
            {
                lane = Copy(pattern);
                setUp(lane.Obj(0x2a3)!);
                lanes.Add(new BwLink(lane));
            }
            var list = (List<object?>)lane.Obj(0x2d8c)!.Obj(0x35ac)![0x21f]!;
            list.AddRange(points.Select(p =>
            {
                var point = Copy(_point);
                point.Set(0x2af, p.Beat);
                point.Set(0x28f, p.Value);
                if (point.Has(0x359e)) point.Set(0x359e, (byte)1, 0x01);
                if (point.Has(0xba6)) point.Set(0xba6, 0.0);   // a straight line to the next point
                return (object?)point;
            }));
            var sorted = list.OrderBy(x => lane.Resolve(x)!.Num(0x2af)).ToList();
            list.Clear();
            list.AddRange(sorted);
            return true;
        }

        /// <summary>Puts the new tracks in the project and writes it, with the plugin settings in its zip.</summary>
        public byte[] Finish(List<BwObject> tracks, double tempo, List<(string Name, byte[] Data)> states)
        {
            _group.Set(0x4de, tracks.Select(tr => (object?)tr).ToList());
            foreach (var o in Reachable(_doc.Root!))
                if (o.Str(0x2bd) == "TEMPO" && o.Has(0x2c8)) o.Set(0x2c8, tempo);

            // The browser's summary of the project lists its tracks by name.
            if (_doc.Meta.GetValueOrDefault("structure") is BwBlob blob)
            {
                var summary = Decode(_doc.Raw[blob.Offset..(blob.Offset + blob.Length)]);
                var project = summary.Root?.Obj(0xad2);
                var trackEntry = project?.List(0xace).FirstOrDefault();
                if (summary.BodyError is null && project is not null && trackEntry is not null)
                {
                    var copier = new Copier(summary);
                    var entries = new List<object?>();
                    project.Set(0xace, entries);
                    project.Set(0xacf, new List<object?>());
                    foreach (var tr in tracks)
                    {
                        var e = copier.Copy(trackEntry);
                        e.Set(0xad1, tr.Str(0x15b));
                        entries.Add(e);
                    }
                    _doc.Meta["structure"] = Encode(summary);
                }
            }

            using var zip = new MemoryStream();
            using (var z = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, data) in states)
                {
                    using var s = z.CreateEntry($"plugin-states/{name}", CompressionLevel.Optimal).Open();
                    s.Write(data);
                }
            return Encode(_doc, states.Count > 0 ? zip.ToArray() : []);
        }

        static IEnumerable<BwObject> Reachable(BwObject root)
        {
            var seen = new HashSet<BwObject>(ReferenceEqualityComparer.Instance);
            var todo = new Stack<BwObject>([root]);
            while (todo.Count > 0)
            {
                var o = todo.Pop();
                if (!seen.Add(o)) continue;
                yield return o;
                foreach (var (_, v) in o.Fields)
                    foreach (var x in Values(v))
                        if (o.Resolve(x) is { } next) todo.Push(next);
            }
        }
    }
}
