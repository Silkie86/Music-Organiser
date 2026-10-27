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
    public async Task Background_work_holds_back_while_the_user_sweeps()
    {
        // Solomon, 2026-10-27: "missed some sounds when sweeping at first", with Deep listening reading files meanwhile
        try
        {
            Parallelism.QuietFor(TimeSpan.FromSeconds(30));
            Assert.True(Parallelism.Quiet);
            var ran = false;
            var piece = Task.Run(() => Parallelism.Background(() => ran = true));
            await Task.Delay(300);
            Assert.False(ran);   // waiting, not started

            Parallelism.QuietFor(TimeSpan.FromMilliseconds(100));   // let go: it starts soon after
            await piece.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(ran);
        }
        finally { Parallelism.QuietFor(TimeSpan.Zero); }
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
    public void Gentle_uses_half_the_processor_and_Fast_everything_only_while_the_app_is_in_the_background()
    {
        try
        {
            Parallelism.Mode = Parallelism.Pace.Gentle;
            Assert.Equal(Math.Max(1, Parallelism.Cores / 2), Parallelism.Workers);
            Parallelism.Mode = Parallelism.Pace.Balanced;
            Assert.Equal(Math.Max(1, Parallelism.Cores - 2), Parallelism.Workers);
            Parallelism.Mode = Parallelism.Pace.Fast;
            Assert.Equal(Math.Max(1, Parallelism.Cores - 2), Parallelism.Workers);
            Parallelism.AppInBackground = true;
            Assert.Equal(Parallelism.Cores, Parallelism.Workers);
            Assert.Equal(Parallelism.Pace.Fast, Parallelism.ParsePace("Fast"));
            Assert.Equal(Parallelism.Pace.Gentle, Parallelism.ParsePace(null));
        }
        finally
        {
            Parallelism.AppInBackground = false;
            Parallelism.Mode = Parallelism.Pace.Gentle;
        }
    }

    [Fact]
    public async Task Clicking_into_or_away_from_the_window_never_waits_for_background_work()
    {
        // A reader holding the slots' lock while the busy processor isn't running it (Solomon's log, 2026-10-22: the
        // window froze 6 s twice, each ending as a MIDI file's read did). The window says it's in front or behind
        // through these, so they must return at once.
        var gate = typeof(Parallelism).GetField("Gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (gate)
            {
                held.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });
        held.Wait();
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Parallelism.AppInBackground = true;
            Parallelism.AppInBackground = false;
            Parallelism.Mode = Parallelism.Pace.Gentle;
            Assert.True(clock.ElapsedMilliseconds < 500, $"took {clock.ElapsedMilliseconds} ms");
        }
        finally
        {
            release.Set();
            await holder;
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
    public async Task Song_choices_read_while_a_scan_is_saving_see_only_saved_rows_and_dont_wait()
    {
        // The Projects page reads your song choices on a background thread after a join or split, often while a scan
        // is saving. They used to be read on the scan's own connection, in the middle of its open save.
        var dir = Fixtures.TempDir();
        using var db = new LibraryDb(Path.Combine(dir, "library.db"));
        db.KeepApart("song a", "song b");
        const System.Reflection.BindingFlags field = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var writeLock = typeof(LibraryDb).GetField("_lock", field)!.GetValue(db)!;
        var conn = (Microsoft.Data.Sqlite.SqliteConnection)typeof(LibraryDb).GetField("_conn", field)!.GetValue(db)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            lock (writeLock)
            {
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO song_choices VALUES ('apart', $p, '')";
                cmd.Parameters.AddWithValue("$p", SongChoices.Pair("not", "saved"));
                cmd.ExecuteNonQuery();
                held.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                tx.Rollback();
            }
        });
        held.Wait();
        try
        {
            var read = Task.Run(db.SongChoices);
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(2000)));
            var choices = await read;
            Assert.True(choices.Apart("song a", "song b"));
            Assert.False(choices.Apart("not", "saved"));
        }
        finally
        {
            release.Set();
            await writer;
        }
    }

    [Fact]
    public void Sweep_timings_forget_previews_that_were_replaced_or_couldnt_play()
    {
        // The galaxy notes each swept star on the window's thread, and copies the waiting list as it does. Stars asked
        // for and then replaced by a newer one before they started used to stay on it all session.
        var pending = (System.Collections.IDictionary)typeof(SweepTiming)
            .GetField("Pending", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var paths = Enumerable.Range(0, 200).Select(i => $"/sweep test/{i}.wav").ToList();
        foreach (var path in paths)
        {
            SweepTiming.Reached(path, 0);
            SweepTiming.Asked(path);
        }
        foreach (var path in paths.SkipLast(1)) SweepTiming.Dropped(path);
        Assert.Equal([paths[^1]], paths.Where(pending.Contains));

        SweepTiming.Opening(paths[^1]);
        SweepTiming.Started(paths[^1]);
        Assert.DoesNotContain(paths, pending.Contains);
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
