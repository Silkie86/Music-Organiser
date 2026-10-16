using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.ViewModels;

// The sound galaxy: every sound a star, sound-alikes close together (see GalaxyMap). Sweeping the mouse over the
// stars plays them; the last ones you passed stay on a trail, which can become a kit.
public sealed partial class SoundsViewModel
{
    public const string Galaxy = "Galaxy";
    public const string GalaxyColourKey = "galaxy_colour";
    public const string GalaxyBasisKey = "galaxy_basis";

    /// <summary>What places the stars. Both is the default; without CLAP's listening it falls back to the measurements, with a warning.</summary>
    public static IReadOnlyList<string> PlacedByChoices { get; } = [SoundOrganiser.Both, SoundOrganiser.ClapOnly, SoundOrganiser.OwnOnly];
    public const int TrailLength = 16;

    /// <summary>What the stars' colours show.</summary>
    public static IReadOnlyList<string> ColourChoices { get; } = ["Type", "Key", "Pack", "Liked", "Used", "Age"];

    public bool IsGalaxy => ViewMode == Galaxy;
    /// <summary>Order and Shuffle mean something for tiles and the list only.</summary>
    public bool ShowsOrder => IsTiles || IsList;

    [ObservableProperty] string _colourBy = "Type";
    [ObservableProperty] IReadOnlyList<GalaxyStar> _stars = [];
    [ObservableProperty] IReadOnlyList<GalaxyRegion> _regions = [];
    [ObservableProperty] IReadOnlyList<LegendItem> _legend = [];
    [ObservableProperty] string _legendNote = "";
    /// <summary>The sounds you swept past, newest first.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasTrail), nameof(TrailDrag), nameof(TrailSummary))]
    IReadOnlyList<Sound> _trail = [];
    [ObservableProperty] string _galaxyNote = "";
    [ObservableProperty] string _placedBy = SoundOrganiser.Both;
    /// <summary>Shown above the galaxy when CLAP was asked for but hasn't analysed the sounds; empty otherwise.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasGalaxyWarning))] string _galaxyWarning = "";
    /// <summary>"Download CLAP" or "Deep listen": the button beside the warning.</summary>
    [ObservableProperty] string _galaxyWarningAction = "";
    public bool HasGalaxyWarning => GalaxyWarning.Length > 0;
    /// <summary>Which tab of the Sounds page is showing (0 Library, 1 Rediscover, 2 Kit builder…).</summary>
    [ObservableProperty] int _tabIndex;

    public bool HasTrail => Trail.Count > 0;
    public TrailDrag TrailDrag => new(Trail.Reverse().ToList());
    public string TrailSummary => Trail.Count == 1 ? "1 sound" : $"{Trail.Count} sounds";
    public int ShownStars => Stars.Count(s => s.Shown);

    /// <summary>The selected sound's six closest sound-alikes, joined to it by lines in the galaxy.</summary>
    public IReadOnlyList<Sound> Constellation => Similar.Take(6).Select(x => x.Sound).ToList();

    partial void OnSimilarChanged(IReadOnlyList<SimilarSound> value) => OnPropertyChanged(nameof(Constellation));

    partial void OnColourByChanged(string value)
    {
        _db.SetSetting(GalaxyColourKey, value);
        BuildStars();
    }

    partial void OnPlacedByChanged(string value)
    {
        _db.SetSetting(GalaxyBasisKey, value);
        _galaxyFailed = false;
        BuildStars();
        _ = MapGalaxyIfNeeded();
    }

    /// <summary>The warning's button: download CLAP, or let it listen to the sounds it hasn't heard.</summary>
    [RelayCommand]
    async Task FixGalaxyWarning()
    {
        if (!ClapModel.IsInstalled()) await DownloadModel();
        if (ClapModel.IsInstalled()) await DeepListen();
    }

    bool _galaxyFailed;
    // The saved map, kept in memory: reading 300,000 positions back on every filter change would be slow.
    Dictionary<long, StarPoint>? _map;

    /// <summary>Works the map out (or adds new sounds to it) when it's behind the library, in the background.</summary>
    async Task MapGalaxyIfNeeded()
    {
        if (_galaxyFailed || IsBusy || !Org.GalaxyOutOfDate(PlacedBy)) return;
        var failed = true;
        await RunAsync("Mapping your sound galaxy", (p, ct) => { _map = Org.UpdateGalaxy(PlacedBy, p, ct); failed = false; },
                       () => ("Sound galaxy mapped", $"{Org.Sounds.Count:N0} sounds placed by how they sound"));
        _galaxyFailed = failed;
    }

    [RelayCommand]
    async Task Remap()
    {
        _galaxyFailed = false;
        Org.Library.SetSetting(SoundOrganiser.GalaxySpaceKey, null);   // forces a fresh layout
        await MapGalaxyIfNeeded();
    }

    int _starsVersion;

    sealed record StarsBuilt(Dictionary<long, StarPoint> Map, List<GalaxyStar> Stars, IReadOnlyList<GalaxyRegion> Regions,
                             string Space, bool HasDeepListening, int NotDeepListened);

    /// <summary>
    /// The stars, with this colour scheme and the current filters (sounds filtered out stay as faint dust). Worked out
    /// off the window's thread: with 357,000 sounds reading the map and colouring every star held the window up for seconds.
    /// </summary>
    async void BuildStars()
    {
        if (!IsGalaxy) return;
        var version = ++_starsVersion;
        var (items, known, colourBy, placedBy, keep) = (Items, _map, ColourBy, PlacedBy, MakeFilter());
        try
        {
            var built = await UiWork.Run(() =>
            {
                var map = known ?? Org.Library.Map();
                var newest = colourBy != "Age" || items.Count == 0 ? []
                    : items.OrderBy(s => s.Added).Select((s, i) => (s.Id, i)).ToDictionary(t => t.Id, t => t.i);
                var stars = new List<GalaxyStar>(map.Count);
                foreach (var s in items)
                {
                    if (!map.TryGetValue(s.Id, out var p)) continue;
                    var colour = colourBy switch
                    {
                        "Key" => StarColours.Key(s),
                        "Pack" => StarColours.Pack(s.Folder),
                        "Liked" => StarColours.Liked(s.LikedShare),
                        "Used" => StarColours.Used(s.UsedIn),
                        "Age" => StarColours.Age(items.Count > 1 ? newest[s.Id] / (double)(items.Count - 1) : 1),
                        _ => StarColours.Type(s.Type),
                    };
                    stars.Add(new GalaxyStar(s, p.X, p.Y, colour, keep(s)));
                }
                return new StarsBuilt(map, stars, FindRegions(stars, colourBy), Org.GalaxySpace(placedBy),
                                      Org.HasDeepListening, Org.NotDeepListened);
            });
            if (version != _starsVersion) return;   // something changed meanwhile: newer stars are on their way
            if (_map is null || ReferenceEquals(_map, known)) _map = built.Map;
            Stars = built.Stars;
            Regions = built.Regions;
            BuildLegend(built.Stars);
            OnPropertyChanged(nameof(ShownStars));
            ShowGalaxyNotes(built);
        }
        catch (Exception e)
        {
            Message = $"Couldn't draw the galaxy: {e.Message}";
        }
    }

    void ShowGalaxyNotes(StarsBuilt built)
    {
        GalaxyNote = Items.Count == 0 ? "Scan your sample folders and every sound becomes a star here."
            : built.Map.Count == 0 ? "Mapping your sounds…"
            : built.Space switch
            {
                "clap" => "Placed by CLAP's listening: close stars are the same kind of sound.",
                "clap+features" => "Placed by CLAP and the app's own measurements: close stars sound alike.",
                _ => "Placed by the app's own measurements: close stars sound alike.",
            };
        var todo = built.NotDeepListened;
        (GalaxyWarning, GalaxyWarningAction) =
            PlacedBy == SoundOrganiser.OwnOnly || Items.Count == 0 ? ("", "")
            : !ClapModel.IsInstalled() ? ("CLAP isn't installed on this computer, so the stars are placed by the app's own measurements.", "Download CLAP")
            : !built.HasDeepListening ? ($"CLAP hasn't analysed your sounds yet ({todo:N0} to go), so the stars are placed by the app's own measurements.", "Deep listen")
            : todo > 0 ? ($"CLAP hasn't analysed {todo:N0} newer sound{(todo == 1 ? "" : "s")} yet; {(todo == 1 ? "it's" : "they're")} placed by the app's own measurements.", "Deep listen")
            : ("", "");
    }

    void BuildLegend(IReadOnlyList<GalaxyStar> stars)
    {
        var shown = stars.Where(s => s.Shown).ToList();
        switch (ColourBy)
        {
            case "Key":
                Legend = Enumerable.Range(1, 12).Select(n => new LegendItem($"{n}", StarColours.Hex(StarColours.Camelot(n, true)))).ToList();
                LegendNote = "Camelot wheel: neighbouring colours mix well";
                break;
            case "Pack":
                Legend = shown.GroupBy(s => s.Sound.Folder).OrderByDescending(g => g.Count()).Take(8)
                    .Select(g => new LegendItem(g.Key, StarColours.Hex(StarColours.Pack(g.Key)))).ToList();
                LegendNote = "The biggest packs";
                break;
            case "Liked":
                Legend = [new("Not yet", StarColours.Hex(StarColours.Liked(0))), new("A little", StarColours.Hex(StarColours.Liked(0.15))),
                          new("Most liked", StarColours.Hex(StarColours.Liked(1)))];
                LegendNote = "Bigger stars are more liked";
                break;
            case "Used":
                Legend = [new("Never used", StarColours.Hex(StarColours.Used(0))), new("In a project", StarColours.Hex(StarColours.Used(1))),
                          new("In many", StarColours.Hex(StarColours.Used(8)))];
                LegendNote = "Lavender stars are waiting to be found";
                break;
            case "Age":
                Legend = [new("Oldest", StarColours.Hex(StarColours.Age(0))), new("Newest", StarColours.Hex(StarColours.Age(1)))];
                LegendNote = "When the file was added";
                break;
            default:
                Legend = shown.GroupBy(s => s.Sound.Type).OrderByDescending(g => g.Count()).Take(10)
                    .Select(g => new LegendItem(g.Key, StarColours.Hex(StarColours.Type(g.Key)))).ToList();
                LegendNote = "";
                break;
        }
    }

    /// <summary>
    /// Names the galaxy's neighbourhoods: the map is cut into a 10 × 10 grid, each square takes the type (or pack) most of
    /// its stars share, and touching squares with the same answer become one region, labelled at its middle.
    /// </summary>
    internal static IReadOnlyList<GalaxyRegion> FindRegions(IReadOnlyList<GalaxyStar> stars, string colourBy)
    {
        if (colourBy is not ("Type" or "Pack")) return [];
        // Finer squares and bigger minimums for a big library, so its families still get their own names.
        var grid = (int)Math.Clamp(Math.Sqrt(stars.Count) / 20, 10, 24);
        var least = Math.Max(5, stars.Count / 3000);
        string Name(GalaxyStar s) => colourBy == "Pack" ? s.Sound.Folder : s.Sound.Type;
        var cells = stars.Where(s => s.Shown && Name(s) is not ("" or "Unknown"))
            .GroupBy(s => (X: Math.Min(grid - 1, (int)(s.X * grid)), Y: Math.Min(grid - 1, (int)(s.Y * grid))))
            .Select(g => (g.Key, Top: g.GroupBy(Name).MaxBy(t => t.Count())!, Count: g.Count()))
            .Where(c => c.Count >= 3 && c.Top.Count() >= c.Count * 0.5)
            .ToDictionary(c => c.Key, c => (Name: c.Top.Key, Stars: c.Top.ToList()));
        var regions = new List<GalaxyRegion>();
        var seen = new HashSet<(int, int)>();
        foreach (var start in cells.Keys.OrderBy(k => k.Y).ThenBy(k => k.X))
        {
            if (!seen.Add(start)) continue;
            var name = cells[start].Name;
            var members = new List<GalaxyStar>();
            var queue = new Queue<(int X, int Y)>([start]);
            while (queue.TryDequeue(out var c))
            {
                members.AddRange(cells[c].Stars);
                foreach (var n in new[] { (c.X + 1, c.Y), (c.X - 1, c.Y), (c.X, c.Y + 1), (c.X, c.Y - 1) })
                    if (cells.TryGetValue(n, out var other) && other.Name == name && seen.Add(n)) queue.Enqueue(n);
            }
            if (members.Count < least) continue;
            var colour = colourBy == "Pack" ? StarColours.Pack(name) : StarColours.Type(name);
            regions.Add(new GalaxyRegion(name, members.Average(m => m.X), members.Min(m => m.Y), members.Count, colour,
                                         members.Min(m => m.X), members.Max(m => m.X), members.Max(m => m.Y)));
        }
        // The biggest few, so the labels never crowd the stars.
        return regions.OrderByDescending(r => r.Count).Take(16).ToList();
    }

    PreviewWorker? _previews;
    /// <summary>Sweep previews start off the UI thread, so the mouse never waits for a file to open.</summary>
    PreviewWorker Previews => _previews ??= new PreviewWorker(_player,
        (path, e) => Message = $"Can't play {System.IO.Path.GetFileName(path)}: {e.Message}");

    /// <summary>
    /// The mouse, held down, swept onto a star (or an arrow key hopped to one): a preview (not a deliberate play),
    /// and onto the trail.
    /// </summary>
    [RelayCommand]
    void Sweep(Sound? s)
    {
        if (s is null) return;
        _preview = s.Path;
        Previews.Play(s.Path);
        _clock.Start();
        Trail = [s, .. Trail.Where(t => t != s).Take(TrailLength - 1)];
    }

    /// <summary>The mouse was let go after sweeping: the preview stops.</summary>
    [RelayCommand]
    void EndSweep()
    {
        if (_preview is null) return;
        Previews.Stop();
        _preview = null;
    }

    /// <summary>A click on a star: picks it, and counts as playing it on purpose.</summary>
    [RelayCommand]
    void PickStar(Sound? s)
    {
        if (s is null) return;
        Selected = s;
        try
        {
            if (_player.Playing == s.Path && _preview == s.Path) { Count(s); _preview = null; }
            else
            {
                _previews?.Cancel();
                Start(s, 0);
            }
        }
        catch (Exception e) { Message = $"Can't play {s.Name}: {e.Message}"; }
        if (!Trail.Contains(s)) Trail = [s, .. Trail.Take(TrailLength - 1)];
    }

    [RelayCommand]
    void ClearTrail() => Trail = [];

    /// <summary>Turns the trail into a kit: one locked pad per sound, oldest first, then opens the Kit builder.</summary>
    [RelayCommand]
    void TrailToKit()
    {
        if (Trail.Count == 0) return;
        Kit.Slots.Clear();
        foreach (var s in Trail.Reverse())
            Kit.Slots.Add(new KitSlotItem(new KitSlot { Name = s.Type, Types = [s.Type], Sound = s, Locked = true }));
        Kit.Status = $"Made from your galaxy trail: {Kit.Slots.Count} pads, locked so a re-roll keeps them.";
        TabIndex = 2;
    }
}

/// <summary>A star on the galaxy map: 0..1 across and down, its colour (0xAARRGGBB), and whether the filters show it.</summary>
public sealed record GalaxyStar(Sound Sound, float X, float Y, uint Colour, bool Shown);

/// <summary>
/// A labelled neighbourhood of the galaxy ("KICK"), centred over its stars: X is their middle, Y their top edge.
/// Left, Right and Bottom finish the box around them, so clicking the name can fly the view in to fit it.
/// </summary>
public sealed record GalaxyRegion(string Name, double X, double Y, int Count, uint Colour, double Left, double Right, double Bottom);

/// <summary>One colour in the galaxy's key, as a #AARRGGBB string for the view.</summary>
public sealed record LegendItem(string Label, string Colour);

/// <summary>The whole trail, to drag into a DAW in one go (oldest first).</summary>
public sealed record TrailDrag(IReadOnlyList<Sound> Sounds);
