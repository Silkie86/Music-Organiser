using System.Windows.Controls;
using MusicOrganiser.App.ViewModels;

namespace MusicOrganiser.App.Views;

public partial class PluginsView : UserControl
{
    public PluginsView() => InitializeComponent();

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && DataContext is PluginsViewModel vm)
            vm.SetSelection(grid.SelectedItems.Cast<object>().ToList());
    }
}
