using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// Bitwig and FL Studio as they are right now, through their links: shown in the sidebar, marked on the project each
/// has open, and followed so that a project is re-read as soon as its DAW saves it. Every couple of seconds it asks
/// both (<see cref="DawWatch"/>), which is quick.
/// </summary>
public sealed partial class LiveDawsViewModel : ObservableObject
{
    static readonly TimeSpan Every = TimeSpan.FromSeconds(2);
    /// <summary>How long a save is followed while the app waits for the DAW to finish writing the file.</summary>
    static readonly TimeSpan SaveWait = TimeSpan.FromSeconds(15);
    /// <summary>At most one scan for new projects this often, for saves of projects the library doesn't have yet.</summary>
    static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(60);

    readonly LibraryDb _db;
    readonly MainViewModel _main;
    readonly Dictionary<string, DawState> _states = [];
    readonly Dictionary<string, ProjectSummary?> _open = [];
    readonly Dictionary<string, Saving> _saving = [];
    IUiTimer? _timer;
    bool _polling;
    DateTime _lastScan = DateTime.MinValue;

    /// <summary>A save being followed: what the DAW showed then, when it was seen, and the file as it was at the last look.</summary>
    sealed record Saving(DawState State, DateTime Seen, (long Mtime, long Size)? Last = null);

    public DawWatch Watch { get; }

    /// <summary>The sidebar's rows: Bitwig, then FL Studio.</summary>
    public IReadOnlyList<LiveDawRow> Rows { get; }

    public LiveDawsViewModel(LibraryDb db, MainViewModel main, DawWatch watch)
    {
        _db = db;
        _main = main;
        Watch = watch;
        Rows = [new LiveDawRow(DawState.Bitwig, this), new LiveDawRow(DawState.FlStudio, this)];
        foreach (var row in Rows) row.Show(new DawState(row.Daw, false), null);
    }

    /// <summary>Starts asking the DAWs every couple of seconds.</summary>
    public void Start()
    {
        _timer ??= Ui.Current.CreateTimer(Every, () => _ = PollAsync());
        _timer.Start();
    }

    /// <summary>What a DAW showed at the last look, or null before the first.</summary>
    public DawState? State(string daw) => _states.GetValueOrDefault(daw);

    public bool IsAnswering(string daw) => State(daw)?.Answering == true;

    /// <summary>The library project a DAW has open, if the library has it.</summary>
    public ProjectSummary? OpenIn(string daw) => _open.GetValueOrDefault(daw);

    /// <summary>Asks both DAWs what they show, and follows up: the sidebar, the open projects, saves. Skipped while the last look is still going.</summary>
    public async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            IReadOnlyList<DawState> states;
            try { states = await Watch.PollAsync(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException) { return; }

            var changed = false;
            foreach (var state in states)
            {
                var before = State(state.Daw);
                _states[state.Daw] = state;
                if (state.SavedSince(before)) _saving[state.Daw] = new Saving(state, DateTime.UtcNow);
                if (before is null || before.Project != state.Project || before.Answering != state.Answering) changed = true;
            }
            if (changed) Match();
            else Show();
            await FollowSavesAsync();
        }
        finally { _polling = false; }
    }

    /// <summary>Finds the library project each DAW has open, and shows it. Called when what they show or the library changes.</summary>
    public void Match()
    {
        foreach (var (daw, state) in _states) _open[daw] = state.FindIn(_db);
        MarkProjects();
        Show();
    }

    void Show()
    {
        foreach (var row in Rows) row.Show(State(row.Daw) ?? new DawState(row.Daw, false), OpenIn(row.Daw));
        _main.Settings.ShowLive(this);
        ShowOn(_main.Projects.Detail);
    }

    /// <summary>Marks the library's rows for the projects open now. The library calls it when it reloads its rows.</summary>
    public void MarkProjects()
    {
        foreach (var item in _main.Projects.Items)
        {
            var mark = "";
            foreach (var (daw, open) in _open)
            {
                if (open is null || !IsAnswering(daw)) continue;
                if (item.Id == open.Id) mark = $"Open in {Short(daw)}";
                else if (item.OlderSaves.Any(o => o.Id == open.Id)) mark = $"Older save open in {Short(daw)}";
            }
            item.OpenIn = mark;
        }
    }

    /// <summary>Tells a project's page whether its DAW has it open, which checks it there by itself the first time.</summary>
    public void ShowOn(ProjectDetailViewModel? detail)
    {
        if (detail is null) return;
        var daw = detail.Item.Daw;
        var state = State(daw);
        if (state is not { Answering: true } || OpenIn(daw) is not { } open)
        {
            detail.ShowLive("", false);
            return;
        }
        if (open.Id == detail.Item.Id)
            detail.ShowLive(state.Modified == true ? $"Open in {Short(daw)} now, with unsaved changes" : $"Open in {Short(daw)} now", true);
        else if (detail.Item.OlderSaves.Any(o => o.Id == open.Id))
            detail.ShowLive($"{Short(daw)} has an older save open: {open.Name}", false);
        else
            detail.ShowLive("", false);
    }

    /// <summary>
    /// Re-reads a project its DAW has just saved, once the file has been written and stays the same for a moment. A
    /// project the library doesn't have yet (saved for the first time, or saved as a new name) is found by a scan.
    /// </summary>
    async Task FollowSavesAsync()
    {
        foreach (var (daw, saving) in _saving.ToList())
        {
            var waited = DateTime.UtcNow - saving.Seen;
            var project = saving.State.FindIn(_db);
            if (project is null)
            {
                if (waited < TimeSpan.FromSeconds(3)) continue;   // let the DAW finish writing it first
                _saving.Remove(daw);
                // A scan running now finds it, but reading plugins' parameters doesn't: that makes way for the scan.
                if (DateTime.UtcNow - _lastScan < ScanEvery || _main.IsScanning && !_main.IsReadingParameters) continue;
                _lastScan = DateTime.UtcNow;
                _ = ScanForSaveAsync();
                continue;
            }
            (long Mtime, long Size)? now = null;
            try
            {
                var file = new FileInfo(project.Path);
                if (file.Exists) now = (new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds(), file.Length);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (now is null || now == (project.Mtime, project.Size) || now != saving.Last)
            {
                // Not written yet (or written and still changing): look again next time, for a while.
                if (waited > SaveWait) _saving.Remove(daw);
                else _saving[daw] = saving with { Last = now };
                continue;
            }
            _saving.Remove(daw);
            await RereadAsync(project, daw);
        }
    }

    /// <summary>
    /// The scan for a saved project the library doesn't have yet, which the looks at the DAWs don't wait for: a scan of a
    /// big library takes a while, and the sidebar and other saves carry on meanwhile.
    /// </summary>
    async Task ScanForSaveAsync()
    {
        await _main.ScanAsync(false, plugins: false);
        Match();
    }

    async Task RereadAsync(ProjectSummary project, string daw)
    {
        try
        {
            var path = project.Path;
            await Task.Run(() => _db.Upsert(Scanner.Analyse(path)));
            _main.ScanStatus = $"Re-read {project.Name} after {Short(daw)} saved it · {DateTime.Now:HH:mm}";
            _main.RefreshAll();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _main.ScanStatus = $"Couldn't re-read {project.Name} after {Short(daw)} saved it: {e.Message}";
        }
    }

    /// <summary>The DAW's name as people say it: Bitwig, FL Studio.</summary>
    public static string Short(string daw) => daw == DawState.Bitwig ? "Bitwig" : daw;

    internal void Open(LiveDawRow row)
    {
        if (OpenIn(row.Daw) is { } project && IsAnswering(row.Daw)) _main.Projects.Reveal(project.Id);
        else _main.ShowSettings();
    }
}

/// <summary>One DAW in the sidebar: what it has open, or why it isn't connected.</summary>
public sealed partial class LiveDawRow(string daw, LiveDawsViewModel owner) : ObservableObject
{
    public string Daw { get; } = daw;
    public string Name { get; } = LiveDawsViewModel.Short(daw);

    /// <summary>The open project's name, or how the DAW stands: "Not open", "Open, but not linked".</summary>
    [ObservableProperty] string _text = "";
    /// <summary>A second line: "Unsaved changes", "Not in your library".</summary>
    [ObservableProperty] string _detail = "";
    [ObservableProperty] string _tip = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Colour))] bool _answering;

    /// <summary>The DAW whose colour the dot takes: its own while it answers, grey ("") while it doesn't.</summary>
    public string Colour => Answering ? Daw : "";

    internal void Show(DawState state, ProjectSummary? open)
    {
        Answering = state.Answering;
        if (state.Answering)
        {
            Text = state.Project.Length > 0 ? state.Project : "A new project, not saved yet";
            var notes = new List<string>();
            if (state.Modified == true) notes.Add("Unsaved changes");
            if (open is null && state.Project.Length > 0) notes.Add("Not in your library");
            Detail = string.Join(" · ", notes);
            Tip = open is not null ? $"Show {open.Name} in the library" : $"{state.Version}, through the Music Organiser link";
            return;
        }
        (Text, Tip) = state.Running switch
        {
            true => ("Open, but not linked", $"{Name} is open, but its link isn't answering. Settings > DAW links sets it up."),
            false => ("Not open", $"The app connects to {Name} by itself when it's open."),
            _ => ("Not connected", $"{Name} isn't answering: it isn't open, or its link isn't set up yet (Settings > DAW links)."),
        };
        Detail = "";
    }

    /// <summary>Shows the open project in the library, or Settings when there's no link.</summary>
    [RelayCommand]
    void Open() => owner.Open(this);
}
