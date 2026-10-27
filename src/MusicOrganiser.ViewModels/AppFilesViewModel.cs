using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Space;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// The app's own files on Settings › General: how big the library and the other databases are, and how much room is left
/// on their drive, with a warning when it's nearly full: on Settings, as a sign on the left bar, and once as a message
/// (Solomon's F12 note, 27 Oct: "add way to see the size of the database the app uses to hold values and make sure it
/// warns you if the disk its on is nearly full").
/// </summary>
public sealed partial class AppFilesViewModel : ObservableObject
{
    /// <summary>How often the room left is checked while the app is open (asking the drive takes no time at all).</summary>
    public static TimeSpan CheckEvery { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Asks a drive its name, room left and size (tests stand in a nearly full one).</summary>
    internal static Func<string, (string Drive, long Free, long Total)> DriveSpace = AppFiles.DriveSpace;

    readonly MainViewModel _main;
    readonly string _folder;
    readonly IReadOnlyList<AppDatabase> _databases;
    IUiTimer? _timer;
    // What the app's files took when last measured (unknown until Settings first opens), and whether the message has
    // been shown since the drive last had room.
    long _appBytes;
    bool _warned;

    public AppFilesViewModel(MainViewModel main, string folder, IReadOnlyList<AppDatabase> databases)
    {
        _main = main;
        _folder = folder;
        _databases = databases;
    }

    /// <summary>Each part, biggest first: the databases, the search models, the logs (drawn as one bar, as a song's room is).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasParts))] IReadOnlyList<SpacePart> _parts = [];
    public bool HasParts => Parts.Count > 0;
    /// <summary>"8.9 GB in all".</summary>
    [ObservableProperty] string _totalText = "";
    /// <summary>"C: has 120 GB free of 500 GB."</summary>
    [ObservableProperty] string _driveText = "";
    /// <summary>The drive the library is on is nearly full (see <see cref="AppFiles.IsLow"/>).</summary>
    [ObservableProperty] bool _isLow;
    [ObservableProperty] string _lowText = "";
    /// <summary>The left bar's sign: the drive ("C:"), and what's wrong and what a click does.</summary>
    [ObservableProperty] string _railText = "";
    [ObservableProperty] string _railTip = "";
    [ObservableProperty] bool _isMeasuring;

    // Each part's colour on the bar: the library in the blue a song's saves have, the rest told apart.
    static readonly Dictionary<string, string> Colours = new()
    {
        ["Your library"] = "#5B9CF2", ["Sound Lab"] = "#E0A640", ["MIDI parts"] = "#B48CE8", ["Search models"] = "#4FB6A8",
        ["Playground sound"] = "#E07A9A", ["Playground ideas"] = "#7BC77B", ["Logs"] = "#C9B458", ["Notes for Claude"] = "#6FB3D9",
        ["Test reports"] = "#A98F7A", ["Everything else"] = "#8D939C",
    };

    /// <summary>The folder the databases and the rest are in.</summary>
    public string Folder => _folder;

    string DrivePath => _databases.Count > 0 ? _databases[0].Path : _folder;

    /// <summary>Starts checking the room left: now, then every few minutes. The app does this once its window shows.</summary>
    public void Start()
    {
        _ = CheckAsync();
        _timer ??= Ui.Current.CreateTimer(CheckEvery, () => _ = CheckAsync());
        _timer.Start();
    }

    public void Stop() => _timer?.Stop();

    /// <summary>Asks the drive how much room is left, and says so once when it's nearly full.</summary>
    public async Task CheckAsync()
    {
        var (drive, free, total) = await Task.Run(() => DriveSpace(DrivePath));
        ShowRoom(drive, free, total, tell: true);
    }

    /// <summary>Measures every part in the background (when Settings opens): the app's folder holds a few hundred files.</summary>
    public async Task MeasureAsync()
    {
        if (IsMeasuring) return;
        IsMeasuring = true;
        try
        {
            var (parts, (drive, free, total)) = await Task.Run(() => (AppFiles.Measure(_folder, _databases), DriveSpace(DrivePath)));
            _appBytes = parts.Sum(p => p.Bytes);
            var whole = (double)Math.Max(1, _appBytes);
            Parts = parts.Select(p => new SpacePart(p.Name, p.Bytes, Colours.GetValueOrDefault(p.Name, "#8D939C"), p.Bytes / whole, p.What)).ToList();
            TotalText = $"{DiskSpace.Text(_appBytes)} in all";
            ShowRoom(drive, free, total, tell: false);
        }
        finally
        {
            IsMeasuring = false;
        }
    }

    void ShowRoom(string drive, long free, long total, bool tell)
    {
        if (free < 0)
        {
            DriveText = LowText = RailTip = "";
            IsLow = false;
            return;
        }
        var name = drive.Length > 0 ? drive : "The drive";
        DriveText = $"{name} has {DiskSpace.Text(free)} free of {DiskSpace.Text(total)}.";
        RailText = drive.Length is > 0 and <= 3 ? drive : "Disk";
        IsLow = AppFiles.IsLow(free, _appBytes);
        if (!IsLow)
        {
            LowText = RailTip = "";
            _warned = false;   // room again: if it fills up later, that's said again
            return;
        }
        LowText = free < AppFiles.VeryLowSpace
            ? $"{name} is almost full: {DiskSpace.Text(free)} left. The library may already be unable to save what scans find. Free some room now."
            : $"{name} is nearly full: {DiskSpace.Text(free)} left. Once it's full, the library can't save what scans find. Free some room soon.";
        RailTip = $"{name} is nearly full: {DiskSpace.Text(free)} left, and the app's library is on it. Click to see what the app keeps there.";
        if (!tell || _warned) return;
        _warned = true;
        _main.Activities.Failed($"{name} is nearly full",
                                $"Only {DiskSpace.Text(free)} left, and the app's library is on it. The Plugins page shows what you could free.");
    }

    /// <summary>Measure again: the button on Settings.</summary>
    [RelayCommand]
    Task MeasureAgain() => MeasureAsync();

    [RelayCommand]
    void OpenFolder() => Shell.Open(_folder);

    /// <summary>Free up space…: the Plugins page, where spare copies, unused plugins and old copies can go.</summary>
    [RelayCommand]
    void FreeUpSpace() => _main.SelectedNav = _main.Nav.First(n => n.Page == _main.Plugins);

    /// <summary>The left bar's sign: Settings › General, where the parts and the room left are.</summary>
    [RelayCommand]
    void Show()
    {
        _main.Settings.Tab = "General";
        _main.ShowSettings();
    }
}
