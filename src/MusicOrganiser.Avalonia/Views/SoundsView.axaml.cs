using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class SoundsView : UserControl, IShortcutPage
{
    public SoundsView()
    {
        InitializeComponent();
        // A click always plays the sound; Autoplay follows the arrow keys. Neither follows every selection
        // change: the grid re-selects rows by itself when you sort or come back to this page.
        SoundGrid.AddHandler(PointerReleasedEvent, OnRowClicked, RoutingStrategies.Bubble, handledEventsToo: true);
        SoundGrid.AddHandler(PointerPressedEvent, OnPressCounted, RoutingStrategies.Tunnel, handledEventsToo: true);
        // A right-click picks the row under it, so its menu (Play, To Bitwig, Show file) is about that sound.
        SoundGrid.AddHandler(PointerPressedEvent, OnRowRightPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Held arrow keys play each sound they pass (after the grid has moved on to it); letting go plays the one you stop on.
        SoundGrid.AddHandler(KeyDownEvent, OnGridKeyPacing, RoutingStrategies.Tunnel);
        SoundGrid.AddHandler(KeyDownEvent, OnGridKeyMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        SoundGrid.AddHandler(KeyUpEvent, OnGridKeyUp, RoutingStrategies.Bubble, handledEventsToo: true);
        // On the way down, before the lists use the keys themselves.
        TileList.AddHandler(KeyDownEvent, OnTilesKeyDown, RoutingStrategies.Tunnel);
        TileList.AddHandler(KeyUpEvent, OnTilesKeyUp, RoutingStrategies.Bubble, handledEventsToo: true);
        HistoryList.AddHandler(PointerReleasedEvent, OnHistoryClicked, RoutingStrategies.Bubble, handledEventsToo: true);
        HistoryList.AddHandler(PointerPressedEvent, OnPressCounted, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.IsOnRow()) (DataContext as SoundsViewModel)?.AuditionCommand.Execute(null);
    }

    /// <summary>
    /// The click count of the last press on the list or History: a click on a playing sound stops it, so the second click
    /// of a double-click is left to the double-click (Audition), which keeps it playing.
    /// </summary>
    int _clicks;

    void OnPressCounted(object? sender, PointerPressedEventArgs e) => _clicks = e.ClickCount;

    void OnRowClicked(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not SoundsViewModel vm || e.InitialPressMouseButton != MouseButton.Left || e.KeyModifiers != KeyModifiers.None) return;
        if (_clicks > 1) return;
        if (e.Source is not Visual source || source.FindAncestorOfType<Button>(true) is not null) return;
        if (source.FindAncestorOfType<DataGridRow>(true) is { } row && row.DataContext == vm.Selected) vm.PlayClicked();
    }

    void OnRowRightPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || !e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        if (e.Source is Visual source && source.FindAncestorOfType<DataGridRow>(true) is { DataContext: Sound sound }) vm.Selected = sound;
    }

    static bool Moves(KeyEventArgs e) =>
        e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End && e.KeyModifiers == KeyModifiers.None;

    // The key held down now: its next KeyDowns are repeats. A held key steps no faster than each sound plays.
    Key? _held;

    bool Held(KeyEventArgs e)
    {
        var held = _held == e.Key;
        _held = e.Key;
        return held;
    }

    void OnGridKeyPacing(object? sender, KeyEventArgs e)
    {
        if (!Moves(e) || Vm is not { } vm) return;
        if (!vm.MayStep(Held(e))) e.Handled = true;   // too soon: the grid stays on the sound that's playing
        else _gridStepped = true;
    }

    bool _gridStepped;

    void OnGridKeyMoved(object? sender, KeyEventArgs e)
    {
        if (!Moves(e) || !_gridStepped) return;
        _gridStepped = false;
        Vm?.PlayPassing();
    }

    void OnGridKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == _held) _held = null;
        if (Moves(e)) Vm?.PlayPicked();
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
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed) vm.Selected = tile.Sound;   // its menu is about this tile
        if (!props.IsLeftButtonPressed) return;
        TileList.Focus();
        vm.Selected = tile.Sound;
        if (e.ClickCount == 1) vm.PlayClicked();
    }

    void OnTileDoubleTapped(object? sender, TappedEventArgs e) => Vm?.AuditionCommand.Execute(null);

    // A Rediscover row plays like a tile: a click anywhere but its buttons (they take their own clicks).
    void OnSuggestionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { DataContext: Suggestion row }) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || OnButton(e.Source)) return;
        vm.Selected = row.Sound;
        if (e.ClickCount == 1) vm.PlayClicked();
    }

    void OnSuggestionDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!OnButton(e.Source)) Vm?.AuditionCommand.Execute(null);
    }

    static bool OnButton(object? source) => source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null;

    // Arrows move round the tiles (Up and Down a whole row), Home and End go to either end. (Space plays: the app's keys.)
    void OnTilesKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.KeyModifiers != KeyModifiers.None) return;
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
        StepTo(vm, shown, at, Math.Clamp(next, 0, shown.Count - 1), Held(e));
    }

    /// <summary>
    /// Picks the sound at <paramref name="next"/> (from <paramref name="at"/>), keeps it in sight and plays a little of it,
    /// so each one passed while a key is held plays; held down, it waits until the one it's on has played.
    /// </summary>
    void StepTo(SoundsViewModel vm, IReadOnlyList<Sound> shown, int at, int next, bool held)
    {
        if (next == at || !vm.MayStep(held)) return;
        vm.Selected = shown[next];
        if (vm.IsTiles && vm.RowOf(shown[next]) is { } row) TileList.ScrollIntoView(row);
        else if (vm.IsList) SoundGrid.ScrollIntoView(shown[next], null);
        vm.PlayPassing();
    }

    void OnTilesKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == _held) _held = null;
        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            Vm?.PlayPicked();   // the tile you let go on plays
    }

    static int IndexOf(IReadOnlyList<Sound> list, Sound s)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == s) return i;
        return -1;
    }

    // ---- history ----

    void OnHistoryDoubleTapped(object? sender, TappedEventArgs e) => Vm?.AuditionCommand.Execute(null);

    void OnHistoryClicked(object? sender, PointerReleasedEventArgs e)
    {
        if (Vm is not { } vm || e.InitialPressMouseButton != MouseButton.Left || e.KeyModifiers != KeyModifiers.None || _clicks > 1) return;
        if (e.Source is not Visual source || source.FindAncestorOfType<Button>(true) is not null) return;
        if (source.FindAncestorOfType<ListBoxItem>(true) is { DataContext: HistoryItem h } && h.Sound == vm.Selected) vm.PlayClicked();
    }

    void OnGalaxyZoomIn(object? sender, RoutedEventArgs e) => Galaxy.ZoomBy(1.5);
    void OnGalaxyZoomOut(object? sender, RoutedEventArgs e) => Galaxy.ZoomBy(1 / 1.5);
    void OnGalaxyFit(object? sender, RoutedEventArgs e) => Galaxy.Fit();

    // ---- keys ----

    /// <summary>
    /// Sound Lab's keys (Settings › Shortcuts). Space plays the picked sound wherever the keyboard is on the page (the
    /// Galaxy keeps its own Space, which pulses the star), and Ctrl+Up and Ctrl+Down step through the sounds even while
    /// typing a search, playing each one as the arrows do.
    /// </summary>
    public bool RunShortcut(string id, bool repeat)
    {
        if (Vm is not { } vm) return false;
        switch (id)
        {
            case "app.play" when Galaxy.IsKeyboardFocusWithin || vm.Selected is null: return false;
            case "app.play": vm.PlayCommand.Execute(null); return true;
            case "app.prevItem" or "app.nextItem" when vm.TabIndex == 0: return StepSound(vm, id == "app.nextItem" ? 1 : -1, repeat);
            case "sounds.stop" when vm.IsSounding: vm.StopPlayingCommand.Execute(null); return true;
            case "sounds.tag" when TagBox.IsEffectivelyVisible: TagBox.Focus(); return true;
        }
        if (vm.Selected is null) return false;
        switch (id)
        {
            case "sounds.favourite": vm.ToggleFavouriteCommand.Execute(null); return true;
            case "sounds.toBitwig": vm.ToBitwigCommand.Execute(null); return true;
            case "sounds.reveal": vm.RevealCommand.Execute(null); return true;
            case "sounds.copyPath": vm.CopyPathCommand.Execute(null); return true;
        }
        return false;
    }

    /// <summary>The sound before or after the picked one (in the library or the history), kept in sight and played.</summary>
    bool StepSound(SoundsViewModel vm, int by, bool held)
    {
        if (vm.IsHistory)
        {
            if (HistoryList.ItemCount == 0) return false;
            var was = HistoryList.SelectedIndex;
            if (!vm.MayStep(held) || !AppShortcuts.Step(HistoryList, by) || HistoryList.SelectedIndex == was) return true;
            vm.PlayPassing();
            return true;
        }
        var shown = vm.Shown;
        if (shown.Count == 0) return false;
        var at = vm.Selected is { } s ? IndexOf(shown, s) : -1;
        StepTo(vm, shown, at, at < 0 ? 0 : Math.Clamp(at + by, 0, shown.Count - 1), held);
        return true;
    }
}
