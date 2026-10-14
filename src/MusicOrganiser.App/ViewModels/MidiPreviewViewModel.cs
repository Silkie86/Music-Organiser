using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.App.Audio;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.App.ViewModels;

/// <summary>
/// The MIDI popup: every channel (or track) with notes, what plays it, and a mini piano roll you
/// can listen to and click along. Channels drag out as MIDI files, one or several at a time.
/// </summary>
public sealed partial class MidiPreviewViewModel : ObservableObject, IFileDragSource, IDisposable
{
    const string AllKey = "midi:all";

    readonly IProjectReader _reader;
    readonly string _path;
    readonly string _exportRoot;
    readonly double? _tempo;
    readonly AudioPlayer _player = new();
    readonly DispatcherTimer _clock = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
    readonly Dictionary<string, MidiRender.Result> _renders = [];
    int _listenRequest;
    IReadOnlyList<MidiChannelRow> _sounding = [];
    string? _playingKey;

    public string Title { get; }
    [ObservableProperty] IReadOnlyList<MidiChannelRow> _channels = [];
    [ObservableProperty] string _summary = "Reading MIDI…";
    [ObservableProperty] string _message = "";
    [ObservableProperty] bool _hasChannels;
    [ObservableProperty] bool _isPlayingAll;
    [ObservableProperty] bool _isPreparing;

    /// <summary>Drag handle for every channel as one multi-track MIDI file.</summary>
    public IFileDragSource AsOneFile { get; }

    double Tempo => _tempo is > 10 and < 1000 ? _tempo.Value : 120;

    public MidiPreviewViewModel(IProjectReader reader, string path, string title, string exportRoot, double? tempo)
    {
        _reader = reader;
        _path = path;
        _exportRoot = exportRoot;
        _tempo = tempo;
        Title = title;
        AsOneFile = new OneFileDrag(this);
        _clock.Tick += (_, _) => Tick();
    }

    public async Task LoadAsync()
    {
        try
        {
            var channels = await Task.Run(() => _reader.ExtractChannels(_path));
            var songEnd = channels.Count == 0 ? 1 : channels.Max(c => c.End);
            Channels = channels.Select((c, i) => new MidiChannelRow(c, songEnd, i, this)).ToList();
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

    // ---- Listening ----

    [RelayCommand]
    Task PlayAll() => IsPlayingAll ? Halt() : Listen(Channels, AllKey, 0);

    internal Task Toggle(MidiChannelRow row) =>
        row.IsPlaying && !IsPlayingAll ? Halt() : Listen([row], Key(row), 0);

    /// <summary>A click on a roll: move the song along if everything's playing, else play that channel from there.</summary>
    internal Task Seek(MidiChannelRow row, double ticks)
    {
        var seconds = MidiRender.TicksToSeconds((long)ticks, row.Channel.Ppq, Tempo);
        if (_playingKey is { } key && (key == AllKey || key == Key(row)) && _player.Playing == key)
        {
            _player.Seek(seconds);
            Tick();
            return Task.CompletedTask;
        }
        return Listen([row], Key(row), seconds);
    }

    static string Key(MidiChannelRow row) => $"midi:{row.Index}";

    async Task Listen(IReadOnlyList<MidiChannelRow> rows, string key, double from)
    {
        if (rows.Count == 0) return;
        Stop();
        // Stop() bumps this, so a later click, a stop or closing the window wins over a render still going.
        var request = _listenRequest;
        if (!_renders.TryGetValue(key, out var render))
        {
            IsPreparing = true;
            Message = rows.Count == 1 ? $"Getting {rows[0].Name} ready to play…" : "Getting the song ready to play…";
            try
            {
                var channels = rows.Select(r => r.Channel).ToList();
                render = await Task.Run(() => MidiRender.Render(channels, _tempo, _path));
                // A preview is a whole song's worth of audio, so only the last few stay ready.
                while (_renders.Count >= 4) _renders.Remove(_renders.Keys.First());
                _renders[key] = render;
            }
            catch (Exception e) when (e is IOException or OutOfMemoryException)
            {
                if (request == _listenRequest) Message = $"Couldn't play it: {e.Message}";
                return;
            }
            finally { if (request == _listenRequest) IsPreparing = false; }
            if (request != _listenRequest) return;
        }
        try { _player.Play(render.Audio, MidiRender.Rate, key, from); }
        catch (Exception e)
        {
            Message = $"Couldn't play it: {e.Message}";
            return;
        }
        _playingKey = key;
        _sounding = rows;
        IsPlayingAll = key == AllKey;
        foreach (var r in rows) r.IsPlaying = true;
        Message = rows.Count == 1
            ? $"Playing {rows[0].Name} with {render.Voices[0]}. Click along the notes to jump."
            : $"Playing all {rows.Count} channels. Sampler channels use their own samples, the rest a stand-in sound.";
        Tick();
        _clock.Start();
    }

    void Tick()
    {
        if (_playingKey is null) return;
        if (_player.Playing != _playingKey || _player.Position is not { } seconds)
        {
            Stop();
            return;
        }
        foreach (var r in _sounding) r.Position = MidiRender.SecondsToTicks(seconds, r.Channel.Ppq, Tempo);
    }

    Task Halt()
    {
        Stop();
        Message = "";
        return Task.CompletedTask;
    }

    void Stop()
    {
        _listenRequest++;
        IsPreparing = false;
        _clock.Stop();
        if (_playingKey is not null && _player.Playing == _playingKey) _player.Stop();
        _playingKey = null;
        foreach (var r in _sounding)
        {
            r.IsPlaying = false;
            r.Position = -1;
        }
        _sounding = [];
        IsPlayingAll = false;
    }

    public void Dispose()
    {
        Stop();
        _player.Dispose();
    }

    // ---- Exporting and dragging ----

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

    static DragCard Card(MidiChannelRow r) => new(r.Name, r.Details, Glyphs.Midi);

    /// <summary>The dragged channels as separate MIDI files, in list order, so each lands on its own track.</summary>
    public DragPayload? MakeDrag(IReadOnlyList<object> items)
    {
        var rows = items.OfType<MidiChannelRow>().ToList();
        if (rows.Count == 0) return null;
        var files = Exporter.MidiFiles(rows.Select(r => r.Channel).ToList(), _path, _exportRoot, _tempo);
        return new DragPayload(files, rows.Select(Card).ToList(),
            rows.Count == 1 ? "1 MIDI clip" : $"{rows.Count} MIDI tracks, in this order", Numbered: rows.Count > 1);
    }

    sealed class OneFileDrag(MidiPreviewViewModel owner) : IFileDragSource
    {
        public DragPayload? MakeDrag(IReadOnlyList<object> items)
        {
            var rows = items.OfType<MidiChannelRow>().ToList();
            if (rows.Count == 0) rows = owner.Channels.ToList();
            if (rows.Count == 0) return null;
            var file = Exporter.MidiTogether(rows.Select(r => r.Channel).ToList(), owner._path, owner._exportRoot, owner._tempo);
            return new DragPayload([file], rows.Select(Card).ToList(),
                $"One MIDI file with {rows.Count} track{(rows.Count == 1 ? "" : "s")}", Numbered: true);
        }
    }
}

public sealed partial class MidiChannelRow(MidiChannel channel, long songEnd, int index, MidiPreviewViewModel owner) : ObservableObject
{
    public MidiChannel Channel => channel;
    public long SongEnd => songEnd;
    public int Index => index;
    public string Name => channel.Name;
    public string Source => channel.Source.Length > 0 && channel.Label != channel.Name ? channel.Source : "";
    public string Details => string.Join(" · ", new[]
    {
        channel.Kind, $"{channel.Notes.Count:N0} note{(channel.Notes.Count == 1 ? "" : "s")}", channel.RangeText,
        $"{channel.Bars} bar{(channel.Bars == 1 ? "" : "s")}",
    }.Where(s => s.Length > 0));

    /// <summary>Where playback is, in song ticks, or -1 when this channel isn't sounding.</summary>
    [ObservableProperty] double _position = -1;
    [ObservableProperty] bool _isPlaying;

    [RelayCommand]
    Task Play() => owner.Toggle(this);

    [RelayCommand]
    Task Seek(double ticks) => owner.Seek(this, ticks);

    [RelayCommand]
    void Export() => owner.Export(this);
}
