using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class ProjectDetailView : UserControl
{
    /// <summary>Below this width Overview's two columns sit one above the other.</summary>
    const double TwoColumnWidth = 620;

    public ProjectDetailView()
    {
        InitializeComponent();
        OverviewColumns.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) LayOutColumns(OverviewColumns.Bounds.Width);
        };
    }

    // A menu with nothing in it doesn't open, so it's filled as soon as the project is known, not when it opens.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (OpenChoices.Flyout is MenuFlyout menu && DataContext is ProjectDetailViewModel d) OpenMenu.Fill(menu.Items, d, row: false);
    }

    void LayOutColumns(double width)
    {
        var stacked = width < TwoColumnWidth;
        Grid.SetColumn(GlanceColumn, stacked ? 0 : 2);
        Grid.SetRow(GlanceColumn, stacked ? 2 : 0);
        Grid.SetColumnSpan(FieldsColumn, stacked ? 3 : 1);
        Grid.SetColumnSpan(GlanceColumn, stacked ? 3 : 1);
        GlanceColumn.Margin = stacked ? new Thickness(0, 22, 0, 0) : default;
    }

    // The glance column's "All plugins / samples / saves" links open that tab.
    void OnSeeAll(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string header })
            Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => Equals(t.Header, header));
    }
}
