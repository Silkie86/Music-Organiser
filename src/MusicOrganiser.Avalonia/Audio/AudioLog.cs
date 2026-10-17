using System.Diagnostics;
using MusicOrganiser.Core.LiveTests;

namespace MusicOrganiser.Avalonia.Audio;

/// <summary>
/// What the player did, in logs\audio.log in the app's data folder: the sound library and output it opened, and for
/// each sound its format and whether it kept time. It's there so Claude can read why a sound wasn't heard straight
/// from Solomon's computer. Debug builds also have OpenAL Soft write its own log beside it (openal.log).
/// </summary>
static class AudioLog
{
    /// <summary>Past this the log starts again, keeping the last one as audio.old.log.</summary>
    const long MaxBytes = 1 << 20;

    static readonly object Gate = new();

    /// <summary>logs\ in the app's data folder; tests point it at a folder of their own.</summary>
    internal static string Folder { get; set; } = Path.Combine(LiveReports.DataFolder, "logs");

    public static void Write(string line)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}";
        Debug.WriteLine("[audio] " + text);
        lock (Gate)
            try
            {
                Directory.CreateDirectory(Folder);
                var file = Path.Combine(Folder, "audio.log");
                if (new FileInfo(file) is { Exists: true, Length: > MaxBytes }) File.Move(file, Path.Combine(Folder, "audio.old.log"), overwrite: true);
                File.AppendAllText(file, text + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // no log is better than no sound
    }
}
