using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class PlaygroundView : UserControl, IShortcutPage
{
    public PlaygroundView()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        Timeline.MenuAsked += OnTimelineMenu;
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
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

    // The window answers the keys (AppShortcuts) and hands this page its own: so there's one key system and one keys sheet.

    /// <summary>Does a Playground shortcut; the ones that work everywhere are left to the window.</summary>
    public bool RunShortcut(string id, bool repeat) => id.StartsWith("playground.") && DataContext is PlaygroundViewModel vm && vm.RunShortcut(id);

    /// <summary>The piano roll keeps Delete and Backspace for the note under the pointer.</summary>
    public bool KeepsKey(KeyEventArgs e, string key) => e.Source is PianoRollEditor && key is "Delete" or "Backspace";

    TopLevel? _top;

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(KeyUpEvent, OnKeyLifted, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _top?.RemoveHandler(KeyUpEvent, OnKeyLifted);
        _top = null;
    }

    // Letting go of Alt opens the menu bar's keys, unless Alt was only held to zoom with the wheel.
    void OnKeyLifted(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftAlt or Key.RightAlt && _altWheeled)
        {
            _altWheeled = false;
            Dispatcher.UIThread.Post(() => MenuBar.Close());
        }
    }

    // ---- Alt + wheel: taller or shorter tracks (the piano roll does its own notes) ----

    bool _altWheeled;

    void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.Delta.Y == 0) return;
        _altWheeled = true;
        if (_vm is null || e.Source is not Visual v || !TrackScroll.IsVisualAncestorOf(v)) return;
        var old = _vm.TrackHeight;
        var height = Math.Round(Math.Clamp(old * Math.Pow(1.15, e.Delta.Y), PlaygroundViewModel.MinTrackHeight, PlaygroundViewModel.MaxTrackHeight));
        e.Handled = true;   // not scrolled as well
        if (height == old) return;
        // the track under the pointer stays under it
        var y = e.GetPosition(TrackScroll).Y;
        var offset = TrackScroll.Offset;
        _vm.TrackHeight = height;
        _vm.KeepTrackHeight();
        var to = Math.Max(0, (offset.Y + y) * height / old - y);
        Dispatcher.UIThread.Post(() => TrackScroll.Offset = TrackScroll.Offset.WithY(to), DispatcherPriority.Background);   // once the rows are laid out at their new height
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
}
