using Avalonia.Headless.XUnit;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

public sealed class PlatformTests
{
    /// <summary>The MIDI window's and Sound Lab's clocks only tick while something plays.</summary>
    [AvaloniaFact]
    public void A_timer_waits_for_Start()
    {
        var ticks = 0;
        var timer = (AvaloniaTimer)new AvaloniaUi().CreateTimer(TimeSpan.FromMilliseconds(30), () => ticks++);

        Assert.False(timer.IsRunning);
        timer.Start();
        Assert.True(timer.IsRunning);
        timer.Stop();
        Assert.False(timer.IsRunning);
        Assert.Equal(0, ticks);
    }
}
