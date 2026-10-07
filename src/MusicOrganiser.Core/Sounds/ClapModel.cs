using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MusicOrganiser.Core.Sounds;

/// <summary>
/// CLAP (Contrastive Language-Audio Pretraining, LAION) run locally with ONNX Runtime.
/// It turns audio and text into vectors in the same space, which gives:
/// search by description ("dusty vinyl snare"), zero-shot sound types, and
/// sound-alike search that hears timbre the way people describe it.
///
/// The model is an optional download (Hugging Face "Xenova/larger_clap_music",
/// ONNX export of laion/larger_clap_music). Expected folder layout:
/// <c>audio_model.onnx</c>, <c>text_model.onnx</c>, <c>tokenizer.json</c> and
/// optionally <c>preprocessor_config.json</c>.
/// </summary>
public sealed class ClapModel : IDisposable
{
    public const string HuggingFaceRepo = "Xenova/larger_clap_music";

    /// <summary>Files to fetch: (path in the repo, local name).</summary>
    public static readonly (string Remote, string Local)[] Files =
    [
        ("onnx/audio_model_quantized.onnx", "audio_model.onnx"),
        ("onnx/text_model_quantized.onnx", "text_model.onnx"),
        ("tokenizer.json", "tokenizer.json"),
        ("preprocessor_config.json", "preprocessor_config.json"),
    ];

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicOrganiser", "models", "clap");

    public static bool IsInstalled(string? folder = null)
    {
        folder ??= DefaultFolder;
        return File.Exists(Path.Combine(folder, "audio_model.onnx")) && File.Exists(Path.Combine(folder, "text_model.onnx")) &&
               File.Exists(Path.Combine(folder, "tokenizer.json"));
    }

    readonly InferenceSession _audio;
    readonly InferenceSession _text;
    readonly BpeTokenizer _tokenizer;
    readonly ClapFeatureExtractor _extractor;
    readonly object _lock = new();
    readonly int _channels;
    readonly string _audioInput, _audioOutput, _textOutput;
    readonly bool _wantsIsLonger;

    public int Dimensions { get; private set; }

    public ClapModel(string? folder = null)
    {
        folder ??= DefaultFolder;
        var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _audio = new InferenceSession(Path.Combine(folder, "audio_model.onnx"), opts);
        _text = new InferenceSession(Path.Combine(folder, "text_model.onnx"), opts);
        _tokenizer = BpeTokenizer.Load(Path.Combine(folder, "tokenizer.json"));
        var cfgPath = Path.Combine(folder, "preprocessor_config.json");
        _extractor = File.Exists(cfgPath) ? ClapFeatureExtractor.FromConfig(File.ReadAllText(cfgPath)) : new ClapFeatureExtractor();

        _audioInput = _audio.InputMetadata.Keys.FirstOrDefault(k => k.Contains("input_features")) ?? _audio.InputMetadata.Keys.First();
        _wantsIsLonger = _audio.InputMetadata.ContainsKey("is_longer");
        var dims = _audio.InputMetadata[_audioInput].Dimensions;
        _channels = dims.Length == 4 && dims[1] > 0 ? dims[1] : _extractor.Fusion ? 4 : 1;
        _audioOutput = _audio.OutputMetadata.Keys.FirstOrDefault(k => k.Contains("audio_embeds")) ?? _audio.OutputMetadata.Keys.First();
        _textOutput = _text.OutputMetadata.Keys.FirstOrDefault(k => k.Contains("text_embeds")) ?? _text.OutputMetadata.Keys.First();
    }

    public void Dispose()
    {
        _audio.Dispose();
        _text.Dispose();
    }

    /// <summary>Unit-length embedding for a sound.</summary>
    public float[] EmbedAudio(AudioData audio)
    {
        var feats = _extractor.Extract(Dsp.Resample(audio.Mono(), audio.SampleRate, _extractor.SampleRate), out var longer);
        var channels = _channels == 1 ? feats[..1] : feats.Length == 1 ? [feats[0], feats[0], feats[0], feats[0]] : feats;
        var frames = channels[0].Length;
        var mels = channels[0][0].Length;
        var t = new DenseTensor<float>([1, channels.Length, frames, mels]);
        for (var c = 0; c < channels.Length; c++)
            for (var f = 0; f < frames; f++)
                for (var m = 0; m < mels; m++) t[0, c, f, m] = channels[c][f][m];
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_audioInput, t) };
        if (_wantsIsLonger) inputs.Add(NamedOnnxValue.CreateFromTensor("is_longer", new DenseTensor<bool>(new[] { longer }, [1, 1])));
        lock (_lock)
        {
            using var res = _audio.Run(inputs, [_audioOutput]);
            return Normalise(res.First().AsEnumerable<float>().ToArray());
        }
    }

    /// <summary>Unit-length embedding for a description such as "punchy acoustic kick".</summary>
    public float[] EmbedText(string text)
    {
        var ids = _tokenizer.Encode(text, 77);
        var idT = new DenseTensor<long>(ids.Select(i => (long)i).ToArray(), [1, ids.Count]);
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, ids.Count).ToArray(), [1, ids.Count]);
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input_ids", idT) };
        if (_text.InputMetadata.ContainsKey("attention_mask")) inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", mask));
        lock (_lock)
        {
            using var res = _text.Run(inputs, [_textOutput]);
            var v = Normalise(res.First().AsEnumerable<float>().ToArray());
            Dimensions = v.Length;
            return v;
        }
    }

    static float[] Normalise(float[] v)
    {
        var n = Math.Sqrt(v.Sum(x => (double)x * x));
        if (n > 0) for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / n);
        return v;
    }
}

/// <summary>
/// C# port of Hugging Face's ClapFeatureExtractor: log-mel spectrograms in the
/// layout the CLAP audio model was trained on ("fusion" or "rand_trunc" modes).
/// </summary>
public sealed class ClapFeatureExtractor
{
    public int SampleRate { get; init; } = 48000;
    public int NFft { get; init; } = 1024;
    public int Hop { get; init; } = 480;
    public int Mels { get; init; } = 64;
    public int MaxSamples { get; init; } = 480000;
    public double FMin { get; init; }
    public double FMax { get; init; } = 14000;
    /// <summary>"fusion" (4 channels, HTK mel filters) or "rand_trunc" (1 channel, Slaney filters).</summary>
    public bool Fusion { get; init; } = true;
    /// <summary>"repeatpad", "repeat" or "pad".</summary>
    public string Padding { get; init; } = "repeatpad";

    double[,]? _filters;
    double[,] Filters => _filters ??= Fusion
        ? Dsp.MelFilters(NFft / 2 + 1, Mels, FMin, FMax, SampleRate, Dsp.MelScale.Htk, false)
        : Dsp.MelFilters(NFft / 2 + 1, Mels, FMin, FMax, SampleRate, Dsp.MelScale.Slaney, true);

    public static ClapFeatureExtractor FromConfig(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        int I(string k, int d) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : d;
        double D(string k, double d) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
        string S(string k, string d) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : d;
        var sr = I("sampling_rate", 48000);
        return new ClapFeatureExtractor
        {
            SampleRate = sr, NFft = I("fft_window_size", 1024), Hop = I("hop_length", 480), Mels = I("feature_size", 64),
            MaxSamples = I("nb_max_samples", I("max_length_s", 10) * sr),
            FMin = D("frequency_min", 0), FMax = D("frequency_max", 14000),
            Fusion = S("truncation", "fusion") == "fusion", Padding = S("padding", "repeatpad"),
        };
    }

    /// <summary>Channels × frames × mels for mono audio at <see cref="SampleRate"/>.</summary>
    public float[][][] Extract(float[] x, out bool longer)
    {
        var chunkFrames = MaxSamples / Hop + 1;
        longer = false;
        if (x.Length > MaxSamples)
        {
            if (!Fusion)
            {
                // The original picks a random crop; the start keeps results repeatable.
                longer = true;
                return [LogMel(x[..MaxSamples])];
            }
            var mel = LogMel(x);
            if (mel.Length == chunkFrames) return [mel, mel, mel, mel];
            longer = true;
            // Downsized whole + front, middle and back crops (deterministic version of _random_mel_fusion).
            var positions = mel.Length - chunkFrames + 1;
            int Mid(int part)
            {
                var from = positions * part / 3;
                var to = positions * (part + 1) / 3;
                return to > from ? (from + to - 1) / 2 : 0;
            }
            return [Shrink(mel, chunkFrames), mel[Mid(0)..(Mid(0) + chunkFrames)], mel[Mid(1)..(Mid(1) + chunkFrames)], mel[Mid(2)..(Mid(2) + chunkFrames)]];
        }

        if (x.Length < MaxSamples && x.Length > 0)
        {
            var padded = new float[MaxSamples];
            if (Padding is "repeatpad" or "repeat")
            {
                var reps = Padding == "repeat" ? MaxSamples / x.Length + 1 : MaxSamples / x.Length;
                for (var r = 0; r < reps; r++)
                {
                    var o = r * x.Length;
                    if (o >= MaxSamples) break;
                    Array.Copy(x, 0, padded, o, Math.Min(x.Length, MaxSamples - o));
                }
            }
            else Array.Copy(x, padded, x.Length);
            x = padded;
        }
        var m = LogMel(x);
        return Fusion ? [m, m, m, m] : [m];
    }

    float[][] LogMel(float[] x)
    {
        var mel = Dsp.ApplyFilters(Dsp.PowerSpectrogram(x, NFft, Hop), Filters);
        foreach (var row in mel)
            for (var i = 0; i < row.Length; i++) row[i] = (float)(10 * Math.Log10(Math.Max(row[i], 1e-10)));
        return mel;
    }

    /// <summary>Bilinear resize of frames × mels to <paramref name="frames"/> × mels (PyTorch, align_corners = false).</summary>
    static float[][] Shrink(float[][] mel, int frames)
    {
        var src = mel.Length;
        var outp = new float[frames][];
        var scale = (double)src / frames;
        for (var i = 0; i < frames; i++)
        {
            var pos = Math.Max(0, (i + 0.5) * scale - 0.5);
            var i0 = Math.Min((int)pos, src - 1);
            var i1 = Math.Min(i0 + 1, src - 1);
            var w = pos - i0;
            var row = outp[i] = new float[mel[0].Length];
            for (var k = 0; k < row.Length; k++) row[k] = (float)(mel[i0][k] * (1 - w) + mel[i1][k] * w);
        }
        return outp;
    }
}

/// <summary>Byte-level BPE tokenizer (GPT-2/RoBERTa style) read from a Hugging Face tokenizer.json.</summary>
public sealed partial class BpeTokenizer
{
    readonly Dictionary<string, int> _vocab;
    readonly Dictionary<(string, string), int> _ranks;
    readonly Dictionary<string, string[]> _cache = new();
    static readonly char[] ByteToChar = BuildByteMap();
    readonly int _bos, _eos;

    public BpeTokenizer(Dictionary<string, int> vocab, IEnumerable<(string, string)> merges, string bos = "<s>", string eos = "</s>")
    {
        _vocab = vocab;
        _ranks = new();
        var rank = 0;
        foreach (var m in merges) _ranks.TryAdd(m, rank++);
        _bos = vocab.GetValueOrDefault(bos, 0);
        _eos = vocab.GetValueOrDefault(eos, 2);
    }

    public static BpeTokenizer Load(string tokenizerJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(tokenizerJson));
        var model = doc.RootElement.GetProperty("model");
        var vocab = model.GetProperty("vocab").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        var merges = model.GetProperty("merges").EnumerateArray().Select(m =>
        {
            if (m.ValueKind == JsonValueKind.Array) return (m[0].GetString()!, m[1].GetString()!);
            var s = m.GetString()!;
            var i = s.IndexOf(' ');
            return (s[..i], s[(i + 1)..]);
        }).ToList();
        return new BpeTokenizer(vocab, merges);
    }

    /// <summary>Token ids including the start and end tokens, cut to <paramref name="maxLength"/>.</summary>
    public List<int> Encode(string text, int maxLength = 512)
    {
        var ids = new List<int> { _bos };
        foreach (Match m in PreTokenize().Matches(text))
        {
            var mapped = new string(Encoding.UTF8.GetBytes(m.Value).Select(b => ByteToChar[b]).ToArray());
            foreach (var piece in Bpe(mapped))
                if (_vocab.TryGetValue(piece, out var id)) ids.Add(id);
        }
        if (ids.Count > maxLength - 1) ids = ids[..(maxLength - 1)];
        ids.Add(_eos);
        return ids;
    }

    string[] Bpe(string token)
    {
        lock (_cache)
            if (_cache.TryGetValue(token, out var hit)) return hit;
        var parts = token.Select(c => c.ToString()).ToList();
        while (parts.Count > 1)
        {
            var best = -1;
            var bestRank = int.MaxValue;
            for (var i = 0; i < parts.Count - 1; i++)
                if (_ranks.TryGetValue((parts[i], parts[i + 1]), out var r) && r < bestRank) { bestRank = r; best = i; }
            if (best < 0) break;
            var (a, b) = (parts[best], parts[best + 1]);
            var merged = new List<string>(parts.Count);
            for (var i = 0; i < parts.Count; i++)
            {
                if (i < parts.Count - 1 && parts[i] == a && parts[i + 1] == b) { merged.Add(a + b); i++; }
                else merged.Add(parts[i]);
            }
            parts = merged;
        }
        var result = parts.ToArray();
        lock (_cache) _cache[token] = result;
        return result;
    }

    /// <summary>GPT-2's printable stand-ins for raw bytes.</summary>
    static char[] BuildByteMap()
    {
        var map = new char[256];
        var printable = new List<int>();
        for (var b = '!'; b <= '~'; b++) printable.Add(b);
        for (var b = '¡'; b <= '¬'; b++) printable.Add(b);
        for (var b = '®'; b <= 'ÿ'; b++) printable.Add(b);
        var extra = 0;
        for (var b = 0; b < 256; b++)
            map[b] = printable.Contains(b) ? (char)b : (char)(256 + extra++);
        return map;
    }

    [GeneratedRegex(@"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+")]
    private static partial Regex PreTokenize();
}
