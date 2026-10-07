namespace MusicOrganiser.Core.Sounds;

/// <summary>Everything the analyser measures about one sample.</summary>
public sealed class SoundAnalysis
{
    public double Seconds { get; set; }
    public int SampleRate { get; set; }
    public int ChannelCount { get; set; }
    public double PeakDb { get; set; }
    public double RmsDb { get; set; }
    /// <summary>Milliseconds from start to peak level.</summary>
    public double AttackMs { get; set; }
    /// <summary>Milliseconds from peak until the level falls 30 dB.</summary>
    public double DecayMs { get; set; }
    /// <summary>Spectral centre of mass in Hz: low = dark, high = bright.</summary>
    public double Brightness { get; set; }
    /// <summary>0 = pure tone, 1 = white noise.</summary>
    public double Noisiness { get; set; }
    /// <summary>0 = mono, 1 = very wide stereo.</summary>
    public double Width { get; set; }
    public bool IsLoop { get; set; }
    public double? Bpm { get; set; }
    public string BpmSource { get; set; } = "";
    /// <summary>Main pitch for one-shots, as a MIDI note number with cents.</summary>
    public double? Pitch { get; set; }
    public double PitchConfidence { get; set; }
    public int? KeyTonic { get; set; }
    public bool KeyMinor { get; set; }
    public double KeyConfidence { get; set; }
    public string KeySource { get; set; } = "";
    /// <summary>Chord name when the whole sample is one chord (chord stabs).</summary>
    public string? Chord { get; set; }
    public List<ChordSpan> Chords { get; set; } = [];
    public double[] Chroma { get; set; } = new double[12];
    /// <summary>Character words: punchy, soft, bright, dark, wide, tonal, noisy…</summary>
    public List<string> Character { get; set; } = [];
    /// <summary>Peak levels in 0..1 for drawing a small waveform.</summary>
    public float[] Waveform { get; set; } = [];
    /// <summary>Hand-crafted feature vector (timbre, envelope, spectrum) used when no neural model is installed.</summary>
    public float[] Features { get; set; } = [];

    public string? KeyName => KeyTonic is { } t ? MusicTheory.NoteNames[t] + (KeyMinor ? "m" : "") : null;
    public string? RootNoteName => Pitch is { } p ? MusicTheory.NoteName((int)Math.Round(p)) : null;
}

/// <summary>
/// Classic signal analysis of a sample: loudness and envelope, timbre (MFCCs and
/// spectral shape), pitch (YIN), key and chords (chroma), tempo (onset autocorrelation
/// plus the bar-length trick for loops).
/// </summary>
public static class SoundAnalyzer
{
    const int NFft = 2048;
    const int Hop = 512;
    const int Mels = 40;
    const int Mfccs = 20;

    /// <summary>Names of the entries in <see cref="SoundAnalysis.Features"/>, in order.</summary>
    public static readonly IReadOnlyList<string> FeatureNames = BuildFeatureNames();

    static List<string> BuildFeatureNames()
    {
        var n = new List<string>();
        for (var i = 0; i < Mfccs; i++) n.Add($"mfcc{i}");
        for (var i = 0; i < Mfccs; i++) n.Add($"mfcc{i}_sd");
        n.AddRange(["centroid", "centroid_sd", "bandwidth", "rolloff", "flatness", "flux", "zcr",
                    "log_seconds", "log_attack", "log_decay", "crest", "rms_db",
                    "band_sub", "band_low", "band_lowmid", "band_high", "band_air",
                    "pitch_conf", "tonal", "width", "onset_rate"]);
        for (var i = 0; i < 12; i++) n.Add($"chroma{i}");
        return n;
    }

    public static SoundAnalysis Analyse(string path) => Analyse(AudioFile.Read(path), path);

    public static SoundAnalysis Analyse(AudioData audio, string path = "")
    {
        var sr = audio.SampleRate;
        var x = audio.Mono();
        var a = new SoundAnalysis
        {
            Seconds = Math.Round(audio.FileSeconds > 0 ? audio.FileSeconds : audio.Seconds, 3),
            SampleRate = sr,
            ChannelCount = audio.Channels.Length,
        };
        a.Width = StereoWidth(audio);

        // ---- level & envelope --------------------------------------------------------
        double peak = 0, sumSq = 0;
        foreach (var v in x) { var av = Math.Abs(v); if (av > peak) peak = av; sumSq += v * v; }
        var rms = x.Length == 0 ? 0 : Math.Sqrt(sumSq / x.Length);
        a.PeakDb = Math.Round(Db(peak), 1);
        a.RmsDb = Math.Round(Db(rms), 1);
        a.Waveform = WaveformPeaks(x, 160, peak);

        var env = Envelope(x, sr);   // 5 ms RMS frames
        var envPeakIdx = env.Length == 0 ? 0 : Array.IndexOf(env, env.Max());
        var envPeak = env.Length == 0 ? 0 : env[envPeakIdx];
        var startIdx = 0;
        while (startIdx < envPeakIdx && env[startIdx] < envPeak * 0.05) startIdx++;   // ignore leading silence
        a.AttackMs = Math.Round((envPeakIdx - startIdx) * 5.0, 1);
        var decayIdx = envPeakIdx;
        while (decayIdx < env.Length && env[decayIdx] > envPeak * 0.0316) decayIdx++;  // -30 dB
        a.DecayMs = Math.Round((decayIdx - envPeakIdx) * 5.0, 1);

        // ---- spectrum ----------------------------------------------------------------
        // Timbre is measured on the first 10 seconds; it's plenty and keeps scans quick.
        var timbre = x.Length > sr * 10 ? x[..(sr * 10)] : x;
        var spec = Dsp.PowerSpectrogram(timbre, NFft, Hop);
        var bins = NFft / 2 + 1;
        var binHz = (double)sr / NFft;
        var fb = MelBank(sr);
        var mel = Dsp.ApplyFilters(spec, fb);

        // Only frames with sound in them describe the timbre.
        var frameEnergy = spec.Select(f => f.Sum()).ToArray();
        var maxE = frameEnergy.Length == 0 ? 0 : frameEnergy.Max();
        var active = Enumerable.Range(0, spec.Length).Where(i => frameEnergy[i] > maxE * 1e-4).ToList();
        if (active.Count == 0) active = Enumerable.Range(0, spec.Length).ToList();

        var mfcc = active.Select(i => Mfcc(mel[i])).ToList();
        var centroids = new List<double>();
        double bandwidthSum = 0, rolloffSum = 0, flatSum = 0, fluxSum = 0;
        var bands = new double[5];
        float[]? prevMag = null;
        foreach (var i in active)
        {
            var f = spec[i];
            double total = 0, weighted = 0;
            for (var k = 1; k < bins; k++) { total += f[k]; weighted += f[k] * k * binHz; }
            var c = total > 0 ? weighted / total : 0;
            centroids.Add(c);
            double bw = 0;
            for (var k = 1; k < bins; k++) bw += f[k] * (k * binHz - c) * (k * binHz - c);
            bandwidthSum += total > 0 ? Math.Sqrt(bw / total) : 0;
            double cum = 0;
            var roll = 0.0;
            for (var k = 1; k < bins; k++) { cum += f[k]; if (cum >= 0.85 * total) { roll = k * binHz; break; } }
            rolloffSum += roll;
            double logSum = 0, linSum = 0;
            for (var k = 1; k < bins; k++) { logSum += Math.Log(f[k] + 1e-12); linSum += f[k]; }
            flatSum += linSum > 0 ? Math.Exp(logSum / (bins - 1)) / (linSum / (bins - 1)) : 0;
            var mag = f.Select(p => (float)Math.Sqrt(p)).ToArray();
            if (prevMag is not null)
            {
                double fl = 0;
                for (var k = 0; k < bins; k++) fl += Math.Max(0, mag[k] - prevMag[k]);
                fluxSum += fl / Math.Max(1e-9, mag.Sum());
            }
            prevMag = mag;
            for (var k = 1; k < bins; k++)
            {
                var hz = k * binHz;
                var b = hz < 60 ? 0 : hz < 250 ? 1 : hz < 2000 ? 2 : hz < 6000 ? 3 : 4;
                bands[b] += f[k];
            }
        }
        var n = active.Count;
        var centroid = centroids.Average();
        var centroidSd = Math.Sqrt(centroids.Average(c => (c - centroid) * (c - centroid)));
        var flatness = flatSum / n;
        var bandTotal = Math.Max(1e-12, bands.Sum());
        a.Brightness = Math.Round(centroid);
        a.Noisiness = Math.Round(Math.Clamp(flatness * 3, 0, 1), 3);

        // ---- pitch, chroma, key, chords ------------------------------------------------
        var (pitch, pitchConf) = Yin.Estimate(x, sr, startIdx * sr / 200 + sr / 50);
        a.PitchConfidence = Math.Round(pitchConf, 3);
        if (pitch is { } f0 && pitchConf >= 0.6) a.Pitch = Math.Round(MusicTheory.HzToMidi(f0), 2);
        if (audio.EmbeddedRootNote is { } root) { a.Pitch = root; a.PitchConfidence = 1; }

        var chromaFrames = ChromaFrames(x.Length > sr * 30 ? x[..(sr * 30)] : x, sr, out var chromaRate);
        var chroma = new double[12];
        foreach (var c in chromaFrames) for (var i = 0; i < 12; i++) chroma[i] += c[i];
        var chromaMax = chroma.Max();
        if (chromaMax > 0) for (var i = 0; i < 12; i++) chroma[i] /= chromaMax;
        a.Chroma = chroma.Select(c => Math.Round(c, 3)).ToArray();
        var tonal = 1 - Math.Clamp(flatness * 3, 0, 1);

        // ---- loop / tempo --------------------------------------------------------------
        var onset = OnsetEnvelope(mel);
        var frameRate = (double)sr / Hop;
        var onsetCount = CountPeaks(onset);
        var onsetRate = timbre.Length == 0 ? 0 : onsetCount / ((double)timbre.Length / sr);
        a.IsLoop = audio.EmbeddedIsLoop ?? SoundTypes.LoopFromPath(path) ??
                   (a.Seconds >= 1.5 && onsetCount >= 3 && BarFit(a.Seconds) is not null);
        if (a.IsLoop)
        {
            if (audio.EmbeddedTempo is { } et) (a.Bpm, a.BpmSource) = (Math.Round(et, 2), "file tag");
            else if (SoundTypes.TempoFromName(path) is { } nt) (a.Bpm, a.BpmSource) = (nt, "file name");
            else if (EstimateTempo(onset, frameRate, a.Seconds) is { } bt) (a.Bpm, a.BpmSource) = (bt, "detected");
        }

        // Key: written in the name beats detection; detection only for tonal material.
        if (SoundTypes.KeyFromName(path) is { } nk && tonal > 0.3)
            (a.KeyTonic, a.KeyMinor, a.KeyConfidence, a.KeySource) = (nk.Tonic, nk.Minor, 1, "file name");
        else if (tonal > 0.35 && MusicTheory.EstimateKey(chroma) is { } key && (a.IsLoop || a.Seconds > 1.5))
            (a.KeyTonic, a.KeyMinor, a.KeyConfidence, a.KeySource) = (key.Tonic, key.Minor, key.Confidence, "detected");
        else if (a.Pitch is { } p && !a.IsLoop)
            (a.KeyTonic, a.KeyMinor, a.KeyConfidence, a.KeySource) = ((int)Math.Round(p) % 12, false, a.PitchConfidence, "root note");

        if (tonal > 0.35)
        {
            if (!a.IsLoop && MusicTheory.NameChord(chroma) is { } ch && chroma.Count(c => c > 0.4) >= 3) a.Chord = ch.Name;
            if (a.IsLoop || a.Seconds > 3) a.Chords = [.. ChordsOverTime(chromaFrames, chromaRate)];
        }

        // ---- character words -----------------------------------------------------------
        if (a.AttackMs <= 10 && peak / Math.Max(rms, 1e-9) > 4) a.Character.Add("punchy");
        else if (a.AttackMs >= 80) a.Character.Add("soft attack");
        a.Character.Add(centroid < 700 ? "dark" : centroid > 3500 ? "bright" : "warm");
        if (bands[0] / bandTotal > 0.25 || bands[1] / bandTotal > 0.5) a.Character.Add("deep");
        if (bands[4] / bandTotal > 0.2) a.Character.Add("airy");
        if (tonal > 0.7 && pitchConf > 0.7) a.Character.Add("tonal");
        else if (flatness > 0.25) a.Character.Add("noisy");
        if (a.Width > 0.35) a.Character.Add("wide");
        else if (a.ChannelCount == 1 || a.Width < 0.02) a.Character.Add("mono");
        if (!a.IsLoop) a.Character.Add(a.DecayMs < 150 ? "short" : a.DecayMs > 1200 ? "long" : "medium");

        // ---- feature vector -------------------------------------------------------------
        var feats = new List<double>();
        for (var i = 0; i < Mfccs; i++) feats.Add(mfcc.Average(m => m[i]));
        for (var i = 0; i < Mfccs; i++)
        {
            var mean = feats[i];
            feats.Add(Math.Sqrt(mfcc.Average(m => (m[i] - mean) * (m[i] - mean))));
        }
        feats.AddRange([
            centroid / 1000, centroidSd / 1000, bandwidthSum / n / 1000, rolloffSum / n / 1000, flatness, fluxSum / Math.Max(1, n - 1),
            Zcr(timbre),
            Math.Log(a.Seconds + 0.01), Math.Log(a.AttackMs + 1), Math.Log(a.DecayMs + 1), Db(peak) - Db(rms), Db(rms) / 10,
            bands[0] / bandTotal, bands[1] / bandTotal, bands[2] / bandTotal, bands[3] / bandTotal, bands[4] / bandTotal,
            pitchConf, tonal, a.Width, Math.Log(onsetRate + 1),
        ]);
        feats.AddRange(chroma);
        a.Features = feats.Select(f => double.IsFinite(f) ? (float)f : 0f).ToArray();
        return a;
    }

    static double Db(double v) => 20 * Math.Log10(Math.Max(v, 1e-6));

    static readonly Dictionary<int, double[,]> MelBanks = new();

    static double[,] MelBank(int sr)
    {
        lock (MelBanks)
        {
            if (!MelBanks.TryGetValue(sr, out var fb))
                MelBanks[sr] = fb = Dsp.MelFilters(NFft / 2 + 1, Mels, 20, Math.Min(sr / 2.0, 16000), sr, Dsp.MelScale.Slaney, true);
            return fb;
        }
    }

    /// <summary>MFCCs: DCT-II (orthonormal) of the log-mel frame.</summary>
    static double[] Mfcc(float[] melFrame)
    {
        var logMel = melFrame.Select(v => 10 * Math.Log10(Math.Max(v, 1e-10))).ToArray();
        var c = new double[Mfccs];
        for (var k = 0; k < Mfccs; k++)
        {
            double s = 0;
            for (var m = 0; m < Mels; m++) s += logMel[m] * Math.Cos(Math.PI * k * (m + 0.5) / Mels);
            c[k] = s * Math.Sqrt((k == 0 ? 1.0 : 2.0) / Mels) / 10;  // scaled down to keep features comparable
        }
        return c;
    }

    static double Zcr(float[] x)
    {
        if (x.Length < 2) return 0;
        var z = 0;
        for (var i = 1; i < x.Length; i++) if ((x[i] >= 0) != (x[i - 1] >= 0)) z++;
        return (double)z / x.Length * 10;
    }

    static double StereoWidth(AudioData audio)
    {
        if (audio.Channels.Length < 2) return 0;
        var l = audio.Channels[0];
        var r = audio.Channels[1];
        double mid = 0, side = 0;
        for (var i = 0; i < l.Length; i++)
        {
            var m = (l[i] + r[i]) * 0.5;
            var s = (l[i] - r[i]) * 0.5;
            mid += m * m;
            side += s * s;
        }
        return mid + side <= 0 ? 0 : Math.Round(Math.Sqrt(side / (mid + side)) * Math.Sqrt(2), 3);
    }

    static float[] WaveformPeaks(float[] x, int points, double peak)
    {
        var w = new float[points];
        if (x.Length == 0 || peak <= 0) return w;
        for (var p = 0; p < points; p++)
        {
            var from = (int)((long)x.Length * p / points);
            var to = Math.Max(from + 1, (int)((long)x.Length * (p + 1) / points));
            float m = 0;
            for (var i = from; i < to && i < x.Length; i++) m = Math.Max(m, Math.Abs(x[i]));
            w[p] = (float)Math.Round(m / peak, 3);
        }
        return w;
    }

    static double[] Envelope(float[] x, int sr)
    {
        var size = Math.Max(1, sr / 200);
        var env = new double[(x.Length + size - 1) / size];
        for (var f = 0; f < env.Length; f++)
        {
            double s = 0;
            var end = Math.Min(x.Length, (f + 1) * size);
            for (var i = f * size; i < end; i++) s += x[i] * x[i];
            env[f] = Math.Sqrt(s / size);
        }
        return env;
    }

    const int ChromaFft = 8192;

    /// <summary>
    /// Per-frame 12-bin chroma. Uses a long FFT so low notes are resolved, then picks
    /// spectral peaks (with parabolic interpolation for their exact frequency) between
    /// 50 Hz and 5 kHz and adds each to its pitch class. Higher peaks count a little less,
    /// which damps overtones. Silent frames are left empty.
    /// </summary>
    static List<double[]> ChromaFrames(float[] x, int sr, out double frameRate)
    {
        var hop = ChromaFft / 2;
        frameRate = (double)sr / hop;
        var spec = Dsp.PowerSpectrogram(x, ChromaFft, hop);
        var binHz = (double)sr / ChromaFft;
        var lo = (int)(50 / binHz);
        var hi = Math.Min(ChromaFft / 2 - 1, (int)(5000 / binHz));
        var energies = spec.Select(f => f.Sum()).ToArray();
        var maxE = energies.Length == 0 ? 0 : energies.Max();
        var frames = new List<double[]>(spec.Length);
        for (var fi = 0; fi < spec.Length; fi++)
        {
            var c = new double[12];
            frames.Add(c);
            if (energies[fi] < maxE * 1e-3) continue;
            var f = spec[fi];
            var mag = new double[f.Length];
            var peak = 0.0;
            for (var k = lo - 1; k <= hi + 1; k++) { mag[k] = Math.Sqrt(f[k]); peak = Math.Max(peak, mag[k]); }
            var floor = peak * 0.01;   // -40 dB
            for (var k = lo; k <= hi; k++)
            {
                if (mag[k] < floor || mag[k] <= mag[k - 1] || mag[k] < mag[k + 1]) continue;
                // Parabolic interpolation on log magnitude for the true peak frequency.
                double a = Math.Log(mag[k - 1] + 1e-12), b = Math.Log(mag[k] + 1e-12), g = Math.Log(mag[k + 1] + 1e-12);
                var den = a - 2 * b + g;
                var delta = Math.Abs(den) > 1e-12 ? 0.5 * (a - g) / den : 0;
                var hz = (k + delta) * binHz;
                var midi = MusicTheory.HzToMidi(hz);
                var pc = ((int)Math.Round(midi) % 12 + 12) % 12;
                var cents = Math.Abs(midi - Math.Round(midi));
                var weight = Math.Cos(Math.PI * cents) * Math.Cos(Math.PI * cents);   // 1 in tune, 0 a quarter-tone off
                c[pc] += weight * Math.Sqrt(mag[k] / peak) / (1 + hz / 2000);
            }
        }
        return frames;
    }

    static IEnumerable<ChordSpan> ChordsOverTime(List<double[]> chromaFrames, double frameRate)
    {
        // Pool frames into ~0.25 s blocks, normalise each.
        var per = Math.Max(1, (int)Math.Round(frameRate / 4));
        var blocks = new List<double[]>();
        for (var s = 0; s < chromaFrames.Count; s += per)
        {
            var b = new double[12];
            for (var f = s; f < Math.Min(chromaFrames.Count, s + per); f++)
                for (var i = 0; i < 12; i++) b[i] += chromaFrames[f][i];
            var max = b.Max();
            if (max > 0) for (var i = 0; i < 12; i++) b[i] = Math.Max(0, b[i] / max - 0.15);
            blocks.Add(b);
        }
        return MusicTheory.ChordSequence(blocks, per / frameRate);
    }

    // ---- onsets & tempo ---------------------------------------------------------------

    /// <summary>Spectral flux of the log-mel spectrogram, a standard onset strength curve.</summary>
    static double[] OnsetEnvelope(float[][] mel)
    {
        var o = new double[mel.Length];
        for (var f = 1; f < mel.Length; f++)
        {
            double s = 0;
            for (var m = 0; m < Mels; m++)
            {
                var d = Math.Log10(mel[f][m] + 1e-8) - Math.Log10(mel[f - 1][m] + 1e-8);
                if (d > 0) s += d;
            }
            o[f] = s;
        }
        return o;
    }

    static int CountPeaks(double[] onset)
    {
        if (onset.Length < 3) return 0;
        var max = onset.Max();
        var mean = onset.Average();
        var threshold = mean + 0.3 * (max - mean);
        var count = 0;
        var last = -10;
        for (var i = 1; i < onset.Length - 1; i++)
            if (onset[i] > threshold && onset[i] >= onset[i - 1] && onset[i] > onset[i + 1] && i - last > 4) { count++; last = i; }
        return count;
    }

    /// <summary>When a loop is a whole number of bars at a sensible tempo, the tempo that fits (sample packs cut loops exactly).</summary>
    public static double? BarFit(double seconds, double? hint = null)
    {
        double? best = null;
        var bestErr = double.MaxValue;
        foreach (var beats in new[] { 4, 8, 16, 32, 64, 2, 12, 24 })
        {
            var bpm = beats * 60 / seconds;
            if (bpm is < 70 or > 180) continue;
            var rounded = Math.Round(bpm);
            var gridErr = Math.Abs(bpm - rounded) / bpm;
            if (gridErr >= 0.004) continue;
            // Among whole-bar fits, take the one closest to the tempo heard in the onsets.
            var err = hint is { } h ? Math.Min(Math.Abs(bpm - h), Math.Abs(bpm - 2 * h)) / h : gridErr;
            if (hint is not null && err > 0.04) continue;
            if (err < bestErr) { bestErr = err; best = rounded; }
        }
        return best;
    }

    /// <summary>Tempo from the autocorrelation of the onset curve, snapped to a whole-bar fit of the loop length when one agrees.</summary>
    static double? EstimateTempo(double[] onset, double frameRate, double seconds)
    {
        if (onset.Length < 16) return BarFit(seconds);
        var mean = onset.Average();
        var o = onset.Select(v => v - mean).ToArray();
        double? bestBpm = null;
        var bestScore = double.MinValue;
        for (var bpm = 60.0; bpm <= 200; bpm += 0.5)
        {
            var lag = frameRate * 60 / bpm;
            var score = 0.0;
            // Beat, two beats and half beat lags all count, so the result prefers the real pulse.
            foreach (var (mult, w) in new[] { (1.0, 1.0), (2.0, 0.5), (0.5, 0.25) })
                score += w * Autocorr(o, lag * mult);
            score *= Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 120) / 0.9, 2));   // gentle prior around 120
            if (score > bestScore) { bestScore = score; bestBpm = bpm; }
        }
        if (bestBpm is null) return BarFit(seconds);
        return BarFit(seconds, bestBpm) ?? Math.Round(bestBpm.Value);
    }

    static double Autocorr(double[] o, double lag)
    {
        var l0 = (int)Math.Floor(lag);
        var frac = lag - l0;
        double s = 0;
        for (var i = 0; i + l0 + 1 < o.Length; i++)
            s += o[i] * (o[i + l0] * (1 - frac) + o[i + l0 + 1] * frac);
        return s / Math.Max(1, o.Length - l0);
    }
}

/// <summary>YIN pitch detector (de Cheveigné &amp; Kawahara), run over a few frames and taking the median.</summary>
public static class Yin
{
    public static (double? Hz, double Confidence) Estimate(float[] x, int sr, int start = 0, double fMin = 30, double fMax = 2000)
    {
        var maxLag = (int)(sr / fMin);
        var minLag = Math.Max(2, (int)(sr / fMax));
        var w = Math.Min(maxLag * 2, sr / 5);
        var results = new List<(double Hz, double Conf)>();
        for (var frame = 0; frame < 8; frame++)
        {
            var o = start + frame * w / 2;
            if (o + w + maxLag >= x.Length) break;
            var d = new double[maxLag + 1];
            for (var tau = 1; tau <= maxLag; tau++)
            {
                double s = 0;
                for (var i = 0; i < w; i++)
                {
                    var diff = x[o + i] - x[o + i + tau];
                    s += diff * diff;
                }
                d[tau] = s;
            }
            // Cumulative mean normalised difference.
            var cmnd = new double[maxLag + 1];
            cmnd[0] = 1;
            double run = 0;
            for (var tau = 1; tau <= maxLag; tau++)
            {
                run += d[tau];
                cmnd[tau] = run <= 0 ? 1 : d[tau] * tau / run;
            }
            var best = -1;
            for (var tau = minLag; tau < maxLag; tau++)
                if (cmnd[tau] < 0.15)
                {
                    while (tau + 1 < maxLag && cmnd[tau + 1] < cmnd[tau]) tau++;
                    best = tau;
                    break;
                }
            if (best < 0)
            {
                best = minLag;
                for (var tau = minLag; tau < maxLag; tau++) if (cmnd[tau] < cmnd[best]) best = tau;
            }
            // Parabolic interpolation around the dip.
            double shift = 0;
            if (best > 1 && best < maxLag)
            {
                var (a, b, c) = (cmnd[best - 1], cmnd[best], cmnd[best + 1]);
                var den = a - 2 * b + c;
                if (Math.Abs(den) > 1e-12) shift = 0.5 * (a - c) / den;
            }
            results.Add((sr / (best + shift), 1 - Math.Min(1, cmnd[best])));
        }
        if (results.Count == 0) return (null, 0);
        var good = results.Where(r => r.Conf > 0.5).OrderBy(r => r.Hz).ToList();
        if (good.Count == 0) return (null, results.Max(r => r.Conf));
        var median = good[good.Count / 2];
        return (median.Hz, good.Average(r => r.Conf) * good.Count / results.Count);
    }
}
