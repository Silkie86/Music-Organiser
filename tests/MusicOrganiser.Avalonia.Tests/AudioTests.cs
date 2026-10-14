using MusicOrganiser.Avalonia.Audio;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

public sealed class AudioTests
{
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
}
