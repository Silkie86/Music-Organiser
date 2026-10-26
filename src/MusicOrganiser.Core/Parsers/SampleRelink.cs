using System.Buffers.Binary;
using System.Text;

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

        foreach (var o in doc.Objects.Where(o => o.Class == ClsFileRef))
            if (o.Str(FFilePath) is { Length: > 0 } f && TextScan.IsAudioPath(f) && New(f) is { } to)
            {
                o.Set(FFilePath, to);
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

        string? New(string f)
        {
            if (relink.TryGetValue(f, out var to)) return to;
            if (IsAbsolute(f)) return null;
            return relink.TryGetValue(Path.GetFullPath(Path.Combine(dir, f)), out to) ? to : null;
        }
    }
}
