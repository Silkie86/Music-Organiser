using System.Collections.ObjectModel;
using MusicOrganiser.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MusicOrganiser.ViewModels;

public enum ActivityState { Running, Done, Failed, Stopped }

/// <summary>
/// The window's activity cards (bottom right): one per job, so it's always clear that something started, how far it
/// has got, and that it finished. A card for a finished job fades away by itself; a failed one stays a little longer.
/// </summary>
public sealed class ActivityFeed
{
    /// <summary>How long a card stays once its job is done (failed jobs stay three times as long). Tests set it to zero.</summary>
    public TimeSpan Linger { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a card takes to fade out, so the view's animation finishes before it goes.</summary>
    public TimeSpan FadeOut { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The cards showing now, oldest first.</summary>
    public ObservableCollection<Activity> Items { get; } = [];

    /// <summary>Most cards on screen at once: the oldest finished ones make way.</summary>
    const int MaxCards = 4;

    /// <summary>The window's thread, when the feed is made there: cards leave on it, wherever their job finished.</summary>
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    readonly Lock _lock = new();

    /// <summary>A job is starting: its card shows a spinner until it is finished, stopped or failed.</summary>
    /// <param name="cancel">What Stop does, if the job can be stopped.</param>
    public Activity Start(string title, string detail = "", Action? cancel = null)
    {
        var a = new Activity(this, title, detail, cancel);
        lock (_lock)
        {
            Items.Add(a);
            while (Items.Count > MaxCards && Items.FirstOrDefault(i => !i.IsRunning) is { } old) Items.Remove(old);
        }
        return a;
    }

    /// <summary>Something happened at once (a file saved, a project sent to a DAW): a card that is already done.</summary>
    public Activity Done(string title, string detail = "", string? actionLabel = null, Action? action = null) =>
        Start(title).Finish(title, detail, actionLabel, action);

    /// <summary>Something couldn't be done.</summary>
    public Activity Failed(string title, string detail = "") => Start(title).Fail(title, detail);

    // async void: nothing may escape it, or it takes the whole app down. Two cards retiring at once off the window's
    // thread (a job finished in the background; in tests, which have no window) once removed at the same moment and did.
    internal async void Retire(Activity a, TimeSpan after)
    {
        try
        {
            var version = a.Version;
            if (after > TimeSpan.Zero) await Task.Delay(after);
            if (a.Version != version || a.IsRunning) return;   // reused or restarted since
            a.IsLeaving = true;
            if (FadeOut > TimeSpan.Zero) await Task.Delay(FadeOut);
            if (_ui is not null && SynchronizationContext.Current != _ui) _ui.Post(_ => Remove(a), null);
            else Remove(a);
        }
        catch (Exception e)
        {
            AppLog.Info("Activity", $"A card couldn't be taken away: {e.Message}");
        }
    }

    void Remove(Activity a)
    {
        try
        {
            lock (_lock) Items.Remove(a);
        }
        catch (Exception e)
        {
            AppLog.Info("Activity", $"A card couldn't be taken away: {e.Message}");
        }
    }
}

/// <summary>One job's card.</summary>
public sealed partial class Activity : ObservableObject
{
    readonly ActivityFeed _feed;
    readonly Action? _cancel;
    Action? _action;
    internal int Version;

    internal Activity(ActivityFeed feed, string title, string detail, Action? cancel)
    {
        _feed = feed;
        _title = title;
        _detail = detail;
        _cancel = cancel;
    }

    [ObservableProperty] string _title;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasDetail))] string _detail;

    /// <summary>0 to 100, or below 0 while it can't say how far it has got.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPercent))] double _percent = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(IsStopped), nameof(CanCancel), nameof(HasPercent))]
    ActivityState _state;

    /// <summary>The card is fading out.</summary>
    [ObservableProperty] bool _isLeaving;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAction))] string? _actionLabel;

    public bool IsRunning => State == ActivityState.Running;
    public bool IsDone => State == ActivityState.Done;
    public bool IsFailed => State == ActivityState.Failed;
    public bool IsStopped => State == ActivityState.Stopped;
    public bool HasDetail => Detail.Length > 0;
    public bool HasPercent => IsRunning && Percent >= 0;
    public bool CanCancel => IsRunning && _cancel is not null;
    public bool HasAction => ActionLabel is not null;

    /// <summary>How far it has got: <paramref name="percent"/> below 0 when it can't say.</summary>
    public Activity Report(double percent, string? detail = null)
    {
        if (!IsRunning) return this;   // a late report doesn't undo "done"
        Percent = Math.Clamp(percent, -1, 100);
        if (detail is not null) Detail = detail;
        return this;
    }

    /// <param name="actionLabel">A button on the card, such as "Show file", running <paramref name="action"/>.</param>
    public Activity Finish(string? title = null, string? detail = null, string? actionLabel = null, Action? action = null)
    {
        Title = title ?? Title;
        Detail = detail ?? "";
        _action = action;
        ActionLabel = action is null ? null : actionLabel ?? "Show";
        return End(ActivityState.Done, _feed.Linger);
    }

    public Activity Fail(string? title = null, string? detail = null)
    {
        Title = title ?? Title;
        Detail = detail ?? "";
        return End(ActivityState.Failed, _feed.Linger * 3);
    }

    public Activity Stop(string title = "Stopped", string detail = "")
    {
        Title = title;
        Detail = detail;
        return End(ActivityState.Stopped, _feed.Linger);
    }

    /// <summary>Starts the card again for a job that picks up where it was (the same card, not a new one).</summary>
    public Activity Restart(string title, string detail = "")
    {
        Version++;
        IsLeaving = false;
        Title = title;
        Detail = detail;
        Percent = -1;
        State = ActivityState.Running;
        if (!_feed.Items.Contains(this)) _feed.Items.Add(this);
        return this;
    }

    Activity End(ActivityState state, TimeSpan linger)
    {
        Version++;
        State = state;
        _feed.Retire(this, linger);
        return this;
    }

    [RelayCommand] void Cancel() => _cancel?.Invoke();

    [RelayCommand] void RunAction() => _action?.Invoke();

    [RelayCommand]
    void Dismiss()
    {
        Version++;
        _feed.Items.Remove(this);
    }
}
