using System.Diagnostics;

namespace MusicOrganiser.Core.Space;

/// <summary>
/// One step the app needs administrator rights for (binning plugin files in Program Files, moving a folder Windows
/// guards): the app starts itself again as administrator for just that step, Windows asks you once, and it waits.
/// The same as Explorer's "You'll need to provide administrator permission". Program.cs hands the new copy to the step.
/// </summary>
static class AdminStep
{
    /// <summary>Runs the app as administrator with <paramref name="args"/> and waits. Returns "" once it ran, or what stopped it.</summary>
    public static string Run(params string[] args)
    {
        if (!OperatingSystem.IsWindows()) return "Only Windows has this administrator step.";
        if (Environment.ProcessPath is not { } exe) return "The app couldn't find itself to ask Windows.";
        var line = string.Join(' ', args.Select(a => $"\"{a}\""));
        // Run with `dotnet app.dll`, the app is the dll after dotnet.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } dll)
            line = $"\"{dll}\" {line}";
        try
        {
            using var helper = Process.Start(new ProcessStartInfo(exe, line) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            helper?.WaitForExit();
            return "";
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return "You said no to Windows' administrator question, so nothing changed.";
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return e.Message;
        }
    }
}
