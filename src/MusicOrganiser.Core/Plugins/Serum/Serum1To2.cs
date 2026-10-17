using System.Text.Json.Nodes;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Plugins.Serum;

/// <summary>
/// Turns a Serum 1 sound into the same sound for Serum 2. Serum 2 won't take Serum 1's state as its own, so each
/// knob is written into Serum 2's named settings, in the units Serum 2 saves (seconds, percent, amplitude), with
/// Serum 2's "Serum 1 compatibility" on, as Serum 2 does when it opens a Serum 1 preset itself.
/// </summary>
/// <remarks>
/// How each knob is stored was worked out on Solomon's PC (/mnt/project-files/music-organiser-notes/serum): every
/// Serum 2 knob was moved through its range and the saved state read back, both Serums' lists of modulation sources
/// and filter types were read from the plugins, and the result was checked against Serum 2's own conversion of
/// "BA Organ Bass". Parts that are only close (rather than what Serum 2 would write) are listed in
/// <see cref="Result.Rough"/>.
/// </remarks>
public static class Serum1To2
{
    /// <summary>A wavetable or noise sample's length, which Serum 2 keeps next to its file name.</summary>
    public sealed record AudioInfo(int Frames, int Channels, int SampleRate);

    /// <param name="Rough">What came across only roughly or not at all, in words for the person using the app.</param>
    public sealed record Result(Serum2State State, IReadOnlyList<string> Rough);

    /// <param name="findAudio">
    /// Looks up a Serum 1 wavetable or noise sample by its path relative to Serum's Tables or Noises folder (as
    /// Serum 1 saves it, with '/'), for its length. Without it, or when it can't find the file, Serum 2 is left to
    /// work the length out when it loads the file.
    /// </param>
    public static Result Translate(Serum1State s1, Func<string, AudioInfo?>? findAudio = null)
    {
        var t = new Translation(s1, Template(), findAudio);
        t.Run();
        return new(new Serum2State(t.Header, t.Settings), t.Rough);
    }

    /// <summary>
    /// Looks up wavetables and noise samples in Serum 1's own folders (Documents\Xfer\Serum Presets\Tables and
    /// Noises, unless another "Serum Presets" folder is given), for <see cref="Translate"/>.
    /// </summary>
    public static Func<string, AudioInfo?> XferAudio(string? serumPresets = null)
    {
        serumPresets ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Xfer", "Serum Presets");
        return relative =>
        {
            foreach (var folder in new[] { "Tables", "Noises" })
            {
                var path = Path.Combine(serumPresets, folder, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) continue;
                try
                {
                    var audio = AudioFile.Read(path);
                    return new(audio.Length, audio.Channels.Length, audio.SampleRate);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException) { }
            }
            return null;
        };
    }

    static (JsonObject Header, JsonObject Settings) Template()
    {
        using var s = typeof(Serum1To2).Assembly.GetManifestResourceStream("MusicOrganiser.Serum.Serum2FromSerum1.json")
            ?? throw new InvalidOperationException("The Serum 2 template isn't in the app.");
        var o = (JsonObject)JsonNode.Parse(s)!;
        return ((JsonObject)o["header"]!.DeepClone(), (JsonObject)o["settings"]!.DeepClone());
    }

    /// <summary>
    /// Serum 1's modulation sources, by number, as Serum 2 numbers them (read from both plugins' own tables).
    /// Serum 2 has an Env 4, LFOs 9 and 10 and four more macros, so later sources move along; Serum 1's two chaos
    /// sources become LFOs 9 and 10, which Serum 2 sets to Lorenz and Rossler for a Serum 1 sound.
    /// </summary>
    static int Source(int s1) => s1 switch
    {
        <= 4 => s1,             // none, mod wheel, Env 1 to 3
        <= 12 => s1 + 1,        // LFO 1 to 8
        <= 16 => s1 + 3,        // velocity, note, aftertouch, poly aftertouch
        17 => 14,               // Chaos 1 (LFO 9)
        18 => 15,               // Chaos 2 (LFO 10)
        <= 27 => s1 + 1,        // noise oscillator, the four note-on randoms, Macro 1 to 4
        <= 33 => s1 + 5,        // pitch bend, MPE X/Y/Z, release velocity, fixed
        _ => -1,
    };

    /// <summary>
    /// Serum 1's 96 filter types, by number, as Serum 2 names them in its settings. Serum 2 keeps the same order and
    /// adds 11 of its own after them.
    /// </summary>
    internal static readonly string[] FilterTypes =
    [
        "MgL6", "MgL12", "MgL18", "MgL24", "L6", "L12", "L18", "L24", "H6", "H12", "H18", "H24", "B12", "B24", "P12", "P24",
        "N12", "N24", "LH6", "LH12", "LB12", "LP12", "LN12", "HB12", "HP12", "HN12", "BP12", "BN12", "PP12", "PN12", "NN12",
        "LBH12", "LBH24", "LPH12", "LPH24", "LNH12", "LNH24", "BPN12", "BPN24", "CombP", "CombN", "CombL6P", "CombL6N",
        "CombH6P", "CombH6N", "CombHL6P", "CombHL6N", "FlangeP", "FlangeN", "FlangeL6P", "FlangeL6N", "FlangeH6P",
        "FlangeH6N", "FlangeHL6P", "FlangeHL6N", "Phase12P", "Phase12N", "Phase24P", "Phase24N", "Phase36P", "Phase36N",
        "Phase48P", "Phase48N", "Phase48L6P", "Phase48L6N", "Phase48H6P", "Phase48H6N", "Phase48HL6P", "Phase48HL6N",
        "FlangePhase12HL6P", "FlangePhase12HL6N", "LEQ6", "LEQ12", "BEQ12", "HEQ6", "HEQ12", "RM", "RMT", "SNH1", "SNH2",
        "Combs", "Allpasses", "Reverb1", "Scream", "ZDF_A", "ADD_BASS", "FormantONE", "FormantTWO", "FormantTWB",
        "BandReject", "DistComb1LP", "DistComb1BP", "DistComb2LP", "DistComb2BP", "Scream3LP", "Scream3BP",
    ];

    /// <summary>Where a Serum 1 knob ends up in Serum 2, so a modulation row aimed at it can be aimed there too.</summary>
    sealed record Target(string Module, int ModuleId, string Param, int ParamId, Func<double, double> Value, string? Sub = null);

    sealed class Translation(Serum1State s1, (JsonObject Header, JsonObject Settings) template, Func<string, AudioInfo?>? findAudio)
    {
        public JsonObject Header { get; } = template.Header;
        public JsonObject Settings { get; } = template.Settings;
        public List<string> Rough { get; } = [];

        readonly Dictionary<int, Target> _targets = [];

        double K(int index) => s1.Knob(index);
        static double Pct(double x) => 100 * x;
        static double Bi(double x, double range) => (2 * x - 1) * range;
        static double Steps(double x, double range) => Choice(x, (int)(2 * range)) - range;

        /// <summary>Which of a Serum 1 list's choices a knob is on: Serum 1 rounds to the nearest of its steps.</summary>
        static int Choice(double x, int last) => (int)Math.Clamp(Math.Round(x * last, MidpointRounding.AwayFromZero), 0, last);
        static double Time(double x) => 32 * Math.Pow(x, 5);
        /// <summary>Serum 1's switches read "on" for any value above 0.</summary>
        static double On(double x) => x > 0 ? 1 : 0;

        JsonObject Module(string name) => (JsonObject)Settings[name]!;

        static JsonObject Plain(JsonObject module)
        {
            if (module["plainParams"] is JsonObject p) return p;
            p = new JsonObject();
            module["plainParams"] = p;
            return p;
        }

        /// <summary>Sets a value unless it's Serum 2's own default for a Serum 1 sound, which Serum 2 leaves out.</summary>
        static void Put(JsonObject module, string key, double value, double? byDefault = null)
        {
            if (byDefault is { } d && Math.Abs(value - d) < 1e-9) return;
            Plain(module)[key] = value;
        }

        static void Put(JsonObject module, string key, string value, string? byDefault = null)
        {
            if (value == byDefault) return;
            Plain(module)[key] = value;
        }

        /// <summary>Writes a knob and remembers where it went, for modulation rows aimed at it.</summary>
        void Knob(int s1Knob, JsonObject module, string moduleType, int moduleId, string key, int paramId,
            Func<double, double> value, double? byDefault = null)
        {
            Put(module, key, value(K(s1Knob)), byDefault);
            _targets[s1Knob] = new(moduleType, moduleId, key, paramId, value);
        }

        public void Run()
        {
            Global();
            Oscillator(0, vol: 1, enable: 212, pitchTrack: 164, warpMenu: 168, uniLR: 172, uniWarp: 174, uniWTPos: 176, uniStack: 178, table: s1.OscATable);
            Oscillator(1, vol: 14, enable: 213, pitchTrack: 165, warpMenu: 169, uniLR: 173, uniWarp: 175, uniWTPos: 177, uniStack: 179, table: s1.OscBTable);
            Noise();
            Sub();
            Routing();
            Filter();
            Envelopes();
            Lfos();
            Macros();
            Fx();
            Mods();
            Tidy(Settings);
        }

        void Global()
        {
            var g = Module("Global0");
            // Serum 2 turned Serum 1's master 0.7 into 0.293 (its own default is 0.25). Serum 1's master is an amplitude of
            // knob³; scaling that by the one known pair is a guess for other values.
            Knob(0, g, "Global", 0, "kParamMasterVolume", 0, x => 0.8541 * x * x * x, 0.25);
            if (Math.Abs(K(0) - 0.7) > 0.01) Rough.Add("the master volume");
            Knob(80, g, "Global", 0, "kParamMasterTuning", 1, x => x, 0.5);
            Knob(222, g, "Global", 0, "kParamVoiceAmp", 2, x => x, 0.5);
            Knob(65, g, "Global", 0, "kParamPortamentoTime", 3, x => 8 * Math.Pow(x, 5), 0);
            Knob(66, g, "Global", 0, "kParamPortamentoCurve", 4, x => Bi(x, 100), 0);
            Knob(166, g, "Global", 0, "kParamBendRangeUp", 5, x => Steps(x, 24), 2);
            Knob(167, g, "Global", 0, "kParamBendRangeDn", 6, x => Steps(x, 24), -2);
            Knob(217, g, "Global", 0, "kParamModWheel", 8, Pct, 0);
        }

        void Oscillator(int n, int vol, int enable, int pitchTrack, int warpMenu, int uniLR, int uniWarp, int uniWTPos, int uniStack, string table)
        {
            var o = Module($"Oscillator{n}");
            var wt = (JsonObject)o[$"WTOsc{n}"]!;
            // Serum 1 keeps each oscillator's 13 knobs together: level, pan, octave, semi, fine, unison, detune, blend,
            // warp, coarse pitch, table position, random phase, phase.
            Knob(vol, o, "Oscillator", n, "kParamVolume", 1, x => x * x);
            Knob(vol + 1, o, "Oscillator", n, "kParamPan", 2, x => Bi(x, 50), 0);
            Knob(vol + 2, o, "Oscillator", n, "kParamOctave", 3, x => Steps(x, 4), 0);
            Knob(vol + 3, o, "Oscillator", n, "kParamPitch", 4, x => Steps(x, 12), 0);
            Knob(vol + 4, o, "Oscillator", n, "kParamFine", 5, x => Bi(x, 100), 0);
            Knob(vol + 5, o, "Oscillator", n, "kParamUnison", 24, x => Math.Round(1 + 15 * x), 1);
            Knob(vol + 6, o, "Oscillator", n, "kParamDetune", 26, x => x * x, 0.25);
            Knob(vol + 7, o, "Oscillator", n, "kParamDetuneWid", 27, Pct, 75);
            Knob(vol + 9, o, "Oscillator", n, "kParamCoarsePit", 6, x => Bi(x, 64), 0);
            Knob(pitchTrack, o, "Oscillator", n, "kParamPitchTrack", 9, On, 1);
            Knob(uniLR, o, "Oscillator", n, "kParamUnisonStereo", 28, Pct, 100);
            Knob(uniWarp, o, "Oscillator", n, "kParamUnisonWarp", 31, x => Bi(x, 100), 0);
            Knob(enable, o, "Oscillator", n, "kParamEnable", 0, On, n == 0 ? 1 : 0);
            Put(o, "kParamUnisonStack", UnisonStacks[Choice(K(uniStack), UnisonStacks.Length - 1)], UnisonStacks[0]);

            // The wavetable's own settings sit in its WTOsc part.
            Knob(vol + 8, wt, "WTOsc", n, "kParamWarp", 0, x => x, 0);
            // Serum 2 saves the position as a frame number from 1, and Serum 1's knob sweeps the whole table, so the
            // frame count is needed (Serum's tables are 2048-sample frames); without the file it's a guess.
            var path = table.Replace('\\', '/');
            var audio = table.Length > 0 ? findAudio?.Invoke(path.TrimStart('/')) : null;
            var frames = audio is { } found ? Math.Max(1, found.Frames / 2048) : 256;
            Knob(vol + 10, wt, "WTOsc", n, "kParamTablePos", 6, x => 1 + (frames - 1) * x, 1);
            Knob(vol + 11, wt, "WTOsc", n, "kParamRandomPhase", 9, Pct);
            Knob(vol + 12, wt, "WTOsc", n, "kParamInitialPhase", 8, x => 360 * x, 180);
            Knob(uniWTPos, wt, "WTOsc", n, "kParamUnisonWTPos", 10, x => Bi(x, 100), 0);
            if (audio is null && K(vol + 10) > 0.001) Rough.Add($"oscillator {(char)('A' + n)}'s wavetable position");
            var warp = Choice(K(warpMenu), WarpModes.Length);
            if (warp < WarpModes.Length) Put(wt, "kParamWarpMenu", WarpModes[warp], WarpModes[0]);
            if (warp is 2 or 3 or 12) Rough.Add($"oscillator {(char)('A' + n)}'s warp mode");

            if (table.Length > 0)
            {
                // A table of the user's own starts with '/'; its waves are inside Serum 1's state, not in a file.
                wt["relativePathToWT"] = path;
                if (audio is { } a) Audio(wt, a);
                if (path.StartsWith('/')) Rough.Add($"oscillator {(char)('A' + n)}'s own wavetable ({path.TrimStart('/')})");
            }
        }

        static void Audio(JsonObject o, AudioInfo a)
        {
            o["numChannels"] = (long)a.Channels;
            o["numFrames"] = (long)a.Frames;
            o["sampleRate"] = (long)a.SampleRate;
        }

        void Noise()
        {
            var o = Module("Oscillator3");
            var noise = (JsonObject)o["NoiseOsc3"]!;
            Knob(27, o, "Oscillator", 3, "kParamVolume", 1, x => x * x);
            Knob(30, o, "Oscillator", 3, "kParamPan", 2, x => Bi(x, 50), 0);
            Knob(214, o, "Oscillator", 3, "kParamEnable", 0, On, 0);
            Knob(28, noise, "NoiseOsc", 3, "kParamColor", 0, x => x);
            Knob(29, noise, "NoiseOsc", 3, "kParamFine", 1, x => Bi(x, 1));
            Knob(32, noise, "NoiseOsc", 3, "kParamInitialPhase", 2, Pct);
            Knob(31, noise, "NoiseOsc", 3, "kParamRandomPhase", 3, Pct);
            if (s1.NoiseSample.Length > 0)
            {
                var path = s1.NoiseSample.Replace('\\', '/');
                noise["relativePathToNoiseSample"] = path;
                if (findAudio?.Invoke(path.TrimStart('/')) is { } a) Audio(noise, a);
            }
            Put(noise, "kParamOneShot", s1.NoiseOneShot ? 1 : 0, 0);
            if (K(214) > 0 && !s1.NoiseFlagsAsChecked) Rough.Add("whether the noise plays once or loops");
        }

        /// <summary>
        /// Serum 1's 23 warp modes, in its order, by the names Serum 2 saves them under (read from Serum 2 itself). The
        /// FM, AM and RM modes take the other oscillator. Serum 2 saves all three of Serum 1's sync modes (Sync, Sync 1/2
        /// Win., Sync Window) as kSync, even when given its other sync names, so the two windowed ones may sound
        /// different; that Mirror is kDLM is a guess (Serum 2 has no kMirror). Those are listed as rough.
        /// </summary>
        static readonly string[] WarpModes =
        [
            "kNoWarp", "kSync", "kSync", "kSync", "kBendPos", "kBendNeg", "kBendPosNeg", "kPWM",
            "kASYMPos", "kASYMNeg", "kASYMPosNeg", "kFlip", "kDLM", "kRemap_1", "kRemap_2", "kRemap_3", "kRemap_4",
            "kQuantize", "kFM_OSC", "kAM_OSC", "kRM_OSC", "kFM_NOISE", "kFM_SUB",
        ];

        static readonly string[] SubShapes = ["kSine", "kRoundRect", "kTriangle", "kSaw", "kSquare", "kPulse"];

        /// <summary>Unison stacking, in the same order in both Serums (off, 12 ×1 to ×3, 12+7 ×1 to ×3, centre −12, −24).</summary>
        static readonly string[] UnisonStacks =
            ["kNoUnisonStack", "kOctave1", "kOctave2", "kOctave3", "kOctaveFifth1", "kOctaveFifth2", "kOctaveFifth3", "kCenter12", "kCenter24"];

        void Sub()
        {
            var o = Module("Oscillator4");
            var sub = (JsonObject)o["SubOsc4"]!;
            Knob(33, o, "Oscillator", 4, "kParamVolume", 1, x => x * x);
            Knob(34, o, "Oscillator", 4, "kParamPan", 2, x => Bi(x, 50), 0);
            Knob(171, o, "Oscillator", 4, "kParamOctave", 3, x => Steps(x, 4), 0);
            Knob(215, o, "Oscillator", 4, "kParamEnable", 0, On, 0);
            Put(sub, "kParamShape", SubShapes[Choice(K(170), SubShapes.Length - 1)], "kSine");
        }

        void Routing()
        {
            // Serum 2 sends oscillator A through the filter unless told otherwise, and the others straight out.
            int[] toFilter = [40, 41, 42, 43];
            for (var n = 0; n < toFilter.Length; n++)
            {
                var on = K(toFilter[n]) > 0;
                if (on == (n == 0)) continue;
                Put(Module($"RoutingSlot{n}"), "kParamRoutingDest", on ? "kRoutingDestFilter" : "kRoutingDestMaster");
                if (!on) Rough.Add("oscillator A playing past the filter");
            }
        }

        void Filter()
        {
            var f = Module("VoiceFilter0");
            Knob(216, f, "VoiceFilter", 0, "kParamEnable", 0, On, 0);
            Put(f, "kParamType", FilterTypes[Choice(K(44), FilterTypes.Length - 1)], "MgL12");
            Knob(45, f, "VoiceFilter", 0, "kParamFreq", 3, x => x, 0.5);
            Knob(46, f, "VoiceFilter", 0, "kParamReso", 4, Pct, 10);
            Knob(47, f, "VoiceFilter", 0, "kParamDrive", 5, Pct, 0);
            Knob(48, f, "VoiceFilter", 0, "kParamVar", 6, Pct, 0);
            Knob(49, f, "VoiceFilter", 0, "kParamWet", 1, Pct, 100);
            Knob(50, f, "VoiceFilter", 0, "kParamStereo", 7, Pct, 50);
        }

        void Envelopes()
        {
            // Serum 1's Env 1 to 3 (attack, hold, decay, sustain, release; then three curves each) are Serum 2's first three.
            (int Knobs, int Curves)[] envs = [(35, 71), (51, 74), (56, 77)];
            for (var n = 0; n < envs.Length; n++)
            {
                var (k, c) = envs[n];
                var e = Module($"Env{n}");
                Knob(k, e, "Env", n, "kParamAttack", 0, Time);
                Knob(k + 1, e, "Env", n, "kParamHold", 1, Time, 0);
                Knob(k + 2, e, "Env", n, "kParamDecay", 2, Time, 1);
                Knob(k + 3, e, "Env", n, "kParamSustain", 3, x => x, 1);
                Knob(k + 4, e, "Env", n, "kParamRelease", 4, Time);
                Knob(c, e, "Env", n, "kParamCurve1", 5, Pct);
                Knob(c + 1, e, "Env", n, "kParamCurve2", 6, Pct);
                Knob(c + 2, e, "Env", n, "kParamCurve3", 7, Pct);
            }
        }

        void Lfos()
        {
            // Serum 1's LFO 1 to 4 knobs come first, its LFO 5 to 8 later on.
            int[] rate = [61, 62, 63, 64, 260, 261, 262, 263], smooth = [223, 224, 225, 226, 264, 265, 266, 267];
            for (var n = 0; n < 8; n++)
            {
                var l = Module($"LFO{n}");
                Knob(rate[n], l, "LFO", n, "kParamRate", 0, x => 100 * Math.Pow(x, 4), 6.25);
                Knob(smooth[n], l, "LFO", n, "kParamSmooth", 1, Pct, 0);
                Knob(273 + n, l, "LFO", n, "kParamRise", 2, x => 4 * x, 0);
                Knob(281 + n, l, "LFO", n, "kParamDelay", 3, x => 4 * x, 0);
            }
            // Serum 1's chaos sources are Serum 2's LFO 9 and 10, run at a tenth of the rate times ten.
            for (var n = 0; n < 2; n++)
            {
                var l = Module($"LFO{8 + n}");
                Knob(69 + n, l, "LFO", 8 + n, "kParamRate", 0, x => 100 * Math.Pow(x, 5));
                Knob(67 + n, l, "LFO", 8 + n, "kParamBeatSync", 13, On);
            }
            if (s1.ModSlots.Any(m => m.Source is >= 5 and <= 12)) Rough.Add("the LFO shapes (kept at Serum 1's starting triangle)");
        }

        void Macros()
        {
            for (var n = 0; n < 4; n++)
                Knob(218 + n, Module($"Macro{n}"), "Macro", n, "kParamValue", 0, Pct, 0);
        }

        /// <summary>One of Serum 1's effects: its switch, its Serum 2 name and number, and how to fill it in.</summary>
        sealed record Effect(int Enable, string Name, int Type, Action<JsonObject> Fill);

        void Fx()
        {
            // Serum 1's effects in its rack's starting order; the order it was left in is kept outside the knobs.
            Effect[] rack =
            [
                new(163, "FXHyperD", 9, p =>
                {
                    Set(p, "kParamWet", Pct(K(147)));
                    Set(p, "kParamRate", Pct(K(148)));
                    Set(p, "kParamDetune", Pct(K(149)));
                    Set(p, "kParamUnison", Choice(K(150), 7));
                    Set(p, "kParamRetrig", On(K(151)));
                    Set(p, "kParamDimESize", Pct(K(152)));
                    Set(p, "kParamDimEWet", Pct(K(153)));
                }),
                new(154, "FXDistortion", 0, p =>
                {
                    Set(p, "kParamWet", Pct(K(96)));
                    Set(p, "kParamDrive", Pct(K(97)));
                    Set(p, "kParamLPHP", Pct(K(98)));
                    Set(p, "kParamFreq", K(100));
                    Set(p, "kParamBW", 0.075 + 7.5 * K(101) * K(101));
                    Set(p, "kParamPrePost", Choice(K(102), 2));
                }),
                new(155, "FXFlanger", 1, p =>
                {
                    Set(p, "kParamWet", Pct(K(103)));
                    Set(p, "kParamBeatSync", On(K(104)));
                    Set(p, "kParamRate", 20 * Math.Pow(K(105), 4));
                    Set(p, "kParamDepth", Pct(K(106)));
                    Set(p, "kParamFeedback", Pct(K(107)));
                    Set(p, "kParamWidth", 360 * K(108));
                }),
                new(156, "FXPhaser", 2, p =>
                {
                    Set(p, "kParamWet", Pct(K(109)));
                    Set(p, "kParamBeatSync", On(K(110)));
                    Set(p, "kParamRate", 20 * Math.Pow(K(111), 4));
                    Set(p, "kParamDepth", Pct(K(112)));
                    Set(p, "kParamFreq", 20 * Math.Pow(900, K(113)));
                    Set(p, "kParamFeedback", Pct(K(114)));
                    Set(p, "kParamWidth", 360 * K(115));
                }),
                new(157, "FXChorus", 3, p =>
                {
                    Set(p, "kParamWet", Pct(K(116)));
                    Set(p, "kParamBeatSync", On(K(117)));
                    Set(p, "kParamRate", 20 * Math.Pow(K(118), 4));
                    Set(p, "kParamDelay", 20 * K(119) * K(119));
                    Set(p, "kParamDelay2", 20 * K(120) * K(120));
                    Set(p, "kParamDepth", 26 * K(121) * K(121));
                    Set(p, "kParamFeedback", 95 * K(122));
                    Set(p, "kParamFilt", 50 * Math.Pow(400, K(123)));
                }),
                new(158, "FXDelay", 4, p =>
                {
                    Set(p, "kParamWet", Pct(K(124)));
                    Set(p, "kParamFreq", 40 * Math.Pow(450, K(125)));
                    Set(p, "kParamBW", 0.75 + 7.5 * K(126));
                    Set(p, "kParamBeatSync", On(K(127)));
                    Set(p, "kParamLink", On(K(128)));
                    // A synced time is saved some other way: Serum 2 turned every one sent like this into 0.501.
                    if (K(127) > 0) Rough.Add("the delay's synced time");
                    else
                    {
                        Set(p, "kParamTimeL", K(129));
                        Set(p, "kParamTimeR", K(130));
                    }
                    Set(p, "kParamMode", Choice(K(131), 2));
                    Set(p, "kParamFeedback", Pct(K(132)));
                    Set(p, "kParamOffsetL", 0.5 + K(133));
                    Set(p, "kParamOffsetR", 0.5 + K(134));
                }),
                new(159, "FXComp", 5, p =>
                {
                    Set(p, "kParamThresh", K(135));
                    Set(p, "kParamAttack", 0.1 + 999.9 * K(137) * K(137));
                    Set(p, "kParamRelease", 0.1 + 999.9 * K(138) * K(138));
                    Set(p, "kParamMultiband", K(140) >= 0.5 ? 1 : 0);
                    Set(p, "kParamWet", Pct(K(269)));
                }),
                new(160, "FXReverb", 6, p =>
                {
                    Set(p, "kParamWet", Pct(K(81)));
                    Set(p, "kParamSize", Pct(K(82)));
                    // Serum 2 made Serum 1's decay of 0.12 into 28 (0.12^0.6); the curve is a guess beyond that.
                    Set(p, "kParamFeedback", 100 * Math.Pow(K(83), 0.6));
                    Set(p, "kParamFreqB", Pct(K(86)));
                    Set(p, "kParamWidth", Pct(K(87)));
                    Set(p, "kParamDelay", 0);
                    Set(p, "kParamPreDelay", 0.0036);
                }),
                new(161, "FXEQ", 7, p =>
                {
                    Set(p, "kParamFreq1", 22 * Math.Pow(20000 / 22.0, K(88)));
                    Set(p, "kParamFreq2", 22 * Math.Pow(20000 / 22.0, K(89)));
                    Set(p, "kParamReso1", Pct(K(90)));
                    Set(p, "kParamReso2", Pct(K(91)));
                    Set(p, "kParamGain1", Bi(K(92), 24));
                    Set(p, "kParamGain2", Bi(K(93), 24));
                    Set(p, "kParamType1", Choice(K(94), 2));
                    Set(p, "kParamType2", Choice(K(95), 2));
                }),
                new(162, "FXFilter", 8, p =>
                {
                    Set(p, "kParamWet", Pct(K(141)));
                    Set(p, "kParamType", FilterTypes[Choice(K(142), FilterTypes.Length - 1)]);
                    Set(p, "kParamFreq", K(143));
                    Set(p, "kParamReso", Pct(K(144)));
                    Set(p, "kParamDrive", Pct(K(145)));
                    Set(p, "kParamVar", Pct(K(146)));
                }),
            ];
            var list = (JsonArray)Module("FXRack0")["FX"]!;
            foreach (var fx in s1.FxOrder.Select(i => rack[i]).Where(fx => K(fx.Enable) > 0))
            {
                var p = new JsonObject();
                fx.Fill(p);
                list.Add(new JsonObject { [fx.Name] = new JsonObject { ["plainParams"] = p }, ["type"] = (long)fx.Type });
                if (fx.Name != "FXReverb") Rough.Add($"the {fx.Name[2..]} effect's settings");
            }
            if (K(160) > 0) Rough.Add("the reverb's decay and spin");
        }

        static void Set(JsonObject p, string key, double value) => p[key] = value;
        static void Set(JsonObject p, string key, string value) => p[key] = value;

        void Mods()
        {
            foreach (var m in s1.ModSlots)
            {
                var source = Source(m.Source);
                if (source < 0 || !_targets.TryGetValue(m.Destination, out var to))
                {
                    Rough.Add($"modulation row {m.Index + 1}");
                    continue;
                }
                var slot = Module($"ModSlot{m.Index}");
                slot["destModuleID"] = (long)to.ModuleId;
                slot["destModuleParamID"] = (long)to.ParamId;
                slot["destModuleParamName"] = to.Param;
                slot["destModuleTypeString"] = to.Module;
                Put(slot, "kParamAmount", 100.0 * m.Amount);
                if (m.Bipolar) Put(slot, "kParamBipolar", 1.0);
                // Rows 1 to 16 have their output knob with the first knobs, 17 to 32 with the later ones.
                var outKnob = m.Index < 16 ? 181 + 2 * m.Index : 229 + 2 * (m.Index - 16);
                Put(slot, "kParamOut", Pct(K(outKnob)), 100);
                slot["source"] = new JsonArray((long)source, 0L);
            }
        }

        /// <summary>A module left with no values reads "default", as Serum 2 writes it; the rest are in name order.</summary>
        static void Tidy(JsonObject o)
        {
            foreach (var (key, value) in o.ToList())
            {
                if (value is not JsonObject child) continue;
                if (key == "plainParams")
                {
                    if (child.Count == 0) { o[key] = "default"; continue; }
                    var sorted = child.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value?.DeepClone())).ToList();
                    child.Clear();
                    foreach (var (k, v) in sorted) child[k] = v;
                    continue;
                }
                Tidy(child);
            }
        }
    }
}
