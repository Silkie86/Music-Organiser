using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MusicOrganiser.Core.DawLink;

/// <summary>How the MIDI port FL Studio runs the script for is made, chosen in Settings (Windows only).</summary>
public enum FlMidiPort
{
    /// <summary>In loopMIDI, a free app, by hand.</summary>
    LoopMidi,
    /// <summary>By the app, with Windows' own MIDI system (Windows MIDI Services): see <see cref="WindowsMidiPort"/>.</summary>
    WindowsMidi,
}

/// <summary>
/// Works out why FL Studio isn't answering, from what this computer shows: whether FL Studio is open, and its
/// MIDI inputs. FL Studio only runs a script as the controller type of a MIDI input, and the app talks to the script
/// through that port (<see cref="FlMidi"/>), so it has to be one the app can send into, like a loopMIDI port. FL
/// Studio picks the script by itself for a port called Music Organiser.
/// </summary>
public static class FlSetup
{
    /// <summary>The MIDI input the script asks FL Studio for (supportedDevices in the script).</summary>
    public const string PortName = "Music Organiser";
    public const string LoopMidiUrl = "https://www.tobias-erichsen.de/software/loopmidi.html";

    /// <summary>How the port is made, so the messages say what to do. Settings sets it from the choice there.</summary>
    public static FlMidiPort Port { get; set; } = FlMidiPort.LoopMidi;

    public const string NotOpen = "FL Studio doesn't seem to be open. Open it, then try again.";
    public const string NoPort = "FL Studio is open, but there's no MIDI port called Music Organiser, which the app and the script " +
                                 "talk through. Make one in loopMIDI (free; it has to be running for the port to be there), then " +
                                 "restart FL Studio.";
    public const string NoWindowsPort = "FL Studio is open, but there's no MIDI port called Music Organiser, which the app and the " +
                                        "script talk through. Press Install for FL Studio in Settings > DAW links to make one with " +
                                        "Windows' MIDI system, then restart FL Studio.";
    public const string NotPicked = "FL Studio is open and there's a Music Organiser MIDI port, but the script isn't answering on it. " +
                                    "In FL Studio's Options > MIDI settings, check that the Music Organiser input is enabled with " +
                                    "Controller type Music Organiser, and give the Music Organiser output the same Port number as " +
                                    "the input: the script answers through it. If you've just installed or updated the script, " +
                                    "restart FL Studio first.";

    public const string PortThere = "There's a MIDI port called Music Organiser already, so FL Studio should pick the script for it by itself.";

    /// <summary>Why FL Studio isn't answering, as exactly as this computer can tell.</summary>
    public static string WhyNotAnswering() => WhyNotAnswering(IsFlStudioOpen(), MidiInputs(), Port);

    /// <param name="open">Whether FL Studio is running, or null if the app can't tell.</param>
    /// <param name="inputs">The computer's MIDI inputs, or null if the app can't list them.</param>
    /// <param name="port">How the port is made, for what to say when there isn't one.</param>
    public static string WhyNotAnswering(bool? open, IReadOnlyList<string>? inputs, FlMidiPort port = FlMidiPort.LoopMidi) =>
        (open, inputs) switch
        {
            (false, _) => NotOpen,
            (true, { } all) when !HasOurPort(all) => port == FlMidiPort.WindowsMidi ? NoWindowsPort : NoPort,
            (true, { }) => NotPicked,
            _ => FlLink.NotRunning,
        };

    /// <summary>Whether one of these MIDI inputs is the port for the script.</summary>
    public static bool HasOurPort(IEnumerable<string> inputs) =>
        inputs.Any(name => name.Contains(PortName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether loopMIDI is installed or running. Null off Windows.</summary>
    public static bool? IsLoopMidiInstalled()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var programs in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var folder = Environment.GetFolderPath(programs);
            if (folder.Length > 0 && File.Exists(Path.Combine(folder, "Tobias Erichsen", "loopMIDI", "loopMIDI.exe"))) return true;
        }
        return IsRunning("loopMIDI");
    }

    /// <summary>Whether FL Studio (FL64, or FL) is running. Null off Windows, where the app can't tell yet.</summary>
    public static bool? IsFlStudioOpen() => OperatingSystem.IsWindows() ? IsRunning("FL64", "FL") : null;

    static bool IsRunning(params string[] names)
    {
        foreach (var name in names)
        {
            var found = Process.GetProcessesByName(name);
            foreach (var p in found) p.Dispose();
            if (found.Length > 0) return true;
        }
        return false;
    }

    /// <summary>The names of this computer's MIDI inputs, as FL Studio sees them. Null off Windows, or if Windows can't say.</summary>
    public static IReadOnlyList<string>? MidiInputs() => MidiPorts(inputs: true)?.Where(n => n.Length > 0).ToList();

    /// <summary>
    /// The names of this computer's MIDI inputs or outputs, in Windows' order, so each one's place is the number
    /// Windows opens it by (empty for one Windows couldn't describe). Null off Windows, or if Windows can't say.
    /// </summary>
    public static List<string>? MidiPorts(bool inputs)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var names = new List<string>();
            var count = inputs ? midiInGetNumDevs() : midiOutGetNumDevs();
            for (uint i = 0; i < count; i++)
            {
                if (inputs)
                {
                    var caps = new MidiInCaps();
                    names.Add(midiInGetDevCapsW(i, ref caps, (uint)Marshal.SizeOf<MidiInCaps>()) == 0 ? caps.Name : "");
                }
                else
                {
                    var caps = new MidiOutCaps();
                    names.Add(midiOutGetDevCapsW(i, ref caps, (uint)Marshal.SizeOf<MidiOutCaps>()) == 0 ? caps.Name : "");
                }
            }
            return names;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Windows' MIDIINCAPSW.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MidiInCaps
    {
        public ushort Manufacturer, Product;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Support;
    }

    /// <summary>Windows' MIDIOUTCAPSW.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MidiOutCaps
    {
        public ushort Manufacturer, Product;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public ushort Technology, Voices, Notes, ChannelMask;
        public uint Support;
    }

    [DllImport("winmm.dll")] static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint midiInGetDevCapsW(nuint device, ref MidiInCaps caps, uint size);
    [DllImport("winmm.dll")] static extern uint midiOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint midiOutGetDevCapsW(nuint device, ref MidiOutCaps caps, uint size);
}
