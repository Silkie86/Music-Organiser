using MusicOrganiser.Core;

namespace MusicOrganiser.Core.Tests;

public class ShellTests
{
    [Fact]
    public void Show_file_on_Linux_names_the_file_as_an_escaped_link()
    {
        var args = Shell.ShowItemsArgs("/home/me/Samples/Kick #2 100%.wav");

        Assert.Contains("org.freedesktop.FileManager1.ShowItems", args);
        Assert.Contains("array:string:file:///home/me/Samples/Kick%20%232%20100%25.wav", args);

        // dbus-send splits an array at its commas, so a comma in the path can't be left as it is.
        Assert.Contains("array:string:file:///home/me/Samples/Kick%2C%20Snare/Hit%2C%202.wav", Shell.ShowItemsArgs("/home/me/Samples/Kick, Snare/Hit, 2.wav"));
    }
}
