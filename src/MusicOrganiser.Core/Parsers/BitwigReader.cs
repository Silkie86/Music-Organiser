using System.IO.Compression;
using System.Text.RegularExpressions;

namespace MusicOrganiser.Core.Parsers;

/// <summary>
/// Bitwig Studio .bwproject reader (heuristic).
///
/// Bitwig's project format is a proprietary binary object stream with no public
/// spec. What can be recovered reliably is the readable text inside it: plugin
/// file paths, sample file paths, Bitwig device names and some metadata. Note
/// data and plugin state are binary and not safely decodable, so MIDI and preset
/// extraction aren't offered; export clips from Bitwig itself.
///
/// A Bitwig project folder usually looks like
///   My Song/My Song.bwproject, samples/, recordings/, bounce/
/// so audio in those folders is attributed to the project too.
/// </summary>
public sealed partial class BitwigReader : IProjectReader
{
    public string Daw => "Bitwig Studio";
    public IReadOnlyList<string> Extensions { get; } = [".bwproject"];

    static readonly HashSet<string> Instruments = new(StringComparer.Ordinal)
    {
        "Polymer", "Phase-4", "Polysynth", "FM-4", "Sampler", "E-Kick", "E-Snare", "E-Hat", "E-Clap", "E-Cowbell",
        "E-Tom", "Drum Machine", "Organ", "Poly Grid", "Wavetable", "Instrument Layer", "Instrument Selector",
    };

    static readonly HashSet<string> Effects = new(StringComparer.Ordinal)
    {
        "EQ+", "EQ-2", "EQ-5", "EQ-DJ", "Filter+", "Delay+", "Delay-1", "Delay-2", "Delay-4", "Reverb", "Convolution",
        "Compressor", "Compressor+", "Dynamics", "Transient Control", "Peak Limiter", "Chorus", "Chorus+", "Flanger",
        "Flanger+", "Phaser", "Phaser+", "Distortion", "Saturator", "Bit-8", "Amp", "Treemonster", "Tool",
        "Spectral Split", "Multiband FX-2", "Multiband FX-3", "FX Grid", "FX Layer", "FX Selector", "Freq Shifter",
        "Pitch Shifter", "Resonator Bank", "Ladder", "Sweep", "Fuzz", "Over", "Heat", "Rotary", "Mid-Side Split",
        "Gate", "Expander", "De-Esser", "Ring-Mod", "Comb", "Tape", "Pitch Echo", "Frequency Shifter+",
    };

    static readonly HashSet<string> NoteEffects = new(StringComparer.Ordinal)
    {
        "Arpeggiator", "Note Echo", "Note Latch", "Multi-Note", "Note Length", "Quantize", "Note Harmonizer",
        "Ricochet", "Key Filter", "Note Filter", "Velocity Curve", "Transposition Map", "Micro-pitch", "Note Grid",
    };

    /// <summary>
    /// Names of plugins installed on this PC (see <see cref="PluginCatalog"/>). Bitwig stores
    /// third-party plugins by name, so text in the project matching one of these is a plugin.
    /// </summary>
    public IReadOnlySet<string> KnownPlugins { get; set; } = PluginCatalog.AlwaysKnown;

    public ProjectInfo Read(string path)
    {
        var raw = File.ReadAllBytes(path);
        var info = new ProjectInfo { Daw = Daw, Confidence = Confidence.Heuristic };
        if (raw.Length < 4 || raw[0] != 'B' || raw[1] != 't' || raw[2] != 'W' || raw[3] != 'g')
            info.Warnings.Add("The file doesn't start with Bitwig's header, so results may be incomplete.");
        info.Warnings.Add("Bitwig's project format is closed, so plugins and samples are found by scanning text " +
                          "inside the file. MIDI and preset extraction aren't available; export clips from Bitwig.");

        foreach (var blob in Blobs(raw))
        {
            var expectVersion = false;
            foreach (var str in TextScan.Strings(blob))
            {
                var s = str.Trim('\0', ' ');
                // The version follows its key, either in the same text run or as the next one.
                if (VersionRx().Match(s) is { Success: true } v)
                    info.Version = v.Groups[1].Value;
                else if (expectVersion && VersionOnly().IsMatch(s))
                    info.Version = s;
                expectVersion = s.EndsWith("application_version_name", StringComparison.Ordinal);
                if (TextScan.IsAudioPath(s))
                    info.Samples.Add(s);
                else if (TextScan.IsPluginPath(s))
                {
                    var p = PathStart().Match(s) is { Success: true } m ? s[m.Index..] : s;
                    info.Plugins.Add(new PluginRef(TextScan.PluginNameFromPath(p), "", PluginKind.Unknown,
                                                   TextScan.PluginFormatFromPath(p), p));
                }
                else if (KnownPlugins.Contains(s))
                    info.Plugins.Add(new PluginRef(s, "", PluginCatalog.KindOf(s), ""));
                else if (Instruments.Contains(s))
                    info.Plugins.Add(new PluginRef(s, "Bitwig", PluginKind.Instrument, "Native"));
                else if (Effects.Contains(s))
                    info.Plugins.Add(new PluginRef(s, "Bitwig", PluginKind.Effect, "Native"));
                else if (NoteEffects.Contains(s))
                    info.Plugins.Add(new PluginRef(s, "Bitwig", PluginKind.NoteEffect, "Native"));
            }
        }

        var dir = Path.GetDirectoryName(path) ?? ".";
        foreach (var sub in new[] { "samples", "recordings", "audio" })
        {
            var d = Path.Combine(dir, sub);
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                if (TextScan.IsAudioPath(f)) info.Samples.Add(f);
        }

        var bounce = Path.Combine(dir, "bounce");
        if (Directory.Exists(bounce))
            info.Details["Bounces"] = Directory.EnumerateFiles(bounce, "*", SearchOption.AllDirectories)
                                               .Count(TextScan.IsAudioPath).ToString();
        return info.Dedupe();
    }

    /// <summary>The raw file plus any zlib streams embedded in it.</summary>
    static IEnumerable<ReadOnlyMemory<byte>> Blobs(byte[] raw)
    {
        yield return raw;
        var attempts = 0;
        for (var i = 0; i + 2 < raw.Length && attempts < 200; i++)
        {
            // zlib header: CMF 0x78, and (CMF*256 + FLG) divisible by 31.
            if (raw[i] != 0x78 || ((0x78 << 8) | raw[i + 1]) % 31 != 0) continue;
            attempts++;
            byte[]? inflated = null;
            try
            {
                using var z = new ZLibStream(new MemoryStream(raw, i, raw.Length - i), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                var buf = new byte[81920];
                int n;
                while ((n = z.Read(buf)) > 0 && ms.Length < 200_000_000) ms.Write(buf, 0, n);
                if (ms.Length >= 8) inflated = ms.ToArray();
            }
            catch (InvalidDataException) { }
            if (inflated is not null) yield return inflated;
        }
    }

    [GeneratedRegex(@"application_version_name\W+(\d+(?:\.\d+)*)")]
    private static partial Regex VersionRx();

    [GeneratedRegex(@"^\d+(?:\.\d+)+$")]
    private static partial Regex VersionOnly();

    [GeneratedRegex(@"[A-Za-z]:[\\/]|/")]
    private static partial Regex PathStart();
}
