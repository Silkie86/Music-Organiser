using Avalonia.Controls;
using Avalonia.Input;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class PlaygroundView : UserControl
{
    public PlaygroundView()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    /// <summary>Space plays or stops the section, as in a DAW (not while typing).</summary>
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.Source is TextBox || DataContext is not PlaygroundViewModel vm) return;
        vm.PlayCommand.Execute(null);
        e.Handled = true;
    }
}
