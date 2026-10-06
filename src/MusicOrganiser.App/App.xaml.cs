using System.Windows;
using System.Windows.Threading;
using MusicOrganiser.App.ViewModels;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.App;

public partial class App : Application
{
    LibraryDb? _db;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        _db = new LibraryDb(LibraryDb.DefaultPath);
        var window = new MainWindow { DataContext = new MainViewModel(_db) };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
