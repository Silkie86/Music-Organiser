using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicOrganiser.Core;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// Playing along with Bitwig (Solomon, 2026-10-22: "have option to sync to daw so you can hear it at the same time", and
/// "Both" of the two ways): Follow Bitwig plays the Playground's own sound when Bitwig plays, from the same place, at
/// Bitwig's tempo; Play in Bitwig puts every part (and the chord track, when it's heard) on a track of its own in Bitwig,
/// as a launcher clip, swapped for the new notes after each edit, so Bitwig plays it with your own instruments. One at a
/// time: both together would play everything twice.
/// </summary>
public sealed partial class PlaygroundViewModel
{
    public const string FollowNudgeKey = "playground_follow_nudge";

    /// <summary>How much earlier than Bitwig says the Playground plays while following it, in milliseconds (later below 0).</summary>
    public static IReadOnlyList<int> FollowNudgeChoices { get; } = [-150, -100, -50, -25, 0, 25, 50, 100, 150, 200];

    [ObservableProperty] int _followNudge;
    partial void OnFollowNudgeChanged(int value)
    {
        SaveOption(FollowNudgeKey, value.ToString());
        _follow.Reset();
    }

    // ---- Follow Bitwig ----

    /// <summary>The Playground plays when Bitwig plays, from the same place in the bar, at Bitwig's tempo, and stops with it.</summary>
    [ObservableProperty] bool _followBitwig;

    readonly DawFollow _follow = new();
    CancellationTokenSource? _following;
    BitwigLink? _followLink;
    /// <summary>Bitwig's tempo, which the Playground plays at while it follows (null: the idea's own).</summary>
    double? _followTempo;
    /// <summary>While the sound is being made to start following, so a look at Bitwig meanwhile doesn't start it again.</summary>
    bool _followStarting;

    /// <summary>Asks Bitwig where it's playing. Tests put a stand-in here.</summary>
    internal Func<CancellationToken, Task<BitwigTransport>> BitwigTransport { get; set; }

    /// <summary>How often it asks while following.</summary>
    internal TimeSpan FollowEvery { get; set; } = TimeSpan.FromMilliseconds(50);

    async Task<BitwigTransport> AskTransportAsync(CancellationToken ct)
    {
        try
        {
            _followLink ??= await _main.Live.Watch.ConnectBitwigAsync(ct);
            return await _followLink.TransportAsync(ct);
        }
        catch
        {
            _followLink?.Dispose();
            _followLink = null;
            throw;
        }
    }

    partial void OnFollowBitwigChanged(bool value)
    {
        _following?.Cancel();
        _following = null;
        if (value)
        {
            PlayInBitwig = false;
            _following = new();
            _ = FollowAsync(_following.Token);
            return;
        }
        _followLink?.Dispose();
        _followLink = null;
        var was = _follow.Following;
        _follow.End();
        _followTempo = null;
        if (was) StopPlaying();
        Message = "Stopped following Bitwig.";
    }

    async Task FollowAsync(CancellationToken ct)
    {
        Message = "Following Bitwig: press Play there and the Playground plays along, at Bitwig's tempo.";
        var quiet = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var bitwig = await BitwigTransport(ct);
                if (ct.IsCancellationRequested) return;
                if (quiet) Message = "Bitwig's answering again: following it.";
                quiet = false;
                await FollowLook(bitwig);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (DawLinkException e) when (e.Message == BitwigLink.NeedsTransport)
            {
                FollowBitwig = false;
                Message = e.Message;
                return;
            }
            catch (Exception e) when (e is DawLinkException or IOException or SocketException or OperationCanceledException or TimeoutException)
            {
                if (!quiet) Message = "Bitwig isn't answering. Check it's open with the Music Organiser extension on: the Playground keeps trying.";
                quiet = true;
                if (_follow.Following)
                {
                    _follow.End();
                    StopPlaying();
                }
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            try { await Task.Delay(FollowEvery, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One look at Bitwig while following: start, jump, stop, or play on at Bitwig's new tempo.</summary>
    internal async Task FollowLook(BitwigTransport bitwig)
    {
        if (_followStarting) return;
        if (bitwig.Playing && !_follow.Following && !HasSomethingToPlay()) return;
        if (bitwig.Playing && _follow.Following && _playing && Math.Abs(bitwig.Tempo - _playTempo) > 0.005)
        {
            // Bitwig's tempo changed: the sound is made again at it, from where it was.
            _followTempo = bitwig.Tempo;
            _follow.Reset();
            await StartFollowing(() => PlayAsync(keepPlace: true));
            return;
        }
        long? at = _playing && _player is { } p && p.Playing == _playKey && p.Position is { } seconds
            ? (long)MidiRender.SecondsToTicks(seconds, Ppq, _playTempo) % Math.Max(1, _playLength)
            : null;
        var step = _follow.Next(bitwig.Playing, bitwig.Position, bitwig.Tempo, Ppq, _playing ? _playLength : LengthToPlay(), at, FollowNudge / 1000.0);
        switch (step.Move)
        {
            case FollowMove.Start:
                _followTempo = bitwig.Tempo;
                await StartFollowing(() => PlayAsync(startAt: (Loop?.Start ?? 0) + step.Tick));
                break;
            case FollowMove.Seek:
                try { _player?.Seek(MidiRender.TicksToSeconds(step.Tick, Ppq, _playTempo)); }
                catch (Exception e) when (e is IOException or InvalidOperationException) { _follow.Reset(); }
                break;
            case FollowMove.Stop:
                StopPlaying();
                break;
        }
    }

    async Task StartFollowing(Func<Task<bool>> play)
    {
        _followStarting = true;
        try
        {
            if (!await play()) _follow.End();
        }
        finally { _followStarting = false; }
    }

    bool HasSomethingToPlay() =>
        PlaygroundSession.Audible(Section).Any(l => l.Notes.Count > 0) || ChordTrackChannel(Section) is not null;

    /// <summary>How long what Play goes round is, in ticks: the highlight, or up to the end of the bar the last clip ends in.</summary>
    long LengthToPlay() =>
        Loop is { } r ? r.End - r.Start
            : PlayedTo(Section.Layers.SelectMany(l => l.Clips).Select(c => c.End).Concat(Section.ChordTrack.Select(c => c.End)).DefaultIfEmpty(0).Max());

    // ---- Play in Bitwig ----

    /// <summary>
    /// Every part plays in Bitwig, each on a track of its own named "(part) (Playground)", as a launcher clip, with the
    /// instruments you put on those tracks; edits here go across as you make them. Bitwig plays at its own tempo.
    /// </summary>
    [ObservableProperty] bool _playInBitwig;

    /// <summary>The Bitwig track each part plays on, by the part's name: kept, so a part played again goes back on its track.</summary>
    readonly Dictionary<string, string> _bitwigTracks = [];
    /// <summary>What was last put on each part's track (its notes and length), and how many times.</summary>
    readonly Dictionary<string, (string Notes, int Times)> _inBitwig = [];
    BitwigLink? _playLink;
    CancellationTokenSource? _sendSoon;
    bool _sendingToPlay, _sendAgain, _toldPlaying;

    /// <summary>Makes a track in Bitwig and says its ID. Tests put a stand-in here, and in the two below.</summary>
    internal Func<string, CancellationToken, Task<string>> BitwigNewTrack { get; set; }
    /// <summary>Swaps the clip in the first slot of the track with the ID given for a MIDI file, and launches it.</summary>
    internal Func<string, string, CancellationToken, Task> BitwigPutClip { get; set; }
    /// <summary>Stops the clips on the tracks with the IDs given.</summary>
    internal Func<IReadOnlyList<string>, CancellationToken, Task> BitwigStopTracks { get; set; }
    /// <summary>How long after an edit the parts go across, so a drag sends once it's done, not at every step.</summary>
    internal TimeSpan SendAfter { get; set; } = TimeSpan.FromMilliseconds(400);

    void SetUpBitwig()
    {
        BitwigTransport = AskTransportAsync;
        BitwigNewTrack = async (name, ct) =>
            (await (await PlayLinkAsync(ct)).NewTrackWithIdAsync(name, ct: ct)).Id ?? throw new DawLinkException(BitwigLink.NeedsTransport);
        BitwigPutClip = async (track, path, ct) => await (await PlayLinkAsync(ct)).ReplaceClipAsync(track, path, 0, launch: true, ct);
        BitwigStopTracks = async (tracks, ct) => await (await PlayLinkAsync(ct)).StopTracksAsync(tracks, ct);
    }

    async Task<BitwigLink> PlayLinkAsync(CancellationToken ct) => _playLink ??= await _main.Live.Watch.ConnectBitwigAsync(ct);

    partial void OnPlayInBitwigChanged(bool value)
    {
        _sendSoon?.Cancel();
        if (value)
        {
            FollowBitwig = false;
            StopPlaying();
            _toldPlaying = false;
            _ = SendToPlayAsync();
        }
        else _ = StopInBitwigAsync();
    }

    /// <summary>After an edit while playing in Bitwig: the parts that changed go across once the edits stop for a moment.</summary>
    void PlayInBitwigSoon()
    {
        if (!PlayInBitwig) return;
        _sendSoon?.Cancel();
        _sendSoon = new();
        _ = SendSoonAsync(_sendSoon.Token);

        async Task SendSoonAsync(CancellationToken ct)
        {
            try { await Task.Delay(SendAfter, ct); }
            catch (OperationCanceledException) { return; }
            await SendToPlayAsync();
        }
    }

    /// <summary>Puts each part that changed since it last went across into Bitwig, and stops the tracks of parts no longer playing.</summary>
    internal async Task SendToPlayAsync()
    {
        if (_sendingToPlay)
        {
            _sendAgain = true;
            return;
        }
        _sendingToPlay = true;
        try
        {
            do
            {
                _sendAgain = false;
                await SendChangedAsync();
            } while (_sendAgain && PlayInBitwig);
        }
        finally { _sendingToPlay = false; }
    }

    async Task SendChangedAsync()
    {
        if (!PlayInBitwig) return;
        var channels = ChannelsToPlay(out var length);
        if (Loop is null) channels = channels.Select(c => c with { Notes = MidiBrowserViewModel.Cut(c.Notes, 0, length) }).ToList();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var playing = new List<string>();
        try
        {
            var dir = ExportFolder();
            foreach (var c in channels)
            {
                var name = c.Name;
                for (var n = 2; playing.Contains(name); n++) name = $"{c.Name} {n}";
                playing.Add(name);
                var notes = $"{length}|{c.GmProgram}|" + string.Join(",", c.Notes.Select(x => $"{x.Start}:{x.Length}:{x.Key}:{x.Velocity}"));
                var had = _inBitwig.GetValueOrDefault(name);
                if (had.Notes == notes) continue;
                if (!PlayInBitwig) return;
                // Two files a part, taken in turn, so the one Bitwig may still be reading isn't written over.
                var file = Path.Combine(dir, TextScan.SafeFileName($"{name} (Playground{(had.Times % 2 == 1 ? " 2" : "")})") + ".mid");
                var clip = new MidiClip { Name = name, Ppq = Ppq, End = length };
                clip.Track(name).AddRange(c.Notes);
                File.WriteAllBytes(file, MidiWriter.Write(clip));
                if (!_bitwigTracks.TryGetValue(name, out var track))
                    track = _bitwigTracks[name] = await BitwigNewTrack($"{name} (Playground)", limit.Token);
                try { await BitwigPutClip(track, file, limit.Token); }
                catch (DawLinkException e) when (e.Message.StartsWith("There's no track with ID", StringComparison.Ordinal))
                {
                    // Its track was deleted in Bitwig: a new one.
                    track = _bitwigTracks[name] = await BitwigNewTrack($"{name} (Playground)", limit.Token);
                    await BitwigPutClip(track, file, limit.Token);
                }
                _inBitwig[name] = (notes, had.Times + 1);
            }
            var gone = _inBitwig.Keys.Where(k => !playing.Contains(k)).ToList();
            if (gone.Count > 0)
            {
                await BitwigStopTracks(gone.Select(k => _bitwigTracks[k]).ToList(), limit.Token);
                foreach (var k in gone) _inBitwig.Remove(k);
            }
            if (!_toldPlaying && PlayInBitwig)
            {
                _toldPlaying = true;
                Message = playing.Count == 0
                    ? "Playing in Bitwig: nothing to play yet. Parts you add go across as you add them."
                    : $"Playing in Bitwig: {(playing.Count == 1 ? "1 part" : $"{playing.Count} parts")} on {(playing.Count == 1 ? "its own track" : "tracks of their own")} there, named “… (Playground)”. " +
                      "Put your instruments on them; edits here go across as you make them, at Bitwig's tempo.";
            }
        }
        catch (DawLinkException e)
        {
            Message = e.Message;
            if (e.Message == BitwigLink.NeedsTransport) PlayInBitwig = false;
            DropPlayLink();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SocketException or OperationCanceledException or TimeoutException)
        {
            Message = $"Playing in Bitwig failed: {e.Message}";
            DropPlayLink();
        }
    }

    void DropPlayLink()
    {
        _playLink?.Dispose();
        _playLink = null;
    }

    async Task StopInBitwigAsync()
    {
        var tracks = _inBitwig.Keys.Select(k => _bitwigTracks[k]).ToList();
        _inBitwig.Clear();
        if (tracks.Count == 0) return;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await BitwigStopTracks(tracks, limit.Token);
            Message = "Stopped playing in Bitwig. Its tracks stay there, and the parts go back on them next time.";
        }
        catch (Exception e) when (e is DawLinkException or IOException or SocketException or OperationCanceledException or TimeoutException)
        {
            Message = $"Couldn't stop the Playground's tracks in Bitwig: {e.Message}";
        }
        finally { DropPlayLink(); }
    }

    void StopWithBitwig()
    {
        FollowBitwig = false;
        _sendSoon?.Cancel();
        _following?.Cancel();
        _followLink?.Dispose();
        _playLink?.Dispose();
    }
}
