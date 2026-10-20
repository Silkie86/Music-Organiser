using MusicOrganiser.Avalonia.Audio;
using MusicOrganiser.Avalonia.Views;
using MusicOrganiser.Core.LiveTests;

namespace MusicOrganiser.Avalonia.Diagnostics;

/// <summary>The self-check's checks that only the app's window side can make (Settings › Self-check runs them with the rest).</summary>
static class AppChecks
{
    public static IReadOnlyList<(string Area, string Name, Func<CancellationToken, Task<LiveVerdict>> Test)> All { get; } =
    [
        ("App", "Sound output plays", SoundAsync),
        ("App", "Galaxy drawing", _ => Task.FromResult(Graphics())),
    ];

    /// <summary>Plays a quarter of a second of silence through the player the app uses, so nothing is heard.</summary>
    static async Task<LiveVerdict> SoundAsync(CancellationToken ct)
    {
        if (!OpenAlPlayer.HasOutput) return LiveVerdict.Fail("No sound output was found: sounds can't play. Is an audio device plugged in and switched on?");
        using var player = new OpenAlPlayer();
        player.Play(new float[12000], 48000, "self-check silence");
        await Task.Delay(150, ct);
        var position = player.Position;
        player.Stop();
        return position is null
            ? LiveVerdict.Fail("The sound output opened, but the player didn't start.")
            : LiveVerdict.Pass($"Played silence through the default output (it reached {position:0.00} s).");
    }

    static LiveVerdict Graphics() => GalaxyGpu.Available switch
    {
        true => LiveVerdict.Pass("The sound galaxy draws on the graphics card."),
        false => LiveVerdict.Note("The sound galaxy draws on the processor (the graphics card version didn't start, or MO_GALAXY_CPU is set)."),
        _ => LiveVerdict.Skip("The galaxy hasn't been opened since the app started, so it hasn't tried the graphics card yet. Open Sounds › Galaxy, then run this again."),
    };
}
