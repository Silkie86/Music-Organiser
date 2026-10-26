using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.LiveTests;
using MusicOrganiser.Core.Plugins;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.ViewModels;

/// <summary>The window: which screen is showing, background scans, and the DAWs open now.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    readonly LibraryDb _db;
    readonly Scanner _scanner;
    CancellationTokenSource? _scanCts;

    /// <summary>The native plugin scanner (mo-scan), if it was built.</summary>
    public PluginScanner PluginScanner { get; }

    /// <summary>The app's data folder (tests use one of their own), where it also keeps its Bitwig template.</summary>
    public string DataFolder { get; }

    /// <summary>The sound library, for Settings' self-check to time loading it.</summary>
    public SoundLibrary SoundLibrary { get; }

    /// <summary>Self-checks only the app's window side can make (sound output, graphics): the app adds them when it starts.</summary>
    public IReadOnlyList<(string Area, string Name, Func<CancellationToken, Task<LiveVerdict>> Test)> AppChecks { get; set; } = [];

    public ProjectsViewModel Projects { get; }
    public PluginsViewModel Plugins { get; }
    public SamplesViewModel Samples { get; }
    public SoundsViewModel Sounds { get; }
    /// <summary>The MIDI page: every MIDI part in your projects and MIDI libraries.</summary>
    public MidiBrowserViewModel Midi { get; }
    /// <summary>The MIDI playground: layer, write and match MIDI parts in sections, then drag them into the DAW.</summary>
    public PlaygroundViewModel Playground { get; }
    /// <summary>The app's keyboard shortcuts (Settings › Shortcuts).</summary>
    public ShortcutsViewModel Shortcuts { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>Bitwig and FL Studio as they are now, through their links (the sidebar's "In your DAWs").</summary>
    public LiveDawsViewModel Live { get; }

    public IReadOnlyList<NavItem> Nav { get; }

    /// <summary>The activity cards: every job says when it starts, how far it has got and when it's done.</summary>
    public ActivityFeed Activities { get; } = new();

    [ObservableProperty] NavItem _selectedNav;
    [ObservableProperty] bool _isScanning;
    [ObservableProperty] string _scanStatus = "";
    /// <summary>The window fills the screen (F11), so the sidebar shows a way out.</summary>
    [ObservableProperty] bool _isFullScreen;
    [ObservableProperty] double _scanPercent;
    Activity? _scanCard;
    readonly bool _midiPage;

    /// <param name="watch">How the DAWs are reached: their usual links unless given (tests use stand-ins).</param>
    /// <param name="pluginScanner">The plugin scanner: mo-scan where the build put it unless given (tests use a stand-in).</param>
    /// <param name="dataFolder">
    /// Where Settings finds MO Probe's reports and puts the live tests' reports: the app's data folder unless given (tests
    /// use a folder of their own).
    /// </param>
    /// <param name="midiPage">Shows the MIDI page: the old WPF app has no view for it, so it leaves it out (and its reading).</param>
    public MainViewModel(LibraryDb db, SoundOrganiser sounds, DawWatch? watch = null, PluginScanner? pluginScanner = null,
                         string? dataFolder = null, bool midiPage = true)
    {
        _db = db;
        Parallelism.Mode = Parallelism.ParsePace(db.GetSetting(SettingsViewModel.PerformanceKey));
        DataFolder = dataFolder ?? LiveReports.DataFolder;
        SoundLibrary = sounds.Library;
        PluginScanner = pluginScanner ?? new();
        Shortcuts = new ShortcutsViewModel(db);
        _scanner = new Scanner(db);
        Live = new LiveDawsViewModel(db, this, watch ?? new DawWatch());
        Projects = new ProjectsViewModel(db, this);
        Plugins = new PluginsViewModel(db, this);
        Samples = new SamplesViewModel(db, this);
        Settings = new SettingsViewModel(db, this, dataFolder);
        Sounds = new SoundsViewModel(db, sounds, this);
        Midi = new MidiBrowserViewModel(db, this);
        Playground = new PlaygroundViewModel(db, this);
        Settings.Liked.Changed += Sounds.WeightsChanged;
        Nav =
        [
            new("projects", "Projects", "", Projects),
            new("sounds", "Sound Lab", "\uE8D6", Sounds),
            new("midi", "MIDI", "", Midi),
            new("playground", "Playground", "", Playground),
            new("plugins", "Plugins", "", Plugins),
            new("settings", "Settings", "", Settings),
        ];
        Settings.Appearance.NamePages(Nav.Select(n => (n.Key, n.Title)));
        _midiPage = midiPage;
        if (!midiPage) Nav = Nav.Where(n => n.Page != Midi && n.Page != Playground).ToList();   // the playground needs the MIDI page, and has no WPF view either
        _selectedNav = StartPage();
        Settings.Appearance.PageSizesChanged += PageSizeChanged;
        Settings.Appearance.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppearanceViewModel.Size)) PageSizeChanged(); };
        PageSizeChanged();
        if (_selectedNav.Page == Midi || _selectedNav.Page == Playground) Midi.Opened();
        if (_selectedNav.Page == Playground) Playground.Opened();
        _ = Projects.ReloadAsync();   // read in the background: 16,000 projects held the window up as it opened
        _projectsShown = _soundsShown = db.ProjectsRevision;   // those two pages read the library as they open

        // First run: send people to Settings to add their project folders.
        if (_db.Roots().Count == 0 && _db.GetList(Scanner.SampleFoldersKey).Count == 0) SelectedNav = Nav[^1];
        else _ = ScanAsync(false);
        WatchProjectFolders();
        Live.Start();
    }

    FolderWatch? _projectWatch;

    /// <summary>How long the project folders must be quiet before what changed is read (a DAW saves in several steps).</summary>
    public static TimeSpan ProjectWatchQuiet { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// While the app is open, the project folders are watched: a project saved, added, moved or deleted is read (or
    /// marked missing) a moment later, on its own, without walking every folder again. Called again when Settings
    /// changes the folders.
    /// </summary>
    public void WatchProjectFolders()
    {
        _projectWatch?.Dispose();
        _projectWatch = new FolderWatch(_db.Roots(), Scanner.IsProjectFile, c => _ = ProjectFilesChangedAsync(c), ProjectWatchQuiet);
    }

    async Task ProjectFilesChangedAsync(FolderWatch.Changes changes)
    {
        // Too much changed to name every file, or a scan is running that may already have passed them: walk the folders.
        if (changes.Overflowed.Count > 0 || IsScanning)
        {
            await ScanAsync(false, plugins: false);
            return;
        }
        try
        {
            var (read, gone) = await Task.Run(() => _scanner.Update(changes.Files));
            if (read + gone == 0) return;
            await RefreshAllAsync();
            if (_midiPage) _ = Midi.AfterScanAsync();   // a saved song's MIDI is read again too
            // Named after a file the scan reads: not a DAW's backup copy saved beside it (Bitwig's auto-backups).
            var roots = _db.Roots();
            var name = Path.GetFileNameWithoutExtension(changes.Files.FirstOrDefault(f => Scanner.IsProjectFile(f) && !Scanner.IsIgnored(f, roots))
                                                        ?? changes.Files.First());
            Activities.Done(read + gone == 1 ? $"Updated {name}" : $"Updated {read + gone} projects",
                            gone == 0 ? "Saved in their folders, so the app read them again"
                                      : $"{(read > 0 ? $"{read} saved, " : "")}{gone} moved or deleted");
            AppLog.Info("Watch", $"Project folders: {read} read, {gone} gone ({changes.Files.Count} changes seen)");
        }
        catch (Exception e)
        {
            AppLog.Info("Watch", $"Couldn't read changed projects: {e.Message}");
        }
    }

    /// <summary>The page the app opens on: the one picked in Settings, or the last one used, or Projects.</summary>
    NavItem StartPage()
    {
        var choice = _db.GetSetting(SettingsViewModel.StartPageKey) ?? "Projects";
        var title = choice == SettingsViewModel.LastPageChoice ? _db.GetSetting(LastPageKey) : choice;
        if (title == "Samples") title = "Sound Lab";   // Samples is a tab on the Sounds page now
        return Nav.FirstOrDefault(n => n.Title == title) ?? Nav[0];
    }

    /// <summary>The page showing when the app was last used, for "Last page I used" in Settings.</summary>
    public const string LastPageKey = "last_page";

    public void ShowProjects() => SelectedNav = Nav.First(n => n.Page == Projects);

    public void ShowSettings() => SelectedNav = Nav.First(n => n.Page == Settings);

    /// <summary>
    /// The window is in front again, perhaps after the template was changed in Bitwig: Settings, if it's showing,
    /// looks at the template again.
    /// </summary>
    public void WindowActivated()
    {
        if (SelectedNav?.Page == Settings) Settings.RefreshTemplate();
    }

    // ---- the size of the page on show (Settings › Appearance sets every page; the rail's size button just this one) ----

    string PageKey => SelectedNav?.Key ?? "";
    string PageTitle => SelectedNav?.Title ?? "";
    /// <summary>The page on show's size, in percent.</summary>
    public int PageSize => Settings.Appearance.SizeFor(PageKey);
    /// <summary>How much bigger the page is drawn than the rest of the window, which <see cref="AppearanceViewModel.Size"/> scales.</summary>
    public double PageScale => SelectedNav?.Scale ?? 1;
    public bool PageHasOwnSize => Settings.Appearance.HasOwnSize(PageKey);
    public string PageSizeText => $"{PageSize}%";
    public string PageSizeTip => PageHasOwnSize
        ? $"{PageTitle} is {PageSize}%, its own size (the other pages are {Settings.Appearance.Size}%). Click to change it."
        : $"Every page is {PageSize}% (Settings › General). Click to size {PageTitle} on its own.";

    void PageSizeChanged()
    {
        // Every page's scale, not just this one's: a page fading out keeps the size it was shown at.
        foreach (var nav in Nav) nav.Scale = Settings.Appearance.SizeFor(nav.Key) / (double)Settings.Appearance.Size;
        OnPropertyChanged(nameof(PageSize));
        OnPropertyChanged(nameof(PageScale));
        OnPropertyChanged(nameof(PageHasOwnSize));
        OnPropertyChanged(nameof(PageSizeText));
        OnPropertyChanged(nameof(PageSizeTip));
        PageBiggerCommand.NotifyCanExecuteChanged();
        PageSmallerCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The page on show a size bigger.</summary>
    [RelayCommand(CanExecute = nameof(CanPageBigger))]
    void PageBigger() => Settings.Appearance.StepPage(PageKey, 1);
    bool CanPageBigger() => PageSize < AppearanceViewModel.Sizes[^1];

    /// <summary>The page on show a size smaller, to fit more on it.</summary>
    [RelayCommand(CanExecute = nameof(CanPageSmaller))]
    void PageSmaller() => Settings.Appearance.StepPage(PageKey, -1);
    bool CanPageSmaller() => PageSize > AppearanceViewModel.Sizes[0];

    /// <summary>The page on show back to the size of the rest.</summary>
    [RelayCommand]
    void PageSizeAsRest() => Settings.Appearance.ResetPage(PageKey);

    // ---- moving between pages with the keyboard ----

    readonly Stack<NavItem> _back = new(), _forward = new();
    bool _stepping;

    /// <summary>Shows the page called <paramref name="title"/> (as in the sidebar), if the app has it.</summary>
    public bool GoTo(string title)
    {
        if (Nav.FirstOrDefault(n => n.Title == title) is not { } page) return false;
        SelectedNav = page;
        return true;
    }

    /// <summary>The page <paramref name="by"/> places down the sidebar (up when negative), round from the end to the start.</summary>
    public void StepPage(int by)
    {
        var at = Nav.ToList().IndexOf(SelectedNav);
        SelectedNav = Nav[((at + by) % Nav.Count + Nav.Count) % Nav.Count];
    }

    /// <summary>Back to the page shown before this one, as a browser's back button does; false when there's none.</summary>
    public bool Back() => Step(_back, _forward);

    /// <summary>Forward again after <see cref="Back"/>.</summary>
    public bool Forward() => Step(_forward, _back);

    bool Step(Stack<NavItem> from, Stack<NavItem> to)
    {
        if (from.Count == 0) return false;
        to.Push(SelectedNav);
        _stepping = true;
        try { SelectedNav = from.Pop(); }
        finally { _stepping = false; }
        return true;
    }

    partial void OnSelectedNavChanged(NavItem? oldValue, NavItem newValue)
    {
        if (_stepping || oldValue is null || oldValue == newValue) return;
        _back.Push(oldValue);
        _forward.Clear();
    }

    partial void OnSelectedNavChanged(NavItem value)
    {
        PageSizeChanged();
        _db.SetSetting(LastPageKey, value.Title);
        if (value.Page == Midi || value.Page == Playground) Midi.Opened();   // the playground matches against the MIDI page's parts
        if (value.Page == Playground) Playground.Opened();
        if (value.Page != Midi) Midi.Left();
        if (value.Page != Playground) Playground.Left();
        if (value.Page != Projects) Projects.Detail?.LeftSongMidi(closed: false);
        // The MO Probes running now are followed only while Settings shows them.
        if (value.Page != Settings)
        {
            Settings.Probe.StopLive();
            return;
        }
        Settings.RefreshTemplate();
        _ = Settings.RefreshLinksAsync();
        _ = Plugins.RefreshHomesAsync();
        _ = Settings.SoundOutput.RefreshAsync();
        _ = Settings.Probe.RefreshAsync();
        Settings.Probe.StartLive();
    }

    /// <summary>The pages whose part of the library changed read it again, in the background (see <see cref="RefreshAllAsync"/>).</summary>
    public void RefreshAll() => _ = RefreshAllAsync();

    // The library revision each page last read (-1: not yet), so a scan that changed nothing leaves the pages alone.
    long _projectsShown, _soundsShown, _samplesShown = -1;
    (long, long) _pluginsShown = (-1, -1);

    /// <summary>
    /// After a scan (or a DAW saving a project): each page whose part of the library changed reads it again in the
    /// background, then shows the result, so a library of 16,000 projects and 357,000 sounds doesn't hold the window up
    /// ("Not responding"). Most scans find nothing new, and then no page reads anything. The sounds themselves don't
    /// change in a project scan, only how many projects use each one.
    /// </summary>
    public async Task RefreshAllAsync()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var times = new List<string>();
        async Task Time(string page, Func<Task> reload)
        {
            var start = timer.Elapsed;
            await reload();
            times.Add($"{page} {(timer.Elapsed - start).TotalSeconds:0.00} s");
        }
        var projects = _db.ProjectsRevision;
        var plugins = (projects, _db.PluginsRevision);
        if (projects != _projectsShown)
        {
            _projectsShown = projects;
            await Time("Projects", Projects.ReloadAsync);
        }
        if (plugins != _pluginsShown)
        {
            _pluginsShown = plugins;
            await Time("Plugins", Plugins.ReloadAsync);
        }
        if (projects != _samplesShown)
        {
            _samplesShown = projects;
            await Time("Samples", Samples.ReloadAsync);
        }
        if (projects != _soundsShown)
        {
            _soundsShown = projects;
            await Time("Sounds", Sounds.RefreshUsageAsync);
        }
        Live.Match();   // the library may have the projects the DAWs have open now
        AppLog.Info("Pages", times.Count == 0
            ? "Nothing new in the library, so the pages were left as they are"
            : $"Refreshed in {timer.Elapsed.TotalSeconds:0.00} s: {string.Join(", ", times)}");
    }

    [RelayCommand]
    Task Rescan() => ScanAsync(false);

    /// <summary>Opens the Recycle Bin, where Clean up, Compact and Uninstall put what they remove (Solomon's F12 note, 25 Oct).</summary>
    [RelayCommand]
    internal void OpenRecycleBin()
    {
        try { Shell.OpenRecycleBin(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Activities.Failed("Couldn't open the Recycle Bin", e.Message);
        }
    }

    [RelayCommand]
    Task FullRescan() => ScanAsync(true);

    [RelayCommand]
    void CancelScan()
    {
        _parametersQueued = false;
        _scanCts?.Cancel();
        DropQueuedScan();
    }

    /// <summary>
    /// Stops scanning as the app closes, and ends the plugin scanner's processes there and then: a plugin still
    /// loading, or waiting on its licence window, would otherwise outlive the app.
    /// </summary>
    public void Shutdown()
    {
        _projectWatch?.Dispose();
        Sounds.StopWatching();
        CancelScan();
        Playground.Dispose();
        Midi.Dispose();
        PluginScanner.StopAll();
    }

    bool _readingParameters, _parametersQueued;

    /// <summary>Reading every plugin's parameters is what's running now.</summary>
    internal bool IsReadingParameters => _readingParameters;

    /// <summary>Reading every plugin's parameters is running, or will once the scan running now ends.</summary>
    internal bool ParametersReadPending => _readingParameters || _parametersQueued;

    /// <summary>
    /// Reads the parameters of every installed plugin that doesn't have them yet (the Plugins screen), with
    /// the scan's progress and Stop button. Asked for during a scan, it starts when the scan ends; a scan asked
    /// for while it runs goes first, and it carries on after.
    /// </summary>
    public Task ReadAllParametersAsync()
    {
        if (ParametersReadPending || !PluginScanner.Available) return Task.CompletedTask;
        if (!IsScanning) return RunParametersAsync();
        _parametersQueued = true;
        return Task.CompletedTask;
    }

    async Task RunParametersAsync()
    {
        _readingParameters = IsScanning = true;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        _scanCard = Activities.Start("Reading plugin parameters", cancel: CancelScan);
        var progress = ProgressInStatus();
        try
        {
            var (read, failed) = await Task.Run(() => _scanner.ReadAllParametersAsync(PluginScanner, progress, ct), ct);
            ScanStatus = $"Parameters read for {read} plugin{(read == 1 ? "" : "s")}" +
                         (failed > 0 ? $"; {failed} couldn't be loaded" : "") + $" · {DateTime.Now:HH:mm}";
            _scanCard.Finish("Plugin parameters read", $"{read} plugin{(read == 1 ? "" : "s")}" +
                                                       (failed > 0 ? $", {failed} couldn't be loaded" : ""));
        }
        catch (OperationCanceledException)
        {
            ScanStatus = _parametersQueued ? "Reading parameters after this scan"   // making way for one
                : "Stopped reading parameters; it carries on from there next time";
            if (_parametersQueued) _scanCard.Stop("Paused reading parameters", "It carries on after this scan");
            else _scanCard.Stop("Stopped reading parameters", "It carries on from there next time");
        }
        catch (Exception e)
        {
            ScanStatus = $"Reading parameters failed: {e.Message}";
            _scanCard.Fail("Reading parameters failed", e.Message);
        }
        finally
        {
            progress.Close();
            _readingParameters = IsScanning = false;
            ScanPercent = 0;
            Plugins.Reload();
        }
        await RunQueuedScanAsync();
    }

    ThrottledProgress<ScanProgress> ProgressInStatus() => new(p =>
    {
        ScanStatus = p.Total > 0 ? $"{p.Stage}: {p.Done} of {p.Total}" : p.Stage;
        ScanPercent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
        _scanCard?.Report(p.Total > 0 ? ScanPercent : -1, p.Total > 0 ? $"{p.Stage}: {p.Done:N0} of {p.Total:N0}" : p.Stage);
    });

    // A scan asked for while another is running (say, adding a folder during the startup scan)
    // runs straight after it instead of being dropped.
    (bool Full, bool Plugins, bool Samples)? _queuedScan;
    TaskCompletionSource? _queuedScanDone;

    public Task ScanAsync(bool full, bool plugins = true, bool samples = false)
    {
        if (!IsScanning) return RunScanAsync(full, plugins, samples);
        var q = _queuedScan ?? default;
        _queuedScan = (q.Full || full, q.Plugins || plugins, q.Samples || samples);
        var done = (_queuedScanDone ??= new TaskCompletionSource()).Task;
        if (_readingParameters && !_parametersQueued && _scanCts is { IsCancellationRequested: false })
        {
            // Reading every plugin's parameters can take an hour: it makes way for the scan (the plugins it was
            // loading are read again) and carries on after it. (Not when Stop has just stopped it.)
            _parametersQueued = true;
            _scanCts.Cancel();
        }
        return done;
    }

    void DropQueuedScan()
    {
        var done = _queuedScanDone;
        _queuedScan = null;
        _queuedScanDone = null;
        done?.TrySetResult();
    }

    async Task RunQueuedScanAsync()
    {
        if (_queuedScan is not { } next)
        {
            // Reading every plugin's parameters, asked for during the scan that just ended or paused for it. It runs
            // on its own, so whoever waited for the scan isn't kept waiting for it too.
            if (_parametersQueued)
            {
                _parametersQueued = false;
                if (_db.ParameterCounts().Left > 0) _ = RunParametersAsync();
            }
            return;
        }
        var done = _queuedScanDone;
        _queuedScan = null;
        _queuedScanDone = null;
        try { await RunScanAsync(next.Full, next.Plugins, next.Samples); }
        finally { done?.TrySetResult(); }
    }

    async Task RunScanAsync(bool full, bool plugins, bool samples)
    {
        IsScanning = true;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        _scanCard = Activities.Start(full ? "Re-reading everything" : "Looking for new and changed projects", cancel: CancelScan);
        var added = 0;
        var progress = ProgressInStatus();
        try
        {
            IReadOnlyList<string> pluginFiles = [];
            await Task.Run(() =>
            {
                // Sample folders are indexed alongside the projects; the projects need the plugin list first.
                var sampling = samples ? Task.Run(() => _scanner.ScanSampleFolders(null, ct)) : Task.CompletedTask;
                if (plugins)
                {
                    ((IProgress<ScanProgress>)progress).Report(new("Checking installed plugins", 0, 0, ""));
                    _scanner.ScanInstalledPlugins(out pluginFiles);
                }
                added = _scanner.ScanProjects(progress, full, ct);
                sampling.GetAwaiter().GetResult();
            }, ct);

            // New or updated plugin files: read what's inside them with the native scanner. That can take
            // a minute the first time, so show the projects first.
            if (pluginFiles.Count > 0 && PluginScanner.Available)
            {
                await RefreshAllAsync();
                await Task.Run(async () =>
                {
                    await _scanner.ReadPluginFilesAsync(PluginScanner, pluginFiles, progress, ct);
                    _scanner.ScanInstalledPlugins();   // now knowing every plugin inside them
                }, ct);
            }
            ScanStatus = $"Library up to date · {DateTime.Now:HH:mm}";
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "Scan stopped";
            _scanCard.Stop("Scan stopped", "Rescan carries on from where it got to");
        }
        catch (Exception e)
        {
            ScanStatus = $"Scan failed: {e.Message}";
            _scanCard.Fail("Scan failed", e.Message);
        }
        finally
        {
            progress.Close();
            IsScanning = false;
            ScanPercent = 0;
            await RefreshAllAsync();
        }
        if (_scanCard.IsRunning)
        {
            // Counted by the scan itself: the list may still have been loading when it started.
            _scanCard.Finish("Library up to date",
                added > 0 ? $"{added} new project file{(added == 1 ? "" : "s")} · {Projects.Items.Count} songs in all" : $"{Projects.Items.Count} songs, nothing new");
        }
        // The MIDI browser reads new and changed projects' MIDI on its own, at the lowest priority.
        if (_midiPage) _ = Midi.AfterScanAsync();
        _ = MeasureSpaceAsync();
        await RunQueuedScanAsync();
    }

    bool _measuringSpace;

    /// <summary>
    /// Measures how much room plugins and songs take, after a scan, in the background slots (see <see cref="SpaceMeter"/>):
    /// plugins once a week (or now, with <paramref name="force"/>), song folders whose size is missing or older than
    /// their newest save. The Plugins and Projects pages pick the sizes up when it's done.
    /// </summary>
    public async Task MeasureSpaceAsync(bool force = false)
    {
        if (_measuringSpace) return;
        _measuringSpace = true;
        var meter = new Core.Space.SpaceMeter(_db);
        var folders = Projects.OwnFolderList();
        using var cts = new CancellationTokenSource();
        Activity? card = null;
        try
        {
            var plugins = force || meter.PluginsStale();
            if (plugins)
            {
                card = Activities.Start("Measuring plugin sizes", cancel: cts.Cancel);
                var before = SpaceSignature(_db.PluginSpaces().Values);
                var after = SpaceSignature(await Task.Run(() => meter.MeasurePlugins(ct: cts.Token), cts.Token));
                // Only when a size changed: the page keeps its rows (and your place in them) otherwise.
                if (before != after) await Plugins.ReloadAsync();
            }
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                card ??= Activities.Start("Measuring song folders", cancel: cts.Cancel);
                card.Report(100.0 * p.Done / Math.Max(1, p.Total), $"{p.Done:N0} of {p.Total:N0} song folders");
            });
            var measured = await Task.Run(() => meter.MeasureFolders(folders, progress, cts.Token), cts.Token);
            if (measured > 0) Projects.ApplySpace(_db.FolderSpaces());
            card?.Finish("Disk space measured", measured > 0 ? $"{measured:N0} song folders" : "Plugins measured");
        }
        catch (OperationCanceledException)
        {
            card?.Stop("Stopped measuring disk space", "It carries on after the next scan");
            Projects.ApplySpace(_db.FolderSpaces());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            card?.Fail("Measuring disk space failed", e.Message);
        }
        finally
        {
            _measuringSpace = false;
        }
    }

    static string SpaceSignature(IEnumerable<Core.Space.PluginSpaceInfo> spaces) =>
        string.Join("|", spaces.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}:{s.Bytes}:{s.SharedBytes}:{s.Folders.Count}"));
}

/// <summary>A place in the sidebar. <paramref name="key"/> names it in saved settings, so renaming the page keeps them.</summary>
public sealed partial class NavItem(string key, string title, string glyph, ObservableObject page) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public ObservableObject Page { get; } = page;

    /// <summary>
    /// How much bigger this page is drawn than the rest of the window (which Settings' size scales as a whole): 1 unless the
    /// page has a size of its own. Each page carries its own, so the one fading out keeps its size while the next fades in.
    /// </summary>
    [ObservableProperty] double _scale = 1;
}
