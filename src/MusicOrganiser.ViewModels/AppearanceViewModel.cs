using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// One look for the whole app: its colours (as #RRGGBB), font and shape. <see cref="AccentText"/> is the text on
/// accent-coloured buttons, dark on a light accent. <see cref="Font"/> names one of <see cref="AppearanceViewModel.Fonts"/>.
/// <see cref="Light"/> themes are dark text on a light background (menus and boxes follow); <see cref="Square"/> ones have
/// square corners, like hardware.
/// </summary>
public sealed record AppTheme(string Name, string Description, string Background, string Sidebar, string Surface, string SurfaceRaised,
                              string Stroke, string Hover, string Text, string Muted, string Faint, string Accent, string AccentText, string Font,
                              bool Light = false, bool Square = false)
{
    /// <summary>The theme preview's corners.</summary>
    public double PreviewRadius => Square ? 1 : 4;
}

/// <summary>An accent colour to pick, or the theme's own when <see cref="Colour"/> is empty.</summary>
public sealed record AccentChoice(string Name, string Colour);

/// <summary>Settings › Appearance: the theme, accent colour, font, size of everything, and how much moves.</summary>
public sealed partial class AppearanceViewModel : ObservableObject
{
    public const string ThemeKey = "appearance_theme";
    public const string AccentKey = "appearance_accent";
    public const string FontKey = "appearance_font";
    public const string SizeKey = "appearance_size";
    public const string MotionKey = "appearance_less_motion";
    public const string PageSizesKey = "appearance_page_sizes";

    /// <summary>The fonts built into the app. "Theme's font" follows the theme.</summary>
    public const string ThemeFont = "Theme's font";
    public const string PlexFont = "IBM Plex";
    public const string SpaceFont = "Space (retro)";
    public const string InterFont = "Inter";
    public const string MonoFont = "Mono (all)";
    public static IReadOnlyList<string> Fonts { get; } = [ThemeFont, PlexFont, SpaceFont, InterFont, MonoFont];

    /// <summary>The themes; the first is the default (Solomon's pick) and what Back to normal returns to.</summary>
    public static IReadOnlyList<AppTheme> Themes { get; } =
    [
        new("Graphite", "Neutral greys with a mint accent: no colour cast at all. The app's own look.",
            "#121212", "#171717", "#1D1D1D", "#252525", "#323232", "#2A2A2A", "#F0F0F0", "#A3A3A3", "#6E6E6E", "#3DDC97", "#06180F", PlexFont),
        new("Studio", "Dark grey with a violet accent.",
            "#101114", "#15161A", "#1A1B20", "#212228", "#2A2C33", "#24252C", "#ECEDF0", "#9A9DA7", "#676A74", "#8B7CF6", "#FFFFFF", PlexFont),
        new("Retro", "Warm brown and cream with amber, like old studio gear. Typewriter titles.",
            "#17130F", "#1E1914", "#241E18", "#2D261E", "#3B3227", "#332A21", "#F3E7D0", "#B9A98D", "#7F725D", "#F2A03D", "#1A1208", SpaceFont),
        new("Midnight", "Deep blue-black with a cool blue accent, easy on the eyes at night.",
            "#0B0F1C", "#0F1424", "#131A2D", "#1A223A", "#252F4A", "#1C2541", "#E6ECF8", "#93A0BF", "#5E6A88", "#4FB3FF", "#06121F", PlexFont),
        new("Paper", "Light: warm paper and ink with a vermilion accent, like a printed track sheet. For daytime.",
            "#F4F1EA", "#EAE5D9", "#FBF9F4", "#FFFFFF", "#DCD4C3", "#ECE6DA", "#1F1B16", "#5F574B", "#958B7B", "#D9480F", "#FFFFFF", PlexFont,
            Light: true),
        new("Hardware", "Square corners, black panels and signal orange, like a drum machine's front.",
            "#161616", "#0F0F0F", "#1E1E1E", "#272727", "#383838", "#2C2C2C", "#EDEDED", "#9C9C9C", "#666666", "#FF6A13", "#140800", PlexFont,
            Square: true),
        new("Terminal", "Green phosphor on black, mono type everywhere, square corners. An 80s computer screen.",
            "#040804", "#060C06", "#0A120A", "#0F1A0F", "#1D331F", "#122214", "#B6FFC0", "#63B871", "#3A6E45", "#39FF7A", "#021006", MonoFont,
            Square: true),
        new("Synthwave", "Deep purple night with hot pink: neon, but still calm enough to work in.",
            "#140A22", "#1A0D2C", "#1F1036", "#2A1647", "#3D2263", "#2F1A50", "#F6EBFF", "#B99CD8", "#7B5F9B", "#FF3EA5", "#FFFFFF", SpaceFont),
        new("High contrast", "Pure black, white text and bright yellow, as clear as possible.",
            "#000000", "#000000", "#0A0A0A", "#141414", "#7A7A7A", "#262626", "#FFFFFF", "#E0E0E0", "#B0B0B0", "#FFD400", "#000000", PlexFont,
            Square: true),
    ];

    public static IReadOnlyList<AccentChoice> Accents { get; } =
    [
        new("Theme's own", ""), new("Violet", "#8B7CF6"), new("Blue", "#4FB3FF"), new("Mint", "#3DDC97"),
        new("Amber", "#F2A03D"), new("Coral", "#F2766B"), new("Pink", "#F28CB1"),
    ];

    /// <summary>
    /// How big everything is, in percent of normal: smaller fits more on a big screen, bigger is easier to read on a small
    /// one (Solomon, 21 Oct: "zoom in and out of the whole screen").
    /// </summary>
    public static IReadOnlyList<int> Sizes { get; } = [70, 80, 90, 100, 110, 125, 150];

    readonly LibraryDb _db;

    public AppearanceViewModel(LibraryDb db)
    {
        _db = db;
        _theme = Themes.FirstOrDefault(t => t.Name == db.GetSetting(ThemeKey)) ?? Themes[0];
        _accent = Accents.FirstOrDefault(a => a.Colour == db.GetSetting(AccentKey)) ?? Accents[0];
        _font = db.GetSetting(FontKey) is { } f && Fonts.Contains(f) ? f : ThemeFont;
        _size = int.TryParse(db.GetSetting(SizeKey), out var s) && Sizes.Contains(s) ? s : 100;
        _lessMotion = db.GetSetting(MotionKey) == "1";
        foreach (var entry in db.GetList(PageSizesKey))
            if (entry.Split('=', 2) is [var page, var size] && int.TryParse(size, out var n) && Sizes.Contains(n)) _pageSizes[page] = n;
    }

    // ---- each page's own size (Solomon, 21 Oct: "every page should be individually or you can do it all together") ----

    readonly Dictionary<string, int> _pageSizes = [];
    readonly Dictionary<string, string> _pageNames = [];

    /// <summary>
    /// The pages, by the key their sizes are saved under and their title in the sidebar. Sizes saved under a title (before
    /// 22 Oct) move to the key, so they survive a page being renamed.
    /// </summary>
    public void NamePages(IEnumerable<(string Key, string Title)> pages)
    {
        var moved = false;
        foreach (var (key, title) in pages)
        {
            _pageNames[key] = title;
            if (title != key && _pageSizes.Remove(title, out var n))
            {
                _pageSizes.TryAdd(key, n);
                moved = true;
            }
        }
        if (moved) SavePageSizes();
    }

    /// <summary>Raised when a page's own size changes, or goes back to <see cref="Size"/>.</summary>
    public event Action? PageSizesChanged;

    /// <summary>The size of <paramref name="page"/> (its key): its own if it has one, else <see cref="Size"/>.</summary>
    public int SizeFor(string page) => _pageSizes.TryGetValue(page, out var n) ? n : Size;

    /// <summary>Whether <paramref name="page"/> has a size of its own.</summary>
    public bool HasOwnSize(string page) => _pageSizes.ContainsKey(page);

    /// <summary>The pages with a size of their own, and what it is.</summary>
    public string OwnSizesText => _pageSizes.Count == 0 ? ""
        : $"Sized on their own: {string.Join(", ", _pageSizes.OrderBy(p => _pageNames.GetValueOrDefault(p.Key, p.Key)).Select(p => $"{_pageNames.GetValueOrDefault(p.Key, p.Key)} {p.Value}%"))}.";
    public bool HasOwnSizes => _pageSizes.Count > 0;

    /// <summary>One page a size up (<paramref name="by"/> 1) or down (-1), staying at the ends; landing on <see cref="Size"/> it follows that again.</summary>
    public bool StepPage(string page, int by)
    {
        var list = Sizes.ToList();
        var at = list.IndexOf(SizeFor(page));
        var next = list[Math.Clamp((at < 0 ? list.IndexOf(100) : at) + by, 0, list.Count - 1)];
        if (next == SizeFor(page)) return false;
        SetPage(page, next == Size ? null : next);
        return true;
    }

    /// <summary>The page back to the size of the rest.</summary>
    public void ResetPage(string page)
    {
        if (HasOwnSize(page)) SetPage(page, null);
    }

    /// <summary>Every page back to <see cref="Size"/>.</summary>
    [RelayCommand]
    void SameSizeEverywhere()
    {
        if (_pageSizes.Count == 0) return;
        _pageSizes.Clear();
        SavePageSizes();
    }

    void SetPage(string page, int? size)
    {
        if (size is { } n) _pageSizes[page] = n;
        else _pageSizes.Remove(page);
        SavePageSizes();
    }

    void SavePageSizes()
    {
        _db.SetList(PageSizesKey, _pageSizes.Select(p => $"{p.Key}={p.Value}"));
        OnPropertyChanged(nameof(OwnSizesText));
        OnPropertyChanged(nameof(HasOwnSizes));
        PageSizesChanged?.Invoke();
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AccentColour), nameof(AccentText), nameof(FontName))] AppTheme _theme;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AccentColour), nameof(AccentText))] AccentChoice _accent;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FontName))] string _font;
    [ObservableProperty] int _size;
    /// <summary>Calmer: no sliding, fading or popping. Spinners and progress still show.</summary>
    [ObservableProperty] bool _lessMotion;

    /// <summary>The accent in use: the one picked, or the theme's own.</summary>
    public string AccentColour => Accent.Colour.Length > 0 ? Accent.Colour : Theme.Accent;

    /// <summary>Text on the accent: dark on a light accent, white on a dark one.</summary>
    public string AccentText => Accent.Colour.Length == 0 ? Theme.AccentText : IsLight(Accent.Colour) ? "#14110D" : "#FFFFFF";

    /// <summary>The font in use: the one picked, or the theme's own.</summary>
    public string FontName => Font == ThemeFont ? Theme.Font : Font;

    partial void OnThemeChanged(AppTheme value) => _db.SetSetting(ThemeKey, value.Name);
    partial void OnAccentChanged(AccentChoice value) => _db.SetSetting(AccentKey, value.Colour);
    partial void OnFontChanged(string value) => _db.SetSetting(FontKey, value);
    partial void OnSizeChanged(int value)
    {
        _db.SetSetting(SizeKey, value.ToString());
        // A page whose own size the rest has now caught up with follows the rest again.
        var same = _pageSizes.Where(p => p.Value == value).Select(p => p.Key).ToList();
        foreach (var page in same) _pageSizes.Remove(page);
        if (same.Count > 0) SavePageSizes();
    }
    partial void OnLessMotionChanged(bool value) => _db.SetSetting(MotionKey, value ? "1" : "0");

    [RelayCommand]
    void Reset()
    {
        Theme = Themes[0];
        Accent = Accents[0];
        Font = ThemeFont;
        Size = 100;
        LessMotion = false;
        SameSizeEverywhere();
    }

    /// <summary>Whether a colour is light enough that white text on it is hard to read.</summary>
    public static bool IsLight(string hex)
    {
        var rgb = Convert.ToInt32(hex.TrimStart('#')[^6..], 16);
        double r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
        return 0.299 * r + 0.587 * g + 0.114 * b > 150;
    }
}
