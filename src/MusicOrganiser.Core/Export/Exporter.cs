using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Export;

public sealed record ExportResult(string Folder, IReadOnlyList<string> Files);

/// <summary>Writes a project's MIDI (one file per channel) and plugin presets into an export folder.</summary>
public static class Exporter
{
    static string OutDir(string exportRoot, string project, string kind)
    {
        var dir = Path.Combine(exportRoot, TextScan.SafeFileName(Path.GetFileNameWithoutExtension(project)), kind);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// An FL Studio project as a DAWproject file Bitwig can open: tracks, notes, plugins with their
    /// settings and the mixer's effects. Returns the file and what couldn't come across.
    /// </summary>
    public static (string File, IReadOnlyList<string> LeftOut) DawProjectFromFl(string project, string exportRoot)
    {
        var name = Path.GetFileNameWithoutExtension(project);
        var result = DawProject.FromFl(Readers.FlStudio.ReadSong(project), name);
        var file = Path.Combine(OutDir(exportRoot, project, "DAWproject"), TextScan.SafeFileName(name) + ".dawproject");
        File.WriteAllBytes(file, result.Bytes);
        return (file, result.Notes);
    }

    /// <summary>One MIDI file per channel (FL Studio) or track, named after it and what plays it.</summary>
    public static ExportResult Midi(IProjectReader reader, string project, string exportRoot, double? tempo) =>
        Midi(reader.ExtractChannels(project), project, exportRoot, tempo);

    public static ExportResult Midi(IEnumerable<MidiChannel> channels, string project, string exportRoot, double? tempo)
    {
        var dir = OutDir(exportRoot, project, "MIDI");
        var files = new List<string>();
        var i = 0;
        foreach (var channel in channels)
        {
            var name = $"{++i:00} {TextScan.SafeFileName(channel.Label)}.mid";
            File.WriteAllBytes(Path.Combine(dir, name), MidiWriter.Write(channel.ToClip(), tempo));
            files.Add(name);
        }
        return new ExportResult(dir, files);
    }

    /// <summary>
    /// Several channels in one multi-track MIDI file, one track per channel in this order, so a
    /// DAW that imports it lays them out as tracks the same way.
    /// </summary>
    public static string MidiTogether(IReadOnlyList<MidiChannel> channels, string project, string exportRoot, double? tempo)
    {
        var dir = OutDir(exportRoot, project, "MIDI");
        var clip = new MidiClip { Name = Path.GetFileNameWithoutExtension(project), Ppq = channels[0].Ppq };
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in channels)
        {
            var name = c.Label;
            for (var n = 2; !used.Add(name); n++) name = $"{c.Label} {n}";   // same-named channels stay separate tracks
            clip.Track(name).AddRange(c.Ppq == clip.Ppq ? c.Notes : c.Notes.Select(x => x with
            {
                Start = x.Start * clip.Ppq / c.Ppq, Length = Math.Max(1, x.Length * clip.Ppq / c.Ppq),
            }));
        }
        var file = Path.Combine(dir, channels.Count == 1 ? $"{TextScan.SafeFileName(channels[0].Label)}.mid"
                                                         : $"{TextScan.SafeFileName(clip.Name)} - {channels.Count} tracks.mid");
        File.WriteAllBytes(file, MidiWriter.Write(clip, tempo));
        return file;
    }

    /// <summary>
    /// One file per channel, named after it (no number in front, so the DAW's track names stay
    /// clean), in this order. Same-named channels get " 2", " 3" so none overwrites another.
    /// </summary>
    public static IReadOnlyList<string> MidiFiles(IReadOnlyList<MidiChannel> channels, string project, string exportRoot, double? tempo)
    {
        var dir = OutDir(exportRoot, project, "MIDI");
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (var c in channels)
        {
            var name = TextScan.SafeFileName(c.Label);
            var unique = name;
            for (var n = 2; !used.Add(unique); n++) unique = $"{name} {n}";
            var file = Path.Combine(dir, unique + ".mid");
            File.WriteAllBytes(file, MidiWriter.Write(c.ToClip(), tempo));
            files.Add(file);
        }
        return files;
    }

    /// <summary>Just one channel, named as in the full export.</summary>
    public static string MidiChannel(MidiChannel channel, string project, string exportRoot, double? tempo)
    {
        var dir = OutDir(exportRoot, project, "MIDI");
        var file = Path.Combine(dir, $"{TextScan.SafeFileName(channel.Label)}.mid");
        File.WriteAllBytes(file, MidiWriter.Write(channel.ToClip(), tempo));
        return file;
    }

    public static ExportResult Presets(IProjectReader reader, string project, string exportRoot) =>
        Presets(reader.ExtractPresets(project), project, exportRoot);

    /// <summary>
    /// Writes presets as "Plugin - Name.ext". Exporting again overwrites the same files,
    /// so exporting one plugin after another builds up a single folder without duplicates.
    /// With <paramref name="bitwig"/> templates, a plugin that has one also gets a Bitwig
    /// preset (.bwpreset) next to its plugin preset.
    /// </summary>
    public static ExportResult Presets(IEnumerable<PresetBlob> presets, string project, string exportRoot,
                                       IReadOnlyDictionary<string, BitwigPreset.Template>? bitwig = null)
    {
        var dir = OutDir(exportRoot, project, "Presets");
        var files = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in presets)
        {
            var label = p.Plugin.Length > 0 && p.Plugin != p.Name ? $"{p.Plugin} - {p.Name}" : p.Name;
            if (p.Where.Length > 0) label += $" ({p.Where.Replace(" · ", " ")})";
            label = TextScan.SafeFileName(label);
            var name = label + p.Extension;
            for (var n = 2; !used.Add(name); n++) name = $"{label} {n}{p.Extension}";
            File.WriteAllBytes(Path.Combine(dir, name), p.Data);
            files.Add(name);
            if (bitwig is not null && BitwigPreset.For(p, bitwig) is { } template)
            {
                try
                {
                    var bw = Path.GetFileNameWithoutExtension(name) + ".bwpreset";
                    File.WriteAllBytes(Path.Combine(dir, bw), BitwigPreset.Build(template, p, p.Name));
                    files.Add(bw);
                }
                catch (InvalidDataException) { }   // the plugin preset above is still there
            }
        }
        return new ExportResult(dir, files);
    }
}
