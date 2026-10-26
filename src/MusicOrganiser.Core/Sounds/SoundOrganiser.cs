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
    List<Sound> _visible = [];
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

    /// <summary>Your sounds, less the copies you've hidden on the Duplicates tab.</summary>
    public IReadOnlyList<Sound> Sounds => _visible;
    /// <summary>Every sound, hidden copies included.</summary>
    public IReadOnlyList<Sound> Everything => _sounds;
    public Sound? Get(long id) => _byId.GetValueOrDefault(id);
    public SoundClassifier? TypeModel => SoundClassifier.FromJson(Library.GetSetting(TypeModelKey));

    /// <summary>True once CLAP embeddings exist for most of the library.</summary>
    /// <remarks>
    /// Worked out when the sounds are read in, not each time it's asked: it was asked once per sound while picking
    /// (Rediscover, kits), and counting 357,000 sounds each time made Shuffle run for ever.
    /// </remarks>
    public bool HasDeepListening { get; private set; }

    public void Reload()
    {
        // Everything is built first and swapped in together, so a search running meanwhile (Rediscover during Deep
        // listen) sees either the old library or the new one, not half of each.
        var sounds = Library.All();
        var byId = sounds.ToDictionary(s => s.Id);
        var hidden = Library.HiddenIds();
        foreach (var s in sounds) s.Hidden = hidden.Contains(s.Id);
        var visible = hidden.Count == 0 ? sounds : sounds.Where(s => !s.Hidden).ToList();
        var features = VectorIndex.Build(Library.Vectors("features"), standardise: true, ChromaDownweight);
        var clap = Library.Vectors("clap");
        var clapIndex = clap.Count > 0 ? VectorIndex.Build(clap, standardise: false) : null;
        var deep = clapIndex is { Count: > 0 } c && c.Count >= sounds.Count(s => s.Analysis is not null) * 0.9;
        ApplyUsage(sounds, _lastUsage);
        (_sounds, _byId, _visible, _features, _clapIndex, HasDeepListening) = (sounds, byId, visible, features, clapIndex, deep);
    }

    /// <summary>
    /// Hides sounds from (or shows them in) the lists after <see cref="SoundLibrary.Hide"/> or Unhide, without reading
    /// the whole library again: with 357,000 sounds a full <see cref="Reload"/> holds things up for seconds.
    /// </summary>
    public void SetHidden(IEnumerable<long> ids, bool hidden)
    {
        foreach (var id in ids)
            if (_byId.TryGetValue(id, out var s)) s.Hidden = hidden;
        _visible = _sounds.Any(s => s.Hidden) ? _sounds.Where(s => !s.Hidden).ToList() : _sounds;
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

    IReadOnlyList<(string Path, int Projects, int Recent, int[] Years)> _lastUsage = [];

    public void ApplyUsage(IReadOnlyList<(string Path, int Projects)> usage) =>
        ApplyUsage(usage.Select(u => (u.Path, u.Projects, u.Projects, Array.Empty<int>())).ToList());

    public void ApplyUsage(IReadOnlyList<(string Path, int Projects, int Recent)> usage) =>
        ApplyUsage(usage.Select(u => (u.Path, u.Projects, u.Recent, Array.Empty<int>())).ToList());

    /// <summary>
    /// Counts how many of your projects use each sound, how many of those were saved in the last year, and the years
    /// they were saved in (for the "Used in" filter). Projects
    /// store samples by full path, relative path or just a file name (Bitwig collects copies), so a full-path match is
    /// used when there is one, otherwise the file name, but only a name no other sound in the library has: thousands
    /// of packs have a "01.wav", and each one was credited with every project using any of them.
    /// </summary>
    public void ApplyUsage(IReadOnlyList<(string Path, int Projects, int Recent, int[] Years)> usage) => ApplyUsage(_sounds, usage);

    void ApplyUsage(List<Sound> sounds, IReadOnlyList<(string Path, int Projects, int Recent, int[] Years)> usage)
    {
        _lastUsage = usage;
        var byPath = new Dictionary<string, (int All, int Recent, int[] Years)>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, (int All, int Recent, int[] Years)>(StringComparer.OrdinalIgnoreCase);
        static (int, int, int[]) Add((int All, int Recent, int[]? Years) a, int n, int r, int[] y) =>
            (a.All + n, a.Recent + r, a.Years is not { Length: > 0 } had ? y : y.Length == 0 ? had : had.Union(y).Order().ToArray());
        foreach (var (p, n, r, y) in usage)
        {
            byPath[p] = Add(byPath.GetValueOrDefault(p), n, r, y);
            var name = p.Split('\\', '/')[^1];
            byName[name] = Add(byName.GetValueOrDefault(name), n, r, y);
        }
        var shared = sounds.GroupBy(s => System.IO.Path.GetFileName(s.Path), StringComparer.OrdinalIgnoreCase)
                           .Where(g => g.Skip(1).Any()).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sounds)
        {
            var name = System.IO.Path.GetFileName(s.Path);
            var (all, recent, years) = byPath.TryGetValue(s.Path, out var u) ? u
                : shared.Contains(name) ? default : byName.GetValueOrDefault(name);
            (s.UsedIn, s.UsedInRecently, s.UsedInYears) = (all, recent, years ?? []);
        }
    }

    // ---- scanning ----------------------------------------------------------------------

    public static List<string> FindAudio(IEnumerable<string> folders, CancellationToken ct = default,
                                         ConcurrentBag<string>? unreadable = null) =>
        FindAudioStamps(folders, ct, unreadable).Select(f => f.Path).ToList();

    /// <summary>The sounds in these folders, with the size and modified time the folder listing gave.</summary>
    public static List<Parallelism.FoundFile> FindAudioStamps(IEnumerable<string> folders, CancellationToken ct = default,
                                                              ConcurrentBag<string>? unreadable = null) =>
        Parallelism.FindFileStamps(folders,
            f => AudioFile.CanRead(f) && !Parallelism.IsMacShadow(f),
            (name, _) => Parallelism.IsHiddenFolder(name), ct: ct,
            unreadable: unreadable);

    /// <summary>Analyses new and changed sounds in your sample folders. Fast: signal analysis only.</summary>
    public int Scan(IReadOnlyList<string> folders, IProgress<SoundProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new("Finding sounds", 0, 0));
        var unreadable = new ConcurrentBag<string>();
        var found = FindAudioStamps(folders, ct, unreadable);
        var files = found.Select(f => f.Path).ToList();
        var known = Library.KnownFiles();
        // The folder listing already says each file's size and modified time, so 357,000 unchanged sounds cost no
        // extra trips to the disk.
        var todo = found.Where(f => !known.TryGetValue(f.Path, out var k) || k.Mtime != f.Mtime || k.Size != f.Size)
                        .Select(f => (f.Path, f.Size, f.Mtime)).ToList();

        // A folder on an unplugged drive (or one that couldn't be read this time) keeps its sounds,
        // tags and deep listening; only files that are really gone are forgotten. So does a sound this app
        // can't read here but could elsewhere (MP3, FLAC, M4A and OGG need Windows' decoders, which a Mac or
        // Linux doesn't have yet).
        static string Dir(string d) => System.IO.Path.EndsInDirectorySeparator(d) ? d : d + System.IO.Path.DirectorySeparatorChar;
        var keep = folders.Where(f => !Directory.Exists(f)).Concat(unreadable).Select(Dir).ToList();
        var listed = folders.Select(Dir).ToList();
        var present = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Kept(string p) => keep.Any(k => p.StartsWith(k, StringComparison.OrdinalIgnoreCase)) ||
                               !AudioFile.CanRead(p) && listed.Any(f => p.StartsWith(f, StringComparison.OrdinalIgnoreCase)) && File.Exists(p);

        // Files renamed or moved (a pack's folder renamed, say) carry on as the sounds they were, with everything
        // you gave them, and aren't listened to again.
        var moves = Moves(known.Where(k => !present.Contains(k.Key) && !Kept(k.Key)).Select(k => (k.Key, k.Value.Size, k.Value.Mtime)),
                          todo.Where(t => !known.ContainsKey(t.Path)));
        var moved = Library.Move(moves);
        if (moves.Count > 0)
        {
            var arrived = moves.Select(m => m.To).ToHashSet(StringComparer.OrdinalIgnoreCase);
            todo = todo.Where(t => !arrived.Contains(t.Path)).ToList();
        }

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
        var removed = Library.RemoveMissing(present, Kept);
        // Nothing new, moved or gone: the sounds in memory are still right, so they aren't read in again (with
        // 357,000 sounds that takes about 10 s, and the Sounds page would rebuild its list for nothing).
        if (todo.Count > 0 || removed > 0 || moved > 0 || _sounds.Count == 0) Reload();
        UpdateTypes(progress);
        return todo.Count;
    }

    /// <summary>
    /// Brings the library up to date with sound files the folder watcher saw change (<see cref="MusicOrganiser.Core.Library.FolderWatch"/>):
    /// new or changed ones are listened to, gone ones forgotten, and nothing else in the folders is looked at. Returns
    /// how many changed.
    /// </summary>
    public int Update(IEnumerable<string> paths, IReadOnlyList<string> folders, IProgress<SoundProgress>? progress = null,
                      CancellationToken ct = default)
    {
        static string Dir(string d) => System.IO.Path.EndsInDirectorySeparator(d) ? d : d + System.IO.Path.DirectorySeparatorChar;
        var roots = folders.Select(Dir).ToList();
        bool Listed(string path)
        {
            // As the scan finds them: under a sample folder, not in a hidden or __MACOSX folder, not a "._" shadow.
            if (roots.FirstOrDefault(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase)) is not { } root) return false;
            var parts = path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 && !Parallelism.IsMacShadow(path) && AudioFile.CanRead(path) &&
                   !parts[..^1].Any(Parallelism.IsHiddenFolder);
        }
        var todo = new List<(string Path, long Size, long Mtime)>();
        var gone = new List<string>();
        var goneKnown = new List<(string Path, long Size, long Mtime)>();
        var added = new List<(string Path, long Size, long Mtime)>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(Listed))
        {
            try
            {
                var fi = new FileInfo(path);
                var known = Library.KnownFile(path);
                if (!fi.Exists)
                {
                    gone.Add(path);
                    if (known is { } was) goneKnown.Add((path, was.Size, was.Mtime));
                    continue;
                }
                var mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                if (known is { } k && k.Mtime == mtime && k.Size == fi.Length) continue;
                todo.Add((path, fi.Length, mtime));
                if (known is null) added.Add((path, fi.Length, mtime));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // still being copied: the next change gets it
        }
        // A rename reports the old name gone and the new one there: it carries on as the sound it was.
        var moves = Moves(goneKnown, added);
        var moved = Library.Move(moves);
        if (moves.Count > 0)
        {
            var from = moves.Select(m => m.From).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var to = moves.Select(m => m.To).ToHashSet(StringComparer.OrdinalIgnoreCase);
            gone.RemoveAll(from.Contains);
            todo.RemoveAll(t => to.Contains(t.Path));
        }
        var results = new ConcurrentBag<SoundLibrary.Analysed>();
        var done = 0;
        Parallel.ForEach(todo, Parallelism.Options(ct), t =>
        {
            try
            {
                var analysis = Parallelism.Background(() => SoundAnalyzer.Analyse(t.Path));
                results.Add(new(t.Path, t.Size, t.Mtime, analysis, null, null, SoundTypes.FromPath(t.Path)));
            }
            catch (Exception e)
            {
                results.Add(new(t.Path, t.Size, t.Mtime, null, $"{e.GetType().Name}: {e.Message}", null, SoundTypes.FromPath(t.Path)));
            }
            progress?.Report(new("Listening to new sounds", Interlocked.Increment(ref done), todo.Count));
        });
        Library.Save(results);
        var removed = Library.Remove(gone);
        if (results.Count + removed + moved == 0) return 0;
        Reload();
        UpdateTypes(progress);
        return results.Count + removed + moved;
    }

    /// <summary>
    /// Pairs files gone from the library with new ones that are the same files renamed or moved: a rename or move keeps
    /// a file's size and modified time. Among files that share those (a pack's loops often do), each pairs with the one
    /// whose path ends the same way (file name, then its folders), when that's the only best match; a single file left
    /// on each side is a rename. Anything less sure isn't paired, and is forgotten and listened to as before.
    /// </summary>
    public static List<(string From, string To)> Moves(IEnumerable<(string Path, long Size, long Mtime)> gone,
                                                       IEnumerable<(string Path, long Size, long Mtime)> added)
    {
        var moves = new List<(string From, string To)>();
        var arrived = added.GroupBy(a => (a.Size, a.Mtime)).ToDictionary(g => g.Key, g => g.Select(a => a.Path).ToList());
        foreach (var group in gone.GroupBy(g => (g.Size, g.Mtime)))
        {
            if (!arrived.TryGetValue(group.Key, out var news)) continue;
            var olds = group.Select(g => g.Path).ToList();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unsure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var alike = olds.SelectMany(o => news.Select(n => (From: o, To: n, Same: SameEnding(o, n)))).Where(a => a.Same > 0).ToList();
            foreach (var level in alike.GroupBy(a => a.Same).OrderByDescending(g => g.Key))
            {
                var open = level.Where(a => !used.Contains(a.From) && !used.Contains(a.To) && !unsure.Contains(a.From) && !unsure.Contains(a.To)).ToList();
                foreach (var a in open)
                {
                    if (open.Count(o => o.From == a.From) == 1 && open.Count(o => o.To == a.To) == 1)
                    {
                        moves.Add((a.From, a.To));
                        used.Add(a.From);
                        used.Add(a.To);
                    }
                    else
                    {
                        // Two equally good matches: neither is sure, now or with a worse match further down.
                        unsure.Add(a.From);
                        unsure.Add(a.To);
                    }
                }
            }
            var oldLeft = olds.Where(o => !used.Contains(o)).ToList();
            var newLeft = news.Where(n => !used.Contains(n)).ToList();
            if (oldLeft.Count == 1 && newLeft.Count == 1 && !unsure.Contains(oldLeft[0]) && !unsure.Contains(newLeft[0]))
                moves.Add((oldLeft[0], newLeft[0]));
        }
        return moves;

        // How many names at the end of two paths are the same: 1 for the file name, 2 for it and its folder, and so on.
        static int SameEnding(string a, string b)
        {
            var x = a.Split('\\', '/');
            var y = b.Split('\\', '/');
            var n = 0;
            while (n < x.Length && n < y.Length && string.Equals(x[^(n + 1)], y[^(n + 1)], StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }
    }

    /// <summary>
    /// "Deep listening": runs CLAP over every sound that doesn't have an embedding yet, or only over <paramref name="only"/>'s
    /// ones (the sounds a scan just found). Slower (a fraction of a second per sound), resumable, and unlocks search by description.
    /// </summary>
    public int DeepListen(IProgress<SoundProgress>? progress = null, CancellationToken ct = default, IEnumerable<long>? only = null)
    {
        var clap = Clap ?? throw new InvalidOperationException("The sound-understanding model isn't installed yet");
        var which = only is null ? _sounds : only.Select(Get).OfType<Sound>();
        var missing = which.Where(s => s.Analysis is not null && (_clapIndex is null || !_clapIndex.Contains(s.Id))).ToList();
        if (missing.Count == 0) return 0;
        var done = 0;
        // Every core reads and prepares audio; the model runs several sounds at once (see ClapModel).
        Parallel.ForEach(missing, Parallelism.Options(ct), s =>
        {
            try
            {
                // Not all in one slot: EmbedAudio takes them only while it works, not while it waits for the model.
                var audio = Parallelism.Background(() => AudioFile.Read(s.Path, 30));
                Library.SetClap(s.Id, clap.EmbedAudio(audio));
            }
            catch (Exception) { }   // unreadable now; it'll be retried next time
            var n = Interlocked.Increment(ref done);
            if (n % 10 == 0) progress?.Report(new("Deep listening", n, missing.Count));
        });
        Reload();
        UpdateTypes(progress);
        return missing.Count;
    }

    // ---- the sound galaxy ---------------------------------------------------------------------

    /// <summary>What can place the galaxy's stars: CLAP and the measurements together, CLAP alone, or the measurements alone.</summary>
    public const string Both = "Both", ClapOnly = "CLAP", OwnOnly = "Own measurements";

    /// <summary>What the stars are placed by this time: the wish, unless deep listening hasn't covered the library, when it's the measurements.</summary>
    public string GalaxySpace(string wanted) =>
        !HasDeepListening || wanted == OwnOnly ? "features" : wanted == ClapOnly ? "clap" : "clap+features";

    /// <summary>How many analysed sounds CLAP hasn't listened to yet.</summary>
    public int NotDeepListened => _features is null ? 0 : _features.Units.Keys.Count(id => _clapIndex?.Contains(id) != true);

    /// <summary>
    /// The fingerprints the galaxy is laid out from. "clap+features" joins CLAP's "what it is" (80 %) to the measured
    /// "how it sounds" (brightness, attack, length, tone: 60 %), so neighbours match in kind and in character. A sound
    /// CLAP hasn't heard yet falls back to its measurements in every mode.
    /// </summary>
    /// <remarks>
    /// The joined fingerprints are worked out on demand rather than stored: for 300,000 sounds a stored copy would
    /// take most of a gigabyte, and the map maker only reads each one a few times.
    /// </remarks>
    public (IReadOnlyList<long> Ids, Func<long, float[]> Vector) GalaxyVectors(string space)
    {
        if (_features is null) return (Array.Empty<long>(), _ => Array.Empty<float>());
        var features = _features;
        var ids = features.Units.Keys.OrderBy(id => id).ToArray();
        var clap = space == "features" ? null : _clapIndex;
        var clapDim = clap?.Units.Values.FirstOrDefault()?.Length ?? 0;
        if (clap is null || clapDim == 0) return (ids, id => features.Units[id]);
        return (ids, id =>
        {
            var f = features.Units[id];
            var v = new float[clapDim + f.Length];
            var c = clap.Get(id) is { } cv && cv.Length == clapDim ? cv : null;
            var (wc, wf) = c is null ? (0f, 1f) : space == "clap" ? (1f, 0f) : (0.8f, 0.6f);
            if (c is not null) for (var i = 0; i < clapDim; i++) v[i] = c[i] * wc;
            for (var i = 0; i < f.Length; i++) v[clapDim + i] = f[i] * wf;
            return VectorIndex.Unit(v);
        });
    }

    /// <summary>True when the saved map for this kind of placing is missing or lacks some sounds.</summary>
    public bool GalaxyOutOfDate(string wanted)
    {
        if (_features is not { Count: > 0 } f) return false;
        var map = Library.Map(GalaxySpace(wanted));
        return f.Units.Keys.Any(id => !map.ContainsKey(id));
    }

    /// <summary>
    /// Brings the galaxy up to date and returns it. Each kind of placing (<see cref="GalaxySpace"/>) keeps a map of its
    /// own, so switching between them only works one out the first time. It's laid out afresh when that kind has no map
    /// yet, <paramref name="fresh"/> asks for it, or more than a fifth of the sounds are new; otherwise the new sounds
    /// are placed beside their sound-alikes, so the stars you know stay where they were.
    /// </summary>
    public Dictionary<long, StarPoint> UpdateGalaxy(string wanted, IProgress<SoundProgress>? progress = null, CancellationToken ct = default,
                                                    bool fresh = false)
    {
        var space = GalaxySpace(wanted);
        var (ids, vector) = GalaxyVectors(space);
        var map = Library.Map(space);
        var newIds = ids.Where(id => !map.ContainsKey(id)).ToArray();
        if (fresh || map.Count == 0 || newIds.Length > ids.Count / 5)
        {
            map = GalaxyMap.Layout(ids, vector, progress, ct);
            Library.SaveMap(space, map, replace: true);
        }
        else if (newIds.Length > 0)
        {
            var mapped = ids.Where(map.ContainsKey).ToArray();
            var added = GalaxyMap.PlaceNew(newIds, mapped, vector, map);
            Library.SaveMap(space, added, replace: false);
            foreach (var (id, p) in added) map[id] = p;
        }
        return map;
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
        // Only the types that changed are written: the sounds in memory hold what the library has (they're read from it,
        // and every change goes to both), and rewriting all 357,000 after each scan kept the database busy for seconds.
        var updates = _sounds.AsParallel().AsOrdered().WithDegreeOfParallelism(Parallelism.Workers).Select(s =>
        {
            var (type, source, conf) = Parallelism.Background(() => ResolveType(s, model, zeroShot, space));
            var changed = s.Type != type || s.TypeSource != source || s.TypeConfidence != conf;
            s.Type = type;
            s.TypeSource = source;
            s.TypeConfidence = conf;
            return (s.Id, type, source, conf, changed);
        }).Where(u => u.changed).Select(u => (u.Id, u.type, u.source, u.conf)).ToList();
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
        return _sounds.AsParallel().WithDegreeOfParallelism(Parallelism.Workers)
            .Where(s => !s.Hidden && s.UserType is null && s.NameType is not null && RawVector(s, model.Space) is not null)
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
                folds[fold] = (toScore.AsParallel().AsOrdered().WithDegreeOfParallelism(Parallelism.Workers)
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
            .Where(h => h.Id != s.Id && _byId.TryGetValue(h.Id, out var x) && !x.Hidden)
            .Select(h => new SimilarSound(_byId[h.Id], Math.Round(h.Score, 3)))
            .Where(h => !sameTypeOnly || h.Sound.Type == s.Type)
            .Take(max).ToList();
    }

    /// <summary>Ranks sounds by how well they match a description, e.g. "dark gritty reese bass". Needs deep listening.</summary>
    public IReadOnlyList<SimilarSound> Describe(string text, int max = 200)
    {
        if (_clapIndex is null || Clap is not { } clap || string.IsNullOrWhiteSpace(text)) return [];
        var q = clap.EmbedText(text);
        // Asks for enough extra to make up for hidden copies, which are dropped after.
        return _clapIndex.Nearest(q, max + (_sounds.Count - _visible.Count), normaliseQuery: false)
            .Where(h => _byId.TryGetValue(h.Id, out var x) && !x.Hidden)
            .Select(h => new SimilarSound(_byId[h.Id], Math.Round(h.Score, 3))).Take(max).ToList();
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
    /// <summary>Every vector, made unit length (and standardised and weighted, when the index does that).</summary>
    public IReadOnlyDictionary<long, float[]> Units => _unit;
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

    KeyValuePair<long, float[]>[]? _entries;

    /// <summary>
    /// The <paramref name="max"/> closest vectors, closest first. Every vector is compared (no approximation), but in
    /// parallel, several numbers at a time, keeping only the best few as it goes: with 450,000 CLAP vectors, sorting
    /// them all took about half a second, and picking a sound waited on it.
    /// </summary>
    public IReadOnlyList<(long Id, double Score)> Nearest(float[] query, int max, bool normaliseQuery = true)
    {
        var q = normaliseQuery ? Unit((float[])query.Clone()) : query;
        if (max <= 0 || _unit.Count == 0) return [];
        var entries = _entries ??= [.. _unit];
        const int Chunk = 4096;
        var chunks = (entries.Length + Chunk - 1) / Chunk;
        var best = new List<(long Id, float Score)>[chunks];
        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = Parallelism.Workers }, c =>
        {
            var heap = new PriorityQueue<(long Id, float Score), float>(max + 1);
            for (int i = c * Chunk, end = Math.Min(entries.Length, i + Chunk); i < end; i++)
            {
                var score = FastDot(q, entries[i].Value);
                if (heap.Count < max) heap.Enqueue((entries[i].Key, score), score);
                else if (score > heap.Peek().Score) heap.EnqueueDequeue((entries[i].Key, score), score);
            }
            best[c] = [.. heap.UnorderedItems.Select(x => x.Element)];
        });
        return best.SelectMany(b => b).OrderByDescending(t => t.Score).ThenBy(t => t.Id).Take(max)
                   .Select(t => (t.Id, (double)t.Score)).ToList();
    }

    static float FastDot(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        var sum = System.Numerics.Vector<float>.Zero;
        var width = System.Numerics.Vector<float>.Count;
        for (; i <= n - width; i += width)
            sum += new System.Numerics.Vector<float>(a, i) * new System.Numerics.Vector<float>(b, i);
        var s = System.Numerics.Vector.Sum(sum);
        for (; i < n; i++) s += a[i] * b[i];
        return s;
    }
}
