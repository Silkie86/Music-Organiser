using System.Collections;
using System.ComponentModel;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// What the screens need from the UI framework: lists that filter and sort, timers, sound, dialogs and popups.
/// The Windows app (WPF) and the Mac/Linux app (Avalonia) each supply their own, set once at startup.
/// </summary>
public interface IPlatformUi
{
    /// <summary>A filterable, sortable, groupable view over a list, which a grid can show directly.</summary>
    IItemsView CreateView(IList items);

    /// <summary>A timer that ticks on the UI thread, for following playback.</summary>
    IUiTimer CreateTimer(TimeSpan interval, Action tick);

    IAudioPlayer CreateAudioPlayer();

    string? PickFolder(string title);

    /// <summary>Asks for an existing file; <paramref name="extension"/> is without the dot, e.g. "bwproject".</summary>
    string? PickFile(string title, string filterName, string extension);

    /// <summary>Asks for a DAW program: an .exe on Windows, an .app on a Mac.</summary>
    string? PickProgram(string title);

    /// <summary>Asks where to save a file; <paramref name="extension"/> is without the dot, e.g. "mid".</summary>
    string? PickSaveFile(string fileName, string filterName, string extension, string? initialFolder);

    bool Confirm(string message, string title);

    void CopyText(string text);

    /// <summary>Opens the MIDI popup for a project.</summary>
    void ShowMidi(MidiPreviewViewModel vm);

    /// <summary>Opens the presets popup for a project.</summary>
    void ShowPresets(PresetsViewModel vm);
}

/// <summary>The UI framework the app is running on.</summary>
public static class Ui
{
    static IPlatformUi? _current;

    public static IPlatformUi Current
    {
        get => _current ?? throw new InvalidOperationException("Set Ui.Current when the app starts.");
        set => _current = value;
    }
}

/// <summary>A list as a grid shows it: filtered, sorted and grouped.</summary>
public interface IItemsView : IEnumerable
{
    Predicate<object>? Filter { get; set; }

    /// <summary>Sorts by a property, replacing any earlier sort.</summary>
    void SortBy(string property, ListSortDirection direction);

    /// <summary>Sorts with a comparer instead, or back to the property sort when null.</summary>
    IComparer? CustomSort { get; set; }

    /// <summary>Groups by a property, or not at all when null.</summary>
    void GroupBy(string? property);

    /// <summary>Re-applies the filter and sort after something they depend on changed.</summary>
    void Refresh();

    /// <summary>How many items pass the filter.</summary>
    int Count { get; }
}

public interface IUiTimer
{
    void Start();
    void Stop();
}

/// <summary>Plays one sound at a time for auditioning. Starting a new one stops the last.</summary>
public interface IAudioPlayer : IDisposable
{
    event Action? Stopped;

    /// <summary>The file path or key of what's playing, or null.</summary>
    string? Playing { get; }

    /// <summary>Where playback is, in seconds, or null when nothing is playing.</summary>
    double? Position { get; }

    void Seek(double seconds);
    void Play(string path, double startSeconds = 0);
    void Play(float[] mono, int sampleRate, string key, double startSeconds = 0);
    void Stop();
}
