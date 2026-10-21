using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;
using MusicOrganiser.Avalonia.Views;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>The real Playground page on the headless screen, with a library of its own in a temp folder and no sound.</summary>
public sealed class PlaygroundPageTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("mo-playground-page-").FullName;
    readonly LibraryDb _db;
    readonly SoundLibrary _soundDb;
    readonly SoundOrganiser _sounds;

    public PlaygroundPageTests()
    {
        Ui.Current = new SilentUi();
        UiWork.InBackground = false;
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
    }

    static void Settle()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Space_plays_after_a_clip_button_is_clicked_and_never_clicks_it_again()
    {
        // F12 note, 20 Oct: after Octave + was clicked, every Space moved the clip up another octave.
        var main = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        p.PutChords("C | F |");
        p.WriteChordsCommand.Execute(null);
        var track = p.Layers[^1].Layer;
        p.SelectClipCommand.Execute(new ClipPick(track, track.Clips[0]));
        var window = new Window { Content = new PlaygroundView { DataContext = p }, Width = 1400, Height = 900 };
        window.Show();
        Settle();

        var clip = p.SelectedClip!.Clip;
        var up = window.GetVisualDescendants().OfType<Button>().Single(b => b.Command == p.SelectedClip.OctaveUpCommand);
        var at = up.TranslatePoint(new Point(up.Bounds.Width / 2, up.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Settle();
        Assert.Equal(12, clip.Transpose);
        Assert.True(up.IsFocused);   // the button keeps the keyboard, as a clicked button does

        void Space()
        {
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Settle();
        }

        Space();
        Assert.True(p.IsPlaying);
        Assert.Equal(12, clip.Transpose);
        Space();
        Assert.False(p.IsPlaying);
        Assert.Equal(12, clip.Transpose);
    }

    [AvaloniaFact]
    public void Dragging_along_the_piano_rolls_bar_numbers_picks_those_bars_of_the_clip()
    {
        // F12 note, 20 Oct: "should be able to select a part of a clip and have suggestions to replace that".
        var main = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        p.PutChords("C | F | G | C");
        p.WriteChordsCommand.Execute(null);
        var track = p.Layers.Single(l => !l.IsWritten).Layer;   // the chords themselves: a written part follows them, so isn't swapped
        p.SelectClipCommand.Execute(new ClipPick(track, track.Clips[0]));
        p.RightTab = 2;
        var window = new Window { Content = new PlaygroundView { DataContext = p }, Width = 1400, Height = 900 };
        window.Show();
        Settle();

        var roll = window.GetVisualDescendants().OfType<PianoRollEditor>().Single();
        Assert.NotNull(roll.StrengthOf);   // the notes are coloured by how well they fit
        Assert.NotNull(roll.FitGuide);
        var drawing = new global::Avalonia.Media.DrawingGroup();
        using (var dc = drawing.Open()) roll.Render(dc);   // and draw so
        Assert.NotEmpty(drawing.Children);
        // The roll shows the clip's 4 bars after its keyboard (34 px): along the bar numbers from bar 2 to bar 3.
        double BarX(double bar) => 34 + (roll.Bounds.Width - 34) * bar / 4;
        Point At(double bar) => roll.TranslatePoint(new Point(BarX(bar), 8), window)!.Value;
        window.MouseDown(At(1.05), MouseButton.Left);
        window.MouseMove(At(1.5), RawInputModifiers.LeftMouseButton);
        window.MouseMove(At(2.95), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(2.95), MouseButton.Left);
        Settle();
        const long Bar = 384;
        Assert.Equal(new RollRange(Bar, 3 * Bar), p.Loop);
        Assert.Equal(new RollRange(Bar, 3 * Bar), roll.Section);
        Assert.Same(track.Clips[0], p.SwapTarget!.Clip);
        Assert.Equal(0, p.RightTab);   // Suggest, to fill them

        // A right-click there lets go.
        window.MouseDown(At(2), MouseButton.Right);
        window.MouseUp(At(2), MouseButton.Right);
        Settle();
        Assert.Null(p.Loop);
        Assert.Null(roll.Section);

        // F12 note, 21 Oct: "right clicking should remove the loop/selection". Off the notes in the roll it does too.
        void Pick()
        {
            p.SetLoopCommand.Execute(new RollRange(Bar, 3 * Bar));
            Settle();
            Assert.NotNull(p.Loop);
        }
        void RightClick(Control on, Point at)
        {
            var spot = on.TranslatePoint(at, window)!.Value;
            window.MouseDown(spot, MouseButton.Right);
            window.MouseUp(spot, MouseButton.Right);
            Settle();
        }
        Pick();
        var notes = track.Clips[0].Notes.Count;
        RightClick(roll, new Point(roll.Bounds.Width - 3, roll.Bounds.Height - 3));
        Assert.Null(p.Loop);
        Assert.Equal(notes, track.Clips[0].Notes.Count);   // no note was there to remove
        // And anywhere on the timeline, over a clip or not.
        var timeline = window.GetVisualDescendants().OfType<TimelineView>().Single();
        Pick();
        RightClick(timeline, new Point(timeline.Bounds.Width - 3, timeline.Bounds.Height - 3));
        Assert.Null(p.Loop);
    }

    [AvaloniaFact]
    public void With_nothing_highlighted_play_goes_round_at_the_end_of_the_last_clip()
    {
        // F12 note, 21 Oct: "it should automatically loop at the end of all the clips", not run on into empty bars.
        var main = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        var p = main.Playground;
        p.Bars = 8;
        p.SetLoopCommand.Execute(new RollRange(0, 2 * 384));
        p.PutChords("C | F");
        p.SetLoopCommand.Execute(null);
        Assert.Equal(8, p.Bars);
        p.PlayCommand.Execute(null);
        Settle();
        Assert.True(p.IsPlaying);
        var seconds = Silence.Last!.Value.Samples / (double)Silence.Last.Value.Rate;
        Assert.Equal(2 * 4 * 60.0 / p.Tempo, seconds, 2);   // two bars, not eight
        p.StopPlaying();
    }

    /// <summary>The app's Avalonia screens, with a player that makes no sound.</summary>
    sealed class SilentUi : IPlatformUi
    {
        readonly AvaloniaUi _ui = new();
        public IItemsView CreateView(IList items) => _ui.CreateView(items);
        public IUiTimer CreateTimer(TimeSpan interval, Action tick) => _ui.CreateTimer(interval, tick);
        public IAudioPlayer CreateAudioPlayer() => new Silence();
        public Task<string?> PickFolder(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFile(string title, string filterName, string extension) => Task.FromResult<string?>(null);
        public Task<string?> PickProgram(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveFile(string fileName, string filterName, string extension, string? initialFolder) => Task.FromResult<string?>(null);
        public Task<bool> Confirm(string message, string title) => Task.FromResult(true);
        public void CopyText(string text) { }
        public void ShowMidi(MidiPreviewViewModel vm) { }
        public void ShowPresets(PresetsViewModel vm) { }
    }

    sealed class Silence : IAudioPlayer
    {
        public event Action? Stopped { add { } remove { } }
        public string? Playing { get; private set; }
        public double? Position => Playing is null ? null : 0;
        public void Seek(double seconds) { }
        public void Play(string path, double startSeconds = 0) => Playing = path;
        /// <summary>The last sound played: how long it was.</summary>
        public static (int Samples, int Rate)? Last;
        public void Play(float[] mono, int sampleRate, string key, double startSeconds = 0)
        {
            Playing = key;
            Last = (mono.Length, sampleRate);
        }
        public void Stop() => Playing = null;
        public void Dispose() { }
    }
}
