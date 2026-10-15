using Avalonia;
using Avalonia.Controls;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>The Duplicates tab on the Sounds page.</summary>
public partial class DuplicatesView : UserControl
{
    public DuplicatesView() => InitializeComponent();

    // The tab's content is only built when it's opened, so what was found last time is read then, not at start-up.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as DuplicatesViewModel)?.EnsureLoaded();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (VisualRoot is not null) (DataContext as DuplicatesViewModel)?.EnsureLoaded();
    }
}
