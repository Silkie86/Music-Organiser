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
