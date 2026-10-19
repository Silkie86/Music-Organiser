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

    sealed record Done(List<RollRange?> Highlights, List<ClipPick> Picks, List<ClipEdit> Edits, List<long> Cursors);

    // Two tracks over 8 bars, 100 px a bar: Keys plays bars 1–4, Bass bars 5–8.
    static (Window Window, TimelineView View, Done Done, PlaygroundLayer Keys) Timeline()
    {
        var keys = new PlaygroundLayer { Name = "Keys", Role = MidiRoles.Chords, Clips = [PlaygroundClip.Of("Keys", MidiRoles.Chords, [new NoteEvent(0, Bar, 60, 90)], Bar, 0, 4L * Bar)] };
        var bass = new PlaygroundLayer { Name = "Bass", Role = MidiRoles.Bass, Clips = [PlaygroundClip.Of("Bass", MidiRoles.Bass, [new NoteEvent(0, Bar, 36, 90)], Bar, 4L * Bar, 4L * Bar)] };
        var done = new Done([], [], [], []);
        var view = new TimelineView
        {
            Width = 800, Length = 8L * Bar,
            Tracks = [new LayerRow(null!, keys), new LayerRow(null!, bass)],
            HighlightCommand = new RelayCommand<RollRange?>(r => done.Highlights.Add(r)),
            SelectCommand = new RelayCommand<ClipPick>(p => done.Picks.Add(p!)),
            EditCommand = new RelayCommand<ClipEdit>(e => done.Edits.Add(e!)),
            CursorCommand = new RelayCommand<long>(t => done.Cursors.Add(t)),
        };
        var window = new Window { Content = view, Width = 800, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, done, keys);
    }

    static double RowY(int row) => TimelineView.Top + (row + 0.5) * TimelineView.DefaultRowHeight;

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

    static void Drag(Window window, Point from, Point to, RawInputModifiers keys = RawInputModifiers.None)
    {
        window.MouseDown(from, MouseButton.Left, keys);
        window.MouseMove(from + (to - from) / 2, RawInputModifiers.LeftMouseButton | keys);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton | keys);
        window.MouseUp(to, MouseButton.Left, keys);
    }

    [AvaloniaFact]
    public void A_clip_slides_to_the_bar_onto_another_track_or_a_new_one()
    {
        var (window, _, done, keys) = Timeline();
        // Keys (bars 1–4) dragged 2.3 bars right and down a row: it lands on bar 3 of the Bass track.
        Drag(window, new Point(150, RowY(0)), new Point(380, RowY(1)));
        var e = Assert.Single(done.Edits);
        Assert.Same(keys.Clips[0], e.Clip);
        Assert.Equal((1, 2L * Bar, 4L * Bar), (e.Row, e.Start, e.Length));

        // Below the last track: a new one. Shift places it off the grid.
        Drag(window, new Point(150, RowY(0)), new Point(180, RowY(2)), RawInputModifiers.Shift);
        Assert.Equal(2, done.Edits[^1].Row);
        Assert.InRange(done.Edits[^1].Start, 110L, 120L);   // 0.3 of a bar
    }

    [AvaloniaFact]
    public void Pulling_the_right_edge_repeats_and_the_left_edge_trims()
    {
        var (window, _, done, _) = Timeline();
        // Keys ends at x=400: pulled to bar 7.
        Drag(window, new Point(398, RowY(0)), new Point(590, RowY(0)));
        Assert.Equal((0L, 6L * Bar, 0L), (done.Edits[^1].Start, done.Edits[^1].Length, done.Edits[^1].Offset));
        // Its start pulled in by 1.3 bars: on the grid, a bar in and a bar shorter, its 1-bar loop starting where it was.
        Drag(window, new Point(2, RowY(0)), new Point(132, RowY(0)));
        Assert.Equal((1L * Bar, 3L * Bar, 0L), (done.Edits[^1].Start, done.Edits[^1].Length, done.Edits[^1].Offset));
        // Off the grid (Shift), half a bar in: the go round now starts half way through.
        Drag(window, new Point(2, RowY(0)), new Point(52, RowY(0)), RawInputModifiers.Shift);
        Assert.Equal((Bar / 2L, Bar / 2L), (done.Edits[^1].Start, done.Edits[^1].Offset));
    }

    [AvaloniaFact]
    public void The_bar_numbers_move_where_play_starts_and_ctrl_scroll_zooms_round_the_pointer()
    {
        var (window, view, done, _) = Timeline();
        var ruler = TimelineView.LabelHeight + 5;
        // A click on the bar numbers in bar 3: Play starts there (snapped to the bar).
        window.MouseDown(new Point(230, ruler), MouseButton.Left);
        window.MouseUp(new Point(230, ruler), MouseButton.Left);
        Assert.Equal(2L * Bar, Assert.Single(done.Cursors));
        Assert.Empty(done.Highlights);

        // Ctrl + scroll up over bar 5's start: in closer, bar 5 stays under the pointer.
        window.MouseWheel(new Point(400, RowY(0)), new Vector(0, 2), RawInputModifiers.Control);
        Assert.InRange(view.ViewLength, 4L * Bar, 6L * Bar);
        Assert.Equal(4L * Bar, view.ViewStart + (long)Math.Round(0.5 * view.ViewLength));
        // Shift + scroll moves along; a double-click on the bar numbers shows it all again.
        var start = view.ViewStart;
        window.MouseWheel(new Point(400, RowY(0)), new Vector(0, -1), RawInputModifiers.Shift);
        Assert.True(view.ViewStart > start);
        window.MouseDown(new Point(400, ruler), MouseButton.Left);
        window.MouseUp(new Point(400, ruler), MouseButton.Left);
        window.MouseDown(new Point(400, ruler), MouseButton.Left);
        window.MouseUp(new Point(400, ruler), MouseButton.Left);
        Assert.Equal((0L, 0L), (view.ViewStart, view.ViewLength));
    }

    [AvaloniaFact]
    public void Relative_snap_keeps_a_clip_off_the_grid_and_object_snap_catches_other_clips()
    {
        var (window, view, done, keys) = Timeline();
        keys.Clips[0].Start = Bar / 4;   // a quarter bar late
        // Grid and Relative both on: 2.1 bars along lands a quarter bar off bar 3, the nearer of the two.
        Drag(window, new Point(150, RowY(0)), new Point(360, RowY(0)));
        Assert.Equal(2L * Bar + Bar / 4, done.Edits[^1].Start);
        // Relative off: straight onto the grid.
        view.SnapRelative = false;
        Drag(window, new Point(150, RowY(0)), new Point(360, RowY(0)));
        Assert.Equal(2L * Bar, done.Edits[^1].Start);
        // Objects only: dragged near the Bass clip's start (bar 5), it catches it.
        view.SnapGrid = false;
        Drag(window, new Point(150, RowY(0)), new Point(150 + 370, RowY(0)));
        Assert.Equal(4L * Bar, done.Edits[^1].Start);
    }
}
