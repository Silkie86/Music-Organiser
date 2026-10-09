namespace MusicOrganiser.Core.Sounds;

/// <summary>One pad in a kit: which sound types can fill it, and what's on it now.</summary>
public sealed class KitSlot
{
    public required string Name { get; set; }
    public required string[] Types { get; set; }
    public bool Locked { get; set; }
    public Sound? Sound { get; set; }
}

/// <summary>
/// Random kits that still make sense: each pad gets a sound of the right type,
/// optionally favouring sounds you haven't used, sticking to one pack for a
/// coherent character, matching a described vibe (with deep listening), and
/// tuning bass and 808 pads to a key. Locked pads stay put on re-rolls.
/// </summary>
public static class KitBuilder
{
    public sealed record Options(bool PreferUnused = true, bool SamePack = false, string? Vibe = null,
                                 int? KeyTonic = null, int Seed = 0, double Randomness = 0.5);

    public static List<KitSlot> DefaultDrumKit() =>
    [
        new() { Name = "Kick", Types = ["Kick"] },
        new() { Name = "Snare", Types = ["Snare"] },
        new() { Name = "Clap", Types = ["Clap", "Snare"] },
        new() { Name = "Closed hat", Types = ["Closed hat", "Hi-hat"] },
        new() { Name = "Open hat", Types = ["Open hat", "Hi-hat", "Cymbal"] },
        new() { Name = "Percussion", Types = ["Percussion", "Tom"] },
        new() { Name = "Percussion 2", Types = ["Percussion", "Tom", "Clap"] },
        new() { Name = "Cymbal", Types = ["Cymbal", "Open hat"] },
        new() { Name = "808 / Bass", Types = ["808", "Bass"] },
        new() { Name = "FX", Types = ["FX", "Texture", "Vocal"] },
    ];

    /// <summary>Fills every unlocked slot. Returns the seed used, so a kit can be rebuilt.</summary>
    public static int Roll(SoundOrganiser org, IList<KitSlot> slots, Options o)
    {
        var seed = o.Seed != 0 ? o.Seed : Random.Shared.Next(1, int.MaxValue);
        var rng = new Random(seed);
        float[]? vibe = null;
        if (!string.IsNullOrWhiteSpace(o.Vibe) && org.HasDeepListening && org.Clap is { } clap) vibe = clap.EmbedText(o.Vibe);

        // The pack (top folder of the sample) everything should come from, when asked.
        string? pack = null;
        if (o.SamePack)
            pack = slots.Where(s => s.Locked && s.Sound is not null).Select(s => Pack(s.Sound!)).FirstOrDefault()
                   ?? PickPack(org, slots, rng);

        var used = slots.Where(s => s.Locked && s.Sound is not null).Select(s => s.Sound!.Id).ToHashSet();
        foreach (var slot in slots)
        {
            if (slot.Locked && slot.Sound is not null) continue;
            var candidates = org.Sounds.Where(s => s.Analysis is not null && !s.IsLoop && slot.Types.Contains(s.Type) && !used.Contains(s.Id)).ToList();
            if (pack is not null && candidates.Any(c => Pack(c) == pack)) candidates = candidates.Where(c => Pack(c) == pack).ToList();
            if (candidates.Count == 0) { slot.Sound = null; continue; }

            var scored = candidates.Select(c =>
            {
                var score = 0.0;
                // Earlier types in the slot are the better fit ("Clap" pad prefers claps over snares).
                score -= Array.IndexOf(slot.Types, c.Type) * 0.3;
                if (o.PreferUnused) score += c.UsedIn == 0 ? 0.4 : -0.1 * Math.Min(c.UsedIn, 5);
                if (c.Favourite) score += 0.2;
                score += Math.Max(0, c.Rating - 3) * 0.1;
                if (vibe is not null && org.ClapVector(c.Id) is { } cv) score += 3 * VectorIndex.Dot(vibe, cv);
                if (o.KeyTonic is { } key && SoundTypes.Tonal.Contains(c.Type) && c.Analysis?.Pitch is { } p)
                {
                    var pc = ((int)Math.Round(p) % 12 + 12) % 12;
                    score += pc == key ? 0.6 : pc == (key + 7) % 12 ? 0.3 : -0.3;
                }
                return (c, score);
            }).ToList();

            slot.Sound = Sample(scored, o.Randomness, rng);
            used.Add(slot.Sound.Id);
        }
        return seed;
    }

    /// <summary>Softmax sampling: low randomness takes the best fits, high randomness anything that fits.</summary>
    static Sound Sample(List<(Sound S, double Score)> scored, double randomness, Random rng)
    {
        var temp = 0.05 + 2 * Math.Clamp(randomness, 0, 1);
        var max = scored.Max(s => s.Score);
        var weights = scored.Select(s => Math.Exp((s.Score - max) / temp)).ToArray();
        var r = rng.NextDouble() * weights.Sum();
        for (var i = 0; i < weights.Length; i++)
        {
            r -= weights[i];
            if (r <= 0) return scored[i].S;
        }
        return scored[^1].S;
    }

    /// <summary>The pack a sample belongs to: the folder two levels above it, or its own folder.</summary>
    public static string Pack(Sound s)
    {
        var dir = Path.GetDirectoryName(s.Path) ?? "";
        var parent = Path.GetDirectoryName(dir);
        return string.IsNullOrEmpty(parent) ? dir : parent;
    }

    static string? PickPack(SoundOrganiser org, IList<KitSlot> slots, Random rng)
    {
        // Packs that can fill most of the kit's slots.
        var packs = org.Sounds.Where(s => !s.IsLoop).GroupBy(Pack)
            .Select(g => (Pack: g.Key, Filled: slots.Count(sl => g.Any(s => sl.Types.Contains(s.Type)))))
            .Where(p => p.Filled >= Math.Max(2, slots.Count / 2)).ToList();
        return packs.Count == 0 ? null : packs[rng.Next(packs.Count)].Pack;
    }

    public sealed record ExportOptions(bool ElektronReady = false, bool Mono = false, bool Normalise = false);

    /// <summary>
    /// Copies the kit into a new folder with numbered pad names. "Elektron ready"
    /// converts to 48 kHz 16-bit WAV for the Digitakt II / Analog Rytm via Elektron Transfer.
    /// </summary>
    public static string Export(IList<KitSlot> slots, string parentFolder, string kitName, ExportOptions o)
    {
        var folder = Path.Combine(parentFolder, Safe(kitName));
        Directory.CreateDirectory(folder);
        var lines = new List<string> { kitName, "" };
        var n = 0;
        foreach (var slot in slots)
        {
            if (slot.Sound is not { } s) continue;
            n++;
            var baseName = $"{n:00} {Safe(slot.Name)} - {Safe(s.Name)}";
            if (!o.ElektronReady && !o.Mono && !o.Normalise)
            {
                var target = Path.Combine(folder, baseName + Path.GetExtension(s.Path));
                File.Copy(s.Path, target, overwrite: true);
            }
            else
            {
                var audio = AudioFile.Read(s.Path, 600);
                var rate = o.ElektronReady ? 48000 : audio.SampleRate;
                var channels = o.Mono ? [audio.Mono()] : audio.Channels;
                channels = channels.Select(c => Dsp.Resample(c, audio.SampleRate, rate)).ToArray();
                if (o.Normalise)
                {
                    var peak = channels.Max(c => c.Length == 0 ? 0 : c.Max(Math.Abs));
                    if (peak > 0)
                    {
                        var g = 0.98f / peak;
                        foreach (var c in channels) for (var i = 0; i < c.Length; i++) c[i] *= g;
                    }
                }
                WavWriter.Write(Path.Combine(folder, baseName + ".wav"), channels, rate, 16);
            }
            lines.Add($"{n:00} {slot.Name}: {s.Path}");
        }
        File.WriteAllLines(Path.Combine(folder, "kit.txt"), lines);
        return folder;
    }

    static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '-' : c)).Trim();
}
