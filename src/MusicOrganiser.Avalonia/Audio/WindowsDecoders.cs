using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MusicOrganiser.Core.Sounds;
using NAudio.Wave;

namespace MusicOrganiser.Avalonia.Audio;

/// <summary>
/// Windows' own decoders, as the WPF app uses them. Media Foundation lets the Sound Lab and the
/// player read MP3, FLAC, M4A, OGG, WMA and AAC (WAV and AIFF are read by the Sound Lab itself),
/// and Windows' audio codecs let the player play WAVs the Sound Lab can't read (ADPCM, µ-law, A-law,
/// GSM, as old drum-machine packs often are). On a Mac or Linux there's nothing to read MP3, FLAC and the
/// rest yet, so the Sound Lab leaves them out there.
/// </summary>
[SupportedOSPlatform("windows")]
static class WindowsDecoders
{
    static readonly string[] Extensions = [".mp3", ".flac", ".m4a", ".ogg", ".wma", ".aac"];

    public static void Register()
    {
        foreach (var ext in Extensions)
            AudioFile.Register(ext, (path, maxSeconds) => Decode(Reader(path), maxSeconds));
    }

    /// <summary>True for the extensions Media Foundation reads here (".mp3" and so on).</summary>
    public static bool Handles(string extension) => Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens one of those files to play as it goes, rather than decoding it all first.</summary>
    public static WaveStream Open(string path) => new MtaOnly(Reader(path));

    /// <summary>MF_E_UNSUPPORTED_BYTESTREAM_TYPE: "The byte stream type of the given URL is unsupported."</summary>
    const int UnsupportedByteStream = unchecked((int)0xC00D36C4);

    /// <summary>
    /// A reader for the file. Media Foundation picks its decoder by the file's extension, so a file whose insides don't
    /// match it is refused with 0xC00D36C4 (Solomon's 19 Oct note: "Pink - Nobody Knows (acapella).mp3"): a WAV or an
    /// M4A saved as .mp3, or an MP3 Media Foundation's own reader won't take. Then the file's first bytes say what it
    /// really is, and the matching reader opens it.
    /// </summary>
    static WaveStream Reader(string path)
    {
        try { return new MediaFoundationReader(path); }
        catch (COMException e) when (e.HResult == UnsupportedByteStream)
        {
            return ByContent(path) ?? throw new IOException(
                $"{e.Message.TrimEnd('.', ' ')} (0x{e.HResult:X8}), and it isn't a WAV, AIFF or MP3 inside either. It starts {Start(path)}.", e);
        }
    }

    /// <summary>A reader chosen by what the file holds rather than its name, or null when nothing reads it.</summary>
    static WaveStream? ByContent(string path)
    {
        var head = new byte[12];
        using (var file = File.OpenRead(path)) file.ReadExactly(head, 0, (int)Math.Min(head.Length, file.Length));
        bool At(int offset, string tag) => System.Text.Encoding.ASCII.GetString(head, offset, tag.Length) == tag;
        if (At(0, "RIFF") && At(8, "WAVE")) return Pcm(new WaveFileReader(path));
        if (At(0, "FORM") && (At(8, "AIFF") || At(8, "AIFC"))) return new AiffFileReader(path);
        // OGG, FLAC and M4A/AAC go straight to Media Foundation's reader by contents: NAudio's MP3 reader would find a
        // fake frame somewhere in them and play noise (code review U2).
        if (At(0, "OggS") || At(0, "fLaC") || At(4, "ftyp")) return ByMediaFoundation(path);
        // An MP3 that Media Foundation's reader won't take: NAudio finds the frames itself (past any tags or junk) and
        // Windows' MP3 codec decodes them. Tried first only when the file starts like an MP3 (a tag, or a frame's sync).
        var mp3 = At(0, "ID3") || head[0] == 0xFF && (head[1] & 0xE0) == 0xE0;
        return mp3 ? Mp3(path) ?? ByMediaFoundation(path) : ByMediaFoundation(path) ?? Mp3(path);
    }

    static WaveStream? Mp3(string path)
    {
        try { return new Mp3FileReaderBase(path, format => new AcmMp3FrameDecompressor(format)); }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IOException or NAudio.MmException) { return null; }
    }

    /// <summary>Anything Media Foundation knows (M4A, FLAC, OGG …), found by its contents rather than the name.</summary>
    static WaveStream? ByMediaFoundation(string path)
    {
        var stream = File.OpenRead(path);
        try { return new Owning(new StreamMediaFoundationReader(stream), stream); }
        catch (COMException)
        {
            stream.Dispose();
            return null;
        }
    }

    /// <summary>
    /// A reader that also closes the file it reads: NAudio's StreamMediaFoundationReader leaves its stream open, which kept
    /// the file locked until the garbage collector got to it (code review U7).
    /// </summary>
    sealed class Owning(WaveStream inner, IDisposable file) : WaveStream
    {
        public override WaveFormat WaveFormat => inner.WaveFormat;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override TimeSpan TotalTime => inner.TotalTime;
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                file.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>The file's first bytes, for the message when nothing reads it: "with \"ftypM4A \"" or "with 00 00 01 BA".</summary>
    static string Start(string path)
    {
        try
        {
            var head = new byte[8];
            using (var file = File.OpenRead(path)) head = head[..file.Read(head, 0, head.Length)];
            return head.All(b => b is >= 0x20 and < 0x7F)
                ? $"with \"{System.Text.Encoding.ASCII.GetString(head)}\""
                : "with " + Convert.ToHexString(head).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + " " + b);
        }
        catch (IOException) { return "unreadable"; }
    }

    /// <summary>A WAV as PCM: compressed ones are converted by Windows' codecs.</summary>
    public static WaveStream Pcm(WaveFileReader reader)
    {
        if (reader.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat) return reader;
        try { return WaveFormatConversionStream.CreatePcmStream(reader); }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Media Foundation's reader is made by the first Read, in that thread's COM apartment, and can't be used from
    /// another: a click plays on the window's thread (single-threaded apartment) and the player reads on from a timer
    /// (the shared one), which failed with "Unable to cast COM object … IMFSourceReader … E_NOINTERFACE" (Solomon's
    /// 19 Oct note). So every read and the closing happen on a shared-apartment thread.
    /// </summary>
    sealed class MtaOnly(WaveStream inner) : WaveStream
    {
        static bool OnSta => Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
        static T Shared<T>(Func<T> f) => OnSta ? MtaThread.Run(f) : f();

        public override WaveFormat WaveFormat => inner.WaveFormat;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override TimeSpan TotalTime => inner.TotalTime;
        public override int Read(byte[] buffer, int offset, int count) => Shared(() => inner.Read(buffer, offset, count));

        protected override void Dispose(bool disposing)
        {
            if (disposing) Shared(() => { inner.Dispose(); return 0; });
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// One thread of its own in the shared apartment, for the window thread's reads: a click used to wait for a thread-pool
    /// thread, which during a scan or deep listen could take hundreds of milliseconds (code review U8).
    /// </summary>
    static class MtaThread
    {
        static readonly System.Collections.Concurrent.BlockingCollection<Action> Work = [];
        static readonly Lazy<Thread> Worker = new(() =>
        {
            var t = new Thread(() => { foreach (var job in Work.GetConsumingEnumerable()) job(); })
                { IsBackground = true, Name = "Media Foundation reads" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            return t;
        });

        public static T Run<T>(Func<T> f)
        {
            _ = Worker.Value;
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Work.Add(() =>
            {
                try { done.SetResult(f()); }
                catch (Exception e) { done.SetException(e); }
            });
            return done.Task.GetAwaiter().GetResult();
        }
    }

    /// <summary>A WAV the Sound Lab can't read: compressed ones are converted to PCM by Windows' codecs.</summary>
    public static AudioData ReadWav(string path, double maxSeconds) => Decode(Pcm(new WaveFileReader(path)), maxSeconds);

    static AudioData Decode(WaveStream stream, double maxSeconds)
    {
        using var reader = stream;
        var provider = reader.ToSampleProvider();
        var rate = provider.WaveFormat.SampleRate;
        var channels = provider.WaveFormat.Channels;
        var max = (long)(maxSeconds * rate) * channels;
        var interleaved = new List<float>(capacity: (int)Math.Min(max, 1 << 22));
        var buf = new float[rate * channels];
        int n;
        while (interleaved.Count < max && (n = provider.Read(buf, 0, buf.Length)) > 0)
            for (var i = 0; i < n && interleaved.Count < max; i++) interleaved.Add(buf[i]);
        var frames = interleaved.Count / channels;
        var data = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            data[c] = new float[frames];
            for (var f = 0; f < frames; f++) data[c][f] = interleaved[f * channels + c];
        }
        return new AudioData { SampleRate = rate, Channels = data, FileSeconds = reader.TotalTime.TotalSeconds };
    }
}
