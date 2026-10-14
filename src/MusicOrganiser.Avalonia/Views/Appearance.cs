using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// Puts Settings › Appearance on screen, and again whenever it changes. The colours are App.axaml's brushes, recoloured
/// in place: every control holding one of them changes with it, with nothing to reload.
/// </summary>
public static class Appearance
{
    const string Fonts = "avares://MusicOrganiser.Avalonia/Assets/Fonts";

    /// <summary>Starts following <paramref name="settings"/>; <paramref name="scaler"/> is what grows or shrinks with Size.</summary>
    public static void Follow(AppearanceViewModel settings, LayoutTransformControl? scaler)
    {
        Apply(settings, scaler);
        settings.PropertyChanged += (_, _) => Apply(settings, scaler);
    }

    static void Apply(AppearanceViewModel a, LayoutTransformControl? scaler)
    {
        if (Application.Current is not { } app) return;
        var t = a.Theme;
        var accent = Color.Parse(a.AccentColour);
        Recolour(app, "AppBackgroundBrush", t.Background);
        Recolour(app, "SidebarBrush", t.Sidebar);
        Recolour(app, "SurfaceBrush", t.Surface);
        Recolour(app, "SurfaceRaisedBrush", t.SurfaceRaised);
        Recolour(app, "StrokeBrush", t.Stroke);
        Recolour(app, "HoverBrush", t.Hover);
        Recolour(app, "TextBrush", t.Text);
        Recolour(app, "MutedBrush", t.Muted);
        Recolour(app, "FaintBrush", t.Faint);
        Recolour(app, "AccentBrush", accent);
        Recolour(app, "AccentFaintBrush", WithAlpha(accent, 0x1A));
        Recolour(app, "AccentSoftBrush", WithAlpha(accent, 0x33));
        Recolour(app, "DataGridColumnHeaderForegroundBrush", t.Muted);
        Recolour(app, "DataGridColumnHeaderHoveredBackgroundBrush", t.Hover);
        Recolour(app, "DataGridColumnHeaderPressedBackgroundBrush", t.Stroke);
        foreach (var key in new[] { "DataGridRowSelectedBackgroundBrush", "DataGridRowSelectedHoveredBackgroundBrush",
                                    "DataGridRowSelectedUnfocusedBackgroundBrush", "DataGridRowSelectedHoveredUnfocusedBackgroundBrush" })
            Recolour(app, key, accent);

        // Fluent's own accent (selected items, ticks, accent buttons) and the text on accent buttons.
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent &&
            fluent.Palettes.TryGetValue(ThemeVariant.Dark, out var palette))
        {
            palette.Accent = accent;
            palette.RegionColor = Color.Parse(t.Background);
        }
        Recolour(app, "OnAccentBrush", a.AccentText);

        // Fonts: words, titles, and numbers that should line up.
        var (text, title, numbers) = a.FontName switch
        {
            AppearanceViewModel.SpaceFont => ($"{Fonts}#Space Grotesk", $"{Fonts}#Space Mono", $"{Fonts}#Space Mono"),
            AppearanceViewModel.InterFont => ("fonts:Inter#Inter", "fonts:Inter#Inter", $"{Fonts}#IBM Plex Mono"),
            _ => ($"{Fonts}#IBM Plex Sans", $"{Fonts}#IBM Plex Sans", $"{Fonts}#IBM Plex Mono"),
        };
        app.Resources["ContentControlThemeFontFamily"] = new FontFamily(text);
        app.Resources["TitleFont"] = new FontFamily(title);
        app.Resources["NumberFont"] = new FontFamily(numbers);
        foreach (var w in Windows(app)) w.FontFamily = new FontFamily(text);

        if (scaler is not null)
            scaler.LayoutTransform = a.Size == 100 ? null : new ScaleTransform(a.Size / 100.0, a.Size / 100.0);

        SetMotion(app, !a.LessMotion);
    }

    static IEnumerable<Window> Windows(Application app) =>
        app.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d ? d.Windows : [];

    static StyleInclude? _motion;

    /// <summary>Motion.axaml in or out of the app's styles.</summary>
    static void SetMotion(Application app, bool on)
    {
        _motion ??= app.Styles.OfType<StyleInclude>().FirstOrDefault(s => s.Source?.OriginalString.EndsWith("Motion.axaml") == true);
        if (_motion is null) return;
        var present = app.Styles.Contains(_motion);
        if (on && !present) app.Styles.Add(_motion);
        else if (!on && present) app.Styles.Remove(_motion);
    }

    static void Recolour(Application app, string key, string hex) => Recolour(app, key, Color.Parse(hex));

    static void Recolour(Application app, string key, Color colour)
    {
        if (app.Resources.TryGetResource(key, null, out var value) && value is SolidColorBrush brush) brush.Color = colour;
    }

    static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
}
