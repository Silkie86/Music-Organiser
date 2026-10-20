using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class SongMidiView : UserControl
{
    public SongMidiView()
    {
        InitializeComponent();
        // Esc closes the quick view wherever the click last was (its piano roll, a button), not only in the list.
        AddHandler(KeyDownEvent, OnEscape);
    }

    SongMidiViewModel? Vm => DataContext as SongMidiViewModel;
    SongMidiViewModel? _watched;

    /// <summary>Shown (the tab opened, or another song picked while it's open): work out what fits, once per song.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Watch(Vm);
        _ = Vm?.LoadAsync();
    }

    /// <summary>Let go of the song's view model when hidden, so it doesn't keep this view alive.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Watch(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (!this.IsAttachedToVisualTree()) return;
        Watch(Vm);
        _ = Vm?.LoadAsync();
    }

    void Watch(SongMidiViewModel? vm)
    {
        if (_watched is not null) _watched.PropertyChanged -= OnVmChanged;
        _watched = vm;
        if (_watched is not null) _watched.PropertyChanged += OnVmChanged;
        Widen();
    }

    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SongMidiViewModel.IsOpen)) Widen();
    }

    /// <summary>A part opened: the quick view takes two thirds, the list the rest.</summary>
    void Widen()
    {
        var open = Vm is { IsOpen: true };
        Body.ColumnDefinitions[1].Width = open ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        Parts.Classes.Set("open", open);
    }

    /// <summary>
    /// A click on a part opens it in the quick view, but not on its play button or its little piano roll, where a click
    /// plays from that spot.
    /// </summary>
    void OnTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(true) is { DataContext: MidiRow row } &&
            v.FindAncestorOfType<Button>(true) is null && v.FindAncestorOfType<NoteRoll>(true) is null)
            Vm?.OpenPartCommand.Execute(row);
    }

    void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(true) is not null && v.FindAncestorOfType<Button>(true) is null)
            Vm?.Selected?.PlayCommand.Execute(null);
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter opens the part picked in the quick view.
        if (e.Key == Key.Enter && Vm?.Selected is { } picked)
        {
            Vm.OpenPartCommand.Execute(picked);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Space || Vm?.Selected is not { } row) return;
        row.PlayCommand.Execute(null);
        e.Handled = true;
    }

    void OnEscape(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Escape || Vm is not { IsOpen: true } vm) return;
        vm.CloseCommand.Execute(null);
        e.Handled = true;
    }
}
