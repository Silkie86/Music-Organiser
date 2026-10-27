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

    /// <summary>How much is read at a time: a long recording is gone through in parts, never decoded whole.</summary>
    const double PartSeconds = 10;

    static AudioPeaks? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || !AudioFile.CanRead(path)) return null;
            // WAV and AIFF a part at a time: decoded whole, a 20 minute recording took most of a gigabyte for one waveform.
            var part = AudioFile.CanReadFrom(path) ? PartSeconds : MaxSeconds;
            var audio = AudioFile.Read(path, part, fromFrame: 0);
            if (audio.Length == 0 || audio.SampleRate <= 0) return null;
            var (rate, fileSeconds) = (audio.SampleRate, audio.FileSeconds);
            var slice = Math.Max(1, (int)(rate * SliceSeconds));
            var most = (long)(MaxSeconds * rate);
            var peaks = new List<float>();
            long done = 0;
            while (true)
            {
                var frames = (int)Math.Min(audio.Length, most - done);
                var need = (int)((done + frames + slice - 1) / slice);
                while (peaks.Count < need) peaks.Add(0);
                var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(peaks);
                foreach (var channel in audio.Channels)
                    for (var i = 0; i < frames; i++)
                    {
                        var v = Math.Abs(channel[i]);
                        ref var p = ref span[(int)((done + i) / slice)];
                        if (v > p) p = v;
                    }
                done += frames;
                if (part >= MaxSeconds || audio.Length < (long)(part * rate) || done >= most) break;   // the end, or as far as is drawn
                audio = AudioFile.Read(path, part, done);
                if (audio.Length == 0) break;
            }
            var result = peaks.Select(p => Math.Min(1, p)).ToArray();
            return new AudioPeaks(result, fileSeconds > 0 ? fileSeconds : (double)done / rate);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                       or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }
}
