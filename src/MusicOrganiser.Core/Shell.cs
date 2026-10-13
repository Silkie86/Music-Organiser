using System.Diagnostics;

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
        else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Opens a project with a specific DAW chosen in Settings (an .exe on Windows, an .app bundle on a Mac).</summary>
    public static void OpenWith(string path, string exe)
    {
        if (OperatingSystem.IsMacOS() && exe.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            Run("open", "-a", exe, path);
        else
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { path } });
    }

    public static void Reveal(string path)
    {
        if (File.Exists(path))
        {
            if (OperatingSystem.IsMacOS()) Run("open", "-R", path);
            else if (OperatingSystem.IsLinux()) Open(Path.GetDirectoryName(path)!);   // no standard "select this file"
            else Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path } });
        }
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
            Open(dir);
    }

    public static void OpenUrl(string url) => Open(url);

    static void Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}
