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

    /// <summary>What one frame needs: the stars (flat lists, never changed once made) and where the camera is.</summary>
    public sealed record Frame(
        float[] Xs, float[] Ys, float[] Rgb, float[] Weight, int[] CellStart, int[] CellItems, int Grid,
        (int X0, int Y0, int X1, int Y1) Cells, double Scale, double OffsetX, double OffsetY,
        int Width, int Height, float Core, float Glow, float Gain);

    // Stamps are stored at a fraction of their brightness so a crowd can pile onto one pixel before the layer runs out
    // of room; the shader scales back up.
    const float Stamp = 1 / 6f;

    const string Shade = """
        uniform shader stars;
        uniform shader glow;
        uniform float core;
        uniform float glowK;
        uniform float gain;
        half4 main(float2 p) {
            half3 c = stars.eval(p).rgb * core + glow.eval(p * 0.25).rgb * glowK;
            float l = max(c.r, max(c.g, c.b));
            if (l < 0.004) return half4(0);
            float a = 1 - exp(-l * gain);
            return half4(c * (a / l), a);
        }
        """;

    static SKRuntimeEffect? _effect;
    static SKImage? _dot;
    static (GRContext Gr, int W, int H, SKSurface Full, SKSurface Small, SKSurface Blurred)? _surfaces;

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
            if (lease?.GrContext is not { } gr) { GiveUp(); return; }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try { Draw(gr, lease.SkCanvas, frame); }
            catch (Exception e)
            {
                // A driver that can't run the shader, say: the processor's picture is just as good, only slower.
                Console.Error.WriteLine($"The galaxy couldn't draw on the graphics card, so it uses the processor: {e.Message}");
                Diagnostics.SessionLog.Current?.Warning("Facts", $"Galaxy: couldn't draw on the graphics card, so it uses the processor: {e}");
                GiveUp();
                return;
            }
            if (Available != true) Diagnostics.SessionLog.Current?.Info("Facts", "Galaxy: drawing on the graphics card");
            Available = true;
            if (Environment.GetEnvironmentVariable("MO_GALAXY_TIMING") is not null)
                Console.Error.WriteLine($"galaxy gpu {timer.Elapsed.TotalMilliseconds:0.0} ms");
        }
    }

    static void GiveUp()
    {
        if (Available == false) return;
        Available = false;
        Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    /// <summary>Raised (on the UI thread) when the graphics card turns out not to be available, so the view repaints on the processor.</summary>
    public static event Action? Changed;

    static void Draw(GRContext gr, SKCanvas target, Frame f)
    {
        int w = f.Width, h = f.Height, sw = w / 4 + 1, sh = h / 4 + 1;
        if (_surfaces is not { } s || s.Gr != gr || s.W != w || s.H != h)
        {
            if (_surfaces is { } old) { old.Full.Dispose(); old.Small.Dispose(); old.Blurred.Dispose(); }
            SKSurface Make(int width, int height) =>
                SKSurface.Create(gr, false, new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("The graphics card couldn't make a drawing layer.");
            _surfaces = s = (gr, w, h, Make(w, h), Make(sw, sh), Make(sw, sh));
        }

        // Every star in view: where its dot goes (centred, about 4 pixels across) and its colour times its brightness.
        var count = 0;
        for (var cy = f.Cells.Y0; cy <= f.Cells.Y1; cy++)
            count += f.CellStart[cy * f.Grid + f.Cells.X1 + 1] - f.CellStart[cy * f.Grid + f.Cells.X0];
        var xforms = new SKRotationScaleMatrix[count];
        var colours = new SKColor[count];
        var sprites = new SKRect[count];
        var dot = Dot();
        var spriteRect = new SKRect(0, 0, 16, 16);
        const float size = 0.28f;   // 16 px × 0.28 ≈ 4.5 px
        var n = 0;
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
                sprites[n] = spriteRect;
                n++;
            }
        if (n < count)
        {
            Array.Resize(ref xforms, n);
            Array.Resize(ref colours, n);
            Array.Resize(ref sprites, n);
        }

        using var add = new SKPaint { BlendMode = SKBlendMode.Plus };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear);
        var full = s.Full.Canvas;
        full.Clear(SKColors.Transparent);
        full.DrawAtlas(dot, sprites, xforms, colours, SKBlendMode.Modulate, sampling, add);

        // The glow: the same stamps at a quarter size, then blurred.
        var small = s.Small.Canvas;
        small.Clear(SKColors.Transparent);
        small.Save();
        small.Scale(0.25f);
        small.DrawAtlas(dot, sprites, xforms, colours, SKBlendMode.Modulate, sampling, add);
        small.Restore();
        using (var smallImage = s.Small.Snapshot())
        using (var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(2.6f, 2.6f) })
        {
            var blurred = s.Blurred.Canvas;
            blurred.Clear(SKColors.Transparent);
            blurred.DrawImage(smallImage, 0, 0, blur);
        }

        _effect ??= SKRuntimeEffect.CreateShader(Shade, out var errors)
                    ?? throw new InvalidOperationException($"The galaxy's shader didn't build: {errors}");
        using var starsImage = s.Full.Snapshot();
        using var glowImage = s.Blurred.Snapshot();
        using var starsShader = starsImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest));
        using var glowShader = glowImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        var uniforms = new SKRuntimeEffectUniforms(_effect)
        {
            ["core"] = f.Core / Stamp,
            // A quarter-size stamp covers a sixteenth of the pixels, so the glow is brought back up to match.
            ["glowK"] = f.Glow / Stamp * 0.9f,
            ["gain"] = f.Gain,
        };
        var children = new SKRuntimeEffectChildren(_effect) { ["stars"] = starsShader, ["glow"] = glowShader };
        using var shader = _effect.ToShader(uniforms, children);
        using var paint = new SKPaint { Shader = shader };
        target.DrawRect(new SKRect(0, 0, w, h), paint);
    }
}
