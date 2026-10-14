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
    public static string DawExeKey(string daw) => $"daw_exe:{daw}";

    readonly LibraryDb _db;
    readonly MainViewModel _main;

    public ProjectItem Item { get; }
    public ProjectDetail? Detail { get; }
    public IReadOnlyList<PluginRow> Plugins { get; }
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
    public bool CanExtractMidi => Detail?.CanExtractMidi == true && !Item.FileMissing;
    public bool CanExtractPresets => Detail?.CanExtractPresets == true && !Item.FileMissing;
    public string MidiNote => "See each channel's notes and what plays them, export them as MIDI, or send them to Bitwig" +
                              (Item.Daw == "Studio One" ? " (experimental for Studio One)" : "");
    public string PluginHeader => $"Plugins ({Plugins.Count}{(Item.MissingPlugins > 0 ? $", {Item.MissingPlugins} missing" : "")})";
    public string SampleHeader => $"Samples ({Samples.Count}{(Item.MissingSamples > 0 ? $", {Item.MissingSamples} missing" : "")})";

    [ObservableProperty] string _message = "";

    public ProjectDetailViewModel(LibraryDb db, ProjectItem item, MainViewModel main)
    {
        _db = db;
        _main = main;
        Item = item;
        Detail = db.Project(item.Id);
        var installed = db.InstalledNames();
        Plugins = Detail?.Plugins.Select(p => new PluginRow(db, p, installed)).ToList() ?? [];
        VisiblePlugins = Plugins;
        Samples = Detail?.Samples.Select(s => new SampleRow(db, s, item, this)).ToList() ?? [];

        var facts = new List<KeyValuePair<string, string>>
        {
            new("DAW", Detail?.Summary.Version is { Length: > 0 } v ? $"{item.Daw} ({v})" : item.Daw),
            new("Tempo", item.TempoText.Length > 0 ? $"{item.TempoText} BPM" : "Unknown"),
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
            foreach (var p in Plugins) p.Silent = p.IsInstrument && !players.Contains(p.Name);
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
    void OpenInDaw() => Open(Item.Id, Item.Path, Item.Daw);

    /// <summary>Open one of the song's older numbered saves.</summary>
    [RelayCommand]
    void OpenSave(ProjectSummary? save)
    {
        if (save is not null) Open(save.Id, save.Path, save.Daw);
    }

    [RelayCommand]
    void RevealSave(ProjectSummary? save)
    {
        if (save is not null) Shell.Reveal(save.Path);
    }

    void Open(long id, string path, string daw)
    {
        if (!File.Exists(path))
        {
            Message = "This project file isn't where it used to be. Rescan to update the library.";
            return;
        }
        if (_db.GetSetting(DawExeKey(daw)) is { Length: > 0 } exe && File.Exists(exe))
            Shell.OpenWith(path, exe);
        else
            Shell.Open(path);
        _db.MarkOpened(id);
        Message = $"Opening {Path.GetFileNameWithoutExtension(path)} in {daw}…";
    }

    [RelayCommand]
    void Reveal() => Shell.Reveal(Item.Path);

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
    /// Converts an FL Studio project into a native Bitwig project and opens it in Bitwig. The first time,
    /// it asks for the Bitwig project to build from (see <see cref="BitwigProject"/>).
    /// </summary>
    [RelayCommand]
    async Task OpenInBitwig()
    {
        var template = BitwigTemplateStore.Find(_db, _main.DataFolder);
        if (template is null)
        {
            (template, var problem) = await SettingsViewModel.PickBitwigTemplate(_db, _main.DataFolder);
            if (template is null)
            {
                Message = problem ?? "Converting needs a Bitwig template: a project saved in Bitwig with a Sampler track, a VST3 synth " +
                                     "with a Note Transpose before it and a clip of notes, and a VST2 synth. Pick it in Settings or try again.";
                return;
            }
            _main.Settings.TemplateChanged(template);
        }
        Message = "Converting for Bitwig…";
        try
        {
            var path = Item.Path;
            var untilNextNote = _db.GetSetting(StepsUntilNextNoteKey) == "1";
            var (file, leftOut) = await Task.Run(() => Exporter.BitwigFromFl(path, ExportRoot, File.ReadAllBytes(template), untilNextNote));
            OpenWithBitwig(_db, file);
            Message = $"Opening {Path.GetFileNameWithoutExtension(file)} in Bitwig." +
                      (leftOut.Count == 0 ? "" : $" Not carried over: {string.Join("; ", leftOut.Take(4))}" +
                                                 $"{(leftOut.Count > 4 ? $"; and {leftOut.Count - 4} more, listed in the notes saved next to it" : "")}.");
        }
        catch (InvalidDataException e) when (e.Message.StartsWith("The Bitwig template", StringComparison.Ordinal))
        {
            Message = $"{e.Message}. Pick another template in Settings.";
        }
        catch (Exception e) { Message = $"Converting for Bitwig failed: {e.Message}"; }
    }

    /// <summary>Opens a Bitwig project with the Bitwig chosen in Settings, or whatever opens .bwproject files.</summary>
    internal static void OpenWithBitwig(LibraryDb db, string file)
    {
        if (db.GetSetting(DawExeKey("Bitwig Studio")) is { Length: > 0 } exe && File.Exists(exe))
            Shell.OpenWith(file, exe);
        else
            Shell.Open(file);
    }

    /// <summary>Converts an FL Studio project to a DAWproject file Bitwig can open, and shows it.</summary>
    [RelayCommand]
    async Task ExportToBitwig()
    {
        Message = "Converting for Bitwig…";
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
        }
        catch (Exception e) { Message = $"Converting for Bitwig failed: {e.Message}"; }
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
        }
        catch (DawLinkException e)
        {
            if (!quiet) Message = e.Message;
        }
        catch (Exception e)
        {
            if (!quiet) Message = $"Checking with {daw} failed: {e.Message}";
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
        Message = $"Exporting {row.Name}…";
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
                return;
            }
            Message = PresetsViewModel.ExportSummary(result, blobs, templates);
            var best = result.Files.FirstOrDefault(f => f.EndsWith(".bwpreset", StringComparison.OrdinalIgnoreCase)) ?? result.Files[0];
            if (result.Files.Count <= 2) Shell.Reveal(Path.Combine(result.Folder, best));
            else Shell.Open(result.Folder);
        }
        catch (Exception e) { Message = $"Preset export failed: {e.Message}"; }
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
