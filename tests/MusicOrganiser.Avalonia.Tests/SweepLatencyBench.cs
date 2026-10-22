using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using MusicOrganiser.Avalonia.Audio;
using MusicOrganiser.Avalonia.Views;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>
/// A virtual fast sweep across a galaxy the size of Solomon's library (450,962 stars), timing each step from the
/// mouse being over a star to OpenAL starting to mix its sound: the mouse reaching the app (held up while a frame
/// draws), waiting its turn (the sweep gap), the preview thread starting, opening the file and queueing it, and
/// OpenAL's next mix. Not a test of right or wrong, so it only runs when asked:
/// MO_SWEEP_BENCH=1 ALSOFT_DRIVERS=null dotnet test --filter SweepLatencyBench (report in MO_SWEEP_REPORT, or the output).
/// </summary>
public sealed class SweepLatencyBench
{
    const int Width = 1600, Height = 900;
    const int Stars = 450_962, Files = 1500;
    const double MoveEveryMs = 8;          // a 125 Hz mouse; Windows sends the newest position when the app is busy
    const double FrameEveryMs = 1000 / 60.0;
    const double TimerTickMs = 15.6;       // Windows' default timer tick, which the sweep's DispatcherTimer runs on

    sealed class Command(Action<Sound> run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run((Sound)parameter!);
    }

    sealed class Ahead(Action<IReadOnlyList<Sound>> run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run((IReadOnlyList<Sound>)parameter!);
    }

    /// <summary>The real player, with the moments Play was called and returned noted for each sound.</summary>
    sealed class TimedPlayer(OpenAlPlayer inner, Stopwatch clock) : IAudioPlayer
    {
        public readonly Dictionary<string, (double Called, double Returned)> Plays = [];
        public event Action? Stopped { add => inner.Stopped += value; remove => inner.Stopped -= value; }
        public string? Playing => inner.Playing;
        public double? Position => inner.Position;
        public void Seek(double seconds) => inner.Seek(seconds);
        public void Play(string path, double startSeconds = 0)
        {
            var called = clock.Elapsed.TotalMilliseconds;
            inner.Play(path, startSeconds);
            lock (Plays) Plays[path] = (called, clock.Elapsed.TotalMilliseconds);
        }
        public void Play(float[] mono, int sampleRate, string key, double startSeconds = 0) => inner.Play(mono, sampleRate, key, startSeconds);
        public void Stop() => inner.Stop();
        public void Dispose() => inner.Dispose();
    }

    [AvaloniaFact]
    public void Time_a_fast_sweep()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MO_SWEEP_BENCH") == "1", "Timing only: set MO_SWEEP_BENCH=1");
        var report = new StringBuilder();
        var folder = Directory.CreateTempSubdirectory("mo-sweep-").FullName;
        var paths = MakeSounds(folder);
        var stars = MakeStars(paths);

        var clock = Stopwatch.StartNew();
        var player = new TimedPlayer(new OpenAlPlayer(), clock);
        player.Play(paths[0]);   // opens the output first, as the app has by the time you sweep
        Thread.Sleep(300);
        player.Stop();
        var previews = new PreviewWorker(player, (_, _) => { });
        var asked = new List<(Sound Sound, double At)>();
        var canvas = new GalaxyCanvas
        {
            SweepMs = int.TryParse(Environment.GetEnvironmentVariable("MO_SWEEP_GAP"), out var gap) ? gap : 90,
            SweepCommand = new Command(s => { asked.Add((s, clock.Elapsed.TotalMilliseconds)); previews.Play(s.Path); }),
            AheadCommand = new Ahead(sounds => previews.Warm(sounds.Select(s => s.Path))),
        };
        var window = new Window { Content = canvas, Width = Width, Height = Height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        canvas.Stars = stars;
        var until = DateTime.UtcNow.AddSeconds(60);
        while (canvas.StarCount < Stars && DateTime.UtcNow < until) { Thread.Sleep(20); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal(Stars, canvas.StarCount);
        Thread.Sleep(1200);   // past the stars' drift-in

        // The first mixed moment of each sound, watched from a thread of its own every half millisecond.
        var mixed = new Dictionary<string, double>();
        var watching = true;
        var watcher = new Thread(() =>
        {
            while (Volatile.Read(ref watching))
            {
                if (player.Playing is { } p && player.Position is > 0 && !mixed.ContainsKey(p))
                    lock (mixed) mixed[p] = clock.Elapsed.TotalMilliseconds;
                Thread.SpinWait(2000);
            }
        }) { IsBackground = true, Priority = ThreadPriority.Highest };
        watcher.Start();

        var zooms = (Environment.GetEnvironmentVariable("MO_SWEEP_ZOOMS") ?? "1,6").Split(',').Select(z => double.Parse(z, CultureInfo.InvariantCulture));
        foreach (var zoom in zooms)
        {
            canvas.Fit();
            canvas.SettleNow();
            if (zoom > 1) { canvas.ZoomBy(zoom); canvas.SettleNow(); }
            for (var i = 0; i < 3; i++) { window.CaptureRenderedFrame(); Dispatcher.UIThread.RunJobs(); }
            // Twice: the first warms up (the code compiling, the files' first reads); the second is reported.
            for (var round = 0; round < 2; round++)
            {
                asked.Clear();
                lock (mixed) mixed.Clear();
                lock (player.Plays) player.Plays.Clear();
                var lines = new StringBuilder();
                Sweep(window, canvas, clock, zoom, asked, player, mixed, lines, stars);
                if (round == 1) report.Append(lines);
                previews.Stop();
                Thread.Sleep(300);
            }
        }
        watching = false;
        watcher.Join();
        player.Dispose();

        if (OpenAlPlayer.OutputTiming is var (period, delay))
            report.Insert(0, $"OpenAL output here: mixes every {period:0.0} ms; says mixed sound comes out after {delay:0.0} ms\n\n");
        var text = report.ToString();
        if (Environment.GetEnvironmentVariable("MO_SWEEP_REPORT") is { Length: > 0 } file) File.WriteAllText(file, text);
        TestContext.Current.SendDiagnosticMessage(text);
        Console.WriteLine(text);
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    /// <summary>
    /// One sweep, left to right through the middle at 1,500 pixels a second, run the way the app's window runs: mouse
    /// moves, a frame drawn every sixtieth of a second, the sweep timer's tick, on one thread, so a slow frame holds
    /// up the mouse just as it does in the app.
    /// </summary>
    static void Sweep(Window window, GalaxyCanvas canvas, Stopwatch clock, double zoom, List<(Sound Sound, double At)> asked,
                      TimedPlayer player, Dictionary<string, double> mixed, StringBuilder report, List<GalaxyStar> stars)
    {
        const double speed = 1.5;   // pixels a millisecond
        const double durationMs = 1500;
        Point Origin(Point p) => canvas.TranslatePoint(p, window)!.Value;
        var y = Height / 2.0 + 13;
        var x0 = 50.0;
        Point At(double ms) => new(x0 + Math.Min(ms, durationMs) * speed, y);

        var moves = new List<(double At, double X)>();
        var frames = new List<double>();
        List<double> moveMs = [], tickMs = [], jobsMs = [];
        var start = clock.Elapsed.TotalMilliseconds;
        double Now() => clock.Elapsed.TotalMilliseconds - start;
        window.MouseDown(Origin(At(0)), MouseButton.Left);
        moves.Add((Now(), At(0).X));
        double nextMove = MoveEveryMs, nextFrame = 0, nextTick = TimerTickMs;
        while (Now() < durationMs + 200)
        {
            var now = Now();
            if (now >= nextMove && now <= durationMs)
            {
                // Like Windows: one move with wherever the mouse is now, however long the app was busy.
                var p = At(now);
                var t0 = Now();
                window.MouseMove(Origin(p), RawInputModifiers.LeftMouseButton);
                moveMs.Add(Now() - t0);
                moves.Add((now, p.X));
                nextMove = now + MoveEveryMs;
            }
            if (now >= nextFrame)
            {
                var started = Now();
                window.CaptureRenderedFrame();
                frames.Add(Now() - started);
                nextFrame = Math.Max(nextFrame + FrameEveryMs, Now());
            }
            if (now >= nextTick)
            {
                var t1 = Now();
                canvas.NextSweep();
                tickMs.Add(Now() - t1);
                nextTick += TimerTickMs;
            }
            var t2 = Now();
            Dispatcher.UIThread.RunJobs();
            jobsMs.Add(Now() - t2);
            Thread.SpinWait(200);
        }
        window.MouseUp(Origin(At(durationMs)), MouseButton.Left);
        Thread.Sleep(150);

        // For each sound asked for: when the mouse was over its star (from the path), when the app saw a move that
        // passed it, when it was asked for, when the preview called Play, when Play returned, and when OpenAL mixed it.
        var rows = new List<double[]>();
        foreach (var (sound, askedAt) in asked)
        {
            var star = stars[(int)sound.Id];
            var path = sound.Path;
            var sx = canvas.ToScreen(star.X, star.Y).X;
            var over = (sx - x0) / speed;
            if (over < 0) continue;
            var seen = moves.FirstOrDefault(m => m.X >= sx - 14).At;   // the first move that reached it (hit radius 14 px)
            (double Called, double Returned) play;
            lock (player.Plays) if (!player.Plays.TryGetValue(path, out play)) continue;
            double mix;
            lock (mixed) if (!mixed.TryGetValue(path, out mix)) continue;
            rows.Add([over, Math.Max(seen, over), askedAt - start, play.Called - start, play.Returned - start, mix - start]);
        }
        var skipped = asked.Count - rows.Count;
        var lines = new StringBuilder();
        lines.AppendLine(CultureInfo.InvariantCulture, $"## Zoom ×{zoom}: {asked.Count} sounds asked for, {rows.Count} heard ({skipped} skipped: a newer one came first)");
        lines.AppendLine(CultureInfo.InvariantCulture, $"Frames: {frames.Count}, average {frames.Average():0.0} ms, slowest {frames.Max():0.0} ms");
        lines.AppendLine(CultureInfo.InvariantCulture, $"UI thread: mouse moves {moveMs.Sum():0} ms (slowest {moveMs.DefaultIfEmpty().Max():0.0}), sweep ticks {tickMs.Sum():0} ms (slowest {tickMs.DefaultIfEmpty().Max():0.0}), other jobs {jobsMs.Sum():0} ms (slowest {jobsMs.DefaultIfEmpty().Max():0.0}), frames {frames.Sum():0} ms");
        lines.AppendLine("Slowest mouse moves (at ms: took ms): " + string.Join(", ", moveMs.Select((m, i) => (m, i)).OrderByDescending(t => t.m).Take(6)
            .Select(t => $"{moves[t.i + 1].At:0}: {t.m:0}")));
        lines.AppendLine(CultureInfo.InvariantCulture, $"Mouse moves the app saw: {moves.Count} in {durationMs:0} ms (one every {durationMs / moves.Count:0.0} ms; 8 ms when nothing holds it up)");
        string Avg(int a, int b) => rows.Count == 0 ? "-" : $"{rows.Average(r => r[b] - r[a]):0.0} ms (slowest {rows.Max(r => r[b] - r[a]):0.0})";
        lines.AppendLine($"1. Mouse over the star -> the app sees the move: {Avg(0, 1)}");
        lines.AppendLine($"2. App sees it -> sound asked for (waiting its turn): {Avg(1, 2)}");
        lines.AppendLine($"3. Asked -> preview thread calls Play: {Avg(2, 3)}");
        lines.AppendLine($"4. Play: open the file, read and queue 0.4 s: {Avg(3, 4)}");
        lines.AppendLine($"5. Queued -> OpenAL mixes its first sound: {Avg(4, 5)}");
        lines.AppendLine($"Total, mouse over the star -> mixed: {Avg(0, 5)}");
        lines.AppendLine("(After step 5 comes OpenAL's output buffer and Windows' and the interface's own: measured on the PC, not here.)");
        lines.AppendLine();
        lines.AppendLine("over_ms,seen_ms,asked_ms,play_called_ms,play_returned_ms,mixed_ms");
        foreach (var r in rows) lines.AppendLine(string.Join(',', r.Select(v => v.ToString("0.0", CultureInfo.InvariantCulture))));
        lines.AppendLine();
        report.Append(lines);
    }

    /// <summary>Short WAVs like a drum library's: 24-bit stereo at 44.1 kHz, 0.1 to 1.5 s.</summary>
    static string[] MakeSounds(string folder)
    {
        var rng = new Random(3);
        var paths = new string[Files];
        for (var i = 0; i < Files; i++)
        {
            var path = Path.Combine(folder, $"hit {i:0000}.wav");
            var frames = (int)(44100 * (0.1 + rng.NextDouble() * 1.4));
            using var w = new BinaryWriter(File.Create(path));
            var bytes = frames * 6;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(44100); w.Write(44100 * 6); w.Write((short)6); w.Write((short)24);
            w.Write("data"u8); w.Write(bytes);
            var data = new byte[bytes];
            for (var f = 0; f < frames; f++)
            {
                var v = (int)(Math.Sin(f * 0.05 * (1 + i % 7)) * Math.Exp(-f / 4410.0) * 4_000_000);
                for (var c = 0; c < 2; c++)
                {
                    data[f * 6 + c * 3] = (byte)v;
                    data[f * 6 + c * 3 + 1] = (byte)(v >> 8);
                    data[f * 6 + c * 3 + 2] = (byte)(v >> 16);
                }
            }
            w.Write(data);
            paths[i] = path;
        }
        return paths;
    }

    /// <summary>450,962 stars in clouds, like a mapped library, each a sound from the files above.</summary>
    static List<GalaxyStar> MakeStars(string[] paths)
    {
        var rng = new Random(11);
        var centres = Enumerable.Range(0, 14).Select(_ => (X: 0.15 + rng.NextDouble() * 0.7, Y: 0.15 + rng.NextDouble() * 0.7, R: 0.03 + rng.NextDouble() * 0.09)).ToArray();
        var stars = new List<GalaxyStar>(Stars);
        for (var i = 0; i < Stars; i++)
        {
            var c = centres[i % centres.Length];
            double u = 1 - rng.NextDouble(), v = rng.NextDouble();
            var g = Math.Sqrt(-2 * Math.Log(u));
            var x = Math.Clamp(c.X + g * Math.Cos(2 * Math.PI * v) * c.R, 0.01, 0.99);
            var y = Math.Clamp(c.Y + g * Math.Sin(2 * Math.PI * v) * c.R, 0.01, 0.99);
            stars.Add(new GalaxyStar(new Sound { Id = i, Path = paths[i % paths.Length] }, (float)x, (float)y, 0xFF80A0FF, true));
        }
        return stars;
    }
}
