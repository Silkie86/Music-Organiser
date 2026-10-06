using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Export;

public sealed record ExportResult(string Folder, IReadOnlyList<string> Files);

/// <summary>Writes a project's MIDI patterns and plugin presets into an export folder.</summary>
public static class Exporter
{
    static string OutDir(string exportRoot, string project, string kind)
    {
        var dir = Path.Combine(exportRoot, TextScan.SafeFileName(Path.GetFileNameWithoutExtension(project)), kind);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static ExportResult Midi(IProjectReader reader, string project, string exportRoot, double? tempo)
    {
        var clips = reader.ExtractMidi(project);
        var dir = OutDir(exportRoot, project, "MIDI");
        var files = new List<string>();
        for (var i = 0; i < clips.Count; i++)
        {
            var name = $"{i + 1:00} {TextScan.SafeFileName(clips[i].Name)}.mid";
            File.WriteAllBytes(Path.Combine(dir, name), MidiWriter.Write(clips[i], tempo));
            files.Add(name);
        }
        return new ExportResult(dir, files);
    }

    public static ExportResult Presets(IProjectReader reader, string project, string exportRoot)
    {
        var presets = reader.ExtractPresets(project);
        var dir = OutDir(exportRoot, project, "Presets");
        var files = new List<string>();
        for (var i = 0; i < presets.Count; i++)
        {
            var p = presets[i];
            var label = p.Plugin.Length > 0 && p.Plugin != p.Name ? $"{p.Plugin} - {p.Name}" : p.Name;
            var name = $"{i + 1:00} {TextScan.SafeFileName(label)}{p.Extension}";
            File.WriteAllBytes(Path.Combine(dir, name), p.Data);
            files.Add(name);
        }
        return new ExportResult(dir, files);
    }
}
