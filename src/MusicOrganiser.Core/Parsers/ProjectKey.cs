using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Parsers;

/// <summary>
/// A project's key, worked out from its notes: every pitched part's notes, weighted by how long they sound, matched
/// against the major and minor key profiles. Drums, one-note parts and sample channels are left out; a sample plays at
/// its own pitch, which the notes alone don't tell.
/// </summary>
public static class ProjectKey
{
    /// <summary>Below this, the notes don't point clearly enough at one key (see <see cref="MusicTheory.EstimateKey"/>).</summary>
    public const double MinConfidence = 0.12;

    /// <summary>Plugins whose notes pick a slice or a pad, not a pitch.</summary>
    static readonly HashSet<string> NotPitched = new(StringComparer.OrdinalIgnoreCase)
    {
        "Slicex", "Fruity Slicer", "FPC", "Drum Machine", "Battery", "Battery 4", "Drumaxx", "Fruity DrumSynth Live",
        "Drumpad", "BeepMap", "Autogun",
    };

    /// <summary>The key, such as "F minor", or null when there are too few notes or no clear key.</summary>
    public static KeyEstimate? Estimate(IEnumerable<MidiChannel> channels)
    {
        var chroma = new double[12];
        var notes = 0;
        foreach (var ch in channels)
        {
            if (!IsPitched(ch)) continue;
            var bar = 4.0 * Math.Max(1, ch.Ppq);
            foreach (var n in ch.Notes)
            {
                if (n.Muted) continue;
                // How long it sounds, in bars: a long pad counts more than a passing note, but not without limit.
                chroma[((n.Key % 12) + 12) % 12] += Math.Clamp(n.Length / bar, 1 / 16.0, 2);
                notes++;
            }
        }
        if (notes < 8 || chroma.Count(c => c > 0) < 4) return null;
        return MusicTheory.EstimateKey(chroma) is { Confidence: >= MinConfidence } key ? key : null;
    }

    /// <summary>Reads the project's notes and works out its key: "" when it has no clear key, null when it can't be read.</summary>
    public static string? Detect(string path)
    {
        if (Readers.For(path) is not { } reader) return null;
        try { return Estimate(reader.ExtractChannels(path))?.Name ?? ""; }
        catch (Exception)   // the key is a bonus: notes that can't be read never stop a scan
        {
            return null;
        }
    }

    static bool IsPitched(MidiChannel ch) =>
        ch.SamplePath.Length == 0 && !NotPitched.Contains(ch.Plugin) && ch.Kind is not ("Drums" or "Hits" or "");
}
