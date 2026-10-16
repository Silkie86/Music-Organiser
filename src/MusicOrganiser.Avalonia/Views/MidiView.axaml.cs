using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Threading;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class MidiView : UserControl
{
    MidiBrowserViewModel? _vm;

    public MidiView()
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
