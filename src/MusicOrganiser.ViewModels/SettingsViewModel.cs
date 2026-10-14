using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.ViewModels;

/// <summary>Where your projects, samples and plugins live, and how projects are opened.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>FL Studio's user data folder, when it was chosen here.</summary>
    public const string FlUserDataKey = "fl_user_data";

    readonly LibraryDb _db;
    readonly MainViewModel _main;

    public ObservableCollection<string> ProjectFolders { get; } = new();
    public ObservableCollection<string> SampleFolders { get; } = new();
    public ObservableCollection<string> PluginFolders { get; } = new();
    public ObservableCollection<DawSetting> Daws { get; } = new();

    [ObservableProperty] string? _selectedProjectFolder;
    [ObservableProperty] string? _selectedSampleFolder;
    [ObservableProperty] string? _selectedPluginFolder;
    [ObservableProperty] string _exportFolder = "";
    [ObservableProperty] string _bitwigTemplate = "";
    [ObservableProperty] string _sampleIndexText = "";
    public string LibraryLocation => _db.FilePath;

    /// <summary>The scripts that let the app ask Bitwig and FL Studio what they show.</summary>
    public IReadOnlyList<DawLinkSetting> Links { get; }

    public SettingsViewModel(LibraryDb db, MainViewModel main)
    {
        _db = db;
        _main = main;
        foreach (var r in db.Roots()) ProjectFolders.Add(r);
        foreach (var f in db.GetList(Scanner.SampleFoldersKey)) SampleFolders.Add(f);
        var plugins = db.GetList(Scanner.PluginFoldersKey);
        foreach (var f in plugins.Count > 0 ? plugins : PluginCatalog.DefaultFolders()) PluginFolders.Add(f);
        foreach (var r in Readers.All)
            Daws.Add(new DawSetting(db, r.Daw, string.Join(", ", r.Extensions)));
        _exportFolder = db.GetSetting(ProjectDetailViewModel.ExportFolderKey) ?? "";
        _bitwigTemplate = db.GetSetting(ProjectDetailViewModel.BitwigTemplateKey) ?? "";
        UpdateSampleIndexText();
        Links =
        [
            new DawLinkSetting("Bitwig Studio", BitwigSteps, () => DawScripts.Bitwig(), CheckBitwig),
            new DawLinkSetting("FL Studio", FlSteps, () => FlScript(db), CheckFlStudio, new FolderChoice(
                "Choose FL Studio's user data folder (the one with Settings, Presets and Projects in it)",
                DawScripts.IsFlUserData,
                "That isn't FL Studio's user data folder: it has Settings, Presets and Projects folders in it. " +
                "FL Studio shows where it is in Options > File settings.",
                f => db.SetSetting(FlUserDataKey, f))),
        ];
    }

    static string BitwigSteps =>
        "Then open Settings > Controllers in Bitwig, click Add controller and pick Music Organiser > DAW link. " +
        "If it isn't in the list, restart Bitwig." +
        (OperatingSystem.IsWindows() ? " If Windows asks whether Bitwig may use the network, you can say no: the app only talks to it on this PC." : "");

    static string FlSteps =>
        "Then restart FL Studio. FL Studio runs a script for a MIDI input, so in Options > MIDI settings, pick an input and " +
        "set its Controller type to Music Organiser. " +
        (OperatingSystem.IsMacOS()
            ? "With no input to spare, turn on the IAC Driver in Audio MIDI Setup and add a port called Music Organiser."
            : "With no input to spare, make one called Music Organiser in loopMIDI (free); FL Studio should then pick the script for it by itself.");

    /// <summary>The FL Studio script, for every FL Studio user data folder: chosen here, in Documents, or holding FL projects.</summary>
    static DawScript FlScript(LibraryDb db) => DawScripts.FlStudio(DawScripts.FlUserDataFolders(
        db.Projects().Where(p => p.Daw == "FL Studio").Select(p => p.Path), db.GetSetting(FlUserDataKey)));

    static async Task<string> CheckBitwig()
    {
        using var link = await BitwigLink.ConnectAsync();
        return $"Connected to {link.Hello.Product} {link.Hello.Version}, with “{link.Hello.Project}” open.";
    }

    static async Task<string> CheckFlStudio()
    {
        var hello = await new FlLink { Timeout = TimeSpan.FromSeconds(3) }.HelloAsync();
        return $"Connected to FL Studio {hello.Version}. Its window says “{hello.Title}”." +
               (hello.Api < 26 ? " This FL Studio is too old to check projects with: that needs scripting version 26 or later." : "");
    }

    /// <summary>Shows whether each DAW's script is installed. Settings calls it each time it opens.</summary>
    public async Task RefreshLinksAsync()
    {
        foreach (var link in Links) await link.RefreshAsync();
    }

    void UpdateSampleIndexText() => SampleIndexText = $"{_db.SampleIndexCount():N0} audio files indexed";

    static Task<string?> PickFolder(string title) => Ui.Current.PickFolder(title);

    [RelayCommand]
    async Task AddProjectFolder()
    {
        if (await PickFolder("Choose a folder with music projects") is not { } f || ProjectFolders.Contains(f)) return;
        _db.AddRoot(f);
        ProjectFolders.Add(f);
        await _main.ScanAsync(false);
    }

    [RelayCommand]
    void RemoveProjectFolder()
    {
        if (SelectedProjectFolder is not { } f) return;
        _db.RemoveRoot(f);
        ProjectFolders.Remove(f);
    }

    [RelayCommand]
    async Task AddSampleFolder()
    {
        if (await PickFolder("Choose a sample library folder") is not { } f || SampleFolders.Contains(f)) return;
        SampleFolders.Add(f);
        _db.SetList(Scanner.SampleFoldersKey, SampleFolders);
        await IndexSamples();
    }

    [RelayCommand]
    void RemoveSampleFolder()
    {
        if (SelectedSampleFolder is not { } f) return;
        SampleFolders.Remove(f);
        _db.SetList(Scanner.SampleFoldersKey, SampleFolders);
    }

    [RelayCommand]
    async Task IndexSamples()
    {
        await _main.ScanAsync(false, plugins: false, samples: true);
        UpdateSampleIndexText();
    }

    [RelayCommand]
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

    /// <summary>Asks for the Bitwig project FL Studio projects are converted with, and remembers it.</summary>
    public static async Task<string?> PickBitwigTemplate(LibraryDb db)
    {
        if (await Ui.Current.PickFile("Choose the Bitwig template (Sampler track, VST3 synth with Note Transpose and a clip, VST2 synth)",
                "Bitwig projects", "bwproject") is not { } file) return null;
        db.SetSetting(ProjectDetailViewModel.BitwigTemplateKey, file);
        return file;
    }

    [RelayCommand]
    async Task ChooseBitwigTemplate()
    {
        if (await PickBitwigTemplate(_db) is { } f) BitwigTemplate = f;
    }

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
/// <param name="IsRight">Whether a folder is the right kind.</param>
/// <param name="Wrong">What to say when it isn't.</param>
/// <param name="Save">Remembers the folder.</param>
public sealed record FolderChoice(string Prompt, Func<string, bool> IsRight, string Wrong, Action<string> Save);

/// <summary>The link with one DAW: its script, installed into the DAW's own folder, and whether the DAW answers.</summary>
public sealed partial class DawLinkSetting : ObservableObject
{
    readonly Func<DawScript> _script;
    readonly Func<Task<string>> _check;
    readonly FolderChoice? _folder;

    public string Daw { get; }
    /// <summary>What to do in the DAW once the script is installed.</summary>
    public string Steps { get; }
    public bool CanChooseFolder => _folder is not null;
    /// <summary>Whether the script is installed, and where.</summary>
    [ObservableProperty] string _status = "";
    /// <summary>What happened when installing or checking.</summary>
    [ObservableProperty] string _result = "";

    public DawLinkSetting(string daw, string steps, Func<DawScript> script, Func<Task<string>> check, FolderChoice? folder = null)
    {
        Daw = daw;
        Steps = steps;
        _script = script;
        _check = check;
        _folder = folder;
    }

    public async Task RefreshAsync()
    {
        try
        {
            Status = await Task.Run(() => Describe(_script()));
        }
        catch (Exception e)
        {
            Status = $"Couldn't look for the script: {e.Message}";
        }
    }

    string Describe(DawScript script) => script.State switch
    {
        ScriptState.NotInstalled when script.Folders.Count == 0 =>
            $"Not installed, and the app didn't find {Daw}'s folder for it. Install asks where it is.",
        ScriptState.NotInstalled => "Not installed.",
        ScriptState.Outdated => "Installed, but an older version. Install it again to update it.",
        _ => $"Installed in {string.Join(" and ", script.InstalledIn)}.",
    };

    [RelayCommand]
    async Task Install()
    {
        try
        {
            var script = await Task.Run(_script);
            if (script.Folders.Count == 0)
            {
                if (!await ChooseFolderAsync()) return;
                script = await Task.Run(_script);
            }
            var update = script.State != ScriptState.NotInstalled;
            await Task.Run(script.Install);
            Result = update ? $"Updated. Restart {Daw} to load the new version." : $"Installed. Now set it up in {Daw} as described above.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Result = $"Couldn't install it: {e.Message}";
        }
        await RefreshAsync();
    }

    [RelayCommand]
    async Task ChooseFolder()
    {
        if (await ChooseFolderAsync())
        {
            await RefreshAsync();
            Result = "Install puts the script there too.";
        }
    }

    async Task<bool> ChooseFolderAsync()
    {
        if (_folder is null || await Ui.Current.PickFolder(_folder.Prompt) is not { } chosen) return false;
        if (!_folder.IsRight(chosen))
        {
            Result = _folder.Wrong;
            return false;
        }
        _folder.Save(chosen);
        return true;
    }

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
