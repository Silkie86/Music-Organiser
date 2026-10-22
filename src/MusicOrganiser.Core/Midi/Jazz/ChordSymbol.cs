using System.Numerics;

namespace MusicOrganiser.Core.Midi.Jazz;

/// <summary>What sort of chord it is, which decides how it behaves: what it resolves to and which notes sit on it.</summary>
public enum ChordFamily { Major, Minor, Dominant, HalfDiminished, Diminished, Augmented, Sus, Power }

/// <summary>
/// A chord as a jazz or gospel player would name it: its root, every note in it (a 12-bit set, bit 0 = C) and the bass
/// note under it, so "G13(b9)", "Bbmaj9/D", "F#m7b5", "C#dim7" and "Ab7alt" all come out right. Made by
/// <see cref="ChordSpeller"/> from notes, or by <see cref="Parse"/> from a typed name.
/// </summary>
public readonly record struct ChordSymbol(int Root, int Mask, int Bass, ChordFamily Family)
{
    /// <summary>Jazz charts name black notes as flats, except F#.</summary>
    public static readonly string[] Names = ["C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];

    /// <summary>The notes from the root up: bit 0 the root, bit 4 a major third, bit 10 a flat seventh.</summary>
    public int Intervals => Rotate(Mask, -Root);

    public bool Has(int interval) => (Intervals >> Mod(interval) & 1) != 0;

    /// <summary>The chord's quality written after the root: "maj9", "m7b5", "13(#11)", "7alt", "9sus4".</summary>
    public string Quality => ChordSpeller.QualityOf(Intervals, Family);

    /// <summary>"Dm9", "G13(b9)", "Cmaj7/E".</summary>
    public string Name => (Family == ChordFamily.Diminished ? Sharps : Names)[Root] + Quality + (Bass != Root ? "/" + Names[Bass] : "");

    /// <summary>The root as <see cref="Name"/> spells it, and the bass under a slash ("" when the root is in the bass).</summary>
    public string RootName => (Family == ChordFamily.Diminished ? Sharps : Names)[Root];
    public string BassName => Bass != Root ? Names[Bass] : "";

    /// <summary>
    /// The quality the way a lead sheet writes it when room is short (Solomon, 2026-10-22: chord names cut off on a crowded
    /// chord lane): Δ for maj, ° for dim, ø for m7b5, sus for sus4, and no bracketed tensions. "maj7" is "Δ7", "13(b9)" is
    /// "13", "dim7" is "°7".
    /// </summary>
    public static string ShortQuality(string quality)
    {
        var q = System.Text.RegularExpressions.Regex.Replace(quality, @"\([^)]*\)", "");
        q = q.Replace("m7b5", "ø").Replace("m9b5", "ø9").Replace("m11b5", "ø11");
        q = q.Replace("dim", "°").Replace("maj", "Δ").Replace("sus4", "sus");
        return q;
    }

    /// <summary>
    /// The least of a quality that still says what kind of chord it is, for when even the shorthand won't fit: "m" for a
    /// minor chord, ° for diminished, ø for half-diminished, + for augmented, and nothing for major and dominant ones.
    /// </summary>
    public static string LeastQuality(string quality)
    {
        var q = ShortQuality(quality);
        if (q.StartsWith('m')) return "m";
        return q.Length > 0 && q[0] is '°' or 'ø' or '+' ? q[..1] : "";
    }

    /// <summary>A diminished chord on a black note is usually a passing chord climbing to the next, so it's named sharp: C#dim7.</summary>
    static readonly string[] Sharps = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    static readonly string[] Flats = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];

    /// <summary>
    /// The name spelled the way the key is written: with sharps in the sharp keys (G, D, A, E and B major and their
    /// relative minors, so C#m in A major, not Dbm), with flats in the flat keys (Gb in Db major), and as <see cref="Name"/>
    /// in C major, A minor or with no key. A diminished chord stays sharp, as it climbs to the chord after it.
    /// </summary>
    public string NameIn(Sounds.KeyEstimate? key)
    {
        if (key is null) return Name;
        var names = Mod(key.Minor ? key.Tonic + 3 : key.Tonic) switch
        {
            7 or 2 or 9 or 4 or 11 => Sharps,
            0 => Names,
            _ => Flats,
        };
        return Spelled(Family == ChordFamily.Diminished ? Sharps : names, names);
    }

    /// <summary>Every way the name is written, for searching: "C#m7" and "Dbm7".</summary>
    public IEnumerable<string> Spellings => new[] { Name, Spelled(Sharps, Sharps), Spelled(Flats, Flats) }.Distinct();

    string Spelled(string[] roots, string[] basses) => roots[Root] + Quality + (Bass != Root ? "/" + basses[Bass] : "");

    public override string ToString() => Name;

    /// <summary>The third and seventh (or sixth): the two notes that say what a chord is, and what jazz voice-leading moves.</summary>
    public int GuideTones
    {
        get
        {
            var g = 0;
            foreach (var i in new[] { 3, 4, 5, 9, 10, 11 })
                if (Has(i) && ChordSpeller.Role(Intervals, Family, i) is ToneRole.Third or ToneRole.Seventh) g |= 1 << Mod(Root + i);
            return g;
        }
    }

    /// <summary>The chord's own notes (root, third, fifth, seventh or sixth) as pitch classes, without its tensions.</summary>
    public int CoreTones
    {
        get
        {
            var core = 0;
            for (var i = 0; i < 12; i++)
                if (Has(i) && ChordSpeller.Role(Intervals, Family, i) is ToneRole.Root or ToneRole.Third or ToneRole.Fifth or ToneRole.Seventh)
                    core |= 1 << Mod(Root + i);
            return core | 1 << Root;
        }
    }

    public ChordSymbol Transposed(int semitones) =>
        new(Mod(Root + semitones), Rotate(Mask, semitones), Mod(Bass + semitones), Family);

    /// <summary>The same chord with its bass on the root (for a part that has no bassline of its own).</summary>
    public ChordSymbol RootPosition => this with { Bass = Root, Mask = Mask | 1 << Root };

    public static int Mod(int n) => ((n % 12) + 12) % 12;

    public static int Rotate(int mask, int semitones)
    {
        var s = Mod(semitones);
        return s == 0 ? mask & 0xFFF : ((mask << s) | (mask >> (12 - s))) & 0xFFF;
    }

    public static int Count(int mask) => BitOperations.PopCount((uint)(mask & 0xFFF));

    /// <summary>A note name's pitch class: "C", "Db", "C#", "Bb", "E#"; -1 when it isn't one.</summary>
    public static int ParseNote(string text, out int used)
    {
        used = 0;
        if (text.Length == 0) return -1;
        var letter = char.ToUpperInvariant(text[0]) switch
        {
            'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11, _ => -1,
        };
        if (letter < 0) return -1;
        used = 1;
        while (used < text.Length && text[used] is '#' or 'b' or '♯' or '♭')
        {
            // "Bb" is B flat, but "Bbm" is B flat minor and "Cb5" C flat... a 'b' always flattens here.
            letter += text[used] is '#' or '♯' ? 1 : -1;
            used++;
            if (used >= 3) break;
        }
        return Mod(letter);
    }

    /// <summary>
    /// A chord from its name as typed: "Dm9", "G7alt", "C6/9", "F#m7b5", "Bbmaj7#11", "Ebdim7", "Ab13sus", "C/E",
    /// "Dø", "C°7", "CΔ9", "C-7". Null when it can't be read.
    /// </summary>
    public static ChordSymbol? Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        var root = ParseNote(text, out var used);
        if (root < 0) return null;
        var rest = text[used..];
        var bass = root;
        var slash = rest.LastIndexOf('/');
        // "6/9" is a chord, not a bass note.
        if (slash >= 0 && slash + 1 < rest.Length && !char.IsDigit(rest[slash + 1]))
        {
            var b = ParseNote(rest[(slash + 1)..], out var bassUsed);
            if (b < 0 || bassUsed != rest.Length - slash - 1) return null;
            bass = b;
            rest = rest[..slash];
        }
        if (ChordSpeller.IntervalsOf(rest) is not { } intervals) return null;
        var family = ChordSpeller.FamilyOf(intervals);
        return new ChordSymbol(root, Rotate(intervals, root) | 1 << bass, bass, family);
    }
}

/// <summary>What one note does in a chord.</summary>
public enum ToneRole { None, Root, Third, Fifth, Seventh, Tension, Avoid }
