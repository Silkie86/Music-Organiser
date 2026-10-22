using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Tests;

public class MidiToolsTests
{
    const int Beat = PhraseAnalyser.Ppq, Bar = 4 * Beat;

    static readonly List<NoteEvent> Tune = [new(0, Beat, 60, 100), new(Beat, Beat, 62, 90), new(2 * Beat, 2 * Beat, 64, 80)];

    [Fact]
    public void Transpose_moves_every_note_and_stays_on_the_keyboard()
    {
        Assert.Equal([61, 63, 65], MidiTools.Transpose(Tune, 1).Select(n => n.Key));
        Assert.Equal(127, MidiTools.Transpose([new(0, Beat, 126, 100)], 5).Single().Key);
        Assert.Equal([60, 62, 64], Tune.Select(n => n.Key));   // the notes given are left as they were
    }

    [Fact]
    public void Quantise_snaps_starts_and_lengths_to_16ths_and_merges_what_lands_together()
    {
        var loose = new List<NoteEvent> { new(5, 20, 60, 100), new(Beat - 7, Beat + 9, 62, 90), new(Beat + 4, 10, 62, 80) };
        var tight = MidiTools.Quantise(loose);
        Assert.Equal([(0L, 24L, 60), (Beat, Beat, 62)], tight.Select(n => (n.Start, n.Length, n.Key)));
    }

    [Fact]
    public void Repeat_reverse_and_first_half()
    {
        var twice = MidiTools.Repeat(Tune, Bar);
        Assert.Equal(6, twice.Count);
        Assert.Equal(Bar + Beat, twice[4].Start);

        var back = MidiTools.Reverse(Tune, Bar);
        Assert.Equal([(0L, 64), (2L * Beat, 62), (3L * Beat, 60)], back.Select(n => (n.Start, n.Key)));

        var two = MidiTools.Repeat(Tune, Bar);
        Assert.Equal(Tune, MidiTools.FirstHalf(two, 2 * Bar));
        // A note running past the half is cut there; a bar is the least it keeps.
        Assert.Equal(3 * Beat, MidiTools.FirstHalf([new(Beat, 4 * Bar, 60, 100)], 2 * Bar).Single().Length);
        Assert.Equal(Tune, MidiTools.FirstHalf(Tune, Bar));
    }
}
