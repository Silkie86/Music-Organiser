using MusicOrganiser.Core.Preview;
using static MusicOrganiser.Core.Parsers.BitwigFormat;

namespace MusicOrganiser.Core.Parsers;

public sealed partial class BitwigReader
{
    // The arrangement, as mapped from Solomon's projects (Bitwig 5.1-6.1).
    const int FTrackColour = 0x54c;              // float[4]: red, green, blue, alpha from 0 to 1
    const int FTrackLanes = 0x2bf8, FArrangerLane = 0x2d8e, FItems = 0x21f;
    const int FMuted = 0x10f8;
    const int FClipContent = 0x2ae9, FContentObject = 0x10e9, FContentName = 0x2d24, FNoteContent = 0x49c, FKeyLanes = 0x18cb;
    const int FPlayRange = 0x2adb, FLoopRange = 0x992, FLoopOn = 0x991, FPlayStart = 0x98c, FPlayEnd = 0x98d;
    const int ClsAudioClip = 0xee;
    // An audio event: what it plays (0x288 → 0x20d → 0x4b4, a sample whose file is 0x129e → 0xd3b), the seconds of the
    // file it starts and ends at, and warp markers (0x192: points of a beat 0x2af and a second 0xa6 of the file).
    const int FAudioPlay = 0x288, FAudioSource = 0x20d, FAudioSample = 0x4b4, FSampleFile = 0x129e, FFileRef = 0xd3b;
    const int FSampleStart = 0xc04, FSampleEnd = 0xc05, FWarp = 0x192, FWarpSecond = 0xa6;

    /// <summary>The project's arrangement as Bitwig shows it: tracks (groups indented) and the clips on their timelines.</summary>
    public Arrangement ReadArrangement(string path)
    {
        var doc = TryDecode(path);
        if (doc?.Root is null || doc.BodyError is not null)
            return new Arrangement("Bitwig arranger", 120, 4, []) { Notes = ["This project couldn't be read in enough detail to draw."] };

        var names = TrackNames(doc, out _);
        var tempo = doc.Objects.Where(o => o.Class == ClsTempo).Select(o => o.Num(FTempo)).FirstOrDefault(t => t is >= 10 and <= 999) ?? 120;
        var lanes = new List<PreviewLane>();
        var launcher = new List<List<PreviewClip?>>();
        var missing = 0;

        void Walk(BwObject group, int depth)
        {
            foreach (var track in group.List(FGroupTracks))
            {
                var children = track.List(FGroupTracks).Any();
                var clips = (track.Obj(FTrackLanes)?.Obj(FArrangerLane)?.List(FItems) ?? []).Select(c => Clip(c, path)).OfType<PreviewClip>().ToList();
                missing += clips.SelectMany(c => c.Audio).Count(a => !a.Found);
                var kind = children ? LaneKind.Group
                         : clips.Any(c => c.Audio.Count > 0) ? LaneKind.Audio
                         : LaneKind.Instrument;
                lanes.Add(new PreviewLane(names.GetValueOrDefault(track, "Track"), kind)
                {
                    Detail = FirstDevice(track),
                    Colour = Colour(track[FTrackColour]),
                    Depth = depth,
                    Clips = clips,
                });
                launcher.Add(LauncherSlots(track).Select(slot => Descendants(slot).FirstOrDefault(IsClip) is { } c ? Clip(c, path) : null).ToList());
                if (children) Walk(track, depth + 1);
            }
        }
        if (doc.Root.Obj(FProjectGroup) is { } project) Walk(project, 0);

        var notes = new List<string>();
        if (lanes.Count > 0 && lanes.All(l => l.Clips.Count == 0))
        {
            // Nothing on the timeline: lay the clip launcher's scenes end to end instead, each as long as its longest clip.
            var scenes = launcher.Select(l => l.Count).DefaultIfEmpty(0).Max();
            var at = 0.0;
            var placed = lanes.Select(_ => new List<PreviewClip>()).ToList();
            for (var scene = 0; scene < scenes; scene++)
            {
                var inScene = launcher.Select((slots, i) => (Lane: i, Clip: scene < slots.Count ? slots[scene] : null)).Where(x => x.Clip is not null).ToList();
                if (inScene.Count == 0) continue;
                foreach (var (lane, clip) in inScene) placed[lane].Add(clip! with { Start = at });
                at += inScene.Max(x => x.Clip!.Length);
            }
            if (at > 0)
            {
                lanes = lanes.Select((l, i) => l with { Clips = placed[i] }).ToList();
                missing = placed.SelectMany(c => c).SelectMany(c => c.Audio).Count(a => !a.Found);
                notes.Add("This project's clips are in the clip launcher, so its scenes are shown one after another.");
            }
            else notes.Add("Nothing is on the arrangement timeline or in the clip launcher yet.");
        }
        if (missing > 0)
            notes.Add($"{missing} audio clip{(missing == 1 ? "'s file isn't" : "s' files aren't")} on this computer, so {(missing == 1 ? "it's" : "they're")} drawn without a waveform.");
        return new Arrangement("Bitwig arranger", tempo, 4, lanes) { Notes = notes };
    }

    /// <summary>A track's clip launcher slots, one per scene (track → 0x2bf8 → 0x2d8f → 0x231).</summary>
    static IEnumerable<BwObject> LauncherSlots(BwObject track) => track.Obj(FTrackLanes)?.Obj(0x2d8f)?.List(0x231) ?? [];

    static bool IsClip(BwObject o) => o.Class is ClsClip or ClsAudioClip && o.Has(FStart) && o.Has(FDuration) && o.Has(FClipContent);

    /// <summary>
    /// Everything inside an object, depth first: what's defined there, and with <paramref name="refs"/> also what it refers
    /// to (a track's devices are often references).
    /// </summary>
    static IEnumerable<BwObject> Descendants(BwObject root, bool refs = false)
    {
        var stack = new Stack<BwObject>([root]);
        var seen = new HashSet<BwObject>(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            if (!seen.Add(o)) continue;
            if (!ReferenceEquals(o, root)) yield return o;
            foreach (var (_, v) in o.Fields.AsEnumerable().Reverse())
            {
                if ((refs ? o.Resolve(v) : v as BwObject) is { } child) stack.Push(child);
                else if (v is System.Collections.IList list)
                    for (var i = list.Count - 1; i >= 0; i--)
                        if ((refs ? o.Resolve(list[i]) : list[i] as BwObject) is { } item) stack.Push(item);
            }
        }
    }

    /// <summary>What plays the track: its instrument (a device and its preset), else its first device.</summary>
    static string FirstDevice(BwObject track)
    {
        // The device chain only; clips and sub-tracks hold no devices of this track's.
        if (track.Obj(0x164) is not { } chain) return "";
        var devices = Descendants(chain, refs: true).Where(o => o.Class is ClsBitwigDevice or ClsVst3Plugin or ClsVst2Plugin && o.Str(FDeviceName).Length > 0).ToList();
        var device = devices.FirstOrDefault(d => BitwigKind(d.Str(FDeviceName), d.Str(FDeviceCategory)) == PluginKind.Instrument ||
                                                 InstrumentCategories.Contains(d.Str(FDeviceCategory)))
                     ?? devices.FirstOrDefault(d => BitwigKind(d.Str(FDeviceName), d.Str(FDeviceCategory)) != PluginKind.NoteEffect)
                     ?? devices.FirstOrDefault();
        if (device is null) return "";
        var name = device.Str(FDeviceName);
        return device.Str(FDevicePreset) is { Length: > 0 } preset && preset != name ? $"{name} · {preset}" : name;
    }

    static int? Colour(object? value) => value is float[] { Length: >= 3 } c
        ? (int)Math.Round(Math.Clamp(c[0], 0, 1) * 255) << 16 | (int)Math.Round(Math.Clamp(c[1], 0, 1) * 255) << 8 | (int)Math.Round(Math.Clamp(c[2], 0, 1) * 255)
        : null;

    /// <summary>
    /// An arrangement clip: where it is, and what of its content plays. The content has its own timeline; the clip plays
    /// it from its play start, and when looping is on, goes back to the loop range's start each time it reaches its end.
    /// </summary>
    static PreviewClip? Clip(BwObject clip, string projectPath)
    {
        if (clip.Num(FStart) is not { } start || clip.Num(FDuration) is not { } length || length <= 0) return null;
        var content = clip.Obj(FClipContent)?.Obj(FContentObject);
        var range = clip.Obj(FPlayRange);
        var offset = range?.Obj(FPlayStart)?.Num(FStart) ?? range?.Obj(FLoopRange)?.Num(FStart) ?? 0;
        var loopStart = range?.Obj(FLoopRange)?.Num(FStart) ?? 0;
        var loopEnd = loopStart + (range?.Obj(FLoopRange)?.Num(FDuration) ?? 0);
        var looping = range?.Num(FLoopOn) is > 0 && loopEnd > loopStart;
        var lanes = content?.Obj(FNoteContent)?.List(FKeyLanes).ToList() ?? [];

        // Where a moment of the content falls in the clip: each pass of the loop after the first starts at the loop start.
        IEnumerable<double> Placements(double at)
        {
            if (!looping)
            {
                if (at >= offset && at < offset + length) yield return at - offset;
                yield break;
            }
            var loop = loopEnd - loopStart;
            if (at >= offset && at < loopEnd) yield return at - offset;
            if (at < loopStart || at >= loopEnd) yield break;
            for (var pass = loopEnd - offset + (at - loopStart); pass < length; pass += loop) yield return pass;
        }

        if (clip.Class == ClsAudioClip)
        {
            var parts = new List<AudioPart>();
            foreach (var e in lanes.SelectMany(l => l.List(FItems)))
            {
                var play = e.Obj(FAudioPlay);
                var file = play?.Obj(FAudioSource)?.Obj(FAudioSample)?.Obj(FSampleFile)?.Obj(FFileRef)?.Str(FFilePath) ?? "";
                var eStart = e.Num(FStart) ?? 0;
                var eLength = e.Num(FDuration) ?? 0;
                if (file.Length == 0 || eLength <= 0) continue;
                var from = e.Num(FSampleStart) ?? 0;
                var to = e.Num(FSampleEnd) ?? 0;
                var warps = play?.Obj(FWarp)?.List(FItems).Select(w => (Beat: w.Num(FStart) ?? 0, Second: w.Num(FWarpSecond) ?? 0)).ToList() ?? [];
                double? rate = warps.Count >= 2 && warps[^1].Beat > warps[0].Beat
                    ? (warps[^1].Second - warps[0].Second) / (warps[^1].Beat - warps[0].Beat)
                    : to > from ? (to - from) / eLength : null;
                var found = ArrangementReader.Locate(file, projectPath);
                // The visible part of the event: from where the clip's window starts to where it (or the clip) ends.
                var visibleFrom = Math.Max(eStart, offset);
                var visibleTo = Math.Min(eStart + eLength, offset + length);
                if (visibleTo <= visibleFrom) continue;
                parts.Add(new AudioPart(found ?? file, visibleFrom - offset, visibleTo - visibleFrom, from + (visibleFrom - eStart) * (rate ?? 0), rate)
                {
                    Found = found is not null,
                });
            }
            var audioName = content?.Str(FContentName) is { Length: > 0 } an ? an : parts.FirstOrDefault()?.Path.Split('\\', '/')[^1] ?? "";
            return new PreviewClip(audioName, start, length, clip.Num(FMuted) is > 0) { Audio = parts };
        }

        var notes = new List<PreviewNote>();
        foreach (var lane in lanes)
        {
            if (lane.Num(FPitch) is not { } key) continue;
            foreach (var n in lane.List(FItems).Where(n => n.Class == ClsNote))
            {
                var at = n.Num(FStart) ?? 0;
                var duration = n.Num(FDuration) ?? 0.25;
                var velocity = Math.Clamp((int)Math.Round((n.Num(FVelocity) ?? 0.8) * 127), 1, 127);
                foreach (var placed in Placements(at))
                    notes.Add(new PreviewNote(placed, Math.Min(duration, length - placed), Math.Clamp((int)key, 0, 127), velocity, n.Num(FMuted) is > 0));
            }
        }
        var name = content?.Str(FContentName) ?? "";
        return new PreviewClip(name, start, length, clip.Num(FMuted) is > 0) { Notes = notes.OrderBy(n => n.Start).ThenBy(n => n.Key).ToList() };
    }
}
