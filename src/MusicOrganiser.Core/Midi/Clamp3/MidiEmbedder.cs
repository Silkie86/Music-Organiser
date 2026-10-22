using MusicOrganiser.Core.Library;

namespace MusicOrganiser.Core.Midi;

/// <summary>
/// Lets CLaMP 3 hear every MIDI part in the index that it hasn't heard yet (one per set of notes, newest files first)
/// and saves what it heard in midi.db. It runs in the shared low-priority background slots, so the window, playback
/// and your DAW come first, and it carries on where it stopped next time.
/// </summary>
public static class MidiEmbedder
{
    public sealed record Result(int Heard, int Failed, int Left);

    /// <param name="saved">Given each batch as it's saved (note hash → what was heard), so search can use it straight away.</param>
    public static Result Run(MidiIndex index, Clamp3Model model, IProgress<ScanProgress>? progress = null, CancellationToken ct = default,
                             Action<IReadOnlyDictionary<string, sbyte[]>>? saved = null)
    {
        index.PruneEmbeddings(Clamp3Model.Version);
        var todo = index.WithoutEmbedding(Clamp3Model.Version);
        if (todo.Count == 0) return new(0, 0, 0);
        progress?.Report(new("Listening to MIDI", 0, todo.Count, ""));
        var batch = new List<(string, byte[])>();
        var batchLock = new object();
        var lastSave = Environment.TickCount64;
        int heard = 0, failed = 0;

        void Save()
        {
            List<(string, byte[])> now;
            lock (batchLock)
            {
                now = [.. batch];
                batch.Clear();
                lastSave = Environment.TickCount64;
            }
            index.SetEmbeddings(Clamp3Model.Version, now);
            if (saved is not null && now.Count > 0)
                saved(now.GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => Clamp3Model.Unpack(g.Last().Item2)));
        }

        try
        {
            // As many at once as the model has copies: each holds one of the shared background slots only while it's
            // hearing, rather than more threads holding slots while they wait for a free copy.
            Parallel.ForEach(todo, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Clamp3Model.Copies }, item =>
            {
                try
                {
                    var notes = index.NotesInBackground(item.Id);
                    var vec = notes.Count == 0 ? null
                        : Parallelism.Background(() => model.EmbedMidi(notes, PhraseAnalyser.Ppq, item.Tempo, item.Drums));
                    if (vec is null) return;
                    bool save;
                    lock (batchLock)
                    {
                        batch.Add((item.Hash, Clamp3Model.Pack(vec)));
                        save = batch.Count >= 200 || Environment.TickCount64 - lastSave > 2000;
                    }
                    if (save) Save();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Interlocked.Increment(ref failed);   // tried again next time
                }
                var n = Interlocked.Increment(ref heard);
                if (n % 20 == 0) progress?.Report(new("Listening to MIDI", n, todo.Count, ""));
            });
        }
        finally { Save(); }
        progress?.Report(new("Done", todo.Count, todo.Count, ""));
        return new(heard - failed, failed, 0);
    }
}
