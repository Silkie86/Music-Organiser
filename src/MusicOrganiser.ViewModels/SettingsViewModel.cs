using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Formats;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.LiveTests;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.ViewModels;

/// <summary>Where your projects, samples and plugins live, and how projects are opened.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>FL Studio's user data folder, when it was chosen here.</summary>
    public const string FlUserDataKey = "fl_user_data";
    public const string BitwigUserKey = "bitwig_user_folder";
    /// <summary>The Extensions folder Bitwig was last seen loading the extension from, which Install puts it in too.</summary>
    public const string BitwigLoadsFromKey = "bitwig_extensions_folder";

    readonly LibraryDb _db;
    readonly MainViewModel _main;
    /// <summary>The <see cref="BitwigLoadsFromKey"/> setting as last read or kept, so each look doesn't read it again.</summary>
    string? _bitwigLoadsFrom;
    /// <summary>Where the live tests' reports go: reports\live in the app's data folder.</summary>
    readonly string _liveReports;

    public ObservableCollection<string> ProjectFolders { get; } = new();
    public ObservableCollection<string> SampleFolders { get; } = new();
    public ObservableCollection<string> PluginFolders { get; } = new();
    public ObservableCollection<DawSetting> Daws { get; } = new();

    [ObservableProperty] string? _selectedProjectFolder;
    [ObservableProperty] string? _selectedSampleFolder;
    [ObservableProperty] string? _selectedPluginFolder;
    [ObservableProperty] string _exportFolder = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditBitwigTemplateCommand), nameof(ShowBitwigTemplateCommand))]
    string _bitwigTemplate = "";
    /// <summary>What's wrong with the template, where it came from, or what Edit in Bitwig did.</summary>
    [ObservableProperty] string _bitwigTemplateNote = "";
    [ObservableProperty] bool _stepsUntilNextNote;
    [ObservableProperty] bool _upgradeSynths;

    /// <summary>Which page the app opens on: a page's title, or <see cref="LastPageChoice"/>.</summary>
    public const string StartPageKey = "start_page";
    public const string LastPageChoice = "Last page I used";
    public static IReadOnlyList<string> StartPageChoices { get; } =
        ["Projects", "Sound Lab", "Plugins", "Settings", LastPageChoice];
    [ObservableProperty] string _startPage;
    [ObservableProperty] string _sampleIndexText = "";
    public string LibraryLocation => _db.FilePath;

    /// <summary>The scripts that let the app ask Bitwig and FL Studio what they show.</summary>
    public IReadOnlyList<DawLinkSetting> Links { get; }

    /// <summary>MO Probe, the test plugin: whether it's installed, the probes running now, and its reports.</summary>
    public ProbeReportsViewModel Probe { get; }

    /// <summary>Where the app's sounds play: the computer's default output, or one chosen (an audio interface, say).</summary>
    public SoundOutputSetting SoundOutput { get; }

    /// <summary>What Set up both did: a line for each DAW.</summary>
    [ObservableProperty] string _setUpResult = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SetUpBothCommand))] bool _settingUp;

    /// <param name="dataFolder">
    /// The app's data folder, where MO Probe's reports and the live tests' reports are: %LOCALAPPDATA%\MusicOrganiser unless
    /// given (tests give one of their own, so opening Settings never reaches the real one).
    /// </param>
    /// <summary>The theme, accent, font, size and motion.</summary>
    public AppearanceViewModel Appearance { get; }
    /// <summary>How Sounds › Most liked weighs things.</summary>
    public LikedSettingsViewModel Liked { get; }

    public SettingsViewModel(LibraryDb db, MainViewModel main, string? dataFolder = null)
    {
        _db = db;
        _main = main;
        Appearance = new AppearanceViewModel(db);
        Liked = new LikedSettingsViewModel(db);
        foreach (var r in db.Roots()) ProjectFolders.Add(r);
        foreach (var f in db.GetList(Scanner.SampleFoldersKey)) SampleFolders.Add(f);
        var plugins = db.GetList(Scanner.PluginFoldersKey);
        foreach (var f in plugins.Count > 0 ? plugins : PluginCatalog.DefaultFolders()) PluginFolders.Add(f);
        foreach (var r in Readers.All)
            Daws.Add(new DawSetting(db, r.Daw, string.Join(", ", r.Extensions)));
        _exportFolder = db.GetSetting(ProjectDetailViewModel.ExportFolderKey) ?? "";
        _bitwigTemplate = BitwigTemplateStore.Find(db, main.DataFolder) ?? "";
        _bitwigTemplateNote = TemplateNote(_bitwigTemplate);
        _stepsUntilNextNote = db.GetSetting(ProjectDetailViewModel.StepsUntilNextNoteKey) == "1";
        _upgradeSynths = db.GetSetting(ProjectDetailViewModel.UpgradeSynthsKey) == "1";
        _startPage = db.GetSetting(StartPageKey) is { } start ? start == "Samples" ? "Sound Lab" : start : "Projects";   // Samples is on the Sounds page now
        if (!StartPageChoices.Contains(_startPage)) _startPage = "Projects";
        UpdateSampleIndexText();
        var watch = main.Live.Watch;
        Probe = new ProbeReportsViewModel(folder: dataFolder is null ? null : Path.Combine(dataFolder, "probe"),
                                          pluginFolders: () => PluginFolders.Concat(MoProbe.InstallFolders()).Distinct(),
                                          connectBitwig: watch.ConnectBitwigAsync);
        _liveReports = dataFolder is null ? LiveReports.DefaultFolder : Path.Combine(dataFolder, "reports", "live");
        var liveTests = new LiveTestsViewModel(BitwigLiveTests.Key, _liveReports, (folder, progress, ct) =>
        {
            // Settings' lists are read here, on the UI thread; the run itself goes on in the background.
            var options = LiveTestOptions() with { ReportFolder = folder };
            return Task.Run(() => BitwigLiveTests.RunAsync(options, progress, ct));
        });
        SelfCheck = new LiveTestsViewModel(Core.LiveTests.SelfCheck.Key,
                                           dataFolder is null ? Core.LiveTests.SelfCheck.DefaultFolder : Path.Combine(dataFolder, "reports", "checks"),
                                           (folder, progress, ct) =>
        {
            // Only Settings' own lists are copied here, on the UI thread (a few folders). Everything else, even listing the
            // library's projects, happens in the background, so the app stays usable while the checks run.
            var (projects, samples, plugins) = (ProjectFolders.ToList(), SampleFolders.ToList(), PluginFolders.ToList());
            return Task.Run(() => Core.LiveTests.SelfCheck.RunAsync(new SelfCheckOptions
            {
                ReportFolder = folder, DataFolder = main.DataFolder, Projects = _db.Projects().Select(p => p.Path).ToList(),
                ProjectFolders = projects, SampleFolders = samples, PluginFolders = plugins,
                PluginFoldersAreDefaults = _db.GetList(Scanner.PluginFoldersKey).Count == 0,
                Sounds = main.SoundLibrary, ScannerExecutable = main.PluginScanner.Executable, AppChecks = main.AppChecks,
            }, progress, ct), ct);
        }) { Answering = true };
        var flPort = new FlPortSetting(db);
        SoundOutput = new SoundOutputSetting(db, Ui.Current.SoundOutputs);
        Links =
        [
            // Into the chosen user folder, each one found in Documents, and the Extensions folder Bitwig loaded it from.
            new DawLinkSetting("Bitwig Studio", () => BitwigSteps,
                               () => DawScripts.BitwigLink(DawScripts.BitwigLinkFolders(db.GetSetting(BitwigUserKey)),
                                                           loadedFrom: db.GetSetting(BitwigLoadsFromKey)),
                               () => CheckBitwig(watch), new FolderChoice(
                "Choose Bitwig's user folder (the Bitwig Studio folder that holds Extensions, Library and Projects; Bitwig shows it in Settings > Locations)",
                DawScripts.BitwigUserFolderFrom,
                "That isn't Bitwig's user folder or a folder in it. It's the Bitwig Studio folder that holds Extensions, Library and " +
                "Projects, and Bitwig shows where it is in Settings > Locations.",
                f => db.SetSetting(BitwigUserKey, f),
                "From now on the app looks for Bitwig projects there. Install puts the link there too, as well as in each Bitwig Studio " +
                "folder in Documents and where Bitwig was seen loading it from, as Bitwig may load extensions from another folder."),
                liveTests: liveTests),
            new DawLinkSetting("FL Studio", () => FlSteps(flPort.Port), () => FlScript(db), () => CheckFlStudio(watch), new FolderChoice(
                "Choose FL Studio's user data folder (the one with Settings, Presets and Projects in it)",
                DawScripts.FlUserDataFrom,
                "That isn't FL Studio's user data folder: it has Settings, Presets and Projects folders in it. " +
                "FL Studio shows where it is in Options > File settings.",
                f => db.SetSetting(FlUserDataKey, f)), flPort),
        ];
    }

    /// <summary>
    /// One button for both DAWs: installs each one's script if the DAW is on this computer, makes FL Studio's MIDI port
    /// the way chosen, then checks each link. Each DAW says below what happened and what's left to do.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSetUp))]
    async Task SetUpBoth()
    {
        SettingUp = true;
        SetUpResult = "Setting up…";
        try
        {
            var lines = new List<string>();
            foreach (var link in Links) lines.Add(await link.SetUpAsync());
            SetUpResult = string.Join(Environment.NewLine, lines);
        }
        finally { SettingUp = false; }
    }

    bool CanSetUp() => !SettingUp;

    /// <summary>Shows whether each DAW answers right now, from the app's last look.</summary>
    public void ShowLive(LiveDawsViewModel live)
    {
        foreach (var link in Links) link.ShowLive(live.State(link.Daw));
        // Where Bitwig loads the extension from, for Install to put the next version there.
        if (DawScripts.BitwigExtensionsFolder(live.State(DawState.Bitwig)?.LoadedFrom) is { } folder &&
            !string.Equals(folder, _bitwigLoadsFrom ??= _db.GetSetting(BitwigLoadsFromKey) ?? "", StringComparison.OrdinalIgnoreCase))
            _db.SetSetting(BitwigLoadsFromKey, _bitwigLoadsFrom = folder);
    }

    /// <summary>
    /// What a live test run from here uses: the app's own way to Bitwig, MO Probe's report folder and the plugin folders
    /// as Settings has them, Bitwig's user folder as chosen, the library's Bitwig projects (those with a Drum Machine
    /// first, where the run looks for its ID) and what the app already knows: MO Probe's IDs as the plugin scanner read
    /// them, and the Drum Machine's ID once Sound Lab has found it.
    /// </summary>
    public BitwigLiveOptions LiveTestOptions()
    {
        var plugins = PluginFolders.Concat(MoProbe.InstallFolders()).Distinct().ToList();
        Guid? drumMachine = Guid.TryParse(_db.GetSetting(KitViewModel.DrumMachineKey), out var known) ? known : null;
        return new BitwigLiveOptions
        {
            Connect = _main.Live.Watch.ConnectBitwigAsync,
            ReportFolder = _liveReports,
            ProbeFolder = Probe.Folder,
            PluginFolders = () => plugins,
            ScannedProbes = _db.ScannedPluginsNamed(BitwigLiveTests.ProbeName),
            BitwigUserFolders = () => DawScripts.BitwigLinkFolders(_db.GetSetting(BitwigUserKey)),
            BringBitwigToFront = () => DawWindows.BringToFront("Bitwig"),
            LibraryProjects = () =>
            {
                var drums = _db.ProjectsUsingPlugin(BitwigSend.DrumMachine).ToHashSet();
                return _db.Projects().Where(p => p.Daw == BitwigLiveTests.Daw && !p.FileMissing)
                          .OrderByDescending(p => drums.Contains(p.Id)).Select(p => p.Path).ToList();
            },
            DrumMachine = drumMachine is { } id ? _ => id : null,
        };
    }

    static string BitwigSteps =>
        "Then open Settings > Controllers in Bitwig, click Add controller and pick Music Organiser > DAW link. " +
        "If it isn't in the list, restart Bitwig. (When the app has its Bitwig extension, Install puts that in place of " +
        "the script: it's the same controller, so one that's set up already stays set up once Bitwig restarts.) " +
        $"Then set that controller's MIDI in and MIDI out to the MIDI port “{BitwigLink.LoopbackPort}”" +
        (OperatingSystem.IsWindows() ? " (make it in loopMIDI first)" : "") +
        ", never to “Music Organiser”, FL Studio's: Bitwig only saves for the app once they're set." +
        (OperatingSystem.IsWindows() ? " If Windows asks whether Bitwig may use the network, you can say no: the app only talks to it on this PC." : "");

    static string FlSteps(FlMidiPort port) =>
        !OperatingSystem.IsWindows()
            ? "The app can't talk to FL Studio's script on this computer yet: they talk over MIDI, which the app only does on Windows so far."
            : "FL Studio only runs scripts for MIDI inputs, and the app and the script talk to each other over that port. " +
              (port == FlMidiPort.WindowsMidi
                  ? "Install also makes a port called Music Organiser with Windows' own MIDI system. Then restart FL Studio, and it " +
                    "should pick the script for that port by itself. "
                  : "Make a port called Music Organiser in loopMIDI (free), then restart FL Studio, and it should pick the script for " +
                    "that port by itself. ") +
              "In Options > MIDI settings, check that the Music Organiser input's Controller type is Music Organiser, and give the " +
              "Music Organiser output the same Port number as the input, so the script can answer.";

    /// <summary>The FL Studio script, for every FL Studio user data folder: chosen here, in Documents, or holding FL projects.</summary>
    static DawScript FlScript(LibraryDb db) => DawScripts.FlStudio(DawScripts.FlUserDataFolders(
        db.Projects().Where(p => p.Daw == "FL Studio").Select(p => p.Path), db.GetSetting(FlUserDataKey)));

    static async Task<string> CheckBitwig(DawWatch watch)
    {
        using var link = await watch.ConnectBitwigAsync();
        return $"Connected to {link.Hello.Product} {link.Hello.Version} through {link.Hello.Answering}, with “{link.Hello.Project}” open." +
               (BitwigLink.LoopbackMissing(link.Hello) is { } setUp ? " " + setUp : "");
    }

    static async Task<string> CheckFlStudio(DawWatch watch)
    {
        var fl = watch.Fl();
        fl.Timeout = TimeSpan.FromSeconds(3);
        var hello = await fl.HelloAsync();
        return $"Connected to FL Studio {hello.Version}. Its window says “{hello.Title}”." +
               (hello.Api < 26 ? " This FL Studio is too old to check projects with: that needs scripting version 26 or later." : "");
    }

    /// <summary>Shows whether each DAW's script is installed. Settings calls it each time it opens.</summary>
    public async Task RefreshLinksAsync()
    {
        foreach (var link in Links) await link.RefreshAsync();
        await SelfCheck.RefreshAsync();
    }

    /// <summary>
    /// The app checking itself with the real library (every project opens, the sound library's speed, the plugin scanner,
    /// sound output, graphics), with a report in reports\checks for Claude to read.
    /// </summary>
    public LiveTestsViewModel SelfCheck { get; }

    void UpdateSampleIndexText() => SampleIndexText = $"{_db.SampleIndexCount():N0} audio files indexed";

    static Task<string?> PickFolder(string title) => Ui.Current.PickFolder(title);

    [RelayCommand(AllowConcurrentExecutions = true)]   // usable again at once: a scan asked for mid-scan is queued
    async Task AddProjectFolder()
    {
        if (await PickFolder("Choose a folder with music projects") is not { } f || ProjectFolders.Contains(f)) return;
        _db.AddRoot(f);
        ProjectFolders.Add(f);
        _main.WatchProjectFolders();
        await _main.ScanAsync(false);
    }

    [RelayCommand]
    void RemoveProjectFolder()
    {
        if (SelectedProjectFolder is not { } f) return;
        _db.RemoveRoot(f);
        ProjectFolders.Remove(f);
        _main.WatchProjectFolders();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]   // usable again at once: a scan asked for mid-scan is queued
    async Task AddSampleFolder()
    {
        if (await PickFolder("Choose a sample library folder") is not { } f || SampleFolders.Contains(f)) return;
        SampleFolders.Add(f);
        _db.SetList(Scanner.SampleFoldersKey, SampleFolders);
        _main.Sounds.WatchSampleFolders();
        await IndexSamples();
    }

    [RelayCommand]
    void RemoveSampleFolder()
    {
        if (SelectedSampleFolder is not { } f) return;
        SampleFolders.Remove(f);
        _db.SetList(Scanner.SampleFoldersKey, SampleFolders);
        _main.Sounds.WatchSampleFolders();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]   // usable again at once: a scan asked for mid-scan is queued
    async Task IndexSamples()
    {
        await _main.ScanAsync(false, plugins: false, samples: true);
        UpdateSampleIndexText();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]   // usable again at once: a scan asked for mid-scan is queued
    async Task AddPluginFolder()
    {
        if (await PickFolder("Choose a plugin folder (VST3, CLAP or VST2)") is not { } f || PluginFolders.Contains(f)) return;
        PluginFolders.Add(f);
        _db.SetList(Scanner.PluginFoldersKey, PluginFolders);
        await _main.ScanAsync(false);
    }

    [RelayCommand]
    void RemovePluginFolder()
    {
        if (SelectedPluginFolder is not { } f) return;
        PluginFolders.Remove(f);
        _db.SetList(Scanner.PluginFoldersKey, PluginFolders);
    }

    /// <summary>
    /// Asks for the Bitwig project FL Studio projects are converted with and keeps a copy of it (see
    /// <see cref="BitwigTemplateStore"/>). Returns the app's copy, or why that project can't be the template.
    /// </summary>
    public static async Task<(string? Template, string? Problem)> PickBitwigTemplate(LibraryDb db, string dataFolder)
    {
        if (await Ui.Current.PickFile("Choose the Bitwig template (Sampler track, VST3 synth with Note Transpose and a clip, VST2 synth, audio track with a clip)",
                "Bitwig projects", "bwproject") is not { } file) return (null, null);
        try
        {
            var raw = await File.ReadAllBytesAsync(file);
            if (Core.Export.BitwigProject.TemplateProblem(raw) is { } problem) return (null, $"{Path.GetFileNameWithoutExtension(file)} can't be the template. {problem}.");
            return (BitwigTemplateStore.Keep(db, file, dataFolder), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, $"Couldn't copy {Path.GetFileName(file)}: {e.Message}");
        }
    }

    /// <summary>Under the template's path: what's missing from it, or where it was copied from.</summary>
    string TemplateNote(string template)
    {
        if (Problem(template) is { } problem)
            return problem.Contains(" needs ", StringComparison.Ordinal)
                ? $"{problem}. Add it with Edit in Bitwig, or Replace the template with another project."
                : $"{problem}. Replace it with another project.";
        return template.Length > 0 && _db.GetSetting(ProjectDetailViewModel.BitwigTemplateKey) is { Length: > 0 } from && from != template
            ? $"The app's own copy, taken from {from}."
            : "";
    }

    /// <summary>Why the template can't be used, or null when it can (or there's none).</summary>
    static string? Problem(string template)
    {
        if (template.Length == 0) return null;
        try { return Core.Export.BitwigProject.TemplateProblem(File.ReadAllBytes(template)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"Couldn't read it: {e.Message}"; }
    }

    partial void OnStartPageChanged(string value) => _db.SetSetting(StartPageKey, value);

    partial void OnStepsUntilNextNoteChanged(bool value) =>
        _db.SetSetting(ProjectDetailViewModel.StepsUntilNextNoteKey, value ? "1" : "0");

    partial void OnUpgradeSynthsChanged(bool value) =>
        _db.SetSetting(ProjectDetailViewModel.UpgradeSynthsKey, value ? "1" : "0");

    [RelayCommand]
    async Task ChooseBitwigTemplate()
    {
        var (template, problem) = await PickBitwigTemplate(_db, _main.DataFolder);
        if (template is not null) TemplateChanged(template);
        else if (problem is not null) BitwigTemplateNote = problem;
    }

    /// <summary>Shows the template the app now uses (after Replace, or the first conversion asked for one).</summary>
    public void TemplateChanged(string template)
    {
        BitwigTemplate = template;
        BitwigTemplateNote = TemplateNote(template);
    }

    /// <summary>Looks at the template again when Settings is shown, since it may have been changed in Bitwig.</summary>
    public void RefreshTemplate() => TemplateChanged(BitwigTemplateStore.Find(_db, _main.DataFolder) ?? "");

    bool HasBitwigTemplate() => File.Exists(BitwigTemplate);

    /// <summary>Opens the app's copy of the template in Bitwig, to change it there and save.</summary>
    [RelayCommand(CanExecute = nameof(HasBitwigTemplate))]
    void EditBitwigTemplate()
    {
        try
        {
            ProjectDetailViewModel.OpenWithBitwig(_db, BitwigTemplate);
            // What's missing stays in view while it's added; it's checked again when the app is back in front.
            BitwigTemplateNote = Problem(BitwigTemplate) is { } problem
                ? $"{problem}. Opening the template in Bitwig: add that there and save it."
                : "Opening the template in Bitwig. Change it there and save it, and the next conversion uses it.";
        }
        catch (Exception e) { BitwigTemplateNote = $"Couldn't open it in Bitwig: {e.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(HasBitwigTemplate))]
    void ShowBitwigTemplate() => Shell.Reveal(BitwigTemplate);

    [RelayCommand]
    async Task ChooseExportFolder()
    {
        if (await PickFolder("Where should exported MIDI and presets go?") is not { } f) return;
        ExportFolder = f;
        _db.SetSetting(ProjectDetailViewModel.ExportFolderKey, f);
    }
}

/// <summary>Optional: a specific DAW program to open projects with, instead of the Windows default.</summary>
public sealed partial class DawSetting : ObservableObject
{
    readonly LibraryDb _db;
    public string Daw { get; }
    public string Extensions { get; }
    [ObservableProperty] string _exePath;

    public DawSetting(LibraryDb db, string daw, string extensions)
    {
        _db = db;
        Daw = daw;
        Extensions = extensions;
        _exePath = db.GetSetting(ProjectDetailViewModel.DawExeKey(daw)) ?? "";
    }

    [RelayCommand]
    async Task Choose()
    {
        if (await Ui.Current.PickProgram($"Choose the {Daw} program") is not { } program) return;
        ExePath = program;
        _db.SetSetting(ProjectDetailViewModel.DawExeKey(Daw), ExePath);
    }

    [RelayCommand]
    void Clear()
    {
        ExePath = "";
        _db.SetSetting(ProjectDetailViewModel.DawExeKey(Daw), "");
    }
}

/// <summary>Where a DAW keeps its files, when the app has to ask.</summary>
/// <param name="Prompt">What the folder picker asks for.</param>
/// <param name="Find">The folder to keep, from the one picked: it, or the right one it's in. Null if it's neither.</param>
/// <param name="Wrong">What to say when it isn't.</param>
/// <param name="Save">Remembers the folder.</param>
/// <param name="Chosen">What to say once a folder is chosen.</param>
public sealed record FolderChoice(string Prompt, Func<string, string?> Find, string Wrong, Action<string> Save,
                                  string Chosen = "Install puts the script there too.");

/// <summary>
/// How the MIDI port FL Studio runs the script for gets made, on Windows: by hand in loopMIDI, a free app, or by
/// Install, with Windows' own MIDI system.
/// </summary>
public sealed partial class FlPortSetting : ObservableObject
{
    public const string Key = "fl_midi_port";
    public const string MakeInLoopMidi = "loopMIDI is installed: make a port called Music Organiser in it, by typing the name " +
                                         "under New port-name and pressing +.";
    public const string GetLoopMidiFirst = "loopMIDI isn't installed. Get it with the Get loopMIDI button, then make a port " +
                                           "called Music Organiser in it.";
    public const string Checking = "Checking whether this PC can make the port…";
    public const string Making = "Making the port with Windows' MIDI system…";

    readonly LibraryDb _db;

    /// <summary>The choice, as an index for the radio buttons: 0 loopMIDI, 1 Windows' MIDI system.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Port))] int _choice;
    /// <summary>Where the port stands: whether there's one, or what it takes.</summary>
    [ObservableProperty] string _note = "";
    /// <summary>Whether loopMIDI is the choice, but there's neither loopMIDI nor a port.</summary>
    [ObservableProperty] bool _needsLoopMidi;

    public FlPortSetting(LibraryDb db)
    {
        _db = db;
        _choice = db.GetSetting(Key) == "windows" ? 1 : 0;
        FlSetup.Port = Port;
    }

    public FlMidiPort Port => Choice == 1 ? FlMidiPort.WindowsMidi : FlMidiPort.LoopMidi;

    partial void OnChoiceChanged(int value)
    {
        _db.SetSetting(Key, value == 1 ? "windows" : "loopmidi");
        FlSetup.Port = Port;
        _ = RefreshAsync();
    }

    /// <summary>Looks at where the port stands, without changing anything. Settings calls it each time it opens.</summary>
    public Task RefreshAsync() => UpdateAsync(make: false);

    /// <summary>After Install: makes the port with Windows' MIDI system, when that's the choice, and says where it stands.</summary>
    public Task SetUpAsync() => UpdateAsync(make: true);

    async Task UpdateAsync(bool make)
    {
        if (!OperatingSystem.IsWindows()) return;
        var port = Port;
        try
        {
            if (port == FlMidiPort.WindowsMidi)
            {
                NeedsLoopMidi = false;
                Note = make ? Making : Checking;
                var result = make ? await WindowsMidiPort.MakeAsync() : await WindowsMidiPort.CheckAsync();
                if (Port == port) Note = result.Message;   // unless the choice changed meanwhile
            }
            else
            {
                var there = await Task.Run(() => FlSetup.MidiInputs() is { } inputs && FlSetup.HasOurPort(inputs));
                var installed = there || await Task.Run(FlSetup.IsLoopMidiInstalled) == true;
                if (Port != port) return;
                NeedsLoopMidi = !installed;
                Note = there ? FlSetup.PortThere : installed ? MakeInLoopMidi : GetLoopMidiFirst;
            }
        }
        catch (Exception e)
        {
            if (Port == port) Note = $"Couldn't look at this PC's MIDI ports: {e.Message}";
        }
    }

    [RelayCommand]
    void GetLoopMidi() => Shell.OpenUrl(FlSetup.LoopMidiUrl);
}

/// <summary>The link with one DAW: its script, installed into the DAW's own folder, and whether the DAW answers.</summary>
public sealed partial class DawLinkSetting : ObservableObject
{
    readonly Func<string> _steps;
    readonly Func<DawScript> _script;
    readonly Func<Task<string>> _check;
    readonly Func<bool> _found;
    readonly FolderChoice? _folder;
    /// <summary>The extension version Install copies, from the last look (null for a script).</summary>
    string? _shipped;
    /// <summary>Whether every folder had the app's own copy at the last look.</summary>
    bool _current;
    DawState? _state;

    public string Daw { get; }
    /// <summary>What to do in the DAW once the script is installed.</summary>
    public string Steps => _steps();
    public bool CanChooseFolder => _folder is not null;
    /// <summary>What Choose folder… is for, which differs by DAW.</summary>
    public string? ChooseFolderTip => _folder?.Prompt;
    /// <summary>For FL Studio: how the MIDI port it runs the script for is made.</summary>
    public FlPortSetting? MidiPort { get; }
    /// <summary>Whether to offer the choice of how the port is made: for FL Studio, on Windows.</summary>
    public bool CanChoosePort => MidiPort is not null && OperatingSystem.IsWindows();
    public bool ShowGetLoopMidi => CanChoosePort && MidiPort!.NeedsLoopMidi;
    /// <summary>For Bitwig: the live tests, which run through the link while Bitwig answers.</summary>
    public LiveTestsViewModel? LiveTests { get; }
    public bool HasLiveTests => LiveTests is not null;
    /// <summary>Whether the script is installed, and where.</summary>
    [ObservableProperty] string _status = "";
    /// <summary>What happened when installing or checking.</summary>
    [ObservableProperty] string _result = "";
    /// <summary>Whether the DAW answers right now, in words, from the app's last look; empty before it has looked.</summary>
    [ObservableProperty] string _live = "";
    [ObservableProperty] bool _isAnswering;

    /// <param name="found">Whether the DAW is on this computer, for Set up both: unless given, whether the app found a folder for its script.</param>
    public DawLinkSetting(string daw, Func<string> steps, Func<DawScript> script, Func<Task<string>> check,
                          FolderChoice? folder = null, FlPortSetting? midiPort = null, Func<bool>? found = null,
                          LiveTestsViewModel? liveTests = null)
    {
        Daw = daw;
        _steps = steps;
        _script = script;
        _check = check;
        _found = found ?? (() => script().Folders.Count > 0);
        _folder = folder;
        MidiPort = midiPort;
        LiveTests = liveTests;
        if (midiPort is not null)
            midiPort.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FlPortSetting.Port)) OnPropertyChanged(nameof(Steps));
                if (e.PropertyName == nameof(FlPortSetting.NeedsLoopMidi)) OnPropertyChanged(nameof(ShowGetLoopMidi));
            };
    }

    public async Task RefreshAsync()
    {
        try
        {
            Status = await Task.Run(() =>
            {
                var script = _script();
                _shipped = script.Version;
                _current = script.State == ScriptState.Installed;
                return Describe(script);
            });
            ShowLive(_state);
        }
        catch (Exception e)
        {
            Status = $"Couldn't look for the script: {e.Message}";
        }
        if (MidiPort is not null) await MidiPort.RefreshAsync();
        if (LiveTests is not null) await LiveTests.RefreshAsync();
    }

    string Describe(DawScript script)
    {
        var copies = script.Version is { } v ? $"extension {v}" : null;
        var others = script.InstalledVersions.Where(i => i.Length > 0 && i != script.Version).Distinct().ToList();
        var newer = others.Where(i => Newer(i, script.Version)).ToList();
        var older = others.Except(newer).ToList();
        var state = script.State switch
        {
            ScriptState.NotInstalled when script.Folders.Count == 0 =>
                $"Not installed, and the app didn't find {Daw}'s folder for it. Install asks where it is.",
            ScriptState.NotInstalled => "Not installed." + (copies is null ? "" : $" Install puts in {copies}."),
            // From a newer build of the app (another branch, say): Install would go back to this app's own.
            ScriptState.Outdated when newer.Count > 0 =>
                $"Installed, and newer ({string.Join(", ", newer)}) than the {copies} this app has: Install would put that older one back.",
            ScriptState.Outdated => "Installed, but an older version" + (older.Count > 0 ? $" ({string.Join(", ", older)})" : "") +
                                    (copies is null ? ". Install it again to update it." : $". Install it again to put in {copies}."),
            _ => $"Installed in {string.Join(" and ", script.InstalledIn)}" + (copies is null ? "." : $" ({copies})."),
        };
        if (Folders(script.Leftovers) is { Length: > 0 } leftovers)
            state += $" There's an old copy in {leftovers} too, which Install removes so {Daw} can't load two.";
        // Running the app with hot reload (as the Music Organiser (latest) shortcut does) doesn't build the extension again.
        return script.ChangedSinceBuilt is { } changed
            ? state + $" The extension the app has was built before its code last changed ({Path.GetFileName(changed)}), so Install would " +
              "put in an old one: close the app and start it again so it's built anew. If this stays, building it failed, and " +
              $"{Path.Combine(script.Code!, "build.log")} says why."
            : state;
    }

    /// <summary>Whether extension version <paramref name="a"/> is newer than <paramref name="b"/> (3.10 is newer than 3.9).</summary>
    internal static bool Newer(string? a, string? b) =>
        Version.TryParse(a, out var x) && Version.TryParse(b, out var y) && x > y;

    [RelayCommand]
    async Task Install()
    {
        try
        {
            var script = await Task.Run(_script);
            if (script.Folders.Count == 0)
            {
                if (await ChooseFolderAsync() is null) return;
                script = await Task.Run(_script);
            }
            var update = script.State != ScriptState.NotInstalled;
            var removed = await Task.Run(script.Install);
            Result = (update ? $"Updated. Restart {Daw} to load the new version." : $"Installed. Now set it up in {Daw} as described above.") +
                     Removed(removed);
            // The port shows how it stands with the choice above it.
            if (MidiPort is not null) await MidiPort.SetUpAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Result = $"Couldn't install it: {e.Message}";
        }
        await RefreshAsync();
    }

    /// <summary>The folders these files are in, for a sentence.</summary>
    static string Folders(IEnumerable<string> files) => string.Join(" and ", files.Select(Path.GetDirectoryName).Distinct());

    /// <summary>What Install removed, as a sentence to add, or "".</summary>
    string Removed(IReadOnlyList<string> removed) =>
        removed.Count == 0 ? "" : $" Removed the old {(removed.Count == 1 ? "copy" : "copies")} in {Folders(removed)}, so {Daw} can't load two.";

    [RelayCommand]
    async Task ChooseFolder()
    {
        if (await ChooseFolderAsync() is { } folder)
        {
            await RefreshAsync();
            Result = $"Using {folder}. {_folder!.Chosen}";
        }
    }

    /// <summary>Asks for the DAW's folder and keeps it. Returns the folder kept, or null.</summary>
    async Task<string?> ChooseFolderAsync()
    {
        if (_folder is null || await Ui.Current.PickFolder(_folder.Prompt) is not { } picked) return null;
        if (_folder.Find(picked) is not { } folder)
        {
            Result = _folder.Wrong;
            return null;
        }
        _folder.Save(folder);
        return folder;
    }

    /// <summary>
    /// For Set up both: installs or updates the script if the DAW is on this computer (or skips it), makes FL Studio's
    /// MIDI port the way chosen, then checks the link. Says what happened in <see cref="Result"/>, and returns a line
    /// for the summary.
    /// </summary>
    public async Task<string> SetUpAsync()
    {
        if (!await Task.Run(_found))
        {
            Result = $"Skipped: the app didn't find {Daw} on this computer." +
                     (_folder is not null ? " If it's here, use Choose folder… to show the app where it keeps its files." : "");
            return $"{Daw}: not found on this computer, so it was skipped.";
        }
        string done;
        var what = "the script";   // or Bitwig's extension, when the app was built with it
        try
        {
            var (script, state, leftovers) = await Task.Run(() => { var s = _script(); return (s, s.State, s.Leftovers.Count > 0); });
            if (script.Binary) what = "the extension";
            IReadOnlyList<string> removed = state != ScriptState.Installed || leftovers ? await Task.Run(script.Install) : [];
            done = state switch
            {
                ScriptState.NotInstalled => $"Installed {what}.",
                ScriptState.Outdated => $"Updated {what}: restart {Daw} to load the new version.",
                _ => $"{char.ToUpperInvariant(what[0])}{what[1..]} was already installed.",
            } + Removed(removed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Result = $"Couldn't install {what}: {e.Message}";
            await RefreshAsync();
            return $"{Daw}: couldn't install {what}, as it says below.";
        }
        if (MidiPort is not null) await MidiPort.SetUpAsync();
        Result = $"{done} Asking {Daw}…";
        var answered = false;
        string answer;
        try
        {
            answer = await _check();
            answered = true;
        }
        catch (DawLinkException e) { answer = e.Message; }
        catch (Exception e) { answer = $"Couldn't reach {Daw}: {e.Message}"; }
        Result = $"{done} {answer}";
        await RefreshAsync();
        return answered ? $"✓ {Daw}: set up and answering." : $"{Daw}: {what} is in place, but {Daw} isn't answering yet. What's left is below.";
    }

    partial void OnIsAnsweringChanged(bool value)
    {
        if (LiveTests is not null) LiveTests.Answering = value;
    }

    public void ShowLive(DawState? state)
    {
        if (state is null) return;
        _state = state;
        IsAnswering = state.Answering;
        Live = state.Answering
            ? $"✓ Answering now: {state.Version}" + (state.Project.Length > 0 ? $", with “{state.Project}” open." : ".") +
              (state.Daw == DawState.Bitwig && !state.Changes
                  ? " To send things to Bitwig, turn on “Let the app make changes” in its controller settings." : "") +
              // Only an Extensions folder, which Install puts the next version in: not a copy Bitwig made somewhere of its own (Temp, say).
              (DawScripts.BitwigExtensionsFolder(state.LoadedFrom) is { } folder ? $" Bitwig loads the extension from {folder}." : "") +
              (state.Extension is { } running && _shipped is { } shipped && running != shipped ? Running(running, shipped) : "")
            : state.Running == true ? $"Not answering right now, though {Daw} is open. Check connection says what's missing."
            : "Not answering right now.";
    }

    /// <summary>What to do when Bitwig runs another extension than the one Install puts in.</summary>
    string Running(string running, string shipped) =>
        Newer(running, shipped) ? $" Bitwig is running extension {running}, newer than the {shipped} this app has."
        // Installed, yet Bitwig runs another: it hasn't restarted since, or it loads extensions from another user folder.
        : _current ? $" Bitwig is running extension {running}, but {shipped} is installed: restart Bitwig to load it. If it still runs " +
                     $"{running} after that, Bitwig loads extensions from another folder: use Choose folder… to pick the one Bitwig " +
                     "shows in Settings > Locations."
        // Windows won't replace an extension Bitwig has open.
        : $" Bitwig is running extension {running}, and Install puts in {shipped}: close Bitwig, press Install, then open Bitwig again.";

    /// <summary>Asks the DAW to answer, through the script.</summary>
    [RelayCommand]
    async Task Check()
    {
        Result = $"Asking {Daw}…";
        try
        {
            Result = await _check();
        }
        catch (DawLinkException e) { Result = e.Message; }
        catch (Exception e) { Result = $"Couldn't reach {Daw}: {e.Message}"; }
    }
}
