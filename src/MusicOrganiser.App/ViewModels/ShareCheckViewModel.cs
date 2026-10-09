using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sharing;

namespace MusicOrganiser.App.ViewModels;

/// <summary>
/// Check a project before you send it, or a zip you've been sent: what won't load on this PC
/// (Audio Units, missing plugins), what won't load for your friend, and missing samples.
/// </summary>
public sealed partial class ShareCheckViewModel : ObservableObject
{
    readonly LibraryDb _db;
    int _checkVersion;
    (string Path, bool Outgoing)? _checking;

    public ObservableCollection<Collaborator> People { get; } = new();

    [ObservableProperty] Collaborator? _selectedPerson;
    [ObservableProperty] ShareReport? _report;
    [ObservableProperty] string _message = "";
    [ObservableProperty] string _error = "";
    [ObservableProperty] bool _isBusy;
    [ObservableProperty] string _newPersonName = "";
    [ObservableProperty] bool _showHowTo;

    public string MacCommand => PluginInventory.MacListCommand;
    public string WindowsCommand => PluginInventory.WindowsListCommand;

    public ShareCheckViewModel(LibraryDb db)
    {
        _db = db;
        foreach (var p in Collaborators.Load(db)) People.Add(p);
        var selected = Collaborators.Selected(db);
        _selectedPerson = People.FirstOrDefault(p => p.Name == selected) ?? People.FirstOrDefault();
        _showHowTo = People.Count == 0;
    }

    // ---- what the view shows ---------------------------------------------------------

    public bool HasReport => Report is not null;
    public bool HasPeople => People.Count > 0;
    public string FriendName => SelectedPerson?.Name ?? "your friend";
    public string FriendHeader => SelectedPerson is { } p ? $"For {p.Name}" : "For your friend";
    public string PersonSummary => SelectedPerson is { } p
        ? $"{p.Plugins.Count} plugins on {p.Name}'s list, updated {p.Updated:d MMM yyyy}"
        : "Add a friend's plugin list to check their side too.";

    public IReadOnlyList<PluginCheck> Plugins => Report?.Plugins ?? [];
    public IReadOnlyList<SampleCheck> MissingSamples => Report?.MissingSamples.ToList() ?? [];
    public bool HasMissingSamples => MissingSamples.Count > 0;
    public string NativeText => Report is { NativePlugins.Count: > 0 } r
        ? $"Plus {r.NativePlugins.Count} built into {r.Daw}, which open on Mac and Windows: {string.Join(", ", r.NativePlugins)}."
        : "";
    public bool IsZip => Report?.FileInZip is not null;
    public string MessageText => Report is null ? "" : ShareCheck.MessageForFriend(Report);
    public bool HasMessage => MessageText.Length > 0;

    public string Facts
    {
        get
        {
            if (Report is not { } r) return "";
            var parts = new List<string> { r.DawVersion.Length > 0 ? $"{r.Daw} {r.DawVersion}" : r.Daw };
            if (r.Tempo is { } t) parts.Add($"{t:0.##} BPM");
            if (r.SavedOn.Length > 0) parts.Add($"last saved on {r.SavedOn}");
            parts.Add($"{r.Plugins.Count + r.NativePlugins.Count} plugins");
            parts.Add($"{r.Samples.Count} samples");
            if (r.FileInZip is { } f) parts.Add($"from the zip: {f}");
            return string.Join(" · ", parts);
        }
    }

    partial void OnReportChanged(ShareReport? value)
    {
        foreach (var name in new[] { nameof(HasReport), nameof(Plugins), nameof(MissingSamples), nameof(HasMissingSamples),
                                     nameof(NativeText), nameof(IsZip), nameof(Facts), nameof(MessageText), nameof(HasMessage) })
            OnPropertyChanged(name);
    }

    partial void OnSelectedPersonChanged(Collaborator? value)
    {
        Collaborators.Select(_db, value?.Name);
        OnPropertyChanged(nameof(FriendName));
        OnPropertyChanged(nameof(FriendHeader));
        OnPropertyChanged(nameof(PersonSummary));
        // Re-check what's showing (or still being checked) against the new friend's list.
        _ = Refresh();
    }

    // ---- checking ----------------------------------------------------------------------

    /// <summary>
    /// Checks a project file or a zip. <paramref name="outgoing"/> is true for your own project
    /// before sending it (from its page), false for a file you've been sent (Browse or drop).
    /// </summary>
    public async Task Open(string path, bool outgoing = false)
    {
        var version = ++_checkVersion;
        _checking = (path, outgoing);
        Error = "";
        Message = "";
        IsBusy = true;
        ShareReport? result = null;
        string error = "";
        try
        {
            var friend = SelectedPerson?.Inventory;
            var here = PluginInventory.ThisPc(_db);
            result = await Task.Run(() => ShareCheck.Run(path, here, friend, outgoing));
        }
        catch (Exception e)   // a file we can't read must not take the app down
        {
            error = $"Couldn't check {Path.GetFileName(path)}: {e.Message}";
        }
        if (version != _checkVersion) return;   // a newer check started meanwhile; it owns the screen
        _checking = null;
        Report = result;
        Error = error;
        IsBusy = false;
    }

    /// <summary>
    /// Checks again whatever is on screen (or still being checked), e.g. after a plugin scan or
    /// when another friend is picked.
    /// </summary>
    public Task Refresh()
    {
        (string Path, bool Outgoing)? current = _checking;
        if (current is null && Report is { } r) current = (r.SourcePath, r.Outgoing);
        return current is { } c ? Open(c.Path, c.Outgoing) : Task.CompletedTask;
    }

    [RelayCommand]
    async Task Browse()
    {
        var exts = string.Join(";", Readers.All.SelectMany(r => r.Extensions).Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = "Choose a project, or a zip you've been sent",
            Filter = $"Projects and zips|*.zip;{exts}|All files|*.*",
        };
        if (dialog.ShowDialog() == true) await Open(dialog.FileName);
    }

    [RelayCommand]
    Task Recheck() => Refresh();

    [RelayCommand]
    void Reveal()
    {
        if (Report is { } r) Shell.Reveal(r.SourcePath);
    }

    // ---- friends' plugin lists ----------------------------------------------------------------

    [RelayCommand]
    void LoadListFromFile() => AddList(fromClipboard: false);

    [RelayCommand]
    void PasteList() => AddList(fromClipboard: true);

    /// <summary>
    /// Adds (or refreshes) a friend's plugin list from the file the Terminal command made, or
    /// from text they pasted into a chat. Uses the name typed in the box, else the selected friend.
    /// </summary>
    void AddList(bool fromClipboard)
    {
        Error = "";
        var name = NewPersonName.Trim().Length > 0 ? NewPersonName.Trim() : SelectedPerson?.Name ?? "";
        if (name.Length == 0)
        {
            Error = "Type your friend's name first.";
            return;
        }
        string text;
        if (fromClipboard)
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            if (text.Trim().Length == 0)
            {
                Error = "There's no text on the clipboard. Copy their plugin list first.";
                return;
            }
        }
        else
        {
            var dialog = new OpenFileDialog { Title = $"Choose {name}'s plugin list", Filter = "Text files (*.txt)|*.txt|All files|*.*" };
            if (dialog.ShowDialog() != true) return;
            text = File.ReadAllText(dialog.FileName);
        }

        var list = PluginInventory.Parse(name, text);
        if (list.IsEmpty)
        {
            Error = "That list has no plugins in it.";
            return;
        }
        var person = Collaborators.Upsert(_db, name, list);
        var existing = People.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) People.Remove(existing);
        People.Add(person);
        NewPersonName = "";
        SelectedPerson = person;
        OnPropertyChanged(nameof(HasPeople));
        OnPropertyChanged(nameof(PersonSummary));
        Message = $"Saved {list.Plugins.Count} plugins for {name}.";
    }

    [RelayCommand]
    void RemovePerson()
    {
        if (SelectedPerson is not { } p) return;
        if (MessageBox.Show($"Forget {p.Name}'s plugin list?", "Share Check", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        Collaborators.Remove(_db, p.Name);
        People.Remove(p);
        SelectedPerson = People.FirstOrDefault();
        OnPropertyChanged(nameof(HasPeople));
    }

    [RelayCommand]
    void CopyMacCommand()
    {
        Clipboard.SetText(MacCommand);
        Message = "Copied. Send it to your friend: they paste it into Terminal and press Return, and my-plugins.txt appears on their Desktop.";
    }

    [RelayCommand]
    void CopyWindowsCommand()
    {
        Clipboard.SetText(WindowsCommand);
        Message = "Copied. Your friend pastes it into PowerShell, and my-plugins.txt appears on their Desktop.";
    }

    [RelayCommand]
    void ExportMyList()
    {
        var mine = PluginInventory.ThisPc(_db);
        if (mine.IsEmpty)
        {
            Error = "No plugins scanned yet. Press Scan for changes first.";
            return;
        }
        var dialog = new SaveFileDialog { Title = "Save your plugin list", FileName = "my-plugins.txt", Filter = "Text files (*.txt)|*.txt" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, mine.ToText());
        Message = $"Saved {mine.Plugins.Count} plugins. Your friend can load it into their copy of the app, or just read it.";
    }

    [RelayCommand]
    void ToggleHowTo() => ShowHowTo = !ShowHowTo;

    // ---- sending things back -------------------------------------------------------------

    [RelayCommand]
    void CopyMessage()
    {
        if (!HasMessage) return;
        Clipboard.SetText(MessageText);
        Message = $"Copied a message for {FriendName}. Paste it into your chat.";
    }

    [RelayCommand]
    void SaveReadme()
    {
        if (Report is not { } r) return;
        var dialog = new SaveFileDialog
        {
            Title = "Save the share readme",
            FileName = $"{TextScan.SafeFileName(r.ProjectName)} - {ShareCheck.ReadmeName}",
            InitialDirectory = Path.GetDirectoryName(r.SourcePath),
            Filter = "Text files (*.txt)|*.txt",
        };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, ShareCheck.Readme(r));
        Message = "Saved. Put it in the zip or send it with the project.";
    }

    /// <summary>
    /// Adds SHARE-README.txt to a zip: the zip being checked, or the zipped loop package
    /// you exported from FL for this project.
    /// </summary>
    [RelayCommand]
    void AddReadmeToZip()
    {
        if (Report is not { } r) return;
        string zip;
        if (r.FileInZip is not null) zip = r.SourcePath;
        else
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose the zip you're sending (e.g. FL's zipped loop package)",
                InitialDirectory = Path.GetDirectoryName(r.SourcePath),
                Filter = "Zip files (*.zip)|*.zip",
            };
            if (dialog.ShowDialog() != true) return;
            zip = dialog.FileName;
        }
        var ok = MessageBox.Show($"Add {ShareCheck.ReadmeName} to\n{zip}?\n\nNothing else in the zip changes.",
                                 "Share Check", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        try
        {
            ShareCheck.AddReadmeToZip(zip, ShareCheck.Readme(r));
            Message = $"Added {ShareCheck.ReadmeName} to {Path.GetFileName(zip)}.";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Error = $"Couldn't update the zip: {e.Message}";
        }
    }

    [RelayCommand]
    void SearchOnline(PluginCheck? p)
    {
        if (p is not null) Shell.OpenUrl(MatchFinder.PluginSearchUrl(p.Name, p.Vendor));
    }
}
