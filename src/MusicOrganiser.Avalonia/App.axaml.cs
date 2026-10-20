using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MusicOrganiser.Avalonia.Diagnostics;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.LiveTests;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia;

public partial class App : Application
{
    LibraryDb? _db;
    SoundLibrary? _soundDb;
    SoundOrganiser? _sounds;

    /// <summary>The app's font: IBM Plex Sans, built into the app (Assets/Fonts).</summary>
    public const string TextFont = "avares://MusicOrganiser.Avalonia/Assets/Fonts#IBM Plex Sans";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.UnhandledException += OnUnhandled;
            Core.Parallelism.FavourThisThread();
            Ui.Current = new AvaloniaUi();
            if (OperatingSystem.IsWindows()) Audio.WindowsDecoders.Register();
            _db = new LibraryDb(LibraryDb.DefaultPath);
            _soundDb = new SoundLibrary(SoundLibrary.DefaultPath);
            _sounds = new SoundOrganiser(_soundDb);
            var main = new MainViewModel(_db, _sounds) { AppChecks = AppChecks.All };
            var window = new MainWindow { DataContext = main };
            // A test build (Debug, as the run-latest launcher starts it) names its commit in the title, so an app left
            // open on an old build is easy to spot: live edits don't change it, only a fresh build does.
#if DEBUG
            if (Commit(LiveReports.AppVersion) is { } commit) window.Title += $" \u00b7 test build {commit}";
#endif
            desktop.MainWindow = window;
            Views.Appearance.Follow(main.Settings.Appearance, window.Scaler);
            // F12: a note to Claude about what's on screen; and each page opened goes in the log, so a note or an
            // error there says where Solomon was.
            window.KeyDown += async (_, e) =>
            {
                if (!NoteForClaude.Gesture.Matches(e)) return;
                e.Handled = true;
                await NoteForClaude.AskAsync(window, main);
            };
            // Freezes, and each job's start and end, in the run log too, so a note to Claude says what froze. It starts
            // once the window shows: the work before that (the window being built) isn't a freeze anyone sees.
            UiWatchdog? watchdog = null;
            window.Opened += (_, _) => watchdog ??= new UiWatchdog(main);
            Facts(window);
            main.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SelectedNav)) SessionLog.Current?.Info("Page", main.SelectedNav?.Title ?? "");
            };
            desktop.Exit += (_, _) =>
            {
                watchdog?.Dispose();
                main.Shutdown();
                _sounds?.Dispose();
                _soundDb?.Dispose();
                _db?.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>One-off facts about this computer for the run log, once the window is up.</summary>
    static void Facts(MainWindow window)
    {
        var log = SessionLog.Current;
        if (log is null) return;
        window.Opened += (_, _) =>
        {
            log.Info("Facts", $"Screen scale {window.RenderScaling:0.##}, window {window.Bounds.Width:0}×{window.Bounds.Height:0}");
            // Not the sound output: finding it opens the audio device, which the app leaves until a sound plays (a DAW
            // may want the interface to itself). Audio/AudioLog.cs says which output opened, when one does.
            Task.Run(() =>
                log.Info("Facts", Core.Plugins.PluginScanner.Find() is { } scanner ? $"Plugin scanner: {scanner}" : "Plugin scanner (mo-scan) not found"));
        };
        // The galaxy logs which way it draws itself, through Core.AppLog (Views/GalaxyGpu.cs, wired up in Program.cs).
    }

    /// <summary>The short commit in a version like "1.0.0+69948c3d…", or null when the build didn't know it.</summary>
    internal static string? Commit(string version) =>
        version.IndexOf('+') is var plus and >= 0 && version.Length - plus > 7 ? version.Substring(plus + 1, 7) : null;

    readonly List<string> _errors = [];

    /// <summary>
    /// Keeps the app alive and says what went wrong rather than vanishing. Errors that happen while
    /// one is on screen are shown after it, once each.
    /// </summary>
    async void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        SessionLog.Current?.Error("App", "Error shown to the user", e.Exception);
        if (_errors.Contains(e.Exception.Message)) return;
        _errors.Add(e.Exception.Message);
        if (_errors.Count > 1) return;   // already showing one; this one waits its turn
        while (_errors.Count > 0)
        {
            if ((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is { IsVisible: true } owner)
                await new ConfirmWindow(_errors[0], "Music Organiser", okOnly: true).ShowDialog(owner);
            _errors.RemoveAt(0);
        }
    }
}
