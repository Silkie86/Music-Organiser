using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia;

public partial class App : Application
{
    LibraryDb? _db;
    SoundLibrary? _soundDb;
    SoundOrganiser? _sounds;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Ui.Current = new AvaloniaUi();
            _db = new LibraryDb(LibraryDb.DefaultPath);
            _soundDb = new SoundLibrary(SoundLibrary.DefaultPath);
            _sounds = new SoundOrganiser(_soundDb);
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(_db, _sounds) };
            desktop.Exit += (_, _) =>
            {
                _sounds?.Dispose();
                _soundDb?.Dispose();
                _db?.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
