using System.Globalization;
using MusicOrganiser.Core.Midi.Playground;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Midi.Jazz;

/// <summary>
/// The key of a run of chords with no notes to go on (the Playground's chord track, a typed chart): which key makes these
/// chords, held this long and moving this way, most like real tunes in that key. It learned how long each kind of chord
/// sounds on each note of the scale in major and minor tunes, and how chords move from one to the next, from 6,500 chord
/// charts labelled with their keys: the Real Book, iReal Pro jazz playlists and gospel, worship and soul charts, the
/// Weimar Jazz Database and the Jazz Corpus, as gathered by ChoCo (Chord Corpus, de Berardinis et al., CC BY 4.0,
/// github.com/smashub/choco). Only the counts are kept (ChordKey.tsv, made by the theory bench's --save-chord-key).
/// </summary>
/// <remarks>
/// Checked on tunes it didn't learn from (Solomon, 2026-10-22: "find jazz and gospel chord libraries that are already
/// labeled correctly so you can improve the engine"): on the Jazz Harmony Treebank's 1,170 tunes it names the key 83% of
/// the time from the whole chart and 75% from the first 8 bars, where hearing the key from the chords' notes got 64% and
/// 55%; on gospel charts 77% and 68% (was 58% and 59%).
/// </remarks>
public sealed class ChordKey
{
    static readonly string[] Kinds = ["", "6", "maj7", "m", "m6", "m7", "7", "m7b5", "dim", "dim7", "sus4", "7sus4", "+", "5"];

    /// <summary>How much the first chord counts, against the chords' shares of the time (per beat, averaged).</summary>
    const double FirstWeight = 0.1;
    /// <summary>How much the moves from chord to chord count (averaged per move).</summary>
    const double MoveWeight = 1.5;

    readonly double[,,] _held = new double[2, 12, Kinds.Length];
    readonly double[,,] _first = new double[2, 12, Kinds.Length];
    readonly double[] _tunes = new double[2];
    readonly Dictionary<(int Mode, int From, int FromKind, int To, int ToKind), double> _moves = [];
    readonly double[] _movesTotal = new double[2];
    readonly double[] _heldTotal = new double[2];
    readonly double[] _firstTotal = new double[2];

    static int KindOf(ChordSymbol c) => Math.Max(0, Array.IndexOf(Kinds, ProgressionModel.Kind(c)));

    /// <summary>Learns from charts with a known key: each chord with how long it lasts.</summary>
    /// <param name="prune">Moves seen fewer times than this are left out (they hardly change a guess, and keep the table small).</param>
    public static ChordKey Learn(IEnumerable<(IReadOnlyList<ChordSymbol> Chords, IReadOnlyList<double> Lengths, int Tonic, bool Minor)> charts, int prune = 8)
    {
        var k = new ChordKey();
        foreach (var (chords, lengths, tonic, minor) in charts)
        {
            if (chords.Count == 0) continue;
            var mode = minor ? 1 : 0;
            k._tunes[mode]++;
            for (var i = 0; i < chords.Count; i++)
                k._held[mode, ChordSymbol.Mod(chords[i].Root - tonic), KindOf(chords[i])] += lengths[i];
            k._first[mode, ChordSymbol.Mod(chords[0].Root - tonic), KindOf(chords[0])]++;
            for (var i = 0; i + 1 < chords.Count; i++)
            {
                // A chord that only changes its colour isn't a move, as Guess reads them.
                if (ProgressionModel.SameChord(chords[i], chords[i + 1])) continue;
                var key = (mode, ChordSymbol.Mod(chords[i].Root - tonic), KindOf(chords[i]), ChordSymbol.Mod(chords[i + 1].Root - tonic), KindOf(chords[i + 1]));
                k._moves[key] = k._moves.GetValueOrDefault(key) + 1;
            }
        }
        foreach (var key in k._moves.Where(kv => kv.Value < prune).Select(kv => kv.Key).ToList()) k._moves.Remove(key);
        k.Totals();
        return k;
    }

    void Totals()
    {
        for (var mode = 0; mode < 2; mode++)
        {
            _heldTotal[mode] = _firstTotal[mode] = 0;
            for (var d = 0; d < 12; d++)
                for (var c = 0; c < Kinds.Length; c++)
                {
                    _heldTotal[mode] += _held[mode, d, c];
                    _firstTotal[mode] += _first[mode, d, c];
                }
            _movesTotal[mode] = _moves.Where(kv => kv.Key.Mode == mode).Sum(kv => kv.Value);
        }
    }

    static string Mode(int mode) => mode == 0 ? "major" : "minor";
    static string Name(int kind) => Kinds[kind].Length == 0 ? "maj" : Kinds[kind];
    static int Kind(string name) => name == "maj" ? 0 : Array.IndexOf(Kinds, name);

    /// <summary>The counts as a table (tab-separated): what ChordKey.tsv holds.</summary>
    public string Save()
    {
        var w = new StringWriter { NewLine = "\n" };
        w.WriteLine("# Key from chords: counts learned from ChoCo (Chord Corpus, de Berardinis et al., CC BY 4.0, github.com/smashub/choco).");
        w.WriteLine("# Made by tools/MusicOrganiser.TheoryBench --save-chord-key. Degrees are semitones above the key's tonic.");
        var inv = CultureInfo.InvariantCulture;
        for (var mode = 0; mode < 2; mode++)
        {
            w.WriteLine($"tunes\t{Mode(mode)}\t{_tunes[mode].ToString(inv)}");
            for (var d = 0; d < 12; d++)
                for (var c = 0; c < Kinds.Length; c++)
                {
                    if (_held[mode, d, c] > 0) w.WriteLine($"held\t{Mode(mode)}\t{d}:{Name(c)}\t{Math.Round(_held[mode, d, c], 2).ToString(inv)}");
                    if (_first[mode, d, c] > 0) w.WriteLine($"first\t{Mode(mode)}\t{d}:{Name(c)}\t{_first[mode, d, c].ToString(inv)}");
                }
        }
        foreach (var (key, n) in _moves.OrderBy(kv => kv.Key))
            w.WriteLine($"move\t{Mode(key.Mode)}\t{key.From}:{Name(key.FromKind)}\t{key.To}:{Name(key.ToKind)}\t{n.ToString(inv)}");
        return w.ToString();
    }

    public static ChordKey Load(TextReader reader)
    {
        var k = new ChordKey();
        static (int, int) Chord(string s) => (int.Parse(s[..s.IndexOf(':')], CultureInfo.InvariantCulture), Kind(s[(s.IndexOf(':') + 1)..]));
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split('\t');
            var mode = f[1] == "minor" ? 1 : 0;
            var value = double.Parse(f[^1], CultureInfo.InvariantCulture);
            switch (f[0])
            {
                case "tunes": k._tunes[mode] = value; break;
                case "held": { var (d, c) = Chord(f[2]); if (c >= 0) k._held[mode, d, c] = value; break; }
                case "first": { var (d, c) = Chord(f[2]); if (c >= 0) k._first[mode, d, c] = value; break; }
                case "move":
                {
                    var (d, c) = Chord(f[2]);
                    var (e, c2) = Chord(f[3]);
                    if (c >= 0 && c2 >= 0) k._moves[(mode, d, c, e, c2)] = value;
                    break;
                }
            }
        }
        k.Totals();
        return k;
    }

    static readonly Lazy<ChordKey> Learned = new(() =>
    {
        using var s = typeof(ChordKey).Assembly.GetManifestResourceStream("MusicOrganiser.Harmony.ChordKey.tsv")
                      ?? throw new InvalidOperationException("ChordKey.tsv isn't in the build");
        using var r = new StreamReader(s);
        return Load(r);
    });

    /// <summary>The counts the app ships with (from ChoCo).</summary>
    public static ChordKey Default => Learned.Value;

    /// <summary>The key of a lane of chords, by <see cref="Default"/>; null with no chords.</summary>
    public static KeyEstimate? Of(IReadOnlyList<TimedChord> lane) =>
        lane.Count == 0 ? null : Default.Guess(lane.Select(c => c.Chord).ToList(), lane.Select(c => (double)c.Length).ToList());

    /// <summary>
    /// The key these chords are most likely in: how long each one lasts on the key's notes, how they move, and the first
    /// one, scored against tunes in each of the 24 keys. The confidence is how clearly it beats the next best (0 to 1).
    /// </summary>
    public KeyEstimate? Guess(IReadOnlyList<ChordSymbol> chords, IReadOnlyList<double> lengths)
    {
        if (chords.Count == 0) return null;
        var total = lengths.Sum();
        if (total <= 0) return null;
        // A chord that only changes its colour (Fmaj7 to Fmaj7(#11)) isn't a move.
        var moves = new List<(ChordSymbol From, ChordSymbol To)>();
        for (var i = 0; i + 1 < chords.Count; i++)
            if (!ProgressionModel.SameChord(chords[i], chords[i + 1])) moves.Add((chords[i], chords[i + 1]));
        var scores = new List<(int Tonic, bool Minor, double Score)>(24);
        for (var mode = 0; mode < 2; mode++)
            for (var tonic = 0; tonic < 12; tonic++)
            {
                double s = 0;
                for (var i = 0; i < chords.Count; i++)
                    s += lengths[i] / total * LogShare(_held, _heldTotal[mode], mode, ChordSymbol.Mod(chords[i].Root - tonic), KindOf(chords[i]));
                s += FirstWeight * LogShare(_first, _firstTotal[mode], mode, ChordSymbol.Mod(chords[0].Root - tonic), KindOf(chords[0]));
                s += 0.1 * Math.Log(_tunes[mode] + 1);
                if (moves.Count > 0)
                {
                    double m = 0;
                    foreach (var (a, b) in moves)
                    {
                        var key = (mode, ChordSymbol.Mod(a.Root - tonic), KindOf(a), ChordSymbol.Mod(b.Root - tonic), KindOf(b));
                        m += Math.Log((_moves.GetValueOrDefault(key) + 0.1) / (_movesTotal[mode] + 0.1 * 144 * Kinds.Length * Kinds.Length));
                    }
                    s += MoveWeight * m / moves.Count;
                }
                scores.Add((tonic, mode == 1, s));
            }
        scores.Sort((a, b) => b.Score.CompareTo(a.Score));
        var best = scores[0];
        return new KeyEstimate(best.Tonic, best.Minor, Math.Round(Math.Clamp(scores[0].Score - scores[1].Score, 0, 1), 3));
    }

    static double LogShare(double[,,] table, double total, int mode, int degree, int kind) =>
        Math.Log((table[mode, degree, kind] + 0.5) / (total + 0.5 * 12 * Kinds.Length));
}
