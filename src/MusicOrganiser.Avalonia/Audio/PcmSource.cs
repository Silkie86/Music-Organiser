using MusicOrganiser.Core.Sounds;
using NAudio.Wave;

namespace MusicOrganiser.Avalonia.Audio;

/// <summary>
/// Sound for the player, read a piece at a time: from the file as it plays, so a long recording
/// starts at once and plays to the end, or from memory (a MIDI preview, or a file only the Sound
/// Lab's own reader understands). Always mono or stereo: wider files are folded into stereo.
/// </summary>
abstract class PcmSource : IDisposable
{
    public abstract int SampleRate { get; }
    public abstract int Channels { get; }

    /// <summary>Moves to a time in the sound (clamped to its length).</summary>
    public abstract void SeekTo(double seconds);

    /// <summary>Fills <paramref name="interleaved"/> with as many whole frames as fit; returns how many (0 at the end).</summary>
    public abstract int Read(float[] interleaved);

    public virtual void Dispose() { }

    /// <summary>Where the sound comes from and its format as read, for the audio log.</summary>
    public abstract string Describe();

    /// <summary>
    /// Opens a file to play. WAV and AIFF (and on Windows MP3, FLAC and the rest) stream from disk;
    /// anything those readers can't take is decoded whole by the Sound Lab's reader, up to
    /// <paramref name="maxSecondsInMemory"/>.
    /// </summary>
    public static PcmSource Open(string path, double maxSecondsInMemory)
    {
        if (TryStream(path) is { } stream) return stream;
        AudioData audio;
        try { audio = AudioFile.Read(path, maxSecondsInMemory); }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException && OperatingSystem.IsWindows() &&
                                  Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            audio = WindowsDecoders.ReadWav(path, maxSecondsInMemory);
        }
        return new MemorySource(audio.Channels, audio.SampleRate);
    }

    static StreamSource? TryStream(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        WaveStream? reader = null;
        try
        {
            reader = ext switch
            {
                ".wav" => new WaveFileReader(path),
                ".aif" or ".aiff" or ".aifc" => new AiffFileReader(path),
                _ when OperatingSystem.IsWindows() && WindowsDecoders.Handles(ext) => WindowsDecoders.Open(path),
                _ => null,
            };
            if (reader is null || reader.Length == 0) throw new InvalidDataException("Nothing to stream");
            switch (reader.WaveFormat.Encoding)
            {
                case WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat:
                    return new StreamSource(reader);   // throws for sizes NAudio can't convert (12-bit, say)
                // Most 24-bit and multichannel WAVs: plain PCM or float behind a longer header.
                case WaveFormatEncoding.Extensible when Plain(reader.WaveFormat) is { } plain:
                    return new StreamSource(new Relabelled(reader, plain));
                // ADPCM, µ-law, A-law and GSM WAVs (old drum-machine packs): Windows' codecs turn them into PCM as they
                // play. Decoded whole first, a long one held the window up for a click to play it.
                case not WaveFormatEncoding.Extensible when ext == ".wav" && OperatingSystem.IsWindows():
                    var pcm = WindowsDecoders.Pcm((WaveFileReader)reader);
                    try { return new StreamSource(pcm); }
                    catch
                    {
                        pcm.Dispose();
                        throw;
                    }
                default:
                    reader.Dispose();
                    return null;
            }
        }
        catch (Exception)
        {
            reader?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// The plain PCM or float format behind a WAVE_FORMAT_EXTENSIBLE header, from the sub-format id
    /// in its extra bytes (NAudio reads those headers without looking inside), or null for anything else.
    /// </summary>
    static WaveFormat? Plain(WaveFormat format)
    {
        if (format is not WaveFormatExtraData { ExtraData.Length: >= 22 } extensible) return null;
        var subFormat = BitConverter.ToUInt16(extensible.ExtraData, 6);   // the GUID's first bytes: 1 = PCM, 3 = float
        int rate = format.SampleRate, channels = format.Channels, bits = format.BitsPerSample;
        return subFormat switch
        {
            1 => new WaveFormat(rate, bits, channels),
            3 => WaveFormat.CreateCustomFormat(WaveFormatEncoding.IeeeFloat, rate, channels, rate * channels * bits / 8, channels * bits / 8, bits),
            _ => null,
        };
    }
}

/// <summary>A stream read as another format (the plain one behind an extensible WAV header), closing the file with it.</summary>
sealed class Relabelled(WaveStream inner, WaveFormat format) : RawSourceWaveStream(inner, format)
{
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Sound already in memory.</summary>
sealed class MemorySource : PcmSource
{
    readonly float[][] _channels;
    readonly int _rate;
    int _frame;

    public MemorySource(float[][] channels, int rate)
    {
        _channels = channels.Length > 2 ? OpenAlPlayer.DownmixToStereo(channels) : channels;
        _rate = rate;
    }

    public override int SampleRate => _rate;
    public override int Channels => _channels.Length;
    int Frames => _channels[0].Length;

    public override void SeekTo(double seconds) => _frame = (int)Math.Clamp(seconds * _rate, 0, Frames);

    public override string Describe() => $"decoded into memory, {Frames / (double)_rate:0.00} s";

    public override int Read(float[] interleaved)
    {
        var n = Math.Min(interleaved.Length / Channels, Frames - _frame);
        for (var f = 0; f < n; f++)
            for (var c = 0; c < Channels; c++)
                interleaved[f * Channels + c] = _channels[c][_frame + f];
        _frame += n;
        return n;
    }
}

/// <summary>Sound read from a file as it plays.</summary>
sealed class StreamSource(WaveStream stream) : PcmSource
{
    readonly ISampleProvider _samples = stream.ToSampleProvider();
    readonly int _fileChannels = stream.WaveFormat.Channels;
    float[] _wide = [];

    public override int SampleRate => stream.WaveFormat.SampleRate;
    public override int Channels => Math.Min(_fileChannels, 2);

    // Timed by whole frames, not the header's bytes a second: a WAV can say 0 there and still play (NAudio's TotalTime
    // then throws), and a log line or a seek mustn't stop it.
    int Align => Math.Max(1, stream.WaveFormat.BlockAlign);

    public override string Describe() =>
        $"streamed by {stream.GetType().Name} as {stream.WaveFormat}, {stream.Length / Align / (double)Math.Max(1, SampleRate):0.00} s";

    /// <summary>Moves to a time, on a whole sample frame so the channels stay in step.</summary>
    public override void SeekTo(double seconds)
    {
        var frame = (long)(Math.Max(0, seconds) * SampleRate);
        stream.Position = Math.Min(frame * Align, Math.Max(0, stream.Length - stream.Length % Align));
    }

    public override int Read(float[] interleaved)
    {
        if (_fileChannels <= 2)
        {
            var wanted = interleaved.Length - interleaved.Length % _fileChannels;
            return ReadFully(interleaved, wanted) / _fileChannels;
        }
        // Quad, 5.1 and the like: fold the extra channels into left and right as they're read.
        var frames = interleaved.Length / 2;
        if (_wide.Length < frames * _fileChannels) _wide = new float[frames * _fileChannels];
        var read = ReadFully(_wide, frames * _fileChannels) / _fileChannels;
        const float Share = 0.7071f;
        for (var f = 0; f < read; f++)
        {
            var src = f * _fileChannels;
            float left = _wide[src], right = _wide[src + 1];
            for (var c = 2; c < _fileChannels; c++)
            {
                left += _wide[src + c] * Share;
                right += _wide[src + c] * Share;
            }
            interleaved[f * 2] = left;
            interleaved[f * 2 + 1] = right;
        }
        return read;
    }

    /// <summary>Some readers hand back less than asked before the end; keep asking until full or done.</summary>
    int ReadFully(float[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = _samples.Read(buffer, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    public override void Dispose() => stream.Dispose();
}
