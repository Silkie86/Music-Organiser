using System.Collections.Concurrent;

namespace MusicOrganiser.Core.Sounds;

public sealed record SoundProgress(string Stage, int Done, int Total);

public sealed record SimilarSound(Sound Sound, double Score);

/// <summary>
/// The sound organiser: scans your sample folders, analyses every sound, learns
/// your library (sound types and your own tags), and answers questions about it:
/// search by description, sound-alikes, notes and chords, rediscovery and kits.
/// </summary>
public sealed class SoundOrganiser : IDisposable
{
    public const string TypeModelKey = "type_model";
    const string TagModelsKey = "tag_models";

    public SoundLibrary Library { get; }

    List<Sound> _sounds = [];
    Dictionary<long, Sound> _byId = [];
    VectorIndex? _features;
    VectorIndex? _clapIndex;
    ClapModel? _clap;
    NoteTranscriber? _transcriber;
    readonly object _modelLock = new();
    Dictionary<string, float[]>? _typePrompts;

    public SoundOrganiser(SoundLibrary library)
    {
        Library = library;
        Reload();
    }

    public void Dispose()
    {
        _clap?.Dispose();
        _transcriber?.Dispose();
    }

    public IReadOnlyList<Sound> Sounds => _sounds;
    public Sound? Get(long id) => _byId.GetValueOrDefault(id);
    public SoundClassifier? TypeModel => SoundClassifier.FromJson(Library.GetSetting(TypeModelKey));

    /// <summary>True once CLAP embeddings exist for most of the library.</summary>
    public bool HasDeepListening => _clapIndex is { Count: > 0 } c && c.Count >= _sounds.Count(s => s.Analysis is not null) * 0.9;

    public void Reload()
    {
        _sounds = Library.All();
        _byId = _sounds.ToDictionary(s => s.Id);
        _features = VectorIndex.Build(Library.Vectors("features"), standardise: true, ChromaDownweight);
        var clap = Library.Vectors("clap");
        _clapIndex = clap.Count > 0 ? VectorIndex.Build(clap, standardise: false) : null;
        ApplyUsage(_lastUsage);
    }

    /// <summary>Chroma (the last 12 features) says which notes are present; it matters less than timbre for "sounds like".</summary>
    static float[] ChromaDownweight(int dim)
    {
        var w = Enumerable.Repeat(1f, dim).ToArray();
        for (var i = Math.Max(0, dim - 12); i < dim; i++) w[i] = 0.35f;
        return w;
    }

    // ---- models --------------------------------------------------------------------

    public ClapModel? Clap
    {
        get
        {
            lock (_modelLock)
            {
                if (_clap is null && ClapModel.IsInstalled())
                    try { _clap = new ClapModel(); } catch (Exception) { _clap = null; }
                return _clap;
            }
        }
    }

    /// <summary>Drops the loaded CLAP model so a newly downloaded one is picked up.</summary>
    public void ResetClap()
    {
        lock (_modelLock)
        {
            _clap?.Dispose();
            _clap = null;
            _typePrompts = null;
        }
    }

    public NoteTranscriber? Transcriber
    {
        get
        {
            lock (_modelLock)
            {
                if (_transcriber is null && NoteTranscriber.IsAvailable) _transcriber = new NoteTranscriber();
                return _transcriber;
            }
        }
    }

    // ---- usage from your projects -------------------------------------------------------

    IReadOnlyList<(string Path, int Projects)> _lastUsage = [];

    /// <summary>
    /// Counts how many of your projects use each sound. Projects store samples by full
    /// path, relative path or just a file name (Bitwig collects copies), so a full-path
    /// match is used when there is one, otherwise the file name.
    /// </summary>
    public void ApplyUsage(IReadOnlyList<(string Path, int Projects)> usage)
    {
        _lastUsage = usage;
        var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, n) in usage)
        {
            byPath[p] = byPath.GetValueOrDefault(p) + n;
            var name = p.Split('\\', '/')[^1];
            byName[name] = byName.GetValueOrDefault(name) + n;
        }
        foreach (var s in _sounds)
            s.UsedIn = byPath.TryGetValue(s.Path, out var n) ? n : byName.GetValueOrDefault(System.IO.Path.GetFileName(s.Path));
    }

    // ---- scanning ----------------------------------------------------------------------

    public static List<string> FindAudio(IEnumerable<string> folders, CancellationToken ct = default,
                                         ConcurrentBag<string>? unreadable = null) =>
        Parallelism.FindFiles(folders,
            f => AudioFile.CanRead(f) && !System.IO.Path.GetFileName(f).StartsWith("._"),
            (name, _) => name.StartsWith('.') || name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase), ct: ct,
            unreadable: unreadable);

    /// <summary>Analyses new and changed sounds in your sample folders. Fast: signal analysis only.</summary>
    public int Scan(IReadOnlyList<string> folders, IProgress<SoundProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new("Finding sounds", 0, 0));
        var unreadable = new ConcurrentBag<string>();
        var files = FindAudio(folders, ct, unreadable);
        var known = Library.KnownFiles();
        var todo = files.AsParallel().AsOrdered().WithDegreeOfParallelism(Parallelism.Cores).WithCancellation(ct)
            .Select(f =>
            {
                try
                {
                    var fi = new FileInfo(f);
                    var mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                    return !known.TryGetValue(f, out var k) || k.Mtime != mtime || k.Size != fi.Length ? (f, fi.Length, mtime) : default;
                }
                catch (IOException) { return default; }
            })
            .Where(t => t.f is not null).Select(t => (Path: t.f, Size: t.Length, Mtime: t.mtime)).ToList();

        var done = 0;
        var pending = new ConcurrentQueue<SoundLibrary.Analysed>();
        void Flush()
        {
            var batch = new List<SoundLibrary.Analysed>();
            while (pending.TryDequeue(out var a)) batch.Add(a);
            if (batch.Count > 0) Library.Save(batch);
        }
        Parallel.ForEach(todo, Parallelism.Options(ct), t =>
        {
            SoundLibrary.Analysed result;
            try
            {
                var analysis = Parallelism.Background(() => SoundAnalyzer.Analyse(t.Path));
                result = new(t.Path, t.Size, t.Mtime, analysis, null, null, SoundTypes.FromPath(t.Path));
            }
            catch (Exception e)   // one unreadable file must never stop a scan
            {
                result = new(t.Path, t.Size, t.Mtime, null, $"{e.GetType().Name}: {e.Message}", null, SoundTypes.FromPath(t.Path));
            }
            pending.Enqueue(result);
            var n = Interlocked.Increment(ref done);
            if (n % 50 == 0)
            {
                lock (pending) Flush();
                progress?.Report(new("Listening to sounds", n, todo.Count));
            }
        });
        Flush();
        // A folder on an unplugged drive (or one that couldn't be read this time) keeps its sounds,
        // tags and deep listening; only files that are really gone are forgotten. So does a sound this app
        // can't read here but could elsewhere (MP3, FLAC, M4A and OGG need Windows' decoders, which a Mac or
        // Linux doesn't have yet).
        static string Dir(string d) => System.IO.Path.EndsInDirectorySeparator(d) ? d : d + System.IO.Path.DirectorySeparatorChar;
        var keep = folders.Where(f => !Directory.Exists(f)).Concat(unreadable).Select(Dir).ToList();
        var listed = folders.Select(Dir).ToList();
        Library.RemoveMissing(files.ToHashSet(StringComparer.OrdinalIgnoreCase),
            p => keep.Any(k => p.StartsWith(k, StringComparison.OrdinalIgnoreCase)) ||
                 !AudioFile.CanRead(p) && listed.Any(f => p.StartsWith(f, StringComparison.OrdinalIgnoreCase)) && File.Exists(p));
        Reload();
        UpdateTypes(progress);
        return todo.Count;
    }

    /// <summary>
    /// "Deep listening": runs CLAP over every sound that doesn't have an embedding yet.
    /// Slower (a fraction of a second per sound), resumable, and unlocks search by description.
    /// </summary>
    public int DeepListen(IProgress<SoundProgress>? progress = null, CancellationToken ct = default)
    {
        var clap = Clap ?? throw new InvalidOperationException("The sound-understanding model isn't installed yet");
        var missing = _sounds.Where(s => s.Analysis is not null && (_clapIndex is null || !_clapIndex.Contains(s.Id))).ToList();
        var done = 0;
        // Every core reads and prepares audio; the model runs several sounds at once (see ClapModel).
        Parallel.ForEach(missing, Parallelism.Options(ct), s =>
        {
            try
            {
                Parallelism.Background(() => Library.SetClap(s.Id, clap.EmbedAudio(AudioFile.Read(s.Path, 30))));
            }
            catch (Exception) { }   // unreadable now; it'll be retried next time
            var n = Interlocked.Increment(ref done);
            if (n % 10 == 0) progress?.Report(new("Deep listening", n, missing.Count));
        });
        Reload();
        UpdateTypes(progress);
        return missing.Count;
    }

    // ---- learning types and tags ------------------------------------------------------------

    string Space => HasDeepListening ? "clap" : "features";
    VectorIndex? Index => HasDeepListening ? _clapIndex : _features;

    float[]? RawVector(Sound s, string space) =>
        space == "clap" ? _clapIndex?.Raw(s.Id) : s.Analysis?.Features is { Length: > 0 } f ? f : null;

    /// <summary>
    /// Trains the type classifier on your library and refreshes every sound's type.
    /// Your own labels count three times as much as folder-name labels.
    /// </summary>
    public SoundClassifier? UpdateTypes(IProgress<SoundProgress>? progress = null)
    {
        progress?.Report(new("Learning your sounds", 0, 0));
        var space = Space;
        var examples = new List<TrainingExample>();
        foreach (var s in _sounds)
        {
            var label = s.UserType ?? s.NameType;
            if (label is null || RawVector(s, space) is not { } v) continue;
            examples.Add(new(v, label, s.UserType is not null ? 3f : 1f));
        }
        SoundClassifier? model = null;
        if (examples.Count >= 20 && examples.Select(e => e.Label).Distinct().Count() >= 2)
        {
            model = SoundClassifier.Train(examples, space);
            Library.SetSetting(TypeModelKey, model.ToJson());
        }
        else Library.SetSetting(TypeModelKey, null);

        var zeroShot = space == "clap" ? TypePrompts() : null;
        var updates = _sounds.AsParallel().AsOrdered().WithDegreeOfParallelism(Parallelism.Cores).Select(s =>
        {
            var (type, source, conf) = Parallelism.Background(() => ResolveType(s, model, zeroShot, space));
            s.Type = type;
            s.TypeSource = source;
            s.TypeConfidence = conf;
            return (s.Id, type, source, conf);
        }).ToList();
        Library.SetTypes(updates);
        TrainTags();
        return model;
    }

    (string Type, string Source, double Conf) ResolveType(Sound s, SoundClassifier? model, Dictionary<string, float[]>? zeroShot, string space)
    {
        if (s.UserType is { } u) return (u, "you", 1);
        if (s.NameType is { } n) return (n, "name", 1);
        if (RawVector(s, space) is { } v)
        {
            if (model?.Predict(v) is [var top, ..] && top.P >= 0.45) return (top.Label, "learned", Math.Round(top.P, 2));
            if (zeroShot is not null && _clapIndex?.Raw(s.Id) is { } cv && ZeroShot(cv, zeroShot) is { } z && z.P >= 0.5)
                return (z.Label, "model", Math.Round(z.P, 2));
        }
        return (SoundTypes.Unknown, "", 0);
    }

    Dictionary<string, float[]>? TypePrompts()
    {
        if (_typePrompts is not null) return _typePrompts;
        if (Clap is not { } clap) return null;
        return _typePrompts = SoundTypes.Prompts.ToDictionary(p => p.Key, p => clap.EmbedText(p.Value));
    }

    static (string Label, double P)? ZeroShot(float[] audio, Dictionary<string, float[]> prompts)
    {
        // CLAP's learned logit scale is around 30-100; 50 gives sensible probabilities.
        var scores = prompts.Select(p => (p.Key, S: 50 * VectorIndex.Dot(audio, p.Value))).ToList();
        var max = scores.Max(s => s.S);
        var sum = scores.Sum(s => Math.Exp(s.S - max));
        var best = scores.MaxBy(s => s.S);
        return (best.Key, Math.Exp(best.S - max) / sum);
    }

    /// <summary>Sounds where the classifier disagrees with the folder name: worth a listen and a correction.</summary>
    public IReadOnlyList<(Sound Sound, string Thinks, double P)> Disagreements(int max = 100)
    {
        if (TypeModel is not { } model) return [];
        return _sounds.AsParallel().WithDegreeOfParallelism(Parallelism.Cores)
            .Where(s => s.UserType is null && s.NameType is not null && RawVector(s, model.Space) is not null)
            .Select(s => (Sound: s, Top: model.Predict(RawVector(s, model.Space)!) is [var top, ..] ? top : default))
            .Where(t => t.Top.Label is not null && t.Top.Label != t.Sound.NameType && t.Top.P >= 0.7)
            .Select(t => (t.Sound, t.Top.Label, Math.Round(t.Top.P, 2)))
            .OrderByDescending(t => t.Item3).ThenBy(t => t.Sound.Id).Take(max).ToList();
    }

    public void SetUserType(Sound s, string? type)
    {
        Library.SetUserType(s.Id, type);
        s.UserType = type;
        (s.Type, s.TypeSource, s.TypeConfidence) = type is not null ? (type, "you", 1) : (s.NameType ?? SoundTypes.Unknown, s.NameType is null ? "" : "name", 1);
    }

    /// <summary>
    /// For every tag you've given at least five sounds, learns what those sounds have in
    /// common and suggests the tag for other sounds that share it (marked as learned).
    /// </summary>
    public IReadOnlyList<(string Tag, int Suggested, double? Accuracy)> TrainTags()
    {
        var results = new List<(string, int, double?)>();
        var space = Space;
        var rng = new Random(11);
        foreach (var (tag, yours, _) in Library.TagCounts())
        {
            Library.ClearLearnedTags(tag);
            if (yours < 5) continue;
            var positives = Library.SoundsTagged(tag).Select(Get).OfType<Sound>().Where(p => RawVector(p, space) is not null).ToList();
            var posIds = positives.Select(p => p.Id).ToHashSet();
            var unlabelled = _sounds.Where(s => !posIds.Contains(s.Id) && RawVector(s, space) is not null).OrderBy(_ => rng.Next()).ToList();
            if (positives.Count < 5 || unlabelled.Count < 10) continue;

            // Untagged sounds aren't truly "not" the tag, so split them in two: train against
            // one half, suggest within the other, then swap. No sound is judged by a model that
            // was told it's a negative.
            // The two halves are independent, so they train and score at the same time.
            var folds = new (List<(Sound S, double P)> Suggested, double? Accuracy)[2];
            Parallel.For(0, 2, fold =>
            {
                var negPool = unlabelled.Where((_, i) => i % 2 != fold).ToList();
                var toScore = unlabelled.Where((_, i) => i % 2 == fold).ToList();
                var negatives = negPool.Take(Math.Max(20, positives.Count * 4)).ToList();
                var examples = positives.Select(p => new TrainingExample(RawVector(p, space)!, "yes", 1f))
                    .Concat(negatives.Select(n => new TrainingExample(RawVector(n, space)!, "no", 1f))).ToList();
                var model = SoundClassifier.Train(examples, space, new SoundClassifier.Options(Epochs: 60));
                folds[fold] = (toScore.AsParallel().AsOrdered().WithDegreeOfParallelism(Parallelism.Cores)
                    .Select(s => (s, P: model.Predict(RawVector(s, space)!).First(p => p.Label == "yes").P))
                    .Where(t => t.P >= 0.85).ToList(), model.HeldOutAccuracy);
            });
            var suggested = folds[0].Suggested.Concat(folds[1].Suggested).ToList();
            var accuracy = folds[0].Accuracy ?? folds[1].Accuracy;
            suggested = suggested.OrderByDescending(t => t.P).Take(300).ToList();
            foreach (var (s, _) in suggested)
            {
                Library.AddTag(s.Id, tag, "learned");
                if (!s.Tags.Contains(tag)) s.Tags.Add(tag);
            }
            results.Add((tag, suggested.Count, accuracy));
        }
        return results;
    }

    // ---- search ------------------------------------------------------------------------

    /// <summary>Sounds that sound like this one, using CLAP when available and the timbre features otherwise.</summary>
    public IReadOnlyList<SimilarSound> Similar(Sound s, int max = 30, bool sameTypeOnly = false)
    {
        var index = Index;
        if (index is null || !index.Contains(s.Id)) index = _features;
        if (index is null || !index.Contains(s.Id)) return [];
        return index.Nearest(index.Get(s.Id)!, max * 3)
            .Where(h => h.Id != s.Id && _byId.ContainsKey(h.Id))
            .Select(h => new SimilarSound(_byId[h.Id], Math.Round(h.Score, 3)))
            .Where(h => !sameTypeOnly || h.Sound.Type == s.Type)
            .Take(max).ToList();
    }

    /// <summary>Ranks sounds by how well they match a description, e.g. "dark gritty reese bass". Needs deep listening.</summary>
    public IReadOnlyList<SimilarSound> Describe(string text, int max = 200)
    {
        if (_clapIndex is null || Clap is not { } clap || string.IsNullOrWhiteSpace(text)) return [];
        var q = clap.EmbedText(text);
        return _clapIndex.Nearest(q, max, normaliseQuery: false)
            .Where(h => _byId.ContainsKey(h.Id))
            .Select(h => new SimilarSound(_byId[h.Id], Math.Round(h.Score, 3))).ToList();
    }

    /// <summary>Notes (and the chords they make) in a sound, with the Basic Pitch model.</summary>
    public (IReadOnlyList<TranscribedNote> Notes, IReadOnlyList<ChordSpan> Chords) Transcribe(Sound s)
    {
        var t = Transcriber ?? throw new InvalidOperationException("The note-detection model is missing from the app folder");
        var notes = t.Transcribe(AudioFile.Read(s.Path));
        return (notes, ChordsFromNotes(notes));
    }

    /// <summary>Chord names over time from transcribed notes, in quarter-second steps.</summary>
    public static IReadOnlyList<ChordSpan> ChordsFromNotes(IReadOnlyList<TranscribedNote> notes, double step = 0.25)
    {
        if (notes.Count == 0) return [];
        var end = notes.Max(n => n.End);
        var spans = new List<ChordSpan>();
        for (var t = 0.0; t < end; t += step)
        {
            var active = notes.Where(n => n.Start < t + step && n.End > t + step * 0.5).Select(n => n.Pitch).ToList();
            if (active.Distinct().Count() < 2 || MusicTheory.NameChord(active) is not { } chord) continue;
            if (spans.Count > 0 && spans[^1].Name == chord.Name && Math.Abs(spans[^1].End - t) < 1e-6)
                spans[^1] = spans[^1] with { End = Math.Round(t + step, 2) };
            else spans.Add(new ChordSpan(Math.Round(t, 2), Math.Round(t + step, 2), chord.Name));
        }
        return spans.Where(s => s.End - s.Start >= 0.25).ToList();
    }

    internal float[]? NormalisedVector(long id) => Index?.Get(id) ?? _features?.Get(id);
    internal float[]? ClapVector(long id) => _clapIndex?.Get(id);
    internal VectorIndex? ActiveIndex => Index ?? _features;
}

/// <summary>Unit-length vectors held in memory for fast cosine-similarity search.</summary>
public sealed class VectorIndex
{
    readonly Dictionary<long, float[]> _unit = new();
    readonly Dictionary<long, float[]> _raw;

    VectorIndex(Dictionary<long, float[]> raw) => _raw = raw;

    public int Count => _unit.Count;
    public bool Contains(long id) => _unit.ContainsKey(id);
    public float[]? Get(long id) => _unit.GetValueOrDefault(id);
    public float[]? Raw(long id) => _raw.GetValueOrDefault(id);

    public static VectorIndex Build(Dictionary<long, float[]> vectors, bool standardise, Func<int, float[]>? weights = null)
    {
        var index = new VectorIndex(vectors);
        if (vectors.Count == 0) return index;
        var dim = vectors.Values.GroupBy(v => v.Length).MaxBy(g => g.Count())!.Key;
        var mean = new double[dim];
        var sd = new double[dim];
        if (standardise)
        {
            foreach (var v in vectors.Values.Where(v => v.Length == dim)) for (var d = 0; d < dim; d++) mean[d] += v[d];
            for (var d = 0; d < dim; d++) mean[d] /= vectors.Count;
            foreach (var v in vectors.Values.Where(v => v.Length == dim)) for (var d = 0; d < dim; d++) sd[d] += (v[d] - mean[d]) * (v[d] - mean[d]);
            for (var d = 0; d < dim; d++) sd[d] = Math.Sqrt(sd[d] / vectors.Count);
        }
        var w = weights?.Invoke(dim);
        foreach (var (id, v) in vectors)
        {
            if (v.Length != dim) continue;
            var u = new float[dim];
            for (var d = 0; d < dim; d++)
            {
                var x = standardise ? (sd[d] > 1e-9 ? (v[d] - mean[d]) / sd[d] : 0) : v[d];
                u[d] = (float)(x * (w?[d] ?? 1));
            }
            index._unit[id] = Unit(u);
        }
        return index;
    }

    public static float[] Unit(float[] v)
    {
        var n = Math.Sqrt(v.Sum(x => (double)x * x));
        if (n > 0) for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / n);
        return v;
    }

    public static double Dot(float[] a, float[] b)
    {
        double s = 0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }

    public IReadOnlyList<(long Id, double Score)> Nearest(float[] query, int max, bool normaliseQuery = true)
    {
        var q = normaliseQuery ? Unit((float[])query.Clone()) : query;
        return _unit.Select(kv => (kv.Key, Dot(q, kv.Value))).OrderByDescending(t => t.Item2).Take(max).ToList();
    }
}
