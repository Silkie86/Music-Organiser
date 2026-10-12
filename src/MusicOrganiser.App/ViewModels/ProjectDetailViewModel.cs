using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.App.ViewModels;

/// <summary>The right-hand panel for the selected project.</summary>
public sealed partial class ProjectDetailViewModel : ObservableObject
{
    public const string ExportFolderKey = "export_folder";
    public static string DawExeKey(string daw) => $"daw_exe:{daw}";

    readonly LibraryDb _db;
    readonly MainViewModel _main;

    public ProjectItem Item { get; }
    public ProjectDetail? Detail { get; }
    public IReadOnlyList<PluginRow> Plugins { get; }
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
    public string MidiNote => "See each channel's notes and what plays them, and export them as MIDI" +
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
    }

    string ExportRoot => _db.GetSetting(ExportFolderKey) is { Length: > 0 } f
        ? f
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Music Organiser Exports");

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
    void CopyPath() => Clipboard.SetText(Item.Path);

    /// <summary>Opens the MIDI popup: each channel's notes, what plays them, and export.</summary>
    [RelayCommand]
    void ShowMidi()
    {
        if (Readers.For(Item.Path) is not { } reader) return;
        var window = new Views.MidiWindow(new MidiPreviewViewModel(reader, Item.Path, Item.Name, ExportRoot, Item.Tempo))
        {
            Owner = Application.Current.MainWindow,
        };
        window.Show();
    }

    [RelayCommand]
    async Task ExportPresets()
    {
        var reader = Readers.For(Item.Path);
        if (reader is null) return;
        Message = "Exporting presets…";
        try
        {
            var result = await Task.Run(() => Exporter.Presets(reader, Item.Path, ExportRoot));
            Message = result.Files.Count == 0 ? "No plugin states found to export." : $"Exported {result.Files.Count} presets.";
            if (result.Files.Count > 0) Shell.Open(result.Folder);
        }
        catch (Exception e) { Message = $"Preset export failed: {e.Message}"; }
    }

    [RelayCommand]
    Task CheckSharing() => _main.CheckSharing(Item.Path);

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
    /// <summary>Audio Units only load on a Mac, so these come from a project saved on one.</summary>
    public bool IsAu => p.Format == "AU";
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
    public void Recover(string candidate)
    {
        var target = System.IO.Path.Combine(project.Folder, FileName);
        if (File.Exists(target))
        {
            MatchMessage = "A file with that name is already in the project folder.";
            return;
        }
        var ok = MessageBox.Show($"Copy\n{candidate}\n\ninto the project folder as\n{FileName}?",
            "Recover sample", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
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
    void Recover() => sample?.Recover(m.Candidate);
}
