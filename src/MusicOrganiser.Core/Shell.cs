using System.Diagnostics;

namespace MusicOrganiser.Core;

/// <summary>Opening things with Windows: projects in their DAW, folders in Explorer, links in the browser.</summary>
public static class Shell
{
    /// <summary>Opens a file with its default app, which for project files is the DAW that owns the extension.</summary>
    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>Opens a project with a specific DAW executable chosen in Settings.</summary>
    public static void OpenWith(string path, string exe) =>
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { path } });

    public static void Reveal(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path } });
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
            Open(dir);
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
