using System.Collections;
using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia;

/// <summary>The screens' view models running on Avalonia.</summary>
public sealed class AvaloniaUi : IPlatformUi
{
    static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    static IStorageProvider? Storage => MainWindow?.StorageProvider;

    public IItemsView CreateView(IList items) => new AvaloniaItemsView(items);

    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new AvaloniaTimer(interval, tick);

    // Auditioning needs a cross-platform player (planned: miniaudio). Until then nothing plays.
    public IAudioPlayer CreateAudioPlayer() => new SilentPlayer();

    public async Task<string?> PickFolder(string title)
    {
        if (Storage is not { } storage) return null;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFile(string title, string filterName, string extension)
    {
        if (Storage is not { } storage) return null;
        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = [$"*.{extension}"] }],
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickProgram(string title)
    {
        if (OperatingSystem.IsWindows()) return await PickFile(title, "Programs", "exe");
        if (Storage is not { } storage) return null;
        // On a Mac the DAW is an .app bundle, which the file picker offers as one item.
        var start = OperatingSystem.IsMacOS() ? await storage.TryGetFolderFromPathAsync("/Applications") : null;
        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFile(string fileName, string filterName, string extension, string? initialFolder)
    {
        if (Storage is not { } storage) return null;
        var start = initialFolder is not null ? await storage.TryGetFolderFromPathAsync(initialFolder) : null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = fileName,
            DefaultExtension = extension,
            SuggestedStartLocation = start,
            FileTypeChoices = [new FilePickerFileType(filterName) { Patterns = [$"*.{extension}"] }],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<bool> Confirm(string message, string title) =>
        MainWindow is { } owner && await new ConfirmWindow(message, title).ShowDialog<bool>(owner);

    public void CopyText(string text)
    {
        if (MainWindow?.Clipboard is { } clipboard) _ = clipboard.SetTextAsync(text);
    }

    public void ShowMidi(MidiPreviewViewModel vm) =>
        NotPortedYet($"MIDI · {vm.Title}", "The MIDI window isn't on this version of the app yet. It's coming as the screens are moved over.");

    public void ShowPresets(PresetsViewModel vm) =>
        NotPortedYet($"Presets · {vm.Title}", "The presets window isn't on this version of the app yet. It's coming as the screens are moved over.");

    static void NotPortedYet(string title, string message)
    {
        if (MainWindow is { } owner) _ = new ConfirmWindow(message, title, okOnly: true).ShowDialog<bool>(owner);
    }
}

/// <summary>
/// Wraps Avalonia's DataGrid view (which can't be subclassed). Grids bind to <see cref="Inner"/>,
/// through <see cref="ItemsViewSource"/> in XAML, so they sort, filter and group it directly.
/// </summary>
sealed class AvaloniaItemsView(IList items) : IItemsView
{
    Predicate<object>? _filter;
    IComparer? _customSort;
    DataGridSortDescription? _propertySort;

    public DataGridCollectionView Inner { get; } = new(items);

    public Predicate<object>? Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            Inner.Filter = value is null ? null : o => value(o);
        }
    }

    public void SortBy(string property, ListSortDirection direction)
    {
        _propertySort = DataGridSortDescription.FromPath(property, direction);
        ApplySort();
    }

    public IComparer? CustomSort
    {
        get => _customSort;
        set
        {
            _customSort = value;
            ApplySort();
        }
    }

    void ApplySort()
    {
        Inner.SortDescriptions.Clear();
        if (_customSort is not null) Inner.SortDescriptions.Add(DataGridSortDescription.FromComparer(_customSort));
        else if (_propertySort is not null) Inner.SortDescriptions.Add(_propertySort);
    }

    public void GroupBy(string? property)
    {
        Inner.GroupDescriptions.Clear();
        if (property is not null) Inner.GroupDescriptions.Add(new DataGridPathGroupDescription(property));
    }

    public void Refresh() => Inner.Refresh();

    public int Count => Inner.Count;

    public IEnumerator GetEnumerator() => ((IEnumerable)Inner).GetEnumerator();
}

sealed class AvaloniaTimer(TimeSpan interval, Action tick) : IUiTimer
{
    readonly DispatcherTimer _timer = new(interval, DispatcherPriority.Render, (_, _) => tick());

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
}

sealed class SilentPlayer : IAudioPlayer
{
    public event Action? Stopped { add { } remove { } }
    public string? Playing => null;
    public double? Position => null;
    public void Seek(double seconds) { }
    public void Play(string path, double startSeconds = 0) { }
    public void Play(float[] mono, int sampleRate, string key, double startSeconds = 0) { }
    public void Stop() { }
    public void Dispose() { }
}
