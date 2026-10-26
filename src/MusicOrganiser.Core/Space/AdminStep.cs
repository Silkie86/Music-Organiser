using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MusicOrganiser.Core.Space;

/// <summary>
/// One step the app needs administrator rights for (binning plugin files in Program Files, moving a folder Windows
/// guards): the app starts itself again as administrator for just that step, Windows asks you once, and it waits.
/// The same as Explorer's "You'll need to provide administrator permission". Program.cs hands the new copy to the step.
/// </summary>
public static class AdminStep
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

    // A list for the step sits in your Temp folder, where any program of yours can change it while Windows asks. Its
    // fingerprint goes on the step's command line, which nothing can change, and the step does nothing if they differ.

    /// <summary>Writes the list of what the step is to do, and returns its fingerprint to pass to the step with it.</summary>
    public static string WriteList(string path, IEnumerable<string> lines)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));
        File.WriteAllBytes(path, bytes);
        return Fingerprint(bytes);
    }

    /// <summary>The list's lines, read once, or null when it isn't the list with that fingerprint: something changed it.</summary>
    public static string[]? ReadList(string path, string fingerprint)
    {
        var bytes = File.ReadAllBytes(path);
        if (!Fingerprint(bytes).Equals(fingerprint, StringComparison.OrdinalIgnoreCase)) return null;
        return [.. Encoding.UTF8.GetString(bytes).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0)];
    }

    /// <summary>
    /// Writes what the step did for the app to read. Only as a new file: one already there (a link put in its place
    /// meanwhile) is left alone, so the step never writes over something else with administrator rights.
    /// </summary>
    public static void WriteResult(string path, string text)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(file);
        writer.Write(text);
    }

    /// <summary>
    /// The same, one line at a time as each is worked out: the file is made before the first line is, so when it can't
    /// be made (something already there) nothing is done at all.
    /// </summary>
    public static void WriteResult(string path, IEnumerable<string> lines)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(file);
        foreach (var line in lines) writer.WriteLine(line);
    }

    static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
