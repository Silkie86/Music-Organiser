using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Plugins;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// The Plugins page answers "what should I install to open more of my old projects?": the missing plugins that would
/// open the most projects come first, then every plugin your projects use, with the projects using the picked one.
/// </summary>
public sealed partial class PluginsViewModel : ObservableObject
{
    readonly LibraryDb _db;
    readonly MainViewModel _main;

    public const string All = "All", Missing = "Missing", Installed_ = "Installed", Instruments = "Instruments", Effects = "Effects";
    /// <summary>The chips over the list, like the ones on Projects and Sounds.</summary>
    public static IReadOnlyList<string> FilterChoices { get; } = [All, Missing, Installed_, Instruments, Effects, "VST2", "VST3", "CLAP", "Native"];
    /// <summary>How many missing plugins the "install these" cards show (Settings › Plugins).</summary>
    [ObservableProperty] int _neededCount;
    public const string NeededCountKey = "plugins_needed_cards";
    public const int DefaultNeededCount = 6;
    public static IReadOnlyList<int> NeededCountChoices { get; } = [3, 6, 9, 12];

    [ObservableProperty] IReadOnlyList<PluginUsage> _items = [];
    [ObservableProperty] IItemsView? _view;
    [ObservableProperty] string _searchText = "";
    [ObservableProperty] string _filter = All;
    [ObservableProperty] PluginUsage? _selected;
    [ObservableProperty] string _summary = "";

    /// <summary>The missing plugins worth installing, the ones that open the most projects first.</summary>
    [ObservableProperty] IReadOnlyList<NeededPlugin> _needed = [];
    /// <summary>"Install these 6 and 23 projects open with nothing missing."</summary>
    [ObservableProperty] string _neededSummary = "";
    public bool HasNeeded => Needed.Count > 0;
    public bool AllInstalled => Items.Count > 0 && Needed.Count == 0;

    /// <summary>
    /// The songs using the picked plugin, newest first: one row per song with its newest save that uses it, or every save
    /// with <see cref="EverySave"/>. Only <see cref="ProjectsDaw"/>'s, unless that's <see cref="ProjectsViewModel.Any"/>.
    /// </summary>
    [ObservableProperty] IReadOnlyList<SongUse> _projects = [];
    [ObservableProperty] string _projectsTitle = "";
    /// <summary>"1949 saves. Each song shows its newest save that uses it."</summary>
    [ObservableProperty] string _projectsNote = "";
    [ObservableProperty] SongUse? _pickedProject;
    [ObservableProperty] bool _everySave;
    /// <summary>The DAW chips over the side panel's list: Any, then each DAW the plugin was used in.</summary>
    public ObservableCollection<string> ProjectDaws { get; } = new();
    [ObservableProperty] string _projectsDaw = ProjectsViewModel.Any;
    public bool HasProjectDaws => ProjectDaws.Count > 2;
    IReadOnlyList<PluginProject> _saves = [];
    /// <summary>"VST3 · Instrument · Xfer Records · last used March 2024".</summary>
    [ObservableProperty] string _selectedDetails = "";

    /// <summary>The installed plugins the selected one matches, as the plugin scanner read them.</summary>
    [ObservableProperty] IReadOnlyList<InstalledPluginItem> _installed = [];
    [ObservableProperty] string _installedNote = "";
    /// <summary>The installed copy whose parameters are showing.</summary>
    [ObservableProperty] InstalledPluginItem? _shownCopy;
    /// <summary>What the plugin scanner has read so far, or why it can't.</summary>
    [ObservableProperty] string _scannerNote = "";
    /// <summary>Some installed plugins' parameters are still to read (Read all parameters).</summary>
    [ObservableProperty] bool _canReadAll;

    public PluginsViewModel(LibraryDb db, MainViewModel main)
    {
        _db = db;
        _main = main;
        _neededCount = int.TryParse(db.GetSetting(NeededCountKey), out var n) && n > 0 ? n : DefaultNeededCount;
        Reload();
    }

    public void Reload() => Show(Load());

    /// <summary>Reload with the database work done in the background, so the window keeps answering.</summary>
    public async Task ReloadAsync() => Show(await UiWork.Run(Load));

    /// <summary>What the page shows, read from the database (on any thread).</summary>
    sealed record Loaded(IReadOnlyList<PluginUsage> Items, HashSet<string> InstalledNames,
                         IReadOnlyDictionary<long, HashSet<string>> MissingBy, (int Files, int Plugins, int Failed) Scans, (int Read, int Left) Parameters);

    Loaded Load() => new(_db.PluginUsage(), _db.InstalledNames(), _db.MissingPluginsByProject(), _db.PluginScanCounts(), _db.ParameterCounts());

    /// <summary>What the page last read, so changing how many cards to show doesn't read the database again.</summary>
    Loaded? _loaded;

    void Show(Loaded loaded)
    {
        _loaded = loaded;
        var selected = Selected;
        Items = loaded.Items;
        View = Ui.Current.CreateView(Items as System.Collections.IList ?? Items.ToList());
        View.Filter = o => o is PluginUsage p && Matches(p);
        var missing = Items.Count(p => !p.Installed);
        Summary = $"{Items.Count} plugins used" + (missing > 0 ? $", {missing} not installed on this PC" : "");
        BuildNeeded(loaded);
        Selected = selected is null ? null : Items.FirstOrDefault(p => p.Name == selected.Name && p.Own == selected.Own);

        if (!_main.PluginScanner.Available)
            ScannerNote = "IDs, versions and parameters come from the plugin scanner, which isn't built on this computer yet. " +
                          (PluginScanner.BuildProblem() is { } problem
                              ? $"The last try to build it said: {problem} (the rest is in native/out/app/build.log)."
                              : OperatingSystem.IsWindows()
                                  ? "Building the app builds it once Visual Studio's \"Desktop development with C++\" workload is installed (see native/README.md)."
                                  : "Building the app builds it once CMake and a C++ compiler are installed (see native/README.md).");
        else
        {
            var (files, plugins, failed) = loaded.Scans;
            var (read, left) = loaded.Parameters;
            ScannerNote = files == 0 ? "The plugin scanner reads your plugin files after the next scan."
                : $"The plugin scanner has read {files} plugin files holding {plugins} plugins" +
                  (failed > 0 ? $"; {failed} couldn't be read." : ".") +
                  (read == 0 ? "" : left > 0 ? $" It has the parameters of {read}, with {left} to go." : $" It has the parameters of {read}.");
            CanReadAll = left > 0 && !_main.ParametersReadPending;
        }
        LoadSelected();
    }

    bool Matches(PluginUsage p)
    {
        var ok = Filter switch
        {
            Missing => !p.Installed,
            Installed_ => p.Installed,
            Instruments => p.Kind == PluginKind.Instrument,
            Effects => p.Kind is PluginKind.Effect or PluginKind.NoteEffect,
            "VST2" or "VST3" or "CLAP" => p.Format.Equals(Filter, StringComparison.OrdinalIgnoreCase),
            "Native" => p.Format.Equals("Native", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
        return ok && (SearchText.Length == 0 || p.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                      p.Vendor.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The "install these" cards: missing plugins, the ones that are the last thing missing from the most projects first
    /// (they open those projects completely), then the ones the most projects use. The summary counts the projects that
    /// would open with nothing missing if you installed all the cards' plugins.
    /// </summary>
    void BuildNeeded(Loaded loaded)
    {
        var installedNames = loaded.InstalledNames;
        var top = Items.Where(p => !p.Installed)
            .OrderByDescending(p => p.Unlocks).ThenByDescending(p => p.Projects).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(NeededCount)
            .Select(p => new NeededPlugin(p, this, PluginCatalog.OtherVersionInstalled(p.Name, installedNames)))
            .ToList();
        Needed = top;
        OnPropertyChanged(nameof(HasNeeded));
        OnPropertyChanged(nameof(AllInstalled));
        var missingBy = loaded.MissingBy;
        if (top.Count == 0 || missingBy.Count == 0)
        {
            NeededSummary = "";
            return;
        }
        var keys = top.Select(t => LibraryDb.NameKey(t.Plugin.Name)).ToHashSet();
        var opened = missingBy.Values.Count(m => m.IsSubsetOf(keys));
        NeededSummary = $"{missingBy.Count} of your projects are missing a plugin. " +
                        (opened > 0 ? $"Install {(top.Count == 1 ? "this one" : $"these {top.Count}")} and {opened} of them open with nothing missing."
                                    : "Each also misses other plugins, so these are the ones the most projects use.");
    }

    /// <summary>
    /// Only the cards change, from what the page already read: reloading the whole page here froze the window for 4 s
    /// on Solomon's library (2026-10-19, Settings › Plugins).
    /// </summary>
    partial void OnNeededCountChanged(int value)
    {
        _db.SetSetting(NeededCountKey, value.ToString());
        if (_loaded is { } loaded) BuildNeeded(loaded);
        else _ = ReloadAsync();
    }

    partial void OnSearchTextChanged(string value) => View?.Refresh();
    partial void OnFilterChanged(string value) => View?.Refresh();
    partial void OnSelectedChanged(PluginUsage? value) => LoadSelected();

    void LoadSelected()
    {
        if (Selected is not { } p)
        {
            _saves = [];
            SelectedDetails = "";
        }
        else
        {
            // A fixed copy waiting for Keep isn't a save yet (as on Projects).
            var pending = _db.PendingFixes();
            _saves = _db.ProjectsWithPlugin(p.Name, p.Own);
            if (pending.Count > 0) _saves = _saves.Where(x => !pending.Contains(x.Path)).ToList();
            SelectedDetails = string.Join(" · ", new[]
            {
                p.Format == "Native" && p.Daws.Contains("FL", StringComparison.OrdinalIgnoreCase) ? "FL Studio native" : p.Format,
                p.Kind switch { PluginKind.Instrument => "Instrument", PluginKind.Effect => "Effect", PluginKind.NoteEffect => "Note effect", _ => "" },
                p.Vendor,
                p.LastUsed is { } t ? $"last used {t:MMMM yyyy}" : "",
            }.Where(x => x.Length > 0));
        }
        var daws = _saves.Select(x => x.Daw).Distinct().Order().ToList();
        if (!ProjectDaws.Skip(1).SequenceEqual(daws))
        {
            ProjectDaws.Clear();
            foreach (var d in daws.Prepend(ProjectsViewModel.Any)) ProjectDaws.Add(d);
            OnPropertyChanged(nameof(HasProjectDaws));
        }
        if (ProjectsDaw != ProjectsViewModel.Any && !daws.Contains(ProjectsDaw)) ProjectsDaw = ProjectsViewModel.Any;   // shows them
        else ShowSaves();
        LoadInstalled();
    }

    partial void OnEverySaveChanged(bool value) => ShowSaves();
    partial void OnProjectsDawChanged(string value)
    {
        if (value is null) ProjectsDaw = ProjectsViewModel.Any;   // the chips' ListBox clears it while they're refilled
        else ShowSaves();
    }

    /// <summary>
    /// The side panel's list: the picked plugin's saves in the picked DAW, as songs or every save. A plugin in thousands of
    /// saves (one in the default template) is sorted into songs in the background, so the click doesn't stall the window.
    /// </summary>
    void ShowSaves()
    {
        var saves = ShownSaves();
        var (every, daw, choices, show) = (EverySave, ProjectsDaw, _main.Projects.Choices, ++_shows);
        if (saves.Count < BackgroundSongsAt)
        {
            Apply(saves, SongVersions.Songs(saves, choices), every, daw);
            return;
        }
        _ = Later();

        async Task Later()
        {
            var songs = await UiWork.Run(() => SongVersions.Songs(saves, choices));
            if (show == _shows) Apply(saves, songs, every, daw);   // not if another plugin was picked meanwhile
        }
    }

    // Counts ShowSaves calls, so a slow one that finishes late doesn't replace a newer list.
    int _shows;
    internal const int BackgroundSongsAt = 2_000;

    void Apply(IReadOnlyList<PluginProject> saves, IReadOnlyList<SongUse> songs, bool every, string daw)
    {
        Projects = every ? saves.Select(x => new SongUse(x.Name, x, 1)).ToList() : songs;
        var where = daw == ProjectsViewModel.Any ? "" : $" in {daw}";
        ProjectsTitle = songs.Count == 1 ? $"Used in 1 song{where}" : $"Used in {songs.Count} songs{where}";
        ProjectsNote = saves.Count == songs.Count ? ""
            : every ? $"Every one of its {saves.Count} saves, newest first."
            : $"{saves.Count} saves in all. Each song shows its newest save that uses it.";
    }

    IReadOnlyList<PluginProject> ShownSaves() =>
        ProjectsDaw == ProjectsViewModel.Any ? _saves : _saves.Where(x => x.Daw == ProjectsDaw).ToList();

    /// <summary>A click on a project in the side panel: opens it on the Projects page.</summary>
    partial void OnPickedProjectChanged(SongUse? value)
    {
        if (value is null) return;
        _main.Projects.Reveal(value.Latest.Id);
        PickedProject = null;
    }

    /// <summary>Loads every installed plugin whose parameters haven't been read, to read them.</summary>
    [RelayCommand]
    Task ReadAllParameters()
    {
        CanReadAll = false;   // back when it ends, if any are left
        return _main.ReadAllParametersAsync();
    }

    void LoadInstalled()
    {
        if (Selected is not { } p)
        {
            Installed = [];
            InstalledNote = "";
            ShownCopy = null;
            return;
        }
        Installed = _db.ScannedPluginsNamed(p.Name).Select(s => new InstalledPluginItem(s, _db, _main.PluginScanner)).ToList();
        ShownCopy = Installed.FirstOrDefault(i => i.Plugin.Params is not null) ?? Installed.FirstOrDefault();
        InstalledNote = Installed.Count > 0 ? ""
            : !_main.PluginScanner.Available || _db.PluginScanCounts().Files == 0 ? "The plugin scanner hasn't read your plugins yet."
            : p.Installed ? "Installed, but the plugin scanner couldn't read it, or it's in a format it doesn't read yet (AU, AAX)."
            : "Not found in your plugin folders.";
    }

    [RelayCommand]
    void ShowProjects(PluginUsage? p)
    {
        if ((p ?? Selected) is not { } plugin) return;
        // The side panel's DAW chip carries over when it's this plugin's panel.
        if (plugin == Selected && ProjectsDaw != ProjectsViewModel.Any)
            _main.Projects.ShowOnly(ShownSaves().Select(x => x.Id), $"Projects using {plugin.Name} in {ProjectsDaw}");
        else
            _main.Projects.ShowOnly(_db.ProjectsUsingPlugin(plugin.Name, plugin.Own), $"Projects using {plugin.Name}");
    }

    internal IReadOnlyList<string> SimilarTo(string name) =>
        MatchFinder.ForPlugin(_db, name, 4).Select(m => m.Candidate).ToList();

    [RelayCommand]
    void SearchOnline(PluginUsage? p)
    {
        if ((p ?? Selected) is { } plugin) Shell.OpenUrl(MatchFinder.PluginSearchUrl(plugin.Name, plugin.Vendor));
    }
}

/// <summary>A missing plugin worth installing: how many projects it would open, with ways to find it or a stand-in.</summary>
public sealed partial class NeededPlugin(PluginUsage plugin, PluginsViewModel owner, string? otherVersion) : ObservableObject
{
    public PluginUsage Plugin => plugin;
    public string Name => plugin.Name;
    /// <summary>The big number: projects it opens completely, or (when each also misses others) projects using it.</summary>
    public int Count => plugin.Unlocks > 0 ? plugin.Unlocks : plugin.Projects;
    public string CountLabel => plugin.Unlocks > 0 ? (plugin.Unlocks == 1 ? "project it opens" : "projects it opens")
                                                  : (plugin.Projects == 1 ? "project uses it" : "projects use it");
    /// <summary>"Used in 14 · VST2 · Xfer Records · last used 2019".</summary>
    public string Details => string.Join(" · ", new[]
    {
        plugin.Unlocks > 0 && plugin.Projects > plugin.Unlocks ? $"used in {plugin.Projects}" : "",
        plugin.Format, plugin.Vendor,
        plugin.LastUsed is { } t ? $"last used {t:yyyy}" : "",
    }.Where(x => x.Length > 0));
    /// <summary>"You have Ozone 11, which may open them", when another version is installed.</summary>
    public string? OtherVersion => otherVersion is null ? null : $"You have {otherVersion}, which may open them";
    public bool HasOtherVersion => otherVersion is not null;

    [ObservableProperty] IReadOnlyList<string> _similar = [];
    [ObservableProperty] string _similarNote = "";
    public bool HasSimilar => Similar.Count > 0;
    partial void OnSimilarChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasSimilar));

    [RelayCommand]
    void Pick() => owner.Selected = plugin;

    /// <summary>Installed plugins with close names: a newer version, or the same plugin under another name.</summary>
    [RelayCommand]
    void FindSimilar()
    {
        owner.Selected = plugin;
        Similar = owner.SimilarTo(plugin.Name);
        SimilarNote = Similar.Count == 0 ? "Nothing similar installed." : "";
    }

    [RelayCommand]
    void SearchOnline() => Shell.OpenUrl(MatchFinder.PluginSearchUrl(plugin.Name, plugin.Vendor));
}

/// <summary>One installed plugin as the plugin scanner read it, with its parameters once they're read.</summary>
public sealed partial class InstalledPluginItem : ObservableObject
{
    readonly LibraryDb _db;
    readonly PluginScanner _scanner;

    public ScannedPlugin Plugin { get; private set; }

    [ObservableProperty] IReadOnlyList<ScannedParam> _parameters = [];
    [ObservableProperty] string _parametersNote = "";
    [ObservableProperty] bool _isReading;

    public InstalledPluginItem(ScannedPlugin plugin, LibraryDb db, PluginScanner scanner)
    {
        Plugin = plugin;
        _db = db;
        _scanner = scanner;
        ShowParameters();
    }

    public string Title => Plugin.Name;
    public string Format => Plugin.Format;

    /// <summary>"Instrument · FabFilter · 3.24".</summary>
    public string Details => string.Join(" · ", new[]
    {
        Plugin.Kind.Length > 0 ? char.ToUpperInvariant(Plugin.Kind[0]) + Plugin.Kind[1..] : "",
        Plugin.Vendor,
        Plugin.Version.Length > 0 ? $"version {Plugin.Version}" : "",
        Plugin.Shell.Length > 0 ? $"in {Plugin.Shell}" : "",
    }.Where(s => s.Length > 0));

    /// <summary>The ID DAW projects save it under.</summary>
    public string IdText => $"ID {Plugin.Id}";
    public string Location => Plugin.Path;
    public bool CanRead => Plugin.Params is null && !IsReading;
    public bool HasParameters => Parameters.Count > 0;

    void ShowParameters()
    {
        if (Plugin.Params is not { } all)
        {
            Parameters = [];
            ParametersNote = Plugin.Error.Length > 0 ? $"The scanner couldn't load it: {Plugin.Error}" : "";
            return;
        }
        Parameters = all.Where(p => !p.Hidden).ToList();
        var hidden = all.Count - Parameters.Count;
        ParametersNote = all.Count == 0 ? "It has no parameters."
            : $"{Parameters.Count} parameters, with the values a new copy starts with" + (hidden > 0 ? $" ({hidden} hidden ones left out)." : ".");
    }

    partial void OnIsReadingChanged(bool value) => OnPropertyChanged(nameof(CanRead));
    partial void OnParametersChanged(IReadOnlyList<ScannedParam> value) => OnPropertyChanged(nameof(HasParameters));

    /// <summary>Loads the plugin in the scanner to read its parameters (some plugins show a licence window).</summary>
    [RelayCommand]
    async Task ReadParameters()
    {
        if (IsReading) return;
        IsReading = true;
        ParametersNote = "Loading the plugin…";
        try
        {
            // Kept either way: its parameters, or why it couldn't be loaded.
            Plugin = await _scanner.ReadParametersAsync(Plugin);
            await Task.Run(() => _db.SaveScannedPlugin(Plugin));
            OnPropertyChanged(nameof(Details));
            ShowParameters();
        }
        finally
        {
            IsReading = false;
        }
    }
}
