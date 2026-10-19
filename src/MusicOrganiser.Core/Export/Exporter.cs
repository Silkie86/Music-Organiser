using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Plugins.Serum;

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
    /// settings, the mixer's effects and automation. Returns the file, what couldn't come across, and
    /// the steps left to do in Bitwig, which are also saved next to it as "... - Bitwig setup.txt".
    /// </summary>
    public static (string File, IReadOnlyList<string> LeftOut, IReadOnlyList<string> Setup) DawProjectFromFl(string project, string exportRoot)
    {
        var name = Path.GetFileNameWithoutExtension(project);
        var result = DawProject.FromFl(Readers.FlStudio.ReadSong(project), name);
        var dir = OutDir(exportRoot, project, "DAWproject");
        var file = Path.Combine(dir, TextScan.SafeFileName(name) + ".dawproject");
        File.WriteAllBytes(file, result.Bytes);
        var setupFile = Path.Combine(dir, TextScan.SafeFileName(name) + " - Bitwig setup.txt");
        if (result.Setup.Count > 0 || result.Notes.Count > 0)
        {
            var lines = new List<string> { $"Finishing {name} in Bitwig", "" };
            if (result.Setup.Count > 0)
                lines.AddRange(["DAWproject can't carry a Sampler's sample or root key, or FL's root key on plugins, so set them here.",
                                .. result.Setup.Select(s => "  " + s), ""]);
            if (result.Notes.Count > 0)
                lines.AddRange(["Not carried over:", .. result.Notes.Select(n => "  " + n)]);
            File.WriteAllLines(setupFile, lines);
        }
        else if (File.Exists(setupFile)) File.Delete(setupFile);
        return (file, result.Notes, result.Setup);
    }

    /// <summary>
    /// An FL Studio project as a native Bitwig project, built from <paramref name="template"/> (a project
    /// Bitwig saved; see <see cref="BitwigProject"/>). It's saved as Song/Song.bwproject, the layout Bitwig
    /// expects, so it opens without asking for a project folder. What couldn't come across is returned and
    /// saved next to it as "... - not carried over.txt". With <paramref name="stepsUntilNextNote"/>, steps on a
    /// Sampler don't end with their sample (see <see cref="BitwigProject.FromFl"/>).
    /// </summary>
    /// <param name="upgrade">Swap older versions of the template's VST3 synth for it (see <see cref="BitwigProject.FromFl"/>).</param>
    /// <param name="saveAs">The name to save it under (see <see cref="BitwigName"/>), or null for the project's own.</param>
    /// <param name="serum2User">Serum 2's user presets folder, for Serum 1's sounds; the usual one (<see cref="Serum1To2.Serum2UserFolder"/>) unless given.</param>
    public static (string File, IReadOnlyList<string> LeftOut) BitwigFromFl(string project, string exportRoot, byte[] template,
                                                                            bool stepsUntilNextNote = false, bool upgrade = false,
                                                                            string? saveAs = null, string? serum2User = null)
    {
        var name = TextScan.SafeFileName(saveAs ?? Path.GetFileNameWithoutExtension(project));
        var result = BitwigProject.FromFl(Readers.FlStudio.ReadSong(project), template, stepsUntilNextNote: stepsUntilNextNote, upgrade: upgrade);
        var dir = Path.Combine(OutDir(exportRoot, project, "Bitwig"), name);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name + ".bwproject");
        File.WriteAllBytes(file, result.Bytes);
        // The swapped synths' sounds as presets for the older version, the newer one's browser opens them too:
        // the way back if the newer one didn't take them.
        if (result.Upgraded is { Count: > 0 } upgraded)
        {
            var sounds = Path.Combine(dir, BitwigProject.UpgradedFolder);
            Directory.CreateDirectory(sounds);
            foreach (var (channel, sound) in upgraded)
                File.WriteAllBytes(Path.Combine(sounds, $"{TextScan.SafeFileName(channel)} ({TextScan.SafeFileName(sound.Plugin)}){sound.Extension}"), sound.Data);
        }
        // Serum 1's sounds also go where Serum 2's browser finds them, as Fix does, named after the FL song.
        IReadOnlyList<string> notes = result.Notes;
        if (Serum1To2.KeepOriginals(Path.GetFileNameWithoutExtension(project), result.Serum1 ?? [], serum2User ?? Serum1To2.Serum2UserFolder()) is { } kept)
            notes = [.. notes, kept];
        var notesFile = Path.Combine(dir, name + " - not carried over.txt");
        if (notes.Count > 0)
            File.WriteAllLines(notesFile, [$"What didn't come across from FL Studio to Bitwig for {name}", "", .. notes.Select(n => "  " + n)]);
        else if (File.Exists(notesFile)) File.Delete(notesFile);
        return (file, notes);
    }

    /// <summary>
    /// The name an FL Studio project's Bitwig copy is saved under: the project's own, or "Name (2)", "Name (3)" and so
    /// on while Bitwig may have a copy with that name open (<paramref name="open"/>). Opening a project Bitwig already
    /// has open only brings that copy forward, so a new convert saved over it would look unchanged.
    /// </summary>
    public static string BitwigName(string project, Func<string, bool> open)
    {
        var name = TextScan.SafeFileName(Path.GetFileNameWithoutExtension(project));
        if (!open(name)) return name;
        for (var n = 2; n < 1000; n++)
            if ($"{name} ({n})" is var numbered && !open(numbered)) return numbered;
        return name;
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
