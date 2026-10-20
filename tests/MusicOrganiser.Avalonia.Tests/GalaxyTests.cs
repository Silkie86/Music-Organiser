using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using MusicOrganiser.Avalonia.Views;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>Sweeping across the sound galaxy with the left button held.</summary>
public sealed class GalaxyTests
{
    sealed class Record(List<string> played) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => played.Add(((Sound)parameter!).Name);
    }

    [AvaloniaFact]
    public void A_quick_sweep_plays_the_stars_it_passed_not_only_where_the_mouse_landed()
    {
        var played = new List<string>();
        // Ten stars in a row, well apart, and nothing else.
        var stars = Enumerable.Range(0, 10)
            .Select(i => new GalaxyStar(new Sound { Id = i, Path = $"/lib/Star {i}.wav" }, 0.05f + i * 0.1f, 0.5f, 0xFF8080FF, true))
            .ToList();
        var canvas = new GalaxyCanvas { Stars = stars, SweepCommand = new Record(played) };
        var window = new Window { Content = canvas, Width = 600, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Point At(int i) => canvas.TranslatePoint(canvas.ToScreen(stars[i].X, stars[i].Y), window)!.Value;

        // Press on the first star, then one big move to the fourth, as a quick flick arrives.
        window.MouseDown(At(0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(At(3));
        Dispatcher.UIThread.RunJobs();
        // A passed star waits its turn so it's heard; the newest is kept.
        var until = DateTime.UtcNow.AddSeconds(2);
        while (played.LastOrDefault() != "Star 3" && DateTime.UtcNow < until)
        {
            Thread.Sleep(20);
            canvas.NextSweep();   // the timer's tick: headless tests don't run timers on their own
        }
        window.MouseUp(At(3), MouseButton.Left);

        // Star 0 on the press, then the star the mouse reached last once Star 0 has had its moment (stars passed in
        // between may play too, depending on timing), in order, ending where the mouse landed.
        Assert.Equal("Star 0", played[0]);
        Assert.Equal("Star 3", played[^1]);
        Assert.True(played.Count >= 2, string.Join(", ", played));
        Assert.Equal(played.Order(), played);
    }

    [AvaloniaFact]
    public void A_big_librarys_stars_are_sorted_in_the_background_and_then_answer_the_mouse()
    {
        var played = new List<string>();
        // 30,000 stars: sorting them waits off the window's thread, so setting them returns at once.
        var stars = Enumerable.Range(0, 30_000)
            .Select(i => new GalaxyStar(new Sound { Id = i, Path = $"/lib/Star {i}.wav" }, i < 10 ? 0.05f + i * 0.1f : 0.02f + i % 97 * 0.01f,
                                        i < 10 ? 0.5f : 0.95f, 0xFF8080FF, true))
            .ToList();
        var canvas = new GalaxyCanvas { SweepCommand = new Record(played) };
        var window = new Window { Content = canvas, Width = 600, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        canvas.Stars = stars;
        var until = DateTime.UtcNow.AddSeconds(10);
        var at = canvas.TranslatePoint(canvas.ToScreen(stars[4].X, stars[4].Y), window)!.Value;
        while (played.Count == 0 && DateTime.UtcNow < until)
        {
            Thread.Sleep(20);
            Dispatcher.UIThread.RunJobs();
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
        }
        Assert.Equal("Star 4", Assert.Single(played.Distinct()));
        window.Close();
    }

    [AvaloniaFact]
    public void A_galaxy_taken_off_screen_lets_go_of_the_graphics_cards_notices()
    {
        static int Listeners() =>
            (typeof(GalaxyGpu).GetField("Changed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
        var before = Listeners();
        var window = new Window { Content = new GalaxyCanvas(), Width = 300, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before + 1, Listeners());

        window.Content = null;   // as when you go to another page
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, Listeners());
        window.Close();
    }
}
