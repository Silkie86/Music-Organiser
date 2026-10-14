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
