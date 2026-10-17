using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicOrganiser.Avalonia.Views;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>The Sounds, Samples, Projects and Plugins grids: double-clicks and the selected row.</summary>
public sealed partial class GridTests
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

    public sealed class PageViewModel
    {
        public List<string> Searched { get; } = [];
        public CommunityToolkit.Mvvm.Input.RelayCommand<Row> SearchCommand { get; }
        public PageViewModel() => SearchCommand = new(r => Searched.Add(r!.Name));
    }

    [AvaloniaFact]
    public void A_button_in_a_cell_reaches_the_pages_command()
    {
        // The Plugins page's "Search" buttons sit in grid cells and reach the page through $parent[UserControl]. The cells
        // are made before they're on screen, which logs a warning once; the button must still work once it shows.
        var vm = new PageViewModel();
        var grid = new DataGrid
        {
            ItemsSource = new List<Row> { new("Serum"), new("Pro-Q 3") }, IsReadOnly = true, Height = 300,
            Columns =
            {
                new DataGridTemplateColumn
                {
                    Header = "Status",
                    CellTemplate = new global::Avalonia.Controls.Templates.FuncDataTemplate<Row>((_, _) => new Button
                    {
                        Content = "Search",
                        [!Button.CommandProperty] = new Binding("DataContext.SearchCommand")
                        {
                            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(UserControl) },
                        },
                        [!Button.CommandParameterProperty] = new Binding(),
                    }),
                },
            },
        };
        var window = new Window { Content = new UserControl { DataContext = vm, Content = grid }, Width = 400, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var button = grid.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is Row { Name: "Pro-Q 3" });
        Click(window, button, 1);

        Assert.Equal(["Pro-Q 3"], vm.Searched);
    }

    public sealed partial class PickerViewModel : ObservableObject
    {
        public List<string> Picks { get; } = [];

        [ObservableProperty] Row? _selected;

        partial void OnSelectedChanged(Row? value) => Picks.Add(value?.Name ?? "none");
    }

    static (Window Window, DataGrid Grid, DataGridCollectionView View, PickerViewModel Vm) LinkedGrid()
    {
        var vm = new PickerViewModel();
        var view = new DataGridCollectionView(new List<Row> { new("Kick"), new("Snare"), new("Hat") });
        var grid = new DataGrid
        {
            ItemsSource = view, IsReadOnly = true, Height = 300, DataContext = vm,
            Columns = { new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)) } },
        };
        grid.Bind(GridSelection.ItemProperty, new Binding(nameof(PickerViewModel.Selected)));
        var window = new Window { Content = grid, Width = 400, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, grid, view, vm);
    }

    static Row Named(DataGridCollectionView view, string name) => view.SourceCollection.Cast<Row>().Single(r => r.Name == name);

    [AvaloniaFact]
    public void A_refresh_keeps_the_selected_row_without_dropping_it_in_between()
    {
        var (_, grid, view, vm) = LinkedGrid();
        vm.Selected = Named(view, "Snare");
        Dispatcher.UIThread.RunJobs();
        vm.Picks.Clear();

        view.Refresh();
        view.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(Row.Name)));
        view.Filter = r => ((Row)r).Name != "Hat";
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(vm.Picks);
        Assert.Equal(Named(view, "Snare"), grid.SelectedItem);
    }

    [AvaloniaFact]
    public void A_refresh_keeps_the_selected_row_when_the_list_is_grouped()
    {
        var (_, grid, view, vm) = LinkedGrid();
        view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(Row.Name)));
        Dispatcher.UIThread.RunJobs();
        vm.Selected = Named(view, "Snare");
        Dispatcher.UIThread.RunJobs();
        vm.Picks.Clear();

        view.Refresh();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(vm.Picks);
        Assert.Equal(Named(view, "Snare"), grid.SelectedItem);
    }

    [AvaloniaFact]
    public void Filtering_out_the_selected_row_moves_the_view_model_with_the_grid()
    {
        var (_, grid, view, vm) = LinkedGrid();
        vm.Selected = Named(view, "Snare");
        Dispatcher.UIThread.RunJobs();

        view.Filter = r => ((Row)r).Name != "Snare";
        Dispatcher.UIThread.RunJobs();

        // Avalonia's grid moves to the first row left; the view model follows it.
        Assert.NotEqual(Named(view, "Snare"), vm.Selected);
        Assert.Equal(grid.SelectedItem, vm.Selected);
    }

    [AvaloniaFact]
    public void Picking_in_the_view_model_selects_the_row()
    {
        var (_, grid, view, vm) = LinkedGrid();

        vm.Selected = Named(view, "Hat");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Named(view, "Hat"), grid.SelectedItem);
    }

    [AvaloniaFact]
    public void Clicking_a_row_selects_it_in_the_view_model()
    {
        var (window, grid, view, vm) = LinkedGrid();

        Click(window, grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => r.DataContext is Row { Name: "Hat" }), 1);

        Assert.Equal(Named(view, "Hat"), vm.Selected);
        Assert.Equal(["Hat"], vm.Picks);
    }

    /// <summary>Why ProjectsView listens for Enter on the way down: the grid uses Enter itself.</summary>
    [AvaloniaFact]
    public void Enter_reaches_a_handler_listening_on_the_way_down()
    {
        var (window, grid, _) = Grid();
        var plain = new List<Key>();
        var early = new List<Key>();
        grid.KeyDown += (_, e) => plain.Add(e.Key);
        grid.AddHandler(InputElement.KeyDownEvent, (object? _, KeyEventArgs e) => early.Add(e.Key), RoutingStrategies.Tunnel);
        grid.SelectedIndex = 0;
        grid.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([Key.Enter], early);
        Assert.Empty(plain);
    }
}
