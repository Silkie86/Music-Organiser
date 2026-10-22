using System.Collections.Concurrent;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Preview;

/// <summary>
/// An audio file's loudness over time for drawing its waveform: the loudest sample in each slice of
/// <see cref="SliceSeconds"/>, from 0 to 1. <see cref="Seconds"/> is how long the file is.
/// </summary>
public sealed record AudioPeaks(float[] Peaks, double Seconds)
{
    /// <summary>A hundredth of a second: fine enough to zoom into a bar, small enough to keep (a 5 minute file is 30,000 numbers).</summary>
    public const double SliceSeconds = 0.01;
    /// <summary>The longest stretch read; longer files are drawn up to here.</summary>
    public const double MaxSeconds = 20 * 60;

    static readonly ConcurrentDictionary<string, Lazy<AudioPeaks?>> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The loudest peak between two times in the file, in seconds.</summary>
    public float Max(double from, double to)
    {
        // Past what was read (files longer than MaxSeconds) there's nothing to show.
        if (Peaks.Length == 0 || from >= Peaks.Length * SliceSeconds) return 0;
        var a = Math.Clamp((int)(from / SliceSeconds), 0, Peaks.Length - 1);
        var b = Math.Clamp((int)Math.Ceiling(to / SliceSeconds), a + 1, Peaks.Length);
        var max = 0f;
        for (var i = a; i < b; i++) max = Math.Max(max, Peaks[i]);
        return max;
    }

    /// <summary>
    /// The file's peaks, read once and kept for the session. Null when it can't be read. A file that was busy (locked by
    /// another program) isn't remembered as unreadable, so it's tried again next time.
    /// </summary>
    public static AudioPeaks? For(string path)
    {
        var lazy = Cache.GetOrAdd(path, p => new Lazy<AudioPeaks?>(() => Read(p)));
        var peaks = lazy.Value;
        if (peaks is null && Busy(path)) Cache.TryRemove(new(path, lazy));
        return peaks;
    }

    static bool Busy(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return false; }
        catch (IOException) when (File.Exists(path)) { return true; }
        catch (Exception) { return false; }
    }

    static AudioPeaks? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || !AudioFile.CanRead(path)) return null;
            var audio = AudioFile.Read(path, MaxSeconds);
            if (audio.Length == 0 || audio.SampleRate <= 0) return null;
            var slice = Math.Max(1, (int)(audio.SampleRate * SliceSeconds));
            var peaks = new float[(audio.Length + slice - 1) / slice];
            foreach (var channel in audio.Channels)
                for (var i = 0; i < channel.Length; i++)
                {
                    var v = Math.Abs(channel[i]);
                    ref var p = ref peaks[i / slice];
                    if (v > p) p = v;
                }
            for (var i = 0; i < peaks.Length; i++) peaks[i] = Math.Min(1, peaks[i]);
            return new AudioPeaks(peaks, audio.FileSeconds > 0 ? audio.FileSeconds : audio.Seconds);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                       or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }
}
