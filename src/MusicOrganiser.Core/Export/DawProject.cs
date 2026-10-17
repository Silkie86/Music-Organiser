using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Export;

/// <summary>
/// Writes DAWproject files (the open format from Bitwig and PreSonus, which Bitwig Studio and
/// Studio One import): project.xml and metadata.xml in a zip, with plugin states under plugins/.
///
/// An FL Studio song becomes: one instrument track per channel that plays notes, with its plugin
/// and settings; one effect track per mixer insert that has effects, which those channels feed;
/// and the master with the master insert's effects. FL Studio's own plugins have no equivalent
/// elsewhere, so they're left out and named in the track's comment.
///
/// A sampler channel becomes Bitwig's Sampler. DAWproject can't carry the Sampler's sample or root
/// key (Bitwig's own exports leave them out too), so those come back as setup steps. Channel volume,
/// pan, and automation clips on volume, pan and plugin parameters come across as track automation.
/// </summary>
public static class DawProject
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const string BitwigSampler = "468bc14b-b2e7-45a1-9666-e83117fe404e";

    /// <param name="Notes">What couldn't come across.</param>
    /// <param name="Setup">Steps to finish in Bitwig: loading each Sampler's sample and setting its root key, and transposing plugins whose FL root key was moved.</param>
    public sealed record Result(byte[] Bytes, IReadOnlyList<string> Notes, IReadOnlyList<string> Setup);

    /// <summary>What's said of a channel whose FL volume envelope is switched on.</summary>
    internal const string EnvelopeLeftOut = "its volume envelope (ADSR) isn't carried over yet, so it plays without one";

    /// <summary>What's said of a channel that the FL playlist never plays, carried as a track with no clips.</summary>
    internal const string PlaysNothing = "the FL Studio playlist never plays it, so its track is here with its sound but no clips";

    /// <summary>What's said of a channel switched off in FL's Channel Rack, whose <paramref name="tracks"/> tracks are muted.</summary>
    internal static string SwitchedOff(int tracks) =>
        $"switched off in FL Studio's Channel Rack, so {(tracks > 1 ? "its tracks are" : "its track is")} muted";

    /// <summary>What's said of a channel whose on/off is automated (<see cref="FlChannel.SwitchedBy"/>); <paramref name="audio"/> when its audio clips are carried.</summary>
    internal static string SwitchedOnAndOff(FlChannel c, bool audio)
    {
        var partly = audio ? c.AudioClips.Count(x => x.PartlyOff && !x.Muted && !x.OnMutedTrack) : 0;
        return $"switched on and off by {string.Join(" and ", c.SwitchedBy.Select(n => $"\"{n}\""))} in FL Studio, so what it plays while it's off is muted" +
               (partly == 0 ? "" : partly == 1 ? "; one audio clip it switches off part of the way through plays whole"
                                               : $"; {partly} audio clips it switches off part of the way through play whole");
    }

    /// <summary>
    /// What's said of the mixer inserts (the master is insert 0) whose fader or pan isn't where FL leaves it, which
    /// isn't carried over yet; null when none.
    /// </summary>
    internal static string? MovedInserts(IEnumerable<FlInsert> inserts)
    {
        var moved = inserts.Where(i => i.Moved).Select(InsertLabel).ToList();
        return moved.Count == 0 ? null
            : $"Mixer: the fader or pan of {string.Join(", ", moved)} isn't at FL Studio's default, and mixer faders and pans aren't carried over yet";
    }

    /// <summary>How a note names an insert: "the master", "insert 4", or "Drums (insert 4)".</summary>
    internal static string InsertLabel(FlInsert insert) =>
        insert.Index == 0 ? "the master" : insert.Name.Length > 0 ? $"{insert.Name} (insert {insert.Index})" : $"insert {insert.Index}";

    /// <summary>Why an automation clip moving something neither export carries yet is left out.</summary>
    internal static string NotCarried(FlAutomation a, FlSong song)
    {
        var insert = song.Inserts.FirstOrDefault(i => i.Index == a.Insert) is { } found ? InsertLabel(found)
                   : a.Insert == 0 ? "the master" : $"insert {a.Insert}";
        return a.Control switch
        {
            FlControl.Tempo => $"tempo automation isn't carried over yet; the project is set to {song.Tempo:0.##} BPM",
            FlControl.MainVolume => "the master volume isn't automated here yet",
            FlControl.InsertVolume => $"the fader of {insert} isn't automated here yet",
            FlControl.InsertPan => $"the pan of {insert} isn't automated here yet",
            FlControl.InsertSlotOnOff => $"switching an effect on {insert} on and off isn't carried over yet, so it stays on",
            FlControl.Pitch => "pitch has no equivalent here",
            _ => "this control has no equivalent here",
        };
    }

    /// <summary>
    /// What's said of a track some of whose clips (<paramref name="on"/>, by playlist track) sit on FL's muted playlist
    /// tracks: the track is muted when they all do (<paramref name="trackMuted"/>), otherwise those clips are. Null when none do.
    /// </summary>
    internal static string? OnMutedTracks(string track, IEnumerable<int> on, IReadOnlyList<FlTrack> muted, bool trackMuted)
    {
        var numbers = on.ToHashSet();
        var from = muted.Where(t => numbers.Contains(t.Number)).ToList();
        if (from.Count == 0) return null;
        var where = $"{(from.Count == 1 ? "a muted playlist track" : "muted playlist tracks")} in FL Studio ({string.Join("; ", from.Select(t => t.Label))})";
        return trackMuted ? $"{track}: muted, because its clips are all on {where}"
                          : $"{track}: its clips on {where} are muted, as it also has clips from other playlist tracks";
    }

    /// <summary>
    /// FL's channel volume knob (0..1, 0 dB at its default 78%) as a gain for a 0..2 linear fader.
    /// FL's curve isn't published; a square law keeps each channel's level relative to the default.
    /// </summary>
    public static double Gain(double flVolume) => Math.Min(2, Math.Pow(flVolume / FlStudioReader.DefaultVolume, 2));

    static readonly string[] NoteNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>A MIDI note's name in Bitwig (60 = C3) or FL Studio (60 = C5).</summary>
    public static string NoteName(int key, bool flStudio = false) => NoteNames[key % 12] + (key / 12 - (flStudio ? 0 : 2));

    public static Result FromFl(FlSong song, string fallbackTitle)
    {
        var ids = 0;
        string Id() => $"id{ids++}";
        static string Num(double v) => v.ToString("0.######", Inv);
        var files = new List<(string Path, byte[] Data)>();
        var notes = new List<string>();

        XElement Param(string tag, string unit, double value, double? min = null, double? max = null)
        {
            var e = new XElement(tag, new XAttribute("id", Id()), new XAttribute("unit", unit), new XAttribute("value", Num(value)));
            if (min is not null) e.Add(new XAttribute("min", Num(min.Value)));
            if (max is not null) e.Add(new XAttribute("max", Num(max.Value)));
            return e;
        }

        XElement? Device(FlPlugin p, string role, List<string> skipped)
        {
            if (p.Native || p.State is null && !p.NothingSaved)
            {
                skipped.Add(p.Native ? $"{p.Name} (FL Studio plugin)" : $"{p.Name} (settings not readable)");
                return null;
            }
            string? path = null;
            if (p.State is { } s)
            {
                // VST2 settings go in as a bank (.fxb), as Bitwig's own DAWproject files have them.
                var (data, extension) = p.Format == "VST2" && s.Extension == ".fxp" ? (Fxp.ToBank(s.Data, s.Program), ".fxb") : (s.Data, s.Extension);
                path = $"plugins/{Guid.NewGuid()}{extension}";
                files.Add((path, data));
            }
            else skipped.Add($"{p.Name}'s sound (FL Studio saved none in this file, so it opens at its default)");
            return new XElement(p.Format == "VST3" ? "Vst3Plugin" : "Vst2Plugin",
                new XAttribute("id", Id()),
                p.DeviceId.Length > 0 ? new XAttribute("deviceID", p.DeviceId) : null,
                new XAttribute("deviceName", p.Name),
                new XAttribute("deviceRole", role),
                new XAttribute("deviceVendor", p.Vendor),
                new XAttribute("loaded", "true"),
                new XElement("Enabled", new XAttribute("id", Id()), new XAttribute("value", p.Enabled ? "true" : "false")),
                path is null ? null : new XElement("State", new XAttribute("path", path)));
        }

        XElement Channel(string role, string? destination, IEnumerable<XElement> devices, double volume = 1, double pan = 0.5,
                         bool muted = false) =>
            new("Channel",
                new XAttribute("id", Id()),
                new XAttribute("role", role),
                new XAttribute("audioChannels", 2),
                destination is null ? null : new XAttribute("destination", destination),
                new XElement("Devices", devices),
                new XElement("Mute", new XAttribute("id", Id()), new XAttribute("value", muted ? "true" : "false")),
                Param("Pan", "normalized", pan, 0, 1),
                Param("Volume", "linear", volume, 0, 2));

        XElement Sampler(string sample) =>
            new("BuiltinDevice",
                new XAttribute("id", Id()),
                new XAttribute("name", Path.GetFileNameWithoutExtension(sample)),
                new XAttribute("deviceID", BitwigSampler),
                new XAttribute("deviceName", "Sampler"),
                new XAttribute("deviceRole", "instrument"),
                new XAttribute("loaded", "true"),
                new XElement("Enabled", new XAttribute("id", Id()), new XAttribute("value", "true")));

        XElement Track(string name, string contentType, XElement channel, IReadOnlyCollection<string> skipped, string todo = "")
        {
            var t = new XElement("Track", new XAttribute("id", Id()), new XAttribute("name", name),
                                 new XAttribute("contentType", contentType), new XAttribute("loaded", "true"), channel);
            var comment = string.Join(". ", new[] { todo, skipped.Count > 0 ? "Left out from FL Studio: " + string.Join(", ", skipped) : "" }
                                                .Where(x => x.Length > 0));
            if (comment.Length > 0) t.Add(new XAttribute("comment", comment));
            return t;
        }

        // Master: the master insert's effects.
        var structure = new XElement("Structure");
        var masterSkipped = new List<string>();
        var masterInsert = song.Inserts.FirstOrDefault(i => i.Index == 0);
        var masterChannel = Channel("master", null,
            (masterInsert?.Effects ?? []).Select(p => Device(p, "audioFX", masterSkipped)).OfType<XElement>());
        var masterId = (string)masterChannel.Attribute("id")!;

        // Inserts that channels feed or that hold effects, as effect tracks into the master.
        // Every channel with a plugin or sample to make the sound, as in FL's Channel Rack, even one the playlist
        // never plays. An audio clip channel with no notes isn't a Sampler.
        var playing = song.Channels.Where(c => (c.Notes.Count > 0 || c.Clips.Count > 0 || c.AudioClips.Count == 0) &&
                                               (c.Instrument is not null || c.Sample.Length > 0)).ToList();
        var used = playing.Select(c => c.Insert).Where(i => i > 0).ToHashSet();
        var insertChannels = new Dictionary<int, string>();
        var insertTracks = new List<XElement>();
        foreach (var ins in song.Inserts.Where(i => i.Index > 0 && (used.Contains(i.Index) || i.Effects.Count > 0)))
        {
            var skipped = new List<string>();
            var ch = Channel("effect", masterId, ins.Effects.Select(p => Device(p, "audioFX", skipped)).OfType<XElement>());
            insertChannels[ins.Index] = (string)ch.Attribute("id")!;
            var name = ins.Name.Length > 0 ? ins.Name : $"Insert {ins.Index}";
            insertTracks.Add(Track(name, "audio", ch, skipped));
            if (skipped.Count > 0) notes.Add($"{name}: left out {string.Join(", ", skipped)}");
        }

        foreach (var c in song.Channels.Where(c => c.AudioClips.Count > 0))
            notes.Add($"{c.Name}: its audio clips in the playlist aren't carried over to DAWproject yet");

        // Instrument tracks, with a clip for each FL pattern clip the channel plays in.
        var lanes = new XElement("Lanes", new XAttribute("id", Id()), new XAttribute("timeUnit", "beats"));
        var setup = new List<string>();
        var placed = new Dictionary<int, (XElement Lanes, XElement Channel, XElement? Device)>();
        foreach (var c in playing)
        {
            var skipped = new List<string>();
            var device = c.Instrument is { } inst ? Device(inst, "instrument", skipped) : null;
            var todo = "";
            if (c.Instrument is null && c.Sample.Length > 0)
            {
                var file = Path.GetFileName(c.Sample.Replace('\\', '/'));
                var tune = c.PitchCents == 0 ? "" : $", tune it {c.PitchCents / 100.0:+0.##;-0.##} semitones";
                todo = $"Sampler: load {file}, set Root key to {NoteName(c.RootKey)} ({NoteName(c.RootKey, flStudio: true)} in FL Studio){tune}";
                setup.Add($"{c.Name}: in its Sampler, load {c.Sample}, then set Root key to {NoteName(c.RootKey)}" +
                          $" ({NoteName(c.RootKey, flStudio: true)} in FL Studio){tune}.");
                device = Sampler(file);
            }
            else if (c.Instrument is { } plugin && c.RootKey != FlStudioReader.DefaultRoot)
            {
                // FL plays a plugin's root key as C5, so a moved root key transposes everything it plays.
                var shift = $"{FlStudioReader.DefaultRoot - c.RootKey:+0;-0} semitones";
                todo = $"Add a Transpose note effect before {plugin.Name}, set to {shift} (FL root key {NoteName(c.RootKey, flStudio: true)})";
                setup.Add($"{c.Name}: add Bitwig's Transpose note effect before {plugin.Name} and set it to {shift}" +
                          $" (its FL root key was {NoteName(c.RootKey, flStudio: true)}).");
            }
            // When all its clips are on muted FL playlist tracks, the track is muted rather than its clips.
            var trackMuted = FlClips.AllOnMutedTracks(c.Clips);
            var ch = Channel("regular", insertChannels.GetValueOrDefault(c.Insert, masterId), device is null ? [] : [device],
                             Gain(c.Volume), c.Pan, muted: !c.Enabled || trackMuted);
            var track = Track(c.Name, "notes", ch, skipped, todo);
            structure.Add(track);
            if (!c.Enabled) notes.Add($"{c.Name}: {SwitchedOff(1)}");
            if (c.SwitchedBy.Count > 0) notes.Add($"{c.Name}: {SwitchedOnAndOff(c, audio: false)}");
            if (OnMutedTracks(c.Name, c.Clips.Where(x => x.OnMutedTrack).Select(x => x.Track), song.MutedTracks, trackMuted) is { } onMuted)
                notes.Add(onMuted);
            if (skipped.Count > 0) notes.Add($"{c.Name}: left out {string.Join(", ", skipped)}");
            if (c.Clips.Count == 0) notes.Add($"{c.Name}: {PlaysNothing}");
            if (c.VolumeEnvelope is not null) notes.Add($"{c.Name}: {EnvelopeLeftOut}");

            var trackLanes = new XElement("Lanes", new XAttribute("id", Id()), new XAttribute("track", (string)track.Attribute("id")!));
            placed[c.Index] = (trackLanes, ch, device?.Name.LocalName is "Vst2Plugin" or "Vst3Plugin" ? device : null);
            lanes.Add(trackLanes);
            var clips = FlClips.Of(c with { Clips = FlClips.OnMutedTracks(c.Clips, trackMuted) }, song.Ppq);
            if (clips.Count == 0) continue;

            // DAWproject has no muted notes, so FL's muted notes are left out; a muted clip is switched off.
            double Beats(long ticks) => ticks / (double)song.Ppq;
            trackLanes.Add(new XElement("Clips", new XAttribute("id", Id()), clips.Select(clip =>
            {
                var x = new XElement("Clip", new XAttribute("time", Num(Beats(clip.Start))), new XAttribute("duration", Num(Beats(clip.Length))),
                    new XAttribute("playStart", "0"), new XAttribute("name", clip.Name),
                    new XElement("Notes", new XAttribute("id", Id()),
                        FlClips.Lengthen(clip.Notes, clip.Length).Where(n => !n.Muted).Select(n => new XElement("Note",
                            new XAttribute("time", Num(Beats(n.Start))),
                            new XAttribute("duration", Num(Beats(n.Length))),
                            new XAttribute("channel", 0),
                            new XAttribute("key", Math.Clamp(n.Key, 0, 127)),
                            new XAttribute("vel", Num(n.Velocity / 127.0)),
                            new XAttribute("rel", Num(n.Velocity / 127.0))))));
                if (clip.Muted) x.Add(new XAttribute("enable", "false"));
                return x;
            })));
        }

        if (MovedInserts(song.Inserts.Where(i => i.Index == 0 || used.Contains(i.Index))) is { } moved) notes.Add(moved);

        // Automation clips, as automation on the track they move: its volume, pan or plugin parameter.
        foreach (var a in song.Automation)
        {
            if (a.Control == FlControl.InsertEffect)
            {
                notes.Add($"{a.Name}: automation left out (automating mixer effects isn't carried over to DAWproject yet)");
                continue;
            }
            if (a.Channel < 0 || a.Control is FlControl.Pitch or FlControl.Other)
            {
                notes.Add($"{a.Name}: automation left out ({NotCarried(a, song)})");
                continue;
            }
            // Carried by leaving out what the channel plays while it's off (see FlChannel.SwitchedBy).
            if (a.Control == FlControl.OnOff) continue;
            if (!placed.TryGetValue(a.Channel, out var t))
            {
                notes.Add($"{a.Name}: automation left out (what it moves isn't converted)");
                continue;
            }
            XElement? target = null;
            Func<double, double> value = v => v;
            var unit = "normalized";
            switch (a.Control)
            {
                case FlControl.Volume:
                    target = t.Channel.Element("Volume");
                    value = Gain;
                    unit = "linear";
                    break;
                case FlControl.Pan:
                    target = t.Channel.Element("Pan");
                    break;
                case FlControl.PluginParameter when t.Device is not null:
                    // FL numbers a VST3 plugin's settings by position, other DAWs by the plugin's own parameter ids,
                    // which FL keeps in that order; VST2 settings are numbered the same way everywhere.
                    long parameterId = a.Parameter;
                    if (t.Device.Name.LocalName == "Vst3Plugin")
                    {
                        var pluginIds = song.Channels.FirstOrDefault(c => c.Index == a.Channel)?.Instrument?.ParameterIds;
                        if (pluginIds is null || a.Parameter < 0 || a.Parameter >= pluginIds.Count)
                        {
                            notes.Add($"{a.Name}: automation left out (FL didn't save which of the plugin's settings it moves)");
                            continue;
                        }
                        parameterId = pluginIds[a.Parameter];
                    }
                    target = new XElement("RealParameter", new XAttribute("id", Id()), new XAttribute("name", a.Name),
                        new XAttribute("parameterID", parameterId), new XAttribute("unit", "normalized"),
                        new XAttribute("min", "0"), new XAttribute("max", "1"), new XAttribute("value", Num(a.Points[0].Value)));
                    if (t.Device.Element("Parameters") is { } ps) ps.Add(target);
                    else t.Device.AddFirst(new XElement("Parameters", target));
                    break;
            }
            if (target is null)
            {
                notes.Add($"{a.Name}: automation left out ({NotCarried(a, song)})");
                continue;
            }
            t.Lanes.Add(new XElement("Points", new XAttribute("id", Id()), new XAttribute("unit", unit),
                new XElement("Target", new XAttribute("parameter", (string)target.Attribute("id")!)),
                a.Points.Select(p => new XElement("RealPoint", new XAttribute("time", Num(p.Beat)),
                    new XAttribute("value", Num(value(p.Value))), new XAttribute("interpolation", "linear")))));
        }
        foreach (var t in insertTracks) structure.Add(t);
        structure.Add(Track("Master", "audio notes", masterChannel, masterSkipped));
        if (masterSkipped.Count > 0) notes.Add($"Master: left out {string.Join(", ", masterSkipped)}");

        var title = song.Title.Length > 0 ? song.Title : fallbackTitle;
        var project = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement("Project", new XAttribute("version", "1.0"),
                new XElement("Application", new XAttribute("name", "Music Organiser"), new XAttribute("version", "1.0")),
                new XElement("Transport",
                    Param("Tempo", "bpm", song.Tempo, 20, 999),
                    new XElement("TimeSignature", new XAttribute("id", Id()), new XAttribute("numerator", 4), new XAttribute("denominator", 4))),
                structure,
                new XElement("Arrangement", new XAttribute("id", Id()), lanes)));
        var metadata = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement("MetaData", new XElement("Title", title), new XElement("Comment", "Converted from FL Studio by Music Organiser")));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, Action<Stream> write)
            {
                using var s = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
                write(s);
            }
            Add("project.xml", s => project.Save(s));
            Add("metadata.xml", s => metadata.Save(s));
            foreach (var (path, data) in files) Add(path, s => s.Write(data));
        }
        return new Result(ms.ToArray(), notes, setup);
    }
}
