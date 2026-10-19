using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MusicOrganiser.Core.DawLink;

/// <summary>
/// The Music Organiser MIDI port, through Windows' own MIDI functions (WinMM): the app sends its requests into it
/// and hears FL Studio's answers come back. With loopMIDI that's the one port, in and out, and the app hears its
/// own requests come back too (it leaves them, as the script leaves its answers). With the loopback pair Windows'
/// MIDI system makes (<see cref="WindowsMidiPort"/>), the app uses the spare end, so what it sends comes out of
/// the Music Organiser end that FL Studio has open, and the other way round.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FlMidi : IFlMidi, IDisposable
{
    const int Buffers = 32, BufferBytes = 2048, LongestMessage = 1 << 16;
    static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);
    static readonly int HeaderSize = Marshal.SizeOf<MidiHeader>() + 8 * IntPtr.Size;   // and MIDIHDR's dwReserved[8]

    static readonly object Shared_ = new();
    static FlMidi? _shared;

    readonly IntPtr _in, _out;
    readonly MidiInProc _callback;   // kept here, so it's never collected while Windows can call it
    readonly List<IntPtr> _headers = [];
    readonly BlockingCollection<(IntPtr Header, bool Error)> _done = new();
    readonly Thread _reader;
    readonly object _gate = new(), _sending = new();
    readonly List<byte> _partial = [];
    bool _closing;

    /// <summary>The ports it uses, as Windows names them.</summary>
    public string InputName { get; }
    public string OutputName { get; }

    /// <summary>Set once sending fails (say, loopMIDI was closed), so the next request opens the port again.</summary>
    public bool Broken { get; private set; }

    public event Action<byte[]>? Received;

    /// <summary>
    /// The app's one connection to the port, opened when first needed and again after it stops working, or null when
    /// there's no Music Organiser port to open.
    /// </summary>
    public static FlMidi? Shared()
    {
        lock (Shared_)
        {
            if (_shared is { Broken: false } open) return open;
            _shared?.Dispose();
            _shared = null;
            return _shared = Open();
        }
    }

    /// <summary>Opens the port, or returns null when there isn't one.</summary>
    public static FlMidi? Open()
    {
        var inputs = FlSetup.MidiPorts(inputs: true) ?? [];
        var outputs = FlSetup.MidiPorts(inputs: false) ?? [];
        foreach (var name in new[] { WindowsMidiPort.SpareEnd, FlSetup.PortName })
        {
            var i = FlSetup.FindPort(inputs, name);
            var o = FlSetup.FindPort(outputs, name);
            if (i >= 0 && o >= 0) return new FlMidi((uint)i, inputs[i], (uint)o, outputs[o]);
        }
        return null;
    }

    FlMidi(uint input, string inputName, uint output, string outputName)
    {
        InputName = inputName;
        OutputName = outputName;
        _callback = OnMidiIn;
        _reader = new Thread(Read) { IsBackground = true, Name = "FL Studio link" };
        try
        {
            Check(midiOutOpen(out _out, output, IntPtr.Zero, IntPtr.Zero, 0), "open", outputName);
            Check(midiInOpen(out _in, input, _callback, IntPtr.Zero, CallbackFunction), "open", inputName);
            for (var b = 0; b < Buffers; b++)
            {
                var header = Marshal.AllocHGlobal(HeaderSize);
                Marshal.Copy(new byte[HeaderSize], 0, header, HeaderSize);
                _headers.Add(header);
                Marshal.StructureToPtr(new MidiHeader { Data = Marshal.AllocHGlobal(BufferBytes), BufferLength = BufferBytes }, header, false);
                Check(midiInPrepareHeader(_in, header, (uint)HeaderSize), "open", inputName);
                Check(midiInAddBuffer(_in, header, (uint)HeaderSize), "open", inputName);
            }
            _reader.Start();
            Check(midiInStart(_in), "open", inputName);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Sends one SysEx message, waiting until Windows has taken it.</summary>
    public void Send(byte[] message)
    {
        lock (_sending)
        {
            if (_closing) throw new ObjectDisposedException(nameof(FlMidi));
            var data = Marshal.AllocHGlobal(message.Length);
            var header = Marshal.AllocHGlobal(HeaderSize);
            try
            {
                Marshal.Copy(message, 0, data, message.Length);
                Marshal.Copy(new byte[HeaderSize], 0, header, HeaderSize);
                Marshal.StructureToPtr(new MidiHeader { Data = data, BufferLength = (uint)message.Length, BytesRecorded = (uint)message.Length },
                                       header, false);
                Check(midiOutPrepareHeader(_out, header, (uint)HeaderSize), "send to", OutputName);
                try
                {
                    Check(midiOutLongMsg(_out, header, (uint)HeaderSize), "send to", OutputName);
                    var waited = Stopwatch.StartNew();
                    while ((Header(header).Flags & Done) == 0)
                    {
                        if (waited.Elapsed > SendTimeout) throw new DawLinkException($"The MIDI port {OutputName} didn't take the app's message.");
                        Thread.Sleep(1);
                    }
                }
                finally
                {
                    if ((Header(header).Flags & Done) == 0) midiOutReset(_out);   // hands it back, so it can be let go
                    midiOutUnprepareHeader(_out, header, (uint)HeaderSize);
                }
            }
            catch
            {
                Broken = true;
                throw;
            }
            finally
            {
                Marshal.FreeHGlobal(header);
                Marshal.FreeHGlobal(data);
            }
        }
    }

    // Windows calls this on a thread of its own, where calling its MIDI functions can deadlock, so the buffer goes
    // to the reader thread, which takes what's in it and gives it back.
    void OnMidiIn(IntPtr handle, uint message, IntPtr instance, IntPtr param1, IntPtr param2)
    {
        if (message is not (MimLongData or MimLongError)) return;
        try { _done.Add((param1, message == MimLongError)); }
        catch (InvalidOperationException) { }   // closing
    }

    void Read()
    {
        foreach (var (header, error) in _done.GetConsumingEnumerable())
        {
            var h = Header(header);
            if (error) _partial.Clear();   // a broken message
            else if (h.BytesRecorded > 0)
            {
                var bytes = new byte[h.BytesRecorded];
                Marshal.Copy(h.Data, bytes, 0, bytes.Length);
                Collect(bytes);
            }
            lock (_gate)
            {
                if (_closing) continue;
                h.BytesRecorded = 0;
                h.Flags &= ~Done;
                Marshal.StructureToPtr(h, header, false);
                midiInAddBuffer(_in, header, (uint)HeaderSize);
            }
        }
    }

    /// <summary>Puts SysEx messages back together: Windows splits one that's longer than a buffer.</summary>
    void Collect(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            if (b == 0xF0) _partial.Clear();
            else if (_partial.Count == 0 || _partial.Count >= LongestMessage) continue;   // the middle of one it missed the start of
            _partial.Add(b);
            if (b != 0xF7) continue;
            var message = _partial.ToArray();
            _partial.Clear();
            try { Received?.Invoke(message); }
            catch (Exception e) { Debug.WriteLine($"FL Studio link: {e}"); }
        }
    }

    public void Dispose()
    {
        lock (_sending)
        lock (_gate)
        {
            if (_closing) return;
            _closing = true;
            if (_in != IntPtr.Zero)
            {
                midiInStop(_in);
                midiInReset(_in);   // hands every buffer back
            }
        }
        _done.CompleteAdding();
        if (_reader.IsAlive) _reader.Join(TimeSpan.FromSeconds(2));
        foreach (var header in _headers)
        {
            if (_in != IntPtr.Zero) midiInUnprepareHeader(_in, header, (uint)HeaderSize);
            Marshal.FreeHGlobal(Header(header).Data);
            Marshal.FreeHGlobal(header);
        }
        _headers.Clear();
        if (_in != IntPtr.Zero) midiInClose(_in);
        if (_out != IntPtr.Zero) midiOutClose(_out);
    }

    static MidiHeader Header(IntPtr header) => Marshal.PtrToStructure<MidiHeader>(header);

    static void Check(uint result, string doing, string port)
    {
        if (result == 0) return;
        var text = new StringBuilder(256);
        var said = midiOutGetErrorTextW(result, text, (uint)text.Capacity) == 0 ? text.ToString().TrimEnd('.') : $"error {result}";
        throw new DawLinkException($"The app couldn't {doing} the MIDI port {port}: {said}.");
    }

    const uint CallbackFunction = 0x30000, MimLongData = 0x3C4, MimLongError = 0x3C6, Done = 1;

    /// <summary>MIDIHDR, up to dwOffset; the space after it (dwReserved) is left as allocated.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct MidiHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public IntPtr Next, Reserved;
        public uint Offset;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void MidiInProc(IntPtr handle, uint message, IntPtr instance, IntPtr param1, IntPtr param2);

    [DllImport("winmm.dll")] static extern uint midiInOpen(out IntPtr handle, uint device, MidiInProc callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern uint midiInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint midiInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint midiInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint midiInStart(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint midiInStop(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint midiInReset(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint midiInClose(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint midiOutOpen(out IntPtr handle, uint device, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern uint midiOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint midiOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint midiOutLongMsg(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint midiOutReset(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint midiOutClose(IntPtr handle);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint midiOutGetErrorTextW(uint error, StringBuilder text, uint size);
}
