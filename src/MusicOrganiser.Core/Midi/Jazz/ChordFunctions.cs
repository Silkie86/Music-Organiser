namespace MusicOrganiser.Core.Midi.Jazz;

/// <summary>What a chord does in its key.</summary>
/// <param name="Roman">The Roman numeral: "ii7", "V7", "bVII7", "#iv°7", "V7/ii", "subV7".</param>
/// <param name="Label">What it's doing, in words: "ii–V–I", "tritone sub", "passing diminished", "borrowed from minor".</param>
/// <param name="Kind">'T' tonic (home), 'S' subdominant (moving away), 'D' dominant (pulling home), or ' ' when it's colour.</param>
public sealed record ChordFunction(string Roman, string Label, char Kind);

/// <summary>
/// Roman-numeral analysis with a jazz and gospel ear. Each chord is read in the key and against the chord after it, so
/// a dominant that falls a fifth is a V (or a secondary dominant, V7/ii), one that falls a semitone is a tritone sub, a
/// minor seventh before a dominant a fourth up is the ii of a ii–V, a diminished seventh that climbs a semitone is a
/// passing chord, bVII7 to I is the backdoor, and IV to iv is the gospel "amen" colour borrowed from minor.
/// </summary>
public static class ChordFunctions
{
    static readonly string[] Degrees = ["I", "bII", "II", "bIII", "III", "IV", "#IV", "V", "bVI", "VI", "bVII", "VII"];

    public static string RomanOf(ChordSymbol c, int tonic)
    {
        var degree = Degrees[ChordSymbol.Mod(c.Root - tonic)];
        var lower = c.Family is ChordFamily.Minor or ChordFamily.HalfDiminished or ChordFamily.Diminished;
        if (lower) degree = degree.ToLowerInvariant();
        var iv = c.Intervals;
        bool Has(int i) => (iv >> i & 1) != 0;
        var suffix = c.Family switch
        {
            ChordFamily.Dominant => "7",
            ChordFamily.Major => Has(11) ? "maj7" : Has(9) ? "6" : "",
            ChordFamily.Minor => Has(10) ? "7" : Has(11) ? "(maj7)" : Has(9) ? "6" : "",
            ChordFamily.HalfDiminished => "ø7",
            ChordFamily.Diminished => Has(9) ? "°7" : "°",
            ChordFamily.Augmented => "+",
            ChordFamily.Sus => Has(10) ? "7sus" : "sus",
            _ => "5",
        };
        return degree + suffix;
    }

    static bool IsDominant(ChordSymbol c) =>
        c.Family == ChordFamily.Dominant || (c.Family == ChordFamily.Sus && c.Has(10)) || (c.Family == ChordFamily.Augmented && c.Has(10));

    static bool IsMinorish(ChordSymbol c) => c.Family is ChordFamily.Minor or ChordFamily.HalfDiminished;

    /// <summary>
    /// What each chord does, in a key (<paramref name="tonic"/> 0 = C). The chords are read as a loop when
    /// <paramref name="loops"/>, so the last chord leads back to the first.
    /// </summary>
    public static IReadOnlyList<ChordFunction> Analyse(IReadOnlyList<ChordSymbol> chords, int tonic, bool minor, bool loops = true)
    {
        var list = new ChordFunction[chords.Count];
        for (var i = 0; i < chords.Count; i++)
        {
            var c = chords[i];
            ChordSymbol? next = i + 1 < chords.Count ? chords[i + 1] : loops && chords.Count > 1 ? chords[0] : null;
            ChordSymbol? after = i + 2 < chords.Count ? chords[i + 2] : loops && chords.Count > 2 ? chords[(i + 2) % chords.Count] : null;
            list[i] = One(c, next, after, tonic, minor);
        }
        return list;
    }

    static ChordFunction One(ChordSymbol c, ChordSymbol? next, ChordSymbol? after, int tonic, bool minor)
    {
        var degree = ChordSymbol.Mod(c.Root - tonic);
        var roman = RomanOf(c, tonic);
        var step = next is { } n ? ChordSymbol.Mod(n.Root - c.Root) : -1;

        if (IsDominant(c) && next is { } target)
        {
            var targetRoman = RomanOf(target, tonic);
            var home = ChordSymbol.Mod(target.Root - tonic) == 0;
            var sus = c.Family == ChordFamily.Sus ? " (sus, the gospel IV/V)" : "";
            if (step == 5 && degree == 0 && c.Family == ChordFamily.Dominant) return new("I7", "I7 to IV, the gospel and blues move", 'D');
            if (step == 5)
                return home
                    ? new(roman, (degree == 7 ? "dominant, resolves home" : "dominant") + sus, 'D')
                    : new($"V7/{Base(targetRoman)}", "secondary dominant: pulls to " + target.Name + sus, 'D');
            if (step == 11)
                return new(home ? "subV7" : $"subV7/{Base(targetRoman)}", "tritone sub: slides down a semitone to " + target.Name, 'D');
            if (degree == 10 && home) return new(roman, "backdoor dominant: bVII7 to I", 'D');
            if (step == 0) return new(roman, "dominant held", 'D');
        }
        if (c.Family == ChordFamily.Diminished && next is { } up)
        {
            if (step == 1) return new(roman, "passing diminished: climbs a semitone to " + up.Name + " (a rootless 7b9)", 'D');
            if (step == 11) return new(roman, "passing diminished, coming down to " + up.Name, ' ');
            if (step == 0) return new(roman, "neighbour diminished, around " + up.Name, ' ');
        }
        if (IsMinorish(c) && next is { } dom && IsDominant(dom) && step == 5)
        {
            var resolves = after is { } a && ChordSymbol.Mod(a.Root - dom.Root) is 5 or 11;
            var homeAfter = after is { } h && ChordSymbol.Mod(h.Root - tonic) == 0;
            return new(roman, resolves ? (homeAfter ? "ii–V–I" : "ii–V into " + after!.Value.Name) : "ii of a ii–V", 'S');
        }
        // Side-slip: the same kind of chord a semitone above where it's going.
        if (next is { } slip && step == 11 && slip.Family == c.Family && !IsDominant(c))
            return new(roman, "side-slip: the same chord a semitone up, sliding into " + slip.Name, ' ');

        var family = c.Family;
        if (!minor)
        {
            switch (degree)
            {
                case 0 when family is ChordFamily.Major or ChordFamily.Sus or ChordFamily.Power: return new(roman, "home (tonic)", 'T');
                case 0 when family is ChordFamily.Minor: return new(roman, "tonic minor, borrowed", 'T');
                case 2 when IsMinorish(c): return new(roman, "ii, moving away", 'S');
                case 4 when IsMinorish(c): return new(roman, "iii, stands in for home", 'T');
                case 5 when family is ChordFamily.Major: return new(roman, "IV, moving away", 'S');
                case 5 when family is ChordFamily.Minor: return new(roman, "iv borrowed from minor (the amen colour)", 'S');
                case 7 when family is ChordFamily.Major or ChordFamily.Sus: return new(roman, "V", 'D');
                case 9 when IsMinorish(c): return new(roman, "vi, the relative minor", 'T');
                case 11 when family is ChordFamily.HalfDiminished or ChordFamily.Diminished: return new(roman, "leading-tone chord", 'D');
                case 3 or 8 or 10 when family is ChordFamily.Major: return new(roman, "borrowed from minor", 'S');
                case 1 when family is ChordFamily.Major: return new(roman, "Neapolitan", 'S');
            }
        }
        else
        {
            switch (degree)
            {
                case 0 when family is ChordFamily.Minor or ChordFamily.Power: return new(roman, "home (tonic)", 'T');
                case 0 when family is ChordFamily.Major: return new(roman, "Picardy third: major home", 'T');
                case 2 when family is ChordFamily.HalfDiminished: return new(roman, "iiø, moving away", 'S');
                case 3 when family is ChordFamily.Major: return new(roman, "bIII, the relative major", 'T');
                case 5 when IsMinorish(c): return new(roman, "iv, moving away", 'S');
                case 5 when family is ChordFamily.Major: return new(roman, "IV, the Dorian colour", 'S');
                case 7 when family is ChordFamily.Minor: return new(roman, "minor v, modal", 'D');
                case 8 when family is ChordFamily.Major: return new(roman, "bVI, moving away", 'S');
                case 10 when family is ChordFamily.Major: return new(roman, "bVII, the subtonic", 'D');
                case 1 when family is ChordFamily.Major: return new(roman, "Neapolitan", 'S');
            }
        }
        return new(roman, IsDominant(c) ? "dominant colour" : "colour chord", ' ');
    }

    static string Base(string roman)
    {
        // "ii7" → "ii", "IVmaj7" → "IV".
        var end = 0;
        while (end < roman.Length && (roman[end] is 'b' or '#' || "IViv".Contains(roman[end]))) end++;
        return end == 0 ? roman : roman[..end];
    }
}

/// <summary>
/// How alike two chords are to a jazz player, beyond sharing notes: Cmaj7 and C6/9 are the same chord, G7 and Db7
/// swap for each other (the tritone sub: same third and seventh), the four diminished sevenths a minor third apart
/// are one chord, Bdim7 is a G7b9 without its root, and C stands in for Am7.
/// </summary>
public static class ChordKinship
{
    static int Class(ChordSymbol c) => c.Family switch
    {
        ChordFamily.Major or ChordFamily.Augmented when !c.Has(10) => 0,
        ChordFamily.Minor => 1,
        ChordFamily.Dominant => 2,
        ChordFamily.Augmented => 2,
        ChordFamily.Sus => c.Has(10) ? 2 : 0,
        ChordFamily.HalfDiminished or ChordFamily.Diminished => 3,
        _ => 4,
    };

    /// <summary>0 to 1.</summary>
    public static double Similarity(ChordSymbol a, ChordSymbol b)
    {
        int ma = a.Mask | 1 << a.Root, mb = b.Mask | 1 << b.Root;
        var union = ChordSymbol.Count(ma | mb);
        var notes = union == 0 ? 0 : (double)ChordSymbol.Count(ma & mb) / union + (a.Root == b.Root ? 0.2 : 0);
        double kin = 0;
        var gap = ChordSymbol.Mod(b.Root - a.Root);
        int ca = Class(a), cb = Class(b);
        if (gap == 0 && ca == cb) kin = 0.95;
        else if (gap == 0 && ((ca == 2 && b.Family == ChordFamily.Sus) || (cb == 2 && a.Family == ChordFamily.Sus))) kin = 0.8;
        else if (ca == 2 && cb == 2 && gap == 6) kin = 0.8;                                         // tritone sub
        else if (a.Family == ChordFamily.Diminished && b.Family == ChordFamily.Diminished && a.Has(9) && b.Has(9) && gap % 3 == 0) kin = 0.95;
        else if (DimOfDominant(a, b) || DimOfDominant(b, a)) kin = 0.75;                           // rootless 7b9
        else if (ca == 0 && cb == 1 && gap == 9 || ca == 1 && cb == 0 && gap == 3) kin = 0.7;      // relative major and minor
        else if (ca == 3 && cb == 1 && gap == 3 || ca == 1 && cb == 3 && gap == 9) kin = 0.7;      // Bm7b5 ≈ Dm6
        if (ChordSymbol.Count(a.GuideTones) >= 2 && a.GuideTones == b.GuideTones) kin = Math.Max(kin, 0.8);
        return Math.Clamp(Math.Max(notes, kin), 0, 1);
    }

    /// <summary>A diminished seventh built on a dominant's third: the dominant with a b9 and no root.</summary>
    static bool DimOfDominant(ChordSymbol dim, ChordSymbol dom) =>
        dim.Family == ChordFamily.Diminished && dim.Has(9) && Class(dom) == 2 && ChordSymbol.Mod(dim.Root - dom.Root) % 3 == 1;
}
