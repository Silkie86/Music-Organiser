using System.Diagnostics;
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
    public sealed record SentChannel(string Name, int Track, string Instrument, bool Clip, IReadOnlyList<string> Problems)
    {
        /// <summary>FL Studio's knobs set on the plugin in Bitwig, when they were given.</summary>
        public KnobCopy? Knobs { get; init; }

        /// <summary>Whether the app read the new track back from Bitwig afterwards; anything it found missing is in the problems.</summary>
        public bool Checked { get; init; }
    }

    /// <summary>FL Studio's knobs, set on the plugin in Bitwig.</summary>
    /// <param name="Matched">FL Studio's knobs that the plugin in Bitwig has too, by name.</param>
    /// <param name="Changed">Those Bitwig had at another value, which were set.</param>
    /// <param name="Unmatched">FL Studio's knobs with no knob of the same name in Bitwig.</param>
    /// <param name="NotTaken">Knobs that didn't end up at FL Studio's value when the app read them back, by Bitwig's names.</param>
    public sealed record KnobCopy(int Matched, int Changed, int Unmatched, IReadOnlyList<string> NotTaken);

    /// <summary>How close a knob in Bitwig has to be to FL Studio's value (both from 0 to 1) to count as the same.</summary>
    const double KnobTolerance = 0.005;
    /// <summary>How long to wait for Bitwig to show a device the app selected, or knobs it set.</summary>
    static readonly TimeSpan SelectionWait = TimeSpan.FromSeconds(4);

    /// <summary>What's on a sent channel's new track: its instrument, as Bitwig named it, and where.</summary>
    /// <param name="Text">What plays the channel, in words: "Serum with the project's settings".</param>
    /// <param name="Before">The device put before it (a Note Transpose), as Bitwig named it.</param>
    /// <param name="Lost">Why the project's settings for the plugin didn't load, or null if they did or there were none.</param>
    sealed record Placed(string Text, string Device, int Index, string? Before = null, string? Lost = null);

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
    /// <param name="knobs">
    /// The plugin's knobs as FL Studio has them now, read through its link, to set on the plugin in Bitwig by name: they
    /// bring unsaved tweaks across, and the sound when Bitwig can't load the saved settings. Null when FL Studio
    /// doesn't have the project open.
    /// </param>
    public static async Task<SentChannel> ChannelAsync(BitwigLink link, MidiChannel channel, PresetBlob? preset,
                                                       IReadOnlyList<ScannedPlugin> installed, Files files,
                                                       IReadOnlyList<FlParam>? knobs = null, CancellationToken ct = default)
    {
        CheckChanges(link);
        var problems = new List<string>();
        var track = await link.NewTrackAsync(channel.Name, ct: ct);
        var placed = channel.SamplePath.Length > 0 ? await SamplerAsync(link, track, channel, problems, ct)
            : channel.Plugin.Length > 0 ? await PluginAsync(link, track, channel, preset, installed, files, problems, ct)
            : null;
        if (channel.SamplePath.Length == 0 && channel.Plugin.Length == 0) problems.Add("nothing plays it in the project, so its track has no instrument");

        KnobCopy? copied = null;
        var knobProblems = new List<string>();
        if (placed is not null && channel.SamplePath.Length == 0 && knobs is not null)
            copied = await CopyKnobsAsync(link, track, channel.Name, placed, knobs, knobProblems, ct);
        if (placed?.Lost is { } lost)
        {
            problems.Add(copied is { Matched: > 0 }
                ? $"{lost}, so its knobs were set to FL Studio's one by one: anything that isn't a knob, like a wavetable or a sample, has its default"
                : $"{channel.Plugin} has its default sound: {lost}" +
                  (knobs is null ? ". Open the project in FL Studio and send it again to copy its knobs across" : ""));
            if (copied is { Matched: > 0, Unmatched: > 0 and var n })
                problems.Add((n == 1 ? "1 of FL Studio's knobs has" : $"{n} of FL Studio's knobs have") +
                             " no knob of the same name in Bitwig, so " + (n == 1 ? "it has its default" : "they have their defaults"));
        }
        problems.AddRange(knobProblems);

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
        var looked = await CheckTrackAsync(link, track, channel.Name, placed, problems, ct);
        return new SentChannel(channel.Name, track, placed?.Text ?? "", clip, problems) { Knobs = copied, Checked = looked };
    }

    static async Task<Placed?> SamplerAsync(BitwigLink link, int track, MidiChannel c, List<string> problems, CancellationToken ct)
    {
        var file = Path.GetFileName(c.SamplePath.Replace('\\', '/'));
        if (!File.Exists(c.SamplePath))
        {
            problems.Add($"its sample isn't at {c.SamplePath}");
            return null;
        }
        var sampler = await link.InsertFileAsync(track, c.SamplePath, ct: ct);
        if (sampler is not { Added: true, Index: { } at })
        {
            problems.Add($"Bitwig didn't load {file}");
            return null;
        }
        // FL Studio plays the sample at its own pitch on the channel's root key; a new Sampler does on C3 (the
        // same note, 60). Its Transpose makes up the difference, and takes the pitch knob too.
        var semitones = FlStudioReader.DefaultRoot - c.RootKey + c.PitchCents / 100.0;
        if (semitones != 0)
            await TuneAsync(link, track, at, semitones,
                $"set the Sampler's root key to {DawProject.NoteName(c.RootKey)}" +
                (c.PitchCents == 0 ? "" : $" and tune it {c.PitchCents / 100.0:+0.##;-0.##} semitones"), problems, ct);
        return new Placed("Sampler", sampler.Device?.Name is { Length: > 0 } shown ? shown : "Sampler", at);
    }

    static async Task<Placed?> PluginAsync(BitwigLink link, int track, MidiChannel c, PresetBlob? preset,
                                           IReadOnlyList<ScannedPlugin> installed, Files files, List<string> problems,
                                           CancellationToken ct)
    {
        var name = c.Plugin;
        BitwigInserted? added = null;
        var withSettings = false;
        string? lost = null;
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
                lost = preset switch
                {
                    { Native: false } => "Bitwig didn't take the settings saved in the project",
                    { Native: true } => "its settings are saved the FL Studio way, which Bitwig can't read",
                    _ => null,
                };
                break;
            }
        }
        if (added is not { Added: true, Index: { } at })
        {
            problems.Add(installed.Count > 0 ? $"Bitwig didn't load {name}"
                : preset is { Native: true } ? $"{name} is one of FL Studio's own plugins, which Bitwig can't load"
                : $"couldn't load {name}: the app hasn't found it among your installed plugins (Plugins screen)");
            return null;
        }

        // FL Studio plays a plugin's notes as they are when the channel's root key is C5 (60); a moved root key
        // transposes them, as does the pitch knob. A Note Transpose before the plugin does the same in Bitwig.
        var shift = FlStudioReader.DefaultRoot - c.RootKey + (int)Math.Round(c.PitchCents / 100.0);
        string? before = null;
        if (shift != 0)
        {
            var byHand = $"put a Note Transpose before {name}, set to {shift:+0;-0} semitones";
            var transpose = await link.InsertBitwigDeviceAsync(track, NoteTranspose, atStart: true, ct);
            if (transpose.Added)
            {
                before = transpose.Device?.Name is { Length: > 0 } called ? called : "Note Transpose";
                at++;   // the plugin moved along one
                await TuneAsync(link, track, 0, shift, byHand, problems, ct);
            }
            else problems.Add($"Bitwig didn't add a Note Transpose: {byHand}");
        }
        if (c.PitchCents % 100 != 0)
            problems.Add($"its pitch knob ({c.PitchCents / 100.0:+0.##;-0.##} semitones) is rounded to whole semitones");
        var device = added.Device?.Name is { Length: > 0 } shown ? shown : name;
        return new Placed(withSettings ? $"{device} with the project's settings" : device, device, at, before, lost);
    }

    /// <summary>
    /// Sets the plugin's knobs in Bitwig to FL Studio's, matched by name (<see cref="MatchKnobs"/>), then reads them back
    /// to see which took. Only knobs at another value are set. It selects the plugin in Bitwig to get at its knobs.
    /// </summary>
    static async Task<KnobCopy?> CopyKnobsAsync(BitwigLink link, int track, string trackName, Placed placed, IReadOnlyList<FlParam> knobs,
                                                List<string> problems, CancellationToken ct)
    {
        try
        {
            await link.SelectAsync(track, placed.Index, ct);
            var shown = await SelectionAsync(link, trackName, placed.Device, s => s.Params.Count > 0, ct);
            if (shown is not { Params.Count: > 0 })
            {
                problems.Add($"FL Studio's knobs weren't copied: Bitwig didn't show {placed.Device}'s knobs");
                return null;
            }
            var pairs = MatchKnobs(knobs, shown.Params);
            var set = pairs.Where(p => p.Fl.Value is { } v && !(p.Bitwig.Value is { } b && Math.Abs(b - v) <= KnobTolerance)).ToList();
            foreach (var (fl, bitwig) in set) await link.SetParameterAsync(bitwig.Id, Math.Clamp(fl.Value!.Value, 0, 1), ct);

            IReadOnlyList<string> notTaken = [];
            if (set.Count > 0)
            {
                bool Took(BitwigSelection s, (FlParam Fl, BitwigParam Bitwig) p) =>
                    s.Params.FirstOrDefault(x => x.Id == p.Bitwig.Id)?.Value is { } now && Math.Abs(now - p.Fl.Value!.Value) <= KnobTolerance;
                var after = await SelectionAsync(link, trackName, placed.Device, s => set.All(p => Took(s, p)), ct);
                notTaken = set.Where(p => after is null || !Took(after, p)).Select(p => p.Bitwig.Name).ToList();
            }
            if (pairs.Count == 0)
                problems.Add($"none of FL Studio's knobs for {placed.Device} has a knob of the same name in Bitwig, so none were copied");
            else if (notTaken.Count > 0)
                problems.Add($"{Checks.Count(notTaken.Count, "knob")} didn't take FL Studio's value in Bitwig: {Checks.List(notTaken)}");
            return new KnobCopy(pairs.Count, set.Count, knobs.Count - pairs.Count, notTaken);
        }
        catch (DawLinkException e)
        {
            problems.Add($"FL Studio's knobs weren't copied: {e.Message.TrimEnd('.')}");
            return null;
        }
    }

    /// <summary>
    /// Pairs FL Studio's knobs with the plugin's knobs in Bitwig by name, ignoring case, spaces and punctuation, since
    /// the plugin's formats can differ (VST2 in FL Studio, CLAP or VST3 in Bitwig). A name used more than once pairs up
    /// in order.
    /// </summary>
    public static IReadOnlyList<(FlParam Fl, BitwigParam Bitwig)> MatchKnobs(IEnumerable<FlParam> fl, IEnumerable<BitwigParam> bitwig)
    {
        var byName = bitwig.GroupBy(p => KnobKey(p.Name)).Where(g => g.Key.Length > 0)
                           .ToDictionary(g => g.Key, g => new Queue<BitwigParam>(g));
        var pairs = new List<(FlParam, BitwigParam)>();
        foreach (var f in fl)
            if (byName.TryGetValue(KnobKey(f.Name), out var same) && same.Count > 0) pairs.Add((f, same.Dequeue()));
        return pairs;
    }

    static string KnobKey(string name) => string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    /// <summary>
    /// The device selected in Bitwig, once it's <paramref name="device"/> on <paramref name="track"/> and
    /// <paramref name="ready"/> says so; or, if Bitwig doesn't get there in time, as it last showed that device (null
    /// if it never did). Bitwig moves its selection, and its knobs move, a moment after being asked.
    /// </summary>
    static async Task<BitwigSelection?> SelectionAsync(BitwigLink link, string track, string device, Func<BitwigSelection, bool> ready,
                                                       CancellationToken ct)
    {
        BitwigSelection? last = null;
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var now = await link.SelectedDeviceAsync(ct);
            if (now.Track == track && now.Device?.Name == device)
            {
                last = now;
                if (ready(now)) return now;
            }
            if (waited.Elapsed >= SelectionWait) return last;
            await Task.Delay(100, ct);
        }
    }

    /// <summary>
    /// Reads the new track back from Bitwig and adds anything that isn't as it was sent to <paramref name="problems"/>:
    /// the track, its instrument and the Note Transpose before it. False if Bitwig couldn't be asked.
    /// </summary>
    static async Task<bool> CheckTrackAsync(BitwigLink link, int index, string name, Placed? placed, List<string> problems, CancellationToken ct)
    {
        BitwigProject project;
        try { project = await link.ProjectAsync(ct); }
        catch (DawLinkException e)
        {
            problems.Add($"the app couldn't check it in Bitwig afterwards: {e.Message.TrimEnd('.')}");
            return false;
        }
        var track = project.Tracks.FirstOrDefault(t => t.Index == index && t.Name == name) ?? project.Tracks.LastOrDefault(t => t.Name == name);
        if (track is null)
        {
            problems.Add("its track wasn't in Bitwig when the app checked afterwards");
            return true;
        }
        if (placed is null) return true;
        var devices = track.Devices.Select(d => d.Name).ToList();
        var at = devices.IndexOf(placed.Device);
        if (at < 0)
            problems.Add($"{placed.Device} wasn't on its track when the app checked afterwards");
        else if (placed.Before is { } before && devices.IndexOf(before) is var b && (b < 0 || b > at))
            problems.Add($"the {before} wasn't before {placed.Device} when the app checked afterwards, so its notes aren't transposed");
        return true;
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
    /// packages Bitwig installed, with Drum Machine presets first. The folders are only searched once the list
    /// is read.
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
        foreach (var file in found.OrderBy(f => f.Contains(DrumMachine, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            yield return file;
    }

    static void CheckChanges(BitwigLink link)
    {
        if (link.Hello.Link < BitwigLink.LinkVersion) throw new DawLinkException(BitwigLink.OldScript);
        if (!link.Hello.Changes) throw new DawLinkException(ChangesOff);
    }
}
