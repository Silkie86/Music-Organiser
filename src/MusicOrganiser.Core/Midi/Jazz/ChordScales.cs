namespace MusicOrganiser.Core.Midi.Jazz;

/// <summary>
/// Which ears to judge harmony with. Jazz and gospel hear a ninth, a thirteenth, an altered note on a dominant and a
/// blue note as colour; classical hears the chord's own notes as the safe ones and a dissonance on a strong beat as a
/// problem that must resolve.
/// </summary>
public enum HarmonyStyle { JazzGospel, Classical }

/// <summary>
/// How well each note sits on a chord, by chord-scale theory (the Berklee and Mark Levine way of thinking): on Cmaj7,
/// D, F# and A are tensions that colour it and F is the "avoid" note; on G7 almost anything goes except C and F#; on
/// Dm7 the 9, 11 and 13 all sound good. Gospel and blues add the blue notes: a flat third or flat seventh over a
/// major chord. A note's value runs from 1 (a chord note) down to below 0 (a note that fights the chord).
/// </summary>
public static class ChordScales
{
    /// <summary>How a note this many semitones above the root sits on a chord of this family (not counting its own notes).</summary>
    static double Colour(ChordFamily family, int i, int iv, HarmonyStyle style)
    {
        var jazz = style == HarmonyStyle.JazzGospel;
        double t = jazz ? 0.8 : 0.45;   // an available tension
        bool b7 = (iv >> 10 & 1) != 0, M7 = (iv >> 11 & 1) != 0;
        return family switch
        {
            ChordFamily.Major => i switch
            {
                2 => t, 9 => jazz ? 0.8 : 0.5, 6 => jazz ? 0.65 : 0.1,       // 9, 6/13, #11 (Lydian)
                11 => b7 ? -0.3 : jazz ? 0.7 : 0.4,                           // maj7 over a triad
                10 => M7 ? -0.4 : jazz ? 0.45 : 0.05,                         // a bluesy b7 over a major triad
                3 => jazz ? 0.3 : -0.2,                                       // the blue third, gospel's favourite rub
                5 => jazz ? 0.1 : 0.15,                                       // the avoid note
                8 => -0.1,
                _ => -0.4,                                                    // b9, b5
            },
            ChordFamily.Augmented => i switch { 2 or 6 => t, 10 => 0.55, 11 => 0.45, 7 => -0.2, _ => -0.3 },
            ChordFamily.Minor => i switch
            {
                2 or 5 => t, 10 => M7 ? 0.2 : 0.75, 9 => jazz ? 0.6 : 0.3,   // 9, 11, b7, 6 (Dorian)
                8 => 0.4,                                                     // b6 (Aeolian)
                11 => M7 ? 1 : 0.3,
                6 => jazz ? 0.3 : -0.2,                                       // the blue b5
                _ => -0.45,                                                   // b9, a major third
            },
            ChordFamily.Dominant => i switch
            {
                2 or 9 => jazz ? 0.85 : 0.5,                                  // 9, 13
                1 or 3 or 6 or 8 => jazz ? 0.6 : 0.0,                         // the altered notes
                5 => jazz ? 0.1 : 0.15,                                       // natural 11 against the third
                _ => -0.5,                                                    // major 7
            },
            ChordFamily.HalfDiminished => i switch { 5 or 8 => t, 2 => jazz ? 0.6 : 0.2, 1 => 0.3, 9 => 0.2, _ => -0.4 },
            ChordFamily.Diminished => i switch { 2 or 5 or 8 or 11 => jazz ? 0.75 : 0.3, 9 => 0.9, _ => -0.3 },
            ChordFamily.Sus => i switch
            {
                2 or 9 or 10 => t, 5 => 0.8, 4 => jazz ? 0.25 : 0.05,         // the third the sus wants to fall to
                1 => jazz ? 0.4 : -0.2, 3 => 0.25, 8 => 0.2, _ => -0.3,
            },
            _ => i switch { 2 or 10 => 0.65, 3 or 4 or 5 or 9 => 0.55, _ => -0.2 },   // a power chord says little
        };
    }

    /// <summary>How well pitch class <paramref name="pc"/> sits on the chord, from 1 (one of its notes) to about −0.5.</summary>
    public static double Sit(ChordSymbol chord, int pc, HarmonyStyle style = HarmonyStyle.JazzGospel)
    {
        var i = ChordSymbol.Mod(pc - chord.Root);
        if (i == 0 || (chord.Mask >> ChordSymbol.Mod(pc) & 1) != 0)
            // Its own notes. An avoid note that's in the chord (an add11) still sounds, but less sweetly.
            return ChordSpeller.Role(chord.Intervals, chord.Family, i) == ToneRole.Avoid ? 0.6 : 1;
        return Colour(chord.Family, i, chord.Intervals, style);
    }

    /// <summary>The 12 values at once, by pitch class.</summary>
    public static float[] Sits(ChordSymbol chord, HarmonyStyle style = HarmonyStyle.JazzGospel)
    {
        var s = new float[12];
        for (var p = 0; p < 12; p++) s[p] = (float)Sit(chord, p, style);
        return s;
    }

    /// <summary>
    /// The notes of the scale that goes with the chord (root first), for writing lines: Mixolydian for a dominant
    /// (altered when it carries b9 or #9), Dorian for a minor seventh, Ionian or Lydian for major, Locrian #2 for
    /// half-diminished, whole-half for diminished.
    /// </summary>
    public static int[] Scale(ChordSymbol chord)
    {
        var iv = chord.Intervals;
        bool Has(int i) => (iv >> i & 1) != 0;
        int[] steps = chord.Family switch
        {
            ChordFamily.Major => Has(6) ? [0, 2, 4, 6, 7, 9, 11] : Has(10) ? [0, 2, 4, 5, 7, 9, 10] : [0, 2, 4, 5, 7, 9, 11],
            ChordFamily.Minor => Has(11) ? [0, 2, 3, 5, 7, 9, 11] : Has(8) ? [0, 2, 3, 5, 7, 8, 10] : [0, 2, 3, 5, 7, 9, 10],
            ChordFamily.Dominant => Has(1) || Has(3) || Has(8) ? [0, 1, 3, 4, 6, 8, 10] : Has(6) ? [0, 2, 4, 6, 7, 9, 10] : [0, 2, 4, 5, 7, 9, 10],
            ChordFamily.HalfDiminished => [0, 2, 3, 5, 6, 8, 10],
            ChordFamily.Diminished => [0, 2, 3, 5, 6, 8, 9, 11],
            ChordFamily.Augmented => [0, 2, 4, 6, 8, 10],
            ChordFamily.Sus => Has(1) ? [0, 1, 3, 5, 7, 8, 10] : [0, 2, 4, 5, 7, 9, 10],
            _ => [0, 2, 3, 5, 7, 9, 10],
        };
        return steps.Select(s => ChordSymbol.Mod(chord.Root + s)).ToArray();
    }
}
