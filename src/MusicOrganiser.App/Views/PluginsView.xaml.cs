using System.Windows;
using System.Windows.Controls;
using MusicOrganiser.App.ViewModels;

namespace MusicOrganiser.App.Views;

public partial class PluginsView : UserControl
{
    public PluginsView()
    {
        InitializeComponent();
        UsedGrid.IsVisibleChanged += OnGridShown;
        UnusedGrid.IsVisibleChanged += OnGridShown;
    }

    /// <summary>Only the list on screen decides what the impact panel shows.</summary>
    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid { Visibility: Visibility.Visible } grid && DataContext is PluginsViewModel vm)
            vm.SetSelection(grid.SelectedItems.Cast<object>().ToList());
    }

    void OnGridShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is DataGrid grid && DataContext is PluginsViewModel vm)
            vm.SetSelection(grid.SelectedItems.Cast<object>().ToList());
    }
}
