using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Sounds;

/// <summary>
/// Turns MIDI channels into audio for previewing. A channel that plays a sample (FL Studio's
/// Sampler) plays that sample, pitched from its root key. Anything else gets a stand-in sound:
/// a soft keys tone, or a short hit for drum parts. It's a sketch of the part, not the plugin.
/// </summary>
public static class MidiRender
{
    public const int Rate = 44100;
    /// <summary>Longest preview rendered; past this the song is cut off.</summary>
    public const double MaxSeconds = 8 * 60;
    const double MaxSampleSeconds = 20;

    /// <summary>Mono audio at <see cref="Rate"/>, and what each channel was played with.</summary>
    public sealed record Result(float[] Audio, IReadOnlyList<string> Voices)
    {
        public double Seconds => (double)Audio.Length / Rate;
    }

    public static double TicksToSeconds(long ticks, int ppq, double tempo) => ticks * 60.0 / (tempo * ppq);
    public static double SecondsToTicks(double seconds, int ppq, double tempo) => seconds * tempo * ppq / 60.0;

    /// <summary>Renders the channels mixed together, from the start of the song.</summary>
    public static Result Render(IReadOnlyList<MidiChannel> channels, double? tempo, string projectPath, CancellationToken ct = default)
    {
        var bpm = tempo is > 10 and < 1000 ? tempo.Value : 120;
        var end = channels.Count == 0 ? 0 : channels.Max(c => TicksToSeconds(c.End, c.Ppq, bpm));
        var mix = new float[(int)(Math.Min(end + 2, MaxSeconds) * Rate)];
        var voices = new List<string>();
        foreach (var c in channels)
        {
            ct.ThrowIfCancellationRequested();
            var sample = LoadSample(c, projectPath);
            voices.Add(sample is not null ? $"its sample, {Path.GetFileName(c.SamplePath)}"
                     : c.SamplePath.Length > 0 ? "a stand-in tone (sample not found)"
                     : c.Kind == "Drums" ? "a stand-in drum hit" : "a stand-in tone");
            var drums = sample is null && c.Kind == "Drums";
            foreach (var n in c.Notes)
            {
                var at = (int)(TicksToSeconds(n.Start, c.Ppq, bpm) * Rate);
                if (at >= mix.Length) continue;
                var held = TicksToSeconds(Math.Max(1, n.Length), c.Ppq, bpm);
                var gain = 0.25f * Math.Clamp(n.Velocity, 1, 127) / 100f;
                if (sample is not null) PlaySample(mix, at, sample.Value.Data, sample.Value.Step * Math.Pow(2, (n.Key - c.RootKey) / 12.0 + c.PitchCents / 1200.0), gain);
                else if (drums) Hit(mix, at, n.Key, gain);
                else Tone(mix, at, held, n.Key, gain);
            }
        }
        ct.ThrowIfCancellationRequested();
        Limit(mix);
        return new Result(mix, voices);
    }

    /// <summary>The channel's sample as mono, with how far to step through it per output sample at the root key.</summary>
    static (float[] Data, double Step)? LoadSample(MidiChannel c, string projectPath)
    {
        if (c.SamplePath.Length == 0) return null;
        var path = c.SamplePath;
        if (!File.Exists(path))
        {
            // Projects moved between drives: try next to the project, by file name.
            var name = Path.GetFileName(path.Replace('\\', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(projectPath) ?? "";
            path = new[] { Path.Combine(dir, name), Path.Combine(dir, "Samples", name) }.FirstOrDefault(File.Exists) ?? "";
        }
        if (path.Length == 0 || !AudioFile.CanRead(path)) return null;
        try
        {
            var audio = AudioFile.Read(path, MaxSampleSeconds);
            return audio.Length == 0 ? null : (audio.Mono(), (double)audio.SampleRate / Rate);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Plays the whole sample, like FL Studio's Sampler with its envelope off, so a short note on
    /// a long sample still rings out.
    /// </summary>
    static void PlaySample(float[] mix, int at, float[] data, double step, float gain)
    {
        var fade = Rate / 400;   // 2.5 ms, so starts never click
        double pos = 0;
        for (var i = at; i < mix.Length; i++, pos += step)
        {
            var j = (int)pos;
            if (j + 1 >= data.Length) break;
            var frac = (float)(pos - j);
            var v = data[j] + (data[j + 1] - data[j]) * frac;
            var k = i - at;
            mix[i] += v * gain * 3 * (k < fade ? (float)k / fade : 1);
        }
    }

    /// <summary>A soft electric-piano-like tone that follows the note's length.</summary>
    static void Tone(float[] mix, int at, double held, int key, float gain)
    {
        var freq = 440 * Math.Pow(2, (key - 69) / 12.0);
        const double attack = 0.005, decay = 0.4, sustain = 0.45, release = 0.15;
        var length = (int)((held + release) * Rate);
        var w = 2 * Math.PI * freq / Rate;
        for (var k = 0; k < length && at + k < mix.Length; k++)
        {
            var t = (double)k / Rate;
            double env;
            if (t < attack) env = t / attack;
            else if (t < held) env = sustain + (1 - sustain) * Math.Exp(-(t - attack) / decay);
            else
            {
                var atRelease = sustain + (1 - sustain) * Math.Exp(-(held - attack) / decay);
                env = atRelease * Math.Max(0, 1 - (t - held) / release);
            }
            var bright = Math.Exp(-t * 3);   // the upper harmonics fade first, like a struck string
            var v = Math.Sin(w * k) + 0.35 * bright * Math.Sin(2 * w * k) + 0.12 * bright * Math.Sin(3 * w * k);
            mix[at + k] += (float)(v * env * gain);
        }
    }

    /// <summary>A short percussive hit: a falling sine thump with a burst of noise, brighter for higher keys.</summary>
    static void Hit(float[] mix, int at, int key, float gain)
    {
        var rng = new Random(key * 7919 + at);
        var low = key < 40;
        var length = (int)((low ? 0.35 : 0.18) * Rate);
        var noiseAmount = key < 40 ? 0.15 : key < 48 ? 0.6 : 0.9;
        var baseFreq = low ? 55 : 180 + (key % 12) * 20;
        double phase = 0;
        float lp = 0;
        for (var k = 0; k < length && at + k < mix.Length; k++)
        {
            var t = (double)k / Rate;
            var freq = baseFreq * (1 + 2 * Math.Exp(-t * 40));
            phase += 2 * Math.PI * freq / Rate;
            var noise = (float)(rng.NextDouble() * 2 - 1);
            lp += (noise - lp) * (key >= 48 ? 0.9f : 0.3f);
            var v = (1 - noiseAmount) * Math.Sin(phase) * Math.Exp(-t * (low ? 9 : 25)) + noiseAmount * lp * Math.Exp(-t * (key >= 48 ? 40 : 20));
            mix[at + k] += (float)(v * gain * 2);
        }
    }

    /// <summary>Keeps the mix out of clipping: scales it down if it peaks above -1 dB.</summary>
    static void Limit(float[] mix)
    {
        var peak = 0f;
        foreach (var v in mix) peak = Math.Max(peak, Math.Abs(v));
        if (peak <= 0.89f) return;
        var scale = 0.89f / peak;
        for (var i = 0; i < mix.Length; i++) mix[i] *= scale;
    }
}
