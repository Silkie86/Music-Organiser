using System.Globalization;
using MusicOrganiser.Avalonia.Views;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>Lengths and times as the Sounds list and the waveform write them.</summary>
public sealed class TimeTextTests
{
    static string Length(double seconds) => (string)new Seconds().Convert(seconds, typeof(string), null, CultureInfo.InvariantCulture);

    [Fact]
    public void A_length_is_rounded_before_it_is_split_into_minutes_and_seconds()
    {
        // Review: 119.6 s showed as "1:60", and 59.96 s as "60s".
        Assert.Equal("2:00", Length(119.6));
        Assert.Equal("1:00", Length(59.96));
        Assert.Equal("10s", Length(9.96));
        Assert.Equal("0.4s", Length(0.4));
        Assert.Equal("12s", Length(12.3));
        Assert.Equal("1:05", Length(65.2));
        Assert.Equal("", new Seconds().Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_waveforms_time_under_the_pointer_never_says_60_seconds()
    {
        // Review: the hover label showed "1:60.0" for 119.96 s.
        Assert.Equal("2:00.0", WaveformView.TimeLabel(119.96));
        Assert.Equal("1:00.0", WaveformView.TimeLabel(59.996));
        Assert.Equal("1:59.9", WaveformView.TimeLabel(119.94));
        Assert.Equal("12.34s", WaveformView.TimeLabel(12.341));
    }
}
