using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using MusicOrganiser.Core.Preview;

namespace MusicOrganiser.Core.Parsers;

public sealed partial class StudioOneReader
{
    /// <summary>
    /// The song as Studio One's arrangement shows it, as mapped from Solomon's songs (Studio One 6.5 and Studio Pro 8):
    /// Song/song.xml lists the tracks (MediaTrack, mediaType Music or Audio) and their events; an instrument part's notes
    /// are in its clip's file (Performances/*.musicx, binary JSON, or *.pattern for a pattern part), found through
    /// Song/mediapool.xml by clipID. Positions are in beats; an event whose timeFormat is 0 gives its length and offset
    /// in seconds. The tempo map holds seconds per beat.
    /// </summary>
    public Arrangement ReadArrangement(string path)
    {
        const string view = "Studio One arrangement";
        using var zip = ZipFile.OpenRead(path);
        if (LoadXml(zip, "Song/song.xml") is not { } song)
            return new Arrangement(view, 120, 4, []) { Notes = ["This song couldn't be read in enough detail to draw."] };

        var secondsPerBeat = song.Descendants("TempoMapSegment").Select(t => Num(Attr(t, "tempo"))).FirstOrDefault(t => t is > 0.05 and < 6);
        double? metaTempo = null;
        if (LoadXml(zip, "metainfo.xml") is { } meta)
            metaTempo = meta.Descendants().Where(e => Id(e) == "Media:Tempo").Select(e => Num(Attr(e, "value"))).FirstOrDefault();
        var tempo = secondsPerBeat is { } spb ? 60 / spb : metaTempo is >= 10 and <= 999 ? metaTempo.Value : 120;
        secondsPerBeat ??= 60 / tempo;
        var signature = song.Descendants("TimeSignatureMapSegment").FirstOrDefault();
        var beatsPerBar = signature is null ? 4
            : (int)Math.Max(1, Math.Round((Num(Attr(signature, "numerator")) ?? 4) * 4 / (Num(Attr(signature, "denominator")) ?? 4)));

        // What each clipID points at: an audio file (with its length) or the file in the song holding a part's notes.
        var audio = new Dictionary<string, (string Path, double Seconds)>(StringComparer.OrdinalIgnoreCase);
        var music = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (LoadXml(zip, "Song/mediapool.xml") is { } pool)
            foreach (var clip in pool.Descendants())
            {
                var id = Attr(clip, "mediaID");
                if (id.Length == 0) continue;
                var url = clip.Elements("Url").Select(u => Attr(u, "url")).FirstOrDefault() ?? "";
                if (clip.Name.LocalName == "AudioClip")
                {
                    var format = clip.Elements("Attributes").FirstOrDefault(a => Id(a) == "format");
                    var frames = format is null ? null : Num(Attr(format, "frameCount"));
                    var rate = format is null ? null : Num(Attr(format, "sampleRate"));
                    audio[id] = (UrlToPath(url), frames is > 0 && rate is > 0 ? frames.Value / rate.Value : 0);
                }
                else if (url.StartsWith("media:///", StringComparison.OrdinalIgnoreCase))
                    music[id] = Uri.UnescapeDataString(url["media:///".Length..]);
            }

        double Beats(XElement e, string attr, bool seconds) => Num(Attr(e, attr)) is { } v ? seconds ? v / secondsPerBeat.Value : v : 0;
        bool InSeconds(XElement e) => Attr(e, "timeFormat") == "0";

        var lanes = new List<PreviewLane>();
        var missing = 0;
        void Walk(XElement list, int depth)
        {
            foreach (var track in list.Elements())
            {
                var kind = track.Name.LocalName switch
                {
                    "MediaTrack" => Attr(track, "mediaType") == "Audio" ? LaneKind.Audio : LaneKind.Instrument,
                    "AudioTrack" => LaneKind.Audio,
                    "MusicTrack" or "InstrumentTrack" => LaneKind.Instrument,
                    "FolderTrack" => LaneKind.Group,
                    _ => (LaneKind?)null,
                };
                if (kind is null) continue;
                var clips = new List<PreviewClip>();
                foreach (var ev in track.Elements("List").Where(l => Id(l) == "Events").SelectMany(l => l.Elements()))
                {
                    var name = Attr(ev, "name");
                    var start = Beats(ev, "start", false);
                    var seconds = InSeconds(ev);
                    var length = Beats(ev, "length", seconds);
                    var offset = Beats(ev, "offset", seconds);
                    var muted = Attr(ev, "mute") == "1";
                    var id = Attr(ev, "clipID");
                    switch (ev.Name.LocalName)
                    {
                        case "AudioEvent":
                        {
                            var (file, fileSeconds) = audio.GetValueOrDefault(id, ("", 0));
                            var found = ArrangementReader.Locate(file, path);
                            if (found is null) missing++;
                            var speed = Num(Attr(ev, "speed")) is { } sp and > 0 ? sp : 1;
                            if (length <= 0 && fileSeconds > 0) length = fileSeconds / speed / secondsPerBeat.Value;
                            var part = new AudioPart(found ?? file, 0, length, seconds ? Num(Attr(ev, "offset")) ?? 0 : offset * secondsPerBeat.Value * speed,
                                                     secondsPerBeat.Value * speed) { Found = found is not null };
                            clips.Add(new PreviewClip(name.Length > 0 ? name : System.IO.Path.GetFileNameWithoutExtension(file), start, length, muted) { Audio = [part] });
                            break;
                        }
                        case "MusicPart" or "NotePart":
                        {
                            var transpose = (int)(Num(Attr(ev, "transpose")) ?? 0);
                            var all = music.TryGetValue(id, out var file) ? MusicxNotes(zip, file) : InlineNotes(ev);
                            var notes = all.Where(n => n.Start >= offset - 1e-9 && n.Start < offset + length)
                                           .Select(n => n with { Start = n.Start - offset, Length = Math.Min(n.Length, offset + length - n.Start), Key = Math.Clamp(n.Key + transpose, 0, 127) })
                                           .ToList();
                            if (length <= 0) length = notes.Select(n => n.Start + n.Length).DefaultIfEmpty(4).Max();
                            clips.Add(new PreviewClip(name.Length > 0 ? name : Attr(track, "name"), start, length, muted) { Notes = notes });
                            break;
                        }
                        case "MusicPatternPart":
                        {
                            var (steps, loop) = music.TryGetValue(id, out var file) ? PatternNotes(zip, file) : ([], 0);
                            var notes = new List<PreviewNote>();
                            if (loop > 0)
                                for (var at = 0.0; at < length - 1e-9; at += loop)
                                    notes.AddRange(steps.Where(n => at + n.Start < length).Select(n => n with { Start = at + n.Start }));
                            clips.Add(new PreviewClip(name.Length > 0 ? name : "Pattern", start, length, muted) { Notes = notes });
                            break;
                        }
                    }
                }
                lanes.Add(new PreviewLane(Attr(track, "name") is { Length: > 0 } n ? n : kind == LaneKind.Audio ? "Audio" : "Instrument", kind.Value)
                {
                    Colour = Colour(Attr(track, "color")),
                    Muted = Attr(track, "mute") == "1",
                    Depth = depth,
                    Clips = clips.OrderBy(c => c.Start).ToList(),
                });
                if (kind == LaneKind.Group && track.Elements("List").FirstOrDefault(l => Id(l) == "Tracks") is { } inner) Walk(inner, depth + 1);
            }
        }
        if (song.Descendants("List").FirstOrDefault(l => Id(l) == "Tracks") is { } tracks) Walk(tracks, 0);
        else if (song.Element("Tracks") is { } plain) Walk(plain, 0);

        // Markers the person placed: not the song's own start and end (types 2 and 3).
        var markers = song.Descendants("MarkerEvent")
            .Where(m => Attr(m, "markerType") is not ("2" or "3") && Attr(m, "name").Length > 0)
            .Select(m => new PreviewMarker(Beats(m, "start", InSeconds(m)), Attr(m, "name")))
            .OrderBy(m => m.Beat).ToList();

        var notes = new List<string>();
        if (lanes.All(l => l.Clips.Count == 0)) notes.Add("Nothing is on this song's timeline yet.");
        if (missing > 0)
            notes.Add($"{missing} audio clip{(missing == 1 ? "'s file isn't" : "s' files aren't")} on this computer, so {(missing == 1 ? "it's" : "they're")} drawn without a waveform.");
        return new Arrangement(view, tempo, beatsPerBar, lanes) { Markers = markers, Notes = notes };
    }

    /// <summary>Studio One's colours are AARRGGBB hex; the preview wants 0xRRGGBB.</summary>
    static int? Colour(string argb) =>
        argb.Length == 8 && int.TryParse(argb[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb) ? rgb : null;

    /// <summary>Notes written straight into a part (as some older songs, and the tests, do).</summary>
    static List<PreviewNote> InlineNotes(XElement part) =>
        part.Descendants("NoteEvent").Select(n => new PreviewNote(Num(Attr(n, "start")) ?? 0, Num(Attr(n, "length")) ?? 0.25,
                                                                  (int)(Num(Attr(n, "pitch")) ?? 60), Velocity(Num(Attr(n, "velocity"))), Attr(n, "mute") == "1"))
            .ToList();

    static int Velocity(double? v) => Math.Clamp((int)Math.Round((v ?? 0.8) <= 1 ? (v ?? 0.8) * 127 : v!.Value), 1, 127);

    /// <summary>
    /// A part's notes from its .musicx file: binary JSON (UBJSON) holding {timeFormat, events: [{start, pitch, length,
    /// velocity, mute}]}, in beats; a note with no start is at 0.
    /// </summary>
    static List<PreviewNote> MusicxNotes(ZipArchive zip, string entry)
    {
        var notes = new List<PreviewNote>();
        if (zip.GetEntry(entry) is not { } e) return notes;
        using var stream = e.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var root = Ubjson.Read(memory.ToArray()) as Dictionary<string, object?>;
        if (root?.GetValueOrDefault("events") is not List<object?> events) return notes;
        var seconds = root.GetValueOrDefault("timeFormat") is 0L;
        foreach (var ev in events.OfType<Dictionary<string, object?>>())
        {
            double N(string key, double fallback) => ev.GetValueOrDefault(key) switch { double d => d, long l => l, _ => fallback };
            if (seconds) continue;   // not seen in Solomon's songs; skipped rather than drawn in the wrong place
            notes.Add(new PreviewNote(N("start", 0), N("length", 0.25), (int)N("pitch", 60), Velocity(N("velocity", 0.8)), N("mute", 0) != 0));
        }
        return notes;
    }

    /// <summary>A pattern part's first variation: its steps as notes, and how many beats it takes before it repeats.</summary>
    static (List<PreviewNote> Notes, double Loop) PatternNotes(ZipArchive zip, string entry)
    {
        if (LoadXml(zip, entry)?.Descendants("PatternPerformance").FirstOrDefault() is not { } p) return ([], 0);
        var step = Num(Attr(p, "stepLength")) is { } sl and > 0 ? sl : 0.25;
        var count = Num(Attr(p, "stepCount")) ?? 16;
        var notes = p.Elements("PatternStep")
            .Select(n => new PreviewNote((Num(Attr(n, "start")) ?? 0) * step, step, (int)(Num(Attr(n, "pitch")) ?? 36), Velocity(Num(Attr(n, "velocity"))), Attr(n, "mute") == "1"))
            .ToList();
        return (notes, count * step);
    }

    /// <summary>Universal Binary JSON, as Studio One writes it: objects, arrays, numbers and strings, big-endian.</summary>
    internal static class Ubjson
    {
        public static object? Read(byte[] data)
        {
            var at = 0;
            try { return Value(data, ref at); }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException) { return null; }
        }

        static object? Value(byte[] d, ref int at)
        {
            var type = (char)d[at++];
            switch (type)
            {
                case '{':
                    var obj = new Dictionary<string, object?>();
                    while (d[at] != '}') { var key = Text(d, ref at); obj[key] = Value(d, ref at); }
                    at++;
                    return obj;
                case '[':
                    var list = new List<object?>();
                    while (d[at] != ']') list.Add(Value(d, ref at));
                    at++;
                    return list;
                case 'S': return Text(d, ref at);
                case 'D': at += 8; return BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(at - 8));
                case 'd': at += 4; return (double)BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(at - 4));
                case 'Z': return null;
                case 'T': return true;
                case 'F': return false;
                default:
                    at--;
                    return Integer(d, ref at);
            }
        }

        static long Integer(byte[] d, ref int at)
        {
            var type = (char)d[at++];
            switch (type)
            {
                case 'i': return (sbyte)d[at++];
                case 'U': return d[at++];
                case 'I': at += 2; return BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(at - 2));
                case 'l': at += 4; return BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(at - 4));
                case 'L': at += 8; return BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(at - 8));
                default: throw new FormatException($"Unknown UBJSON type '{type}'.");
            }
        }

        static string Text(byte[] d, ref int at)
        {
            var length = (int)Integer(d, ref at);
            var text = Encoding.UTF8.GetString(d, at, length);
            at += length;
            return text;
        }
    }
}
