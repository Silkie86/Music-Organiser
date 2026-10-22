using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// The MIDI page's list of parts, with its search and kinds. The MIDI page puts the picked part's card beside it
/// (<see cref="Side"/>); the Playground shows it smaller (Classes="compact") with an Add button on each part
/// (<see cref="AddCommand"/>) while its idea is empty.
/// </summary>
public partial class MidiListView : UserControl
{
    public static readonly StyledProperty<object?> SideProperty = AvaloniaProperty.Register<MidiListView, object?>(nameof(Side));
    public static readonly StyledProperty<ICommand?> AddCommandProperty = AvaloniaProperty.Register<MidiListView, ICommand?>(nameof(AddCommand));

    /// <summary>What goes beside the list: the MIDI page's card for the part picked.</summary>
    public object? Side
    {
        get => GetValue(SideProperty);
        set => SetValue(SideProperty, value);
    }

    /// <summary>What a part's Add button does with it (given the <see cref="MidiRow"/>); compact lists only.</summary>
    public ICommand? AddCommand
    {
        get => GetValue(AddCommandProperty);
        set => SetValue(AddCommandProperty, value);
    }

    MidiBrowserViewModel? _vm;

    public MidiListView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.NewSearchShown -= OnNewSearch;
            _vm = Vm;
            if (_vm is not null) _vm.NewSearchShown += OnNewSearch;
        };
    }

    MidiBrowserViewModel? Vm => DataContext as MidiBrowserViewModel;

    /// <summary>The list beside its card, for the MIDI page to widen the card.</summary>
    internal Grid BodyGrid => Body;

    /// <summary>After the list has scrolled to the part still picked, go back to the top, where the best match is.</summary>
    void OnNewSearch(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => { if (Parts.ItemCount > 0) Parts.ScrollIntoView(0); }, DispatcherPriority.Background);

    void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // On a part, not on its play button (which plays it already).
        if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(true) is not null && v.FindAncestorOfType<Button>(true) is null)
            Vm?.PlaySelectedCommand.Execute(null);
    }

    /// <summary>Space plays or stops the part picked, as in a DAW's browser.</summary>
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Vm is not { } vm) return;
        vm.PlaySelectedCommand.Execute(null);
        e.Handled = true;
    }
}
