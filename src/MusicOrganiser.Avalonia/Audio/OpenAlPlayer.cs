using MusicOrganiser.Core;
using MusicOrganiser.ViewModels;
using Avalonia.Threading;
using Silk.NET.OpenAL;

namespace MusicOrganiser.Avalonia.Audio;

/// <summary>
/// Plays one sound at a time for auditioning, through OpenAL Soft on Windows, macOS and Linux.
/// Files stream from disk as they play (see <see cref="PcmSource"/>), so a long recording starts
/// straight away and plays to the end: a few short buffers are queued ahead and topped up in the
/// background.
/// </summary>
public sealed unsafe class OpenAlPlayer : IAudioPlayer
{
    /// <summary>Longest stretch decoded into memory, for files that can't stream.</summary>
    const double MaxSecondsInMemory = 600;

    /// <summary>Four tenths of a second queued ahead: enough that a busy disk or CPU can't starve it.</summary>
    const int BufferCount = 4;
    const double BufferSeconds = 0.1;
    static readonly TimeSpan PumpEvery = TimeSpan.FromMilliseconds(20);

    static readonly Lazy<(AL Al, ALContext Alc)?> Api = new(Open);

    readonly object _gate = new();
    PcmSource? _pcm;
    uint _source;
    uint[] _buffers = [];
    readonly Dictionary<uint, int> _framesIn = [];
    float[] _chunk = [];
    short[] _pcm16 = [];
    double _startSeconds;
    long _framesPlayed;      // frames in buffers already taken back off the queue since the start or last seek
    bool _ended;             // the source has nothing more to give
    string? _playing;
    Timer? _pump;
    int _generation;

    /// <summary>Raised on the UI thread when a sound plays to its end (not when it's stopped).</summary>
    public event Action? Stopped;

    /// <summary>Opens the default sound output once for the whole app, or nothing when there isn't one.</summary>
    static (AL, ALContext)? Open()
    {
        try
        {
            var alc = ALContext.GetApi(true);
            var al = AL.GetApi(true);
            var device = alc.OpenDevice("");
            if (device == null) return null;
            var context = alc.CreateContext(device, null);
            alc.MakeContextCurrent(context);
            return (al, alc);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>True when there's somewhere to play sound.</summary>
    internal static bool HasOutput => Api.Value is not null;

    static AL Al => Api.Value?.Al ?? throw new InvalidOperationException("No sound output was found on this computer.");

    public string? Playing
    {
        get { lock (_gate) return _playing; }
    }

    public double? Position
    {
        get
        {
            lock (_gate)
            {
                if (_playing is null || _pcm is null) return null;
                Al.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out var offset);
                return _startSeconds + (_framesPlayed + offset) / (double)_pcm.SampleRate;
            }
        }
    }

    public void Seek(double seconds)
    {
        lock (_gate)
        {
            if (_playing is null || _pcm is null) return;
            var al = Al;
            al.SourceStop(_source);
            al.SetSourceProperty(_source, SourceInteger.Buffer, 0);   // a stopped source lets go of its whole queue
            _pcm.SeekTo(Math.Max(0, seconds));
            _startSeconds = Math.Max(0, seconds);
            _framesPlayed = 0;
            _ended = false;
            QueueAll(al);
            al.SourcePlay(_source);
        }
    }

    public void Play(string path, double startSeconds = 0)
    {
        Stop();
        Start(PcmSource.Open(path, MaxSecondsInMemory), path, startSeconds);
    }

    public void Play(float[] mono, int sampleRate, string key, double startSeconds = 0)
    {
        Stop();
        Start(new MemorySource([mono], sampleRate), key, startSeconds);
    }

    void Start(PcmSource pcm, string key, double startSeconds)
    {
        AL al;
        try { al = Al; }
        catch
        {
            pcm.Dispose();
            throw;
        }
        lock (_gate)
        {
            _pcm = pcm;
            if (startSeconds > 0) pcm.SeekTo(startSeconds);
            _startSeconds = Math.Max(0, startSeconds);
            _framesPlayed = 0;
            _ended = false;
            var frames = Math.Max(256, (int)(pcm.SampleRate * BufferSeconds));
            _chunk = new float[frames * pcm.Channels];
            _pcm16 = new short[_chunk.Length];
            _source = al.GenSource();
            _buffers = al.GenBuffers(BufferCount);
            QueueAll(al);
            al.SourcePlay(_source);
            _playing = key;
            Parallelism.AudioPlaying = true;
            var generation = ++_generation;
            _pump = new Timer(_ => Pump(generation), null, PumpEvery, PumpEvery);
        }
    }

    /// <summary>Fills every buffer from where the source is and queues them, stopping early at the end.</summary>
    void QueueAll(AL al)
    {
        foreach (var buffer in _buffers)
        {
            if (!Fill(al, buffer)) break;
            var b = buffer;
            al.SourceQueueBuffers(_source, 1, &b);
        }
    }

    /// <summary>Reads the next piece into a buffer; false (and nothing filled) at the end.</summary>
    bool Fill(AL al, uint buffer)
    {
        var frames = _pcm!.Read(_chunk);
        if (frames == 0)
        {
            _ended = true;
            return false;
        }
        var count = frames * _pcm.Channels;
        for (var i = 0; i < count; i++) _pcm16[i] = (short)Math.Clamp(_chunk[i] * 32767f, -32768f, 32767f);
        fixed (short* p = _pcm16)
            al.BufferData(buffer, _pcm.Channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16, p, count * sizeof(short), _pcm.SampleRate);
        _framesIn[buffer] = frames;
        return true;
    }

    /// <summary>In the background: takes back the buffers that have played, refills and requeues them.</summary>
    void Pump(int generation)
    {
        lock (_gate)
        {
            if (generation != _generation || _pcm is null) return;
            try
            {
                var al = Al;
                al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out var done);
                for (; done > 0; done--)
                {
                    uint buffer;
                    al.SourceUnqueueBuffers(_source, 1, &buffer);
                    _framesPlayed += _framesIn.GetValueOrDefault(buffer);
                    if (!_ended && Fill(al, buffer)) al.SourceQueueBuffers(_source, 1, &buffer);
                }
                al.GetSourceProperty(_source, GetSourceInteger.SourceState, out var state);
                if (state == (int)SourceState.Playing) return;
                al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out var queued);
                if (queued > 0) al.SourcePlay(_source);   // ran dry for a moment: carry on
                else if (_ended) Finish(generation);
            }
            catch (Exception)
            {
                Finish(generation);   // a file that breaks off part way just ends there
            }
        }
    }

    /// <summary>Played to the end: let everything go and say so.</summary>
    void Finish(int generation)
    {
        Release();
        Dispatcher.UIThread.Post(() =>
        {
            if (generation == _generation) Stopped?.Invoke();
        });
    }

    /// <summary>
    /// Folds channels past the first two (centre, surrounds, LFE) into left and right, so nothing in a
    /// quad or 5.1 file goes silent: OpenAL here only takes mono or stereo.
    /// </summary>
    internal static float[][] DownmixToStereo(float[][] channels)
    {
        const float Share = 0.7071f;   // -3 dB into each side
        var left = (float[])channels[0].Clone();
        var right = (float[])channels[1].Clone();
        for (var c = 2; c < channels.Length; c++)
            for (var f = 0; f < left.Length; f++)
            {
                left[f] += channels[c][f] * Share;
                right[f] += channels[c][f] * Share;
            }
        return [left, right];
    }

    public void Stop()
    {
        lock (_gate)
        {
            _generation++;
            Release();
        }
    }

    void Release()
    {
        _pump?.Dispose();
        _pump = null;
        if (Api.Value is { Al: var al })
        {
            if (_source != 0)
            {
                al.SourceStop(_source);
                al.DeleteSource(_source);
                _source = 0;
            }
            if (_buffers.Length > 0)
            {
                al.DeleteBuffers(_buffers);
                _buffers = [];
            }
        }
        _framesIn.Clear();
        _pcm?.Dispose();
        _pcm = null;
        _playing = null;
        Parallelism.AudioPlaying = false;
    }

    public void Dispose() => Stop();
}
