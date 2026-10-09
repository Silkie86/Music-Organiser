using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Sounds;

public sealed record TranscribedNote(double Start, double End, int Pitch, double Amplitude)
{
    public string Name => MusicTheory.NoteName(Pitch);
}

/// <summary>
/// Polyphonic note transcription with Spotify's Basic Pitch model (Apache 2.0),
/// run locally with ONNX Runtime. A C# port of basic_pitch.inference and
/// basic_pitch.note_creation (v0.4.0), without pitch bends.
/// </summary>
public sealed class NoteTranscriber : IDisposable
{
    const int SampleRate = 22050;
    const int FftHop = 256;
    const int WindowSamples = SampleRate * 2 - FftHop;      // 43844
    const int FramesPerWindow = SampleRate / FftHop * 2;     // 172
    const int OverlapFrames = 30;
    const int OverlapSamples = OverlapFrames * FftHop;
    const int HopSamples = WindowSamples - OverlapSamples;
    const int MidiOffset = 21;
    const int MaxFreqIdx = 87;
    const double FramesPerSecond = (double)(SampleRate / FftHop);   // 86 (integer division, as in Basic Pitch)

    readonly InferenceSession _session;
    readonly object _lock = new();

    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "Models", "basic-pitch.onnx");

    public static bool IsAvailable => File.Exists(DefaultModelPath);

    public NoteTranscriber(string? modelPath = null)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(modelPath ?? DefaultModelPath, options);
    }

    public void Dispose() => _session.Dispose();

    public sealed record Settings(double OnsetThreshold = 0.5, double FrameThreshold = 0.3, double MinNoteMs = 127.7,
                                  bool MelodiaTrick = true, double? MinHz = null, double? MaxHz = null);

    public IReadOnlyList<TranscribedNote> Transcribe(AudioData audio, Settings? settings = null) =>
        Transcribe(Dsp.Resample(audio.Mono(), audio.SampleRate, SampleRate), settings);

    /// <summary>Notes in mono audio already at 22,050 Hz.</summary>
    public IReadOnlyList<TranscribedNote> Transcribe(float[] audio22k, Settings? settings = null)
    {
        settings ??= new Settings();
        var (frames, onsets) = RunModel(audio22k);
        var minLen = (int)Math.Round(settings.MinNoteMs / 1000 * ((double)SampleRate / FftHop));
        var events = NotesFromPosteriors(frames, onsets, settings.OnsetThreshold, settings.FrameThreshold, minLen,
                                         inferOnsets: true, settings.MaxHz, settings.MinHz, settings.MelodiaTrick);
        var times = FrameTimes(frames.GetLength(0));
        return events
            .Select(e => new TranscribedNote(Math.Round(times[e.Start], 4), Math.Round(times[e.End], 4), e.Pitch, Math.Round(e.Amp, 3)))
            .OrderBy(n => n.Start).ThenBy(n => n.Pitch)
            .ToList();
    }

    /// <summary>Raw model output: frame (note) and onset activations, frames × 88.</summary>
    public (float[,] Frames, float[,] Onsets) RunModel(float[] audio)
    {
        // Pad with half the overlap at the front, then cut into overlapping 2 s windows.
        var padded = new float[OverlapSamples / 2 + audio.Length];
        Array.Copy(audio, 0, padded, OverlapSamples / 2, audio.Length);
        var starts = new List<int>();
        for (var i = 0; i < padded.Length; i += HopSamples) starts.Add(i);

        var notes = new List<float[,]>();
        var onsets = new List<float[,]>();
        const int batch = 8;
        for (var b = 0; b < starts.Count; b += batch)
        {
            var count = Math.Min(batch, starts.Count - b);
            var input = new DenseTensor<float>([count, WindowSamples, 1]);
            for (var w = 0; w < count; w++)
            {
                var s = starts[b + w];
                for (var i = 0; i < WindowSamples && s + i < padded.Length; i++) input[w, i, 0] = padded[s + i];
            }
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results;
            lock (_lock)
                results = _session.Run([NamedOnnxValue.CreateFromTensor("serving_default_input_2:0", input)],
                                       ["StatefulPartitionedCall:1", "StatefulPartitionedCall:2"]);
            using (results)
            {
                var note = results.First(r => r.Name == "StatefulPartitionedCall:1").AsTensor<float>();
                var onset = results.First(r => r.Name == "StatefulPartitionedCall:2").AsTensor<float>();
                for (var w = 0; w < count; w++)
                {
                    notes.Add(Slice(note, w));
                    onsets.Add(Slice(onset, w));
                }
            }
        }
        var nFrames = (int)Math.Floor(audio.Length * (FramesPerSecond / SampleRate));
        return (Unwrap(notes, nFrames), Unwrap(onsets, nFrames));
    }

    static float[,] Slice(Tensor<float> t, int w)
    {
        var frames = t.Dimensions[1];
        var bins = t.Dimensions[2];
        var m = new float[frames, bins];
        for (var f = 0; f < frames; f++)
            for (var k = 0; k < bins; k++) m[f, k] = t[w, f, k];
        return m;
    }

    /// <summary>Drops half the overlap from both ends of each window and joins them.</summary>
    static float[,] Unwrap(List<float[,]> windows, int nFrames)
    {
        const int cut = OverlapFrames / 2;
        var bins = windows[0].GetLength(1);
        var per = windows[0].GetLength(0) - 2 * cut;
        var total = Math.Min(nFrames, per * windows.Count);
        var outp = new float[total, bins];
        for (var f = 0; f < total; f++)
        {
            var w = windows[f / per];
            var src = f % per + cut;
            for (var k = 0; k < bins; k++) outp[f, k] = w[src, k];
        }
        return outp;
    }

    static double[] FrameTimes(int nFrames)
    {
        var times = new double[nFrames + 1];
        var windowOffset = (double)FftHop / SampleRate * (FramesPerWindow - (double)WindowSamples / FftHop) + 0.0018;
        for (var i = 0; i <= nFrames; i++)
            times[i] = (double)i * FftHop / SampleRate - windowOffset * Math.Floor((double)i / FramesPerWindow);
        return times;
    }

    internal readonly record struct NoteEventIdx(int Start, int End, int Pitch, double Amp);

    internal static List<NoteEventIdx> NotesFromPosteriors(float[,] frames, float[,] onsetsIn, double onsetThresh, double frameThresh,
        int minNoteLen, bool inferOnsets, double? maxFreq, double? minFreq, bool melodiaTrick, int energyTol = 11)
    {
        var nFrames = frames.GetLength(0);
        var nFreqs = frames.GetLength(1);
        var onsets = (float[,])onsetsIn.Clone();
        var fr = (float[,])frames.Clone();
        if (maxFreq is { } mx)
        {
            var idx = (int)Math.Round(MusicTheory.HzToMidi(mx) - MidiOffset);
            for (var t = 0; t < nFrames; t++) for (var k = Math.Max(0, idx); k < nFreqs; k++) onsets[t, k] = fr[t, k] = 0;
        }
        if (minFreq is { } mn)
        {
            var idx = (int)Math.Round(MusicTheory.HzToMidi(mn) - MidiOffset);
            for (var t = 0; t < nFrames; t++) for (var k = 0; k < Math.Min(idx, nFreqs); k++) onsets[t, k] = fr[t, k] = 0;
        }
        if (inferOnsets) onsets = InferOnsets(onsets, fr);

        // Local maxima over time that clear the onset threshold, latest first.
        var peaks = new List<(int T, int F)>();
        for (var t = 1; t < nFrames - 1; t++)
            for (var k = 0; k < nFreqs; k++)
            {
                var v = onsets[t, k];
                if (v > onsets[t - 1, k] && v > onsets[t + 1, k] && v >= onsetThresh) peaks.Add((t, k));
            }
        peaks.Reverse();

        var remaining = (float[,])fr.Clone();
        var events = new List<NoteEventIdx>();
        foreach (var (start, f) in peaks)
        {
            if (start >= nFrames - 1) continue;
            var i = start + 1;
            var k = 0;
            while (i < nFrames - 1 && k < energyTol)
            {
                k = remaining[i, f] < frameThresh ? k + 1 : 0;
                i++;
            }
            i -= k;
            if (i - start <= minNoteLen) continue;
            for (var t = start; t < i; t++)
            {
                remaining[t, f] = 0;
                if (f < MaxFreqIdx) remaining[t, f + 1] = 0;
                if (f > 0) remaining[t, f - 1] = 0;
            }
            events.Add(new(start, i, f + MidiOffset, Mean(fr, start, i, f)));
        }

        if (melodiaTrick)
        {
            while (true)
            {
                var (best, bt, bf) = (0f, -1, -1);
                for (var t = 0; t < nFrames; t++)
                    for (var q = 0; q < nFreqs; q++)
                        if (remaining[t, q] > best) (best, bt, bf) = (remaining[t, q], t, q);
                if (best <= frameThresh) break;
                remaining[bt, bf] = 0;

                var i = bt + 1;
                var k = 0;
                while (i < nFrames - 1 && k < energyTol)
                {
                    k = remaining[i, bf] < frameThresh ? k + 1 : 0;
                    Clear(remaining, i, bf);
                    i++;
                }
                var end = i - 1 - k;

                i = bt - 1;
                k = 0;
                while (i > 0 && k < energyTol)
                {
                    k = remaining[i, bf] < frameThresh ? k + 1 : 0;
                    Clear(remaining, i, bf);
                    i--;
                }
                var begin = i + 1 + k;
                if (end - begin <= minNoteLen) continue;
                events.Add(new(begin, end, bf + MidiOffset, Mean(fr, begin, end, bf)));
            }
        }
        return events;
    }

    static void Clear(float[,] m, int t, int f)
    {
        m[t, f] = 0;
        if (f < MaxFreqIdx) m[t, f + 1] = 0;
        if (f > 0) m[t, f - 1] = 0;
    }

    static double Mean(float[,] m, int from, int to, int f)
    {
        if (to <= from) return 0;
        double s = 0;
        for (var t = from; t < to; t++) s += m[t, f];
        return s / (to - from);
    }

    /// <summary>Adds onsets where frame activations jump, rescaled to the onset range.</summary>
    static float[,] InferOnsets(float[,] onsets, float[,] frames, int nDiff = 2)
    {
        var T = frames.GetLength(0);
        var F = frames.GetLength(1);
        var diff = new float[T, F];
        float maxDiff = 0, maxOnset = 0;
        for (var t = 0; t < T; t++)
            for (var f = 0; f < F; f++)
            {
                var min = float.MaxValue;
                for (var n = 1; n <= nDiff; n++)
                {
                    var prev = t - n >= 0 ? frames[t - n, f] : 0;
                    min = Math.Min(min, frames[t, f] - prev);
                }
                var v = t < nDiff || min < 0 ? 0 : min;
                diff[t, f] = v;
                maxDiff = Math.Max(maxDiff, v);
                maxOnset = Math.Max(maxOnset, onsets[t, f]);
            }
        var outp = new float[T, F];
        for (var t = 0; t < T; t++)
            for (var f = 0; f < F; f++)
            {
                var d = maxDiff > 0 ? maxOnset * diff[t, f] / maxDiff : 0;
                outp[t, f] = Math.Max(onsets[t, f], d);
            }
        return outp;
    }

    /// <summary>The notes as a one-track MIDI clip (480 PPQ), for <see cref="Export.MidiWriter"/>.</summary>
    public static MidiClip ToMidi(string name, IReadOnlyList<TranscribedNote> notes, double bpm = 120)
    {
        const int ppq = 480;
        var clip = new MidiClip { Name = name, Ppq = ppq };
        var track = clip.Track(name);
        var ticksPerSecond = ppq * bpm / 60;
        foreach (var n in notes)
        {
            var start = (long)Math.Round(n.Start * ticksPerSecond);
            var len = Math.Max(1, (long)Math.Round((n.End - n.Start) * ticksPerSecond));
            track.Add(new NoteEvent(start, len, n.Pitch, Math.Clamp((int)Math.Round(127 * n.Amplitude), 1, 127)));
        }
        return clip;
    }
}
