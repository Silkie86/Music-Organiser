using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// The sound galaxy, drawn by hand: every sound a glowing star, bigger the more you like it, close to the sounds it
/// sounds like. Sweep the mouse over the stars to hear them; click one to pick it; drag to move about, scroll to zoom.
/// The picked sound shows lines to its six closest sound-alikes, and the trail of sounds you swept past is drawn as a
/// fading line so you can find your way back to a good one.
/// </summary>
public sealed class GalaxyCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<GalaxyStar>?> StarsProperty =
        AvaloniaProperty.Register<GalaxyCanvas, IReadOnlyList<GalaxyStar>?>(nameof(Stars));
    public static readonly StyledProperty<IReadOnlyList<GalaxyRegion>?> RegionsProperty =
        AvaloniaProperty.Register<GalaxyCanvas, IReadOnlyList<GalaxyRegion>?>(nameof(Regions));
    public static readonly StyledProperty<IReadOnlyList<Sound>?> TrailProperty =
        AvaloniaProperty.Register<GalaxyCanvas, IReadOnlyList<Sound>?>(nameof(Trail));
    public static readonly StyledProperty<IReadOnlyList<Sound>?> ConstellationProperty =
        AvaloniaProperty.Register<GalaxyCanvas, IReadOnlyList<Sound>?>(nameof(Constellation));
    public static readonly StyledProperty<Sound?> SelectedProperty =
        AvaloniaProperty.Register<GalaxyCanvas, Sound?>(nameof(Selected), defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<ICommand?> SweepCommandProperty = AvaloniaProperty.Register<GalaxyCanvas, ICommand?>(nameof(SweepCommand));
    public static readonly StyledProperty<ICommand?> PickCommandProperty = AvaloniaProperty.Register<GalaxyCanvas, ICommand?>(nameof(PickCommand));

    public IReadOnlyList<GalaxyStar>? Stars { get => GetValue(StarsProperty); set => SetValue(StarsProperty, value); }
    public IReadOnlyList<GalaxyRegion>? Regions { get => GetValue(RegionsProperty); set => SetValue(RegionsProperty, value); }
    public IReadOnlyList<Sound>? Trail { get => GetValue(TrailProperty); set => SetValue(TrailProperty, value); }
    public IReadOnlyList<Sound>? Constellation { get => GetValue(ConstellationProperty); set => SetValue(ConstellationProperty, value); }
    public Sound? Selected { get => GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public ICommand? SweepCommand { get => GetValue(SweepCommandProperty); set => SetValue(SweepCommandProperty, value); }
    public ICommand? PickCommand { get => GetValue(PickCommandProperty); set => SetValue(PickCommandProperty, value); }

    static GalaxyCanvas() => AffectsRender<GalaxyCanvas>(StarsProperty, RegionsProperty, TrailProperty, ConstellationProperty, SelectedProperty);

    // The camera: the map point in the middle of the view, and how far in (1 = the whole galaxy fits).
    double _cx = 0.5, _cy = 0.5, _zoom = 1;
    Point? _press;
    bool _panning;
    Point _last;
    GalaxyStar? _hover;
    Dictionary<long, GalaxyStar> _byId = [];
    readonly List<(GalaxyStar Star, DateTime Start)> _pulses = [];
    DateTime? _intro;
    readonly DispatcherTimer _frame;
    readonly Dictionary<uint, (IBrush Core, IBrush Glow, IBrush Dim)> _brushes = [];
    static readonly Typeface Body = new("avares://MusicOrganiser.Avalonia/Assets/Fonts#IBM Plex Sans");
    static readonly Typeface Bold = new("avares://MusicOrganiser.Avalonia/Assets/Fonts#IBM Plex Sans", FontStyle.Normal, FontWeight.SemiBold);

    public GalaxyCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        _frame = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Animate());
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StarsProperty)
        {
            var old = change.OldValue as IReadOnlyList<GalaxyStar>;
            _byId = (Stars ?? []).ToDictionary(s => s.Sound.Id);
            if (_hover is not null) _hover = _byId.GetValueOrDefault(_hover.Sound.Id);
            // The first time the stars arrive they drift in from the middle.
            if (old is not { Count: > 0 } && Stars is { Count: > 0 } && Appearance.Motion)
            {
                _intro = DateTime.UtcNow;
                _frame.Start();
            }
        }
        else if (change.Property == SelectedProperty && Selected is { } s && _byId.TryGetValue(s.Id, out var star) && !OnScreen(star, 30))
        {
            _cx = star.X;   // picked from somewhere else (the trail, the list): bring it into view
            _cy = star.Y;
        }
    }

    // ---- camera -----------------------------------------------------------------------------------------

    // Room kept clear at the top (the star count) and bottom (the colour key) when the whole galaxy is showing.
    const double TopRoom = 64, BottomRoom = 56;
    double Scale => Math.Min(Bounds.Width, Math.Max(40, Bounds.Height - TopRoom - BottomRoom)) * _zoom;
    double MidY => (Bounds.Height + TopRoom - BottomRoom) / 2;
    Point ToScreen(double x, double y) => new((x - _cx) * Scale + Bounds.Width / 2, (y - _cy) * Scale + MidY);
    (double X, double Y) ToMap(Point p) => ((p.X - Bounds.Width / 2) / Scale + _cx, (p.Y - MidY) / Scale + _cy);

    bool OnScreen(GalaxyStar s, double margin)
    {
        var p = ToScreen(s.X, s.Y);
        return p.X > -margin && p.Y > -margin && p.X < Bounds.Width + margin && p.Y < Bounds.Height + margin;
    }

    /// <summary>Back to the whole galaxy.</summary>
    public void Fit()
    {
        _cx = _cy = 0.5;
        _zoom = 1;
        InvalidateVisual();
    }

    /// <summary>Zooms about a point on screen (the middle when none), keeping that point still.</summary>
    public void ZoomBy(double factor, Point? about = null)
    {
        var at = about ?? new Point(Bounds.Width / 2, MidY);
        var (mx, my) = ToMap(at);
        _zoom = Math.Clamp(_zoom * factor, 0.6, 60);
        var (nx, ny) = ToMap(at);
        _cx += mx - nx;
        _cy += my - ny;
        InvalidateVisual();
    }

    // ---- input ------------------------------------------------------------------------------------------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ZoomBy(Math.Pow(1.18, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _press = _last = e.GetPosition(this);
        _panning = false;
        if (e.ClickCount == 2 && HitTest(_last) is null) ZoomBy(2, _last);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_press is { } start)
        {
            if (!_panning && Distance(p, start) > 4)
            {
                _panning = true;
                Cursor = new Cursor(StandardCursorType.SizeAll);
            }
            if (_panning)
            {
                _cx -= (p.X - _last.X) / Scale;
                _cy -= (p.Y - _last.Y) / Scale;
                _last = p;
                InvalidateVisual();
                return;
            }
        }
        var hit = HitTest(p);
        if (hit == _hover) return;
        _hover = hit;
        if (hit is not null)
        {
            // Sweeping: each new star the pointer lands on plays (a preview) and joins the trail.
            if (SweepCommand?.CanExecute(hit.Sound) == true) SweepCommand.Execute(hit.Sound);
            Pulse(hit);
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var wasPanning = _panning;
        _press = null;
        _panning = false;
        Cursor = new Cursor(StandardCursorType.Cross);
        if (wasPanning) return;
        if (HitTest(e.GetPosition(this)) is { } star && PickCommand?.CanExecute(star.Sound) == true)
        {
            PickCommand.Execute(star.Sound);
            Pulse(star);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        (double X, double Y)? dir = e.Key switch
        {
            Key.Left => (-1, 0), Key.Right => (1, 0), Key.Up => (0, -1), Key.Down => (0, 1), _ => null,
        };
        if (e.Key is Key.Home or Key.F) { Fit(); e.Handled = true; return; }
        if (e.Key == Key.Space && Selected is { } picked)
        {
            // Space plays the picked star on purpose (it counts, like a click).
            if (PickCommand?.CanExecute(picked) == true) PickCommand.Execute(picked);
            if (_byId.GetValueOrDefault(picked.Id) is { } ps) Pulse(ps);
            e.Handled = true;
            return;
        }
        if (e.Key is Key.OemPlus or Key.Add) { ZoomBy(1.5); e.Handled = true; return; }
        if (e.Key is Key.OemMinus or Key.Subtract) { ZoomBy(1 / 1.5); e.Handled = true; return; }
        if (dir is not { } d || Stars is not { Count: > 0 } stars) return;
        // Arrow keys hop to the nearest star in that direction (within a 45° cone) and preview it.
        var from = Selected is { } sel && _byId.TryGetValue(sel.Id, out var cur) ? cur : null;
        GalaxyStar? next;
        if (from is null) next = stars.Where(s => s.Shown).MinBy(s => Math.Abs(s.X - _cx) + Math.Abs(s.Y - _cy));
        else
            next = stars.Where(s => s.Shown && s != from)
                .Select(s => (s, dx: s.X - from.X, dy: s.Y - from.Y))
                .Where(t => t.dx * d.X + t.dy * d.Y > Math.Abs(t.dx * d.Y - t.dy * d.X))
                .MinBy(t => Math.Sqrt(t.dx * t.dx + t.dy * t.dy) * (1 + 0.6 * Math.Abs(t.dx * d.Y - t.dy * d.X) / Math.Max(1e-6, Math.Sqrt(t.dx * t.dx + t.dy * t.dy))))
                .s;
        e.Handled = true;
        if (next is null) return;
        Selected = next.Sound;
        if (SweepCommand?.CanExecute(next.Sound) == true) SweepCommand.Execute(next.Sound);
        if (!OnScreen(next, -40)) { _cx = next.X; _cy = next.Y; }
        Pulse(next);
        InvalidateVisual();
    }

    GalaxyStar? HitTest(Point p)
    {
        if (Stars is null) return null;
        GalaxyStar? best = null;
        var bestD = 14.0;   // pixels: generous, so sweeping doesn't need aim
        foreach (var s in Stars)
        {
            if (!s.Shown) continue;
            var d = Distance(ToScreen(s.X, s.Y), p);
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ---- animation -------------------------------------------------------------------------------------

    void Pulse(GalaxyStar s)
    {
        if (!Appearance.Motion) return;
        _pulses.Add((s, DateTime.UtcNow));
        if (_pulses.Count > 12) _pulses.RemoveAt(0);
        _frame.Start();
    }

    void Animate()
    {
        var now = DateTime.UtcNow;
        _pulses.RemoveAll(p => (now - p.Start).TotalMilliseconds > 700);
        if (_intro is { } t && (now - t).TotalMilliseconds > 1100) _intro = null;
        if (_pulses.Count == 0 && _intro is null) _frame.Stop();
        InvalidateVisual();
    }

    // ---- drawing ---------------------------------------------------------------------------------------

    Color Res(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is ISolidColorBrush b ? b.Color : fallback;

    (IBrush Core, IBrush Glow, IBrush Dim) BrushesFor(uint colour)
    {
        if (_brushes.TryGetValue(colour, out var b)) return b;
        var c = Color.FromUInt32(colour);
        b = (new ImmutableSolidColorBrush(c),
             new ImmutableRadialGradientBrush([new ImmutableGradientStop(0, Color.FromArgb(110, c.R, c.G, c.B)),
                                               new ImmutableGradientStop(1, Color.FromArgb(0, c.R, c.G, c.B))], radius: 0.5),
             new ImmutableSolidColorBrush(Color.FromArgb(38, c.R, c.G, c.B)));
        _brushes[colour] = b;
        return b;
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 4 || h < 4) return;
        var bg = Res("AppBackgroundBrush", Color.Parse("#121212"));
        var raised = Res("SurfaceRaisedBrush", Color.Parse("#252525"));
        var accent = Res("AccentBrush", Color.Parse("#3DDC97"));
        var text = Res("TextBrush", Colors.White);
        var muted = Res("MutedBrush", Colors.Gray);
        var surface = Res("SurfaceBrush", Color.Parse("#1D1D1D"));
        var stroke = Res("StrokeBrush", Color.Parse("#323232"));

        // Deep space: a soft glow in the middle, a hint of the accent, and still dust that drifts slower than the stars.
        ctx.FillRectangle(new ImmutableSolidColorBrush(bg), new Rect(0, 0, w, h));
        ctx.FillRectangle(new ImmutableRadialGradientBrush(
            [new ImmutableGradientStop(0, Blend(raised, accent, 0.06)), new ImmutableGradientStop(1, bg)], radius: 0.7), new Rect(0, 0, w, h));
        var dust = new ImmutableSolidColorBrush(Color.FromArgb(28, text.R, text.G, text.B));
        var rng = new Random(7);
        for (var i = 0; i < 220; i++)
        {
            double x = rng.NextDouble(), y = rng.NextDouble();
            var sx = ((x - _cx * 0.25) * w * 1.3) % w;
            var sy = ((y - _cy * 0.25) * h * 1.3) % h;
            if (sx < 0) sx += w;
            if (sy < 0) sy += h;
            var r = rng.NextDouble() < 0.1 ? 1.2 : 0.7;
            ctx.DrawEllipse(dust, null, new Point(sx, sy), r, r);
        }

        if (Stars is not { Count: > 0 } stars) return;
        var now = DateTime.UtcNow;
        var intro = _intro is { } t0 ? Math.Clamp((now - t0).TotalMilliseconds / 1000, 0, 1) : 1;
        var size = Math.Min(w, h);
        var grow = 0.75 + 0.25 * Math.Sqrt(_zoom);

        Point Pos(GalaxyStar s)
        {
            var p = ToScreen(s.X, s.Y);
            if (intro >= 1) return p;
            // Ease out from the middle, the outer stars a little later.
            var mid = ToScreen(0.5, 0.5);
            var e = 1 - Math.Pow(1 - Math.Clamp(intro * 1.25 - Distance(p, mid) / size * 0.25, 0, 1), 3);
            return new Point(mid.X + (p.X - mid.X) * e, mid.Y + (p.Y - mid.Y) * e);
        }
        double Radius(GalaxyStar s) => Math.Clamp((1.9 + 3.4 * Math.Sqrt(s.Sound.LikedShare)) * grow, 1.6, 9);

        // Region names, faint and wide, behind the stars (until you're zoomed in enough to read star names instead).
        if (Regions is { Count: > 0 } regions && _zoom < 5)
        {
            var taken = new List<Rect>();
            foreach (var r in regions)   // biggest first, so a smaller neighbour is the one that moves
            {
                var c = Color.FromUInt32(r.Colour);
                var label = string.Join(' ', r.Name.ToUpperInvariant().ToCharArray());
                var ft = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold,
                    Math.Clamp(10 + Math.Log(r.Count) * 1.6, 11, 19), new ImmutableSolidColorBrush(Color.FromArgb((byte)(110 * intro), c.R, c.G, c.B)));
                var p = ToScreen(r.X, r.Y);
                var box = new Rect(p.X - ft.Width / 2, p.Y - ft.Height - 14, ft.Width, ft.Height);   // just above the cloud
                // Labels never sit on each other: step a crowded one up or down a line, or leave it out.
                var fits = false;
                foreach (var dy in new[] { 0.0, -ft.Height - 2, ft.Height + 2, -2 * ft.Height - 4 })
                    if (!taken.Any(t => t.Intersects(box.Translate(new Vector(0, dy)).Inflate(3))))
                    {
                        box = box.Translate(new Vector(0, dy));
                        fits = true;
                        break;
                    }
                if (!fits) continue;
                taken.Add(box);
                ctx.DrawText(ft, box.TopLeft);
            }
        }

        // Filtered-out stars stay as faint dust, so the shape of your library never changes under you.
        foreach (var s in stars)
        {
            if (s.Shown || !OnScreen(s, 4)) continue;
            ctx.DrawEllipse(BrushesFor(s.Colour).Dim, null, Pos(s), 1.3, 1.3);
        }

        // The trail: a line through the sounds you swept past, fading with age.
        if (Trail is { Count: > 1 } trail)
        {
            var points = trail.Select(t => _byId.GetValueOrDefault(t.Id)).OfType<GalaxyStar>().Select(Pos).ToList();
            for (var i = 0; i < points.Count - 1; i++)
            {
                var a = (byte)(200 * (1 - (double)i / points.Count) * intro);
                ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(a, text.R, text.G, text.B)), 1.2,
                    new ImmutableDashStyle([3, 3], 0), PenLineCap.Round), points[i], points[i + 1]);
            }
        }

        // The constellation: the picked sound's closest sound-alikes.
        var selected = Selected is { } sel ? _byId.GetValueOrDefault(sel.Id) : null;
        if (selected is not null && Constellation is { Count: > 0 } cons)
        {
            var pen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(120, accent.R, accent.G, accent.B)), 1.1);
            var from = Pos(selected);
            foreach (var c in cons)
                if (_byId.GetValueOrDefault(c.Id) is { } cs)
                {
                    var to = Pos(cs);
                    ctx.DrawLine(pen, from, to);
                    ctx.DrawEllipse(null, pen, to, Radius(cs) + 3, Radius(cs) + 3);
                }
        }

        // The stars: a soft glow, then a bright core; favourites get a little sparkle.
        var sparkle = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(170, text.R, text.G, text.B)), 1);
        var drawn = 0;
        foreach (var s in stars)
        {
            if (!s.Shown || !OnScreen(s, 20)) continue;
            var p = Pos(s);
            var r = Radius(s) * (0.4 + 0.6 * intro);
            var (core, glow, _) = BrushesFor(s.Colour);
            if (drawn++ < 6000) ctx.DrawEllipse(glow, null, p, r * 3.2, r * 3.2);
            ctx.DrawEllipse(core, null, p, r, r);
            if (s.Sound.Favourite)
            {
                ctx.DrawLine(sparkle, new Point(p.X - r * 2.4, p.Y), new Point(p.X + r * 2.4, p.Y));
                ctx.DrawLine(sparkle, new Point(p.X, p.Y - r * 2.4), new Point(p.X, p.Y + r * 2.4));
            }
        }

        // Zoomed in: names beside the stars.
        if (_zoom >= 5)
        {
            var nameBrush = new ImmutableSolidColorBrush(Color.FromArgb(170, muted.R, muted.G, muted.B));
            var n = 0;
            foreach (var s in stars)
            {
                if (!s.Shown || !OnScreen(s, 0) || s == _hover || s == selected) continue;
                if (++n > 250) break;
                var ft = new FormattedText(s.Sound.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Body, 10.5, nameBrush);
                var p = Pos(s);
                ctx.DrawText(ft, new Point(p.X + Radius(s) + 5, p.Y - ft.Height / 2));
            }
        }

        // Pulses: a ring spreading out from each star as it plays.
        foreach (var (s, start) in _pulses)
        {
            var k = Math.Clamp((now - start).TotalMilliseconds / 700, 0, 1);
            var c = Color.FromUInt32(s.Colour);
            var pen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb((byte)(200 * (1 - k)), c.R, c.G, c.B)), 2 * (1 - k) + 0.5);
            var rr = Radius(s) + 4 + 26 * (1 - Math.Pow(1 - k, 2));
            ctx.DrawEllipse(null, pen, Pos(s), rr, rr);
        }

        if (selected is not null)
        {
            var p = Pos(selected);
            var r = Radius(selected);
            ctx.DrawEllipse(null, new ImmutablePen(new ImmutableSolidColorBrush(accent), 2), p, r + 5, r + 5);
            ctx.DrawEllipse(null, new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(70, accent.R, accent.G, accent.B)), 6), p, r + 9, r + 9);
            if (selected != _hover) Card(ctx, selected, p, r, surface, stroke, text, muted, accent);
        }
        if (_hover is { } hov)
        {
            var p = Pos(hov);
            var r = Radius(hov);
            ctx.DrawEllipse(null, new ImmutablePen(new ImmutableSolidColorBrush(text), 1.5), p, r + 4, r + 4);
            Card(ctx, hov, p, r, surface, stroke, text, muted, accent);
        }
    }

    /// <summary>A small label card beside a star: its name, then what it is, its key, how much it's used and liked.</summary>
    void Card(DrawingContext ctx, GalaxyStar s, Point p, double r, Color surface, Color stroke, Color text, Color muted, Color accent)
    {
        var snd = s.Sound;
        var line2 = string.Join(" · ", new[]
        {
            snd.Type, snd.Key, snd.Bpm is { } b ? $"{b:0.#} BPM" : "", $"{snd.Seconds:0.0}s",
            snd.UsedIn > 0 ? $"in {snd.UsedIn} project{(snd.UsedIn == 1 ? "" : "s")}" : "never used",
        }.Where(x => x.Length > 0));
        var title = new FormattedText(snd.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 12.5, new ImmutableSolidColorBrush(text));
        var sub = new FormattedText(line2, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Body, 11, new ImmutableSolidColorBrush(muted));
        var cw = Math.Max(title.Width, sub.Width) + 26;
        var ch = title.Height + sub.Height + 20;
        var x = p.X + r + 14;
        if (x + cw > Bounds.Width - 6) x = p.X - r - 14 - cw;
        var y = Math.Clamp(p.Y - ch / 2, 6, Math.Max(6, Bounds.Height - ch - 6));
        var rect = new Rect(x, y, cw, ch);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(235, surface.R, surface.G, surface.B)),
                          new ImmutablePen(new ImmutableSolidColorBrush(stroke), 1), rect, 8, 8);
        var c = Color.FromUInt32(s.Colour);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(c), null, new Rect(x + 8, y + 9, 3, ch - 18), 1.5, 1.5);
        ctx.DrawText(title, new Point(x + 17, y + 8));
        ctx.DrawText(sub, new Point(x + 17, y + 10 + title.Height));
        // Its liked bar along the bottom edge.
        if (snd.LikedShare > 0)
            ctx.DrawRectangle(new ImmutableSolidColorBrush(accent), null, new Rect(x + 17, y + ch - 5, (cw - 34) * snd.LikedShare, 2), 1, 1);
    }

    static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
