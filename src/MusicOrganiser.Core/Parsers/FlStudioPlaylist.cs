using System.Buffers.Binary;

namespace MusicOrganiser.Core.Parsers;

/// <summary>A playlist track: its place from 0, its name when it was given one, and its colour (0xRRGGBB).</summary>
public sealed record FlPlaylistTrack(int Index, string Name, int? Colour);

/// <summary>A pattern: its number, name, colour (0xRRGGBB, when not FL's default) and the notes in it, by channel.</summary>
public sealed record FlPattern(int Index, string Name, int? Colour, IReadOnlyList<(int Channel, NoteEvent Note)> Notes);

/// <summary>
/// One clip in the playlist, in song ticks. A pattern clip has <see cref="Pattern"/> set and starts
/// <see cref="Offset"/> ticks into the pattern; an audio clip has <see cref="Channel"/> set and starts
/// <see cref="Offset"/> seconds into its channel's sample.
/// </summary>
public sealed record FlPlacement(int Track, long Start, long Length, double Offset, bool Muted, int? Pattern, int? Channel);

/// <summary>A marker on the playlist's timeline, in song ticks.</summary>
public sealed record FlTimeMarker(long At, string Name);

/// <summary>
/// The playlist as FL Studio shows it (its first arrangement): tracks, the clips on them, the patterns they play and
/// the timeline's markers. <see cref="ChannelColours"/> holds each channel's colour (0xRRGGBB), <see cref="ChannelNames"/>
/// its name, and <see cref="AutomationChannels"/> the channels that are automation clips.
/// </summary>
public sealed record FlPlaylist(int Ppq, int BeatsPerBar, IReadOnlyList<FlPlaylistTrack> Tracks, IReadOnlyList<FlPattern> Patterns,
                                IReadOnlyList<FlPlacement> Items, IReadOnlyList<FlTimeMarker> Markers,
                                IReadOnlyDictionary<int, int> ChannelColours, IReadOnlyDictionary<int, string> ChannelNames,
                                IReadOnlySet<int> AutomationChannels);

public sealed partial class FlStudioReader
{
    const int EvTimeSigNumerator = 17;          // BYTE: beats in a bar
    const int EvChannelColour = 128;            // DWORD: a channel's colour, 0x00BBGGRR
    const int EvTimeMarker = 148;               // DWORD: a marker's position in ticks (the top bits say what kind)
    const int EvPatternColour = 150;            // DWORD: a pattern's colour, 0x00BBGGRR
    const int EvArrangementNew = 99;            // WORD: starts an arrangement (FL 12.9 onwards)
    const int EvMarkerName = Text + 13;         // 205: a marker's name, after its position
    const int EvTrackData = Data + 30;          // 238: a playlist track: u32 number from 1, u32 colour 0x00BBGGRR, ...
    const int EvTrackName = Text + 47;          // 239: the track's name, after its data (only when it was named)

    /// <summary>Reads the playlist (the first arrangement) as FL Studio lays it out, for drawing it.</summary>
    public FlPlaylist ReadPlaylist(string path)
    {
        var flp = Load(path);
        var beatsPerBar = 4;
        var channels = new List<int>();
        var channelColours = new Dictionary<int, int>();
        var channelNames = new Dictionary<int, string>();
        var automation = new HashSet<int>();
        var patternNames = new Dictionary<int, string>();
        var patternColours = new Dictionary<int, int>();
        var rawNotes = new List<(int Pattern, ReadOnlyMemory<byte> Data)>();
        var tracks = new Dictionary<int, (string Name, int? Colour)>();
        var markers = new List<FlTimeMarker>();
        ReadOnlyMemory<byte>? playlist = null;
        int? channel = null, pattern = null, track = null;
        long? markerAt = null;
        var arrangements = 0;
        var inMixer = false;

        foreach (var e in flp.Events)
        {
            // Only the first arrangement: the next one repeats its playlist, tracks and markers.
            var first = arrangements <= 1;
            switch (e.Id)
            {
                case EvTimeSigNumerator when e.Int is >= 1 and <= 32: beatsPerBar = (int)e.Int; break;
                case EvChannelNew:
                    channel = (int)e.Int;
                    if (!channels.Contains(channel.Value)) channels.Add(channel.Value);
                    break;
                case EvInsertFlags or EvMixerSlot: inMixer = true; break;
                case EvPluginName or EvChannelNameOld when channel is not null && !inMixer: channelNames.TryAdd(channel.Value, flp.Str(e)); break;
                case EvChannelType when channel is not null && !inMixer && e.Int == ChannelTypeAutomation: automation.Add(channel.Value); break;
                case EvChannelColour when channel is not null && !inMixer: channelColours.TryAdd(channel.Value, Rgb(e.Int)); break;
                case EvPatternNew: pattern = (int)e.Int; break;
                case EvPatternName when pattern is not null: patternNames[pattern.Value] = flp.Str(e); break;
                case EvPatternColour when pattern is not null: patternColours[pattern.Value] = Rgb(e.Int); break;
                case EvPatternNotes when pattern is not null: rawNotes.Add((pattern.Value, e.Payload)); break;
                case EvArrangementNew: arrangements++; break;
                case EvPlaylist when playlist is null: playlist = e.Payload; break;
                case EvTrackData when first && e.Payload.Length >= 8:
                    var d = e.Payload.Span;
                    track = (int)BinaryPrimitives.ReadUInt32LittleEndian(d) - 1;
                    tracks[track.Value] = ("", Rgb(BinaryPrimitives.ReadUInt32LittleEndian(d[4..])));
                    break;
                case EvTrackName when first && track is not null:
                    tracks[track.Value] = (flp.Str(e), tracks.GetValueOrDefault(track.Value).Colour);
                    break;
                case EvTimeMarker when first:
                    markerAt = e.Int & 0x07FFFFFF;
                    markers.Add(new FlTimeMarker(markerAt.Value, ""));
                    break;
                case EvMarkerName when first && markerAt is not null && markers.Count > 0:
                    markers[^1] = markers[^1] with { Name = flp.Str(e) };
                    break;
            }
        }

        var known = channels.ToHashSet();
        var patterns = rawNotes.GroupBy(r => r.Pattern).OrderBy(g => g.Key)
            .Select(g => new FlPattern(g.Key, patternNames.GetValueOrDefault(g.Key, $"Pattern {g.Key}"), patternColours.TryGetValue(g.Key, out var c) ? c : null,
                                       g.SelectMany(r => DecodeNotes(r.Data.Span, known)).ToList()))
            .ToList();
        foreach (var (index, name) in patternNames)
            if (patterns.All(p => p.Index != index))
                patterns.Add(new FlPattern(index, name, patternColours.TryGetValue(index, out var c) ? c : null, []));

        var items = new List<FlPlacement>();
        if (playlist is { } pl)
        {
            var placed = PlaylistItems(pl.Span);
            var top = TopSlot(flp.Version, placed.Select(i => i.Slot).ToList());
            foreach (var i in placed.Where(i => i.Length > 0).OrderBy(i => i.At))
                items.Add(i.Index > PatternBase
                    ? new FlPlacement(Math.Max(0, top - i.Slot), i.At, i.Length, i.From, i.Muted, i.Index - PatternBase, null)
                    : new FlPlacement(Math.Max(0, top - i.Slot), i.At, i.Length, i.Offset / 1000, i.Muted, null, i.Index));
        }

        return new FlPlaylist(flp.Ppq, beatsPerBar,
                              tracks.OrderBy(t => t.Key).Select(t => new FlPlaylistTrack(t.Key, t.Value.Name, t.Value.Colour)).ToList(),
                              patterns, items, markers, channelColours, channelNames, automation);
    }

    /// <summary>
    /// What FL saves for the first playlist track: tracks count down from it. From FL 12.9.1 (500 tracks) the first is
    /// 499; before that 198 (199 tracks), though a 2012 song with all its clips on tracks 1 to 4 counted down from 98,
    /// so an older song whose slots all fit under 99 is read that way. A file whose version can't be read goes by what it holds.
    /// </summary>
    static int TopSlot(string version, IReadOnlyCollection<int> slots)
    {
        var parts = version.Split('.').Select(p => int.TryParse(p, out var n) ? n : -1).Concat([0, 0, 0]).Take(3).ToArray();
        var highest = slots.DefaultIfEmpty(0).Max();
        if (parts[0] >= 0 && (parts[0], parts[1], parts[2]).CompareTo((12, 9, 1)) >= 0) return 499;
        return highest > 198 ? 499 : highest > 98 ? 198 : 98;
    }

    /// <summary>FL's 0x00BBGGRR colour as 0xRRGGBB.</summary>
    static int Rgb(uint bgr) => (int)((bgr & 0xFF) << 16 | (bgr & 0xFF00) | (bgr >> 16 & 0xFF));
}
