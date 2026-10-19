using Avalonia;
using Avalonia.Controls;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// Stops a grid squeezing its columns to fit. When columns can be resized, Avalonia's grid shrinks fixed-width
/// columns (down to a sliver) so every column fits the panel, instead of scrolling sideways. Turned on, each
/// fixed-width column's width becomes its smallest: it can still be dragged wider, and a narrow panel scrolls.
/// Set for every grid in App.axaml.
/// </summary>
public sealed class ColumnFloor
{
    ColumnFloor() { }

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<ColumnFloor, DataGrid, bool>("Enabled");

    public static bool GetEnabled(DataGrid g) => g.GetValue(EnabledProperty);
    public static void SetEnabled(DataGrid g, bool v) => g.SetValue(EnabledProperty, v);

    static ColumnFloor() => EnabledProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
    {
        if (e.NewValue is not true) return;
        foreach (var column in grid.Columns)
            if (column.Width.IsAbsolute && column.MinWidth < column.Width.Value)
                column.MinWidth = column.Width.Value;
    });
}
