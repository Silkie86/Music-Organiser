using MusicOrganiser.Avalonia.Views;
using SkiaSharp;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>
/// How bright the galaxy's painted stars come out, drawn by <see cref="GalaxyGpu"/> in memory (the same steps the
/// graphics card runs) for a made-up big library: most of its sounds in tight clumps of near-identical ones, as a real
/// library's are, and the rest on their own. Solomon, 2026-10-27, zoomed out on 450,962 stars: "stars can go missing
/// when you zoom in and out", "should be able to increase brightness of stars".
/// </summary>
[Collection(nameof(GalaxyBrightnessTests))]
public sealed class GalaxyBrightnessTests
{
    const int Grid = 192, W = 1600, H = 700;

    sealed record Library(float[] Xs, float[] Ys, float[] Rgb, float[] Weight, int[] CellStart, int[] CellItems);

    // The lone star the tests look at.
    const float LoneX = 0.62f, LoneY = 0.41f;

    static Library Make()
    {
        var rng = new Random(11);
        var points = new List<(float X, float Y, uint Colour, float Weight)>();
        uint[] palette = [0xE5484D, 0xF5A524, 0xF76B9A, 0xE9D44A, 0x9B7BF7, 0x7AA2C9, 0x8E9EF0, 0x52C7B8];
        (float, float) InDisc()
        {
            while (true)
            {
                var x = rng.NextSingle() * 0.8f + 0.1f;
                var y = rng.NextSingle() * 0.8f + 0.1f;
                if ((x - 0.5f) * (x - 0.5f) + (y - 0.5f) * (y - 0.5f) < 0.16f) return (x, y);
            }
        }
        float Twinkle() => 0.5f * (0.6f + 0.4f * rng.NextSingle());
        // 270,000 in 1,800 clumps, each a pixel or so across zoomed out.
        for (var c = 0; c < 1800; c++)
        {
            var (cx, cy) = InDisc();
            var colour = palette[rng.Next(palette.Length)];
            for (var i = 0; i < 150; i++)
                points.Add((cx + (rng.NextSingle() - 0.5f) * 0.001f, cy + (rng.NextSingle() - 0.5f) * 0.001f, colour, Twinkle()));
        }
        // 40,000 on their own.
        for (var i = 0; i < 40_000; i++)
        {
            var (x, y) = InDisc();
            points.Add((x, y, palette[rng.Next(palette.Length)], Twinkle()));
        }
        // Room round the one looked at.
        points.RemoveAll(p => MathF.Abs(p.X - LoneX) < 0.06f && MathF.Abs(p.Y - LoneY) < 0.06f);
        points.Add((LoneX, LoneY, 0x7AA2C9, 0.4f));

        var n = points.Count;
        var cellOf = new int[n];
        var cellStart = new int[Grid * Grid + 1];
        static int Cell(float v) => Math.Clamp((int)MathF.Floor(v * Grid), 0, Grid - 1);
        for (var i = 0; i < n; i++)
        {
            cellOf[i] = Cell(points[i].Y) * Grid + Cell(points[i].X);
            cellStart[cellOf[i] + 1]++;
        }
        for (var c = 0; c < Grid * Grid; c++) cellStart[c + 1] += cellStart[c];
        var fill = (int[])cellStart.Clone();
        var items = new int[n];
        for (var i = 0; i < n; i++) items[fill[cellOf[i]]++] = i;
        var rgb = new float[3 * n];
        for (var i = 0; i < n; i++)
        {
            rgb[3 * i] = ((points[i].Colour >> 16) & 255) / 255f;
            rgb[3 * i + 1] = ((points[i].Colour >> 8) & 255) / 255f;
            rgb[3 * i + 2] = (points[i].Colour & 255) / 255f;
        }
        return new([.. points.Select(p => p.X)], [.. points.Select(p => p.Y)], rgb, [.. points.Select(p => p.Weight)], cellStart, items);
    }

    static readonly Library Stars = Make();

    /// <summary>As the galaxy sets up a frame: the whole map fitted at zoom 1, looking at (cx, cy).</summary>
    static GalaxyGpu.Frame FrameAt(double zoom, double cx, double cy, float brightness = 1)
    {
        var scale = Math.Min(W, H - 48.0) * zoom;
        var ox = W / 2 - cx * scale;
        var oy = H / 2 - cy * scale;
        static int Cell(double v) => Math.Clamp((int)Math.Floor(v * Grid), 0, Grid - 1);
        var cells = (Cell(-ox / scale), Cell(-oy / scale), Cell((W - ox) / scale), Cell((H - oy) / scale));
        return new(Stars.Xs, Stars.Ys, Stars.Rgb, Stars.Weight, Stars.CellStart, Stars.CellItems, Grid, cells, scale, ox, oy, W, H,
                   Core: 1, Glow: 1, brightness);
    }

    /// <summary>The frame drawn on its own (nothing behind it), and how lit the lone star's brightest pixel is, 0 to 255.</summary>
    static int LoneStar(GalaxyGpu.Frame f)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        GalaxyGpu.Forget();
        GalaxyGpu.Draw(null, surface.Canvas, f);
        using var image = surface.Snapshot();
        using var pixels = image.PeekPixels();
        var x = (int)(LoneX * f.Scale + f.OffsetX);
        var y = (int)(LoneY * f.Scale + f.OffsetY);
        var best = 0;
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                best = Math.Max(best, pixels.GetPixelColor(x + dx, y + dy).Alpha);
        GalaxyGpu.Forget();
        return best;
    }

    [Fact]
    public void Zoomed_out_on_a_big_clumpy_library_a_star_on_its_own_is_as_bright_as_close_in()
    {
        var far = LoneStar(FrameAt(1, 0.5, 0.5));
        var near = LoneStar(FrameAt(4, LoneX, LoneY));
        Assert.True(near > 40, $"close in: {near}");
        Assert.True(far >= near * 0.85, $"zoomed out {far}, close in {near}");
        // Before, the brightness was guessed from stars per pixel of the whole view: 310,000 stars here took it from 1.7
        // down to 0.68, and the lone star to a faint smudge (as on Solomon's screen, where most stars looked missing).
        var guessed = 1.7 / Math.Max(1, Stars.Xs.Length * 9.0 / (W * H));
        var before = LoneStar(FrameAt(1, 0.5, 0.5, brightness: (float)(guessed / 1.7)));
        Assert.True(far > before * 1.6, $"now {far}, before {before}");
    }

    [Fact]
    public void The_brightness_setting_lights_the_stars_up_or_down()
    {
        var normal = LoneStar(FrameAt(1, 0.5, 0.5));
        var brighter = LoneStar(FrameAt(1, 0.5, 0.5, brightness: 2.5f));
        var dimmer = LoneStar(FrameAt(1, 0.5, 0.5, brightness: 0.5f));
        Assert.True(brighter > normal * 1.4, $"{brighter} against {normal}");
        Assert.True(dimmer < normal * 0.75, $"{dimmer} against {normal}");
    }

    [Fact]
    public void A_few_crowded_clumps_dont_turn_everything_else_down()
    {
        // Most lit squares hold one star; a few hold hundreds. The middle one decides, so the gain stays up.
        var light = new float[10_000];
        for (var i = 0; i < 2000; i++) light[i * 5] = 0.3f;
        for (var i = 0; i < 100; i++) light[i * 5 + 1] = 60;
        Assert.Equal(1.7f, GalaxyGpu.GainFor(light, light.Length));
        // Where every square is crowded, it's held back so the cloud doesn't glow flat.
        Array.Fill(light, 8f);
        Assert.True(GalaxyGpu.GainFor(light, light.Length) < 0.5f);
        Assert.Equal(1.7f, GalaxyGpu.GainFor(new float[100], 100));
    }
}
