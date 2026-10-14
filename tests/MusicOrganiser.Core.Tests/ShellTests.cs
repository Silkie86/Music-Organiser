using MusicOrganiser.Core;

namespace MusicOrganiser.Core.Tests;

public class ShellTests
{
    [Fact]
    public void Show_file_on_Linux_names_the_file_as_an_escaped_link()
    {
        var args = Shell.ShowItemsArgs("/home/sol/Samples/Kick #2 100%.wav");

        Assert.Contains("org.freedesktop.FileManager1.ShowItems", args);
        Assert.Contains("array:string:file:///home/sol/Samples/Kick%20%232%20100%25.wav", args);
    }
}
