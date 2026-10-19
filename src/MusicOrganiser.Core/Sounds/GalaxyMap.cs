namespace MusicOrganiser.Core.Sounds;

/// <summary>Where a sound sits on the galaxy map: 0..1 across and down.</summary>
public readonly record struct StarPoint(float X, float Y);

/// <summary>
/// Lays the sound galaxy out: every sound becomes a point on a flat map, and sounds that sound alike end up close
/// together. It works like UMAP (the method XLN's XO is known for), cut down to what a sample library needs and built
/// to cope with hundreds of thousands of sounds:
/// <list type="number">
/// <item>Squash each sound's fingerprint (CLAP's 512 numbers, the measured features, or both) to its 32 most telling
/// directions (PCA, learned from a sample of at most 20,000 sounds), which keeps "what sounds like what" but makes the
/// next step fast.</item>
/// <item>Find each sound's 15 nearest sound-alikes: exactly for small libraries, and for big ones by "neighbours of
/// neighbours" (NN-descent, as UMAP does), which gets almost all of them right in a fraction of the time.</item>
/// <item>Start from the PCA picture, then let close sound-alikes pull together and random pairs push apart for a few
/// hundred rounds, until the clusters settle.</item>
/// </list>
/// A small library always gives the same map (a fixed seed); a big one is laid out on every core at once, so it can
/// differ slightly between layouts. Either way it's worked out once and saved; new sounds are then placed beside their
/// nearest sound-alikes (<see cref="PlaceNew"/>), so the stars you know don't move.
/// </summary>
public static class GalaxyMap
{
    public const int Neighbours = 15;
    /// <summary>Libraries smaller than this get exact neighbours and a single-threaded (repeatable) layout.</summary>
    public const int ExactBelow = 6000;
    const int Dims = 32;
    const int PcaSample = 20_000;
    const int NegativeSamples = 5;
    // UMAP's curve for min_dist 0.5: close neighbours keep a little room, so a family of sounds reads as a cloud of
    // separate stars rather than one blob.
    const double A = 0.583, B = 1.334;
    // How much the final picture is evened out (see Layout), 0 to 1.
    const double Even = 0.35;

    public static Dictionary<long, StarPoint> Layout(IReadOnlyDictionary<long, float[]> vectors, IProgress<SoundProgress>? progress = null,
                                                     CancellationToken ct = default, int seed = 42, int exactBelow = ExactBelow) =>
        Layout(vectors.Keys.ToList(), id => vectors[id], progress, ct, seed, exactBelow);

    /// <summary>
    /// Lays out the sounds <paramref name="ids"/>, fetching each fingerprint from <paramref name="vector"/> when it's
    /// needed (so a big library's fingerprints are never all held twice). <paramref name="vector"/> must be safe to
    /// call from several threads.
    /// </summary>
    public static Dictionary<long, StarPoint> Layout(IReadOnlyList<long> ids, Func<long, float[]> vector, IProgress<SoundProgress>? progress = null,
                                                     CancellationToken ct = default, int seed = 42, int exactBelow = ExactBelow)
    {
        var order = ids.Distinct().Order().ToArray();
        var n = order.Length;
        var result = new Dictionary<long, StarPoint>(n);
        if (n == 0) return result;
        if (n < 3)
        {
            for (var i = 0; i < n; i++) result[order[i]] = new StarPoint(0.3f + 0.4f * i, 0.5f);
            return result;
        }
        var rng = new Random(seed);
        var exact = n < exactBelow;

        progress?.Report(new SoundProgress("Finding what each sound is like", 0, 4));
        var (reduced, pos) = Pca(order, vector, rng, ct);

        progress?.Report(new SoundProgress("Finding sound-alikes", 1, 4));
        var k = Math.Min(Neighbours, n - 1);
        var (nbr, dist) = exact ? ExactNeighbours(reduced, k, ct) : DescentNeighbours(reduced, k, seed, ct);

        progress?.Report(new SoundProgress("Weighing sound-alikes", 2, 4));
        var edges = Edges(nbr, dist);

        progress?.Report(new SoundProgress("Letting the galaxy settle", 3, 4));
        var epochs = n <= 10_000 ? 400 : n <= 100_000 ? 200 : 120;
        if (exact) Optimise(pos, edges, 0, edges.Length, n, epochs, rng, ct);
        else OptimiseParallel(pos, edges, n, epochs, seed, ct);

        Finish(pos, n);
        for (var i = 0; i < n; i++) result[order[i]] = new StarPoint((float)pos[2 * i], (float)pos[2 * i + 1]);
        progress?.Report(new SoundProgress("Galaxy mapped", 4, 4));
        return result;
    }

    /// <summary>
    /// Evens the picture out a little (each star's place blended with its rank across and down, so tight families open
    /// up and the galaxy fills its frame, while what's left of what and what's above what stays the same), then fits it
    /// into 0..1 with one scale for both directions and a margin for the labels.
    /// </summary>
    static void Finish(double[] pos, int n)
    {
        // Evening out, the same in every direction: each star keeps its direction from the middle, but its distance
        // moves part of the way to where it would be if the stars were spread evenly over a disc. (Doing it across
        // and down separately squared the clouds off.)
        double mx = 0, my = 0;
        for (var i = 0; i < n; i++) { mx += pos[2 * i]; my += pos[2 * i + 1]; }
        mx /= n;
        my /= n;
        var dist = new double[n];
        for (var i = 0; i < n; i++) dist[i] = Math.Sqrt((pos[2 * i] - mx) * (pos[2 * i] - mx) + (pos[2 * i + 1] - my) * (pos[2 * i + 1] - my));
        var order = Enumerable.Range(0, n).ToArray();
        var keys = (double[])dist.Clone();
        Array.Sort(keys, order);
        var far = Math.Max(keys[^1], 1e-9);
        for (var r = 0; r < n; r++)
        {
            var i = order[r];
            if (dist[i] < 1e-12) continue;
            var even = Math.Sqrt((r + 0.5) / n) * far;
            var k = ((1 - Even) * dist[i] + Even * even) / dist[i];
            pos[2 * i] = mx + (pos[2 * i] - mx) * k;
            pos[2 * i + 1] = my + (pos[2 * i + 1] - my) * k;
        }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (var i = 0; i < n; i++)
        {
            minX = Math.Min(minX, pos[2 * i]); maxX = Math.Max(maxX, pos[2 * i]);
            minY = Math.Min(minY, pos[2 * i + 1]); maxY = Math.Max(maxY, pos[2 * i + 1]);
        }
        var span = Math.Max(Math.Max(maxX - minX, maxY - minY), 1e-9);
        var offX = (1 - (maxX - minX) / span) / 2;
        var offY = (1 - (maxY - minY) / span) / 2;
        for (var i = 0; i < n; i++)
        {
            pos[2 * i] = 0.07 + 0.86 * (offX + (pos[2 * i] - minX) / span);
            pos[2 * i + 1] = 0.07 + 0.86 * (offY + (pos[2 * i + 1] - minY) / span);
        }
    }

    /// <summary>
    /// Places sounds the map doesn't have yet (new since it was made) at the middle of their 3 nearest mapped
    /// sound-alikes, nudged a little so they don't sit exactly on top of one. In a big library they're compared with a
    /// sample of 10,000 mapped sounds, which is plenty to find their neighbourhood.
    /// </summary>
    public static Dictionary<long, StarPoint> PlaceNew(IReadOnlyDictionary<long, float[]> vectors, IReadOnlyDictionary<long, StarPoint> map) =>
        PlaceNew(vectors.Keys.Where(id => !map.ContainsKey(id)).ToList(), map.Keys.Where(vectors.ContainsKey).ToList(), id => vectors[id], map);

    public static Dictionary<long, StarPoint> PlaceNew(IReadOnlyList<long> newIds, IReadOnlyList<long> mappedIds, Func<long, float[]> vector,
                                                       IReadOnlyDictionary<long, StarPoint> map)
    {
        var placed = new System.Collections.Concurrent.ConcurrentDictionary<long, StarPoint>();
        if (mappedIds.Count == 0 || newIds.Count == 0) return new();
        var sample = mappedIds.Count <= 10_000 ? mappedIds.ToArray() : mappedIds.OrderBy(id => Hash(id)).Take(10_000).ToArray();
        var sampleVectors = sample.Select(vector).ToArray();
        For(newIds.Count, default, i =>
        {
            var id = newIds[i];
            var v = vector(id);
            var best = new (double S, long Id)[3];
            for (var b = 0; b < 3; b++) best[b] = (double.MinValue, -1);
            for (var m = 0; m < sample.Length; m++)
            {
                var s = VectorIndex.Dot(v, sampleVectors[m]);
                if (s <= best[2].S) continue;
                best[2] = (s, sample[m]);
                Array.Sort(best, (p, q) => q.S.CompareTo(p.S));
            }
            var near = best.Where(b => b.Id >= 0).ToList();
            var x = near.Average(t => map[t.Id].X);
            var y = near.Average(t => map[t.Id].Y);
            var jitter = new Random((int)(id % int.MaxValue));
            placed[id] = new StarPoint((float)Math.Clamp(x + (jitter.NextDouble() - 0.5) * 0.004, 0, 1),
                                       (float)Math.Clamp(y + (jitter.NextDouble() - 0.5) * 0.004, 0, 1));
        });
        return new Dictionary<long, StarPoint>(placed);
    }

    /// <summary>
    /// A parallel loop that keeps to the app's rule for background work (<see cref="Parallelism"/>): at most
    /// <see cref="Parallelism.Workers"/> threads, below normal priority, so the window and the galaxy itself stay
    /// smooth while the map is worked out. The work goes in chunks, so setting the priority costs nothing much.
    /// </summary>
    static void For(int n, CancellationToken ct, Action<int> body)
    {
        if (n <= 0) return;
        var chunk = Math.Max(1, n / Math.Max(1, Parallelism.Workers * 8));
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, n, chunk), Parallelism.Options(ct), r =>
            Parallelism.Background(() =>
            {
                for (var i = r.Item1; i < r.Item2; i++) body(i);
            }));
    }

    static double Dot(float[] a, float[] b)
    {
        var w = System.Numerics.Vector<float>.Count;
        var acc = System.Numerics.Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - w; i += w)
            acc += new System.Numerics.Vector<float>(a, i) * new System.Numerics.Vector<float>(b, i);
        double s = System.Numerics.Vector.Sum(acc);
        for (; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    static ulong Hash(long id)
    {
        var x = (ulong)id * 0x9E3779B97F4A7C15UL;
        return x ^ (x >> 31);
    }

    /// <summary>
    /// The top 32 principal directions (learned from up to 20,000 sounds), every sound projected onto them, and a 2D
    /// starting layout from the first two, as x,y pairs.
    /// </summary>
    static (float[][] Reduced, double[] Init) Pca(long[] ids, Func<long, float[]> vector, Random rng, CancellationToken ct)
    {
        var n = ids.Length;
        var sampleIds = n <= PcaSample ? ids : ids.OrderBy(Hash).Take(PcaSample).ToArray();
        var x = sampleIds.Select(vector).ToArray();
        var d = x.Max(v => v.Length);
        var dims = Math.Min(Dims, d);
        var mean = new double[d];
        foreach (var v in x) for (var j = 0; j < v.Length; j++) mean[j] += v[j];
        for (var j = 0; j < d; j++) mean[j] /= x.Length;

        // The covariance matrix. Each measurement's centred values sit in one row so a pair is a straight dot product.
        var cols = new float[d][];
        For(d, ct, a =>
        {
            var col = new float[x.Length];
            for (var i = 0; i < x.Length; i++) col[i] = (float)(At(x[i], a) - mean[a]);
            cols[a] = col;
        });
        var cov = new double[d, d];
        For(d, ct, a =>
        {
            for (var b = a; b < d; b++) cov[a, b] = cov[b, a] = Dot(cols[a], cols[b]) / x.Length;
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
                For(d, ct, a =>
                {
                    double s = 0;
                    for (var b = 0; b < d; b++) s += cov[a, b] * w[b];
                    next[a] = s;
                });
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
        var init = new double[2 * n];
        For(n, ct, i =>
        {
            var v = vector(ids[i]);
            var r = new float[comps.Count];
            for (var c = 0; c < comps.Count; c++)
            {
                double s = 0;
                var comp = comps[c];
                for (var j = 0; j < v.Length && j < d; j++) s += (v[j] - mean[j]) * comp[j];
                r[c] = (float)s;
            }
            reduced[i] = r;
            init[2 * i] = r.Length > 0 ? r[0] : 0;
            init[2 * i + 1] = r.Length > 1 ? r[1] : 0;
        });
        // Scale the start to about ±10 (UMAP's usual range), with a whisker of noise so identical sounds don't stack.
        var max = 1e-9;
        for (var i = 0; i < 2 * n; i++) max = Math.Max(max, Math.Abs(init[i]));
        // A big library starts spread wider: every star wants about the same room, and squeezed into UMAP's usual
        // ±10 a few hundred thousand of them can only line up in threads.
        var room = 10 * Math.Max(1, Math.Sqrt(n / 10_000.0));
        for (var i = 0; i < 2 * n; i++) init[i] = init[i] / max * room + (rng.NextDouble() - 0.5) * 0.01;
        return (reduced, init);
    }

    static float At(float[] v, int i) => i < v.Length ? v[i] : 0;

    static float Distance2(float[] a, float[] b)
    {
        float s = 0;
        for (var c = 0; c < a.Length; c++) { var t = a[c] - b[c]; s += t * t; }
        return s;
    }

    static (int[][] Nbr, float[][] Dist) ExactNeighbours(float[][] x, int k, CancellationToken ct)
    {
        var n = x.Length;
        var nbr = new int[n][];
        var dist = new float[n][];
        For(n, ct, i =>
        {
            var best = new KBest(k);
            for (var j = 0; j < n; j++)
                if (j != i) best.Offer(j, Distance2(x[i], x[j]));
            (nbr[i], dist[i]) = best.Sorted();
        });
        return (nbr, dist);
    }

    /// <summary>
    /// NN-descent: start each sound with likely neighbours (its neighbours along a few random directions), then
    /// repeatedly check its neighbours' neighbours, keeping the closest. Each round only a sound's own list changes, so
    /// every core can work at once. Stops when a round barely improves anything.
    /// </summary>
    static (int[][] Nbr, float[][] Dist) DescentNeighbours(float[][] x, int k, int seed, CancellationToken ct)
    {
        var n = x.Length;
        var best = new KBest[n];
        for (var i = 0; i < n; i++) best[i] = new KBest(k);

        // Start: neighbours in sorted order along 8 random directions.
        var rng = new Random(seed);
        for (var p = 0; p < 8; p++)
        {
            ct.ThrowIfCancellationRequested();
            var dir = Enumerable.Range(0, x[0].Length).Select(_ => (float)(rng.NextDouble() - 0.5)).ToArray();
            var keys = new float[n];
            For(n, ct, i =>
            {
                float s = 0;
                for (var c = 0; c < dir.Length; c++) s += x[i][c] * dir[c];
                keys[i] = s;
            });
            var idx = Enumerable.Range(0, n).ToArray();
            Array.Sort(keys, idx);
            var window = Math.Max(2, k / 2);
            For(n, ct, r =>
            {
                var i = idx[r];
                for (var o = -window; o <= window; o++)
                    if (o != 0 && r + o >= 0 && r + o < n) best[i].Offer(idx[r + o], Distance2(x[i], x[idx[r + o]]));
            });
        }

        for (var round = 0; round < 10; round++)
        {
            ct.ThrowIfCancellationRequested();
            // Each sound's candidates: its neighbours, plus up to 10 sounds that list it as theirs.
            var lists = best.Select(b => b.Items()).ToArray();
            var reverse = new List<int>[n];
            for (var i = 0; i < n; i++) reverse[i] = [];
            for (var i = 0; i < n; i++)
                foreach (var j in lists[i])
                    if (reverse[j].Count < 10) reverse[j].Add(i);
            var cands = new int[n][];
            for (var i = 0; i < n; i++) cands[i] = lists[i].Concat(reverse[i]).Distinct().ToArray();

            var changed = 0;
            For(n, ct, i =>
            {
                var local = 0;
                foreach (var u in cands[i])
                    foreach (var v in cands[u])
                        if (v != i && best[i].Offer(v, Distance2(x[i], x[v]))) local++;
                if (local > 0) Interlocked.Add(ref changed, local);
            });
            if (changed < n * k / 500) break;
        }

        var nbr = new int[n][];
        var dist = new float[n][];
        For(n, ct, i => (nbr[i], dist[i]) = best[i].Sorted());
        return (nbr, dist);
    }

    /// <summary>The k closest seen so far (squared distances), no repeats.</summary>
    sealed class KBest(int k)
    {
        readonly int[] _ids = new int[k];
        readonly float[] _d = new float[k];
        int _count;
        int _worst;   // index of the farthest kept

        public bool Offer(int id, float d2)
        {
            if (_count == k && d2 >= _d[_worst]) return false;
            for (var t = 0; t < _count; t++) if (_ids[t] == id) return false;
            if (_count < k)
            {
                _ids[_count] = id;
                _d[_count] = d2;
                _count++;
            }
            else
            {
                _ids[_worst] = id;
                _d[_worst] = d2;
            }
            _worst = 0;
            for (var t = 1; t < _count; t++) if (_d[t] > _d[_worst]) _worst = t;
            return true;
        }

        public int[] Items() => _ids[.._count];

        public (int[] Ids, float[] Dist) Sorted()
        {
            var ids = _ids[.._count];
            var d = _d[.._count];
            Array.Sort(d, ids);
            for (var t = 0; t < d.Length; t++) d[t] = MathF.Sqrt(d[t]);
            return (ids, d);
        }
    }

    readonly record struct Edge(int I, int J, float W);

    /// <summary>How strongly each pair of neighbours belongs together (UMAP's fuzzy weights, made symmetric).</summary>
    static Edge[] Edges(int[][] nbr, float[][] dist)
    {
        var n = nbr.Length;
        var directed = new (long Key, float W)[nbr.Sum(a => a.Length)];
        var offsets = new int[n + 1];
        for (var i = 0; i < n; i++) offsets[i + 1] = offsets[i] + nbr[i].Length;
        var target = Math.Log2(Math.Max(2, nbr.Max(a => a.Length)));
        For(n, default, i =>
        {
            var rho = dist[i].Length > 0 ? dist[i][0] : 0;
            // Find the width that makes the weights add up to log2(k), by halving.
            double lo = 1e-6, hi = 1e3, sigma = 1;
            for (var it = 0; it < 48; it++)
            {
                sigma = (lo + hi) / 2;
                double sum = 0;
                foreach (var dd in dist[i]) sum += Math.Exp(-Math.Max(0, dd - rho) / sigma);
                if (sum > target) hi = sigma; else lo = sigma;
            }
            for (var t = 0; t < nbr[i].Length; t++)
            {
                var j = nbr[i][t];
                var (a, b) = i < j ? (i, j) : (j, i);
                directed[offsets[i] + t] = ((long)a * n + b, (float)Math.Exp(-Math.Max(0, dist[i][t] - rho) / sigma));
            }
        });
        // The same pair from both ends joins as a + b - ab.
        Array.Sort(directed, (p, q) => p.Key.CompareTo(q.Key));
        var edges = new List<Edge>(directed.Length);
        for (var t = 0; t < directed.Length; t++)
        {
            var (key, w) = directed[t];
            if (t + 1 < directed.Length && directed[t + 1].Key == key)
            {
                var v = directed[++t].W;
                w = w + v - w * v;
            }
            edges.Add(new Edge((int)(key / n), (int)(key % n), w));
        }
        return edges.ToArray();
    }

    static void Optimise(double[] pos, Edge[] edges, int from, int to, int n, int epochs, Random rng, CancellationToken ct)
    {
        var maxW = MaxWeight(edges);
        for (var epoch = 0; epoch < epochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            Epoch(pos, edges, from, to, n, 1.0 - (double)epoch / epochs, maxW, rng);
        }
    }

    /// <summary>
    /// The same rounds on every core: each core takes a slice of the pairs. Cores sometimes nudge the same star at the
    /// same moment; like UMAP, that's allowed, since each nudge is tiny.
    /// </summary>
    static void OptimiseParallel(double[] pos, Edge[] edges, int n, int epochs, int seed, CancellationToken ct)
    {
        var maxW = MaxWeight(edges);
        var slices = Math.Max(1, Parallelism.Workers * 4);
        var size = (edges.Length + slices - 1) / slices;
        for (var epoch = 0; epoch < epochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            var alpha = 1.0 - (double)epoch / epochs;
            var e = epoch;
            For(slices, ct, s =>
                Epoch(pos, edges, s * size, Math.Min(edges.Length, (s + 1) * size), n, alpha, maxW, new Random(seed + e * 7919 + s)));
        }
    }

    static float MaxWeight(Edge[] edges)
    {
        var m = 1e-6f;
        foreach (var e in edges) m = Math.Max(m, e.W);
        return m;
    }

    static void Epoch(double[] pos, Edge[] edges, int from, int to, int n, double alpha, float maxW, Random rng)
    {
        for (var t = from; t < to; t++)
        {
            var (i, j, w) = edges[t];
            // Stronger pairs get a pull more often, like UMAP's edge sampling.
            if (rng.NextDouble() > w / maxW) continue;
            double dx = pos[2 * i] - pos[2 * j], dy = pos[2 * i + 1] - pos[2 * j + 1];
            var d2 = dx * dx + dy * dy;
            if (d2 > 0)
            {
                var pb = Math.Pow(d2, B - 1);
                var g = -2 * A * B * pb / (1 + A * pb * d2);
                var gx = Clip(g * dx) * alpha;
                var gy = Clip(g * dy) * alpha;
                pos[2 * i] += gx; pos[2 * i + 1] += gy;
                pos[2 * j] -= gx; pos[2 * j + 1] -= gy;
            }
            for (var s = 0; s < NegativeSamples; s++)
            {
                var k = rng.Next(n);
                if (k == i) continue;
                dx = pos[2 * i] - pos[2 * k];
                dy = pos[2 * i + 1] - pos[2 * k + 1];
                d2 = dx * dx + dy * dy;
                var g = 2 * B / ((0.001 + d2) * (1 + A * Math.Pow(d2, B)));
                pos[2 * i] += Clip(g * dx) * alpha;
                pos[2 * i + 1] += Clip(g * dy) * alpha;
            }
        }
    }

    static double Clip(double v) => Math.Clamp(v, -4, 4);
}
