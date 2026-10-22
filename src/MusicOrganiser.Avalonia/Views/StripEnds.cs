using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// Marks the first and last shown button of a joined strip (StackPanel.group) with the classes "stripStart" and
/// "stripEnd", so Studio.axaml can round and edge the strip's ends. A hidden button doesn't count: a song with no older
/// saves hides "Older saves", and the strip still closes after Open. (:nth-child counts hidden ones, so it can't.)
/// Studio.axaml turns this on for every strip.
/// </summary>
public static class StripEnds
{
    public static readonly AttachedProperty<bool> MarkProperty =
        AvaloniaProperty.RegisterAttached<StackPanel, bool>("Mark", typeof(StripEnds));

    public static bool GetMark(StackPanel p) => p.GetValue(MarkProperty);
    public static void SetMark(StackPanel p, bool value) => p.SetValue(MarkProperty, value);

    static StripEnds()
    {
        MarkProperty.Changed.AddClassHandler<StackPanel>((panel, e) =>
        {
            panel.Children.CollectionChanged -= OnChildren;
            if (e.NewValue is not true) return;
            panel.Children.CollectionChanged += OnChildren;
            Mark(panel);
        });
        // One handler for every control: when a strip's button shows or hides, its strip's ends move.
        Visual.IsVisibleProperty.Changed.AddClassHandler<Control>((c, _) =>
        {
            if (c.Parent is StackPanel panel && GetMark(panel)) Mark(panel);
        });
    }

    static void OnChildren(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is Controls { Count: > 0 } children && children[0].Parent is StackPanel panel) Mark(panel);
    }

    static void Mark(StackPanel panel)
    {
        Control? first = null, last = null;
        foreach (var child in panel.Children)
            if (child.IsVisible) { first ??= child; last = child; }
        foreach (var child in panel.Children)
        {
            child.Classes.Set("stripStart", child == first);
            child.Classes.Set("stripEnd", child == last);
        }
    }
}
