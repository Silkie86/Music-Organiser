using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// One look for the whole app: its colours (as #RRGGBB) and fonts. <see cref="AccentText"/> is the text on accent-coloured
/// buttons, dark on a light accent. <see cref="Font"/> names one of <see cref="AppearanceViewModel.Fonts"/>.
/// </summary>
public sealed record AppTheme(string Name, string Description, string Background, string Sidebar, string Surface, string SurfaceRaised,
                              string Stroke, string Hover, string Text, string Muted, string Faint, string Accent, string AccentText, string Font);

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

    /// <summary>The fonts built into the app. "Theme's font" follows the theme.</summary>
    public const string ThemeFont = "Theme's font";
    public const string PlexFont = "IBM Plex";
    public const string SpaceFont = "Space (retro)";
    public const string InterFont = "Inter";
    public static IReadOnlyList<string> Fonts { get; } = [ThemeFont, PlexFont, SpaceFont, InterFont];

    public static IReadOnlyList<AppTheme> Themes { get; } =
    [
        new("Studio", "Dark grey with a violet accent: the app's own look.",
            "#101114", "#15161A", "#1A1B20", "#212228", "#2A2C33", "#24252C", "#ECEDF0", "#9A9DA7", "#676A74", "#8B7CF6", "#FFFFFF", PlexFont),
        new("Retro", "Warm brown and cream with amber, like old studio gear. Typewriter titles.",
            "#17130F", "#1E1914", "#241E18", "#2D261E", "#3B3227", "#332A21", "#F3E7D0", "#B9A98D", "#7F725D", "#F2A03D", "#1A1208", SpaceFont),
        new("Midnight", "Deep blue-black with a cool blue accent, easy on the eyes at night.",
            "#0B0F1C", "#0F1424", "#131A2D", "#1A223A", "#252F4A", "#1C2541", "#E6ECF8", "#93A0BF", "#5E6A88", "#4FB3FF", "#06121F", PlexFont),
        new("Graphite", "Neutral greys with a mint accent: no colour cast at all.",
            "#121212", "#171717", "#1D1D1D", "#252525", "#323232", "#2A2A2A", "#F0F0F0", "#A3A3A3", "#6E6E6E", "#3DDC97", "#06180F", PlexFont),
    ];

    public static IReadOnlyList<AccentChoice> Accents { get; } =
    [
        new("Theme's own", ""), new("Violet", "#8B7CF6"), new("Blue", "#4FB3FF"), new("Mint", "#3DDC97"),
        new("Amber", "#F2A03D"), new("Coral", "#F2766B"), new("Pink", "#F28CB1"),
    ];

    /// <summary>How big everything is, in percent of normal.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [90, 100, 110, 125];

    readonly LibraryDb _db;

    public AppearanceViewModel(LibraryDb db)
    {
        _db = db;
        _theme = Themes.FirstOrDefault(t => t.Name == db.GetSetting(ThemeKey)) ?? Themes[0];
        _accent = Accents.FirstOrDefault(a => a.Colour == db.GetSetting(AccentKey)) ?? Accents[0];
        _font = db.GetSetting(FontKey) is { } f && Fonts.Contains(f) ? f : ThemeFont;
        _size = int.TryParse(db.GetSetting(SizeKey), out var s) && Sizes.Contains(s) ? s : 100;
        _lessMotion = db.GetSetting(MotionKey) == "1";
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
    partial void OnSizeChanged(int value) => _db.SetSetting(SizeKey, value.ToString());
    partial void OnLessMotionChanged(bool value) => _db.SetSetting(MotionKey, value ? "1" : "0");

    [RelayCommand]
    void Reset()
    {
        Theme = Themes[0];
        Accent = Accents[0];
        Font = ThemeFont;
        Size = 100;
        LessMotion = false;
    }

    /// <summary>Whether a colour is light enough that white text on it is hard to read.</summary>
    public static bool IsLight(string hex)
    {
        var rgb = Convert.ToInt32(hex.TrimStart('#')[^6..], 16);
        double r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
        return 0.299 * r + 0.587 * g + 0.114 * b > 150;
    }
}
