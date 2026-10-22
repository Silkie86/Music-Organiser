using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MusicOrganiser.Core;

/// <summary>
/// Opening things with the operating system: projects in their DAW, folders in Explorer / Finder / the file manager,
/// links in the browser. Windows behaves as it always has; macOS and Linux use their own launchers.
/// </summary>
public static class Shell
{
    /// <summary>Opens a file with its default app, which for project files is the DAW that owns the extension.</summary>
    public static void Open(string path)
    {
        if (OperatingSystem.IsMacOS()) Run("open", path);
        else if (OperatingSystem.IsLinux()) Run("xdg-open", path);
        else if (File.Exists(path)) StartDetached(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), path);
        else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Opens a project with a specific DAW chosen in Settings (an .exe on Windows, an .app bundle on a Mac).</summary>
    public static void OpenWith(string path, string exe)
    {
        if (OperatingSystem.IsMacOS() && exe.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            Run("open", "-a", exe, path);
        else if (OperatingSystem.IsWindows())
            StartDetached(exe, path);
        else
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { path } });
    }

    public static void Reveal(string path)
    {
        if (File.Exists(path))
        {
            if (OperatingSystem.IsMacOS()) Run("open", "-R", path);
            else if (OperatingSystem.IsLinux()) RevealOnLinux(path);
            else Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path } });
        }
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
            Open(dir);
    }

    public static void OpenUrl(string url) => Open(url);

    /// <summary>
    /// Asks the file manager to open the folder with the file picked, through the freedesktop
    /// FileManager1 call that Nautilus, Dolphin, Nemo, Caja and Thunar answer; just the folder when
    /// none does. In the background, since the file manager may take a moment to start.
    /// </summary>
    static void RevealOnLinux(string path) => Task.Run(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("dbus-send") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in ShowItemsArgs(path)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is not null && p.WaitForExit(5000) && p.ExitCode == 0) return;
        }
        catch (Exception)
        {
            // no dbus-send here: open the folder instead
        }
        Open(Path.GetDirectoryName(path)!);
    });

    /// <summary>
    /// The dbus-send arguments that show <paramref name="path"/> picked in the file manager. dbus-send splits an
    /// array at its commas, and a file link may keep them, so they're written as %2C, which the file manager reads back.
    /// </summary>
    public static string[] ShowItemsArgs(string path) =>
    [
        "--session", "--print-reply", "--reply-timeout=4000", "--dest=org.freedesktop.FileManager1",
        "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
        "array:string:" + new UriBuilder { Scheme = Uri.UriSchemeFile, Host = "", Path = path }.Uri.AbsoluteUri.Replace(",", "%2C"), "string:",
    ];

    /// <summary>
    /// Starts a program on Windows with nothing tying it to this app: no shared console window, its own Ctrl+C
    /// group, and out of any job this app runs in. A DAW the app opens otherwise goes down with the app when its
    /// console gets Ctrl+C or is closed, or when dotnet watch restarts it, losing unsaved work.
    /// (Opening a file through Explorer does the same for "open with its default app".)
    /// </summary>
    static void StartDetached(string exe, string argument)
    {
        const uint DetachedProcess = 0x8, NewProcessGroup = 0x200, BreakawayFromJob = 0x0100_0000;
        var line = new StringBuilder(WindowsCommandLine(exe, argument));
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessW(null, line, IntPtr.Zero, IntPtr.Zero, false, DetachedProcess | NewProcessGroup | BreakawayFromJob,
                IntPtr.Zero, null, ref startup, out var info) &&
            // a job that doesn't allow leaving it: start it detached anyway
            !CreateProcessW(null, line, IntPtr.Zero, IntPtr.Zero, false, DetachedProcess | NewProcessGroup,
                IntPtr.Zero, null, ref startup, out info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(info.hProcess);
        CloseHandle(info.hThread);
    }

    /// <summary>A Windows command line that runs <paramref name="exe"/> with each argument passed through exactly.</summary>
    public static string WindowsCommandLine(string exe, params string[] args) =>
        string.Join(' ', new[] { exe }.Concat(args).Select(Quote));

    static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            // backslashes only need doubling right before a quote
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    static void Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}
