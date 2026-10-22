using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Data;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicOrganiser.Avalonia.Views;
using MusicOrganiser.ViewModels;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>
/// Dragging rows out of the app: which rows a drag carries, and that clicks, selection and the
/// buttons inside rows still behave. The drag itself goes to the system, so these check what the
/// app asks to drag (the source records it and returns nothing, so no real drag starts).
/// </summary>
public sealed class FileDragTests
{
    sealed class Recorder : IFileDragSource
    {
        public List<IReadOnlyList<object>> Drags { get; } = [];

        public DragPayload? MakeDrag(IReadOnlyList<object> items)
        {
            Drags.Add(items);
            return null;
        }
    }

    static Window Show(Control content)
    {
        var window = new Window { Content = content, Width = 400, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    static ListBox List(Recorder source, params string[] rows)
    {
        var list = new ListBox { ItemsSource = rows, SelectionMode = SelectionMode.Multiple };
        FileDrag.SetSource(list, source);
        return list;
    }

    static Point Middle(Window window, Control c) =>
        c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), window) ?? throw new InvalidOperationException("Not on screen");

    static Point RowPoint(Window window, ListBox list, string row) =>
        Middle(window, list.ContainerFromItem(row) ?? throw new InvalidOperationException($"No row for {row}"));

    /// <summary>Presses at a point and moves the mouse by (dx, dy) with the button held, then lets go.</summary>
    static void PressMoveRelease(Window window, Point at, double dx, double dy)
    {
        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(at + new Vector(dx, dy), RawInputModifiers.LeftMouseButton);
        window.MouseUp(at + new Vector(dx, dy), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Dragging_a_row_carries_just_that_row()
    {
        var source = new Recorder();
        var list = List(source, "Kick", "Snare", "Hat");
        var window = Show(list);

        PressMoveRelease(window, RowPoint(window, list, "Snare"), 0, 20);

        Assert.Equal(["Snare"], Assert.Single(source.Drags));
    }

    [AvaloniaFact]
    public void A_small_wobble_is_a_click_not_a_drag()
    {
        var source = new Recorder();
        var list = List(source, "Kick", "Snare", "Hat");
        var window = Show(list);

        PressMoveRelease(window, RowPoint(window, list, "Snare"), 3, 3);

        Assert.Empty(source.Drags);
        Assert.Equal("Snare", list.SelectedItem);
    }

    [AvaloniaFact]
    public void Dragging_one_of_several_selected_rows_carries_them_all_in_list_order()
    {
        var source = new Recorder();
        var list = List(source, "Kick", "Snare", "Hat");
        var window = Show(list);
        list.SelectedItems!.Add("Hat");
        list.SelectedItems.Add("Kick");

        PressMoveRelease(window, RowPoint(window, list, "Hat"), 0, 20);

        Assert.Equal(["Kick", "Hat"], Assert.Single(source.Drags));
        Assert.Equal(2, list.SelectedItems.Count);
    }

    [AvaloniaFact]
    public void Clicking_one_of_several_selected_rows_selects_just_it()
    {
        var source = new Recorder();
        var list = List(source, "Kick", "Snare", "Hat");
        var window = Show(list);
        list.SelectedItems!.Add("Kick");
        list.SelectedItems.Add("Hat");

        PressMoveRelease(window, RowPoint(window, list, "Hat"), 0, 0);

        Assert.Empty(source.Drags);
        Assert.Equal(["Hat"], list.SelectedItems.Cast<object>());
    }

    [AvaloniaFact]
    public void Buttons_inside_a_row_still_work_and_never_start_a_drag()
    {
        var source = new Recorder();
        var clicked = 0;
        var list = List(source, "Kick", "Snare");
        list.ItemTemplate = new FuncDataTemplate<string>((row, _) =>
        {
            var play = new Button { Content = "Play", Width = 60 };
            play.Click += (_, _) => clicked++;
            var panel = new DockPanel();
            DockPanel.SetDock(play, Dock.Left);
            panel.Children.Add(play);
            panel.Children.Add(new TextBlock { Text = row });
            return panel;
        });
        var window = Show(list);
        var button = list.ContainerFromItem("Snare")!.GetLogicalDescendants().OfType<Button>().Single();

        PressMoveRelease(window, Middle(window, button), 0, 20);
        PressMoveRelease(window, Middle(window, button), 0, 0);

        Assert.Empty(source.Drags);
        Assert.Equal(1, clicked);
    }

    [AvaloniaFact]
    public void The_right_button_never_drags()
    {
        var source = new Recorder();
        var list = List(source, "Kick", "Snare");
        var window = Show(list);
        var at = RowPoint(window, list, "Kick");

        window.MouseDown(at, MouseButton.Right);
        window.MouseMove(at + new Vector(0, 20), RawInputModifiers.RightMouseButton);
        window.MouseUp(at + new Vector(0, 20), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(source.Drags);
    }

    [AvaloniaFact]
    public void A_drag_all_chip_carries_every_row_of_its_list_in_order()
    {
        var rows = new Recorder();
        var chipSource = new Recorder();
        var list = List(rows, "Bass", "Lead", "Pad");
        var chip = new Border { Width = 120, Height = 30, Background = Brushes.Transparent, Child = new TextBlock { Text = "Drag all" } };
        FileDrag.SetSource(chip, chipSource);
        FileDrag.SetAllOf(chip, list);
        var panel = new StackPanel { Children = { chip, list } };
        var window = Show(panel);

        PressMoveRelease(window, Middle(window, chip), 20, 0);

        Assert.Equal(["Bass", "Lead", "Pad"], Assert.Single(chipSource.Drags));
        Assert.Empty(rows.Drags);
    }

    [AvaloniaFact]
    public void An_ordinary_element_carries_its_own_data()
    {
        var source = new Recorder();
        var card = new Border { Width = 120, Height = 30, Background = Brushes.Transparent, DataContext = "Kit 1", Child = new TextBlock { Text = "Kit" } };
        FileDrag.SetSource(card, source);
        var window = Show(new StackPanel { Children = { card } });

        PressMoveRelease(window, Middle(window, card), 20, 0);

        Assert.Equal(["Kit 1"], Assert.Single(source.Drags));
    }

    public sealed record Row(string Name, int Bpm);

    [AvaloniaFact]
    public void A_grid_carries_its_selected_rows_in_the_order_they_are_sorted()
    {
        var source = new Recorder();
        var view = new DataGridCollectionView(new List<Row> { new("Kick", 120), new("Snare", 90), new("Hat", 140) });
        view.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(Row.Bpm), System.ComponentModel.ListSortDirection.Descending));
        var grid = new DataGrid
        {
            ItemsSource = view, SelectionMode = DataGridSelectionMode.Extended, IsReadOnly = true, Height = 300,
            Columns = { new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)) } },
        };
        FileDrag.SetSource(grid, source);
        var window = Show(grid);
        var kick = view.Cast<Row>().Single(r => r.Name == "Kick");
        var snare = view.Cast<Row>().Single(r => r.Name == "Snare");
        grid.SelectedItems.Add(snare);
        grid.SelectedItems.Add(kick);
        Dispatcher.UIThread.RunJobs();
        var row = grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => Equals(r.DataContext, snare));

        PressMoveRelease(window, Middle(window, row), 0, 20);

        // Sorted by tempo, fastest first: Hat 140, Kick 120, Snare 90.
        Assert.Equal([kick, snare], Assert.Single(source.Drags));
    }
}
