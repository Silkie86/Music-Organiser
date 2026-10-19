using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class PlaygroundView : UserControl
{
    public PlaygroundView()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        // Shortcuts are looked at on the way in, so a focused button doesn't take Space first (as in a DAW)
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
        LeftPane.SizeChanged += (_, e) => { if (e.HeightChanged) LayOutRows(); };   // not while the side panel's edge is dragged
    }

    // ---- pane sizes (Solomon's design, point 18): dragged, remembered, double-click a divider to put it back ----

    PlaygroundViewModel? _vm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as PlaygroundViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        LayOut();
    }

    void LayOut()
    {
        LayOutSide();
        LayOutRows();
    }

    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaygroundViewModel.HasSelectedClip) or nameof(PlaygroundViewModel.PianoFolded)
            or nameof(PlaygroundViewModel.ClipPanelHeight) or nameof(PlaygroundViewModel.SidePanelWidth))
            LayOut();
    }

    /// <summary>
    /// Size the panes as last dragged: the side panel's width, and the clip panel under the timeline (just its buttons
    /// when the piano roll is folded, nothing when no clip is picked). The timeline always keeps some room.
    /// </summary>
    void LayOutSide()
    {
        if (_vm is not { } vm) return;
        var side = Body.ColumnDefinitions[2];
        side.Width = new GridLength(vm.SidePanelWidth);
        side.MinWidth = PlaygroundViewModel.MinSidePanelWidth;
        side.MaxWidth = PlaygroundViewModel.MaxSidePanelWidth;
        Body.ColumnDefinitions[0].MinWidth = 420;
    }

    void LayOutRows()
    {
        if (_vm is not { } vm) return;
        var rows = LeftPane.RowDefinitions;
        rows[1].Height = new GridLength(1, GridUnitType.Star);
        var open = vm.HasSelectedClip && !vm.PianoFolded;
        ClipDivider.IsVisible = open;
        if (!open) { rows[3].Height = GridLength.Auto; rows[3].MinHeight = 0; rows[3].MaxHeight = double.PositiveInfinity; return; }
        // leave the timeline at least 120 px, however tall the panel was dragged on a bigger screen
        var room = LeftPane.Bounds.Height - rows[0].ActualHeight - 12 - 120;
        rows[3].MinHeight = PlaygroundViewModel.MinClipPanelHeight;
        rows[3].MaxHeight = room > PlaygroundViewModel.MinClipPanelHeight ? room : double.PositiveInfinity;
        rows[3].Height = new GridLength(room > PlaygroundViewModel.MinClipPanelHeight ? Math.Min(vm.ClipPanelHeight, room) : vm.ClipPanelHeight);
    }

    void OnClipDividerMoved(object? sender, VectorEventArgs e)
    {
        if (_vm is null) return;
        _vm.ClipPanelHeight = LeftPane.RowDefinitions[3].ActualHeight;
        LayOut();
    }

    void OnClipDividerReset(object? sender, TappedEventArgs e)
    {
        if (_vm is not null) _vm.ClipPanelHeight = PlaygroundViewModel.DefaultClipPanelHeight;
    }

    void OnSideDividerMoved(object? sender, VectorEventArgs e)
    {
        if (_vm is null) return;
        _vm.SidePanelWidth = Math.Clamp(Body.ColumnDefinitions[2].ActualWidth, PlaygroundViewModel.MinSidePanelWidth, PlaygroundViewModel.MaxSidePanelWidth);
        LayOut();
    }

    void OnSideDividerReset(object? sender, TappedEventArgs e)
    {
        if (_vm is not null) _vm.SidePanelWidth = PlaygroundViewModel.DefaultSidePanelWidth;
    }

    // Dragging the line under a track's name makes every track taller or shorter. Each track above the line grows too,
    // so the line follows the pointer by moving the height a share of the drag.
    (double Y, double Height, int Rows)? _trackDrag;

    void OnTrackEdgePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null || sender is not Control { DataContext: LayerRow row } edge || !e.GetCurrentPoint(edge).Properties.IsLeftButtonPressed) return;
        _trackDrag = (e.GetPosition(this).Y, _vm.TrackHeight, Math.Max(1, _vm.Layers.IndexOf(row) + 1));
        e.Pointer.Capture(edge);
        e.Handled = true;
    }

    void OnTrackEdgeMoved(object? sender, PointerEventArgs e)
    {
        if (_vm is null || _trackDrag is not { } d) return;
        var h = d.Height + (e.GetPosition(this).Y - d.Y) / d.Rows;
        _vm.TrackHeight = Math.Round(Math.Clamp(h, PlaygroundViewModel.MinTrackHeight, PlaygroundViewModel.MaxTrackHeight));
    }

    void OnTrackEdgeReleased(object? sender, PointerReleasedEventArgs e)
    {
        _trackDrag = null;
        e.Pointer.Capture(null);
    }

    void OnTrackEdgeReset(object? sender, TappedEventArgs e)
    {
        if (_vm is not null) _vm.TrackHeight = PlaygroundViewModel.DefaultTrackHeight;
    }

    /// <summary>
    /// The Playground's keys, from the app's shortcut list (Settings › Shortcuts): Bitwig's unless changed. While typing in
    /// a box only Ctrl keys count, and lists and the piano roll keep their own arrows and Delete.
    /// </summary>
    void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (DataContext is not PlaygroundViewModel vm || ShortcutKeys.Of(e) is not { } key) return;
        if (vm.CheatSheetOpen && key == "Esc") { vm.CheatSheetOpen = false; e.Handled = true; return; }
        var plain = !ShortcutsViewModel.WorksWhileTyping(key);
        if (plain && (ShortcutKeys.InTextBox(e) || ShortcutKeys.InList(e))) return;
        if (plain && e.Source is PianoRollEditor && key is "Delete" or "Backspace") return;   // deletes the note under the pointer
        if (vm.Shortcuts.Find(ShortcutsViewModel.Playground, key) is { } id && vm.RunShortcut(id)) e.Handled = true;
    }

    /// <summary>Enter in the chord chart or a part's name puts what's typed in (those boxes take it when you leave them,
    /// not on every key, so a half-typed chord isn't read).</summary>
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.Source is TextBox { Classes: var classes } box && classes.Contains("commitOnEnter"))
        {
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    void OnCheatSheetBackdrop(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is PlaygroundViewModel vm) vm.CheatSheetOpen = false;
    }

    void OnCheatSheetCard(object? sender, PointerPressedEventArgs e) => e.Handled = true;   // a click on the sheet itself leaves it open
}
