using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// Something a key does, on one page (or <see cref="ShortcutsViewModel.Everywhere"/>): <see cref="Default"/> is the key it
/// starts with ("" for none), and <see cref="From"/> where that key comes from ("Bitwig", "Windows" and so on), so it
/// feels like the apps it's used beside.
/// </summary>
public sealed record ShortcutAction(string Id, string Page, string Name, string Default, string From = "Bitwig");

/// <summary>
/// One list of keyboard shortcuts for the whole app (Solomon, 2026-10-19 and 2026-10-21): every key can be changed in
/// Settings › Shortcuts, and ? or F1 on a page shows its keys. The window asks <see cref="Find"/> what a key press means:
/// the page's own key first, then the ones that work everywhere, so a page can give a key a job of its own (Space plays
/// the Playground's idea, and the picked sound in Sound Lab).
/// </summary>
/// <remarks>
/// A key is written as its modifiers in the order Ctrl, Shift, Alt, then the key, joined with "+" ("Ctrl+D",
/// "Shift+Alt+=", "Space", "Delete"); "?" stands on its own. Only keys changed from the defaults are saved. Ctrl is Cmd on a
/// Mac (see <see cref="Display"/>).
/// </remarks>
public sealed partial class ShortcutsViewModel : ObservableObject
{
    public const string SettingKey = "shortcuts";
    public const string Everywhere = "Everywhere";
    public const string Projects = "Projects";
    public const string Sounds = "Sound Lab";
    public const string Midi = "MIDI";
    public const string Playground = "Playground";
    public const string Plugins = "Plugins";

    /// <summary>Keys the app keeps for itself: F12 writes a note for Claude, Tab moves between the boxes and buttons.</summary>
    public static IReadOnlyList<string> Kept { get; } = ["F12", "Tab", "Shift+Tab"];

    /// <summary>
    /// Every action, with the key it starts with on Windows. The Playground's are Bitwig's (its user guide and shortcut
    /// lists for Bitwig 5), and the rest follow Windows and the browsers where they have a key for the job: Ctrl+F finds,
    /// F5 refreshes, F11 is full screen, Alt+Left goes back, Ctrl+Page Down moves to the next tab. The same idea has the
    /// same key on every page: Ctrl+B is Bitwig, Ctrl+Shift+E shows the file in Explorer, Ctrl+Shift+C copies its path.
    /// </summary>
    public static IReadOnlyList<ShortcutAction> Actions { get; } =
    [
        new("app.help", Everywhere, "Show the keys for this page", "?", "the Playground"),
        new("app.helpF1", Everywhere, "Show the keys for this page (the help key)", "F1", "Windows"),
        new("app.goProjects", Everywhere, "Go to Projects", "Ctrl+1", "the browsers' tab keys"),
        new("app.goSounds", Everywhere, "Go to Sound Lab", "Ctrl+2", "the browsers' tab keys"),
        new("app.goMidi", Everywhere, "Go to MIDI", "Ctrl+3", "the browsers' tab keys"),
        new("app.goPlayground", Everywhere, "Go to the Playground", "Ctrl+4", "the browsers' tab keys"),
        new("app.goPlugins", Everywhere, "Go to Plugins", "Ctrl+5", "the browsers' tab keys"),
        new("app.goSettings", Everywhere, "Go to Settings", "Ctrl+,", "Mac and Visual Studio Code"),
        new("app.prevPage", Everywhere, "The page above in the sidebar", "Ctrl+PageUp", "the browsers"),
        new("app.nextPage", Everywhere, "The page below in the sidebar", "Ctrl+PageDown", "the browsers"),
        new("app.back", Everywhere, "Back to the page you were on", "Alt+Left", "the browsers"),
        new("app.forward", Everywhere, "Forward again", "Alt+Right", "the browsers"),
        new("app.search", Everywhere, "Search this page", "Ctrl+F", "Windows"),
        new("app.prevItem", Everywhere, "The song, sound, part or plugin above in the list (even while typing a search)", "Ctrl+Up", "Music Organiser"),
        new("app.nextItem", Everywhere, "The song, sound, part or plugin below in the list (even while typing a search)", "Ctrl+Down", "Music Organiser"),
        new("app.prevTab", Everywhere, "The tab to the left on this page", "Ctrl+Left", "Music Organiser"),
        new("app.nextTab", Everywhere, "The tab to the right on this page", "Ctrl+Right", "Music Organiser"),
        new("app.play", Everywhere, "Play or stop the picked sound or MIDI part", "Space", "Bitwig's browser"),
        new("app.rescan", Everywhere, "Look for new and changed projects", "F5", "Windows' refresh key"),
        new("app.fullScreen", Everywhere, "Full screen on or off", "F11", "Windows"),

        new("projects.open", Projects, "Open the picked song (FL Studio songs go to Bitwig)", "Ctrl+O", "Windows"),
        new("projects.openInBitwig", Projects, "Open the picked song in Bitwig", "Ctrl+B", "Music Organiser"),
        new("projects.fix", Projects, "Fix the picked song's missing samples and plugins", "Ctrl+Shift+F", "Music Organiser"),
        new("projects.midi", Projects, "Show the picked song's MIDI", "Ctrl+Shift+M", "Music Organiser"),
        new("projects.reveal", Projects, "Show the song's file in Explorer", "Ctrl+Shift+E", "Visual Studio Code"),
        new("projects.copyPath", Projects, "Copy where the song's file is", "Ctrl+Shift+C", "Windows Explorer"),

        new("sounds.stop", Sounds, "Stop the sound playing", "Esc", "Music Organiser"),
        new("sounds.favourite", Sounds, "Favourite the picked sound, or not", "Ctrl+D", "the browsers' bookmark key"),
        new("sounds.tag", Sounds, "Type a tag for the picked sound", "Ctrl+T", "Music Organiser"),
        new("sounds.toBitwig", Sounds, "Put the picked sound into Bitwig", "Ctrl+B", "Music Organiser"),
        new("sounds.reveal", Sounds, "Show the sound's file in Explorer", "Ctrl+Shift+E", "Visual Studio Code"),
        new("sounds.copyPath", Sounds, "Copy where the sound's file is", "Ctrl+Shift+C", "Windows Explorer"),

        new("midi.playTogether", Midi, "Play the picked part on top of what it's fitted to", "Shift+Space", "Music Organiser"),
        new("midi.fit", Midi, "What goes with the picked part?", "Ctrl+G", "Music Organiser"),
        new("midi.moreLike", Midi, "More parts like the picked one", "Ctrl+L", "Music Organiser"),
        new("midi.toPlayground", Midi, "Send the picked part to the Playground", "Ctrl+P", "Music Organiser"),
        new("midi.toBitwig", Midi, "Send the picked part to Bitwig", "Ctrl+B", "Music Organiser"),
        new("midi.save", Midi, "Save the picked part as a MIDI file", "Ctrl+S", "Windows"),
        new("midi.octaveUp", Midi, "Picked part up an octave", "Shift+Alt+=", "the Playground"),
        new("midi.octaveDown", Midi, "Picked part down an octave", "Shift+Alt+-", "the Playground"),
        new("midi.source", Midi, "Show the song or file the part comes from", "Ctrl+Shift+E", "Visual Studio Code"),

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

        new("plugins.projects", Plugins, "Show the songs that use the picked plugin", "Ctrl+O", "Music Organiser"),
        new("plugins.searchOnline", Plugins, "Search the web for the picked plugin", "Ctrl+Shift+O", "Music Organiser"),
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
        _changed.TryGetValue(id, out var k) ? k : Actions.FirstOrDefault(a => a.Id == id)?.Default ?? "";

    /// <summary>
    /// The action <paramref name="key"/> does on <paramref name="page"/>: the page's own first, then one that works
    /// <see cref="Everywhere"/>; or null.
    /// </summary>
    public string? Find(string page, string key)
    {
        if (key.Length == 0) return null;
        key = Normalise(key);
        string? Look(string on, string k) => Actions.FirstOrDefault(a => a.Page == on && KeyOf(a.Id) == k)?.Id;
        // ? where the keyboard doesn't say what it types, and Ctrl and the + key with Shift held (Bitwig's "Ctrl++")
        string? On(string on) => Look(on, key) ?? key switch
        {
            "Shift+/" => Look(on, "?"),
            "Ctrl+Shift+=" => Look(on, "Ctrl+="),
            _ => null,
        };
        return On(page) ?? (page == Everywhere ? null : On(Everywhere));
    }

    /// <summary>Whether <paramref name="id"/> is one of the keys that work on every page.</summary>
    public static bool IsEverywhere(string id) => id.StartsWith("app.");

    /// <summary>A key as shown: Cmd for Ctrl and Option for Alt on a Mac, where they do those keys' jobs.</summary>
    public static string Display(string key) => OperatingSystem.IsMacOS() ? key.Replace("Ctrl+", "Cmd+").Replace("Alt+", "Option+") : key;

    /// <summary>The shortcuts on <paramref name="page"/> that have a key.</summary>
    public IReadOnlyList<ShortcutRow> For(string page) => Rows.Where(r => r.Action.Page == page && r.Key.Length > 0).ToList();

    // ---- the ? sheet ----

    /// <summary>The keys sheet over the window (? or F1), for the page it was opened on.</summary>
    [ObservableProperty] bool _sheetOpen;
    [ObservableProperty] string _sheetPage = Everywhere;
    [ObservableProperty] IReadOnlyList<ShortcutRow> _sheetRows = [];
    [ObservableProperty] IReadOnlyList<ShortcutRow> _sheetEverywhere = [];

    public string SheetTitle => SheetPage == Everywhere ? "Keys" : $"{SheetPage} keys";
    public bool SheetHasPage => SheetRows.Count > 0;

    /// <summary>Opens the keys sheet on <paramref name="page"/>: its own keys, then the ones that work everywhere (less any the page gives a job of its own).</summary>
    public void OpenSheet(string page)
    {
        SheetPage = page;
        SheetRows = page == Everywhere ? [] : For(page);
        var own = SheetRows.Select(r => r.Key).ToHashSet();
        SheetEverywhere = For(Everywhere).Where(r => !own.Contains(r.Key)).ToList();
        OnPropertyChanged(nameof(SheetTitle));
        OnPropertyChanged(nameof(SheetHasPage));
        SheetOpen = true;
    }

    [RelayCommand] void CloseSheet() => SheetOpen = false;

    /// <summary>
    /// Gives <paramref name="id"/> the key <paramref name="key"/> ("" for none). Another action on the same page that had
    /// it is left with no key, and <see cref="Message"/> warns about it, so one key never does two things on a page. A
    /// page's key may also be one that works everywhere: the page's job wins there, and the message says so.
    /// </summary>
    public void Set(string id, string key)
    {
        if (Actions.FirstOrDefault(a => a.Id == id) is not { } action) return;
        key = Normalise(key);
        Message = "";
        if (Kept.Contains(key))
        {
            Message = key == "F12" ? "F12 is kept for writing a note to Claude." : $"{key} is kept for moving between boxes and buttons.";
            return;
        }
        if (key.Length > 0 && Actions.FirstOrDefault(a => a.Id != id && a.Page == action.Page && KeyOf(a.Id) == key) is { } taken)
        {
            Store(taken, "");
            Message = $"{Display(key)} was \"{taken.Name}\", which has no key now. \"Default\" puts it back.";
        }
        else if (key.Length > 0 && action.Page != Everywhere && Actions.FirstOrDefault(a => a.Page == Everywhere && KeyOf(a.Id) == key) is { } wide)
            Message = $"{Display(key)} is also \"{wide.Name}\" everywhere else; on {action.Page} it does this instead.";
        else if (key.Length > 0 && action.Page == Everywhere && Actions.Where(a => a.Page != Everywhere && KeyOf(a.Id) == key).Select(a => a.Page).Distinct().ToList() is { Count: > 0 } pages)
            Message = $"{string.Join(" and ", pages)} keep{(pages.Count == 1 ? "s" : "")} {Display(key)} for a job of {(pages.Count == 1 ? "its" : "their")} own.";
        Store(action, key);
        Save();
    }

    void Store(ShortcutAction action, string key)
    {
        if (key == action.Default) _changed.Remove(action.Id);
        else _changed[action.Id] = key;
        Rows.FirstOrDefault(r => r.Action == action)?.Refresh();
    }

    void Save()
    {
        _db?.SetSetting(SettingKey, JsonSerializer.Serialize(_changed));
        OnPropertyChanged(nameof(AnyChanged));
    }

    public bool AnyChanged => _changed.Count > 0;

    /// <summary>Every key back to the one it started with.</summary>
    [RelayCommand]
    public void ResetAll()
    {
        _changed.Clear();
        foreach (var r in Rows) r.Refresh();
        Message = "All keys are back to the ones they started with.";
        Save();
    }

    internal void Reset(ShortcutAction action) => Set(action.Id, action.Default);

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

    /// <summary>
    /// Whether a key can fire while typing in a box: only ones held with Ctrl (Solomon's design, point 13), and Alt with
    /// an arrow (back and forward), which a box has no use for, so they work from the Projects search box too. Not on a
    /// Mac, where Option and an arrow move along the text a word at a time.
    /// </summary>
    public static bool WorksWhileTyping(string key) =>
        key.StartsWith("Ctrl+") || !OperatingSystem.IsMacOS() && key is "Alt+Left" or "Alt+Right" or "Alt+Up" or "Alt+Down";
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
    public bool IsChanged => Key != Action.Default;
    /// <summary>The key it starts with, and where that comes from.</summary>
    public string DefaultText => Action.Default.Length == 0 ? $"No key to start with ({Action.From} has none)"
        : $"Starts as {ShortcutsViewModel.Display(Action.Default)}, as in {Action.From}";
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
