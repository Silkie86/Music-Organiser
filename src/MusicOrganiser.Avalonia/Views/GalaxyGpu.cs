using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// The galaxy's painted stars, drawn by the graphics card. Each star is a small glowing dot stamped in its colour onto
/// a high-precision layer where the stamps add up; a quarter-size copy, blurred, becomes the glow around crowded
/// places; then a tiny program on the graphics card (a shader) squeezes the brightness so a crowd stays vivid and keeps
/// its colour. It's the same picture <see cref="GalaxyCanvas"/> paints on the processor, which stays as the fallback
/// for computers where Avalonia isn't drawing with the graphics card.
/// </summary>
static class GalaxyGpu
{
    /// <summary>Unknown until the first try; then whether the graphics card is drawing. MO_GALAXY_CPU=1 forces the processor.</summary>
    public static bool? Available = Environment.GetEnvironmentVariable("MO_GALAXY_CPU") is not null ? false : null;

    /// <summary>
    /// What one frame needs: the stars (flat lists, never changed once made), where the camera is, and the brightness
    /// chosen beside the galaxy (1 as measured: see <see cref="GainFor"/>).
    /// </summary>
    public sealed record Frame(
        float[] Xs, float[] Ys, float[] Rgb, float[] Weight, int[] CellStart, int[] CellItems, int Grid,
        (int X0, int Y0, int X1, int Y1) Cells, double Scale, double OffsetX, double OffsetY,
        int Width, int Height, float Core, float Glow, float Brightness, bool Moving = false);

    // Stamps are stored at a fraction of their brightness so a crowd can pile onto one pixel before the layer runs out
    // of room; the shader scales back up.
    const float Stamp = 1 / 6f;

    const string ShaderCode = """
        uniform shader stars;
        uniform shader glow;
        uniform float core;
        uniform float glowK;
        uniform float gain;
        uniform float2 shift;
        half4 main(float2 p) {
            p -= shift;
            half3 c = stars.eval(p).rgb * core + glow.eval(p * 0.25).rgb * glowK;
            float l = max(c.r, max(c.g, c.b));
            if (l < 0.004) return half4(0);
            float a = 1 - exp(-l * gain);
            return half4(c * (a / l), a);
        }
        """;

    static SKRuntimeEffect? _effect;
    static SKImage? _dot;
    static (GRContext? Gr, int W, int H, SKSurface Full, SKSurface Small, SKSurface Blurred, SKSurface Sky)? _surfaces;

    // The stars' stamps (Full and its blurred quarter-size copy) and the view they were stamped at. Stamping 366,000
    // stars is the slow part (Skia turns each into its own pair of triangles on the processor, every time), so it's
    // done only when the view really needs it: the brightness settings are the shader's, applied after, and a stamp of
    // the whole galaxy (zoomed out, every star on it) is just slid along when you drag, never stamped again.
    static (float[] Xs, float[] Rgb, float[] Weight, double X, double Y, double Scale, bool Whole, float Gain)? _stamped;
    // The finished sky on screen, and what made it, so a frame that changes nothing (a hover, a pulse) just shows it.
    static object? _skyKey;
    // The last settled sky with every star on it, and the view it was made for.
    static SKImage? _whole;
    static (float[] Xs, float[] Rgb, float[] Weight, double X, double Y, double Scale)? _wholeAt;
    static (double X, double Y, double Scale) _skyAt;
    // What the last stamp cost (working out the list plus stamping), deciding whether the next waits for a thread.
    static double _costMs = double.MaxValue;

    // ---- the list of stamps, worked out off the render thread -----------------------------------------------

    /// <summary>Every star in one view: where its dot goes and its colour. Kept and reused, never made per frame.</summary>
    sealed class Stamps
    {
        public SKRotationScaleMatrix[] Xforms = [];
        public SKColor[] Colours = [];
        public SKRect[] Sprites = [];
        public Frame? For;
        public bool Whole;
        public double BuildMs;
        // How the stars' light is spread, on a grid of 2-pixel squares, and the gain worked out from it.
        public float[] Light = [];
        public float Gain = 1.7f;
    }

    static readonly object BuildGate = new();
    static Stamps? _ready, _spare;
    static Frame? _wanted;
    static bool _building;

    static bool SameView(Frame a, Frame b) =>
        a.Xs == b.Xs && a.Rgb == b.Rgb && a.Weight == b.Weight && a.Width == b.Width && a.Height == b.Height &&
        a.Scale == b.Scale && a.OffsetX == b.OffsetX && a.OffsetY == b.OffsetY && a.Cells == b.Cells;

    /// <summary>Asks for this view's stamps on a background thread; the newest view asked for wins.</summary>
    static void Want(Frame f)
    {
        lock (BuildGate)
        {
            _wanted = f;
            if (_building) return;
            _building = true;
        }
        Task.Run(() =>
        {
            while (true)
            {
                Frame next;
                Stamps into;
                lock (BuildGate)
                {
                    if (_wanted is not { } w) { _building = false; return; }
                    next = w;
                    _wanted = null;
                    into = _spare ?? new Stamps();
                    _spare = null;
                }
                try { Build(next, into); }
                catch (Exception e)
                {
                    MusicOrganiser.Core.AppLog.Info("Galaxy", $"Working out the stars to draw failed: {e.Message}");
                    lock (BuildGate) _building = false;
                    return;
                }
                lock (BuildGate)
                {
                    if (_ready is not null) _spare ??= _ready;   // never drawn: the newer one replaces it
                    _ready = into;
                }
                Dispatcher.UIThread.Post(() => Changed?.Invoke());   // draw it
            }
        });
    }

    /// <summary>The stamps worked out for exactly this view, if they're ready (taken: hand them back with <see cref="Return"/>).</summary>
    static Stamps? Take(Frame f)
    {
        lock (BuildGate)
        {
            if (_ready is not { For: { } built } r || !SameView(built, f)) return null;
            _ready = null;
            return r;
        }
    }

    static void Return(Stamps used)
    {
        lock (BuildGate) _spare ??= used;
    }

    static void Build(Frame f, Stamps into)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        int w = f.Width, h = f.Height;
        var count = 0;
        for (var cy = f.Cells.Y0; cy <= f.Cells.Y1; cy++)
            count += f.CellStart[cy * f.Grid + f.Cells.X1 + 1] - f.CellStart[cy * f.Grid + f.Cells.X0];
        // The lists keep their size while the count stays the same (zoomed out, it's every star each time), so
        // there's no 450,000-long list made and thrown away per view for .NET to tidy up later.
        var n = 0;
        var xforms = into.Xforms.Length >= count ? into.Xforms : new SKRotationScaleMatrix[count];
        var colours = into.Colours.Length >= count ? into.Colours : new SKColor[count];
        int lw = w / 2 + 1, lh = h / 2 + 1;
        var light = into.Light.Length >= lw * lh ? into.Light : new float[lw * lh];
        Array.Clear(light, 0, lw * lh);
        const float size = 0.28f;   // 16 px × 0.28 ≈ 4.5 px
        for (var cy = f.Cells.Y0; cy <= f.Cells.Y1; cy++)
            for (var k = f.CellStart[cy * f.Grid + f.Cells.X0]; k < f.CellStart[cy * f.Grid + f.Cells.X1 + 1]; k++)
            {
                var i = f.CellItems[k];
                var x = (float)(f.Xs[i] * f.Scale + f.OffsetX);
                var y = (float)(f.Ys[i] * f.Scale + f.OffsetY);
                if (x < -4 || y < -4 || x > w + 4 || y > h + 4) continue;
                xforms[n] = new SKRotationScaleMatrix(size, 0, x - 8 * size, y - 8 * size);
                // The colour keeps its full hue; the brightness rides in the alpha, which the stamp multiplies through.
                colours[n] = new SKColor((byte)(f.Rgb[3 * i] * 255), (byte)(f.Rgb[3 * i + 1] * 255), (byte)(f.Rgb[3 * i + 2] * 255),
                                         (byte)Math.Clamp(f.Weight[i] * Stamp * 255, 1, 255));
                light[Math.Clamp((int)y >> 1, 0, lh - 1) * lw + Math.Clamp((int)x >> 1, 0, lw - 1)] +=
                    f.Weight[i] * MathF.Max(f.Rgb[3 * i], MathF.Max(f.Rgb[3 * i + 1], f.Rgb[3 * i + 2]));
                n++;
            }
        // Skia draws every entry of the lists, so they're cut to the stars that landed on screen (only when that changes).
        if (xforms.Length != n) Array.Resize(ref xforms, n);
        if (colours.Length != n) Array.Resize(ref colours, n);
        var sprites = into.Sprites;
        if (sprites.Length != n)
        {
            sprites = new SKRect[n];
            Array.Fill(sprites, new SKRect(0, 0, 16, 16));
        }
        (into.Xforms, into.Colours, into.Sprites, into.For, into.Light) = (xforms, colours, sprites, f, light);
        into.Gain = GainFor(light, lw * lh);
        // Every star in the galaxy landed on it: sliding this stamp along shows exactly what stamping again would.
        into.Whole = n == f.CellStart[f.Grid * f.Grid];
        into.BuildMs = timer.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// How bright to show the stars, from how their light is spread over the view (<paramref name="light"/>: each
    /// 2-pixel square's stars added up), set so a typical lit square shows clearly, as the processor's painting does
    /// (<see cref="GalaxyCanvas"/>'s Paint): bright where few stars are, held back where they crowd so a cloud keeps its
    /// light and dark parts. "Typical" is the middle one (the median), so the tight clumps of near-identical sounds a
    /// big library has don't decide it. The old guess, stars per pixel of the whole view, turned zoomed-out stars down
    /// to a third and left most of them too faint to see (Solomon, 2026-10-27: "stars can go missing when you zoom in
    /// and out").
    /// </summary>
    internal static float GainFor(float[] light, int count)
    {
        // A histogram in quarter-octaves from 1/256 up, so finding the middle doesn't sort a million squares.
        const int bins = 128;
        Span<int> histogram = stackalloc int[bins];
        var lit = 0;
        for (var k = 0; k < count; k++)
        {
            var v = light[k];
            if (v <= 0.004f) continue;
            histogram[Math.Clamp((int)((MathF.Log2(v) + 8) * 4), 0, bins - 1)]++;
            lit++;
        }
        if (lit == 0) return 1.7f;
        var half = lit / 2;
        var bin = 0;
        for (var seen = 0; bin < bins - 1 && (seen += histogram[bin]) <= half; bin++) { }
        var median = MathF.Pow(2, (bin + 0.5f) / 4 - 8);
        // A star's dot spreads about 2.2 pixels' worth of its light over the 4 pixels of a square.
        return (float)Math.Clamp(0.9 / (median * 2.2 / 4), 0.35, 1.7);
    }

    /// <summary>A soft white dot, 16 pixels across, that each star is stamped with.</summary>
    static SKImage Dot()
    {
        if (_dot is not null) return _dot;
        using var bmp = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
            {
                double dx = x - 7.5, dy = y - 7.5;
                var a = (byte)Math.Clamp(255 * Math.Exp(-(dx * dx + dy * dy) / 9.0), 0, 255);
                bmp.SetPixel(x, y, new SKColor(255, 255, 255, a));
            }
        return _dot = SKImage.FromBitmap(bmp);
    }

    public sealed class Op(Rect bounds, Frame frame) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            using var lease = feature?.Lease();
            // No graphics card here (or Avalonia is drawing in software): the processor takes over.
            if (lease?.GrContext is not { } gr)
            {
                // Once it has drawn on the graphics card, a frame without it is Windows taking the card away for a
                // moment (the screen going to sleep, the window moving to another screen): the processor stands in
                // and the card is tried again, rather than painting 450,000 stars on the processor from then on.
                if (Available == true && _lost < 3)
                {
                    // For a moment the last picture stands in, copied back from the card, rather than a blank sky
                    // while the processor paints all the stars; only if the card stays away does the processor take over.
                    _lostAt ??= DateTime.UtcNow;
                    if (lease?.SkCanvas is { } canvas && _copy is { } copy && DateTime.UtcNow - _lostAt < TimeSpan.FromSeconds(3))
                    {
                        Slide(canvas, copy, _copyAt, frame);
                        return;
                    }
                    Lost();
                }
                else GiveUp("Avalonia isn't drawing with the graphics card here");
                return;
            }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try { Draw(gr, lease.SkCanvas, frame); }
            catch (Exception e)
            {
                // A driver that can't run the shader, say: the processor's picture is just as good, only slower.
                Console.Error.WriteLine($"The galaxy couldn't draw on the graphics card, so it uses the processor: {e.Message}");
                GiveUp($"the graphics card couldn't draw it ({e.GetType().Name}: {e.Message})");
                return;
            }
            if (Available != true) MusicOrganiser.Core.AppLog.Info("Galaxy", "Drawing the stars on the graphics card");
            Available = true;
            _lostAt = null;
            GalaxyTiming.Gpu(timer.Elapsed.TotalMilliseconds);
            if (Environment.GetEnvironmentVariable("MO_GALAXY_TIMING") is not null)
                Console.Error.WriteLine($"galaxy gpu {timer.Elapsed.TotalMilliseconds:0.0} ms");
        }
    }

    /// <summary>Forgets every layer and stamp, as if nothing had been drawn yet (the tests, between pictures).</summary>
    internal static void Forget()
    {
        if (_surfaces is { } old) { old.Full.Dispose(); old.Small.Dispose(); old.Blurred.Dispose(); old.Sky.Dispose(); }
        _whole?.Dispose();
        (_surfaces, _stamped, _skyKey, _whole, _wholeAt, _costMs) = (null, null, null, null, null, double.MaxValue);
        lock (BuildGate) (_ready, _spare, _wanted) = (null, null, null);
    }

    static int _lost;
    static DateTime? _lostAt;
    // The last finished sky, copied back from the card once the view settles, for the moments the card goes away.
    static SKImage? _copy;
    static (double X, double Y, double Scale) _copyAt;
    static object? _copyKey;

    static void Lost()
    {
        _lost++;
        _lostAt = null;
        Available = false;
        _surfaces = null;   // made with the context that went away
        (_whole, _wholeAt) = (null, null);
        MusicOrganiser.Core.AppLog.Info("Galaxy", "The graphics card went away for a moment: the processor draws the stars until it's back");
        Dispatcher.UIThread.Post(() =>
        {
            Changed?.Invoke();
            DispatcherTimer.RunOnce(() => { Available = null; Changed?.Invoke(); }, TimeSpan.FromSeconds(3));
        });
    }

    static void GiveUp(string why)
    {
        if (Available == false) return;
        Available = false;
        MusicOrganiser.Core.AppLog.Info("Galaxy", $"Drawing the stars on the processor: {why}");
        Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    /// <summary>Raised (on the UI thread) when the graphics card turns out not to be available, so the view repaints on the processor.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Draws one frame onto <paramref name="target"/>. With no graphics card (<paramref name="gr"/> null: the tests),
    /// the same layers are made in memory, so the processor runs the very same steps.
    /// </summary>
    internal static void Draw(GRContext? gr, SKCanvas target, Frame f)
    {
        int w = f.Width, h = f.Height, sw = w / 4 + 1, sh = h / 4 + 1;
        if (_surfaces is not { } s || s.Gr != gr || s.W != w || s.H != h)
        {
            if (_surfaces is { } old) { old.Full.Dispose(); old.Small.Dispose(); old.Blurred.Dispose(); old.Sky.Dispose(); }
            _whole?.Dispose();   // made by that graphics card, or for another size of view
            _whole = null;
            _wholeAt = null;
            SKSurface Make(int width, int height) =>
                (gr is null ? SKSurface.Create(new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Premul))
                            : SKSurface.Create(gr, false, new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Premul)))
                ?? throw new InvalidOperationException("The graphics card couldn't make a drawing layer.");
            _surfaces = s = (gr, w, h, Make(w, h), Make(sw, sh), Make(sw, sh), Make(w, h));
            _stamped = null;
            _skyKey = null;
        }

        // Are this view's stars already stamped? Exactly, or as a stamp of the whole galaxy that only has to slide.
        var fits = _stamped is { } st && st.Xs == f.Xs && st.Rgb == f.Rgb && st.Weight == f.Weight && st.Scale == f.Scale &&
                   (st.Whole || (st.X == f.OffsetX && st.Y == f.OffsetY));
        if (!fits)
        {
            if (Take(f) is { } ready)
            {
                StampAll(s, ready);
                Return(ready);
            }
            else if (_skyKey is null || _costMs < 6)
            {
                // Nothing on screen yet, or a view with few stars (zoomed in) that's quicker to stamp than to wait
                // for: worked out here, so the edges never come in a frame late.
                Stamps here;
                lock (BuildGate) { here = _spare ?? new Stamps(); _spare = null; }
                Build(f, here);
                StampAll(s, here);
                Return(here);
            }
            else
            {
                // Worked out on another thread while the last sky moves and scales with the camera; stamped once ready.
                Want(f);
                using var sky = s.Sky.Snapshot();
                // Zooming out from close in, the last sky covers only the middle of the new view: round it, the whole
                // galaxy as it last looked, so no stars go missing at the edges while the new view is worked out.
                if (_whole is { } whole && _wholeAt is { } wa && wa.Xs == f.Xs && wa.Rgb == f.Rgb && wa.Weight == f.Weight)
                {
                    var ratio = (float)(f.Scale / _skyAt.Scale);
                    var x = (float)(f.OffsetX - _skyAt.X * ratio);
                    var y = (float)(f.OffsetY - _skyAt.Y * ratio);
                    target.Save();
                    target.ClipRect(new SKRect(x, y, x + s.W * ratio, y + s.H * ratio), SKClipOperation.Difference);
                    Slide(target, whole, (wa.X, wa.Y, wa.Scale), f);
                    target.Restore();
                }
                Slide(target, sky, _skyAt, f);
                return;
            }
        }

        // The brightness pass, cheap: one go over the screen. Skipped when nothing about the picture changed.
        var at = _stamped!.Value;
        var key = (at.Xs, at.X, at.Y, at.Scale, f.OffsetX, f.OffsetY, f.Core, f.Glow, f.Brightness, w, h);
        if (!Equals(key, _skyKey))
        {
            Shade(s, f, (float)(f.OffsetX - at.X), (float)(f.OffsetY - at.Y));
            _skyKey = key;
            _skyAt = (f.OffsetX, f.OffsetY, f.Scale);
        }
        else if (!Equals(_copyKey, key))
        {
            // The second frame showing the same sky: the view has settled, so it's copied back for a card drop-out.
            using var settled = s.Sky.Snapshot();
            if (at.Whole)
            {
                // Every star is on it: kept to fill in round the edges while zooming out (see above).
                _whole?.Dispose();
                _whole = s.Sky.Snapshot();
                _wholeAt = (at.Xs, at.Rgb, at.Weight, _skyAt.X, _skyAt.Y, _skyAt.Scale);
            }
            using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (settled.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
            {
                _copy?.Dispose();
                _copy = SKImage.FromBitmap(bitmap);
                _copyAt = _skyAt;
            }
            _copyKey = key;
        }
        using (var sky = s.Sky.Snapshot()) target.DrawImage(sky, 0, 0);
    }

    /// <summary>A sky drawn for another view, moved and scaled to where the camera is now.</summary>
    static void Slide(SKCanvas target, SKImage sky, (double X, double Y, double Scale) at, Frame f)
    {
        var ratio = (float)(f.Scale / at.Scale);
        target.Save();
        target.Translate((float)(f.OffsetX - at.X * ratio), (float)(f.OffsetY - at.Y * ratio));
        target.Scale(ratio);
        target.DrawImage(sky, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        target.Restore();
    }

    /// <summary>Stamps every star in the list onto the full-size layer and the quarter-size one, then blurs that into the glow.</summary>
    static void StampAll((GRContext? Gr, int W, int H, SKSurface Full, SKSurface Small, SKSurface Blurred, SKSurface Sky) s, Stamps st)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var f = st.For!;
        var dot = Dot();
        using var add = new SKPaint { BlendMode = SKBlendMode.Plus };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear);
        var full = s.Full.Canvas;
        full.Clear(SKColors.Transparent);
        full.DrawAtlas(dot, st.Sprites, st.Xforms, st.Colours, SKBlendMode.Modulate, sampling, add);

        // The glow: the same stamps at a quarter size, then blurred.
        var small = s.Small.Canvas;
        small.Clear(SKColors.Transparent);
        small.Save();
        small.Scale(0.25f);
        small.DrawAtlas(dot, st.Sprites, st.Xforms, st.Colours, SKBlendMode.Modulate, sampling, add);
        small.Restore();
        using (var smallImage = s.Small.Snapshot())
        using (var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(2.6f, 2.6f) })
        {
            var blurred = s.Blurred.Canvas;
            blurred.Clear(SKColors.Transparent);
            blurred.DrawImage(smallImage, 0, 0, blur);
        }
        _stamped = (f.Xs, f.Rgb, f.Weight, f.OffsetX, f.OffsetY, f.Scale, st.Whole, st.Gain);
        _skyKey = null;
        _costMs = st.BuildMs + timer.Elapsed.TotalMilliseconds;
        GalaxyTiming.Stamped(st.BuildMs, timer.Elapsed.TotalMilliseconds, st.Xforms.Length);
    }

    /// <summary>
    /// The brightness pass: stamps and glow, moved by (<paramref name="dx"/>, <paramref name="dy"/>) when a stamp of the
    /// whole galaxy is sliding, squeezed so a crowd stays vivid and keeps its colour.
    /// </summary>
    static void Shade((GRContext? Gr, int W, int H, SKSurface Full, SKSurface Small, SKSurface Blurred, SKSurface Sky) s, Frame f, float dx, float dy)
    {
        _effect ??= SKRuntimeEffect.CreateShader(ShaderCode, out var errors)
                    ?? throw new InvalidOperationException($"The galaxy's shader didn't build: {errors}");
        using var starsImage = s.Full.Snapshot();
        using var glowImage = s.Blurred.Snapshot();
        using var starsShader = starsImage.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, new SKSamplingOptions(SKFilterMode.Nearest));
        using var glowShader = glowImage.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, new SKSamplingOptions(SKFilterMode.Linear));
        var uniforms = new SKRuntimeEffectUniforms(_effect)
        {
            ["core"] = f.Core / Stamp,
            // A quarter-size stamp covers a sixteenth of the pixels, so the glow is brought back up to match.
            ["glowK"] = f.Glow / Stamp * 0.9f,
            ["gain"] = _stamped!.Value.Gain * f.Brightness,
            ["shift"] = new[] { dx, dy },
        };
        var children = new SKRuntimeEffectChildren(_effect) { ["stars"] = starsShader, ["glow"] = glowShader };
        using var shader = _effect.ToShader(uniforms, children);
        using var paint = new SKPaint { Shader = shader };
        var skyCanvas = s.Sky.Canvas;
        skyCanvas.Clear(SKColors.Transparent);
        skyCanvas.DrawRect(new SKRect(0, 0, s.W, s.H), paint);
    }
}

/// <summary>
/// How long the galaxy's frames take, summed up in the run's log every few seconds while it's moving (Diagnostics:
/// <see cref="MusicOrganiser.Core.AppLog"/>), so a slow galaxy on a big library shows where the time goes.
/// </summary>
static class GalaxyTiming
{
    const double Every = 10;   // seconds between notes
    const double Slow = 33;    // a frame slower than this is a stutter you can see

    static readonly object Gate = new();
    static int _frames, _slow, _gpuFrames, _stamps, _stampStars;
    static double _total, _max, _gpuTotal, _gpuMax, _buildTotal, _stampTotal, _stampMax;
    static DateTime _since = DateTime.UtcNow;

    /// <summary>The graphics card's part of a frame (on the render thread).</summary>
    public static void Gpu(double ms)
    {
        lock (Gate)
        {
            _gpuFrames++;
            _gpuTotal += ms;
            _gpuMax = Math.Max(_gpuMax, ms);
        }
    }

    /// <summary>The stars stamped again: working out the list (off the render thread, mostly) and stamping it.</summary>
    public static void Stamped(double buildMs, double stampMs, int stars)
    {
        lock (Gate)
        {
            _stamps++;
            _buildTotal += buildMs;
            _stampTotal += stampMs;
            _stampMax = Math.Max(_stampMax, stampMs);
            _stampStars = stars;
        }
    }

    /// <summary>A frame worked out on the UI thread; writes the note once enough time has gone by.</summary>
    public static void Frame(double ms, Func<string> view)
    {
        string? note = null;
        lock (Gate)
        {
            _frames++;
            _total += ms;
            _max = Math.Max(_max, ms);
            if (ms > Slow) _slow++;
            var now = DateTime.UtcNow;
            if ((now - _since).TotalSeconds < Every) return;
            var drawn = GalaxyGpu.Available switch { true => "graphics card", false => "processor", _ => "not decided yet" };
            note = $"{_frames} frames in {(now - _since).TotalSeconds:0} s on the {drawn}: UI thread average {_total / _frames:0.0} ms, " +
                   $"slowest {_max:0.0} ms, {_slow} over {Slow:0} ms" +
                   (_gpuFrames > 0 ? $"; graphics card average {_gpuTotal / _gpuFrames:0.0} ms, slowest {_gpuMax:0.0} ms" : "") +
                   (_stamps > 0 ? $"; stars stamped again {_stamps} times ({_stampStars:N0} last time): working out the list " +
                                  $"{_buildTotal / _stamps:0.0} ms, stamping {_stampTotal / _stamps:0.0} ms (slowest {_stampMax:0.0})" : "") +
                   $". {view()}";
            _frames = _slow = _gpuFrames = _stamps = 0;
            _total = _max = _gpuTotal = _gpuMax = _buildTotal = _stampTotal = _stampMax = 0;
            _since = now;
        }
        MusicOrganiser.Core.AppLog.Info("Galaxy", note);
    }
}
