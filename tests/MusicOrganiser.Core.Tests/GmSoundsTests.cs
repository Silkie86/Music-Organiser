using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Parsers;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Tests;

public class GmSoundsTests
{
    [Fact]
    public void Each_kind_of_part_starts_with_a_fitting_sound_and_drums_get_a_kit()
    {
        Assert.Equal("Electric piano", GmSounds.ChoiceFor(GmSounds.DefaultFor(MidiRoles.Chords)).Name);
        Assert.Equal("Synth bass", GmSounds.ChoiceFor(GmSounds.DefaultFor(MidiRoles.Bass)).Name);
        Assert.Equal("Drum kit", GmSounds.ChoiceFor(GmSounds.DefaultFor(MidiRoles.Drums)).Name);
        Assert.Equal(GmSounds.Choices.Count, GmSounds.Choices.Select(c => c.Program).Distinct().Count());
    }

    [Fact]
    public void Without_a_soundfont_parts_still_play_with_the_stand_in_sound()
    {
        // Both missing: the MIDI page sets the chosen one as tests running alongside open it, and this machine may have
        // downloaded the usual one.
        var missing = Path.Combine(Path.GetTempPath(), "no such soundfont.sf2");
        GmSounds.ChosenFile = missing;
        GmSounds.DefaultFile = missing;
        try
        {
            var keys = new MidiChannel("Keys", "", 96, [new NoteEvent(0, 96, 60, 100)]) { GmProgram = 4 };
            var result = MidiRender.Render([keys], 120, "");
            Assert.Contains(result.Audio, v => Math.Abs(v) > 0.01f);
            Assert.Equal("a stand-in tone", result.Voices.Single());
        }
        finally
        {
            GmSounds.ChosenFile = null;
            GmSounds.DefaultFile = null;
        }
    }

    [Fact]
    public void A_file_that_isnt_a_soundfont_is_not_taken_for_one()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, "RIFF0000WAVEfmt ");
        Assert.False(GmSounds.LooksLikeSoundFont(file));
        File.Delete(file);
    }

    /// <summary>With a real SoundFont (MO_SOUNDFONT names one), parts play through it, quickly.</summary>
    [Fact]
    public void With_a_soundfont_parts_play_through_it()
    {
        if (Environment.GetEnvironmentVariable("MO_SOUNDFONT") is not { Length: > 0 } sf2 || !File.Exists(sf2)) return;
        GmSounds.ChosenFile = sf2;
        GmSounds.DefaultFile = sf2;   // the MIDI page may set the chosen one back meanwhile
        try
        {
            Assert.True(GmSounds.LooksLikeSoundFont(sf2));
            var notes = Enumerable.Range(0, 64).Select(i => new NoteEvent(i * 96L, 90, 48 + i % 12, 100)).ToList();
            var bass = new MidiChannel("Bass", "", 96, notes) { GmProgram = 38 };
            var drums = new MidiChannel("Drums", "", 96, notes.Select(n => n with { Key = 36 + n.Key % 3 * 2 }).ToList()) { GmProgram = GmSounds.DrumKit };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = MidiRender.Render([bass, drums], 120, "");
            Assert.Equal(["Synth bass", "Drum kit"], result.Voices);
            Assert.True(result.Audio.Max(Math.Abs) > 0.05f);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
            Console.WriteLine($"Rendered {result.Seconds:0.0} s in {clock.ElapsedMilliseconds} ms");
        }
        finally
        {
            GmSounds.ChosenFile = null;
            GmSounds.DefaultFile = null;
        }
    }
}
