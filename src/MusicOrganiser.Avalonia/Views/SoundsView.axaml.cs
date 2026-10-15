using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MusicOrganiser.Core.Sounds;
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
        // On the way down, before the lists use the keys themselves.
        TileList.AddHandler(KeyDownEvent, OnTilesKeyDown, RoutingStrategies.Tunnel);
        HistoryList.AddHandler(KeyDownEvent, OnHistoryKeyDown, RoutingStrategies.Tunnel);
    }

    void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.IsOnRow()) (DataContext as SoundsViewModel)?.AuditionCommand.Execute(null);
    }

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

    SoundsViewModel? Vm => DataContext as SoundsViewModel;

    // ---- tiles ----

    /// <summary>Tiles are at least this wide; as many fit across as will.</summary>
    const double TileWidth = 200;

    void OnTilesSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (Vm is { } vm) vm.TileColumns = (int)Math.Max(1, (e.NewSize.Width - 16) / TileWidth);
    }

    void OnTilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { DataContext: SoundTile tile }) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        TileList.Focus();
        vm.Selected = tile.Sound;
        if (e.ClickCount == 1) vm.PlayPicked();
    }

    void OnTileDoubleTapped(object? sender, TappedEventArgs e) => Vm?.AuditionCommand.Execute(null);

    // Arrows move round the tiles (Up and Down a whole row), Space plays, Home and End go to either end.
    void OnTilesKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key == Key.Space)
        {
            vm.PlayCommand.Execute(null);
            e.Handled = true;
            return;
        }
        var shown = vm.Shown;
        if (shown.Count == 0) return;
        var at = vm.Selected is { } s ? IndexOf(shown, s) : -1;
        var cols = vm.TileColumns;
        var next = e.Key switch
        {
            Key.Right => at + 1,
            Key.Left => at - 1,
            Key.Down => at < 0 ? 0 : at + cols,
            Key.Up => at - cols,
            Key.PageDown => at + cols * 4,
            Key.PageUp => at - cols * 4,
            Key.Home => 0,
            Key.End => shown.Count - 1,
            _ => int.MinValue,
        };
        if (next == int.MinValue) return;
        e.Handled = true;
        next = Math.Clamp(next, 0, shown.Count - 1);
        if (next == at) return;
        vm.Selected = shown[next];
        if (vm.RowOf(shown[next]) is { } row) TileList.ScrollIntoView(row);
        vm.PlayPicked();
    }

    static int IndexOf(IReadOnlyList<Sound> list, Sound s)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == s) return i;
        return -1;
    }

    // ---- history ----

    void OnHistoryDoubleTapped(object? sender, TappedEventArgs e) => Vm?.AuditionCommand.Execute(null);

    void OnGalaxyZoomIn(object? sender, RoutedEventArgs e) => Galaxy.ZoomBy(1.5);
    void OnGalaxyZoomOut(object? sender, RoutedEventArgs e) => Galaxy.ZoomBy(1 / 1.5);
    void OnGalaxyFit(object? sender, RoutedEventArgs e) => Galaxy.Fit();

    void OnHistoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        Vm?.PlayCommand.Execute(null);
        e.Handled = true;
    }
}
