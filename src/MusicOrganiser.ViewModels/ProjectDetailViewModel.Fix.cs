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
    /// <summary>Set once the waiting fixed copy has been opened, so Keep and Delete still show after the page reloads.</summary>
    public static string FixOpenedKey(string projectPath) => $"fix_opened:{projectPath}";

    /// <summary>Whether this project has something missing, or a Serum to swap for Serum 2, and is a kind Fix can write.</summary>
    public bool CanFix => (Item.HasMissing || _canSwapSerum) && !Item.FileMissing && ProjectFixer.CanWrite(Item.Path);

    readonly bool _canSwapSerum;

    /// <summary>The preview's lines, while it's open.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsFixOpen))] IReadOnlyList<FixRow> _fixRows = [];
    public bool IsFixOpen => FixRows.Count > 0;
    [ObservableProperty] string _fixSummary = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FixButtonText))] bool _fixBusy;
    public string FixButtonText => FixBusy ? "Looking…" : "Fix…";
    public string FixTip => Item.HasMissing
        ? $"{Item.MissingText} missing, so it will open with gaps. Fix finds what it can on your drives and saves a repaired copy next to the original."
        : "Fix can swap Serum for Serum 2 in a copy next to the original, with Serum's sound translated.";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveFixedCommand))] int _fixTicked;

    public string FixedName => System.IO.Path.GetFileName(ProjectFixer.FixedPath(Item.Path));
    public string FixFootnote => $"The original stays exactly as it is. The fixed copy is saved next to it as {FixedName}.";

    /// <summary>A fixed copy waiting for the verdict, or null.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPendingFix), nameof(PendingFixText), nameof(PendingFixDetail))] string? _pendingFix;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PendingFixText), nameof(PendingFixDetail))] bool _pendingFixOpened;
    public bool HasPendingFix => PendingFix is not null;
    public string PendingFixText => PendingFixOpened
        ? $"Did {System.IO.Path.GetFileNameWithoutExtension(PendingFix)} load correctly?"
        : $"{System.IO.Path.GetFileName(PendingFix)} is saved next to the original.";
    public string PendingFixDetail => PendingFixOpened
        ? "Keep it if it plays right. If it didn't load right, delete it: the original was never changed."
        : $"Open it in {Item.Daw}{(IsFlStudio ? " or Bitwig" : "")} to check it, then say whether it loaded correctly. " +
          "Until you keep it, the song still opens the original.";

    /// <summary>The save the waiting fixed copy was made from (its setting is kept under that path).</summary>
    string? _pendingFrom;

    /// <summary>
    /// The copy waiting for a verdict may have been made from any of the song's saves (an older one, or one whose
    /// kept fix is now the newest): look under each.
    /// </summary>
    void LoadPendingFix()
    {
        foreach (var from in Item.OlderSaves.Select(o => o.Path).Prepend(Item.Path))
            if (_db.GetSetting(FixCheckKey(from)) is { Length: > 0 } pending && File.Exists(pending))
            {
                _pendingFrom = from;
                PendingFix = pending;
                PendingFixOpened = _db.GetSetting(FixOpenedKey(from)) == pending;
                return;
            }
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
            var relink = lines.Count(l => l.Action == FixAction.Relink && !l.IsSwap);
            var suggest = lines.Count(l => l.Action == FixAction.Suggestion && !l.IsSwap);
            var still = lines.Count(l => l.Action == FixAction.StillMissing);
            FixSummary = string.Join(" · ", new[]
            {
                lines.Any(l => l.IsSwap) ? "Serum can be swapped for Serum 2" : null,
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
        var use = FixRows.Where(r => r.CanTick && r.Ticked).Select(r => r.ToWrite).ToList();
        if (use.Count == 0) return;
        FixBusy = true;
        var samples = use.Count(l => l.IsSample);
        var doing = string.Join(" and ", new[]
        {
            samples > 0 ? $"relinking {SampleCount(samples)}" : null,
            use.Any(l => l.IsSwap) ? "swapping Serum for Serum 2" : null,
        }.OfType<string>());
        var card = Activities.Start($"Fixing {Item.Name}", $"{char.ToUpper(doing[0])}{doing[1..]} in a copy");
        try
        {
            var path = Item.Path;
            var result = await Task.Run(() => ProjectFixer.Write(path, use));
            var file = result.Path;
            _db.SetSetting(FixCheckKey(path), file);
            _pendingFrom = path;
            PendingFix = file;
            PendingFixOpened = false;
            FixRows = [];
            var done = string.Join(" ", new[]
            {
                result.Relinked > 0 ? $"Relinked {SampleCount(result.Relinked)}." : null,
                result.Notes.Count > 0 ? string.Join(" ", result.Notes) : null,
                "Open it to check it loads.",
            }.OfType<string>());
            // The page reloads just below and this song gets a new panel: the card's button opens the copy from whichever
            // panel shows it then, so its Keep and Delete buttons appear.
            card.Finish($"Saved {System.IO.Path.GetFileName(file)}", done, "Open it",
                () => _ = (_main.Projects.Detail is { } now && now.PendingFix == file ? now : this).OpenFixed());
            // Read it into the library now, so Keep can list it as the newest save straight away. Until then the Projects
            // list leaves it out (see LibraryDb.PendingFixes), so the song still opens the original.
            await Task.Run(() => _db.Upsert(Scanner.Analyse(file)));
            await _main.Projects.ReloadAsync();
            // The list may have reloaded while it was writing (a DAW saving something else) and given the song a new
            // panel, made before there was a copy to ask about.
            if (_main.Projects.Detail is { PendingFix: null } now && now.Item.Id == Item.Id) now.LoadPendingFix();
        }
        catch (Exception e)
        {
            // The whole error, with where it happened and which project, so a failure on Solomon's PC can be traced.
            AppLog.Info("Fix", $"Couldn't save a fixed copy of {Item.Path}: {e}");
            Message = $"Fix couldn't save a fixed copy: {e.Message}";
            card.Fail("Fix couldn't save a fixed copy", e.Message);
        }
        finally { FixBusy = false; }
    }

    bool CanSaveFixed() => FixTicked > 0;

    static string SampleCount(int n) => n == 1 ? "1 sample" : $"{n} samples";

    /// <summary>Opens the fixed copy in the project's own DAW, then asks whether it loaded correctly.</summary>
    [RelayCommand]
    async Task OpenFixed()
    {
        if (PendingFix is not { } file) return;
        if (!File.Exists(file))
        {
            ForgetFix();
            Activities.Failed("The fixed copy isn't there any more", "It was moved or deleted outside the app.");
            return;
        }
        try { await OpenIn(_db, Item.Daw, file); }
        catch (Exception e)
        {
            Activities.Failed($"Couldn't open it in {Item.Daw}", e.Message);
            return;
        }
        if (PendingFix == file) FixOpened(file);
        Activities.Done($"Opening {System.IO.Path.GetFileNameWithoutExtension(file)}", $"Sent to {Item.Daw}. Then say here whether it loaded correctly.");
    }

    /// <summary>Converts the FL Studio fixed copy for Bitwig and opens it there, then asks whether it loaded correctly.</summary>
    [RelayCommand]
    async Task OpenFixedInBitwig()
    {
        if (PendingFix is not { } file) return;
        if (!File.Exists(file))
        {
            ForgetFix();
            Activities.Failed("The fixed copy isn't there any more", "It was moved or deleted outside the app.");
            return;
        }
        var name = System.IO.Path.GetFileNameWithoutExtension(file);
        await ConvertForBitwig(_db.ProjectAt(file) ?? new ProjectSummary { Path = file, Name = name, Daw = Item.Daw }, name);
        if (PendingFix == file) FixOpened(file);
    }

    void FixOpened(string file)
    {
        PendingFixOpened = true;
        if (_pendingFrom is { } from) _db.SetSetting(FixOpenedKey(from), file);
    }

    /// <summary>It loaded fine: keep the copy, stop asking, and list it as the song's newest save.</summary>
    [RelayCommand]
    async Task KeepFixed()
    {
        if (PendingFix is not { } file) return;
        ForgetFix();
        Activities.Done($"Kept {System.IO.Path.GetFileName(file)}", "It's now the newest save of this song, so Open uses it.");
        await _main.Projects.ReloadAsync(select: file);
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
            var from = _pendingFrom;
            ForgetFix();
            await Task.Run(() => _db.RemoveProject(file));
            Activities.Done($"Deleted {System.IO.Path.GetFileName(file)}", "The original project is unchanged.");
            await _main.Projects.ReloadAsync(select: from);
        }
        catch (Exception e)
        {
            Activities.Failed("Couldn't delete the fixed copy", e.Message);
        }
    }

    void ForgetFix()
    {
        _db.SetSetting(FixCheckKey(_pendingFrom ?? Item.Path), "");
        _db.SetSetting(FixOpenedKey(_pendingFrom ?? Item.Path), "");
        _pendingFrom = null;
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
        _chosen = line.Use;
    }

    public FixLine Line { get; }
    public string Name => Line.Name;
    public string Note => Line.Note;
    /// <summary>"FL Studio looked for: C:\…\Sd 090.wav": where the project expects the sample, a clue to where it went.</summary>
    public string LookedFor => Line.IsSample ? $"{_owner.Item.Daw} looked for: {Line.Missing}" : "";
    public bool HasLookedFor => Line.IsSample;
    /// <summary>The file to use, which you can swap for another copy or close match.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Use), nameof(HasUse))] string? _chosen;
    public string? Use => Chosen;
    public bool HasUse => Chosen is not null;
    public IReadOnlyList<string> Choices => Line.Choices;
    public bool HasChoices => Line.CanWrite && Line.Choices.Count > 1;
    public string ChoicesTip => $"{Line.Choices.Count} files could stand in for it: pick another";
    /// <summary>The line as it will be written, with the file you picked.</summary>
    public FixLine ToWrite => Line with { Use = Chosen };
    public string KindText => Line.IsSample ? "Sample" : "Plugin";
    /// <summary>"Relink", "Choose", "Swap", "Swap by hand" or "Still missing", for the line's badge.</summary>
    public string ActionText => Line.IsSwap ? "Swap" : Line.Action switch
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
