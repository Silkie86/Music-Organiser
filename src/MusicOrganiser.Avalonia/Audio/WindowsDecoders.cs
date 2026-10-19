using System.Runtime.Versioning;
using MusicOrganiser.Core.Sounds;
using NAudio.Wave;

namespace MusicOrganiser.Avalonia.Audio;

/// <summary>
/// Windows' own decoders, as the WPF app uses them. Media Foundation lets the Sound Lab and the
/// player read MP3, FLAC, M4A, OGG, WMA and AAC (WAV and AIFF are read by the Sound Lab itself),
/// and Windows' audio codecs let the player play WAVs the Sound Lab can't read (ADPCM, µ-law, A-law,
/// GSM, as old drum-machine packs often are). On a Mac or Linux there's nothing to read MP3, FLAC and the
/// rest yet, so the Sound Lab leaves them out there.
/// </summary>
[SupportedOSPlatform("windows")]
static class WindowsDecoders
{
    static readonly string[] Extensions = [".mp3", ".flac", ".m4a", ".ogg", ".wma", ".aac"];

    public static void Register()
    {
        foreach (var ext in Extensions)
            AudioFile.Register(ext, (path, maxSeconds) => Decode(new MediaFoundationReader(path), maxSeconds));
    }

    /// <summary>True for the extensions Media Foundation reads here (".mp3" and so on).</summary>
    public static bool Handles(string extension) => Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens one of those files to play as it goes, rather than decoding it all first.</summary>
    public static WaveStream Open(string path) => new MediaFoundationReader(path);

    /// <summary>A WAV the Sound Lab can't read: compressed ones are converted to PCM by Windows' codecs.</summary>
    public static AudioData ReadWav(string path, double maxSeconds)
    {
        var reader = new WaveFileReader(path);
        if (reader.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat) return Decode(reader, maxSeconds);
        try { return Decode(WaveFormatConversionStream.CreatePcmStream(reader), maxSeconds); }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    static AudioData Decode(WaveStream stream, double maxSeconds)
    {
        using var reader = stream;
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
