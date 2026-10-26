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
            Key.PageUp => "PageUp",   // named once here: the key has two names (Prior too), and either may come back
            Key.PageDown => "PageDown",
            >= Key.D0 and <= Key.D9 => ((char)('0' + (e.Key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (e.Key - Key.NumPad0))).ToString(),
            _ => e.Key.ToString(),
        };
        return (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta) ? "Ctrl+" : "")
             + (mods.HasFlag(KeyModifiers.Shift) ? "Shift+" : "")
             + (mods.HasFlag(KeyModifiers.Alt) ? "Alt+" : "") + key;
    }

    /// <summary>Whether the key was pressed in a box being typed in, where only Ctrl keys count as shortcuts.</summary>
    public static bool InTextBox(KeyEventArgs e) => Box(e) is not null;

    /// <summary>
    /// Whether the control the key was pressed in uses that key itself: the arrows (and Home, End, Page Up/Down) in a
    /// list, a drop-down or a slider. A tab strip doesn't count, so the side panel's tabs never swallow the page's keys.
    /// </summary>
    public static bool UsedByControl(KeyEventArgs e, string key)
    {
        if (key is not ("Up" or "Down" or "Left" or "Right" or "Home" or "End" or "PageUp" or "PageDown") || e.Source is not Visual v) return false;
        return v is ListBox or ComboBox or Slider || v.FindAncestorOfType<ListBox>() is not null
            || v.FindAncestorOfType<ComboBox>() is not null || v.FindAncestorOfType<Slider>() is not null;
    }

    /// <summary>The keys a box being typed in keeps for its own text, even though they're held with Ctrl.</summary>
    public static bool IsTextKey(string key) =>
        key is "Ctrl+Z" or "Ctrl+Shift+Z" or "Ctrl+Y" or "Ctrl+A" or "Ctrl+C" or "Ctrl+V" or "Ctrl+X" or "Ctrl+Left" or "Ctrl+Right"
            or "Ctrl+Shift+Left" or "Ctrl+Shift+Right" or "Ctrl+Backspace" or "Ctrl+Delete" or "Ctrl+Home" or "Ctrl+End";

    /// <summary>The box the key was pressed in, if any.</summary>
    public static TextBox? Box(KeyEventArgs e) => e.Source as TextBox ?? (e.Source as Visual)?.FindAncestorOfType<TextBox>();

    /// <summary>A key from the shortcut list as a menu shows it ("Ctrl+D"), or none when it has no key or can't be shown.</summary>
    public static KeyGesture? Gesture(string key)
    {
        if (key.Length == 0 || key == "?") return null;
        // the last part is the key, the rest are Ctrl, Shift and Alt; keys set in Settings can be any of these
        var cut = key.Length > 1 && key.EndsWith('+') ? key.Length - 1 : key.LastIndexOf('+') + 1;
        var last = key[cut..] switch
        {
            "=" => "OemPlus", "-" => "OemMinus", "," => "OemComma", "." => "OemPeriod", "/" => "OemQuestion",
            "[" => "OemOpenBrackets", "]" => "OemCloseBrackets", ";" => "OemSemicolon", "'" => "OemQuotes",
            "Esc" => "Escape", "Enter" => "Return", "Backspace" => "Back",
            [var c] when char.IsDigit(c) => "D" + c,
            var k => k,
        };
        try { return KeyGesture.Parse(key[..cut] + last); }
        catch (Exception e) when (e is ArgumentException or FormatException or KeyNotFoundException) { return null; }
    }

    /// <summary>
    /// Each menu line whose Tag is an action's id ("sounds.favourite") shows that action's key as it is now, so a key
    /// changed in Settings › Shortcuts shows the next time the menu opens.
    /// </summary>
    public static void ShowKeys(IEnumerable<object?> lines, ShortcutsViewModel keys)
    {
        foreach (var item in lines.OfType<MenuItem>())
            if (item.Tag is string id) item.InputGesture = Gesture(keys.KeyOf(id));
    }
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

    /// <summary>Waiting for the new key: the page's own keys stand aside until it's pressed.</summary>
    public bool IsListening => _listening;

    protected override void OnClick()
    {
        base.OnClick();
        _listening = !_listening;
        Show();
    }

    ShortcutRow? _watched;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Watch(Row);
        Show();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Watch(Row);
        Show();
    }

    // The rows last as long as the app and Settings is rebuilt on each visit, so a box lets go of its row when it goes.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Watch(null);
    }

    void Watch(ShortcutRow? row)
    {
        if (_watched == row) return;
        if (_watched is not null) _watched.PropertyChanged -= OnRowChanged;
        _watched = row;
        if (row is not null) row.PropertyChanged += OnRowChanged;
    }

    void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (!_listening) Show(); }


    void Show() => Content = _listening ? "Press a key…" : Row?.KeyText ?? "";

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (!_listening || e.Key == Key.Tab) return;   // Tab still moves on, so it can't be a shortcut
        e.Handled = true;
        if (e.Key == Key.Escape) { _listening = false; Show(); return; }
        if (ShortcutKeys.Of(e) is not { } key) return;   // a modifier on its own: wait for the rest
        _listening = false;
        Row?.SetCommand.Execute(key);
        Show();
    }
}
