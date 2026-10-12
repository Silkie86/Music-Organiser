using System.IO;
using MusicOrganiser.Core.Sounds;
using NAudio.Wave;

namespace MusicOrganiser.App.Audio;

/// <summary>Plays one sample at a time for auditioning. Starting a new one stops the last.</summary>
public sealed class AudioPlayer : IDisposable
{
    WaveOutEvent? _out;
    WaveStream? _reader;

    public event Action? Stopped;

    public string? Playing { get; private set; }

    public void Play(string path)
    {
        Stop();
        try
        {
            _reader = Preload(path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".aif", StringComparison.OrdinalIgnoreCase) ||
                              path.EndsWith(".aiff", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                ? new AudioFileReader(path)
                : new MediaFoundationReader(path));
            // A little more buffering than the minimum, so a busy disk or CPU can't starve playback.
            _out = new WaveOutEvent { DesiredLatency = 150, NumberOfBuffers = 3 };
            _out.Init(_reader);
            _out.PlaybackStopped += (_, _) => { Playing = null; Stopped?.Invoke(); };
            _out.Play();
            Playing = path;
        }
        catch (Exception)
        {
            Stop();
            throw;
        }
    }

    /// <summary>
    /// Decodes a sample fully into memory before it plays, so playback never waits on a disk
    /// that a scan is busy reading. Long files (over three minutes) still stream.
    /// </summary>
    static WaveStream Preload(WaveStream reader)
    {
        if (reader.TotalTime > TimeSpan.FromMinutes(3)) return reader;
        using (reader)
        {
            var memory = new MemoryStream();
            reader.CopyTo(memory);
            memory.Position = 0;
            return new RawSourceWaveStream(memory, reader.WaveFormat);
        }
    }

    public void Stop()
    {
        _out?.Stop();
        _out?.Dispose();
        _reader?.Dispose();
        _out = null;
        _reader = null;
        Playing = null;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Lets the analyser read MP3, FLAC, M4A and OGG through Windows Media Foundation
    /// (WAV and AIFF are read by the analyser itself).
    /// </summary>
    public static void RegisterDecoders()
    {
        foreach (var ext in new[] { ".mp3", ".flac", ".m4a", ".ogg", ".wma", ".aac" })
            AudioFile.Register(ext, DecodeWithMediaFoundation);
    }

    static AudioData DecodeWithMediaFoundation(string path, double maxSeconds)
    {
        using var reader = new MediaFoundationReader(path);
        var provider = reader.ToSampleProvider();
        var rate = provider.WaveFormat.SampleRate;
        var channels = provider.WaveFormat.Channels;
        var max = (long)(maxSeconds * rate) * channels;
        var interleaved = new List<float>(capacity: (int)Math.Min(max, 1 << 22));
        var buf = new float[rate * channels];
        int n;
        while (interleaved.Count < max && (n = provider.Read(buf, 0, buf.Length)) > 0)
            for (var i = 0; i < n && interleaved.Count < max; i++) interleaved.Add(buf[i]);
        var frames = interleaved.Count / channels;
        var data = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            data[c] = new float[frames];
            for (var f = 0; f < frames; f++) data[c][f] = interleaved[f * channels + c];
        }
        return new AudioData { SampleRate = rate, Channels = data, FileSeconds = reader.TotalTime.TotalSeconds };
    }
}
