using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// Fix: a preview of what can be done about what's missing, then a fixed copy saved next to the original
/// ("Didums_77 (fixed).flp"), then, once it's been opened, Keep or Delete fixed copy.
/// </summary>
public sealed partial class ProjectDetailViewModel
{
    /// <summary>Setting that remembers a fixed copy still waiting for "did it load correctly?", by the original's path.</summary>
    public static string FixCheckKey(string projectPath) => $"fix_check:{projectPath}";

    /// <summary>Whether this project has something missing and is a kind Fix can write.</summary>
    public bool CanFix => Item.HasMissing && !Item.FileMissing && ProjectFixer.CanWrite(Item.Path);

    /// <summary>The preview's lines, while it's open.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsFixOpen))] IReadOnlyList<FixRow> _fixRows = [];
    public bool IsFixOpen => FixRows.Count > 0;
    [ObservableProperty] string _fixSummary = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FixButtonText))] bool _fixBusy;
    public string FixButtonText => FixBusy ? "Looking…" : "Fix…";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveFixedCommand))] int _fixTicked;

    public string FixedName => System.IO.Path.GetFileName(ProjectFixer.FixedPath(Item.Path));
    public string FixFootnote => $"The original stays exactly as it is. The fixed copy is saved next to it as {FixedName}.";

    /// <summary>A fixed copy waiting for the verdict, or null.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPendingFix), nameof(PendingFixText), nameof(PendingFixDetail))] string? _pendingFix;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PendingFixText), nameof(PendingFixDetail))] bool _pendingFixOpened;
    public bool HasPendingFix => PendingFix is not null;
    public string PendingFixText => PendingFixOpened
        ? $"Did {System.IO.Path.GetFileNameWithoutExtension(PendingFix)} load correctly in {Item.Daw}?"
        : $"{System.IO.Path.GetFileName(PendingFix)} is saved next to the original.";
    public string PendingFixDetail => PendingFixOpened
        ? "Keep it if the samples play. If it didn't load right, delete it: the original was never changed."
        : $"Open it in {Item.Daw} to check it, then say whether it loaded correctly.";

    void LoadPendingFix()
    {
        if (_db.GetSetting(FixCheckKey(Item.Path)) is { Length: > 0 } pending && File.Exists(pending)) PendingFix = pending;
    }

    /// <summary>Works out the preview: one line per missing sample and plugin with the best guess.</summary>
    [RelayCommand]
    async Task PlanFix()
    {
        if (Detail is null || FixBusy) return;
        FixBusy = true;
        try
        {
            var detail = Detail;
            // The first time, the sample folders haven't been searched yet: do that first, so Fix has somewhere to look.
            if (_db.SampleIndexCount() == 0 && _db.GetList(Scanner.SampleFoldersKey).Count > 0)
            {
                var card = Activities.Start("Searching your sample folders", "So Fix can look for the missing samples");
                var found = await Task.Run(() => new Scanner(_db).ScanSampleFolders());
                card.Finish("Searched your sample folders", $"{found:N0} sound files");
            }
            var lines = await Task.Run(() => ProjectFixer.Plan(_db, detail));
            var rows = lines.Select(l => new FixRow(l, this)).ToList();
            FixRows = rows;
            CountTicked();
            var relink = lines.Count(l => l.Action == FixAction.Relink);
            var suggest = lines.Count(l => l.Action == FixAction.Suggestion);
            var still = lines.Count(l => l.Action == FixAction.StillMissing);
            FixSummary = string.Join(" · ", new[]
            {
                relink > 0 ? $"{relink} found" : null,
                suggest > 0 ? $"{suggest} to choose" : null,
                still > 0 ? $"{still} still missing" : null,
            }.Where(s => s is not null));
        }
        catch (Exception e)
        {
            Message = $"Fix couldn't look for what's missing: {e.Message}";
        }
        finally { FixBusy = false; }
    }

    internal void CountTicked() => FixTicked = FixRows.Count(r => r.CanTick && r.Ticked);

    [RelayCommand]
    void CloseFix() => FixRows = [];

    /// <summary>Writes the fixed copy with the ticked samples relinked.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveFixed))]
    async Task SaveFixed()
    {
        var use = FixRows.Where(r => r.CanTick && r.Ticked).Select(r => r.Line).ToList();
        if (use.Count == 0) return;
        FixBusy = true;
        var card = Activities.Start($"Fixing {Item.Name}", $"Relinking {SampleCount(use.Count)} in a copy");
        try
        {
            var path = Item.Path;
            var (file, _) = await Task.Run(() => ProjectFixer.Write(path, use));
            _db.SetSetting(FixCheckKey(Item.Path), file);
            PendingFix = file;
            PendingFixOpened = false;
            FixRows = [];
            card.Finish($"Saved {System.IO.Path.GetFileName(file)}",
                        $"Relinked {SampleCount(use.Count)}. Open it to check it loads.", "Open it", OpenFixed);
        }
        catch (Exception e)
        {
            Message = $"Fix couldn't save a fixed copy: {e.Message}";
            card.Fail("Fix couldn't save a fixed copy", e.Message);
        }
        finally { FixBusy = false; }
    }

    bool CanSaveFixed() => FixTicked > 0;

    static string SampleCount(int n) => n == 1 ? "1 sample" : $"{n} samples";

    /// <summary>Opens the fixed copy in the project's own DAW, then asks whether it loaded correctly.</summary>
    [RelayCommand]
    void OpenFixed()
    {
        if (PendingFix is not { } file) return;
        if (!File.Exists(file))
        {
            ForgetFix();
            Activities.Failed("The fixed copy isn't there any more", "It was moved or deleted outside the app.");
            return;
        }
        try
        {
            if (_db.GetSetting(DawExeKey(Item.Daw)) is { Length: > 0 } exe && File.Exists(exe)) Shell.OpenWith(file, exe);
            else Shell.Open(file);
        }
        catch (Exception e)
        {
            Activities.Failed($"Couldn't open it in {Item.Daw}", e.Message);
            return;
        }
        PendingFixOpened = true;
        Activities.Done($"Opening {System.IO.Path.GetFileNameWithoutExtension(file)}", $"Sent to {Item.Daw}. Then say here whether it loaded correctly.");
    }

    /// <summary>It loaded fine: keep the copy and stop asking.</summary>
    [RelayCommand]
    void KeepFixed()
    {
        if (PendingFix is not { } file) return;
        ForgetFix();
        Activities.Done($"Kept {System.IO.Path.GetFileName(file)}", "It shows up as a save of this song after the next scan.");
    }

    /// <summary>It didn't load right: delete the copy (the original was never changed).</summary>
    [RelayCommand]
    async Task DeleteFixed()
    {
        if (PendingFix is not { } file) return;
        if (!await Ui.Current.Confirm($"Delete {System.IO.Path.GetFileName(file)}?\n\nThe original project stays as it is.", "Delete fixed copy")) return;
        try
        {
            if (File.Exists(file)) File.Delete(file);
            ForgetFix();
            Activities.Done($"Deleted {System.IO.Path.GetFileName(file)}", "The original project is unchanged.");
        }
        catch (Exception e)
        {
            Activities.Failed("Couldn't delete the fixed copy", e.Message);
        }
    }

    void ForgetFix()
    {
        _db.SetSetting(FixCheckKey(Item.Path), "");
        PendingFix = null;
        PendingFixOpened = false;
    }
}

/// <summary>One line of Fix's preview. Samples it found, or that you tick, go into the fixed copy.</summary>
public sealed partial class FixRow : ObservableObject
{
    readonly ProjectDetailViewModel _owner;

    public FixRow(FixLine line, ProjectDetailViewModel owner)
    {
        Line = line;
        _owner = owner;
        _ticked = line.Action == FixAction.Relink;
    }

    public FixLine Line { get; }
    public string Name => Line.Name;
    public string Note => Line.Note;
    public string? Use => Line.Use;
    public bool HasUse => Line.Use is not null;
    public string KindText => Line.IsSample ? "Sample" : "Plugin";
    /// <summary>"Relink", "Choose", "Swap by hand" or "Still missing", for the line's badge.</summary>
    public string ActionText => Line.Action switch
    {
        FixAction.Relink => "Relink",
        FixAction.Suggestion when Line.IsSample => "Choose",
        FixAction.Suggestion => "Swap by hand",
        _ => "Still missing",
    };
    public bool IsRelink => Line.Action == FixAction.Relink;
    public bool IsSuggestion => Line.Action == FixAction.Suggestion;
    public bool IsMissing => Line.Action == FixAction.StillMissing;
    public bool CanTick => Line.CanWrite;

    [ObservableProperty] bool _ticked;
    partial void OnTickedChanged(bool value) => _owner.CountTicked();
}
