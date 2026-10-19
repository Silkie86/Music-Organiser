using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>Turns a key press into the way <see cref="ShortcutsViewModel"/> writes keys ("Ctrl+D", "Shift+Alt+=", "?").</summary>
public static class ShortcutKeys
{
    /// <summary>The key pressed, or null for a modifier on its own (Ctrl, Shift, Alt) or a key the list doesn't name.</summary>
    public static string? Of(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.None) return null;
        var mods = e.KeyModifiers;
        // ? is Shift and / on most keyboards but not all, so it's known by what it types
        if (e.KeySymbol == "?" && (mods & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0) return "?";
        var key = e.Key switch
        {
            Key.OemPlus or Key.Add => "=",
            Key.OemMinus or Key.Subtract => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.Back => "Backspace",
            Key.Return => "Enter",
            Key.Escape => "Esc",
            >= Key.D0 and <= Key.D9 => ((char)('0' + (e.Key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (e.Key - Key.NumPad0))).ToString(),
            _ => e.Key.ToString(),
        };
        return (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta) ? "Ctrl+" : "")
             + (mods.HasFlag(KeyModifiers.Shift) ? "Shift+" : "")
             + (mods.HasFlag(KeyModifiers.Alt) ? "Alt+" : "") + key;
    }

    /// <summary>Whether the key was pressed in a box being typed in, where only Ctrl keys count as shortcuts.</summary>
    public static bool InTextBox(KeyEventArgs e) => e.Source is TextBox || (e.Source as Visual)?.FindAncestorOfType<TextBox>() is not null;

    /// <summary>Whether the key was pressed in a list or drop-down, which uses arrows and Delete itself.</summary>
    public static bool InList(KeyEventArgs e) =>
        e.Source is Visual v && (v is SelectingItemsControl or ListBoxItem or ComboBoxItem || v.FindAncestorOfType<SelectingItemsControl>() is not null);
}

/// <summary>
/// The key button on Settings › Shortcuts: click it, press the new key (Esc to leave it as it was), and it's set.
/// </summary>
public sealed class ShortcutBox : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    bool _listening;

    public ShortcutBox()
    {
        MinWidth = 130;
        HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Center;
        ToolTip.SetTip(this, "Click, then press the key you want (Esc leaves it as it is)");
        AddHandler(KeyDownEvent, OnKey, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
        LostFocus += (_, _) => { _listening = false; Show(); };
    }

    ShortcutRow? Row => DataContext as ShortcutRow;

    protected override void OnClick()
    {
        base.OnClick();
        _listening = !_listening;
        Show();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Row is { } row) row.PropertyChanged += (_, _) => { if (!_listening) Show(); };
        Show();
    }


    void Show() => Content = _listening ? "Press a key…" : Row?.KeyText ?? "";

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (!_listening) return;
        e.Handled = true;
        if (e.Key == Key.Escape) { _listening = false; Show(); return; }
        if (ShortcutKeys.Of(e) is not { } key) return;   // a modifier on its own: wait for the rest
        _listening = false;
        Row?.SetCommand.Execute(key);
        Show();
    }
}
