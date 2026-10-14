using System.Text;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Plugins;

namespace MusicOrganiser.Core.DawLink;

/// <summary>
/// Sends things from the app into the project open in Bitwig, through the Music Organiser script: an FL Studio
/// channel (its sample in a Sampler, or its plugin with the settings the project saved, and its notes as a clip
/// in the clip launcher), or a kit on a Drum Machine's pads. Bitwig needs "Let the app make changes" turned on.
/// </summary>
public static class BitwigSend
{
    public static readonly Guid Sampler = new("468bc14b-b2e7-45a1-9666-e83117fe404e");
    public static readonly Guid NoteTranspose = new("0815cd9e-3a31-4429-a268-dabd952a3b68");
    /// <summary>Bitwig's name for its Drum Machine, by which the app finds the device's ID in Bitwig's own files.</summary>
    public const string DrumMachine = "Drum Machine";
    public const string ChangesOff = "Bitwig isn't letting the app make changes. Turn on \"Let the app make changes\" in Bitwig " +
                                     "(Settings > Controllers > Music Organiser), then send it again.";

    /// <summary>Where the files Bitwig loads are written: the project's export folder, as Export writes them.</summary>
    /// <param name="Templates">Presets Bitwig saved, to build Bitwig presets of the project's plugin settings from.</param>
    public sealed record Files(string Project, string ExportRoot, double? Tempo,
                               IReadOnlyDictionary<string, BitwigPreset.Template> Templates);

    /// <summary>What went into Bitwig for one channel.</summary>
    /// <param name="Track">Its new track's index in Bitwig's track listing.</param>
    /// <param name="Instrument">What plays it there, e.g. "Sampler" or "Serum with the project's settings"; empty for nothing.</param>
    /// <param name="Clip">Whether its notes went in, as a clip in the track's first empty launcher slot.</param>
    /// <param name="Problems">What didn't come across, in words to show.</param>
    public sealed record SentChannel(string Name, int Track, string Instrument, bool Clip, IReadOnlyList<string> Problems);

    /// <summary>The settings the project saved for the plugin on this channel, if it has any.</summary>
    public static PresetBlob? PresetFor(MidiChannel channel, IEnumerable<PresetBlob> presets)
    {
        var instruments = presets.Where(p => p.Kind == PluginKind.Instrument).ToList();
        if (channel.Channel is { } index && instruments.Any(p => p.Channel is not null))
            return instruments.FirstOrDefault(p => p.Channel == index);
        // Presets that don't say which channel they're on: the same plugin, saved under the channel's name.
        var plugin = LibraryDb.NameKey(channel.Plugin);
        return plugin.Length == 0 ? null
            : instruments.FirstOrDefault(p => LibraryDb.NameKey(p.Plugin) == plugin && p.Name == channel.Name);
    }

    /// <summary>
    /// Sends one channel: a new instrument track named after it, with its sample in a Sampler (tuned to its root
    /// key and pitch) or its plugin (loaded with the project's settings when Bitwig takes them, else by its ID
    /// from the plugin scan), and its notes as a clip.
    /// </summary>
    /// <param name="preset">The plugin's settings from the project (<see cref="PresetFor"/>).</param>
    /// <param name="installed">The plugin as the plugin scan found it, to load by ID if the settings don't load.</param>
    public static async Task<SentChannel> ChannelAsync(BitwigLink link, MidiChannel channel, PresetBlob? preset,
                                                       IReadOnlyList<ScannedPlugin> installed, Files files, CancellationToken ct = default)
    {
        CheckChanges(link);
        var problems = new List<string>();
        var track = await link.NewTrackAsync(channel.Name, ct: ct);
        var instrument = channel.SamplePath.Length > 0 ? await SamplerAsync(link, track, channel, problems, ct)
            : channel.Plugin.Length > 0 ? await PluginAsync(link, track, channel, preset, installed, files, problems, ct)
            : "";
        if (channel.SamplePath.Length == 0 && channel.Plugin.Length == 0) problems.Add("nothing plays it in the project, so its track has no instrument");

        var clip = false;
        if (channel.Notes.Count > 0)
        {
            try
            {
                await link.AddClipAsync(track, Exporter.MidiChannel(channel, files.Project, files.ExportRoot, files.Tempo), ct: ct);
                clip = true;
            }
            catch (Exception e) when (e is DawLinkException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"its notes didn't go in: {e.Message}");
            }
        }
        return new SentChannel(channel.Name, track, instrument, clip, problems);
    }

    static async Task<string> SamplerAsync(BitwigLink link, int track, MidiChannel c, List<string> problems, CancellationToken ct)
    {
        var file = Path.GetFileName(c.SamplePath.Replace('\\', '/'));
        if (!File.Exists(c.SamplePath))
        {
            problems.Add($"its sample isn't at {c.SamplePath}");
            return "";
        }
        var sampler = await link.InsertFileAsync(track, c.SamplePath, ct: ct);
        if (sampler is not { Added: true, Index: { } at })
        {
            problems.Add($"Bitwig didn't load {file}");
            return "";
        }
        // FL Studio plays the sample at its own pitch on the channel's root key; a new Sampler does on C3 (the
        // same note, 60). Its Transpose makes up the difference, and takes the pitch knob too.
        var semitones = FlStudioReader.DefaultRoot - c.RootKey + c.PitchCents / 100.0;
        if (semitones != 0)
            await TuneAsync(link, track, at, semitones,
                $"set the Sampler's root key to {DawProject.NoteName(c.RootKey)}" +
                (c.PitchCents == 0 ? "" : $" and tune it {c.PitchCents / 100.0:+0.##;-0.##} semitones"), problems, ct);
        return "Sampler";
    }

    static async Task<string> PluginAsync(BitwigLink link, int track, MidiChannel c, PresetBlob? preset,
                                          IReadOnlyList<ScannedPlugin> installed, Files files, List<string> problems,
                                          CancellationToken ct)
    {
        var name = c.Plugin;
        BitwigInserted? added = null;
        var withSettings = false;
        if (preset is { Native: false })
        {
            List<string> settings;
            try { settings = PresetFiles(preset, files); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                problems.Add($"its settings couldn't be saved for Bitwig: {e.Message.TrimEnd('.')}");
                settings = [];
            }
            foreach (var file in settings)
            {
                added = await link.InsertFileAsync(track, file, ct: ct);
                if (!added.Added) continue;
                withSettings = true;
                break;
            }
        }
        if (added is not { Added: true })
        {
            foreach (var plugin in ByPreference(installed, preset).Take(2))
            {
                added = await InsertAsync(link, track, plugin, ct);
                if (!added.Added) continue;
                if (preset is { Native: false })
                    problems.Add($"{name} has its default sound: Bitwig didn't take the settings saved in the project");
                else if (preset is { Native: true })
                    problems.Add($"{name} has its default sound: its settings are saved the FL Studio way, which Bitwig can't read");
                break;
            }
        }
        if (added is not { Added: true, Index: { } at })
        {
            problems.Add(installed.Count > 0 ? $"Bitwig didn't load {name}"
                : preset is { Native: true } ? $"{name} is one of FL Studio's own plugins, which Bitwig can't load"
                : $"couldn't load {name}: the app hasn't found it among your installed plugins (Plugins screen)");
            return "";
        }

        // FL Studio plays a plugin's notes as they are when the channel's root key is C5 (60); a moved root key
        // transposes them, as does the pitch knob. A Note Transpose before the plugin does the same in Bitwig.
        var shift = FlStudioReader.DefaultRoot - c.RootKey + (int)Math.Round(c.PitchCents / 100.0);
        if (shift != 0)
        {
            var byHand = $"put a Note Transpose before {name}, set to {shift:+0;-0} semitones";
            var transpose = await link.InsertBitwigDeviceAsync(track, NoteTranspose, atStart: true, ct);
            if (transpose.Added) await TuneAsync(link, track, 0, shift, byHand, problems, ct);
            else problems.Add($"Bitwig didn't add a Note Transpose: {byHand}");
        }
        if (c.PitchCents % 100 != 0)
            problems.Add($"its pitch knob ({c.PitchCents / 100.0:+0.##;-0.##} semitones) is rounded to whole semitones");
        var device = added.Device?.Name is { Length: > 0 } shown ? shown : name;
        return withSettings ? $"{device} with the project's settings" : device;
    }

    static async Task TuneAsync(BitwigLink link, int track, int device, double semitones, string byHand, List<string> problems,
                                CancellationToken ct)
    {
        try
        {
            var pitch = await link.TransposeAsync(track, device, semitones, ct);
            if (Math.Abs(pitch.Value - semitones) > 0.01)
                problems.Add($"Bitwig set the pitch to {pitch.Display} rather than {semitones:+0.##;-0.##} semitones: {byHand}");
        }
        catch (DawLinkException e)
        {
            problems.Add($"its pitch wasn't set ({e.Message.TrimEnd('.')}): {byHand}");
        }
    }

    /// <summary>
    /// The project's settings as files Bitwig can load, best first: a Bitwig preset (when a template for the plugin
    /// was found), then the plugin's own format. A VST2 program goes in as a one-program bank, the form Bitwig
    /// restores.
    /// </summary>
    static List<string> PresetFiles(PresetBlob preset, Files files)
    {
        var written = Exporter.Presets([preset], files.Project, files.ExportRoot, files.Templates);
        var paths = written.Files.Select(f => Path.Combine(written.Folder, f)).ToList();
        var best = paths.Where(p => p.EndsWith(".bwpreset", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var p in paths)
        {
            switch (Path.GetExtension(p).ToLowerInvariant())
            {
                case ".vstpreset":
                    best.Add(p);
                    break;
                case ".fxp" when Fxp.ToBank(preset.Data) is var bank && !ReferenceEquals(bank, preset.Data):
                    var fxb = Path.ChangeExtension(p, ".fxb");
                    File.WriteAllBytes(fxb, bank);
                    best.Add(fxb);
                    break;
            }
        }
        return best;
    }

    /// <summary>The installed copies to try: the format the project used first, then instruments before anything else.</summary>
    static IEnumerable<ScannedPlugin> ByPreference(IEnumerable<ScannedPlugin> installed, PresetBlob? preset)
    {
        var used = preset is null ? "" : BitwigPreset.StateFormat(preset.Extension);
        return installed.Where(p => p.Id.Length > 0)
                        .OrderBy(p => p.Format.Equals(used, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(p => p.Kind is "instrument" or "" ? 0 : 1)
                        .ThenBy(p => p.Error.Length > 0 ? 1 : 0)
                        .ThenBy(p => p.Format switch { "CLAP" => 0, "VST3" => 1, _ => 2 });
    }

    static Task<BitwigInserted> InsertAsync(BitwigLink link, int track, ScannedPlugin plugin, CancellationToken ct) =>
        plugin.Format.ToUpperInvariant() switch
        {
            "VST2" when BitwigLink.Vst2Id(plugin.Id) is { } id => link.InsertVst2Async(track, id, ct: ct),
            "VST3" or "CLAP" => link.InsertPluginAsync(track, plugin.Format, plugin.Id, ct: ct),
            _ => Task.FromResult(new BitwigInserted(false, null, null)),
        };

    // ---- Kits ----

    /// <summary>A sound for a kit: the pad's name, the kind of sound it's for ("Kick", "Closed hat"...) and the sample.</summary>
    public sealed record KitSound(string Name, string Type, string Path);

    /// <summary>
    /// General MIDI's drum notes, which drum MIDI files and Bitwig's own kits follow: kick on C1 (36), snare on D1,
    /// clap on D#1, closed hat on F#1, open hat on A#1, crash on C#2.
    /// </summary>
    static readonly (string Type, int Note)[] DrumNotes =
    [
        ("Kick", 36), ("Snare", 38), ("Clap", 39), ("Closed hat", 42), ("Hi-hat", 42), ("Tom", 45), ("Open hat", 46),
        ("Cymbal", 49),
    ];

    /// <summary>
    /// Where each sound goes on a Drum Machine: the General MIDI note for its kind, so drum MIDI plays the right pads,
    /// and the rest on the free notes from C1 up.
    /// </summary>
    public static IReadOnlyList<BitwigPad> KitPads(IEnumerable<KitSound> sounds)
    {
        var list = sounds.ToList();
        var notes = new int?[list.Count];
        var taken = new HashSet<int>();
        for (var i = 0; i < list.Count; i++)
        {
            var type = list[i].Type.Length > 0 ? list[i].Type : list[i].Name;
            var gm = DrumNotes.Where(d => d.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).Select(d => (int?)d.Note).FirstOrDefault();
            if (gm is { } note && taken.Add(note)) notes[i] = note;
        }
        var next = 36;
        for (var i = 0; i < list.Count; i++)
        {
            if (notes[i] is not null) continue;
            while (taken.Contains(next)) next++;
            if (next > 127) break;
            notes[i] = next;
            taken.Add(next);
        }
        return list.Select((s, i) => (s, i)).Where(x => notes[x.i] is not null)
                   .Select(x => new BitwigPad(notes[x.i]!.Value, x.s.Path, x.s.Name)).OrderBy(p => p.Note).ToList();
    }

    /// <summary>What happened to a kit: its new track (null when it went on the selected Drum Machine) and its pads.</summary>
    /// <param name="DrumMachine">False when Bitwig didn't add a Drum Machine to the new track for the ID given.</param>
    public sealed record SentKit(int? Track, bool DrumMachine, IReadOnlyList<BitwigPad> Filled, IReadOnlyList<BitwigPad> Skipped,
                                 IReadOnlyList<BitwigPad> Failed);

    /// <summary>
    /// Puts a kit on a Drum Machine: a new one on a new track named <paramref name="name"/> when Bitwig's Drum
    /// Machine ID is known (<see cref="FindDeviceId"/>), else the Drum Machine selected in Bitwig, where pads that
    /// already have something on them are left alone.
    /// </summary>
    public static async Task<SentKit> KitAsync(BitwigLink link, string name, IReadOnlyList<BitwigPad> pads, Guid? drumMachine,
                                               CancellationToken ct = default)
    {
        CheckChanges(link);
        int? track = null;
        BitwigPadsFilled filled;
        if (drumMachine is { } id)
        {
            track = await link.NewTrackAsync(name, ct: ct);
            // An ID Bitwig doesn't know adds nothing; one learned wrongly adds some other device.
            if (await link.InsertBitwigDeviceAsync(track.Value, id, ct: ct) is not { Added: true, Index: { } at } added ||
                added.Device is { Name: var shown } && shown != DrumMachine)
                return new SentKit(track, false, [], [], pads);
            filled = await link.FillPadsAsync(pads, track, at, ct);
        }
        else
        {
            filled = await link.FillPadsAsync(pads, ct: ct);
        }
        IReadOnlyList<BitwigPad> Of(IReadOnlyList<int> notes) => pads.Where(p => notes.Contains(p.Note)).ToList();
        return new SentKit(track, true, Of(filled.Filled), Of(filled.Skipped), Of(filled.Failed));
    }

    /// <summary>
    /// The ID Bitwig saves one of its devices under (say the Drum Machine), from the first of these Bitwig files
    /// (projects or presets) that has the device in it, or null if none does.
    /// </summary>
    public static Guid? FindDeviceId(string device, IEnumerable<string> files, CancellationToken ct = default)
    {
        // The name is in the file as text (UTF-8, or UTF-16 for some), so most files can be passed over unread.
        byte[] utf8 = Encoding.UTF8.GetBytes(device), utf16 = Encoding.BigEndianUnicode.GetBytes(device);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(file).Length > 256 << 20) continue;
                var raw = File.ReadAllBytes(file);
                if (!BitwigFormat.IsBitwig(raw) || (raw.AsSpan().IndexOf(utf8) < 0 && raw.AsSpan().IndexOf(utf16) < 0)) continue;
                foreach (var o in BitwigFormat.Decode(raw).Objects)
                    if (o.Class == 0x40 && o.Str(0x9a) == device && Guid.TryParse(o[0x99]?.ToString(), out var id)) return id;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        return null;
    }

    /// <summary>
    /// Bitwig files to look for a device's ID in: the presets in Bitwig's user folder, then those in the sound
    /// packages Bitwig installed, with Drum Machine presets first.
    /// </summary>
    public static IEnumerable<string> BitwigPresets(CancellationToken ct = default)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var packages = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bitwig Studio", "installed-packages")
            : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support", "Bitwig", "Bitwig Studio", "installed-packages")
            : Path.Combine(home, ".BitwigStudio", "installed-packages");
        var found = Parallelism.FindFiles([.. BitwigPreset.DefaultFolders(), packages],
            f => f.EndsWith(".bwpreset", StringComparison.OrdinalIgnoreCase), (name, _) => name.StartsWith('.'), ct: ct);
        return found.OrderBy(f => f.Contains(DrumMachine, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
    }

    static void CheckChanges(BitwigLink link)
    {
        if (link.Hello.Link < BitwigLink.LinkVersion) throw new DawLinkException(BitwigLink.OldScript);
        if (!link.Hello.Changes) throw new DawLinkException(ChangesOff);
    }
}
