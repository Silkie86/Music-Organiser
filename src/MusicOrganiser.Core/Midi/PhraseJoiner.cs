using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Preview;

namespace MusicOrganiser.Core.Midi;

/// <summary>
/// One clip inside a phrase joined from several: the hash its notes have on their own (as the browser lists that clip),
/// and where it sits in the phrase, in ticks at <see cref="PhraseAnalyser.Ppq"/>.
/// </summary>
public readonly record struct PhraseMember(string Hash, long Start, long Length)
{
    public static string Encode(IEnumerable<PhraseMember> members) => string.Join(" ", members.Select(m => $"{m.Hash}@{m.Start}+{m.Length}"));

    public static IReadOnlyList<PhraseMember> Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var list = new List<PhraseMember>();
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = part.IndexOf('@'), plus = part.IndexOf('+', Math.Max(0, at));
            if (at > 0 && plus > at && long.TryParse(part[(at + 1)..plus], out var start) && long.TryParse(part[(plus + 1)..], out var length))
                list.Add(new PhraseMember(part[..at], start, length));
        }
        return list;
    }
}

/// <summary>A clip on a track: its name, where it starts and how long it is, and its notes timed from its start (ticks at <see cref="PhraseAnalyser.Ppq"/>).</summary>
public sealed record TrackClip(string Name, long Start, long Length, IReadOnlyList<NoteEvent> Notes)
{
    /// <summary>The hash these notes get when the clip is listed on its own.</summary>
    public string Hash { get; } = PhraseAnalyser.HashOf(PhraseAnalyser.Normalise(Notes, PhraseAnalyser.Ppq).Take(PhraseAnalyser.MaxNotes).ToList());
    public long End => Start + Length;
}

/// <summary>A phrase joined from clips that play one after another on a track, ready to save.</summary>
public sealed record JoinedPhrase(string Track, string Clip, List<NoteEvent> Notes, IReadOnlyList<PhraseMember> Members);

/// <summary>
/// Finds the longer phrases a song is built from: clips on the same track that play back to back in the arrangement
/// (FL Studio's playlist, Bitwig's arranger, Studio One's song) are joined into one phrase, which is then cut where the
/// music breathes (Solomon, 2026-10-16: "sometime when im making music this could be part of a larger phrase"). Four
/// 4-bar patterns that make one 16-bar progression become that progression; the single clips stay listed too.
/// </summary>
public static class PhraseJoiner
{
    const long Bar = 4 * PhraseAnalyser.Ppq;

    /// <summary>Clips this close together (less than a bar of nothing between them) play as one phrase.</summary>
    public const long MaxGap = Bar;

    /// <summary>A project's tracks and the clips on each, from its arrangement; empty when the format has none.</summary>
    public static IReadOnlyList<(string Track, IReadOnlyList<TrackClip> Clips)> Tracks(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".flp":
            {
                // The playlist alone: where each pattern is placed and the notes in it. (Reading the whole song for
                // another DAW would also copy every plugin's settings, which is a lot of memory for nothing here.)
                var playlist = new FlStudioReader().ReadPlaylist(path);
                // With no patterns in the playlist, FL plays them in pattern mode: their order there means nothing.
                if (!playlist.Items.Any(i => i.Pattern is not null)) return [];
                var scale = (double)PhraseAnalyser.Ppq / Math.Max(1, playlist.Ppq);
                var patterns = playlist.Patterns.ToDictionary(p => p.Index);
                var byChannel = new Dictionary<int, List<TrackClip>>();
                foreach (var item in playlist.Items.Where(i => i.Pattern is not null && !i.Muted).OrderBy(i => i.Start))
                {
                    if (!patterns.TryGetValue(item.Pattern!.Value, out var pattern)) continue;
                    long from = (long)Math.Max(0, item.Offset), to = from + item.Length;
                    foreach (var channel in pattern.Notes.Where(n => n.Note.Start >= from && n.Note.Start < to).GroupBy(n => n.Channel))
                    {
                        var notes = channel.Select(n => n.Note with { Start = n.Note.Start - from, Length = Math.Min(n.Note.Length, to - n.Note.Start) });
                        if (!byChannel.TryGetValue(channel.Key, out var list)) byChannel[channel.Key] = list = [];
                        list.Add(new TrackClip(pattern.Name, (long)Math.Round(item.Start * scale), (long)Math.Round(item.Length * scale),
                                               PhraseAnalyser.Normalise(notes, playlist.Ppq, keepPlace: true)));
                    }
                }
                return byChannel.Select(kv => (playlist.ChannelNames.GetValueOrDefault(kv.Key, $"Channel {kv.Key + 1}"), (IReadOnlyList<TrackClip>)kv.Value)).ToList();
            }
            case ".bwproject" or ".song":
            {
                var arrangement = ArrangementReader.Read(path).FirstOrDefault();
                // Clips only in Bitwig's clip launcher are laid out scene by scene for the preview; that's not a song's order.
                if (arrangement is null || arrangement.Notes.Any(n => n.Contains("clip launcher", StringComparison.Ordinal))) return [];
                long Ticks(double beats) => (long)Math.Round(beats * PhraseAnalyser.Ppq);
                return arrangement.Lanes.Where(l => l.Kind == LaneKind.Instrument && !l.Muted)
                    .Select(l => (l.Name, (IReadOnlyList<TrackClip>)l.Clips.Where(c => !c.Muted).Select(c => new TrackClip(c.Name, Ticks(c.Start), Ticks(c.Length),
                        c.Notes.Where(n => !n.Muted).Select(n => new NoteEvent(Ticks(n.Start), Math.Max(1, Ticks(n.Length)), n.Key, n.Velocity))
                               .OrderBy(n => n.Start).ThenBy(n => n.Key).ToList())).ToList()))
                    .ToList();
            }
            default: return [];
        }
    }

    /// <summary>
    /// Each run of clips on a track that play back to back, as one phrase cut where the music breathes. A run needs two
    /// different clips: a clip played four times over is already listed as itself.
    /// </summary>
    public static IEnumerable<JoinedPhrase> Phrases(string track, IReadOnlyList<TrackClip> clips)
    {
        var playing = clips.Where(c => c.Notes.Count > 0).OrderBy(c => c.Start).ToList();
        var run = new List<TrackClip>();
        long end = long.MinValue;
        foreach (var clip in playing.Append(null))
        {
            if (clip is not null && (run.Count == 0 || clip.Start < end + MaxGap))
            {
                run.Add(clip);
                end = Math.Max(end, clip.End);
                continue;
            }
            if (run.Select(c => c.Hash).Distinct().Count() >= 2)
                foreach (var phrase in Join(track, run)) yield return phrase;
            run = clip is null ? [] : [clip];
            end = clip?.End ?? long.MinValue;
        }
    }

    static IEnumerable<JoinedPhrase> Join(string track, List<TrackClip> run)
    {
        // The run from the start of its first clip's bar.
        var zero = run[0].Start / Bar * Bar;
        var notes = run.SelectMany(c => c.Notes.Select(n => n with { Start = n.Start + c.Start - zero }))
                       .OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
        var starts = run.Where(c => (c.Start - zero) % Bar == 0).Select(c => (c.Start - zero) / Bar).ToHashSet();
        foreach (var (where, piece, from) in PhraseAnalyser.Split(notes, starts))
        {
            var to = from + PhraseAnalyser.BarsOf(piece) * Bar;
            var members = run.Where(c => c.Start - zero < to && c.End - zero > from)
                             .Select(c => new PhraseMember(c.Hash, c.Start - zero - from, c.Length)).ToList();
            if (members.Select(m => m.Hash).Distinct().Count() < 2) continue;   // just one clip again, which is listed already
            var names = Steps(run.Where(c => c.Start - zero < to && c.End - zero > from).Select(c => c.Name));
            yield return new JoinedPhrase(track, where.Length == 0 ? names : $"{names} · {where}", piece, members);
        }
    }

    /// <summary>The clips in order, a clip played again straight away once ("Verse ×2 → Hook"), and the first few only.</summary>
    static string Steps(IEnumerable<string> names)
    {
        var steps = new List<(string Name, int Times)>();
        foreach (var name in names)
            if (steps.Count > 0 && steps[^1].Name == name) steps[^1] = (name, steps[^1].Times + 1);
            else steps.Add((name, 1));
        var text = string.Join(" → ", steps.Take(4).Select(s => s.Times > 1 ? $"{s.Name} ×{s.Times}" : s.Name));
        return steps.Count > 4 ? text + " → …" : text;
    }
}
