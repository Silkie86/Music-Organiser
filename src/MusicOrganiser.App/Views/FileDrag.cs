using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.App.Views;

/// <summary>
/// Lets rows be dragged out of the app as files, into a DAW or Explorer.
///
/// Set <c>FileDrag.Source</c> to an <see cref="IFileDragSource"/>. On a list (ListBox, DataGrid)
/// the drag carries the selected rows in display order when the row under the mouse is one
/// of them, otherwise just that row. On anything else it carries the element's DataContext,
/// or every row of the list named by <c>FileDrag.AllOf</c>.
///
/// While dragging, the rows being carried dim and a preview follows the cursor showing what
/// will land (as numbered tracks, for MIDI).
/// </summary>
public static class FileDrag
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(IFileDragSource), typeof(FileDrag), new PropertyMetadata(null, OnSourceChanged));

    public static IFileDragSource? GetSource(DependencyObject d) => (IFileDragSource?)d.GetValue(SourceProperty);
    public static void SetSource(DependencyObject d, IFileDragSource? v) => d.SetValue(SourceProperty, v);

    /// <summary>On a handle (say a "Drag all" chip): drag every row of this list.</summary>
    public static readonly DependencyProperty AllOfProperty = DependencyProperty.RegisterAttached(
        "AllOf", typeof(ItemsControl), typeof(FileDrag), new PropertyMetadata(null));

    public static ItemsControl? GetAllOf(DependencyObject d) => (ItemsControl?)d.GetValue(AllOfProperty);
    public static void SetAllOf(DependencyObject d, ItemsControl? v) => d.SetValue(AllOfProperty, v);

    sealed class State
    {
        public Point Start;
        public object? Item;
        public bool Armed;
        public object? DeferredSelect;   // a click on an already-selected row: select just it on release, unless it becomes a drag
    }

    static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(State), typeof(FileDrag));

    static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.PreviewMouseLeftButtonDown -= OnDown;
        el.PreviewMouseMove -= OnMove;
        el.PreviewMouseLeftButtonUp -= OnUp;
        if (e.NewValue is null) return;
        el.SetValue(StateProperty, new State());
        el.PreviewMouseLeftButtonDown += OnDown;
        el.PreviewMouseMove += OnMove;
        el.PreviewMouseLeftButtonUp += OnUp;
        if (el is FrameworkElement fe && fe is not ItemsControl) fe.Cursor ??= Cursors.SizeAll;
    }

    static void OnDown(object sender, MouseButtonEventArgs e)
    {
        var el = (UIElement)sender;
        var st = (State)el.GetValue(StateProperty);
        st.Armed = false;
        st.DeferredSelect = null;
        if (IsInteractive(e.OriginalSource as DependencyObject, el)) return;

        st.Start = e.GetPosition(el);
        if (el is ItemsControl list)
        {
            if (ContainerAt(list, e.OriginalSource as DependencyObject) is not { } container) return;
            st.Item = list.ItemContainerGenerator.ItemFromContainer(container);
            // Pressing on one of several selected rows keeps the selection, so all of them can be dragged.
            if (Keyboard.Modifiers == ModifierKeys.None && e.ClickCount == 1 && SelectedItems(list) is { Count: > 1 } sel && sel.Contains(st.Item))
            {
                st.DeferredSelect = st.Item;
                e.Handled = true;
            }
        }
        else st.Item = (el as FrameworkElement)?.DataContext;
        st.Armed = true;
    }

    static void OnUp(object sender, MouseButtonEventArgs e)
    {
        var el = (UIElement)sender;
        var st = (State)el.GetValue(StateProperty);
        if (st.DeferredSelect is { } item && el is ItemsControl list)
        {
            switch (list)
            {
                case DataGrid g: g.SelectedItems.Clear(); g.SelectedItem = item; break;
                case ListBox b: b.SelectedItems.Clear(); b.SelectedItem = item; break;
            }
        }
        st.Armed = false;
        st.DeferredSelect = null;
    }

    static void OnMove(object sender, MouseEventArgs e)
    {
        var el = (UIElement)sender;
        var st = (State)el.GetValue(StateProperty);
        if (!st.Armed || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(el) - st.Start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance * 2 &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        st.Armed = false;
        st.DeferredSelect = null;

        var items = ItemsToDrag(el, st.Item);
        if (items.Count == 0 || GetSource(el) is not { } source) return;
        DragPayload? payload;
        try { payload = source.MakeDrag(items); }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(el), $"Couldn't prepare the files to drag: {ex.Message}", "Music Organiser");
            return;
        }
        if (payload is not { Files.Count: > 0 }) return;
        Run(el, items, payload);
    }

    static void Run(UIElement el, IReadOnlyList<object> items, DragPayload payload)
    {
        var data = new DataObject();
        var list = new StringCollection();
        list.AddRange(payload.Files.ToArray());
        data.SetFileDropList(list);

        var lifted = LiftRows(el, items);
        var preview = new DragPreview(payload);
        preview.ShowNearCursor();
        void Feedback(object _, GiveFeedbackEventArgs a)
        {
            preview.FollowCursor();
            preview.SetAccepted(a.Effects != DragDropEffects.None);
        }
        el.GiveFeedback += Feedback;
        try { DragDrop.DoDragDrop(el, data, DragDropEffects.Copy); }
        finally
        {
            el.GiveFeedback -= Feedback;
            preview.FadeAway();
            foreach (var row in lifted) Animate(row, 1, 180);
        }
    }

    /// <summary>The rows being carried, dimmed while they're "in the air".</summary>
    static List<UIElement> LiftRows(UIElement el, IReadOnlyList<object> items)
    {
        var rows = new List<UIElement>();
        if (GetAllOf(el) is { } all) el = all;
        if (el is not ItemsControl list) return rows;
        foreach (var item in items)
            if (list.ItemContainerGenerator.ContainerFromItem(item) is UIElement row)
            {
                rows.Add(row);
                Animate(row, 0.45, 120);
            }
        return rows;
    }

    static void Animate(UIElement row, double to, int ms) =>
        row.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    static IReadOnlyList<object> ItemsToDrag(UIElement el, object? pressed)
    {
        if (GetAllOf(el) is { } all) return all.Items.Cast<object>().ToList();
        if (el is not ItemsControl list) return pressed is null ? [] : [pressed];
        if (pressed is null) return [];
        var selected = SelectedItems(list);
        if (selected.Count > 1 && selected.Contains(pressed))
            return list.Items.Cast<object>().Where(selected.Contains).ToList();   // display order, not click order
        return [pressed];
    }

    static HashSet<object> SelectedItems(ItemsControl list) => list switch
    {
        MultiSelector m => m.SelectedItems.Cast<object>().ToHashSet(),
        ListBox b => b.SelectedItems.Cast<object>().ToHashSet(),
        Selector s when s.SelectedItem is { } one => [one],
        _ => [],
    };

    static DependencyObject? ContainerAt(ItemsControl list, DependencyObject? from)
    {
        for (var d = from; d is not null && d != list; d = Parent(d))
            if (list.ItemContainerGenerator.ItemFromContainer(d) != DependencyProperty.UnsetValue) return d;
        return null;
    }

    /// <summary>Buttons, text boxes, sliders and scroll bars keep working normally inside draggable rows.</summary>
    static bool IsInteractive(DependencyObject? d, UIElement stop)
    {
        for (; d is not null && d != stop; d = Parent(d))
            if (d is ButtonBase or TextBoxBase or Thumb or ScrollBar or Slider or ComboBox or DataGridColumnHeader or NoteRoll or WaveformView) return true;
        return false;
    }

    static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
}
