using MusicOrganiser.Core.Library;
using MusicOrganiser.ViewModels;
using Xunit;

namespace MusicOrganiser.Core.Tests;

/// <summary>Big libraries without the window stopping: progress that doesn't flood it, and database work in batches.</summary>
public class ResponsivenessTests
{
    [Fact]
    public async Task Progress_is_shown_now_and_then_and_always_ends_on_the_newest()
    {
        var shown = new List<int>();
        var progress = new ThrottledProgress<int>(n => { lock (shown) shown.Add(n); }, everyMs: 50);
        await Task.Run(() =>
        {
            for (var i = 1; i <= 200_000; i++) progress.Report(i);
        });
        for (var waited = 0; waited < 2000 && !Last(200_000); waited += 20) await Task.Delay(20);
        Assert.True(Last(200_000));
        Assert.True(shown.Count < 200, $"shown {shown.Count} times");

        // Once the job has ended, a late report doesn't show.
        progress.Close();
        progress.Report(5);
        await Task.Delay(150);
        Assert.True(Last(200_000));

        bool Last(int n)
        {
            lock (shown) return shown.Count > 0 && shown[^1] == n;
        }
    }

    [Fact]
    public async Task Background_jobs_together_leave_cores_free_and_run_below_normal_priority()
    {
        var running = 0;
        var most = 0;
        var priorities = new System.Collections.Concurrent.ConcurrentBag<ThreadPriority>();
        void Job() => Parallel.ForEach(Enumerable.Range(0, 64), Parallelism.Options(), _ => Parallelism.Background(() =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            priorities.Add(Thread.CurrentThread.Priority);
            Parallelism.Background(() => Thread.Sleep(5));   // work started from background work doesn't wait on itself
            Interlocked.Decrement(ref running);
        }));
        await Task.WhenAll(Task.Run(Job), Task.Run(Job));   // say a scan and Deep listen at once

        Assert.InRange(most, 1, Parallelism.Workers);
        Assert.True(Parallelism.Workers <= Math.Max(1, Environment.ProcessorCount - 2));
        Assert.All(priorities, p => Assert.Equal(ThreadPriority.BelowNormal, p));

        static void InterlockedMax(ref int target, int value)
        {
            for (var seen = target; value > seen; seen = target)
                if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
        }
    }

    [Fact]
    public async Task Reading_the_library_never_waits_for_a_long_write()
    {
        var dir = Fixtures.TempDir();
        Fixtures.FlStudioProject(dir);
        using var db = new LibraryDb(Path.Combine(dir, "library.db"));
        db.AddRoot(dir);
        new Scanner(db).ScanProjects();

        // A write holding the database (a scan saving a batch, a big index going in) for a long time.
        var writeLock = typeof(LibraryDb).GetField("_lock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(db)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            lock (writeLock)
            {
                held.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });
        held.Wait();
        try
        {
            var read = Task.Run(() => (db.Projects().Count, db.GetSetting("anything")));
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(2000)));
            Assert.Equal((1, (string?)null), await read);
        }
        finally
        {
            release.Set();
            await writer;
        }
    }

    [Fact]
    public void A_big_sample_index_goes_in_whole_across_batches()
    {
        var dir = Fixtures.TempDir();
        using var db = new LibraryDb(Path.Combine(dir, "library.db"));
        db.ReplaceSampleIndex(Enumerable.Range(0, 4_500).Select(i => $"/samples/Kick {i}.wav"));
        Assert.Equal(4_500, db.SampleIndexCount());
        db.ReplaceSampleIndex(["/samples/Snare.wav"]);
        Assert.Equal(1, db.SampleIndexCount());
        Assert.Equal(["/samples/Snare.wav"], db.SamplesNamed("Snare.wav"));
    }

    [Fact]
    public async Task Cards_finished_on_many_threads_at_once_all_leave_without_a_crash()
    {
        var feed = new ActivityFeed { Linger = TimeSpan.Zero, FadeOut = TimeSpan.FromMilliseconds(20) };
        var cards = Enumerable.Range(0, 200).Select(i => feed.Start($"Job {i}")).ToList();
        await Task.WhenAll(cards.Select(c => Task.Run(() => c.Finish())));
        for (var i = 0; i < 100 && feed.Items.Count > 0; i++) await Task.Delay(20);
        Assert.Empty(feed.Items);
    }
}
