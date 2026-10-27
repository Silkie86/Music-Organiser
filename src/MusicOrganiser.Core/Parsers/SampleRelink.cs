using System.Buffers.Binary;
using System.Text;
using MusicOrganiser.Core.Export;

namespace MusicOrganiser.Core.Parsers;

public sealed partial class FlStudioReader
{
    /// <summary>
    /// A copy of an FL Studio project with sample paths pointed somewhere else. <paramref name="relink"/> maps a sample
    /// path, as the app lists it (with %FLStudioUserData% worked out), to the file to use instead. Only those sample
    /// events change; every other byte is copied as it was, and the FLdt chunk's length follows the new paths.
    /// <c>CantWrite</c> lists the files an FL Studio older than 11.5 can't be pointed to: it keeps paths as one byte a
    /// letter, so a letter outside Western European ones (Ł, ş, ア) would become "?". Those samples are left as they were.
    /// </summary>
    public static (byte[] Data, int Changed, IReadOnlyList<string> CantWrite) RelinkSamples(byte[] raw, string projectPath,
                                                                                            IReadOnlyDictionary<string, string> relink)
    {
        var stream = ReadEvents(raw);
        var changes = new Dictionary<int, byte[]>();
        var cantWrite = new List<string>();
        foreach (var e in stream.Events)
            if (e.Id == EvSamplePath && New(stream.Text(e.Payload.Span)) is { } to)
            {
                if (stream.Utf16) changes[e.Index] = Encoding.Unicode.GetBytes(to + "\0");
                else if (to.All(c => c <= '\u00ff')) changes[e.Index] = Encoding.Latin1.GetBytes(to + "\0");
                else if (!cantWrite.Contains(to)) cantWrite.Add(to);
            }
        var data = Rewrite(raw, stream, changes);
        SameApartFrom(stream, ReadEvents(data), changes);
        return (data, changes.Count, cantWrite);

        string? New(string sample) =>
            relink.TryGetValue(sample, out var to) || relink.TryGetValue(ResolveSamplePath(sample, projectPath), out to) ? to : null;
    }
}

public sealed partial class FlStudioReader
{
    /// <summary>
    /// Checks a copy Fix wrote: every event as it was in the original, in the same order, but the ones it meant to change.
    /// Throws when anything else differs, so a writer bug never reaches FL Studio as a quietly changed project.
    /// </summary>
    static void SameApartFrom(FlEventStream original, FlEventStream copy, IReadOnlyDictionary<int, byte[]> changes)
    {
        var same = original.Events.Count == copy.Events.Count;
        for (var i = 0; same && i < original.Events.Count; i++)
        {
            var (a, b) = (original.Events[i], copy.Events[i]);
            same = a.Id == b.Id && (changes.TryGetValue(i, out var to) ? b.Payload.Span.SequenceEqual(to)
                                                                       : b.Payload.Span.SequenceEqual(a.Payload.Span));
        }
        if (!same) throw new InvalidDataException("Fix's copy came out different from the original in more than what it meant to change, so it wasn't saved.");
    }
}

public sealed partial class BitwigReader
{
    // A Sampler zone (0x74c) holds its sample at 0x4112 → 0x748 and ends it at 0x75f and 0x761, in the sample's seconds.
    // A sample saves its length in seconds at 0x95, and what Bitwig recognises its file by (MD5) at 0x129e → 0x513 → 0x16.
    const int FZoneSample = 0x4112, FZoneSampleObject = 0x748, FZoneEnd = 0x75f, FZoneLoopEnd = 0x761;
    const int FSampleSeconds = 0x95, FSampleHash = 0x513, FHash = 0x16;

    /// <summary>
    /// A copy of a Bitwig project with sample paths pointed somewhere else: every file reference (and used_files entry)
    /// whose path, or path inside the project folder, is a key of <paramref name="relink"/>. Null when the project can't
    /// be decoded cleanly enough to write back.
    /// </summary>
    public static (byte[] Data, int Changed)? RelinkSamples(byte[] raw, string projectPath, IReadOnlyDictionary<string, string> relink)
    {
        if (!BitwigFormat.IsBitwig(raw)) return null;
        var doc = BitwigFormat.Decode(raw);
        if (doc.Root is null || doc.BodyError is not null) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
        var changed = 0;

        // The samples (a Sampler's, an audio clip's) each file reference belongs to. Bitwig recognises a sample by the
        // size and MD5 saved with it and trusts its saved length, so one pointed at another file gets that file's.
        var samples = new Dictionary<BitwigFormat.BwObject, List<BitwigFormat.BwObject>>(ReferenceEqualityComparer.Instance);
        foreach (var s in doc.Objects)
            if (s.Obj(FSampleFile)?.Obj(FFileRef) is { Class: ClsFileRef } r)
                (samples.TryGetValue(r, out var list) ? list : samples[r] = []).Add(s);
        // What plays each sample (a Sampler zone, an audio clip's audio): its ends and warp markers are in the file's seconds.
        var players = new Dictionary<BitwigFormat.BwObject, List<BitwigFormat.BwObject>>(ReferenceEqualityComparer.Instance);
        foreach (var p in doc.Objects)
            if ((p.Obj(FZoneSample)?.Obj(FZoneSampleObject) ?? p.Obj(FAudioPlay)?.Obj(FAudioSource)?.Obj(FAudioSample)) is { } s)
                (players.TryGetValue(s, out var list) ? list : players[s] = []).Add(p);
        var read = new Dictionary<string, BitwigProject.SampleFile?>(StringComparer.OrdinalIgnoreCase);

        foreach (var o in doc.Objects.Where(o => o.Class == ClsFileRef))
            if (o.Str(FFilePath) is { Length: > 0 } f && TextScan.IsAudioPath(f) && New(f) is { } to)
            {
                o.Set(FFilePath, to);
                foreach (var s in samples.GetValueOrDefault(o) ?? [])
                {
                    if (!read.TryGetValue(to, out var info)) read[to] = info = BitwigProject.ReadSample(to);
                    var was = Trusted(s) ? s.Num(FSampleSeconds) : null;
                    BitwigProject.SampleIs(s, to, info);
                    if (info is { Seconds: > 0 })
                        foreach (var p in players.GetValueOrDefault(s) ?? []) Fit(p, was, info.Seconds);
                }
                changed++;
            }
        if (doc.Meta.GetValueOrDefault("used_files") is List<string> used)
            for (var i = 0; i < used.Count; i++)
                if (New(used[i]) is { } to)
                {
                    used[i] = to;
                    changed++;
                }
        return (BitwigFormat.Encode(doc), changed);

        // Whether Bitwig trusted the length saved with a sample: with a zero MD5 it reads the file itself.
        bool Trusted(BitwigFormat.BwObject sample) => sample.Obj(FSampleFile)?.Obj(FSampleHash)?[FHash] switch
        {
            BitwigFormat.BwBlob b => raw.AsSpan(b.Offset, b.Length).ContainsAnyExcept((byte)0),
            byte[] h => h.AsSpan().ContainsAnyExcept((byte)0),
            _ => false,
        };

        string? New(string f)
        {
            if (relink.TryGetValue(f, out var to)) return to;
            if (IsAbsolute(f)) return null;
            return relink.TryGetValue(Path.GetFullPath(Path.Combine(dir, f)), out to) ? to : null;
        }
    }

    /// <summary>
    /// Fits what plays a relinked sample to the new file's length: a Sampler zone's ends, or an audio clip's start and end
    /// in the file and its warp markers, each moved to the same part of the new file as it was of the old one, so a zone
    /// that played the whole old file plays the whole new one. The old length is the one saved with the sample, or where
    /// the zone or clip ended when Bitwig didn't trust that one.
    /// </summary>
    static void Fit(BitwigFormat.BwObject player, double? was, double seconds)
    {
        var old = was is > 0 ? was.Value : player.Num(FZoneLoopEnd) ?? player.Num(FZoneEnd) ?? player.Num(FSampleEnd) ?? 0;
        if (old <= 0 || Math.Abs(old - seconds) < 1e-9) return;
        void Move(BitwigFormat.BwObject o, int id)
        {
            if (o.Num(id) is { } at) o.Set(id, Math.Min(at / old * seconds, seconds));
        }
        Move(player, FZoneEnd);
        Move(player, FZoneLoopEnd);
        Move(player, FSampleStart);
        Move(player, FSampleEnd);
        foreach (var marker in player.Obj(FAudioPlay)?.Obj(FWarp)?.List(FItems) ?? []) Move(marker, FWarpSecond);
    }
}
