using Avalonia;
using Avalonia.Logging;
using Avalonia.Media;
using MusicOrganiser.Avalonia.Diagnostics;

namespace MusicOrganiser.Avalonia;

static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Memory is cleaned up alongside the window rather than by pausing it: long jobs (scans, Deep listen, the
        // duplicate finder) churn through audio buffers, and full blocking clean-ups showed up as 0.3-1.3 s freezes.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        // This run's diary, for Claude to read (Diagnostics/SessionLog.cs): Avalonia's warnings go into it too.
        var log = SessionLog.Start();
        MusicOrganiser.Core.AppLog.Noted += (area, message) => log.Info(area, message);
        var app = BuildAvaloniaApp();
        Logger.Sink = new SessionLog.Sink(log, Logger.Sink);
        try { app.StartWithClassicDesktopLifetime(args); }
        finally { log.Dispose(); }
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()   // for characters IBM Plex doesn't have
            .With(new FontManagerOptions { DefaultFamilyName = App.TextFont })
            .LogToTrace();
}
