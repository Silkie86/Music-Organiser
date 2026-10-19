using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MusicOrganiser.Avalonia.Views;
using MusicOrganiser.ViewModels;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>Key presses written the way the shortcut list writes them, and the Settings › Shortcuts key button.</summary>
public sealed class ShortcutTests
{
    [AvaloniaFact]
    public void Key_presses_are_written_as_the_shortcut_list_writes_them_and_boxes_are_known()
    {
        var box = new TextBox { Width = 200 };
        var panel = new StackPanel { Children = { box } };
        var window = new Window { Content = panel, Width = 400, Height = 200 };
        var seen = new List<(string? Key, bool Typing)>();
        window.AddHandler(InputElement.KeyDownEvent, (object? _, KeyEventArgs e) => seen.Add((ShortcutKeys.Of(e), ShortcutKeys.InTextBox(e))), RoutingStrategies.Tunnel);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.D, "d");
        window.KeyPress(Key.OemPlus, RawInputModifiers.Shift | RawInputModifiers.Alt, PhysicalKey.Equal, "+");
        window.KeyPress(Key.OemQuestion, RawInputModifiers.Shift, PhysicalKey.Slash, "?");
        window.KeyPress(Key.LeftShift, RawInputModifiers.Shift, PhysicalKey.ShiftLeft, null);
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Assert.Equal(["Ctrl+D", "Shift+Alt+=", "?", null, "Space"], seen.Select(s => s.Key));
        Assert.All(seen, s => Assert.False(s.Typing));

        seen.Clear();
        box.Focus();
        window.KeyPress(Key.Z, RawInputModifiers.None, PhysicalKey.Z, "z");
        Assert.Equal(("Z", true), seen.Single());
        Assert.False(ShortcutsViewModel.WorksWhileTyping("Z"));   // so typing a z doesn't zoom
    }

    [AvaloniaFact]
    public void The_key_button_listens_for_the_next_key_and_esc_leaves_it_alone()
    {
        var keys = new ShortcutsViewModel(null);
        var row = keys.Rows.Single(r => r.Action.Id == "playground.duplicate");
        var button = new ShortcutBox { DataContext = row };
        var window = new Window { Content = button, Width = 300, Height = 100 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Ctrl+D", button.Content);

        void Click()
        {
            button.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);   // a focused button clicks on Enter
            Dispatcher.UIThread.RunJobs();
        }

        Click();
        Assert.Equal("Press a key…", button.Content);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Equal("Ctrl+D", button.Content);

        Click();
        window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);   // waits for the rest
        Assert.Equal("Press a key…", button.Content);
        window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, "k");
        Assert.Equal("Ctrl+K", button.Content);
        Assert.Equal("playground.duplicate", keys.Find(ShortcutsViewModel.Playground, "Ctrl+K"));
        Assert.True(row.IsChanged);
    }
}
