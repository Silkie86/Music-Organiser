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
/// </summary>
public static class DawProject
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record Result(byte[] Bytes, IReadOnlyList<string> Notes);

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
            if (p.Native || p.State is null)
            {
                skipped.Add(p.Native ? $"{p.Name} (FL Studio plugin)" : $"{p.Name} (settings not readable)");
                return null;
            }
            var path = $"plugins/{Guid.NewGuid()}{p.State.Extension}";
            files.Add((path, p.State.Data));
            return new XElement(p.Format == "VST3" ? "Vst3Plugin" : "Vst2Plugin",
                new XAttribute("id", Id()),
                p.DeviceId.Length > 0 ? new XAttribute("deviceID", p.DeviceId) : null,
                new XAttribute("deviceName", p.Name),
                new XAttribute("deviceRole", role),
                new XAttribute("deviceVendor", p.Vendor),
                new XAttribute("loaded", "true"),
                new XElement("Enabled", new XAttribute("id", Id()), new XAttribute("value", "true")),
                new XElement("State", new XAttribute("path", path)));
        }

        XElement Channel(string role, string? destination, IEnumerable<XElement> devices) =>
            new("Channel",
                new XAttribute("id", Id()),
                new XAttribute("role", role),
                new XAttribute("audioChannels", 2),
                destination is null ? null : new XAttribute("destination", destination),
                new XElement("Devices", devices),
                new XElement("Mute", new XAttribute("id", Id()), new XAttribute("value", "false")),
                Param("Pan", "normalized", 0.5, 0, 1),
                Param("Volume", "linear", 1, 0, 2));

        XElement Track(string name, string contentType, XElement channel, IReadOnlyCollection<string> skipped)
        {
            var t = new XElement("Track", new XAttribute("id", Id()), new XAttribute("name", name),
                                 new XAttribute("contentType", contentType), new XAttribute("loaded", "true"), channel);
            if (skipped.Count > 0) t.Add(new XAttribute("comment", "Left out from FL Studio: " + string.Join(", ", skipped)));
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
        var playing = song.Channels.Where(c => c.Notes.Count > 0 && (c.Instrument is not null || c.Sample.Length > 0)).ToList();
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

        // Instrument tracks, each with one clip holding its notes across the song.
        var lanes = new XElement("Lanes", new XAttribute("id", Id()), new XAttribute("timeUnit", "beats"));
        foreach (var c in playing)
        {
            var skipped = new List<string>();
            var device = c.Instrument is { } inst ? Device(inst, "instrument", skipped) : null;
            if (c.Instrument is null && c.Sample.Length > 0) skipped.Add($"sampler with {Path.GetFileName(c.Sample)}");
            var ch = Channel("regular", insertChannels.GetValueOrDefault(c.Insert, masterId), device is null ? [] : [device]);
            var track = Track(c.Name, "notes", ch, skipped);
            structure.Add(track);
            if (skipped.Count > 0) notes.Add($"{c.Name}: left out {string.Join(", ", skipped)}");

            double Beats(long ticks) => ticks / (double)song.Ppq;
            var end = Math.Ceiling(Beats(c.Notes.Max(n => n.Start + n.Length)) / 4) * 4;
            lanes.Add(new XElement("Lanes", new XAttribute("id", Id()), new XAttribute("track", (string)track.Attribute("id")!),
                new XElement("Clips", new XAttribute("id", Id()),
                    new XElement("Clip", new XAttribute("time", "0"), new XAttribute("duration", Num(end)),
                        new XAttribute("playStart", "0"), new XAttribute("name", c.Name),
                        new XElement("Notes", new XAttribute("id", Id()),
                            c.Notes.Select(n => new XElement("Note",
                                new XAttribute("time", Num(Beats(n.Start))),
                                new XAttribute("duration", Num(Beats(n.Length))),
                                new XAttribute("channel", 0),
                                new XAttribute("key", Math.Clamp(n.Key, 0, 127)),
                                new XAttribute("vel", Num(n.Velocity / 127.0)),
                                new XAttribute("rel", Num(n.Velocity / 127.0)))))))));
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
        return new Result(ms.ToArray(), notes);
    }
}
