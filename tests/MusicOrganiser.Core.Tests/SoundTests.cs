using MusicOrganiser.Core.Sounds;
using Xunit;

namespace MusicOrganiser.Core.Tests;

/// <summary>Small synthesised sounds for the sound-analysis tests.</summary>
static class Synth
{
    public static float[] Silence(double seconds, int sr = 44100) => new float[(int)(seconds * sr)];

    /// <summary>A plucked harmonic tone (five partials, exponential decay).</summary>
    public static void Tone(float[] x, double midi, double start, double length, double amp = 0.3, int sr = 44100)
    {
        var f = MusicTheory.MidiToHz(midi);
        var s0 = (int)(start * sr);
        var n = (int)(length * sr);
        for (var i = 0; i < n && s0 + i < x.Length; i++)
        {
            var t = (double)i / sr;
            var env = Math.Exp(-t * 1.5) * Math.Min(1, t / 0.005);
            double v = 0;
            for (var h = 0; h < 5; h++) v += Math.Pow(0.6, h) * Math.Sin(2 * Math.PI * f * (h + 1) * t);
            x[s0 + i] += (float)(amp * env * v);
        }
    }

    /// <summary>A kick: a sine sweeping down from ~150 Hz to ~50 Hz with a fast decay.</summary>
    public static float[] Kick(int seed, int sr = 44100)
    {
        var rng = new Random(seed);
        var x = new float[(int)(0.5 * sr)];
        var f0 = 140 + rng.NextDouble() * 40;
        var f1 = 45 + rng.NextDouble() * 15;
        double phase = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var t = (double)i / sr;
            var f = f1 + (f0 - f1) * Math.Exp(-t * 30);
            phase += 2 * Math.PI * f / sr;
            x[i] = (float)(0.9 * Math.Exp(-t * (6 + rng.NextDouble() * 3)) * Math.Sin(phase));
        }
        return x;
    }

    /// <summary>A hat: high-passed noise with a very short decay.</summary>
    public static float[] Hat(int seed, int sr = 44100)
    {
        var rng = new Random(seed);
        var x = new float[(int)(0.15 * sr)];
        double prev = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var t = (double)i / sr;
            var noise = rng.NextDouble() * 2 - 1;
            var hp = noise - prev;   // crude high-pass
            prev = noise;
            x[i] = (float)(0.5 * Math.Exp(-t * (40 + rng.NextDouble() * 20)) * hp);
        }
        return x;
    }

    /// <summary>A snare: a 190 Hz body plus broadband noise.</summary>
    public static float[] Snare(int seed, int sr = 44100)
    {
        var rng = new Random(seed);
        var x = new float[(int)(0.3 * sr)];
        for (var i = 0; i < x.Length; i++)
        {
            var t = (double)i / sr;
            var body = Math.Sin(2 * Math.PI * (180 + 20 * rng.NextDouble()) * t) * Math.Exp(-t * 25);
            var noise = (rng.NextDouble() * 2 - 1) * Math.Exp(-t * (12 + rng.NextDouble() * 6));
            x[i] = (float)(0.4 * body + 0.4 * noise);
        }
        return x;
    }

    public static void Write(string path, float[] x, int sr = 44100) => WavWriter.Write(path, [x], sr, 16);
}

public class AudioFileTests
{
    [Fact]
    public void Wav_round_trip_16_and_24_bit()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var x = new float[1000];
        for (var i = 0; i < x.Length; i++) x[i] = (float)Math.Sin(i * 0.05) * 0.5f;
        foreach (var bits in new[] { 16, 24 })
        {
            var path = Path.Combine(dir, $"t{bits}.wav");
            WavWriter.Write(path, [x, x], 48000, bits);
            var a = AudioFile.Read(path);
            Assert.Equal(48000, a.SampleRate);
            Assert.Equal(2, a.Channels.Length);
            Assert.Equal(1000, a.Length);
            Assert.InRange(Math.Abs(a.Channels[1][123] - x[123]), 0, bits == 16 ? 1e-4 : 1e-6);
        }
    }

    [Fact]
    public void Reads_acid_chunk_tempo_and_root()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "loop.wav");
        WavWriter.Write(path, [new float[4410]], 44100);
        // Append an ACID chunk: flags (root note set, not one-shot), root 57 (A3), tempo 128.
        var bytes = File.ReadAllBytes(path).ToList();
        var acid = new List<byte>();
        acid.AddRange("acid"u8.ToArray());
        acid.AddRange(BitConverter.GetBytes(24));
        acid.AddRange(BitConverter.GetBytes(2));
        acid.AddRange(BitConverter.GetBytes((ushort)57));
        acid.AddRange(new byte[14]);
        acid.AddRange(BitConverter.GetBytes(128f));
        bytes.AddRange(acid);
        var riffSize = BitConverter.GetBytes(bytes.Count - 8);
        for (var i = 0; i < 4; i++) bytes[4 + i] = riffSize[i];
        File.WriteAllBytes(path, bytes.ToArray());

        var a = AudioFile.Read(path);
        Assert.Equal(128, a.EmbeddedTempo);
        Assert.Equal(57, a.EmbeddedRootNote);
        Assert.True(a.EmbeddedIsLoop);
    }

    [Fact]
    public void Reads_aiff()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "t.aif");
        using (var fs = File.Create(path))
        {
            void BE32(int v) { fs.WriteByte((byte)(v >> 24)); fs.WriteByte((byte)(v >> 16)); fs.WriteByte((byte)(v >> 8)); fs.WriteByte((byte)v); }
            void BE16(int v) { fs.WriteByte((byte)(v >> 8)); fs.WriteByte((byte)v); }
            const int frames = 100;
            fs.Write("FORM"u8); BE32(4 + 26 + 16 + frames * 2); fs.Write("AIFF"u8);
            fs.Write("COMM"u8); BE32(18); BE16(1); BE32(frames); BE16(16);
            fs.Write(new byte[] { 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0 });   // 44100 as 80-bit extended
            fs.Write("SSND"u8); BE32(8 + frames * 2); BE32(0); BE32(0);
            for (var i = 0; i < frames; i++) BE16(i * 100);
        }
        var a = AudioFile.Read(path);
        Assert.Equal(44100, a.SampleRate);
        Assert.Equal(100, a.Length);
        Assert.Equal(5000 / 32768.0, a.Channels[0][50], 5);
    }
}

public class MusicTheoryTests
{
    [Theory]
    [InlineData(new[] { 60, 64, 67 }, "C")]
    [InlineData(new[] { 57, 60, 64 }, "Am")]
    [InlineData(new[] { 50, 53, 57, 60 }, "Dm7")]
    [InlineData(new[] { 55, 59, 62, 65 }, "G7")]
    [InlineData(new[] { 53, 57, 60, 64 }, "Fmaj7")]
    public void Names_chords_from_notes(int[] notes, string expected) =>
        Assert.Equal(expected, MusicTheory.NameChord(notes)?.Name);

    [Theory]
    [InlineData(9, true, "8A")]
    [InlineData(0, false, "8B")]
    [InlineData(6, true, "11A")]
    [InlineData(7, false, "9B")]
    public void Camelot_codes(int tonic, bool minor, string code) => Assert.Equal(code, MusicTheory.Camelot(tonic, minor));

    [Fact]
    public void Finds_key_of_a_progression()
    {
        // i - iv - V - i in A minor, with the bass doubling roots.
        var x = Synth.Silence(8.5);
        var chords = new[] { new[] { 45, 57, 60, 64 }, new[] { 50, 57, 62, 65 }, new[] { 52, 56, 59, 64 }, new[] { 45, 57, 60, 64 } };
        for (var c = 0; c < chords.Length; c++)
            foreach (var n in chords[c]) Synth.Tone(x, n, c * 2, 2, 0.2);
        var a = SoundAnalyzer.Analyse(new AudioData { SampleRate = 44100, Channels = [x] }, "progression.wav");
        Assert.Equal(9, a.KeyTonic);
        Assert.True(a.KeyMinor);
        Assert.Contains(a.Chords, c => c.Name.StartsWith("Am"));
        Assert.Contains(a.Chords, c => c.Name.StartsWith("Dm"));
        Assert.Contains(a.Chords, c => c.Name.StartsWith('E'));
    }

    [Theory]
    [InlineData("Bass_Fm_120bpm.wav", 5, true)]
    [InlineData("Keys - C# minor - 90.wav", 1, true)]
    [InlineData("Pad_Ebmaj.wav", 3, false)]
    public void Reads_key_from_name(string name, int tonic, bool minor) =>
        Assert.Equal((tonic, minor), SoundTypes.KeyFromName(name));

    [Fact]
    public void Reads_tempo_from_name()
    {
        Assert.Equal(128, SoundTypes.TempoFromName(@"C:\Loops\Drums_128bpm_Top.wav"));
        Assert.Equal(92, SoundTypes.TempoFromName("Groove_92_Am.wav"));
        Assert.Null(SoundTypes.TempoFromName("Kick_01.wav"));
    }
}

public class SoundAnalyzerTests
{
    [Fact]
    public void Finds_pitch_of_a_808()
    {
        var x = Synth.Silence(1.5);
        var f = MusicTheory.MidiToHz(33);   // A1, 55 Hz
        for (var i = 0; i < x.Length; i++) x[i] = (float)(0.8 * Math.Sin(2 * Math.PI * f * i / 44100) * Math.Exp(-i / 44100.0 * 1.2));
        var a = SoundAnalyzer.Analyse(new AudioData { SampleRate = 44100, Channels = [x] }, "808_long.wav");
        Assert.Equal("A1", a.RootNoteName);
        Assert.False(a.IsLoop);
        Assert.Contains("tonal", a.Character);
    }

    [Fact]
    public void Finds_tempo_of_a_drum_loop()
    {
        const double bpm = 124;
        var beat = 60 / bpm;
        var x = Synth.Silence(beat * 8);   // two bars exactly
        var kick = Synth.Kick(1);
        var hat = Synth.Hat(2);
        for (var b = 0; b < 8; b++)
        {
            Mix(x, kick, b * beat);
            Mix(x, hat, b * beat + beat / 2);
        }
        var a = SoundAnalyzer.Analyse(new AudioData { SampleRate = 44100, Channels = [x] }, "groove.wav");
        Assert.True(a.IsLoop);
        Assert.Equal(bpm, a.Bpm);

        // A loop with a tail that isn't a whole number of bars still gets its tempo from the onsets.
        var tail = new float[x.Length + 13000];
        Array.Copy(x, tail, x.Length);
        var b2 = SoundAnalyzer.Analyse(new AudioData { SampleRate = 44100, Channels = [tail] }, "groove loop.wav");
        Assert.InRange(b2.Bpm ?? 0, bpm - 2, bpm + 2);
    }

    static void Mix(float[] x, float[] s, double at)
    {
        var o = (int)(at * 44100);
        for (var i = 0; i < s.Length && o + i < x.Length; i++) x[o + i] += s[i];
    }

    [Fact]
    public void Kicks_are_darker_and_punchier_than_hats()
    {
        var kick = SoundAnalyzer.Analyse(new AudioData { SampleRate = 44100, Channels = [Synth.Kick(3)] });
        var hat = SoundAnalyzer.Analyse(new AudioData { SampleRate = 44100, Channels = [Synth.Hat(3)] });
        Assert.True(kick.Brightness < hat.Brightness);
        Assert.True(hat.Noisiness > kick.Noisiness);
        Assert.Contains("dark", kick.Character);
        Assert.Equal(SoundAnalyzer.FeatureNames.Count, kick.Features.Length);
    }

    [Theory]
    [InlineData(@"D:\Samples\Pack\Drums\Kicks\Hard 01.wav", "Kick")]
    [InlineData(@"D:\Samples\Pack\BD_Punchy.wav", "Kick")]
    [InlineData(@"D:\Samples\Pack\OneShots\SnareRim_03.wav", "Snare")]
    [InlineData(@"D:\Samples\Pack\Hats\Closed\CH 2.wav", "Closed hat")]
    [InlineData(@"D:\Samples\Pack\OHH_Long.wav", "Open hat")]
    [InlineData(@"D:\Samples\Pack\808s\808_F.wav", "808")]
    [InlineData(@"D:\Samples\Pack\Loops\Drum Loops\Groove_120.wav", "Drum loop")]
    [InlineData(@"D:\Samples\Pack\Vox\Chop_Am.wav", "Vocal")]
    [InlineData(@"D:\Samples\Pack\Misc\sample_07.wav", null)]
    public void Types_from_paths(string path, string? type) => Assert.Equal(type, SoundTypes.FromPath(path));
}

public class ReferenceTests
{
    static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "Data", name);

    /// <summary>
    /// Log-mel features match Hugging Face transformers' audio_utils (mel_filter_bank +
    /// spectrogram, as ClapFeatureExtractor calls them) on the same signal. Values were
    /// produced with transformers 5.x in Python; points near the -100 dB floor are skipped.
    /// </summary>
    [Theory]
    [InlineData(true, new[] { 0, 0, 25.4393, 0, 5, 30.8562, 0, 33, 6.6673, 0, 63, -13.1853, 7, 0, -19.6051, 7, 5, 3.6789, 7, 33, -58.8825,
                              300, 5, 3.581, 300, 33, -61.203, 1000, 0, 24.0828, 1000, 5, 30.021, 1000, 33, -1.7287, 1000, 63, -20.684 })]
    [InlineData(false, new[] { 0, 0, 9.969, 0, 5, 21.7922, 0, 33, -12.7109, 0, 63, -41.171, 7, 0, -34.1678, 7, 5, 16.4398, 7, 33, -75.0945,
                               300, 5, 16.4388, 300, 33, -78.3712, 1000, 0, 8.6337, 1000, 5, 21.6211, 1000, 33, -21.4672, 1000, 63, -48.6702 })]
    public void Clap_features_match_transformers(bool fusion, double[] points)
    {
        const int sr = 48000;
        var x = new float[(int)(sr * 2.5)];
        for (var n = 0; n < x.Length; n++)
            x[n] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * n / sr) + 0.25 * Math.Sin(2 * Math.PI * 1234.5 * n / sr) * Math.Exp(-(double)n / sr));
        var fe = new ClapFeatureExtractor { FMin = 50, FMax = 14000, Fusion = fusion };
        var mel = fe.Extract(x, out var longer);
        Assert.False(longer);
        Assert.Equal(fusion ? 4 : 1, mel.Length);
        Assert.Equal(1001, mel[0].Length);
        Assert.Equal(64, mel[0][0].Length);
        for (var i = 0; i < points.Length; i += 3)
            Assert.True(Math.Abs(mel[0][(int)points[i]][(int)points[i + 1]] - points[i + 2]) < 0.05,
                        $"frame {points[i]} mel {points[i + 1]}: {mel[0][(int)points[i]][(int)points[i + 1]]} vs {points[i + 2]}");
    }

    /// <summary>
    /// The C# Basic Pitch port gives the same notes as Spotify's Python package (0.4.0,
    /// ONNX model) on the same 22.05 kHz file: a C major chord, then A3, then D5.
    /// </summary>
    [Fact]
    public void Basic_pitch_matches_python_reference()
    {
        (double Start, double End, int Pitch, double Amp)[] expected =
        [
            (0.0116, 0.9985, 60, 0.744), (0.0116, 0.9985, 64, 0.737), (0.0116, 1.0101, 67, 0.748), (0.0116, 0.6618, 72, 0.329),
            (0.0116, 0.9288, 76, 0.382), (0.0116, 0.685, 91, 0.341), (1.1958, 1.8692, 57, 0.767), (1.9982, 2.8341, 74, 0.765),
            (2.0098, 2.7064, 102, 0.373),
        ];
        var audio = AudioFile.Read(Data("chord-and-melody-22k.wav"));
        Assert.Equal(22050, audio.SampleRate);
        using var t = new NoteTranscriber();
        var notes = t.Transcribe(audio.Mono());
        Assert.Equal(expected.Length, notes.Count);
        foreach (var (e, n) in expected.Zip(notes))
        {
            Assert.Equal(e.Pitch, n.Pitch);
            Assert.InRange(n.Start, e.Start - 0.001, e.Start + 0.001);
            Assert.InRange(n.End, e.End - 0.001, e.End + 0.001);
            Assert.InRange(n.Amplitude, e.Amp - 0.01, e.Amp + 0.01);
        }
        var chords = SoundOrganiser.ChordsFromNotes(notes);
        Assert.Equal("C", chords[0].Name[..1]);
    }

    [Fact]
    public void Bpe_tokenizer_merges_by_rank()
    {
        var vocab = new Dictionary<string, int> { ["<s>"] = 0, ["</s>"] = 2, ["k"] = 3, ["i"] = 4, ["c"] = 5, ["ki"] = 6, ["kick"] = 7, ["ck"] = 8, ["Ġ"] = 9, ["Ġkick"] = 10, ["Ġk"] = 11 };
        var tok = new BpeTokenizer(vocab, [("k", "i"), ("c", "k"), ("ki", "ck"), ("Ġ", "k"), ("Ġk", "i"), ("Ġki", "ck")]);
        Assert.Equal([0, 7, 2], tok.Encode("kick"));
        // Merges follow rank order: k+i, c+k, ki+ck; the leading space (Ġ) never meets "k" again.
        Assert.Equal([0, 7, 9, 7, 2], tok.Encode("kick kick"));
    }
}

public class LearningTests
{
    [Fact]
    public void Classifier_learns_two_clusters()
    {
        var rng = new Random(1);
        var data = new List<TrainingExample>();
        for (var i = 0; i < 200; i++)
        {
            var a = i % 2 == 0;
            var v = new float[5];
            for (var d = 0; d < 5; d++) v[d] = (float)((a ? 1 : -1) * (d == 0 ? 2 : 0) + rng.NextDouble());
            data.Add(new(v, a ? "A" : "B", 1));
        }
        var m = SoundClassifier.Train(data, "features");
        Assert.True(m.HeldOutAccuracy > 0.95);
        Assert.Equal("A", m.Predict([2.5f, 0.5f, 0.5f, 0.5f, 0.5f])[0].Label);
        var back = SoundClassifier.FromJson(m.ToJson())!;
        Assert.Equal("B", back.Predict([-1.5f, 0.5f, 0.5f, 0.5f, 0.5f])[0].Label);
    }

    [Fact]
    public void Organiser_learns_types_suggests_and_builds_kits()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var samples = Path.Combine(root, "Samples");
        Directory.CreateDirectory(Path.Combine(samples, "Pack A", "Kicks"));
        Directory.CreateDirectory(Path.Combine(samples, "Pack A", "Hats"));
        Directory.CreateDirectory(Path.Combine(samples, "Pack A", "Snares"));
        Directory.CreateDirectory(Path.Combine(samples, "Unsorted"));
        for (var i = 0; i < 12; i++)
        {
            Synth.Write(Path.Combine(samples, "Pack A", "Kicks", $"Kick {i:00}.wav"), Synth.Kick(i));
            Synth.Write(Path.Combine(samples, "Pack A", "Hats", $"Closed Hat {i:00}.wav"), Synth.Hat(i));
            Synth.Write(Path.Combine(samples, "Pack A", "Snares", $"Snare {i:00}.wav"), Synth.Snare(i));
        }
        // Unnamed sounds the classifier has to work out from what it learned.
        Synth.Write(Path.Combine(samples, "Unsorted", "sample_01.wav"), Synth.Kick(100));
        Synth.Write(Path.Combine(samples, "Unsorted", "sample_02.wav"), Synth.Hat(101));
        Synth.Write(Path.Combine(samples, "Unsorted", "sample_03.wav"), Synth.Snare(102));

        using var lib = new SoundLibrary(Path.Combine(root, "sounds.db"));
        using var org = new SoundOrganiser(lib);
        Assert.Equal(39, org.Scan([samples]));
        Assert.Equal(0, org.Scan([samples]));   // nothing changed

        string TypeOf(string name) => org.Sounds.Single(s => s.Name == name).Type;
        Assert.Equal("Kick", TypeOf("sample_01"));
        Assert.Equal("Closed hat", TypeOf("sample_02"));
        Assert.Equal("Snare", TypeOf("sample_03"));
        Assert.Equal("learned", org.Sounds.Single(s => s.Name == "sample_01").TypeSource);
        Assert.NotNull(org.TypeModel);

        // A label you set survives retraining.
        var s3 = org.Sounds.Single(s => s.Name == "sample_03");
        org.SetUserType(s3, "Clap");
        org.UpdateTypes();
        org.Reload();
        Assert.Equal("Clap", TypeOf("sample_03"));

        // Sound-alikes of a kick are kicks.
        var kick = org.Sounds.Single(s => s.Name == "Kick 00");
        Assert.All(org.Similar(kick, 5), h => Assert.Equal("Kick", h.Sound.Type));

        // Rediscover: with three kicks used in projects, unused kicks come back, never the used ones.
        org.ApplyUsage([(Path.Combine(samples, "Pack A", "Kicks", "Kick 00.wav"), 3), ("Kick 01.wav", 2), ("Kick 02.wav", 1)]);
        Assert.Equal(3, org.Sounds.Single(s => s.Name == "Kick 00").UsedIn);
        var picks = Rediscover.Suggest(org, new Rediscover.Options(Type: "Kick", Count: 5, Seed: 3));
        Assert.Equal(5, picks.Count);
        Assert.All(picks, p => Assert.Equal(0, p.Sound.UsedIn));
        Assert.All(picks, p => Assert.Equal("Kick", p.Sound.Type));

        // Kits: each pad gets its type, locks hold, the export is written.
        var slots = KitBuilder.DefaultDrumKit().Take(4).ToList();
        KitBuilder.Roll(org, slots, new KitBuilder.Options(Seed: 5));
        Assert.Equal("Kick", slots[0].Sound!.Type);
        Assert.Equal("Snare", slots[1].Sound!.Type);
        Assert.Contains(slots[3].Sound!.Type, new[] { "Closed hat", "Hi-hat" });
        slots[0].Locked = true;
        var keep = slots[0].Sound;
        KitBuilder.Roll(org, slots, new KitBuilder.Options(Seed: 6));
        Assert.Same(keep, slots[0].Sound);
        var folder = KitBuilder.Export(slots, root, "Test kit", new KitBuilder.ExportOptions(ElektronReady: true, Mono: true));
        var first = Directory.GetFiles(folder, "01 *.wav").Single();
        Assert.Equal(48000, AudioFile.Read(first).SampleRate);
        Assert.True(File.Exists(Path.Combine(folder, "kit.txt")));
    }

    [Fact]
    public void Unplugged_sample_drive_keeps_its_sounds()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var drive = Path.Combine(root, "External");
        var local = Path.Combine(root, "Local");
        Directory.CreateDirectory(drive);
        Directory.CreateDirectory(local);
        Synth.Write(Path.Combine(drive, "Kick.wav"), Synth.Kick(1));
        Synth.Write(Path.Combine(local, "Hat.wav"), Synth.Hat(2));
        Synth.Write(Path.Combine(local, "Snare.wav"), Synth.Snare(3));
        using var lib = new SoundLibrary(Path.Combine(root, "sounds.db"));
        using var org = new SoundOrganiser(lib);
        Assert.Equal(3, org.Scan([drive, local]));
        lib.AddTag(org.Sounds.Single(s => s.Name == "Kick").Id, "favourite");

        // The drive is unplugged and a local file is deleted: only the deleted one is forgotten.
        Directory.Move(drive, drive + " (away)");
        File.Delete(Path.Combine(local, "Snare.wav"));
        org.Scan([drive, local]);
        Assert.Equal(new[] { "Hat", "Kick" }, org.Sounds.Select(s => s.Name).Order().ToArray());
        Assert.Single(lib.SoundsTagged("favourite"));

        // Taking the folder out of the list still forgets its sounds.
        org.Scan([local]);
        Assert.Equal(new[] { "Hat" }, org.Sounds.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Learns_your_own_tags()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var samples = Path.Combine(root, "Samples");
        Directory.CreateDirectory(samples);
        for (var i = 0; i < 10; i++)
        {
            Synth.Write(Path.Combine(samples, $"one {i}.wav"), Synth.Kick(i));
            Synth.Write(Path.Combine(samples, $"two {i}.wav"), Synth.Hat(i));
        }
        using var lib = new SoundLibrary(Path.Combine(root, "sounds.db"));
        using var org = new SoundOrganiser(lib);
        org.Scan([samples]);
        // Tag six of the kick-like sounds "boomy"; the model should suggest it for the other kicks only.
        foreach (var s in org.Sounds.Where(s => s.Name.StartsWith("one")).Take(6)) lib.AddTag(s.Id, "boomy");
        var result = org.TrainTags();
        Assert.Contains(result, r => r.Tag == "boomy");
        org.Reload();
        var learned = lib.SoundsTagged("boomy", "learned").Select(id => org.Get(id)!.Name).ToList();
        Assert.NotEmpty(learned);
        Assert.All(learned, n => Assert.StartsWith("one", n));
    }
}
