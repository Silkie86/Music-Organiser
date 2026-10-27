using System.Diagnostics;

namespace MusicOrganiser.Core.Space;

/// <summary>A plugin folder moved to another drive: where it was, where it is now, and its old copy until you delete it.</summary>
public sealed record MovedFolder(string From, string To, string OldCopy, long Bytes, DateTime Moved)
{
    /// <summary>The plugin it belongs to (its name key), and what it holds.</summary>
    public string Key { get; init; } = "";
    public SpaceKind Kind { get; init; } = SpaceKind.Sounds;
    /// <summary>
    /// The other plugins whose folder it also was when it moved (a maker's folder: Toontrack's is EZkeys 2's and Superior
    /// Drummer 3's), so their Space tabs show it moved too. Empty for moves made before this was kept.
    /// </summary>
    public IReadOnlyList<string> SharedWith { get; init; } = [];
    /// <summary>Whether the plugin with this name key had it: the one it was moved from, or one sharing it.</summary>
    public bool Belongs(string key) => key.Length > 0 && (Key == key || SharedWith.Contains(key));
    /// <summary>Whether the old copy is still beside the link, waiting for you to delete it.</summary>
    public bool HasOldCopy => Directory.Exists(OldCopy);
}

/// <summary>
/// A folder left behind for you to delete once the plugins open fine: the old place of the folder the moved folders live
/// in, after it moved to another drive, or the folder an update made where the link was (or a second copy a later move
/// made there), once its files joined the moved copy.
/// </summary>
public sealed record OldHome(string Path, long Bytes, string Note = "its old place");

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

    /// <summary>The folder a plugin update made in the link's place, once it's set aside and the link is back.</summary>
    public const string UpdateSuffix = " (after update)";

    /// <summary>The second copy a later move of the folder around it made, once it's set aside and the link is back.</summary>
    public const string SecondSuffix = " (second copy)";

    /// <summary>Room left over on the drive after the copy, so the move never fills it.</summary>
    public const long Headroom = 2L << 30;

    /// <summary>Where the folder goes in the folder you picked: a folder of the same name.</summary>
    public static string Target(string from, string destination) =>
        Path.Combine(destination, Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(from))));

    /// <summary>
    /// Whether the folder is a link to another place (moved already, or set up that way). A link whose folder has gone
    /// still counts: it's the link that's there. A OneDrive folder isn't one, though Windows marks it the same way.
    /// </summary>
    public static bool IsLink(string path) => DiskSpace.IsLink(new DirectoryInfo(path));

    /// <summary>
    /// The moved folder around <paramref name="path"/> (Native Instruments, moved, around Reaktor 6), or null when none
    /// is: then the path is where it looks, on this drive. Only the app's own moves count; a link the system made (a
    /// Mac's /var) isn't a move.
    /// </summary>
    public static string? InsideMoved(string path, IReadOnlyList<MovedFolder> moved)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return moved.Select(m => m.From)
                    .Where(f => !f.Equals(path, StringComparison.OrdinalIgnoreCase) && DiskSpace.IsInside(path, f))
                    .OrderBy(f => f.Length).FirstOrDefault(IsLink);
    }

    /// <summary>Why the folder can't move to <paramref name="destination"/>, or "" when it can.</summary>
    /// <param name="driveOf">Which drive a path is on (tests stand in a second drive with a folder).</param>
    /// <param name="ct">Stops the look through the folder for OneDrive files (a big folder takes a while).</param>
    /// <param name="moved">The folders moved already, so one inside them isn't moved again.</param>
    public static string Problem(string from, string destination, long bytes, Func<string, long>? freeSpace = null, Func<string, string>? driveOf = null,
                                 CancellationToken ct = default, IReadOnlyList<MovedFolder>? moved = null)
    {
        from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (!Directory.Exists(from)) return "The folder isn't there any more.";
        if (IsLink(from)) return "It's moved already: it points to another place.";
        if (InsideMoved(from, moved ?? []) is { } outer)
            return $"It's inside {Path.GetFileName(outer)}, which is on another drive already, so moving it frees nothing here.";
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
        // Copying a OneDrive file that's only online downloads it first, onto the drive being freed, and the size left it out.
        if (DiskSpace.HasOnlineOnly(from, ct)) return OnlineOnly;
        var free = (freeSpace ?? FreeSpace)(destination);
        if (free >= 0 && free < bytes + Headroom)
            return $"That drive has {DiskSpace.Text(free)} free; this needs {DiskSpace.Text(bytes)} and some room to spare.";
        return "";
    }

    const string OnlineOnly = "Some of its files are only online in OneDrive, so copying them would download them onto this drive first. " +
                              "In File Explorer, right-click the folder and pick Always keep on this device. Once they've downloaded, try again.";

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
    /// <param name="alreadyMoved">The folders moved before: one around this one stops it, and old copies inside it stay behind.</param>
    public static MovedFolder Move(string from, string destination, long bytes, IProgress<long>? progress = null, CancellationToken ct = default,
                                   Func<string, string>? driveOf = null, IReadOnlyList<MovedFolder>? alreadyMoved = null)
    {
        from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        // The old copies of folders moved from inside it before (Reaktor 6's inside Native Instruments) aren't copied:
        // they stay in its own old copy, and go when that does.
        var leaveOut = (alreadyMoved ?? []).Where(m => DiskSpace.IsInside(m.OldCopy, from)).Select(m => m.OldCopy).ToList();
        var problem = Problem(from, destination, bytes, driveOf: driveOf, ct: ct, moved: alreadyMoved);
        if (problem.Length > 0) throw new IOException(problem);
        var target = Target(from, destination);
        // Find out before copying whether Windows lets the folder be renamed, rather than after an hour of copying.
        var check = CheckRename(from);
        if (check.Length > 0 && check != NeedsAdmin) throw new IOException(check);
        try
        {
            Walk(from, target, onlyNewer: false, progress, ct, leaveOut);
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
    /// Undoes a move: the link goes and the old copy takes its place again, with everything saved on the other drive since
    /// the move copied into it first (Solomon, 26 Oct: presets the plugin wrote through the link live only there). Once
    /// the old copy is deleted, the folder is copied back instead. The copy on the other drive is left for you to remove
    /// (it goes to that drive's Recycle Bin).
    /// </summary>
    public static void MoveBack(MovedFolder moved, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var restore = moved.OldCopy;
        if (Directory.Exists(restore))
        {
            try { Freshen(moved.To, restore, progress, ct); }
            catch (Exception e) when (Denied(e))
            {
                throw new IOException("Windows guards the old copy, so what the plugin saved since the move couldn't be copied into it. " +
                                      "Nothing changed: the plugin still uses the copy on the other drive.");
            }
        }
        else
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

    /// <summary>
    /// Copies every file, keeping its dates, and checks each one's size once it's written. A link inside the folder is
    /// made again as a link, never followed: Arturia's Presets\FactoryEdit points at a place that isn't there (F12 log,
    /// 26 Oct), and a link to elsewhere shouldn't drag that place along.
    /// </summary>
    static void Copy(string from, string to, IProgress<long>? progress, CancellationToken ct) => Walk(from, to, onlyNewer: false, progress, ct);

    /// <summary>Copies the files that are new, or newer than the ones in <paramref name="to"/>, into it. Nothing is removed.</summary>
    static void Freshen(string from, string to, IProgress<long>? progress, CancellationToken ct) => Walk(from, to, onlyNewer: true, progress, ct);

    static void Walk(string from, string to, bool onlyNewer, IProgress<long>? progress, CancellationToken ct,
                     IReadOnlyCollection<string>? leaveOut = null)
    {
        var skip = (leaveOut ?? []).Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        long done = 0;
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        var folders = new Stack<(string From, string To)>();
        folders.Push((from, to));
        while (folders.TryPop(out var folder))
        {
            Directory.CreateDirectory(folder.To);
            foreach (var entry in new DirectoryInfo(folder.From).EnumerateFileSystemInfos("*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (skip.Contains(entry.FullName)) continue;
                var copy = Path.Combine(folder.To, entry.Name);
                if (entry.LinkTarget is { } target)
                {
                    if (!onlyNewer || !Directory.Exists(copy) && !File.Exists(copy)) CopyLink(entry, target, copy);
                    continue;
                }
                if (entry is DirectoryInfo)
                {
                    folders.Push((entry.FullName, copy));
                    continue;
                }
                var source = (FileInfo)entry;
                // Never downloaded to be copied (one that went online-only after the check): it stops before anything is swapped.
                if (DiskSpace.IsOnlineOnly(source.Attributes)) throw new IOException(OnlineOnly);
                if (onlyNewer && new FileInfo(copy) is { Exists: true } there && there.LastWriteTimeUtc >= source.LastWriteTimeUtc) continue;
                File.Copy(source.FullName, copy, overwrite: onlyNewer);
                File.SetLastWriteTimeUtc(copy, source.LastWriteTimeUtc);
                if (new FileInfo(copy).Length != source.Length)
                    throw new IOException($"{source.Name} didn't copy whole, so nothing was moved.");
                done += source.Length;
                progress?.Report(done);
            }
        }
    }

    /// <summary>Makes a link in the copy pointing where the original link points. One that points at nothing is left out.</summary>
    static void CopyLink(FileSystemInfo link, string target, string copy)
    {
        var full = Path.GetFullPath(target, Path.GetDirectoryName(link.FullName)!);
        if (link is DirectoryInfo)
        {
            // A junction needs the whole path on Windows; elsewhere the link keeps its own wording (it may be relative).
            var made = Link(copy, OperatingSystem.IsWindows() ? full : target);
            if (made.Length > 0 && Directory.Exists(full))
                throw new IOException($"{link.Name} is a link to {full} that Windows wouldn't make again ({made}), so nothing was moved.");
            return;
        }
        try
        {
            File.CreateSymbolicLink(copy, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Windows only lets an administrator (or developer mode) make a file link: copy what it points at instead.
            if (File.Exists(full)) File.Copy(full, copy);
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
    static string Swap(string from, string target, string? aside = null)
    {
        aside ??= from + OldSuffix;
        var problem = SwapHere(from, target, aside);
        if (problem != NeedsAdmin) return problem;
        return RunAsAdmin("swap", from, target, aside) is { Length: > 0 } admin ? admin : SwapDone(from, target, aside) ? "" : "The administrator step didn't finish.";
    }

    static string Unswap(string from, string restore)
    {
        var problem = UnswapHere(from, restore);
        if (problem != NeedsAdmin) return problem;
        return RunAsAdmin("unswap", from, restore) is { Length: > 0 } admin ? admin : !IsLink(from) && Directory.Exists(from) ? "" : "The administrator step didn't finish.";
    }

    static bool SwapDone(string from, string target, string aside) => IsLink(from) && Directory.Exists(aside) && Directory.Exists(target);

    const string NeedsAdmin = "admin";

    /// <summary>
    /// Windows says "access denied" both when a folder needs administrator rights and, for a folder, as an IOException
    /// rather than UnauthorizedAccessException (Spectrasonics in ProgramData, Altiverb in Program Files: F12 note, 26 Oct).
    /// </summary>
    static bool Denied(Exception e) => e is UnauthorizedAccessException || e is IOException && (e.HResult & 0xFFFF) == 5;

    /// <summary>Whether the swap needs Windows' administrator question, or what stops it ("" when nothing does).</summary>
    public static (bool Admin, string Problem) CheckSwap(string from) =>
        CheckRename(Path.TrimEndingDirectorySeparator(Path.GetFullPath(from))) is var said && said == NeedsAdmin ? (true, "") : (false, said);

    /// <summary>
    /// Renames the folder and straight back, to learn whether the swap will work: "" when it does, <see cref="NeedsAdmin"/>
    /// when Windows wants administrator rights (asked for once, at the swap), or what's in the way.
    /// </summary>
    static string CheckRename(string from)
    {
        var check = from + " (checking)";
        try
        {
            Directory.Move(from, check);
        }
        catch (Exception e) when (Denied(e)) { return NeedsAdmin; }
        catch (IOException e) { return $"Windows wouldn't rename the folder ({e.Message}). Close your DAW and the plugin, then try again. Nothing was moved."; }
        for (var tries = 0; ; tries++)
        {
            try
            {
                Directory.Move(check, from);
                return "";
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && tries < 20)
            {
                Thread.Sleep(250);   // something (a virus checker) looked in as it was renamed
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"The folder is called \"{Path.GetFileName(check)}\" for now: Windows wouldn't rename it back ({e.Message}). Rename it back in Explorer.";
            }
        }
    }

    static string SwapHere(string from, string target, string aside)
    {
        try
        {
            Directory.Move(from, aside);
        }
        catch (Exception e) when (Denied(e)) { return NeedsAdmin; }
        catch (IOException e)
        {
            return $"Windows wouldn't rename the folder ({e.Message}). Close your DAW and the plugin, then try again. Nothing was moved.";
        }
        var link = Link(from, target);
        if (link.Length == 0 && Directory.Exists(from)) return "";
        // The link didn't take: put the original back as it was. In a folder Windows guards, the link needs
        // administrator rights too, so the whole swap is tried once more that way.
        try { if (IsLink(from)) Directory.Delete(from); Directory.Move(aside, from); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Environment.IsPrivilegedProcess || !OperatingSystem.IsWindows() ? $"Windows wouldn't make the link ({link}). Nothing was moved." : NeedsAdmin;
    }

    static string UnswapHere(string from, string restore)
    {
        string? was = null;
        try
        {
            if (IsLink(from))
            {
                was = new DirectoryInfo(from).LinkTarget;
                Directory.Delete(from);   // the link only, never what it points to
            }
        }
        catch (Exception e) when (Denied(e)) { return NeedsAdmin; }
        catch (IOException e) { return $"Windows wouldn't move the folder back ({e.Message}). Close your DAW and the plugin, then try again."; }
        for (var tries = 0; ; tries++)
        {
            try
            {
                Directory.Move(restore, from);
                return "";
            }
            catch (IOException e) when (!Denied(e) && tries < 20 && Directory.Exists(restore) && !Directory.Exists(from))
            {
                Thread.Sleep(250);   // a virus checker looking at the files just copied into the old copy
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The link goes back, so the plugin still finds its files on the other drive.
                if (was is not null && !Directory.Exists(from)) Link(from, was);
                return Denied(e) ? NeedsAdmin : $"Windows wouldn't move the folder back ({e.Message}). Close your DAW and the plugin, then try again.";
            }
        }
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

    /// <summary>
    /// The app started for <see cref="RunAsAdmin"/>: <c>--move-folder swap|unswap from to [aside] result</c>, or for the links
    /// Windows guards: <c>--move-folder relink list result fingerprint</c>.
    /// </summary>
    public const string HelperFlag = "--move-folder";

    static string RunAsAdmin(string step, params string[] paths)
    {
        var result = Path.Combine(Path.GetTempPath(), $"mo-move-{Guid.NewGuid():N}.txt");
        try
        {
            var problem = AdminStep.Run([HelperFlag, step, .. paths, result]);
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
        if (args.Length >= 2 && args[0] == HelperFlag && args[1] == "relink")
        {
            if (args.Length != 5) return true;   // not as the app starts it: nothing, and no window as administrator either
            try
            {
                // A list changed since the app wrote it (its fingerprint differs) relinks nothing.
                if (AdminStep.ReadList(args[2], args[4]) is not { } list) return true;
                var lines = list.Select(l => l.Split('\t', 2)).Where(x => x.Length == 2)
                                .Select(x => (At: x[0], Said: RelinkHere(x[0], x[1])))
                                .Select(x => $"{x.At}\t{(x.Said == NeedsAdmin ? "Windows wouldn't allow it even as administrator." : x.Said)}");
                // Lazily: the result file is made first, so nothing is relinked when it can't be.
                AdminStep.WriteResult(args[3], lines);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return true;
        }
        if (args.Length is not (5 or 6) || args[0] != HelperFlag) return false;
        var said = (args[1], args.Length) switch
        {
            ("swap", 6) => SwapHere(args[2], args[3], args[4]),
            ("swap", _) => SwapHere(args[2], args[3], args[2] + OldSuffix),
            ("unswap", _) => UnswapHere(args[2], args[3]),
            _ => "?",
        };
        try { AdminStep.WriteResult(args[^1], said == NeedsAdmin ? "Windows wouldn't allow it even as administrator." : said); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return true;
    }

    // ---- the folder the moved folders live in (I:\Plugin Data), moved as a whole (Solomon, 26 Oct 2026) ----------------

    /// <summary>Where a moved folder lives: the folder you picked when moving it.</summary>
    public static string HomeOf(MovedFolder m) => Path.GetDirectoryName(m.To) ?? m.To;

    /// <summary>
    /// Moves the folder the moved folders live in (all of it, other files too) and points every link at the new place.
    /// On the same drive it's renamed; to another drive it's copied and checked, and the old one stays for you to delete.
    /// Returns the moves as they are now, and the links that couldn't be pointed there (they still point at the old place).
    /// </summary>
    public static (IReadOnlyList<MovedFolder> Moved, IReadOnlyDictionary<string, string> Problems) MoveHome(
        string home, string destination, IReadOnlyList<MovedFolder> moved, long bytes, IProgress<long>? progress = null,
        CancellationToken ct = default, Func<string, string>? driveOf = null)
    {
        home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        var target = Target(home, destination);
        if (DiskSpace.IsInside(destination, home)) throw new IOException("That folder is inside the one being moved.");
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException($"{target} is there already. Pick another folder, or rename that one.");
        driveOf ??= DriveOf;
        if (driveOf(home).Equals(driveOf(destination), StringComparison.OrdinalIgnoreCase))
            Directory.Move(home, target);
        else
        {
            var free = FreeSpace(destination);
            if (free >= 0 && free < bytes + Headroom)
                throw new IOException($"That drive has {DiskSpace.Text(free)} free; this needs {DiskSpace.Text(bytes)} and some room to spare.");
            try { Copy(home, target, progress, ct); }
            catch
            {
                RemoveCopy(target);
                throw;
            }
        }
        return Repoint(home, target, moved);
    }

    /// <summary>
    /// "I moved it myself": points the links of every folder that lived in <paramref name="oldHome"/> at the same folder in
    /// <paramref name="newHome"/>, where each one is found.
    /// </summary>
    public static (IReadOnlyList<MovedFolder> Moved, IReadOnlyDictionary<string, string> Problems) Repoint(
        string oldHome, string newHome, IReadOnlyList<MovedFolder> moved)
    {
        oldHome = Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldHome));
        newHome = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newHome));
        var links = moved.Where(m => DiskSpace.IsInside(m.To, oldHome))
                         .Select(m => (Moved: m, To: Path.Combine(newHome, Path.GetRelativePath(oldHome, m.To)))).ToList();
        var problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in links.Where(l => !Directory.Exists(l.To)))
            problems[l.Moved.From] = $"{Path.GetFileName(l.To)} isn't in {newHome}.";
        foreach (var (at, problem) in RelinkAll(links.Where(l => !problems.ContainsKey(l.Moved.From)).Select(l => (l.Moved.From, l.To)).ToList()))
            problems[at] = problem;
        var now = moved.Select(m => links.FirstOrDefault(l => ReferenceEquals(l.Moved, m)) is { Moved: not null } l && !problems.ContainsKey(m.From)
                                        ? m with { To = l.To } : m).ToList();
        return (now, problems);
    }

    /// <summary>
    /// Whether the old copy beside the link can go: the link is there and points at the copy on the other drive, and
    /// that copy is there. Not when the drive is unplugged or the copy was moved or renamed (then the old copy is the
    /// only one the plugin could use), nor when an update put a real folder back where the link was.
    /// </summary>
    public static bool OldCopyCanGo(MovedFolder moved)
    {
        if (!moved.HasOldCopy || !Directory.Exists(moved.To)) return false;
        try
        {
            if (new DirectoryInfo(moved.From).LinkTarget is not { } target) return false;
            var points = Path.GetFullPath(target, Path.GetDirectoryName(Path.GetFullPath(moved.From))!);
            return Path.TrimEndingDirectorySeparator(points).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(moved.To)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>
    /// Whether a folder left behind can go: it's there, and no moved folder (or its link) still lives in it. After a
    /// move of the Plugin Data folder, a link that couldn't be pointed at the new place still points into the old one.
    /// </summary>
    public static bool OldHomeCanGo(OldHome home, IReadOnlyList<MovedFolder> moved) =>
        Directory.Exists(home.Path) && !moved.Any(m => DiskSpace.IsInside(m.To, home.Path) || DiskSpace.IsInside(m.From, home.Path));

    /// <summary>The moved folders whose link points at nothing: their folder on the other drive was moved or renamed.</summary>
    public static IReadOnlyList<MovedFolder> Lost(IReadOnlyList<MovedFolder> moved) =>
        moved.Where(m => IsLink(m.From) && !Directory.Exists(m.To)).ToList();

    /// <summary>
    /// The moved folders a plugin update put a real folder back in place of (some installers delete the folder and make
    /// it again): the plugin writes on this drive again, and the copy on the other drive is left out of date. A folder
    /// inside one moved later isn't one of these: see <see cref="Doubled"/>.
    /// </summary>
    public static IReadOnlyList<MovedFolder> Replaced(IReadOnlyList<MovedFolder> moved) =>
        moved.Where(m => IsRealAgain(m) && InsideMoved(m.From, moved) is null).ToList();

    /// <summary>
    /// The moved folders that were inside a folder moved after them, before links inside a folder were kept as links (F12
    /// note, 27 Oct: Reaktor 6 and Reaktor5, then Native Instruments around them). That move copied what their links
    /// pointed at, so the other drive has two copies: the plugin now uses the one inside the later move, and the folder's
    /// own copy is left out of date. Each comes with the moved folder around it.
    /// </summary>
    public static IReadOnlyList<(MovedFolder Moved, string Around)> Doubled(IReadOnlyList<MovedFolder> moved) =>
        moved.Where(IsRealAgain).Select(m => (Moved: m, Around: InsideMoved(m.From, moved))).Where(x => x.Around is not null)
             .Select(x => (x.Moved, x.Around!)).ToList();

    static bool IsRealAgain(MovedFolder m) => Directory.Exists(m.From) && !IsLink(m.From) && Directory.Exists(m.To);

    /// <summary>
    /// Where a path inside a moved folder really is (Reaktor 6's second copy, inside Native Instruments, is on I:), so what
    /// is set aside in there goes to that drive's Recycle Bin rather than being copied back to this one to be binned.
    /// </summary>
    public static string RealPlace(string path, IReadOnlyList<MovedFolder> moved)
    {
        if (InsideMoved(path, moved) is not { } outer) return path;
        var m = moved.First(m => m.From.Equals(outer, StringComparison.OrdinalIgnoreCase));
        return Path.Join(m.To, Path.GetRelativePath(outer, Path.GetFullPath(path)));
    }

    /// <summary>
    /// Fills in the other plugins each moved folder was shared with, for moves made before that was kept (F12 note, 27 Oct:
    /// EZkeys 2 still showed Toontrack on C: after it moved). Their sizes from before the move still list the folder, so
    /// those plugins are also the ones to measure again. Each folder is only looked at once: it keeps what's found.
    /// </summary>
    public static (IReadOnlyList<MovedFolder> Moved, IReadOnlyList<string> Stale) FindSharers(IReadOnlyList<MovedFolder> moved,
                                                                                            IReadOnlyDictionary<string, PluginSpaceInfo> spaces)
    {
        var stale = new List<string>();
        var now = moved.Select(m =>
        {
            if (m.SharedWith.Count > 0) return m;
            var sharers = spaces.Values.Where(s => s.Key != m.Key && s.Folders.Any(f => f.Path.Equals(m.From, StringComparison.OrdinalIgnoreCase)))
                                .Select(s => s.Key).ToList();
            stale.AddRange(sharers);
            return sharers.Count > 0 ? m with { SharedWith = sharers } : m;
        }).ToList();
        return (now, stale.Distinct().ToList());
    }

    /// <summary>
    /// After an update replaced the link with a real folder (or a later move left a second copy where the link was): the
    /// folder's new and newer files join the copy on the other drive, then it's set aside as "… (after update)" (or
    /// "… (second copy)") and the link goes back. Returns the folder set aside, to delete once the plugin opens fine.
    /// </summary>
    public static OldHome LinkAgain(MovedFolder moved, long bytes, IProgress<long>? progress = null, CancellationToken ct = default,
                                    bool second = false)
    {
        if (!Directory.Exists(moved.To)) throw new IOException($"{moved.To} isn't there any more.");
        var suffix = second ? SecondSuffix : UpdateSuffix;
        var aside = moved.From + suffix;
        for (var n = 2; Directory.Exists(aside); n++) aside = $"{moved.From}{suffix[..^1]} {n})";
        var check = CheckRename(moved.From);
        if (check.Length > 0 && check != NeedsAdmin) throw new IOException(check);
        Freshen(moved.From, moved.To, progress, ct);
        var swap = Swap(moved.From, moved.To, aside);
        if (swap.Length > 0) throw new IOException(swap);
        return new OldHome(aside, bytes, second ? "a second copy" : "left by an update");
    }

    /// <summary>
    /// Points each link at its new place: the ones Windows guards with one administrator question for all. Outer links go
    /// first: a moved folder inside another (Reaktor 6 in Native Instruments) is reached through the outer link, so it's
    /// pointed at the new place only once the outer one is, or it would change the link left in the old place.
    /// </summary>
    static IReadOnlyDictionary<string, string> RelinkAll(IReadOnlyList<(string At, string Target)> links)
    {
        var problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static bool Around(string outer, string inner) => !outer.Equals(inner, StringComparison.OrdinalIgnoreCase) && DiskSpace.IsInside(inner, outer);
        foreach (var level in links.GroupBy(l => links.Count(o => Around(o.At, l.At))).OrderBy(g => g.Key))
            RelinkLevel(level.ToList(), problems);
        return problems;
    }

    static void RelinkLevel(IReadOnlyList<(string At, string Target)> links, Dictionary<string, string> problems)
    {
        var guarded = new List<(string At, string Target)>();
        foreach (var (at, target) in links)
        {
            var problem = RelinkHere(at, target);
            if (problem == NeedsAdmin) guarded.Add((at, target));
            else if (problem.Length > 0) problems[at] = problem;
        }
        if (guarded.Count == 0) return;
        var list = Path.Combine(Path.GetTempPath(), $"mo-relink-{Guid.NewGuid():N}.txt");
        var result = list + ".result";
        try
        {
            var fingerprint = AdminStep.WriteList(list, guarded.Select(g => $"{g.At}\t{g.Target}"));
            var admin = AdminStep.Run(HelperFlag, "relink", list, result, fingerprint);
            var said = admin.Length == 0 && File.Exists(result)
                ? File.ReadAllLines(result).Select(l => l.Split('\t', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (at, _) in guarded)
                if (!said.TryGetValue(at, out var problem)) problems[at] = admin.Length > 0 ? admin : "The administrator step didn't finish.";
                else if (problem.Length > 0) problems[at] = problem;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var (at, _) in guarded) problems[at] = e.Message;
        }
        finally
        {
            try { File.Delete(list); File.Delete(result); } catch (IOException) { }
        }
    }

    /// <summary>Swaps the link at <paramref name="at"/> for one to <paramref name="target"/>. Never touches a real folder.</summary>
    static string RelinkHere(string at, string target)
    {
        if (!Directory.Exists(target)) return $"{target} isn't there.";
        if (!IsLink(at))
            return Directory.Exists(at) ? $"{at} is a real folder now, not a link, so it was left alone." : Link(at, target) is { Length: > 0 } made ? made : "";
        var was = new DirectoryInfo(at).LinkTarget;
        try { DeleteLink(at); }
        catch (Exception e) when (Denied(e)) { return NeedsAdmin; }
        catch (IOException e) { return $"Windows wouldn't change the link ({e.Message})."; }
        var link = Link(at, target);
        if (link.Length == 0) return "";
        if (was is not null) Link(at, was);   // put the old link back
        return Environment.IsPrivilegedProcess || !OperatingSystem.IsWindows() ? $"Windows wouldn't make the link ({link})." : NeedsAdmin;
    }

    /// <summary>Removes a link only (a junction, or a symbolic link even when what it pointed at is gone).</summary>
    static void DeleteLink(string at)
    {
        try { Directory.Delete(at); }
        catch (DirectoryNotFoundException) when (!OperatingSystem.IsWindows()) { File.Delete(at); }
    }
}
