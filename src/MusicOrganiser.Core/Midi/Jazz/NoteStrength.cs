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
        var has = chord.Has(i);
        // What the note is in the chord, as the chord speller names it, so a 5th is a 5th the same way everywhere: the
        // ♭5 of C7♭5 and of Cm7♭5 alike.
        var tone = ChordSpeller.Role(chord.Intervals, chord.Family, i);
        // The 5th is the chord's even when the voicing leaves it out, unless an altered one stands in its place.
        var fifth = tone == ToneRole.Fifth && (has || i == 7 && !ChordScales.AlteredFifth(chord));
        if (tone == ToneRole.Root || fifth || bass && pc == chord.Bass) return FitStrength.Strongest;
        // So is the 3rd, or a sus chord's note in its place when it's there.
        if (tone == ToneRole.Third && (has || chord.Family != ChordFamily.Sus)) return bass ? FitStrength.Strong : FitStrength.Strongest;
        // Its 7th, or the 6th of a sixth chord.
        if (tone == ToneRole.Seventh && has) return bass ? FitStrength.Colour : FitStrength.Strong;
        if (!bass && has) return FitStrength.Strong;   // its other notes: the 9 of a Cmaj9
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

    /// <summary>
    /// The colour the piano roll shows a note in: <see cref="Of"/>, kept in step with what the note is called over the chord
    /// (<see cref="ChordScales.Degree"/>) and the legend under it (Solomon, 2026-10-22: "colour doesnt match the what degree
    /// the note says"). Away from the bass, a note that fits and is named 9, 11 or 13 is violet even when the chord has it
    /// (the 9 of a Cmaj9), and one named 7 or 6 is blue even over a plain triad. How well it fits for scoring stays <see cref="Of"/>.
    /// </summary>
    public static FitStrength Shown(ChordSymbol chord, int pc, string role, HarmonyStyle style = HarmonyStyle.JazzGospel)
    {
        var strength = Of(chord, pc, role, style);
        if (role == MidiRoles.Bass || strength is not (FitStrength.Strong or FitStrength.Colour)) return strength;
        return ChordScales.Degree(chord, pc) switch
        {
            "7" or "♭7" or "6" or "°7" => FitStrength.Strong,
            "1" or "3" or "♭3" or "5" or "♭5" or "♯5" or "4" or "2" => strength,
            _ => FitStrength.Colour,
        };
    }

    /// <summary>As <see cref="Shown"/>, the 12 at once.</summary>
    public static FitStrength[] AllShown(ChordSymbol chord, string role, HarmonyStyle style = HarmonyStyle.JazzGospel)
    {
        var all = new FitStrength[12];
        for (var pc = 0; pc < 12; pc++) all[pc] = Shown(chord, pc, role, style);
        return all;
    }
}
