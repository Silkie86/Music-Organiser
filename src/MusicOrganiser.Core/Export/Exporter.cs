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
    /// </summary>
    public static ExportResult Presets(IEnumerable<PresetBlob> presets, string project, string exportRoot)
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
        }
        return new ExportResult(dir, files);
    }
}
