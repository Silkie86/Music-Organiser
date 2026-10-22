using System.Buffers.Binary;
using System.Text;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.Core.Parsers;

/// <summary>
/// An FL Studio save that FL stopped writing part-way. FL writes the data chunk's name and length ("FLdt") last, so a
/// save it never finished has junk there, and may stop before the song's end: Solomon's "i dont know_8" (FL 7) has its
/// first 15 channels and every pattern, then nothing, so FL won't open it.
/// </summary>
/// <param name="EndsEarly">The events stop before the mixer and playlist, so the last channels may be missing too.</param>
/// <param name="Channels">How many channels it got as far as.</param>
public sealed record FlDamage(bool EndsEarly, int Channels);

public sealed partial class FlStudioReader
{
    /// <summary>How the save is damaged, or null when FL finished writing it (or it isn't an FL project at all).</summary>
    public static FlDamage? Damage(byte[] raw)
    {
        if (!Garbled(raw)) return null;
        FlEventStream stream;
        try { stream = ReadEvents(raw); }
        catch (InvalidDataException) { return null; }
        return new FlDamage(EndsEarly(stream), stream.Events.Count(e => e.Id == EvChannelNew));
    }

    /// <summary>Damage, read from the file; null when it can't be read.</summary>
    public static FlDamage? Damage(string path)
    {
        try { return Damage(File.ReadAllBytes(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Whether the bytes after the FLhd header aren't "FLdt": FL didn't get as far as writing them.</summary>
    static bool Garbled(byte[] raw)
    {
        if (raw.Length < 22 || Encoding.ASCII.GetString(raw, 0, 4) != "FLhd") return false;
        var dt = 8 + BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4));
        return dt >= 0 && dt + 8 <= raw.Length && Encoding.ASCII.GetString(raw, dt, 4) != "FLdt";
    }

    /// <summary>No mixer insert and no playlist: FL writes both after the channels, so it stopped before them.</summary>
    static bool EndsEarly(FlEventStream stream) => !stream.Events.Any(e => e.Id is EvInsertFlags or EvPlaylist);

    /// <summary>
    /// The newest older save of the song (same folder, same name, a lower number) that FL finished writing, or null.
    /// </summary>
    public static string? WholeOlderSave(string path)
    {
        var (name, number) = SongVersions.Split(Path.GetFileNameWithoutExtension(path));
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(path))!, "*.flp").ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return files
            .Select(f => (File: f, Version: SongVersions.Split(Path.GetFileNameWithoutExtension(f))))
            .Where(f => f.Version.Song.Equals(name, StringComparison.OrdinalIgnoreCase) && f.Version.Number < number)
            .OrderByDescending(f => f.Version.Number)
            .Select(f => f.File)
            .FirstOrDefault(f =>
            {
                try { var raw = File.ReadAllBytes(f); ReadEvents(raw); return Damage(raw) is null; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
            });
    }

    /// <summary>
    /// A copy of a damaged save that FL can open: the data chunk's name and length written in, and when the save stopped
    /// before the song's end, the rest (its last channels, the mixer, the playlist) taken from <paramref name="older"/>,
    /// an earlier save of the same song, from the channel where the damaged one stopped. Everything the damaged save
    /// did get written is kept as it is, so its newer notes and settings stay.
    /// </summary>
    /// <returns>The repaired project, and how many of its channels came from the older save (-1 when none could:
    /// no older save, or its channels don't line up with the damaged one's).</returns>
    public static (byte[] Data, int FromOlder) Repair(byte[] damaged, byte[]? older)
    {
        var stream = ReadEvents(damaged);
        var events = stream.Events;
        var dt = 8 + BinaryPrimitives.ReadInt32LittleEndian(damaged.AsSpan(4));
        var start = dt + 8;
        int Begin(IReadOnlyList<FlEvent> list, int i, int first) => i == 0 ? first : list[i - 1].Offset + list[i - 1].Payload.Length;

        var body = new MemoryStream(damaged.Length + (older?.Length ?? 0));
        var keep = events.Count;   // how many of the damaged save's events go in
        var fromOlder = -1;
        (byte[] Raw, int From)? tail = null;
        if (older is not null && EndsEarly(stream) && events.Count > 0)
        {
            var old = ReadEvents(older);
            var oldStart = 8 + BinaryPrimitives.ReadInt32LittleEndian(older.AsSpan(4)) + 8;
            // The channel it stopped in: carry on from the same point of the same channel in the older save, or from the
            // start of that channel when they don't match event for event.
            var last = events.Select((e, i) => (e, i)).LastOrDefault(x => x.e.Id == EvChannelNew);
            if (last.e is not null)
            {
                var at = old.Events.Select((e, i) => (e, i)).FirstOrDefault(x => x.e.Id == EvChannelNew && x.e.Int == last.e.Int);
                if (at.e is not null && SamePlugin(stream, last.i, old, at.i))
                {
                    var written = events.Count - last.i;
                    var match = at.i + written <= old.Events.Count &&
                                Enumerable.Range(0, written).All(k => old.Events[at.i + k].Id == events[last.i + k].Id);
                    (keep, var from) = match ? (events.Count, at.i + written) : (last.i, at.i);
                    if (from < old.Events.Count)
                    {
                        tail = (older, Begin(old.Events, from, oldStart));
                        fromOlder = old.Events.Skip(at.i).Count(e => e.Id == EvChannelNew) - (match ? 1 : 0);
                    }
                    else fromOlder = 0;
                }
            }
        }

        var end = keep == 0 ? start : events[keep - 1].Offset + events[keep - 1].Payload.Length;
        body.Write(damaged, start, end - start);
        if (tail is { } t)
        {
            var old = ReadEvents(t.Raw);
            var oldEnd = old.Events.Count == 0 ? t.From : old.Events[^1].Offset + old.Events[^1].Payload.Length;
            body.Write(t.Raw, t.From, oldEnd - t.From);
        }

        var file = new MemoryStream((int)body.Length + dt + 8);
        file.Write(damaged, 0, dt);
        file.Write("FLdt"u8);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, (int)body.Length);
        file.Write(len);
        body.WriteTo(file);
        var data = file.ToArray();
        // FLhd's second word counts the channels: make it the channels the copy has.
        var channels = ReadEvents(data).Events.Count(e => e.Id == EvChannelNew);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), (ushort)channels);
        return (data, fromOlder);
    }

    /// <summary>Whether the channel starting at each event index holds the same plugin (by FL's internal name), when both name one.</summary>
    static bool SamePlugin(FlEventStream a, int ai, FlEventStream b, int bi)
    {
        string? Plugin(FlEventStream s, int i)
        {
            for (var k = i + 1; k < s.Events.Count && s.Events[k].Id != EvChannelNew; k++)
                if (s.Events[k].Id == EvPluginInternal) return s.Text(s.Events[k].Payload.Span);
            return null;
        }
        var (x, y) = (Plugin(a, ai), Plugin(b, bi));
        return x is null || y is null || x == y;
    }
}
