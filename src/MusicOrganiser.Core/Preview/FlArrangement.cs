using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Preview;

/// <summary>
/// An FL Studio project's arrangement, two ways: its playlist as FL shows it (tracks holding pattern, audio and
/// automation clips), and one track per channel, the way "Open in Bitwig" lays it out.
/// </summary>
static class FlArrangement
{
    public const string PlaylistView = "FL Studio playlist";
    public const string BitwigView = "As it opens in Bitwig";
    /// <summary>FL's default playlist track colour, which isn't worth showing.</summary>
    const int DefaultTrackColour = 0x485156;
    /// <summary>FL's default channel colour.</summary>
    const int DefaultChannelColour = 0x5C656A;

    public static IReadOnlyList<Arrangement> Read(string path)
    {
        var (song, playlist) = new FlStudioReader().ReadSongAndPlaylist(path);
        var tempo = song.Tempo > 0 ? song.Tempo : 120;
        // Each sample is looked for once, however many clips play it: looking can mean several checks on a slow drive.
        var located = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Find(string sample) => located.TryGetValue(sample, out var f) ? f : located[sample] = ArrangementReader.Locate(sample, path);
        return [Playlist(song, playlist, Find, tempo), ByChannel(song, playlist, Find, tempo)];
    }

    /// <summary>Ticks per beat; a damaged header can say 0, so FL's default stands in.</summary>
    static double Ppq(int ppq) => ppq > 0 ? ppq : 96;

    static int? Colour(IReadOnlyDictionary<int, int> colours, int channel) =>
        colours.TryGetValue(channel, out var c) && c != DefaultChannelColour ? c : null;

    static Arrangement Playlist(FlSong song, FlPlaylist pl, Func<string, string?> find, double tempo)
    {
        double Beats(double ticks) => ticks / Ppq(pl.Ppq);
        var channels = song.Channels.ToDictionary(c => c.Index);
        var patterns = pl.Patterns.ToDictionary(p => p.Index);
        var notes = new List<string>();
        var lanes = new List<PreviewLane>();
        var last = pl.Items.Select(i => i.Track).DefaultIfEmpty(-1).Max();
        var tracks = pl.Tracks.ToDictionary(t => t.Index);

        for (var t = 0; t <= last; t++)
        {
            var info = tracks.GetValueOrDefault(t);
            var clips = new List<PreviewClip>();
            var kinds = new HashSet<LaneKind>();
            foreach (var item in pl.Items.Where(i => i.Track == t))
            {
                var start = Beats(item.Start);
                var length = Beats(item.Length);
                if (item.Pattern is { } p)
                {
                    kinds.Add(LaneKind.Instrument);
                    var pattern = patterns.GetValueOrDefault(p);
                    var from = (long)item.Offset;
                    var inClip = (pattern?.Notes ?? [])
                        .Where(n => n.Note.Start >= from && n.Note.Start < from + item.Length)
                        .Select(n => new PreviewNote(Beats(n.Note.Start - from), Beats(Math.Min(Math.Max(n.Note.Length, 1), from + item.Length - n.Note.Start)),
                                                     n.Note.Key, n.Note.Velocity, n.Note.Muted))
                        .ToList();
                    clips.Add(new PreviewClip(pattern?.Name ?? $"Pattern {p}", start, length, item.Muted) { Notes = inClip, Colour = pattern?.Colour });
                }
                else if (item.Channel is { } ch && pl.AutomationChannels.Contains(ch))
                {
                    kinds.Add(LaneKind.Automation);
                    var name = pl.ChannelNames.GetValueOrDefault(ch, $"Automation {ch + 1}");
                    // The clip's own curve, from as far into it as this placement starts (its offset is in ticks / 1000).
                    var curve = song.Curves.GetValueOrDefault(ch);
                    clips.Add(new PreviewClip(name, start, length, item.Muted)
                    {
                        Points = curve is null ? [] : Cut(curve, Beats(item.Offset * 1000), length),
                        Colour = Colour(pl.ChannelColours, ch),
                    });
                }
                else if (item.Channel is { } audio && channels.TryGetValue(audio, out var c))
                {
                    kinds.Add(LaneKind.Audio);
                    clips.Add(new PreviewClip(c.Name, start, length, item.Muted)
                    {
                        Audio = c.Sample.Length > 0 ? [Part(c, find, length, item.Offset, tempo)] : [],
                        Colour = Colour(pl.ChannelColours, audio),
                    });
                }
            }
            // An empty track only shows if it was named: an old project's clips can sit a hundred tracks down.
            if (clips.Count == 0 && info?.Name is not { Length: > 0 }) continue;
            var kind = kinds.Count == 1 ? kinds.First() : LaneKind.Instrument;
            lanes.Add(new PreviewLane(info?.Name is { Length: > 0 } n ? n : $"Track {t + 1}", kind)
            {
                Colour = info?.Colour is { } col && col != DefaultTrackColour ? col : null,
                Clips = clips,
                Detail = Describe(clips),
            });
        }
        if (pl.Items.All(i => i.Pattern is null) && pl.Patterns.Any(p => p.Notes.Count > 0))
            notes.Add("No patterns are in the playlist: FL Studio plays them in pattern mode. \"As it opens in Bitwig\" lays them out one after another.");

        return new Arrangement(PlaylistView, tempo, pl.BeatsPerBar, lanes)
        {
            Markers = pl.Markers.Select(m => new PreviewMarker(Beats(m.At), m.Name)).ToList(),
            Notes = notes,
        };
    }

    /// <summary>What a playlist track holds, as a line under its name: its patterns' or clips' names.</summary>
    static string Describe(List<PreviewClip> clips)
    {
        var names = clips.Select(c => c.Name).Distinct().ToList();
        return names.Count switch
        {
            0 => "",
            1 => names[0],
            _ => $"{names[0]} + {names.Count - 1} more",
        };
    }

    static Arrangement ByChannel(FlSong song, FlPlaylist pl, Func<string, string?> find, double tempo)
    {
        double Beats(long ticks) => ticks / Ppq(song.Ppq);
        var lanes = new List<PreviewLane>();
        // As BitwigProject.FromFl makes them: every channel with a plugin or sample gets a track, even one the playlist
        // never plays, except an audio clip channel with no notes, whose clips go on audio tracks.
        var audio = song.Channels.Where(c => c.AudioClips.Count > 0 && c.Sample.Length > 0).ToHashSet();
        var playing = song.Channels.Where(c => (c.Notes.Count > 0 || c.Clips.Count > 0 || !audio.Contains(c)) &&
                                               (c.Instrument is not null || c.Sample.Length > 0)).ToHashSet();
        foreach (var c in song.Channels)
        {
            var colour = Colour(pl.ChannelColours, c.Index);
            if (c.Clips.Count > 0 || playing.Contains(c))
                lanes.Add(new PreviewLane(c.Name, LaneKind.Instrument)
                {
                    Detail = c.Instrument?.Name ?? (c.Sample.Length > 0 ? "Sampler · " + c.Sample.Split('\\', '/')[^1] : ""),
                    Colour = colour,
                    Clips = c.Clips.Select(k => new PreviewClip(k.Name, Beats(k.Start), Beats(k.Length), k.Muted)
                    {
                        Notes = k.Notes.Select(n => new PreviewNote(Beats(n.Start), Beats(Math.Max(n.Length, 1)), n.Key, n.Velocity, n.Muted)).ToList(),
                    }).ToList(),
                });
            if (c.AudioClips.Count > 0)
                lanes.Add(new PreviewLane(c.Name, LaneKind.Audio)
                {
                    Detail = c.Sample.Length > 0 ? c.Sample.Split('\\', '/')[^1] : "Sample not saved in the project",
                    Colour = colour,
                    Clips = c.AudioClips.Select(k => new PreviewClip(c.Name, Beats(k.Start), Beats(k.Length), k.Muted)
                    {
                        Audio = c.Sample.Length > 0 ? [Part(c, find, Beats(k.Length), k.From, tempo)] : [],
                    }).ToList(),
                });
        }
        foreach (var a in song.Automation)
        {
            if (a.Points.Count == 0) continue;
            var start = a.Points[0].Beat;
            var length = Math.Max(a.Points[^1].Beat - start, 1);
            var target = song.Channels.FirstOrDefault(c => c.Index == a.Channel)?.Name;
            lanes.Add(new PreviewLane(a.Name, LaneKind.Automation)
            {
                Detail = target is null ? a.Control.ToString() : $"Moves {target}",
                Clips = [new PreviewClip(a.Name, start, length) { Points = Cut(a.Points, start, length) }],
            });
        }
        return new Arrangement(BitwigView, tempo, pl.BeatsPerBar, lanes)
        {
            Notes = song.Channels.Any(c => c.AudioClips.Count > 0 && c.Sample.Length == 0)
                ? ["Some audio clips are left out: FL Studio didn't save which sample they play."]
                : [],
        };
    }

    /// <summary>A channel's sample playing in a clip <paramref name="beats"/> long, from <paramref name="from"/> seconds in, stretched as the channel says.</summary>
    static AudioPart Part(FlChannel c, Func<string, string?> find, double beats, double from, double tempo)
    {
        var found = find(c.Sample);
        var s = c.Stretch;
        // Without the Time knob, how far the sample is stretched doesn't depend on its length; with it, the whole sample
        // is fitted to a length, so how fast it moves is only known once the file's length is.
        var part = s.Time > 0
            ? new AudioPart(found ?? c.Sample, 0, beats, from, null, s.PlayedSeconds(0, tempo) * tempo / 60)
            : new AudioPart(found ?? c.Sample, 0, beats, from, 60 / tempo / Math.Max(s.PlayedSeconds(1, tempo), 1e-6));
        return part with { Found = found is not null, Reversed = s.Reversed };
    }

    /// <summary>The points of a song-long curve between <paramref name="start"/> and start + length, timed from start, with the value at each end.</summary>
    static IReadOnlyList<PreviewPoint> Cut(IReadOnlyList<FlPoint> curve, double start, double length)
    {
        if (curve.Count == 0) return [];
        double At(double beat)
        {
            if (beat <= curve[0].Beat) return curve[0].Value;
            for (var i = 1; i < curve.Count; i++)
                if (beat <= curve[i].Beat)
                {
                    var (a, b) = (curve[i - 1], curve[i]);
                    return b.Beat - a.Beat < 1e-9 ? b.Value : a.Value + (b.Value - a.Value) * (beat - a.Beat) / (b.Beat - a.Beat);
                }
            return curve[^1].Value;
        }
        var points = new List<PreviewPoint> { new(0, At(start)) };
        points.AddRange(curve.Where(p => p.Beat > start && p.Beat < start + length).Select(p => new PreviewPoint(p.Beat - start, p.Value)));
        points.Add(new PreviewPoint(length, At(start + length)));
        return points;
    }
}
