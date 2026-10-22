using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// The colours sounds wear: by type everywhere (tile dots, waveforms, the galaxy), and in the galaxy also by key,
/// pack, how liked, whether used, or age. Colours are 0xAARRGGBB so the views can turn them into brushes.
/// </summary>
public static class StarColours
{
    public const uint Grey = 0xFF6E6E6E;

    static readonly (string Type, uint Colour)[] Known =
    [
        ("Kick", 0xFFFF6B5B), ("808", 0xFFFF8A4C), ("Snare", 0xFFFFB547), ("Clap", 0xFFFFD15C), ("Hat", 0xFFE8E36A),
        ("Cymbal", 0xFFC9E46A), ("Perc", 0xFFF59ABF), ("Tom", 0xFFF07C9E), ("Bass", 0xFF7C8CFF), ("Lead", 0xFF4FB3FF),
        ("Pad", 0xFF5CD6E0), ("Keys", 0xFF3DDC97), ("Pluck", 0xFF7FE0B0), ("Chord", 0xFF62C9A8), ("Vocal", 0xFFC792EA),
        ("FX", 0xFFB39DDB), ("Loop", 0xFF9AB8D6), ("Guitar", 0xFFE0A86A), ("Texture", 0xFF8FA3B8),
    ];
    static readonly uint[] Spare = [0xFF8FA3B8, 0xFFA8B88F, 0xFFB89A8F, 0xFF9F8FB8];

    public static uint Type(string? type)
    {
        type ??= "";
        if (type is "" or "Unknown") return Grey;
        foreach (var (t, c) in Known) if (t.Equals(type, StringComparison.OrdinalIgnoreCase)) return c;
        foreach (var (t, c) in Known) if (type.Contains(t, StringComparison.OrdinalIgnoreCase)) return c;
        return Spare[type.ToLowerInvariant().Sum(ch => ch) % Spare.Length];
    }

    /// <summary>Round the Camelot wheel: neighbouring numbers (keys that mix well) get neighbouring colours; minor a shade deeper.</summary>
    public static uint Camelot(int number, bool minor) => Hsl((number - 1) * 30.0, 0.72, minor ? 0.58 : 0.70);

    public static uint Key(Sound s) =>
        s.Analysis?.KeyTonic is { } t && int.TryParse(MusicTheory.Camelot(t, s.Analysis.KeyMinor).TrimEnd('A', 'B'), out var n)
            ? Camelot(n, s.Analysis.KeyMinor) : Grey;

    /// <summary>Each sample pack (folder) its own colour, the same every time.</summary>
    public static uint Pack(string folder)
    {
        var h = 2166136261u;   // FNV-1a: string.GetHashCode changes every run
        foreach (var ch in folder.ToLowerInvariant()) h = (h ^ ch) * 16777619;
        return Hsl(h % 360, 0.55, 0.64);
    }

    /// <summary>Cool slate (0) to warm gold (1).</summary>
    public static uint Liked(double share) => share <= 0 ? 0xFF4A4F5A : Mix(0xFF5B6B8C, 0xFFFFC857, Math.Sqrt(share));

    /// <summary>Used in projects: mint, brighter the more projects. Never used: lavender, the unexplored bits.</summary>
    public static uint Used(int projects) => projects <= 0 ? 0xFFB39DDB : Mix(0xFF2E9E6E, 0xFF7DFFC4, Math.Min(1, projects / 8.0));

    /// <summary>Oldest blue (0) to newest orange (1).</summary>
    public static uint Age(double newness) => Mix(0xFF4F8BFF, 0xFFFF8A4C, newness);

    public static string Hex(uint c) => $"#{c:X8}";

    static uint Mix(uint a, uint b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        uint Ch(int shift) => (uint)Math.Round(((a >> shift) & 0xFF) * (1 - t) + ((b >> shift) & 0xFF) * t);
        return 0xFF000000 | Ch(16) << 16 | Ch(8) << 8 | Ch(0);
    }

    static uint Hsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        uint B(double v) => (uint)Math.Round((v + m) * 255);
        return 0xFF000000 | B(r) << 16 | B(g) << 8 | B(b);
    }
}
