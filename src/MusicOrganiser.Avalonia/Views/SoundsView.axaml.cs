using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class SoundsView : UserControl
{
    public SoundsView()
    {
        InitializeComponent();
        // Autoplay follows your clicks and arrow keys, not every selection change: the grid
        // re-selects rows by itself when you sort or come back to this page.
        SoundGrid.AddHandler(PointerReleasedEvent, OnRowClicked, RoutingStrategies.Bubble, handledEventsToo: true);
        SoundGrid.AddHandler(KeyUpEvent, OnGridKeyUp, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    void OnGridDoubleTapped(object? sender, TappedEventArgs e) => (DataContext as SoundsViewModel)?.AuditionCommand.Execute(null);

    // Space plays the selected sound, as in the Windows app.
    void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        (DataContext as SoundsViewModel)?.PlayCommand.Execute(null);
        e.Handled = true;
    }

    void OnRowClicked(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not SoundsViewModel vm || e.InitialPressMouseButton != MouseButton.Left || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Source is not Visual source || source.FindAncestorOfType<Button>(true) is not null) return;
        if (source.FindAncestorOfType<DataGridRow>(true) is { } row && row.DataContext == vm.Selected) vm.PlayPicked();
    }

    void OnGridKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End && e.KeyModifiers == KeyModifiers.None)
            (DataContext as SoundsViewModel)?.PlayPicked();
    }
}
