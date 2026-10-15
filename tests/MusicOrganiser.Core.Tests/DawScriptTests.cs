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

        // Without a built extension (the app was built without a JDK), Settings keeps installing the script.
        var built = Path.Combine(Fixtures.TempDir(), "MusicOrganiser.bwextension");
        Assert.Equal("MusicOrganiser.control.js", DawScripts.BitwigLink([user], built).FileName);

        File.WriteAllBytes(built, [0x50, 0x4B, 3, 4, 1, 2, 3]);
        var extension = DawScripts.BitwigLink([user], built);
        Assert.Equal("MusicOrganiser.bwextension", extension.FileName);
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
    public void InstallTakesTheLinkOutOfBitwigsOtherUserFolders()
    {
        // The first install went to Documents\Bitwig Studio before Bitwig's own folder was chosen, and Bitwig mustn't load two.
        var root = Fixtures.TempDir();
        var chosen = Path.Combine(root, "K", "Bitwig Studio");
        var other = Path.Combine(root, "Documents", "Bitwig Studio");
        var oldExtension = Extension(Path.Combine(other, "Extensions", "MusicOrganiser.bwextension"), "3.0");
        var oldScript = Path.Combine(other, "Controller Scripts", "Music Organiser", "MusicOrganiser.control.js");
        Directory.CreateDirectory(Path.GetDirectoryName(oldScript)!);
        File.WriteAllText(oldScript, "// the old script");
        var built = Extension(Path.Combine(Fixtures.TempDir(), "MusicOrganiser.bwextension"), "3.3");

        // Others found include the chosen one (it's in Documents too, say), which Install puts the link in, not takes it out of.
        var link = DawScripts.BitwigLink([chosen], built, others: [chosen + Path.DirectorySeparatorChar, other]);
        Assert.Equal([oldExtension, oldScript], link.Leftovers);
        Assert.Equal([oldExtension, oldScript], link.Install());
        Assert.Equal(["3.3"], link.InstalledVersions);
        Assert.Empty(link.Leftovers);
        // Bitwig's own folders stay, even empty; the script's folder goes once empty.
        Assert.True(Directory.Exists(Path.Combine(other, "Extensions")));
        Assert.True(Directory.Exists(Path.Combine(other, "Controller Scripts")));
        Assert.False(Directory.Exists(Path.GetDirectoryName(oldScript)));
        Assert.Empty(link.Install());

        // Installing the script instead (an app built without a JDK) takes both out of the others too.
        Extension(oldExtension, "3.0");
        var script = DawScripts.BitwigLink([chosen], Path.Combine(root, "no extension.bwextension"), others: [other]);
        Assert.Equal("MusicOrganiser.control.js", script.FileName);
        Assert.Equal([oldExtension], script.Install());
        Assert.True(File.Exists(Path.Combine(chosen, "Extensions", "MusicOrganiser.bwextension")));   // not one of the others
    }

    [Fact]
    public void SaysWhichExtensionVersionInstallPutsIn()
    {
        var user = Fixtures.TempDir();
        var built = Extension(Path.Combine(Fixtures.TempDir(), "MusicOrganiser.bwextension"), "3.2");
        var extension = DawScripts.BitwigExtension([user], built);
        Assert.Equal("3.2", extension.Version);
        Assert.Empty(extension.InstalledVersions);
        extension.Install();
        Assert.Equal(["3.2"], extension.InstalledVersions);

        Extension(Path.Combine(user, "Extensions", "MusicOrganiser.bwextension"), "3.0");
        Assert.Equal((ScriptState.Outdated, "3.0"), (extension.State, extension.InstalledVersions.Single()));
        Assert.Null(DawScript.ExtensionVersion(Path.Combine(user, "nothing here.bwextension")));
        Assert.Null(DawScripts.Bitwig(user).Version);   // the script has none
    }

    [Fact]
    public void SaysWhenTheExtensionWasBuiltBeforeItsCodeChanged()
    {
        // Running the app with hot reload doesn't build the extension again, so Install would put in an old one.
        var code = Directory.CreateDirectory(Path.Combine(Fixtures.TempDir(), "bitwig-java")).FullName;
        var link = Path.Combine(Directory.CreateDirectory(Path.Combine(code, "src", "main", "java")).FullName, "Link.java");
        File.WriteAllText(Path.Combine(code, "pom.xml"), "<project/>");
        File.WriteAllText(link, "final class Link {}");
        var built = Extension(Path.Combine(Fixtures.TempDir(), "MusicOrganiser.bwextension"), "3.2");
        var then = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(built, then);
        foreach (var f in new[] { link, Path.Combine(code, "pom.xml") }) File.SetLastWriteTimeUtc(f, then.AddMinutes(-5));
        var extension = DawScripts.BitwigExtension([Fixtures.TempDir()], built) with { Code = code };
        Assert.Null(extension.ChangedSinceBuilt);

        File.SetLastWriteTimeUtc(link, then.AddMinutes(1));
        Assert.Equal(link, extension.ChangedSinceBuilt);
        Assert.Null((extension with { Code = null }).ChangedSinceBuilt);   // an installed app has no code next to it
    }

    [Fact]
    public async Task SaysWhereToLookWhenTheExtensionDidntBuildAgain()
    {
        // A build that fails (no JDK, a mistake in the code) leaves the old extension, so starting again doesn't help.
        var code = Directory.CreateDirectory(Path.Combine(Fixtures.TempDir(), "bitwig-java")).FullName;
        File.WriteAllText(Path.Combine(code, "pom.xml"), "<project/>");
        var built = Extension(Path.Combine(Fixtures.TempDir(), "MusicOrganiser.bwextension"), "3.3");
        File.SetLastWriteTimeUtc(built, DateTime.UtcNow.AddHours(-1));
        var link = new MusicOrganiser.ViewModels.DawLinkSetting("Bitwig Studio", () => "",
            () => DawScripts.BitwigExtension([Fixtures.TempDir()], built) with { Code = code }, () => Task.FromResult(""), found: () => true);
        await link.RefreshAsync();
        Assert.EndsWith("close the app and start it again so it's built anew. If this stays, building it failed, and " +
                        $"{Path.Combine(code, "build.log")} says why.", link.Status);
    }

    /// <summary>A stand-in for the built extension: a Java archive whose manifest gives its version.</summary>
    internal static string Extension(string path, string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        using var manifest = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open());
        manifest.Write($"Manifest-Version: 1.0\r\nImplementation-Title: Music Organiser\r\nImplementation-Version: {version}\r\n\r\n");
        return path;
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

    [Fact]
    public void TakesTheUserFolderAPickedFolderIsIn()
    {
        // Solomon's Bitwig user folder is on another drive, and the folder that came to mind was Extensions, where the link goes.
        var root = Fixtures.TempDir();
        var bitwig = Path.Combine(root, "K", "Bitwig Studio");
        foreach (var sub in new[] { "Extensions", "Library", "Projects" }) Directory.CreateDirectory(Path.Combine(bitwig, sub));
        Directory.CreateDirectory(Path.Combine(bitwig, "Library", "Presets", "Bass"));
        Directory.CreateDirectory(Path.Combine(bitwig, "Extensions", "Library"));   // an extension's own, not Bitwig's
        var installed = Directory.CreateDirectory(Path.Combine(root, "Program Files", "Bitwig Studio", "resources")).FullName;
        Assert.Equal(bitwig, DawScripts.BitwigUserFolderFrom(bitwig));
        Assert.Equal(bitwig, DawScripts.BitwigUserFolderFrom(bitwig + Path.DirectorySeparatorChar));
        Assert.Equal(bitwig, DawScripts.BitwigUserFolderFrom(Path.Combine(bitwig, "Extensions")));
        Assert.Equal(bitwig, DawScripts.BitwigUserFolderFrom(Path.Combine(bitwig, "Library", "Presets", "Bass")));
        Assert.Null(DawScripts.BitwigUserFolderFrom(Path.Combine(root, "K")));
        Assert.Null(DawScripts.BitwigUserFolderFrom(installed));   // where Bitwig is installed, not its user folder
        Assert.Equal(bitwig, DawScripts.BitwigUserFolderFrom(Path.Combine(bitwig, "Extensions", "Library")));
        // Folders with Bitwig's folder names that aren't in a Bitwig user folder.
        foreach (var elsewhere in new[] { Path.Combine("D", "Music", "Projects"), Path.Combine("E", "Audio", "Library"), Path.Combine("SomeApp", "Extensions") })
            Assert.Null(DawScripts.BitwigUserFolderFrom(Directory.CreateDirectory(Path.Combine(root, elsewhere)).FullName));

        var fl = FlUserData(Path.Combine(root, "Image-Line", "FL Studio"));
        var hardware = Directory.CreateDirectory(Path.Combine(fl, "Settings", "Hardware")).FullName;
        Assert.Equal(fl, DawScripts.FlUserDataFrom(fl));
        Assert.Equal(fl, DawScripts.FlUserDataFrom(hardware));
        Assert.Null(DawScripts.FlUserDataFrom(Path.Combine(root, "Image-Line")));
    }

    static string FlUserData(string folder)
    {
        foreach (var sub in new[] { "Settings", "Presets", "Projects" }) Directory.CreateDirectory(Path.Combine(folder, sub));
        return folder;
    }
}
