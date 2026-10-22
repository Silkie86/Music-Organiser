namespace MusicOrganiser.Core;

/// <summary>
/// Notes about what the app is doing (how the galaxy is drawn, how long a frame takes), for this run's log. Whoever
/// keeps the log listens to <see cref="Noted"/>; with no one listening, the notes go nowhere.
/// </summary>
public static class AppLog
{
    /// <summary>A note: the part of the app it's about, then what happened.</summary>
    public static event Action<string, string>? Noted;

    /// <summary>MO_LOG_CONSOLE=1 also prints the notes, for a run with no log (a cloud test run).</summary>
    static readonly bool Echo = Environment.GetEnvironmentVariable("MO_LOG_CONSOLE") is not null;

    public static void Info(string area, string message)
    {
        if (Echo) Console.Error.WriteLine($"[{area}] {message}");
        Noted?.Invoke(area, message);
    }
}
