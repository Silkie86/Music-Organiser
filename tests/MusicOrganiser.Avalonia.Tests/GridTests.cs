using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicOrganiser.Avalonia.Views;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>Double-clicks on the Sounds, Samples and Projects grids.</summary>
public sealed class GridTests
{
    public sealed record Row(string Name);

    static (Window Window, DataGrid Grid, List<string> Opened) Grid()
    {
        var opened = new List<string>();
        var grid = new DataGrid
        {
            ItemsSource = new List<Row> { new("Kick"), new("Snare") }, IsReadOnly = true, CanUserSortColumns = true, Height = 300,
            Columns = { new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)), Width = new DataGridLength(200) } },
        };
        grid.DoubleTapped += (_, e) =>
        {
            if (e.IsOnRow()) opened.Add((grid.SelectedItem as Row)?.Name ?? "nothing");
        };
        var window = new Window { Content = grid, Width = 400, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, grid, opened);
    }

    static void Click(Window window, Control c, int times)
    {
        var at = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), window)!.Value;
        for (var i = 0; i < times; i++)
        {
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Double_clicking_a_row_opens_it()
    {
        var (window, grid, opened) = Grid();

        Click(window, grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => r.DataContext is Row { Name: "Snare" }), 2);

        Assert.Equal(["Snare"], opened);
    }

    [AvaloniaFact]
    public void Double_clicking_a_column_header_only_sorts()
    {
        var (window, grid, opened) = Grid();
        grid.SelectedIndex = 0;

        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(h => h.Content is "Name");
        var doubleClicks = 0;
        grid.DoubleTapped += (_, _) => doubleClicks++;

        // Each click flips the sort and redraws the header, so it takes a few quick clicks before two
        // land on the same part of it and count as a double-click.
        Click(window, header, 4);

        Assert.True(doubleClicks > 0, "The header never saw a double-click");
        Assert.Empty(opened);
    }
}
