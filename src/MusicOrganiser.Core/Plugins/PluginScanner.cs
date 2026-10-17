using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    /// <summary>
    /// On a Mac or Linux, nice, which starts the scanner with its niceness 10 more than the app's, so it never runs at the
    /// app's priority, even for a moment. Setting its priority after it started didn't hold on macOS CI: the scanner read
    /// niceness -10 a second later, twice in a row.
    /// </summary>
    static readonly string? Nice = OperatingSystem.IsWindows() ? null : new[] { "/usr/bin/nice", "/bin/nice" }.FirstOrDefault(File.Exists);
    readonly HashSet<Process> _running = [];
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

    /// <summary>
    /// Why building mo-scan failed last time, from the log the build left in native/out/app of the source
    /// folders the app was built from: build-scanner's own reason, or the first error. Null when there's no
    /// log or it shows no problem.
    /// </summary>
    public static string? BuildProblem(string? appFolder = null)
    {
        for (var dir = new DirectoryInfo(appFolder ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var log = Path.Combine(dir.FullName, "native", "out", "app", "build.log");
            if (!File.Exists(log)) continue;
            try
            {
                var problem = File.ReadLines(log).Select(l => l.Trim()).FirstOrDefault(l =>
                    l.Contains("wasn't found", StringComparison.Ordinal) || l.Contains("has no C++ compiler", StringComparison.Ordinal) ||
                    l.Contains("isn't installed", StringComparison.Ordinal) || l.Contains("error", StringComparison.OrdinalIgnoreCase));
                return problem is null ? null : problem.Length > 300 ? problem[..300] + "…" : problem;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        return null;
    }

    /// <summary>
    /// Ends every scanner process still running, e.g. as the app closes: a plugin stuck loading, or waiting on its
    /// licence window, would otherwise outlive the app. Their scans return as if the plugin had crashed.
    /// </summary>
    public void StopAll()
    {
        Process[] running;
        lock (_running) running = [.. _running];
        foreach (var process in running)
            try { process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException) { }
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
    /// (quick, and CLAP and VST3 plugins aren't even started; a VST3 with a moduleinfo.json isn't even
    /// loaded, see <see cref="Vst3ModuleInfo"/>); with them it loads each plugin.
    /// <paramref name="only"/> picks one plugin by ID, e.g. one from a Waves shell.
    /// </summary>
    public async Task<PluginFileScan> ScanAsync(string path, bool parameters = false, string? only = null, CancellationToken ct = default)
    {
        if (Executable is null) return new(path, [], "The plugin scanner isn't built (see native/README.md)");
        // A VST3 that says what's inside it needn't be loaded just to list it.
        if (!parameters && Vst3ModuleInfo.Read(path) is { } listed)
        {
            var wanted = only is null ? listed : listed.Where(p => p.Id.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();
            if (wanted.Count > 0) return new(path, wanted);
        }
        // Through nice only when the scanner can run, so a missing one still says it couldn't be run, rather than how nice failed.
        var niced = !OperatingSystem.IsWindows() && Nice is not null && File.Exists(Executable) &&
                    (File.GetUnixFileMode(Executable) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        var psi = new ProcessStartInfo(niced ? Nice! : Executable)
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
        if (niced)
        {
            // nice becomes the scanner (same process), and finds it from the scanner's working folder, so it needs the full path.
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("10");
            psi.ArgumentList.Add(Path.GetFullPath(Executable));
        }
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
        // Below normal, as the app's own background work is, so a scan gets only what the PC has spare and the app
        // (or a DAW playing) never lags behind it. On a Mac or Linux nice has started it 10 below the app already, and
        // setting it again is left out: on macOS CI the scanner read -10 after it. A scanner that has already finished
        // can't be changed, which is fine.
        if (!niced)
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        lock (_running) _running.Add(process);

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
                catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException) { }
            }
        }
        lock (_running) _running.Remove(process);
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

    /// <summary>
    /// Loads one plugin to read its parameters. Returns it with them, or with why it couldn't be loaded, which is
    /// kept with it so reading every plugin's parameters leaves it out next time.
    /// </summary>
    public async Task<ScannedPlugin> ReadParametersAsync(ScannedPlugin plugin, CancellationToken ct = default)
    {
        var scan = await ScanAsync(plugin.Path, parameters: true, only: plugin.Id, ct: ct);
        var found = scan.Plugins.FirstOrDefault(p => p.Id == plugin.Id);
        if (found?.Params is not null)
            return plugin with { Params = found.Params, Error = found.Error, Json = found.Json, Kind = found.Kind.Length > 0 ? found.Kind : plugin.Kind,
                                 Version = found.Version.Length > 0 ? found.Version : plugin.Version };
        var why = found?.Error is { Length: > 0 } e ? e : scan.Error.Length > 0 ? scan.Error : "it didn't report its parameters";
        return plugin with { Error = why, Json = WithError(plugin.Json, why) };
    }

    /// <summary>A scanner line with its "error" set.</summary>
    static string WithError(string json, string error)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject o)
            {
                o["error"] = error;
                return o.ToJsonString();
            }
        }
        catch (JsonException) { }
        return json;
    }

    static string LastLine(string text)
    {
        var line = text.TrimEnd().Split('\n').LastOrDefault()?.Trim() ?? "";
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
