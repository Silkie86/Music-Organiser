using System.Text.RegularExpressions;

namespace MusicOrganiser.Core.Sounds;

/// <summary>
/// The kinds of sound the organiser sorts samples into, and the folder/file-name
/// clues used as starting labels before the classifier has learned your library.
/// </summary>
public static partial class SoundTypes
{
    public const string Unknown = "Unknown";

    /// <summary>Type → regex over the lower-cased path. Order matters: earlier, more specific types win.</summary>
    static readonly (string Type, Regex Pattern, string Prompt)[] Rules =
    [
        ("808", R(@"\b808s?\b|\bsub ?bass|\bsubs?\b"), "an 808 sub bass"),
        ("Kick", R(@"\bkicks?\b|\bkik\b|\bbd\b|bassdrum|bass drum|\bkck"), "a kick drum"),
        ("Snare", R(@"\bsnares?\b|\bsnr\b|\bsd\b|rimshot|\brim\b"), "a snare drum"),
        ("Clap", R(@"\bclaps?\b|\bclp\b|handclap|\bsnap(s)?\b"), "a hand clap"),
        ("Closed hat", R(@"closed ?(hi ?-?)?hats?|\bchh?\b|\bc ?hat\b"), "a closed hi-hat"),
        ("Open hat", R(@"open ?(hi ?-?)?hats?|\bohh?\b|\bo ?hat\b"), "an open hi-hat"),
        ("Hi-hat", R(@"\bhi ?-?hats?\b|\bhats?\b|\bhh\b"), "a hi-hat"),
        ("Cymbal", R(@"\bcymbals?\b|\bcrash(es)?\b|\brides?\b|\bsplash\b|\bchina\b"), "a cymbal crash"),
        ("Tom", R(@"\btoms?\b|\bfloor ?tom"), "a tom drum"),
        ("Percussion", R(@"\bperc(ussion)?s?\b|\bshakers?\b|\bconga|\bbongo|tambourine|\bcowbell|\bclave|\bwoodblock|\btriangle|\bblock\b"), "a percussion hit"),
        ("Drum loop", R(@"\bdrum ?loops?\b|\bbeats?\b|\bbreaks?\b|\bbreakbeats?\b|\btop ?loops?\b|\bgrooves?\b|\bdrums?\b.*\bloop"), "a drum loop"),
        ("Vocal", R(@"\bvox\b|\bvocals?\b|\bvoice|\bacapella|\bchops?\b|\bphrase|\bspoken|\bchants?\b|\bad ?libs?\b"), "a vocal"),
        ("Bass", R(@"\bbass(es|line)?\b|\breese\b"), "a bass synth"),
        ("Pad", R(@"\bpads?\b|\bdrones?\b"), "a synth pad"),
        ("Lead", R(@"\bleads?\b|\bsynth ?lines?\b"), "a synth lead"),
        ("Pluck", R(@"\bplucks?\b|\bpizz"), "a pluck"),
        ("Keys", R(@"\bkeys\b|\bpiano|\brhodes\b|\bepiano|\borgan\b|\bwurli"), "piano keys"),
        ("Chord", R(@"\bchords?\b|\bstabs?\b|\bprogression"), "a chord stab"),
        ("Guitar", R(@"\bguitars?\b|\bgtr\b"), "a guitar"),
        ("Strings & brass", R(@"\bstrings?\b|\bviolin|\bcello|\bbrass\b|\bhorns?\b|\btrumpet|\bsax"), "strings or brass"),
        ("FX", R(@"\bfx\b|\bsfx\b|\brisers?\b|\bdownlifters?\b|\buplifters?\b|\bsweeps?\b|\bimpacts?\b|\bwhoosh|\btransitions?\b|\bnoise\b|\bzaps?\b|\breverse"), "a sound effect riser or impact"),
        ("Texture", R(@"\btextures?\b|\batmos(phere)?s?\b|\bambien(ce|t)\b|\bfoley\b|\bfield ?rec"), "an ambient texture"),
        ("Music loop", R(@"\bloops?\b|\bmelod(y|ies)\b|\bmusic\b|\bmelodic\b"), "a melodic music loop"),
    ];

    static Regex R(string p) => new(p, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> All { get; } = Rules.Select(r => r.Type).Distinct().ToList();

    /// <summary>Drum one-shot types, used by the kit builder.</summary>
    public static readonly IReadOnlySet<string> Drums = new HashSet<string>
        { "Kick", "Snare", "Clap", "Closed hat", "Open hat", "Hi-hat", "Cymbal", "Tom", "Percussion" };

    /// <summary>Types whose pitch matters, so key and root note are worth showing.</summary>
    public static readonly IReadOnlySet<string> Tonal = new HashSet<string>
        { "808", "Bass", "Pad", "Lead", "Pluck", "Keys", "Chord", "Guitar", "Strings & brass", "Music loop", "Vocal" };

    /// <summary>A text description of each type, for zero-shot matching with the CLAP model.</summary>
    public static IReadOnlyDictionary<string, string> Prompts { get; } =
        Rules.GroupBy(r => r.Type).ToDictionary(g => g.Key, g => "the sound of " + g.First().Prompt);

    /// <summary>The type named by a sample's folder or file name, or null. The file name wins over folders.</summary>
    public static string? FromPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        // File name first, then folders from the innermost out.
        for (var i = parts.Length - 1; i >= Math.Max(0, parts.Length - 5); i--)
        {
            var text = Normalise(i == parts.Length - 1 ? Path.GetFileNameWithoutExtension(parts[i]) : parts[i]);
            foreach (var (type, pattern, _) in Rules)
                if (pattern.IsMatch(text)) return type;
        }
        return null;
    }

    /// <summary>"Loop" or "One-shot" when the name says so.</summary>
    public static bool? LoopFromPath(string path)
    {
        var t = Normalise(path);
        if (OneShotWords().IsMatch(t)) return false;
        if (LoopWords().IsMatch(t)) return true;
        return null;
    }

    /// <summary>Lower-case with separators turned into spaces, and camelCase split, so "KickHard_01" reads "kick hard 01".</summary>
    static string Normalise(string s)
    {
        s = CamelSplit().Replace(s, "$1 $2");
        s = Separators().Replace(s.ToLowerInvariant(), " ");
        return DigitSplit().Replace(s, "$1 $2");
    }

    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex CamelSplit();

    [GeneratedRegex(@"[_\-\.\(\)\[\]&+]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"([a-z])(\d)")]
    private static partial Regex DigitSplit();

    [GeneratedRegex(@"\bone ?shots?\b|\boneshots?\b|\bhits?\b|\bshots?\b")]
    private static partial Regex OneShotWords();

    [GeneratedRegex(@"\bloops?\b|\bbpm\b|\b\d{2,3} ?bpm")]
    private static partial Regex LoopWords();

    /// <summary>BPM written in a file name ("Loop_128bpm", "128 BPM", "_128_"), if any.</summary>
    public static double? TempoFromName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var m = BpmTag().Match(name);
        if (m.Success && double.TryParse(m.Groups[1].Value, out var bpm) && bpm is >= 50 and <= 220) return bpm;
        m = BareNumber().Match(name);
        if (m.Success && double.TryParse(m.Groups[1].Value, out bpm) && bpm is >= 70 and <= 200) return bpm;
        return null;
    }

    [GeneratedRegex(@"(\d{2,3}(?:\.\d+)?)\s*-?bpm", RegexOptions.IgnoreCase)]
    private static partial Regex BpmTag();

    [GeneratedRegex(@"(?:^|[_\s-])(\d{2,3})(?:[_\s-]|$)")]
    private static partial Regex BareNumber();

    /// <summary>A key written in a file name ("Am", "F# minor", "_Cmaj_"), if any.</summary>
    public static (int Tonic, bool Minor)? KeyFromName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var m = KeyTag().Match(name);
        if (!m.Success) return null;
        return MusicTheory.ParseKey(m.Groups[1].Value + m.Groups[2].Value + " " + m.Groups[3].Value);
    }

    [GeneratedRegex(@"(?:^|[_\s\-\(\[])([A-G])([#b♯♭]?)\s?(maj(?:or)?|min(?:or)?|m)?(?:[_\s\-\)\]]|$)")]
    private static partial Regex KeyTag();
}
