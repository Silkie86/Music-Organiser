using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MusicOrganiser.Core.DawLink;

/// <summary>
/// Works out why FL Studio isn't answering, from what this computer shows: whether FL Studio is open, and its
/// MIDI inputs. FL Studio only runs a script as the controller type of a MIDI input, so the script needs one even
/// though it never uses MIDI: any input will do, and FL Studio picks the script by itself for one called Music Organiser.
/// </summary>
public static class FlSetup
{
    /// <summary>The MIDI input the script asks FL Studio for (supportedDevices in the script).</summary>
    public const string PortName = "Music Organiser";

    public const string NotOpen = "FL Studio doesn't seem to be open. Open it, then try again.";
    public const string NoPort = "FL Studio is open, but the Music Organiser script isn't running. FL Studio only runs scripts for " +
                                 "MIDI inputs, and there's no MIDI port called Music Organiser. Make one in loopMIDI (free), or set " +
                                 "a spare input's Controller type to Music Organiser in Options > MIDI settings.";
    public const string NotPicked = "FL Studio is open and there's a Music Organiser MIDI port, but the script isn't running for it. " +
                                    "In FL Studio's Options > MIDI settings, check that the port is enabled and its Controller type " +
                                    "is Music Organiser. If you've just installed the script, restart FL Studio first.";

    /// <summary>Why FL Studio isn't answering, as exactly as this computer can tell.</summary>
    public static string WhyNotAnswering() => WhyNotAnswering(IsFlStudioOpen(), MidiInputs());

    /// <param name="open">Whether FL Studio is running, or null if the app can't tell.</param>
    /// <param name="inputs">The computer's MIDI inputs, or null if the app can't list them.</param>
    public static string WhyNotAnswering(bool? open, IReadOnlyList<string>? inputs) => (open, inputs) switch
    {
        (false, _) => NotOpen,
        (true, { } all) when !all.Any(IsOurPort) => NoPort,
        (true, { }) => NotPicked,
        _ => FlLink.NotRunning,
    };

    static bool IsOurPort(string name) => name.Contains(PortName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether FL Studio (FL64, or FL) is running. Null off Windows, where the app can't tell yet.</summary>
    public static bool? IsFlStudioOpen()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var name in new[] { "FL64", "FL" })
        {
            var found = Process.GetProcessesByName(name);
            foreach (var p in found) p.Dispose();
            if (found.Length > 0) return true;
        }
        return false;
    }

    /// <summary>The names of this computer's MIDI inputs, as FL Studio sees them. Null off Windows, or if Windows can't say.</summary>
    public static IReadOnlyList<string>? MidiInputs()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var names = new List<string>();
            var count = midiInGetNumDevs();
            for (uint i = 0; i < count; i++)
            {
                var caps = new MidiInCaps();
                if (midiInGetDevCapsW(i, ref caps, (uint)Marshal.SizeOf<MidiInCaps>()) == 0) names.Add(caps.Name);
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

    [DllImport("winmm.dll")] static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint midiInGetDevCapsW(nuint device, ref MidiInCaps caps, uint size);
}
