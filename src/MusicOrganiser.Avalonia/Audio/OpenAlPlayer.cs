using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MusicOrganiser.Core;
using MusicOrganiser.ViewModels;
using Avalonia.Threading;
using Silk.NET.OpenAL;

namespace MusicOrganiser.Avalonia.Audio;

/// <summary>
/// Plays one sound at a time for auditioning, through OpenAL Soft on Windows, macOS and Linux.
/// Files stream from disk as they play (see <see cref="PcmSource"/>), so a long recording starts
/// straight away and plays to the end: a few short buffers are queued ahead and topped up in the
/// background. What it opens and plays goes in <see cref="AudioLog"/>.
/// </summary>
public sealed unsafe class OpenAlPlayer : IAudioPlayer
{
    /// <summary>Longest stretch decoded into memory, for files that can't stream.</summary>
    const double MaxSecondsInMemory = 600;

    /// <summary>Four tenths of a second queued ahead: enough that a busy disk or CPU can't starve it.</summary>
    const int BufferCount = 4;
    const double BufferSeconds = 0.1;
    static readonly TimeSpan PumpEvery = TimeSpan.FromMilliseconds(20);

    /// <summary>How long a sound plays before the log says whether it kept time.</summary>
    static readonly TimeSpan TimedFor = TimeSpan.FromSeconds(1);

    /// <summary>Times one sound reconnects the output before it gives up, so an output that keeps going can't loop.</summary>
    const int MaxReconnects = 3;

    static readonly Lazy<Output?> Api = new(Open);

    /// <summary>Held while the sound output is reconnected, as every player shares it.</summary>
    static readonly object DeviceGate = new();

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
    Stopwatch? _timing;      // from the start or last seek, until the log has said whether it kept time
    int _restarts;           // times it ran dry and was started again, this sound
    int _reconnects;         // times the output went while it played, this sound

    /// <summary>Raised on the UI thread when a sound plays to its end (not when it's stopped).</summary>
    public event Action? Stopped;

    /// <summary>
    /// The app's one sound output: OpenAL Soft, the device it opened, and the device calls Silk.NET doesn't cover,
    /// taken from the OpenAL library itself (null where it doesn't have one).
    /// </summary>
    sealed class Output
    {
        const int AlcConnected = 0x313, AlcFrequency = 0x1007, AlcRefresh = 0x1008;
        const int AlcDefaultAllDevices = 0x1012, AlcAllDevices = 0x1013;

        public required AL Al { get; init; }
        public required ALContext Alc { get; init; }
        public required nint Device { get; init; }
        public string? Library { get; private set; }
        delegate* unmanaged[Cdecl]<nint, int, int, int*, void> _getIntegers;
        delegate* unmanaged[Cdecl]<nint, int, byte*> _getString;
        delegate* unmanaged[Cdecl]<nint, byte*, int*, byte> _reopen;

        public bool CanReconnect => _reopen != null;

        /// <summary>Finds the OpenAL library this process loaded, and the calls above in it.</summary>
        public void Bind()
        {
            Library = LoadedLibrary();
            if (Library is null || !NativeLibrary.TryLoad(Library, out var lib)) return;
            if (NativeLibrary.TryGetExport(lib, "alcGetIntegerv", out var f)) _getIntegers = (delegate* unmanaged[Cdecl]<nint, int, int, int*, void>)f;
            if (NativeLibrary.TryGetExport(lib, "alcGetString", out f)) _getString = (delegate* unmanaged[Cdecl]<nint, int, byte*>)f;
            // An extension's call is asked for by name: the Linux library doesn't list it among its exports.
            if (Alc.IsExtensionPresent((Device*)Device, "ALC_SOFT_reopen_device") &&
                NativeLibrary.TryGetExport(lib, "alcGetProcAddress", out f))
                fixed (byte* name = "alcReopenDeviceSOFT"u8)
                    _reopen = (delegate* unmanaged[Cdecl]<nint, byte*, int*, byte>)((delegate* unmanaged[Cdecl]<nint, byte*, nint>)f)(Device, name);
        }

        /// <summary>
        /// False once the output has gone: unplugged, or taken over (a DAW opening the audio interface for itself, or
        /// changing its sample rate). OpenAL Soft then stops every sound at once and won't play one again, so a sound
        /// "played" then races through its buffers in silence, until it's reconnected.
        /// </summary>
        public bool Connected => ConnectedOverride?.Invoke() ?? (_getIntegers == null || Integer(AlcConnected) != 0);

        /// <summary>Moves the output to whatever is the computer's default output now, keeping every sound and buffer.</summary>
        public bool Reopen() => ReopenOverride?.Invoke() ?? (_reopen != null && _reopen(Device, null, null) != 0 && Connected);

        public int Frequency => Integer(AlcFrequency);
        public int Refresh => Integer(AlcRefresh);

        public string Name => _getString != null && Strings(Device, AlcAllDevices).FirstOrDefault() is { } name
            ? name : Alc.GetContextProperty((Device*)Device, GetContextString.DeviceSpecifier);

        public string? DefaultName => Strings(0, AlcDefaultAllDevices).FirstOrDefault();

        public IReadOnlyList<string> All => Strings(0, AlcAllDevices);

        int Integer(int what)
        {
            if (_getIntegers == null) return 0;
            var value = 0;
            _getIntegers(Device, what, 1, &value);
            return value;
        }

        /// <summary>A string, or for a list (the outputs) each string in it: they come one after another, ending in an empty one.</summary>
        List<string> Strings(nint device, int what)
        {
            var list = new List<string>();
            if (_getString == null) return list;
            for (var p = _getString(device, what); p != null && *p != 0;)
            {
                var length = 0;
                while (p[length] != 0) length++;
                list.Add(Encoding.UTF8.GetString(p, length));
                if (what != AlcAllDevices || device != 0) break;   // only the list of outputs has more than one
                p += length + 1;
            }
            return list;
        }

        /// <summary>The OpenAL library's file, from the process's own list of what it has loaded.</summary>
        static string? LoadedLibrary()
        {
            try
            {
                using var self = Process.GetCurrentProcess();
                var files = self.Modules.Cast<ProcessModule>().Select(m => m.FileName).ToList();
                return files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("soft_oal", StringComparison.OrdinalIgnoreCase)) ??
                       files.FirstOrDefault(f => Path.GetFileName(f).Contains("openal", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Tests' stand-ins for whether the output is connected, and for reconnecting it.</summary>
    internal static Func<bool>? ConnectedOverride, ReopenOverride;

    /// <summary>Opens the default sound output once for the whole app, or nothing when there isn't one.</summary>
    static Output? Open()
    {
        LogOpenAlItself();
        try
        {
            // Silk.NET looks for the bundled OpenAL Soft by the system's own id (ubuntu.24.04-x64, say)
            // and on Ubuntu never tries linux-x64, where the library sits. .NET's own search finds it, and
            // once it's loaded Silk's "libopenal.so.1" picks up that copy (its soname).
            if (OperatingSystem.IsLinux()) NativeLibrary.TryLoad("libopenal.so", typeof(OpenAlPlayer).Assembly, null, out _);
            var alc = ALContext.GetApi(true);
            var al = AL.GetApi(true);
            var device = alc.OpenDevice("");
            if (device == null)
            {
                AudioLog.Write("No sound output: OpenAL Soft couldn't open the default output");
                return null;
            }
            var context = alc.CreateContext(device, null);
            if (context == null)
            {
                AudioLog.Write($"No sound output: OpenAL Soft opened the default output but couldn't start on it ({alc.GetError(device)})");
                alc.CloseDevice(device);
                return null;
            }
            alc.MakeContextCurrent(context);
            var output = new Output { Al = al, Alc = alc, Device = (nint)device };
            output.Bind();
            AudioLog.Write($"OpenAL library: {output.Library ?? "(not found among the loaded files)"}");
            AudioLog.Write($"OpenAL: {al.GetStateProperty(StateString.Renderer)} {al.GetStateProperty(StateString.Version)}");
            AudioLog.Write($"Output: \"{output.Name}\" at {output.Frequency} Hz, mixed {output.Refresh} times a second" +
                           (output.CanReconnect ? "" : " (it can't be reconnected if it goes)"));
            AudioLog.Write($"Default output: \"{output.DefaultName}\"; all outputs: {string.Join(", ", output.All.Select(n => $"\"{n}\""))}");
            return output;
        }
        catch (Exception e) when (e is DllNotFoundException or FileNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            AudioLog.Write($"No sound output: the OpenAL library didn't load ({e.GetType().Name}: {e.Message})");
            return null;
        }
    }

    /// <summary>
    /// In a Debug build, has OpenAL Soft write its own detailed log to openal.log beside the app's audio log (unless
    /// it's been told where already). Set before it starts, as it reads them then.
    /// </summary>
    [Conditional("DEBUG")]
    static void LogOpenAlItself()
    {
        if (Environment.GetEnvironmentVariable("ALSOFT_LOGFILE") is { Length: > 0 }) return;
        try
        {
            Directory.CreateDirectory(AudioLog.Folder);
            NativeEnvironment.Set("ALSOFT_LOGLEVEL", "3");
            NativeEnvironment.Set("ALSOFT_LOGFILE", Path.Combine(AudioLog.Folder, "openal.log"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>True when there's somewhere to play sound.</summary>
    internal static bool HasOutput => Api.Value is not null;

    /// <summary>The output sounds play through, as OpenAL names it, or null when there's none.</summary>
    internal static string? OutputName => Api.Value?.Name;

    static Output Out => Api.Value ?? throw new InvalidOperationException("No sound output was found on this computer.");

    static AL Al => Out.Al;

    /// <summary>Reconnects the output if it has gone; false if it's still gone.</summary>
    static bool EnsureConnected(Output output, string when)
    {
        lock (DeviceGate)
        {
            if (output.Connected) return true;
            var ok = output.Reopen();
            AudioLog.Write(ok ? $"The sound output had gone ({when}): reconnected to \"{output.Name}\" at {output.Frequency} Hz"
                              : $"The sound output had gone ({when}) and couldn't be reconnected" +
                                (output.CanReconnect ? $" ({output.Alc.GetError((Device*)output.Device)})" : ": this OpenAL can't reconnect"));
            return ok;
        }
    }

    public string? Playing
    {
        get { lock (_gate) return _playing; }
    }

    public double? Position
    {
        get
        {
            lock (_gate) return _playing is null || _pcm is null ? null : PositionNow(Al);
        }
    }

    double PositionNow(AL al)
    {
        al.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out var offset);
        return _startSeconds + (_framesPlayed + offset) / (double)_pcm!.SampleRate;
    }

    public void Seek(double seconds)
    {
        lock (_gate)
        {
            if (_playing is null || _pcm is null) return;
            Restart(Al, seconds);
        }
    }

    /// <summary>Plays on from a time: drops what's queued and queues from there.</summary>
    void Restart(AL al, double seconds)
    {
        al.SourceStop(_source);
        al.SetSourceProperty(_source, SourceInteger.Buffer, 0);   // a stopped source lets go of its whole queue
        _pcm!.SeekTo(Math.Max(0, seconds));
        _startSeconds = Math.Max(0, seconds);
        _framesPlayed = 0;
        _ended = false;
        _timing = Stopwatch.StartNew();
        QueueAll(al);
        al.SourcePlay(_source);
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
        Output output;
        try
        {
            output = Out;
            if (!EnsureConnected(output, "before playing"))
                throw new InvalidOperationException("The sound output has gone: it was unplugged, or another app (a DAW, say) has taken it over. " +
                                                    "Check Windows' sound output, or close the app holding it, then play again.");
        }
        catch
        {
            pcm.Dispose();
            throw;
        }
        var al = output.Al;
        lock (_gate)
        {
            _pcm = pcm;
            if (startSeconds > 0) pcm.SeekTo(startSeconds);
            _startSeconds = Math.Max(0, startSeconds);
            _framesPlayed = 0;
            _ended = false;
            _restarts = 0;
            _reconnects = 0;
            var frames = Math.Max(256, (int)(pcm.SampleRate * BufferSeconds));
            _chunk = new float[frames * pcm.Channels];
            _pcm16 = new short[_chunk.Length];
            _source = al.GenSource();
            _buffers = al.GenBuffers(BufferCount);
            QueueAll(al);
            al.SourcePlay(_source);
            var error = al.GetError();
            AudioLog.Write($"Play \"{key}\"{(startSeconds > 0 ? $" from {startSeconds:0.00} s" : "")}: {pcm.Describe()}, " +
                           $"{pcm.SampleRate} Hz, {(pcm.Channels == 2 ? "stereo" : "mono")}" +
                           (error == AudioError.NoError ? "" : $"; OpenAL said {error}"));
            _timing = Stopwatch.StartNew();
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
                var output = Out;
                var al = output.Al;
                // Gone, it has stopped the sound and counts every queued buffer as played: carrying on would race
                // through the sound in silence. Play on, once it's back, from the oldest buffer still queued.
                if (!output.Connected)
                {
                    var at = _startSeconds + _framesPlayed / (double)_pcm.SampleRate;
                    if (++_reconnects <= MaxReconnects && EnsureConnected(output, $"while playing \"{_playing}\", at {at:0.00} s")) Restart(al, at);
                    else
                    {
                        if (_reconnects > MaxReconnects) AudioLog.Write($"The sound output keeps going: stopped \"{_playing}\" at {at:0.00} s");
                        Finish(generation);
                    }
                    return;
                }
                al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out var done);
                for (; done > 0; done--)
                {
                    uint buffer;
                    al.SourceUnqueueBuffers(_source, 1, &buffer);
                    _framesPlayed += _framesIn.GetValueOrDefault(buffer);
                    if (!_ended && Fill(al, buffer)) al.SourceQueueBuffers(_source, 1, &buffer);
                }
                if (_timing is { } clock && clock.Elapsed >= TimedFor)
                {
                    LogTiming(clock.Elapsed, PositionNow(al) - _startSeconds, "");
                    _timing = null;
                }
                al.GetSourceProperty(_source, GetSourceInteger.SourceState, out var state);
                if (state == (int)SourceState.Playing) return;
                al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out var queued);
                if (queued > 0)
                {
                    // Ran dry for a moment: carry on.
                    if (++_restarts <= 3) AudioLog.Write($"Ran dry at {PositionNow(al):0.00} s and started again");
                    al.SourcePlay(_source);
                }
                else if (_ended)
                {
                    if (_timing is { } t) LogTiming(t.Elapsed, _framesPlayed / (double)_pcm.SampleRate, " (to its end)");
                    Finish(generation);
                }
            }
            catch (Exception e)
            {
                AudioLog.Write($"Stopped part way: {e.GetType().Name}: {e.Message}");
                Finish(generation);   // a file that breaks off part way just ends there
            }
        }
    }

    /// <summary>Says how much sound played in how long: a speed far from 1 means the output isn't keeping time.</summary>
    static void LogTiming(TimeSpan took, double played, string note) =>
        AudioLog.Write($"Kept time? {played:0.00} s of sound in {took.TotalSeconds:0.00} s{note}: " +
                       $"{(took.TotalSeconds > 0 ? played / took.TotalSeconds : 0):0.00}× real time");

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
        _timing = null;
        Parallelism.AudioPlaying = false;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Sets an environment variable where native code reads it too: .NET's own setting reaches Windows' copy, but not
/// the C runtime's (Windows) or the C library's (Mac and Linux), which OpenAL Soft reads.
/// </summary>
static partial class NativeEnvironment
{
    public static void Set(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            if (OperatingSystem.IsWindows()) _ = WindowsPut(name, value);
            else _ = UnixSet(name, value, 1);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [LibraryImport("ucrtbase.dll", EntryPoint = "_wputenv_s", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsPut(string name, string value);

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnixSet(string name, string value, int overwrite);
}
