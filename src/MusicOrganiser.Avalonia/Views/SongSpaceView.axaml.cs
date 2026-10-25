using Avalonia;
using Avalonia.Controls;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class SongSpaceView : UserControl
{
    public SongSpaceView() => InitializeComponent();

    /// <summary>Shown (the tab opened): measure the song, once per song.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ = (DataContext as SongSpaceViewModel)?.LoadAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        (DataContext as SongSpaceViewModel)?.StopPlaying();
    }

    SongSpaceViewModel? _shown;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _shown?.StopPlaying();
        _shown = DataContext as SongSpaceViewModel;
        if (VisualRoot is not null) _ = (DataContext as SongSpaceViewModel)?.LoadAsync();
    }
}
