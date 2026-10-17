using System.IO.Compression;
using System.Security.Cryptography;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Plugins.Serum;
using MusicOrganiser.Core.Sounds;
using static MusicOrganiser.Core.Parsers.BitwigFormat;

namespace MusicOrganiser.Core.Export;

/// <summary>
/// Writes an FL Studio song as a native Bitwig project (.bwproject), which carries more of the song
/// than DAWproject can: each Sampler's sample, root key and tune, a Note Transpose in front of
/// plugins whose FL root key was moved, muted notes and clips, audio clips stretched and pitched as
/// in FL, and automation of volume, pan and plugin settings.
///
/// Bitwig's format isn't published, so the project is built from a template Bitwig saved: one with
/// a Sampler track, a VST3 instrument track with a Note Transpose before the plugin and a clip of
/// notes, a VST2 instrument track and, for automation, a track with its volume automated; for audio
/// clips, an audio track with an audio clip on it. Those tracks, devices, clips and the lane are
/// copied for each FL channel and filled in; the template's own tracks, effect tracks and sends are
/// left out.
///
/// Each channel that plays notes becomes a track on its own mixer channel, as Bitwig tracks are, with
/// a clip for each FL pattern clip it plays in and the VST effects of the FL insert it feeds after its
/// instrument; an audio clip channel's clips in the playlist go on an audio track like it. The master
/// insert's effects go on the master. FL Studio's own plugins have no Bitwig equivalent, so they're
/// left out and listed.
/// </summary>
public static class BitwigProject
{
    /// <summary>What Bitwig records about a sample file: its size and MD5 (how it recognises the file) and its format.</summary>
    public sealed record SampleFile(long Bytes, byte[] Md5, int Rate, int Channels, double Seconds);

    /// <param name="Notes">What couldn't come across.</param>
    /// <param name="Upgraded">
    /// The synths swapped for newer versions: the name to save each under (its channel's, numbered when channels share
    /// one) and the older synth's own settings.
    /// </param>
    public sealed record Result(byte[] Bytes, IReadOnlyList<string> Notes, IReadOnlyList<(string Channel, PresetBlob Sound)>? Upgraded = null);

    const string SamplerId = "468bc14b-b2e7-45a1-9666-e83117fe404e";
    const string NoteTransposeId = "0815cd9e-3a31-4429-a268-dabd952a3b68";
    const int Vst3 = 0x728, Vst2 = 0x1a5;                 // plugin device classes
    const int AudioEffect = 0, Instrument = 2;            // device kinds (field 0x9d)
    const int AudioClipClass = 0xee;                      // an arrangement audio clip (a clip of notes is 0x47)
    const int AudioEntry = 0x287;                         // an audio track in the project's summary (instrument tracks are 0x288)

    /// <summary>Why a project can't be the template (for example "The Bitwig template needs a VST2 instrument track"), or null when it can.</summary>
    public static string? TemplateProblem(byte[] template)
    {
        try
        {
            _ = new Template(template);
            return null;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException or EndOfStreamException)
        {
            return e.Message.StartsWith("The Bitwig template", StringComparison.Ordinal) ? e.Message : $"The Bitwig template couldn't be read ({e.Message})";
        }
    }

    /// <summary>
    /// Reads what Bitwig records about a sample, or null when the file can't be read. When it can be read but not
    /// decoded (a format the app doesn't play), its length is 0.
    /// </summary>
    public static SampleFile? ReadSample(string path)
    {
        byte[] md5;
        long bytes;
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            bytes = fs.Length;
            md5 = MD5.HashData(fs);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        try
        {
            var audio = AudioFile.CanRead(path) ? AudioFile.Read(path, maxSeconds: 0.01) : null;
            return new SampleFile(bytes, md5, audio?.SampleRate ?? 44100, audio?.Channels.Length ?? 2, audio?.FileSeconds ?? 0);
        }
        // Windows' own decoders (for MP3 and the like) fail with COM errors.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or
                                       System.Runtime.InteropServices.ExternalException)
        {
            return new SampleFile(bytes, md5, 44100, 2, 0);
        }
    }

    /// <param name="template">A project saved by Bitwig with the tracks described above.</param>
    /// <param name="readSample">Reads a sample's details; <see cref="ReadSample"/> unless given.</param>
    /// <param name="stepsUntilNextNote">
    /// Steps from FL's step sequencer on a Sampler last until the next note on their key or the end of their
    /// clip, as on a plugin track. Otherwise they also end when their sample does.
    /// </param>
    /// <param name="upgrade">
    /// An older version of the template's VST3 synth (Serum for the template's Serum 2) is swapped for the template's.
    /// Serum 1's sounds are translated for Serum 2 (<see cref="Serum1To2"/>); others open at the newer one's default.
    /// </param>
    /// <param name="serumAudio">
    /// Finds Serum 1's wavetables for the translation; Serum's own folders (<see cref="Serum1To2.XferAudio"/>) unless given.
    /// </param>
    public static Result FromFl(FlSong song, byte[] template, Func<string, SampleFile?>? readSample = null, bool stepsUntilNextNote = false,
                                bool upgrade = false, Func<string, Serum1To2.AudioInfo?>? serumAudio = null)
    {
        serumAudio ??= Serum1To2.XferAudio();
        readSample ??= ReadSample;
        var t = new Template(template);
        var notes = new List<string>();
        var states = new List<(string Name, byte[] Data)>();
        var upgraded = new HashSet<int>();
        var sounds = new List<(string Channel, PresetBlob Sound)>();

        // A plugin as a Bitwig device: a copy of the template's VST3 or VST2 device with this plugin's identity and settings.
        BwObject? Plugin(FlPlugin p, int kind, List<string> skipped, bool fresh = false)
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
                var (data, extension) = !vst3 && s.Extension == ".fxp" ? (Fxp.ToBank(s.Data, s.Program), ".fxb") : (s.Data, s.Extension);
                state = $"{Guid.NewGuid()}{extension}";
                states.Add((state, data));
            }
            else if (p.NothingSaved) skipped.Add($"{p.Name}'s sound (FL Studio saved none in this file, so it opens at its default)");
            else if (!fresh) skipped.Add($"{p.Name}'s settings (opens at its defaults)");
            def.Set(0xbe5, state);
            return device;
        }

        // Each copy of an insert's effect, by insert and slot, with the track it's on, for automating its settings.
        var effectCopies = new Dictionary<(int Insert, int Slot), List<(BwObject Track, BwObject Device, FlPlugin Plugin)>>();

        // FL inserts' VST effects as devices on a track; an insert's effects are copied onto every track that feeds it.
        // One switched off in FL is switched off here too.
        List<BwObject> Effects(FlInsert? insert, BwObject track, List<string> skipped)
        {
            var made = new List<BwObject>();
            foreach (var effect in insert?.Effects ?? [])
            {
                if (Plugin(effect, AudioEffect, skipped) is not { } device) continue;
                made.Add(device);
                if (!effect.Enabled)
                {
                    if (Def(device)!.Has(0xa3)) Def(device)!.Set(0xa3, (byte)0, 0x05);
                    else notes.Add($"{effect.Name} on {InsertName(insert!)}: switched off in FL Studio, but on here (the Bitwig template's plugin has no on/off to copy)");
                }
                if (effect.Slot < 0) continue;
                if (!effectCopies.TryGetValue((insert!.Index, effect.Slot), out var copies)) effectCopies[(insert.Index, effect.Slot)] = copies = [];
                copies.Add((track, device, effect));
            }
            return made;
        }

        // Audio clip channels play their clips on audio tracks; one with no notes needs no instrument track too.
        var audio = song.Channels.Where(c => c.AudioClips.Count > 0 && c.Sample.Length > 0).ToHashSet();
        // Every other channel with a plugin or sample gets an instrument track, as in FL's Channel Rack, even
        // one the playlist never plays, so its sound comes over.
        var playing = song.Channels.Where(c => (c.Notes.Count > 0 || c.Clips.Count > 0 || !audio.Contains(c)) &&
                                               (c.Instrument is not null || c.Sample.Length > 0)).ToHashSet();
        var inserts = song.Inserts.ToDictionary(i => i.Index);
        var shared = song.Channels.Where(c => (playing.Contains(c) || audio.Contains(c) && t.AudioTrack is not null) && c.Insert > 0)
                                  .GroupBy(c => c.Insert).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var tracks = new List<BwObject>();
        // Each channel's tracks: its instrument track first, if it has one, then its audio tracks.
        var placed = new Dictionary<int, (FlChannel Channel, List<BwObject> Tracks)>();
        var instruments = new Dictionary<int, (BwObject Device, FlPlugin Plugin)>();
        // How many of each channel's tracks have their own copy of its insert's effects.
        var tracksWithEffects = new Dictionary<int, int>();

        // The VST effects of the FL insert a channel feeds, after what's on its track.
        void InsertEffects(FlChannel c, BwObject track, List<BwObject> devices, List<string> skipped)
        {
            if (c.Insert <= 0 || !inserts.TryGetValue(c.Insert, out var insert)) return;
            var effects = Effects(insert, track, skipped);
            devices.AddRange(effects);
            if (effects.Count == 0) return;
            tracksWithEffects[c.Index] = tracksWithEffects.GetValueOrDefault(c.Index) + 1;
            if (shared.Contains(c.Insert))
                notes.Add($"{c.Name}: has its own copy of {InsertName(insert)}'s effects, which other channels also feed in FL Studio");
        }

        // A channel that plays notes: an instrument track with its Sampler or plugin, and a clip for each FL pattern clip.
        BwObject InstrumentTrack(FlChannel c)
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
                if (info is { Seconds: > 0 } && !stepsUntilNextNote)
                    sampleLength = key => SampleTicks(info.Seconds, key - c.RootKey + c.PitchCents / 100.0, song.Tempo, song.Ppq);
            }
            else
            {
                var instrument = upgrade && Upgrade(c.Instrument, t) is { } newer ? newer : c.Instrument;
                var swapped = instrument != c.Instrument;
                if (swapped)
                {
                    upgraded.Add(c.Index);
                    // Channels often share a name ("Serum" twice): each gets its own file, numbered.
                    var saveAs = c.Name;
                    for (var n = 2; sounds.Any(s => s.Channel.Equals(saveAs, StringComparison.OrdinalIgnoreCase)); n++) saveAs = $"{c.Name} ({n})";
                    sounds.Add((saveAs, c.Instrument.State!));
                    var backup = $"The original is saved as \"{saveAs} ({c.Instrument.Name})\" in the \"{UpgradedFolder}\" folder next to this project, " +
                                 $"to load in {instrument.Name}'s preset browser";
                    if (Translate(c.Instrument, instrument, serumAudio) is var (state, rough))
                    {
                        instrument = instrument with { State = state };
                        notes.Add($"{c.Name}: {c.Instrument.Name} swapped for {instrument.Name}, with its sound translated" +
                                  (rough.Count > 0 ? $" (only roughly: {Serum1To2.Describe(rough)})" : "") + $". {backup}");
                    }
                    else notes.Add($"{c.Name}: {c.Instrument.Name} swapped for {instrument.Name}, which opens with its default sound. {backup}");
                }
                track = t.Copy(instrument.Format == "VST2" ? t.Vst2Track : t.Vst3Track);
                if (Plugin(instrument, Instrument, skipped, fresh: swapped) is { } plugin)
                {
                    instruments[c.Index] = (plugin, instrument);
                    // FL plays a plugin's root key as C5, so a moved root key transposes it, as does the pitch knob.
                    var shift = FlStudioReader.DefaultRoot - c.RootKey + (int)Math.Round(c.PitchCents / 100.0);
                    if (shift != 0) devices.Add(Transpose(t.Copy(t.NoteTranspose), shift));
                    if (c.PitchCents % 100 != 0)
                        notes.Add($"{c.Name}: its pitch knob ({c.PitchCents / 100.0:+0.##;-0.##} semitones) is rounded to whole semitones");
                    devices.Add(plugin);
                }
            }
            InsertEffects(c, track, devices, skipped);
            Chain(track, devices);
            track.Set(0x15b, c.Name);
            // When all its clips are on muted FL playlist tracks, the track is muted rather than its clips.
            var trackMuted = FlClips.AllOnMutedTracks(c.Clips);
            Mixer(track, c, trackMuted);
            foreach (var clip in FlClips.Of(c with { Clips = FlClips.OnMutedTracks(c.Clips, trackMuted) }, song.Ppq))
                t.AddClip(track, clip with { Notes = FlClips.Lengthen(clip.Notes, clip.Length, sampleLength) }, song.Ppq);
            if (DawProject.OnMutedTracks(c.Name, c.Clips.Where(x => x.OnMutedTrack).Select(x => x.Track), song.MutedTracks, trackMuted) is { } onMuted)
                notes.Add(onMuted);
            if (skipped.Count > 0) notes.Add($"{c.Name}: left out {string.Join(", ", skipped)}");
            if (c.VolumeEnvelope is not null) notes.Add($"{c.Name}: {DawProject.EnvelopeLeftOut}");
            return track;
        }

        // An audio clip channel's clips in the playlist on audio tracks, stretched and pitched as in FL. Clips that
        // overlap (on different playlist tracks in FL) play at once, so each goes on the first of its tracks that's free.
        List<BwObject> AudioClipTracks(FlChannel c)
        {
            var skipped = new List<string>();
            var info = readSample(c.Sample);
            var lanes = new List<List<FlAudioClip>>();
            foreach (var clip in c.AudioClips.OrderBy(x => x.Start))
            {
                var lane = lanes.FindIndex(l => l[^1].Start + l[^1].Length <= clip.Start);
                if (lane < 0) lanes.Add([clip]);
                else lanes[lane].Add(clip);
            }
            var made = new List<BwObject>();
            foreach (var clips in lanes)
            {
                var track = t.NewAudioTrack();
                var devices = new List<BwObject>();
                InsertEffects(c, track, devices, made.Count == 0 ? skipped : []);
                Chain(track, devices);
                var name = made.Count == 0 ? c.Name : $"{c.Name} {made.Count + 1}";
                track.Set(0x15b, name);
                // When all its clips are on muted FL playlist tracks, the track is muted rather than its clips.
                var trackMuted = FlClips.AllOnMutedTracks(clips);
                Mixer(track, c, trackMuted);
                foreach (var clip in FlClips.OnMutedTracks(clips, trackMuted)) t.AddAudioClip(track, c, clip, info, song.Tempo, song.Ppq);
                if (DawProject.OnMutedTracks(name, clips.Where(x => x.OnMutedTrack).Select(x => x.Track), song.MutedTracks, trackMuted) is { } onMuted)
                    notes.Add(onMuted);
                made.Add(track);
            }
            if (lanes.Count > 1) notes.Add($"{c.Name}: its audio clips overlap in FL Studio, so they're on {lanes.Count} tracks");
            if (info is null)
                notes.Add($"{c.Name}: couldn't read {c.Sample}, so Bitwig will ask where it is, and how far its clips are stretched is a guess");
            else if (info.Seconds <= 0)
                notes.Add($"{c.Name}: couldn't tell how long {c.Sample} is, so Bitwig will ask where it is, and how far its clips are stretched is a guess");
            if (c.Stretch.Reversed) notes.Add($"{c.Name}: plays its sample backwards in FL Studio; reverse its clips in Bitwig");
            if (c.PitchCents != 0)
                notes.Add($"{c.Name}: its channel pitch knob ({c.PitchCents / 100.0:+0.##;-0.##} semitones) isn't carried over to its audio clips; " +
                          "the pitch in its time stretching is");
            if (skipped.Count > 0) notes.Add($"{c.Name}: left out {string.Join(", ", skipped)}");
            return made;
        }

        foreach (var c in song.Channels)
        {
            var own = new List<BwObject>();
            if (playing.Contains(c)) own.Add(InstrumentTrack(c));
            if (playing.Contains(c) && c.Clips.Count == 0 && !audio.Contains(c)) notes.Add($"{c.Name}: {DawProject.PlaysNothing}");
            if (c.AudioClips.Count > 0 && c.Sample.Length == 0)
                notes.Add($"{c.Name}: its audio clips are left out (FL Studio didn't save which sample they play)");
            else if (audio.Contains(c) && t.AudioTrack is null)
                notes.Add($"{c.Name}: its audio clips are left out (the Bitwig template has no audio track with an audio clip on it " +
                          "that can be copied; it can't be in a group)");
            else if (audio.Contains(c)) own.AddRange(AudioClipTracks(c));
            if (own.Count == 0) continue;
            if (tracksWithEffects.GetValueOrDefault(c.Index) is var copies and > 1)
                notes.Add($"{c.Name}: each of its {copies} tracks has its own copy of {InsertName(inserts[c.Insert])}'s effects, " +
                          "which FL Studio runs once on all of them");
            if (!c.Enabled) notes.Add($"{c.Name}: {DawProject.SwitchedOff(own.Count)}");
            if (c.SwitchedBy.Count > 0) notes.Add($"{c.Name}: {DawProject.SwitchedOnAndOff(c, audio.Contains(c) && t.AudioTrack is not null)}");
            tracks.AddRange(own);
            placed[c.Index] = (c, own);
        }


        // The master insert's effects on Bitwig's master.
        var masterSkipped = new List<string>();
        var masterEffects = Effects(inserts.GetValueOrDefault(0), t.Master, masterSkipped);
        if (masterEffects.Count > 0) Chain(t.Master, [.. Chain(t.Master), .. masterEffects]);
        if (masterSkipped.Count > 0) notes.Add($"Master: left out {string.Join(", ", masterSkipped)}");

        // FL's insert faders and pans aren't carried yet (how FL's fader maps to gain isn't worked out).
        if (DawProject.MovedInserts(placed.Values.Select(p => p.Channel.Insert).Append(0).Distinct().Order()
                                          .Select(inserts.GetValueOrDefault).OfType<FlInsert>()) is { } moved)
            notes.Add(moved);

        // Automation clips as automation lanes on the track they move: its volume, pan or a setting of its plugin, or a
        // setting of an effect on a mixer insert, on each track that has a copy of it.
        const string noLane = "the Bitwig template has no automation lane to copy";
        foreach (var a in song.Automation)
        {
            if (a.Control == FlControl.InsertEffect)
            {
                string? left = null;
                if (!effectCopies.TryGetValue((a.Insert, a.Slot), out var copies)) left = "its effect isn't converted";
                else if (SettingPath(copies[0].Plugin, a.Parameter) is not { } setting) left = "FL didn't save which of the effect's settings it moves";
                else
                    foreach (var (track, device, effect) in copies)
                        if (!t.AddPluginAutomation(track, device, setting.Path, setting.Id, SettingName(a.Name, effect.Name), Lane(a)))
                            left = noLane;
                if (left is not null) notes.Add($"{a.Name}: automation left out ({left})");
                continue;
            }
            if (a.Channel < 0 || a.Control is FlControl.Pitch or FlControl.Other)
            {
                notes.Add($"{a.Name}: automation left out ({DawProject.NotCarried(a, song)})");
                continue;
            }
            if (!placed.TryGetValue(a.Channel, out var on))
            {
                notes.Add($"{a.Name}: automation left out (what it moves isn't converted)");
                continue;
            }
            string? why = null;
            switch (a.Control)
            {
                // The channel's volume or pan moves all its tracks' faders.
                case FlControl.Volume or FlControl.Pan:
                    var pan = a.Control == FlControl.Pan;
                    Func<double, double> value = pan ? v => Math.Clamp((v - 0.5) * 2, -1, 1) : v => Math.Cbrt(DawProject.Gain(v));
                    foreach (var track in on.Tracks)
                    {
                        if (track.Obj(0x165)?.Obj(pan ? 0x1a5 : 0x1a4) is not { } param) why = "this track has no fader";
                        else if (!t.AddMixerAutomation(track, param, pan ? "Pan" : "Volume", Lane(a, value)))
                            why = noLane;
                    }
                    break;
                // The plugin is on the channel's instrument track, its first.
                case FlControl.PluginParameter when upgraded.Contains(a.Channel):
                    why = "its plugin was swapped for a newer version, whose settings are numbered differently";
                    break;
                case FlControl.PluginParameter when instruments.TryGetValue(a.Channel, out var instrument):
                    if (SettingPath(instrument.Plugin, a.Parameter) is not { } setting)
                        why = "FL didn't save which of the plugin's settings it moves";
                    else if (!t.AddPluginAutomation(on.Tracks[0], instrument.Device, setting.Path, setting.Id, SettingName(a.Name, on.Channel.Name), Lane(a)))
                        why = noLane;
                    break;
                case FlControl.PluginParameter:
                    why = "its plugin isn't converted";
                    break;
                // Carried by muting what the channel plays while it's off (see FlChannel.SwitchedBy).
                case FlControl.OnOff:
                    break;
            }
            if (why is not null) notes.Add($"{a.Name}: automation left out ({why})");
        }

        // A channel's notes about its insert or sample can come from each of its tracks.
        return new Result(t.Finish(tracks, song.Tempo, states), notes.Distinct().ToList(), sounds);
    }

    /// <summary>Where the original synths' sounds are saved, next to a converted project, when they were swapped for newer versions.</summary>
    public const string UpgradedFolder = "Sounds from older plugins";

    /// <summary>
    /// The template's VST3 synth in place of an older version of it (Serum 2 for Serum, by name with the version left
    /// off), with no settings yet. The older one's settings can't be handed to it as they are: Serum 2 refuses Serum's
    /// as its own state and then won't load at all (seen on Solomon's PC). So the old sound is translated where the
    /// app can (<see cref="Translate"/>), and also saved beside the project, since Serum 2's browser opens Serum's
    /// presets. Null when it isn't an older version (a project's Kontakt 8 stays, beside a template's Kontakt 7), or has
    /// no sound to save.
    /// </summary>
    static FlPlugin? Upgrade(FlPlugin p, Template t)
    {
        var newer = Def(t.Vst3Device)!;
        var name = newer.Str(0x9a);
        if (p.Native || p.State is null || name.Length == 0 || p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            PluginCatalog.Family(p.Name) != PluginCatalog.Family(name) || PluginCatalog.VersionOf(name) <= PluginCatalog.VersionOf(p.Name) ||
            newer[0xce4] is not string classId || classId.Length != 32)
            return null;
        var path = newer.Obj(0xc5a)?[0xc5b] is List<object?> { Count: > 0 } location && newer.Obj(0xc5a)!.Resolve(location[0]) is { } file
            ? file.Str(0x3b) : "";
        return new FlPlugin(name, newer.Str(0x9b), "VST3", classId, null, path);
    }

    /// <summary>An automation clip's points for a lane, with where the FL clip each one belongs to starts (see <see cref="FlAutomation.StartOf"/>).</summary>
    static IEnumerable<(double Beat, double Value, double From)> Lane(FlAutomation a, Func<double, double>? value = null) =>
        a.Points.Select(p => (p.Beat, value is null ? p.Value : value(p.Value), a.StartOf(p)));

    /// <summary>
    /// The older synth's sound for the newer one, when the app can translate it (Serum 1's for Serum 2, which won't
    /// take Serum 1's as its own), as a VST3 preset; and what came across only roughly. Null otherwise.
    /// </summary>
    static (PresetBlob State, IReadOnlyList<string> Rough)? Translate(FlPlugin older, FlPlugin newer, Func<string, Serum1To2.AudioInfo?>? audio)
    {
        if (!newer.Name.Contains('2') || older.State is not { } state || Serum1State.Read(state.Data) is not { } serum1) return null;
        var result = Serum1To2.Translate(serum1, audio);
        // Bitwig hands Serum 2 the component state; its controller rebuilds its own from that.
        return (new PresetBlob(state.Name, newer.Name, ".vstpreset", VstPreset.Build(newer.DeviceId, result.State.Write(), [])), result.Rough);
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
        if (zone is null || sample is null || !SampleIs(sample, c.Sample, info)) throw new InvalidDataException("The template's Sampler has no sample in it");
        zone.Set(0x75c, (byte)Math.Clamp(c.RootKey, 0, 127), 0x01);
        if (info is not { Seconds: > 0 })
        {
            notes.Add(info is null ? $"{c.Name}: couldn't read {c.Sample}, so Bitwig will ask where it is"
                                   : $"{c.Name}: couldn't tell how long {c.Sample} is, so Bitwig will ask where it is");
            return sampler;
        }
        zone.Set(0x75f, info.Seconds);
        zone.Set(0x761, info.Seconds);
        return sampler;
    }

    /// <summary>
    /// Points a sample Bitwig saved (a Sampler's, or an audio clip's) at another file: its name, path, and what Bitwig
    /// recognises it by. When the file couldn't be read, or how long it is couldn't be told, its MD5 is zeros, so
    /// Bitwig asks where it is and reads it itself rather than trusting a made-up length. False when
    /// <paramref name="sample"/> isn't a sample.
    /// </summary>
    static bool SampleIs(BwObject sample, string path, SampleFile? info)
    {
        if (sample.Obj(0x129e) is not { } file) return false;
        var fileName = Path.GetFileName(path.Replace('\\', '/'));
        sample.Set(0x129f, fileName);
        file.Set(0x512, fileName);
        file.Obj(0xd3b)?.Set(0x3b, path);
        if (info is not { Seconds: > 0 })
        {
            file.Obj(0x513)?.Set(0x16, new byte[16], 0x0d);
            return true;
        }
        file.Obj(0x513)?.Set(0x16, info.Md5, 0x0d);
        file.Set(0x17, info.Bytes, IntType(info.Bytes));
        sample.Set(0x94, info.Rate, IntType(info.Rate));
        sample.Set(0x95, info.Seconds);
        sample.Set(0x96, (byte)info.Channels, 0x01);
        return true;
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

    /// <summary>
    /// Channel volume and pan on the track's mixer, and its mute when the channel is switched off or the track is
    /// <paramref name="muted"/>. Bitwig's fader holds the cube root of the gain; its mute button is on when MUTE's value (0xd2) is 1.
    /// </summary>
    static void Mixer(BwObject track, FlChannel c, bool muted)
    {
        var mixer = track.Obj(0x165);
        mixer?.Obj(0x1a4)?.Set(0x2c8, Math.Cbrt(DawProject.Gain(c.Volume)));
        mixer?.Obj(0x1a5)?.Set(0x2c8, Math.Clamp((c.Pan - 0.5) * 2, -1, 1));
        muted |= !c.Enabled;
        if (mixer?.Obj(0x1a6) is { } mute && (muted || mute.Has(0xd2))) mute.Set(0xd2, (byte)(muted ? 1 : 0), 0x05);
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
        readonly BwObject? _mixerLane, _pluginLane, _point, _setting, _audioClip;
        /// <summary>Where the FL clip each automation point came from starts (see <see cref="AddLane"/>).</summary>
        readonly Dictionary<BwObject, double> _from = new(ReferenceEqualityComparer.Instance);
        readonly HashSet<BwObject> _audioTracks = new(ReferenceEqualityComparer.Instance);
        readonly Copier _copier;
        public BwObject SamplerTrack { get; }
        public BwObject Vst3Track { get; }
        public BwObject Vst2Track { get; }
        public BwObject Vst3Device { get; }
        public BwObject Vst2Device { get; }
        public BwObject NoteTranspose { get; }
        public BwObject Master { get; }
        /// <summary>A track to copy for audio clips, when the template has one with an audio clip on it.</summary>
        public BwObject? AudioTrack { get; }

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
            // A clip of notes, the VST3 track's first; an audio clip's content is laid out the same, so it's told apart by its class.
            _clip = Need(originals.OrderBy(tr => tr == Vst3Track ? 0 : 1).SelectMany(Clips)
                                  .FirstOrDefault(c => c.Class != AudioClipClass && KeyLanes(c).Any(l => l.List(0x21f).Any())), "a clip with notes");
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
            // An audio track and an audio clip with a sample, on a track of its own, if the template has them.
            AudioTrack = originals.Except([SamplerTrack, Vst3Track, Vst2Track], ReferenceEqualityComparer.Instance).Cast<BwObject>()
                                  .FirstOrDefault(tr => Clips(tr).Any(IsAudioClip));
            _audioClip = AudioTrack is null ? null : Clips(AudioTrack).First(IsAudioClip);

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
            if (AudioTrack is not null) Chain(AudioTrack, []);
            // Sends to effect tracks inside devices (a multi-out plugin's output chains) go with the effect tracks.
            foreach (var o in new[] { SamplerTrack, Vst3Track, Vst2Track, AudioTrack }.OfType<BwObject>().SelectMany(Reachable))
                foreach (var (_, v) in o.Fields)
                    if (v is List<object?> list)
                        list.RemoveAll(x => o.Resolve(x)?.Str(0x2b9).StartsWith("TRACK_SEND(", StringComparison.Ordinal) == true);
            _keyLane.Set(0x21f, new List<object?>());
            NoteContent(_clip)!.Set(0x18cb, new List<object?>());
            if (_audioClip is not null)
            {
                // Only the audio that's filled in: anything else in the template's clip would play its own sample.
                var audioLane = KeyLanes(_audioClip).First();
                audioLane.Set(0x21f, new List<object?> { AudioEvent(_audioClip) });
                NoteContent(_audioClip)!.Set(0x18cb, new List<object?> { audioLane });
            }
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
            // Audio clips are filled in where they're copied, so nothing in one may be shared with the rest of the project.
            if (AudioTrack is not null && (_copier.Shares(AudioTrack) || Reachable(_audioClip!).Any(_copier.Shares)))
                (AudioTrack, _audioClip) = (null, null);
        }

        public BwObject Copy(BwObject pattern) => _copier.Copy(pattern);

        static IEnumerable<BwObject> Clips(BwObject track) => track.Obj(0x2bf8)?.Obj(0x2d8e)?.List(0x21f) ?? [];

        /// <summary>An audio clip's audio: its one event, which plays the sample.</summary>
        static BwObject? AudioEvent(BwObject clip) => clip.Class == AudioClipClass ? KeyLanes(clip).FirstOrDefault()?.List(0x21f).FirstOrDefault() : null;

        static bool IsAudioClip(BwObject clip) => AudioEvent(clip)?.Obj(0x288)?.Obj(0x20d)?.Obj(0x4b4)?.Has(0x129e) == true;
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

        /// <summary>A copy of the template's audio track, to hold audio clips.</summary>
        public BwObject NewAudioTrack()
        {
            var track = Copy(AudioTrack ?? throw new InvalidOperationException("The Bitwig template has no audio track"));
            _audioTracks.Add(track);
            return track;
        }

        /// <summary>
        /// One of an FL audio clip channel's clips as an arrangement audio clip. The clip's audio is the whole sample,
        /// stretched to play as long as it does in FL (warp markers at its start and end) and transposed by the
        /// semitones it's pitched in FL; the clip plays the part of it the FL clip does. The sample's transients,
        /// which Bitwig found in the template's sample, are emptied for it to find this one's.
        /// </summary>
        public void AddAudioClip(BwObject track, FlChannel c, FlAudioClip fl, SampleFile? info, double tempo, int ppq)
        {
            double Beats(long ticks) => ticks / (double)ppq;
            var length = Beats(fl.Length);
            var beatsPerSecond = tempo / 60;
            // How long the sample is, and how long it plays: when it couldn't be read, a guess that the clip plays to its end.
            var seconds = info is { Seconds: > 0 } ? info.Seconds : fl.From + length / beatsPerSecond / Math.Max(c.Stretch.PlayedSeconds(1, 0), 1e-6);
            var played = c.Stretch.PlayedSeconds(seconds, tempo) * beatsPerSecond;
            var from = seconds > 0 ? fl.From / seconds * played : 0;

            var clip = Copy(_audioClip!);
            clip.Set(0x2af, Beats(fl.Start));
            clip.Set(0x26, length);
            if (fl.Muted || clip.Has(0x10f8)) clip.Set(0x10f8, (byte)(fl.Muted ? 1 : 0), 0x05);
            if (clip.Obj(0x2adb) is { } range)
            {
                range.Obj(0x992)?.Set(0x2af, from);
                range.Obj(0x992)?.Set(0x26, length);
                range.Obj(0x98c)?.Set(0x2af, from);
                range.Obj(0x98d)?.Set(0x2af, from + length);
            }
            var content = clip.Obj(0x2ae9)!.Obj(0x10e9)!;
            if (content.Has(0x2d24)) content.Set(0x2d24, c.Name);

            var audio = AudioEvent(clip)!;
            audio.Set(0x2af, 0.0);
            audio.Set(0x26, played);
            if (audio.Has(0x10f8)) audio.Set(0x10f8, (byte)0, 0x05);
            if (audio.Has(0xc04)) audio.Set(0xc04, 0.0);
            if (audio.Has(0xc05)) audio.Set(0xc05, seconds);   // where the sample ends, in its seconds
            var play = audio.Obj(0x288)!;
            // Bitwig's stretch mode that keeps pitch and length apart, as it saves a clip stretched and transposed.
            if (play.Has(0x4b0)) play.Set(0x4b0, (byte)3);
            var semitones = c.Stretch.Semitones(seconds, played / beatsPerSecond);
            if (Math.Abs(semitones) < 1e-6) play.Set(0xc33, null, 0x0a);
            else play.Set(0xc33, Transposition(semitones), 0x09);
            SampleIs(play.Obj(0x20d)!.Obj(0x4b4)!, c.Sample, info);
            // Warp markers: the sample's start at the clip's, its end where it ends when stretched.
            if (play.Obj(0x192) is { } warp)
            {
                var marker = warp.List(0x21f).FirstOrDefault()?.Class ?? 0x43;
                BwObject Marker(double beat, double second)
                {
                    var m = new BwObject { Class = marker };
                    m.Set(0x2af, beat);
                    m.Set(0xa6, second);
                    return m;
                }
                warp.Set(0x21f, new List<object?> { Marker(0, 0), Marker(played, seconds) });
            }
            if (play.Obj(0x10ea)?.Obj(0x10e9) is { } transients)
            {
                transients.Set(0x21f, new List<object?>());
                if (transients.Has(0x4406)) transients.Set(0x4406, (byte)0);
            }

            ((List<object?>)track.Obj(0x2bf8)!.Obj(0x2d8e)![0x21f]!).Add(clip);
            if (track.Obj(0x2aed)?[0x10e6] is List<object?> contents) contents.Add(new BwLink(content));
        }

        /// <summary>An audio clip's transposition, as Bitwig saves it: one point holding the semitones.</summary>
        static BwObject Transposition(double semitones)
        {
            var point = new BwObject { Class = 0x108 };
            point.Set(0x2af, 0.0);
            point.Set(0x28f, semitones);
            point.Set(0x359e, (byte)1);
            point.Set(0xba6, 0.0);
            point.Set(0x2ebe, 0.0);
            var expression = new BwObject { Class = 0x2af };
            expression.Set(0x1fd, (byte)0);
            expression.Set(0x21f, new List<object?> { point });
            return expression;
        }

        /// <summary>Automates the track's volume or pan fader (<paramref name="param"/>), with values in Bitwig's units.</summary>
        public bool AddMixerAutomation(BwObject track, BwObject param, string name, IEnumerable<(double Beat, double Value, double From)> points) =>
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
                                        IEnumerable<(double Beat, double Value, double From)> points)
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
        /// <paramref name="pattern"/> if the track has none yet; several FL clips moving one setting share it, in time
        /// order, and where two meet on a beat the one starting there goes last (From is where its clip starts).
        /// False when the template has no lane to copy.
        /// </summary>
        bool AddLane(BwObject track, BwObject? pattern, Func<BwObject, bool> moves, Action<BwObject> setUp,
                     IEnumerable<(double Beat, double Value, double From)> points)
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
                _from[point] = p.From;
                return (object?)point;
            }));
            var sorted = list.OrderBy(x => Math.Round((lane.Resolve(x)?.Num(0x2af) ?? 0) * 1e4))
                             .ThenBy(x => lane.Resolve(x) is { } o ? _from.GetValueOrDefault(o) : 0).ToList();
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
                var trackEntry = project?.List(0xace).FirstOrDefault(e => e.Class != AudioEntry) ?? project?.List(0xace).FirstOrDefault();
                // Audio tracks have their own kind of entry; the template's, if it lists one.
                var audioEntry = project?.List(0xace).FirstOrDefault(e => e.Class == AudioEntry) ?? trackEntry;
                if (summary.BodyError is null && project is not null && trackEntry is not null && audioEntry is not null)
                {
                    var copier = new Copier(summary);
                    var entries = new List<object?>();
                    project.Set(0xace, entries);
                    project.Set(0xacf, new List<object?>());
                    foreach (var tr in tracks)
                    {
                        var e = copier.Copy(_audioTracks.Contains(tr) ? audioEntry : trackEntry);
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
