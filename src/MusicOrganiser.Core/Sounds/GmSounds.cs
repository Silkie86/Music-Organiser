using MeltySynth;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Sounds;

/// <summary>
/// Real instrument sounds for previewing MIDI: a General MIDI SoundFont played with MeltySynth (MIT), so a bassline
/// sounds like a bass and chords like an electric piano rather than one stand-in tone (Solomon, 2026-10-17: "i want
/// better sounds to test the midi like general midi"). The SoundFont is GeneralUser GS by S. Christian Collins
/// (free to use and pass on, about 32 MB), downloaded once into the app's folder, or any .sf2 you choose.
/// </summary>
public static class GmSounds
{
    public const string DownloadUrl = "https://raw.githubusercontent.com/mrbumpy409/GeneralUser-GS/main/GeneralUser-GS.sf2";
    public const string DownloadName = "GeneralUser GS";

    /// <summary>The program number used for a drum kit (General MIDI's channel 10).</summary>
    public const int DrumKit = 128;

    /// <summary>Where the downloaded SoundFont goes (tests point it elsewhere, so they don't hear whatever this machine has
    /// downloaded; null: back to the usual place).</summary>
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public static string DefaultFile
    {
        get => _defaultFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
                                            "MusicOrganiser", "sounds", "GeneralUser-GS.sf2");
        set => _defaultFile = value;
    }
    static string? _defaultFile;

    /// <summary>A SoundFont you picked instead (Settings), or null for the downloaded one.</summary>
    public static string? ChosenFile { get; set; }

    /// <summary>The SoundFont played with, or null when there's none yet (the stand-in sounds play).</summary>
    public static string? FileInUse =>
        ChosenFile is { Length: > 0 } chosen && File.Exists(chosen) ? chosen : File.Exists(DefaultFile) ? DefaultFile : null;

    public static bool IsInstalled => FileInUse is not null;

    static readonly object Gate = new();
    static SoundFont? _font;
    static string? _fontPath;

    /// <summary>The SoundFont, read once (and again if another is chosen); null when there's none or it can't be read.</summary>
    public static SoundFont? Font()
    {
        var path = FileInUse;
        if (path is null) return null;
        lock (Gate)
        {
            if (_font is not null && _fontPath == path) return _font;
            try
            {
                _font = new SoundFont(path);
                _fontPath = path;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException or NotSupportedException)
            {
                AppLog.Info("Sounds", $"Couldn't read the SoundFont {path}: {e.Message}");
                _font = null;
                _fontPath = null;
            }
            return _font;
        }
    }

    /// <summary>A SoundFont file starts "RIFF", its size, then "sfbk".</summary>
    public static bool LooksLikeSoundFont(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[12];
            return f.Read(head) == 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("sfbk"u8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Downloads GeneralUser GS into the app's folder, through a .part file so a broken download never counts.</summary>
    public static async Task DownloadAsync(IProgress<(long Done, long? Total)>? progress = null, string? target = null,
                                           string url = DownloadUrl, CancellationToken ct = default)
    {
        target ??= DefaultFile;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        var part = target + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(part))
        {
            var buf = new byte[1 << 16];
            long done = 0, reported = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (done - reported >= 1 << 19) progress?.Report((reported = done, total));
            }
            progress?.Report((done, total));
        }
        if (!LooksLikeSoundFont(part))
        {
            File.Delete(part);
            throw new InvalidDataException("What came down isn't a SoundFont.");
        }
        File.Move(part, target, overwrite: true);
    }

    /// <summary>A sound to pick for a part: its name and General MIDI program (0 to 127, or <see cref="DrumKit"/>).</summary>
    public sealed record Choice(string Name, int Program)
    {
        public override string ToString() => Name;
    }

    public static readonly IReadOnlyList<Choice> Choices =
    [
        new("Piano", 0), new("Bright piano", 1), new("Electric piano", 4), new("Electric piano 2", 5), new("Clavinet", 7),
        new("Vibraphone", 11), new("Marimba", 12), new("Organ", 16), new("Nylon guitar", 24), new("Clean guitar", 27),
        new("Finger bass", 33), new("Picked bass", 34), new("Fretless bass", 35), new("Synth bass", 38), new("Synth bass 2", 39),
        new("Strings", 48), new("Slow strings", 49), new("Synth strings", 50), new("Choir", 52), new("Brass", 61),
        new("Synth brass", 62), new("Flute", 73), new("Square lead", 80), new("Saw lead", 81), new("Harp", 46),
        new("Kalimba", 108), new("Warm pad", 89), new("Poly pad", 90), new("Halo pad", 94), new("Sweep pad", 95),
        new("Drum kit", DrumKit),
    ];

    /// <summary>The sound a kind of part starts with.</summary>
    public static int DefaultFor(string role) => role switch
    {
        MidiRoles.Chords => 4,
        MidiRoles.Melody => 81,
        MidiRoles.Bass => 38,
        MidiRoles.Arp => 46,
        MidiRoles.Drums => DrumKit,
        _ => 0,
    };

    public static Choice ChoiceFor(int program) => Choices.FirstOrDefault(c => c.Program == program) ?? Choices[0];

    /// <summary>
    /// Plays the channels that have a <see cref="MidiChannel.GmProgram"/> through the SoundFont, into
    /// <paramref name="mix"/> (mono at <paramref name="rate"/>). Each gets its own MIDI channel (drums on 10).
    /// </summary>
    internal static void Render(float[] mix, int rate, IReadOnlyList<MidiChannel> channels, double bpm, SoundFont font, CancellationToken ct)
    {
        var synth = new Synthesizer(font, new SynthesizerSettings(rate) { MaximumPolyphony = 128, EnableReverbAndChorus = true });
        var events = new List<(long At, int Order, int Channel, int Command, int Data1, int Data2)>();
        var next = 0;
        foreach (var c in channels)
        {
            if (c.GmProgram is not { } program) continue;
            var drums = program == DrumKit;
            int ch;
            if (drums) ch = 9;
            else
            {
                if (next == 9) next++;
                if (next > 15) continue;
                ch = next++;
            }
            if (!drums) events.Add((0, 0, ch, 0xC0, program, 0));
            events.Add((0, 0, ch, 0xB0, 7, (int)Math.Clamp(c.Volume * 100, 0, 127)));
            foreach (var n in c.Notes)
            {
                long on = (long)(MidiRender.TicksToSeconds(n.Start, c.Ppq, bpm) * rate);
                if (on >= mix.Length) continue;
                long off = (long)(MidiRender.TicksToSeconds(n.Start + Math.Max(1, n.Length), c.Ppq, bpm) * rate);
                events.Add((on, 2, ch, 0x90, Math.Clamp(n.Key, 0, 127), Math.Clamp(n.Velocity, 1, 127)));
                events.Add((Math.Min(off, mix.Length - 1), 1, ch, 0x80, Math.Clamp(n.Key, 0, 127), 0));
            }
        }
        if (events.Count == 0) return;
        // A note ending where the next starts lets go first.
        events.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Order.CompareTo(b.Order));

        var left = new float[mix.Length];
        var right = new float[mix.Length];
        var at = 0;
        foreach (var e in events)
        {
            var to = (int)Math.Min(e.At, mix.Length);
            if (to > at)
            {
                synth.Render(left.AsSpan(at, to - at), right.AsSpan(at, to - at));
                at = to;
                ct.ThrowIfCancellationRequested();
            }
            synth.ProcessMidiMessage(e.Channel, e.Command, e.Data1, e.Data2);
        }
        if (at < mix.Length) synth.Render(left.AsSpan(at), right.AsSpan(at));
        for (var i = 0; i < mix.Length; i++) mix[i] += (left[i] + right[i]) * 0.5f;
    }
}
