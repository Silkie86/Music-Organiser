using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Parsers;
using Xunit.Abstractions;

namespace MusicOrganiser.Core.Tests;

/// <summary>Chords for a melody or a bassline on their own (Solomon, 2026-10-21: "harmonise a bassline or bass an melody").</summary>
public class HarmoniserTests(ITestOutputHelper output)
{
    const int Beat = PhraseAnalyser.Ppq, Bar = 4 * Beat;

    /// <summary>Quarter notes, one bar per array, from bar 1 (C4 = 60).</summary>
    static List<NoteEvent> Tune(params int[][] bars) =>
        bars.SelectMany((keys, b) => keys.Select((k, i) => new NoteEvent(b * Bar + i * (Bar / keys.Length), Bar / keys.Length, k, 100))).ToList();

    // Twinkle, twinkle: C C G G | A A G - | F F E E | D D C -
    static readonly List<NoteEvent> Twinkle = Tune([60, 60, 67, 67], [69, 69, 67, 67], [65, 65, 64, 64], [62, 62, 60, 60]);

    [Fact]
    public void A_melody_gets_chords_that_hold_its_notes_home_at_both_ends()
    {
        var ideas = Harmoniser.Harmonise(Twinkle, [], 4 * Bar, 0, false);
        foreach (var i in ideas) output.WriteLine($"{i.Title}: {i.After}");
        var plain = ideas.Single(i => i.Title == "Harmonise: plain");
        Assert.True(plain.Whole);
        Assert.Equal("C", plain.Lane[0].Chord.Name);
        Assert.Equal("C", plain.Lane[^1].Chord.Name);
        Assert.Equal("F", ChordLane.At(plain.Lane, 2 * Bar + 1)!.Value.Name);   // F F E E: IV
        Assert.Contains(ideas, i => i.Title == "Harmonise: jazz");
        // every long note sits on its chord
        foreach (var idea in ideas)
            Assert.DoesNotContain(Twinkle, n => ChordLane.Fights(idea.Lane, n.Start, n.Length, n.Key % 12));
    }

    [Fact]
    public void A_bassline_alone_puts_its_notes_at_the_root()
    {
        // A walking-free root line in C: C | A | D | G
        var bass = Tune([36, 36, 36, 36], [45, 45, 45, 45], [38, 38, 38, 38], [43, 43, 43, 43]);
        var ideas = Harmoniser.Harmonise([], bass, 4 * Bar, 0, false);
        foreach (var i in ideas) output.WriteLine($"{i.Title}: {i.After}");
        var jazz = ideas.Single(i => i.Title == "Harmonise: jazz");
        Assert.Equal([0, 9, 2, 7], jazz.Lane.Select(c => c.Chord.Root));
        Assert.Equal("your bassline", jazz.Before);
    }
}
