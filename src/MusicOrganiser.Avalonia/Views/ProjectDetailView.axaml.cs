using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class ProjectDetailView : UserControl
{
    ProjectDetailViewModel? _vm;

    public ProjectDetailView() => InitializeComponent();

    // A menu with nothing in it doesn't open, so it's filled as soon as the project is known, not when it opens.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as ProjectDetailViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmChanged;
        if (OlderSaves.Flyout is MenuFlyout menu) OpenMenu.FillOlderSaves(menu.Items, _vm, () => ShowTab("Saves"));
        LayOutOverview();
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectDetailViewModel.IsFixOpen)) return;
        if (_vm?.IsFixOpen == true) ShowTab("Overview");   // Fix's list opens on Overview, whichever tab Fix was pressed on
        LayOutOverview();
    }

    /// <summary>
    /// The arrangement fills Overview. While Fix's list is open under it, the two share the height where the divider
    /// was last left.
    /// </summary>
    void LayOutOverview()
    {
        var rows = OverviewGrid.RowDefinitions;
        if (_vm?.IsFixOpen == true)
        {
            var share = _vm.ArrangementShare;
            rows[1].Height = new GridLength(share, GridUnitType.Star);
            rows[3].Height = new GridLength(1 - share, GridUnitType.Star);
        }
        else
        {
            rows[1].Height = new GridLength(1, GridUnitType.Star);
            rows[3].Height = GridLength.Auto;
        }
    }

    void OnArrangementDividerMoved(object? sender, VectorEventArgs e)
    {
        var rows = OverviewGrid.RowDefinitions;
        var (top, bottom) = (rows[1].ActualHeight, rows[3].ActualHeight);
        if (_vm is null || top + bottom <= 0) return;
        _vm.ArrangementShare = top / (top + bottom);
        LayOutOverview();
    }

    // The counts on Overview, and the Older saves menu's last line, open their tab.
    void OnSeeAll(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string header }) ShowTab(header);
    }

    void ShowTab(string header) =>
        Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => Equals(t.Header, header)) ?? Tabs.SelectedItem;
}
