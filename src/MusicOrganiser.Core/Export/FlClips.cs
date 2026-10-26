using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Export;

/// <summary>An FL channel's clips as another DAW's track holds them.</summary>
static class FlClips
{
    /// <summary>
    /// A channel's clips, in order, as one track can hold them: clips that overlap in FL (on different
    /// playlist tracks) become one, where notes from a muted clip are muted and a note both play is
    /// there once. A channel made without clips (not read from a file) gets one from the start of the
    /// song holding its notes, in whole bars.
    /// </summary>
    public static List<FlClip> Of(FlChannel c, int ppq)
    {
        if (c.Clips.Count == 0)
        {
            if (c.Notes.Count == 0) return [];
            var bar = 4L * ppq;
            var end = c.Notes.Max(n => n.Start + n.Length);
            return [new FlClip(c.Name, 0, Math.Max(bar, (end + bar - 1) / bar * bar), c.Notes)];
        }
        var clips = new List<FlClip>();
        foreach (var clip in c.Clips.OrderBy(x => x.Start))
        {
            if (clips.Count == 0 || clip.Start >= clips[^1].Start + clips[^1].Length)
            {
                clips.Add(clip);
                continue;
            }
            var last = clips[^1];
            var muted = last.Muted && clip.Muted;
            IEnumerable<NoteEvent> Part(FlClip x) =>
                x.Notes.Select(n => n with { Start = x.Start - last.Start + n.Start, Muted = n.Muted || x.Muted && !muted });
            clips[^1] = last with
            {
                Length = Math.Max(last.Start + last.Length, clip.Start + clip.Length) - last.Start,
                Notes = Part(last).Concat(Part(clip)).OrderBy(n => n.Start).ThenBy(n => n.Key).ThenBy(n => n.Muted)
                                  .DistinctBy(n => (n.Start, n.Key)).ToList(),
                Muted = muted,
            };
        }
        return clips;
    }

    /// <summary>Whether all of a track's clips sit on muted FL playlist tracks, so the track is muted rather than they are.</summary>
    public static bool AllOnMutedTracks(IReadOnlyCollection<FlClip> clips) => clips.Count > 0 && clips.All(x => x.OnMutedTrack);

    /// <inheritdoc cref="AllOnMutedTracks(IReadOnlyCollection{FlClip})"/>
    public static bool AllOnMutedTracks(IReadOnlyCollection<FlAudioClip> clips) => clips.Count > 0 && clips.All(x => x.OnMutedTrack);

    /// <summary>
    /// A track's clips with those on muted FL playlist tracks muted, unless the track itself is muted for them
    /// (<paramref name="trackMuted"/>), when they keep only their own mutes.
    /// </summary>
    public static List<FlClip> OnMutedTracks(IEnumerable<FlClip> clips, bool trackMuted) =>
        clips.Select(x => x with { Muted = x.Muted || x.OnMutedTrack && !trackMuted }).ToList();

    /// <inheritdoc cref="OnMutedTracks(IEnumerable{FlClip}, bool)"/>
    public static List<FlAudioClip> OnMutedTracks(IEnumerable<FlAudioClip> clips, bool trackMuted) =>
        clips.Select(x => x with { Muted = x.Muted || x.OnMutedTrack && !trackMuted }).ToList();

    /// <summary>
    /// Gives steps from FL's step sequencer, which have no length, the length FL plays them for: FL plays a
    /// step until its sound ends. Here that's until the next note on the same key or the end of the clip,
    /// and no longer than <paramref name="longest"/> gives for the key (a sample's length), when it knows.
    /// </summary>
    public static IReadOnlyList<NoteEvent> Lengthen(IReadOnlyList<NoteEvent> notes, long clipLength, Func<int, long?>? longest = null)
    {
        if (!notes.Any(n => n.Step)) return notes;
        // Each key's starts in order, so the next is found by halves: a whole song's steps can all be on one key.
        var starts = notes.GroupBy(n => n.Key).ToDictionary(g => g.Key, g => g.Select(n => n.Start).Distinct().Order().ToArray());
        return notes.Select(n =>
        {
            if (!n.Step) return n;
            var on = starts[n.Key];
            var i = Array.BinarySearch(on, n.Start);   // always there: it's this note's own start
            var next = i + 1 < on.Length ? on[i + 1] : clipLength;
            var length = Math.Min(next, clipLength) - n.Start;
            if (longest?.Invoke(n.Key) is { } most) length = Math.Min(length, most);
            return n with { Length = Math.Max(n.Length, length) };
        }).ToList();
    }
}
