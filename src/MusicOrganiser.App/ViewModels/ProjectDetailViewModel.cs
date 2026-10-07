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
    public bool CanExtractMidi => Detail?.CanExtractMidi == true && !Item.FileMissing;
    public bool CanExtractPresets => Detail?.CanExtractPresets == true && !Item.FileMissing;
    public string MidiNote => Item.Daw == "Studio One" ? "Experimental for Studio One" : "";
    public string PluginHeader => $"Plugins ({Plugins.Count}{(Item.MissingPlugins > 0 ? $", {Item.MissingPlugins} missing" : "")})";
    public string SampleHeader => $"Samples ({Samples.Count}{(Item.MissingSamples > 0 ? $", {Item.MissingSamples} missing" : "")})";

    [ObservableProperty] string _message = "";

    public ProjectDetailViewModel(LibraryDb db, ProjectItem item, MainViewModel main)
    {
        _db = db;
        _main = main;
        Item = item;
        Detail = db.Project(item.Id);
        Plugins = Detail?.Plugins.Select(p => new PluginRow(db, p)).ToList() ?? [];
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
    void OpenInDaw()
    {
        if (!File.Exists(Item.Path))
        {
            Message = "This project file isn't where it used to be. Rescan to update the library.";
            return;
        }
        if (_db.GetSetting(DawExeKey(Item.Daw)) is { Length: > 0 } exe && File.Exists(exe))
            Shell.OpenWith(Item.Path, exe);
        else
            Shell.Open(Item.Path);
        _db.MarkOpened(Item.Id);
        Message = $"Opening in {Item.Daw}…";
    }

    [RelayCommand]
    void Reveal() => Shell.Reveal(Item.Path);

    [RelayCommand]
    void CopyPath() => Clipboard.SetText(Item.Path);

    [RelayCommand]
    async Task ExportMidi()
    {
        var reader = Readers.For(Item.Path);
        if (reader is null) return;
        Message = "Exporting MIDI…";
        try
        {
            var result = await Task.Run(() => Exporter.Midi(reader, Item.Path, ExportRoot, Item.Tempo));
            Message = result.Files.Count == 0 ? "No notes found to export." : $"Exported {result.Files.Count} MIDI files.";
            if (result.Files.Count > 0) Shell.Open(result.Folder);
        }
        catch (Exception e) { Message = $"MIDI export failed: {e.Message}"; }
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

public sealed partial class PluginRow(LibraryDb db, ProjectPlugin p) : ObservableObject
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
