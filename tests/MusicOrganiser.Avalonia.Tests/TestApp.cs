using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(MusicOrganiser.Avalonia.Tests.TestApp))]

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>
/// The real app (its styles, colours and DataGrid theme) on Avalonia's headless platform, with no
/// window on screen. The app only opens its databases on a desktop, so tests never touch them.
/// </summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
