using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// Lets rows be dragged out of the app as files, into a DAW or the file manager.
///
/// Set <c>FileDrag.Source</c> to an <see cref="IFileDragSource"/>. On a list (ListBox, DataGrid,
/// ItemsControl) the drag carries the selected rows in display order when the row under the mouse
/// is one of them, otherwise just that row. On anything else it carries the element's DataContext,
/// or every row of the list named by <c>FileDrag.AllOf</c>.
///
/// While dragging, the rows being carried dim and, on Windows, a preview follows the cursor
/// showing what will land (as numbered tracks, for MIDI).
/// </summary>
public sealed class FileDrag
{
    FileDrag() { }

    public static readonly AttachedProperty<IFileDragSource?> SourceProperty =
        AvaloniaProperty.RegisterAttached<FileDrag, Control, IFileDragSource?>("Source");

    public static IFileDragSource? GetSource(Control c) => c.GetValue(SourceProperty);
    public static void SetSource(Control c, IFileDragSource? v) => c.SetValue(SourceProperty, v);

    /// <summary>On a handle (say a "Drag all" chip): drag every row of this list.</summary>
    public static readonly AttachedProperty<Control?> AllOfProperty =
        AvaloniaProperty.RegisterAttached<FileDrag, Control, Control?>("AllOf");

    public static Control? GetAllOf(Control c) => c.GetValue(AllOfProperty);
    public static void SetAllOf(Control c, Control? v) => c.SetValue(AllOfProperty, v);

    /// <summary>How far the mouse moves with the button down before it counts as a drag.</summary>
    const double Threshold = 8;

    sealed class State
    {
        public Point Start;
        public object? Item;
        public bool Armed;
        public object? DeferredSelect;   // a click on an already-selected row: select just it on release, unless it becomes a drag
        public PointerPressedEventArgs? Press;
    }

    static readonly ConditionalWeakTable<Control, State> States = new();

    static FileDrag() => SourceProperty.Changed.AddClassHandler<Control>(OnSourceChanged);

    static void OnSourceChanged(Control c, AvaloniaPropertyChangedEventArgs e)
    {
        c.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
        c.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
        c.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
        if (e.NewValue is null) return;
        States.AddOrUpdate(c, new State());
        // Tunnel, so the press is seen before a row selects itself (like WPF's Preview events).
        c.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        c.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        c.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (c is not ItemsControl and not DataGrid) c.Cursor ??= new Cursor(StandardCursorType.SizeAll);
    }

    static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control el || !States.TryGetValue(el, out var st)) return;
        st.Armed = false;
        st.DeferredSelect = null;
        st.Press = null;
        if (!e.GetCurrentPoint(el).Properties.IsLeftButtonPressed) return;
        if (IsInteractive(e.Source as Visual, el)) return;

        st.Start = e.GetPosition(el);
        if (el is DataGrid or ItemsControl)
        {
            if (RowAt(el, e.Source as Visual) is not { } item) return;
            st.Item = item;
            // Pressing on one of several selected rows keeps the selection, so all of them can be dragged.
            if (e.KeyModifiers == KeyModifiers.None && e.ClickCount == 1 && SelectedItems(el) is { Count: > 1 } sel && sel.Contains(item))
            {
                st.DeferredSelect = item;
                e.Handled = true;
            }
        }
        else st.Item = el.DataContext;
        st.Press = e;
        st.Armed = true;
    }

    static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control el || !States.TryGetValue(el, out var st)) return;
        if (st.DeferredSelect is { } item)
        {
            switch (el)
            {
                case DataGrid g: g.SelectedItems.Clear(); g.SelectedItem = item; break;
                case ListBox b: b.SelectedItems?.Clear(); b.SelectedItem = item; break;
            }
        }
        st.Armed = false;
        st.DeferredSelect = null;
        st.Press = null;
    }

    static async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control el || !States.TryGetValue(el, out var st) || !st.Armed || st.Press is not { } press) return;
        if (!e.GetCurrentPoint(el).Properties.IsLeftButtonPressed)
        {
            st.Armed = false;
            return;
        }
        var d = e.GetPosition(el) - st.Start;
        if (Math.Abs(d.X) < Threshold && Math.Abs(d.Y) < Threshold) return;
        st.Armed = false;
        st.DeferredSelect = null;
        st.Press = null;

        var items = ItemsToDrag(el, st.Item);
        if (items.Count == 0 || GetSource(el) is not { } source) return;
        try
        {
            if (source.MakeDrag(items) is not { Files.Count: > 0 } payload) return;
            await Run(el, press, items, payload);
        }
        catch (Exception ex)
        {
            if ((TopLevel.GetTopLevel(el) as Window ?? MainWindow) is { } owner)
                await new ConfirmWindow($"Couldn't prepare the files to drag: {ex.Message}", "Music Organiser", okOnly: true).ShowDialog(owner);
        }
    }

    static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>
    /// Drags <paramref name="items"/> out of the app as files from code, for a control that decides for itself when a
    /// drag leaves it (the Playground's timeline, when a clip or a label is pulled out of it).
    /// </summary>
    public static async Task DragOut(Control el, PointerPressedEventArgs press, IFileDragSource source, IReadOnlyList<object> items)
    {
        try
        {
            if (source.MakeDrag(items) is { Files.Count: > 0 } payload) await Run(el, press, items, payload);
        }
        catch (Exception ex)
        {
            if ((TopLevel.GetTopLevel(el) as Window ?? MainWindow) is { } owner)
                await new ConfirmWindow($"Couldn't prepare the files to drag: {ex.Message}", "Music Organiser", okOnly: true).ShowDialog(owner);
        }
    }

    /// <summary>
    /// The app's own files are being dragged out (a clip, a track, the arrangement), so a drop back onto the app isn't
    /// taken as new files coming in.
    /// </summary>
    public static bool Dragging { get; private set; }

    static async Task Run(Control el, PointerPressedEventArgs press, IReadOnlyList<object> items, DragPayload payload)
    {
        if (TopLevel.GetTopLevel(el)?.StorageProvider is not { } storage) return;
        var data = new DataTransfer();
        foreach (var path in payload.Files)
            if (await storage.TryGetFileFromPathAsync(path) is { } file)
                data.Add(DataTransferItem.CreateFile(file));
        if (data.Items.Count == 0) return;

        var lifted = LiftRows(el, items);
        Action? hidePreview = null;
        if (OperatingSystem.IsWindows()) hidePreview = ShowPreview(payload);
        Dragging = true;
        try { await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy); }
        finally
        {
            Dragging = false;
            hidePreview?.Invoke();
            foreach (var row in lifted) row.Opacity = 1;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static Action ShowPreview(DragPayload payload)
    {
        var preview = new DragPreview(payload);
        preview.ShowNearCursor();
        return preview.FadeAway;
    }

    /// <summary>The rows being carried, dimmed while they're "in the air".</summary>
    static List<Control> LiftRows(Control el, IReadOnlyList<object> items)
    {
        if (GetAllOf(el) is { } all) el = all;
        var rows = el switch
        {
            DataGrid g => g.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.DataContext is { } item && items.Contains(item)).Cast<Control>().ToList(),
            ItemsControl list => items.Select(list.ContainerFromItem).OfType<Control>().ToList(),
            _ => [],
        };
        foreach (var row in rows) row.Opacity = 0.45;
        return rows;
    }

    static IReadOnlyList<object> ItemsToDrag(Control el, object? pressed)
    {
        if (GetAllOf(el) is { } all) return Rows(all);
        if (el is not (DataGrid or ItemsControl)) return pressed is null ? [] : [pressed];
        if (pressed is null) return [];
        var selected = SelectedItems(el);
        if (selected.Count > 1 && selected.Contains(pressed))
            return Rows(el).Where(selected.Contains).ToList();   // display order, not click order
        return [pressed];
    }

    /// <summary>Every row of a list, in the order shown (a grid's sort and filter included).</summary>
    static IReadOnlyList<object> Rows(Control list) => list switch
    {
        DataGrid g => (g.CollectionView ?? g.ItemsSource)?.Cast<object>().ToList() ?? [],
        ItemsControl i => i.Items.Cast<object>().ToList(),
        _ => [],
    };

    static HashSet<object> SelectedItems(Control list) => list switch
    {
        DataGrid g => g.SelectedItems.Cast<object>().ToHashSet(),
        ListBox b when b.SelectedItems is { } items => items.Cast<object>().ToHashSet(),
        SelectingItemsControl s when s.SelectedItem is { } one => [one],
        _ => [],
    };

    /// <summary>The row (its item) under the mouse, or null when the press wasn't on a row.</summary>
    static object? RowAt(Control list, Visual? from)
    {
        if (list is DataGrid) return from?.FindAncestorOfType<DataGridRow>(true)?.DataContext;
        var items = (ItemsControl)list;
        for (var v = from; v is not null && v != list; v = v.GetVisualParent())
            if (v is Control c && items.IndexFromContainer(c) >= 0) return items.ItemFromContainer(c);
        return null;
    }

    /// <summary>Buttons, text boxes, sliders and scroll bars keep working normally inside draggable rows.</summary>
    static bool IsInteractive(Visual? v, Control stop)
    {
        for (; v is not null && v != stop; v = v.GetVisualParent())
            if (v is Button or TextBox or Thumb or ScrollBar or Slider or ComboBox or DataGridColumnHeader or NoteRoll or WaveformView) return true;
        return false;
    }
}
