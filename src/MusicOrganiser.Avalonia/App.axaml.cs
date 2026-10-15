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
            main.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SelectedNav)) SessionLog.Current?.Info("Page", main.SelectedNav?.Title ?? "");
            };
            desktop.Exit += (_, _) =>
            {
                main.Shutdown();
                _sounds?.Dispose();
                _soundDb?.Dispose();
                _db?.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
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
