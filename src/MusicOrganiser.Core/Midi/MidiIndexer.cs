using System.Collections.Concurrent;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi;

/// <summary>
/// Fills the MIDI browser's index: every pattern or clip in the projects the library knows, and every .mid file in your
/// MIDI library folders, read where it is (nothing is moved, copied or changed). Only new and changed files are read.
/// It runs on a couple of lowest-priority threads, so the app and your DAW always come first.
/// </summary>
public sealed class MidiIndexer(MidiIndex index, LibraryDb library)
{
    /// <summary>The MIDI library folders, a list in the library's settings.</summary>
    public const string FoldersKey = "midi_folders";

    /// <summary>Raised when the analysis changes, so every file is read again once.</summary>
    public const int Version = 1;

    /// <summary>At most this many files are read at once: a quarter of the cores, and never more than 3.</summary>
    public static int Workers => Math.Clamp(Environment.ProcessorCount / 4, 1, 3);

    public sealed record Result(int Read, int Failed, int Forgotten, int Phrases);

    public Result Run(IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new("Finding MIDI", 0, 0, ""));
        var wanted = new Dictionary<string, MidiSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in library.Projects())
            if (!p.FileMissing && p.Error is null && Readers.For(p.Path) is not null)
                wanted[p.Path] = new MidiSource(p.Path, MidiSource.Project, p.Daw, p.Mtime, p.Size, p.Tempo);
        var folders = library.GetList(FoldersKey).Where(Directory.Exists).ToList();
        foreach (var f in Parallelism.FindFiles(folders, MidiFileReader.IsMidiPath, (name, _) => name.StartsWith('.'), ct: ct))
        {
            try
            {
                var fi = new FileInfo(f);
                wanted[f] = new MidiSource(f, MidiSource.File, "MIDI", new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(), fi.Length, null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        var known = index.Sources();
        var forget = known.Keys.Where(k => !wanted.ContainsKey(k)).ToList();
        if (forget.Count > 0) index.Forget(forget);
        var todo = new ConcurrentQueue<MidiSource>(wanted.Values
            .Where(s => !known.TryGetValue(s.Path, out var k) || k.Mtime != s.Mtime || k.Size != s.Size || k.Version != Version)
            .OrderByDescending(s => s.Mtime));   // newest first: what you made lately shows up first
        var total = todo.Count;
        if (total == 0) return new(0, 0, forget.Count, 0);

        var results = new BlockingCollection<(MidiSource, int, IReadOnlyList<IndexedPhrase>, string?)>(boundedCapacity: 256);
        int read = 0, failed = 0, phrases = 0;
        var threads = Enumerable.Range(0, Math.Min(Workers, total)).Select(i => new Thread(() =>
        {
            try
            {
                while (!ct.IsCancellationRequested && todo.TryDequeue(out var source))
                {
                    try
                    {
                        var found = source.Kind == MidiSource.File ? FromFile(source.Path, folders) : FromProject(source.Path);
                        results.Add((source, Version, found, null), ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception e)   // one unreadable file never stops the rest
                    {
                        Interlocked.Increment(ref failed);
                        try { results.Add((source, Version, [], $"{e.GetType().Name}: {e.Message}"), ct); }
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

    /// <summary>Each pattern (FL Studio) or clip (Bitwig, Studio One) as one phrase per channel or track with notes.</summary>
    public static IReadOnlyList<IndexedPhrase> FromProject(string path)
    {
        if (Readers.For(path) is not { } reader) return [];
        var phrases = new List<IndexedPhrase>();
        var seen = new HashSet<string>();
        foreach (var clip in reader.ExtractMidi(path))
            foreach (var (track, notes) in clip.Tracks)
            {
                var drums = ProjectKey.IsUnpitchedPlugin(track);
                var clipName = clip.Name.Equals(track, StringComparison.OrdinalIgnoreCase) ? "" : clip.Name;
                Add(phrases, seen, PhraseAnalyser.Normalise(notes, clip.Ppq), track, clipName, $"{track} {clip.Name}", drums);
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
        foreach (var (where, piece) in PhraseAnalyser.Split(notes))
        {
            var analysis = PhraseAnalyser.Analyse(piece, hints, drums);
            if (!seen.Add(analysis.Hash)) continue;   // the same notes again in this file
            phrases.Add(new IndexedPhrase(part, where.Length == 0 ? clip : clip.Length == 0 ? where : $"{clip} · {where}", piece, analysis, drums));
        }
    }
}
