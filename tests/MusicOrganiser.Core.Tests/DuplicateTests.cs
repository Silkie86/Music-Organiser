using MusicOrganiser.Core.Sounds;
using Xunit;

namespace MusicOrganiser.Core.Tests;

public class DuplicateTests
{
    /// <summary>An 8-second groove: drums on every beat plus a little melody, different for each seed.</summary>
    static float[] Loop(int seed, double seconds = 8, int sr = 44100)
    {
        var rng = new Random(seed);
        var x = Synth.Silence(seconds, sr);
        var kick = Synth.Kick(seed, sr);
        var hat = Synth.Hat(seed + 1, sr);
        var beat = 0.5;
        for (var b = 0; b * beat < seconds - 0.5; b++)
        {
            var at = (int)(b * beat * sr);
            var hit = b % 2 == 0 ? kick : hat;
            for (var i = 0; i < hit.Length && at + i < x.Length; i++) x[at + i] += hit[i] * 0.6f;
            Synth.Tone(x, 60 + rng.Next(12), b * beat + 0.25, 0.4, 0.15, sr);
        }
        return x;
    }

    static float[] Gain(float[] x, float g) => x.Select(v => v * g).ToArray();
    static float[] Pad(float[] x, double before, int sr = 44100) => [.. new float[(int)(before * sr)], .. x];
    static float[] Cut(float[] x, double from, double to, int sr = 44100) => x[(int)(from * sr)..(int)(to * sr)];

    [Fact]
    public void Finds_exact_converted_and_trimmed_copies_but_not_lookalikes()
    {
        var root = Fixtures.SoundsDir();
        string Dir(string name) { var d = Path.Combine(root, "Samples", name); Directory.CreateDirectory(d); return d; }
        var a = Dir("Pack A");
        var b = Dir("Pack B");
        var c = Dir("Converted");

        for (var i = 0; i < 6; i++)
        {
            Synth.Write(Path.Combine(a, $"Kick {i}.wav"), Synth.Kick(i));
            Synth.Write(Path.Combine(a, $"Hat {i}.wav"), Synth.Hat(i));   // six hats that sound much alike but aren't copies
            Synth.Write(Path.Combine(a, $"Snare {i}.wav"), Synth.Snare(i));
        }
        Synth.Write(Path.Combine(a, "Groove.wav"), Loop(1));
        Synth.Write(Path.Combine(a, "Other groove.wav"), Loop(2));

        // An exact copy under another name; the same kick resampled to 48 kHz, 24-bit and quieter; a snare with
        // silence added before it and its tail cut; and two bars cut out of the middle of the groove.
        File.Copy(Path.Combine(a, "Kick 0.wav"), Path.Combine(b, "BD heavy.wav"));
        WavWriter.Write(Path.Combine(c, "Kick 1 (48k).wav"), [Gain(Dsp.Resample(Synth.Kick(1), 44100, 48000), 0.5f)], 48000, 24);
        Synth.Write(Path.Combine(b, "Snare 2 edit.wav"), Cut(Pad(Synth.Snare(2), 0.04), 0, 0.25));
        Synth.Write(Path.Combine(b, "Groove bars 3-6.wav"), Cut(Loop(1), 2.0, 5.5));
        // And a kick that starts like Kick 3 but dies away sooner: very similar, not the same sound.
        Synth.Write(Path.Combine(b, "Kick 3 short.wav"), Shorter(Synth.Kick(3)));
        foreach (var f in Directory.GetFiles(b).Concat(Directory.GetFiles(c))) File.SetLastWriteTime(f, DateTime.Now.AddDays(1));   // the copies came later

        using var lib = new SoundLibrary(Path.Combine(root, "sounds.db"));
        using var org = new SoundOrganiser(lib);
        org.Scan([Path.Combine(root, "Samples")]);
        Assert.Equal(org.Sounds.Count, DuplicateFinder.Update(org));

        var groups = DuplicateFinder.Groups(org);
        string Names(DuplicateGroup g) => string.Join(" + ", g.Members.Select(m => m.Sound.Name));
        Assert.True(groups.Count == 5, string.Join("\n", groups.Select(Names)));

        DuplicateGroup With(string name) => groups.Single(g => g.Members.Any(m => m.Sound.Name == name));
        var exact = With("BD heavy");
        Assert.Equal(["Kick 0", "BD heavy"], exact.Members.Select(m => m.Sound.Name).Order().Reverse());
        Assert.Equal(CopyKind.Exact, exact.Kind);
        Assert.Equal("Exact copy, renamed", exact.Members[1].Why);
        Assert.Equal(new FileInfo(Path.Combine(b, "BD heavy.wav")).Length, exact.SpareBytes);
        Assert.True(exact.Members[1].Same);

        var converted = With("Kick 1 (48k)");
        Assert.Equal("Kick 1", converted.Members[0].Sound.Name);   // both WAVs the same length, so the older one
        Assert.Equal(CopyKind.Converted, converted.Kind);
        Assert.True(converted.Members[1].Same, converted.Members[1].Why);   // another rate, depth and level, but the same sound
        Assert.Equal(1, converted.SameCopies);

        // Only very similar: listed, but not the same sound, so it's never offered for hiding.
        var shorter = With("Kick 3 short");
        Assert.Equal("Kick 3", shorter.Members[0].Sound.Name);
        Assert.False(shorter.Members[1].Same);
        Assert.Equal(0, shorter.SameCopies);
        Assert.True(shorter.HasSimilar);
        Assert.Equal(0, shorter.SpareBytes);
        Assert.StartsWith("Very similar", shorter.Members[1].Why);

        var snare = With("Snare 2 edit");
        Assert.Equal("Snare 2", snare.Members[0].Sound.Name);   // the full-length one
        Assert.Equal(CopyKind.Trimmed, snare.Kind);
        Assert.Equal(0, snare.SpareBytes);
        Assert.False(snare.Members[1].Same);

        var groove = With("Groove bars 3-6");
        Assert.Equal("Groove", groove.Members[0].Sound.Name);
        Assert.Equal(CopyKind.Trimmed, groove.Kind);
        var match = lib.Matches().Single(m => m.Kind == CopyKind.Trimmed && (org.Get(m.A)!.Name == "Groove" || org.Get(m.B)!.Name == "Groove"));
        Assert.InRange(match.Offset, 1.7, 2.3);   // found about 2 seconds in

        Assert.DoesNotContain(groups, g => g.Members.Count(m => m.Sound.Name.StartsWith("Hat")) > 0);
        Assert.DoesNotContain(groups, g => g.Members.Any(m => m.Sound.Name == "Other groove"));

        // Hiding a copy takes it out of the app's lists (not off the disk), and it still shows in its group.
        var bd = org.Everything.Single(s => s.Name == "BD heavy");
        lib.Hide([bd.Id], "test");
        org.Reload();
        Assert.DoesNotContain(org.Sounds, s => s.Name == "BD heavy");
        Assert.True(File.Exists(bd.Path));
        Assert.True(DuplicateFinder.Groups(org).Single(g => g.Members.Any(m => m.Sound.Name == "BD heavy")).Members.Single(m => m.Sound.Name == "BD heavy").Sound.Hidden);
        Assert.Equal([bd.Id], lib.LastHidden()!.Value.Ids);
        lib.Unhide([bd.Id]);
        org.Reload();
        Assert.Contains(org.Sounds, s => s.Name == "BD heavy");
        Assert.Null(lib.LastHidden());

        // Nothing new: nothing printed again, same answer.
        Assert.Equal(0, DuplicateFinder.Update(org));
        Assert.Equal(groups.Count, DuplicateFinder.Groups(org).Count);

        // A file that changes is printed again; a file that goes takes its copies with it.
        Synth.Write(Path.Combine(b, "Snare 2 edit.wav"), Synth.Snare(77));
        File.Delete(Path.Combine(b, "BD heavy.wav"));
        org.Scan([Path.Combine(root, "Samples")]);
        Assert.Equal(1, DuplicateFinder.Update(org));
        var after = DuplicateFinder.Groups(org);
        Assert.Equal(3, after.Count);
        Assert.DoesNotContain(after, g => g.Members.Any(m => m.Sound.Name is "Snare 2 edit" or "Kick 0"));
    }

    static float[] Shorter(float[] x, double factor = 0.7, int sr = 44100)
    {
        var y = (float[])x.Clone();
        for (var i = (int)(0.12 * sr); i < y.Length; i++) y[i] *= (float)Math.Pow(factor, (i - 0.12 * sr) / (0.1 * sr));
        return y;
    }

    static float[] Low(float[] x, float c)
    {
        var y = (float[])x.Clone();
        float s = 0;
        for (var i = 0; i < y.Length; i++) { s += c * (y[i] - s); y[i] = s; }
        return y;
    }

    [Fact]
    public void Same_sound_is_told_from_very_similar()
    {
        const int sr = 44100;
        static double Apart(float[] a, float[] b, int rateB = sr) =>
            SoundPrint.SpectralDistance(SoundPrint.WaveStart(a, sr, 20, sr), SoundPrint.WaveStart(rateB == sr ? b : Dsp.Resample(b, rateB, sr), sr, 20, sr), sr);
        foreach (var x in new[] { Synth.Kick(4), Synth.Snare(3), Synth.Hat(2) })
        {
            // The same sound: resampled, quieter, with silence before it.
            Assert.InRange(Apart(x, Pad(Gain(Dsp.Resample(x, sr, 48000), 0.5f), 0.05, 48000), 48000), 0, 0.2);
            // Very similar: another EQ, more drive.
            Assert.True(Apart(x, Low(x, 0.6f)) > DuplicateFinder.SameDbLossy, $"{Apart(x, Low(x, 0.6f))}");
            Assert.True(Apart(x, x.Select(v => (float)Math.Tanh(3 * v) / 1.2f).ToArray()) > DuplicateFinder.SameDbLossy);
        }
        // A tail that dies away sooner.
        Assert.True(Apart(Synth.Kick(4), Shorter(Synth.Kick(4), 0.85)) > DuplicateFinder.SameDbLossy);
        Assert.True(Apart(Synth.Snare(3), Shorter(Synth.Snare(3), 0.85)) > DuplicateFinder.SameDbLossy);
        // Still copies, though: typically close, nowhere near a different sound.
        foreach (var other in new[] { Shorter(Synth.Kick(4), 0.85), Low(Synth.Kick(4), 0.6f), Synth.Kick(4).Select(v => (float)Math.Tanh(3 * v) / 1.2f).ToArray() })
            Assert.True(Typical(Synth.Kick(4), other) < DuplicateFinder.NotACopyDb, $"{Typical(Synth.Kick(4), other)}");

        // A kick loop and a hi-hat loop with the same rhythm aren't copies at all (SoundFingerprinting can match them).
        static float[] Every(float[] hit, double gap, int n)
        {
            var x = new float[(int)(gap * n * sr)];
            for (var i = 0; i < n; i++) for (var j = 0; j < hit.Length && (int)(i * gap * sr) + j < x.Length; j++) x[(int)(i * gap * sr) + j] += hit[j];
            return x;
        }
        Assert.True(Typical(Every(Synth.Kick(1), 0.5, 8), Every(Synth.Hat(1), 0.5, 8)) > DuplicateFinder.NotACopyDb);
        // A loop cut from a longer one, lined up where it starts, is the same where they overlap.
        var loop = Every(Synth.Kick(1), 0.5, 8).Zip(Every(Synth.Hat(2), 0.25, 16), (k, h) => k + h).ToArray();
        Assert.InRange(Typical(Cut(loop, 1.0, 3.0), Cut(loop, 1.0, loop.Length / (double)sr)), 0, 0.2);

        static double Typical(float[] a, float[] b) =>
            SoundPrint.SpectralDistances(SoundPrint.WaveStart(a, sr, 20, sr), SoundPrint.WaveStart(b, sr, 20, sr), sr).Typical;
    }

    [Fact]
    public void Prints_ignore_level_sample_rate_and_leading_silence()
    {
        var kick = Synth.Kick(5);
        var p = SoundPrint.Make(new AudioData { SampleRate = 44100, Channels = [kick] });
        var q = SoundPrint.Make(new AudioData { SampleRate = 48000, Channels = [Pad(Gain(Dsp.Resample(kick, 44100, 48000), 0.3f), 0.1, 48000)] });
        Assert.True(SoundPrint.Similarity(p, q) > 0.97, $"{SoundPrint.Similarity(p, q)}");
        Assert.True(p.Keys.Zip(q.Keys).Any(k => k.First == k.Second));
        Assert.False(p.Exact.AsSpan().SequenceEqual(q.Exact));

        var other = SoundPrint.Make(new AudioData { SampleRate = 44100, Channels = [Synth.Snare(5)] });
        Assert.True(SoundPrint.Similarity(p, other) < DuplicateFinder.PrintThreshold);

        var silent = SoundPrint.Make(new AudioData { SampleRate = 44100, Channels = [Synth.Silence(1)] });
        Assert.Equal(0, silent.Frames);
        Assert.Empty(silent.Keys);
    }
}
