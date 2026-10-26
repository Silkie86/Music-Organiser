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
    [ObservableProperty] bool _hasOldCopies;
    [ObservableProperty] string _oldCopiesSizeText = "";
    [ObservableProperty] string _oldCopiesTitle = "";

    void BuildOldCopies(IReadOnlyList<MovedFolder> old)
    {
        _oldCopies = old;
        HasOldCopies = old.Count > 0;
        OldCopiesSizeText = DiskSpace.Text(old.Sum(m => m.Bytes));
        OldCopiesTitle = old.Count == 1 ? "1 old copy left after a move" : $"{old.Count} old copies left after moving folders";
    }

    /// <summary>A Keep or Delete card for every old copy at once, each ticked.</summary>
    [RelayCommand]
    void ReviewOldCopies()
    {
        if (_oldCopies.Count == 0) return;
        var card = new SpaceCard(this)
        {
            Title = "Delete the old copies?",
            Intro = "Each folder now lives on another drive, and the plugins use that copy. Untick any whose plugin you haven't opened yet. " +
                    "They go to the Recycle Bin: empty the bin to get the room back.",
            Items = _oldCopies.Select(m => new CleanupItem(new SpaceFolder(m.OldCopy, m.Kind, m.Bytes, []), "", ticked: true,
                                                           label: Path.GetFileName(m.OldCopy), detail: $"Now on {m.To}")).ToList(),
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
                ? "The link goes and the old copy takes its place again, as before the move."
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
        var activity = _main.Activities.Start($"Moving {name} back", "", cancel: moved.HasOldCopy ? null : stop.Cancel);
        var progress = new Progress<long>(done => activity.Report(100.0 * done / Math.Max(1, moved.Bytes), $"Copied {DiskSpace.Text(done)} of {DiskSpace.Text(moved.Bytes)}"));
        try
        {
            await Task.Run(() => FolderMove.MoveBack(moved, progress, stop.Token));
        }
        catch (OperationCanceledException)
        {
            activity.Stop($"Stopped moving {name} back", "It's still on the other drive, as before.");
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
