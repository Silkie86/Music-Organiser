using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using MusicOrganiser.Core;
using MusicOrganiser.ViewModels;
using Avalonia.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Silk.NET.OpenAL;

namespace MusicOrganiser.Avalonia.Audio;

/// <summary>
/// Plays one sound at a time for auditioning, through OpenAL Soft on Windows, macOS and Linux.
/// Files stream from disk as they play (see <see cref="PcmSource"/>), so a long recording starts
/// straight away and plays to the end: a few short buffers are queued ahead and topped up in the
/// background. Sound goes to the output chosen in Settings, or the computer's default, and follows it
/// when it changes or comes back. What it opens and plays goes in <see cref="AudioLog"/>.
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

    /// <summary>After an output couldn't be opened, how long before a sound tries again.</summary>
    static readonly TimeSpan RetryOpenAfter = TimeSpan.FromSeconds(2);

    /// <summary>Where nothing says when outputs come and go (Mac, Linux), how often a sound looks again.</summary>
    static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(3);

    /// <summary>Held while the sound output is opened, moved or reconnected, as every player shares it.</summary>
    static readonly object DeviceGate = new();

    /// <summary>The app's one output, opened when a sound first needs it; null until then, or while none would open.</summary>
    static volatile Output? _output;
    static readonly Stopwatch SinceOpenTried = new();
    /// <summary>The output chosen in Settings, by name, or null for the computer's default.</summary>
    static string? _chosen;
    /// <summary>What the output was last opened as: the chosen name, or null for the default.</summary>
    static string? _openedAs;
    /// <summary>The computer's default output when the output was last opened on it.</summary>
    static string? _defaultSeen;
    /// <summary>Set when the outputs may have changed (Windows said so, or a new choice): the next sound looks again.</summary>
    static volatile bool _recheck;
    static readonly Stopwatch SinceChecked = Stopwatch.StartNew();
    /// <summary>Whether Windows tells the app when outputs change, so it needn't keep looking.</summary>
    static bool _watched;
    /// <summary>The chosen output the log last said wasn't there, so it says so once, not at every look.</summary>
    static string? _missingSaid;
    /// <summary>
    /// Counts reconnections. Each one followed the output going, which stopped every player's sound, so a player that
    /// sees it go up starts its own sound again even if another player did the reconnecting.
    /// </summary>
    static int _reconnections;

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
    // Written under _gate, read without it: the window asks what's playing many times a second, and must never wait
    // while a preview reads its file's start under the lock (a slow disk once held the window up 2.2 s).
    volatile string? _playing;
    double? _lastPosition;
    Timer? _pump;
    int _generation;
    Stopwatch? _timing;      // from the start or last seek, until the log has said whether it kept time
    int _restarts;           // times it ran dry and was started again, this sound
    int _reconnects;         // times the output went while it played, this sound
    int _seenReconnections;  // _reconnections when this sound was last started

    /// <summary>Raised on the UI thread when a sound plays to its end (not when it's stopped).</summary>
    public event Action? Stopped;

    /// <summary>
    /// The app's one sound output: OpenAL Soft, the device it opened, and the device calls Silk.NET doesn't cover,
    /// taken from the OpenAL library itself (null where it doesn't have one).
    /// </summary>
    sealed class Output
    {
        const int AlcConnected = 0x313, AlcFrequency = 0x1007, AlcRefresh = 0x1008;
        const int AlcAllDevices = 0x1013;
        const int AlcDeviceLatency = 0x1601;   // ALC_DEVICE_LATENCY_SOFT, in nanoseconds

        public required AL Al { get; init; }
        public required ALContext Alc { get; init; }
        public required nint Device { get; init; }
        public string? Library { get; private set; }
        delegate* unmanaged[Cdecl]<nint, int, int, int*, void> _getIntegers;
        delegate* unmanaged[Cdecl]<nint, int, byte*> _getString;
        delegate* unmanaged[Cdecl]<nint, byte*, int*, byte> _reopen;
        delegate* unmanaged[Cdecl]<nint, int, int, long*, void> _getIntegers64;

        public bool CanReconnect => _reopen != null;

        /// <summary>Takes the calls above from the OpenAL library Silk.NET loaded, and notes where that is for the log.</summary>
        public void Bind()
        {
            Library = LoadedLibrary();
            var library = Alc.Context;
            if (library.TryGetProcAddress("alcGetIntegerv", out var f, null)) _getIntegers = (delegate* unmanaged[Cdecl]<nint, int, int, int*, void>)f;
            if (library.TryGetProcAddress("alcGetString", out f, null)) _getString = (delegate* unmanaged[Cdecl]<nint, int, byte*>)f;
            // An extension's call is asked of OpenAL by name: the Linux library doesn't list it among its exports.
            if (Alc.IsExtensionPresent((Device*)Device, "ALC_SOFT_reopen_device"))
                _reopen = (delegate* unmanaged[Cdecl]<nint, byte*, int*, byte>)Alc.GetProcAddress((Device*)Device, "alcReopenDeviceSOFT");
            if (Alc.IsExtensionPresent((Device*)Device, "ALC_SOFT_device_clock"))
                _getIntegers64 = (delegate* unmanaged[Cdecl]<nint, int, int, long*, void>)Alc.GetProcAddress((Device*)Device, "alcGetInteger64vSOFT");
        }

        /// <summary>
        /// False once the output has gone: unplugged, or taken over (a DAW opening the audio interface for itself, or
        /// changing its sample rate). OpenAL Soft then stops every sound at once and won't play one again, so a sound
        /// "played" then races through its buffers in silence, until it's reconnected.
        /// </summary>
        public bool Connected => ConnectedOverride?.Invoke() ?? (_getIntegers == null || Integer(AlcConnected) != 0);

        /// <summary>
        /// Moves the output to the one named, or to whatever is the computer's default now (null), keeping every sound and
        /// buffer: a sound playing carries on there.
        /// </summary>
        public bool Reopen(string? name)
        {
            if (ReopenOverride is { } fake) return fake(name);
            if (_reopen == null) return false;
            byte done;
            if (name is null) done = _reopen(Device, null, null);
            else
                fixed (byte* p = Encoding.UTF8.GetBytes(name + "\0"))
                    done = _reopen(Device, p, null);
            return done != 0 && Connected;
        }

        public int Frequency => Integer(AlcFrequency);
        public int Refresh => Integer(AlcRefresh);

        /// <summary>
        /// How long sound mixed now takes to come out, in milliseconds, as OpenAL Soft works it out: what it has queued
        /// ahead of Windows plus the delay Windows reports for the output (its own and the interface's). Negative when
        /// this OpenAL can't say.
        /// </summary>
        public double DelayMs
        {
            get
            {
                if (_getIntegers64 == null) return -1;
                long ns = 0;
                _getIntegers64(Device, AlcDeviceLatency, 1, &ns);
                return ns / 1e6;
            }
        }

        /// <summary>How long OpenAL mixes ahead each time, in milliseconds.</summary>
        public double PeriodMs => Refresh > 0 ? 1000.0 / Refresh : -1;

        public string Name => Plain(_getString != null && Strings(Device, AlcAllDevices).FirstOrDefault() is { } name
            ? name : Alc.GetContextProperty((Device*)Device, GetContextString.DeviceSpecifier));

        /// <summary>Every output there is now, looked at afresh, the computer's default first (as OpenAL Soft lists them).</summary>
        public IReadOnlyList<string> All => OutputsOverride?.Invoke() ?? Strings(0, AlcAllDevices).Select(Plain).ToList();

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

        /// <summary>
        /// The OpenAL library's file, from the process's own list of what it has loaded (Windows and Linux; a Mac lists
        /// only the app itself).
        /// </summary>
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

    /// <summary>
    /// An output's name as people know it. On Windows OpenAL Soft puts "OpenAL Soft on " in front, and takes a name
    /// with or without it when opening one.
    /// </summary>
    static string Plain(string name) => name.StartsWith("OpenAL Soft on ", StringComparison.Ordinal) ? name["OpenAL Soft on ".Length..] : name;

    /// <summary>Tests' stand-ins for whether the output is connected, for moving it (given where to), and for the list of outputs.</summary>
    internal static Func<bool>? ConnectedOverride;
    internal static Func<string?, bool>? ReopenOverride;
    internal static Func<IReadOnlyList<string>>? OutputsOverride;

    /// <summary>
    /// The output, opened the first time a sound needs it: the one chosen, or the computer's default. Null when none
    /// would open (a DAW may have the audio interface to itself), and then a sound a moment later tries again.
    /// </summary>
    static Output? TryOutput()
    {
        if (_output is { } open) return open;
        lock (DeviceGate)
        {
            if (_output is not null) return _output;
            if (SinceOpenTried.IsRunning && SinceOpenTried.Elapsed < RetryOpenAfter) return null;
            SinceOpenTried.Restart();
            var output = Open(_chosen);
            if (output is null) return null;
            if (!_watched && OperatingSystem.IsWindows()) _watched = WindowsOutputWatch.Start(OutputsChanged);
            SinceChecked.Restart();
            return _output = output;
        }
    }

    /// <summary>Opens a sound output for the whole app: the one named if it will open, otherwise the default.</summary>
    static Output? Open(string? chosen)
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
            var opened = chosen is null ? null : OpenOn(alc, chosen);
            var onChosen = opened is not null;
            if (!onChosen && chosen is not null) AudioLog.Write($"Opening the default output instead of \"{chosen}\"");
            opened ??= OpenOn(alc, null);
            if (opened is not var (device, context)) return null;
            alc.MakeContextCurrent((Context*)context);
            var output = new Output { Al = al, Alc = alc, Device = device };
            output.Bind();
            var all = output.All;
            _openedAs = onChosen ? chosen : null;
            _defaultSeen = all.FirstOrDefault();
            AudioLog.Write($"OpenAL library: {output.Library ?? "(this system doesn't say which file)"}");
            AudioLog.Write($"OpenAL: {al.GetStateProperty(StateString.Renderer)} {al.GetStateProperty(StateString.Version)}");
            AudioLog.Write($"Output: \"{output.Name}\" ({(_openedAs is null ? "the default" : "chosen in Settings")}) at {output.Frequency} Hz, " +
                           $"mixed {output.Refresh} times a second" + (output.CanReconnect ? "" : " (it can't be moved or reconnected)"));
            AudioLog.Write($"Default output: \"{_defaultSeen}\"; all outputs: {string.Join(", ", all.Select(n => $"\"{n}\""))}");
            // Read once here, where the output opens (or reopens on another device): it only changes then, and asking
            // OpenAL on every play would sit outside the device lock while an output was being reopened.
            var delay = output.DelayMs;
            SweepTiming.Output(output.PeriodMs / 2, delay);
            AudioLog.Write($"Output delay: sound mixed now comes out after {delay:0.0} ms (OpenAL's queue, Windows' and the interface's " +
                           $"delay), mixed every {output.PeriodMs:0.0} ms");
            return output;
        }
        catch (Exception e) when (e is DllNotFoundException or FileNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            AudioLog.Write($"No sound output: the OpenAL library didn't load ({e.GetType().Name}: {e.Message})");
            return null;
        }
    }

    /// <summary>Opens one output (null: the default) and starts OpenAL on it; null if either fails.</summary>
    static (nint Device, nint Context)? OpenOn(ALContext alc, string? name)
    {
        var which = name is null ? "the default output" : $"\"{name}\"";
        var device = alc.OpenDevice(name ?? "");
        if (device == null)
        {
            AudioLog.Write($"OpenAL Soft couldn't open {which}");
            return null;
        }
        var context = alc.CreateContext(device, null);
        if (context == null)
        {
            // The usual reason on Windows: a DAW has the audio interface to itself (exclusive mode).
            AudioLog.Write($"OpenAL Soft opened {which} but couldn't start on it ({alc.GetError(device)})");
            alc.CloseDevice(device);
            return null;
        }
        return ((nint)device, (nint)context);
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

    internal const string NoOutputMessage = "No sound output would open. If a DAW has your audio interface to itself, close it or " +
                                            "choose another output in Settings › Sound output, then play again.";

    /// <summary>How often the output mixes and how long mixed sound takes to come out, in milliseconds (null before it's open).</summary>
    internal static (double PeriodMs, double DelayMs)? OutputTiming => _output is { } o ? (o.PeriodMs, o.DelayMs) : null;

    /// <summary>True when there's somewhere to play sound.</summary>
    internal static bool HasOutput => TryOutput() is not null;

    /// <summary>The output sounds play through, as people know it, or null when there's none.</summary>
    internal static string? OutputName => TryOutput()?.Name;

    static Output Out => TryOutput() ?? throw new InvalidOperationException(NoOutputMessage);

    /// <summary>Only while a sound is playing, when the output is open.</summary>
    static AL Al => _output!.Al;

    /// <summary>The outputs there are now, the computer's default first; none when no output would open.</summary>
    internal static IReadOnlyList<string> OutputNames()
    {
        if (TryOutput() is not { } output) return [];
        lock (DeviceGate) return output.All;
    }

    /// <summary>
    /// Plays through this output from now on (null: the computer's default), moving a sound that's playing. Before
    /// any sound has played it's only remembered, so the app's start opens nothing.
    /// </summary>
    internal static void ChooseOutput(string? name)
    {
        lock (DeviceGate)
        {
            _chosen = string.IsNullOrEmpty(name) ? null : name;
            _recheck = true;
            if (_output is { } output) EnsureOutput(output, "chosen in Settings");
            else if (SinceOpenTried.IsRunning) SinceOpenTried.Reset();   // a new choice may open where the last one didn't
        }
    }

    /// <summary>
    /// Looks at the outputs again, moving to the wanted one if it's back or the default changed, and says where sounds
    /// go now, or why they can't go there, in a line for Settings.
    /// </summary>
    internal static string CheckOutput()
    {
        if (TryOutput() is not { } output) return NoOutputMessage;
        lock (DeviceGate)
        {
            _recheck = true;
            if (!EnsureOutput(output, "Settings looked"))
                return $"The output has gone ({output.Name}): it was unplugged, or another app has it to itself. The app tries again when you play.";
            if (_chosen is not null && _openedAs != _chosen)
                return $"{_chosen} isn't there or won't open now (unplugged, switched off, or a DAW has it to itself?), " +
                       $"so sounds play through the default output, {output.Name}.";
            return $"Sounds play through {output.Name}.";
        }
    }

    /// <summary>Windows says outputs came, went or the default changed: the next sound looks again.</summary>
    internal static void OutputsChanged() => _recheck = true;

    /// <summary>True when the output should be looked at before carrying on: it may have gone, or the wanted one changed.</summary>
    static bool CheckDue(Output output) =>
        !output.Connected || _recheck || (!_watched && SinceChecked.Elapsed >= RecheckEvery);

    /// <summary>
    /// Makes sure sound can go out, and goes where it's wanted: the output chosen in Settings while it's there, otherwise
    /// the computer's default, following it when it changes. Reopens the output when it has gone (unplugged, or taken
    /// over by a DAW) or isn't the wanted one. False only when there's nowhere to play.
    /// </summary>
    static bool EnsureOutput(Output output, string when)
    {
        lock (DeviceGate)
        {
            var connected = output.Connected;
            if (connected && !_recheck && (_watched || SinceChecked.Elapsed < RecheckEvery)) return true;
            _recheck = false;
            SinceChecked.Restart();
            var all = output.All;
            var defaultNow = all.FirstOrDefault();
            var target = _chosen is not null && all.Contains(_chosen) ? _chosen : null;
            var missing = _chosen is not null && target is null ? _chosen : null;
            if (missing is not null && missing != _missingSaid)
                AudioLog.Write($"\"{missing}\" isn't there now ({when}): sounds go to the default output");
            _missingSaid = missing;
            if (connected && target == _openedAs && (target is not null || defaultNow == _defaultSeen)) return true;

            var from = output.Name;
            var moved = output.Reopen(target);
            if (!moved && target is not null)
            {
                // The chosen one won't open (a DAW may have it to itself): the default, then, unless sound is there already.
                AudioLog.Write($"\"{target}\" won't open ({when}; {Why(output)}): sounds go to the default output");
                target = null;
                if (output.Connected && _openedAs is null && defaultNow == _defaultSeen) return true;
                moved = output.Reopen(null);
            }
            if (moved)
            {
                _openedAs = target;
                _defaultSeen = defaultNow;
                if (!connected) _reconnections++;
                AudioLog.Write($"{(connected ? "Moved" : "The output had gone: reconnected")} ({when}) from \"{from}\" to \"{output.Name}\" " +
                               $"({(target is null ? "the default" : "chosen in Settings")}) at {output.Frequency} Hz");
                return true;
            }
            AudioLog.Write(output.Connected ? $"Couldn't move ({when}; {Why(output)}): staying on \"{from}\""
                                            : $"The output had gone ({when}) and couldn't be reconnected ({Why(output)})");
            return output.Connected;
        }
    }

    /// <summary>OpenAL's last word on why the output wouldn't open or move.</summary>
    static string Why(Output output) => output.CanReconnect ? $"{output.Alc.GetError((Device*)output.Device)}" : "this OpenAL can't move it";

    public string? Playing => _playing;

    /// <summary>
    /// Where the sound is. The player's own refills hold it for a moment, so that's waited for; a file's start read from
    /// a slow or sleeping disk can hold it for seconds, and then it's the last answer (or where the sound began).
    /// </summary>
    public double? Position
    {
        get
        {
            if (!Monitor.TryEnter(_gate, 50)) return _playing is null ? null : _lastPosition;
            try { return _lastPosition = _playing is null || _pcm is null ? null : PositionNow(Al); }
            finally { Monitor.Exit(_gate); }
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
        _lastPosition = _startSeconds;
        _framesPlayed = 0;
        _ended = false;
        _timing = Stopwatch.StartNew();
        _seenReconnections = Volatile.Read(ref _reconnections);
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
            if (!EnsureOutput(output, "before playing"))
                throw new InvalidOperationException("The sound output has gone: it was unplugged, or another app (a DAW, say) has taken it over. " +
                                                    "Close the app holding it, or choose another output in Settings › Sound output, then play again.");
        }
        catch
        {
            pcm.Dispose();
            throw;
        }
        var al = output.Al;
        lock (_gate)
        {
            // Another Play may have started between this one's Stop and here (a sweep preview opens on a background
            // thread while a click plays on the window's): stop it, or its sound and OpenAL source would be orphaned.
            _generation++;
            Release();
            _pcm = pcm;
            if (startSeconds > 0) pcm.SeekTo(startSeconds);
            _startSeconds = Math.Max(0, startSeconds);
            _lastPosition = _startSeconds;
            _framesPlayed = 0;
            _ended = false;
            _restarts = 0;
            _reconnects = 0;
            _seenReconnections = Volatile.Read(ref _reconnections);
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
                var output = _output!;
                var al = output.Al;
                // How many queued buffers have played, read before asking whether the output is there: when it goes,
                // OpenAL Soft says so first and only then counts every queued buffer as played, so a count read before
                // a yes is a true one.
                al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out var done);
                // Gone, it has stopped every sound and counts every queued buffer as played: carrying on would race
                // through the sound in silence. Play on, once it's back, from the oldest buffer still queued. Read
                // together, so a reconnection another player is part way through can't be half seen.
                bool connected;
                int reconnections;
                lock (DeviceGate) (connected, reconnections) = (output.Connected, _reconnections);
                if (!connected || reconnections != _seenReconnections)
                {
                    var at = _startSeconds + _framesPlayed / (double)_pcm.SampleRate;
                    if (++_reconnects <= MaxReconnects && EnsureOutput(output, $"while playing \"{_playing}\", at {at:0.00} s") && output.Connected)
                    {
                        AudioLog.Write($"Started \"{_playing}\" again at {at:0.00} s, now the output is back");
                        Restart(al, at);
                    }
                    else
                    {
                        if (_reconnects > MaxReconnects) AudioLog.Write($"The sound output keeps going: stopped \"{_playing}\" at {at:0.00} s");
                        Finish(generation);
                    }
                    return;
                }
                // A new choice, or a new default output: the sound moves there as it plays.
                if (CheckDue(output)) EnsureOutput(output, $"while playing \"{_playing}\"");
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
                if (!output.Connected) return;   // it went just now: the next look plays on from where it was
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
        if (_output is { Al: var al })
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
        _lastPosition = null;
        _timing = null;
        Parallelism.AudioPlaying = false;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// On Windows, hears when outputs come and go or the default output changes, so the player looks again only then
/// instead of every few seconds. Windows calls it on a thread of its own, so it only raises a flag.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class WindowsOutputWatch(Action changed) : IMMNotificationClient
{
    /// <summary>Kept for the app's life: Windows stops calling once they're collected.</summary>
    static MMDeviceEnumerator? _devices;
    static WindowsOutputWatch? _watch;

    /// <summary>Starts listening; false (and the player keeps looking every few seconds) if Windows won't say.</summary>
    public static bool Start(Action changed)
    {
        try
        {
            _devices = new MMDeviceEnumerator();
            _watch = new WindowsOutputWatch(changed);
            var result = _devices.RegisterEndpointNotificationCallback(_watch);
            if (result == 0) return true;
            AudioLog.Write($"Windows won't say when outputs change (0x{result:X8}): looking again every few seconds instead");
        }
        catch (Exception e)   // optional: never let it stop sound
        {
            AudioLog.Write($"Windows won't say when outputs change ({e.GetType().Name}: {e.Message}): looking again every few seconds instead");
        }
        return false;
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => changed();
    public void OnDeviceAdded(string pwstrDeviceId) => changed();
    public void OnDeviceRemoved(string deviceId) => changed();

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render) changed();
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
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
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]   // C runtime calls are cdecl, which matters on 32-bit Windows
    private static partial int WindowsPut(string name, string value);

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int UnixSet(string name, string value, int overwrite);
}
