using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using MusicOrganiser.Avalonia.Diagnostics;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>The run diary (logs\) and F12's notes for Claude (notes\), which Claude reads from Solomon's computer.</summary>
public sealed class DiagnosticsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("mo-diag-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_log_starts_with_the_build_and_holds_what_happened()
    {
        string path;
        using (var log = new SessionLog(_dir, new DateTime(2026, 10, 15, 19, 30, 5)))
        {
            log.Info("Page", "Sounds");
            log.Error("App", "Something broke", new InvalidOperationException("bad state"));
            path = log.Path;
        }

        Assert.Equal("session-2026-10-15_19-30-05.log", Path.GetFileName(path));
        var text = File.ReadAllText(path);
        Assert.Contains("Music Organiser started 2026-10-15 19:30:05", text);
        Assert.Contains("Version:", text);
        Assert.Contains("[Page] Sounds", text);
        Assert.Contains("Something broke: System.InvalidOperationException: bad state", text);
        Assert.Contains("[App] Closed", text);
    }

    [Fact]
    public void A_message_that_keeps_coming_is_counted_not_repeated()
    {
        string path;
        using (var log = new SessionLog(_dir))
        {
            for (var i = 0; i < 100; i++) log.Warning("Binding", "Could not find Title on Row   ");
            path = log.Path;
        }

        var lines = File.ReadAllLines(path).Where(l => l.Contains("[Binding]")).ToList();
        Assert.Equal(5, lines.Count);   // three times, then at 10 and 100
        Assert.EndsWith("Could not find Title on Row", lines[0]);   // without Avalonia's padding
        Assert.Contains("(said 10 times now)", lines[3]);
        Assert.Contains("(said 100 times now)", lines[4]);
    }

    [Fact]
    public void Only_the_newest_runs_are_kept()
    {
        for (var i = 0; i < SessionLog.Kept + 5; i++)
            new SessionLog(_dir, new DateTime(2026, 1, 1).AddMinutes(i)).Dispose();

        var kept = Directory.GetFiles(_dir, "session-*.log").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(SessionLog.Kept, kept.Count);
        Assert.Equal("session-2026-01-01_00-05-00.log", kept[0]);
    }

    [Fact]
    public void A_log_that_cant_be_written_doesnt_stop_the_app()
    {
        var blocked = Path.Combine(_dir, "logs");
        File.WriteAllText(blocked, "a file where the folder should be");

        using var log = new SessionLog(blocked);
        log.Info("App", "still running");

        Assert.Contains(log.Recent(), l => l.EndsWith("still running", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_runs_in_the_same_second_get_a_file_each()
    {
        var now = new DateTime(2026, 10, 15, 8, 0, 0);
        using var a = new SessionLog(_dir, now);
        using var b = new SessionLog(_dir, now);
        Assert.NotEqual(a.Path, b.Path);
    }

    [Fact]
    public void Avalonia_warnings_go_into_the_log_with_their_values()
    {
        using var log = new SessionLog(_dir);
        var sink = new SessionLog.Sink(log, next: null);

        Assert.True(sink.IsEnabled(LogEventLevel.Warning, "Binding"));
        Assert.False(sink.IsEnabled(LogEventLevel.Debug, "Layout"));
        sink.Log(LogEventLevel.Warning, "Binding", this, "Could not find {Property} on {Type}", "Title", "Row");
        sink.Log(LogEventLevel.Information, "Layout", null, "Measured {Count}", 3);

        var recent = log.Recent();
        Assert.Contains(recent, l => l.EndsWith("Warning [Binding] Could not find Title on Row (in DiagnosticsTests)"));
        Assert.DoesNotContain(recent, l => l.Contains("Measured"));
    }

    [Theory]
    [InlineData("1.0.0+69948c3d2a1b", "69948c3")]
    [InlineData("1.0.0", null)]
    [InlineData("1.0.0+abc", null)]
    public void A_test_build_names_its_commit(string version, string? commit) =>
        Assert.Equal(commit, App.Commit(version));

    [Fact]
    public void A_note_is_saved_with_its_picture_and_page()
    {
        var path = NoteForClaude.Save(_dir, "  The tiles look squashed  ", "Sounds", [1, 2, 3], new DateTime(2026, 10, 15, 20, 1, 2));

        Assert.Equal(Path.Combine(_dir, "note-2026-10-15_20-01-02.md"), path);
        var text = File.ReadAllText(path);
        Assert.Contains("The tiles look squashed\n", text.ReplaceLineEndings("\n"));
        Assert.Contains("- Page: Sounds", text);
        Assert.Contains("- Picture: note-2026-10-15_20-01-02.png", text);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(_dir, "note-2026-10-15_20-01-02.png")));
    }

    [Fact]
    public void A_half_written_note_is_kept_until_it_is_saved_or_dropped()
    {
        Assert.Null(NoteForClaude.ReadDraft(_dir));
        NoteForClaude.KeepDraft(_dir, "The galaxy jumps when I");
        Assert.Equal("The galaxy jumps when I", NoteForClaude.ReadDraft(_dir));   // offered back after a restart
        NoteForClaude.ClearDraft(_dir);
        Assert.Null(NoteForClaude.ReadDraft(_dir));
    }

    [Fact]
    public void Writing_a_note_puts_up_a_sign_with_the_apps_id()
    {
        NoteForClaude.Writing(_dir, true);
        Assert.Equal(Environment.ProcessId.ToString(), File.ReadAllText(Path.Combine(_dir, NoteForClaude.WritingFile)));
        NoteForClaude.Writing(_dir, false);
        Assert.False(File.Exists(Path.Combine(_dir, NoteForClaude.WritingFile)));
    }

    [Fact]
    public void A_note_without_words_or_picture_still_says_so()
    {
        var text = File.ReadAllText(NoteForClaude.Save(_dir, "", "", null, new DateTime(2026, 10, 15)));

        Assert.Contains("(no words)", text);
        Assert.Contains("- Page: ?", text);
        Assert.DoesNotContain("Picture:", text);
        Assert.Empty(Directory.GetFiles(_dir, "*.png"));
    }
}

/// <summary>The freeze watchdog and the jobs' lines in the run log.</summary>
public sealed class WatchdogTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("mo-watchdog-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [global::Avalonia.Headless.XUnit.AvaloniaFact]
    public void A_long_freeze_names_the_code_the_window_was_stuck_in()
    {
        // On 2026-10-22 a 5 s freeze said only "waiting (Executive)": now the line says which code it was in.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The stack is read on Windows only");
        var log = SessionLog.Start(_dir);
        var jobs = new MusicOrganiser.ViewModels.ActivityFeed { Linger = TimeSpan.Zero, FadeOut = TimeSpan.Zero };
        using (new UiWatchdog(jobs, () => "Sound Lab"))
        {
            HeldUpByAFreezeTest();
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!log.Recent().Any(l => l.Contains("[Freeze] The app didn't respond")) && DateTime.UtcNow < until)
            {
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(20);
            }
        }

        var line = log.Recent().Single(l => l.Contains("[Freeze] The app didn't respond"));
        Assert.Contains("the window's thread was in ", line);
        Assert.Contains("WatchdogTests.HeldUpByAFreezeTest", line);
        log.Dispose();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void HeldUpByAFreezeTest() => Thread.Sleep(2500);

    [global::Avalonia.Headless.XUnit.AvaloniaFact]
    public void A_freeze_is_logged_with_the_page_and_the_job_running()
    {
        var log = SessionLog.Start(_dir);
        var jobs = new MusicOrganiser.ViewModels.ActivityFeed { Linger = TimeSpan.Zero, FadeOut = TimeSpan.Zero };
        var early = jobs.Start("Startup scan");   // running before the watchdog began, as the app's first scan is
        using (new UiWatchdog(jobs, () => "Plugins"))
        {
            var job = jobs.Start("Scanning plugins", "VST3");
            Thread.Sleep(700);   // the UI thread (this test's) stuck, as a slow job on it would
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!log.Recent().Any(l => l.Contains("[Freeze]")) && DateTime.UtcNow < until)
            {
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(20);
            }
            job.Finish("Scanned plugins");
            jobs.Done("Note saved for Claude");
        }
        early.Finish("Startup scan done");

        var recent = log.Recent();
        Assert.Contains(recent, l => l.Contains("[Job] Started: Scanning plugins (VST3)"));
        Assert.Contains(recent, l => l.Contains("[Freeze] The app didn't respond for") && l.Contains("on Plugins, while running: ") && l.Contains("Startup scan") && l.Contains("Scanning plugins") && l.Contains("[memory clean-up: "));
        Assert.Contains(recent, l => l.Contains("[Job] Done: Scanned plugins after"));
        // A one-off notice is one line, not a start and an end.
        Assert.DoesNotContain(recent, l => l.Contains("Started: Note saved for Claude"));
        Assert.Contains(recent, l => l.EndsWith("[Job] Done: Note saved for Claude", StringComparison.Ordinal));
        log.Dispose();
    }

    [AvaloniaFact]
    public void An_error_that_keeps_happening_is_shown_once_not_again_each_time_its_box_closes()
    {
        // Review: an error from a timer or drawing brought its box straight back each time OK was pressed.
        var app = (App)Application.Current!;
        var tick = Thrown(() => throw new InvalidOperationException("Made-up timer error"));
        Assert.True(app.ShouldShow(tick, 1_000));
        app.Shown(App.Key(tick));
        Assert.False(app.ShouldShow(tick, 1_016));
        Assert.False(app.ShouldShow(tick, 1_032));
        // Once it has stayed away a while (a button pressed again), it says so again.
        Assert.True(app.ShouldShow(tick, 1_032 + App.QuietMs));
    }

    [AvaloniaFact]
    public void Different_bugs_with_the_same_words_each_get_their_box()
    {
        // Review: matching on the words alone hid a second, unrelated NullReferenceException (and its stack) for the run.
        var app = (App)Application.Current!;
        var projects = Thrown(() => throw new NullReferenceException());
        var soundLab = Thrown(() => throw new NullReferenceException());
        Assert.Equal(projects.Message, soundLab.Message);
        Assert.True(app.ShouldShow(projects, 5_000));
        app.Shown(App.Key(projects));
        Assert.True(app.ShouldShow(soundLab, 5_010));
    }

    [AvaloniaFact]
    public void An_error_from_before_the_window_showed_gets_its_box_next_time()
    {
        // Review: an error raised before the window was up was counted as seen, so its box never came.
        var app = (App)Application.Current!;
        var early = Thrown(() => throw new InvalidOperationException("Made-up startup error"));
        Assert.True(app.ShouldShow(early, 9_000));   // no window yet: the box never goes up
        Assert.True(app.ShouldShow(early, 9_100));
    }

    static Exception Thrown(Action action)
    {
        try { action(); }
        catch (Exception e) { return e; }
        throw new InvalidOperationException("Nothing was thrown");
    }
}
