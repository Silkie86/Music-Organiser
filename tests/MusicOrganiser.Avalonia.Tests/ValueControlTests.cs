using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Avalonia.Views;
using MusicOrganiser.ViewModels;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>The inspector's dragged values and the tracks' volume faders: each drag is one undo step of its own.</summary>
public sealed class ValueControlTests
{
    [AvaloniaFact]
    public void A_fader_move_and_a_dragged_value_never_share_an_undo_step()
    {
        var gestures = new List<int>();
        var value = new DragValue { Width = 60, Height = 24, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Black,
                                    ByCommand = new RelayCommand<ValueStep>(s => gestures.Add(s.Gesture)) };
        var fader = new VolumeFader { Width = 100, Height = 10, HorizontalAlignment = HorizontalAlignment.Left,
                                      Command = new RelayCommand<FaderMove>(m => gestures.Add(m.Gesture)) };
        var window = new Window { Content = new StackPanel { Margin = new Thickness(0, 30, 0, 0), Children = { value, fader } }, Width = 200, Height = 100 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The octave dragged up two steps, a track's fader moved, then the octave scrolled.
        window.MouseDown(new Point(30, 42), MouseButton.Left);
        window.MouseMove(new Point(30, 22), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(30, 22), MouseButton.Left);
        window.MouseDown(new Point(50, 59), MouseButton.Left);
        window.MouseUp(new Point(50, 59), MouseButton.Left);
        window.MouseWheel(new Point(30, 42), new Vector(0, 1));

        var each = gestures.Distinct().ToList();
        Assert.Equal(3, each.Count);
        Assert.Equal(each.Order(), each);   // numbered in turn across both kinds, so no two can ever match
    }
}
