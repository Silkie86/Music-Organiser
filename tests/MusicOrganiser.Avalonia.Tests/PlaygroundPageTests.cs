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

        // The track's Mute (the inspector's Octave + went on 22 Oct: its values are dragged now).
        var row = p.Layers[^1];
        var mute = window.GetVisualDescendants().OfType<Button>().First(b => b.Command == row.MuteCommand);
        var at = mute.TranslatePoint(new Point(mute.Bounds.Width / 2, mute.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Settle();
        Assert.True(row.Muted);
        Assert.True(mute.IsFocused);   // the button keeps the keyboard, as a clicked button does

        void Space()
        {
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Settle();
        }

        Space();
        Assert.True(p.IsPlaying);
        Assert.True(row.Muted);
        Space();
        Assert.False(p.IsPlaying);
        Assert.True(row.Muted);
    }

    [AvaloniaFact]
    public void The_inspectors_values_change_by_dragging_up_or_down_or_typing()
    {
        // Solomon, 22 Oct: folding sections, values dragged or typed, no − and + buttons.
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
        var semis = window.GetVisualDescendants().OfType<DragValue>().Single(d => d.UpCommand == p.SelectedClip.UpCommand);
        var at = semis.TranslatePoint(new Point(semis.Bounds.Width / 2, semis.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(at - new Point(0, 17));   // up two steps
        window.MouseUp(at - new Point(0, 17), MouseButton.Left);
        Settle();
        Assert.Equal(2, clip.Transpose);
        Assert.Equal("+2 st", p.SelectedClip.SemitoneText);

        // Double-click and type: 5 semitones.
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Settle();
        var box = semis.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.True(box.IsVisible);
        box.Text = "5";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "");
        Settle();
        Assert.Equal(5, clip.Transpose);
        Assert.False(box.IsVisible);
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
        public void Play(float[] mono, int sampleRate, string key, double startSeconds = 0) => Playing = key;
        public void Stop() => Playing = null;
        public void Dispose() { }
    }
}
