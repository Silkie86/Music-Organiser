namespace MusicOrganiser.Core.Sounds;

public sealed record Suggestion(Sound Sound, string Reason, double Score);

public sealed record FolderUse(string Folder, int Sounds, int Used, double Share);

/// <summary>
/// "Sounds you haven't used much": learns your taste from the sounds your projects
/// use (plus favourites, ratings and what you play), then digs out unused sounds
/// that are close to that taste, kept varied so the list isn't ten copies of one kick.
/// </summary>
public static class Rediscover
{
    public sealed record Options(string? Type = null, int Count = 30, double Adventure = 0.3, int Seed = 0, int MaxUses = 0);

    public static IReadOnlyList<Suggestion> Suggest(SoundOrganiser org, Options o)
    {
        var rng = o.Seed == 0 ? new Random() : new Random(o.Seed);
        var sounds = org.Sounds.Where(s => s.Analysis is not null && (o.Type is null || s.Type == o.Type)).ToList();

        // Your taste: what you've used, starred, rated or keep playing.
        var taste = sounds.Where(s => s.UsedIn > 0 || s.Favourite || s.Rating >= 4 || s.Plays >= 3)
                          .Select(s => (s, W: s.UsedIn + (s.Favourite ? 3 : 0) + Math.Max(0, s.Rating - 3) * 2 + Math.Min(s.Plays, 10) * 0.2))
                          .Where(t => org.NormalisedVector(t.s.Id) is not null)
                          .ToList();
        var candidates = sounds.Where(s => s.UsedIn <= o.MaxUses && org.NormalisedVector(s.Id) is not null).ToList();
        if (candidates.Count == 0) return [];

        // Per-type taste centroids, so a hat is compared with hats you like.
        var centroids = taste.GroupBy(t => t.s.Type).ToDictionary(g => g.Key, g => Centroid(org, g));
        var overall = taste.Count > 0 ? Centroid(org, taste) : null;

        var scored = new List<(Sound S, double Score, string Reason, float[] V)>();
        foreach (var c in candidates)
        {
            var v = org.NormalisedVector(c.Id)!;
            var centroid = centroids.GetValueOrDefault(c.Type) ?? overall;
            var fit = centroid is null ? 0.5 : (VectorIndex.Dot(v, centroid) + 1) / 2;   // 0..1
            var fresh = c.Plays == 0 ? 1.0 : 1.0 / (1 + c.Plays);
            var score = (1 - o.Adventure) * fit + 0.15 * fresh + o.Adventure * rng.NextDouble();
            scored.Add((c, score, "", v));
        }

        // Pick with maximal marginal relevance for variety.
        var picked = new List<(Sound S, double Score, float[] V)>();
        var pool = scored.OrderByDescending(t => t.Score).Take(Math.Max(o.Count * 20, 200)).ToList();
        while (picked.Count < o.Count && pool.Count > 0)
        {
            var best = 0;
            var bestVal = double.MinValue;
            for (var i = 0; i < pool.Count; i++)
            {
                var redundancy = picked.Count == 0 ? 0 : picked.Max(p => VectorIndex.Dot(p.V, pool[i].V));
                var val = 0.75 * pool[i].Score - 0.25 * redundancy;
                if (val > bestVal) { bestVal = val; best = i; }
            }
            picked.Add((pool[best].S, pool[best].Score, pool[best].V));
            pool.RemoveAt(best);
        }

        return picked.Select(p => new Suggestion(p.S, Reason(org, p.S, p.V, taste), Math.Round(p.Score, 3))).ToList();
    }

    static float[] Centroid(SoundOrganiser org, IEnumerable<(Sound s, double W)> items)
    {
        float[]? c = null;
        foreach (var (s, w) in items)
        {
            var v = org.NormalisedVector(s.Id)!;
            c ??= new float[v.Length];
            for (var i = 0; i < Math.Min(c.Length, v.Length); i++) c[i] += (float)(v[i] * w);
        }
        return VectorIndex.Unit(c!);
    }

    static string Reason(SoundOrganiser org, Sound s, float[] v, List<(Sound s, double W)> taste)
    {
        var nearest = taste.Where(t => t.s.Type == s.Type).Select(t => (t.s, Sim: VectorIndex.Dot(v, org.NormalisedVector(t.s.Id)!)))
                           .OrderByDescending(t => t.Sim).FirstOrDefault();
        var unused = s.UsedIn == 0 ? "never used" : $"used in {s.UsedIn} project{(s.UsedIn == 1 ? "" : "s")}";
        if (nearest.s is not null && nearest.Sim > 0.5)
        {
            var why = nearest.s.UsedIn > 0 ? $"used in {nearest.s.UsedIn} project{(nearest.s.UsedIn == 1 ? "" : "s")}"
                    : nearest.s.Favourite ? "a favourite" : "one you like";
            return $"Sounds like {nearest.s.Name} ({why}), {unused}";
        }
        return $"{char.ToUpper(unused[0])}{unused[1..]}, from {s.Folder}";
    }

    /// <summary>Your sample folders by how much of them your projects use: find forgotten packs.</summary>
    public static IReadOnlyList<FolderUse> Folders(SoundOrganiser org) =>
        org.Sounds.GroupBy(s => Path.GetDirectoryName(s.Path) ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(g => new FolderUse(g.Key, g.Count(), g.Count(s => s.UsedIn > 0), Math.Round((double)g.Count(s => s.UsedIn > 0) / g.Count(), 3)))
            .OrderBy(f => f.Share).ThenByDescending(f => f.Sounds).ToList();
}
