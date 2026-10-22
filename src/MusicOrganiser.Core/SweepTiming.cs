using System.Diagnostics;

namespace MusicOrganiser.Core;

/// <summary>
/// How long a sound swept in the galaxy takes to be heard, step by step, summed up in the run's log every few seconds
/// of sweeping (see <see cref="AppLog"/>): the mouse reaching the app, waiting its turn (the sweep gap), the preview
/// thread starting, opening the file and handing it to OpenAL, then OpenAL's next mix and its output delay (Windows'
/// and the audio interface's included). It's the evidence for where a sound that trails the mouse loses its time.
/// </summary>
public static class SweepTiming
{
    const double Every = 10;   // seconds between notes
    const double StaleMs = 2000;   // no sweep keeps a star waiting this long

    static readonly object Gate = new();
    // Each sound on its way: when the mouse reached its star, how late the app heard about that move, when it was asked for.
    static readonly Dictionary<string, (long Reached, double InputMs, long Asked, long Opening)> Pending = [];
    static readonly Step Input = new(), Turn = new(), Start = new(), Open = new(), OpenAhead = new(), OpenCold = new();
    static int _sounds;
    static double _mixMs = -1, _outputMs = -1;
    static long _since = Stopwatch.GetTimestamp();

    sealed class Step
    {
        public double Total, Max;
        public int Count;
        public void Add(double ms) { Total += ms; Max = Math.Max(Max, ms); Count++; }
        public double Average => Count == 0 ? 0 : Total / Count;
        public void Clear() => Total = Max = Count = 0;
    }

    static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// The mouse reached a star; <paramref name="inputMs"/> is how long after the move happened the app got to it (the
    /// window's thread busy drawing, say), or 0 when the system doesn't say.
    /// </summary>
    public static void Reached(string path, double inputMs)
    {
        lock (Gate)
        {
            var now = Stopwatch.GetTimestamp();
            // A long sweep's passed stars that never played: drop those, never one already asked for or being opened.
            if (Pending.Count > 64)
                foreach (var (key, old) in Pending.ToList())
                    if (old.Asked == 0) Pending.Remove(key);
            // Reached again before it was asked for: it waits from now, not from the first pass (seconds ago, perhaps).
            if (!Pending.TryGetValue(path, out var p) || p.Asked == 0)
                Pending[path] = (now, Math.Max(0, inputMs), 0, 0);
        }
    }

    /// <summary>Its turn came: the preview was asked for.</summary>
    public static void Asked(string path)
    {
        lock (Gate)
        {
            var now = Stopwatch.GetTimestamp();
            // Reached long ago and passed by (a click on it now, say, never reaches it again): its wait starts now.
            // Counted from the old pass, one such star once read as a 42 s wait and made a 10 s average 1.8 s.
            Pending[path] = Pending.TryGetValue(path, out var p) && Ms(p.Reached, now) < StaleMs ? p with { Asked = now } : (now, 0, now, 0);
        }
    }

    /// <summary>The preview thread is opening it now.</summary>
    public static void Opening(string path)
    {
        lock (Gate)
            if (Pending.TryGetValue(path, out var p) && p.Asked != 0) Pending[path] = p with { Opening = Stopwatch.GetTimestamp() };
    }

    /// <summary>Opened and handed to OpenAL, which mixes it in from its next pass.</summary>
    /// <param name="readAhead">Whether its start had already been read ahead (<c>PreviewWorker.Warm</c>) when it was opened.</param>
    public static void Started(string path, bool readAhead = false)
    {
        string? note = null;
        lock (Gate)
        {
            if (!Pending.Remove(path, out var p) || p.Asked == 0 || p.Opening == 0) return;
            var now = Stopwatch.GetTimestamp();
            Input.Add(p.InputMs);
            Turn.Add(Ms(p.Reached, p.Asked));
            Start.Add(Ms(p.Asked, p.Opening));
            Open.Add(Ms(p.Opening, now));
            (readAhead ? OpenAhead : OpenCold).Add(Ms(p.Opening, now));
            _sounds++;
            if (Ms(_since, now) / 1000 >= Every) note = Note(now);
        }
        if (note is not null) AppLog.Info("Sweep", note);
    }

    /// <summary>
    /// What the output adds after that, from OpenAL itself: the wait for its next mix (half its mixing period, on
    /// average) and how long a mixed sound takes to come out (its buffer, Windows' and the interface's). Negative: not known.
    /// </summary>
    public static void Output(double mixMs, double outputMs)
    {
        lock (Gate) (_mixMs, _outputMs) = (mixMs, outputMs);
    }

    static string Note(long now)
    {
        var total = Input.Average + Turn.Average + Start.Average + Open.Average + Math.Max(0, _mixMs) + Math.Max(0, _outputMs);
        var note = $"{_sounds} swept sounds in {Ms(_since, now) / 1000:0} s, from the mouse over a star to hearing it: " +
                   $"mouse reaching the app {Input.Average:0} ms (slowest {Input.Max:0}), waiting its turn {Turn.Average:0} ms " +
                   $"(slowest {Turn.Max:0}), preview starting {Start.Average:0.0} ms, opening the file {Open.Average:0.0} ms " +
                   $"(slowest {Open.Max:0}; {OpenAhead.Count} read ahead took {OpenAhead.Average:0.0} ms, {OpenCold.Count} not read ahead {OpenCold.Average:0.0} ms), " +
                   (_mixMs >= 0 ? $"OpenAL's next mix about {_mixMs:0} ms, " : "") +
                   (_outputMs >= 0 ? $"then out of the speakers after {_outputMs:0} ms (OpenAL's buffer, Windows and the interface). " : "output delay not known. ") +
                   $"About {total:0} ms in all.";
        foreach (var s in new[] { Input, Turn, Start, Open, OpenAhead, OpenCold }) s.Clear();
        _sounds = 0;
        _since = now;
        return note;
    }
}
