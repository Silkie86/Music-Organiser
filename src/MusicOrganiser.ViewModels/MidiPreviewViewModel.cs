using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Plugins;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// The MIDI popup: every channel (or track) with notes, what plays it, and a mini piano roll you
/// can listen to and click along. Channels drag out as MIDI files, one or several at a time, or go
/// straight into the project open in Bitwig.
/// </summary>
public sealed partial class MidiPreviewViewModel : ObservableObject, IFileDragSource, IDisposable
{
    const string AllKey = "midi:all";

    readonly IProjectReader _reader;
    readonly string _path;
    readonly string _exportRoot;
    readonly double? _tempo;
    readonly LibraryDb? _db;
    readonly IAudioPlayer _player = Ui.Current.CreateAudioPlayer();
    readonly IUiTimer _clock;
    readonly Dictionary<string, MidiRender.Result> _renders = [];
    IReadOnlyList<MidiChannelRow> _sounding = [];
    string? _playingKey;
    CancellationTokenSource? _sending;

    public string Title { get; }
    [ObservableProperty] IReadOnlyList<MidiChannelRow> _channels = [];
    [ObservableProperty] string _summary = "Reading MIDI…";
    [ObservableProperty] string _message = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendAllToBitwigCommand))] bool _hasChannels;
    [ObservableProperty] bool _isPlayingAll;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsBusy))] bool _isPreparing;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsBusy))] bool _isSending;
    public bool IsBusy => IsPreparing || IsSending;

    /// <summary>Drag handle for every channel as one multi-track MIDI file.</summary>
    public IFileDragSource AsOneFile { get; }

    double Tempo => _tempo is > 10 and < 1000 ? _tempo.Value : 120;

    /// <param name="db">The library, for the plugins the plugin scan found, which Bitwig loads by their IDs.</param>
    public MidiPreviewViewModel(IProjectReader reader, string path, string title, string exportRoot, double? tempo, LibraryDb? db = null)
    {
        _reader = reader;
        _path = path;
        _exportRoot = exportRoot;
        _tempo = tempo;
        _db = db;
        Title = title;
        AsOneFile = new OneFileDrag(this);
        _clock = Ui.Current.CreateTimer(TimeSpan.FromMilliseconds(30), Tick);
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
                Message = $"Couldn't play it: {e.Message}";
                return;
            }
            finally { IsPreparing = false; }
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
        _sending?.Cancel();
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

    // ---- Sending to Bitwig ----

    /// <summary>
    /// Puts every channel into the project open in Bitwig, each on a new track named after it: its sample in a
    /// Sampler or its plugin with the settings the project saved, and its notes as a clip.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendAll))]
    Task SendAllToBitwig() => SendToBitwig(Channels);

    bool CanSendAll() => HasChannels && !IsSending;

    partial void OnIsSendingChanged(bool value)
    {
        SendAllToBitwigCommand.NotifyCanExecuteChanged();
        foreach (var r in Channels) r.SendToBitwigCommand.NotifyCanExecuteChanged();
    }

    internal async Task SendToBitwig(IReadOnlyList<MidiChannelRow> rows)
    {
        if (rows.Count == 0 || IsSending) return;
        IsSending = true;
        using var cancel = new CancellationTokenSource();
        _sending = cancel;
        var sent = 0;
        try
        {
            Message = "Connecting to Bitwig…";
            using var link = await BitwigLink.ConnectAsync(ct: cancel.Token);
            var presets = await Task.Run(() =>
            {
                try { return _reader.ExtractPresets(_path); }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return []; }
            }, cancel.Token);
            var settings = rows.Select(r => BitwigSend.PresetFor(r.Channel, presets)).ToList();
            // Presets saved in Bitwig turn the project's plugin settings into Bitwig presets; only needed for plugins.
            var templates = settings.Any(p => p is { Native: false })
                ? await Task.Run(PresetsViewModel.BitwigTemplates, cancel.Token)
                : new Dictionary<string, BitwigPreset.Template>();
            var files = new BitwigSend.Files(_path, _exportRoot, _tempo, templates);
            foreach (var (row, preset) in rows.Zip(settings))
            {
                Message = rows.Count == 1 ? $"Sending {row.Name} to Bitwig…" : $"Sending {sent + 1} of {rows.Count} to Bitwig: {row.Name}…";
                var c = row.Channel;
                IReadOnlyList<ScannedPlugin> installed = c.SamplePath.Length == 0 && c.Plugin.Length > 0 && _db is not null
                    ? _db.ScannedPluginsNamed(c.Plugin) : [];
                var result = await BitwigSend.ChannelAsync(link, c, preset, installed, files, cancel.Token);
                row.Sent = Describe(result);
                row.SentWhole = result.Problems.Count == 0;
                sent++;
            }
            var rough = rows.Count(r => !r.SentWhole);
            Message = rows.Count == 1 ? $"{rows[0].Name} is on a new track in Bitwig." +
                                        (rough == 0 ? "" : " Not everything came across: it says what under its name.")
                : $"Sent {rows.Count} channels to Bitwig, each on a new track named after it." +
                  (rough == 0 ? "" : $" {rough} didn't come across whole: each says what under its name.");
        }
        catch (DawLinkException e) { Message = sent == 0 ? e.Message : $"Sent {sent} of {rows.Count}, then: {e.Message}"; }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = $"Sending to Bitwig failed: {e.Message}"; }
        finally
        {
            _sending = null;
            IsSending = false;
        }
    }

    /// <summary>What a channel's row says once it's in Bitwig: what plays it there, and what didn't come across.</summary>
    static string Describe(BitwigSend.SentChannel sent)
    {
        var what = (sent.Instrument.Length > 0, sent.Clip) switch
        {
            (true, true) => $"In Bitwig: {sent.Instrument}, and its notes as a clip.",
            (true, false) => $"In Bitwig: {sent.Instrument}.",
            (false, true) => "In Bitwig: its notes, as a clip.",
            _ => "In Bitwig as an empty track.",
        };
        return string.Join(" ", [what, .. sent.Problems.Select(p => char.ToUpperInvariant(p[0]) + p[1..] + (p.EndsWith('.') ? "" : "."))]);
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
    /// <summary>What happened when it was last sent to Bitwig, or empty.</summary>
    [ObservableProperty] string _sent = "";
    /// <summary>Whether everything came across the last time it was sent to Bitwig.</summary>
    [ObservableProperty] bool _sentWhole;

    [RelayCommand]
    Task Play() => owner.Toggle(this);

    [RelayCommand]
    Task Seek(double ticks) => owner.Seek(this, ticks);

    [RelayCommand]
    void Export() => owner.Export(this);

    /// <summary>Puts this channel on a new track in the project open in Bitwig.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    Task SendToBitwig() => owner.SendToBitwig([this]);

    bool CanSend() => !owner.IsSending;
}
