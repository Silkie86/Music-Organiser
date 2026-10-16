using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MusicOrganiser.Core.Preview;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// A project drawn like a DAW's arrangement: track names down the left, bars along the top, and each clip in its place
/// with its notes, its audio's waveform or its automation curve inside. The whole song fits the width at first; the
/// mouse wheel scrolls, Ctrl + wheel zooms about the mouse, dragging moves around and a double-click fits it again.
/// Waveforms are read in the background: their clips shimmer until they arrive.
/// </summary>
public sealed class ArrangementView : Control
{
    public static readonly StyledProperty<Arrangement?> ArrangementProperty =
        AvaloniaProperty.Register<ArrangementView, Arrangement?>(nameof(Arrangement));

    public Arrangement? Arrangement { get => GetValue(ArrangementProperty); set => SetValue(ArrangementProperty, value); }

    /// <summary>The ruler: bar numbers, and a second row for markers when the song has any.</summary>
    double RulerHeight => Arrangement is { Markers.Count: > 0 } ? 42 : 24;
    const double ScrollbarSize = 5;

    double HeaderWidth => Bounds.Width < 520 ? 118 : 172;

    static double LaneHeight(PreviewLane lane) => lane.Kind switch
    {
        LaneKind.Group => 26,
        LaneKind.Automation => 32,
        _ => 46,
    };

    /// <summary>Pixels per beat; 0 fits the song to the width.</summary>
    double _zoom;
    /// <summary>The beat at the left edge of the timeline, and how far down the lanes are scrolled.</summary>
    double _left, _top;
    Point? _drag;
    (double Left, double Top) _dragFrom;
    Point? _hover;
    DateTime _shown;
    readonly Dictionary<string, AudioPeaks?> _peaks = new(StringComparer.OrdinalIgnoreCase);
    int _loading;
    DispatcherTimer? _timer;

    static ArrangementView() => AffectsRender<ArrangementView>(ArrangementProperty);

    public ArrangementView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ArrangementProperty) return;
        _zoom = 0;
        _left = _top = 0;
        _shown = DateTime.UtcNow;
        Animate();
        LoadWaveforms(Arrangement);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
    }

    /// <summary>Redraws every frame while something moves: the lanes coming in, or waveforms still loading.</summary>
    void Animate()
    {
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
        {
            if (Intro >= 1 && _loading == 0) _timer!.Stop();
            InvalidateVisual();
        });
        if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>0 to 1 as the lanes slide in after a new arrangement arrives.</summary>
    double Intro => Appearance.Motion ? Math.Clamp((DateTime.UtcNow - _shown).TotalMilliseconds / 450, 0, 1) : 1;

    void LoadWaveforms(Arrangement? a)
    {
        if (a is null) return;
        var files = a.Lanes.SelectMany(l => l.Clips).SelectMany(c => c.Audio).Where(p => p.Found).Select(p => p.Path)
                     .Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !_peaks.ContainsKey(p)).ToList();
        if (files.Count == 0) return;
        _loading += files.Count;
        Animate();
        _ = Task.Run(() =>
        {
            foreach (var f in files)
            {
                var peaks = AudioPeaks.For(f);
                Dispatcher.UIThread.Post(() =>
                {
                    _peaks[f] = peaks;
                    _loading--;
                    InvalidateVisual();
                });
            }
        });
    }

    // ---- Geometry ----------------------------------------------------------

    double Width0 => Math.Max(1, Bounds.Width - HeaderWidth - 8);
    double SongBeats => Math.Max(Arrangement?.BeatsPerBar ?? 4, (Arrangement?.Length ?? 0) * 1.02);
    double FitZoom => Width0 / SongBeats;
    double Zoom => _zoom > 0 ? _zoom : FitZoom;
    double ContentHeight => Arrangement?.Lanes.Sum(LaneHeight) ?? 0;

    double X(double beat) => HeaderWidth + (beat - _left) * Zoom;
    double Beat(double x) => _left + (x - HeaderWidth) / Zoom;

    void Clamp()
    {
        var visible = Width0 / Zoom;
        _left = Math.Clamp(_left, 0, Math.Max(0, SongBeats - visible));
        _top = Math.Clamp(_top, 0, Math.Max(0, ContentHeight - (Bounds.Height - RulerHeight)));
    }

    // ---- Input -------------------------------------------------------------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Arrangement is null) return;
        var at = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            var beat = Beat(Math.Max(at.X, HeaderWidth));
            _zoom = Math.Clamp(Zoom * Math.Pow(1.25, e.Delta.Y), FitZoom, 400);
            _left = beat - (Math.Max(at.X, HeaderWidth) - HeaderWidth) / _zoom;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
            _left -= (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * 60 / Zoom;
        else
            _top -= e.Delta.Y * 40;
        Clamp();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            _zoom = 0;
            _left = 0;
            InvalidateVisual();
            return;
        }
        _drag = e.GetPosition(this);
        _dragFrom = (_left, _top);
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = null;
        Cursor = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        _hover = at;
        if (_drag is { } from)
        {
            _left = _dragFrom.Left - (at.X - from.X) / Zoom;
            _top = _dragFrom.Top - (at.Y - from.Y);
            Clamp();
        }
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    // ---- Drawing -----------------------------------------------------------

    IBrush Res(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    Color ThemeColour(string key, Color fallback) => Res(key, new SolidColorBrush(fallback)) is ISolidColorBrush s ? s.Color : fallback;

    /// <summary>A clip's colour: its own, else its lane's, else one for what the lane holds that follows the theme.</summary>
    Color ColourOf(PreviewLane lane, PreviewClip? clip)
    {
        if (clip?.Colour is { } c) return Rgb(c);
        if (lane.Colour is { } l) return Rgb(l);
        return lane.Kind switch
        {
            LaneKind.Audio => ThemeColour("OkBrush", Color.FromRgb(0x4A, 0xCB, 0x8B)),
            LaneKind.Automation => ThemeColour("WarningBrush", Color.FromRgb(0xF2, 0xB3, 0x3D)),
            _ => ThemeColour("AccentBrush", Color.FromRgb(0x3D, 0xDC, 0x97)),
        };
    }

    static Color Rgb(int c) => Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);
    static Color Alpha(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    FormattedText Text(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(this.TryFindResource("ContentControlThemeFontFamily", out var f) && f is FontFamily ff ? ff : FontFamily.Default, FontStyle.Normal, weight),
            size, brush);

    /// <summary>Text cut to a width, with an ellipsis.</summary>
    FormattedText Fit(string s, double size, IBrush brush, double width, FontWeight weight = FontWeight.Normal)
    {
        var t = Text(s, size, brush, weight);
        if (t.Width <= width || s.Length == 0) return t;
        t.MaxTextWidth = Math.Max(1, width);
        t.MaxLineCount = 1;
        t.Trimming = TextTrimming.CharacterEllipsis;
        return t;
    }

    public override void Render(DrawingContext dc)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        dc.FillRectangle(Res("SurfaceBrush", Brushes.Black), new Rect(0, 0, w, h));
        if (Arrangement is not { } a || w < 60 || h < 40) return;
        Clamp();

        var text = Res("TextBrush", Brushes.White);
        var muted = Res("MutedBrush", Brushes.Gray);
        var faint = Res("FaintBrush", Brushes.DimGray);
        var stroke = Res("StrokeBrush", Brushes.DimGray);
        var raised = Res("SurfaceRaisedBrush", Brushes.Black);
        var intro = Intro;
        var ease = 1 - Math.Pow(1 - intro, 3);
        var hx = HeaderWidth;
        var bars = BarStep(a);

        // Bar lines behind everything.
        using (dc.PushClip(new Rect(hx, RulerHeight, w - hx, h - RulerHeight)))
            ForEachBar(a, bars, (bar, x) => dc.FillRectangle(bar % (bars * 4) == 0 ? stroke : Res("HoverBrush", stroke), new Rect(x, RulerHeight, 1, h)));

        // Lanes.
        var y = RulerHeight - _top;
        var hoverText = (string?)null;
        for (var i = 0; i < a.Lanes.Count; i++)
        {
            var lane = a.Lanes[i];
            var lh = LaneHeight(lane);
            if (y + lh >= RulerHeight && y <= h)
            {
                // Each lane slides in a little after the one above it.
                var delay = Math.Clamp(ease * 1.6 - i * 0.04, 0, 1);
                using (dc.PushOpacity(delay))
                {
                    var tip = DrawLane(dc, a, lane, y, lh, w, text, muted, faint, stroke, raised);
                    hoverText ??= tip;
                }
            }
            y += lh;
        }
        if (y < h) dc.FillRectangle(stroke, new Rect(0, y, w, 1));

        DrawRuler(dc, a, bars, w, text, muted, raised, stroke);
        dc.FillRectangle(stroke, new Rect(hx - 1, 0, 1, h));

        // Thin scroll bars when zoomed in or when the lanes don't fit.
        var visible = Width0 / Zoom;
        if (visible < SongBeats - 1e-6)
        {
            var bw = Width0 * visible / SongBeats;
            dc.DrawRectangle(faint, null, new RoundedRect(new Rect(hx + Width0 * _left / SongBeats, h - ScrollbarSize - 2, Math.Max(16, bw), ScrollbarSize), 2.5));
        }
        var lanesH = h - RulerHeight;
        if (ContentHeight > lanesH + 1)
        {
            var bh = lanesH * lanesH / ContentHeight;
            dc.DrawRectangle(faint, null, new RoundedRect(new Rect(w - ScrollbarSize - 2, RulerHeight + lanesH * _top / ContentHeight, ScrollbarSize, Math.Max(16, bh)), 2.5));
        }

        // What's under the mouse.
        if (_hover is { } p && _drag is null && p.X > hx)
        {
            dc.FillRectangle(muted, new Rect(p.X, RulerHeight, 1, h - RulerHeight));
            var beat = Beat(p.X);
            var bpb = Math.Max(1, a.BeatsPerBar);
            var label = $"Bar {(int)(beat / bpb) + 1}.{(int)(beat % bpb) + 1}" + (hoverText is null ? "" : "  ·  " + hoverText);
            var t = Fit(label, 11.5, text, Math.Max(80, w - hx - 20));
            var lx = Math.Clamp(p.X + 10, hx + 4, w - t.Width - 10);
            var ly = Math.Clamp(p.Y + 16, RulerHeight + 2, h - t.Height - 8);
            dc.DrawRectangle(Res("SidebarBrush", Brushes.Black), new Pen(stroke, 1), new RoundedRect(new Rect(lx - 6, ly - 3, t.Width + 12, t.Height + 6), 5));
            dc.DrawText(t, new Point(lx, ly));
        }
    }

    /// <summary>How many bars apart the ruler's numbers are, so they're at least 44 pixels apart.</summary>
    int BarStep(Arrangement a)
    {
        var barPx = Zoom * Math.Max(1, a.BeatsPerBar);
        var step = 1;
        while (step * barPx < 44 && step < 4096) step *= 2;
        return step;
    }

    void ForEachBar(Arrangement a, int step, Action<int, double> draw)
    {
        var bpb = Math.Max(1, a.BeatsPerBar);
        var first = (int)Math.Floor(_left / bpb / step) * step;
        for (var bar = Math.Max(0, first); ; bar += step)
        {
            var x = X(bar * (double)bpb);
            if (x > Bounds.Width) break;
            if (x >= HeaderWidth) draw(bar, x);
        }
    }

    void DrawRuler(DrawingContext dc, Arrangement a, int step, double w, IBrush text, IBrush muted, IBrush raised, IBrush stroke)
    {
        dc.FillRectangle(raised, new Rect(0, 0, w, RulerHeight));
        dc.FillRectangle(stroke, new Rect(0, RulerHeight - 1, w, 1));
        var head = Fit(a.View.ToUpperInvariant(), 10, muted, HeaderWidth - 20, FontWeight.SemiBold);
        dc.DrawText(head, new Point(12, (24 - head.Height) / 2));
        using (dc.PushClip(new Rect(HeaderWidth, 0, w - HeaderWidth, RulerHeight)))
        {
            ForEachBar(a, step, (bar, x) =>
            {
                dc.FillRectangle(muted, new Rect(x, 17, 1, 6));
                dc.DrawText(Text((bar + 1).ToString(CultureInfo.CurrentCulture), 10.5, muted), new Point(x + 4, 3));
            });
            // Markers on their own row, each label cut short where the next marker starts.
            var accent = Res("WarningBrush", Brushes.Orange);
            var ink = Res("InkBrush", Brushes.Black);
            var markers = a.Markers.OrderBy(m => m.Beat).ToList();
            for (var i = 0; i < markers.Count; i++)
            {
                var x = X(markers[i].Beat);
                var next = i + 1 < markers.Count ? X(markers[i + 1].Beat) : w;
                if (next < HeaderWidth || x > w) continue;
                var room = next - x - 3;
                dc.FillRectangle(accent, new Rect(x, 24, 1.5, Bounds.Height));
                if (room < 8) continue;
                var label = Fit(markers[i].Name.Length > 0 ? markers[i].Name : "Marker", 10, ink, Math.Max(1, room - 8), FontWeight.SemiBold);
                dc.DrawRectangle(accent, null, new RoundedRect(new Rect(x, 24, Math.Min(label.Width + 8, room), 15), 3));
                dc.DrawText(label, new Point(x + 4, 24 + (15 - label.Height) / 2));
            }
        }
    }

    /// <summary>Draws one lane: its name on the left, its clips on the timeline. Returns a line about the clip under the mouse.</summary>
    string? DrawLane(DrawingContext dc, Arrangement a, PreviewLane lane, double y, double lh, double w,
                     IBrush text, IBrush muted, IBrush faint, IBrush stroke, IBrush raised)
    {
        var hx = HeaderWidth;
        var colour = ColourOf(lane, null);
        dc.FillRectangle(stroke, new Rect(0, y + lh - 1, w, 1));
        if (lane.Kind == LaneKind.Group) dc.FillRectangle(new SolidColorBrush(Alpha(colour, 0.08)), new Rect(0, y, w, lh - 1));

        // The name, with a strip of the lane's colour like a DAW's track header.
        var indent = 10 + lane.Depth * 12;
        dc.DrawRectangle(new SolidColorBrush(colour), null, new RoundedRect(new Rect(indent - 6, y + 6, 3, lh - 13), 1.5));
        var nameSize = lane.Kind == LaneKind.Automation ? 11 : 12;
        var name = Fit(lane.Name, nameSize, lane.Muted ? muted : text, hx - indent - 10, lane.Kind == LaneKind.Group ? FontWeight.Bold : FontWeight.SemiBold);
        if (lane.Detail.Length > 0 && lh >= 40)
        {
            var detail = Fit(lane.Detail, 10.5, muted, hx - indent - 10);
            var top = y + (lh - name.Height - detail.Height) / 2;
            dc.DrawText(name, new Point(indent, top));
            dc.DrawText(detail, new Point(indent, top + name.Height));
        }
        else dc.DrawText(name, new Point(indent, y + (lh - name.Height) / 2));

        string? tip = null;
        using (dc.PushClip(new Rect(hx, Math.Max(y, RulerHeight), w - hx, lh)))
        {
            // Notes share one key range across the lane, so the same note sits at the same height in every clip.
            var keys = lane.Clips.SelectMany(c => c.Notes).Select(n => n.Key).ToList();
            var (lo, hi) = keys.Count > 0 ? (keys.Min(), keys.Max()) : (60, 60);
            foreach (var clip in lane.Clips)
            {
                var x0 = X(clip.Start);
                var x1 = X(clip.End);
                if (x1 < hx || x0 > w) continue;
                var r = new Rect(x0 + 0.5, y + 3, Math.Max(2, x1 - x0 - 1), lh - 7);
                var c = ColourOf(lane, clip);
                using (dc.PushOpacity(clip.Muted ? 0.35 : 1))
                    DrawClip(dc, a, lane, clip, r, c, lo, hi, text);
                if (_hover is { } p && r.Contains(p)) tip = Describe(lane, clip);
            }
        }
        return tip;
    }

    void DrawClip(DrawingContext dc, Arrangement a, PreviewLane lane, PreviewClip clip, Rect r, Color c, int lo, int hi, IBrush text)
    {
        var body = new RoundedRect(r, 3);
        dc.DrawRectangle(new SolidColorBrush(Alpha(c, 0.24)), new Pen(new SolidColorBrush(Alpha(c, 0.75)), 1), body);
        // A title bar with the clip's name, when there's room for it.
        var titleH = r.Height >= 30 && r.Width > 28 ? 13.0 : 0;
        if (titleH > 0)
        {
            using (dc.PushClip(body))
                dc.FillRectangle(new SolidColorBrush(Alpha(c, 0.55)), new Rect(r.X, r.Y, r.Width, titleH));
            if (clip.Name.Length > 0 && r.Width > 34)
                dc.DrawText(Fit(clip.Name, 9.5, text, r.Width - 8, FontWeight.SemiBold), new Point(r.X + 4, r.Y));
        }
        var inner = new Rect(r.X + 2, r.Y + titleH + 2, Math.Max(1, r.Width - 4), Math.Max(1, r.Height - titleH - 4));
        var fill = new SolidColorBrush(Lighten(c, 0.25));
        using (dc.PushClip(inner))
        {
            if (clip.Notes.Count > 0) DrawNotes(dc, clip, inner, lo, hi, fill);
            foreach (var part in clip.Audio) DrawAudio(dc, part, clip, inner, fill, c);
            if (clip.Points.Count > 1) DrawCurve(dc, clip, inner, fill);
        }
        if (clip.Muted && r.Width > 40)
            dc.DrawText(Text("muted", 9, text), new Point(r.Right - 34, r.Bottom - 13));
    }

    static Color Lighten(Color c, double amount) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));

    void DrawNotes(DrawingContext dc, PreviewClip clip, Rect inner, int lo, int hi, IBrush fill)
    {
        var rows = Math.Max(1, hi - lo + 1);
        var rowH = Math.Clamp(inner.Height / rows, 1.2, 5);
        var span = inner.Height - rowH;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
            foreach (var n in clip.Notes)
            {
                if (n.Muted) continue;
                var x = X(clip.Start + n.Start);
                if (x > Bounds.Width) break;
                var nw = Math.Max(1.2, n.Length * Zoom - 0.5);
                if (x + nw < HeaderWidth) continue;
                var ny = inner.Y + (rows == 1 ? span / 2 : span * (1 - (double)(n.Key - lo) / (rows - 1)));
                ctx.BeginFigure(new Point(x, ny), true);
                ctx.LineTo(new Point(x + nw, ny));
                ctx.LineTo(new Point(x + nw, ny + rowH));
                ctx.LineTo(new Point(x, ny + rowH));
                ctx.EndFigure(true);
            }
        dc.DrawGeometry(fill, null, g);
    }

    void DrawAudio(DrawingContext dc, AudioPart part, PreviewClip clip, Rect inner, IBrush fill, Color c)
    {
        var x0 = Math.Max(X(clip.Start + part.Start), HeaderWidth);
        var x1 = Math.Min(X(clip.Start + part.Start + part.Length), Bounds.Width);
        if (x1 <= x0) return;
        var mid = inner.Y + inner.Height / 2;
        if (!part.Found)
        {
            // No file: a flat line and a note, so it's clear the clip is there but its audio isn't.
            dc.FillRectangle(new SolidColorBrush(Alpha(c, 0.6)), new Rect(x0, mid, x1 - x0, 1));
            if (x1 - x0 > 90 && inner.Height > 14)
                dc.DrawText(Text("file not on this computer", 9, Res("MutedBrush", Brushes.Gray)), new Point(x0 + 4, mid - 12));
            return;
        }
        if (!_peaks.TryGetValue(part.Path, out var peaks))
        {
            // Still loading: a soft band that sweeps across.
            var phase = (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond % 1200) / 1200.0;
            var sweep = x0 + (x1 - x0) * phase;
            dc.FillRectangle(new SolidColorBrush(Alpha(c, 0.18)), new Rect(x0, mid - inner.Height * 0.3, x1 - x0, inner.Height * 0.6));
            dc.FillRectangle(new SolidColorBrush(Alpha(c, 0.35)), new Rect(Math.Max(x0, sweep - 20), mid - inner.Height * 0.3, Math.Min(40, x1 - x0), inner.Height * 0.6));
            return;
        }
        if (peaks is null)
        {
            dc.FillRectangle(new SolidColorBrush(Alpha(c, 0.6)), new Rect(x0, mid, x1 - x0, 1));
            return;
        }
        var rate = part.Rate(peaks.Seconds);
        if (rate <= 0) rate = peaks.Seconds / Math.Max(part.Length, 1e-6);
        var half = inner.Height / 2;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
            for (var x = x0; x < x1; x += 2)
            {
                var beatA = Beat(x) - clip.Start - part.Start;
                var beatB = Beat(x + 2) - clip.Start - part.Start;
                var (sa, sb) = (part.From + beatA * rate, part.From + beatB * rate);
                if (part.Reversed) (sa, sb) = (peaks.Seconds - sb, peaks.Seconds - sa);
                if (sb < 0 || sa > peaks.Seconds) continue;
                var v = Math.Sqrt(peaks.Max(Math.Max(0, sa), sb));   // a gentle curve so quiet parts still show
                var ph = Math.Max(0.5, v * half);
                ctx.BeginFigure(new Point(x, mid - ph), true);
                ctx.LineTo(new Point(x + 1.5, mid - ph));
                ctx.LineTo(new Point(x + 1.5, mid + ph));
                ctx.LineTo(new Point(x, mid + ph));
                ctx.EndFigure(true);
            }
        dc.DrawGeometry(fill, null, g);
    }

    void DrawCurve(DrawingContext dc, PreviewClip clip, Rect inner, IBrush fill)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var first = true;
            foreach (var p in clip.Points)
            {
                var pt = new Point(X(clip.Start + p.Beat), inner.Bottom - p.Value * inner.Height);
                if (first) ctx.BeginFigure(pt, false);
                else ctx.LineTo(pt);
                first = false;
            }
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, new Pen(fill, 1.5), g);
    }

    /// <summary>One line about a clip for the hover label.</summary>
    static string Describe(PreviewLane lane, PreviewClip clip)
    {
        var what = clip.Name.Length > 0 ? clip.Name : lane.Name;
        var bits = new List<string> { what };
        if (clip.Notes.Count > 0) bits.Add($"{clip.Notes.Count(n => !n.Muted)} notes");
        foreach (var part in clip.Audio.Take(1))
            bits.Add(part.Found ? System.IO.Path.GetFileName(part.Path.Replace('\\', '/')) : $"{System.IO.Path.GetFileName(part.Path.Replace('\\', '/'))} (not on this computer)");
        if (clip.Points.Count > 0) bits.Add("automation");
        if (clip.Muted) bits.Add("muted");
        return string.Join(" · ", bits);
    }
}
