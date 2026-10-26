using System.Diagnostics;

namespace MusicOrganiser.Core.Space;

/// <summary>A plugin folder moved to another drive: where it was, where it is now, and its old copy until you delete it.</summary>
public sealed record MovedFolder(string From, string To, string OldCopy, long Bytes, DateTime Moved)
{
    /// <summary>The plugin it belongs to (its name key), and what it holds.</summary>
    public string Key { get; init; } = "";
    public SpaceKind Kind { get; init; } = SpaceKind.Sounds;
    /// <summary>Whether the old copy is still beside the link, waiting for you to delete it.</summary>
    public bool HasOldCopy => Directory.Exists(OldCopy);
}

/// <summary>
/// Moves a plugin's sounds or presets to another drive without the plugin noticing (Solomon, 26 Oct 2026: "a way to move
/// some plugins data folders safely to a different drive"). The folder is copied and every file checked, then the
/// original is renamed to "… (old copy)" and a link (a junction on Windows) takes its place, pointing at the new copy, so
/// the plugin and every project still find their files where they were. The old copy goes only when you say, after the
/// plugin has opened fine; <see cref="MoveBack"/> undoes the lot.
/// </summary>
public static class FolderMove
{
    public const string OldSuffix = " (old copy)";

    /// <summary>Room left over on the drive after the copy, so the move never fills it.</summary>
    public const long Headroom = 2L << 30;

    /// <summary>Where the folder goes in the folder you picked: a folder of the same name.</summary>
    public static string Target(string from, string destination) =>
        Path.Combine(destination, Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(from))));

    /// <summary>Whether the folder is a link to another place (moved already, or set up that way).</summary>
    public static bool IsLink(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Why the folder can't move to <paramref name="destination"/>, or "" when it can.</summary>
    /// <param name="driveOf">Which drive a path is on (tests stand in a second drive with a folder).</param>
    public static string Problem(string from, string destination, long bytes, Func<string, long>? freeSpace = null, Func<string, string>? driveOf = null)
    {
        from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (!Directory.Exists(from)) return "The folder isn't there any more.";
        if (IsLink(from)) return "It's moved already: it points to another place.";
        if (Directory.Exists(from + OldSuffix)) return $"There's already a folder called {Path.GetFileName(from)}{OldSuffix} beside it.";
        if (DiskSpace.IsInside(destination, from)) return "That folder is inside the one being moved.";
        driveOf ??= DriveOf;
        if (driveOf(from).Equals(driveOf(destination), StringComparison.OrdinalIgnoreCase))
            return "That's the same drive, so moving there frees nothing. Pick a folder on another drive.";
        var target = Target(from, destination);
        if (Directory.Exists(target) || File.Exists(target)) return $"{target} is there already. Pick another folder, or rename that one.";
        if (OperatingSystem.IsWindows())
        {
            var drive = new DriveInfo(Path.GetPathRoot(destination)!);
            if (drive.DriveType is DriveType.Network) return "Windows can't point a folder at a network drive. Pick a drive in this PC.";
        }
        var free = (freeSpace ?? FreeSpace)(destination);
        if (free >= 0 && free < bytes + Headroom)
            return $"That drive has {DiskSpace.Text(free)} free; this needs {DiskSpace.Text(bytes)} and some room to spare.";
        return "";
    }

    /// <summary>The drive (on a Mac or Linux, the mounted disk) a path is on.</summary>
    public static string DriveOf(string path)
    {
        if (OperatingSystem.IsWindows()) return Path.GetPathRoot(path) ?? "";
        try
        {
            return DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName).Where(r => DiskSpace.IsInside(path, r))
                            .OrderByDescending(r => r.Length).FirstOrDefault() ?? "/";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "/"; }
    }

    static long FreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return -1; }
    }

    /// <summary>
    /// Copies the folder to the other drive, checks every file arrived whole, then swaps the original for a link to the
    /// copy. If anything goes wrong before the swap, the half-made copy is removed and nothing else has changed.
    /// </summary>
    /// <param name="progress">Bytes copied so far, of the whole.</param>
    public static MovedFolder Move(string from, string destination, long bytes, IProgress<long>? progress = null, CancellationToken ct = default,
                                   Func<string, string>? driveOf = null)
    {
        from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        var problem = Problem(from, destination, bytes, driveOf: driveOf);
        if (problem.Length > 0) throw new IOException(problem);
        var target = Target(from, destination);
        try
        {
            Copy(from, target, progress, ct);
        }
        catch
        {
            RemoveCopy(target);
            throw;
        }
        var swap = Swap(from, target);
        if (swap.Length > 0)
        {
            RemoveCopy(target);
            throw new IOException(swap);
        }
        return new MovedFolder(from, target, from + OldSuffix, bytes, DateTime.Now);
    }

    /// <summary>
    /// Undoes a move: the link goes and the old copy takes its place again, or, once the old copy is deleted, the folder
    /// is copied back first. The copy on the other drive is left for you to remove (it goes to that drive's Recycle Bin).
    /// </summary>
    public static void MoveBack(MovedFolder moved, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var restore = moved.OldCopy;
        if (!Directory.Exists(restore))
        {
            // The old copy is gone: copy the folder back beside the link first, then swap.
            restore = moved.From + " (moving back)";
            try { Copy(moved.To, restore, progress, ct); }
            catch
            {
                RemoveCopy(restore);
                throw;
            }
        }
        var problem = Unswap(moved.From, restore);
        if (problem.Length > 0) throw new IOException(problem);
    }

    /// <summary>Copies every file, keeping its dates, and checks each one's size once it's written.</summary>
    static void Copy(string from, string to, IProgress<long>? progress, CancellationToken ct)
    {
        long done = 0;
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            ct.ThrowIfCancellationRequested();
            var copy = Path.Combine(to, Path.GetRelativePath(from, file));
            var source = new FileInfo(file);
            File.Copy(file, copy);
            File.SetLastWriteTimeUtc(copy, source.LastWriteTimeUtc);
            if (new FileInfo(copy).Length != source.Length)
                throw new IOException($"{Path.GetFileName(file)} didn't copy whole, so nothing was moved.");
            done += source.Length;
            progress?.Report(done);
        }
    }

    /// <summary>Removes a copy the app made itself (never a link, never the original).</summary>
    static void RemoveCopy(string path)
    {
        try
        {
            if (Directory.Exists(path) && !IsLink(path)) Directory.Delete(path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The original becomes "… (old copy)" and a link to the copy takes its name. Returns "" or what stopped it.</summary>
    static string Swap(string from, string target)
    {
        var problem = SwapHere(from, target);
        if (problem != NeedsAdmin) return problem;
        return RunAsAdmin("swap", from, target) is { Length: > 0 } admin ? admin : SwapDone(from, target) ? "" : "The administrator step didn't finish.";
    }

    static string Unswap(string from, string restore)
    {
        var problem = UnswapHere(from, restore);
        if (problem != NeedsAdmin) return problem;
        return RunAsAdmin("unswap", from, restore) is { Length: > 0 } admin ? admin : !IsLink(from) && Directory.Exists(from) ? "" : "The administrator step didn't finish.";
    }

    static bool SwapDone(string from, string target) => IsLink(from) && Directory.Exists(from + OldSuffix) && Directory.Exists(target);

    const string NeedsAdmin = "admin";

    static string SwapHere(string from, string target)
    {
        try
        {
            Directory.Move(from, from + OldSuffix);
        }
        catch (UnauthorizedAccessException) { return NeedsAdmin; }
        catch (IOException e)
        {
            return $"Windows wouldn't rename the folder ({e.Message}). Close your DAW and the plugin, then try again. Nothing was moved.";
        }
        var link = Link(from, target);
        if (link.Length == 0 && Directory.Exists(from)) return "";
        // The link didn't take: put the original back as it was.
        try { if (IsLink(from)) Directory.Delete(from); Directory.Move(from + OldSuffix, from); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return $"Windows wouldn't make the link ({link}). Nothing was moved.";
    }

    static string UnswapHere(string from, string restore)
    {
        try
        {
            if (IsLink(from)) Directory.Delete(from);   // the link only, never what it points to
            Directory.Move(restore, from);
            return "";
        }
        catch (UnauthorizedAccessException) { return NeedsAdmin; }
        catch (IOException e) { return $"Windows wouldn't move the folder back ({e.Message}). Close your DAW and the plugin, then try again."; }
    }

    /// <summary>A junction on Windows (no administrator rights or developer mode needed), a symbolic link elsewhere.</summary>
    static string Link(string at, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(at, target);
                return "";
            }
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/c", "mklink", "/J", at, target }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var said = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? "" : said.Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return e.Message;
        }
    }

    /// <summary>The app started for <see cref="RunAsAdmin"/>: <c>--move-folder swap|unswap from to result</c>.</summary>
    public const string HelperFlag = "--move-folder";

    static string RunAsAdmin(string step, string from, string to)
    {
        var result = Path.Combine(Path.GetTempPath(), $"mo-move-{Guid.NewGuid():N}.txt");
        try
        {
            var problem = AdminStep.Run(HelperFlag, step, from, to, result);
            if (problem.Length > 0) return problem;
            return File.Exists(result) ? File.ReadAllText(result).Trim() : "The administrator step didn't finish.";
        }
        finally
        {
            try { File.Delete(result); } catch (IOException) { }
        }
    }

    /// <summary>Runs the administrator step when the app was started for it. Returns false when it wasn't.</summary>
    public static bool RunHelper(string[] args)
    {
        if (args.Length != 5 || args[0] != HelperFlag) return false;
        var said = args[1] switch
        {
            "swap" => SwapHere(args[2], args[3]),
            "unswap" => UnswapHere(args[2], args[3]),
            _ => "?",
        };
        try { File.WriteAllText(args[4], said == NeedsAdmin ? "Windows wouldn't allow it even as administrator." : said); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return true;
    }
}
