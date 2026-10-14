using MusicOrganiser.Core;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;
using Silk.NET.OpenAL;

namespace MusicOrganiser.Avalonia.Audio;

/// <summary>
/// Plays one sound at a time for auditioning, through OpenAL Soft on Windows, macOS and Linux.
/// Files are decoded by the Sound Lab's own reader (WAV and AIFF, plus MP3, FLAC and the rest on
/// Windows through <see cref="WindowsDecoders"/>), up to ten minutes.
/// </summary>
public sealed unsafe class OpenAlPlayer : IAudioPlayer
{
    const double MaxSeconds = 600;

    static readonly Lazy<(AL Al, ALContext Alc)?> Api = new(Open);

    uint _source;
    uint _buffer;

    public event Action? Stopped { add { } remove { } }

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

    static AL Al => Api.Value?.Al ?? throw new InvalidOperationException("No sound output was found on this computer.");

    string? _playing;

    public string? Playing
    {
        get
        {
            if (_playing is null) return null;
            Al.GetSourceProperty(_source, GetSourceInteger.SourceState, out var state);
            if (state != (int)SourceState.Playing)
            {
                _playing = null;
                Parallelism.AudioPlaying = false;
            }
            return _playing;
        }
    }

    public double? Position
    {
        get
        {
            if (Playing is null) return null;
            Al.GetSourceProperty(_source, SourceFloat.SecOffset, out var seconds);
            return seconds;
        }
    }

    public void Seek(double seconds)
    {
        if (_playing is not null) Al.SetSourceProperty(_source, SourceFloat.SecOffset, (float)Math.Max(0, seconds));
    }

    public void Play(string path, double startSeconds = 0)
    {
        AudioData audio;
        try { audio = AudioFile.Read(path, MaxSeconds); }
        catch (NotSupportedException) when (OperatingSystem.IsWindows() && Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            audio = WindowsDecoders.ReadCompressedWav(path, MaxSeconds);
        }
        Start(audio.Channels, audio.SampleRate, path, startSeconds);
    }

    public void Play(float[] mono, int sampleRate, string key, double startSeconds = 0) =>
        Start([mono], sampleRate, key, startSeconds);

    void Start(float[][] channels, int sampleRate, string key, double startSeconds)
    {
        Stop();
        var al = Al;
        if (channels.Length > 2) channels = DownmixToStereo(channels);
        var stereo = channels.Length > 1;
        var frames = channels[0].Length;
        var pcm = new short[frames * (stereo ? 2 : 1)];
        for (var f = 0; f < frames; f++)
        {
            pcm[stereo ? f * 2 : f] = ToShort(channels[0][f]);
            if (stereo) pcm[f * 2 + 1] = ToShort(channels[1][f]);
        }

        _buffer = al.GenBuffer();
        fixed (short* p = pcm)
            al.BufferData(_buffer, stereo ? BufferFormat.Stereo16 : BufferFormat.Mono16, p, pcm.Length * sizeof(short), sampleRate);
        _source = al.GenSource();
        al.SetSourceProperty(_source, SourceInteger.Buffer, (int)_buffer);
        if (startSeconds > 0) al.SetSourceProperty(_source, SourceFloat.SecOffset, (float)startSeconds);
        al.SourcePlay(_source);
        _playing = key;
        Parallelism.AudioPlaying = true;
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

    static short ToShort(float v) => (short)Math.Clamp(v * 32767f, -32768f, 32767f);

    public void Stop()
    {
        if (Api.Value is not { Al: var al }) return;
        if (_source != 0)
        {
            al.SourceStop(_source);
            al.DeleteSource(_source);
            _source = 0;
        }
        if (_buffer != 0)
        {
            al.DeleteBuffer(_buffer);
            _buffer = 0;
        }
        _playing = null;
        Parallelism.AudioPlaying = false;
    }

    public void Dispose() => Stop();
}
