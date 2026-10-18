using System.Text.RegularExpressions;

namespace MusicOrganiser.Core.Midi;

/// <summary>What instrument a MIDI part is for, when something attached to it says so, and how the app knows.</summary>
/// <param name="Name">The family, such as "Piano" or "Drums" (one of <see cref="Instruments.All"/>).</param>
/// <param name="Why">How it was told, such as "Keyscape is a piano plugin" or "General MIDI program 34: Electric Bass (pick)".</param>
public sealed record InstrumentGuess(string Name, string Why);

/// <summary>
/// Names the instrument a MIDI part is for, but only where it's plain from what comes with the notes: the track or
/// clip name, a plugin everyone knows (Keyscape is a piano, Addictive Drums is drums), the file and folder names, MIDI
/// channel 10 (General MIDI keeps it for drums) or the General MIDI program a .mid file asks for. The notes alone can't
/// say whether chords are for a piano or a pad, so when nothing says, it says nothing.
/// </summary>
public static partial class Instruments
{
    public const string Piano = "Piano", Organ = "Organ", Bells = "Bells", Guitar = "Guitar", Bass = "Bass", Strings = "Strings",
                        Brass = "Brass", Woodwind = "Woodwind", Vocal = "Vocals", Lead = "Lead", Pad = "Pad", Pluck = "Pluck",
                        Drums = "Drums", Percussion = "Percussion", Fx = "FX", World = "World";

    public static readonly IReadOnlyList<string> All =
        [Piano, Organ, Bells, Guitar, Bass, Strings, Brass, Woodwind, Vocal, Lead, Pad, Pluck, Drums, Percussion, World, Fx];

    /// <summary>Words that search should also find each family by ("keys" finds pianos).</summary>
    public static string SearchWords(string family) => family switch
    {
        Piano => "piano keys", Bells => "bells mallets", Vocal => "vocals vocal vox voice", Drums => "drums drum",
        Strings => "strings string orchestra", Brass => "brass horns", Woodwind => "woodwind wind", _ => family.ToLowerInvariant(),
    };

    // Plugins and instruments whose name says what they play. Checked first, on whole words (as Simple() writes them,
    // so "VB-3" is "vb 3"). Names that also come before other instruments ("upright bass", "grand" anything) aren't here:
    // the instrument words below name those.
    static readonly (string Words, string Family)[] Plugins =
    [
        ("addictive drums", Drums), ("superior drummer", Drums), ("ezdrummer", Drums), ("battery", Drums), ("stylus", Drums),
        ("drum machine", Drums), ("drum rack", Drums), ("fpc", Drums), ("geist", Drums), ("xo", Drums), ("analog rytm", Drums),
        ("tr 808", Drums), ("tr 909", Drums), ("909", Drums), ("bass drum", Drums), ("cow bell", Drums), ("kick", Drums), ("snare", Drums),
        ("keyscape", Piano), ("pianoteq", Piano), ("ravenscroft", Piano), ("noire", Piano), ("una corda", Piano), ("the giant", Piano),
        ("scarbee", Piano), ("lounge lizard", Piano), ("mark i", Piano), ("fazioli", Piano), ("steinway", Piano),
        ("rhodes", Piano), ("wurlitzer", Piano), ("wurli", Piano), ("clavinet", Piano), ("clav", Piano),
        ("electric piano", Piano), ("e piano", Piano), ("epiano", Piano),
        ("trilian", Bass), ("vb 3", Organ), ("hammond", Organ), ("farfisa", Organ),
    ];

    // Libraries mostly known for one family that hold others too ("Spitfire LABS Soft Piano"): only when no word says.
    static readonly (string Words, string Family)[] Libraries = [("spitfire", Strings), ("bbc so", Strings), ("albion", Strings)];

    // Styles of music whose names hold instrument words: a "Drum and Bass" folder isn't drums, nor "Future Bass" a bass.
    static readonly string[] Styles =
        ["drum and bass", "drum n bass", "drum bass", "drumnbass", "future bass", "bass house", "bass music", "piano house"];

    // Words in names that say what plays. Drum words come before "bass" so "bass drum" is drums; "808" is a bass in names.
    static readonly (string[] Words, string Family)[] Words =
    [
        (["drum", "drums", "hat", "hats", "hihat", "hihats", "hh", "ohh", "chh", "clap", "claps", "perc", "percs", "percussion",
          "cymbal", "crash", "ride", "tom", "toms", "rim", "shaker", "tambourine", "conga", "congas", "bongo", "bongos", "cowbell"], Drums),
        (["piano", "pianos", "keys", "keyboard", "keyboards"], Piano),
        (["organ", "organs"], Organ),
        (["bell", "bells", "celesta", "glock", "glockenspiel", "marimba", "vibes", "vibraphone", "xylophone", "kalimba", "mallet", "mallets", "music box"], Bells),
        (["guitar", "guitars", "gtr", "strat", "telecaster"], Guitar),
        (["bass", "basses", "bassline", "basslines", "sub", "808", "reese"], Bass),
        (["string", "strings", "violin", "violins", "viola", "cello", "cellos", "orchestra", "orchestral", "pizz", "pizzicato", "harp"], Strings),
        (["brass", "horn", "horns", "trumpet", "trumpets", "trombone", "tuba"], Brass),
        (["sax", "saxophone", "flute", "flutes", "clarinet", "oboe", "bassoon", "recorder", "pan flute"], Woodwind),
        (["vox", "vocal", "vocals", "voice", "voices", "choir", "chant"], Vocal),
        (["lead", "leads"], Lead),
        (["pad", "pads", "atmos", "atmosphere"], Pad),
        (["pluck", "plucks"], Pluck),
    ];

    /// <summary>
    /// The instrument, when something says. <paramref name="names"/> are the track name, clip name, file name and the
    /// folders (most specific first: the first that says wins); <paramref name="channel10"/> is a .mid part on MIDI
    /// channel 10; <paramref name="program"/> the General MIDI program a .mid file sets (0–127).
    /// </summary>
    public static InstrumentGuess? Guess(IEnumerable<string> names, bool channel10 = false, int? program = null)
    {
        if (channel10) return new(Drums, "It's on MIDI channel 10, which General MIDI keeps for drums");
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var text = " " + Simple(raw) + " ";
            foreach (var style in StylesPadded)
                if (text.Contains(style, StringComparison.Ordinal)) text = text.Replace(style, " ", StringComparison.Ordinal);
            foreach (var (padded, words, family) in PluginsPadded)
                if (text.Contains(padded, StringComparison.Ordinal))
                    return IsPluginName(words)
                        ? new(family, $"“{raw.Trim()}” is {Article(family)} {Noun(family)} plugin")
                        : new(family, $"The name “{raw.Trim()}” says {words}");
            foreach (var (padded, w, family) in WordsPadded)
                if (text.Contains(padded, StringComparison.Ordinal))
                    return new(family, $"The name “{raw.Trim()}” says {w}");
            foreach (var (padded, words, family) in LibrariesPadded)
                if (text.Contains(padded, StringComparison.Ordinal))
                    return new(family, $"“{raw.Trim()}” is mostly {Article(family)} {Noun(family)} library");
        }
        // Program 0 (Acoustic Grand Piano) is what most programs write when nobody chose a sound, so it says nothing.
        if (program is > 0 and < 128) return new(FamilyOf(program.Value), $"The file asks for General MIDI sound {program + 1}: {GeneralMidi[program.Value]}");
        return null;
    }

    static bool IsPluginName(string words) =>
        words is not ("bass drum" or "kick" or "snare" or "electric piano" or "e piano" or "epiano" or
                      "rhodes" or "wurlitzer" or "wurli" or "clavinet" or "clav" or "hammond" or "909" or "tr 808" or "tr 909" or "cow bell");

    // Each table's words with a space either side, made once: names are checked for every part on every catalogue load.
    static readonly (string Padded, string Words, string Family)[] PluginsPadded = [.. Plugins.Select(x => (Spaced(x.Words), x.Words, x.Family))];
    static readonly (string Padded, string Words, string Family)[] LibrariesPadded = [.. Libraries.Select(x => (Spaced(x.Words), x.Words, x.Family))];
    static readonly (string Padded, string Word, string Family)[] WordsPadded =
        [.. Words.SelectMany(x => x.Words.Select(w => (Spaced(w), w, x.Family)))];
    static readonly string[] StylesPadded = [.. Styles.Select(Spaced)];

    static string Spaced(string words) => " " + words + " ";

    static string Article(string family) => family is Organ or Fx ? "an" : "a";

    /// <summary>The family as a word before "plugin": "drum plugin", "piano plugin".</summary>
    static string Noun(string family) => family switch
    {
        Drums => "drum", Strings => "string", Vocal => "vocal", Bells => "bell", Fx => "effects", _ => family.ToLowerInvariant(),
    };

    /// <summary>Lower case, with anything that isn't a letter or digit as a space, and letters and digits split ("Pad2" → "pad 2").</summary>
    static string Simple(string s)
    {
        var lowered = NotWord().Replace(s.ToLowerInvariant(), " ");
        lowered = LetterDigit().Replace(lowered, "$1 $2");
        return MultipleSpaces().Replace(lowered, " ").Trim();
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")] private static partial Regex NotWord();
    [GeneratedRegex(@"(\p{L})(\p{N})")] private static partial Regex LetterDigit();
    [GeneratedRegex(" {2,}")] private static partial Regex MultipleSpaces();

    /// <summary>The family General MIDI puts a program in (programs counted from 0).</summary>
    public static string FamilyOf(int program) => (program / 8) switch
    {
        0 => Piano, 1 => Bells, 2 => Organ, 3 => Guitar, 4 => Bass, 5 => Strings,
        6 => program is >= 52 and <= 54 ? Vocal : Strings,
        7 => Brass, 8 or 9 => Woodwind, 10 => Lead, 11 => Pad, 12 => Fx, 13 => World, 14 => Percussion, _ => Fx,
    };

    /// <summary>The 128 General MIDI sounds, by program number from 0.</summary>
    public static readonly IReadOnlyList<string> GeneralMidi =
    [
        "Acoustic Grand Piano", "Bright Acoustic Piano", "Electric Grand Piano", "Honky-tonk Piano", "Electric Piano 1", "Electric Piano 2", "Harpsichord", "Clavinet",
        "Celesta", "Glockenspiel", "Music Box", "Vibraphone", "Marimba", "Xylophone", "Tubular Bells", "Dulcimer",
        "Drawbar Organ", "Percussive Organ", "Rock Organ", "Church Organ", "Reed Organ", "Accordion", "Harmonica", "Tango Accordion",
        "Acoustic Guitar (nylon)", "Acoustic Guitar (steel)", "Electric Guitar (jazz)", "Electric Guitar (clean)", "Electric Guitar (muted)", "Overdriven Guitar", "Distortion Guitar", "Guitar Harmonics",
        "Acoustic Bass", "Electric Bass (finger)", "Electric Bass (pick)", "Fretless Bass", "Slap Bass 1", "Slap Bass 2", "Synth Bass 1", "Synth Bass 2",
        "Violin", "Viola", "Cello", "Contrabass", "Tremolo Strings", "Pizzicato Strings", "Orchestral Harp", "Timpani",
        "String Ensemble 1", "String Ensemble 2", "Synth Strings 1", "Synth Strings 2", "Choir Aahs", "Voice Oohs", "Synth Voice", "Orchestra Hit",
        "Trumpet", "Trombone", "Tuba", "Muted Trumpet", "French Horn", "Brass Section", "Synth Brass 1", "Synth Brass 2",
        "Soprano Sax", "Alto Sax", "Tenor Sax", "Baritone Sax", "Oboe", "English Horn", "Bassoon", "Clarinet",
        "Piccolo", "Flute", "Recorder", "Pan Flute", "Blown Bottle", "Shakuhachi", "Whistle", "Ocarina",
        "Lead 1 (square)", "Lead 2 (sawtooth)", "Lead 3 (calliope)", "Lead 4 (chiff)", "Lead 5 (charang)", "Lead 6 (voice)", "Lead 7 (fifths)", "Lead 8 (bass + lead)",
        "Pad 1 (new age)", "Pad 2 (warm)", "Pad 3 (polysynth)", "Pad 4 (choir)", "Pad 5 (bowed)", "Pad 6 (metallic)", "Pad 7 (halo)", "Pad 8 (sweep)",
        "FX 1 (rain)", "FX 2 (soundtrack)", "FX 3 (crystal)", "FX 4 (atmosphere)", "FX 5 (brightness)", "FX 6 (goblins)", "FX 7 (echoes)", "FX 8 (sci-fi)",
        "Sitar", "Banjo", "Shamisen", "Koto", "Kalimba", "Bagpipe", "Fiddle", "Shanai",
        "Tinkle Bell", "Agogo", "Steel Drums", "Woodblock", "Taiko Drum", "Melodic Tom", "Synth Drum", "Reverse Cymbal",
        "Guitar Fret Noise", "Breath Noise", "Seashore", "Bird Tweet", "Telephone Ring", "Helicopter", "Applause", "Gunshot",
    ];
}
