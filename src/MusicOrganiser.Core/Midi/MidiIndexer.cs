using System.Collections.Concurrent;
using System.Diagnostics;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi;

/// <summary>
/// Fills the MIDI browser's index: every pattern or clip in the newest save of each song the library knows (or every
/// save, with <see cref="OlderSavesKey"/>), and every .mid file in your MIDI library folders, read where it is (nothing
/// is moved, copied or changed). Only new and changed files are read, and files that failed are only tried again when
/// asked. It runs on a couple of lowest-priority threads in the app's shared background slots, so the app and your DAW
/// always come first.
/// </summary>
public sealed class MidiIndexer(MidiIndex index, LibraryDb library)
{
    /// <summary>The MIDI library folders, a list in the library's settings.</summary>
    public const string FoldersKey = "midi_folders";

    /// <summary>
    /// "1": read every save of a song, not just its newest. Off by default: a song saved 36 times is mostly the same MIDI
    /// 36 times (Solomon, 2026-10-15: "if there is a long list of projects that are just different versions just do the
    /// midi extraction for those").
    /// </summary>
    public const string OlderSavesKey = "midi_older_saves";

    /// <summary>Raised when the analysis changes, so every file is read again once.</summary>
    public const int Version = 5;   // 2: empty bars trimmed and long gaps split (2026-10-15); 3: clips joined into phrases, cut where the music breathes; 4: low basslines and chords no longer taken for GM drums (2026-10-17); 5: parts named after a drum sound ("Tamb") are drums (2026-10-18)

    /// <summary>At most this many files are read at once: a quarter of the cores, and never more than 3.</summary>
    public static int Workers => Math.Clamp(Environment.ProcessorCount / 4, 1, 3);

    /// <summary>A MIDI file's header (14 bytes) and one empty track (8) and no notes: anything shorter is skipped as empty.</summary>
    public const int MinMidiBytes = 22;

    public sealed record Result(int Read, int Failed, int Forgotten, int Phrases);

    /// <param name="retryFailed">Also read again the files that failed last time, though they haven't changed.</param>
    public Result Run(IProgress<ScanProgress>? progress = null, CancellationToken ct = default, bool retryFailed = false)
    {
        progress?.Report(new("Finding MIDI", 0, 0, ""));
        var finding = Stopwatch.StartNew();
        var wanted = new Dictionary<string, MidiSource>(StringComparer.OrdinalIgnoreCase);
        var readable = library.ProjectFiles().Where(p => !p.FileMissing && p.Error is null && Readers.For(p.Path) is not null).ToList();
        // The same song grouping as the Projects page's "One row per song", so its newest save is the one read.
        var projects = library.GetSetting(OlderSavesKey) == "1" ? readable : SongVersions.Group(readable).Select(g => g.Latest);
        foreach (var p in projects)
            wanted[p.Path] = new MidiSource(p.Path, MidiSource.Project, p.Daw, p.Mtime, p.Size, p.Tempo);
        var folders = library.GetList(FoldersKey).Where(Directory.Exists).ToList();
        // Sizes and times come with the folder listing: no extra look at each of tens of thousands of files.
        foreach (var f in Parallelism.FindFileStamps(folders, MidiFileReader.IsMidiPath, (name, _) => name.StartsWith('.'), ct: ct))
        {
            // Too short to hold a single note (an empty or never-finished download): nothing to read, and not a failure.
            if (f.Size < MinMidiBytes) continue;
            wanted[f.Path] = new MidiSource(f.Path, MidiSource.File, "MIDI", f.Mtime, f.Size, null);
        }

        var known = index.Sources();
        var failedBefore = retryFailed ? index.Failures().Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        var forget = known.Keys.Where(k => !wanted.ContainsKey(k)).ToList();
        if (forget.Count > 0) index.Forget(forget);
        var todo = new ConcurrentQueue<MidiSource>(wanted.Values
            .Where(s => !known.TryGetValue(s.Path, out var k) || k.Mtime != s.Mtime || k.Size != s.Size || k.Version != Version ||
                        failedBefore.Contains(s.Path))
            .OrderByDescending(s => s.Mtime));   // newest first: what you made lately shows up first
        var total = todo.Count;
        if (finding.Elapsed.TotalSeconds >= SlowSeconds)
            AppLog.Info("MIDI", $"Finding new and changed files took {finding.Elapsed.TotalSeconds:0.0} s ({wanted.Count:N0} files, {total:N0} to read)");
        if (total == 0) return new(0, 0, forget.Count, 0);

        var results = new BlockingCollection<(MidiSource, int, IReadOnlyList<IndexedPhrase>, string?)>(boundedCapacity: 256);
        int read = 0, failed = 0, phrases = 0;
        var threads = Enumerable.Range(0, Math.Min(Workers, total)).Select(i => new Thread(() =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // While you're using the app, one song at a time: reading a song makes a lot of memory to clean up,
                    // and on a big library each clean-up pauses the window (Solomon's log, 2026-10-16: freezes of 0.3 to
                    // 1.7 s back to back while MIDI was read). All of them while the app is minimised or behind another.
                    if (todo.IsEmpty) break;
                    if (i > 0 && !Parallelism.AppInBackground)
                    {
                        Thread.Sleep(500);
                        continue;
                    }
                    if (!todo.TryDequeue(out var source)) break;
                    try
                    {
                        // In one of the app's shared background slots, so a scan or Deep listen running too still leaves cores free.
                        var asked = Stopwatch.GetTimestamp();
                        var began = asked;
                        var paused = GC.GetTotalPauseDuration();
                        var found = Parallelism.Background(() =>
                        {
                            began = Stopwatch.GetTimestamp();
                            return source.Kind == MidiSource.File ? FromFile(source.Path, folders) : FromProject(source.Path);
                        });
                        LogIfSlow(source, found.Count, Stopwatch.GetElapsedTime(asked, began), Stopwatch.GetElapsedTime(began),
                                  GC.GetTotalPauseDuration() - paused);
                        results.Add((source, Version, found, null), ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception e)   // one unreadable file never stops the rest
                    {
                        Interlocked.Increment(ref failed);
                        AppLog.Info("MIDI", $"Couldn't read {source.Path}: {e}");
                        try { results.Add((source, Version, [], Why(e)), ct); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }
            catch (InvalidOperationException) { }   // the saver stopped taking results
        })
        {
            IsBackground = true, Priority = ThreadPriority.Lowest, Name = $"MIDI index {i + 1}",
        }).ToList();
        foreach (var t in threads) t.Start();
        var waiter = Task.Run(() =>
        {
            foreach (var t in threads) t.Join();
            if (!results.IsAddingCompleted) results.CompleteAdding();
        });

        // Saved here, a batch at a time, so the database is written from one place.
        var batch = new List<(MidiSource, int, IReadOnlyList<IndexedPhrase>, string?)>();
        var lastSave = Environment.TickCount64;
        try
        {
            foreach (var r in results.GetConsumingEnumerable(ct))
            {
                batch.Add(r);
                read++;
                phrases += r.Item3.Count;
                if (batch.Count >= 100 || Environment.TickCount64 - lastSave > 1000)
                {
                    index.ReplaceMany(batch);
                    batch.Clear();
                    lastSave = Environment.TickCount64;
                    progress?.Report(new("Reading MIDI", read, total, Path.GetFileName(r.Item1.Path)));
                }
            }
        }
        finally
        {
            // Stopped or failed part way: the readers still waiting to hand a file over stop too.
            if (!results.IsAddingCompleted) results.CompleteAdding();
            index.ReplaceMany(batch);
            waiter.Wait(CancellationToken.None);
        }
        ct.ThrowIfCancellationRequested();
        progress?.Report(new("Done", total, total, ""));
        return new(read, failed, forget.Count, phrases);
    }

    /// <summary>A file that takes this long (or waits this long for a free slot) is noted in the log, with why.</summary>
    public const double SlowSeconds = 2;

    /// <summary>
    /// Notes a slow file in the log: how long it waited for a free slot (other background work had them all), how long
    /// reading it took, and how long the app spent cleaning up memory meanwhile. On Solomon's PC songs read here in under a
    /// second took about five each (2026-10-19), and his log is the only place that can say why.
    /// </summary>
    static void LogIfSlow(MidiSource source, int parts, TimeSpan waited, TimeSpan took, TimeSpan cleaning)
    {
        if (waited.TotalSeconds < SlowSeconds && took.TotalSeconds < SlowSeconds) return;
        var why = new List<string> { $"{source.Size / 1e6:0.#} MB, {parts:N0} parts" };
        if (waited.TotalSeconds >= 0.5) why.Add($"waited {waited.TotalSeconds:0.0} s for a free background slot first");
        if (cleaning.TotalSeconds >= 0.5) why.Add($"the app paused {cleaning.TotalSeconds:0.0} s cleaning up memory meanwhile");
        AppLog.Info("MIDI", $"Slow read: {source.Path} took {took.TotalSeconds:0.0} s ({string.Join("; ", why)})");
    }

    /// <summary>Why a file couldn't be read, in a few plain words, for the list of failed ones.</summary>
    public static string Why(Exception e) => e switch
    {
        UnauthorizedAccessException => "Windows didn't allow reading it",
        FileNotFoundException or DirectoryNotFoundException => "It isn't there any more",
        EndOfStreamException or InvalidDataException or FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException
            => $"It looks cut short or damaged, or saved in a way the reader doesn't know yet ({e.GetType().Name})",
        IOException => $"It couldn't be opened, maybe because another program had it open ({e.Message})",
        _ => $"{e.GetType().Name}: {e.Message}",
    };

    /// <summary>Each pattern (FL Studio) or clip (Bitwig, Studio One) as one phrase per channel or track with notes.</summary>
    public static IReadOnlyList<IndexedPhrase> FromProject(string path)
    {
        if (Readers.For(path) is not { } reader) return [];
        // The clips, the playlist and the channels all come from one read of the file.
        using var once = ReadOnce.Scope();
        var phrases = new List<IndexedPhrase>();
        var seen = new HashSet<string>();
        foreach (var clip in reader.ExtractMidi(path))
            foreach (var (track, notes) in clip.Tracks)
            {
                var drums = ProjectKey.IsUnpitchedPlugin(track);
                var clipName = clip.Name.Equals(track, StringComparison.OrdinalIgnoreCase) ? "" : clip.Name;
                Add(phrases, seen, PhraseAnalyser.Normalise(notes, clip.Ppq), track, clipName, $"{track} {clip.Name}", drums);
            }

        // Then the longer phrases: clips that play back to back on a track in the arrangement, joined. Not for drums,
        // which have no harmony to fit and play the same few hits all song long.
        if (phrases.Count < 2) return phrases;
        IReadOnlyList<(string Track, IReadOnlyList<TrackClip> Clips)> tracks;
        try { tracks = PhraseJoiner.Tracks(path); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The clips on their own are still worth having.
            AppLog.Info("MIDI", $"Couldn't read the arrangement of {path}: {e.Message}");
            tracks = [];
        }
        foreach (var (track, clips) in tracks)
        {
            if (ProjectKey.IsUnpitchedPlugin(track)) continue;
            foreach (var joined in PhraseJoiner.Phrases(track, clips))
            {
                if (joined.Notes.Count < 3) continue;
                var analysis = PhraseAnalyser.Analyse(joined.Notes, $"{track} {joined.Clip}");
                if (analysis.Role == MidiRoles.Drums || !seen.Add(analysis.Hash)) continue;
                phrases.Add(new IndexedPhrase(track, joined.Clip, joined.Notes, analysis, false) { Members = joined.Members });
            }
        }
        return phrases;
    }

    /// <summary>Each track of a .mid file (each channel, in a one-track file), with its file and folder names as hints.</summary>
    public static IReadOnlyList<IndexedPhrase> FromFile(string path, IReadOnlyList<string> roots)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var root = roots.Where(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase)).MaxBy(r => r.Length) ?? "";
        var folders = (Path.GetDirectoryName(path) ?? "")[Math.Min(root.Length, (Path.GetDirectoryName(path) ?? "").Length)..]
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).TakeLast(3);
        var phrases = new List<IndexedPhrase>();
        var seen = new HashSet<string>();
        foreach (var part in MidiFileReader.Read(path))
            Add(phrases, seen, PhraseAnalyser.Normalise(part.Notes, part.Ppq), part.Name, name,
                $"{part.Name} {name} {string.Join(' ', folders)}", part.Drums);
        return phrases;
    }

    static void Add(List<IndexedPhrase> phrases, HashSet<string> seen, List<NoteEvent> notes, string part, string clip, string hints, bool drums)
    {
        if (notes.Count < 3) return;
        foreach (var (where, piece, _) in PhraseAnalyser.Split(notes))
        {
            var analysis = PhraseAnalyser.Analyse(piece, hints, drums);
            if (!seen.Add(analysis.Hash)) continue;   // the same notes again in this file
            phrases.Add(new IndexedPhrase(part, where.Length == 0 ? clip : clip.Length == 0 ? where : $"{clip} · {where}", piece, analysis, drums));
        }
    }
}
