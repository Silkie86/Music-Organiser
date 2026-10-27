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
                _followTempo = null;
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
        TellTimeSignature(bitwig.TimeSignature);
        var now = Clock();
        if (bitwig.Playing && _follow.Following && _playing && Math.Abs(bitwig.Tempo - _playTempo) > 0.005 &&
            (Math.Abs(bitwig.Tempo - _playTempo) >= TempoJump || now - _tempoAt >= TempoEvery))
        {
            // Bitwig's tempo changed: the sound is made again at it, from where it was. Not at every look while it ramps,
            // which would stutter: a small change waits a moment, and the jumps meanwhile keep it with Bitwig.
            _tempoAt = now;
            _followTempo = bitwig.Tempo;
            _follow.Reset();
            await StartFollowing(() => PlayAsync(keepPlace: true));
            return;
        }
        long? at = _playing && _player is { } p && p.Playing == _playKey && p.Position is { } seconds
            ? (long)MidiRender.SecondsToTicks(seconds, Ppq, _playTempo) % Math.Max(1, _playLength)
            : null;
        // Going round again: the player lets go of the sound before the screen hears it ended, so a look just then sees
        // it stopped. That's waited out for a few looks, rather than starting it again on top of the next go round.
        if (at is null && _playing && _follow.Following && bitwig.Playing && ++_unplacedLooks <= UnplacedLooks) return;
        if (at is not null) _unplacedLooks = 0;
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
                _followTempo = null;   // Play here plays at the idea's own tempo again
                break;
        }
    }

    async Task StartFollowing(Func<Task<bool>> play)
    {
        _followStarting = true;
        _unplacedLooks = 0;
        try
        {
            if (!await play())
            {
                _follow.End();
                _followTempo = null;
            }
        }
        finally { _followStarting = false; }
    }

    /// <summary>A tempo change this big (BPM) makes the sound again straight away; a smaller one waits <see cref="TempoEvery"/>.</summary>
    const double TempoJump = 1;
    static readonly TimeSpan TempoEvery = TimeSpan.FromMilliseconds(500);
    TimeSpan _tempoAt = TimeSpan.MinValue / 2;

    /// <summary>How many looks in a row the sound may seem stopped while Bitwig plays before it's started again.</summary>
    const int UnplacedLooks = 4;
    int _unplacedLooks;

    /// <summary>The time now, for spacing out tempo changes. Tests put a stand-in here.</summary>
    internal Func<TimeSpan> Clock { get; set; } = () => TimeSpan.FromMilliseconds(Environment.TickCount64);

    string _toldSignature = "";

    /// <summary>
    /// Bitwig's song in another time signature than 4/4: the Playground's bars are 4/4, so it follows Bitwig's beats, and
    /// its bars won't fall on Bitwig's. Said once for each signature.
    /// </summary>
    void TellTimeSignature(string? signature)
    {
        if (string.IsNullOrEmpty(signature) || signature == _toldSignature) return;
        _toldSignature = signature;
        if (signature != "4/4")
            Message = $"Bitwig's song is in {signature} and the Playground plays in 4/4, so it follows Bitwig's beats: its bars won't line up with Bitwig's.";
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
    /// <summary>The track a part's clip is going across to now, until Bitwig has it (it launches it once it's in).</summary>
    string? _goingAcross;
    BitwigLink? _playLink;
    /// <summary>
    /// The project Bitwig had in front when Play in Bitwig was turned on (null until it's asked), by its name now (it
    /// follows a save under a new name): the parts only go across while it's in front, since Bitwig's track list follows
    /// whichever project is.
    /// </summary>
    string? _playProject;
    CancellationTokenSource? _sendSoon;
    bool _sendAgain, _toldPlaying;
    /// <summary>The last send was refused because another project was in front in Bitwig.</summary>
    bool _paused;

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
            (await InPlayProjectAsync(link => link.NewTrackWithIdAsync(name, ct: ct), ct)).Id ?? throw new DawLinkException(BitwigLink.NeedsTransport);
        BitwigPutClip = (track, path, ct) => InPlayProjectAsync(async link =>
        {
            await link.ReplaceClipAsync(track, path, 0, launch: true, ct);
            return true;
        }, ct);
        BitwigStopTracks = (tracks, ct) => InPlayProjectAsync(link => link.StopTracksAsync(tracks, ct), ct);
    }

    /// <summary>
    /// Makes a change in the project the parts play in. Refused because another project is in front, it looks whether
    /// that's the same project under a new name (saved for the first time, or with Save As): it is when it holds a track
    /// the Playground made there, and then the parts go to that name from now on, starting with this change.
    /// </summary>
    async Task<T> InPlayProjectAsync<T>(Func<BitwigLink, Task<T>> change, CancellationToken ct)
    {
        var link = await PlayLinkAsync(ct);
        try { return await change(link); }
        catch (DawLinkException e) when (BitwigLink.IsElsewhere(e))
        {
            // By its ID and the name it was given: another project's IDs could look alike.
            var ours = _bitwigTracks.Values.ToHashSet();
            var project = await link.ProjectAsync(ct);
            if (!project.Tracks.Any(t => t.Id is { } id && ours.Contains(id) && t.Name.EndsWith("(Playground)", StringComparison.Ordinal))) throw;
            link.OnlyIn = _playProject = project.Name;
        }
        return await change(link);
    }

    async Task<BitwigLink> PlayLinkAsync(CancellationToken ct)
    {
        if (_playLink is not { } link)
        {
            link = await _main.Live.Watch.ConnectBitwigAsync(ct);
            // An older extension takes the first parts into empty slots without playing them, so it would say it's playing
            // while Bitwig stays silent.
            if (!link.CanPlayAlong)
            {
                link.Dispose();
                throw new DawLinkException(BitwigLink.NeedsTransport);
            }
            _playLink = link;
        }
        // Turned on again with the link still open: the project in front now, not when it connected.
        else if (_playProject is null) await link.HelloAsync(ct);
        // Every change stays in that project: with another in front, a part would go on a new track there, and start it.
        link.OnlyIn = _playProject ??= link.Hello.Project;
        return link;
    }

    partial void OnPlayInBitwigChanged(bool value)
    {
        _sendSoon?.Cancel();
        if (value)
        {
            FollowBitwig = false;
            StopPlaying();
            _toldPlaying = false;
            _playProject = null;   // the one in front now
            _paused = false;
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
    internal Task SendToPlayAsync()
    {
        if (_sending is { IsCompleted: false })
        {
            _sendAgain = true;
            return _sending;
        }
        return _sending = SendAllAsync();

        async Task SendAllAsync()
        {
            do
            {
                _sendAgain = false;
                await SendChangedAsync();
            } while (_sendAgain && PlayInBitwig);
        }
    }

    /// <summary>The parts going across now, if they are.</summary>
    Task? _sending;

    async Task SendChangedAsync()
    {
        if (!PlayInBitwig) return;
        var channels = ChannelsToPlay(out var length);
        if (Loop is null) channels = channels.Select(c => c with { Notes = MidiBrowserViewModel.Cut(c.Notes, 0, length) }).ToList();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var playing = new List<string>();
        var sent = false;
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
                var title = TextScan.SafeFileName($"{name} (Playground{(had.Times % 2 == 1 ? " 2" : "")})");
                var file = Path.Combine(dir, title + ".mid");
                // Named as its file, so the clip's name in Bitwig changes at each swap: that's how the extension sees it's in.
                var clip = new MidiClip { Name = title, Ppq = Ppq, End = length };
                clip.Track(title).AddRange(c.Notes);
                File.WriteAllBytes(file, MidiWriter.Write(clip));
                if (!_bitwigTracks.TryGetValue(name, out var track))
                    track = _bitwigTracks[name] = await BitwigNewTrack($"{name} (Playground)", limit.Token);
                _goingAcross = track;
                try { await BitwigPutClip(track, file, limit.Token); }
                catch (DawLinkException e) when (e.Message.StartsWith("There's no track with ID", StringComparison.Ordinal))
                {
                    // Its track was deleted in Bitwig: a new one.
                    track = _bitwigTracks[name] = await BitwigNewTrack($"{name} (Playground)", limit.Token);
                    _goingAcross = track;
                    await BitwigPutClip(track, file, limit.Token);
                }
                _inBitwig[name] = (notes, had.Times + 1);
                _goingAcross = null;
                sent = true;
            }
            var gone = _inBitwig.Keys.Where(k => !playing.Contains(k)).ToList();
            if (gone.Count > 0)
            {
                await BitwigStopTracks(gone.Select(k => _bitwigTracks[k]).ToList(), limit.Token);
                foreach (var k in gone) _inBitwig.Remove(k);
                sent = true;
            }
            if (_paused && sent && PlayInBitwig)
            {
                _paused = false;
                Message = $"Playing in Bitwig again, in {StartedIn(_playProject)}.";
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
            // Another project in front: nothing went there, and the parts go across again from the next edit once it's back.
            _paused = BitwigLink.IsElsewhere(e);
            Message = _paused
                ? $"Playing in Bitwig is paused: another project is in front there, and the Playground's tracks are in {StartedIn(_playProject)}. " +
                  "Go back to it and your next edit goes across, or turn Play in Bitwig off and on to play in the one in front."
                : e.Message;
            if (e.Message == BitwigLink.NeedsTransport) PlayInBitwig = false;
            DropPlayLink();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SocketException or OperationCanceledException or TimeoutException)
        {
            Message = $"Playing in Bitwig failed: {e.Message}";
            DropPlayLink();
        }
        finally { _goingAcross = null; }
    }

    static string StartedIn(string? project) => project is { Length: > 0 } ? $"“{project}”" : "the project you started in";

    void DropPlayLink()
    {
        _playLink?.Dispose();
        _playLink = null;
    }

    async Task StopInBitwigAsync()
    {
        // A part going across as it's turned off finishes first (it stops before the next), so its track is stopped too.
        if (_sending is { IsCompleted: false } sending)
            try { await sending; }
            catch (Exception e) when (e is DawLinkException or IOException or SocketException or OperationCanceledException or TimeoutException) { }
        if (PlayInBitwig) return;   // turned on again meanwhile
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
            Message = e is DawLinkException d && BitwigLink.IsElsewhere(d)
                ? $"Couldn't stop the Playground's tracks in Bitwig: they're in {StartedIn(_playProject)}, and another project is in front there."
                : $"Couldn't stop the Playground's tracks in Bitwig: {e.Message}";
        }
        finally { DropPlayLink(); }
    }

    /// <summary>How long the app waits as it closes for Bitwig to stop the clips the Playground launched there.</summary>
    internal TimeSpan StopOnClose { get; set; } = TimeSpan.FromSeconds(1.5);

    void StopWithBitwig()
    {
        FollowBitwig = false;
        _sendSoon?.Cancel();
        _following?.Cancel();
        _followLink?.Dispose();
        // The app is closing: the Playground's clips in Bitwig stop too, rather than play on with nothing here to stop
        // them. Briefly, and off this thread (the window has gone by now); a Bitwig that doesn't answer is left as it is.
        var tracks = _inBitwig.Keys.Select(k => _bitwigTracks[k]).ToList();
        // A part still going across launches once Bitwig has it, so its track is stopped too (the stop waits behind it there).
        if (_goingAcross is { } going && !tracks.Contains(going)) tracks.Add(going);
        _inBitwig.Clear();
        if (tracks.Count > 0)
        {
            // Not disposed: the stop may still be using it once the wait is over.
            var limit = new CancellationTokenSource(StopOnClose);
            try { Task.Run(() => BitwigStopTracks(tracks, limit.Token)).Wait(StopOnClose); }
            catch (AggregateException) { }
        }
        _playLink?.Dispose();
    }
}
