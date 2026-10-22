using Avalonia;
using Avalonia.Controls;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class MidiView : UserControl
{
    public MidiView() => InitializeComponent();

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
        List.BodyGrid.ColumnDefinitions[1].Width = wide ? new GridLength(2, GridUnitType.Star) : new GridLength(370);
    }
}
