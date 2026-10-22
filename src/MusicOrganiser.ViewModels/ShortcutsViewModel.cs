using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.ViewModels;

/// <summary>Something a key does, on one page: <see cref="Bitwig"/> is the key Bitwig gives it (or "" when Bitwig has none).</summary>
public sealed record ShortcutAction(string Id, string Page, string Name, string Bitwig);

/// <summary>
/// One list of keyboard shortcuts for the whole app (Solomon, 2026-10-19): the keys start as Bitwig's, any can be
/// changed in Settings › Shortcuts, and pressing ? on a page shows that page's. Pages ask <see cref="Find"/> what a key
/// press means. The Playground is first; the other pages move over to it later.
/// </summary>
/// <remarks>
/// A key is written as its modifiers in the order Ctrl, Shift, Alt, then the key, joined with "+" ("Ctrl+D",
/// "Shift+Alt+=", "Space", "Delete"); "?" stands on its own. Only keys changed from Bitwig's are saved.
/// </remarks>
public sealed partial class ShortcutsViewModel : ObservableObject
{
    public const string SettingKey = "shortcuts";
    public const string Playground = "Playground";

    /// <summary>
    /// Every action, with Bitwig's key for it on Windows. Bitwig's own list was checked where it could be (its user guide
    /// and shortcut lists for Bitwig 5); "" where Bitwig has nothing like it.
    /// </summary>
    public static IReadOnlyList<ShortcutAction> Actions { get; } =
    [
        new("playground.play", Playground, "Play or stop", "Space"),
        new("playground.loop", Playground, "Loop on or off", "Ctrl+L"),
        new("playground.undo", Playground, "Undo", "Ctrl+Z"),
        new("playground.redo", Playground, "Redo", "Ctrl+Shift+Z"),
        new("playground.save", Playground, "Save the idea", "Ctrl+S"),
        new("playground.duplicate", Playground, "Duplicate the picked clip", "Ctrl+D"),
        new("playground.split", Playground, "Split the picked clip at the play marker or highlight", "Ctrl+E"),
        new("playground.delete", Playground, "Delete the picked clip", "Delete"),
        new("playground.nudgeLeft", Playground, "Move the picked clip back one grid step", "Left"),
        new("playground.nudgeRight", Playground, "Move the picked clip on one grid step", "Right"),
        new("playground.nudgeBarLeft", Playground, "Move the picked clip back a bar", "Shift+Left"),
        new("playground.nudgeBarRight", Playground, "Move the picked clip on a bar", "Shift+Right"),
        new("playground.up", Playground, "Picked clip up a semitone", "Alt+="),
        new("playground.down", Playground, "Picked clip down a semitone", "Alt+-"),
        new("playground.octaveUp", Playground, "Picked clip up an octave", "Shift+Alt+="),
        new("playground.octaveDown", Playground, "Picked clip down an octave", "Shift+Alt+-"),
        new("playground.fitAgain", Playground, "Fit the picked clip to the chords again", ""),
        new("playground.zoomIn", Playground, "Zoom in", "Ctrl+="),
        new("playground.zoomOut", Playground, "Zoom out", "Ctrl+-"),
        new("playground.zoomFit", Playground, "Zoom to the highlight, or the whole idea", "Z"),
        new("playground.help", Playground, "Show these shortcuts", "?"),
        // The Playground's menus (2026-10-21): every line is an action here, so its key can be changed. Bitwig's keys
        // for copy, cut, paste, rename and new; the rest have none until one is picked.
        new("playground.copy", Playground, "Copy the picked clip", "Ctrl+C"),
        new("playground.cut", Playground, "Cut the picked clip", "Ctrl+X"),
        new("playground.paste", Playground, "Paste a clip at the play marker", "Ctrl+V"),
        new("playground.rename", Playground, "Rename the picked clip or track", "Ctrl+R"),
        new("playground.new", Playground, "New idea", "Ctrl+N"),
        new("playground.ideas", Playground, "Open an idea", "Ctrl+O"),
        new("playground.sendToBitwig", Playground, "Send the idea to Bitwig", ""),
        new("playground.addLabel", Playground, "Label over the highlight", ""),
        new("playground.writeChords", Playground, "Write chords", ""),
        new("playground.writeBass", Playground, "Write a bassline", ""),
        new("playground.writeGuideTones", Playground, "Write guide tones", ""),
        new("playground.smooth", Playground, "Smooth inversions on the picked track", ""),
        new("playground.alternatives", Playground, "Find alternatives for the picked clip", ""),
        new("playground.copyGroove", Playground, "Copy the picked track's groove", ""),
        new("playground.mute", Playground, "Mute the picked track", ""),
        new("playground.solo", Playground, "Solo the picked track", ""),
        new("playground.duplicateTrack", Playground, "Duplicate the picked track", ""),
        new("playground.deleteTrack", Playground, "Delete the picked track", ""),
        new("playground.jazz", Playground, "Jazz and gospel harmony", ""),
        new("playground.classical", Playground, "Classical harmony", ""),
        new("playground.reharm", Playground, "Reharmonise", ""),
        new("playground.foldRoll", Playground, "Show or hide the piano roll", ""),
        new("playground.tabMidi", Playground, "Show your MIDI or suggestions", ""),
        new("playground.tabWrite", Playground, "Show Write", ""),
        new("playground.tabGroove", Playground, "Show Groove", ""),
        new("playground.tabSong", Playground, "Show Song", ""),
    ];

    readonly LibraryDb? _db;
    readonly Dictionary<string, string> _changed;

    public ShortcutsViewModel(LibraryDb? db)
    {
        _db = db;
        _changed = Load(db?.GetSetting(SettingKey));
        Rows = new(Actions.Select(a => new ShortcutRow(this, a)));
    }

    static Dictionary<string, string> Load(string? json)
    {
        try { return json is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>The Settings › Shortcuts list, in the order of <see cref="Actions"/>.</summary>
    public ObservableCollection<ShortcutRow> Rows { get; }

    /// <summary>What the last change did, such as which action lost its key to it.</summary>
    [ObservableProperty] string _message = "";

    public string KeyOf(string id) =>
        _changed.TryGetValue(id, out var k) ? k : Actions.FirstOrDefault(a => a.Id == id)?.Bitwig ?? "";

    /// <summary>The action <paramref name="key"/> does on <paramref name="page"/>, or null.</summary>
    public string? Find(string page, string key)
    {
        if (key.Length == 0) return null;
        key = Normalise(key);
        string? Look(string k) => Actions.FirstOrDefault(a => a.Page == page && KeyOf(a.Id) == k)?.Id;
        // ? where the keyboard doesn't say what it types, and Ctrl and the + key with Shift held (Bitwig's "Ctrl++")
        return Look(key) ?? key switch
        {
            "Shift+/" => Look("?"),
            "Ctrl+Shift+=" => Look("Ctrl+="),
            _ => null,
        };
    }

    /// <summary>A key as shown: Cmd for Ctrl on a Mac, where Cmd does Ctrl's jobs.</summary>
    public static string Display(string key) => OperatingSystem.IsMacOS() ? key.Replace("Ctrl+", "Cmd+") : key;

    /// <summary>The shortcuts on <paramref name="page"/> that have a key, for the ? cheat sheet.</summary>
    public IReadOnlyList<ShortcutRow> For(string page) => Rows.Where(r => r.Action.Page == page && r.Key.Length > 0).ToList();

    /// <summary>
    /// Gives <paramref name="id"/> the key <paramref name="key"/> ("" for none). Another action on the same page that had
    /// it is left with no key, and <see cref="Message"/> warns about it, so one key never does two things.
    /// </summary>
    public void Set(string id, string key)
    {
        if (Actions.FirstOrDefault(a => a.Id == id) is not { } action) return;
        key = Normalise(key);
        Message = "";
        if (key.Length > 0 && Actions.FirstOrDefault(a => a.Id != id && a.Page == action.Page && KeyOf(a.Id) == key) is { } taken)
        {
            Store(taken, "");
            Message = $"{key} was \"{taken.Name}\", which has no key now. \"Back to Bitwig's\" puts it back.";
        }
        Store(action, key);
        Save();
    }

    void Store(ShortcutAction action, string key)
    {
        if (key == action.Bitwig) _changed.Remove(action.Id);
        else _changed[action.Id] = key;
        Rows.FirstOrDefault(r => r.Action == action)?.Refresh();
    }

    void Save()
    {
        _db?.SetSetting(SettingKey, JsonSerializer.Serialize(_changed));
        OnPropertyChanged(nameof(AnyChanged));
    }

    public bool AnyChanged => _changed.Count > 0;

    /// <summary>Every key back to Bitwig's.</summary>
    [RelayCommand]
    public void ResetAll()
    {
        _changed.Clear();
        foreach (var r in Rows) r.Refresh();
        Message = "All keys are Bitwig's again.";
        Save();
    }

    internal void Reset(ShortcutAction action) => Set(action.Id, action.Bitwig);

    /// <summary>A key written the one way this list writes it: modifiers as Ctrl, Shift, Alt in that order, then the key.</summary>
    public static string Normalise(string key)
    {
        if (key.Length == 0 || key == "?") return key;
        // "Ctrl++" means Ctrl and the plus key, which this list calls "=" (the same key unshifted)
        var parts = key.EndsWith("++") ? [.. key[..^2].Split('+', StringSplitOptions.RemoveEmptyEntries), "="] : key.Split('+', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0) return "";
        bool ctrl = false, shift = false, alt = false;
        foreach (var p in parts.Take(parts.Count - 1))
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control" or "cmd" or "meta": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt" or "option": alt = true; break;
            }
        var last = parts[^1];
        last = last.Length == 1 ? last.ToUpperInvariant() : char.ToUpperInvariant(last[0]) + last[1..];
        return (ctrl ? "Ctrl+" : "") + (shift ? "Shift+" : "") + (alt ? "Alt+" : "") + last;
    }

    /// <summary>Whether a key can fire while typing in a box: only ones held with Ctrl (Solomon's design, point 13).</summary>
    public static bool WorksWhileTyping(string key) => key.StartsWith("Ctrl+");
}

/// <summary>One action on the Settings › Shortcuts tab and the ? cheat sheet.</summary>
public sealed partial class ShortcutRow(ShortcutsViewModel owner, ShortcutAction action) : ObservableObject
{
    public ShortcutAction Action { get; } = action;
    public string Name => Action.Name;
    public string Page => Action.Page;
    public string Key => owner.KeyOf(Action.Id);
    /// <summary>The key as shown: "None" when it has none.</summary>
    public string KeyText => Key.Length == 0 ? "None" : ShortcutsViewModel.Display(Key);
    public bool IsChanged => Key != Action.Bitwig;
    public string BitwigText => Action.Bitwig.Length == 0 ? "Bitwig has no key for this" : $"Bitwig's key: {ShortcutsViewModel.Display(Action.Bitwig)}";
    /// <summary>The first row of a page, which shows the page's name above it.</summary>
    public bool StartsPage => owner.Rows.FirstOrDefault(r => r.Page == Page) == this;

    /// <summary>A key pressed while this row was listening for one.</summary>
    [RelayCommand] void Set(string? key) => owner.Set(Action.Id, key ?? "");
    [RelayCommand] void Clear() => owner.Set(Action.Id, "");
    [RelayCommand] void Reset() => owner.Reset(Action);

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Key));
        OnPropertyChanged(nameof(KeyText));
        OnPropertyChanged(nameof(IsChanged));
    }
}
