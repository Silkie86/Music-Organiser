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
        // A row under the pointer gets half the theme's hover tint: a full one flashed bright as the pointer crossed a list.
        app.Resources["DataGridRowHoveredBackgroundColor"] = Mix(Color.Parse(t.Background), Color.Parse(t.Hover), 0.5);
        foreach (var key in new[] { "DataGridRowSelectedBackgroundBrush", "DataGridRowSelectedHoveredBackgroundBrush",
                                    "DataGridRowSelectedUnfocusedBackgroundBrush", "DataGridRowSelectedHoveredUnfocusedBackgroundBrush" })
            Recolour(app, key, accent);

        // Light or dark for Fluent's own controls (boxes, menus, switches), with the theme's accent (selected items, ticks).
        var variant = t.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent && fluent.Palettes.TryGetValue(variant, out var palette))
        {
            palette.Accent = accent;
            palette.RegionColor = Color.Parse(t.Background);
        }
        app.RequestedThemeVariant = variant;

        // Corners: small, like the studio look's (Studio.axaml), or square for the hardware-style themes.
        var (s, m, l, pill, control, overlay) = t.Square ? (1.0, 2.0, 3.0, 3.0, 1.0, 2.0) : (3.0, 4.0, 6.0, 6.0, 3.0, 4.0);
        app.Resources["RadiusS"] = new CornerRadius(s);
        app.Resources["RadiusM"] = new CornerRadius(m);
        app.Resources["RadiusL"] = new CornerRadius(l);
        app.Resources["RadiusPill"] = new CornerRadius(pill);
        app.Resources["RadiusStart"] = new CornerRadius(s, 0, 0, s);
        app.Resources["RadiusEnd"] = new CornerRadius(0, s, s, 0);
        app.Resources["RadiusInner"] = new CornerRadius(s - 1);
        app.Resources["ControlCornerRadius"] = new CornerRadius(control);
        app.Resources["OverlayCornerRadius"] = new CornerRadius(overlay);
        Recolour(app, "OnAccentBrush", a.AccentText);

        // Fonts: words, titles, and numbers that should line up.
        var (text, title, numbers) = a.FontName switch
        {
            AppearanceViewModel.SpaceFont => ($"{Fonts}#Space Grotesk", $"{Fonts}#Space Mono", $"{Fonts}#Space Mono"),
            AppearanceViewModel.InterFont => ("fonts:Inter#Inter", "fonts:Inter#Inter", $"{Fonts}#IBM Plex Mono"),
            AppearanceViewModel.MonoFont => ($"{Fonts}#IBM Plex Mono", $"{Fonts}#IBM Plex Mono", $"{Fonts}#IBM Plex Mono"),
            _ => ($"{Fonts}#IBM Plex Sans", $"{Fonts}#IBM Plex Sans", $"{Fonts}#IBM Plex Mono"),
        };
        app.Resources["ContentControlThemeFontFamily"] = new FontFamily(text);
        app.Resources["TitleFont"] = new FontFamily(title);
        app.Resources["NumberFont"] = new FontFamily(numbers);
        foreach (var w in Windows(app)) w.FontFamily = new FontFamily(text);

        if (scaler is not null)
            scaler.LayoutTransform = a.Size == 100 ? null : new ScaleTransform(a.Size / 100.0, a.Size / 100.0);

        SetMotion(app, !a.LessMotion);
        Shade(app, t);
    }

    /// <summary>
    /// The studio look's shading (Studio.axaml), from the theme's own colours (Solomon, 2026-10-21: shading, grooves and
    /// joined buttons "is what makes it look more professional"): faces lit from the top, wells pressed in, and grooves
    /// of a dark line over a light one. Light themes get softer shadows and brighter highlights.
    /// </summary>
    static void Shade(Application app, AppTheme t)
    {
        Color bg = Color.Parse(t.Background), surface = Color.Parse(t.Surface), raised = Color.Parse(t.SurfaceRaised);
        var light = t.Light;
        var black = Colors.Black;
        var white = Colors.White;
        static LinearGradientBrush Down(Color top, Color bottom) => new()
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        };
        static LinearGradientBrush Groove(Color dark, Color lit, bool across) => new()
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = across ? new RelativePoint(1, 0, RelativeUnit.Relative) : new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(dark, 0), new GradientStop(dark, 0.5), new GradientStop(lit, 0.5), new GradientStop(lit, 1) },
        };
        // Flat faces, only a breath of shading (2026-10-22: the lit ones looked "too bubbly").
        app.Resources["StudioFaceBrush"] = Down(Mix(raised, white, light ? 0.5 : 0.03), Mix(raised, black, light ? 0.02 : 0.03));
        app.Resources["StudioFaceHoverBrush"] = Down(Mix(raised, white, light ? 0.65 : 0.08), Mix(raised, white, light ? 0.4 : 0.04));
        // The tile that's on inside a sunk strip.
        Recolour(app, "StudioTileBrush", Mix(raised, white, light ? 0.6 : 0.05));
        app.Resources["StudioPressedBrush"] = Down(Mix(bg, black, light ? 0.1 : 0.3), Mix(bg, black, light ? 0.04 : 0.12));
        var edge = Mix(bg, black, light ? 0.35 : 0.5);
        Recolour(app, "StudioWellBrush", Mix(bg, black, light ? 0.05 : 0.3));
        Recolour(app, "StudioEdgeBrush", edge);
        var lit = Mix(surface, white, light ? 0.7 : 0.08);
        app.Resources["StudioGrooveBrush"] = Groove(edge, lit, across: false);
        app.Resources["StudioGrooveUpBrush"] = Groove(edge, lit, across: true);
        app.Resources["StudioRaise"] = BoxShadows.Parse(light ? "inset 0 1 0 0 #B3FFFFFF" : "inset 0 1 0 0 #24FFFFFF");
        app.Resources["StudioSink"] = BoxShadows.Parse(light ? "inset 0 1 2 0 #26000000" : "inset 0 1 3 0 #73000000");
    }

    static IEnumerable<Window> Windows(Application app) =>
        app.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d ? d.Windows : [];

    static StyleInclude? _motion;

    /// <summary>False when Settings › Less motion is on: hand-drawn animation (the galaxy's) skips itself too.</summary>
    public static bool Motion { get; private set; } = true;

    /// <summary>Motion.axaml in or out of the app's styles.</summary>
    static void SetMotion(Application app, bool on)
    {
        Motion = on;
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

    static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
}
