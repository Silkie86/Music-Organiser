using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// Links a grid's selected row with a view model's <c>Selected</c>, both ways, like binding
/// <c>SelectedItem</c>, but it rides out a refresh. When a search, filter, sort or grouping change
/// refreshes the list, Avalonia's grid drops the selected row and picks it straight back; bound
/// directly, the view model saw "nothing selected" in between and rebuilt the details panel,
/// losing what was in it. Here a row that's dropped only counts once the refresh is over and the
/// grid still has nothing selected (the row was filtered out, say).
/// </summary>
public sealed class GridSelection
{
    GridSelection() { }

    static readonly object NotSet = new();

    public static readonly AttachedProperty<object?> ItemProperty =
        AvaloniaProperty.RegisterAttached<GridSelection, DataGrid, object?>("Item", NotSet, defaultBindingMode: BindingMode.TwoWay);

    public static object? GetItem(DataGrid g) => g.GetValue(ItemProperty);
    public static void SetItem(DataGrid g, object? v) => g.SetValue(ItemProperty, v);

    static readonly AttachedProperty<bool> HookedProperty =
        AvaloniaProperty.RegisterAttached<GridSelection, DataGrid, bool>("Hooked");

    static readonly AttachedProperty<bool> PushingProperty =
        AvaloniaProperty.RegisterAttached<GridSelection, DataGrid, bool>("Pushing");

    static GridSelection() => ItemProperty.Changed.AddClassHandler<DataGrid>(OnItemChanged);

    /// <summary>The view model picked a row (or none): show it in the grid.</summary>
    static void OnItemChanged(DataGrid grid, AvaloniaPropertyChangedEventArgs e)
    {
        if (!grid.GetValue(HookedProperty))
        {
            grid.SetValue(HookedProperty, true);
            grid.SelectionChanged += OnSelectionChanged;
        }
        if (e.NewValue == NotSet || grid.GetValue(PushingProperty) || Equals(grid.SelectedItem, e.NewValue)) return;
        grid.SelectedItem = e.NewValue;
    }

    /// <summary>The grid's selection changed: tell the view model, unless it's a refresh's passing gap.</summary>
    static void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is { } item) Push(grid, item);
        else Dispatcher.UIThread.Post(() =>
        {
            if (grid.SelectedItem is not null) return;
            // The view model's row is still in the list (a reload swapped in new rows, and the grid tried to pick the
            // new one while its list was briefly empty): show it again rather than letting the selection go.
            if (grid.GetValue(ItemProperty) is { } wanted && wanted != NotSet && Contains(grid, wanted)) grid.SelectedItem = wanted;
            else Push(grid, null);
        });
    }

    static bool Contains(DataGrid grid, object item) =>
        grid.ItemsSource is System.Collections.IEnumerable rows && rows.Cast<object>().Contains(item);

    static void Push(DataGrid grid, object? item)
    {
        if (Equals(grid.GetValue(ItemProperty), item)) return;
        grid.SetValue(PushingProperty, true);
        try { grid.SetCurrentValue(ItemProperty, item); }
        finally { grid.SetValue(PushingProperty, false); }
    }
}
