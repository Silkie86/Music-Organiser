using System.Text.Json;

namespace MusicOrganiser.Core.Sounds;

/// <summary>One labelled example: a sound's vector, its label, and how much to trust the label.</summary>
public readonly record struct TrainingExample(float[] X, string Label, float Weight);

/// <summary>
/// A softmax (multinomial logistic regression) classifier trained on your own
/// library: the vectors are CLAP embeddings when the model is installed, otherwise
/// the analyser's hand-crafted features. Labels come from folder names and from
/// your own corrections, which count more. Small, fast to train, and easy to
/// retrain whenever you correct it.
/// </summary>
public sealed class SoundClassifier
{
    public string[] Classes { get; init; } = [];
    public float[] Mean { get; init; } = [];
    public float[] Scale { get; init; } = [];
    /// <summary>Weights, classes × dimensions, flattened.</summary>
    public float[] W { get; init; } = [];
    public float[] B { get; init; } = [];
    /// <summary>Which vectors it was trained on: "clap" or "features".</summary>
    public string Space { get; init; } = "features";
    public int Examples { get; init; }
    /// <summary>Accuracy on a held-out fifth of the examples (0..1), or null when there were too few.</summary>
    public double? HeldOutAccuracy { get; init; }
    public DateTime Trained { get; init; } = DateTime.Now;

    int Dim => Mean.Length;

    public sealed record Options(int Epochs = 40, double LearningRate = 0.01, double L2 = 1e-4, int MaxPerClass = 800, int Seed = 7);

    public static SoundClassifier Train(IReadOnlyList<TrainingExample> all, string space, Options? options = null)
    {
        options ??= new Options();
        var rng = new Random(options.Seed);
        // Balance: cap each class so a huge folder of hats doesn't drown out everything else.
        var data = all.GroupBy(e => e.Label)
                      .SelectMany(g => g.OrderByDescending(e => e.Weight).ThenBy(_ => rng.Next()).Take(options.MaxPerClass))
                      .OrderBy(_ => rng.Next()).ToList();
        if (data.Count == 0) throw new InvalidOperationException("Nothing to train on yet");
        var classes = data.Select(e => e.Label).Distinct().Order().ToArray();
        if (classes.Length < 2) throw new InvalidOperationException("Need at least two kinds of sound to learn the difference");

        // The held-out check and the real model are trained at the same time.
        double? accuracy = null;
        var fitting = Task.Run(() => Fit(data, classes, space, options));
        if (data.Count >= 40)
        {
            var test = data.Where((_, i) => i % 5 == 0).ToList();
            var train = data.Where((_, i) => i % 5 != 0).ToList();
            if (train.Select(e => e.Label).Distinct().Count() >= 2)
            {
                var probe = Fit(train, classes, space, options);
                accuracy = Math.Round(test.Count(e => probe.Predict(e.X)[0].Label == e.Label) / (double)test.Count, 3);
            }
        }
        var model = fitting.GetAwaiter().GetResult();
        return new SoundClassifier
        {
            Classes = model.Classes, Mean = model.Mean, Scale = model.Scale, W = model.W, B = model.B,
            Space = space, Examples = all.Count, HeldOutAccuracy = accuracy,
        };
    }

    static SoundClassifier Fit(List<TrainingExample> data, string[] classes, string space, Options o)
    {
        var dim = data[0].X.Length;
        var k = classes.Length;
        var index = classes.Select((c, i) => (c, i)).ToDictionary(p => p.c, p => p.i);

        // Standardise each dimension.
        var mean = new float[dim];
        var scale = new float[dim];
        foreach (var e in data) for (var d = 0; d < dim; d++) mean[d] += e.X[d];
        for (var d = 0; d < dim; d++) mean[d] /= data.Count;
        foreach (var e in data) for (var d = 0; d < dim; d++) scale[d] += (e.X[d] - mean[d]) * (e.X[d] - mean[d]);
        for (var d = 0; d < dim; d++)
        {
            var sd = MathF.Sqrt(scale[d] / data.Count);
            scale[d] = sd > 1e-6f ? 1 / sd : 0;
        }
        var xs = data.Select(e =>
        {
            var z = new float[dim];
            for (var d = 0; d < dim; d++) z[d] = (e.X[d] - mean[d]) * scale[d];
            return z;
        }).ToArray();
        var ys = data.Select(e => index[e.Label]).ToArray();

        // Class weights so rare classes still matter.
        var counts = new double[k];
        foreach (var y in ys) counts[y]++;
        var classW = counts.Select(c => c > 0 ? data.Count / (k * c) : 0).ToArray();

        var w = new double[k * dim];
        var b = new double[k];
        // Adam optimiser state.
        var mw = new double[w.Length]; var vw = new double[w.Length];
        var mb = new double[k]; var vb = new double[k];
        const double beta1 = 0.9, beta2 = 0.999, eps = 1e-8;
        var step = 0;
        const int batch = 64;
        var order = Enumerable.Range(0, xs.Length).ToArray();
        var rng = new Random(o.Seed);
        var gw = new double[w.Length];
        var gb = new double[k];
        var logits = new double[k];

        for (var epoch = 0; epoch < o.Epochs; epoch++)
        {
            rng.Shuffle(order);
            for (var s = 0; s < order.Length; s += batch)
            {
                Array.Clear(gw);
                Array.Clear(gb);
                var end = Math.Min(order.Length, s + batch);
                double totalWeight = 0;
                for (var j = s; j < end; j++)
                {
                    var i = order[j];
                    var x = xs[i];
                    Softmax(w, b, x, dim, logits);
                    var sw = data[i].Weight * classW[ys[i]];
                    totalWeight += sw;
                    for (var c = 0; c < k; c++)
                    {
                        var g = (logits[c] - (c == ys[i] ? 1 : 0)) * sw;
                        if (g == 0) continue;
                        gb[c] += g;
                        var row = c * dim;
                        for (var d = 0; d < dim; d++) gw[row + d] += g * x[d];
                    }
                }
                step++;
                var lr = o.LearningRate * Math.Sqrt(1 - Math.Pow(beta2, step)) / (1 - Math.Pow(beta1, step));
                var inv = 1 / Math.Max(1e-9, totalWeight);
                for (var p = 0; p < w.Length; p++)
                {
                    var g = gw[p] * inv + o.L2 * w[p];
                    mw[p] = beta1 * mw[p] + (1 - beta1) * g;
                    vw[p] = beta2 * vw[p] + (1 - beta2) * g * g;
                    w[p] -= lr * mw[p] / (Math.Sqrt(vw[p]) + eps);
                }
                for (var c = 0; c < k; c++)
                {
                    var g = gb[c] * inv;
                    mb[c] = beta1 * mb[c] + (1 - beta1) * g;
                    vb[c] = beta2 * vb[c] + (1 - beta2) * g * g;
                    b[c] -= lr * mb[c] / (Math.Sqrt(vb[c]) + eps);
                }
            }
        }
        return new SoundClassifier
        {
            Classes = classes, Mean = mean, Scale = scale, Space = space,
            W = w.Select(v => (float)v).ToArray(), B = b.Select(v => (float)v).ToArray(),
        };
    }

    static void Softmax(double[] w, double[] b, float[] x, int dim, double[] outp)
    {
        var max = double.MinValue;
        for (var c = 0; c < outp.Length; c++)
        {
            var s = b[c];
            var row = c * dim;
            for (var d = 0; d < dim; d++) s += w[row + d] * x[d];
            outp[c] = s;
            if (s > max) max = s;
        }
        double sum = 0;
        for (var c = 0; c < outp.Length; c++) { outp[c] = Math.Exp(outp[c] - max); sum += outp[c]; }
        for (var c = 0; c < outp.Length; c++) outp[c] /= sum;
    }

    /// <summary>Classes with probabilities, most likely first.</summary>
    public IReadOnlyList<(string Label, double P)> Predict(float[] x)
    {
        if (x.Length != Dim) return [];
        var k = Classes.Length;
        var z = new float[Dim];
        for (var d = 0; d < Dim; d++) z[d] = (x[d] - Mean[d]) * Scale[d];
        var p = new double[k];
        var max = double.MinValue;
        for (var c = 0; c < k; c++)
        {
            double s = B[c];
            var row = c * Dim;
            for (var d = 0; d < Dim; d++) s += W[row + d] * z[d];
            p[c] = s;
            max = Math.Max(max, s);
        }
        double sum = 0;
        for (var c = 0; c < k; c++) { p[c] = Math.Exp(p[c] - max); sum += p[c]; }
        return Enumerable.Range(0, k).Select(c => (Classes[c], p[c] / sum)).OrderByDescending(t => t.Item2).ToList();
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static SoundClassifier? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<SoundClassifier>(json); }
        catch (JsonException) { return null; }
    }
}
