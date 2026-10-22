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
}
