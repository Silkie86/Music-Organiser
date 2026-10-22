using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// The menu bar and right-click menus, built from the Playground's menu lists so each line shows its key from
/// Settings › Shortcuts and is greyed when there's nothing for it to act on.
/// </summary>
public partial class PlaygroundView
{
    PlaygroundViewModel? _menusFor;

    void BuildMenus()
    {
        if (_vm is not { } vm || _menusFor == vm) return;
        if (_menusFor is not null) { _menusFor.RenameAsked -= OnRenameAsked; _menusFor.IdeasAsked -= OnIdeasAsked; }
        _menusFor = vm;
        vm.RenameAsked += OnRenameAsked;
        vm.IdeasAsked += OnIdeasAsked;
        var top = new List<MenuItem>();
        foreach (var menu in PlaygroundViewModel.MenuBar)
        {
            var item = new MenuItem { Header = menu.Header };
            item.ItemsSource = menu.Items.Select(i => Line(vm, i)).ToList();
            item.SubmenuOpened += (_, _) => Refresh(vm, item.Items.OfType<Control>());
            top.Add(item);
        }
        MenuBar.ItemsSource = top;
    }

    static Control Line(PlaygroundViewModel vm, PlaygroundMenuItem line)
    {
        if (line.IsSeparator) return new Separator();
        var item = new MenuItem
        {
            Header = line.Header,
            Tag = line.Id,
            Command = new RelayCommand(() => vm.RunShortcut(line.Id)),
        };
        if (PlaygroundViewModel.IsToggle(line.Id)) item.ToggleType = MenuItemToggleType.CheckBox;
        return item;
    }

    /// <summary>Keys, greying and ticks as they are now, just before a menu shows.</summary>
    static void Refresh(PlaygroundViewModel vm, IEnumerable<Control> lines)
    {
        foreach (var item in lines.OfType<MenuItem>())
        {
            if (item.Tag is not string id) continue;
            item.IsEnabled = vm.CanRun(id);
            item.InputGesture = Gesture(vm.Shortcuts.KeyOf(id));
            if (item.ToggleType == MenuItemToggleType.CheckBox) item.IsChecked = vm.IsTicked(id);
        }
    }

    /// <summary>A key from the shortcut list as a menu shows it ("Ctrl+D"), or none when it has no key or can't be shown.</summary>
    static KeyGesture? Gesture(string key)
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

    void ShowMenu(Control at, IReadOnlyList<PlaygroundMenuItem> lines)
    {
        if (_vm is not { } vm) return;
        var items = lines.Select(l => Line(vm, l)).ToList();
        Refresh(vm, items);
        var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
        menu.Open(at);
    }

    void OnTimelineMenu(object? sender, string kind)
    {
        if (sender is Control at) ShowMenu(at, kind == "clip" ? PlaygroundViewModel.ClipMenu : PlaygroundViewModel.TimelineMenu);
    }

    void OnTrackMenu(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: LayerRow row } at || _vm is not { } vm) return;
        vm.PickClip(null, null);
        vm.SelectedLayer = row;
        ShowMenu(at, PlaygroundViewModel.TrackMenu);
        e.Handled = true;
    }

    void OnChordMenu(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control at) return;
        ShowMenu(at, PlaygroundViewModel.ChordMenu);
        e.Handled = true;
    }

    /// <summary>Rename (menu or key): the track's name becomes a box in its header, as in a DAW, so it works with no clip picked too.</summary>
    void OnRenameAsked(object? sender, EventArgs e)
    {
        if (_vm is not { } vm || (vm.SelectedClip?.Track ?? vm.SelectedLayer) is not { } row) return;
        TrackList.ScrollIntoView(row);
        TrackList.UpdateLayout();
        if (TrackList.ContainerFromItem(row) is not { } item ||
            item.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.Classes.Contains("trackRename")) is not { } box) return;
        box.IsVisible = true;
        box.Focus();
        box.SelectAll();
    }

    void OnTrackNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: LayerRow row } || _vm is not { } vm) return;
        vm.PickClip(null, null);
        vm.SelectedLayer = row;
        OnRenameAsked(this, EventArgs.Empty);
        e.Handled = true;
    }

    void OnTrackRenameKey(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Escape)
        {
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateTarget();   // back to the name it had
            box.IsVisible = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
            box.IsVisible = false;
            e.Handled = true;
        }
    }

    void OnTrackRenameDone(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) box.IsVisible = false;
    }

    void OnIdeasAsked(object? sender, EventArgs e) => IdeasButton.Flyout?.ShowAt(IdeasButton);
}
