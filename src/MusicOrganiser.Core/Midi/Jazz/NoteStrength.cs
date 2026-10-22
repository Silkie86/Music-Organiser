namespace MusicOrganiser.Core.Midi.Jazz;

/// <summary>How strongly a note fits the chord under it, weakest first.</summary>
public enum FitStrength { Fights, Weak, Colour, Strong, Strongest }

/// <summary>A chord's stretch of a piano roll (ticks) with how strongly each of the 12 notes fits it, by pitch class.</summary>
public sealed record FitSpan(long Start, long Length, FitStrength[] Strengths);

/// <summary>
/// How strongly each note fits the chord under it, by the part's job (Solomon, 2026-10-20: "a system to visually tell
/// which notes are the strongest fit so when it comes to bass the root and the 5th should be the strongest fit then 3rd
/// ect"). A bassline leans on the root (and a slash chord's bass note) and the 5th, then the 3rd, then the chord's 7th or
/// 6th; any other part on the root, 3rd and 5th, then the chord's 7th or 6th and its other notes, then the tensions that
/// colour it (<see cref="ChordScales"/>). Whatever's left is weak, or fights the chord.
/// </summary>
public static class NoteStrength
{
    public static FitStrength Of(ChordSymbol chord, int pc, string role, HarmonyStyle style = HarmonyStyle.JazzGospel)
    {
        pc = ChordSymbol.Mod(pc);
        var i = ChordSymbol.Mod(pc - chord.Root);
        var bass = role == MidiRoles.Bass;
        if (i == 0 || i == FifthOf(chord) || bass && pc == chord.Bass) return FitStrength.Strongest;
        if (i == ThirdOf(chord)) return bass ? FitStrength.Strong : FitStrength.Strongest;
        if (IsSeventh(chord, i)) return bass ? FitStrength.Colour : FitStrength.Strong;
        if (!bass && chord.Has(i)) return FitStrength.Strong;   // its other notes: the 9 of a Cmaj9
        var sit = ChordScales.Sit(chord, pc, style);
        return sit >= 0.6 ? bass ? FitStrength.Weak : FitStrength.Colour : sit >= 0.2 ? FitStrength.Weak : FitStrength.Fights;
    }

    /// <summary>The 12 at once, by pitch class.</summary>
    public static FitStrength[] All(ChordSymbol chord, string role, HarmonyStyle style = HarmonyStyle.JazzGospel)
    {
        var all = new FitStrength[12];
        for (var pc = 0; pc < 12; pc++) all[pc] = Of(chord, pc, role, style);
        return all;
    }

    /// <summary>How far above the root its 5th is: ♭5 on diminished chords, ♯5 on augmented ones, none on an altered dominant.</summary>
    static int? FifthOf(ChordSymbol chord) => chord.Family switch
    {
        ChordFamily.Diminished or ChordFamily.HalfDiminished => 6,
        ChordFamily.Augmented => 8,
        ChordFamily.Dominant when !chord.Has(7) && (chord.Has(6) || chord.Has(8)) => null,
        _ => 7,
    };

    /// <summary>How far above the root its 3rd is, or the note a sus chord holds in its place.</summary>
    static int? ThirdOf(ChordSymbol chord) => chord.Family switch
    {
        ChordFamily.Major or ChordFamily.Dominant or ChordFamily.Augmented => 4,
        ChordFamily.Minor or ChordFamily.Diminished or ChordFamily.HalfDiminished => 3,
        ChordFamily.Sus => chord.Has(5) ? 5 : chord.Has(2) ? 2 : null,
        _ => null,
    };

    /// <summary>Its 7th, or the 6th of a sixth chord (a 13 on a seventh chord is a tension, not its 6th).</summary>
    static bool IsSeventh(ChordSymbol chord, int i) => i switch
    {
        10 or 11 => chord.Has(i),
        9 => chord.Has(9) && (chord.Family == ChordFamily.Diminished || !chord.Has(10) && !chord.Has(11)),
        _ => false,
    };
}
