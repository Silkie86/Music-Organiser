using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi;

/// <summary>
/// CLaMP 3 (Wu et al. 2025, MIT licence) run locally with ONNX Runtime: it turns MIDI and words into vectors in the same
/// space, which gives the MIDI browser search by description ("dark pad chords", "bouncy funk bassline") and "more like
/// this" by how parts sound and move. It knows nothing about your keys or chords; harmony fitting stays music theory.
///
/// Two encoders from the symbolic ("C2") checkpoint, exported to ONNX by tools/clamp3/export_onnx.py: the M3 encoder
/// for MIDI (as MTF text, see <see cref="MtfText"/>) and the XLM-RoBERTa text encoder. Each takes its input and gives a
/// 768-number vector (pooled and projected; this class makes it unit length). The files are an optional download into
/// the app's data folder; nothing of the model is in git.
/// </summary>
public sealed class Clamp3Model : IDisposable
{
    /// <summary>Which export the app expects; saved with each embedding so a new export re-reads everything.</summary>
    public const string Version = "clamp3-c2-onnx-v1";

    /// <summary>Where the ONNX export lives: a GitHub release of this app's repository (made by the clamp3-onnx workflow).</summary>
    public const string DownloadBase = "https://github.com/Silkie86/Music-Organiser/releases/download/" + Version + "/";

    /// <summary>Files to fetch: (name in the release, local name).</summary>
    public static readonly (string Remote, string Local)[] Files =
    [
        ("clamp3_symbolic_int8.onnx", "symbolic.onnx"),
        ("clamp3_text_int8.onnx", "text.onnx"),
        ("tokenizer.json", "tokenizer.json"),
        ("LICENSE-clamp3.txt", "LICENSE-clamp3.txt"),
    ];

    public const int Dimensions = 768;
    public const int MaxTextLength = 128;

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "MusicOrganiser", "models", "clamp3");

    public static bool IsInstalled(string? folder = null)
    {
        folder ??= DefaultFolder;
        return Files.Where(f => f.Local.EndsWith(".onnx") || f.Local.EndsWith(".json")).All(f => File.Exists(Path.Combine(folder, f.Local)));
    }

    /// <summary>
    /// Downloads the model into <paramref name="folder"/>. Each file goes to a .part file first, so an interrupted
    /// download never looks installed. Progress is reported when each file starts and then about every half a megabyte.
    /// </summary>
    public static async Task DownloadAsync(IProgress<(string File, long Done, long? Total)>? progress = null,
                                           string? folder = null, string? baseUrl = null, CancellationToken ct = default)
    {
        folder ??= DefaultFolder;
        baseUrl ??= DownloadBase;
        Directory.CreateDirectory(folder);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MusicOrganiser");
        foreach (var (remote, local) in Files)
        {
            var target = Path.Combine(folder, local);
            if (File.Exists(target)) continue;
            using var resp = await http.GetAsync(baseUrl + remote, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            progress?.Report((local, 0, total));
            var part = target + ".part";
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(part))
            {
                var buf = new byte[1 << 16];
                long done = 0, reported = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (done - reported >= 1 << 19) progress?.Report((local, reported = done, total));
                }
                progress?.Report((local, done, total));
            }
            File.Move(part, target, overwrite: true);
        }
    }

    // Copies of the MIDI encoder run side by side, each on its own share of the background workers (as ClapModel does),
    // so reading a big MIDI library keeps them busy but leaves two cores for the window.
    static readonly int Copies = Math.Max(1, Parallelism.Workers / 4);
    readonly string _symbolicPath;
    readonly SessionOptions _symbolicOptions;
    readonly ConcurrentBag<InferenceSession> _pool = [];
    readonly SemaphoreSlim _slots = new(Copies, Copies);
    readonly List<InferenceSession> _all = [];
    readonly InferenceSession _text;
    readonly object _textLock = new();
    readonly UnigramTokenizer _tokenizer;

    public Clamp3Model(string? folder = null)
    {
        folder ??= DefaultFolder;
        _symbolicPath = Path.Combine(folder, "symbolic.onnx");
        _symbolicOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Parallelism.Workers / Copies),
        };
        // ONNX Runtime's threads otherwise spin at normal priority between pieces of work, eating the cores left for the window.
        _symbolicOptions.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        _pool.Add(NewSymbolicSession());
        using var textOptions = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _text = new InferenceSession(Path.Combine(folder, "text.onnx"), textOptions);
        _tokenizer = UnigramTokenizer.Load(Path.Combine(folder, "tokenizer.json"));
    }

    InferenceSession NewSymbolicSession()
    {
        var session = new InferenceSession(_symbolicPath, _symbolicOptions);
        lock (_all) _all.Add(session);
        return session;
    }

    public void Dispose()
    {
        lock (_all) foreach (var s in _all) s.Dispose();
        _symbolicOptions.Dispose();
        _slots.Dispose();
        _text.Dispose();
    }

    /// <summary>Unit-length vector for a MIDI part (notes at <paramref name="ppq"/>).</summary>
    public float[] EmbedMidi(IReadOnlyList<NoteEvent> notes, int ppq, double? tempo = null, bool drums = false) =>
        EmbedMtf(MtfText.FromNotes(notes, ppq, tempo, drums));

    /// <summary>Unit-length vector for an MTF text: long pieces are read in windows and averaged, as CLaMP 3 does.</summary>
    public float[] EmbedMtf(string mtf)
    {
        var patches = M3Patches.Encode(mtf);
        double[]? sum = null;
        var weights = 0.0;
        foreach (var (start, count, weight) in M3Patches.Windows(patches.Count))
        {
            var input = new DenseTensor<long>([1, count, M3Patches.PatchSize]);
            for (var p = 0; p < count; p++)
                for (var c = 0; c < M3Patches.PatchSize; c++) input[0, p, c] = patches[start + p][c];
            var mask = new DenseTensor<long>(Enumerable.Repeat(1L, count).ToArray(), [1, count]);
            var v = RunSymbolic([NamedOnnxValue.CreateFromTensor("patches", input), NamedOnnxValue.CreateFromTensor("mask", mask)]);
            sum ??= new double[v.Length];
            for (var i = 0; i < v.Length; i++) sum[i] += v[i] * (double)weight;
            weights += weight;
        }
        return Normalise(sum!.Select(x => (float)(x / weights)).ToArray());
    }

    float[] RunSymbolic(List<NamedOnnxValue> inputs)
    {
        _slots.Wait();
        try
        {
            // A free copy, or a new one: there are never more copies than slots.
            if (!_pool.TryTake(out var session)) session = NewSymbolicSession();
            try
            {
                using var res = session.Run(inputs);
                return res.First().AsEnumerable<float>().ToArray();
            }
            finally { _pool.Add(session); }
        }
        finally { _slots.Release(); }
    }

    /// <summary>Unit-length vector for a description such as "dark pad chords".</summary>
    public float[] EmbedText(string text)
    {
        var ids = _tokenizer.Encode(text, MaxTextLength);
        var idT = new DenseTensor<long>(ids.Select(i => (long)i).ToArray(), [1, ids.Count]);
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, ids.Count).ToArray(), [1, ids.Count]);
        lock (_textLock)
        {
            using var res = _text.Run([NamedOnnxValue.CreateFromTensor("input_ids", idT), NamedOnnxValue.CreateFromTensor("attention_mask", mask)]);
            return Normalise(res.First().AsEnumerable<float>().ToArray());
        }
    }

    static float[] Normalise(float[] v)
    {
        var n = Math.Sqrt(v.Sum(x => (double)x * x));
        if (n > 0) for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / n);
        return v;
    }

    /// <summary>A unit vector as 768 signed bytes (×127): a quarter of the size, and plenty for ranking.</summary>
    public static byte[] Pack(float[] unit)
    {
        var b = new byte[unit.Length];
        for (var i = 0; i < unit.Length; i++) b[i] = (byte)(sbyte)Math.Clamp(Math.Round(unit[i] * 127), -127, 127);
        return b;
    }

    public static sbyte[] Unpack(byte[] packed)
    {
        var s = new sbyte[packed.Length];
        Buffer.BlockCopy(packed, 0, s, 0, packed.Length);
        return s;
    }

    /// <summary>Cosine likeness (−1 to 1) of a unit vector and a packed one.</summary>
    public static float Similarity(float[] unit, sbyte[] packed)
    {
        if (packed.Length != unit.Length) return 0;
        float dot = 0, norm = 0;
        for (var i = 0; i < unit.Length; i++)
        {
            dot += unit[i] * packed[i];
            norm += packed[i] * packed[i];
        }
        return norm > 0 ? dot / MathF.Sqrt(norm) : 0;
    }

    /// <summary>Cosine likeness (−1 to 1) of two packed vectors.</summary>
    public static float Similarity(sbyte[] a, sbyte[] b)
    {
        if (a.Length != b.Length) return 0;
        int dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na > 0 && nb > 0 ? dot / MathF.Sqrt((float)na * nb) : 0;
    }
}
