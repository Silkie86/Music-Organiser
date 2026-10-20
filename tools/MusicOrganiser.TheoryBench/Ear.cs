using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Parsers;

/// <summary>
/// A jazz ear learned from melodies (--ear): for each chord family and each note that isn't the chord's own, how much
/// more the right melodies play it than other songs' melodies moved into the key ("lift"). EarFit scores a melody the
/// way the Playground's SectionFit does, with those notes' values blended towards what the lift says.
/// </summary>
static class EarTable
{
    const int Beat = 96, Bar = 4 * Beat;
    public static readonly int Families = Enum.GetValues<ChordFamily>().Length;

    /// <summary>Melody time per family and interval above the root, strong beats counting more, as SectionFit weighs it.</summary>
    public static double[,] Count(IEnumerable<(IReadOnlyList<NoteEvent> Mel, IReadOnlyList<TimedChord> Lane, long Len)> items)
    {
        var t = new double[Families, 12];
        foreach (var (mel, lane, len) in items)
            foreach (var n in mel)
            {
                if (n.Start >= len) continue;
                foreach (var (chord, start, l, _) in ChordLane.Under(lane, n.Start, Math.Min(n.Length, len - n.Start)))
                {
                    var inBar = start % Bar;
                    t[(int)chord.Family, ChordSymbol.Mod(n.Key - chord.Root)] +=
                        Math.Clamp(l / (double)Beat, 0.25, 2) * (inBar == 0 ? 1.5 : inBar == 2 * Beat ? 1.25 : 1);
                }
            }
        return t;
    }

    /// <summary>Right over other, per family and interval, from shares of each family's time; null rows have too little time.</summary>
    public static double[]?[] Lift(double[,] right, double[,] other, double minTime = 200)
    {
        var lift = new double[]?[Families];
        for (var f = 0; f < Families; f++)
        {
            double r = 0, o = 0;
            for (var i = 0; i < 12; i++) { r += right[f, i]; o += other[f, i]; }
            if (r < minTime || o < minTime) continue;
            lift[f] = new double[12];
            for (var i = 0; i < 12; i++) lift[f]![i] = (right[f, i] / r + 0.005) / (other[f, i] / o + 0.005);
        }
        return lift;
    }

    public static void Save(string path, double[]?[] lift) =>
        File.WriteAllLines(path, lift.Select(row => row is null ? "-" : string.Join(" ", row.Select(v => v.ToString("0.0000")))));

    public static double[]?[] Load(string path) =>
        File.ReadAllLines(path).Select(l => l == "-" ? null : l.Split(' ').Select(double.Parse).ToArray()).ToArray();

    /// <summary>The app's Sit, with a note that isn't the chord's own moved a share <paramref name="blend"/> towards 0.8 + slope × ln(lift).</summary>
    public static Func<ChordSymbol, int, double> Sit(double[]?[] lift, double blend, double slope) => (chord, pc) =>
    {
        var v = ChordScales.Sit(chord, pc);
        if (v >= 1 || lift[(int)chord.Family] is not { } row) return v;
        var learned = Math.Clamp(0.8 + slope * Math.Log(row[ChordSymbol.Mod(pc - chord.Root)]), -0.5, 0.95);
        return (1 - blend) * v + blend * learned;
    };
}

static class EarFit
{
    const int Ppq = PhraseAnalyser.Ppq, Beat = Ppq, Bar = 4 * Ppq;

    static double Metric(long start)
    {
        var inBar = start % Bar;
        if (inBar == 0) return 1.5;
        if (inBar == 2 * Beat) return 1.25;
        if (inBar % Beat == 0) return 1.0;
        if (inBar % (Beat / 2) == 0) return 0.75;
        return 0.6;
    }

    /// <summary>SectionFit.Score for a melody with the jazz ear, with the note values and the strong-beat tension weight swappable.</summary>
    public static double Score(IReadOnlyList<NoteEvent> notes, IReadOnlyList<TimedChord> lane, long length, Func<ChordSymbol, int, double> sit,
                               double strong = 0.6)
    {
        if (notes.Count == 0 || lane.Count == 0) return 0;
        var sorted = notes.Where(n => n.Start < length).OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();
        double sum = 0, total = 0;
        for (var i = 0; i < sorted.Count; i++)
        {
            var n = sorted[i];
            var pc = n.Key % 12;
            foreach (var (chord, start, len, struck) in ChordLane.Under(lane, n.Start, Math.Min(n.Length, length - n.Start)))
            {
                var metric = Metric(start);
                var w = Math.Clamp(len / (double)Beat, 0.25, 2) * metric * (struck ? 1 : 0.8);
                var v = sit(chord, pc);
                if (metric >= 1.25 && v > 0 && v < 1) v *= strong;
                if (struck && v < 0.55 && n.Length <= Beat / 2 && metric < 1.25)
                {
                    var next = sorted.Skip(i + 1).FirstOrDefault(m => m.Start > n.Start);
                    if (next.Length > 0 && Math.Abs(next.Key - n.Key) <= 2 && ChordLane.At(lane, next.Start + 1) is { } then &&
                        sit(then, next.Key % 12) >= 0.75)
                        v = Math.Max(v, 0.65);
                    else v = Math.Max(v, 0.15);
                }
                sum += w * v;
                total += w;
            }
        }
        return total <= 0 ? 0 : Math.Clamp(sum / total, 0, 1);
    }

    public static readonly (double Blend, double Slope, double Strong)[] Grid =
        [(0, 0, 0.6), (0, 0, 1), (0.5, 0.5, 0.6), (0.5, 1, 0.6), (1, 0.5, 0.6), (1, 1, 0.6), (1, 1.5, 0.6), (0.5, 1, 1), (1, 1, 1)];

    public static string Name((double Blend, double Slope, double Strong) g) =>
        g.Blend == 0 ? $"app's ear, strong-beat tensions x{g.Strong}" : $"learned ear: blend {g.Blend}, slope {g.Slope}, strong x{g.Strong}";
}
