using System.Text.Json;

namespace MusicOrganiser.Core.Sounds;

/// <summary>
/// How much each sign that you like a sound counts towards "Most liked" (Solomon's starting values; adjustable in
/// Settings): each project using it, each drag out of the app or send into a DAW, each deliberate play, and being a
/// favourite. Anything older than a year counts <see cref="OlderShare"/> as much.
/// </summary>
public sealed record LikedWeights(double Project = 5, double Drag = 3, double Send = 3, double Play = 1, double Favourite = 10,
                                  double OlderShare = 0.5)
{
    public static readonly LikedWeights Default = new();

    public string ToJson() => JsonSerializer.Serialize(this);

    public static LikedWeights FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try { return JsonSerializer.Deserialize<LikedWeights>(json) ?? Default; }
        catch (JsonException) { return Default; }
    }

    /// <summary>The sound's score: higher is more liked.</summary>
    public double Score(Sound s)
    {
        var a = s.Activity;
        double Both(int recent, int older) => recent + older * OlderShare;
        return Project * Both(s.UsedInRecently, Math.Max(0, s.UsedIn - s.UsedInRecently)) +
               Drag * Both(a.Drags, a.OlderDrags) +
               Send * Both(a.Sends, a.OlderSends) +
               Play * Both(a.Plays, a.OlderPlays) +
               (s.Favourite ? Favourite : 0);
    }

    /// <summary>The liked bar's tooltip: "Favourite · in 4 projects · dragged out 6 times · played 12 times".</summary>
    public static string Explain(Sound s)
    {
        var a = s.Activity;
        static string Times(int n) => n == 1 ? "once" : $"{n} times";
        var parts = new List<string>();
        if (s.Favourite) parts.Add("Favourite");
        if (s.UsedIn > 0) parts.Add(s.UsedIn == 1 ? "in 1 project" : $"in {s.UsedIn} projects");
        if (a.Drags + a.OlderDrags > 0) parts.Add($"dragged out {Times(a.Drags + a.OlderDrags)}");
        if (a.Sends + a.OlderSends > 0) parts.Add($"sent into a DAW {Times(a.Sends + a.OlderSends)}");
        if (a.Plays + a.OlderPlays > 0) parts.Add($"played {Times(a.Plays + a.OlderPlays)}");
        if (parts.Count == 0) return "Not used, played or dragged out yet";
        var text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
