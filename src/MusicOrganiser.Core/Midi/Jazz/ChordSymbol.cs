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

    /// <summary>A diminished chord on a black note is usually a passing chord climbing to the next, so it's named sharp: C#dim7.</summary>
    static readonly string[] Sharps = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public override string ToString() => Name;

    /// <summary>The third and seventh (or sixth): the two notes that say what a chord is, and what jazz voice-leading moves.</summary>
    public int GuideTones
    {
        get
        {
            var g = 0;
            foreach (var i in new[] { 3, 4, 5, 9, 10, 11 })
                if (ChordSpeller.Role(Intervals, Family, i) is ToneRole.Third or ToneRole.Seventh) g |= 1 << Mod(Root + i);
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
