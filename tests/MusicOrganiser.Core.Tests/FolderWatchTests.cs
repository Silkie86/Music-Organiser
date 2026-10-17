using MusicOrganiser.Core.Library;
using Xunit;

namespace MusicOrganiser.Core.Tests;

public class FolderWatchTests
{
    static FolderWatch.Changes? Wait(List<FolderWatch.Changes> heard, Func<FolderWatch.Changes, bool> enough)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            lock (heard)
                if (heard.FirstOrDefault(enough) is { } c) return c;
            Thread.Sleep(50);
        }
        return null;
    }

    [Fact]
    public void A_folder_moved_in_from_outside_reports_its_files()
    {
        var root = Directory.CreateTempSubdirectory("mo-watch-").FullName;
        var watched = Directory.CreateDirectory(Path.Combine(root, "Samples")).FullName;
        var pack = Directory.CreateDirectory(Path.Combine(root, "Downloads", "Pack Vol.2", "Kicks")).FullName;
        File.WriteAllBytes(Path.Combine(pack, "Kick 01.wav"), [0]);
        File.WriteAllBytes(Path.Combine(pack, "readme.txt"), [0]);
        var heard = new List<FolderWatch.Changes>();
        using var watch = new FolderWatch([watched], p => p.EndsWith(".wav"), c => { lock (heard) heard.Add(c); }, TimeSpan.FromMilliseconds(200));

        Directory.Move(Path.Combine(root, "Downloads", "Pack Vol.2"), Path.Combine(watched, "Pack Vol.2"));

        var changes = Wait(heard, c => c.Files.Count + c.Overflowed.Count > 0);
        Assert.NotNull(changes);
        Assert.Equal([Path.Combine(watched, "Pack Vol.2", "Kicks", "Kick 01.wav")], changes.Files);
    }

    [Theory]
    [InlineData("/s/Pack Vol.2", true)]
    [InlineData("/s/Drums. Dry", true)]
    [InlineData("/s/Kicks", true)]
    [InlineData("/s/notes.txt", false)]
    [InlineData("/s/Kick.asd", false)]
    [InlineData("/s/backup.flp", false)]
    public void A_deleted_path_with_a_dot_can_still_be_a_folder(string path, bool folder) =>
        Assert.Equal(folder, FolderWatch.LooksLikeFolder(path));
}
