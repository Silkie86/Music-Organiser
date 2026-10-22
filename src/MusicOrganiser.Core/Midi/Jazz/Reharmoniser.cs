namespace MusicOrganiser.Core.Midi.Jazz;

/// <summary>One reharmonisation idea: what it's called, why it works, and the whole chord lane with it done.</summary>
/// <param name="Bar">The bar it changes, from 1, for showing where.</param>
public sealed record Reharm(string Title, string Why, string Before, string After, int Bar, IReadOnlyList<TimedChord> Lane)
{
    /// <summary>Chords for the whole idea where it had none (<see cref="Harmoniser"/>): kept as a chord part the whole way.</summary>
    public bool Whole { get; init; }
}

/// <summary>
/// Reharmonisation ideas for a chord lane, the moves jazz and gospel players reach for: the tritone sub, turning a
/// dominant into a ii–V, a secondary dominant or a side-slip into a chord, a passing diminished between two chords a
/// step apart, the backdoor ii–V, the minor iv (the amen colour), the sus-then-resolve gospel dominant, the gospel
/// walk-up IV – #iv°7 – I/5, a I – VI7 – ii – V turnaround at the end, and richer extensions. Each comes as a whole
/// new lane, so it can be heard and kept or dropped.
/// </summary>
public static class Reharmoniser
{
    const int Bar = ChordLane.Bar, Beat = Bar / 4;

    static ChordSymbol Make(int root, string quality, int? bass = null)
    {
        var name = ChordSymbol.Names[ChordSymbol.Mod(root)] + quality + (bass is { } b ? "/" + ChordSymbol.Names[ChordSymbol.Mod(b)] : "");
        return ChordSymbol.Parse(name) ?? throw new InvalidOperationException("Unreadable chord " + name);
    }

    static bool IsDominant(ChordSymbol c) => c.Family == ChordFamily.Dominant;

    /// <summary>The lane with chord <paramref name="i"/> replaced by these chords, each taking its share of its time.</summary>
    static List<TimedChord> Replace(IReadOnlyList<TimedChord> lane, int i, params (double Share, ChordSymbol Chord)[] with)
    {
        var list = lane.ToList();
        var span = lane[i];
        var parts = new List<TimedChord>();
        long at = span.Start;
        for (var k = 0; k < with.Length; k++)
        {
            var len = k == with.Length - 1 ? span.End - at : (long)Math.Round(span.Length * with[k].Share / (Beat / 2.0)) * (Beat / 2);
            if (len <= 0) continue;
            parts.Add(new TimedChord(at, len, with[k].Chord));
            at += len;
        }
        list.RemoveAt(i);
        list.InsertRange(i, parts);
        return list;
    }

    /// <summary>The ideas for this lane in this key, the likeliest first, at most <paramref name="most"/>.</summary>
    public static IReadOnlyList<Reharm> Suggest(IReadOnlyList<TimedChord> lane, int tonic, bool minor, int most = 16) =>
        // One of each kind of idea for each place, the likeliest kinds first, a few of each.
        All(lane, tonic, minor).GroupBy(r => r.Title.Split(' ')[0]).SelectMany(g => g.Take(3)).Take(most).ToList();

    /// <summary>Every idea for this lane in this key, chord by chord: the Reharm tab shows the ones for the chord clicked.</summary>
    public static IReadOnlyList<Reharm> All(IReadOnlyList<TimedChord> lane, int tonic, bool minor)
    {
        var ideas = new List<Reharm>();
        if (lane.Count == 0) return ideas;
        int BarOf(TimedChord c) => (int)(c.Start / Bar) + 1;
        void Add(string title, string why, int i, IReadOnlyList<TimedChord> result, string? before = null)
        {
            var after = string.Join(" ", result.Where(c => c.Start < lane[i].End && c.End > lane[i].Start).Select(c => c.Chord.Name));
            ideas.Add(new Reharm(title, why, before ?? lane[i].Chord.Name, after, BarOf(lane[i]), result));
        }

        for (var i = 0; i < lane.Count; i++)
        {
            var c = lane[i].Chord;
            var next = lane[(i + 1) % lane.Count].Chord;
            var hasNext = lane.Count > 1;
            var step = hasNext ? ChordSymbol.Mod(next.Root - c.Root) : -1;
            var degree = ChordSymbol.Mod(c.Root - tonic);
            var long1 = lane[i].Length >= Bar;

            if (IsDominant(c) && step == 5)
            {
                var sub = Make(c.Root + 6, "9");
                Add("Tritone sub", $"{sub.Name} shares {c.Name}'s third and seventh, so it pulls the same way, and the bass slides down a semitone into {next.Name}.",
                    i, Replace(lane, i, (1, sub)));
            }
            if (IsDominant(c) && long1 && !(i > 0 && lane[i - 1].Chord.Family is ChordFamily.Minor or ChordFamily.HalfDiminished &&
                                             ChordSymbol.Mod(c.Root - lane[i - 1].Chord.Root) == 5))
            {
                var resolvesMinor = hasNext && step == 5 && next.Family is ChordFamily.Minor;
                var two = Make(c.Root + 7, resolvesMinor ? "m7b5" : "m9");
                var five = resolvesMinor ? Make(c.Root, "7(b9)") : c;
                Add("Make it a ii–V", $"Put its ii, {two.Name}, in front: half the time on each. The bass falls a fourth into {c.Name}, the heart of jazz harmony.",
                    i, Replace(lane, i, (0.5, two), (0.5, five)));
            }
            if (IsDominant(c) && long1)
                Add("Sus, then resolve (gospel)", $"Hold {Make(c.Root, "9sus4").Name} (the IV/V sound), then let it fall to {Make(c.Root, "13(b9)").Name} before the change.",
                    i, Replace(lane, i, (0.5, Make(c.Root, "9sus4")), (0.5, Make(c.Root, "13(b9)"))));
            if (IsDominant(c) && long1 && degree == 7 && hasNext && ChordSymbol.Mod(next.Root - tonic) == 0)
            {
                var iv = Make(tonic + 5, "m7");
                var bVII = Make(tonic + 10, "9");
                Add("Backdoor ii–V", $"{iv.Name} to {bVII.Name} comes home to {next.Name} from below instead: softer than the V, a favourite in gospel and ballads.",
                    i, Replace(lane, i, (0.5, iv), (0.5, bVII)));
            }
            if (!hasNext) continue;
            var j = (i + 1) % lane.Count;
            if (j == 0 && lane.Count < 2) continue;

            // Chords leading into the next one: over the second half of this chord, when it lasts a bar or more.
            if (long1 && !IsDominant(c) && next.Family is not ChordFamily.Diminished)
            {
                if (!(IsDominant(c) && step == 5) && step != 0)
                {
                    var five = Make(next.Root + 7, next.Family is ChordFamily.Minor ? "7(b9)" : "7");
                    Add($"Secondary dominant into {next.Name}", $"{five.Name} is {next.Name}'s own V, so it pulls straight into it.",
                        i, Replace(lane, i, (0.5, c), (0.5, five)));
                }
                if (step == 2)
                {
                    var dim = Make(c.Root + 1, "dim7");
                    Add("Passing diminished", $"{dim.Name} fills the gap between {c.Name} and {next.Name}: every voice moves by a semitone or holds.",
                        i, Replace(lane, i, (0.5, c), (0.5, dim)));
                }
                if (step != 0)
                {
                    var slip = new ChordSymbol(ChordSymbol.Mod(next.Root + 1), ChordSymbol.Rotate(next.Mask, 1), ChordSymbol.Mod(next.Bass + 1), next.Family);
                    Add($"Side-slip into {next.Name}", $"The last beat becomes {slip.Name}, the same chord a semitone up, and slides down into {next.Name}.",
                        i, Replace(lane, i, (0.75, c), (0.25, slip)));
                }
            }
            if (!minor && degree == 5 && c.Family == ChordFamily.Major)
            {
                var ivm = Make(c.Root, "m6");
                Add("Minor iv (the amen colour)", $"Let {c.Name} turn minor, {ivm.Name}, before it goes on: borrowed from the minor key, the sound at the end of a hymn.",
                    i, long1 ? Replace(lane, i, (0.5, c), (0.5, ivm)) : Replace(lane, i, (1, ivm)));
                if (long1 && ChordSymbol.Mod(next.Root - tonic) == 0 && next.Family is ChordFamily.Major)
                {
                    var walk = Replace(lane, i, (0.5, c), (0.5, Make(c.Root + 1, "dim7")));
                    var home = walk.FindIndex(x => x.Start == lane[j].Start);
                    if (home >= 0) walk[home] = walk[home] with { Chord = next with { Bass = ChordSymbol.Mod(next.Root + 7), Mask = next.Mask | 1 << ChordSymbol.Mod(next.Root + 7) } };
                    Add("Gospel walk-up", $"{c.Name} – {Make(c.Root + 1, "dim7").Name} – {next.Name} over its fifth: the bass climbs a semitone at a time into the chord.",
                        i, walk);
                }
            }
        }

        // A turnaround: the last chord, when it's home and lasts two bars or more, becomes I – VI7 | ii7 – V7.
        var lastIndex = lane.Count - 1;
        var last = lane[lastIndex];
        if (ChordSymbol.Mod(last.Chord.Root - tonic) == 0 && last.Length >= 2 * Bar)
        {
            var rest = last.Length - Bar;
            var one = minor ? Make(tonic, "m9") : last.Chord;
            var turn = minor
                ? Replace(lane, lastIndex, ((double)(last.Length - rest) / last.Length, one), (rest / 4.0 / last.Length, Make(tonic + 8, "maj7")),
                          (rest / 4.0 / last.Length, Make(tonic + 2, "m7b5")), (rest / 2.0 / last.Length, Make(tonic + 7, "7(b9)")))
                : Replace(lane, lastIndex, ((double)(last.Length - rest) / last.Length, one), (rest / 4.0 / last.Length, Make(tonic + 9, "7(b9)")),
                          (rest / 4.0 / last.Length, Make(tonic + 2, "m9")), (rest / 2.0 / last.Length, Make(tonic + 7, "13")));
            Add("Turnaround", minor ? "i – bVImaj7 – iiø – V7 to lead back to the top." : "I – VI7 – ii – V: the classic way back to the top, each chord pulling to the next.",
                lastIndex, turn);
        }

        // Richer chords: every plain triad and seventh given its jazz extensions.
        var richer = lane.Select(c => c with { Chord = Richer(c.Chord, tonic) }).ToList();
        if (richer.Zip(lane).Any(p => p.First.Chord.Name != p.Second.Chord.Name))
            ideas.Add(new Reharm("Richer chords", "Ninths, elevenths and thirteenths on every chord: major sevenths become maj9 or 6/9, minors m9 or m11, dominants 13.",
                                 string.Join(" ", lane.Take(4).Select(c => c.Chord.Name)), string.Join(" ", richer.Take(4).Select(c => c.Chord.Name)), 1, richer));

        return ideas;
    }

    /// <summary>The chord with the extensions a jazz player would add.</summary>
    public static ChordSymbol Richer(ChordSymbol c, int tonic)
    {
        var degree = ChordSymbol.Mod(c.Root - tonic);
        var bass = c.Bass == c.Root ? (int?)null : c.Bass;
        return c.Family switch
        {
            ChordFamily.Major when degree == 7 && !c.Has(11) && !c.Has(9) => Make(c.Root, "13", bass),
            ChordFamily.Major when !c.Has(2) => Make(c.Root, degree is 0 or 5 && !c.Has(9) ? "maj9" : "6/9", bass),
            ChordFamily.Minor when !c.Has(2) && !c.Has(11) => Make(c.Root, degree == 2 ? "m11" : "m9", bass),
            ChordFamily.Dominant when !c.Has(9) && !c.Has(1) && !c.Has(3) && !c.Has(8) => Make(c.Root, "13", bass),
            ChordFamily.HalfDiminished when !c.Has(5) => Make(c.Root, "m11b5", bass),
            ChordFamily.Sus when c.Has(10) && !c.Has(2) => Make(c.Root, "9sus4", bass),
            _ => c,
        };
    }
}
