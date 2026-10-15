using System.Security.Cryptography;
using SoundFingerprinting.Audio;
using SoundFingerprinting.Builder;
using SoundFingerprinting.Data;
using SoundFingerprinting.Strides;

namespace MusicOrganiser.Core.Sounds;

/// <summary>
/// A sound's "print" for finding duplicates: what stays the same when a sample is copied, renamed, converted to
/// another format, sample rate or bit depth, turned up or down, or trimmed.
/// <list type="bullet">
/// <item><see cref="Exact"/>: a hash of the decoded audio. Equal means the very same audio, whatever the file is called.</item>
/// <item><see cref="Bands"/>: how loud each of 24 frequency bands is in the first ¾ second after the sound starts,
/// relative to its loudest moment. Lining it up on the start skips silence trimmed off the front, and measuring
/// against the loudest moment ignores level changes. Close prints are only candidates: a copy is confirmed by
/// comparing the two waveforms (<see cref="DuplicateFinder"/>).</item>
/// <item><see cref="Sketch"/>: the first 26 ms of the waveform itself, small and rough, to rule out most lookalikes
/// (a pack of similar hi-hats) without reading their files again.</item>
/// <item><see cref="Keys"/>: buckets for the bands, so 300,000 sounds are compared only with likely matches.</item>
/// <item><see cref="Sfp"/>: SoundFingerprinting hashes (AddictedCS/soundfingerprinting, MIT) for sounds of 2 seconds
/// or more. They find a sound inside another one: a loop cut out of a longer one, or an intro trimmed off.</item>
/// </list>
/// </summary>
public sealed record SoundPrint(byte[] Exact, int Frames, byte[] Bands, ushort[] Keys, byte[] Sketch, double Seconds, byte[]? Sfp)
{
    /// <summary>Bumped when the way prints are made changes, so old ones are made again.</summary>
    public const int Version = 1;

    public const int Rate = 22050, Hop = 512, MaxFrames = 32, BandCount = 24;
    const int KeyFrames = 8, BitsPerTable = 12;
    /// <summary>How many bucket keys a print has (<see cref="Keys"/> holds these, then each one's place in line).</summary>
    public const int Tables = 10;
    /// <summary>How much audio a print reads: SoundFingerprinting looks at the first 20 seconds.</summary>
    public const double ReadSeconds = 20;
    public const double SfpMinSeconds = 2;
    /// <summary>SoundFingerprinting's own sample rate, and the step between stored fingerprints (about 0.37 s).</summary>
    public const int SfpRate = 5512, StoredStride = 2048, QueryStride = 512;

    public static SoundPrint Make(AudioData audio) => Make(audio, out _);

    /// <param name="query">For sounds long enough: dense fingerprints to look this sound up with (not stored).</param>
    public static SoundPrint Make(AudioData audio, out AVHashes? query)
    {
        query = null;
        var exact = ExactHash(audio);
        var mono = audio.Mono();
        var (frames, bands) = BandPrint(mono, audio.SampleRate);
        byte[]? sfp = null;
        var seconds = audio.FileSeconds > 0 ? audio.FileSeconds : audio.Seconds;
        if (audio.Seconds >= SfpMinSeconds && frames > 0)
        {
            var low = Resampler.Resample(mono, audio.SampleRate, SfpRate);
            var stored = Fingerprint(low, StoredStride);
            if (stored.Count > 0)
            {
                sfp = Pack(stored);
                query = new AVHashes(Fingerprint(low, QueryStride), null);
            }
        }
        return new SoundPrint(exact, frames, bands, frames > 0 ? LshKeys(frames, bands) : [], frames > 0 ? MakeSketch(mono, audio.SampleRate) : [], seconds, sfp);
    }

    // ---- the exact hash -----------------------------------------------------------------

    static byte[] ExactHash(AudioData audio)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(BitConverter.GetBytes(audio.SampleRate));
        sha.AppendData(BitConverter.GetBytes(audio.Channels.Length));
        sha.AppendData(BitConverter.GetBytes(audio.Length));
        foreach (var ch in audio.Channels)
            sha.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(ch.AsSpan()));
        return sha.GetHashAndReset()[..16];
    }

    // ---- the band print -----------------------------------------------------------------

    static readonly int[] BandEdges = MakeEdges();

    // 24 bands spaced evenly in pitch from 40 Hz to 10 kHz, as FFT bins (1024 points at 22,050 Hz, ~21.5 Hz each).
    static int[] MakeEdges()
    {
        var edges = new int[BandCount + 1];
        for (var b = 0; b <= BandCount; b++)
        {
            var hz = 40 * Math.Pow(10000.0 / 40, (double)b / BandCount);
            edges[b] = Math.Max(b == 0 ? 1 : edges[b - 1] + 1, (int)Math.Round(hz * 1024 / Rate));
        }
        return edges;
    }

    /// <summary>Where a sound starts: the first sample within 26 dB of its peak.</summary>
    public static int Onset(float[] x, out float peak)
    {
        peak = 0;
        foreach (var v in x) peak = Math.Max(peak, Math.Abs(v));
        var gate = peak * 0.05f;
        for (var i = 0; i < x.Length; i++)
            if (Math.Abs(x[i]) >= gate) return i;
        return x.Length;
    }

    static (int Frames, byte[] Bands) BandPrint(float[] mono, int rate)
    {
        // Only the start is needed: resample a little over 32 frames' worth after the onset.
        var onsetIn = Onset(mono, out var peak);
        if (peak < 1e-4f) return (0, []);   // silence
        var need = (int)Math.Ceiling((MaxFrames * Hop + 1024.0) * rate / Rate) + 64;
        var from = Math.Max(0, onsetIn - 64 * rate / Rate);
        var piece = Resampler.Resample(mono.AsSpan(from, Math.Min(need, mono.Length - from)).ToArray(), rate, Rate);
        var start = Onset(piece, out _);
        var frames = Math.Min(MaxFrames, (piece.Length - start + Hop - 1) / Hop);
        if (frames <= 0) return (0, []);
        var window = Dsp.Hann(1024);
        var re = new double[1024];
        var im = new double[1024];
        var db = new double[frames * BandCount];
        var max = double.MinValue;
        for (var f = 0; f < frames; f++)
        {
            var o = start + f * Hop;
            for (var i = 0; i < 1024; i++)
            {
                re[i] = o + i < piece.Length ? piece[o + i] * window[i] : 0;
                im[i] = 0;
            }
            Dsp.Fft(re, im);
            for (var b = 0; b < BandCount; b++)
            {
                double p = 0;
                for (var k = BandEdges[b]; k < BandEdges[b + 1]; k++) p += re[k] * re[k] + im[k] * im[k];
                var v = db[f * BandCount + b] = 10 * Math.Log10(p + 1e-12);
                max = Math.Max(max, v);
            }
        }
        var bands = new byte[db.Length];
        for (var i = 0; i < db.Length; i++)
            bands[i] = (byte)Math.Round(4 * Math.Clamp(db[i] - max + 60, 0, 60));   // 0 = 60 dB down or quieter, 240 = the loudest
        return (frames, bands);
    }

    /// <summary>How alike two band prints are over the frames both have: a correlation, 1 = identical.</summary>
    public static double Similarity(SoundPrint a, SoundPrint b) => Similarity(a.Frames, a.Bands, b.Frames, b.Bands);

    public static double Similarity(int fa, byte[] a, int fb, byte[] b)
    {
        // When one is shorter, its last frame runs off its end, so it's left out.
        var n = (fa == fb ? fa : Math.Min(fa, fb) - 1) * BandCount;
        if (n < 3 * BandCount) return 0;
        return Correlation(a.AsSpan(0, n), b.AsSpan(0, n));
    }

    static double Correlation(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        double sa = 0, sb = 0;
        for (var i = 0; i < a.Length; i++) { sa += a[i]; sb += b[i]; }
        double ma = sa / a.Length, mb = sb / b.Length, ab = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            double x = a[i] - ma, y = b[i] - mb;
            ab += x * y; aa += x * x; bb += y * y;
        }
        return aa < 1e-9 || bb < 1e-9 ? (aa < 1e-9 && bb < 1e-9 ? 1 : 0) : ab / Math.Sqrt(aa * bb);
    }

    // ---- the waveform sketch ---------------------------------------------------------------

    /// <summary>The rate waveforms are compared at, and the lead-in kept before a sound's start for lining them up (3 ms).</summary>
    public const int WaveRate = 11025, LeadIn = WaveRate * 3 / 1000;
    const int SketchLength = 320;

    /// <summary>
    /// A sound's waveform from 3 ms before its start, at 11,025 Hz: <paramref name="seconds"/> of it (silence where the
    /// file runs out or starts right on it), scaled so its peak is 1.
    /// </summary>
    public static float[] WaveStart(float[] mono, int rate, double seconds, int toRate = WaveRate)
    {
        var start = Onset(mono, out var peak);
        if (peak <= 0) return [];
        var before = (int)Math.Ceiling((double)(toRate * 3 / 1000) * rate / toRate);
        var piece = new float[before + Math.Min(mono.Length - start, (int)(seconds * rate))];
        var from = Math.Max(0, start - before);
        mono.AsSpan(from, start - from + piece.Length - before).CopyTo(piece.AsSpan(before - (start - from)));
        var y = Resampler.Resample(piece, rate, toRate);
        var scale = 1 / peak;
        for (var i = 0; i < y.Length; i++) y[i] *= scale;
        return y;
    }

    static byte[] MakeSketch(float[] mono, int rate)
    {
        var w = WaveStart(mono, rate, (double)(SketchLength - LeadIn) / WaveRate);
        var b = new byte[Math.Min(SketchLength, w.Length)];
        for (var i = 0; i < b.Length; i++) b[i] = (byte)(sbyte)Math.Clamp(Math.Round(w[i] * 127), -127, 127);
        return b;
    }

    public static float[] FromSketch(byte[] b) => b.Select(v => (sbyte)v / 127f).ToArray();

    /// <summary>
    /// How alike two waveforms from <see cref="WaveStart"/> are: the best correlation, lined up within ±3 ms.
    /// A copy, even converted to MP3, scores near 1; two different hi-hats don't, however alike they sound.
    /// </summary>
    public static double WaveSimilarity(float[] x, float[] y)
    {
        const int maxLag = LeadIn;
        var n = Math.Min(x.Length, y.Length) - maxLag;
        if (n < maxLag + WaveRate / 100) return 0;   // under 10 ms: too little to tell
        var best = 0.0;
        for (var lag = -maxLag; lag <= maxLag; lag++)
        {
            double xy = 0, xx = 0, yy = 0;
            for (var i = maxLag; i < n; i++)
            {
                var j = i + lag;
                xy += x[i] * y[j]; xx += x[i] * x[i]; yy += y[j] * y[j];
            }
            if (xx > 0 && yy > 0) best = Math.Max(best, Math.Abs(xy) / Math.Sqrt(xx * yy));
        }
        return best;
    }

    /// <summary>
    /// How far apart two waveforms from <see cref="WaveStart"/> sound, in dB: after lining them up to the sample and
    /// taking away any level change, how much their loudness differs in 32 bands from 40 Hz to 9 kHz, frame by frame
    /// (the 90th percentile, so a few odd frames don't count but a changed tail or top end does). Unlike a correlation
    /// it hears the highs and the tail as much as the hit, so a copy with another EQ, more drive or a shorter tail is
    /// told apart from the same sound in another file, which comes out near 0 (an MP3 within about a dB).
    /// </summary>
    public static double SpectralDistance(float[] x, float[] y, int rate) => SpectralDistances(x, y, rate).Worst;

    /// <summary>
    /// <see cref="SpectralDistance"/> (<paramref name="Worst"/>, the 90th percentile) and the median difference
    /// (<paramref name="Typical"/>): two sounds that differ all through, such as a kick loop and a hi-hat loop that
    /// share a rhythm, are far apart in both; a copy with only its tail or top end changed is close in the median.
    /// </summary>
    public static (double Typical, double Worst) SpectralDistances(float[] x, float[] y, int rate)
    {
        var none = (double.PositiveInfinity, double.PositiveInfinity);
        // Line them up: WaveStart starts both at their onset, give or take a few milliseconds.
        var maxLag = rate * 3 / 1000;
        var probe = Math.Min(Math.Min(x.Length, y.Length) - maxLag, rate);
        if (probe <= maxLag) return none;
        var (best, lag) = (-1.0, 0);
        for (var l = -maxLag; l <= maxLag; l++)
        {
            double xy = 0, xx = 0, yy = 0;
            for (var i = maxLag; i < probe; i++) { var j = i + l; xy += x[i] * y[j]; xx += x[i] * x[i]; yy += y[j] * y[j]; }
            var c = xx > 0 && yy > 0 ? Math.Abs(xy) / Math.Sqrt(xx * yy) : 0;
            if (c > best) (best, lag) = (c, l);
        }
        if (lag > 0) y = y[lag..]; else if (lag < 0) x = x[-lag..];

        const int bands = 32;
        var nFft = rate > 32000 ? 2048 : 1024;   // about 46 ms frames
        var hop = nFft / 4;
        var sx = Dsp.PowerSpectrogram(x, nFft, hop, center: false);
        var sy = Dsp.PowerSpectrogram(y, nFft, hop, center: false);
        var frames = Math.Min(sx.Length, sy.Length);
        if (frames == 0) return none;
        var top = Math.Min(9000, rate * 0.45);
        var edges = new int[bands + 1];
        for (var b = 0; b <= bands; b++)
            edges[b] = Math.Clamp((int)Math.Round(40 * Math.Pow(top / 40, (double)b / bands) * nFft / rate), 1, nFft / 2);
        double[,] Db(float[][] s)
        {
            var d = new double[frames, bands];
            for (var f = 0; f < frames; f++)
                for (var b = 0; b < bands; b++)
                {
                    double e = 0;
                    for (var k = edges[b]; k < Math.Max(edges[b] + 1, edges[b + 1]); k++) e += s[f][k];
                    d[f, b] = 10 * Math.Log10(e + 1e-12);
                }
            return d;
        }
        var (dx, dy) = (Db(sx), Db(sy));
        var loudest = double.NegativeInfinity;
        var bandLoudest = new double[bands];
        Array.Fill(bandLoudest, double.NegativeInfinity);
        for (var f = 0; f < frames; f++)
            for (var b = 0; b < bands; b++)
            {
                var m = Math.Max(dx[f, b], dy[f, b]);
                loudest = Math.Max(loudest, m);
                bandLoudest[b] = Math.Max(bandLoudest[b], m);
            }
        // The cells that matter: within 50 dB of the loudest overall, and 30 dB of the loudest in their band.
        var diffs = new List<double>();
        for (var f = 0; f < frames; f++)
            for (var b = 0; b < bands; b++)
            {
                var m = Math.Max(dx[f, b], dy[f, b]);
                if (m >= loudest - 50 && m >= bandLoudest[b] - 30) diffs.Add(dx[f, b] - dy[f, b]);
            }
        if (diffs.Count == 0) return none;
        // A level change isn't a different sound: take away the typical difference first.
        var sorted = diffs.Order().ToList();
        var level = sorted[sorted.Count / 2];
        var apart = diffs.Select(d => Math.Min(20, Math.Abs(d - level))).Order().ToList();
        return (apart[apart.Count / 2], apart[apart.Count * 9 / 10]);
    }

    // ---- buckets ------------------------------------------------------------------------

    static readonly float[][] Planes = MakePlanes();

    static float[][] MakePlanes()
    {
        var rng = new Random(20261015);   // fixed, so keys made on different days still meet
        var planes = new float[Tables * (BitsPerTable + 1)][];
        for (var p = 0; p < planes.Length; p++)
        {
            planes[p] = new float[KeyFrames * BandCount];
            for (var i = 0; i < planes[p].Length; i++)
            {
                var u1 = 1 - rng.NextDouble();
                var u2 = rng.NextDouble();
                planes[p][i] = (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
            }
        }
        return planes;
    }

    /// <summary>
    /// Ten bucket numbers from the first eight frames (random-hyperplane hashing): two prints that correlate well
    /// almost always share at least one, while unrelated ones rarely do. Then, for each bucket, a place in line
    /// (one more projection): a crowded bucket is compared only between neighbours in that line, and copies sit
    /// right next to each other in it.
    /// </summary>
    static ushort[] LshKeys(int frames, byte[] bands)
    {
        var v = new float[KeyFrames * BandCount];
        var n = Math.Min(frames, KeyFrames) * BandCount;
        for (var i = 0; i < n; i++) v[i] = bands[i];   // missing frames stay at the floor, 0
        var mean = v.Average();
        for (var i = 0; i < v.Length; i++) v[i] -= mean;
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        float Dot(float[] plane)
        {
            float dot = 0;
            for (var i = 0; i < v.Length; i++) dot += plane[i] * v[i];
            return dot;
        }
        var keys = new ushort[Tables * 2];
        for (var t = 0; t < Tables; t++)
        {
            var k = 0;
            for (var bit = 0; bit < BitsPerTable; bit++)
                if (Dot(Planes[t * BitsPerTable + bit]) > 0) k |= 1 << bit;
            keys[t] = (ushort)k;
            var line = norm > 0 ? Dot(Planes[Tables * BitsPerTable + t]) / norm : 0;
            keys[Tables + t] = (ushort)Math.Clamp(32768 + line * 10000, 0, 65535);
        }
        return keys;
    }

    // ---- SoundFingerprinting ---------------------------------------------------------------

    static Hashes Fingerprint(float[] samples5512, int stride) =>
        FingerprintCommandBuilder.Instance.BuildFingerprintCommand()
            .From(new AudioSamples(samples5512, "", SfpRate))
            .WithFingerprintConfig(c => { c.Audio.Stride = new IncrementalStaticStride(stride); return c; })
            .Hash().GetAwaiter().GetResult().Audio ?? new Hashes([], 0, MediaType.Audio);

    /// <summary>Stored fingerprints, compactly: duration, then per fingerprint its number, start and 25 hash bins.</summary>
    static byte[] Pack(Hashes h)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(h.DurationInSeconds);
        w.Write(h.Count);
        foreach (var f in h)
        {
            w.Write(f.SequenceNumber);
            w.Write(f.StartsAt);
            w.Write((byte)f.HashBins.Length);
            foreach (var bin in f.HashBins) w.Write(bin);
        }
        w.Flush();
        return ms.ToArray();
    }

    public static Hashes Unpack(byte[] blob)
    {
        using var r = new BinaryReader(new MemoryStream(blob));
        var duration = r.ReadDouble();
        var count = r.ReadInt32();
        var list = new List<HashedFingerprint>(count);
        for (var i = 0; i < count; i++)
        {
            var seq = r.ReadUInt32();
            var at = r.ReadSingle();
            var bins = new int[r.ReadByte()];
            for (var j = 0; j < bins.Length; j++) bins[j] = r.ReadInt32();
            list.Add(new HashedFingerprint(bins, seq, at, []));
        }
        return new Hashes(list, duration, MediaType.Audio);
    }

    public static byte[] PackKeys(ushort[] keys)
    {
        var b = new byte[keys.Length * 2];
        Buffer.BlockCopy(keys, 0, b, 0, b.Length);
        return b;
    }

    public static ushort[] UnpackKeys(byte[]? b)
    {
        if (b is null) return [];
        var k = new ushort[b.Length / 2];
        Buffer.BlockCopy(b, 0, k, 0, k.Length * 2);
        return k;
    }
}

/// <summary>
/// Quick resampling for prints: a windowed-sinc low-pass evaluated at the nearest input sample. A fraction of a
/// sample's timing error doesn't matter for prints, and it's many times faster than <see cref="Dsp.Resample"/>.
/// </summary>
public static class Resampler
{
    public static float[] Resample(float[] x, int from, int to)
    {
        if (from == to || x.Length == 0) return x;
        var ratio = (double)to / from;
        var cutoff = Math.Min(1.0, ratio) * 0.9;
        var half = (int)Math.Ceiling(8 / cutoff);
        var kernel = new float[2 * half + 1];
        double sum = 0;
        for (var k = -half; k <= half; k++)
        {
            var d = k * cutoff;
            var sinc = k == 0 ? 1.0 : Math.Sin(Math.PI * d) / (Math.PI * d);
            var win = 0.42 + 0.5 * Math.Cos(Math.PI * k / (half + 1)) + 0.08 * Math.Cos(2 * Math.PI * k / (half + 1));
            sum += kernel[k + half] = (float)(sinc * win);
        }
        for (var k = 0; k < kernel.Length; k++) kernel[k] /= (float)sum;
        var y = new float[(int)(x.Length * ratio)];
        for (var i = 0; i < y.Length; i++)
        {
            var c = (int)Math.Round(i / ratio);
            float acc = 0;
            var lo = Math.Max(-half, -c);
            var hi = Math.Min(half, x.Length - 1 - c);
            for (var k = lo; k <= hi; k++) acc += kernel[k + half] * x[c + k];
            y[i] = acc;
        }
        return y;
    }
}
