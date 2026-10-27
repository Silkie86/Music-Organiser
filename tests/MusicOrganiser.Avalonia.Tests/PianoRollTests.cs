using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Avalonia.Views;
using MusicOrganiser.Core.Parsers;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>The piano roll: new notes copying the last one, notes snapping like Bitwig, and notes picked together.</summary>
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

    [AvaloniaFact]
    public void Alt_scroll_makes_the_notes_taller_round_the_key_under_the_pointer()
    {
        var (window, roll, edits) = Roll();
        window.MouseWheel(new Point(XOf(2 * Beat), Key60), new Vector(0, 3), RawInputModifiers.Alt);
        window.MouseDown(new Point(XOf(2 * Beat) + 2, Key60), MouseButton.Left);
        window.MouseUp(new Point(XOf(2 * Beat) + 2, Key60), MouseButton.Left);
        Assert.Equal(60, Assert.Single(edits).Single(n => n.Start != 0).Key);   // zoomed round middle C, which stayed under the pointer
        // Three rows up was E above it before; the rows are taller now, so it's nearer.
        roll.Notes = [];
        var up = Key60 - 3 * 280 / 25.0;
        window.MouseDown(new Point(XOf(3 * Beat) + 2, up), MouseButton.Left);
        window.MouseUp(new Point(XOf(3 * Beat) + 2, up), MouseButton.Left);
        Assert.InRange(edits[^1].Single().Key, 61, 62);
    }

    [AvaloniaFact]
    public void A_note_drag_cut_off_by_another_window_goes_back_and_changes_nothing()
    {
        var (window, roll, edits) = Roll();
        IPointer? pointer = null;
        roll.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.MouseDown(new Point(XOf(Beat / 2), Key60), MouseButton.Left);
        window.MouseMove(new Point(XOf(Beat / 2 + Beat), Key60), RawInputModifiers.LeftMouseButton);
        pointer!.Capture(null);   // Alt+Tab, or a dialog opening, mid-drag
        window.MouseMove(new Point(XOf(Beat / 2 + 2 * Beat), Key60));   // no button held: nothing follows
        Assert.Empty(edits);
        // The next click puts a new note in after it, with the note where it always was.
        window.MouseDown(new Point(XOf(3 * Beat) + 1, Key60), MouseButton.Left);
        window.MouseUp(new Point(XOf(3 * Beat) + 1, Key60), MouseButton.Left);
        Assert.Equal([0L, 3L * Beat], Assert.Single(edits).Select(n => n.Start));
    }

    // ---- notes picked together, and snapping to the chords (Solomon, 2026-10-27) ----

    /// <summary>Where key <paramref name="key"/>'s row is, with <paramref name="notes"/> on the roll (two octaves round them).</summary>
    static double YOf(int key, IReadOnlyList<NoteEvent> notes)
    {
        int low = notes.Min(n => n.Key) - 2, high = notes.Max(n => n.Key) + 2;
        if (high - low < 24) high = (low + high) / 2 + 12;
        return 20 + (high - key) * (280 / 25.0) + 5;
    }

    static readonly NoteEvent A = new(0, Beat, 60, 70), B = new(2 * Beat, Beat, 64, 70);
    static bool CMajor(NoteEvent n) => n.Key % 12 is 0 or 4 or 7;

    static void Press(Window window, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        var physical = key switch
        {
            Key.Up => PhysicalKey.ArrowUp, Key.Down => PhysicalKey.ArrowDown, Key.Left => PhysicalKey.ArrowLeft, Key.Right => PhysicalKey.ArrowRight,
            Key.Escape => PhysicalKey.Escape, Key.Delete => PhysicalKey.Delete, Key.A => PhysicalKey.A, _ => PhysicalKey.None,
        };
        window.KeyPress(key, mods, physical, null);
        window.KeyRelease(key, mods, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Ctrl_click_picks_notes_and_dragging_one_drags_them_all()
    {
        var (window, roll, edits) = Roll();
        roll.Notes = [A, B];
        IReadOnlyList<NoteEvent> notes = [A, B];
        window.MouseDown(new Point(XOf(Beat / 2), YOf(60, notes)), MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(new Point(XOf(Beat / 2), YOf(60, notes)), MouseButton.Left, RawInputModifiers.Control);
        window.MouseDown(new Point(XOf(2 * Beat + Beat / 2), YOf(64, notes)), MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(new Point(XOf(2 * Beat + Beat / 2), YOf(64, notes)), MouseButton.Left, RawInputModifiers.Control);
        Assert.Empty(edits);
        Assert.Equal([A, B], roll.Selection!.OrderBy(n => n.Start));

        // The first dragged a beat later and two keys up: the second goes with it, and both stay picked.
        window.MouseDown(new Point(XOf(Beat / 2), YOf(60, notes)), MouseButton.Left);
        window.MouseMove(new Point(XOf(Beat + Beat / 2), YOf(62, notes)), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(XOf(Beat + Beat / 2), YOf(62, notes)), MouseButton.Left);
        NoteEvent[] moved = [A with { Start = Beat, Key = 62 }, B with { Start = 3 * Beat, Key = 66 }];
        Assert.Equal(moved, Assert.Single(edits).OrderBy(n => n.Start));
        Assert.Equal(moved, roll.Selection!.OrderBy(n => n.Start));

        // A Ctrl+click on a picked note lets it go; a plain click on one picks it alone.
        notes = roll.Notes!;
        window.MouseDown(new Point(XOf(Beat + Beat / 2), YOf(62, notes)), MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(new Point(XOf(Beat + Beat / 2), YOf(62, notes)), MouseButton.Left, RawInputModifiers.Control);
        Assert.Equal([moved[1]], roll.Selection);
        // (Grabbed somewhere else along it, so it isn't a double-click, which takes a note away.)
        window.MouseDown(new Point(XOf(Beat + 15), YOf(62, notes)), MouseButton.Left);
        window.MouseUp(new Point(XOf(Beat + 15), YOf(62, notes)), MouseButton.Left);
        Assert.Equal([moved[0]], roll.Selection);
    }

    [AvaloniaFact]
    public void Ctrl_dragging_on_an_empty_spot_picks_the_notes_in_the_box()
    {
        var (window, roll, edits) = Roll();
        roll.Notes = [A, B];
        IReadOnlyList<NoteEvent> notes = [A, B];
        // From an empty spot half a beat before B, two keys above it, to half way along it a key below: only B is in the box.
        var from = new Point(XOf(Beat + Beat / 2), YOf(66, notes));
        var to = new Point(XOf(2 * Beat + Beat / 2), YOf(63, notes));
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.Control);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton | RawInputModifiers.Control);
        window.MouseUp(to, MouseButton.Left, RawInputModifiers.Control);
        Assert.Empty(edits);   // picking changes nothing
        Assert.Equal([B], roll.Selection);

        // Ctrl+A picks them all, Esc lets go.
        Press(window, Key.A, RawInputModifiers.Control);
        Assert.Equal(2, roll.Selection!.Count);
        Press(window, Key.Escape);
        Assert.Null(roll.Selection);
    }

    [AvaloniaFact]
    public void The_arrows_move_the_picked_notes_to_the_next_note_of_the_chords_when_snapping_to_them()
    {
        var (window, roll, edits) = Roll();
        roll.Notes = [A, B];
        roll.ChordTest = CMajor;
        roll.SnapChord = true;
        roll.Selection = [B];
        roll.Focus();
        Press(window, Key.Up);
        Assert.Equal(67, edits[^1].Single(n => n.Start == B.Start).Key);   // E to G, over C
        Assert.Equal(60, edits[^1].Single(n => n.Start == 0).Key);          // the one not picked stays
        Press(window, Key.Up, RawInputModifiers.Shift);
        Assert.Equal(79, edits[^1].Single(n => n.Start == B.Start).Key);   // Shift: an octave
        Press(window, Key.Right);
        Assert.Equal(B.Start + Beat / 4, roll.Selection!.Single().Start);   // along by a sixteenth
        roll.SnapChord = false;
        Press(window, Key.Down);
        Assert.Equal(78, roll.Selection!.Single().Key);   // a semitone when not snapping to the chords
        Press(window, Key.Delete);
        Assert.Equal([A], edits[^1]);
        Assert.Null(roll.Selection);
    }

    [AvaloniaFact]
    public void Snapping_to_the_chords_puts_notes_dragged_or_clicked_in_only_on_the_chords_notes()
    {
        var (window, roll, edits) = Roll();
        roll.ChordTest = CMajor;
        roll.SnapChord = true;
        IReadOnlyList<NoteEvent> notes = roll.Notes!;
        // Dragged up three keys, to D♯: it goes on to E, the nearest note of C the way it was going.
        window.MouseDown(new Point(XOf(Beat / 2), YOf(60, notes)), MouseButton.Left);
        window.MouseMove(new Point(XOf(Beat / 2), YOf(63, notes)), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(XOf(Beat / 2), YOf(63, notes)), MouseButton.Left);
        Assert.Equal(64, Assert.Single(edits).Single().Key);
        // Clicked in on C♯: it goes in on C.
        notes = roll.Notes!;
        window.MouseDown(new Point(XOf(3 * Beat) + 1, YOf(61, notes)), MouseButton.Left);
        window.MouseUp(new Point(XOf(3 * Beat) + 1, YOf(61, notes)), MouseButton.Left);
        Assert.Equal(60, edits[^1].Single(n => n.Start == 3 * Beat).Key);
    }
}
