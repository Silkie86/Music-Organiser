using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.App.ViewModels;

/// <summary>The MIDI popup: every channel (or track) with notes, what plays it, and a mini piano roll.</summary>
public sealed partial class MidiPreviewViewModel : ObservableObject
{
    readonly IProjectReader _reader;
    readonly string _path;
    readonly string _exportRoot;
    readonly double? _tempo;

    public string Title { get; }
    [ObservableProperty] IReadOnlyList<MidiChannelRow> _channels = [];
    [ObservableProperty] string _summary = "Reading MIDI…";
    [ObservableProperty] string _message = "";
    [ObservableProperty] bool _hasChannels;

    public MidiPreviewViewModel(IProjectReader reader, string path, string title, string exportRoot, double? tempo)
    {
        _reader = reader;
        _path = path;
        _exportRoot = exportRoot;
        _tempo = tempo;
        Title = title;
    }

    public async Task LoadAsync()
    {
        try
        {
            var channels = await Task.Run(() => _reader.ExtractChannels(_path));
            var songEnd = channels.Count == 0 ? 1 : channels.Max(c => c.End);
            Channels = channels.Select(c => new MidiChannelRow(c, songEnd, this)).ToList();
            HasChannels = Channels.Count > 0;
            var notes = channels.Sum(c => c.Notes.Count);
            Summary = channels.Count == 0
                ? "No notes in this project."
                : $"{channels.Count} channel{(channels.Count == 1 ? "" : "s")} with notes · {notes:N0} notes · {channels.Max(c => c.Bars)} bars";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Summary = $"Couldn't read the MIDI: {e.Message}";
        }
    }

    [RelayCommand]
    async Task ExportAll()
    {
        Message = "Exporting…";
        try
        {
            var result = await Task.Run(() => Exporter.Midi(Channels.Select(c => c.Channel), _path, _exportRoot, _tempo));
            Message = $"Exported {result.Files.Count} MIDI files.";
            Shell.Open(result.Folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = $"Export failed: {e.Message}"; }
    }

    public void Export(MidiChannelRow row)
    {
        try
        {
            var file = Exporter.MidiChannel(row.Channel, _path, _exportRoot, _tempo);
            Message = $"Exported {Path.GetFileName(file)}";
            Shell.Reveal(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = $"Export failed: {e.Message}"; }
    }
}

public sealed partial class MidiChannelRow(MidiChannel channel, long songEnd, MidiPreviewViewModel owner) : ObservableObject
{
    public MidiChannel Channel => channel;
    public long SongEnd => songEnd;
    public string Name => channel.Name;
    public string Source => channel.Source.Length > 0 && channel.Label != channel.Name ? channel.Source : "";
    public string Details => string.Join(" · ", new[]
    {
        channel.Kind, $"{channel.Notes.Count:N0} note{(channel.Notes.Count == 1 ? "" : "s")}", channel.RangeText,
        $"{channel.Bars} bar{(channel.Bars == 1 ? "" : "s")}",
    }.Where(s => s.Length > 0));

    [RelayCommand]
    void Export() => owner.Export(this);
}
