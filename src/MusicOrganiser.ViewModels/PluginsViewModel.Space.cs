using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Space;

namespace MusicOrganiser.ViewModels;

// Freeing space (Solomon, 25 Oct 2026): how much room each plugin takes, which big ones no project has used for years,
// and Clean up / Uninstall for them. Removing always goes through a Keep or Delete card and into the Recycle Bin. A
// plugin's sound library only goes when no project uses the plugin at all (his answer to "can Clean up remove a plugin's
// own sound library?"); everything under Program Files is left to the plugin's own uninstaller.
public sealed partial class PluginsViewModel
{
    public const string BigUnused = "Big and unused", NeverUsed = "Never used";
    public static IReadOnlyList<int> UnusedYearsChoices { get; } = [1, 2, 3, 5];
    public const int DefaultUnusedYears = 2;
    /// <summary>How many years without a project save make a plugin "unused".</summary>
    [ObservableProperty] int _unusedYears;
    /// <summary>The biggest unused plugins, for the cards over the list.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCandidates))] IReadOnlyList<SpaceCandidate> _candidates = [];
    public bool HasCandidates => Candidates.Count > 0;
    /// <summary>"71.4 GB", what Clean up and Uninstall could free across the unused plugins.</summary>
    [ObservableProperty] string _freeableText = "";
    [ObservableProperty] string _freeableNote = "";
    /// <summary>When sizes were measured, or that they haven't been yet.</summary>
    [ObservableProperty] string _spaceNote = "";

    /// <summary>The side panel's tab: 0 the songs using it, 1 its room on disk, 2 its installed copies.</summary>
    [ObservableProperty] int _sideTab;
    /// <summary>The picked plugin's room on disk, folder by folder.</summary>
    [ObservableProperty] IReadOnlyList<SpaceFolderItem> _spaceFolders = [];
    /// <summary>"38.2 GB on disk · last used Mar 2022 in garage sept".</summary>
    [ObservableProperty] string _spaceSummary = "";
    /// <summary>The bar's parts: plugin files, presets and settings, sounds.</summary>
    [ObservableProperty] IReadOnlyList<SpacePart> _spaceParts = [];
    [ObservableProperty] string _sharedText = "";
    [ObservableProperty] bool _hasSpace;
    /// <summary>The Keep or Delete card showing, if any.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCard))] SpaceCard? _card;
    public bool HasCard => Card is not null;

    IReadOnlyDictionary<string, PluginSpaceInfo> _spaces = new Dictionary<string, PluginSpaceInfo>();
    // Each row's measured room, found by matching names (see Matched), so the side panel shows the same as the list.
    readonly Dictionary<PluginUsage, PluginSpaceInfo> _rowSpace = new(ReferenceEqualityComparer.Instance);

    static int ReadUnusedYears(LibraryDb db) =>
        int.TryParse(db.GetSetting(LibraryDb.UnusedYearsKey), out var y) && UnusedYearsChoices.Contains(y) ? y : DefaultUnusedYears;

    partial void OnUnusedYearsChanged(int value)
    {
        _db.SetSetting(LibraryDb.UnusedYearsKey, value.ToString());
        if (_loaded is { } loaded) Show(loaded);
    }

    /// <summary>
    /// The rows with their sizes, plus the installed plugins no project uses (they aren't in the projects' list at all).
    /// A project may name a plugin differently from its file ("Pro-Q 3" and "FabFilter Pro-Q 3.vst3"), so an installed
    /// plugin counts as used when its name holds a used one's or the other way round: a plugin in doubt is never offered
    /// for removal.
    /// </summary>
    IReadOnlyList<PluginUsage> WithSpace(IReadOnlyList<PluginUsage> used, IReadOnlyDictionary<string, PluginSpaceInfo> spaces, int years)
    {
        _spaces = spaces;
        _rowSpace.Clear();
        var cutoff = DateTime.Now.AddYears(-years);
        // Each name's key and family worked out once: matching thousands of sizes against thousands of rows by
        // working them out again each time froze the page for half a second after every Clean up (F12 note, 25 Oct).
        var families = used.Select((u, at) => (Row: u, At: at, Key: LibraryDb.NameKey(u.Name), Family: PluginCatalog.Family(u.Name)))
                           .Where(u => u.Row.Format != "Native").ToList();
        var byKey = new Dictionary<string, (PluginUsage Row, int At, string Key, string Family)>();
        foreach (var f in families) byKey.TryAdd(f.Key, f);
        var claimed = new HashSet<string>();
        var rows = used.ToList();
        void Give((PluginUsage Row, int At, string Key, string Family) f, PluginSpaceInfo s)
        {
            rows[f.At] = f.Row with { Bytes = s.Bytes, SharedBytes = s.SharedBytes, Unused = f.Row.Installed && !(f.Row.LastUsed >= cutoff) };
            _rowSpace[rows[f.At]] = s;
            claimed.Add(f.Key);
        }
        // Same names first ("Serum 2" is Serum2's), then close ones for what's left.
        var rest = new List<PluginSpaceInfo>();
        foreach (var s in spaces.Values)
        {
            if (byKey.TryGetValue(s.Key, out var exact) && !claimed.Contains(s.Key)) Give(exact, s);
            else rest.Add(s);
        }
        // Every Waves plugin lives in one WaveShell file, and projects name them one by one ("CLA-76 Stereo"), so the
        // shell is used whenever a project uses a Waves plugin.
        var wavesUsed = used.Any(u => IsWaves(u.Name, u.Vendor));
        foreach (var s in rest)
        {
            var family = PluginCatalog.Family(s.Name);
            var close = families.Where(f => Matched(f.Family, family)).ToList();
            if (close.Count == 0 && wavesUsed && IsShell(s)) continue;   // used, so never offered (as below)
            if (close.Count == 0)
            {
                rows.Add(new PluginUsage(s.Name, s.Vendor, FormatOf(s), 0, "", true) { Bytes = s.Bytes, SharedBytes = s.SharedBytes, Unused = true });
                _rowSpace[rows[^1]] = s;
            }
            // A row that already has its own size keeps it; this one counts as used either way, so it's never offered.
            else if (close.FirstOrDefault(f => !claimed.Contains(f.Key)) is { Row: not null } row) Give(row, s);
        }
        return rows;
    }

    static bool IsWaves(string name, string vendor) =>
        PluginCatalog.IsWaves(name, "") || vendor.StartsWith("Waves", StringComparison.OrdinalIgnoreCase);

    /// <summary>A Waves shell: the one file every Waves plugin lives in.</summary>
    static bool IsShell(PluginSpaceInfo s) =>
        PluginCatalog.IsWaves(s.Name, "") || s.Folders.Any(f => f.Kind == SpaceKind.Program && PluginCatalog.IsWaves(s.Name, f.Path));

    static bool Matched(string a, string b) =>
        a.Length >= 4 && b.Length >= 4 && (a == b || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));

    static string FormatOf(PluginSpaceInfo s) =>
        s.Folders.Where(f => f.Kind == SpaceKind.Program).Select(f => TextScan.PluginFormatFromPath(f.Path)).FirstOrDefault(f => f.Length > 0) ?? "";

    /// <summary>The banner and cards: the biggest unused plugins, and what they all add up to.</summary>
    void BuildCandidates()
    {
        // Plugins no project has ever used come first (nothing goes missing when they go), biggest first.
        var unused = Items.Where(p => p.Unused && p.Bytes > 0).OrderByDescending(p => p.Projects == 0).ThenByDescending(p => p.Bytes).ToList();
        Candidates = unused.Take(3).Select(p => new SpaceCandidate(p, this)).ToList();
        FreeableText = unused.Count > 0 ? DiskSpace.Text(unused.Sum(p => p.Bytes)) : "";
        var years = UnusedYears == 1 ? "a year" : $"{UnusedYears} years";
        FreeableNote = unused.Count == 0 ? ""
            : $"from {(unused.Count == 1 ? "1 plugin" : $"{unused.Count} plugins")} none of your projects have used in over {years}. " +
              "Clean up removes its presets or sounds; Uninstall runs its own uninstaller.";
        SpaceNote = _db.PluginSpaceMeasured() is { } t ? $"Sizes measured {t:d MMM yyyy}." : "Sizes are measured in the background after the next scan.";
    }

    /// <summary>The side panel's Space tab for the picked plugin.</summary>
    void LoadSpace()
    {
        var space = Selected is { } p && _rowSpace.TryGetValue(p, out var s) ? s : null;
        HasSpace = space is not null;
        if (space is null || Selected is not { } plugin)
        {
            SpaceFolders = [];
            LoadMoved("");
            SpaceSummary = Selected is { Installed: false } ? "Not installed, so it takes no room." : Selected is null ? "" : "Not measured yet.";
            SpaceParts = [];
            SharedText = "";
            return;
        }
        var last = plugin.LastUsed is { } t ? $"last used {t:MMM yyyy}" + (_saves.Count > 0 ? $" in {_saves[0].Name}" : "") : "no project uses it";
        SpaceSummary = $"{DiskSpace.Text(space.Bytes)} on disk · {last}";
        SharedText = space.SharedBytes > 0 ? $"{DiskSpace.Text(space.SharedBytes)} more in folders it shares with other plugins" : "";
        var total = (double)Math.Max(1, space.Bytes);
        SpaceParts = new[]
        {
            new SpacePart("Plugin files", space.Program, "#5B9CF2", space.Program / total),
            new SpacePart("Presets and settings", space.Presets, "#B48CE8", space.Presets / total),
            new SpacePart("Sounds", space.Sounds, "#E0A640", space.Sounds / total),
        }.Where(p => p.Bytes > 0).ToList();
        SpaceFolders = space.Folders.OrderByDescending(f => f.Bytes).Select(f => new SpaceFolderItem(f, this, NamesOf(f.SharedWith))).ToList();
        LoadMoved(space.Key);
    }

    string NamesOf(IEnumerable<string> keys) =>
        string.Join(", ", keys.Select(k => _spaces.TryGetValue(k, out var s) ? s.Name : k));

    /// <summary>Measures every plugin again now (the button under the list).</summary>
    [RelayCommand]
    Task MeasureAgain() => _main.MeasureSpaceAsync(force: true);

    /// <summary>Adds a folder of the picked plugin's that the guess missed (a sound library on another drive).</summary>
    [RelayCommand]
    async Task AddSpaceFolder()
    {
        if (Selected is not { } p || !_rowSpace.TryGetValue(p, out var s)) return;
        if (await Ui.Current.PickFolder($"Choose a folder of {p.Name}'s (presets or sounds)") is not { } folder) return;
        var key = LibraryDb.AddedFoldersKey(s.Key);
        _db.SetList(key, _db.GetList(key).Append(folder).Distinct(StringComparer.OrdinalIgnoreCase));
        await RemeasureAsync(s.Key);
    }

    /// <summary>A guessed folder that isn't the plugin's: left out from now on (or a folder you added, taken off).</summary>
    internal async Task NotThisPlugins(SpaceFolder folder)
    {
        if (Selected is not { } p || !_rowSpace.TryGetValue(p, out var s)) return;
        if (folder.Added)
        {
            var key = LibraryDb.AddedFoldersKey(s.Key);
            _db.SetList(key, _db.GetList(key).Where(f => !f.Equals(folder.Path, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            var key = LibraryDb.RuledOutKey(s.Key);
            _db.SetList(key, _db.GetList(key).Append(folder.Path).Distinct(StringComparer.OrdinalIgnoreCase));
        }
        await RemeasureAsync(s.Key);
    }

    async Task RemeasureAsync(params string[] keys)
    {
        var meter = new SpaceMeter(_db);
        await Task.Run(() => meter.MeasurePlugins(keys));
        await ReloadAsync();
    }

    // ---- Clean up --------------------------------------------------------------------------------------------------

    /// <summary>Opens the Keep or Delete card for the picked plugin's presets and sounds.</summary>
    [RelayCommand]
    async Task CleanUp(PluginUsage? row)
    {
        if ((row ?? Selected) is not { } p || !_rowSpace.TryGetValue(p, out var space)) return;
        Selected = p;
        SideTab = 1;
        var meter = new SpaceMeter(_db);
        var protectedRoots = meter.ProtectedRoots();
        bool NoProjects(string key) =>
            _rowSpace.Where(r => r.Value.Key == key).All(r => r.Key.Projects == 0);
        // Why a plugin's folder has to stay because of how projects use the plugin, or "".
        string InUse(SpaceFolder f, PluginUsage p) =>
            f.Shared && !f.SharedWith.All(NoProjects) ? $"Shared with {NamesOf(f.SharedWith)}, which your projects use."
            : f.Kind == SpaceKind.Sounds && (p.Projects > 0 || !f.SharedWith.All(NoProjects))
                ? $"Sounds stay while a project uses {p.Name}: {(p.Projects == 1 ? "1 project does" : $"{p.Projects} projects do")}."
            : !p.Unused ? $"Your projects still use {p.Name}."
            : "";
        var folders = space.Folders.Where(f => f.Kind != SpaceKind.Program).OrderByDescending(f => f.Bytes).ToList();
        var spaces = _spaces.Values.ToList();
        // Which folders hold a sample a project uses, and which hold another listed folder (a maker's folder holds its
        // plugins' own, which were measured apart): read in the background (the samples table is big).
        // A sample found here under another path than the project's (an old PC's, a drive with a new letter) counts too.
        var (holding, inside) = await Task.Run(() =>
        {
            var direct = folders.ToDictionary(f => f.Path, f => _db.SampleInside(f.Path));
            var named = _db.SamplesNamedIn(folders.Where(f => direct[f.Path] is null).Select(f => f.Path).ToList());
            return (folders.ToDictionary(f => f.Path, f => direct[f.Path] ?? named.GetValueOrDefault(f.Path)),
                    folders.ToDictionary(f => f.Path, f => FoldersInside(f.Path, spaces)));
        });
        // Removing a folder removes the folders in it, so one holding a folder that has to stay stays too.
        string Holds(SpaceFolder f)
        {
            foreach (var (s, g) in inside[f.Path])
            {
                if (g.Kind == SpaceKind.Program) return $"It holds {Path.GetFileName(g.Path)}, {s.Name}'s plugin file: Uninstall removes that.";
                var rows = _rowSpace.Where(r => r.Value.Key == s.Key).Select(r => r.Key).ToList();
                // A plugin without a row of its own counts as used (see WithSpace).
                var why = rows.Count == 0 ? $"Your projects still use {s.Name}." : rows.Select(r => InUse(g, r)).FirstOrDefault(w => w.Length > 0);
                if (why is { Length: > 0 }) return $"It holds {Path.GetFileName(g.Path)}, which stays. {why}";
            }
            return "";
        }
        var items = folders.Select(f =>
        {
            var why =
                PluginSpace.IsInstallFolder(f.Path) ? "Inside Program Files: Uninstall removes this."
                : protectedRoots.FirstOrDefault(r => DiskSpace.IsInside(f.Path, r) || DiskSpace.IsInside(r, f.Path)) is { } mine ? $"Part of your folder {mine}."
                : holding[f.Path] is { } sample ? $"A project uses a sample in it ({Path.GetFileName(sample)})."
                : InUse(f, p) is { Length: > 0 } use ? use
                : Holds(f);
            return new CleanupItem(f, why, ticked: why.Length == 0 && f.Kind == SpaceKind.Sounds);
        }).ToList();
        var card = new SpaceCard(this)
        {
            Title = $"Clean up {p.Name}?",
            Intro = "Tick what to remove. Everything goes to the Recycle Bin, so you can still put it back.",
            Items = items,
            Warning = p.Projects > 0 ? $"{(p.Projects == 1 ? "1 project uses" : $"{p.Projects} projects use")} {p.Name}. Its presets are safe to remove: projects keep their own settings." : "",
            Empty = items.Count == 0 ? "No presets or sound folders were found for it. Use Uninstall, or add its folder on the Space tab." : "",
        };
        card.Confirm = () => DeleteAsync(card, space.Key);
        card.Refresh();
        Card = card;
    }

    /// <summary>The listed folders, any plugin's, that are inside this one (a maker's folder holds its plugins' own).</summary>
    internal static List<(PluginSpaceInfo Space, SpaceFolder Folder)> FoldersInside(string path, IReadOnlyList<PluginSpaceInfo> spaces) =>
        spaces.SelectMany(s => s.Folders.Where(g => DiskSpace.IsInside(g.Path, path) && !DiskSpace.IsInside(path, g.Path)).Select(g => (s, g))).ToList();

    async Task DeleteAsync(SpaceCard card, string key)
    {
        var picked = card.Items.Where(i => i.IsTicked).ToList();
        card.Busy = true;
        var problems = await Task.Run(() => RecycleBin.SendMany(picked.Select(i => i.Folder.Path).ToList()));
        var freed = picked.Where(i => !problems.ContainsKey(i.Folder.Path)).Sum(i => i.Folder.Bytes);
        Card = null;
        if (freed > 0) _main.Activities.Start("Cleaned up").Finish($"Freed {DiskSpace.Text(freed)}", "They're in the Recycle Bin if you want them back.", "Open Recycle Bin", _main.OpenRecycleBin);
        // One card for what stayed, not one each (a spare-copies clean up can hold hundreds).
        if (problems.Count > 0)
            _main.Activities.Failed(problems.Count == 1 ? $"Couldn't remove {Path.GetFileName(problems.First().Key)}" : $"{problems.Count} couldn't be removed",
                                    problems.First().Value);
        if (key.Length > 0) await RemeasureAsync(key);
        else await ReloadAsync();
    }

    // ---- Uninstall -------------------------------------------------------------------------------------------------

    /// <summary>Opens the card to run the picked plugin's own uninstaller (or, without one, bin its plugin files).</summary>
    [RelayCommand]
    async Task Uninstall(PluginUsage? row)
    {
        if ((row ?? Selected) is not { } p || !_rowSpace.TryGetValue(p, out var space)) return;
        Selected = p;
        SideTab = 1;
        var found = await Task.Run(() => Uninstallers.For(p.Name, p.Vendor, Uninstallers.All()));
        var files = space.Folders.Where(f => f.Kind == SpaceKind.Program).ToList();
        // Binning a WaveShell would take every Waves plugin with it.
        var shell = IsShell(space);
        var card = new SpaceCard(this)
        {
            Title = $"Uninstall {p.Name}?",
            Warning = p.Projects > 0 ? $"{(p.Projects == 1 ? "1 project uses" : $"{p.Projects} projects use")} it. They'll open with {p.Name} missing." : "",
            Empty = found.Count == 0 && shell ? "Every Waves plugin lives in this one file, so it isn't removed here. Use Waves Central to remove Waves plugins." : "",
        };
        if (found.Count > 0)
        {
            // Another version's uninstaller (Ozone 11's for Ozone 9) would remove a plugin you may still use: you pick it yourself.
            var other = Uninstallers.IsOtherVersion(p.Name, found[0]);
            card.Intro = "This runs its own uninstaller, the same one Windows Settings › Apps uses. It opens in its own window and may ask questions." +
                         (other ? $" These look like other versions of {p.Name}, not this one. Pick one only if you're sure it's the right one." : "");
            card.Uninstallers = found;
            card.MustPick = other;
            card.PickedUninstaller = other ? null : found[0];
            card.ConfirmText = "Uninstall";
            card.Confirm = () => RunUninstallerAsync(card, space.Key, files.Select(f => f.Path).ToList());
        }
        else if (!shell)
        {
            card.Intro = OperatingSystem.IsWindows()
                ? "Windows has no uninstaller for it that's still there. Its plugin files can go to the Recycle Bin instead (Windows may ask for administrator rights):"
                : "Its plugin files go to the Bin:";
            card.Items = files.Select(f => new CleanupItem(f, "", ticked: true)).ToList();
            card.Confirm = () => DeleteAsync(card, space.Key);
        }
        card.Refresh();
        Card = card;
    }

    /// <summary>How long Uninstall waits for a plugin's files to go before saying it's still installed.</summary>
    internal static TimeSpan UninstallWait = TimeSpan.FromMinutes(20);
    internal static TimeSpan UninstallPoll = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs the uninstaller, then waits for the plugin's files to go. Many uninstallers hand over to a copy of themselves
    /// and close at once, so the program closing says nothing: the files going does. Only then is the list read again, so
    /// an uninstall you cancel leaves the page exactly as it was (Solomon's F12 note, 25 Oct).
    /// </summary>
    async Task RunUninstallerAsync(SpaceCard card, string key, IReadOnlyList<string> files)
    {
        if (card.PickedUninstaller is not { } u) return;
        var (running, problem) = Uninstallers.Start(u);
        Card = null;
        if (problem.Length > 0)
        {
            _main.Activities.Failed($"Couldn't start {u.Name}'s uninstaller", problem);
            return;
        }
        using var stop = new CancellationTokenSource();
        var activity = _main.Activities.Start($"Uninstalling {u.Name}", "Its uninstaller is open in its own window.", cancel: stop.Cancel);
        static bool AllLeft(IReadOnlyList<string> paths) => paths.All(p => File.Exists(p) || Directory.Exists(p));
        try
        {
            if (files.Count == 0)
            {
                // Nothing to watch: go by the program closing, then read the list again.
                if (running is not null) await running.WaitForExitAsync(stop.Token);
            }
            else
            {
                var until = DateTime.UtcNow + UninstallWait;
                while (await Task.Run(() => AllLeft(files)))
                {
                    if (DateTime.UtcNow > until)
                    {
                        activity.Stop($"{u.Name} is still installed", "Its files are still there, so nothing on the page changed.");
                        return;
                    }
                    await Task.Delay(UninstallPoll, stop.Token);
                }
                // Files are going: give the uninstaller a moment to finish before the list is read.
                await Task.Delay(UninstallPoll * 2, stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            activity.Stop($"Stopped waiting for {u.Name}'s uninstaller", "Press Rescan once it's done to update the list.");
            return;
        }
        activity.Finish($"{u.Name} uninstalled", "Looking for what's still installed…");
        // The row it leaves: the one after it in the list gets picked, so you carry on from the same place.
        var next = NextShown(Selected);
        await _main.ScanAsync(false, plugins: true);
        await RemeasureAsync(key);
        if (next is not null && Selected is null)
            Selected = Items.FirstOrDefault(p => p.Name == next.Name && p.Own == next.Own);
    }

    /// <summary>The row after this one as the list shows it (or before it, at the end).</summary>
    PluginUsage? NextShown(PluginUsage? row)
    {
        if (row is null || View is null) return null;
        var shown = View.Cast<PluginUsage>().ToList();
        var at = shown.IndexOf(row);
        return at < 0 ? null : at + 1 < shown.Count ? shown[at + 1] : at > 0 ? shown[at - 1] : null;
    }

    // ---- Spare copies ----------------------------------------------------------------------------------------------

    IReadOnlyList<SpareCopy> _spares = [];
    [ObservableProperty] bool _hasSpares;
    [ObservableProperty] string _spareSizeText = "";
    [ObservableProperty] string _spareTitle = "";
    [ObservableProperty] string _spareNote = "";

    /// <summary>The spare copies line: plugin files in formats none of your projects use.</summary>
    void BuildSpares(IReadOnlyList<SpareCopy> spares)
    {
        _spares = spares;
        HasSpares = spares.Count > 0;
        SpareSizeText = DiskSpace.Text(spares.Sum(c => c.Bytes));
        SpareTitle = spares.Count == 1 ? "1 spare plugin copy" : $"{spares.Count} spare plugin copies";
        var formats = spares.GroupBy(c => c.Format).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}");
        SpareNote = $"{string.Join(", ", formats)}: copies in a format none of your projects use. The format they use stays.";
    }

    /// <summary>Opens the Keep or Delete card for the spare copies, every one ticked.</summary>
    [RelayCommand]
    void ReviewSpares()
    {
        if (_spares.Count == 0) return;
        var card = new SpaceCard(this)
        {
            Title = "Remove spare plugin copies?",
            Intro = "Each is a plugin in a format none of your projects use; the format they use stays installed. They go to the Recycle Bin." +
                    (OperatingSystem.IsWindows() ? " They're in Program Files, so Windows asks once for administrator rights." : ""),
            Items = _spares.Select(c => new CleanupItem(new SpaceFolder(c.Path, SpaceKind.Program, c.Bytes, [], false), "", ticked: true,
                                                        label: $"{c.Name} · {c.Format}", detail: c.Why)).ToList(),
        };
        card.Confirm = async () =>
        {
            // Checked again against your projects as they are now: the list is made once a week, and a song saved since
            // may use a format it calls spare.
            card.Busy = true;
            var now = await Task.Run(() => new SpaceMeter(_db).SpareCopiesNow());
            var still = now.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stale = card.Items.Where(i => i.IsTicked && !still.Contains(i.Folder.Path)).ToList();
            foreach (var i in stale) i.IsTicked = false;
            var kept = stale.Where(i => File.Exists(i.Path) || Directory.Exists(i.Path)).ToList();
            _db.SaveSpareCopies(now);
            if (kept.Count > 0)
                _main.Activities.Failed(kept.Count == 1 ? $"Kept {kept[0].Name}" : $"Kept {kept.Count} of the copies",
                                        "Your projects use those formats now, so they stay.");
            await DeleteAsync(card, "");
        };
        card.Refresh();
        Card = card;
    }

    internal void CloseCard() => Card = null;
}

/// <summary>A big unused plugin on a card over the list.</summary>
public sealed partial class SpaceCandidate(PluginUsage plugin, PluginsViewModel owner)
{
    public PluginUsage Plugin => plugin;
    public string Name => plugin.Name;
    public string SizeText => plugin.SizeText;
    /// <summary>"Last used Mar 2022 · 3 projects ever", or "Never used in any project".</summary>
    public string Line => plugin.Projects == 0 ? "Never used in any project"
        : $"Last used {plugin.LastUsedText} · {(plugin.Projects == 1 ? "1 project" : $"{plugin.Projects} projects")} ever";

    [RelayCommand] void Pick() => owner.Selected = plugin;
    [RelayCommand] Task CleanUp() => owner.CleanUpCommand.ExecuteAsync(plugin);
    [RelayCommand] Task Uninstall() => owner.UninstallCommand.ExecuteAsync(plugin);
}

/// <summary>One of the picked plugin's files or folders on its Space tab.</summary>
public sealed partial class SpaceFolderItem(SpaceFolder folder, PluginsViewModel owner, string sharedNames)
{
    public SpaceFolder Folder => folder;
    public string Path => folder.Path;
    public string SizeText => DiskSpace.Text(folder.Bytes);
    public string KindText => folder.Kind switch
    {
        SpaceKind.Program => "Plugin file",
        SpaceKind.Sounds => "Sounds",
        _ => "Presets and settings",
    } + (folder.Added ? " · added by you" : "");
    public string SharedText => folder.Shared ? $"Shared with {sharedNames}" : "";
    public bool IsShared => folder.Shared;
    /// <summary>Plugin files are found, not guessed: only folders can be ruled out.</summary>
    public bool CanRuleOut => folder.Kind != SpaceKind.Program;

    [RelayCommand] void Show() => Shell.Reveal(folder.Path);
    [RelayCommand] Task NotThis() => owner.NotThisPlugins(folder);
    /// <summary>Sounds and presets can live on another drive; plugin files stay where the DAWs look for them.</summary>
    public bool CanMove => folder.Kind != SpaceKind.Program;
    [RelayCommand] Task Move() => owner.MoveFolderAsync(folder);
}

/// <summary>A folder on a Clean up card: ticked to go, or why it has to stay.</summary>
public sealed partial class CleanupItem : ObservableObject
{
    public CleanupItem(SpaceFolder folder, string why, bool ticked, string? label = null, string detail = "")
    {
        Folder = folder;
        Why = why;
        _isTicked = ticked && why.Length == 0;
        _label = label;
        Detail = detail;
    }

    readonly string? _label;
    public SpaceFolder Folder { get; }
    public string Why { get; }
    /// <summary>Why it can go ("Your projects use its VST3 (412 projects); none use this VST2.").</summary>
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
    public bool CanTick => Why.Length == 0;
    public string Name => _label ?? Folder.Kind switch
    {
        SpaceKind.Program => "Plugin file",
        SpaceKind.Sounds => "Sounds",
        _ => "Presets and settings",
    };
    public string SizeText => DiskSpace.Text(Folder.Bytes);
    public string Path => Folder.Path;
    [ObservableProperty] bool _isTicked;
    internal SpaceCard? Card { get; set; }
    partial void OnIsTickedChanged(bool value) => Card?.Refresh();
}

/// <summary>The Keep or Delete card: what will go, how much it frees, and the two buttons.</summary>
public sealed partial class SpaceCard(PluginsViewModel owner) : ObservableObject
{
    public string Title { get; init; } = "";
    public string Intro { get; set; } = "";
    public string Warning { get; init; } = "";
    public bool HasWarning => Warning.Length > 0;
    public string Empty { get; init; } = "";
    public bool HasEmpty => Empty.Length > 0;

    IReadOnlyList<CleanupItem> _items = [];
    public IReadOnlyList<CleanupItem> Items
    {
        get => _items;
        set
        {
            _items = value;
            foreach (var i in value) i.Card = this;
        }
    }

    /// <summary>For Uninstall: the uninstallers that may be this plugin's, best first, and the one picked.</summary>
    public IReadOnlyList<Uninstaller> Uninstallers { get; set; } = [];
    public bool HasUninstallers => Uninstallers.Count > 0;
    public bool HasChoice => Uninstallers.Count > 1 || MustPick;
    /// <summary>None is picked for you (they're for other versions): even a single one is yours to pick.</summary>
    public bool MustPick { get; set; }
    [ObservableProperty] Uninstaller? _pickedUninstaller;
    partial void OnPickedUninstallerChanged(Uninstaller? value) => Refresh();

    /// <summary>"Delete 35.5 GB", or "Uninstall".</summary>
    [ObservableProperty] string _confirmText = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand))] bool _canConfirm;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand))] bool _busy;
    internal Func<Task>? Confirm { get; set; }
    bool _fixedText;

    internal void Refresh()
    {
        if (HasUninstallers)
        {
            _fixedText = true;
            CanConfirm = PickedUninstaller is not null;
            return;
        }
        if (_fixedText)
        {
            CanConfirm = true;   // Move, Move back: the ticks are extras
            return;
        }
        var bytes = Items.Where(i => i.IsTicked).Sum(i => i.Folder.Bytes);
        ConfirmText = bytes > 0 ? $"Delete {DiskSpace.Text(bytes)}" : "Delete";
        CanConfirm = bytes > 0;
    }

    /// <summary>A card whose button always reads the same ("Move 4.5 GB"), ready to press.</summary>
    internal void Fix(string confirmText)
    {
        _fixedText = true;
        ConfirmText = confirmText;
        CanConfirm = true;
    }

    [RelayCommand] void Keep() => owner.CloseCard();

    [RelayCommand(CanExecute = nameof(CanRun))]
    Task Run() => Confirm?.Invoke() ?? Task.CompletedTask;

    bool CanRun() => CanConfirm && !Busy;
}
