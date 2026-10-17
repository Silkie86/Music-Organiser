using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MusicOrganiser.Avalonia.Views;

static class RowTaps
{
    /// <summary>
    /// True when a double-click on a grid landed on a row, not on a column header, a scroll bar or a
    /// button in the row. So flipping a sort or scrolling fast never plays a sound or opens a project.
    /// </summary>
    public static bool IsOnRow(this TappedEventArgs e) =>
        e.Source is Visual v && v.FindAncestorOfType<DataGridRow>(true) is not null && v.FindAncestorOfType<Button>(true) is null;
}
