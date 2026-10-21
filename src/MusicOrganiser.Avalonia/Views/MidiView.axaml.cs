using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Threading;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class MidiView : UserControl, IShortcutPage
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
    MidiBrowserViewModel? _watched;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_watched is not null) _watched.PropertyChanged -= OnVmChanged;
        _watched = Vm;
        if (_watched is not null) _watched.PropertyChanged += OnVmChanged;
        Widen();
    }

    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MidiBrowserViewModel.IsPairing) or nameof(MidiBrowserViewModel.HasSelection)) Widen();
    }

    /// <summary>
    /// A part picked: the card takes two thirds of the page, for the piano roll to edit it on and to play a suggestion with
    /// what it's fitted to (Solomon, 2026-10-18: "the card on the right could take up 2/3 of the screen").
    /// </summary>
    void Widen()
    {
        var wide = Vm is { HasSelection: true } or { IsPairing: true };
        Body.ColumnDefinitions[1].Width = wide ? new GridLength(2, GridUnitType.Star) : new GridLength(370);
    }

    /// <summary>After the list has scrolled to the part still picked, go back to the top, where the best match is.</summary>
    void OnNewSearch(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => { if (Parts.ItemCount > 0) Parts.ScrollIntoView(0); }, DispatcherPriority.Background);

    void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // On a part, not on its play button (which plays it already).
        if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(true) is not null && v.FindAncestorOfType<Button>(true) is null)
            Vm?.PlaySelectedCommand.Execute(null);
    }

    /// <summary>
    /// The MIDI page's keys (Settings › Shortcuts), for the part picked: Space plays or stops it, as in a DAW's browser,
    /// wherever the keyboard is on the page.
    /// </summary>
    public bool RunShortcut(string id, bool repeat)
    {
        if (Vm is not { Selected: { } row } vm) return false;
        System.Windows.Input.ICommand? command = id switch
        {
            "app.play" => vm.PlaySelectedCommand,
            "midi.playTogether" => vm.PlaySelectedTogetherCommand,
            "midi.fit" => vm.FitToSelectedCommand,
            "midi.moreLike" => vm.MoreLikeSelectedCommand,
            "midi.toPlayground" => vm.SendToPlaygroundCommand,
            "midi.toBitwig" => vm.SendBothToBitwigCommand,
            "midi.save" => vm.SaveSelectedCommand,
            "midi.source" => vm.ShowSourceCommand,
            "midi.octaveUp" when row.CanChangeOctave => row.OctaveUpCommand,
            "midi.octaveDown" when row.CanChangeOctave => row.OctaveDownCommand,
            _ => null,
        };
        if (command?.CanExecute(null) != true) return false;
        command.Execute(null);
        return true;
    }
}
