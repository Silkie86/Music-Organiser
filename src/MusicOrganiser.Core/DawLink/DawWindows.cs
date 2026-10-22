using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MusicOrganiser.Core.DawLink;

/// <summary>
/// A DAW's window, on Windows. Bitwig may only act on some of what it's asked (its Save action, say) while its window is in
/// front, as when one presses its keyboard shortcut.
/// </summary>
public static class DawWindows
{
    /// <summary>
    /// Brings the main window of the program whose process name starts with <paramref name="name"/> ("Bitwig") to the
    /// front, restoring it if it's minimised. Returns whether it's in front afterwards, or null where that can't be told:
    /// not on Windows, or no such window. Windows lets a program put another in front only while it's in front itself (as
    /// the app is when its button was just pressed), so this can come back false.
    /// </summary>
    public static bool? BringToFront(string name)
    {
        if (!OperatingSystem.IsWindows() || MainWindow(name) is not { } window) return null;
        try
        {
            if (IsIconic(window)) ShowWindow(window, Restore);
            SetForegroundWindow(window);
            return GetForegroundWindow() == window;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The main window of the first running program whose process name starts with <paramref name="name"/>, if any.</summary>
    static IntPtr? MainWindow(string name)
    {
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or PlatformNotSupportedException) { return null; }
        foreach (var p in all)
        {
            try
            {
                // Bitwig runs its audio engine and plugin hosts as programs of their own, without a window.
                if (p.ProcessName.StartsWith(name, StringComparison.OrdinalIgnoreCase) && p.MainWindowHandle != IntPtr.Zero)
                    return p.MainWindowHandle;
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }   // ended, or not ours to look at
            finally { p.Dispose(); }
        }
        return null;
    }

    const int Restore = 9;   // SW_RESTORE

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int show);
}
