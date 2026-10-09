using System.Buffers.Binary;
using System.Text;

namespace MusicOrganiser.Core.Sounds;

/// <summary>Decoded audio: one float array per channel, plus loop metadata some sample packs embed.</summary>
public sealed class AudioData
{
    public required int SampleRate { get; init; }
    public required float[][] Channels { get; init; }
    /// <summary>Length of the whole file in seconds (may be longer than what was decoded).</summary>
    public double FileSeconds { get; init; }
    /// <summary>Tempo from an ACID chunk, when the pack author set one.</summary>
    public double? EmbeddedTempo { get; init; }
    /// <summary>MIDI root note from an ACID or sampler chunk.</summary>
    public int? EmbeddedRootNote { get; init; }
    /// <summary>True when an ACID chunk marks the file as a loop, false when it marks a one-shot.</summary>
    public bool? EmbeddedIsLoop { get; init; }

    public int Length => Channels.Length == 0 ? 0 : Channels[0].Length;
    public double Seconds => (double)Length / SampleRate;

    public float[] Mono()
    {
        if (Channels.Length == 1) return Channels[0];
        var mono = new float[Length];
        foreach (var ch in Channels)
            for (var i = 0; i < mono.Length; i++) mono[i] += ch[i];
        var scale = 1f / Channels.Length;
        for (var i = 0; i < mono.Length; i++) mono[i] *= scale;
        return mono;
    }
}

/// <summary>
/// Reads audio files. WAV and AIFF are decoded here in pure C#; other formats
/// (MP3, FLAC, OGG, M4A) use decoders the Windows app registers at start-up.
/// </summary>
public static class AudioFile
{
    /// <summary>Longest stretch decoded for analysis. Long files are analysed from their first minute.</summary>
    public const double MaxSeconds = 60;

    static readonly Dictionary<string, Func<string, double, AudioData>> Extra = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds a decoder for an extension such as ".mp3". The function gets the path and the maximum seconds to decode.</summary>
    public static void Register(string extension, Func<string, double, AudioData> decoder)
    {
        lock (Extra) Extra[extension] = decoder;
    }

    public static bool CanRead(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) || ext.Equals(".aif", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".aiff", StringComparison.OrdinalIgnoreCase) || ext.Equals(".aifc", StringComparison.OrdinalIgnoreCase))
            return true;
        lock (Extra) return Extra.ContainsKey(ext);
    }

    public static AudioData Read(string path, double maxSeconds = MaxSeconds)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".wav" or ".aif" or ".aiff" or ".aifc")
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[4];
            fs.ReadExactly(head);
            fs.Position = 0;
            var magic = Encoding.ASCII.GetString(head);
            if (magic is "RIFF" or "RF64") return ReadWav(fs, maxSeconds);
            if (magic == "FORM") return ReadAiff(fs, maxSeconds);
            throw new InvalidDataException("Not a WAV or AIFF file");
        }
        Func<string, double, AudioData>? decoder;
        lock (Extra) Extra.TryGetValue(ext, out decoder);
        return decoder?.Invoke(path, maxSeconds) ?? throw new NotSupportedException($"No decoder for {ext} files");
    }

    // ---- WAV --------------------------------------------------------------------

    static AudioData ReadWav(Stream s, double maxSeconds)
    {
        var r = new BinaryReader(s);
        r.ReadBytes(12);  // RIFF, size, WAVE
        int format = 0, channels = 0, rate = 0, bits = 0;
        long dataStart = -1, dataLength = 0;
        double? tempo = null;
        int? root = null;
        bool? isLoop = null;

        while (s.Position + 8 <= s.Length)
        {
            var id = Encoding.ASCII.GetString(r.ReadBytes(4));
            long size = r.ReadUInt32();
            var next = s.Position + size + (size & 1);
            switch (id)
            {
                case "fmt ":
                    format = r.ReadUInt16();
                    channels = r.ReadUInt16();
                    rate = r.ReadInt32();
                    r.ReadInt32();
                    r.ReadUInt16();
                    bits = r.ReadUInt16();
                    if (format == 0xFFFE && size >= 40)  // WAVE_FORMAT_EXTENSIBLE: real format is in the sub-format GUID
                    {
                        r.ReadBytes(8);
                        format = r.ReadUInt16();
                    }
                    break;
                case "data":
                    dataStart = s.Position;
                    dataLength = size == 0xFFFFFFFF || s.Position + size > s.Length ? s.Length - s.Position : size;
                    next = s.Position + dataLength + (dataLength & 1);
                    break;
                case "acid" when size >= 24:
                {
                    var flags = r.ReadUInt32();
                    var rootNote = r.ReadUInt16();
                    r.ReadBytes(14);
                    var t = r.ReadSingle();
                    isLoop = (flags & 1) == 0;  // bit 0 = one-shot
                    if ((flags & 2) != 0 && rootNote is > 0 and < 128) root = rootNote;
                    if (t is > 30 and < 400) tempo = t;
                    break;
                }
                case "smpl" when size >= 36:
                {
                    r.ReadBytes(12);
                    var unity = r.ReadInt32();
                    if (root is null && unity is > 0 and < 128 && unity != 60) root = unity;  // 60 is the usual "not set" default
                    break;
                }
            }
            s.Position = next;
        }
        if (dataStart < 0 || channels == 0 || rate == 0) throw new InvalidDataException("WAV file has no audio data");

        var bytesPerSample = bits / 8;
        if (bytesPerSample == 0) throw new InvalidDataException("Unsupported WAV bit depth");
        var frameBytes = bytesPerSample * channels;
        var totalFrames = dataLength / frameBytes;
        var frames = (int)Math.Min(totalFrames, (long)(maxSeconds * rate));
        s.Position = dataStart;
        var raw = new byte[frames * frameBytes];
        var read = s.ReadAtLeast(raw, raw.Length, throwOnEndOfStream: false);
        frames = read / frameBytes;

        var isFloat = format == 3;
        if (format != 1 && format != 3) throw new NotSupportedException($"WAV format {format} isn't PCM");
        var data = Decode(raw, frames, channels, bytesPerSample, isFloat, bigEndian: false);
        return new AudioData
        {
            SampleRate = rate, Channels = data, FileSeconds = (double)totalFrames / rate,
            EmbeddedTempo = tempo, EmbeddedRootNote = root, EmbeddedIsLoop = isLoop,
        };
    }

    // ---- AIFF -------------------------------------------------------------------

    static AudioData ReadAiff(Stream s, double maxSeconds)
    {
        var buf = new byte[12];
        s.ReadExactly(buf);
        var isAifc = Encoding.ASCII.GetString(buf, 8, 4) == "AIFC";
        int channels = 0, bits = 0;
        long totalFrames = 0, dataStart = -1;
        double rate = 0;
        var isFloat = false;
        var littleEndian = false;
        int? root = null;

        var hdr = new byte[8];
        while (s.Position + 8 <= s.Length)
        {
            s.ReadExactly(hdr);
            var id = Encoding.ASCII.GetString(hdr, 0, 4);
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(4));
            var next = s.Position + size + (size & 1);
            if (id == "COMM")
            {
                var c = new byte[Math.Min(size, 64)];
                s.ReadExactly(c);
                channels = BinaryPrimitives.ReadInt16BigEndian(c);
                totalFrames = BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(2));
                bits = BinaryPrimitives.ReadInt16BigEndian(c.AsSpan(6));
                rate = Extended80(c.AsSpan(8, 10));
                if (isAifc && c.Length >= 22)
                {
                    var comp = Encoding.ASCII.GetString(c, 18, 4);
                    isFloat = comp is "fl32" or "FL32" or "fl64" or "FL64";
                    littleEndian = comp == "sowt";
                    if (comp is not ("NONE" or "none" or "sowt" or "twos" or "fl32" or "FL32" or "fl64" or "FL64"))
                        throw new NotSupportedException($"Compressed AIFF ({comp})");
                }
            }
            else if (id == "SSND")
            {
                var off = new byte[8];
                s.ReadExactly(off);
                dataStart = s.Position + BinaryPrimitives.ReadUInt32BigEndian(off);
            }
            else if (id == "INST" && size >= 1)
            {
                var b = s.ReadByte();
                if (b is > 0 and < 128 && b != 60) root = b;
            }
            s.Position = Math.Min(next, s.Length);
        }
        if (dataStart < 0 || channels == 0 || rate <= 0) throw new InvalidDataException("AIFF file has no audio data");

        var bytesPerSample = (bits + 7) / 8;
        var frameBytes = bytesPerSample * channels;
        var frames = (int)Math.Min(totalFrames, (long)(maxSeconds * rate));
        s.Position = dataStart;
        var raw = new byte[frames * frameBytes];
        frames = s.ReadAtLeast(raw, raw.Length, throwOnEndOfStream: false) / frameBytes;
        var data = Decode(raw, frames, channels, bytesPerSample, isFloat, bigEndian: !littleEndian);
        return new AudioData { SampleRate = (int)Math.Round(rate), Channels = data, FileSeconds = totalFrames / rate, EmbeddedRootNote = root };
    }

    /// <summary>80-bit IEEE extended float, used for AIFF sample rates.</summary>
    static double Extended80(ReadOnlySpan<byte> b)
    {
        var exp = ((b[0] & 0x7F) << 8) | b[1];
        var mant = BinaryPrimitives.ReadUInt64BigEndian(b[2..]);
        if (exp == 0 && mant == 0) return 0;
        return mant * Math.Pow(2, exp - 16383 - 63) * ((b[0] & 0x80) != 0 ? -1 : 1);
    }

    static float[][] Decode(byte[] raw, int frames, int channels, int bps, bool isFloat, bool bigEndian)
    {
        var data = new float[channels][];
        for (var c = 0; c < channels; c++) data[c] = new float[frames];
        var span = raw.AsSpan();
        var o = 0;
        for (var i = 0; i < frames; i++)
            for (var c = 0; c < channels; c++, o += bps)
                data[c][i] = Sample(span.Slice(o, bps), bps, isFloat, bigEndian);
        return data;
    }

    static float Sample(ReadOnlySpan<byte> b, int bps, bool isFloat, bool be)
    {
        if (isFloat)
            return bps == 8
                ? (float)(be ? BinaryPrimitives.ReadDoubleBigEndian(b) : BinaryPrimitives.ReadDoubleLittleEndian(b))
                : be ? BinaryPrimitives.ReadSingleBigEndian(b) : BinaryPrimitives.ReadSingleLittleEndian(b);
        switch (bps)
        {
            case 1: return be ? (sbyte)b[0] / 128f : (b[0] - 128) / 128f;  // WAV 8-bit is unsigned, AIFF signed
            case 2: return (be ? BinaryPrimitives.ReadInt16BigEndian(b) : BinaryPrimitives.ReadInt16LittleEndian(b)) / 32768f;
            case 3:
            {
                var v = be ? (b[0] << 24) | (b[1] << 16) | (b[2] << 8) : (b[2] << 24) | (b[1] << 16) | (b[0] << 8);
                return (v >> 8) / 8388608f;
            }
            case 4: return (be ? BinaryPrimitives.ReadInt32BigEndian(b) : BinaryPrimitives.ReadInt32LittleEndian(b)) / 2147483648f;
            default: throw new NotSupportedException($"{bps * 8}-bit audio");
        }
    }
}

/// <summary>Writes 16- or 24-bit PCM WAV files (used for kit exports).</summary>
public static class WavWriter
{
    public static void Write(string path, float[][] channels, int sampleRate, int bits = 16)
    {
        var frames = channels[0].Length;
        var bps = bits / 8;
        var dataBytes = frames * channels.Length * bps;
        using var fs = File.Create(path);
        var w = new BinaryWriter(fs);
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels.Length);
        w.Write(sampleRate);
        w.Write(sampleRate * channels.Length * bps);
        w.Write((short)(channels.Length * bps));
        w.Write((short)bits);
        w.Write("data"u8);
        w.Write(dataBytes);
        for (var i = 0; i < frames; i++)
            foreach (var ch in channels)
            {
                var v = Math.Clamp(ch[i], -1f, 1f);
                if (bits == 16) w.Write((short)Math.Round(v * 32767));
                else
                {
                    var x = (int)Math.Round(v * 8388607);
                    w.Write((byte)x);
                    w.Write((byte)(x >> 8));
                    w.Write((byte)(x >> 16));
                }
            }
    }
}
