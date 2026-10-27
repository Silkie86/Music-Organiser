using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MusicOrganiser.Core.Parsers;

/// <summary>
/// Studio One / Fender Studio Pro .song reader.
///
/// A .song file is a zip archive of XML documents plus plugin preset files:
///   Song/song.xml        tracks, parts, notes, tempo
///   Song/mediapool.xml   audio files used by the song
///   Devices/*.xml        instruments (audiosynthfolder) and mixer inserts (audiomixer)
///   Presets/**           stored plugin states (.vstpreset, .fxp, .aupreset, .preset)
///   metainfo.xml         title, tempo, application version
/// The XML relies on generic &lt;Attributes x:id="..."&gt; elements, so parsing is
/// deliberately tolerant and looks for known attribute names anywhere in the tree.
/// </summary>
public sealed partial class StudioOneReader : IProjectReader
{
    public string Daw => "Studio One";
    public int Version => 2;   // 2: each insert's plugin by its own name and ID, not the name the insert shows; Studio One's own effects and Note FX (2026-10-27)
    public IReadOnlyList<string> Extensions { get; } = [".song"];

    static readonly string[] PresetExtensions = [".vstpreset", ".fxp", ".fxb", ".aupreset", ".preset", ".multipreset"];
    const int MidiPpq = 960;

    static XElement? LoadXml(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open());
        var text = reader.ReadToEnd();
        // Studio One uses an undeclared "x:" prefix. Drop it from element names and
        // turn prefixed attributes into plain ones (x:id → x_id) so the XML parses.
        text = ElementPrefix().Replace(text, "<$1");
        text = AttributePrefix().Replace(text, "$1$2_$3=");
        try { return XElement.Parse(text); }
        catch (System.Xml.XmlException) { return null; }
    }

    static string Attr(XElement e, string name) => e.Attribute(name)?.Value ?? "";

    static string Id(XElement e) => e.Attribute("x_id")?.Value ?? Attr(e, "id");

    static string UrlToPath(string url)
    {
        if (!url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return Uri.UnescapeDataString(url);
        var p = Uri.UnescapeDataString(url["file:".Length..].TrimStart('/'));
        // file:///C:/... → C:/...   file:///Users/... → /Users/...
        return DriveLetter().IsMatch(p) ? p.Replace('/', '\\') : "/" + p;
    }

    static double? Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public ProjectInfo Read(string path)
    {
        var info = new ProjectInfo { Daw = Daw, Confidence = Confidence.Partial, CanExtractMidi = true, CanExtractPresets = true };
        using var zip = ZipFile.OpenRead(path);

        if (LoadXml(zip, "metainfo.xml") is { } meta)
        {
            foreach (var e in meta.Descendants())
            {
                var value = Attr(e, "value");
                switch (Id(e))
                {
                    case "Document:Generator": info.Version = value; break;
                    case "Media:Title": info.Title = value; break;
                    case "Media:Tempo": info.Tempo = Num(value); break;
                }
            }
        }

        if (LoadXml(zip, "Song/mediapool.xml") is { } pool)
        {
            foreach (var e in pool.DescendantsAndSelf())
            {
                var url = Attr(e, "url");
                if (url.Length > 0 && TextScan.IsAudioPath(url))
                    info.Samples.Add(UrlToPath(url));
            }
        }

        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("Devices/") && e.FullName.EndsWith(".xml")))
        {
            if (LoadXml(zip, entry.FullName) is not { } root) continue;
            var kind = entry.Name.Contains("synth", StringComparison.OrdinalIgnoreCase) ? PluginKind.Instrument : PluginKind.Effect;
            foreach (var e in root.DescendantsAndSelf())
            {
                var name = Attr(e, "name").Trim();
                if (name.Length == 0) continue;
                // Each insert keeps a note of its plugin, "classInfo": the plugin's own name and ID, and whether it's a VST2, a
                // VST3 or one of Studio One's own. Its "deviceData" has the name the insert shows, which Studio One numbers
                // ("FabFilter Pro-Q 2 2", "Kontakt 5 16out 2") and you can change: read by that, plugins you have showed as
                // missing (Solomon's "Click Clack", 27 Oct 2026). An insert without the note goes by its own name.
                if (e.Attribute("classID") is { } classId)
                    info.Plugins.Add(Plugin(e, name, classId.Value, kind));
                else if (Id(e) == "deviceData" && e.Parent?.Descendants().Any(d => d.Attribute("classID") is not null) != true)
                    info.Plugins.Add(Plugin(e, name, e.Parent?.Elements().FirstOrDefault(u => Id(u) == "deviceClassID") is { } u ? Attr(u, "uid") : "", kind));
            }
        }

        var presets = zip.Entries.Count(e => PresetExtensions.Any(x => e.FullName.EndsWith(x, StringComparison.OrdinalIgnoreCase)));
        info.Details["Stored plugin states"] = presets.ToString();

        if (LoadXml(zip, "Song/song.xml") is { } song)
        {
            foreach (var e in song.DescendantsAndSelf())
            {
                if (e.Name.LocalName is "MediaTrack" or "AudioTrack" or "MusicTrack" or "Track" && Attr(e, "name") is { Length: > 0 } t)
                    info.Tracks.Add(t);
                if (info.Tempo is null && Num(Attr(e, "tempo")) is { } tempo)
                    info.Tempo = tempo;
            }
            info.Details["Notes"] = song.Descendants("NoteEvent").Count().ToString();
        }
        else
        {
            info.Warnings.Add("Song/song.xml is missing or unreadable.");
        }
        return info.Dedupe();
    }

    /// <summary>A plugin from Studio One's note of it, with its ID as the plugin scanner writes it.</summary>
    static PluginRef Plugin(XElement e, string name, string classId, PluginKind kind)
    {
        var vendor = Attr(e, "vendor");
        var format = FormatOf(e, vendor);
        var id = PluginCatalog.ClassId(classId);
        // A VST2 has no class ID of its own: Studio One makes one with the plugin's four letters inside.
        if (format == "VST2") id = PluginCatalog.Vst2IdIn(id) ?? "";
        return new PluginRef(name, vendor, KindOf(e) ?? kind, format, "", id);
    }

    static PluginKind? KindOf(XElement e) => Attr(e, "category") switch
    {
        "AudioSynth" => PluginKind.Instrument,
        "AudioEffect" or "AudioMixEffect" => PluginKind.Effect,
        "MusicEffect" => PluginKind.NoteEffect,
        _ => null,
    };

    static string FormatOf(XElement e, string vendor)
    {
        // subCategory starts with it: "VST2", "VST3/Delay", "(Native)/EQ".
        var sub = Attr(e, "subCategory");
        if (sub.StartsWith("(Native)", StringComparison.Ordinal)) return "Native";
        foreach (var key in new[] { "subCategory", "deviceClass", "category", "type" })
        {
            var v = Attr(e, key).ToLowerInvariant();
            if (v.Contains("vst3")) return "VST3";
            if (v.Contains("vst")) return "VST2";
            if (v.Contains("audiounit")) return "AU";
            if (v.Contains("clap")) return "CLAP";
        }
        // Note FX (the Arpeggiator, Chorder, Repeater) are all Studio One's own, and say nothing more.
        if (Attr(e, "category") == "MusicEffect" && sub.Length == 0) return "Native";
        return vendor.Contains("PreSonus", StringComparison.OrdinalIgnoreCase) ? "Native" : "";
    }

    /// <summary>
    /// Notes from each instrument part. Positions are read as quarter notes unless
    /// the part says its time format is seconds, in which case the song tempo is
    /// used to convert. Studio One has changed this between versions, so the UI
    /// labels Studio One MIDI export as experimental.
    /// </summary>
    public IReadOnlyList<MidiClip> ExtractMidi(string path)
    {
        var tempo = Read(path).Tempo ?? 120.0;
        using var zip = ZipFile.OpenRead(path);
        var clips = new List<MidiClip>();
        if (LoadXml(zip, "Song/song.xml") is not { } song) return clips;

        foreach (var track in song.DescendantsAndSelf().Where(e => e.Name.LocalName is "MusicTrack"))
        {
            var trackName = Attr(track, "name") is { Length: > 0 } n ? n : "Track";
            var parts = track.Descendants().Where(e => e.Name.LocalName is "MusicPart" or "NotePart").ToList();
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var seconds = (Attr(part, "timeFormat") + Attr(track, "timeFormat")).Contains("seconds", StringComparison.OrdinalIgnoreCase);
                double Beats(double v) => seconds ? v * tempo / 60 : v;
                var clip = new MidiClip { Name = Attr(part, "name") is { Length: > 0 } pn ? pn : $"{trackName} part {i + 1}", Ppq = MidiPpq };
                var notes = clip.Track(trackName);
                foreach (var ev in part.Descendants("NoteEvent"))
                {
                    if (Num(Attr(ev, "start")) is not { } start) continue;
                    var length = Num(Attr(ev, "length")) ?? 0.25;
                    var key = (int)(Num(Attr(ev, "pitch")) ?? 60);
                    var vel = Num(Attr(ev, "velocity")) ?? 0.8;
                    var midiVel = (int)Math.Round(vel <= 1 ? vel * 127 : vel);
                    notes.Add(new NoteEvent((long)Math.Round(Beats(start) * MidiPpq),
                        Math.Max(1, (long)Math.Round(Beats(length) * MidiPpq)),
                        Math.Clamp(key, 0, 127), Math.Clamp(midiVel, 1, 127)));
                }
                if (notes.Count > 0) clips.Add(clip);
            }
        }
        return clips;
    }

    public IReadOnlyList<PresetBlob> ExtractPresets(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var presets = new List<PresetBlob>();
        foreach (var entry in zip.Entries)
        {
            var ext = Path.GetExtension(entry.Name);
            if (!PresetExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var folder = entry.FullName.Split('/') is { Length: > 1 } bits ? bits[^2] : "";
            presets.Add(new PresetBlob(Path.GetFileNameWithoutExtension(entry.Name), folder, ext, ms.ToArray()));
        }
        return presets;
    }

    [GeneratedRegex(@"<(/?)\w+:")]
    private static partial Regex ElementPrefix();

    [GeneratedRegex(@"(\s)(\w+):(\w+)=")]
    private static partial Regex AttributePrefix();

    [GeneratedRegex(@"^[A-Za-z]:/")]
    private static partial Regex DriveLetter();
}
