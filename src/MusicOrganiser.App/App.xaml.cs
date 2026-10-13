using System.Windows;
using System.Windows.Threading;
using MusicOrganiser.ViewModels;
using MusicOrganiser.App.Audio;
using MusicOrganiser.App.Platform;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.App;

public partial class App : Application
{
    LibraryDb? _db;
    SoundLibrary? _soundDb;
    SoundOrganiser? _sounds;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        Ui.Current = new WpfUi();
        _db = new LibraryDb(LibraryDb.DefaultPath);
        AudioPlayer.RegisterDecoders();
        _soundDb = new SoundLibrary(SoundLibrary.DefaultPath);
        _sounds = new SoundOrganiser(_soundDb);
        var window = new MainWindow { DataContext = new MainViewModel(_db, _sounds) };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _sounds?.Dispose();
        _soundDb?.Dispose();
        _db?.Dispose();
        base.OnExit(e);
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app alive and say what went wrong rather than vanishing.
        MessageBox.Show(e.Exception.Message, "Music Organiser", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
