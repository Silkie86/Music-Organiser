using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>The rail's size button: one page bigger or smaller, in the real window (Solomon, 21 Oct).</summary>
public sealed class PageSizeTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("mo-page-size-").FullName;
    readonly LibraryDb _db;
    readonly SoundLibrary _soundDb;
    readonly SoundOrganiser _sounds;

    public PageSizeTests()
    {
        Ui.Current = new AvaloniaUi();
        UiWork.InBackground = false;
        _db = new LibraryDb(Path.Combine(_root, "library.db"));
        _soundDb = new SoundLibrary(Path.Combine(_root, "sounds.db"));
        _sounds = new SoundOrganiser(_soundDb);
    }

    public void Dispose()
    {
        _sounds.Dispose();
        _soundDb.Dispose();
        _db.Dispose();
    }

    static void Settle()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_size_button_makes_just_this_page_smaller()
    {
        var main = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 900 };
        window.Show();
        Settle();
        main.SelectedNav = main.Nav.First(n => n.Title == "Projects");
        Settle();

        var sizeButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Flyout is not null && ToolTip.GetTip(b) is string tip && tip.Contains("size"));
        sizeButton.Flyout!.ShowAt(sizeButton);
        Settle();
        var content = (Control)((Flyout)sizeButton.Flyout).Content!;
        var smaller = content.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "−");
        var at = smaller.TranslatePoint(new Point(smaller.Bounds.Width / 2, smaller.Bounds.Height / 2), TopLevel.GetTopLevel(smaller)!)!.Value;
        var top = TopLevel.GetTopLevel(smaller)!;
        top.MouseDown(at, MouseButton.Left);
        top.MouseUp(at, MouseButton.Left);
        Settle();
        Assert.Equal("90%", main.PageSizeText);
        var scaler = window.FindControl<LayoutTransformControl>("PageScaler")!;
        Assert.Equal(0.9, ((ScaleTransform)scaler.LayoutTransform!).ScaleX, 3);
        window.Close();
    }

}
