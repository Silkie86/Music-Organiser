using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi.Playground;

/// <summary>
/// A track on the playground's timeline: its clips, the sound it plays with, and where it came from, so parts from the
/// same song are never suggested to go with it. <see cref="Notes"/> is every clip as it plays (from bar 1, at
/// <see cref="PhraseAnalyser.Ppq"/>), worked out again by <see cref="Render"/> after a clip changes.
/// </summary>
public sealed class PlaygroundLayer
{
    public string Name { get; set; } = "Part";
    public string Role { get; set; } = MidiRoles.Other;
    /// <summary>The notes, packed as <see cref="MidiIndex.EncodeNotes"/> does (base64 in the saved file).</summary>
    public string Data { get; set; } = "";
    public int? Program { get; set; }
    public bool Muted { get; set; }
    public bool Solo { get; set; }
    /// <summary>The project or .mid file it came from, or "" for a part the playground wrote.</summary>
    public string SourcePath { get; set; } = "";
    /// <summary>The library part's note hash, so the same part isn't suggested again.</summary>
    public string SourceHash { get; set; } = "";
    /// <summary>For a part the playground wrote from the chords ("chords:Gospel:Held", "bass:Walking"): written again when the chords change.</summary>
    public string Written { get; set; } = "";
    /// <summary>
    /// For a written part with a groove copied onto it: the source part's notes (packed like <see cref="Data"/>), so the
    /// groove is put on again when the chords change and the part is rewritten.
    /// </summary>
    public string GrooveData { get; set; } = "";
    public GrooveDepth GrooveDepth { get; set; }
    public double GrooveAmount { get; set; } = 1;
    /// <summary>Where the copied groove came from, to show ("Keys · Gospel stab").</summary>
    public string GrooveFrom { get; set; } = "";

    [JsonIgnore]
    public IReadOnlyList<NoteEvent> GrooveNotes
    {
        get => GrooveData.Length == 0 ? [] : MidiIndex.DecodeNotes(Convert.FromBase64String(GrooveData));
        set => GrooveData = value.Count == 0 ? "" : Convert.ToBase64String(MidiIndex.EncodeNotes(value.OrderBy(n => n.Start).ThenBy(n => n.Key).ToList()));
    }

    [JsonIgnore]
    public IReadOnlyList<NoteEvent> Notes
    {
        get => Data.Length == 0 ? [] : MidiIndex.DecodeNotes(Convert.FromBase64String(Data));
        set => Data = Convert.ToBase64String(MidiIndex.EncodeNotes(value.OrderBy(n => n.Start).ThenBy(n => n.Key).ToList()));
    }

    /// <summary>The clips on the track. A track saved before the timeline has none until <see cref="PlaygroundSession.Upgrade"/>.</summary>
    public List<PlaygroundClip> Clips { get; set; } = [];

    /// <summary><see cref="Notes"/> made again from the clips.</summary>
    public void Render()
    {
        Notes = Clips.SelectMany(c => c.Render()).ToList();
        if (Clips.Count > 0 && SourcePath.Length == 0) SourcePath = Clips.FirstOrDefault(c => c.SourcePath.Length > 0)?.SourcePath ?? "";
    }

    public PlaygroundLayer Copy()
    {
        var l = (PlaygroundLayer)MemberwiseClone();
        l.Clips = Clips.Select(c => c.Copy()).ToList();
        return l;
    }
}

/// <summary>A section of the idea (A, B, a chorus…): how many bars, its chords if typed, and its parts.</summary>
public sealed class PlaygroundSection
{
    public string Name { get; set; } = "A";
    public int Bars { get; set; } = 4;
    /// <summary>The chords typed as a chart ("Dm9 G13 | Cmaj9 | %"), or "" to hear them from the parts.</summary>
    public string Chart { get; set; } = "";
    /// <summary>What the section is for (a verse, a build, a drop): suggestions are ranked on how well their energy suits it.</summary>
    public SectionFeel Feel { get; set; }
    public List<PlaygroundLayer> Layers { get; set; } = [];
    /// <summary>Names over stretches of bars (Verse, Build, Drop…) and what each is for.</summary>
    public List<PlaygroundLabel> Labels { get; set; } = [];

    [JsonIgnore]
    public long Length => (long)Math.Max(1, Bars) * ChordLane.Bar;

    /// <summary>The label over <paramref name="tick"/>, if any.</summary>
    public PlaygroundLabel? LabelAt(long tick) => Labels.LastOrDefault(l => tick >= l.Start && tick < l.End);
}

/// <summary>
/// A MIDI playground idea: its sections, the order they play in, the tempo and key, and which ears judge the harmony.
/// Saved as JSON in the app's data folder, the one being worked on after every change.
/// </summary>
public sealed class PlaygroundSession
{
    public string Name { get; set; } = "Untitled idea";
    public double Tempo { get; set; } = 90;
    /// <summary>A typed key ("Eb", "C minor"), or "" to work it out from the notes.</summary>
    public string Key { get; set; } = "";
    public HarmonyStyle Style { get; set; } = HarmonyStyle.JazzGospel;
    public List<PlaygroundSection> Sections { get; set; } = [new()];
    /// <summary>The order the sections play in, by name: "A A B A". "" plays each once, in order.</summary>
    public string Arrangement { get; set; } = "";
    public int Current { get; set; }
    /// <summary>2 for an idea on one timeline of clips; 1 (or missing) for one saved as sections, opened by <see cref="Upgrade"/>.</summary>
    public int Version { get; set; } = 2;

    /// <summary>The timeline: since version 2 an idea is one section, its parts tracks of clips and its old sections labels.</summary>
    [JsonIgnore]
    public PlaygroundSection Timeline => Sections[0];

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static PlaygroundSession? FromJson(string json)
    {
        try
        {
            var s = JsonSerializer.Deserialize<PlaygroundSession>(json, Json);
            if (s is null) return null;
            // Ideas saved before the timeline have no version.
            if (JsonNode.Parse(json) is JsonObject o && !o.ContainsKey(nameof(Version))) s.Version = 1;
            if (s.Sections.Count == 0) s.Sections.Add(new PlaygroundSection());
            s.Current = Math.Clamp(s.Current, 0, s.Sections.Count - 1);
            s.Upgrade();
            return s;
        }
        catch (JsonException) { return null; }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// An idea saved as sections, laid out on one timeline (Solomon, 2026-10-19, "saved ideas open with each old section
    /// laid end to end as a label, and its parts as clips filling it"): the sections in the order they played, each a
    /// label over its bars, and parts of the same name joined into one track. A typed chart becomes a chord part, since
    /// chords are now heard from the parts, never typed.
    /// </summary>
    public void Upgrade()
    {
        if (Version >= 2 && Sections.Count == 1 && Timeline.Layers.All(l => l.Clips.Count > 0 || l.Data.Length == 0)) return;
        var order = Order();
        var labelled = order.Count > 1 || order.Any(s => s.Feel != SectionFeel.Any);
        var timeline = new PlaygroundSection { Name = "Timeline" };
        long at = 0;
        foreach (var section in order)
        {
            var length = section.Length;
            var typed = section.Chart.Trim().Length > 0 ? SectionHarmony.Of(section).Lane : null;
            foreach (var layer in section.Layers)
            {
                var track = timeline.Layers.FirstOrDefault(t => t.Name == layer.Name);
                if (track is null)
                {
                    track = layer.Copy();
                    track.Clips = [];
                    timeline.Layers.Add(track);
                }
                // Clips already there (an idea saved part way) keep their places, moved along with their section.
                var clips = layer.Clips.Count > 0
                    ? layer.Clips.Select(c => { var copy = c.Copy(); copy.Start += at; return copy; }).ToList()
                    : [PlaygroundClip.Of(layer.Name, layer.Role, layer.Notes.Where(n => n.Start < length).Select(n => n with { Length = Math.Min(n.Length, length - n.Start) }).ToList(),
                                         length, at, length, layer.SourcePath, layer.SourceHash)];
                foreach (var c in clips) c.Pinned = true;
                track.Clips.AddRange(clips);
                // Parts written from typed chords keep the notes they had: there are no typed chords to follow any more.
                if (typed is not null) track.Written = "";
            }
            if (typed is { Count: > 0 } && section.Layers.All(l => l.Role != MidiRoles.Chords))
            {
                var track = timeline.Layers.FirstOrDefault(t => t.Name == "Chords" && t.Written.Length == 0);
                if (track is null) timeline.Layers.Add(track = new PlaygroundLayer { Name = "Chords", Role = MidiRoles.Chords, Program = 4 });
                track.Clips.Add(PlaygroundClip.Of("Chords", MidiRoles.Chords, Voicer.Plain(typed), length, at, length));
            }
            timeline.Labels.AddRange(section.Labels.Select(l => { var copy = l.Copy(); copy.Start += at; return copy; }));
            if (labelled) timeline.Labels.Add(new PlaygroundLabel { Name = section.Name, Start = at, Length = length, Feel = section.Feel });
            at += length;
        }
        timeline.Bars = (int)Math.Max(1, at / ChordLane.Bar);
        foreach (var t in timeline.Layers) t.Render();
        Sections = [timeline];
        Current = 0;
        Arrangement = "";
        Version = 2;
    }

    /// <summary>The sections in the order they play.</summary>
    public IReadOnlyList<PlaygroundSection> Order()
    {
        var names = Arrangement.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var list = names.Select(n => Sections.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                        .OfType<PlaygroundSection>().ToList();
        return list.Count > 0 ? list : Sections;
    }

    /// <summary>
    /// Every part across the arrangement as tracks: parts with the same name in different sections join into one track,
    /// each section's notes placed where it plays. This is what's dragged into the DAW.
    /// </summary>
    public IReadOnlyList<(string Name, int? Program, List<NoteEvent> Notes)> ArrangedTracks()
    {
        var tracks = new List<(string Name, int? Program, List<NoteEvent> Notes)>();
        long at = 0;
        foreach (var section in Order())
        {
            foreach (var layer in Audible(section))
            {
                var i = tracks.FindIndex(t => t.Name == layer.Name);
                if (i < 0) { tracks.Add((layer.Name, layer.Program, [])); i = tracks.Count - 1; }
                tracks[i].Notes.AddRange(layer.Notes.Where(n => n.Start < section.Length)
                                              .Select(n => n with { Start = n.Start + at, Length = Math.Min(n.Length, section.Length - n.Start) }));
            }
            at += section.Length;
        }
        return tracks;
    }

    /// <summary>The parts that play: the soloed ones when any is, else every one not muted.</summary>
    public static IEnumerable<PlaygroundLayer> Audible(PlaygroundSection section)
    {
        var solo = section.Layers.Any(l => l.Solo);
        return section.Layers.Where(l => solo ? l.Solo : !l.Muted);
    }
}

/// <summary>Where playground ideas are kept: the one being worked on, and ideas saved by name.</summary>
public sealed class PlaygroundStore(string folder)
{
    public string Folder { get; } = folder;
    string CurrentFile => Path.Combine(Folder, "current.json");
    /// <summary>Whether there's an idea being worked on yet (none the first time, or after the folder was cleared).</summary>
    public bool HasCurrent => File.Exists(CurrentFile);
    string IdeasFolder => Path.Combine(Folder, "ideas");

    public PlaygroundSession LoadCurrent()
    {
        try { return File.Exists(CurrentFile) ? PlaygroundSession.FromJson(File.ReadAllText(CurrentFile)) ?? new() : new(); }
        catch (IOException) { return new(); }
    }

    public void SaveCurrent(PlaygroundSession session)
    {
        Directory.CreateDirectory(Folder);
        var temp = CurrentFile + ".tmp";
        File.WriteAllText(temp, session.ToJson());
        File.Move(temp, CurrentFile, overwrite: true);
    }

    /// <summary>The ideas saved by name, newest first.</summary>
    public IReadOnlyList<string> Ideas() =>
        Directory.Exists(IdeasFolder)
            ? new DirectoryInfo(IdeasFolder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToList()
            : [];

    public void SaveIdea(PlaygroundSession session)
    {
        Directory.CreateDirectory(IdeasFolder);
        File.WriteAllText(Path.Combine(IdeasFolder, TextSafe(session.Name) + ".json"), session.ToJson());
    }

    public PlaygroundSession? LoadIdea(string name)
    {
        var file = Path.Combine(IdeasFolder, TextSafe(name) + ".json");
        return File.Exists(file) ? PlaygroundSession.FromJson(File.ReadAllText(file)) : null;
    }

    static string TextSafe(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return safe.Length == 0 ? "Untitled idea" : safe;
    }
}
