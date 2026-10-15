using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MusicOrganiser.Avalonia.Diagnostics;
using MusicOrganiser.Core.Library;
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
            Ui.Current = new AvaloniaUi();
            if (OperatingSystem.IsWindows()) Audio.WindowsDecoders.Register();
            _db = new LibraryDb(LibraryDb.DefaultPath);
            _soundDb = new SoundLibrary(SoundLibrary.DefaultPath);
            _sounds = new SoundOrganiser(_soundDb);
            var main = new MainViewModel(_db, _sounds) { AppChecks = AppChecks.All };
            var window = new MainWindow { DataContext = main };
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
            // Freezes, and each job's start and end, in the run log too, so a note to Claude says what froze.
            var watchdog = new UiWatchdog(main);
            Facts(window);
            main.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SelectedNav)) SessionLog.Current?.Info("Page", main.SelectedNav?.Title ?? "");
            };
            desktop.Exit += (_, _) =>
            {
                watchdog.Dispose();
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
            Task.Run(() =>
            {
                log.Info("Facts", Audio.OpenAlPlayer.HasOutput ? "Sound output found" : "No sound output found");
                log.Info("Facts", Core.Plugins.PluginScanner.Find() is { } scanner ? $"Plugin scanner: {scanner}" : "Plugin scanner (mo-scan) not found");
            });
        };
        // The galaxy says which way it draws the first time it draws (Views/GalaxyGpu.cs).
        Views.GalaxyGpu.Changed += () => log.Info("Facts", "Galaxy: the graphics card version failed, drawing on the processor");
    }

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
