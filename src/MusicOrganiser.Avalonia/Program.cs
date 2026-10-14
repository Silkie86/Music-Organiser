using Avalonia;
using Avalonia.Media;

namespace MusicOrganiser.Avalonia;

static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()   // for characters IBM Plex doesn't have
            .With(new FontManagerOptions { DefaultFamilyName = App.TextFont })
            .LogToTrace();
}
