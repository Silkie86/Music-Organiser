using System.Collections.Concurrent;
using SoundFingerprinting;
using SoundFingerprinting.Builder;
using SoundFingerprinting.Data;
using SoundFingerprinting.InMemory;

namespace MusicOrganiser.Core.Sounds;

/// <summary>
/// One sound in a group of copies. <see cref="Kind"/> says how it's a copy of the one to keep (null for that one).
/// <see cref="Same"/>: it's the same sound as the one to keep (an exact copy, or the same audio in another file),
/// not just very like it; only these are offered for hiding.
/// </summary>
public sealed record DuplicateMember(Sound Sound, long Size, bool Keep, CopyKind? Kind, string Why, bool Same)
{
    public string Format => System.IO.Path.GetExtension(Sound.Path).TrimStart('.').ToUpperInvariant();
}

/// <summary>
/// Sounds that are copies of each other, the suggested one to keep first. <see cref="SpareBytes"/>: the space the
/// copies that are the same sound take (very similar and trimmed ones may differ on purpose, so they don't count).
/// </summary>
public sealed record DuplicateGroup(IReadOnlyList<DuplicateMember> Members, CopyKind Kind, long SpareBytes)
{
    /// <summary>How many of the copies are the same sound as the one to keep.</summary>
    public int SameCopies => Members.Count(m => !m.Keep && m.Same);
    /// <summary>Whether some copies are only very similar (or trimmed), not the same sound.</summary>
    public bool HasSimilar => Members.Any(m => !m.Keep && !m.Same);
}

/// <summary>
/// Finds sounds in your library that are copies of each other, even renamed, converted or trimmed. It never moves
/// or deletes anything; it only lists them.
/// <para>
/// Each sound gets a <see cref="SoundPrint"/> once (kept in sounds.db, made again only when its file changes).
/// Copies are then found three ways: equal audio hashes; close band prints confirmed by comparing waveforms; and,
/// for sounds of 2 seconds or more, SoundFingerprinting, which also finds a sound inside a longer one.
/// </para>
/// </summary>
public static class DuplicateFinder
{
    /// <summary>Band prints at least this alike are compared waveform to waveform.</summary>
    public const double PrintThreshold = 0.95;
    /// <summary>The waveform sketches must be at least this alike before the files are read to compare them properly.</summary>
    public const double SketchThreshold = 0.8;
    /// <summary>Waveforms at least this alike (a correlation, after lining up their starts) are the same sound.</summary>
    public const double WaveThreshold = 0.95;
    /// <summary>
    /// Converted copies are the same sound when their whole sounds are at most this far apart, in dB (see
    /// <see cref="SoundPrint.SpectralDistance"/>); otherwise they're only very similar. An MP3 or similar
    /// shifts the sound a little (up to about 1 dB at 128 kbit/s), so it gets <see cref="SameDbLossy"/>.
    /// </summary>
    public const double SameDb = 0.5, SameDbLossy = 1.2;
    /// <summary>SoundFingerprinting must find this share of the shorter sound in the longer one.</summary>
    public const double CoverThreshold = 0.7;
    /// <summary>Buckets up to this size are compared all against all; bigger ones only along their line.</summary>
    const int SmallBucket = 48;
    /// <summary>How many neighbours along the line each sound in a crowded bucket is compared with.</summary>
    const int Neighbours = 12;
    /// <summary>How many of a sound's closest lookalikes get their waveforms compared.</summary>
    const int MaxChecksPerSound = 8;

    /// <summary>Prints new and changed sounds and finds their copies. Returns how many sounds were printed.</summary>
    public static int Update(SoundOrganiser org, IProgress<SoundProgress>? progress = null, CancellationToken ct = default)
    {
        var lib = org.Library;
        var todo = lib.SoundsToPrint();
        if (todo.Count > 0)
        {
            lib.ForgetPrints(todo.Select(t => t.Id));
            PrintAndFingerprint(org, todo, progress, ct);
        }
        CompareLikelyCopies(org, progress, ct);
        CheckSameness(org, progress, ct);
        return todo.Count;
    }

    // ---- printing, and SoundFingerprinting look-ups as each sound is printed ------------------------

    sealed record Printed(long Id, long Mtime, long Size, SoundPrint? Print, AVHashes? Query);

    static void PrintAndFingerprint(SoundOrganiser org, IReadOnlyList<(long Id, string Path, long Mtime, long Size)> todo,
                                    IProgress<SoundProgress>? progress, CancellationToken ct)
    {
        var lib = org.Library;
        progress?.Report(new("Getting ready", 0, 0));
        var store = new InMemoryModelService();
        var stored = 0;
        lib.ForEachSfp((id, blob) =>
        {
            ct.ThrowIfCancellationRequested();
            store.Insert(new TrackInfo(id.ToString(), "", ""), new AVHashes(SoundPrint.Unpack(blob), null));
            stored++;
        });
        var exact = lib.PrintKeys().Where(k => k.Exact is not null).ToDictionary(k => k.Id, k => Convert.ToHexString(k.Exact!));
        var seconds = lib.PrintKeys().ToDictionary(k => k.Id, k => k.Seconds);

        using var queue = new BlockingCollection<Printed>(256);
        var reader = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(todo, Parallelism.Options(ct), t =>
                {
                    Printed p;
                    try
                    {
                        var (print, query) = Parallelism.Background(() =>
                        {
                            var made = SoundPrint.Make(AudioFile.Read(t.Path, SoundPrint.ReadSeconds), out var q);
                            return (made, q);
                        });
                        p = new Printed(t.Id, t.Mtime, t.Size, print, query);
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { p = new Printed(t.Id, t.Mtime, t.Size, null, null); }
                    queue.Add(p, ct);
                });
            }
            finally { queue.CompleteAdding(); }
        }, ct);

        var done = 0;
        var batch = new List<Printed>();
        foreach (var p in queue.GetConsumingEnumerable(ct))
        {
            batch.Add(p);
            if (batch.Count < 64 && queue.Count > 0) continue;
            if (batch.Count < 64 && !queue.IsAddingCompleted && batch.Count < 16) continue;
            Flush();
        }
        Flush();
        reader.GetAwaiter().GetResult();

        void Flush()
        {
            if (batch.Count == 0) return;
            foreach (var b in batch.Where(b => b.Print is not null))
            {
                exact[b.Id] = Convert.ToHexString(b.Print!.Exact);
                seconds[b.Id] = b.Print.Seconds;
            }
            var matches = new ConcurrentBag<SoundMatch>();
            var withSfp = batch.Where(b => b.Query is not null && b.Print?.Sfp is not null).ToList();
            if (withSfp.Count > 0)
            {
                // Each against everything printed before, then against the rest of its own batch.
                var own = new InMemoryModelService();
                foreach (var b in withSfp) own.Insert(new TrackInfo(b.Id.ToString(), "", ""), new AVHashes(SoundPrint.Unpack(b.Print!.Sfp!), null));
                Parallel.ForEach(withSfp, Parallelism.Options(ct), b =>
                {
                    if (stored > 0) Look(b, store, matches);
                    Look(b, own, matches);
                });
                foreach (var b in withSfp) store.Insert(new TrackInfo(b.Id.ToString(), "", ""), new AVHashes(SoundPrint.Unpack(b.Print!.Sfp!), null));
                stored += withSfp.Count;
            }
            lib.SavePrints(batch.Select(b => (b.Id, b.Mtime, b.Size, b.Print)), matches);
            done += batch.Count;
            batch.Clear();
            progress?.Report(new("Reading sounds", done, todo.Count));
        }

        void Look(Printed q, IModelService service, ConcurrentBag<SoundMatch> found)
        {
            var result = QueryCommandBuilder.Instance.BuildQueryCommand()
                .From(q.Query!)
                .WithQueryConfig(c => { c.Audio.MaxTracksToReturn = 50; return c; })
                .UsingServices(service)
                .Query().GetAwaiter().GetResult();
            foreach (var e in result.ResultEntries)
            {
                if (e.Audio is not { } r || !long.TryParse(r.Track.Id, out var other) || other == q.Id) continue;
                if (exact.TryGetValue(other, out var h) && h == Convert.ToHexString(q.Print!.Exact)) continue;   // exact copies are found by hash
                var c = r.Coverage;
                var qs = q.Print!.Seconds;
                var ts = seconds.GetValueOrDefault(other, r.Track.Length);
                var queryShorter = qs <= ts;
                var cover = queryShorter ? c.QueryRelativeCoverage : c.TrackRelativeCoverage;
                if (cover < CoverThreshold) continue;
                var similarLength = SameLength(qs, ts, org.Get(q.Id)?.Path, org.Get(other)?.Path);
                var kind = similarLength && c.QueryRelativeCoverage >= 0.85 && c.TrackRelativeCoverage >= 0.85 ? CopyKind.Converted : CopyKind.Trimmed;
                var offset = queryShorter ? r.TrackMatchStartsAt - r.QueryMatchStartsAt : r.QueryMatchStartsAt - r.TrackMatchStartsAt;
                found.Add(new SoundMatch(Math.Min(q.Id, other), Math.Max(q.Id, other), kind, Math.Min(1, cover), Math.Max(0, offset)));
            }
        }
    }

    // ---- likely copies from the band prints, confirmed by their waveforms ---------------------------

    static void CompareLikelyCopies(SoundOrganiser org, IProgress<SoundProgress>? progress, CancellationToken ct)
    {
        var lib = org.Library;
        var keys = lib.PrintKeys();
        if (keys.All(k => k.Compared)) return;
        progress?.Report(new("Looking for likely copies", 0, 0));
        var known = lib.Matches().Select(m => (m.A, m.B)).ToHashSet();
        var buckets = new Dictionary<int, List<int>>();
        for (var i = 0; i < keys.Count; i++)
            for (var t = 0; t < Math.Min(SoundPrint.Tables, keys[i].Keys.Length); t++)
            {
                var key = (t << 16) | keys[i].Keys[t];
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
                list.Add(i);
            }
        var pairs = new HashSet<(long, long)>();
        void Consider(int x, int y)
        {
            var (a, b) = (keys[x], keys[y]);
            if (a.Compared && b.Compared) return;
            if (a.Exact is not null && b.Exact is not null && a.Exact.AsSpan().SequenceEqual(b.Exact)) return;
            var pair = a.Id < b.Id ? (a.Id, b.Id) : (b.Id, a.Id);
            if (!known.Contains(pair)) pairs.Add(pair);
        }
        foreach (var (key, list) in buckets)
        {
            ct.ThrowIfCancellationRequested();
            if (list.Count < 2) continue;
            if (list.Count <= SmallBucket)
            {
                for (var x = 0; x < list.Count; x++)
                    for (var y = x + 1; y < list.Count; y++) Consider(list[x], list[y]);
                continue;
            }
            // A crowded bucket (lots of lookalikes): only neighbours in its line are compared.
            var t = key >> 16;
            var line = list.OrderBy(i => keys[i].Keys.Length > SoundPrint.Tables + t ? keys[i].Keys[SoundPrint.Tables + t] : 0).ToList();
            for (var x = 0; x < line.Count; x++)
                for (var y = x + 1; y < Math.Min(line.Count, x + 1 + Neighbours); y++) Consider(line[x], line[y]);
        }

        var seconds = keys.ToDictionary(k => k.Id, k => k.Seconds);
        var bands = lib.PrintBands(pairs.SelectMany(p => new[] { p.Item1, p.Item2 }));
        // Lookalikes (a pack's worth of similar hi-hats) can be many, and each costs two file reads to rule out,
        // so each sound has only its closest few checked. Real copies are always among the closest.
        var close = pairs.AsParallel().WithCancellation(ct)
            .Select<(long, long), ((long, long) Pair, double Sim)>(p =>
            {
                if (!bands.TryGetValue(p.Item1, out var a) || !bands.TryGetValue(p.Item2, out var b)) return (p, 0.0);
                var sim = SoundPrint.Similarity(a.Frames, a.Bands, b.Frames, b.Bands);
                if (sim < PrintThreshold) return (p, 0.0);
                return SoundPrint.WaveSimilarity(SoundPrint.FromSketch(a.Sketch), SoundPrint.FromSketch(b.Sketch)) >= SketchThreshold ? (p, sim) : (p, 0.0);
            })
            .Where(t => t.Sim > 0).ToList();
        var closest = close.SelectMany(t => new[] { (Id: t.Pair.Item1, t.Pair, t.Sim), (Id: t.Pair.Item2, t.Pair, t.Sim) })
            .GroupBy(t => t.Id)
            .SelectMany(g => g.OrderByDescending(t => t.Sim).Take(MaxChecksPerSound).Select(t => t.Pair))
            .ToHashSet();
        var likely = closest.ToList();
        var found = new ConcurrentBag<SoundMatch>();
        var done = 0;
        Parallel.ForEach(likely, Parallelism.Options(ct), p =>
        {
            if (org.Get(p.Item1) is { } sa && org.Get(p.Item2) is { } sb &&
                Parallelism.Background(() => WaveSimilarity(sa.Path, sb.Path)) is var w && w >= WaveThreshold)
            {
                var (da, db) = (seconds[p.Item1], seconds[p.Item2]);
                var kind = SameLength(da, db, sa.Path, sb.Path) ? CopyKind.Converted : CopyKind.Trimmed;
                found.Add(new SoundMatch(p.Item1, p.Item2, kind, w, 0));
            }
            var n = Interlocked.Increment(ref done);
            if (n % 20 == 0) progress?.Report(new("Comparing likely copies", n, likely.Count));
        });
        lib.SaveMatches(found);
        lib.MarkCompared();
    }

    // ---- same sound, or only very similar ----------------------------------------------------------

    /// <summary>Compares the whole sounds of converted copies, so the ones that are only very similar aren't offered for hiding.</summary>
    static void CheckSameness(SoundOrganiser org, IProgress<SoundProgress>? progress, CancellationToken ct)
    {
        var todo = org.Library.SamenessToCheck();
        if (todo.Count == 0) return;
        var done = 0;
        foreach (var chunk in todo.Chunk(256))
        {
            var checks = new ConcurrentBag<(long, long, double)>();
            Parallel.ForEach(chunk, Parallelism.Options(ct), p =>
            {
                var apart = org.Get(p.A) is { } a && org.Get(p.B) is { } b ? Parallelism.Background(() => HowFarApart(a.Path, b.Path)) : Unreadable;
                checks.Add((p.A, p.B, apart));
            });
            org.Library.SaveSameness(checks);
            done += chunk.Length;
            progress?.Report(new("Checking which copies are the same sound", done, todo.Count));
        }
    }

    /// <summary>What's saved for a pair whose files can't be read: as far apart as can be.</summary>
    const double Unreadable = 99;
    const int SameRate = 44100;

    /// <summary>How far apart two files sound, in dB, over their first <see cref="SoundPrint.ReadSeconds"/> seconds.</summary>
    public static double HowFarApart(string a, string b)
    {
        try { return Math.Min(Unreadable, SoundPrint.SpectralDistance(Wave(a), Wave(b), SameRate)); }
        catch (Exception) { return Unreadable; }
        static float[] Wave(string path)
        {
            // Dsp.Resample is slower than the prints' resampler but exact, which the highs need here.
            var x = AudioFile.Read(path, SoundPrint.ReadSeconds);
            var mono = x.SampleRate == SameRate ? x.Mono() : Dsp.Resample(x.Mono(), x.SampleRate, SameRate);
            return SoundPrint.WaveStart(mono, SameRate, SoundPrint.ReadSeconds, SameRate);
        }
    }

    static bool IsSame(SoundMatch m, string? pathA, string? pathB) =>
        m.Kind == CopyKind.Converted && m.Apart is { } apart &&
        apart <= (Lossy.Contains(System.IO.Path.GetExtension(pathA ?? "")) || Lossy.Contains(System.IO.Path.GetExtension(pathB ?? "")) ? SameDbLossy : SameDb);

    static readonly HashSet<string> Lossy = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wma" };

    /// <summary>
    /// Whether two copies are the same length, give or take what a conversion adds: a few milliseconds, or up to
    /// 60 ms when one is an MP3 or similar (their encoders pad the start and end).
    /// </summary>
    static bool SameLength(double a, double b, string? pathA, string? pathB)
    {
        var lossy = Lossy.Contains(System.IO.Path.GetExtension(pathA ?? "")) || Lossy.Contains(System.IO.Path.GetExtension(pathB ?? ""));
        return Math.Abs(a - b) <= (lossy ? 0.06 : Math.Max(0.01, 0.005 * Math.Max(a, b)));
    }

    /// <summary>How alike two sounds' waveforms are over their first second (see <see cref="SoundPrint.WaveSimilarity"/>).</summary>
    public static double WaveSimilarity(string a, string b)
    {
        try { return SoundPrint.WaveSimilarity(Start(a), Start(b)); }
        catch (Exception) { return 0; }
        static float[] Start(string path) { var x = AudioFile.Read(path, 3); return SoundPrint.WaveStart(x.Mono(), x.SampleRate, 1); }
    }

    // ---- groups ---------------------------------------------------------------------------------

    static readonly HashSet<string> Lossless = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".aif", ".aiff", ".aifc", ".flac" };

    /// <summary>The copies found so far, grouped, biggest saving first.</summary>
    public static IReadOnlyList<DuplicateGroup> Groups(SoundOrganiser org)
    {
        var lib = org.Library;
        var matches = lib.Matches();
        var exact = lib.ExactCopies();
        var parent = new Dictionary<long, long>();
        long Find(long x)
        {
            if (!parent.TryGetValue(x, out var p)) return parent[x] = x;
            return p == x ? x : parent[x] = Find(p);
        }
        void Join(long a, long b) { var (ra, rb) = (Find(a), Find(b)); if (ra != rb) parent[ra] = rb; }

        // Sounds that are the same sound as each other: exact copies, and converted copies whose whole sounds match.
        var sameParent = new Dictionary<long, long>();
        long Root(long x)
        {
            if (!sameParent.TryGetValue(x, out var p)) return sameParent[x] = x;
            return p == x ? x : sameParent[x] = Root(p);
        }
        void Same(long a, long b) { var (ra, rb) = (Root(a), Root(b)); if (ra != rb) sameParent[ra] = rb; }

        var hashOf = exact.ToDictionary(e => e.Id, e => e.Hash);
        foreach (var g in exact.GroupBy(e => e.Hash))
            foreach (var e in g.Skip(1)) { Join(g.First().Id, e.Id); Same(g.First().Id, e.Id); }
        var links = new Dictionary<long, List<(long Other, CopyKind Kind)>>();
        foreach (var m in matches)
        {
            Join(m.A, m.B);
            if (IsSame(m, org.Get(m.A)?.Path, org.Get(m.B)?.Path)) Same(m.A, m.B);
            (links.TryGetValue(m.A, out var la) ? la : links[m.A] = []).Add((m.B, m.Kind));
            (links.TryGetValue(m.B, out var lb) ? lb : links[m.B] = []).Add((m.A, m.Kind));
        }

        var sizes = lib.Sizes();
        var groups = new List<DuplicateGroup>();
        foreach (var ids in parent.Keys.GroupBy(Find))
        {
            var sounds = ids.Select(org.Get).OfType<Sound>().ToList();
            if (sounds.Count < 2) continue;
            var sameSize = sounds.GroupBy(s => Root(s.Id)).ToDictionary(g => g.Key, g => g.Count());
            var keep = sounds.OrderBy(s => s.Hidden).ThenByDescending(s => s.UsedIn)
                .ThenByDescending(s => sameSize[Root(s.Id)] > 1)   // one of the copies that are the same sound, so the others can go
                .ThenByDescending(s => Lossless.Contains(System.IO.Path.GetExtension(s.Path)))
                .ThenByDescending(s => Math.Round(s.Seconds, 2))
                .ThenBy(s => s.Added)
                .ThenByDescending(s => Math.Round(s.Analysis?.PeakDb ?? -99))   // a turned-down copy is rarely the original
                .ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
                .First();
            CopyKind KindOf(Sound s)
            {
                if (hashOf.TryGetValue(s.Id, out var h) && hashOf.GetValueOrDefault(keep.Id) == h) return CopyKind.Exact;
                var own = links.GetValueOrDefault(s.Id) ?? [];
                if (own.FirstOrDefault(l => l.Other == keep.Id) is { Other: > 0 } direct) return direct.Kind;
                return own.Count > 0 ? own.Max(l => l.Kind) : CopyKind.Exact;
            }
            var keepRoot = Root(keep.Id);
            var members = new List<DuplicateMember> { new(keep, sizes.GetValueOrDefault(keep.Id), true, null, KeepReason(keep, sounds), true) };
            members.AddRange(sounds.Where(s => s != keep)
                .Select(s => (Sound: s, Kind: KindOf(s), Same: Root(s.Id) == keepRoot))
                .Select(t => t.Same && t.Kind == CopyKind.Trimmed ? t with { Kind = CopyKind.Converted } : t)   // same via another copy
                .OrderByDescending(t => t.Same).ThenBy(t => t.Kind).ThenBy(t => t.Sound.Path, StringComparer.OrdinalIgnoreCase)
                .Select(t => new DuplicateMember(t.Sound, sizes.GetValueOrDefault(t.Sound.Id), false, t.Kind, Describe(t.Kind, t.Same, t.Sound, keep), t.Same)));
            var spare = members.Where(m => !m.Keep && m.Same).Sum(m => m.Size);
            groups.Add(new DuplicateGroup(members, members.Skip(1).Max(m => m.Kind!.Value), spare));
        }
        return groups.OrderByDescending(g => g.SpareBytes).ThenByDescending(g => g.Members.Count).ToList();
    }

    static string KeepReason(Sound keep, List<Sound> all)
    {
        if (keep.UsedIn > 0 && all.Count(s => s.UsedIn > 0) == 1)
            return keep.UsedIn == 1 ? "Best to keep: your projects use this one" : $"Best to keep: {keep.UsedIn} of your projects use this one";
        if (keep.UsedIn > 0) return "Best to keep: your projects use it most";
        if (Lossless.Contains(System.IO.Path.GetExtension(keep.Path)) && all.Any(s => !Lossless.Contains(System.IO.Path.GetExtension(s.Path))))
            return "Best to keep: full quality, not compressed";
        if (all.Any(s => s.Seconds < keep.Seconds - 0.05)) return "Best to keep: the full-length one";
        return "Best to keep: the oldest copy";
    }

    static string Describe(CopyKind kind, bool same, Sound s, Sound keep) => kind switch
    {
        CopyKind.Exact when same => s.Name == keep.Name ? "Exact copy" : "Exact copy, renamed",
        CopyKind.Converted when same => Changes(s, keep) is { Length: > 0 } c ? $"Same sound, {c}" : "Same sound, another file",
        CopyKind.Exact or CopyKind.Converted => "Very similar, but not the same sound all the way through",
        _ => s.Seconds < keep.Seconds ? $"Trimmed: {s.Seconds:0.##} s of {keep.Seconds:0.##} s" : $"Longer version ({s.Seconds:0.##} s)",
    };

    static string Changes(Sound s, Sound keep)
    {
        var bits = new List<string>();
        if (!System.IO.Path.GetExtension(s.Path).Equals(System.IO.Path.GetExtension(keep.Path), StringComparison.OrdinalIgnoreCase))
            bits.Add($"converted to {System.IO.Path.GetExtension(s.Path).TrimStart('.').ToUpperInvariant()}");
        if (s.Analysis is { } a && keep.Analysis is { } k)
        {
            if (a.SampleRate != k.SampleRate) bits.Add($"{a.SampleRate / 1000.0:0.#} kHz");
            if (Math.Abs(a.PeakDb - k.PeakDb) >= 1) bits.Add(a.PeakDb > k.PeakDb ? "louder" : "quieter");
        }
        if (!s.Name.Equals(keep.Name, StringComparison.OrdinalIgnoreCase)) bits.Add("renamed");
        return string.Join(", ", bits);
    }
}
