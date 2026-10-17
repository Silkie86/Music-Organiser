using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using MusicOrganiser.App.Audio;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.App.Platform;

/// <summary>The screens' view models running on WPF.</summary>
public sealed class WpfUi : IPlatformUi
{
    public IItemsView CreateView(IList items) => new WpfItemsView(items);

    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new WpfTimer(interval, tick);

    public IAudioPlayer CreateAudioPlayer() => new AudioPlayer();

    public Task<string?> PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FolderName : null);
    }

    public Task<string?> PickFile(string title, string filterName, string extension)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = $"{filterName} (*.{extension})|*.{extension}" };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }

    public Task<string?> PickProgram(string title) => PickFile(title, "Programs", "exe");

    public Task<string?> PickSaveFile(string fileName, string filterName, string extension, string? initialFolder)
    {
        var dialog = new SaveFileDialog { FileName = fileName, Filter = $"{filterName} (*.{extension})|*.{extension}" };
        if (initialFolder is not null) dialog.InitialDirectory = initialFolder;
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }

    public Task<bool> Confirm(string message, string title) =>
        Task.FromResult(MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK);

    public void CopyText(string text) => Clipboard.SetText(text);

    public void ShowMidi(MidiPreviewViewModel vm) =>
        new Views.MidiWindow(vm) { Owner = Application.Current.MainWindow }.Show();

    public void ShowPresets(PresetsViewModel vm) =>
        new Views.PresetsWindow(vm) { Owner = Application.Current.MainWindow }.Show();
}

/// <summary>A WPF collection view, so grids bind to it exactly as before.</summary>
sealed class WpfItemsView(IList items) : ListCollectionView(items), IItemsView
{
    public void SortBy(string property, ListSortDirection direction)
    {
        SortDescriptions.Clear();
        SortDescriptions.Add(new SortDescription(property, direction));
    }

    public void GroupBy(string? property)
    {
        GroupDescriptions.Clear();
        if (property is not null) GroupDescriptions.Add(new PropertyGroupDescription(property));
    }
}

sealed class WpfTimer : IUiTimer
{
    readonly DispatcherTimer _timer;

    public WpfTimer(TimeSpan interval, Action tick)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = interval };
        _timer.Tick += (_, _) => tick();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
}
