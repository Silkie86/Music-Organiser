using Avalonia.Controls;
using Avalonia.Input;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class SoundsView : UserControl
{
    public SoundsView() => InitializeComponent();

    void OnGridDoubleTapped(object? sender, TappedEventArgs e) => (DataContext as SoundsViewModel)?.PlayCommand.Execute(null);

    // Space plays the selected sound, as in the Windows app.
    void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        (DataContext as SoundsViewModel)?.PlayCommand.Execute(null);
        e.Handled = true;
    }
}
