using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MusicOrganiser.Core.Space;

/// <summary>
/// Removes files and folders by putting them in the Recycle Bin (the Trash on a Mac or Linux), never deleting them for
/// good: anything the app removes to free space can be put back from there.
/// </summary>
public static class RecycleBin
{
    /// <summary>
    /// Puts a file or folder in the bin. Returns "" when it's gone there, or what stopped it. When Windows can't keep
    /// something that big in its bin, it asks first rather than deleting it for good.
    /// </summary>
    public static string Send(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return "";   // already gone
        try
        {
            if (OperatingSystem.IsWindows()) return SendOnWindows(path);
            if (OperatingSystem.IsMacOS()) return MoveInto(path, Path.Combine(Home, ".Trash"), null);
            var trash = Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".local", "share"), "Trash");
            return MoveInto(path, Path.Combine(trash, "files"), Path.Combine(trash, "info"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>What <see cref="Send"/> says when Windows wants administrator rights (plugin files in Program Files).</summary>
    public const string NeedsAdmin = "Windows needs administrator rights to remove this. Uninstall it instead, or remove it in Explorer.";

    /// <summary>
    /// Puts several files or folders in the bin. The ones Windows guards (plugin files in Program Files) go in one more
    /// step, with one administrator question for all of them (see <see cref="RunHelper"/>). Returns what couldn't go, and why.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SendMany(IReadOnlyList<string> paths)
    {
        var problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var guarded = new List<string>();
        foreach (var path in paths)
        {
            var problem = Send(path);
            if (problem == NeedsAdmin) guarded.Add(path);
            else if (problem.Length > 0) problems[path] = problem;
        }
        if (guarded.Count > 0)
            foreach (var (path, problem) in OperatingSystem.IsWindows() ? SendAsAdmin(guarded) : guarded.Select(p => (p, NeedsAdmin)))
                if (problem.Length > 0) problems[path] = problem;
        return problems;
    }

    /// <summary>The app started for <see cref="SendAsAdmin"/>: <c>--recycle-bin list result fingerprint</c>.</summary>
    public const string HelperFlag = "--recycle-bin";

    /// <summary>
    /// Runs the administrator step when the app was started for it: bins each path in the list file and writes
    /// "path, tab, problem" lines to the result file. A list changed since the app wrote it (its fingerprint differs)
    /// bins nothing. Returns false when the app wasn't started for this.
    /// </summary>
    public static bool RunHelper(string[] args)
    {
        if (args.Length == 0 || args[0] != HelperFlag) return false;
        if (args.Length != 4) return true;   // not as the app starts it: nothing, and no window as administrator either
        try
        {
            if (AdminStep.ReadList(args[1], args[3]) is not { } paths) return true;
            // Lazily: the result file is made first, so nothing is binned when it can't be.
            AdminStep.WriteResult(args[2], paths.Select(p => $"{p}\t{Send(p)}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return true;
    }

    /// <summary>
    /// Starts the app again with administrator rights (Windows asks you once) to bin files in folders that need them, and
    /// waits for it: the same as Explorer's "You'll need to provide administrator permission" step.
    /// </summary>
    static IEnumerable<(string Path, string Problem)> SendAsAdmin(IReadOnlyList<string> paths)
    {
        var list = Path.Combine(Path.GetTempPath(), $"mo-recycle-{Guid.NewGuid():N}.txt");
        var result = list + ".result";
        try
        {
            var fingerprint = AdminStep.WriteList(list, paths);
            var problem = AdminStep.Run(HelperFlag, list, result, fingerprint);
            if (problem.Length > 0) return paths.Select(p => (p, problem));
            if (!File.Exists(result)) return paths.Select(p => (p, "The administrator step didn't finish."));
            var said = File.ReadAllLines(result).Select(l => l.Split('\t', 2)).Where(x => x.Length == 2)
                           .ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
            return paths.Select(p => (p, said.TryGetValue(p, out var problem) ? problem : "The administrator step skipped it.")).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return paths.Select(p => (p, e.Message));
        }
        finally
        {
            try { File.Delete(list); File.Delete(result); } catch (IOException) { }
        }
    }

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The Mac and freedesktop.org Trash: a move into the bin folder, with a note of where it came from on Linux.</summary>
    static string MoveInto(string path, string files, string? info)
    {
        Directory.CreateDirectory(files);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var target = Path.Combine(files, name);
        for (var n = 2; File.Exists(target) || Directory.Exists(target); n++)
            target = Path.Combine(files, $"{Path.GetFileNameWithoutExtension(name)} {n}{Path.GetExtension(name)}");
        if (info is not null)
        {
            Directory.CreateDirectory(info);
            File.WriteAllText(Path.Combine(info, Path.GetFileName(target) + ".trashinfo"),
                $"[Trash Info]\nPath={Uri.EscapeDataString(Path.GetFullPath(path)).Replace("%2F", "/")}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
        }
        if (Directory.Exists(path)) Directory.Move(path, target);
        else File.Move(path, target);
        return "";
    }

    // ---- Windows: the shell's own delete, with undo ----------------------------------------------------------------

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400, FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    static string SendOnWindows(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0",   // a list of paths, ended by two nulls
            // Undo = the Recycle Bin. "Nuke warning": if it's too big for the bin, Windows asks before deleting it for good.
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_SILENT | FOF_NOERRORUI,
        };
        var result = SHFileOperation(ref op);
        if (op.fAnyOperationsAborted) return "You chose to keep it.";
        if (result == 0) return File.Exists(path) || Directory.Exists(path) ? "Windows left some of it, probably files in use." : "";
        return result switch
        {
            5 or 0x78 => NeedsAdmin,
            32 or 0x20 => "A program is using it. Close your DAW and try again.",
            _ => $"Windows couldn't move it to the Recycle Bin (code {result}).",
        };
    }
}
