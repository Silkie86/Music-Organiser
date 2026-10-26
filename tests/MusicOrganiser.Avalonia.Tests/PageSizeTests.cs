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
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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

        var sizeButton = window.FindControl<Button>("PageSizeButton")!;
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
        Assert.Equal(0.9, ((ScaleTransform)PageScaler(window, main.Projects).LayoutTransform!).ScaleX, 3);

        // The other pages stay as they are, and at the smallest size "−" greys out.
        main.SelectedNav = main.Nav.First(n => n.Title == "Sound Lab");
        Settle();
        Assert.Null(PageScaler(window, main.Sounds).LayoutTransform);
        while (main.PageSmallerCommand.CanExecute(null)) main.PageSmallerCommand.Execute(null);
        Settle();
        Assert.Equal("70%", main.PageSizeText);
        Assert.False(smaller.IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void The_sidebar_scrolls_when_it_cant_fit_so_Settings_stays_reachable()
    {
        var main = new MainViewModel(_db, _sounds, dataFolder: Path.Combine(_root, "data"));
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 900 };
        window.Show();
        Settle();
        var settings = window.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.DataContext is NavItem { Title: "Settings" });
        var rail = settings.GetVisualAncestors().OfType<ScrollViewer>().Last();   // the nav list's own one is nearer
        // Room to spare: no scrolling, and Settings sits at the bottom as before.
        Assert.True(rail.Extent.Height <= rail.Viewport.Height + 0.5);
        Assert.True(settings.TranslatePoint(default, rail)!.Value.Y > rail.Viewport.Height / 2);

        // Settings' biggest size on a short screen: the rail scrolls.
        window.Scaler.LayoutTransform = new ScaleTransform(1.5, 1.5);
        window.Height = 560;
        Settle();
        Assert.True(rail.Extent.Height > rail.Viewport.Height);
        window.Close();
    }

    /// <summary>The scaler around <paramref name="page"/>'s view (each page has its own, so one fading out keeps its size).</summary>
    static LayoutTransformControl PageScaler(Window window, object page) =>
        window.GetVisualDescendants().OfType<LayoutTransformControl>()
            .Single(l => l.Classes.Contains("pageScaler") && l.GetVisualDescendants().OfType<UserControl>().Any(u => u.DataContext == page));

    [AvaloniaFact]
    public void A_page_size_step_leaves_the_theme_alone_and_a_theme_click_puts_it_on_screen_once_it_has_settled()
    {
        // Review: every settings change re-applied the whole theme, twice for each Ctrl+= and four times for a theme click.
        var settings = new AppearanceViewModel(_db);
        Views.Appearance.Follow(settings, null);
        object Face() => Application.Current!.Resources["StudioFaceBrush"]!;
        var face = Face();

        Assert.True(settings.StepPage("projects", 1));
        Settle();
        Assert.Same(face, Face());

        settings.Theme = AppearanceViewModel.Themes.First(t => t.Light);
        Assert.Same(face, Face());   // waits for the rest of the click's changes
        Settle();
        Assert.NotSame(face, Face());

        settings.Theme = AppearanceViewModel.Themes[0];   // the app's own look again, for the other tests
        Settle();
    }
}
