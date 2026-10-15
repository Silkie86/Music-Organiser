namespace MusicOrganiser.Core.Sounds;

/// <summary>Where a sound sits on the galaxy map: 0..1 across and down.</summary>
public readonly record struct StarPoint(float X, float Y);

/// <summary>
/// Lays the sound galaxy out: every sound becomes a point on a flat map, and sounds that sound alike end up close
/// together. It works like UMAP (the method XLN's XO is known for), cut down to what a sample library needs:
/// <list type="number">
/// <item>Squash each sound's fingerprint (CLAP's 512 numbers, or the measured features) to its 32 most telling
/// directions (PCA), which keeps "what sounds like what" but makes the next step fast.</item>
/// <item>Find each sound's 15 nearest sound-alikes.</item>
/// <item>Start from the PCA picture, then let close sound-alikes pull together and random pairs push apart for a few
/// hundred rounds, until the clusters settle.</item>
/// </list>
/// The same library always gives the same map (a fixed seed), so the galaxy doesn't rearrange itself between opens.
/// It's worked out once and saved; new sounds are then placed beside their nearest sound-alikes (<see cref="PlaceNew"/>).
/// </summary>
public static class GalaxyMap
{
    public const int Neighbours = 15;
    const int Dims = 32;
    const int NegativeSamples = 5;
    // UMAP's curve for min_dist 0.5: close neighbours keep a little room, so a family of sounds reads as a cloud of
    // separate stars rather than one blob.
    const double A = 0.583, B = 1.334;
    // How much the final picture is evened out (see Layout), 0 to 1.
    const double Even = 0.35;

    public static Dictionary<long, StarPoint> Layout(IReadOnlyDictionary<long, float[]> unitVectors, IProgress<SoundProgress>? progress = null,
                                                     CancellationToken ct = default, int seed = 42)
    {
        var ids = unitVectors.Keys.OrderBy(i => i).ToArray();
        var n = ids.Length;
        var result = new Dictionary<long, StarPoint>();
        if (n == 0) return result;
        if (n < 3)
        {
            for (var i = 0; i < n; i++) result[ids[i]] = new StarPoint(0.3f + 0.4f * i, 0.5f);
            return result;
        }
        var rng = new Random(seed);

        progress?.Report(new SoundProgress("Finding what each sound is like", 0, 3));
        var (reduced, init) = Pca(ids.Select(i => unitVectors[i]).ToArray(), Math.Min(Dims, unitVectors[ids[0]].Length), rng, ct);

        progress?.Report(new SoundProgress("Finding sound-alikes", 1, 3));
        var k = Math.Min(Neighbours, n - 1);
        var (nbr, dist) = NearestNeighbours(reduced, k, ct);
        var edges = Edges(nbr, dist);

        progress?.Report(new SoundProgress("Letting the galaxy settle", 2, 3));
        var pos = init;
        Optimise(pos, edges, n, n > 10_000 ? 200 : 400, rng, ct);

        // Even it out a little: blend each star's place with its rank across and down, so tight families open up and
        // the galaxy fills its frame, while the order (what's left of what, what's above what) stays the same.
        foreach (var axis in new[] { 0, 1 })
        {
            var order = Enumerable.Range(0, n).OrderBy(i => pos[i, axis]).ToArray();
            double lo = pos[order[0], axis], hi = pos[order[^1], axis];
            var range = Math.Max(hi - lo, 1e-9);
            for (var r = 0; r < n; r++)
            {
                var i = order[r];
                pos[i, axis] = (1 - Even) * (pos[i, axis] - lo) / range + Even * r / (n - 1.0);
            }
        }

        // Fit into 0..1, keeping the shape (one scale for both directions), with a margin for the labels.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (var i = 0; i < n; i++)
        {
            minX = Math.Min(minX, pos[i, 0]); maxX = Math.Max(maxX, pos[i, 0]);
            minY = Math.Min(minY, pos[i, 1]); maxY = Math.Max(maxY, pos[i, 1]);
        }
        var span = Math.Max(Math.Max(maxX - minX, maxY - minY), 1e-9);
        var offX = (1 - (maxX - minX) / span) / 2;
        var offY = (1 - (maxY - minY) / span) / 2;
        for (var i = 0; i < n; i++)
            result[ids[i]] = new StarPoint((float)(0.07 + 0.86 * (offX + (pos[i, 0] - minX) / span)),
                                           (float)(0.07 + 0.86 * (offY + (pos[i, 1] - minY) / span)));
        progress?.Report(new SoundProgress("Galaxy mapped", 3, 3));
        return result;
    }

    /// <summary>
    /// Places sounds the map doesn't have yet (new since it was made) at the middle of their 3 nearest mapped
    /// sound-alikes, nudged a little so they don't sit exactly on top of one.
    /// </summary>
    public static Dictionary<long, StarPoint> PlaceNew(IReadOnlyDictionary<long, float[]> unitVectors, IReadOnlyDictionary<long, StarPoint> map)
    {
        var placed = new Dictionary<long, StarPoint>();
        var mapped = map.Keys.Where(unitVectors.ContainsKey).ToArray();
        if (mapped.Length == 0) return placed;
        foreach (var (id, v) in unitVectors)
        {
            if (map.ContainsKey(id)) continue;
            var near = mapped.Select(m => (m, s: VectorIndex.Dot(v, unitVectors[m]))).OrderByDescending(t => t.s).Take(3).ToList();
            var x = near.Average(t => map[t.m].X);
            var y = near.Average(t => map[t.m].Y);
            var jitter = new Random((int)(id % int.MaxValue));
            placed[id] = new StarPoint((float)Math.Clamp(x + (jitter.NextDouble() - 0.5) * 0.01, 0, 1),
                                       (float)Math.Clamp(y + (jitter.NextDouble() - 0.5) * 0.01, 0, 1));
        }
        return placed;
    }

    /// <summary>The top <paramref name="dims"/> principal directions (power iteration), and a 2D starting layout from the first two.</summary>
    static (float[][] Reduced, double[,] Init) Pca(float[][] x, int dims, Random rng, CancellationToken ct)
    {
        var n = x.Length;
        var d = x[0].Length;
        var mean = new double[d];
        foreach (var v in x) for (var j = 0; j < d; j++) mean[j] += v[j];
        for (var j = 0; j < d; j++) mean[j] /= n;

        // The covariance matrix, a row per worker.
        var cov = new double[d, d];
        Parallel.For(0, d, new ParallelOptions { CancellationToken = ct }, a =>
        {
            for (var b = a; b < d; b++)
            {
                double s = 0;
                foreach (var v in x) s += (v[a] - mean[a]) * (v[b] - mean[b]);
                cov[a, b] = cov[b, a] = s / n;
            }
        });

        var comps = new List<double[]>();
        for (var c = 0; c < dims; c++)
        {
            ct.ThrowIfCancellationRequested();
            var w = new double[d];
            for (var j = 0; j < d; j++) w[j] = rng.NextDouble() - 0.5;
            for (var it = 0; it < 60; it++)
            {
                var next = new double[d];
                for (var a = 0; a < d; a++)
                {
                    double s = 0;
                    for (var b = 0; b < d; b++) s += cov[a, b] * w[b];
                    next[a] = s;
                }
                foreach (var p in comps)   // keep it at right angles to the directions already found
                {
                    var dot = 0.0;
                    for (var j = 0; j < d; j++) dot += next[j] * p[j];
                    for (var j = 0; j < d; j++) next[j] -= dot * p[j];
                }
                var norm = Math.Sqrt(next.Sum(t => t * t));
                if (norm < 1e-12) break;
                for (var j = 0; j < d; j++) next[j] /= norm;
                w = next;
            }
            if (w.All(t => t == 0)) break;
            comps.Add(w);
        }

        var reduced = new float[n][];
        var init = new double[n, 2];
        Parallel.For(0, n, i =>
        {
            var r = new float[comps.Count];
            for (var c = 0; c < comps.Count; c++)
            {
                double s = 0;
                for (var j = 0; j < d; j++) s += (x[i][j] - mean[j]) * comps[c][j];
                r[c] = (float)s;
            }
            reduced[i] = r;
            init[i, 0] = r.Length > 0 ? r[0] : 0;
            init[i, 1] = r.Length > 1 ? r[1] : 0;
        });
        // Scale the start to about ±10 (UMAP's usual range), with a whisker of noise so identical sounds don't stack.
        var max = 1e-9;
        for (var i = 0; i < n; i++) max = Math.Max(max, Math.Max(Math.Abs(init[i, 0]), Math.Abs(init[i, 1])));
        for (var i = 0; i < n; i++)
        {
            init[i, 0] = init[i, 0] / max * 10 + (rng.NextDouble() - 0.5) * 0.01;
            init[i, 1] = init[i, 1] / max * 10 + (rng.NextDouble() - 0.5) * 0.01;
        }
        return (reduced, init);
    }

    static (int[][] Nbr, double[][] Dist) NearestNeighbours(float[][] x, int k, CancellationToken ct)
    {
        var n = x.Length;
        var nbr = new int[n][];
        var dist = new double[n][];
        Parallel.For(0, n, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var best = new PriorityQueue<int, double>();   // the k closest so far; the farthest of them on top
            for (var j = 0; j < n; j++)
            {
                if (j == i) continue;
                double s = 0;
                var a = x[i];
                var b = x[j];
                for (var c = 0; c < a.Length; c++) { var t = a[c] - b[c]; s += t * t; }
                if (best.Count < k) best.Enqueue(j, -s);
                else if (best.TryPeek(out _, out var worst) && -worst > s) { best.Dequeue(); best.Enqueue(j, -s); }
            }
            var list = new List<(int J, double D)>();
            while (best.TryDequeue(out var j, out var p)) list.Add((j, Math.Sqrt(-p)));
            list.Reverse();
            nbr[i] = list.Select(t => t.J).ToArray();
            dist[i] = list.Select(t => t.D).ToArray();
        });
        return (nbr, dist);
    }

    /// <summary>How strongly each pair of neighbours belongs together (UMAP's fuzzy weights, made symmetric).</summary>
    static List<(int I, int J, double W)> Edges(int[][] nbr, double[][] dist)
    {
        var n = nbr.Length;
        var w = new Dictionary<(int, int), double>();
        var target = Math.Log2(Math.Max(2, nbr[0].Length));
        for (var i = 0; i < n; i++)
        {
            var rho = dist[i].Length > 0 ? dist[i][0] : 0;
            // Find the width that makes the weights add up to log2(k), by halving.
            double lo = 1e-6, hi = 1e3, sigma = 1;
            for (var it = 0; it < 48; it++)
            {
                sigma = (lo + hi) / 2;
                var sum = dist[i].Sum(dd => Math.Exp(-Math.Max(0, dd - rho) / sigma));
                if (sum > target) hi = sigma; else lo = sigma;
            }
            for (var t = 0; t < nbr[i].Length; t++)
            {
                var j = nbr[i][t];
                var key = i < j ? (i, j) : (j, i);
                var v = Math.Exp(-Math.Max(0, dist[i][t] - rho) / sigma);
                w[key] = w.TryGetValue(key, out var old) ? old + v - old * v : v;
            }
        }
        return w.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).OrderBy(e => e.Item1).ThenBy(e => e.Item2).ToList();
    }

    static void Optimise(double[,] pos, List<(int I, int J, double W)> edges, int n, int epochs, Random rng, CancellationToken ct)
    {
        var maxW = edges.Count == 0 ? 1 : edges.Max(e => e.W);
        for (var epoch = 0; epoch < epochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            var alpha = 1.0 - (double)epoch / epochs;
            foreach (var (i, j, w) in edges)
            {
                // Stronger pairs get a pull more often, like UMAP's edge sampling.
                if (rng.NextDouble() > w / maxW) continue;
                double dx = pos[i, 0] - pos[j, 0], dy = pos[i, 1] - pos[j, 1];
                var d2 = dx * dx + dy * dy;
                if (d2 > 0)
                {
                    var g = -2 * A * B * Math.Pow(d2, B - 1) / (1 + A * Math.Pow(d2, B));
                    var gx = Clip(g * dx) * alpha;
                    var gy = Clip(g * dy) * alpha;
                    pos[i, 0] += gx; pos[i, 1] += gy;
                    pos[j, 0] -= gx; pos[j, 1] -= gy;
                }
                for (var s = 0; s < NegativeSamples; s++)
                {
                    var k = rng.Next(n);
                    if (k == i) continue;
                    dx = pos[i, 0] - pos[k, 0];
                    dy = pos[i, 1] - pos[k, 1];
                    d2 = dx * dx + dy * dy;
                    var g = 2 * B / ((0.001 + d2) * (1 + A * Math.Pow(d2, B)));
                    pos[i, 0] += Clip(g * dx) * alpha;
                    pos[i, 1] += Clip(g * dy) * alpha;
                }
            }
        }
    }

    static double Clip(double v) => Math.Clamp(v, -4, 4);
}
