using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
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
        Timeline.MenuAsked += OnTimelineMenu;
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
        BuildMenus();
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
        if (_vm is null || e.Vector.Y == 0) return;   // a click without a drag keeps the size chosen
        _vm.ClipPanelHeight = LeftPane.RowDefinitions[3].ActualHeight;
        LayOut();
    }

    void OnClipDividerReset(object? sender, TappedEventArgs e)
    {
        if (_vm is not null) _vm.ClipPanelHeight = PlaygroundViewModel.DefaultClipPanelHeight;
    }

    void OnSideDividerMoved(object? sender, VectorEventArgs e)
    {
        if (_vm is null || e.Vector.X == 0) return;
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
        if (sender is Control edge && !e.GetCurrentPoint(edge).Properties.IsLeftButtonPressed) { EndTrackDrag(); return; }
        var h = d.Height + (e.GetPosition(this).Y - d.Y) / d.Rows;
        _vm.TrackHeight = Math.Round(Math.Clamp(h, PlaygroundViewModel.MinTrackHeight, PlaygroundViewModel.MaxTrackHeight));
    }

    void OnTrackEdgeReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        EndTrackDrag();
    }

    void OnTrackEdgeCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndTrackDrag();

    void EndTrackDrag()
    {
        if (_trackDrag is null) return;
        _trackDrag = null;
        _vm?.KeepTrackHeight();   // saved once, not on every move
    }

    void OnTrackEdgeReset(object? sender, TappedEventArgs e)
    {
        if (_vm is null) return;
        _vm.TrackHeight = PlaygroundViewModel.DefaultTrackHeight;
        _vm.KeepTrackHeight();
    }

    // ---- keyboard shortcuts ----

    // The keys are looked at by the window, on the way in, while this page is on show: so they work straight after the
    // page is picked in the sidebar (which keeps the keyboard), and a focused button doesn't take Space first.
    TopLevel? _top;
    readonly HashSet<Key> _held = [];

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
        _top?.AddHandler(KeyUpEvent, OnKeyLifted, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (_top is not null) _top.LostFocus += OnTopLostFocus;
    }

    protected override void OnDetachedFromVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _top?.RemoveHandler(KeyDownEvent, OnShortcut);
        _top?.RemoveHandler(KeyUpEvent, OnKeyLifted);
        if (_top is not null) _top.LostFocus -= OnTopLostFocus;
        _top = null;
        _held.Clear();
    }

    void OnKeyLifted(object? sender, KeyEventArgs e) => _held.Remove(e.Key);
    void OnTopLostFocus(object? sender, RoutedEventArgs e) => _held.Clear();

    /// <summary>
    /// The Playground's keys, from the app's shortcut list (Settings › Shortcuts): Bitwig's unless changed. While typing in
    /// a box only Ctrl keys count (the box keeps its own Ctrl+Z, Ctrl+C and the like, and what's typed is put in first);
    /// lists, drop-downs and sliders keep their arrows, and the piano roll its Delete. Holding a key down does it once.
    /// </summary>
    void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (!IsEffectivelyVisible || DataContext is not PlaygroundViewModel vm || ShortcutKeys.Of(e) is not { } key) return;
        if (e.Source is Visual v && !this.IsVisualAncestorOf(v) && e.Source != _top && !IsSidebar(v)) return;   // a key for another part of the window
        var repeat = !_held.Add(e.Key);
        var id = vm.Shortcuts.Find(ShortcutsViewModel.Playground, key);
        if (vm.CheatSheetOpen)
        {
            // only closing it works while the sheet covers the page
            if (key == "Esc" || id == "playground.help") { if (!repeat) vm.CheatSheetOpen = false; e.Handled = true; }
            return;
        }
        if (id is null) return;
        var box = ShortcutKeys.Box(e);
        if (box is not null && (!ShortcutsViewModel.WorksWhileTyping(key) || ShortcutKeys.IsTextKey(key))) return;
        if (ShortcutKeys.UsedByControl(e, key)) return;
        if (e.Source is PianoRollEditor && key is "Delete" or "Backspace") return;   // deletes the note under the pointer
        e.Handled = true;
        if (repeat && id is "playground.play" or "playground.help" or "playground.loop" or "playground.save" or "playground.split" or "playground.duplicate"
            or "playground.paste" or "playground.new" or "playground.ideas" or "playground.rename") return;
        if (box is not null) BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();   // Ctrl+S saves the name as typed
        if (!vm.RunShortcut(id)) e.Handled = false;
    }

    /// <summary>The page list down the side, which keeps the keyboard after the Playground is picked in it.</summary>
    bool IsSidebar(Visual v) => v.FindAncestorOfType<ListBox>(includeSelf: true) is { } list && !this.IsVisualAncestorOf(list) && list.Classes.Contains("nav");

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
