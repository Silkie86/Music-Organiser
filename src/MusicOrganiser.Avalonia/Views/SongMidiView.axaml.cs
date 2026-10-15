using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class SongMidiView : UserControl
{
    public SongMidiView() => InitializeComponent();

    SongMidiViewModel? Vm => DataContext as SongMidiViewModel;

    /// <summary>Shown (the tab opened, or another song picked while it's open): work out what fits, once per song.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ = Vm?.LoadAsync();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree()) _ = Vm?.LoadAsync();
    }

    void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(true) is not null && v.FindAncestorOfType<Button>(true) is null)
            Vm?.Selected?.PlayCommand.Execute(null);
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Vm?.Selected is not { } row) return;
        row.PlayCommand.Execute(null);
        e.Handled = true;
    }
}
