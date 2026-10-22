using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Avalonia.Views;
using MusicOrganiser.Core.Parsers;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>The piano roll: new notes copying the last one, and notes snapping like Bitwig.</summary>
public sealed class PianoRollTests
{
    const int Beat = 96;

    // One bar shown 800 px wide after the 34 px keyboard; one note, key 60, a half note long at velocity 70, a little late.
    static (Window Window, PianoRollEditor Roll, List<IReadOnlyList<NoteEvent>> Edits) Roll(long start = 0)
    {
        var edits = new List<IReadOnlyList<NoteEvent>>();
        var roll = new PianoRollEditor
        {
            Width = 834, Height = 300, Period = 4 * Beat, Length = 4 * Beat,
            Notes = [new NoteEvent(start, 2 * Beat, 60, 70)],
        };
        roll.EditedCommand = new RelayCommand<IReadOnlyList<NoteEvent>>(n => { edits.Add(n!); roll.Notes = n; });
        var window = new Window { Content = roll, Width = 834, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, roll, edits);
    }

    static double XOf(long tick) => 34 + 800.0 * tick / (4 * Beat);
    const double Key60 = 20 + 12 * (280 / 25.0) + 5;   // rows from 72 down to 48

    [AvaloniaFact]
    public void A_new_note_copies_the_length_and_velocity_of_the_last_note_clicked()
    {
        var (window, _, edits) = Roll();
        window.MouseDown(new Point(XOf(Beat / 2), Key60), MouseButton.Left);
        window.MouseUp(new Point(XOf(Beat / 2), Key60), MouseButton.Left);
        Assert.Empty(edits);   // a click changes nothing
        // A new note in beat 4, two keys up.
        var y = Key60 - 2 * (280 / 25.0);
        window.MouseDown(new Point(XOf(3 * Beat) + 1, y), MouseButton.Left);
        window.MouseUp(new Point(XOf(3 * Beat) + 1, y), MouseButton.Left);
        var added = Assert.Single(edits).Single(n => n.Key == 62);
        Assert.Equal((3L * Beat, (long)Beat, 70), (added.Start, added.Length, added.Velocity));   // as long as fits before the end
    }

    [AvaloniaFact]
    public void A_pushed_note_keeps_its_push_with_relative_snap_and_lands_on_the_grid_without_it()
    {
        var (window, roll, edits) = Roll(start: 10);
        void DragBy(long ticks, long grab)
        {
            var from = new Point(XOf(10 + grab), Key60);
            var to = new Point(XOf(10 + grab + ticks), Key60);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            window.MouseUp(to, MouseButton.Left);
        }
        DragBy(Beat + 5, Beat / 2);
        Assert.Equal(10L + Beat, edits[^1].Single().Start);
        roll.Notes = [new NoteEvent(10, 2 * Beat, 60, 70)];
        roll.SnapRelative = false;
        DragBy(Beat - 5, Beat + Beat / 2);   // grabbed somewhere else, so it isn't a double-click (which takes a note away)
        Assert.Equal((long)Beat, edits[^1].Single().Start);
    }

    [AvaloniaFact]
    public void Ctrl_scroll_zooms_in_time_and_ctrl_shift_scroll_makes_the_notes_taller()
    {
        var (window, roll, edits) = Roll();
        // Zoomed in round beat 3: a click there now lands nearer the pointer's tick than the whole-bar view would put it.
        window.MouseWheel(new Point(XOf(2 * Beat), Key60), new Vector(0, 3), RawInputModifiers.Control);
        window.MouseWheel(new Point(XOf(2 * Beat), Key60), new Vector(0, 3), RawInputModifiers.Control | RawInputModifiers.Shift);
        // Beat 3 stays under the pointer: a note drawn there starts on beat 3.
        window.MouseDown(new Point(XOf(2 * Beat) + 2, Key60), MouseButton.Left);
        window.MouseUp(new Point(XOf(2 * Beat) + 2, Key60), MouseButton.Left);
        var added = Assert.Single(edits).Single(n => n.Start != 0);
        Assert.Equal(2L * Beat, added.Start);
        Assert.Equal(60, added.Key);   // the key under the pointer stayed put too
        // A double-click on the bar numbers shows it all again.
        window.MouseDown(new Point(400, 10), MouseButton.Left);
        window.MouseUp(new Point(400, 10), MouseButton.Left);
        window.MouseDown(new Point(400, 10), MouseButton.Left);
        window.MouseUp(new Point(400, 10), MouseButton.Left);
        roll.Notes = [];
        window.MouseDown(new Point(XOf(3 * Beat) + 2, Key60), MouseButton.Left);
        window.MouseUp(new Point(XOf(3 * Beat) + 2, Key60), MouseButton.Left);
        Assert.Equal(3L * Beat, edits[^1].Single().Start);
    }
}
