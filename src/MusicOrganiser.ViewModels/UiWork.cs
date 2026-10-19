namespace MusicOrganiser.ViewModels;

/// <summary>
/// A page's heavy work (filtering and sorting 357,000 sounds, reading the database) run off the UI thread, so the
/// window keeps answering; the page shows the result when it's done.
/// </summary>
public static class UiWork
{
    /// <summary>False runs the work straight away on the calling thread (the tests, which check the result at once).</summary>
    public static bool InBackground { get; set; } = true;

    public static Task<T> Run<T>(Func<T> work) => InBackground ? Task.Run(work) : Task.FromResult(work());

    /// <summary>
    /// The same on a thread of its own rather than the shared pool: for big one-off jobs (loading 50,000 MIDI parts) that
    /// mustn't queue behind other background work holding the pool's threads while it waits its turn.
    /// </summary>
    public static Task<T> RunOwnThread<T>(Func<T> work) => InBackground
        ? Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
        : Task.FromResult(work());
}
