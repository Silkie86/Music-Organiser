using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;
using MusicOrganiser.Avalonia.Views;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>Sound Lab's Rediscover tab on the headless screen, with a few made-up kicks and no sound.</summary>
public sealed class RediscoverPageTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("mo-rediscover-").FullName;
    readonly LibraryDb _db;
    readonly SoundLibrary _soundDb;
    readonly SoundOrganiser _sounds;

    public RediscoverPageTests()
    {
        Ui.Current = new PlaygroundPageTests.SilentUi();
        UiWork.InBackground = false;
        PreviewWorker.InBackground = false;
        _db = new LibraryDb(Path.Combine(_root, "library.db"));
        _soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        _sounds = new SoundOrganiser(_soundDb);
    }

    public void Dispose()
    {
        _sounds.Dispose();
        _soundDb.Dispose();
        _db.Dispose();
        UiWork.InBackground = true;
        PreviewWorker.InBackground = true;
    }

    static void Settle()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    static float[] Kick(int seed)
    {
        const int sr = 44100;
        var x = new float[sr / 2];
        double phase = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var t = (double)i / sr;
            phase += 2 * Math.PI * (50 + (100 + seed * 5) * Math.Exp(-t * 30)) / sr;
            x[i] = (float)(0.9 * Math.Exp(-t * (6 + seed % 3)) * Math.Sin(phase));
        }
        return x;
    }

    /// <summary>Twelve made-up kicks, two of them used, and Sound Lab open on Rediscover with its suggestions in.</summary>
    (MainViewModel Main, MainWindow Window) Open()
    {
        var kicks = Path.Combine(_root, "Samples", "Kicks");
        Directory.CreateDirectory(kicks);
        for (var i = 0; i < 12; i++) WavWriter.Write(Path.Combine(kicks, $"Kick {i:00}.wav"), [Kick(i)], 44100);
        _sounds.Scan([Path.Combine(_root, "Samples")]);
        _sounds.ApplyUsage([(Path.Combine(kicks, "Kick 00.wav"), 3), (Path.Combine(kicks, "Kick 01.wav"), 2)]);

        var main = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 900 };
        window.Show();
        main.GoTo("Sound Lab");
        main.Sounds.ReloadAsync().GetAwaiter().GetResult();
        main.Sounds.TabIndex = SoundsViewModel.RediscoverTab;
        for (var i = 0; i < 50 && (main.Sounds.Rediscover.IsBusy || main.Sounds.Rediscover.Suggestions.Count == 0); i++)
        {
            Thread.Sleep(20);
            Settle();
        }
        Settle();
        Assert.NotEmpty(main.Sounds.Rediscover.Suggestions);
        // Picked once: the list doesn't change under the mouse a moment later (it did, the year box setting off a second pick).
        var picked = main.Sounds.Rediscover.Suggestions;
        for (var i = 0; i < 30; i++)
        {
            Thread.Sleep(20);
            Settle();
        }
        Assert.Same(picked, main.Sounds.Rediscover.Suggestions);
        return (main, window);
    }

    static void Click(Window window, Point at)
    {
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Settle();
    }

    [AvaloniaFact]
    public void A_click_anywhere_on_a_row_plays_it_and_each_row_shows_its_waveform()
    {
        // F12 notes, 22 Oct: "clicking anywhere on the tile should play the sound", "should be able see waveform"
        var (main, window) = Open();
        var rows = window.GetVisualDescendants().OfType<DockPanel>()
            .Where(d => d.DataContext is Suggestion && d.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(rows);
        var row = rows[0];
        var sound = ((Suggestion)row.DataContext!).Sound;
        var wave = row.GetVisualDescendants().OfType<MiniWave>().Single();
        Assert.NotNull(wave.Peaks);
        Assert.True(wave.Bounds.Width > 100);

        // The middle of the line of words, not the ▶ button
        var words = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text is { Length: > 0 } && t.Text == ((Suggestion)row.DataContext).Reason);
        PlaygroundPageTests.Silence.LastPath = null;
        Click(window, words.TranslatePoint(new Point(10, words.Bounds.Height / 2), window)!.Value);
        Assert.Equal(sound.Path, PlaygroundPageTests.Silence.LastPath);
        Assert.Equal(sound, main.Sounds.Selected);

        // A year box beside the type ("should be able to filter by year here")
        var re = main.Sounds.Rediscover;
        Assert.Equal([RediscoverViewModel.AnyYear, DateTime.Now.Year.ToString()], re.Years);
        Assert.Contains(window.GetVisualDescendants().OfType<ComboBox>(), c => c.IsEffectivelyVisible && ReferenceEquals(c.ItemsSource, re.Years));
    }

    [AvaloniaFact]
    public void A_rows_edge_its_waveform_and_the_arrow_keys_play_too()
    {
        // F12 notes, 27 Oct: "sometimes i click and the sound does[n't] play also theres no autoplay",
        // "click on waveform should start sample at cursor position"
        var (main, window) = Open();
        var items = window.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(i => i.DataContext is Suggestion && i.IsEffectivelyVisible).OrderBy(i => i.Bounds.Y).ToList();
        Assert.True(items.Count >= 4);
        Sound SoundOf(ListBoxItem item) => ((Suggestion)item.DataContext!).Sound;

        // The row's edge: the list's own padding above its words, which played nothing before.
        PlaygroundPageTests.Silence.LastPath = null;
        Click(window, items[1].TranslatePoint(new Point(items[1].Bounds.Width / 2, 3), window)!.Value);
        Assert.Equal(SoundOf(items[1]).Path, PlaygroundPageTests.Silence.LastPath);
        Assert.Equal(0, PlaygroundPageTests.Silence.LastStart);

        // Three quarters along a row's waveform: it plays from three quarters of the way in.
        var wave = items[2].GetVisualDescendants().OfType<MiniWave>().Single();
        Click(window, wave.TranslatePoint(new Point(wave.Bounds.Width * 0.75, wave.Bounds.Height / 2), window)!.Value);
        Assert.Equal(SoundOf(items[2]).Path, PlaygroundPageTests.Silence.LastPath);
        Assert.Equal(SoundOf(items[2]), main.Sounds.Selected);
        Assert.InRange(PlaygroundPageTests.Silence.LastStart, SoundOf(items[2]).Seconds * 0.7, SoundOf(items[2]).Seconds * 0.8);

        // Autoplay, as in the Library: the box is here too, and with it on the arrow keys play each row they reach.
        var autoplay = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.IsEffectivelyVisible && c.Content as string == "Autoplay");
        autoplay.IsChecked = true;
        Assert.True(main.Sounds.AutoPlay);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Settle();
        Assert.Equal(SoundOf(items[3]), main.Sounds.Selected);
        Assert.Equal(SoundOf(items[3]).Path, PlaygroundPageTests.Silence.LastPath);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        window.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Settle();
        Assert.Equal(SoundOf(items[2]), main.Sounds.Selected);
        Assert.Equal(SoundOf(items[2]).Path, PlaygroundPageTests.Silence.LastPath);
    }
}
