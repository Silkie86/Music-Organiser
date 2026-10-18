using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using MusicOrganiser.Core.Sounds;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// The sound galaxy, drawn by hand: every sound a glowing star, bigger the more you like it, close to the sounds it
/// sounds like. Hold the left button and sweep over the stars to hear them; click one to pick it; drag with the middle
/// (or right) button to move about, scroll to zoom.
/// The picked sound shows lines to its six closest sound-alikes, and the trail of sounds you swept past is drawn as a
/// fading line so you can find your way back to a good one.
/// </summary>
/// <remarks>
/// Built for libraries of 300,000 sounds. Far out, the stars are painted into one picture (each star adds its colour
/// to the pixels it lands on, and crowds of them add up to a glowing cloud), so the cost hardly depends on how many
/// there are. Closer in, once a few thousand are in view, each star is drawn properly and the picture fades back to a
/// soft glow behind them. A grid over the map finds the stars near the mouse or in view without checking them all.
/// </remarks>
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
    public static readonly StyledProperty<ICommand?> SweepEndedCommandProperty = AvaloniaProperty.Register<GalaxyCanvas, ICommand?>(nameof(SweepEndedCommand));

    public IReadOnlyList<GalaxyStar>? Stars { get => GetValue(StarsProperty); set => SetValue(StarsProperty, value); }
    public IReadOnlyList<GalaxyRegion>? Regions { get => GetValue(RegionsProperty); set => SetValue(RegionsProperty, value); }
    public IReadOnlyList<Sound>? Trail { get => GetValue(TrailProperty); set => SetValue(TrailProperty, value); }
    public IReadOnlyList<Sound>? Constellation { get => GetValue(ConstellationProperty); set => SetValue(ConstellationProperty, value); }
    public Sound? Selected { get => GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public ICommand? SweepCommand { get => GetValue(SweepCommandProperty); set => SetValue(SweepCommandProperty, value); }
    public ICommand? PickCommand { get => GetValue(PickCommandProperty); set => SetValue(PickCommandProperty, value); }
    /// <summary>The left button was let go after sweeping: the preview stops.</summary>
    public ICommand? SweepEndedCommand { get => GetValue(SweepEndedCommandProperty); set => SetValue(SweepEndedCommandProperty, value); }

    static GalaxyCanvas() => AffectsRender<GalaxyCanvas>(StarsProperty, RegionsProperty, TrailProperty, ConstellationProperty, SelectedProperty);

    // The camera: the map point in the middle of the view, and how far in (1 = the whole galaxy fits). The t-values
    // are where it's gliding to; with an anchor, the map point under the mouse stays put while it glides.
    double _cx = 0.5, _cy = 0.5, _zoom = 1;
    double _tx = 0.5, _ty = 0.5, _tz = 1;
    (Point Screen, double X, double Y)? _anchor;
    double _maxZoom = 60;
    DateTime _lastFrame = DateTime.UtcNow;

    Point? _press;
    // Left held: sweeping (each star the pointer reaches plays). Middle or right held: dragging the view about.
    bool _sweeping, _swept, _panning, _miniDrag, _gliding;
    Point _last;
    GalaxyStar? _hover;
    Dictionary<long, GalaxyStar> _byId = [];
    readonly List<(GalaxyStar Star, DateTime Start)> _pulses = [];
    DateTime? _intro;
    readonly DispatcherTimer _frame, _sweepTimer;
    // Stars the sweep has passed and not played yet, oldest first, and when the last one started.
    readonly List<GalaxyStar> _sweepQueue = [];
    DateTime _sweptAt;
    readonly Dictionary<uint, (IBrush Core, IBrush Glow, IBrush Dim)> _brushes = [];
    static readonly Typeface Body = new("avares://MusicOrganiser.Avalonia/Assets/Fonts#IBM Plex Sans");
    static readonly Typeface Bold = new("avares://MusicOrganiser.Avalonia/Assets/Fonts#IBM Plex Sans", FontStyle.Normal, FontWeight.SemiBold);

    // Names you can click to fly in, from the last frame drawn: where the name is, and the part of the map it names.
    readonly List<(Rect Box, Rect Area)> _labels = [];
    int _hoverLabel = -1;

    public GalaxyCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        _frame = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Animate());
        _sweepTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Input, (_, _) => NextSweep());
    }

    // GalaxyGpu.Changed is static: held only while on screen, or every Sound Lab page ever shown (and its star
    // buffers) would stay in memory.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        GalaxyGpu.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        GalaxyGpu.Changed -= InvalidateVisual;
        _frame.Stop();
        _sweepTimer.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StarsProperty)
        {
            var old = change.OldValue as IReadOnlyList<GalaxyStar>;
            Index();
            _byId = new Dictionary<long, GalaxyStar>(_all.Length);
            foreach (var s in _all) _byId[s.Sound.Id] = s;
            if (_hover is not null) _hover = _byId.GetValueOrDefault(_hover.Sound.Id);
            // The first time the stars arrive they drift in from the middle.
            if (old is not { Count: > 0 } && _all.Length > 0 && Appearance.Motion)
            {
                _intro = DateTime.UtcNow;
                _frame.Start();
            }
        }
        else if (change.Property == SelectedProperty && Selected is { } s && _byId.TryGetValue(s.Id, out var star) && !OnScreen(star, -30))
            FlyTo(star.X, star.Y, _tz);   // picked from somewhere else (the trail, the list): bring it into view
    }

    // ---- the grid over the map ---------------------------------------------------------------------------

    const int Grid = 192;
    GalaxyStar[] _all = [];
    string?[] _packs = [];
    float[] _xs = [], _ys = [], _weight = [], _rgb = [];
    // Made afresh each time (never changed in place), because the graphics card's drawing reads them on another thread.
    int[] _cellStart = new int[Grid * Grid + 1];
    int[] _cellShown = new int[Grid * Grid];
    int[] _cellItems = [];
    int _version;

    static int Cell(double v) => Math.Clamp((int)Math.Floor(v * Grid), 0, Grid - 1);

    /// <summary>Sorts the stars into grid squares, so finding the ones in view or under the mouse is quick.</summary>
    void Index()
    {
        _all = Stars switch { null => [], GalaxyStar[] a => a, var list => list.ToArray() };
        var n = _all.Length;
        var cellOf = new int[n];
        _cellStart = new int[Grid * Grid + 1];
        _cellShown = new int[Grid * Grid];
        for (var i = 0; i < n; i++)
        {
            var c = Cell(_all[i].Y) * Grid + Cell(_all[i].X);
            cellOf[i] = c;
            _cellStart[c + 1]++;
            if (_all[i].Shown) _cellShown[c]++;
        }
        for (var c = 0; c < Grid * Grid; c++) _cellStart[c + 1] += _cellStart[c];
        var fill = (int[])_cellStart.Clone();
        _cellItems = new int[n];
        for (var i = 0; i < n; i++) _cellItems[fill[cellOf[i]]++] = i;
        _packs = new string?[n];
        // What painting needs, in flat lists: where, what colour, how bright (liked stars brighter, filtered-out ones
        // faint, and each with a little twinkle of its own so a crowd reads as stars, not paint).
        _xs = new float[n];
        _ys = new float[n];
        _weight = new float[n];
        _rgb = new float[3 * n];
        for (var i = 0; i < n; i++)
        {
            var s = _all[i];
            _xs[i] = s.X;
            _ys[i] = s.Y;
            var twinkle = 0.6f + 0.4f * ((uint)(s.Sound.Id * 2654435761L) >> 24) / 255f;
            _weight[i] = (s.Shown ? 0.5f + 0.9f * MathF.Sqrt((float)s.Sound.LikedShare) : 0.05f) * twinkle;
            _rgb[3 * i] = ((s.Colour >> 16) & 255) / 255f;
            _rgb[3 * i + 1] = ((s.Colour >> 8) & 255) / 255f;
            _rgb[3 * i + 2] = (s.Colour & 255) / 255f;
        }
        _version++;
        // Room to zoom until the stars of a 300,000-sound library sit well apart.
        _maxZoom = Math.Max(60, Math.Sqrt(n) * 0.8);
        _tz = Math.Min(_tz, _maxZoom);
    }

    string Pack(int i) => _packs[i] ??= _all[i].Sound.Folder;

    /// <summary>The grid squares under the view, with a margin in pixels.</summary>
    (int X0, int Y0, int X1, int Y1) InView(double margin)
    {
        var (ax, ay) = ToMap(new Point(-margin, -margin));
        var (bx, by) = ToMap(new Point(_w + margin, _h + margin));
        return (Cell(ax), Cell(ay), Cell(bx), Cell(by));
    }

    IEnumerable<int> StarsIn((int X0, int Y0, int X1, int Y1) r)
    {
        for (var y = r.Y0; y <= r.Y1; y++)
            for (var x = r.X0; x <= r.X1; x++)
            {
                var c = y * Grid + x;
                for (var k = _cellStart[c]; k < _cellStart[c + 1]; k++) yield return _cellItems[k];
            }
    }

    /// <summary>How many shown stars are on screen: counted exactly when that's cheap, else from the grid squares.</summary>
    int ShownIn((int X0, int Y0, int X1, int Y1) r)
    {
        var n = 0;
        for (var y = r.Y0; y <= r.Y1; y++)
            for (var x = r.X0; x <= r.X1; x++) n += _cellShown[y * Grid + x];
        if (n > 20_000) return n;
        n = 0;
        foreach (var i in StarsIn(r))
            if (_all[i].Shown && OnScreen(_all[i], 0)) n++;
        return n;
    }

    // ---- camera -----------------------------------------------------------------------------------------

    // A little room kept at the top and bottom when the whole galaxy is showing. The star count and colour key sit in
    // the corners, which a round galaxy leaves empty, so it can use nearly the full height.
    const double TopRoom = 24, BottomRoom = 24;
    // The view's size, read once a frame (asking Avalonia for Bounds per star was most of a frame's time).
    double _w, _h;
    double Fitted => Math.Min(_w, Math.Max(40, _h - TopRoom - BottomRoom));
    double Scale => Fitted * _zoom;
    double MidY => (_h + TopRoom - BottomRoom) / 2;
    internal Point ToScreen(double x, double y) => new((x - _cx) * Scale + _w / 2, (y - _cy) * Scale + MidY);
    (double X, double Y) ToMap(Point p) => ((p.X - _w / 2) / Scale + _cx, (p.Y - MidY) / Scale + _cy);

    bool OnScreen(GalaxyStar s, double margin)
    {
        var p = ToScreen(s.X, s.Y);
        return p.X > -margin && p.Y > -margin && p.X < _w + margin && p.Y < _h + margin;
    }

    /// <summary>Back to the whole galaxy.</summary>
    public void Fit() => FlyTo(0.5, 0.5, 1);

    /// <summary>Zooms about a point on screen (the middle when none), keeping that point still.</summary>
    public void ZoomBy(double factor, Point? about = null)
    {
        var at = about ?? new Point(_w / 2, MidY);
        var (mx, my) = ToMap(at);
        _tz = Math.Clamp(_tz * factor, 0.6, _maxZoom);
        _anchor = (at, mx, my);
        Glide();
    }

    /// <summary>Glides the view to a map point at a zoom: how clicking a name flies you into its cloud.</summary>
    public void FlyTo(double x, double y, double zoom)
    {
        _tx = x;
        _ty = y;
        _tz = Math.Clamp(zoom, 0.6, _maxZoom);
        _anchor = null;
        Glide();
    }

    /// <summary>Fits a part of the map (a region, a pack) in the view, with some room round it.</summary>
    void FlyInto(Rect area)
    {
        var size = Math.Max(Math.Max(area.Width, area.Height), 0.004);
        FlyTo(area.Center.X, area.Center.Y, Math.Max(_tz * 1.5, Math.Min(0.7 / size, _maxZoom)));
    }

    void Glide()
    {
        if (!Appearance.Motion) { Settle(1); return; }
        _lastFrame = DateTime.UtcNow;
        _frame.Start();
    }

    /// <summary>Moves the camera a share of the way to where it's going; true once it's there.</summary>
    bool Settle(double k)
    {
        _zoom = Math.Exp(Math.Log(_zoom) + (Math.Log(_tz) - Math.Log(_zoom)) * k);
        if (_anchor is { } a)
        {
            // Keep the map point under the mouse where it is.
            _cx = _tx = a.X - (a.Screen.X - _w / 2) / Scale;
            _cy = _ty = a.Y - (a.Screen.Y - MidY) / Scale;
        }
        else
        {
            _cx += (_tx - _cx) * k;
            _cy += (_ty - _cy) * k;
        }
        var done = Math.Abs(Math.Log(_tz / _zoom)) < 0.002 && Math.Abs(_tx - _cx) * Scale < 0.3 && Math.Abs(_ty - _cy) * Scale < 0.3;
        _gliding = !done;
        if (done)
        {
            _zoom = _tz;
            if (_anchor is null) { _cx = _tx; _cy = _ty; }
            _anchor = null;
        }
        InvalidateVisual();
        return done;
    }

    // ---- input ------------------------------------------------------------------------------------------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ZoomBy(Math.Pow(1.25, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        _press = _last = e.GetPosition(this);
        e.Handled = true;
        if (props.IsMiddleButtonPressed || props.IsRightButtonPressed)
        {
            // Middle (or right) drag moves the view about.
            _panning = true;
            _sweeping = false;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(this);
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        _panning = _swept = false;
        if (MiniMapRect is { } mini && mini.Inflate(new Thickness(0, 24, 0, 0)).Contains(_last))
        {
            // Click (or drag) on the mini-map: go there.
            _miniDrag = true;
            var (x, y) = FromMini(_last, mini);
            FlyTo(x, y, _tz);
            return;
        }
        if (LabelAt(_last) is { } label)
        {
            FlyInto(_labels[label].Area);
            _press = null;
            return;
        }
        if (e.ClickCount == 2 && HitTest(_last) is null)
        {
            ZoomBy(2.5, _last);
            return;
        }
        // Held down, the left button sweeps: the star under it plays now, and each one it reaches after that.
        _sweeping = true;
        _sweepQueue.Clear();
        e.Pointer.Capture(this);
        if (HitTest(_last) is { } star)
        {
            _hover = star;
            Sweep(star);
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_miniDrag && MiniMapRect is { } mini)
        {
            var (x, y) = FromMini(p, mini);
            _anchor = null;
            _cx = _tx = x;
            _cy = _ty = y;
            InvalidateVisual();
            return;
        }
        if (_panning)
        {
            _anchor = null;
            _cx -= (p.X - _last.X) / Scale;
            _cy -= (p.Y - _last.Y) / Scale;
            _tx = _cx;
            _ty = _cy;
            _last = p;
            InvalidateVisual();
            return;
        }
        if (_sweeping && _press is { } start && Distance(p, start) > 4) _swept = true;
        if (_sweeping)
        {
            // Every star along the way since the last move, not just where the mouse landed: a quick sweep moves
            // many pixels between two moves, and the stars in between are the ones you meant to hear.
            var from = _last;
            _last = p;
            var passed = HitPath(from, p, _hover);
            if (passed.Count == 0) return;
            _hover = passed[^1];
            foreach (var star in passed) QueueSweep(star);
            InvalidateVisual();
            return;
        }
        var label = LabelAt(p) ?? -1;
        if (label != _hoverLabel)
        {
            _hoverLabel = label;
            Cursor = new Cursor(label >= 0 ? StandardCursorType.Hand : StandardCursorType.Cross);
            InvalidateVisual();
        }
        var hit = label >= 0 ? null : HitTest(p);
        if (hit == _hover) return;
        // Only while the left button is held does a star play; hovering just shows its card.
        _hover = hit;
        InvalidateVisual();
    }

    /// <summary>How long each swept sound plays before the next one takes over, so each is heard.</summary>
    static readonly TimeSpan SweepGap = TimeSpan.FromMilliseconds(110);
    /// <summary>
    /// How many passed stars wait their turn; past that the oldest are dropped, so the sound keeps up with the mouse.
    /// One: what plays next is always the star the mouse reached last (two let it trail a star behind).
    /// </summary>
    const int SweepBacklog = 1;

    /// <summary>
    /// A star the sweep reached: plays now if the last one has had its moment, otherwise waits its turn. Without the
    /// wait, a sweep across a crowded part changes star every move and each preview is cut off before it's heard.
    /// </summary>
    void QueueSweep(GalaxyStar star)
    {
        if (_sweepQueue.Count > 0 && _sweepQueue[^1] == star) return;
        if (_sweepQueue.Count == 0 && DateTime.UtcNow - _sweptAt >= SweepGap)
        {
            Sweep(star);
            return;
        }
        _sweepQueue.Add(star);
        if (_sweepQueue.Count > SweepBacklog) _sweepQueue.RemoveAt(0);
        _sweepTimer.Start();
    }

    internal void NextSweep()
    {
        if (_sweepQueue.Count == 0 || !_sweeping) { _sweepQueue.Clear(); _sweepTimer.Stop(); return; }
        if (DateTime.UtcNow - _sweptAt < SweepGap) return;
        var star = _sweepQueue[0];
        _sweepQueue.RemoveAt(0);
        Sweep(star);
    }

    void Sweep(GalaxyStar star)
    {
        _sweptAt = DateTime.UtcNow;
        if (SweepCommand?.CanExecute(star.Sound) == true) SweepCommand.Execute(star.Sound);
        Pulse(star);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var moved = _panning || _miniDrag;
        var sweeping = _sweeping;
        var pressed = _press is not null;
        _press = null;
        _panning = _miniDrag = _sweeping = false;
        _sweepQueue.Clear();
        e.Pointer.Capture(null);
        if (moved) InvalidateVisual();   // paint the view properly where the drag ended
        Cursor = new Cursor(_hoverLabel >= 0 ? StandardCursorType.Hand : StandardCursorType.Cross);
        if (moved || !pressed || e.InitialPressMouseButton != MouseButton.Left) return;
        if (sweeping && _swept)
        {
            // Let go after sweeping: the sound stops with the mouse.
            if (SweepEndedCommand?.CanExecute(null) == true) SweepEndedCommand.Execute(null);
            return;
        }
        // A click without sweeping off: picks the star, and it counts as playing it on purpose.
        if (HitTest(e.GetPosition(this)) is { } star && PickCommand?.CanExecute(star.Sound) == true)
            PickCommand.Execute(star.Sound);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // The window lost the mouse mid-sweep (a dialog, Alt+Tab): nothing keeps playing on its own.
        if (_sweeping && _swept && SweepEndedCommand?.CanExecute(null) == true) SweepEndedCommand.Execute(null);
        _press = null;
        _panning = _miniDrag = _sweeping = false;
        _sweepQueue.Clear();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hover = null;
        _hoverLabel = -1;
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
        if (e.Key is Key.OemPlus or Key.Add) { ZoomBy(1.6); e.Handled = true; return; }
        if (e.Key is Key.OemMinus or Key.Subtract) { ZoomBy(1 / 1.6); e.Handled = true; return; }
        if (dir is not { } d || _all.Length == 0) return;
        // Arrow keys hop to the nearest star in that direction (within a 45° cone) and preview it.
        var from = Selected is { } sel && _byId.TryGetValue(sel.Id, out var cur) ? cur : null;
        GalaxyStar? next;
        if (from is null) next = _all.Where(s => s.Shown).MinBy(s => Math.Abs(s.X - _cx) + Math.Abs(s.Y - _cy));
        else
            next = _all.Where(s => s.Shown && s != from)
                .Select(s => (s, dx: s.X - from.X, dy: s.Y - from.Y))
                .Where(t => t.dx * d.X + t.dy * d.Y > Math.Abs(t.dx * d.Y - t.dy * d.X))
                .MinBy(t => Math.Sqrt(t.dx * t.dx + t.dy * t.dy) * (1 + 0.6 * Math.Abs(t.dx * d.Y - t.dy * d.X) / Math.Max(1e-6, Math.Sqrt(t.dx * t.dx + t.dy * t.dy))))
                .s;
        e.Handled = true;
        if (next is null) return;
        Selected = next.Sound;
        if (!OnScreen(next, -40)) FlyTo(next.X, next.Y, _tz);
        Sweep(next);
        InvalidateVisual();
    }

    GalaxyStar? HitTest(Point p)
    {
        if (_all.Length == 0) return null;
        GalaxyStar? best = null;
        var bestD = 14.0;   // pixels: generous, so sweeping doesn't need aim
        var (mx, my) = ToMap(p);
        var r = bestD / Scale;
        foreach (var i in StarsIn((Cell(mx - r), Cell(my - r), Cell(mx + r), Cell(my + r))))
        {
            var s = _all[i];
            if (!s.Shown) continue;
            var d = Distance(ToScreen(s.X, s.Y), p);
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    /// <summary>
    /// The stars the pointer passed going from <paramref name="a"/> to <paramref name="b"/>, in the order it reached
    /// them, leaving out <paramref name="last"/> (the one it was already on): a hit test every few pixels of the way.
    /// </summary>
    List<GalaxyStar> HitPath(Point a, Point b, GalaxyStar? last)
    {
        var passed = new List<GalaxyStar>();
        var steps = Math.Clamp((int)Math.Ceiling(Distance(a, b) / 6), 1, 200);
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            if (HitTest(new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t)) is not { } star || star == last) continue;
            passed.Add(star);
            last = star;
        }
        return passed;
    }

    int? LabelAt(Point p)
    {
        for (var i = 0; i < _labels.Count; i++)
            if (_labels[i].Box.Inflate(4).Contains(p)) return i;
        return null;
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
        var dt = Math.Clamp((now - _lastFrame).TotalSeconds, 0.001, 0.1);
        _lastFrame = now;
        _pulses.RemoveAll(p => (now - p.Start).TotalMilliseconds > 700);
        if (_intro is { } t && (now - t).TotalMilliseconds > 1100) _intro = null;
        // The camera eases in: quick at first, gentle as it arrives.
        var moving = !Settle(1 - Math.Exp(-dt / 0.085));
        if (_pulses.Count == 0 && _intro is null && !moving) _frame.Stop();
    }

    // ---- painting many stars at once ---------------------------------------------------------------------

    WriteableBitmap? _sky, _mini;
    object? _skyKey;
    double _skyCx, _skyCy, _paintMs;
    int _miniVersion = -1;
    float[] _acc = [], _glow = [], _blur = [];
    int[] _pixels = [];

    /// <summary>
    /// Paints stars into a picture: each adds its colour (brighter if liked) to a small spot, and to a blurred copy
    /// a quarter the size that becomes the glow around crowded places. Brightness is then squeezed so a crowd of a
    /// thousand stars is vivid but never a white blob, and it keeps its colour.
    /// </summary>
    /// <remarks>
    /// Stars are placed at x * scale + ox, y * scale + oy, unless <paramref name="warp"/> moves them (the intro).
    /// </remarks>
    void Paint(WriteableBitmap bmp, (int X0, int Y0, int X1, int Y1) cells, double scale, double ox, double oy,
               Func<GalaxyStar, Point>? warp, float core, float glow)
    {
        var pw = bmp.PixelSize.Width;
        var ph = bmp.PixelSize.Height;
        var gw = pw / 4 + 2;
        var gh = ph / 4 + 2;
        if (_acc.Length < pw * ph * 3) _acc = new float[pw * ph * 3];
        if (_glow.Length < gw * gh * 3) { _glow = new float[gw * gh * 3]; _blur = new float[gw * gh * 3]; }
        if (_pixels.Length < pw * ph) _pixels = new int[pw * ph];
        var acc = _acc;
        var gl = _glow;
        Array.Clear(acc, 0, pw * ph * 3);
        Array.Clear(gl, 0, gw * gh * 3);
        float[] xs = _xs, ys = _ys, weight = _weight, rgb = _rgb;
        int[] items = _cellItems, starts = _cellStart;

        for (var cy = cells.Y0; cy <= cells.Y1; cy++)
        for (var c = cy * Grid + cells.X0; c <= cy * Grid + cells.X1; c++)
        for (var k = starts[c]; k < starts[c + 1]; k++)
        {
            var i = items[k];
            double sx, sy;
            if (warp is null) { sx = xs[i] * scale + ox; sy = ys[i] * scale + oy; }
            else { var p = warp(_all[i]); sx = p.X; sy = p.Y; }
            if (sx < -1 || sy < -1 || sx > pw || sy > ph) continue;
            float r = rgb[3 * i], g = rgb[3 * i + 1], b = rgb[3 * i + 2];
            var w = weight[i];
            var ix = (int)(sx + 0.5);
            var iy = (int)(sy + 0.5);
            if (core > 0)
                for (var dy = -1; dy <= 1; dy++)
                {
                    var y = iy + dy;
                    if (y < 0 || y >= ph) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var x = ix + dx;
                        if (x < 0 || x >= pw) continue;
                        var kk = w * (dx == 0 ? 1f : 0.45f) * (dy == 0 ? 1f : 0.45f);
                        var o = (y * pw + x) * 3;
                        acc[o] += r * kk;
                        acc[o + 1] += g * kk;
                        acc[o + 2] += b * kk;
                    }
                }
            var gx = Math.Clamp((int)(sx / 4) + 1, 0, gw - 1);
            var gy = Math.Clamp((int)(sy / 4) + 1, 0, gh - 1);
            var go = (gy * gw + gx) * 3;
            gl[go] += r * w;
            gl[go + 1] += g * w;
            gl[go + 2] += b * w;
        }
        if (glow > 0)
            for (var pass = 0; pass < 3; pass++)   // three box blurs make a soft round glow
            {
                Blur(gl, _blur, gw, gh, 1, gw, 2);   // across
                Blur(_blur, gl, gh, gw, gw, 1, 2);   // down
            }

        var px = _pixels;
        var glowK = glow / 16f;
        // How bright: set from how crowded the picture is, so a packed galaxy keeps its light and dark parts
        // instead of glowing flat, and a sparse one still shines.
        double sum = 0;
        var lit = 0;
        for (var o = 0; o < pw * ph * 3; o += 3 * 7)
        {
            var l = Math.Max(acc[o], Math.Max(acc[o + 1], acc[o + 2])) * core;
            if (l > 0.004) { sum += l; lit++; }
        }
        var gain = lit == 0 ? 1.7f : (float)Math.Clamp(0.9 / (sum / lit), 0.35, 1.7);
        Parallel.For(0, ph, y =>
        {
            var fy = y / 4f + 0.5f;
            var y0 = Math.Min((int)fy, gh - 2);
            var ty = fy - y0;
            for (var x = 0; x < pw; x++)
            {
                var o = (y * pw + x) * 3;
                float vr = acc[o] * core, vg = acc[o + 1] * core, vb = acc[o + 2] * core;
                if (glowK > 0)
                {
                    var fx = x / 4f + 0.5f;
                    var x0 = Math.Min((int)fx, gw - 2);
                    var tx = fx - x0;
                    int a = (y0 * gw + x0) * 3, bI = a + 3, cI = a + gw * 3, dI = cI + 3;
                    float w00 = (1 - tx) * (1 - ty) * glowK, w10 = tx * (1 - ty) * glowK, w01 = (1 - tx) * ty * glowK, w11 = tx * ty * glowK;
                    vr += gl[a] * w00 + gl[bI] * w10 + gl[cI] * w01 + gl[dI] * w11;
                    vg += gl[a + 1] * w00 + gl[bI + 1] * w10 + gl[cI + 1] * w01 + gl[dI + 1] * w11;
                    vb += gl[a + 2] * w00 + gl[bI + 2] * w10 + gl[cI + 2] * w01 + gl[dI + 2] * w11;
                }
                var l = MathF.Max(vr, MathF.Max(vg, vb));
                if (l < 0.004f) { px[y * pw + x] = 0; continue; }
                var alpha = 1 - MathF.Exp(-l * gain);
                var sc = alpha / l * 255f;
                px[y * pw + x] = ((int)(alpha * 255) << 24) | ((int)(vr * sc) << 16) | ((int)(vg * sc) << 8) | (int)(vb * sc);
            }
        });
        using var fb = bmp.Lock();
        for (var y = 0; y < ph; y++) Marshal.Copy(px, y * pw, fb.Address + y * fb.RowBytes, pw);
    }

    /// <summary>A box blur along one direction of an interleaved RGB buffer (lines × length, with strides).</summary>
    static void Blur(float[] src, float[] dst, int length, int lines, int step, int lineStep, int radius)
    {
        Parallel.For(0, lines, line =>
        {
            var start = line * lineStep;
            var inv = 1f / (2 * radius + 1);
            for (var ch = 0; ch < 3; ch++)
            {
                float sum = 0;
                for (var k = -radius; k <= radius; k++) sum += src[(start + Math.Clamp(k, 0, length - 1) * step) * 3 + ch];
                for (var i = 0; i < length; i++)
                {
                    dst[(start + i * step) * 3 + ch] = sum * inv;
                    sum += src[(start + Math.Min(i + radius + 1, length - 1) * step) * 3 + ch]
                         - src[(start + Math.Max(i - radius, 0) * step) * 3 + ch];
                }
            }
        });
    }

    static WriteableBitmap Bitmap(WriteableBitmap? old, int w, int h) =>
        old is not null && old.PixelSize.Width == w && old.PixelSize.Height == h ? old
            : new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    // ---- the mini-map --------------------------------------------------------------------------------------

    const double MiniSize = 132;

    /// <summary>Where the mini-map sits (bottom right, above the hint), or null while the whole galaxy is in view.</summary>
    Rect? MiniMapRect => _zoom > 1.25 && _all.Length > 0 && _w > 420 && _h > 320
        ? new Rect(_w - MiniSize - 14, _h - MiniSize - 36, MiniSize, MiniSize) : null;   // a header sits above it

    static (double X, double Y) FromMini(Point p, Rect mini) =>
        (Math.Clamp((p.X - mini.X) / mini.Width, 0, 1), Math.Clamp((p.Y - mini.Y) / mini.Height, 0, 1));

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

    static double Smooth(double a, double b, double v)
    {
        var t = Math.Clamp((v - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    // Below this many stars in view each is drawn properly; above the upper one they're only painted.
    const int Crisp = 1500, Painted = 5000;

    /// <summary>The parts of the view the page's own panels cover (the star count, the zoom buttons, the mini-map), kept free of names.</summary>
    List<Rect> Covered()
    {
        var covered = new List<Rect> { new(0, 0, 380, 60), new(_w - 60, 0, 60, 190) };
        if (MiniMapRect is { } mini) covered.Add(new Rect(mini.X - 4, mini.Y - 28, mini.Width + 8, mini.Height + 32));
        return covered;
    }

    public override void Render(DrawingContext ctx)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        _w = Bounds.Width;
        _h = Bounds.Height;
        var w = _w;
        var h = _h;
        _labels.Clear();
        if (w < 4 || h < 4) return;
        var bg = Res("AppBackgroundBrush", Color.Parse("#121212"));
        var raised = Res("SurfaceRaisedBrush", Color.Parse("#252525"));
        var accent = Res("AccentBrush", Color.Parse("#3DDC97"));
        var text = Res("TextBrush", Colors.White);
        var muted = Res("MutedBrush", Colors.Gray);
        var surface = Res("SurfaceBrush", Color.Parse("#1D1D1D"));
        var stroke = Res("StrokeBrush", Color.Parse("#323232"));

        // Deep space: a soft glow in the middle, a hint of the accent, and dust that drifts slower than the stars
        // (and spreads a little as you zoom, so flying in feels like flying).
        ctx.FillRectangle(new ImmutableSolidColorBrush(bg), new Rect(0, 0, w, h));
        ctx.FillRectangle(new ImmutableRadialGradientBrush(
            [new ImmutableGradientStop(0, Blend(raised, accent, 0.06)), new ImmutableGradientStop(1, bg)], radius: 0.7), new Rect(0, 0, w, h));
        var dust = new ImmutableSolidColorBrush(Color.FromArgb(28, text.R, text.G, text.B));
        var rng = new Random(7);
        var spread = Math.Log(Math.Max(_zoom, 0.1)) * 0.08;
        for (var i = 0; i < 220; i++)
        {
            double x = rng.NextDouble(), y = rng.NextDouble();
            var sx = ((x - _cx * 0.25 + (x - 0.5) * spread) * w * 1.3) % w;
            var sy = ((y - _cy * 0.25 + (y - 0.5) * spread) * h * 1.3) % h;
            if (sx < 0) sx += w;
            if (sy < 0) sy += h;
            var r = rng.NextDouble() < 0.1 ? 1.2 : 0.7;
            ctx.DrawEllipse(dust, null, new Point(sx, sy), r, r);
        }

        if (_all.Length == 0) return;
        var stars = _all;
        var now = DateTime.UtcNow;
        var intro = _intro is { } t0 ? Math.Clamp((now - t0).TotalMilliseconds / 1000, 0, 1) : 1;
        var size = Math.Min(w, h);
        var grow = 0.75 + 0.25 * Math.Sqrt(Math.Min(_zoom, 30));

        Point Pos(GalaxyStar s)
        {
            var p = ToScreen(s.X, s.Y);
            if (intro >= 1) return p;
            // Ease out from the middle, the outer stars a little later.
            var mid = ToScreen(0.5, 0.5);
            var e = 1 - Math.Pow(1 - Math.Clamp(intro * 1.25 - Distance(p, mid) / size * 0.25, 0, 1), 3);
            return new Point(mid.X + (p.X - mid.X) * e, mid.Y + (p.Y - mid.Y) * e);
        }
        // Stars grow as you zoom in, but never so big that a crowd of them runs together.
        var room = 9.0;
        double Radius(GalaxyStar s) => Math.Clamp((1.9 + 3.4 * Math.Sqrt(s.Sound.LikedShare)) * grow, 1.6, room * (1 + s.Sound.LikedShare));

        // How many stars are in view decides how they're drawn: painted far out, crisp close up, a blend between.
        var view = intro < 1 ? (0, 0, Grid - 1, Grid - 1) : InView(20);
        var inView = ShownIn(view);
        var painted = (float)Smooth(Crisp, Painted, inView);
        var glow = (float)(0.35 + 0.65 * painted);
        room = Math.Clamp(Math.Sqrt(w * h / Math.Max(inView, 1)) * 0.22, 1.6, 9);

        // The painted sky, repainted only when the view or the stars change.
        // On the graphics card when there is one: redrawn when the view changes, and slid along while you drag a big one.
        if (GalaxyGpu.Available != false && intro >= 1)
        {
            var area = Math.Max(w * h, 1);
            var gain = (float)Math.Clamp(1.7 / Math.Max(1, inView * 9.0 / area), 0.35, 1.7);
            ctx.Custom(new GalaxyGpu.Op(new Rect(0, 0, w, h), new GalaxyGpu.Frame(
                _xs, _ys, _rgb, _weight, _cellStart, _cellItems, Grid, view, Scale, w / 2 - _cx * Scale, MidY - _cy * Scale,
                (int)Math.Ceiling(w), (int)Math.Ceiling(h), painted, glow, gain, _panning || _miniDrag || _gliding)));
        }
        else
        {
            // While you drag a big, slow-to-paint view, the last picture just slides along with you; it's repainted
            // when you let go.
            var key = (_cx, _cy, _zoom, w, h, intro, _version, painted);
            var shift = new Vector();
            var sliding = (_panning || _miniDrag) && _paintMs > 25 && _sky is not null && _skyKey is not null
                          && Equals(key with { Item1 = _skyCx, Item2 = _skyCy }, _skyKey);
            if (sliding) shift = new Vector((_skyCx - _cx) * Scale, (_skyCy - _cy) * Scale);
            else if (_sky is null || !Equals(key, _skyKey))
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                _sky = Bitmap(_sky, (int)Math.Ceiling(w), (int)Math.Ceiling(h));
                Paint(_sky, view, Scale, w / 2 - _cx * Scale, MidY - _cy * Scale, intro < 1 ? Pos : null, painted * (float)(0.4 + 0.6 * intro), glow);
                _skyKey = key;
                (_skyCx, _skyCy) = (_cx, _cy);
                _paintMs = started.Elapsed.TotalMilliseconds;
            }
            var skyRect = new Rect(0, 0, _sky!.PixelSize.Width, _sky.PixelSize.Height);
            ctx.DrawImage(_sky, skyRect, skyRect.Translate(shift));
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

        // Close up: every star in view drawn properly, a soft glow then a bright core; favourites get a little
        // sparkle; filtered-out stars stay as faint dust so the shape of your library never changes under you.
        if (painted < 0.99f && intro >= 1)
        {
            using var crisp = ctx.PushOpacity(1 - painted);
            var sparkle = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(170, text.R, text.G, text.B)), 1);
            var shown = new List<GalaxyStar>();
            foreach (var i in StarsIn(view))
            {
                var s = stars[i];
                if (!OnScreen(s, 20)) continue;
                if (!s.Shown) { ctx.DrawEllipse(BrushesFor(s.Colour).Dim, null, Pos(s), 1.3, 1.3); continue; }
                shown.Add(s);
            }
            foreach (var s in shown)
            {
                var p = Pos(s);
                var r = Radius(s);
                var brushes = BrushesFor(s.Colour);
                ctx.DrawEllipse(brushes.Glow, null, p, r * 2.8, r * 2.8);
                ctx.DrawEllipse(brushes.Core, null, p, r, r);
                if (s.Sound.Favourite)
                {
                    ctx.DrawLine(sparkle, new Point(p.X - r * 2.4, p.Y), new Point(p.X + r * 2.4, p.Y));
                    ctx.DrawLine(sparkle, new Point(p.X, p.Y - r * 2.4), new Point(p.X, p.Y + r * 2.4));
                }
            }
            // Few enough to read: names beside the stars, the most liked first, none on top of another.
            var onScreen = shown.Where(s => OnScreen(s, 0)).ToList();
            if (onScreen.Count <= 300)
            {
                var nameBrush = new ImmutableSolidColorBrush(Color.FromArgb((byte)(170 * (1 - Smooth(120, 300, onScreen.Count))), muted.R, muted.G, muted.B));
                var taken = Covered();
                foreach (var s in onScreen.OrderByDescending(s => s.Sound.LikedShare))
                {
                    if (s == _hover || s == selected) continue;
                    var ft = new FormattedText(s.Sound.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Body, 10.5, nameBrush);
                    var p = Pos(s);
                    var box = new Rect(p.X + Radius(s) + 5, p.Y - ft.Height / 2, ft.Width, ft.Height);
                    if (taken.Any(t => t.Intersects(box))) continue;   // a crowded name waits until you're closer
                    taken.Add(box);
                    ctx.DrawText(ft, box.TopLeft);
                }
            }
        }

        // Names over the clouds: the regions (kicks, pads…) far out, fading as you fly in, where the packs take over.
        var regionFade = 1 - Smooth(1.8, 3.2, _zoom);
        if (Regions is { Count: > 0 } regions && regionFade > 0.02)
        {
            var taken = Covered();
            // Only as many names as the galaxy's size has room for, so they label the clouds rather than cover them.
            var names = (int)Math.Clamp(Scale * 0.86 / 55, 4, 16);
            foreach (var r in regions.Take(names))   // biggest first, so a smaller neighbour is the one that moves
                Label(ctx, taken, r.Name, r.Count, r.Colour, ToScreen(r.X, r.Y), regionFade * intro, bg,
                      new Rect(r.Left, r.Y, r.Right - r.Left, r.Bottom - r.Y), big: true);
        }
        else if (_zoom > 2.2 && inView < 100_000)
            PackLabels(ctx, view, Smooth(2.2, 3.2, _zoom), bg);

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
            ctx.DrawEllipse(BrushesFor(selected.Colour).Core, null, p, r, r);
            ctx.DrawEllipse(null, new ImmutablePen(new ImmutableSolidColorBrush(accent), 2), p, r + 5, r + 5);
            ctx.DrawEllipse(null, new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(70, accent.R, accent.G, accent.B)), 6), p, r + 9, r + 9);
            if (selected != _hover) Card(ctx, selected, p, r, surface, stroke, text, muted, accent);
        }
        if (_hover is { } hov)
        {
            var p = Pos(hov);
            var r = Radius(hov);
            ctx.DrawEllipse(BrushesFor(hov.Colour).Core, null, p, r, r);
            ctx.DrawEllipse(null, new ImmutablePen(new ImmutableSolidColorBrush(text), 1.5), p, r + 4, r + 4);
            Card(ctx, hov, p, r, surface, stroke, text, muted, accent);
        }

        MiniMap(ctx, inView, surface, stroke, text, muted, accent);
        GalaxyTiming.Frame(timer.Elapsed.TotalMilliseconds, () => $"{_all.Length:N0} stars, zoom ×{_zoom:0.#}, {inView:N0} in view");
        if (Environment.GetEnvironmentVariable("MO_GALAXY_TIMING") is not null)
            Console.Error.WriteLine($"galaxy frame {timer.Elapsed.TotalMilliseconds:0.0} ms, zoom {_zoom:0.0}, in view {inView}, painted {painted:0.00}");
    }

    /// <summary>
    /// A faint, wide name over a cloud of stars, kept clear of the others (stepped up or down a line, or left out).
    /// It can be clicked to fly into its cloud, and lights up under the mouse to say so.
    /// </summary>
    void Label(DrawingContext ctx, List<Rect> taken, string name, int count, uint colour, Point at, double fade, Color bg, Rect area, bool big)
    {
        var c = Color.FromUInt32(colour);
        var hovered = _labels.Count == _hoverLabel;
        var label = string.Join(' ', name.ToUpperInvariant().ToCharArray());
        var alpha = (byte)Math.Clamp((hovered ? 255 : 200) * fade, 0, 255);
        var ft = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold,
            big ? Math.Clamp(10 + Math.Log(count) * 1.6, 11, 19) : Math.Clamp(9 + Math.Log(count), 10, 14),
            new ImmutableSolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)));
        var box = new Rect(at.X - ft.Width / 2, at.Y - ft.Height - (big ? 14 : 8), ft.Width, ft.Height);   // just above the cloud
        var fits = false;
        foreach (var dy in new[] { 0.0, -ft.Height - 2, ft.Height + 2, -2 * ft.Height - 4 })
            if (!taken.Any(t => t.Intersects(box.Translate(new Vector(0, dy)).Inflate(new Thickness(12, 3)))))
            {
                box = box.Translate(new Vector(0, dy));
                fits = true;
                break;
            }
        if (!fits || box.Right < 0 || box.Bottom < 0 || box.X > _w || box.Y > _h) return;
        taken.Add(box);
        // A soft backing so the name reads over a bright cloud; it lights up in the cloud's colour under the mouse.
        ctx.DrawRectangle(new ImmutableSolidColorBrush(hovered ? WithAlpha(Blend(bg, c, 0.22), (byte)(220 * fade)) : WithAlpha(bg, (byte)(70 * fade))),
                          null, box.Inflate(new Thickness(8, 3)), 6, 6);
        ctx.DrawText(ft, box.TopLeft);
        if (fade > 0.5) _labels.Add((box, area));
    }

    /// <summary>
    /// Flying in, the names of the packs in view: the view is cut into a few squares, each takes the pack most of its
    /// stars come from (when it's at least 45 % of them), and each pack is named once, where it's biggest.
    /// </summary>
    void PackLabels(DrawingContext ctx, (int X0, int Y0, int X1, int Y1) view, double fade, Color bg)
    {
        // Counting the packs goes through every star in view, up to 100,000 of them, so it's kept between frames:
        // worked out again when the stars change or the view settles, and every so often while it moves (the names are
        // placed on the map, so they stay in the right spot meanwhile). Hovering over a star doesn't count them again.
        var key = (view, _cx, _cy, _zoom, _w, _h);
        if (_packNames is null || _packNamesVersion != _version
            || (!Equals(key, _packNamesKey) && (!(_panning || _miniDrag || _frame.IsEnabled) || (DateTime.UtcNow - _packNamesAt).TotalMilliseconds > 120)))
        {
            _packNames = CountPacks(view);
            _packNamesKey = key;
            _packNamesVersion = _version;
            _packNamesAt = DateTime.UtcNow;
        }
        var taken = Covered();
        foreach (var (name, b) in _packNames)
            Label(ctx, taken, name, b.N, StarColours.Pack(name), ToScreen(b.X / b.N, b.Top), fade, bg,
                  new Rect(b.L, b.Top, b.R - b.L, b.B - b.Top), big: false);
    }

    List<(string Name, (int N, double X, float Top, float L, float R, float B) Box)>? _packNames;
    object? _packNamesKey;
    int _packNamesVersion = -1;
    DateTime _packNamesAt;

    List<(string Name, (int N, double X, float Top, float L, float R, float B) Box)> CountPacks((int X0, int Y0, int X1, int Y1) view)
    {
        const int cols = 5, rows = 4;
        var (ax, ay) = ToMap(new Point(0, 0));
        var (bx, by) = ToMap(new Point(_w, _h));
        var cells = new Dictionary<string, (int N, double X, float Top, float L, float R, float B)>?[cols * rows];
        var totals = new int[cols * rows];
        foreach (var i in StarsIn(view))
        {
            var s = _all[i];
            if (!s.Shown || s.X < ax || s.X > bx || s.Y < ay || s.Y > by) continue;
            var c = Math.Min(rows - 1, (int)((s.Y - ay) / (by - ay) * rows)) * cols + Math.Min(cols - 1, (int)((s.X - ax) / (bx - ax) * cols));
            var d = cells[c] ??= new();
            var name = Pack(i);
            d[name] = d.TryGetValue(name, out var e)
                ? (e.N + 1, e.X + s.X, Math.Min(e.Top, s.Y), Math.Min(e.L, s.X), Math.Max(e.R, s.X), Math.Max(e.B, s.Y))
                : (1, s.X, s.Y, s.X, s.X, s.Y);
            totals[c]++;
        }
        var best = new Dictionary<string, (int N, double X, float Top, float L, float R, float B)>();
        for (var c = 0; c < cells.Length; c++)
        {
            if (cells[c] is not { } d || totals[c] < 6) continue;
            var (name, top) = d.MaxBy(kv => kv.Value.N);
            if (top.N < totals[c] * 0.45 || name.Length == 0) continue;
            if (!best.TryGetValue(name, out var had) || had.N < top.N) best[name] = top;
        }
        return best.OrderByDescending(kv => kv.Value.N).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>
    /// The whole galaxy in miniature with a box for what you're looking at, once you've zoomed in: click or drag on
    /// it to go somewhere else. Above it, how far in you are and how many sounds are in view.
    /// </summary>
    void MiniMap(DrawingContext ctx, int inView, Color surface, Color stroke, Color text, Color muted, Color accent)
    {
        if (MiniMapRect is not { } mini) return;
        using var fade = ctx.PushOpacity(Smooth(1.25, 1.6, _zoom));
        if (_miniVersion != _version || _mini is null)
        {
            _mini = Bitmap(_mini, (int)MiniSize, (int)MiniSize);
            Paint(_mini, (0, 0, Grid - 1, Grid - 1), MiniSize, 0, 0, null, 1.3f, 0.15f);
            _miniVersion = _version;
        }
        // One panel: how far in and how many in view along the top, the map below.
        const double head = 24;
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(235, surface.R, surface.G, surface.B)),
                          new ImmutablePen(new ImmutableSolidColorBrush(stroke), 1), new Rect(mini.X - 1, mini.Y - head, mini.Width + 2, mini.Height + head + 1), 8, 8);
        using (ctx.PushClip(new RoundedRect(mini, 0, 0, 7, 7)))
        {
            ctx.DrawImage(_mini, new Rect(0, 0, MiniSize, MiniSize), mini);
            var (ax, ay) = ToMap(new Point(0, 0));
            var (bx, by) = ToMap(new Point(_w, _h));
            var box = new Rect(mini.X + ax * mini.Width, mini.Y + ay * mini.Height, (bx - ax) * mini.Width, (by - ay) * mini.Height);
            if (box.Width < 6) box = new Rect(box.Center.X - 3, box.Center.Y - 3, 6, 6);   // a speck when deep in
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(36, accent.R, accent.G, accent.B)),
                              new ImmutablePen(new ImmutableSolidColorBrush(accent), 1.4), box, 2, 2);
        }
        var zoomText = new FormattedText($"×{_zoom:0.#}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 11.5,
                                         new ImmutableSolidColorBrush(text));
        var countText = new FormattedText($"  {inView:N0} in view", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Body, 11,
                                          new ImmutableSolidColorBrush(muted));
        var y = mini.Y - head + (head - zoomText.Height) / 2;
        ctx.DrawText(zoomText, new Point(mini.X + 8, y));
        ctx.DrawText(countText, new Point(mini.X + 8 + zoomText.Width, y + 0.5));
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
        if (x + cw > _w - 6) x = p.X - r - 14 - cw;
        var y = Math.Clamp(p.Y - ch / 2, 6, Math.Max(6, _h - ch - 6));
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

    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
