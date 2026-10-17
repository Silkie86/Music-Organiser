using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Midi.Playground;

/// <summary>What a section is for, as energy over its bars: a steady verse, a build that rises, a full drop, a sparse breakdown.</summary>
public enum SectionFeel
{
    /// <summary>No energy asked for: suggestions are ranked on harmony alone.</summary>
    Any,
    Verse,
    Build,
    Drop,
    Breakdown,
}

/// <summary>
/// A tension curve: how much energy each bar carries, 0 to 1 (Solomon, 2026-10-17: "a tension curve per section, so
/// Suggest can find parts that match a verse, a build or a drop"). It blends how busy the bar is (onsets, against what's
/// usual for that kind of part), how hard and how high it plays, and how far its harmony pulls from the key: the
/// tonal tension of Herremans and Chew (2016), measured on Chew's spiral array, where each note sits on a helix of
/// fifths. Tensile strain is how far a bar's centre of effect lies from the key's centre; cloud diameter is how far apart
/// its notes lie. A bar of Cmaj7 in C is calm; an altered dominant is not.
/// </summary>
public static class Energy
{
    const int Bar = ChordLane.Bar;
    static readonly double H = Math.Sqrt(2.0 / 15);   // the helix's rise per fifth, with radius 1
    static readonly double MaxDiameter = Diameter([-5, 6]);

    /// <summary>Onsets in a bar that count as "busy" for each kind of part.</summary>
    static double Busy(string role) => role switch
    {
        MidiRoles.Chords => 6,
        MidiRoles.Bass => 8,
        MidiRoles.Melody => 8,
        MidiRoles.Arp => 16,
        MidiRoles.Drums => 20,
        _ => 14,   // a whole mix
    };

    /// <summary>A pitch class's place on the line of fifths around the tonic: -5 to 6.</summary>
    static int Fifths(int pc, int tonic)
    {
        var k = ChordSymbol.Mod((pc - tonic) * 7);
        return k > 6 ? k - 12 : k;
    }

    static (double X, double Y, double Z) Point(double k) => (Math.Sin(k * Math.PI / 2), Math.Cos(k * Math.PI / 2), k * H);

    static (double X, double Y, double Z) Mix(params (double W, (double X, double Y, double Z) P)[] parts)
    {
        double x = 0, y = 0, z = 0, w = 0;
        foreach (var (wt, p) in parts) { x += wt * p.X; y += wt * p.Y; z += wt * p.Z; w += wt; }
        return w <= 0 ? (0, 0, 0) : (x / w, y / w, z / w);
    }

    static double Distance((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>A chord's centre on the spiral: root, fifth and third weighted 0.516, 0.315, 0.168 (Chew's weights).</summary>
    static (double, double, double) ChordCentre(int root, bool minor) =>
        Mix((0.516, Point(root)), (0.315, Point(root + 1)), (0.168, Point(root + (minor ? -3 : 4))));

    /// <summary>The key's centre: its I, V and IV chords, weighted the same way.</summary>
    static (double, double, double) KeyCentre(bool minor) =>
        Mix((0.516, ChordCentre(0, minor)), (0.315, ChordCentre(1, false)), (0.168, ChordCentre(-1, minor)));

    static double Diameter(IReadOnlyList<int> fifths)
    {
        var max = 0.0;
        for (var i = 0; i < fifths.Count; i++)
            for (var j = i + 1; j < fifths.Count; j++) max = Math.Max(max, Distance(Point(fifths[i]), Point(fifths[j])));
        return max;
    }

    /// <summary>How tense the harmony of these notes is in the key: 0 (home) to 1.</summary>
    public static double HarmonicTension(IEnumerable<NoteEvent> notes, int tonic, bool minor)
    {
        var weights = new double[12];
        foreach (var n in notes) weights[n.Key % 12] += Math.Max(1, n.Length);
        var total = weights.Sum();
        if (total <= 0) return 0;
        var present = Enumerable.Range(0, 12).Where(p => weights[p] > total * 0.04).Select(p => Fifths(p, tonic)).ToList();
        var centre = Mix(Enumerable.Range(0, 12).Where(p => weights[p] > 0).Select(p => (weights[p], Point(Fifths(p, tonic)))).ToArray());
        var strain = Distance(centre, KeyCentre(minor));
        var diameter = Diameter(present) / MaxDiameter;
        return Math.Clamp(0.6 * Math.Min(1, strain / 1.4) + 0.4 * diameter, 0, 1);
    }

    /// <summary>Each bar's energy, 0 to 1, for a part (or a whole section when <paramref name="role"/> is "").</summary>
    public static double[] Curve(IReadOnlyList<NoteEvent> notes, long length, string role, KeyEstimate? key)
    {
        var bars = (int)Math.Max(1, (length + Bar - 1) / Bar);
        var curve = new double[bars];
        var busy = Busy(role);
        var drums = role == MidiRoles.Drums;
        var byBar = new List<NoteEvent>[bars];
        for (var b = 0; b < bars; b++) byBar[b] = [];
        foreach (var n in notes)
            if (!n.Muted && n.Start >= 0 && n.Start < length) byBar[n.Start / Bar].Add(n);
        for (var b = 0; b < bars; b++)
        {
            var bar = byBar[b];
            if (bar.Count == 0) continue;
            var onsets = bar.Select(n => n.Start).Distinct().Count();
            var density = Math.Min(1, onsets / busy);
            var loud = bar.Average(n => n.Velocity) / 127.0;
            if (drums)
            {
                curve[b] = Math.Clamp(0.65 * density + 0.35 * loud, 0, 1);
                continue;
            }
            var register = Math.Clamp((bar.Average(n => n.Key) - 36) / 48.0, 0, 1);
            var tension = key is { } k ? HarmonicTension(bar, k.Tonic, k.Minor) : 0.3;
            curve[b] = Math.Clamp(0.4 * density + 0.2 * loud + 0.1 * register + 0.3 * tension, 0, 1);
        }
        return curve;
    }

    /// <summary>The curve a feel asks for over so many bars.</summary>
    public static double[] Target(SectionFeel feel, int bars)
    {
        bars = Math.Max(1, bars);
        return Enumerable.Range(0, bars).Select(b => feel switch
        {
            SectionFeel.Verse => 0.38,
            SectionFeel.Build => bars == 1 ? 0.6 : 0.25 + 0.6 * b / (bars - 1),
            SectionFeel.Drop => 0.8,
            SectionFeel.Breakdown => 0.18,
            _ => 0.5,
        }).ToArray();
    }

    /// <summary>
    /// How well a curve follows a target, 0 to 1: its level bar by bar, and for a rising target its shape too, so a
    /// part that climbs suits a build even when it starts a little high.
    /// </summary>
    public static double Match(double[] curve, double[] target)
    {
        if (curve.Length == 0 || target.Length == 0) return 0;
        double diff = 0;
        for (var i = 0; i < target.Length; i++) diff += Math.Abs(curve[i % curve.Length] - target[i]);
        var level = Math.Clamp(1 - 1.6 * diff / target.Length, 0, 1);
        if (target.Max() - target.Min() < 0.1) return level;
        var c = Enumerable.Range(0, target.Length).Select(i => curve[i % curve.Length]).ToArray();
        var shape = (Correlation(c, target) + 1) / 2;
        return 0.55 * level + 0.45 * shape;
    }

    static double Correlation(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), ab = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++) { ab += (a[i] - ma) * (b[i] - mb); aa += (a[i] - ma) * (a[i] - ma); bb += (b[i] - mb) * (b[i] - mb); }
        return aa <= 1e-9 || bb <= 1e-9 ? 0 : ab / Math.Sqrt(aa * bb);
    }

    public static string Name(SectionFeel feel) => feel switch
    {
        SectionFeel.Verse => "verse", SectionFeel.Build => "build", SectionFeel.Drop => "drop", SectionFeel.Breakdown => "breakdown", _ => "",
    };
}
