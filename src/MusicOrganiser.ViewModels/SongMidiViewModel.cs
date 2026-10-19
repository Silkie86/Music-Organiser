using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// A song's "MIDI that fits" tab: the MIDI parts from your other projects and MIDI libraries that fit this song's
/// harmony best, each already moved into its key, to play or drag straight into the DAW (Solomon, 2026-10-15: "on
/// project tab add way to see recommended midi files"). It's worked out the first time the tab is opened, from the
/// same index and matching as the MIDI page.
/// </summary>
public sealed partial class SongMidiViewModel(MidiBrowserViewModel midi, MainViewModel main, string path, string name)
    : ObservableObject, IFileDragSource
{
    /// <summary>How many parts each kind shows, unless Settings › MIDI says otherwise.</summary>
    public const int Limit = 40;

    /// <summary>A fit this good or better counts towards the tab's "N good fits", unless Settings › MIDI says otherwise.</summary>
    public const double GoodFit = 0.7;

    public static IReadOnlyList<RoleChip> Kinds { get; } =
    [
        new("All", "All"), new("Chords", MidiRoles.Chords), new("Melodies", MidiRoles.Melody),
        new("Basslines", MidiRoles.Bass), new("Arps", MidiRoles.Arp),
    ];

    HarmonyTarget? _target;
    Task? _loading;
    bool _loaded;
    int _request;

    [ObservableProperty] IReadOnlyList<MidiRow> _rows = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection))] MidiRow? _selected;
    [ObservableProperty] RoleChip _kind = Kinds[0];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty))] bool _isLoading;
    /// <summary>What the song's harmony is ("Key A minor · Am F C G"), or why nothing can be shown.</summary>
    [ObservableProperty] string _status = "";
    /// <summary>For the song panel's counts line: "12 good fits", once worked out.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LinkText))] string _countText = "";
    /// <summary>The counts line's link: "MIDI that fits" until it's been worked out, then "12 good fits".</summary>
    public string LinkText => CountText.Length > 0 ? CountText : "MIDI that fits";
    [ObservableProperty] int _goodCount;

    public bool HasSelection => Selected is not null;
    public bool IsEmpty => !IsLoading && Rows.Count == 0;
    partial void OnRowsChanged(IReadOnlyList<MidiRow> value) => OnPropertyChanged(nameof(IsEmpty));

    /// <summary>Works it out, the first time the tab is opened; tried again next time when it couldn't be (no MIDI read
    /// yet, or something went wrong).</summary>
    public Task LoadAsync()
    {
        if (_loading is { IsCompleted: true } && !_loaded) _loading = null;
        return _loading ??= RunLoadAsync();
    }

    async Task RunLoadAsync()
    {
        IsLoading = true;
        Status = $"Listening to the harmony of {name}…";
        try
        {
            var (target, why) = await MidiBrowserViewModel.HarmonyOfAsync(path, name);
            if (target is null)
            {
                Status = why;
                _loaded = true;   // the song says nothing to fit to: no use reading it again each time
                return;
            }
            _target = _whole = target;
            SongParts = target.Parts.Select(c => new SongPart(c, PartsChanged)).ToList();
            if (await midi.CatalogueAsync() is not { Entries.Count: > 0 } catalogue)
            {
                Status = "No MIDI has been read yet. Open the MIDI page to read your projects and MIDI folders.";
                return;
            }
            var good = await UiWork.Run(() => catalogue.Search(new MidiQuery(Fit: target, Limit: 500, Leave: path, Notes: midi.NotesOf), out _)
                                                       .Count(h => h.Fit!.Score >= midi.GoodFitPercent / 100.0));
            GoodCount = good;
            CountText = good == 1 ? "1 good fit" : $"{good:N0} good fits";
            Status = Describe(target);
            await ShowAsync();
            _loaded = true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppLog.Info("MIDI", $"MIDI that fits {path}: {e}");
            Status = $"Couldn't work out what fits: {e.Message}";
        }
        finally { IsLoading = false; }
    }

    static string Describe(HarmonyTarget target) => string.Join(" · ", new[]
    {
        target.Key is { } k ? $"Key {k.Name}" : "",
        target.Chords.Count > 0 ? target.ChordText() : "",
    }.Where(s => s.Length > 0));

    async Task ShowAsync()
    {
        var request = ++_request;
        if (_target is not { } target || await midi.CatalogueAsync() is not { } catalogue) return;
        var query = new MidiQuery(Role: Kind.Role == "All" ? null : Kind.Role, Fit: target, Limit: midi.SongTabLimit, Leave: path, Notes: midi.NotesOf);
        var hits = await UiWork.Run(() => catalogue.Search(query, out _));
        if (request != _request) return;
        Rows = hits.Select(h => new MidiRow(h, midi, target)).ToList();
        Selected = Rows.FirstOrDefault();
    }

    partial void OnKindChanged(RoleChip value) => _ = ShowAsync();

    /// <summary>The same list on the MIDI page, with its search, filters and "What goes with this?".</summary>
    [RelayCommand]
    async Task OpenInMidiPage()
    {
        main.SelectedNav = main.Nav.First(n => n.Page == midi);
        await midi.FitToProjectAsync(path, name);
    }

    // ---- which of the song's parts to fit against ----

    HarmonyTarget? _whole;
    int _partsRequest;

    /// <summary>The song's pitched parts, each on or off: the list is fitted to, and plays with, just the ones on.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSongParts))] IReadOnlyList<SongPart> _songParts = [];
    public bool HasSongParts => SongParts.Count >= 2;

    /// <summary>Every part on again.</summary>
    [RelayCommand]
    void AllSongParts()
    {
        foreach (var p in SongParts) p.SetQuietly(true);
        PartsChanged();
    }

    // A few clicks on the chips in a row make one new fit, once they rest.
    void PartsChanged() => _ = RefitAsync();

    async Task RefitAsync()
    {
        var request = ++_partsRequest;
        if (UiWork.InBackground) await Task.Delay(300);
        if (request != _partsRequest || _whole is not { } whole) return;
        var on = SongParts.Where(p => p.IsOn).Select(p => p.Channel).ToList();
        if (on.Count == 0)
        {
            Status = "Pick at least one of the song's parts to fit to.";
            return;
        }
        var target = on.Count == SongParts.Count ? whole : await UiWork.Run(() => HarmonyTarget.FromChannels(name, on));
        if (request != _partsRequest) return;
        if (target is null)
        {
            Status = "Those parts have too few notes to fit MIDI to. Pick another as well.";
            return;
        }
        _target = target;
        Status = Describe(target);
        // The part open in the quick view stays open, fitted afresh.
        var open = Open?.Entry;
        await ShowAsync();
        if (open is not null && Rows.FirstOrDefault(r => MidiBrowserViewModel.Same(r.Entry, open)) is { } again) OpenPart(again);
    }

    // ---- the quick view ----

    /// <summary>
    /// The part opened beside the list with a click: a slim mini playground to try it with the song, edit it and drag it
    /// out, with the Playground for more (Solomon, 2026-10-19: "a slimmed down playground for quick testing").
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsOpen))] MidiRow? _open;
    public bool IsOpen => Open is not null;

    /// <summary>The MIDI page, for what the quick view shares with it (looping).</summary>
    public MidiBrowserViewModel Midi => midi;

    // While it's open, picking another part (a click, the arrow keys, another kind) opens that one.
    partial void OnSelectedChanged(MidiRow? value)
    {
        if (Open is not null && value is not null) Open = value;
    }

    [RelayCommand]
    void OpenPart(MidiRow? row)
    {
        if (row is null) return;
        Selected = row;
        Open = row;
    }

    [RelayCommand]
    void Close() => Open = null;

    public DragPayload? MakeDrag(IReadOnlyList<object> items) =>
        midi.MakeDrag(items.Count > 0 ? items : Selected is { } s ? [s] : []);
}

/// <summary>One of a song's parts on its "MIDI that fits" tab: on, to fit to and play with, or left out.</summary>
public sealed partial class SongPart(MidiChannel channel, Action changed) : ObservableObject
{
    bool _quiet;

    public MidiChannel Channel => channel;
    public string Name => channel.Name;
    public string Tip => $"{channel.Kind} · {channel.Notes.Count:N0} notes" + (channel.Plugin.Length > 0 ? $" · {channel.Plugin}" : "");

    [ObservableProperty] bool _isOn = true;

    partial void OnIsOnChanged(bool value)
    {
        if (!_quiet) changed();
    }

    internal void SetQuietly(bool on)
    {
        _quiet = true;
        IsOn = on;
        _quiet = false;
    }
}
