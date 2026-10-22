using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using MusicOrganiser.Avalonia.Audio;

[assembly: AvaloniaTestApplication(typeof(MusicOrganiser.Avalonia.Tests.TestApp))]

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>
/// The real app (its styles, colours and DataGrid theme) on Avalonia's headless platform, with no
/// window on screen. The app only opens its databases on a desktop, so tests never touch them.
/// </summary>
public static class TestApp
{
    /// <summary>
    /// Drawing is skipped unless MO_HEADLESS_SKIA=1 asks for real drawing with Skia (on the processor), as the sweep
    /// timing does, so a frame takes as long as it would in the app.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        Environment.GetEnvironmentVariable("MO_HEADLESS_SKIA") == "1"
            ? AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            : AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

    /// <summary>Before any test runs: the player's log goes in a folder of the tests' own, never the app's data folder.</summary>
    [ModuleInitializer]
    internal static void KeepLogsOutOfTheAppsFolder() =>
        AudioLog.Folder = Directory.CreateTempSubdirectory("mo-audio-log-").FullName;
}
