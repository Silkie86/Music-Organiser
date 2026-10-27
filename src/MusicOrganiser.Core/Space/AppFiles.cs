namespace MusicOrganiser.Core.Space;

/// <summary>One of the app's databases: what it's called on Settings, what it holds, and its file.</summary>
public sealed record AppDatabase(string Name, string What, string Path);

/// <summary>One part of the app's own files: a database, the search models, the logs.</summary>
public sealed record AppFilesPart(string Name, string What, long Bytes);

/// <summary>
/// How much room the app's own files take, and whether the drive they're on is nearly full (Solomon's F12 note, 27 Oct:
/// "add way to see the size of the database the app uses to hold values and make sure it warns you if the disk its on is
/// nearly full"). The library, the Sound Lab and the MIDI parts are databases that grow with every scan, and a database
/// can't save anything once its drive is full.
/// </summary>
public static class AppFiles
{
    /// <summary>Under this much room left, the drive counts as nearly full.</summary>
    public const long LowSpace = 10L << 30;

    /// <summary>Under this much, saving may already fail.</summary>
    public const long VeryLowSpace = 2L << 30;

    /// <summary>
    /// Whether the drive is nearly full: under <see cref="LowSpace"/> free, or under what the app's own files take (a
    /// library that size can grow by as much again in a big scan). Unknown room never counts.
    /// </summary>
    public static bool IsLow(long free, long appBytes) => free >= 0 && free < Math.Max(LowSpace, appBytes);

    /// <summary>The three databases, by where they are.</summary>
    public static IReadOnlyList<AppDatabase> Databases(string library, string sounds, string midi) =>
    [
        new("Your library", "Projects, plugins, samples, tags and ratings", library),
        new("Sound Lab", "Every sound's details, likes and look-alikes", sounds),
        new("MIDI parts", "The MIDI read from your projects and MIDI files", midi),
    ];

    /// <summary>The files SQLite keeps beside a database while it's open (its write-ahead log and its index of it).</summary>
    static readonly string[] Journals = ["", "-wal", "-shm", "-journal"];

    /// <summary>
    /// Measures the databases, each with the files SQLite keeps beside it ("library.db-wal"), then the rest of the app's
    /// <paramref name="folder"/> by what each folder at its top holds, biggest first. Links are left out, as everywhere
    /// sizes are measured.
    /// </summary>
    public static IReadOnlyList<AppFilesPart> Measure(string folder, IReadOnlyList<AppDatabase> databases, CancellationToken ct = default)
    {
        var parts = new Dictionary<string, (string What, long Bytes)>();
        void Add(string name, string what, long bytes) => parts[name] = (what, (parts.TryGetValue(name, out var p) ? p.Bytes : 0) + bytes);
        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var db in databases)
            foreach (var file in Journals.Select(j => Path.GetFullPath(db.Path + j)))
                if (counted.Add(file) && File.Exists(file)) Add(db.Name, db.What, Length(new FileInfo(file)));
        foreach (var entry in Entries(folder))
        {
            ct.ThrowIfCancellationRequested();
            if (counted.Contains(entry.FullName) || DiskSpace.IsLink(entry)) continue;
            var (name, what) = PartOf(entry.Name, entry is DirectoryInfo);
            Add(name, what, entry is FileInfo file ? Length(file) : DiskSpace.Measure(entry.FullName, ct).Bytes);
        }
        return parts.Select(p => new AppFilesPart(p.Key, p.Value.What, p.Value.Bytes)).Where(p => p.Bytes > 0)
                    .OrderByDescending(p => p.Bytes).ToList();
    }

    static IReadOnlyList<FileSystemInfo> Entries(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static long Length(FileInfo file)
    {
        try { return file.Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Which part a file or folder at the top of the app's folder is, and what it holds.</summary>
    internal static (string Name, string What) PartOf(string name, bool folder) =>
        (folder ? name.ToLowerInvariant() : "") switch
        {
            "models" => ("Search models", "What finds sounds and MIDI from what you type"),
            "sounds" => ("Playground sound", "The SoundFont the Playground plays"),
            "playground" or "playground-quick" => ("Playground ideas", "Your Playground ideas and quick saves"),
            "logs" => ("Logs", "What the app did, for when something goes wrong"),
            "notes" => ("Notes for Claude", "What you wrote with F12, with its pictures"),
            "reports" or "probe" => ("Test reports", "Live tests, self-checks and MO Probe"),
            _ => ("Everything else", "Your Bitwig template and the rest"),
        };

    /// <summary>The drive a file or folder is on ("C:", or a Mac's or Linux's disk), its room left and its size; -1 when unknown.</summary>
    public static (string Drive, long Free, long Total) DriveSpace(string path)
    {
        if (path.Length == 0) return ("", -1, -1);
        try
        {
            var root = OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetFullPath(path)) ?? "" : FolderMove.DriveOf(path);
            var drive = new DriveInfo(root);
            return (DriveName(root), drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return ("", -1, -1); }
    }

    /// <summary>"C:" on Windows; elsewhere the disk's folder name, or "your disk" for the main one.</summary>
    static string DriveName(string root)
    {
        if (OperatingSystem.IsWindows()) return Path.TrimEndingDirectorySeparator(root);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        return name.Length > 0 ? name : "your disk";
    }
}
