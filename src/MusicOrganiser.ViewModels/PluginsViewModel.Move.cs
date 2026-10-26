using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Space;

namespace MusicOrganiser.ViewModels;

// Moving a plugin's sounds or presets to another drive (Solomon, 26 Oct 2026): copied and checked, then the original
// is swapped for a link so the plugin and every project still find it (see FolderMove). The old copy stays until you
// say the plugin opened fine; Move back undoes it.
public sealed partial class PluginsViewModel
{
    /// <summary>The picked plugin's folders that live on another drive now.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMoved))] IReadOnlyList<MovedFolderItem> _movedFolders = [];
    public bool HasMoved => MovedFolders.Count > 0;

    // ---- the old copies card over the list: everything moved whose old copy is still here (F12 note, 26 Oct) ----------

    IReadOnlyList<MovedFolder> _oldCopies = [];
    IReadOnlyList<OldHome> _oldHomes = [];
    [ObservableProperty] bool _hasOldCopies;
    [ObservableProperty] string _oldCopiesSizeText = "";
    [ObservableProperty] string _oldCopiesTitle = "";

    void BuildOldCopies(IReadOnlyList<MovedFolder> old, IReadOnlyList<OldHome> homes)
    {
        _oldCopies = old;
        _oldHomes = homes;
        var n = old.Count + homes.Count;
        HasOldCopies = n > 0;
        OldCopiesSizeText = DiskSpace.Text(old.Sum(m => m.Bytes) + homes.Sum(h => h.Bytes));
        OldCopiesTitle = n == 1 ? "1 old copy left after a move" : $"{n} old copies left after moving folders";
    }

    /// <summary>A Keep or Delete card for every old copy at once, each ticked.</summary>
    [RelayCommand]
    void ReviewOldCopies()
    {
        if (_oldCopies.Count + _oldHomes.Count == 0) return;
        var card = new SpaceCard(this)
        {
            Title = "Delete the old copies?",
            Intro = "Each folder now lives on another drive, and the plugins use that copy. Untick any whose plugin you haven't opened yet. " +
                    "They go to the Recycle Bin: empty the bin to get the room back.",
            Items = _oldCopies.Select(m => new CleanupItem(new SpaceFolder(m.OldCopy, m.Kind, m.Bytes, []), "", ticked: true,
                                                           label: Path.GetFileName(m.OldCopy), detail: $"Now on {m.To}"))
                              .Concat(_oldHomes.Select(h => new CleanupItem(new SpaceFolder(h.Path, SpaceKind.Sounds, h.Bytes, []), "", ticked: true,
                                                                            label: $"{Path.GetFileName(h.Path)} ({h.Note})", detail: h.Path))).ToList(),
        };
        card.Confirm = () => DeleteAsync(card, "");
        card.Refresh();
        Card = card;
    }

    void LoadMoved(string key) =>
        MovedFolders = key.Length == 0 ? [] : _db.MovedFolders().Where(m => m.Key == key).Select(m => new MovedFolderItem(m, this)).ToList();

    void SaveMoved(Func<IEnumerable<MovedFolder>, IEnumerable<MovedFolder>> change) => _db.SaveMovedFolders(change(_db.MovedFolders()).ToList());

    /// <summary>Move…: pick a folder on another drive, then a card says what happens before anything does.</summary>
    internal async Task MoveFolderAsync(SpaceFolder folder)
    {
        if (Selected is not { } p || !_rowSpace.TryGetValue(p, out var space)) return;
        var destination = await Ui.Current.PickFolder($"Move {Path.GetFileName(folder.Path)} to which folder? Pick one on another drive");
        if (destination is null) return;
        var (admin, problem) = await Task.Run(() =>
            FolderMove.Problem(folder.Path, destination, folder.Bytes) is { Length: > 0 } why ? (false, why) : FolderMove.CheckSwap(folder.Path));
        if (problem.Length > 0)
        {
            _main.Activities.Failed($"Can't move {Path.GetFileName(folder.Path)} there", problem);
            return;
        }
        var target = FolderMove.Target(folder.Path, destination);
        var card = new SpaceCard(this)
        {
            Title = $"Move {Path.GetFileName(folder.Path)} to {Path.GetPathRoot(target)}?",
            Intro = $"It's copied to {target} and every file checked. Then the folder here becomes \"{Path.GetFileName(folder.Path)}{FolderMove.OldSuffix}\" " +
                    "and a link takes its place, so the plugin and your projects still find everything where it was. " +
                    "Close your DAW first. Nothing is deleted: once the plugin opens fine, Delete old copy frees the room here." +
                    (admin ? " Windows guards this folder, so it asks for administrator rights once the copy is made." : ""),
        };
        card.Fix($"Move {DiskSpace.Text(folder.Bytes)}");
        card.Confirm = () => RunMoveAsync(space.Key, p.Name, folder, destination);
        Card = card;
    }

    async Task RunMoveAsync(string key, string plugin, SpaceFolder folder, string destination)
    {
        Card = null;
        var name = Path.GetFileName(folder.Path);
        using var stop = new CancellationTokenSource();
        var activity = _main.Activities.Start($"Moving {name}", $"Copying to {destination}", cancel: stop.Cancel);
        var progress = new Progress<long>(done => activity.Report(100.0 * done / Math.Max(1, folder.Bytes),
                                                                  $"Copied {DiskSpace.Text(done)} of {DiskSpace.Text(folder.Bytes)}"));
        try
        {
            var moved = await Task.Run(() => FolderMove.Move(folder.Path, destination, folder.Bytes, progress, stop.Token));
            SaveMoved(all => all.Where(m => !m.From.Equals(moved.From, StringComparison.OrdinalIgnoreCase))
                                .Append(moved with { Key = key, Kind = folder.Kind }));
            activity.Finish($"Moved {name} to {Path.GetPathRoot(moved.To)}",
                            $"Open {plugin} in your DAW. Once it's fine, Delete old copy frees {DiskSpace.Text(folder.Bytes)} here.",
                            "Show", () => Shell.Reveal(moved.To));
        }
        catch (OperationCanceledException)
        {
            activity.Stop($"Stopped moving {name}", "The half-made copy was removed. Nothing else changed.");
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            activity.Fail($"Couldn't move {name}", e.Message);
            return;
        }
        await RemeasureAsync(key);
    }

    /// <summary>Delete old copy: a Keep or Delete card for the copy left beside the link.</summary>
    internal void DeleteOldCopy(MovedFolder moved)
    {
        if (!moved.HasOldCopy) return;
        var card = new SpaceCard(this)
        {
            Title = $"Delete the old copy of {Path.GetFileName(moved.From)}?",
            Intro = $"The plugin now uses the copy on {Path.GetPathRoot(moved.To)}. Only delete this once it has opened fine. " +
                    "It goes to the Recycle Bin: empty the bin to get the room back.",
            Items = [new CleanupItem(new SpaceFolder(moved.OldCopy, moved.Kind, moved.Bytes, []), "", ticked: true,
                                     label: Path.GetFileName(moved.OldCopy), detail: moved.OldCopy)],
        };
        card.Confirm = async () =>
        {
            await DeleteAsync(card, "");
            if (!Directory.Exists(moved.OldCopy)) LoadMoved(moved.Key);
        };
        card.Refresh();
        Card = card;
    }

    /// <summary>Move back: the folder comes back here, and the copy on the other drive can go.</summary>
    internal void MoveBack(MovedFolder moved)
    {
        var card = new SpaceCard(this)
        {
            Title = $"Move {Path.GetFileName(moved.From)} back?",
            Intro = moved.HasOldCopy
                ? "Anything the plugin saved since the move is copied into the old copy, then the link goes and the old copy takes its place again. Close your DAW first."
                : $"The old copy is gone, so it's copied back from {Path.GetPathRoot(moved.To)} first ({DiskSpace.Text(moved.Bytes)}). Close your DAW first.",
            Items = [new CleanupItem(new SpaceFolder(moved.To, moved.Kind, moved.Bytes, []), "", ticked: true,
                                     label: $"Then remove the copy on {Path.GetPathRoot(moved.To)}", detail: "To that drive's Recycle Bin")],
        };
        card.Fix("Move back");
        card.Confirm = () => RunMoveBackAsync(card, moved);
        Card = card;
    }

    async Task RunMoveBackAsync(SpaceCard card, MovedFolder moved)
    {
        var removeCopy = card.Items.Any(i => i.IsTicked);
        Card = null;
        var name = Path.GetFileName(moved.From);
        using var stop = new CancellationTokenSource();
        var activity = _main.Activities.Start($"Moving {name} back", "", cancel: stop.Cancel);
        var progress = new Progress<long>(done => activity.Report(100.0 * done / Math.Max(1, moved.Bytes), $"Copied {DiskSpace.Text(done)} of {DiskSpace.Text(moved.Bytes)}"));
        try
        {
            await Task.Run(() => FolderMove.MoveBack(moved, progress, stop.Token));
        }
        catch (OperationCanceledException)
        {
            activity.Stop($"Stopped moving {name} back", "It's still on the other drive, as before. The plugin uses that copy.");
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            activity.Fail($"Couldn't move {name} back", e.Message);
            return;
        }
        SaveMoved(all => all.Where(m => !m.From.Equals(moved.From, StringComparison.OrdinalIgnoreCase)));
        var left = removeCopy ? await Task.Run(() => RecycleBin.Send(moved.To)) : "";
        activity.Finish($"{name} is back where it was",
                        left.Length > 0 ? $"The copy on {Path.GetPathRoot(moved.To)} stayed: {left}" : removeCopy ? $"The copy on {Path.GetPathRoot(moved.To)} is in its Recycle Bin." : "");
        await RemeasureAsync(moved.Key);
    }
}

public sealed partial class PluginsViewModel
{
    // ---- where the moved folders live (I:\Plugin Data): moved as a whole, or found again (Solomon, 26 Oct 2026) -------

    /// <summary>Each folder the moved folders live in, for Settings › Plugins.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasDataHomes))] IReadOnlyList<PluginDataHome> _dataHomes = [];
    public bool HasDataHomes => DataHomes.Count > 0;
    /// <summary>Moved folders whose link points at nothing, because their folder was moved or renamed outside the app.</summary>
    [ObservableProperty] bool _hasLost;
    [ObservableProperty] string _lostText = "";
    /// <summary>Moved folders a plugin update put a real folder back in place of, so the plugin writes on this drive again.</summary>
    [ObservableProperty] bool _hasReplaced;
    [ObservableProperty] string _replacedText = "";
    IReadOnlyList<MovedFolder> _replaced = [];

    void BuildHomes(IReadOnlyList<MovedFolder> moved, IReadOnlyList<MovedFolder> lost, IReadOnlyList<MovedFolder> replaced)
    {
        _replaced = replaced;
        HasReplaced = replaced.Count > 0;
        var names = string.Join(", ", replaced.Select(m => Path.GetFileName(m.From)));
        ReplacedText = replaced.Count == 1
            ? $"{names} is a real folder here again, not a link: an update probably made it. New files land on this drive, and the copy on the other drive misses them."
            : $"{names} are real folders here again, not links: updates probably made them. New files land on this drive, and the copies on the other drive miss them.";
        var lostFrom = lost.Select(m => m.From).ToHashSet(StringComparer.OrdinalIgnoreCase);
        DataHomes = moved.GroupBy(FolderMove.HomeOf, StringComparer.OrdinalIgnoreCase)
                         .Select(g => new PluginDataHome(g.Key, g.Count(), g.Sum(m => m.Bytes), g.Count(m => lostFrom.Contains(m.From)), this))
                         .OrderByDescending(h => h.Bytes).ToList();
        HasLost = lost.Count > 0;
        var where = string.Join(", ", lost.Select(FolderMove.HomeOf).Distinct(StringComparer.OrdinalIgnoreCase));
        LostText = lost.Count == 1 ? $"1 moved folder can't be found: it was in {where}." : $"{lost.Count} moved folders can't be found: they were in {where}.";
    }

    /// <summary>Reads the moved folders again for Settings › Plugins, without loading the whole Plugins page.</summary>
    public async Task RefreshHomesAsync()
    {
        var moved = _db.MovedFolders();
        var (lost, replaced) = await Task.Run(() => (FolderMove.Lost(moved), FolderMove.Replaced(moved)));
        BuildHomes(moved, lost, replaced);
    }

    /// <summary>The warning's Find it…: the first place with folders that can't be found.</summary>
    [RelayCommand]
    Task FindLost() => DataHomes.FirstOrDefault(h => h.Lost > 0) is { } home ? FindHomeAsync(home.Path) : Task.CompletedTask;

    /// <summary>Link again: a card first, then each folder an update made joins its copy on the other drive and the link goes back.</summary>
    [RelayCommand]
    void LinkAgain()
    {
        if (_replaced.Count == 0) return;
        var replaced = _replaced;
        var card = new SpaceCard(this)
        {
            Title = replaced.Count == 1 ? $"Link {Path.GetFileName(replaced[0].From)} again?" : $"Link {replaced.Count} folders again?",
            Intro = $"New and newer files in each folder are copied to its place on the other drive. Then the folder here becomes \"…{FolderMove.UpdateSuffix}\" " +
                    "and the link goes back, so the plugin writes to the other drive again. Close your DAW first. " +
                    "Nothing is deleted: the set-aside folders go on the old copies card, to delete once the plugins open fine.",
        };
        card.Fix("Link again");
        card.Confirm = () => RunLinkAgainAsync(replaced);
        Card = card;
    }

    async Task RunLinkAgainAsync(IReadOnlyList<MovedFolder> replaced)
    {
        Card = null;
        using var stop = new CancellationTokenSource();
        var activity = _main.Activities.Start("Linking the folders again", "", cancel: stop.Cancel);
        var problems = new List<string>();
        foreach (var m in replaced)
        {
            var name = Path.GetFileName(m.From);
            try
            {
                var bytes = (await Task.Run(() => DiskSpace.Measure(m.From, stop.Token), stop.Token)).Bytes;
                var progress = new Progress<long>(done => activity.Report(100.0 * done / Math.Max(1, bytes), $"{name}: copied {DiskSpace.Text(done)} of what's new"));
                var aside = await Task.Run(() => FolderMove.LinkAgain(m, bytes, progress, stop.Token));
                _db.SaveOldHomes(_db.OldHomes().Append(aside));
            }
            catch (OperationCanceledException)
            {
                activity.Stop("Stopped linking the folders again", $"{name} is as it was. Its new files may be on the other drive too now, which does no harm.");
                await ReloadAsync();
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{name}: {e.Message}");
            }
        }
        if (problems.Count == 0)
            activity.Finish(replaced.Count == 1 ? "Linked it again" : $"Linked {replaced.Count} folders again",
                            "The plugins write to the other drive again. The folders set aside are on the old copies card.");
        else
            activity.Fail($"{problems.Count} couldn't be linked again", string.Join(" ", problems));
        await ReloadAsync();
    }

    /// <summary>Move it…: the whole folder goes into one you pick, and every link follows.</summary>
    internal async Task MoveHomeAsync(string home)
    {
        var name = Path.GetFileName(home);
        var destination = await Ui.Current.PickFolder($"Move {name} into which folder?");
        if (destination is null) return;
        using var stop = new CancellationTokenSource();
        var activity = _main.Activities.Start($"Moving {name}", "Measuring it", cancel: stop.Cancel);
        try
        {
            var bytes = (await Task.Run(() => DiskSpace.Measure(home, stop.Token), stop.Token)).Bytes;
            var progress = new Progress<long>(done => activity.Report(100.0 * done / Math.Max(1, bytes), $"Copied {DiskSpace.Text(done)} of {DiskSpace.Text(bytes)}"));
            var sameDrive = FolderMove.DriveOf(home).Equals(FolderMove.DriveOf(destination), StringComparison.OrdinalIgnoreCase);
            var (now, problems) = await Task.Run(() => FolderMove.MoveHome(home, destination, _db.MovedFolders(), bytes, progress, stop.Token));
            _db.SaveMovedFolders(now);
            if (!sameDrive) _db.SaveOldHomes(_db.OldHomes().Append(new OldHome(home, bytes)));
            var target = FolderMove.Target(home, destination);
            if (problems.Count == 0)
                activity.Finish($"Moved {name} to {target}",
                                sameDrive ? "Every plugin's link points there now." : "Every plugin's link points there now. Its old place is on the old copies card on Plugins, to delete once the plugins open fine.",
                                "Show", () => Shell.Reveal(target));
            else
                activity.Fail($"{problems.Count} of the links still point at the old place",
                              $"{problems.First().Value} The rest point at {target}. Keep the old folder until Find it… has fixed them.");
        }
        catch (OperationCanceledException)
        {
            activity.Stop($"Stopped moving {name}", "The half-made copy was removed. Nothing else changed.");
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            activity.Fail($"Couldn't move {name}", e.Message);
            return;
        }
        await ReloadAsync();
    }

    /// <summary>I moved it, find it…: you show the folder's new place, and every link that can be pointed there is.</summary>
    internal async Task FindHomeAsync(string home)
    {
        var name = Path.GetFileName(home);
        var found = await Ui.Current.PickFolder($"Where is {name} now? Pick the folder itself in its new place");
        if (found is null) return;
        var activity = _main.Activities.Start($"Pointing the links at {found}");
        var (now, problems) = await Task.Run(() => FolderMove.Repoint(home, found, _db.MovedFolders()));
        _db.SaveMovedFolders(now);
        var moved = _db.MovedFolders().Count(m => DiskSpace.IsInside(m.To, found));
        if (problems.Count == 0) activity.Finish($"The plugins find {name} in {found} now", $"{(moved == 1 ? "1 link points" : $"{moved} links point")} there.");
        else activity.Fail($"{problems.Count} couldn't be pointed at {found}", problems.First().Value);
        await ReloadAsync();
    }
}

/// <summary>A folder the moved folders live in (I:\Plugin Data), on Settings › Plugins.</summary>
public sealed partial class PluginDataHome(string path, int count, long bytes, int lost, PluginsViewModel owner)
{
    public string Path => path;
    public long Bytes => bytes;
    public int Lost => lost;
    public string Text => $"{(count == 1 ? "1 plugin folder" : $"{count} plugin folders")} · {DiskSpace.Text(bytes)}" +
                          (lost > 0 ? $" · {lost} can't be found: Find it…" : "");
    public bool HasLost => lost > 0;

    [RelayCommand] void Show() => Shell.Open(path);
    [RelayCommand] Task MoveIt() => owner.MoveHomeAsync(path);
    [RelayCommand] Task FindIt() => owner.FindHomeAsync(path);
}

/// <summary>A plugin folder that lives on another drive now, on the plugin's Space tab.</summary>
public sealed partial class MovedFolderItem(MovedFolder moved, PluginsViewModel owner)
{
    public MovedFolder Moved => moved;
    public string Title => $"{Path.GetFileName(moved.From)} · moved to {Path.GetPathRoot(moved.To)}";
    public string Where => moved.To;
    public string SizeText => DiskSpace.Text(moved.Bytes);
    public bool HasOldCopy => moved.HasOldCopy;
    public string OldCopyText => HasOldCopy ? $"Its old copy is still here ({DiskSpace.Text(moved.Bytes)}): delete it once the plugin opens fine." : "";

    [RelayCommand] void Show() => Shell.Reveal(moved.To);
    [RelayCommand] void DeleteOldCopy() => owner.DeleteOldCopy(moved);
    [RelayCommand] void MoveBack() => owner.MoveBack(moved);
}
