using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.App.Views;

public partial class SoundsView : UserControl
{
    public SoundsView()
    {
        InitializeComponent();
        // Autoplay follows your clicks and arrow keys, not every selection change, so sorting,
        // filtering or coming back to this page doesn't start a sound by itself.
        SoundGrid.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnRowClicked), handledEventsToo: true);
        SoundGrid.AddHandler(KeyUpEvent, new KeyEventHandler(OnGridKeyUp), handledEventsToo: true);
    }

    void OnRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SoundsViewModel vm || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.OriginalSource is not DependencyObject source || InButton(source)) return;
        if (ItemsControl.ContainerFromElement(SoundGrid, source) is DataGridRow row && row.Item == vm.Selected) vm.PlayPicked();
    }

    void OnGridKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End
            && Keyboard.Modifiers == ModifierKeys.None)
            (DataContext as SoundsViewModel)?.PlayPicked();
    }

    // The favourite star is a button inside the row; clicking it shouldn't start the sound.
    static bool InButton(DependencyObject? d)
    {
        for (; d is not null and not DataGridRow; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase) return true;
        return false;
    }
}
