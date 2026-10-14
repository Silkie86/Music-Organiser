using MusicOrganiser.Core.DawLink;

namespace MusicOrganiser.Core.Tests;

/// <summary>Installing the Bitwig and FL Studio scripts from Settings.</summary>
public class DawScriptTests
{
    [Fact]
    public void ShipsBothScriptsNextToTheApp()
    {
        Assert.True(File.Exists(DawScripts.Bitwig().Source));
        Assert.True(File.Exists(DawScripts.FlStudio([]).Source));
    }

    [Fact]
    public void InstallsTheBitwigScriptAndSaysWhenItsOutOfDate()
    {
        var user = Fixtures.TempDir();
        var script = DawScripts.Bitwig(user);
        Assert.Equal(ScriptState.NotInstalled, script.State);

        script.Install();
        Assert.Equal(ScriptState.Installed, script.State);
        var installed = Path.Combine(user, "Controller Scripts", "Music Organiser", "MusicOrganiser.control.js");
        Assert.Equal(File.ReadAllText(script.Source), File.ReadAllText(installed));

        File.AppendAllText(installed, "// an older version\n");
        Assert.Equal(ScriptState.Outdated, script.State);
        // The same script with Windows line endings (from a checkout that converts them) isn't a new version.
        File.WriteAllText(installed, File.ReadAllText(script.Source).ReplaceLineEndings("\r\n"));
        Assert.Equal(ScriptState.Installed, script.State);
    }

    [Fact]
    public void PutsTheBitwigScriptInTheChosenUserFolder()
    {
        // Documents in OneDrive and an older one at home can both have a Bitwig Studio folder; Bitwig uses one.
        var root = Fixtures.TempDir();
        var chosen = Path.Combine(root, "OneDrive", "Documents", "Bitwig Studio");
        var other = Path.Combine(root, "Documents", "Bitwig Studio");
        Directory.CreateDirectory(Path.Combine(chosen, "Library"));
        Directory.CreateDirectory(Path.Combine(other, "Projects"));
        Assert.Equal([chosen], DawScripts.BitwigUserFolders(chosen));
        Assert.True(DawScripts.IsBitwigUserFolder(chosen));
        Assert.True(DawScripts.IsBitwigUserFolder(Path.Combine(root, "Music")) is false);
        Directory.CreateDirectory(Path.Combine(root, "Music", "Projects"));
        Assert.True(DawScripts.IsBitwigUserFolder(Path.Combine(root, "Music")));   // moved and renamed, with its folders

        DawScripts.Bitwig(DawScripts.BitwigUserFolders(chosen)).Install();
        Assert.True(File.Exists(Path.Combine(chosen, "Controller Scripts", "Music Organiser", "MusicOrganiser.control.js")));
        Assert.False(Directory.Exists(Path.Combine(other, "Controller Scripts")));

        // With no choice, every Bitwig Studio folder found (only ones that are there).
        var both = DawScripts.Bitwig([chosen, other]);
        both.Install();
        Assert.Equal(2, both.InstalledIn.Count);
        Assert.All(DawScripts.BitwigUserFolders(), f => Assert.True(Directory.Exists(f)));
    }

    [Fact]
    public void TheBitwigExtensionTakesOverFromTheScript()
    {
        var user = Fixtures.TempDir();
        DawScripts.Bitwig(user).Install();
        var script = Path.Combine(user, "Controller Scripts", "Music Organiser", "MusicOrganiser.control.js");
        Assert.True(File.Exists(script));

        // Without a built extension next to the app, Settings keeps installing the script.
        Assert.Equal("MusicOrganiser.control.js", DawScripts.BitwigLink([user]).FileName);

        var built = Path.Combine(Fixtures.TempDir(), "MusicOrganiser.bwextension");
        File.WriteAllBytes(built, [0x50, 0x4B, 3, 4, 1, 2, 3]);
        var extension = DawScripts.BitwigExtension([user], built);
        Assert.True(extension.Binary);
        Assert.Equal(ScriptState.NotInstalled, extension.State);
        extension.Install();
        Assert.Equal(ScriptState.Installed, extension.State);
        Assert.Equal(File.ReadAllBytes(built), File.ReadAllBytes(Path.Combine(user, "Extensions", "MusicOrganiser.bwextension")));
        // The script goes, with its now empty folder, so Bitwig doesn't run both on the same port.
        Assert.False(File.Exists(script));
        Assert.False(Directory.Exists(Path.GetDirectoryName(script)));

        File.WriteAllBytes(built, [0x50, 0x4B, 3, 4, 9]);
        Assert.Equal(ScriptState.Outdated, extension.State);
    }

    [Fact]
    public void KeepsTheFlStudioChangesSettingWhenUpdating()
    {
        var data = FlUserData(Fixtures.TempDir());
        var script = DawScripts.FlStudio([data]);
        script.Install();
        var installed = Path.Combine(data, "Settings", "Hardware", "Music Organiser", FlLink.Script);
        File.WriteAllText(installed, File.ReadAllText(installed).Replace("LET_THE_APP_MAKE_CHANGES = False", "LET_THE_APP_MAKE_CHANGES = True"));
        Assert.Equal(ScriptState.Installed, script.State);   // turning changes on doesn't make it another version

        File.AppendAllText(installed, "# an older version\n");
        Assert.Equal(ScriptState.Outdated, script.State);
        script.Install();
        Assert.Equal(ScriptState.Installed, script.State);
        Assert.Contains("\nLET_THE_APP_MAKE_CHANGES = True\n", File.ReadAllText(installed).ReplaceLineEndings("\n"));
        Assert.DoesNotContain("older version", File.ReadAllText(installed));
    }

    [Fact]
    public void FindsFlStudiosUserDataFolders()
    {
        var root = Fixtures.TempDir();
        var moved = FlUserData(Path.Combine(root, "FL Studio Data", "FL Studio"));
        var chosen = Path.Combine(root, "Chosen");
        string[] projects =
        [
            Path.Combine(moved, "Projects", "Night Drive", "Night Drive.flp"),
            Path.Combine(moved, "Projects", "Other.flp"),
            Path.Combine(root, "Loose", "Song.flp"),
        ];
        var found = DawScripts.FlUserDataFolders(projects, chosen);
        Assert.Equal(chosen, found[0]);
        Assert.Single(found, f => f == moved);
        Assert.DoesNotContain(found, f => f.StartsWith(root, StringComparison.Ordinal) && f != moved && f != chosen);
        Assert.Equal(Path.Combine(moved, "Settings", "Hardware", "Music Organiser"),
                     DawScripts.FlStudio(found).Folders.Single(f => f.StartsWith(moved, StringComparison.Ordinal)));
    }

    static string FlUserData(string folder)
    {
        foreach (var sub in new[] { "Settings", "Presets", "Projects" }) Directory.CreateDirectory(Path.Combine(folder, sub));
        return folder;
    }
}
