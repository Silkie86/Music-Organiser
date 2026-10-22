using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.ViewModels;

/// <summary>The right-hand panel for the selected project.</summary>
public sealed partial class ProjectDetailViewModel : ObservableObject
{
    public const string ExportFolderKey = "export_folder";
    /// <summary>The Bitwig project that FL Studio projects are converted with.</summary>
    public const string BitwigTemplateKey = "bitwig_template";
    /// <summary>"1" when FL's steps on a Sampler play until the next note or the clip end, not until their sample ends.</summary>
    public const string StepsUntilNextNoteKey = "bitwig_steps_until_next_note";
    /// <summary>"1" when converting swaps older versions of the template's synth (Serum) for the template's own (Serum 2).</summary>
    public const string UpgradeSynthsKey = "bitwig_upgrade_synths";
    public static string DawExeKey(string daw) => $"daw_exe:{daw}";

    readonly LibraryDb _db;
    readonly MainViewModel _main;

    public ProjectItem Item { get; }
    public ProjectDetail? Detail { get; }
    public IReadOnlyList<PluginRow> Plugins { get; }

    SongMidiViewModel? _songMidi;
    /// <summary>The "MIDI that fits" tab: worked out the first time it's opened (see <see cref="SongMidiViewModel"/>).</summary>
    public SongMidiViewModel SongMidi => _songMidi ??= new(_main.Midi, _main, Item.Path, Item.Name, Item.Tempo);
    /// <summary>The "MIDI that fits" tab's small playground stops when another page shows (<paramref name="closed"/>: another song is picked, so it goes).</summary>
    public void LeftSongMidi(bool closed) { if (closed) _songMidi?.Closed(); else _songMidi?.Left(); }
    /// <summary>The plugin list as shown: synths that play no notes in the song are hidden until asked for.</summary>
    [ObservableProperty] IReadOnlyList<PluginRow> _visiblePlugins = [];
    [ObservableProperty] bool _showSilent;
    [ObservableProperty] int _silentCount;
    public string SilentToggleText => ShowSilent ? "Hide unused synths" : $"Show {SilentCount} unused synth{(SilentCount == 1 ? "" : "s")}";
    public IReadOnlyList<SampleRow> Samples { get; }
    public IReadOnlyList<string> Tracks => Detail?.Tracks ?? [];
    public IReadOnlyList<KeyValuePair<string, string>> Facts { get; }
    public IReadOnlyList<string> Warnings => Detail?.Warnings ?? [];
    public bool HasWarnings => Warnings.Count > 0;
    public IReadOnlyList<ProjectSummary> OlderSaves => Item.OlderSaves;
    public bool HasOlderSaves => Item.OlderSaves.Count > 0;
    public string SavesText => HasOlderSaves ? $"Newest of {Item.SaveCount} saves: {Item.FileName}" : "";
    public string OlderSavesHeader => $"Older saves ({Item.OlderSaves.Count})";
    /// <summary>The song's working periods, newest first, each one's last save ready to open (the Open button's drop-down).</summary>
    public IReadOnlyList<RunChoice> RunChoices { get; }
    public bool HasRunChoices => RunChoices.Count > 1;
    public string OlderSavesTip => HasRunChoices
        ? $"Open an older save: its {RunChoices.Count} versions (saves more than {SongVersions.RunGapMonths} months apart), or any save"
        : "Open one of the older saves";
    /// <summary>"First made 2 Feb 2019 · saved 3 Aug 2024", or "Saved 3 Aug 2024" when it's all one day.</summary>
    public string DatesText => Item.Made.Date == Item.Modified.Date
        ? $"Saved {Item.Modified:d MMM yyyy}"
        : $"First made {Item.Made:d MMM yyyy} · saved {Item.Modified:d MMM yyyy}";
    public bool CanExtractMidi => Detail?.CanExtractMidi == true && !Item.FileMissing;
    public bool CanExtractPresets => Detail?.CanExtractPresets == true && !Item.FileMissing;
    public string MidiNote => "See each channel's notes and what plays them, export them as MIDI, or send them to Bitwig" +
                              (Item.Daw == "Studio One" ? " (experimental for Studio One)" : "");
    public string PluginHeader => $"Plugins ({Plugins.Count}{(Item.MissingPlugins > 0 ? $", {Item.MissingPlugins} missing" : "")})";
    public string SampleHeader => $"Samples ({Samples.Count}{(Item.MissingSamples > 0 ? $", {Item.MissingSamples} missing" : "")})";

    /// <summary>Overview's line of counts ("12 plugins · 3 missing"), each opening its own tab.</summary>
    public string PluginsCountText => CountOf(Plugins.Count, "plugin");
    public string MissingPluginsText => Item.MissingPlugins > 0 ? $"{Item.MissingPlugins} missing" : "";
    public string SamplesCountText => CountOf(Samples.Count, "sample");
    public string MissingSamplesText => Item.MissingSamples > 0 ? $"{Item.MissingSamples} missing" : "";
    public string SavesCountText => CountOf(Item.SaveCount, "save");
    public string TracksCountText => CountOf(Tracks.Count, IsFlStudio ? "channel" : "track");
    static string CountOf(int n, string thing) => n == 0 ? $"No {thing}s" : n == 1 ? $"1 {thing}" : $"{n:N0} {thing}s";

    /// <summary>The line above the name: tempo and when it was made and saved.</summary>
    public string HeaderFacts => string.Join(" · ", new[] { Item.TempoText.Length > 0 ? $"{Item.TempoText} BPM" : "", DatesText }.Where(t => t.Length > 0));

    /// <summary>How much of Overview's height the arrangement keeps when Fix's list sits under it (the divider between them).</summary>
    public const string ArrangementShareKey = "project_arrangement_share";
    public double ArrangementShare
    {
        get => double.TryParse(_db.GetSetting(ArrangementShareKey), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0.15, 0.9) : 0.55;
        set => _db.SetSetting(ArrangementShareKey, Math.Clamp(value, 0.15, 0.9).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    [ObservableProperty] string _message = "";

    public ProjectDetailViewModel(LibraryDb db, ProjectItem item, MainViewModel main)
    {
        _db = db;
        _main = main;
        Item = item;
        Detail = db.Project(item.Id);
        var installed = db.InstalledNames();
        _canSwapSerum = Detail is not null && ProjectFixer.CanSwapSerum(Detail, installed);
        _needsRepair = Detail is not null && ProjectFixer.NeedsRepair(Detail);
        Plugins = Detail?.Plugins.Select(p => new PluginRow(db, p, installed)).ToList() ?? [];
        VisiblePlugins = Plugins;
        Samples = Detail?.Samples.Select(s => new SampleRow(db, s, item, this)).ToList() ?? [];
        RunChoices = item.Runs.Reverse().Select((r, i) => new RunChoice(this, r, i == 0)).ToList();

        var facts = new List<KeyValuePair<string, string>>
        {
            new("DAW", Detail?.Summary.Version is { Length: > 0 } v ? $"{item.Daw} ({v})" : item.Daw),
            new("Tempo", item.TempoText.Length > 0 ? $"{item.TempoText} BPM" : "Unknown"),
            new("First made", item.Made.ToString("d MMM yyyy")),
            new("Modified", item.Modified.ToString("d MMM yyyy, HH:mm")),
            new("Last opened from here", item.LastOpened?.ToString("d MMM yyyy, HH:mm") ?? "Never"),
            new("Location", item.Folder),
        };
        if (Detail?.Title is { Length: > 0 } title) facts.Insert(0, new("Title", title));
        if (item.Hardware.Count > 0) facts.Add(new("Elektron boxes", string.Join(", ", item.Hardware)));
        foreach (var d in Detail?.Details ?? new Dictionary<string, string>()) facts.Add(d);
        if (item.Error is { } err) facts.Add(new("Couldn't read", err));
        Facts = facts;
        if (item.Daw == "FL Studio" && CanExtractMidi) _ = FindSilentSynthsAsync();
        FindSongs();
        LoadPendingFix();
    }

    /// <summary>
    /// FL projects often keep synths on channels that never play. Any instrument whose
    /// channel has no notes in the song is tucked away behind a toggle.
    /// </summary>
    async Task FindSilentSynthsAsync()
    {
        if (Readers.For(Item.Path) is not { } reader) return;
        try
        {
            var players = await Task.Run(() => MidiChannel.Players(reader.ExtractChannels(Item.Path)));
            if (players.Count == 0) return;
            foreach (var p in Plugins) p.Silent = p.IsInstrument && p.Installed && !players.Contains(p.Name);   // a missing one stays in sight
            SilentCount = Plugins.Count(p => p.Silent);
            ApplyPluginFilter();
        }
        catch (Exception) { /* the list just stays complete */ }
    }

    partial void OnShowSilentChanged(bool value) => ApplyPluginFilter();

    void ApplyPluginFilter()
    {
        VisiblePlugins = ShowSilent ? Plugins : Plugins.Where(p => !p.Silent).ToList();
        OnPropertyChanged(nameof(SilentToggleText));
    }

    [RelayCommand]
    void ToggleSilent() => ShowSilent = !ShowSilent;

    string ExportRoot => _db.GetSetting(ExportFolderKey) is { Length: > 0 } f
        ? f
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.Create), "Music Organiser Exports");

    [RelayCommand]
    Task OpenInDaw() => Open(Item.Id, Item.Path, Item.Daw);

    /// <summary>Open one of the song's older numbered saves.</summary>
    [RelayCommand]
    Task OpenSave(ProjectSummary? save) => save is null ? Task.CompletedTask : Open(save.Id, save.Path, save.Daw);

    /// <summary>Opens a save in its own DAW (FL Studio for an FL project).</summary>
    internal Task OpenInOwnDaw(ProjectSummary save) => Open(save.Id, save.Path, save.Daw);

    [RelayCommand]
    void RevealSave(ProjectSummary? save)
    {
        if (save is not null) Shell.Reveal(save.Path);
    }

    // ---- Which song the saves go under (Solomon, 19 Oct: "you should be able to change what list they go under") -------

    /// <summary>Every save of the song, newest first, for the Saves tab.</summary>
    public IReadOnlyList<ProjectSummary> AllSaves => [Item.Summary, .. Item.OlderSaves];
    public string AllSavesHeader => Item.SaveCount == 1 ? "1 save" : $"{Item.SaveCount} saves, newest first";

    /// <summary>Whether you changed how this song's saves are grouped, so Reset has something to undo.</summary>
    public bool HasSongChanges => _main.Projects.Choices.Changed(Item.SongKey, AllSaves.Select(s => s.Path));

    /// <summary>The save being moved to another song, or null to put the whole song under another.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveTitle), nameof(IsMovingSave))]
    ProjectSummary? _moving;
    public bool IsMovingSave => Moving is not null;
    public string MoveTitle => Moving is { } save ? $"Move {save.Name} to another song" : $"Put {Item.Name} under another song";

    /// <summary>What you typed to find the song to put this under.</summary>
    [ObservableProperty] string _songSearch = "";
    /// <summary>Songs to put this under: ones in the same folder and DAW first, then the rest whose name matches.</summary>
    [ObservableProperty] IReadOnlyList<ProjectItem> _songMatches = [];
    public bool HasSongMatches => SongMatches.Count > 0;
    partial void OnSongMatchesChanged(IReadOnlyList<ProjectItem> value) => OnPropertyChanged(nameof(HasSongMatches));
    partial void OnSongSearchChanged(string value) => FindSongs();

    /// <summary>Fills <see cref="SongMatches"/>: with nothing typed, the songs beside this one; else any song whose name has the words.</summary>
    public void FindSongs()
    {
        var words = SongSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Near(ProjectItem s) => s.Daw == Item.Daw && string.Equals(s.Folder, Item.Folder, StringComparison.OrdinalIgnoreCase);
        SongMatches = _main.Projects.Items
            .Where(s => s.SongKey != Item.SongKey && (words.Length == 0 ? Near(s) : words.All(w => s.Name.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(Near).ThenByDescending(s => s.Modified)
            .Take(8).ToList();
    }

    /// <summary>The row's Move button: pick the song to move this one save to.</summary>
    [RelayCommand]
    void MoveSave(ProjectSummary? save)
    {
        Moving = save;
        FindSongs();
    }

    [RelayCommand]
    void CancelMove() => Moving = null;

    /// <summary>Puts the song (or the save being moved) under <paramref name="target"/>; the list then opens that song.</summary>
    [RelayCommand]
    async Task JoinSong(ProjectItem? target)
    {
        if (target is null || Item.SongKey.Length == 0) return;
        var from = Item.SongKey;
        if (Moving is { } save)
        {
            _db.SplitSave(save.Path);
            from = SongChoices.SplitKey(save.Path);
        }
        if (!_db.JoinSong(from, target.SongKey))
        {
            Activities.Failed("Can't put it there", $"{target.Name} is already under {Item.Name}. Use Reset on {target.Name} first.");
            return;
        }
        Activities.Done(Moving is { } moved ? $"Moved {moved.Name}" : $"Put {Item.Name} under {target.Name}",
                        "Reset on the song's Saves tab puts it back.");
        await _main.Projects.ReloadAsync(target.Path);
    }

    /// <summary>Takes one save out of this song, into a song of its own.</summary>
    [RelayCommand]
    async Task SplitOff(ProjectSummary? save)
    {
        if (save is null || Item.SaveCount == 1) return;
        _db.SplitSave(save.Path);
        Activities.Done($"{save.Name} is its own song now", "Reset on the song's Saves tab puts it back.");
        await _main.Projects.ReloadAsync(save == Item.Summary ? Item.OlderSaves[0].Path : Item.Path);
    }

    /// <summary>Undoes your changes to this song: it goes back to the app's guess from the names and folder.</summary>
    [RelayCommand]
    async Task ResetSong()
    {
        _db.ForgetSongChoices(Item.SongKey, AllSaves.Select(s => s.Path));
        await _main.Projects.ReloadAsync(Item.Path);
    }

    /// <summary>The window's activity cards.</summary>
    internal ActivityFeed Activities => _main.Activities;

    async Task Open(long id, string path, string daw)
    {
        if (!await Task.Run(() => File.Exists(path)))
        {
            Message = "This project file isn't where it used to be. Rescan to update the library.";
            Activities.Failed("Can't find the project file", "It has moved or been deleted. Rescan to update the library.");
            return;
        }
        try { await OpenIn(_db, daw, path); }
        catch (Exception e)
        {
            Activities.Failed($"Couldn't open it in {daw}", e.Message);
            return;
        }
        _db.MarkOpened(id);
        Message = "";
        Activities.Done($"Opening {Path.GetFileNameWithoutExtension(path)}", $"Sent to {daw}. It opens there in a moment.");
    }

    [RelayCommand]
    void Reveal() => Shell.Reveal(Item.Path);

    static string Things(int n) => n == 1 ? "1 thing" : $"{n} things";

    [RelayCommand]
    void CopyPath() => Ui.Current.CopyText(Item.Path);

    /// <summary>Opens the MIDI popup: each channel's notes, what plays them, and export.</summary>
    [RelayCommand]
    void ShowMidi()
    {
        if (Readers.For(Item.Path) is not { } reader) return;
        Ui.Current.ShowMidi(new MidiPreviewViewModel(reader, Item.Path, Item.Name, ExportRoot, Item.Tempo, _db, _main.Live));
    }

    public bool IsFlStudio => Item.Daw == "FL Studio";

    /// <summary>
    /// The main Open button (and Enter or a double-click in the list): open the project in its DAW, except FL Studio projects,
    /// which go to Bitwig, since that's where Solomon takes them. Open in FL Studio sits beside it.
    /// </summary>
    [RelayCommand]
    Task OpenMain()
    {
        if (IsFlStudio) return OpenInBitwig();
        OpenInDaw();
        return Task.CompletedTask;
    }

    public string OpenMainText => IsFlStudio ? "Open in Bitwig" : $"Open in {Item.Daw}";
    public string OpenMainTip => IsFlStudio
        ? "Convert this FL Studio project into a Bitwig project and open it (Enter does the same in the list)"
        : "Open the newest save in its DAW (Enter does the same in the list)";

    /// <summary>Converts one of the song's older saves into a Bitwig project and opens it.</summary>
    [RelayCommand]
    Task OpenSaveInBitwig(ProjectSummary? save) => save is null ? Task.CompletedTask : ConvertForBitwig(save, save.Name);

    /// <summary>
    /// Converts an FL Studio project into a native Bitwig project and opens it in Bitwig. The first time,
    /// it asks for the Bitwig project to build from (see <see cref="BitwigProject"/>).
    /// </summary>
    [RelayCommand]
    Task OpenInBitwig() => ConvertForBitwig(Item.Summary, Item.Name);

    /// <summary>
    /// The names of the Bitwig copies this app has opened since Bitwig was last seen closed. Bitwig can have several
    /// projects open but only says which one is in front, and opening one it has open just brings that copy forward.
    /// </summary>
    static readonly HashSet<string> OpenedInBitwig = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Converts one save (the newest, or the last of an older run) and opens it in Bitwig; false when it couldn't.</summary>
    internal async Task<bool> ConvertForBitwig(ProjectSummary save, string name)
    {
        if (!File.Exists(save.Path))
        {
            Message = "This project file isn't where it used to be. Rescan to update the library.";
            Activities.Failed("Can't find the project file", "It has moved or been deleted. Rescan to update the library.");
            return false;
        }
        var template = BitwigTemplateStore.Find(_db, _main.DataFolder);
        if (template is null)
        {
            (template, var problem) = await SettingsViewModel.PickBitwigTemplate(_db, _main.DataFolder);
            if (template is null)
            {
                Message = problem ?? "Converting needs a Bitwig template: a project saved in Bitwig with a Sampler track, a VST3 synth " +
                                     "with a Note Transpose before it and a clip of notes, and a VST2 synth (and an audio track with an audio clip on it, " +
                                     "for FL Studio's audio clips). Pick it in Settings or try again.";
                return false;
            }
            _main.Settings.TemplateChanged(template);
        }
        Message = "";
        var card = Activities.Start($"Converting {name} for Bitwig");
        try
        {
            var path = save.Path;
            var untilNextNote = _db.GetSetting(StepsUntilNextNoteKey) == "1";
            var upgrade = _db.GetSetting(UpgradeSynthsKey) == "1";
            // A new convert saved over a copy Bitwig has open would look unchanged there, so it gets a fresh name.
            var bitwig = _main.Live.State(DawState.Bitwig);
            if (bitwig is { Running: false }) OpenedInBitwig.Clear();
            var saveAs = Exporter.BitwigName(path, n => OpenedInBitwig.Contains(n) ||
                                                        bitwig is { Answering: true } && bitwig.Project.Trim().Equals(n, StringComparison.OrdinalIgnoreCase));
            var (file, leftOut) = await Task.Run(() => Exporter.BitwigFromFl(path, ExportRoot, File.ReadAllBytes(template), untilNextNote, upgrade, saveAs));
            await OpenWithBitwig(_db, file);
            OpenedInBitwig.Add(saveAs);
            var renamed = saveAs != Exporter.BitwigName(path, _ => false)
                ? " It has a new name because Bitwig may still have an earlier convert open, and would show that one instead. " +
                  "Close the old one without saving."
                : "";
            Message = $"Opening {Path.GetFileNameWithoutExtension(file)} in Bitwig.{renamed}" +
                      (leftOut.Count == 0 ? "" : $" Not carried over: {string.Join("; ", leftOut.Take(4))}" +
                                                 $"{(leftOut.Count > 4 ? $"; and {leftOut.Count - 4} more, listed in the notes saved next to it" : "")}.");
            card.Finish($"Opening {Path.GetFileNameWithoutExtension(file)} in Bitwig",
                        leftOut.Count == 0 ? "Converted. Everything carried over." : $"Converted. {Things(leftOut.Count)} not carried over (see the project panel).",
                        "Show file", () => Shell.Reveal(file));
            return true;
        }
        catch (InvalidDataException e) when (e.Message.StartsWith("The Bitwig template", StringComparison.Ordinal))
        {
            Message = $"{e.Message}. Pick another template in Settings.";
            card.Fail("Converting for Bitwig failed", "The Bitwig template needs changing (see the project panel).");
            return false;
        }
        catch (Exception e)
        {
            Message = $"Converting for Bitwig failed: {e.Message}";
            card.Fail("Converting for Bitwig failed", e.Message);
            return false;
        }
    }

    /// <summary>Opens a Bitwig project with the Bitwig chosen in Settings, or whatever opens .bwproject files.</summary>
    internal static Task OpenWithBitwig(LibraryDb db, string file) => OpenIn(db, "Bitwig Studio", file);

    /// <summary>
    /// Opens a project with the DAW chosen in Settings for it, or whatever opens its kind of file, off the window's thread:
    /// handing a project to a DAW that's already running can keep Windows busy for seconds (a 2.3 s freeze on Projects
    /// as FL Studio was sent a fixed copy).
    /// </summary>
    internal static Task OpenIn(LibraryDb db, string daw, string file)
    {
        var exe = db.GetSetting(DawExeKey(daw));
        return Task.Run(() =>
        {
            if (exe is { Length: > 0 } && File.Exists(exe)) Shell.OpenWith(file, exe);
            else Shell.Open(file);
        });
    }

    /// <summary>Converts an FL Studio project to a DAWproject file Bitwig can open, and shows it.</summary>
    [RelayCommand]
    async Task ExportToBitwig()
    {
        Message = "";
        var card = Activities.Start($"Saving {Item.Name} as DAWproject");
        try
        {
            var path = Item.Path;
            var (file, leftOut, setup) = await Task.Run(() => Exporter.DawProjectFromFl(path, ExportRoot));
            Message = $"Saved {Path.GetFileName(file)}. In Bitwig, use File > Open and pick it." +
                      (setup.Count switch
                      {
                          0 => "",
                          1 => " One track needs its sample or root key set by hand; the steps are in the setup notes saved next to it.",
                          var n => $" {n} tracks need their sample or root key set by hand; the steps are in the setup notes saved next to it.",
                      }) +
                      (leftOut.Count == 0 ? "" : $" Not carried over yet: {string.Join("; ", leftOut.Take(4))}{(leftOut.Count > 4 ? $"; and {leftOut.Count - 4} more" : "")}.");
            Shell.Reveal(file);
            card.Finish($"Saved {Path.GetFileName(file)}", "In Bitwig, use File > Open and pick it.", "Show file", () => Shell.Reveal(file));
        }
        catch (Exception e)
        {
            Message = $"Converting for Bitwig failed: {e.Message}";
            card.Fail("Converting for Bitwig failed", e.Message);
        }
    }

    public bool IsBitwig => Item.Daw == "Bitwig Studio";

    /// <summary>Bitwig and FL Studio projects can be checked against what the DAW itself shows, through its script.</summary>
    public bool CanCheckWithDaw => IsBitwig || IsFlStudio;
    public string CheckWithDawText => IsFlStudio ? "Check with FL Studio" : "Check with Bitwig";

    /// <summary>The last check with the DAW, line by line: the same in both, different, or a note.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCheckLines))] IReadOnlyList<CheckLine> _checkLines = [];
    public bool HasCheckLines => CheckLines.Count > 0;

    /// <summary>"Open in Bitwig now" while the project's DAW has it open, or that it has an older save of it open; else empty.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OlderSaveOpen))] string _liveText = "";
    /// <summary>Whether the project's DAW has this project open now.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OlderSaveOpen))] bool _isLive;
    /// <summary>Whether <see cref="LiveText"/> says the DAW has an older save of it open instead.</summary>
    public bool OlderSaveOpen => !IsLive && LiveText.Length > 0;
    bool _checkedLive;

    /// <summary>
    /// Whether the project's DAW has it open now (<paramref name="open"/>), in words. The first time it does, the page
    /// checks it there by itself, quietly: only the result shows.
    /// </summary>
    public void ShowLive(string text, bool open)
    {
        LiveText = text;
        IsLive = open;
        if (!open || _checkedLive || !CanCheckWithDaw || Item.FileMissing) return;
        _checkedLive = true;
        _ = CheckAsync(quiet: true);
    }

    /// <summary>
    /// Asks the DAW what it shows for this project (through the Music Organiser script installed in it) and
    /// compares that with what the app read from the file, to catch anything the app reads wrong.
    /// </summary>
    [RelayCommand]
    Task CheckWithDaw() => CheckAsync(quiet: false);

    async Task CheckAsync(bool quiet)
    {
        var daw = IsFlStudio ? "FL Studio" : "Bitwig";
        var card = quiet ? null : Activities.Start($"Checking with {daw}", "Comparing what the app read with what the DAW shows");
        if (!quiet)
        {
            CheckLines = [];
            Message = $"Asking {daw}…";
        }
        try
        {
            var plugins = Plugins.Select(p => p.Name).ToList();
            IReadOnlyList<CheckLine> lines;
            if (IsFlStudio)
            {
                lines = FlCheck.Compare(Item.Path, Item.Tempo, Tracks, plugins, await _main.Live.Watch.Fl().ProjectAsync());
            }
            else
            {
                using var link = await _main.Live.Watch.ConnectBitwigAsync();
                lines = BitwigCheck.Compare(Item.Path, Item.Tempo, Tracks, plugins, await link.ProjectAsync());
            }
            CheckLines = quiet ? [new(CheckResult.Note, $"{daw} has this project open, so the app checked it there by itself."), .. lines] : lines;
            if (!quiet) Message = "";
            var differ = lines.Count(l => l.Result == CheckResult.Different);
            card?.Finish($"Checked with {daw}", differ == 0 ? "Everything matches" : $"{Things(differ)} differ (listed in the project panel)");
        }
        catch (DawLinkException e)
        {
            if (!quiet) Message = e.Message;
            card?.Fail($"Couldn't check with {daw}", e.Message);
        }
        catch (Exception e)
        {
            if (!quiet) Message = $"Checking with {daw} failed: {e.Message}";
            card?.Fail($"Checking with {daw} failed", e.Message);
        }
    }

    /// <summary>Opens the presets popup: every plugin state, with filters, ready to export.</summary>
    [RelayCommand]
    void ShowPresets()
    {
        if (Readers.For(Item.Path) is not { } reader) return;
        Ui.Current.ShowPresets(new PresetsViewModel(reader, Item.Path, Item.Name, ExportRoot));
    }

    /// <summary>Exports the saved state of one plugin (every instance of it) from this project.</summary>
    [RelayCommand]
    async Task ExportPluginPreset(PluginRow? row)
    {
        if (row is null || Readers.For(Item.Path) is not { } reader) return;
        Message = "";
        var card = Activities.Start($"Exporting {row.Name}");
        try
        {
            var path = Item.Path;
            var (result, blobs, templates) = await Task.Run(() =>
            {
                var b = reader.ExtractPresets(path).Where(p => p.Plugin.Equals(row.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                var t = PresetsViewModel.BitwigTemplates();
                return (Exporter.Presets(b, path, ExportRoot, t), b, t);
            });
            if (result.Files.Count == 0)
            {
                Message = $"This project has no saved state for {row.Name}.";
                card.Fail($"Nothing to export for {row.Name}", "This project has no saved state for it.");
                return;
            }
            Message = PresetsViewModel.ExportSummary(result, blobs, templates);
            var best = result.Files.FirstOrDefault(f => f.EndsWith(".bwpreset", StringComparison.OrdinalIgnoreCase)) ?? result.Files[0];
            if (result.Files.Count <= 2) Shell.Reveal(Path.Combine(result.Folder, best));
            else Shell.Open(result.Folder);
            card.Finish($"Exported {row.Name}", result.Files.Count == 1 ? "1 file" : $"{result.Files.Count} files",
                        "Show", () => Shell.Open(result.Folder));
        }
        catch (Exception e)
        {
            Message = $"Preset export failed: {e.Message}";
            card.Fail("Preset export failed", e.Message);
        }
    }

    [RelayCommand]
    void ShowProjectsUsingPlugin(PluginRow? row)
    {
        if (row is null) return;
        _main.Projects.ShowOnly(_db.ProjectsUsingPlugin(row.Name), $"Projects using {row.Name}");
    }

    /// <summary>Re-read just this project, e.g. after recovering a sample.</summary>
    public void Reindex()
    {
        _db.Upsert(Scanner.Analyse(Item.Path));
        _main.RefreshAll();
    }
}


public sealed partial class PluginRow(LibraryDb db, ProjectPlugin p, IReadOnlySet<string> installed) : ObservableObject
{
    public string Name => p.Name;
    public string Vendor => p.Vendor;
    public string Format => p.Format;
    public string KindText => p.Kind switch
    {
        PluginKind.Instrument => "Instrument",
        PluginKind.Effect => "Effect",
        PluginKind.NoteEffect => "Note FX",
        _ => "",
    };
    public bool Installed => p.Installed;
    public bool IsInstrument => p.Kind == PluginKind.Instrument;
    /// <summary>A synth whose channel plays no notes in the song.</summary>
    [ObservableProperty] bool _silent;
    /// <summary>Another version you do have, e.g. "Ozone 12" for a project that used Ozone 8.</summary>
    public string? OtherVersion { get; } = p.Installed ? null : PluginCatalog.OtherVersionInstalled(p.Name, installed);
    public string? Elektron => PluginCatalog.ElektronDevice(p.Name);

    public ObservableCollection<MatchRow> Matches { get; } = new();
    [ObservableProperty] string _matchMessage = "";

    [RelayCommand]
    void FindSimilar()
    {
        Matches.Clear();
        foreach (var m in MatchFinder.ForPlugin(db, Name)) Matches.Add(new MatchRow(m, null));
        MatchMessage = Matches.Count == 0 ? "No similar plugins installed." : "";
    }

    [RelayCommand]
    void SearchOnline() => Shell.OpenUrl(MatchFinder.PluginSearchUrl(Name, Vendor));
}

public sealed partial class SampleRow(LibraryDb db, ProjectSample s, ProjectItem project, ProjectDetailViewModel owner) : ObservableObject
{
    public string Path => s.Path;
    public string FileName => s.Path.Split('\\', '/')[^1];
    public bool Found => s.Found;

    public ObservableCollection<MatchRow> Matches { get; } = new();
    [ObservableProperty] string _matchMessage = "";

    [RelayCommand]
    void FindMatches()
    {
        Matches.Clear();
        if (db.SampleIndexCount() == 0)
        {
            MatchMessage = "Add your sample folders in Settings and index them to search your drives.";
            return;
        }
        foreach (var m in MatchFinder.ForSample(db, s.Path)) Matches.Add(new MatchRow(m, this));
        MatchMessage = Matches.Count == 0 ? "No similar files in your sample folders." : "";
    }

    [RelayCommand]
    void SearchOnline() => Shell.OpenUrl(MatchFinder.SampleSearchUrl(s.Path));

    [RelayCommand]
    void Reveal() => Shell.Reveal(s.Path);

    /// <summary>
    /// Copies a found file into the project's folder under the name the project expects,
    /// which is where the DAWs look first when a sample has moved.
    /// </summary>
    public async Task Recover(string candidate)
    {
        var target = System.IO.Path.Combine(project.Folder, FileName);
        if (File.Exists(target))
        {
            MatchMessage = "A file with that name is already in the project folder.";
            return;
        }
        if (!await Ui.Current.Confirm($"Copy\n{candidate}\n\ninto the project folder as\n{FileName}?", "Recover sample")) return;
        File.Copy(candidate, target);
        MatchMessage = "Copied. Your DAW will find it next time the project opens.";
        owner.Activities.Done($"Copied {FileName} into the project folder", "Your DAW will find it next time the project opens.");
        owner.Reindex();
    }
}

public sealed partial class MatchRow(Match m, SampleRow? sample) : ObservableObject
{
    public string Candidate => m.Candidate;
    public string Reason => m.Reason;
    public string ScoreText => $"{m.Score:P0}";
    public bool CanRecover => sample is not null;

    [RelayCommand]
    void Reveal()
    {
        if (sample is not null) Shell.Reveal(m.Candidate);
    }

    [RelayCommand]
    Task Recover() => sample?.Recover(m.Candidate) ?? Task.CompletedTask;
}

/// <summary>
/// One line of the Open button's drop-down (and the list's right-click menu): a working period on the song, with its
/// last save to open in its DAW or, for FL Studio, to send to Bitwig.
/// </summary>
public sealed class RunChoice(ProjectDetailViewModel owner, SaveRun run, bool newest)
{
    public SaveRun Run => run;
    public bool IsNewest => newest;
    /// <summary>"2024 rework · Didums_77 · 3 Aug 2024 · 12 saves".</summary>
    public string Header => $"{run.Label}{(newest ? " (newest)" : "")} · {run.Details}";
    public bool IsFlStudio => run.Last.Daw == "FL Studio";
    public string OpenInDawText => $"Open in {run.Last.Daw}";

    public IRelayCommand OpenInDawCommand { get; } = new AsyncRelayCommand(() => owner.OpenInOwnDaw(run.Last));
    public IAsyncRelayCommand OpenInBitwigCommand { get; } = new AsyncRelayCommand(() => owner.ConvertForBitwig(run.Last, run.Last.Name));
    public IRelayCommand RevealCommand { get; } = new RelayCommand(() => Shell.Reveal(run.Last.Path));
}
