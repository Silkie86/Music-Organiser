namespace MusicOrganiser.Core.Sounds;

/// <summary>Signal-processing building blocks: FFT, windows, resampling, spectrograms, mel filters.</summary>
public static class Dsp
{
    /// <summary>In-place radix-2 complex FFT. Length must be a power of two.</summary>
    public static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var a = i + k;
                    var b = a + len / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    var nr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = nr;
                }
            }
        }
    }

    /// <summary>Periodic Hann window (as numpy/librosa/transformers use for spectrograms).</summary>
    public static double[] Hann(int n)
    {
        var w = new double[n];
        for (var i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
        return w;
    }

    /// <summary>
    /// Power spectrogram, frames × (nFft/2+1). Frames are centred with reflect padding,
    /// matching librosa and Hugging Face feature extractors.
    /// </summary>
    public static float[][] PowerSpectrogram(float[] x, int nFft, int hop, bool center = true)
    {
        var window = Hann(nFft);
        var pad = center ? nFft / 2 : 0;
        var padded = center ? ReflectPad(x, pad) : x;
        var frames = padded.Length < nFft ? 0 : 1 + (padded.Length - nFft) / hop;
        var bins = nFft / 2 + 1;
        var spec = new float[frames][];
        var re = new double[nFft];
        var im = new double[nFft];
        for (var f = 0; f < frames; f++)
        {
            var o = f * hop;
            for (var i = 0; i < nFft; i++)
            {
                re[i] = padded[o + i] * window[i];
                im[i] = 0;
            }
            Fft(re, im);
            var row = spec[f] = new float[bins];
            for (var k = 0; k < bins; k++) row[k] = (float)(re[k] * re[k] + im[k] * im[k]);
        }
        return spec;
    }

    static float[] ReflectPad(float[] x, int pad)
    {
        var y = new float[x.Length + 2 * pad];
        Array.Copy(x, 0, y, pad, x.Length);
        if (x.Length < 2) return y;
        for (var i = 0; i < pad; i++)
        {
            y[pad - 1 - i] = x[Reflect(i + 1, x.Length)];
            y[pad + x.Length + i] = x[Reflect(x.Length - 2 - i, x.Length)];
        }
        return y;
    }

    static int Reflect(int i, int n)
    {
        var period = 2 * (n - 1);
        i = ((i % period) + period) % period;
        return i < n ? i : period - i;
    }

    // ---- mel filters --------------------------------------------------------------

    public enum MelScale { Htk, Slaney }

    public static double HzToMel(double hz, MelScale scale)
    {
        if (scale == MelScale.Htk) return 2595.0 * Math.Log10(1.0 + hz / 700.0);
        const double minLogHz = 1000, minLogMel = 15, logStep = 27.0;
        var stepLog = Math.Log(6.4) / logStep;
        return hz >= minLogHz ? minLogMel + Math.Log(hz / minLogHz) / stepLog : 3.0 * hz / 200.0;
    }

    public static double MelToHz(double mel, MelScale scale)
    {
        if (scale == MelScale.Htk) return 700.0 * (Math.Pow(10, mel / 2595.0) - 1.0);
        const double minLogHz = 1000, minLogMel = 15, logStep = 27.0;
        var stepLog = Math.Log(6.4) / logStep;
        return mel >= minLogMel ? minLogHz * Math.Exp(stepLog * (mel - minLogMel)) : 200.0 * mel / 3.0;
    }

    /// <summary>Triangular mel filter bank, bins × mels (Hugging Face <c>mel_filter_bank</c> layout).</summary>
    public static double[,] MelFilters(int bins, int mels, double fMin, double fMax, int sampleRate, MelScale scale, bool slaneyNorm)
    {
        var melMin = HzToMel(fMin, scale);
        var melMax = HzToMel(fMax, scale);
        var filterFreqs = new double[mels + 2];
        for (var i = 0; i < mels + 2; i++) filterFreqs[i] = MelToHz(melMin + (melMax - melMin) * i / (mels + 1), scale);
        var fb = new double[bins, mels];
        for (var k = 0; k < bins; k++)
        {
            var f = (double)(sampleRate / 2) * k / (bins - 1);
            for (var m = 0; m < mels; m++)
            {
                var down = (f - filterFreqs[m]) / (filterFreqs[m + 1] - filterFreqs[m]);
                var up = (filterFreqs[m + 2] - f) / (filterFreqs[m + 2] - filterFreqs[m + 1]);
                var v = Math.Max(0, Math.Min(down, up));
                if (slaneyNorm) v *= 2.0 / (filterFreqs[m + 2] - filterFreqs[m]);
                fb[k, m] = v;
            }
        }
        return fb;
    }

    /// <summary>Applies a filter bank to a power spectrogram: frames × mels.</summary>
    public static float[][] ApplyFilters(float[][] spec, double[,] fb)
    {
        var bins = fb.GetLength(0);
        var mels = fb.GetLength(1);
        var outp = new float[spec.Length][];
        for (var f = 0; f < spec.Length; f++)
        {
            var row = outp[f] = new float[mels];
            var s = spec[f];
            for (var m = 0; m < mels; m++)
            {
                double acc = 0;
                for (var k = 0; k < bins; k++)
                {
                    var w = fb[k, m];
                    if (w != 0) acc += w * s[k];
                }
                row[m] = (float)acc;
            }
        }
        return outp;
    }

    // ---- resampling -----------------------------------------------------------------

    /// <summary>Band-limited (windowed-sinc) resampling. Good enough for analysis and kit exports.</summary>
    public static float[] Resample(float[] x, int from, int to)
    {
        if (from == to || x.Length == 0) return x;
        var ratio = (double)to / from;
        var outLen = (int)Math.Ceiling(x.Length * ratio);
        var y = new float[outLen];
        var cutoff = Math.Min(1.0, ratio);       // fraction of the input Nyquist to keep
        const int halfTaps = 16;
        var support = halfTaps / cutoff;
        for (var i = 0; i < outLen; i++)
        {
            var t = i / ratio;
            var left = (int)Math.Floor(t - support) + 1;
            var right = (int)Math.Floor(t + support);
            double acc = 0, wsum = 0;
            for (var j = Math.Max(0, left); j <= Math.Min(x.Length - 1, right); j++)
            {
                var d = (t - j) * cutoff;
                var sinc = Math.Abs(d) < 1e-9 ? 1.0 : Math.Sin(Math.PI * d) / (Math.PI * d);
                var u = (t - j) / support;                 // -1..1
                var win = 0.42 + 0.5 * Math.Cos(Math.PI * u) + 0.08 * Math.Cos(2 * Math.PI * u);  // Blackman
                var w = sinc * win;
                acc += x[j] * w;
                wsum += w;
            }
            y[i] = (float)(wsum > 1e-9 ? acc / wsum : 0);
        }
        return y;
    }

    public static int NextPow2(int n)
    {
        var p = 1;
        while (p < n) p <<= 1;
        return p;
    }
}
