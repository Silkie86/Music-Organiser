using Avalonia.Controls;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class ProjectDetailView : UserControl
{
    public ProjectDetailView() => InitializeComponent();

    // A menu with nothing in it doesn't open, so it's filled as soon as the project is known, not when it opens.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (OpenChoices.Flyout is MenuFlyout menu && DataContext is ProjectDetailViewModel d) OpenMenu.Fill(menu.Items, d, row: false);
    }
}
