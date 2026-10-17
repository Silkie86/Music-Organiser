using Avalonia.Controls;
using Avalonia.Data;
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

    /// <summary>Space plays or stops the section, as in a DAW (not while typing). Enter in the chord chart or a part's name
    /// puts what's typed in (those boxes take it when you leave them, not on every key, so a half-typed chord isn't read).</summary>
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.Source is TextBox { Classes: var classes } box && classes.Contains("commitOnEnter"))
        {
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Space || e.Source is TextBox || DataContext is not PlaygroundViewModel vm) return;
        vm.PlayCommand.Execute(null);
        e.Handled = true;
    }
}
