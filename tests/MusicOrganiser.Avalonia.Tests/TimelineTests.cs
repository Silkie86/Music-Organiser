using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Avalonia.Views;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.ViewModels;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>The Playground's timeline: picking clips and highlighting bars with the mouse.</summary>
public sealed class TimelineTests
{
    const int Bar = 4 * PhraseAnalyser.Ppq;

    sealed record Done(List<RollRange?> Highlights, List<ClipPick> Picks);

    // Two tracks over 8 bars, 100 px a bar: Keys plays bars 1–4, Bass bars 5–8.
    static (Window Window, TimelineView View, Done Done, PlaygroundLayer Keys) Timeline()
    {
        var keys = new PlaygroundLayer { Name = "Keys", Role = MidiRoles.Chords, Clips = [PlaygroundClip.Of("Keys", MidiRoles.Chords, [new NoteEvent(0, Bar, 60, 90)], Bar, 0, 4L * Bar)] };
        var bass = new PlaygroundLayer { Name = "Bass", Role = MidiRoles.Bass, Clips = [PlaygroundClip.Of("Bass", MidiRoles.Bass, [new NoteEvent(0, Bar, 36, 90)], Bar, 4L * Bar, 4L * Bar)] };
        var done = new Done([], []);
        var view = new TimelineView
        {
            Width = 800, Length = 8L * Bar,
            Tracks = [new LayerRow(null!, keys), new LayerRow(null!, bass)],
            HighlightCommand = new RelayCommand<RollRange?>(r => done.Highlights.Add(r)),
            SelectCommand = new RelayCommand<ClipPick>(p => done.Picks.Add(p!)),
        };
        var window = new Window { Content = view, Width = 800, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, done, keys);
    }

    static double RowY(int row) => TimelineView.Top + (row + 0.5) * TimelineView.RowHeight;

    [AvaloniaFact]
    public void Clicking_a_clip_picks_it()
    {
        var (window, _, done, keys) = Timeline();
        window.MouseDown(new Point(150, RowY(0)), MouseButton.Left);
        window.MouseUp(new Point(150, RowY(0)), MouseButton.Left);
        Assert.Same(keys.Clips[0], Assert.Single(done.Picks).Clip);
        Assert.Empty(done.Highlights);
    }

    [AvaloniaFact]
    public void Dragging_across_empty_bars_highlights_whole_bars_and_a_click_clears_it()
    {
        var (window, _, done, _) = Timeline();
        // From bar 5 to part way through bar 7 on the Keys row, which is empty there.
        window.MouseDown(new Point(420, RowY(0)), MouseButton.Left);
        window.MouseMove(new Point(500, RowY(0)), RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Point(650, RowY(0)), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(650, RowY(0)), MouseButton.Left);
        Assert.Equal(new RollRange(4L * Bar, 7L * Bar), Assert.Single(done.Highlights));

        window.MouseDown(new Point(450, RowY(0)), MouseButton.Left);
        window.MouseUp(new Point(450, RowY(0)), MouseButton.Left);
        Assert.Null(done.Highlights[^1]);
    }
}
