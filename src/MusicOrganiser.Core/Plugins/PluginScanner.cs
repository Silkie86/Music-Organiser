using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MusicOrganiser.Core.Plugins;

/// <summary>What the scanner found in one plugin file.</summary>
public sealed record PluginFileScan(string Path, IReadOnlyList<ScannedPlugin> Plugins, string Error = "");

/// <summary>A plugin file's size and modified time, to tell when it needs reading again.</summary>
public readonly record struct PluginStamp(long Size, long Modified);

/// <summary>
/// Runs the native plugin scanner (native/mo-scan, C++) on plugin files: one process per file, with a
/// time limit, so a plugin that crashes or hangs only takes the scanner down, never the app.
/// </summary>
public sealed class PluginScanner
{
    public static readonly string FileName = OperatingSystem.IsWindows() ? "mo-scan.exe" : "mo-scan";

    /// <summary>The formats mo-scan reads. Audio Units (Mac) and AAX aren't among them yet.</summary>
    static readonly string[] Readable = [".clap", ".vst3", ".dll", ".vst"];

    public string? Executable { get; }
    public bool Available => Executable is not null;
    /// <summary>How long one plugin file may take before the scanner is stopped.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public PluginScanner(string? executable = null) => Executable = executable ?? Find();

    /// <summary>
    /// mo-scan next to the app (the build copies it there), or a newer one in native/out/app/bin or
    /// native/build/bin of the source folders the app was built from.
    /// </summary>
    public static string? Find(string? appFolder = null)
    {
        var folder = appFolder ?? AppContext.BaseDirectory;
        var candidates = new List<string> { Path.Combine(folder, FileName) };
        for (var dir = new DirectoryInfo(folder); dir is not null && candidates.Count < 20; dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "native", "out", "app", "bin", FileName));
            candidates.Add(Path.Combine(dir.FullName, "native", "build", "bin", FileName));
        }
        return candidates.Where(File.Exists)
                         .Select((path, order) => (path, order, time: File.GetLastWriteTimeUtc(path)))
                         .OrderByDescending(c => c.time).ThenBy(c => c.order)
                         .Select(c => c.path).FirstOrDefault();
    }

    public static bool CanRead(string path) =>
        Readable.Any(e => path.TrimEnd('\\', '/').EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Size and modified time of the plugin's code: the file itself, or for a bundle folder
    /// (.vst3, and .clap and .vst on a Mac) the files in its binary folders.
    /// </summary>
    public static PluginStamp Stamp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var f = new FileInfo(path);
                return new(f.Length, f.LastWriteTimeUtc.Ticks);
            }
            if (!Directory.Exists(path)) return default;
            long size = 0, modified = Directory.GetLastWriteTimeUtc(path).Ticks;
            var contents = Path.Combine(path, "Contents");
            if (Directory.Exists(contents))
                foreach (var bin in Directory.EnumerateDirectories(contents))
                {
                    var name = Path.GetFileName(bin);
                    if (!name.EndsWith("-win", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("-linux", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("MacOS", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var file in new DirectoryInfo(bin).EnumerateFiles())
                    {
                        size += file.Length;
                        modified = Math.Max(modified, file.LastWriteTimeUtc.Ticks);
                    }
                }
            return new(size, modified);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; }
    }

    /// <summary>
    /// Reads one plugin file. Without <paramref name="parameters"/> it only lists the plugins inside
    /// (quick, and CLAP and VST3 plugins aren't even started); with them it loads each plugin.
    /// <paramref name="only"/> picks one plugin by ID, e.g. one from a Waves shell.
    /// </summary>
    public async Task<PluginFileScan> ScanAsync(string path, bool parameters = false, string? only = null, CancellationToken ct = default)
    {
        if (Executable is null) return new(path, [], "The plugin scanner isn't built (see native/README.md)");
        var psi = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // Some plugins write logs to the working folder; keep them out of the app's.
            WorkingDirectory = Path.GetTempPath(),
        };
        if (!parameters) psi.ArgumentList.Add("--no-params");
        if (only is not null)
        {
            psi.ArgumentList.Add("--only");
            psi.ArgumentList.Add(only);
        }
        psi.ArgumentList.Add(path);

        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new(path, [], $"Couldn't run the plugin scanner: {e.Message}");
        }

        var lines = new List<string>();
        var stdout = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                lock (lines) lines.Add(line);
        });
        // Plugins can be chatty; drain it so a full pipe can't stall them. The end of it is kept for errors.
        var stderr = process.StandardError.ReadToEndAsync();

        var timedOut = false;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limit.CancelAfter(Timeout);
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                try { process.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
            }
        }
        // Everything mo-scan printed is in the pipe now. A helper process the plugin started may still
        // hold the pipe open, so don't wait long for its end.
        await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
        _ = stdout.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        _ = stderr.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        ct.ThrowIfCancellationRequested();

        List<string> got;
        lock (lines) got = [.. lines];
        var plugins = got.Select(ScannedPlugin.Parse).OfType<ScannedPlugin>().ToList();
        var error = got.Select(ScannedPlugin.FileError).FirstOrDefault(e => e is not null) ?? "";
        if (timedOut)
            error = plugins.Count > 0 ? $"stopped after {Timeout.TotalSeconds:0} seconds, part way through" : $"didn't finish within {Timeout.TotalSeconds:0} seconds";
        else if (error.Length == 0 && process.ExitCode != 0)
        {
            var detail = stderr.IsCompletedSuccessfully ? LastLine(stderr.Result) : "";
            error = (plugins.Count > 0 ? "the scanner crashed part way through" : "the plugin crashed the scanner") +
                    (detail.Length > 0 ? $": {detail}" : $" (code {process.ExitCode})");
        }
        return new(path, plugins, error);
    }

    static string LastLine(string text)
    {
        var line = text.TrimEnd().Split('\n').LastOrDefault()?.Trim() ?? "";
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
