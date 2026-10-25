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
            5 or 0x78 => "Windows needs administrator rights to remove this. Uninstall it instead, or remove it in Explorer.",
            32 or 0x20 => "A program is using it. Close your DAW and try again.",
            _ => $"Windows couldn't move it to the Recycle Bin (code {result}).",
        };
    }
}
