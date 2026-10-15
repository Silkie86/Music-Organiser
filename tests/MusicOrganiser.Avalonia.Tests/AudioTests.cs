using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MusicOrganiser.Avalonia.Audio;
using NAudio.Wave;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>
/// The player and what it reads. Tests that play need OpenAL Soft's silent output
/// (ALSOFT_DRIVERS=null, which CI sets), so running the tests never makes a sound on a real
/// computer; without it they're skipped.
/// </summary>
public sealed class AudioTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("mo-audio-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A WAV whose every sample says where it is: frame n of channel c is (n % 100000) / 200000 + c / 10.</summary>
    string Wav(string name, double seconds, WaveFormat format)
    {
        var path = Path.Combine(_dir, name);
        using var writer = new WaveFileWriter(path, format);
        var frames = (int)(seconds * format.SampleRate);
        var frame = new float[format.Channels];
        for (var n = 0; n < frames; n++)
        {
            for (var c = 0; c < format.Channels; c++) frame[c] = Sample(n, c);
            writer.WriteSamples(frame, 0, frame.Length);
        }
        return path;
    }

    static float Sample(int frame, int channel) => frame % 100000 / 200000f + channel / 10f;

    static void NeedSilentOutput()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ALSOFT_DRIVERS") == "null",
            "Plays sound: run with ALSOFT_DRIVERS=null for OpenAL's silent output");
        Assert.True(OpenAlPlayer.HasOutput, "The bundled OpenAL Soft didn't load, so the app would have no sound here");
    }

    [Fact]
    public void Channels_past_the_first_two_are_heard_on_both_sides()
    {
        float[][] quad = [[0.1f, 0.2f], [0.3f, 0.4f], [1f, 0f], [0f, 1f]];

        var stereo = OpenAlPlayer.DownmixToStereo(quad);

        Assert.Equal(2, stereo.Length);
        Assert.Equal(0.1f + 0.7071f, stereo[0][0], 4);
        Assert.Equal(0.3f + 0.7071f, stereo[1][0], 4);
        Assert.Equal(0.2f + 0.7071f, stereo[0][1], 4);
        Assert.Equal(0.4f + 0.7071f, stereo[1][1], 4);
        Assert.Equal(0.1f, quad[0][0]);   // the decoded sound itself is left as it was
    }

    [Fact]
    public void A_wav_streams_from_where_you_start_it()
    {
        var path = Wav("ramp.wav", 2.5, WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
        using var source = PcmSource.Open(path, 600);
        var buffer = new float[4];

        source.SeekTo(1.5);
        source.Read(buffer);

        Assert.IsType<StreamSource>(source);
        Assert.Equal(Sample(12000, 0), buffer[0]);
        Assert.Equal(Sample(12001, 0), buffer[1]);
    }

    [Fact]
    public void A_24_bit_wav_with_a_long_header_streams_too()
    {
        var path = Wav("wide24.wav", 1, new WaveFormatExtensible(8000, 24, 2));
        using var source = PcmSource.Open(path, 600);
        var buffer = new float[4];

        source.Read(buffer);

        Assert.IsType<StreamSource>(source);
        Assert.Equal(2, source.Channels);
        Assert.Equal(0f, buffer[0], 3);
        Assert.Equal(0.1f, buffer[1], 3);
    }

    [Fact]
    public void A_quad_wav_streams_folded_into_stereo()
    {
        var path = Wav("quad.wav", 1, new WaveFormat(8000, 16, 4));
        using var source = PcmSource.Open(path, 600);
        var buffer = new float[2];

        source.Read(buffer);

        Assert.Equal(2, source.Channels);
        Assert.Equal(0f + (0.2f + 0.3f) * 0.7071f, buffer[0], 3);
        Assert.Equal(0.1f + (0.2f + 0.3f) * 0.7071f, buffer[1], 3);
    }

    [Fact]
    public void A_64_bit_float_wav_streams()
    {
        var path = Path.Combine(_dir, "double.wav");
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateCustomFormat(WaveFormatEncoding.IeeeFloat, 8000, 1, 64000, 8, 64)))
            for (var n = 0; n < 8000; n++) writer.Write(BitConverter.GetBytes((double)Sample(n, 0)), 0, 8);
        using var source = PcmSource.Open(path, 600);
        var buffer = new float[2];

        source.SeekTo(0.5);
        source.Read(buffer);

        Assert.IsType<StreamSource>(source);
        Assert.Equal(Sample(4000, 0), buffer[0], 5);
        Assert.Equal(Sample(4001, 0), buffer[1], 5);
    }

    [AvaloniaFact]
    public void A_long_file_starts_at_once_and_plays_past_ten_minutes()
    {
        NeedSilentOutput();
        var path = Wav("long.wav", 11 * 60, new WaveFormat(8000, 16, 1));
        using var player = new OpenAlPlayer();

        var clock = Stopwatch.StartNew();
        player.Play(path, startSeconds: 650);
        var started = clock.Elapsed;
        Thread.Sleep(200);

        Assert.True(started < TimeSpan.FromMilliseconds(300), $"Took {started.TotalMilliseconds:0} ms to start");
        Assert.Equal(path, player.Playing);
        Assert.InRange(player.Position ?? 0, 650.1, 650.6);
    }

    [AvaloniaFact]
    public void A_sound_plays_to_its_end_and_says_so()
    {
        NeedSilentOutput();
        var path = Wav("short.wav", 0.3, new WaveFormat(8000, 16, 1));
        using var player = new OpenAlPlayer();
        var stopped = 0;
        player.Stopped += () => stopped++;

        player.Play(path);
        var clock = Stopwatch.StartNew();
        while (player.Playing is not null && clock.Elapsed < TimeSpan.FromSeconds(3)) Thread.Sleep(20);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(player.Playing);
        Assert.Null(player.Position);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.2, 1.5);
        Assert.Equal(1, stopped);
    }

    [AvaloniaFact]
    public void Seeking_moves_playback_and_stopping_says_nothing()
    {
        NeedSilentOutput();
        var path = Wav("three.wav", 3, new WaveFormat(8000, 16, 1));
        using var player = new OpenAlPlayer();
        var stopped = 0;
        player.Stopped += () => stopped++;

        player.Play(path);
        player.Seek(2.0);
        Thread.Sleep(100);
        var position = player.Position;
        player.Stop();
        Dispatcher.UIThread.RunJobs();

        Assert.InRange(position ?? 0, 2.0, 2.4);
        Assert.Null(player.Playing);
        Assert.Equal(0, stopped);
    }

    /// <summary>Plays with the output reported as gone, as OpenAL Soft reports it once a DAW takes the audio interface.</summary>
    static void WithOutput(Func<bool> connected, Func<bool> reopen, Action test)
    {
        OpenAlPlayer.ConnectedOverride = connected;
        OpenAlPlayer.ReopenOverride = reopen;
        try { test(); }
        finally
        {
            OpenAlPlayer.ConnectedOverride = null;
            OpenAlPlayer.ReopenOverride = null;
        }
    }

    [AvaloniaFact]
    public void A_sound_reconnects_and_keeps_time_when_the_output_goes_and_comes_back()
    {
        NeedSilentOutput();
        var path = Wav("three.wav", 3, new WaveFormat(8000, 16, 1));
        using var player = new OpenAlPlayer();
        bool connected = true;
        var reopened = 0;

        WithOutput(() => connected, () => { reopened++; return connected = true; }, () =>
        {
            player.Play(path);
            Thread.Sleep(300);
            connected = false;
            var clock = Stopwatch.StartNew();
            Thread.Sleep(400);
            var position = player.Position;

            Assert.Equal(1, reopened);
            Assert.Equal(path, player.Playing);
            // Picks up from the piece it was playing, then keeps time: no racing through in silence.
            Assert.InRange(position ?? 0, 0.15, 0.3 + clock.Elapsed.TotalSeconds + 0.1);
        });
    }

    [AvaloniaFact]
    public void A_sound_stops_and_says_so_when_the_output_cant_be_got_back()
    {
        NeedSilentOutput();
        var path = Wav("three.wav", 3, new WaveFormat(8000, 16, 1));
        using var player = new OpenAlPlayer();
        var stopped = 0;
        player.Stopped += () => stopped++;
        var connected = true;

        WithOutput(() => connected, () => false, () =>
        {
            player.Play(path);
            Thread.Sleep(100);
            connected = false;
            var clock = Stopwatch.StartNew();
            while (player.Playing is not null && clock.Elapsed < TimeSpan.FromSeconds(1)) Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(player.Playing);
            Assert.Equal(1, stopped);
        });
    }

    [AvaloniaFact]
    public void Playing_says_why_when_the_output_has_gone()
    {
        NeedSilentOutput();
        var path = Wav("short.wav", 0.3, new WaveFormat(8000, 16, 1));
        using var player = new OpenAlPlayer();

        WithOutput(() => false, () => false, () =>
        {
            var error = Assert.Throws<InvalidOperationException>(() => player.Play(path));

            Assert.Contains("taken it over", error.Message);
            Assert.Null(player.Playing);
        });
    }

    [AvaloniaFact]
    public void The_log_says_what_played_and_whether_it_kept_time()
    {
        NeedSilentOutput();
        var path = Wav("logged.wav", 0.3, new WaveFormat(8000, 16, 1));
        using var player = new OpenAlPlayer();

        player.Play(path);
        var clock = Stopwatch.StartNew();
        while (player.Playing is not null && clock.Elapsed < TimeSpan.FromSeconds(3)) Thread.Sleep(20);

        var log = File.ReadAllText(Path.Combine(AudioLog.Folder, "audio.log"));
        Assert.StartsWith(Path.GetTempPath(), AudioLog.Folder);
        Assert.Contains("Output: \"", log);
        Assert.Contains($"Play \"{path}\": streamed by WaveFileReader as 16 bit PCM: 8000Hz 1 channels, {0.3:0.00} s, 8000 Hz, mono", log);
        Assert.Contains("(to its end)", log);
    }

    [AvaloniaFact]
    public void A_midi_preview_plays_from_memory()
    {
        NeedSilentOutput();
        using var player = new OpenAlPlayer();

        player.Play(new float[8000 * 2], 8000, "preview", startSeconds: 1);
        Thread.Sleep(100);

        Assert.Equal("preview", player.Playing);
        Assert.InRange(player.Position ?? 0, 1.0, 1.4);
    }
}
