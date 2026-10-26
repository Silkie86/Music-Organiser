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

    public IAudioPlayer CreateAudioPlayer() => new Audio.OpenAlPlayer();

    public ISoundOutputs? SoundOutputs { get; } = new Audio.OpenAlOutputs();

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

    public void ShowMidi(MidiPreviewViewModel vm)
    {
        if (MainWindow is { } owner) new Views.MidiWindow(vm).Show(owner);
    }

    public void ShowPresets(PresetsViewModel vm)
    {
        if (MainWindow is { } owner) new Views.PresetsWindow(vm).Show(owner);
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

    public void KeepSortOf(IItemsView? earlier)
    {
        if (earlier is not AvaloniaItemsView old) return;
        _customSort = old._customSort;
        _propertySort = old._propertySort;
        // The grid's own header sort lives in the inner view: carry it over as it is.
        Inner.SortDescriptions.Clear();
        foreach (var d in old.Inner.SortDescriptions) Inner.SortDescriptions.Add(d);
    }

    public IEnumerator GetEnumerator() => ((IEnumerable)Inner).GetEnumerator();
}

sealed class AvaloniaTimer : IUiTimer
{
    readonly DispatcherTimer _timer;

    public AvaloniaTimer(TimeSpan interval, Action tick)
    {
        // Made stopped, as in the Windows app, so it only runs from Start(). (The constructor that
        // takes the handler starts the timer straight away.)
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = interval };
        _timer.Tick += (_, _) => tick();
    }

    internal bool IsRunning => _timer.IsEnabled;

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
}
