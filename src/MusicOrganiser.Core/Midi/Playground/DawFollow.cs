namespace MusicOrganiser.Core.Midi.Playground;

/// <summary>What the Playground does next to keep in step with the DAW it follows.</summary>
public enum FollowMove
{
    /// <summary>Nothing: it's in step, or still looking.</summary>
    Wait,
    /// <summary>The DAW started playing: play from <see cref="FollowStep.Tick"/>.</summary>
    Start,
    /// <summary>It's drifted or the DAW jumped: carry on from <see cref="FollowStep.Tick"/>.</summary>
    Seek,
    /// <summary>The DAW stopped: stop too.</summary>
    Stop,
}

/// <param name="Tick">For Start and Seek, where in what the Playground plays round, in ticks from its start.</param>
public readonly record struct FollowStep(FollowMove Move, long Tick = 0);

/// <summary>
/// Follow Bitwig (Solomon, 2026-10-22: "have option to sync to daw so you can hear it at the same time"): the Playground
/// plays when Bitwig plays, from the same place in the bar, and stops when it stops. Bitwig's bar 1 is the Playground's,
/// so on bar 9 of the song an 8-bar idea is back at its own bar 1.
/// <para>
/// Bitwig says where it is many times a second, but each answer can be a little old (it sends its position along when it
/// gets round to it, and the question takes a moment), never early. So the most ahead of the last few differences says
/// best where Bitwig really is, and the Playground only jumps when that's further off than you'd hear.
/// </para>
/// </summary>
public sealed class DawFollow
{
    /// <summary>How far apart the two can be, in seconds, before the Playground jumps to where Bitwig is.</summary>
    public const double Tolerance = 0.03;

    /// <summary>How many looks are weighed before a jump: about a fifth of a second of them.</summary>
    public const int Looks = 4;

    readonly List<double> _ahead = [];

    /// <summary>Whether the Playground is playing because Bitwig is.</summary>
    public bool Following { get; private set; }

    /// <summary>Forgets what it saw, after the Playground started again from somewhere (a new tempo, an edit).</summary>
    public void Reset() => _ahead.Clear();

    /// <summary>Stops following: the Playground stopped, or was told to stop following.</summary>
    public void End()
    {
        Following = false;
        _ahead.Clear();
    }

    /// <summary>The next move, from one look at Bitwig and at the Playground.</summary>
    /// <param name="playing">Whether Bitwig is playing.</param>
    /// <param name="beats">Where Bitwig is, in beats from the start of its song.</param>
    /// <param name="tempo">Bitwig's tempo, which the Playground plays at while it follows.</param>
    /// <param name="length">How long what the Playground plays round is, in ticks.</param>
    /// <param name="at">Where the Playground is in that, in ticks, or null when it isn't playing.</param>
    /// <param name="nudge">Seconds to play ahead of where Bitwig says (behind when below 0), for sound that comes out late.</param>
    public FollowStep Next(bool playing, double beats, double tempo, int ppq, long length, long? at, double nudge = 0)
    {
        if (!playing)
        {
            _ahead.Clear();
            // Playing on its own while Bitwig's stopped (Play pressed in the Playground) is left alone.
            if (!Following) return new(FollowMove.Wait);
            Following = false;
            return new(FollowMove.Stop);
        }
        length = Math.Max(1, length);
        tempo = Math.Max(1, tempo);
        var target = Mod((long)Math.Round((beats + nudge * tempo / 60) * ppq), length);
        if (!Following || at is not { } now)
        {
            Following = true;
            _ahead.Clear();
            return new(FollowMove.Start, target);
        }
        _ahead.Add(Around(target - now, length) / (double)ppq * 60 / tempo);
        if (_ahead.Count < Looks) return new(FollowMove.Wait);
        var ahead = _ahead.Max();
        if (Math.Abs(ahead) <= Tolerance)
        {
            _ahead.RemoveAt(0);
            return new(FollowMove.Wait);
        }
        _ahead.Clear();
        return new(FollowMove.Seek, Mod(now + (long)Math.Round(ahead * tempo / 60 * ppq), length));
    }

    static long Mod(long x, long n) => (x % n + n) % n;

    /// <summary>How far ahead <paramref name="x"/> ticks is going round <paramref name="n"/>: from -n/2 to n/2.</summary>
    static long Around(long x, long n)
    {
        var m = Mod(x, n);
        return m > n / 2 ? m - n : m;
    }
}
